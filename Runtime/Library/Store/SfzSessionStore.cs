// SfzSessionStore.cs — session store for SFZ (ZIP archive) and FileIo (loose file) sessions.
//
// Single source of truth for SFZ-mode session lifecycle and data access.
// CameraSubsystem drives it through StartSession / Tick / StopSessionAsync;
// OcclusionSubsystem, ControlBridge, and other consumers read state through
// the proxy properties — never touching IO types directly.
//
// Private nested types (all invisible outside this file):
//   IBackendState / BackendState  — ring-buffer state contract + implementation
//   ISessionBackend               — three-phase backend contract (Open / TryGetSessionJson / StartLoading)
//   SessionLoadState              — session lifecycle enum (Idle / Waiting / LoadingAttachments / Loading / Ready)
//   SfzTrackInfo                  — per-track metadata (name, sample interval, record count)
//   SfzAttachmentInfo             — per-attachment metadata (name, file, format)
//   SfzSessionData                — parsed session.json aggregate
//   SfzSessionJson / DTOs         — session.json deserialization types
//   SfzPoseToMatrix4x4            — SFZ pose record → Matrix4x4
//   SfzIntrinsicsToVector4        — SFZ intrinsics record → Vector4
//   ReadZipEntry                  — drains a ZipArchiveEntry into byte[]
//   SfzBackendBase                — ring-buffer streaming on a background thread
//   SfzFileBackend                — SFZ (ZIP archive) source
//   FileIoBackend                 — FileIo (loose files) source
//   ScannedMeshLoaderImpl         — polls for scene_mesh attachment, drives PLY parse
//   ScannedSceneMeshLoadOperation — wraps Task-based PLY parse; TryComplete() polled each frame
//   ScannedSceneMeshData          — parsed vertex/normal/color/triangle arrays
//   PlyMeshReader                 — ASCII and binary-little-endian PLY decoder

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

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
        internal static bool IsActive             => s_LoadState != SessionLoadState.Idle;
        internal static bool IsLoadingAttachments => s_LoadState == SessionLoadState.LoadingAttachments;
        internal static bool IsReady              => s_LoadState == SessionLoadState.Ready;

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
                        s_LoadState = SessionLoadState.LoadingAttachments;
                        Debug.Log($"[SF] SfzSessionStore: session parsed. id={s_SessionData.SessionId} " +
                                  $"tracks={s_SessionData.Tracks.Count} attachments={s_SessionData.Attachments.Count}");
                    }
                    break;

                case SessionLoadState.LoadingAttachments:
                    s_MeshLoader.Tick();
                    if (s_MeshLoader.IsComplete)
                    {
                        s_Backend.StartLoading(s_SessionData, s_BufSize, s_FramesToWait);
                        s_LoadState = SessionLoadState.Loading;
                        Debug.Log("[SF] SfzSessionStore: attachments ready — starting frame streaming.");
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

            var raw = JsonUtility.FromJson<SfzSessionJson>(json);
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

        // ── Nested: Ring buffer state ─────────────────────────────────────────

        interface IBackendState
        {
            double    FrameInterval                  { get; set; }
            Matrix4x4 CoordConvMatrix                { get; set; }
            bool      UseNegativeZForwardOpticalAxis  { get; set; }
            int       TotalFrames                    { get; set; }
            int       BufSize                        { get; }
            bool      IsReady                        { get; set; }
            Texture2D[]  Frames      { get; set; }
            byte[][]     DepthBins   { get; set; }
            Matrix4x4[]  Poses       { get; set; }
            Vector4[]    Intrinsics  { get; set; }
            bool[]       SlotReady   { get; set; }
            int[]        SlotGlobalIdx { get; set; }
            int  PlayHead           { get; set; }
            int  LatestGlobalIndex  { get; set; }
            int  PendingDecodeCount { get; set; }
            void AllocateRingBuffer();
            void MarkBuffered(int framesToWait);
            void DestroyTextures();
        }

        sealed class BackendState : IBackendState
        {
            int m_PlayHead;
            int m_BufferedFrames;

            public double    FrameInterval                 { get; set; }
            public Matrix4x4 CoordConvMatrix               { get; set; } = Matrix4x4.identity;
            public bool      UseNegativeZForwardOpticalAxis { get; set; }
            public int       TotalFrames                   { get; set; } = int.MaxValue;
            public int       BufSize                       { get; }
            public bool      IsReady                       { get; set; }
            public Texture2D[]  Frames      { get; set; }
            public byte[][]     DepthBins   { get; set; }
            public Matrix4x4[]  Poses       { get; set; }
            public Vector4[]    Intrinsics  { get; set; }
            public bool[]       SlotReady   { get; set; }
            public int[]        SlotGlobalIdx { get; set; }
            public int PendingDecodeCount   { get; set; }

            public int PlayHead
            {
                get => Volatile.Read(ref m_PlayHead);
                set => Volatile.Write(ref m_PlayHead, value);
            }

            int m_LatestGlobalIndex = -1;

            public int LatestGlobalIndex
            {
                get => Volatile.Read(ref m_LatestGlobalIndex);
                set => Volatile.Write(ref m_LatestGlobalIndex, value);
            }

            public BackendState(int bufSize) { BufSize = bufSize; }

            public void AllocateRingBuffer()
            {
                Frames        = new Texture2D[BufSize];
                DepthBins     = new byte[BufSize][];
                Poses         = new Matrix4x4[BufSize];
                Intrinsics    = new Vector4[BufSize];
                SlotReady     = new bool[BufSize];
                SlotGlobalIdx = new int[BufSize];

                for (int i = 0; i < BufSize; i++)
                    SlotGlobalIdx[i] = -1;
            }

            public void MarkBuffered(int framesToWait)
            {
                m_BufferedFrames++;
                if (!IsReady && m_BufferedFrames >= framesToWait)
                    IsReady = true;
            }

            public void DestroyTextures()
            {
                if (Frames == null) return;
                for (int i = 0; i < Frames.Length; i++)
                {
                    if (Frames[i] == null) continue;
                    UnityEngine.Object.Destroy(Frames[i]);
                    Frames[i] = null;
                }
            }
        }

        // ── Nested: Session contracts ─────────────────────────────────────────

        enum SessionLoadState { Idle, Waiting, LoadingAttachments, Loading, Ready }

        interface ISessionBackend
        {
            /// <summary>Open / validate the data source. Returns false on hard failure.</summary>
            bool Open(ARSensorFlexSession session);

            /// <summary>
            /// Returns session.json text when available; false if still waiting.
            /// Called each Tick() until it returns true.
            /// </summary>
            bool TryGetSessionJson(out string json);

            /// <summary>Allocate ring buffer and start streaming. Called once after parse.</summary>
            void StartLoading(SfzSessionData data, int bufSize, int framesToWait);

            /// <summary>
            /// Returns raw bytes for a named attachment when available, null otherwise.
            /// Calling this consumes the bytes (subsequent calls return null for the same name).
            /// </summary>
            byte[] TryGetAttachmentBytes(string attachmentName);

            /// <summary>Ring-buffer state; non-null after StartLoading.</summary>
            IBackendState State { get; }

            void DrainMainThreadWork();
            void Dispatch();
            Task StopAsync();
        }

        // ── Nested: Session data model ────────────────────────────────────────

        sealed class SfzTrackInfo
        {
            public string Name           { get; }
            public double SampleInterval { get; }
            public int    RecordCount    { get; }

            internal SfzTrackInfo(string name, double sampleInterval, int recordCount)
            { Name = name; SampleInterval = sampleInterval; RecordCount = recordCount; }
        }

        sealed class SfzAttachmentInfo
        {
            public string Name   { get; }
            public string File   { get; }
            public string Format { get; }

            internal SfzAttachmentInfo(string name, string file, string format)
            { Name = name; File = file; Format = format; }
        }

        sealed class SfzSessionData
        {
            public string SessionId { get; }
            public IReadOnlyDictionary<string, SfzTrackInfo>       Tracks      { get; }
            public IReadOnlyDictionary<string, SfzAttachmentInfo>  Attachments { get; }
            internal SfzFrameRecordJson[] FrameRecords { get; }

            internal SfzSessionData(
                string sessionId,
                IReadOnlyDictionary<string, SfzTrackInfo>      tracks,
                IReadOnlyDictionary<string, SfzAttachmentInfo> attachments,
                SfzFrameRecordJson[]                  frameRecords)
            {
                SessionId    = sessionId ?? "session";
                Tracks       = tracks      ?? new Dictionary<string, SfzTrackInfo>();
                Attachments  = attachments ?? new Dictionary<string, SfzAttachmentInfo>();
                FrameRecords = frameRecords;
            }
        }

        // ── Nested: session.json DTOs ─────────────────────────────────────────

        [Serializable] class SfzDeviceJson { public string model; public string os; public string ar_framework; }

        [Serializable] class SfzRgbChannelJson   { public int width; public int height; public string format; }
        [Serializable] class SfzDepthChannelJson { public int width; public int height; public string format; public float invalid_value; }
        [Serializable] class SfzChannelsJson     { public SfzRgbChannelJson rgb; public SfzDepthChannelJson depth; }

        [Serializable] class SfzFramesMetadataJson { public int fps; public SfzChannelsJson channels; }

        [Serializable] class SfzFileRefJson         { public string file; }
        [Serializable] class SfzPoseJson            { public float[] position; public float[] rotation; }
        [Serializable] class SfzIntrinsicsJson      { public float fx; public float fy; public float cx; public float cy; }
        [Serializable] class SfzLightEstimationJson { public float ambient_intensity; public float color_temperature; }
        [Serializable] class SfzCameraJson          { public SfzPoseJson pose; public SfzIntrinsicsJson intrinsics; }
        [Serializable] class SfzFrameRecordJson
        {
            public long                   timestamp_ns;
            public SfzCameraJson          camera;
            public SfzLightEstimationJson light_estimation;
            public SfzFileRefJson         rgb;
            public SfzFileRefJson         depth;
        }
        [Serializable] class SfzFramesTrackJson { public SfzFramesMetadataJson metadata; public SfzFrameRecordJson[] data; }

        [Serializable] class SfzImuMetadataJson { public float sample_rate_hz; }
        [Serializable] class SfzImuSampleJson
        {
            public long    timestamp_ns;
            public float[] acceleration;
            public float[] rotation_rate;
            public float[] gravity;
        }
        [Serializable] class SfzImuTrackJson { public SfzImuMetadataJson metadata; public SfzImuSampleJson[] data; }

        [Serializable] class SfzTracksJson    { public SfzFramesTrackJson frames; public SfzImuTrackJson imu; }

        [Serializable] class SfzMeshAttachmentJson { public string file; public string format; }
        [Serializable] class SfzAttachmentsJson    { public SfzMeshAttachmentJson scene_mesh; }

        [Serializable] class SfzSessionJson
        {
            public string              version;
            public string              session_id;
            public string              start_time_utc;
            public SfzDeviceJson       device;
            public SfzTracksJson       tracks;
            public SfzAttachmentsJson  attachments;
        }

        // ── Nested: SFZ conversion helpers ───────────────────────────────────

        static Matrix4x4 SfzPoseToMatrix4x4(SfzPoseJson pose)
        {
            if (pose?.position == null || pose.position.Length < 3 ||
                pose.rotation  == null || pose.rotation.Length  < 4)
                return Matrix4x4.identity;
            return Matrix4x4.TRS(
                new Vector3(pose.position[0], pose.position[1], pose.position[2]),
                new Quaternion(pose.rotation[0], pose.rotation[1], pose.rotation[2], pose.rotation[3]),
                Vector3.one);
        }

        static Vector4 SfzIntrinsicsToVector4(SfzIntrinsicsJson intr) =>
            intr != null ? new Vector4(intr.fx, intr.fy, intr.cx, intr.cy) : Vector4.zero;

        static byte[] ReadZipEntry(ZipArchiveEntry entry)
        {
            var buf = new byte[(int)entry.Length];
            using var s = entry.Open();
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return buf;
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
                            m_State.Poses[slot] = SfzPoseToMatrix4x4(rec.camera.pose);
                        if (rec.camera?.intrinsics != null)
                            m_State.Intrinsics[slot] = SfzIntrinsicsToVector4(rec.camera.intrinsics);
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
                if (s_SessionData == null ||
                    !s_SessionData.Attachments.TryGetValue(attachmentName, out var att) ||
                    string.IsNullOrEmpty(att.File))
                    return null;

                try
                {
                    using var archive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
                    var entry = archive.GetEntry($"session/{att.File}");
                    return entry != null ? ReadZipEntry(entry) : null;
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
                    return entry != null ? ReadZipEntry(entry) : null;
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
                if (s_SessionData == null ||
                    !s_SessionData.Attachments.TryGetValue(attachmentName, out var att) ||
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

        // ── Nested: ScannedSceneMeshLoadOperation ─────────────────────────────

        sealed class ScannedSceneMeshLoadOperation
        {
            readonly Task<ScannedSceneMeshData> m_Task;
            readonly string m_SceneId;

            public bool   IsCompleted => m_Task.IsCompleted;
            public string SceneId     => m_SceneId;

            ScannedSceneMeshLoadOperation(Task<ScannedSceneMeshData> task, string sceneId)
            { m_Task = task; m_SceneId = sceneId; }

            public static ScannedSceneMeshLoadOperation StartFromPlyBytes(
                byte[] plyBytes, Matrix4x4 coordConvMatrix, string sceneId)
            {
                var task = Task.Run(() => PlyMeshReader.Parse(plyBytes, coordConvMatrix));
                return new ScannedSceneMeshLoadOperation(task, sceneId);
            }

            public bool TryComplete(out Mesh mesh)
            {
                mesh = null;
                if (!m_Task.IsCompleted) return false;

                if (m_Task.IsFaulted)
                {
                    Debug.LogError("[SF] Scanned mesh load failed: " + m_Task.Exception?.GetBaseException());
                    return true;
                }

                var data = m_Task.Result;
                if (data == null) return true;

                mesh = BuildUnityMesh(data, m_SceneId);
                return true;
            }

            static Mesh BuildUnityMesh(ScannedSceneMeshData data, string sceneId)
            {
                var mesh = new Mesh { name = $"SensorFlexScannedMesh-{sceneId}" };
                mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.vertices  = data.Vertices;
                mesh.triangles = data.Triangles;
                if (data.Colors  != null && data.Colors.Length  == data.Vertices.Length) mesh.colors32 = data.Colors;
                if (data.Normals != null && data.Normals.Length == data.Vertices.Length) mesh.normals  = data.Normals;
                else mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // ── Nested: ScannedSceneMeshData ──────────────────────────────────────

        sealed class ScannedSceneMeshData
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Color32[] Colors;
            public int[]     Triangles;
        }

        // ── Nested: PlyMeshReader ─────────────────────────────────────────────

        static class PlyMeshReader
        {
            enum PlyFormat { Ascii, BinaryLittleEndian }

            sealed class Header
            {
                public PlyFormat Format;
                public int HeaderBytes;
                public int VertexCount;
                public int FaceCount;
                public readonly List<PlyProperty> VertexProperties = new();
                public PlyProperty FaceListProperty;
            }

            sealed class PlyProperty
            {
                public string Name;
                public string Type;
                public string CountType;
                public bool   IsList;
            }

            public static ScannedSceneMeshData Parse(byte[] bytes, Matrix4x4 coordConvMatrix)
            {
                var header = ParseHeader(bytes);
                return header.Format == PlyFormat.Ascii
                    ? ParseAscii(bytes, header, coordConvMatrix)
                    : ParseBinaryLittleEndian(bytes, header, coordConvMatrix);
            }

            static Header ParseHeader(byte[] bytes)
            {
                int headerEnd = FindHeaderEnd(bytes);
                string headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                var lines = headerText.Split('\n');
                var header = new Header { HeaderBytes = headerEnd };

                bool inVertexElement = false;
                bool inFaceElement   = false;
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.StartsWith("format ", StringComparison.Ordinal))
                    {
                        if (line.Contains("ascii"))
                            header.Format = PlyFormat.Ascii;
                        else if (line.Contains("binary_little_endian"))
                            header.Format = PlyFormat.BinaryLittleEndian;
                        else
                            throw new NotSupportedException($"Unsupported PLY format line: {line}");
                    }
                    else if (line.StartsWith("element ", StringComparison.Ordinal))
                    {
                        inVertexElement = false;
                        inFaceElement   = false;
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) continue;
                        if (parts[1] == "vertex")
                        { header.VertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture); inVertexElement = true; }
                        else if (parts[1] == "face")
                        { header.FaceCount = int.Parse(parts[2], CultureInfo.InvariantCulture); inFaceElement = true; }
                    }
                    else if (line.StartsWith("property ", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (inVertexElement)
                            header.VertexProperties.Add(new PlyProperty { Name = parts[^1], Type = parts[1] });
                        else if (inFaceElement && parts.Length >= 5 && parts[1] == "list")
                            header.FaceListProperty = new PlyProperty { IsList = true, CountType = parts[2], Type = parts[3], Name = parts[4] };
                    }
                }

                if (header.VertexCount <= 0)   throw new InvalidOperationException("PLY mesh has no vertices.");
                if (header.FaceCount   <= 0)   throw new InvalidOperationException("PLY mesh has no faces.");
                if (header.FaceListProperty == null) throw new NotSupportedException("PLY face list property is missing.");
                return header;
            }

            static int FindHeaderEnd(byte[] bytes)
            {
                var marker = Encoding.ASCII.GetBytes("end_header\n");
                int idx = IndexOf(bytes, marker);
                if (idx >= 0) return idx + marker.Length;

                marker = Encoding.ASCII.GetBytes("end_header\r\n");
                idx = IndexOf(bytes, marker);
                if (idx >= 0) return idx + marker.Length;

                throw new InvalidOperationException("PLY header terminator not found.");
            }

            static ScannedSceneMeshData ParseAscii(byte[] bytes, Header header, Matrix4x4 coordConvMatrix)
            {
                using var reader = new StringReader(Encoding.ASCII.GetString(bytes, header.HeaderBytes, bytes.Length - header.HeaderBytes));
                var    vertices = new Vector3[header.VertexCount];
                Vector3[] normals = TryAllocateNormals(header);
                Color32[] colors  = TryAllocateColors(header);

                for (int i = 0; i < header.VertexCount; i++)
                    vertices[i] = ReadVertex(ReadTokens(reader), header.VertexProperties, ref normals, ref colors, i);

                var triangles = new List<int>(header.FaceCount * 3);
                for (int i = 0; i < header.FaceCount; i++)
                {
                    var parts = ReadTokens(reader);
                    int count = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    TriangulateFace(parts, 1, count, triangles);
                }

                ApplyCoordinateConversion(vertices, normals, triangles, coordConvMatrix);
                return new ScannedSceneMeshData { Vertices = vertices, Normals = normals, Colors = colors, Triangles = triangles.ToArray() };
            }

            static ScannedSceneMeshData ParseBinaryLittleEndian(byte[] bytes, Header header, Matrix4x4 coordConvMatrix)
            {
                var    vertices = new Vector3[header.VertexCount];
                Vector3[] normals = TryAllocateNormals(header);
                Color32[] colors  = TryAllocateColors(header);

                using var ms = new MemoryStream(bytes, header.HeaderBytes, bytes.Length - header.HeaderBytes, false);
                using var br = new BinaryReader(ms);

                for (int i = 0; i < header.VertexCount; i++)
                    vertices[i] = ReadBinaryVertex(br, header.VertexProperties, ref normals, ref colors, i);

                var triangles = new List<int>(header.FaceCount * 3);
                for (int i = 0; i < header.FaceCount; i++)
                {
                    int count   = (int)ReadScalar(br, header.FaceListProperty.CountType);
                    var indices = new int[count];
                    for (int j = 0; j < count; j++)
                        indices[j] = (int)ReadScalar(br, header.FaceListProperty.Type);
                    TriangulateFace(indices, triangles);
                }

                ApplyCoordinateConversion(vertices, normals, triangles, coordConvMatrix);
                return new ScannedSceneMeshData { Vertices = vertices, Normals = normals, Colors = colors, Triangles = triangles.ToArray() };
            }

            static string[] ReadTokens(StringReader reader)
            {
                string line;
                do
                {
                    line = reader.ReadLine();
                    if (line == null) throw new EndOfStreamException("Unexpected end of ASCII PLY stream.");
                    line = line.Trim();
                }
                while (line.Length == 0);
                return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            }

            static Vector3[] TryAllocateNormals(Header header)
            {
                foreach (var p in header.VertexProperties)
                    if (p.Name == "nx" || p.Name == "ny" || p.Name == "nz")
                        return new Vector3[header.VertexCount];
                return null;
            }

            static Color32[] TryAllocateColors(Header header)
            {
                bool r = false, g = false, b = false;
                foreach (var p in header.VertexProperties)
                {
                    if (p.Name == "red")   r = true;
                    if (p.Name == "green") g = true;
                    if (p.Name == "blue")  b = true;
                }
                return r && g && b ? new Color32[header.VertexCount] : null;
            }

            static Vector3 ReadVertex(string[] parts, List<PlyProperty> props, ref Vector3[] normals, ref Color32[] colors, int idx)
            {
                float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0;
                byte red = 255, green = 255, blue = 255, alpha = 255;
                for (int i = 0; i < props.Count && i < parts.Length; i++)
                {
                    switch (props[i].Name)
                    {
                        case "x":     x     = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "y":     y     = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "z":     z     = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "nx":    nx    = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "ny":    ny    = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "nz":    nz    = float.Parse(parts[i], CultureInfo.InvariantCulture); break;
                        case "red":   red   = byte.Parse(parts[i],  CultureInfo.InvariantCulture); break;
                        case "green": green = byte.Parse(parts[i],  CultureInfo.InvariantCulture); break;
                        case "blue":  blue  = byte.Parse(parts[i],  CultureInfo.InvariantCulture); break;
                        case "alpha": alpha = byte.Parse(parts[i],  CultureInfo.InvariantCulture); break;
                    }
                }
                if (normals != null) normals[idx] = new Vector3(nx, ny, nz);
                if (colors  != null) colors[idx]  = new Color32(red, green, blue, alpha);
                return new Vector3(x, y, z);
            }

            static Vector3 ReadBinaryVertex(BinaryReader br, List<PlyProperty> props, ref Vector3[] normals, ref Color32[] colors, int idx)
            {
                float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0;
                byte red = 255, green = 255, blue = 255, alpha = 255;
                for (int i = 0; i < props.Count; i++)
                {
                    switch (props[i].Name)
                    {
                        case "x":     x     = (float)ReadScalar(br, props[i].Type); break;
                        case "y":     y     = (float)ReadScalar(br, props[i].Type); break;
                        case "z":     z     = (float)ReadScalar(br, props[i].Type); break;
                        case "nx":    nx    = (float)ReadScalar(br, props[i].Type); break;
                        case "ny":    ny    = (float)ReadScalar(br, props[i].Type); break;
                        case "nz":    nz    = (float)ReadScalar(br, props[i].Type); break;
                        case "red":   red   = (byte)ReadScalar(br, props[i].Type);  break;
                        case "green": green = (byte)ReadScalar(br, props[i].Type);  break;
                        case "blue":  blue  = (byte)ReadScalar(br, props[i].Type);  break;
                        case "alpha": alpha = (byte)ReadScalar(br, props[i].Type);  break;
                        default:      ReadScalar(br, props[i].Type);                break;
                    }
                }
                if (normals != null) normals[idx] = new Vector3(nx, ny, nz);
                if (colors  != null) colors[idx]  = new Color32(red, green, blue, alpha);
                return new Vector3(x, y, z);
            }

            static double ReadScalar(BinaryReader br, string type) => type switch
            {
                "char"   or "int8"    => br.ReadSByte(),
                "uchar"  or "uint8"   => br.ReadByte(),
                "short"  or "int16"   => br.ReadInt16(),
                "ushort" or "uint16"  => br.ReadUInt16(),
                "int"    or "int32"   => br.ReadInt32(),
                "uint"   or "uint32"  => br.ReadUInt32(),
                "float"  or "float32" => br.ReadSingle(),
                "double" or "float64" => br.ReadDouble(),
                _ => throw new NotSupportedException($"Unsupported PLY scalar type '{type}'.")
            };

            static void TriangulateFace(string[] parts, int start, int count, List<int> triangles)
            {
                if (count < 3) return;
                int first = int.Parse(parts[start], CultureInfo.InvariantCulture);
                for (int i = 1; i < count - 1; i++)
                {
                    triangles.Add(first);
                    triangles.Add(int.Parse(parts[start + i],     CultureInfo.InvariantCulture));
                    triangles.Add(int.Parse(parts[start + i + 1], CultureInfo.InvariantCulture));
                }
            }

            static void TriangulateFace(int[] indices, List<int> triangles)
            {
                if (indices.Length < 3) return;
                int first = indices[0];
                for (int i = 1; i < indices.Length - 1; i++)
                {
                    triangles.Add(first);
                    triangles.Add(indices[i]);
                    triangles.Add(indices[i + 1]);
                }
            }

            static void ApplyCoordinateConversion(Vector3[] vertices, Vector3[] normals, List<int> triangles, Matrix4x4 m)
            {
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = m.MultiplyPoint3x4(vertices[i]);
                if (normals != null)
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = m.MultiplyVector(normals[i]).normalized;
                if (ChangesHandedness(m))
                    for (int i = 0; i + 2 < triangles.Count; i += 3)
                        (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            }

            static bool ChangesHandedness(Matrix4x4 m)
            {
                var x = m.MultiplyVector(Vector3.right);
                var y = m.MultiplyVector(Vector3.up);
                var z = m.MultiplyVector(Vector3.forward);
                return Vector3.Dot(Vector3.Cross(x, y), z) < 0f;
            }

            static int IndexOf(byte[] haystack, byte[] needle)
            {
                for (int i = 0; i <= haystack.Length - needle.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < needle.Length; j++)
                        if (haystack[i + j] != needle[j]) { match = false; break; }
                    if (match) return i;
                }
                return -1;
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

            public bool IsComplete
            {
                get
                {
                    var data = s_SessionData;
                    if (data == null) return false;
                    if (!data.Attachments.ContainsKey("scene_mesh")) return true;
                    return m_Started && m_PendingLoad == null;
                }
            }

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
