// SfzSessionStore.cs — session store for SFZ (ZIP archive) and FileIo (loose file) sessions.
//
// Single source of truth for SFZ-mode session lifecycle and data access.
// CameraSubsystem drives it through StartSession / Tick / StopSessionAsync;
// OcclusionSubsystem, ControlBridge, and other consumers read state through
// the proxy properties — never touching IO types directly.
//
// Private nested types (all invisible outside this file):
//   SfzBackendBase          — ring-buffer streaming on a background thread
//   SfzFileBackend          — SFZ (ZIP archive) source
//   FileIoBackend           — FileIo (loose files) source
//   ScannedMeshLoaderImpl   — polls for scene_mesh attachment, drives PLY parse

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal static class SfzSessionStore
    {
        // ── Private state ─────────────────────────────────────────────────────
        static SessionLoadState s_LoadState = SessionLoadState.Idle;
        static ISessionBackend  s_Backend;
        static SfzSessionData   s_SessionData;
        static int              s_BufSize;
        static int              s_FramesToWait;
        static readonly ScannedMeshLoaderImpl s_MeshLoader = new();

        // ── Per-frame data (written by CameraSubsystem each frame) ────────────
        internal static long       LatestTimestampNs       = 0;
        internal static Vector4    LatestIntrinsics        = new(935.3f, 935.3f, 960f, 720f);
        internal static Vector2Int LatestTextureDimensions = new(1920, 1440);

        // ── Session state ─────────────────────────────────────────────────────
        internal static bool           IsActive    => s_LoadState != SessionLoadState.Idle;
        internal static bool           IsReady     => s_LoadState == SessionLoadState.Ready;
        internal static SfzSessionData SessionData => s_SessionData;

        // ── Ring buffer proxies (read-only for all consumers) ─────────────────
        internal static double    FrameInterval                  => s_Backend?.State?.FrameInterval ?? (1.0 / 30);
        internal static Matrix4x4 CoordConvMatrix                => s_Backend?.State?.CoordConvMatrix ?? Matrix4x4.identity;
        internal static bool      UseNegativeZForwardOpticalAxis => s_Backend?.State?.UseNegativeZForwardOpticalAxis ?? false;
        internal static int       TotalFrames                    => s_Backend?.State?.TotalFrames ?? 0;
        internal static int       BufSize                        => s_Backend?.State?.BufSize ?? 0;
        internal static Texture2D[]  Frames        => s_Backend?.State?.Frames;
        internal static byte[][]     DepthBins     => s_Backend?.State?.DepthBins;
        internal static Matrix4x4[]  Poses         => s_Backend?.State?.Poses;
        internal static Vector4[]    Intrinsics    => s_Backend?.State?.Intrinsics;
        internal static bool[]       SlotReady     => s_Backend?.State?.SlotReady;
        internal static int[]        SlotGlobalIdx => s_Backend?.State?.SlotGlobalIdx;
        internal static int          LatestGlobalIndex  => s_Backend?.State?.LatestGlobalIndex ?? -1;
        internal static int          PendingDecodeCount => s_Backend?.State?.PendingDecodeCount ?? 0;

        internal static int PlayHead
        {
            get => s_Backend?.State?.PlayHead ?? -1;
            set { if (s_Backend?.State != null) s_Backend.State.PlayHead = value; }
        }

        // ── Lifecycle (called by CameraSubsystem) ─────────────────────────────

        internal static void StartSession(ARSensorFlexSession session, int maxFramesToLoad, int framesToWait)
        {
            s_BufSize      = maxFramesToLoad;
            s_FramesToWait = framesToWait;
            s_Backend      = CreateBackend(session.SourceMode);
            s_MeshLoader.Reset();

            if (!s_Backend.Open(session))
            {
                Debug.LogError("[SF] SfzSessionStore: backend failed to open.");
                s_Backend = null;
                return;
            }

            s_LoadState = SessionLoadState.Waiting;
            Debug.Log($"[SF] SfzSessionStore: waiting for session data. mode={session.SourceMode}");
        }

        internal static void Tick()
        {
            s_Backend?.Dispatch();

            switch (s_LoadState)
            {
                case SessionLoadState.Waiting:
                    if (s_Backend.TryGetSessionJson(out var json) && TryParseSession(json, out s_SessionData))
                    {
                        s_Backend.StartLoading(s_SessionData, s_BufSize, s_FramesToWait);
                        s_LoadState = SessionLoadState.Loading;
                        Debug.Log($"[SF] SfzSessionStore: session loaded. id={s_SessionData.SessionId} " +
                                  $"tracks={s_SessionData.Tracks.Count} attachments={s_SessionData.Attachments.Count}");
                    }
                    break;

                case SessionLoadState.Loading:
                case SessionLoadState.Ready:
                    s_Backend.DrainMainThreadWork();
                    if (s_LoadState == SessionLoadState.Loading && s_Backend.State?.IsReady == true)
                    {
                        s_LoadState = SessionLoadState.Ready;
                        Debug.Log("[SF] SfzSessionStore: ready.");
                    }
                    break;
            }

            s_MeshLoader.Tick();
        }

        internal static async Task StopSessionAsync()
        {
            s_LoadState   = SessionLoadState.Idle;
            s_SessionData = null;
            s_MeshLoader.Reset();
            ResetFrameData();

            var backend = s_Backend;
            s_Backend = null;

            if (backend != null)
            {
                await backend.StopAsync();
                backend.State?.DestroyTextures();
            }
        }

        internal static byte[] TryConsumeAttachment(string name) => s_Backend?.TryGetAttachmentBytes(name);

        // Resets all state without stopping any active backend.
        // Use only when no session is running (e.g., on initial Start()).
        internal static void Clear()
        {
            s_LoadState   = SessionLoadState.Idle;
            s_SessionData = null;
            s_Backend     = null;
            s_MeshLoader.Reset();
            ResetFrameData();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static void ResetFrameData()
        {
            LatestTimestampNs       = 0;
            LatestIntrinsics        = new Vector4(935.3f, 935.3f, 960f, 720f);
            LatestTextureDimensions = new Vector2Int(1920, 1440);
        }

        static ISessionBackend CreateBackend(ARSensorFlexSession.FrameSourceMode mode) => mode switch
        {
            ARSensorFlexSession.FrameSourceMode.Sfz    => new SfzFileBackend(),
            ARSensorFlexSession.FrameSourceMode.FileIo => new FileIoBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        static bool TryParseSession(string json, out SfzSessionData data)
        {
            data = null;
            if (string.IsNullOrEmpty(json)) return false;

            var raw = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(json);
            if (raw == null || string.IsNullOrEmpty(raw.version)) return false;

            var tracks = new Dictionary<string, SfzTrackInfo>();
            if (raw.tracks?.frames != null)
            {
                int fps   = raw.tracks.frames.metadata?.fps ?? 30;
                int count = raw.tracks.frames.data?.Length ?? 0;
                tracks["frames"] = new SfzTrackInfo("frames", 1.0 / Math.Max(1, fps), count);
            }
            if (raw.tracks?.imu != null)
            {
                float hz  = raw.tracks.imu.metadata?.sample_rate_hz ?? 100f;
                int count = raw.tracks.imu.data?.Length ?? 0;
                tracks["imu"] = new SfzTrackInfo("imu", 1.0 / Math.Max(1f, hz), count);
            }

            var attachments = new Dictionary<string, SfzAttachmentInfo>();
            if (raw.attachments?.scene_mesh != null)
                attachments["scene_mesh"] = new SfzAttachmentInfo(
                    "scene_mesh",
                    raw.attachments.scene_mesh.file,
                    raw.attachments.scene_mesh.format);

            data = new SfzSessionData(
                raw.session_id,
                tracks,
                attachments,
                raw.tracks?.frames?.data);

            return true;
        }

        // ── Nested: SfzBackendBase ────────────────────────────────────────────

        abstract class SfzBackendBase : ISessionBackend
        {
            const int UploadBatchSize = 3;

            // Frame packet exchanged between the background loader thread and the main thread.
            struct LoadedFrame
            {
                public int    GlobalFrameIndex;
                public int    RecordIndex;
                public byte[] Jpg;
                public byte[] DepthBin;
            }

            protected ARSensorFlexSession m_Session;
            protected IBackendState       m_State;
            int           m_FramesToWait;
            volatile bool m_StopLoading;
            Thread        m_LoadThread;
            ConcurrentQueue<LoadedFrame> m_UploadQueue;
            int  m_UploadedFrames;
            bool m_LoggedFirstEnqueue;
            bool m_LoggedFirstUpload;
            bool m_LoggedReady;

            protected SfzSessionData m_SessionData;

            // ── ISessionBackend ───────────────────────────────────────────────

            public abstract bool   Open(ARSensorFlexSession session);
            public abstract bool   TryGetSessionJson(out string json);
            public abstract byte[] TryGetAttachmentBytes(string attachmentName);

            public void StartLoading(SfzSessionData data, int bufSize, int framesToWait)
            {
                m_SessionData        = data;
                m_FramesToWait       = framesToWait;
                m_StopLoading        = false;
                m_UploadedFrames     = 0;
                m_LoggedFirstEnqueue = false;
                m_LoggedFirstUpload  = false;
                m_LoggedReady        = false;

                if (m_SessionData.FrameRecords == null || m_SessionData.FrameRecords.Length == 0)
                {
                    Debug.LogError($"[SF] {BackendLabel}: frames track missing or empty.");
                    return;
                }

                m_State = new BackendState(bufSize);

                bool hasFrames = m_SessionData.Tracks.TryGetValue("frames", out var framesTrack);
                m_State.TotalFrames    = hasFrames && framesTrack.RecordCount > 0
                    ? framesTrack.RecordCount
                    : int.MaxValue;
                m_State.FrameInterval  = hasFrames ? framesTrack.SampleInterval : 1.0 / 30;
                m_State.CoordConvMatrix               = Matrix4x4.identity;
                m_State.UseNegativeZForwardOpticalAxis = false;
                m_State.AllocateRingBuffer();

                m_UploadQueue = new ConcurrentQueue<LoadedFrame>();
                m_LoadThread  = new Thread(LoadFrames) { IsBackground = true, Name = $"SF-{BackendLabel}" };
                m_LoadThread.Start();

                Debug.Log($"[SF] {BackendLabel} streaming started. frames={m_State.TotalFrames} fps={1.0 / m_State.FrameInterval:F0} bufSize={m_State.BufSize}");
            }

            public IBackendState State    => m_State;
            public void          Dispatch() { }

            public Task StopAsync()
            {
                m_StopLoading = true;
                if (m_LoadThread != null && m_LoadThread.IsAlive)
                {
                    m_LoadThread.Join(500);
                    m_LoadThread = null;
                }
                m_UploadQueue = null;
                return Task.CompletedTask;
            }

            public void DrainMainThreadWork()
            {
                if (m_UploadQueue == null) return;

                int uploaded = 0;
                while (uploaded < UploadBatchSize && m_UploadQueue.TryDequeue(out var item))
                {
                    int slot = item.GlobalFrameIndex % m_State.BufSize;

                    if (m_State.Frames[slot] == null)
                        m_State.Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                    m_State.Frames[slot].LoadImage(item.Jpg);
                    m_State.Frames[slot].Apply();
                    m_State.DepthBins[slot] = item.DepthBin;

                    var records = m_SessionData?.FrameRecords;
                    if (records != null && item.RecordIndex >= 0 && item.RecordIndex < records.Length)
                    {
                        var rec = records[item.RecordIndex];
                        if (rec.camera?.pose != null)
                            m_State.Poses[slot] = SfzUtils.SfzPoseToMatrix4x4(rec.camera.pose);
                        if (rec.camera?.intrinsics != null)
                            m_State.Intrinsics[slot] = SfzUtils.SfzIntrinsicsToVector4(rec.camera.intrinsics);
                    }

                    m_State.SlotGlobalIdx[slot] = item.GlobalFrameIndex;
                    m_State.SlotReady[slot]     = true;
                    m_State.MarkBuffered(m_FramesToWait);
                    m_UploadedFrames++;

                    if (!m_LoggedFirstUpload)
                    {
                        Debug.Log($"[SF] {BackendLabel} first frame uploaded. GlobalFrame={item.GlobalFrameIndex} Slot={slot}");
                        m_LoggedFirstUpload = true;
                    }

                    if (!m_LoggedReady && m_State.IsReady)
                    {
                        Debug.Log($"[SF] {BackendLabel} ready. Uploaded={m_UploadedFrames} FramesToWait={m_FramesToWait}");
                        m_LoggedReady = true;
                    }

                    uploaded++;
                }
            }

            // ── Subclass contract ─────────────────────────────────────────────

            protected abstract string BackendLabel { get; }
            protected abstract byte[] ReadSessionFile(string relativePath);
            protected virtual  void   BeginLoadPass() { }
            protected virtual  void   EndLoadPass()   { }

            // ── Background loader thread ──────────────────────────────────────

            void LoadFrames()
            {
                bool looping   = m_Session.LoopSequence;
                int  iteration = 0;

                while (!m_StopLoading)
                {
                    int globalOffset = iteration * m_State.TotalFrames;
                    BeginLoadPass();
                    try
                    {
                        for (int i = 0; i < m_SessionData.FrameRecords.Length && !m_StopLoading; i++)
                        {
                            var record = m_SessionData.FrameRecords[i];
                            if (string.IsNullOrEmpty(record.rgb?.file)) continue;

                            byte[] jpg = ReadSessionFile(record.rgb.file);
                            if (jpg == null) continue;

                            byte[] depth = !string.IsNullOrEmpty(record.depth?.file)
                                ? ReadSessionFile(record.depth.file)
                                : null;

                            Enqueue(globalOffset + i, i, jpg, depth);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SF] {BackendLabel} loader error: {ex}");
                        return;
                    }
                    finally
                    {
                        EndLoadPass();
                    }
                    if (!looping) break;
                    iteration++;
                }
            }

            void Enqueue(int globalFrameIndex, int recordIndex, byte[] jpg, byte[] depth)
            {
                while (!m_StopLoading && globalFrameIndex - m_State.PlayHead >= m_State.BufSize)
                    Thread.Sleep(1);

                if (m_StopLoading) return;

                if (!m_LoggedFirstEnqueue)
                {
                    Debug.Log($"[SF] {BackendLabel} first frame enqueued. GlobalFrame={globalFrameIndex}");
                    m_LoggedFirstEnqueue = true;
                }

                m_UploadQueue.Enqueue(new LoadedFrame
                {
                    GlobalFrameIndex = globalFrameIndex,
                    RecordIndex      = recordIndex,
                    Jpg              = jpg,
                    DepthBin         = depth
                });
            }
        }

        // ── Nested: SFZ (ZIP archive) ─────────────────────────────────────────

        sealed class SfzFileBackend : SfzBackendBase
        {
            string     m_ArchivePath;
            ZipArchive m_PassArchive;

            protected override string BackendLabel => "SFZ";

            public override bool Open(ARSensorFlexSession session)
            {
                m_Session     = session;
                m_ArchivePath = session.SfzFilePath;
                if (!Path.IsPathRooted(m_ArchivePath))
                    m_ArchivePath = Path.Combine(Application.streamingAssetsPath, m_ArchivePath);

                if (!File.Exists(m_ArchivePath))
                {
                    Debug.LogError($"[SF] SFZ archive not found: {m_ArchivePath}");
                    return false;
                }
                return true;
            }

            public override bool TryGetSessionJson(out string json)
            {
                json = null;
                try
                {
                    using var archive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
                    var entry = archive.GetEntry("session/session.json");
                    if (entry == null) { Debug.LogError("[SF] SFZ: session/session.json not found."); return false; }
                    using var sr = new StreamReader(entry.Open());
                    json = sr.ReadToEnd();
                    return true;
                }
                catch (Exception e) { Debug.LogError("[SF] SFZ: failed to read session.json: " + e); return false; }
            }

            public override byte[] TryGetAttachmentBytes(string attachmentName)
            {
                if (m_SessionData == null ||
                    !m_SessionData.Attachments.TryGetValue(attachmentName, out var att) ||
                    string.IsNullOrEmpty(att.File))
                    return null;

                try
                {
                    using var archive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
                    var entry = archive.GetEntry($"session/{att.File}");
                    return entry != null ? SfzUtils.ReadEntry(entry) : null;
                }
                catch (Exception e) { Debug.LogWarning($"[SF] SFZ: failed to read attachment '{attachmentName}': {e.Message}"); return null; }
            }

            // Keep the archive open for the duration of each pass so the central
            // directory is parsed only once per loop iteration.
            protected override void BeginLoadPass()
                => m_PassArchive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);

            protected override void EndLoadPass() { m_PassArchive?.Dispose(); m_PassArchive = null; }

            protected override byte[] ReadSessionFile(string relativePath)
            {
                try
                {
                    var entry = m_PassArchive?.GetEntry($"session/{relativePath}");
                    return entry != null ? SfzUtils.ReadEntry(entry) : null;
                }
                catch (Exception e) { Debug.LogWarning($"[SF] SFZ: failed to read {relativePath}: {e.Message}"); return null; }
            }
        }

        // ── Nested: FileIo (loose files) ──────────────────────────────────────

        sealed class FileIoBackend : SfzBackendBase
        {
            string m_SessionDir;

            protected override string BackendLabel => "FileIo";

            public override bool Open(ARSensorFlexSession session)
            {
                m_Session    = session;
                m_SessionDir = session.FileIoPath;
                if (!Path.IsPathRooted(m_SessionDir))
                    m_SessionDir = Path.Combine(Application.streamingAssetsPath, m_SessionDir);

                if (!Directory.Exists(m_SessionDir))
                {
                    Debug.LogError($"[SF] FileIo: session directory not found: {m_SessionDir}");
                    return false;
                }
                return true;
            }

            public override bool TryGetSessionJson(out string json)
            {
                json = null;
                string path = Path.Combine(m_SessionDir, "session.json");
                if (!File.Exists(path)) { Debug.LogError($"[SF] FileIo: session.json not found at {path}"); return false; }
                try   { json = File.ReadAllText(path); return true; }
                catch (Exception e) { Debug.LogError("[SF] FileIo: failed to read session.json: " + e); return false; }
            }

            public override byte[] TryGetAttachmentBytes(string attachmentName)
            {
                if (m_SessionData == null ||
                    !m_SessionData.Attachments.TryGetValue(attachmentName, out var att) ||
                    string.IsNullOrEmpty(att.File))
                    return null;

                string fullPath = Path.Combine(m_SessionDir, att.File);
                if (!File.Exists(fullPath)) return null;
                try   { return File.ReadAllBytes(fullPath); }
                catch (Exception e) { Debug.LogWarning($"[SF] FileIo: failed to read attachment '{attachmentName}': {e.Message}"); return null; }
            }

            protected override byte[] ReadSessionFile(string relativePath)
            {
                string fullPath = Path.Combine(m_SessionDir, relativePath);
                if (!File.Exists(fullPath)) return null;
                try   { return File.ReadAllBytes(fullPath); }
                catch (Exception e) { Debug.LogWarning($"[SF] FileIo: failed to read {relativePath}: {e.Message}"); return null; }
            }
        }

        // ── Nested: ScannedMeshLoaderImpl ─────────────────────────────────────

        sealed class ScannedMeshLoaderImpl
        {
            // ScanNet++ PLY meshes are in ARKit space (right-handed, -Z forward).
            // Flip Z to land positions in the same Unity world space as camera poses.
            static readonly Matrix4x4 k_ArkitToUnity = new Matrix4x4(
                new Vector4( 1, 0,  0, 0),
                new Vector4( 0, 1,  0, 0),
                new Vector4( 0, 0, -1, 0),
                new Vector4( 0, 0,  0, 1));

            ScannedSceneMeshLoadOperation m_PendingLoad;
            bool m_Started;

            public void Tick()
            {
                var sessionData = SfzSessionStore.s_SessionData;
                if (sessionData == null) return;

                if (!m_Started && sessionData.Attachments.ContainsKey("scene_mesh"))
                {
                    var bytes = SfzSessionStore.TryConsumeAttachment("scene_mesh");
                    if (bytes != null)
                    {
                        m_PendingLoad = ScannedSceneMeshLoadOperation.StartFromPlyBytes(
                            bytes, k_ArkitToUnity, sessionData.SessionId);
                        m_Started = true;
                        Debug.Log("[SF] SfzSessionStore: scene_mesh loading started.");
                    }
                }

                if (m_PendingLoad == null) return;
                if (!m_PendingLoad.TryComplete(out var mesh)) return;

                if (mesh != null)
                {
                    ScannedSceneMeshBridge.SetMesh(mesh, m_PendingLoad.SceneId);
                    Debug.Log($"[SF] Mesh ready: vertices={mesh.vertexCount} triangles={mesh.triangles.Length / 3}");
                }
                m_PendingLoad = null;
            }

            public void Reset()
            {
                m_PendingLoad = null;
                m_Started     = false;
            }
        }
    }
}
