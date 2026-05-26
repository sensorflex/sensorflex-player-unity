// FrameLoading.cs — SensorFlex frame ingestion pipeline.
//
// FrameLoader is the single public surface: callers Start() it with an ARSensorFlexSession,
// then each Unity frame call DrainUploadQueue() (main thread GPU upload) and
// DispatchWebSocket() (WebSocket message pump). All source-specific I/O is hidden
// behind IFrameLoaderBackend; the shared mutable buffer lives in IFrameLoaderState /
// FrameLoaderState.
//
// Three backends are supported:
//   FileSystem  — synchronous bulk load from StreamingAssets/<folder>/ at startup.
//                 Allocates a linear Texture2D[]. Simple, no ring buffer needed.
//   Zip         — background thread streams frames from a .zip archive into a ring
//                 buffer. Main thread drains the upload queue in batches of 3 per frame.
//                 Back-pressure: loader thread sleeps when the ring buffer is full.
//   WebSocket   — async connection to a server; receives binary frame packets and JSON
//                 scene/window messages. Same ring-buffer drain pattern as Zip.
//
// Class relationship diagram:
//
//   ┌─────────────────────────────────────────────────────────────────────┐
//   │                          FrameLoader                                │
//   │  (public facade — owns IFrameLoaderState + IFrameLoaderBackend)     │
//   └──────────┬──────────────────────────┬───────────────────────────────┘
//              │ holds                    │ creates via CreateBackend()
//              ▼                          ▼
//   ┌──────────────────────┐   ┌──────────────────────────────────────────┐
//   │  «interface»         │   │  «interface»                             │
//   │  IFrameLoaderState   │   │  IFrameLoaderBackend                     │
//   │  ─────────────────   │   │  ──────────────────                      │
//   │  FrameInterval       │   │  Start(session, state, framesToWait)     │
//   │  TotalFrames         │   │  DrainMainThreadWork()                   │
//   │  BufSize             │   │  Dispatch()                              │
//   │  IsReady             │   │  StopAsync()                             │
//   │  Frames[]            │   └───────────┬──────────────────────────────┘
//   │  DepthBins[]         │               │ implemented by
//   │  Poses[]             │       ┌───────┼──────────────────────────┐
//   │  Intrinsics[]        │       ▼       ▼                          ▼
//   │  SlotReady[]         │  ┌─────────┐ ┌──────────┐ ┌────────────────────┐
//   │  SlotGlobalIdx[]     │  │FileSystem│ │   Zip    │ │    WebSocket       │
//   │  PlayHead            │  │Backend  │ │ Backend  │ │    Backend         │
//   └──────────┬───────────┘  │         │ │          │ │                    │
//              │ implemented by│sync load│ │bg thread │ │async WS conn       │
//              ▼              │ at Start│ │+ ring buf│ │+ ring buf          │
//   ┌──────────────────────┐  └─────────┘ └──────────┘ └────────────────────┘
//   │  FrameLoaderState    │
//   │  ────────────────    │   All three backends read/write IFrameLoaderState.
//   │  (concrete impl)     │   FrameLoader proxies every property read through
//   │  thread-safe PlayHead│   m_State so callers never touch the state directly.
//   └──────────────────────┘

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal interface IFrameLoaderState
    {
        double FrameInterval { get; set; }
        Matrix4x4 CoordConvMatrix { get; set; }
        bool UseNegativeZForwardOpticalAxis { get; set; }
        int TotalFrames { get; set; }
        int BufSize { get; }
        bool IsReady { get; set; }
        Texture2D[] Frames { get; set; }
        byte[][] DepthBins { get; set; }
        Matrix4x4[] Poses { get; set; }
        Vector4[] Intrinsics { get; set; }
        bool[] SlotReady { get; set; }
        int[] SlotGlobalIdx { get; set; }
        int PlayHead { get; set; }
        void AllocateLinearFrames(int count);
        void AllocateRingBuffer();
        void MarkBuffered(int framesToWait);
        void DestroyTextures();
    }

    internal sealed class FrameLoaderState : IFrameLoaderState
    {
        int m_PlayHead;
        int m_BufferedFrames;

        public double FrameInterval { get; set; }
        public Matrix4x4 CoordConvMatrix { get; set; } = Matrix4x4.identity;
        public bool UseNegativeZForwardOpticalAxis { get; set; }
        public int TotalFrames { get; set; } = int.MaxValue;
        public int BufSize { get; }
        public bool IsReady { get; set; }
        public Texture2D[] Frames { get; set; }
        public byte[][] DepthBins { get; set; }
        public Matrix4x4[] Poses { get; set; }
        public Vector4[] Intrinsics { get; set; }
        public bool[] SlotReady { get; set; }
        public int[] SlotGlobalIdx { get; set; }

        public int PlayHead
        {
            get => Volatile.Read(ref m_PlayHead);
            set => Volatile.Write(ref m_PlayHead, value);
        }

        public FrameLoaderState(int bufSize)
        {
            BufSize = bufSize;
        }

        public void AllocateLinearFrames(int count)
        {
            Frames = new Texture2D[count];
        }

        public void AllocateRingBuffer()
        {
            Frames = new Texture2D[BufSize];
            DepthBins = new byte[BufSize][];
            Poses = new Matrix4x4[BufSize];
            Intrinsics = new Vector4[BufSize];
            SlotReady = new bool[BufSize];
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
            if (Frames == null)
                return;

            for (int i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] == null)
                    continue;

                UnityEngine.Object.Destroy(Frames[i]);
                Frames[i] = null;
            }
        }
    }

    internal interface IFrameLoaderBackend
    {
        void Start(ARSensorFlexSession session, IFrameLoaderState state, int framesToWait);
        void DrainMainThreadWork();
        void Dispatch();
        Task StopAsync();
    }

    internal sealed class FrameLoader
    {
        IFrameLoaderState m_State;
        IFrameLoaderBackend m_Backend;

        public double FrameInterval => m_State.FrameInterval;
        public Matrix4x4 CoordConvMatrix => m_State.CoordConvMatrix;
        public bool UseNegativeZForwardOpticalAxis => m_State.UseNegativeZForwardOpticalAxis;
        public int TotalFrames => m_State.TotalFrames;
        public int BufSize => m_State.BufSize;
        public bool IsReady => m_State.IsReady;
        public Texture2D[] Frames => m_State.Frames;
        public byte[][] DepthBins => m_State.DepthBins;
        public Matrix4x4[] Poses => m_State.Poses;
        public Vector4[] Intrinsics => m_State.Intrinsics;
        public bool[] SlotReady => m_State.SlotReady;
        public int[] SlotGlobalIdx => m_State.SlotGlobalIdx;

        public int PlayHead
        {
            get => m_State.PlayHead;
            set => m_State.PlayHead = value;
        }

        public FrameLoader()
        {
            m_State = new FrameLoaderState(0);
        }

        public void Start(ARSensorFlexSession session, int maxFramesToLoad, int framesToWait)
        {
            if (session == null)
                throw new InvalidOperationException("[SF] FrameLoader.Start() requires an active ARSensorFlexSession.");

            var state = new FrameLoaderState(maxFramesToLoad)
            {
                IsReady = false,
                PlayHead = -1,
                FrameInterval = 1.0 / Math.Max(1, session.TargetFPS)
            };

            m_Backend = CreateBackend(session.SourceMode);
            m_State = state;
            m_Backend.Start(session, state, framesToWait);
        }

        public void DrainUploadQueue()
        {
            m_Backend?.DrainMainThreadWork();
        }

        public void DispatchWebSocket()
        {
            m_Backend?.Dispatch();
        }

        public async Task StopAsync()
        {
            if (m_Backend != null)
                await m_Backend.StopAsync();
        }

        public void DestroyTextures()
        {
            m_State.DestroyTextures();
        }

        static IFrameLoaderBackend CreateBackend(ARSensorFlexSession.FrameSourceMode mode)
        {
            return mode switch
            {
                ARSensorFlexSession.FrameSourceMode.FileSystem => new FileSystemFrameLoaderBackend(),
                ARSensorFlexSession.FrameSourceMode.Sfz        => new SfzFrameLoaderBackend(),
                ARSensorFlexSession.FrameSourceMode.FileIo     => new FileIoFrameLoaderBackend(),
                ARSensorFlexSession.FrameSourceMode.WebSocket  => new WebSocketFrameLoaderBackend(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }
    }

    internal sealed class FileSystemFrameLoaderBackend : IFrameLoaderBackend
    {
        public void Start(ARSensorFlexSession session, IFrameLoaderState state, int framesToWait)
        {
            string folder = session.ImageFolder;
            if (!Path.IsPathRooted(folder))
                folder = Path.Combine(Application.streamingAssetsPath, folder);

            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[SF] Folder not found: {folder}");
                return;
            }

            var files = new List<string>();
            foreach (var file in Directory.GetFiles(folder))
            {
                if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }

            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0)
            {
                Debug.LogError($"[SF] No image files in {folder}");
                return;
            }

            int count = Mathf.Min(state.BufSize, files.Count);
            state.TotalFrames = count;
            state.AllocateLinearFrames(count);

            for (int i = 0; i < count; i++)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(File.ReadAllBytes(files[i]));
                tex.Apply();
                state.Frames[i] = tex;
            }

            state.IsReady = true;
            Debug.Log($"[SF] FileSystem: loaded {count} frames from {folder}.");
        }

        public void DrainMainThreadWork() { }
        public void Dispatch() { }
        public Task StopAsync() => Task.CompletedTask;
    }

    internal sealed class WebSocketFrameLoaderBackend : IFrameLoaderBackend
    {
        const int UploadBatchSize = 3;
        const uint FrameMagic = 0x50574653; // "SFWP"

        struct PendingFramePacket
        {
            public int GlobalFrameIndex;
            public byte[] RgbBytes;
            public byte[] MetaJsonBytes;
            public byte[] DepthBytes;
        }

        [Serializable]
        sealed class HelloMessage
        {
            public string type = "hello";
            public int protocolVersion = 1;
            public int requestedBufferSize;
            public int framesToWarm;
            public bool loop;
            public bool wantDepth;
        }

        [Serializable]
        sealed class WindowMessage
        {
            public string type = "window";
            public int playHead;
            public int bufferSize;
        }

        [Serializable]
        sealed class SceneMessage
        {
            public string type;
            public int protocolVersion;
            public string scene_id;
            public int n_frames;
            public int fps;
            public ArchiveIOUtils.CoordSystem coordinate_system;
            public ArchiveIOUtils.MeshMetaJson scanned_mesh;
            public DepthInfo depth;
        }

        [Serializable]
        sealed class DepthInfo
        {
            public bool available;
            public string format;
            public int width;
            public int height;
        }

        WebSocket m_WebSocket;
        ARSensorFlexSession m_Session;
        IFrameLoaderState m_State;
        int m_FramesToWait;
        bool m_Started;
        bool m_HasScene;
        int m_LastAdvertisedPlayHead = int.MinValue;
        readonly ConcurrentQueue<PendingFramePacket> m_UploadQueue = new();

        public async void Start(ARSensorFlexSession session, IFrameLoaderState state, int framesToWait)
        {
            if (m_Started)
                return;

            m_Started = true;
            m_Session = session;
            m_State = state;
            m_FramesToWait = framesToWait;

            state.TotalFrames = int.MaxValue;
            state.AllocateRingBuffer();

            Debug.Log($"[SF] WebSocket connecting to {session.WebSocketUrl}");
            m_WebSocket = new WebSocket(session.WebSocketUrl);

            m_WebSocket.OnOpen += HandleOpen;
            m_WebSocket.OnError += error => Debug.LogError("[SF] WebSocket error: " + error);
            m_WebSocket.OnClose += code => Debug.Log("[SF] WebSocket closed: " + code);
            m_WebSocket.OnMessage += HandleMessage;

            try
            {
                await m_WebSocket.Connect();
            }
            catch (Exception exception)
            {
                Debug.LogError("[SF] WebSocket connect exception: " + exception);
            }
        }

        void HandleOpen()
        {
            var helloMessage = new HelloMessage
            {
                requestedBufferSize = m_State.BufSize,
                framesToWarm = m_FramesToWait,
                loop = m_Session != null && m_Session.LoopSequence,
                wantDepth = m_Session != null && m_Session.DepthEnabled
            };

            _ = m_WebSocket.SendText(JsonUtility.ToJson(helloMessage));
        }

        void HandleMessage(byte[] messageBytes)
        {
            if (messageBytes == null || messageBytes.Length == 0)
                return;

            if (messageBytes[0] == (byte)'{' || messageBytes[0] == (byte)'[')
            {
                HandleJsonMessage(Encoding.UTF8.GetString(messageBytes));
                return;
            }

            HandleFrameMessage(messageBytes);
        }

        void HandleJsonMessage(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            if (!json.Contains("\"type\":\"scene\""))
                return;

            var sceneMessage = JsonUtility.FromJson<SceneMessage>(json);
            if (sceneMessage == null)
                return;

            m_State.TotalFrames = sceneMessage.n_frames;
            m_State.FrameInterval = 1.0 / Math.Max(1, sceneMessage.fps);
            m_State.CoordConvMatrix = sceneMessage.coordinate_system != null
                ? ArchiveIOUtils.ComputeConversionMatrix(
                    sceneMessage.coordinate_system.handedness,
                    sceneMessage.coordinate_system.forward)
                : Matrix4x4.identity;
            m_State.UseNegativeZForwardOpticalAxis =
                string.Equals(sceneMessage.coordinate_system?.forward, "-Z", StringComparison.OrdinalIgnoreCase);

            m_HasScene = true;
            SendWindowUpdate(force: true);

            Debug.Log(
                $"[SF] WebSocket scene ready. scene={sceneMessage.scene_id} " +
                $"frames={sceneMessage.n_frames} fps={sceneMessage.fps}");
        }

        void HandleFrameMessage(byte[] messageBytes)
        {
            if (messageBytes.Length < 22)
            {
                Debug.LogWarning("[SF] WebSocket frame packet too small.");
                return;
            }

            uint magic = BitConverter.ToUInt32(messageBytes, 0);
            byte version = messageBytes[4];
            byte messageType = messageBytes[5];

            if (magic != FrameMagic || version != 1 || messageType != 1)
            {
                Debug.LogWarning("[SF] Ignoring unknown WebSocket binary packet.");
                return;
            }

            int globalFrameIndex = BitConverter.ToInt32(messageBytes, 6);
            int rgbLength = BitConverter.ToInt32(messageBytes, 10);
            int metaJsonLength = BitConverter.ToInt32(messageBytes, 14);
            int depthLength = BitConverter.ToInt32(messageBytes, 18);

            int expectedLength = 22 + rgbLength + metaJsonLength + depthLength;
            if (expectedLength != messageBytes.Length)
            {
                Debug.LogWarning($"[SF] Invalid WebSocket frame packet length. expected={expectedLength} actual={messageBytes.Length}");
                return;
            }

            int cursor = 22;

            var rgbBytes = new byte[rgbLength];
            Buffer.BlockCopy(messageBytes, cursor, rgbBytes, 0, rgbLength);
            cursor += rgbLength;

            var metaJsonBytes = new byte[metaJsonLength];
            Buffer.BlockCopy(messageBytes, cursor, metaJsonBytes, 0, metaJsonLength);
            cursor += metaJsonLength;

            byte[] depthBytes = null;
            if (depthLength > 0)
            {
                depthBytes = new byte[depthLength];
                Buffer.BlockCopy(messageBytes, cursor, depthBytes, 0, depthLength);
            }

            m_UploadQueue.Enqueue(new PendingFramePacket
            {
                GlobalFrameIndex = globalFrameIndex,
                RgbBytes = rgbBytes,
                MetaJsonBytes = metaJsonBytes,
                DepthBytes = depthBytes
            });
        }

        public void DrainMainThreadWork()
        {
            int uploadedCount = 0;
            while (uploadedCount < UploadBatchSize && m_UploadQueue.TryDequeue(out var pendingFrame))
            {
                int slot = pendingFrame.GlobalFrameIndex % m_State.BufSize;

                if (m_State.Frames[slot] == null)
                    m_State.Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                m_State.Frames[slot].LoadImage(pendingFrame.RgbBytes);
                m_State.Frames[slot].Apply();

                m_State.DepthBins[slot] = pendingFrame.DepthBytes;

                if (pendingFrame.MetaJsonBytes != null && pendingFrame.MetaJsonBytes.Length > 0)
                {
                    string json = Encoding.UTF8.GetString(pendingFrame.MetaJsonBytes);
                    var poseValues = ArchiveIOUtils.ExtractFloatsFromField(json, "pose");
                    var intrinsicValues = ArchiveIOUtils.ExtractFloatsFromField(json, "intrinsic");

                    if (poseValues != null && poseValues.Length >= 16)
                        m_State.Poses[slot] = ArchiveIOUtils.FloatsToMatrix4x4(poseValues);

                    if (intrinsicValues != null && intrinsicValues.Length >= 9)
                    {
                        m_State.Intrinsics[slot] = new Vector4(
                            intrinsicValues[0],
                            intrinsicValues[4],
                            intrinsicValues[2],
                            intrinsicValues[5]);
                    }
                }

                m_State.SlotGlobalIdx[slot] = pendingFrame.GlobalFrameIndex;
                m_State.SlotReady[slot] = true;
                m_State.MarkBuffered(m_FramesToWait);

                uploadedCount++;
            }
        }

        public void Dispatch()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            m_WebSocket?.DispatchMessageQueue();
#endif
            SendWindowUpdate(force: false);
        }

        void SendWindowUpdate(bool force)
        {
            if (!m_HasScene || m_WebSocket == null)
                return;

            int playHead = m_State.PlayHead;
            if (!force && playHead == m_LastAdvertisedPlayHead)
                return;

            m_LastAdvertisedPlayHead = playHead;

            var windowMessage = new WindowMessage
            {
                playHead = playHead,
                bufferSize = m_State.BufSize
            };

            _ = m_WebSocket.SendText(JsonUtility.ToJson(windowMessage));
        }

        public async Task StopAsync()
        {
            if (m_WebSocket != null)
            {
                try
                {
                    await m_WebSocket.Close();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[SF] WebSocket close: " + exception);
                }
            }

            m_WebSocket = null;
            m_Started = false;
            m_HasScene = false;
            m_LastAdvertisedPlayHead = int.MinValue;

            while (m_UploadQueue.TryDequeue(out _)) { }
        }
    }

    // Shared frame packet used by both SFZ and FileIo ring-buffer backends.
    struct SfzLoadedFrame
    {
        public int    GlobalFrameIndex;
        public int    RecordIndex;     // index into the pre-parsed SfzFrameRecordJson[]
        public byte[] Jpg;
        public byte[] DepthBin;
    }

    // Common logic shared by SfzFrameLoaderBackend and FileIoFrameLoaderBackend.
    // Subclasses implement ReadBytes() to abstract ZIP vs filesystem I/O.
    internal abstract class SfzBackendBase : IFrameLoaderBackend
    {
        const int UploadBatchSize = 3;

        protected ARSensorFlexSession m_Session;
        protected IFrameLoaderState   m_State;
        int    m_FramesToWait;
        volatile bool m_StopLoading;
        Thread m_LoadThread;
        ConcurrentQueue<SfzLoadedFrame> m_UploadQueue;
        int  m_UploadedFrames;
        bool m_LoggedFirstEnqueue;
        bool m_LoggedFirstUpload;
        bool m_LoggedReady;

        ArchiveIOUtils.SfzFrameRecordJson[] m_FrameRecords;

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

            m_UploadQueue = new ConcurrentQueue<SfzLoadedFrame>();
            m_LoadThread  = new Thread(LoadFrames) { IsBackground = true, Name = "SF-SfzLoader" };
            m_LoadThread.Start();

            Debug.Log($"[SF] {BackendLabel} streaming started. frames={state.TotalFrames} fps={framesTrack.metadata?.fps} bufSize={state.BufSize}");
        }

        // Subclass contract ──────────────────────────────────────────────────────

        protected abstract string BackendLabel { get; }

        // Returns false and logs an error on failure.
        protected abstract bool TryReadSessionJson(out ArchiveIOUtils.SfzSessionJson sessionJson);

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

                if (!looping)
                    break;

                iteration++;
            }
        }

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
                        m_State.Poses[slot] = ArchiveIOUtils.SfzPoseToMatrix4x4(record.camera.pose);
                    if (record.camera?.intrinsics != null)
                        m_State.Intrinsics[slot] = ArchiveIOUtils.SfzIntrinsicsToVector4(record.camera.intrinsics);
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
        string m_ArchivePath;

        protected override string BackendLabel => "SFZ";

        protected override bool TryReadSessionJson(out ArchiveIOUtils.SfzSessionJson sessionJson)
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

                sessionJson = JsonUtility.FromJson<ArchiveIOUtils.SfzSessionJson>(json);
                return sessionJson != null;
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] SFZ: failed to read session.json: " + e);
                return false;
            }
        }

        protected override byte[] ReadSessionFile(string relativePath)
        {
            try
            {
                using var archive = new ZipArchive(File.OpenRead(m_ArchivePath), ZipArchiveMode.Read);
                var entry = archive.GetEntry($"session/{relativePath}");
                return entry != null ? ArchiveIOUtils.ReadEntry(entry) : null;
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

        protected override bool TryReadSessionJson(out ArchiveIOUtils.SfzSessionJson sessionJson)
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
                sessionJson = JsonUtility.FromJson<ArchiveIOUtils.SfzSessionJson>(File.ReadAllText(jsonPath));
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