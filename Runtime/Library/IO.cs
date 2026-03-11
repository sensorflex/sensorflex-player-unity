using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    // ── Static ZIP / JSON decoding helpers ───────────────────────────────────────

    /// <summary>
    /// Static helpers for ZIP entry reading, JSON float extraction, matrix packing,
    /// and coordinate-system conversion. Used by <see cref="FrameLoader"/> and
    /// any other subsystem that reads from the SensorFlex archive format.
    /// </summary>
    internal static class IO
    {
        // ── Scene meta.json DTOs ─────────────────────────────────────────────────

        [Serializable] internal class CoordSystem   { public string handedness; public string forward; public string up; }
        [Serializable] internal class SceneMetaJson { public string scene_id; public int n_frames; public int fps; public CoordSystem coordinate_system; }

        // ── ZIP entry reading ────────────────────────────────────────────────────

        /// <summary>Reads the full decompressed content of a ZIP entry into a byte array.</summary>
        internal static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            var buf   = new byte[(int)entry.Length];
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

        // ── JSON helpers ─────────────────────────────────────────────────────────

        // Matches integers, decimals, and scientific-notation floats (including negatives).
        static readonly Regex s_NumRegex = new Regex(@"-?\d+\.?\d*(?:[eE][+-]?\d+)?");

        /// <summary>
        /// Extracts all float values from a named JSON array field.
        /// Works with nested arrays such as <c>"pose": [[a,b],[c,d]]</c>.
        /// </summary>
        internal static float[] ExtractFloatsFromField(string json, string field)
        {
            int keyPos = json.IndexOf($"\"{field}\"", StringComparison.Ordinal);
            if (keyPos < 0) return null;
            int start = json.IndexOf('[', keyPos);
            if (start < 0) return null;

            int depth = 0, end = start;
            for (int i = start; i < json.Length; i++)
            {
                if      (json[i] == '[') depth++;
                else if (json[i] == ']') { if (--depth == 0) { end = i; break; } }
            }

            var matches = s_NumRegex.Matches(json.Substring(start, end - start + 1));
            var result  = new float[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                result[i] = float.Parse(matches[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }

        // ── Matrix helpers ───────────────────────────────────────────────────────

        /// <summary>Packs a flat 16-element row-major float array into a <see cref="Matrix4x4"/>.</summary>
        internal static Matrix4x4 FloatsToMatrix4x4(float[] f)
        {
            var m = new Matrix4x4();
            m.m00=f[0];  m.m01=f[1];  m.m02=f[2];  m.m03=f[3];
            m.m10=f[4];  m.m11=f[5];  m.m12=f[6];  m.m13=f[7];
            m.m20=f[8];  m.m21=f[9];  m.m22=f[10]; m.m23=f[11];
            m.m30=f[12]; m.m31=f[13]; m.m32=f[14]; m.m33=f[15];
            return m;
        }

        // ── Coordinate conversion ────────────────────────────────────────────────

        /// <summary>
        /// Builds the flip matrix C that converts a source coordinate system to
        /// Unity's left-handed (+Y up, +Z forward) convention.
        /// For right-handed -Z-forward sources (ARKit / OpenGL): C = diag(1,1,-1,1).
        /// </summary>
        internal static Matrix4x4 ComputeConversionMatrix(string handedness, string forward)
        {
            if (handedness != "right") return Matrix4x4.identity;
            if (string.IsNullOrEmpty(forward) || forward == "-Z")
                return new Matrix4x4(new Vector4(1,0,0,0), new Vector4(0,1,0,0),
                                     new Vector4(0,0,-1,0), new Vector4(0,0,0,1));
            return Matrix4x4.identity;
        }

        /// <summary>
        /// Converts a camera-to-world matrix from a source coordinate system to
        /// a Unity <see cref="Pose"/> using the symmetric formula M_unity = C * M_source * C.
        /// </summary>
        internal static Pose ConvertToUnityPose(Matrix4x4 source, Matrix4x4 c)
        {
            var m        = c * source * c;
            var position = new Vector3(m.m03, m.m13, m.m23);
            var forward  = new Vector3(m.m02, m.m12, m.m22); // col-2 = camera +Z in Unity
            var up       = new Vector3(m.m01, m.m11, m.m21);
            if (forward == Vector3.zero || up == Vector3.zero)
                return new Pose(position, Quaternion.identity);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }
    }


    // ── Frame loader ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires and buffers frame data from all supported sources (FileSystem, WebSocket, ZIP).
    ///
    /// <para>Usage: create an instance, call <see cref="Start"/>, then each frame on the main
    /// thread call <see cref="DrainUploadQueue"/> (ZIP) and <see cref="DispatchWebSocket"/>
    /// (WebSocket). Teardown: <c>await StopAsync()</c> then <see cref="DestroyTextures"/>.</para>
    ///
    /// <para><b>Depth (ZIP mode):</b> <see cref="DepthBins"/> is populated alongside RGB for every
    /// ZIP frame. Each entry is the raw <c>depth.bin</c> bytes (256×192 float32 LE, row-major,
    /// meters, 0.0 = invalid). <see cref="SensorFlex.Player.Subsystem.OcclusionSubsystem"/> can
    /// decode these via <c>Buffer.BlockCopy</c> into a <c>float[192,256]</c> without additional IO.
    /// Entries are <c>null</c> when depth is absent or the source is not ZIP.</para>
    /// </summary>
    internal sealed class FrameLoader
    {
        // ── Exposed state (read on main thread) ──────────────────────────────────

        /// <summary>Seconds between frames. Set from meta.json (ZIP) or settings (other modes).</summary>
        public double FrameInterval { get; private set; }

        /// <summary>Right→left-handed coordinate conversion matrix, derived from scene meta.json.</summary>
        public Matrix4x4 CoordConvMatrix { get; private set; } = Matrix4x4.identity;

        /// <summary>Total frame count. <c>int.MaxValue</c> until known.</summary>
        public int TotalFrames { get; private set; } = int.MaxValue;

        /// <summary>Ring buffer / preload array size.</summary>
        public int BufSize { get; private set; }

        /// <summary>True once enough frames are buffered to begin playback.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Decoded RGB texture per ring-buffer slot (or per preloaded index for FileSystem/WebSocket).</summary>
        public Texture2D[] Frames { get; private set; }

        /// <summary>
        /// Raw <c>depth.bin</c> bytes per ring-buffer slot (ZIP mode only).
        /// Format: 256×192 IEEE 754 float32 little-endian, row-major, meters, 0.0 = invalid.
        /// Decode with: <c>Buffer.BlockCopy(DepthBins[slot], 0, floatArray, 0, bytes.Length)</c>.
        /// <c>null</c> for non-ZIP modes or frames where depth.bin is absent.
        /// </summary>
        public byte[][] DepthBins { get; private set; }

        /// <summary>Per-slot camera-to-world pose matrix (ZIP mode only).</summary>
        public Matrix4x4[] Poses { get; private set; }

        /// <summary>Per-slot camera intrinsics (fx, fy, cx, cy) (ZIP mode only).</summary>
        public Vector4[] Intrinsics { get; private set; }

        /// <summary>Per-slot readiness flag (ZIP ring buffer).</summary>
        public bool[] SlotReady { get; private set; }

        /// <summary>Global frame index stored in each slot (ZIP ring buffer).</summary>
        public int[] SlotGlobalIdx { get; private set; }

        /// <summary>Global frame index currently displayed. Camera advances this as playback progresses.</summary>
        public volatile int PlayHead;


        // ── Private ───────────────────────────────────────────────────────────────

        SensorFlexSettings m_Settings;
        int m_FramesToWait;
        int m_FilledSlots;
        volatile bool m_StopLoading;
        Thread m_LoadThread;
        ConcurrentQueue<LoadedFrame> m_UploadQueue;

        WebSocket m_WebSocket;
        bool m_WsStarted;
        int m_WsExpected;
        int m_WsReceived;

        struct LoadedFrame
        {
            public int    FrameIndex;
            public byte[] Jpg;
            public byte[] Meta;
            public byte[] DepthBin; // null if absent
        }

        const int UploadBatchSize = 3;


        // ── Start ────────────────────────────────────────────────────────────────

        /// <summary>Initialises the loader and begins acquiring frames according to <paramref name="settings"/>.</summary>
        public void Start(SensorFlexSettings settings, int maxFramesToLoad, int framesToWait)
        {
            m_Settings     = settings;
            m_FramesToWait = framesToWait;
            BufSize        = maxFramesToLoad;
            IsReady        = false;
            PlayHead       = 0;
            m_FilledSlots  = 0;
            m_StopLoading  = false;
            FrameInterval  = 1.0 / Math.Max(1, settings.targetFPS);

            switch (settings.frameSourceMode)
            {
                case SensorFlexSettings.FrameSourceMode.FileSystem: StartFileSystem(); break;
                case SensorFlexSettings.FrameSourceMode.Zip:        StartZip();        break;
                case SensorFlexSettings.FrameSourceMode.WebSocket:  StartWebSocket();  break;
            }
        }


        // ── FileSystem ───────────────────────────────────────────────────────────

        void StartFileSystem()
        {
            string folder = m_Settings.imageFolder;
            if (!Path.IsPathRooted(folder))
                folder = Path.Combine(Application.streamingAssetsPath, folder);

            if (!Directory.Exists(folder)) { Debug.LogError($"[SF] Folder not found: {folder}"); return; }

            var files = new List<string>();
            foreach (var f in Directory.GetFiles(folder))
                if (f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    files.Add(f);
            files.Sort(StringComparer.Ordinal);

            if (files.Count == 0) { Debug.LogError($"[SF] No image files in {folder}"); return; }

            int count = Mathf.Min(BufSize, files.Count);
            TotalFrames = count;
            Frames = new Texture2D[count];

            for (int i = 0; i < count; i++)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(File.ReadAllBytes(files[i]));
                tex.Apply();
                Frames[i] = tex;
            }

            IsReady = true;
            Debug.Log($"[SF] FileSystem: loaded {count} frames from {folder}.");
        }


        // ── WebSocket ────────────────────────────────────────────────────────────

        async void StartWebSocket()
        {
            if (m_WsStarted) return;
            m_WsStarted  = true;
            m_WsExpected = BufSize;
            Frames       = new Texture2D[BufSize];

            Debug.Log($"[SF] WebSocket connecting to {m_Settings.webSocketUrl}, requesting {BufSize} frames.");
            m_WebSocket = new WebSocket(m_Settings.webSocketUrl);

            m_WebSocket.OnOpen  += () => { Debug.Log("[SF] WebSocket connected."); _ = m_WebSocket.SendText($"GET_FRAMES {m_WsExpected}"); };
            m_WebSocket.OnError += e  => Debug.LogError("[SF] WebSocket error: " + e);
            m_WebSocket.OnClose += c  => Debug.Log("[SF] WebSocket closed: " + c);

            m_WebSocket.OnMessage += (byte[] msg) =>
            {
                if (msg == null || msg.Length < 5) return;
                int fi = BitConverter.ToInt32(msg, 0);
                if (fi < 0 || fi >= Frames.Length) { Debug.LogWarning($"[SF] WS out-of-range frameIndex={fi}"); return; }

                int imgLen   = msg.Length - 4;
                var imgBytes = new byte[imgLen];
                Buffer.BlockCopy(msg, 4, imgBytes, 0, imgLen);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(imgBytes)) { Debug.LogWarning($"[SF] WS failed to decode frame {fi}"); UnityEngine.Object.Destroy(tex); return; }
                tex.Apply();

                if (Frames[fi] != null) UnityEngine.Object.Destroy(Frames[fi]);
                else m_WsReceived++;

                Frames[fi] = tex;
                if (m_WsReceived >= m_WsExpected)
                {
                    TotalFrames = m_WsExpected;
                    IsReady     = true;
                    Debug.Log($"[SF] WebSocket preload complete: {m_WsReceived}/{m_WsExpected} frames.");
                }
            };

            try { await m_WebSocket.Connect(); }
            catch (Exception e) { Debug.LogError("[SF] WebSocket connect exception: " + e); }
        }

        /// <summary>
        /// Dispatches pending WebSocket messages on the main thread.
        /// No-op on WebGL (messages arrive natively there).
        /// </summary>
        public void DispatchWebSocket()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            m_WebSocket?.DispatchMessageQueue();
#endif
        }


        // ── ZIP ──────────────────────────────────────────────────────────────────

        void StartZip()
        {
            string path = m_Settings.zipFilePath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Application.streamingAssetsPath, path);

            if (!File.Exists(path)) { Debug.LogError($"[SF] ZIP not found: {path}"); return; }

            string sceneId = null;
            try
            {
                using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
                ZipArchiveEntry metaEntry = null;
                foreach (var e in archive.Entries)
                    if (e.Name == "meta.json") { metaEntry = e; break; }

                if (metaEntry == null) { Debug.LogError("[SF] meta.json not found in ZIP"); return; }

                string json;
                using (var sr = new StreamReader(metaEntry.Open())) json = sr.ReadToEnd();

                var meta      = JsonUtility.FromJson<IO.SceneMetaJson>(json);
                sceneId       = meta.scene_id;
                TotalFrames   = meta.n_frames;
                FrameInterval = 1.0 / Math.Max(1, meta.fps);
                CoordConvMatrix = meta.coordinate_system != null
                    ? IO.ComputeConversionMatrix(meta.coordinate_system.handedness, meta.coordinate_system.forward)
                    : Matrix4x4.identity;
                Debug.Log($"[SF] ZIP meta: scene={sceneId} frames={TotalFrames} fps={meta.fps} " +
                          $"handedness={meta.coordinate_system?.handedness} forward={meta.coordinate_system?.forward}");
            }
            catch (Exception e) { Debug.LogError("[SF] ZIP meta read error: " + e); return; }

            Frames        = new Texture2D[BufSize];
            DepthBins     = new byte[BufSize][];
            Poses         = new Matrix4x4[BufSize];
            Intrinsics    = new Vector4[BufSize];
            SlotReady     = new bool[BufSize];
            SlotGlobalIdx = new int[BufSize];
            for (int i = 0; i < BufSize; i++) SlotGlobalIdx[i] = -1;

            m_UploadQueue = new ConcurrentQueue<LoadedFrame>();
            m_LoadThread  = new Thread(() => ZipLoadThread(path, sceneId)) { IsBackground = true, Name = "SF-ZipLoader" };
            m_LoadThread.Start();

            Debug.Log($"[SF] ZIP streaming started. Ring buffer size={BufSize}");
        }

        // Background thread: reads frames from the ZIP and enqueues them.
        void ZipLoadThread(string path, string sceneId)
        {
            bool looping  = m_Settings.loopSequence;
            int iteration = 0;

            while (!m_StopLoading)
            {
                int globalOffset = iteration * TotalFrames;
                try
                {
                    using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
                    for (int fi = 0; fi < TotalFrames && !m_StopLoading; fi++)
                    {
                        string prefix  = $"{sceneId}/frames/{fi:D6}/";
                        var rgbEntry   = archive.GetEntry(prefix + "rgb.jpg");
                        var metaEntry  = archive.GetEntry(prefix + "meta.json");
                        var depthEntry = archive.GetEntry(prefix + "depth.bin");
                        if (rgbEntry == null || metaEntry == null) continue;

                        byte[] jpg   = IO.ReadEntry(rgbEntry);
                        byte[] meta  = IO.ReadEntry(metaEntry);
                        byte[] depth = depthEntry != null ? IO.ReadEntry(depthEntry) : null;

                        Enqueue(globalOffset + fi, jpg, meta, depth);
                    }
                }
                catch (Exception e) { Debug.LogError("[SF] ZIP loader error: " + e); return; }

                if (!looping) break;
                iteration++;
            }
        }

        void Enqueue(int globalFi, byte[] jpg, byte[] meta, byte[] depth)
        {
            while (!m_StopLoading && globalFi - PlayHead >= BufSize)
                Thread.Sleep(1);
            if (!m_StopLoading)
                m_UploadQueue.Enqueue(new LoadedFrame { FrameIndex = globalFi, Jpg = jpg, Meta = meta, DepthBin = depth });
        }

        /// <summary>
        /// Uploads up to <c>UploadBatchSize</c> pending frames from the ZIP loader to GPU.
        /// Must be called on the main thread each frame.
        /// </summary>
        public void DrainUploadQueue()
        {
            if (m_UploadQueue == null) return;
            int uploaded = 0;
            while (uploaded < UploadBatchSize && m_UploadQueue.TryDequeue(out var item))
            {
                int slot = item.FrameIndex % BufSize;

                if (Frames[slot] == null)
                    Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Frames[slot].LoadImage(item.Jpg);
                Frames[slot].Apply();

                // Raw depth bytes — decoded on demand by OcclusionSubsystem
                DepthBins[slot] = item.DepthBin;

                if (item.Meta != null && item.Meta.Length > 0)
                {
                    string json = Encoding.UTF8.GetString(item.Meta);
                    var pose = IO.ExtractFloatsFromField(json, "pose");
                    var intr = IO.ExtractFloatsFromField(json, "intrinsic");
                    if (pose != null && pose.Length >= 16) Poses[slot]      = IO.FloatsToMatrix4x4(pose);
                    if (intr != null && intr.Length >= 9)  Intrinsics[slot] = new Vector4(intr[0], intr[4], intr[2], intr[5]);
                }

                SlotGlobalIdx[slot] = item.FrameIndex;
                SlotReady[slot]     = true;

                m_FilledSlots++;
                if (!IsReady && m_FilledSlots >= m_FramesToWait)
                    IsReady = true;

                uploaded++;
            }
        }


        // ── Stop ─────────────────────────────────────────────────────────────────

        /// <summary>Signals the background loader to stop and awaits the WebSocket close handshake.</summary>
        public async Task StopAsync()
        {
            m_StopLoading = true;
            if (m_LoadThread != null && m_LoadThread.IsAlive)
            {
                m_LoadThread.Join(500);
                m_LoadThread = null;
            }
            m_UploadQueue = null;

            if (m_WebSocket != null)
            {
                try { await m_WebSocket.Close(); }
                catch (Exception e) { Debug.LogWarning("[SF] WebSocket close: " + e); }
                m_WebSocket = null;
            }
        }

        /// <summary>Destroys all GPU textures. Call after <see cref="StopAsync"/>.</summary>
        public void DestroyTextures()
        {
            if (Frames == null) return;
            for (int i = 0; i < Frames.Length; i++)
                if (Frames[i] != null) { UnityEngine.Object.Destroy(Frames[i]); Frames[i] = null; }
        }
    }
}
