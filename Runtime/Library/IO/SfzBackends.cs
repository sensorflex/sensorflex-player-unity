// Common ring-buffer backends for Sfz (ZIP archive) and FileIo (loose files).
// SfzBackendBase owns the background load thread and upload queue;
// subclasses implement ReadBytes() to abstract the I/O source.

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    // Shared frame packet used by both ring-buffer backends.
    struct SfzLoadedFrame
    {
        public int    GlobalFrameIndex;
        public int    RecordIndex;     // index into the pre-parsed SfzFrameRecordJson[]
        public byte[] Jpg;
        public byte[] DepthBin;
    }

    internal abstract class SfzBackendBase : IFrameLoaderBackend
    {
        const int UploadBatchSize = 3;

        protected ARSensorFlexSession m_Session;
        protected IFrameLoaderState   m_State;
        int    m_FramesToWait;
        volatile bool m_StopLoading;
        Thread m_LoadThread;
        System.Collections.Concurrent.ConcurrentQueue<SfzLoadedFrame> m_UploadQueue;
        int  m_UploadedFrames;
        bool m_LoggedFirstEnqueue;
        bool m_LoggedFirstUpload;
        bool m_LoggedReady;

        SfzUtils.SfzFrameRecordJson[] m_FrameRecords;

        public void Start(ARSensorFlexSession session, IFrameLoaderState state, int framesToWait)
        {
            m_Session      = session;
            m_State        = state;
            m_FramesToWait = framesToWait;
            m_StopLoading  = false;
            m_UploadedFrames = 0;
            m_LoggedFirstEnqueue = false;
            m_LoggedFirstUpload  = false;
            m_LoggedReady        = false;

            if (!TryReadSessionJson(out var sessionJson))
                return;

            var framesTrack = sessionJson?.tracks?.frames;
            if (framesTrack?.data == null || framesTrack.data.Length == 0)
            {
                Debug.LogError("[SF] SFZ session.json: frames track missing or empty.");
                return;
            }

            m_FrameRecords         = framesTrack.data;
            state.TotalFrames      = m_FrameRecords.Length;
            state.FrameInterval    = 1.0 / Math.Max(1, framesTrack.metadata?.fps ?? 30);
            state.CoordConvMatrix  = Matrix4x4.identity;
            state.UseNegativeZForwardOpticalAxis = false;
            state.AllocateRingBuffer();

            m_UploadQueue = new System.Collections.Concurrent.ConcurrentQueue<SfzLoadedFrame>();
            m_LoadThread  = new Thread(LoadFrames) { IsBackground = true, Name = "SF-SfzLoader" };
            m_LoadThread.Start();

            Debug.Log($"[SF] {BackendLabel} streaming started. frames={state.TotalFrames} fps={framesTrack.metadata?.fps} bufSize={state.BufSize}");
        }

        // Subclass contract ──────────────────────────────────────────────────────

        protected abstract string BackendLabel { get; }

        // Returns false and logs an error on failure.
        protected abstract bool TryReadSessionJson(out SfzUtils.SfzSessionJson sessionJson);

        // Returns the file bytes for a path relative to the session root, or null if missing.
        protected abstract byte[] ReadSessionFile(string relativePath);

        // ────────────────────────────────────────────────────────────────────────

        void LoadFrames()
        {
            bool looping  = m_Session.LoopSequence;
            int  iteration = 0;

            while (!m_StopLoading)
            {
                int globalOffset = iteration * m_State.TotalFrames;
                BeginLoadPass();
                try
                {
                    for (int i = 0; i < m_FrameRecords.Length && !m_StopLoading; i++)
                    {
                        var record = m_FrameRecords[i];
                        if (string.IsNullOrEmpty(record.rgb?.file))
                            continue;

                        byte[] jpg   = ReadSessionFile(record.rgb.file);
                        byte[] depth = !string.IsNullOrEmpty(record.depth?.file)
                            ? ReadSessionFile(record.depth.file)
                            : null;

                        if (jpg == null)
                            continue;

                        Enqueue(globalOffset + i, i, jpg, depth);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[SF] {BackendLabel} loader error: {exception}");
                    return;
                }
                finally
                {
                    EndLoadPass();
                }

                if (!looping)
                    break;

                iteration++;
            }
        }

        // Called once at the start of each pass; subclasses can open shared resources here.
        protected virtual void BeginLoadPass() { }

        // Called after each pass (even on exception). Pair with BeginLoadPass().
        protected virtual void EndLoadPass() { }

        void Enqueue(int globalFrameIndex, int recordIndex, byte[] jpg, byte[] depth)
        {
            while (!m_StopLoading && globalFrameIndex - m_State.PlayHead >= m_State.BufSize)
                Thread.Sleep(1);

            if (m_StopLoading)
                return;

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
            if (m_UploadQueue == null)
                return;

            int uploaded = 0;
            while (uploaded < UploadBatchSize && m_UploadQueue.TryDequeue(out var item))
            {
                int slot = item.GlobalFrameIndex % m_State.BufSize;

                if (m_State.Frames[slot] == null)
                    m_State.Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                m_State.Frames[slot].LoadImage(item.Jpg);
                m_State.Frames[slot].Apply();
                m_State.DepthBins[slot] = item.DepthBin;

                if (m_FrameRecords != null &&
                    item.RecordIndex >= 0 &&
                    item.RecordIndex < m_FrameRecords.Length)
                {
                    var record = m_FrameRecords[item.RecordIndex];
                    if (record.camera?.pose != null)
                        m_State.Poses[slot] = SfzUtils.SfzPoseToMatrix4x4(record.camera.pose);
                    if (record.camera?.intrinsics != null)
                        m_State.Intrinsics[slot] = SfzUtils.SfzIntrinsicsToVector4(record.camera.intrinsics);
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
    }

    internal sealed class SfzFrameLoaderBackend : SfzBackendBase
    {
        string     m_ArchivePath;
        ZipArchive m_PassArchive;

        protected override string BackendLabel => "SFZ";

        protected override bool TryReadSessionJson(out SfzUtils.SfzSessionJson sessionJson)
        {
            sessionJson = null;

            m_ArchivePath = m_Session.SfzFilePath;
            if (!Path.IsPathRooted(m_ArchivePath))
                m_ArchivePath = Path.Combine(Application.streamingAssetsPath, m_ArchivePath);

            if (!File.Exists(m_ArchivePath))
            {
                Debug.LogError($"[SF] SFZ archive not found: {m_ArchivePath}");
                return false;
            }

            try
            {
                using var archive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
                var entry = archive.GetEntry("session/session.json");
                if (entry == null)
                {
                    Debug.LogError("[SF] SFZ: session/session.json not found in archive.");
                    return false;
                }

                string json;
                using (var sr = new StreamReader(entry.Open()))
                    json = sr.ReadToEnd();

                sessionJson = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(json);
                return sessionJson != null;
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] SFZ: failed to read session.json: " + e);
                return false;
            }
        }

        // Keep the archive open for the entire pass so the central directory is only parsed once.
        protected override void BeginLoadPass()
        {
            m_PassArchive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
        }

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

    internal sealed class FileIoFrameLoaderBackend : SfzBackendBase
    {
        string m_SessionDir;

        protected override string BackendLabel => "FileIo";

        protected override bool TryReadSessionJson(out SfzUtils.SfzSessionJson sessionJson)
        {
            sessionJson = null;

            m_SessionDir = m_Session.FileIoPath;
            if (!Path.IsPathRooted(m_SessionDir))
                m_SessionDir = Path.Combine(Application.streamingAssetsPath, m_SessionDir);

            string jsonPath = Path.Combine(m_SessionDir, "session.json");
            if (!File.Exists(jsonPath))
            {
                Debug.LogError($"[SF] FileIo: session.json not found at {jsonPath}");
                return false;
            }

            try
            {
                sessionJson = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(File.ReadAllText(jsonPath));
                return sessionJson != null;
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] FileIo: failed to read session.json: " + e);
                return false;
            }
        }

        protected override byte[] ReadSessionFile(string relativePath)
        {
            string fullPath = Path.Combine(m_SessionDir, relativePath);
            if (!File.Exists(fullPath))
                return null;

            try   { return File.ReadAllBytes(fullPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] FileIo: failed to read {relativePath}: {e.Message}");
                return null;
            }
        }
    }
}
