// SfzFrameLoaderBackend and FileIoFrameLoaderBackend — ISessionBackend implementations
// for ZIP-archive and loose-file sessions respectively.
//
// Both share the ring-buffer streaming logic in SfzBackendBase:
//   Open()             — validate the path and store it (subclass)
//   TryGetSessionJson() — read session.json from the source (subclass)
//   StartLoading()     — create own BackendState, start background thread
//   TryGetAttachmentBytes() — one-shot read of an attachment file (subclass)
//
// The background thread (LoadFrames) loops over frame records and blocks
// when the ring buffer is full, throttled by PlayHead.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    struct SfzLoadedFrame
    {
        public int    GlobalFrameIndex;
        public int    RecordIndex;
        public byte[] Jpg;
        public byte[] DepthBin;
    }

    internal abstract class SfzBackendBase : ISessionBackend
    {
        const int UploadBatchSize = 3;

        protected ARSensorFlexSession m_Session;
        protected IBackendState   m_State;
        int    m_FramesToWait;
        volatile bool m_StopLoading;
        Thread m_LoadThread;
        ConcurrentQueue<SfzLoadedFrame> m_UploadQueue;
        int  m_UploadedFrames;
        bool m_LoggedFirstEnqueue;
        bool m_LoggedFirstUpload;
        bool m_LoggedReady;

        protected SfzSessionData m_SessionData;

        // ── ISessionBackend — Phase 1 ─────────────────────────────────────────

        public abstract bool Open(ARSensorFlexSession session);

        // ── ISessionBackend — Phase 2 ─────────────────────────────────────────

        public abstract bool TryGetSessionJson(out string json);

        // ── ISessionBackend — Phase 3 ─────────────────────────────────────────

        public void StartLoading(SfzSessionData data, int bufSize, int framesToWait)
        {
            m_SessionData  = data;
            m_FramesToWait = framesToWait;
            m_StopLoading  = false;
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
            m_State.TotalFrames   = hasFrames && framesTrack.RecordCount > 0
                ? framesTrack.RecordCount
                : int.MaxValue;
            m_State.FrameInterval = hasFrames ? framesTrack.SampleInterval : 1.0 / 30;
            m_State.CoordConvMatrix               = Matrix4x4.identity;
            m_State.UseNegativeZForwardOpticalAxis = false;
            m_State.AllocateRingBuffer();

            m_UploadQueue = new ConcurrentQueue<SfzLoadedFrame>();
            m_LoadThread  = new Thread(LoadFrames) { IsBackground = true, Name = $"SF-{BackendLabel}" };
            m_LoadThread.Start();

            Debug.Log($"[SF] {BackendLabel} streaming started. frames={m_State.TotalFrames} fps={1.0/m_State.FrameInterval:F0} bufSize={m_State.BufSize}");
        }

        // ── ISessionBackend — attachment bytes ────────────────────────────────

        public abstract byte[] TryGetAttachmentBytes(string attachmentName);

        // ── ISessionBackend — runtime ─────────────────────────────────────────

        public IBackendState State => m_State;

        public void Dispatch() { }

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

        // ── Subclass contract ─────────────────────────────────────────────────

        protected abstract string BackendLabel { get; }

        // Returns bytes for a path relative to the session root using the per-pass
        // open archive handle (only valid during BeginLoadPass / EndLoadPass window).
        protected abstract byte[] ReadSessionFile(string relativePath);

        protected virtual void BeginLoadPass() { }
        protected virtual void EndLoadPass()   { }

        // ── Background loader thread ──────────────────────────────────────────

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

            m_UploadQueue.Enqueue(new SfzLoadedFrame
            {
                GlobalFrameIndex = globalFrameIndex,
                RecordIndex      = recordIndex,
                Jpg              = jpg,
                DepthBin         = depth
            });
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
    }

    // ── SFZ (ZIP archive) backend ─────────────────────────────────────────────

    internal sealed class SfzFrameLoaderBackend : SfzBackendBase
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
                if (entry == null)
                {
                    Debug.LogError("[SF] SFZ: session/session.json not found in archive.");
                    return false;
                }

                using var sr = new StreamReader(entry.Open());
                json = sr.ReadToEnd();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] SFZ: failed to read session.json: " + e);
                return false;
            }
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
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] SFZ: failed to read attachment '{attachmentName}': {e.Message}");
                return null;
            }
        }

        // Keep archive open across the full pass to parse the central directory only once.
        protected override void BeginLoadPass()
            => m_PassArchive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);

        protected override void EndLoadPass()
        {
            m_PassArchive?.Dispose();
            m_PassArchive = null;
        }

        protected override byte[] ReadSessionFile(string relativePath)
        {
            try
            {
                var entry = m_PassArchive?.GetEntry($"session/{relativePath}");
                return entry != null ? SfzUtils.ReadEntry(entry) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] SFZ: failed to read {relativePath}: {e.Message}");
                return null;
            }
        }
    }

    // ── FileIo (loose files) backend ──────────────────────────────────────────

    internal sealed class FileIoFrameLoaderBackend : SfzBackendBase
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
            if (!File.Exists(path))
            {
                Debug.LogError($"[SF] FileIo: session.json not found at {path}");
                return false;
            }

            try   { json = File.ReadAllText(path); return true; }
            catch (Exception e)
            {
                Debug.LogError("[SF] FileIo: failed to read session.json: " + e);
                return false;
            }
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
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] FileIo: failed to read attachment '{attachmentName}': {e.Message}");
                return null;
            }
        }

        protected override byte[] ReadSessionFile(string relativePath)
        {
            string fullPath = Path.Combine(m_SessionDir, relativePath);
            if (!File.Exists(fullPath)) return null;

            try   { return File.ReadAllBytes(fullPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] FileIo: failed to read {relativePath}: {e.Message}");
                return null;
            }
        }
    }
}
