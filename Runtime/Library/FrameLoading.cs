// FrameLoading.cs — SensorFlex frame ingestion pipeline.
//
// FrameLoader is the single public surface: callers Start() it with an ARSensorFlexSession,
// then each Unity frame call DrainUploadQueue() (main thread GPU upload) and
// DispatchWebSocket() (WebSocket message pump). All source-specific I/O is hidden
// behind IFrameLoaderBackend; the shared mutable buffer lives in IFrameLoaderState /
// FrameLoaderState.
//
// Three backends are supported:
//   Zip (Sfz)       — background thread streams frames from a .zip archive into a ring
//                     buffer. Main thread drains the upload queue in batches of 3 per frame.
//                     Back-pressure: loader thread sleeps when the ring buffer is full.
//   FileIo          — same as Zip but reads loose files from a session directory instead of
//                     a ZIP archive.
//   Live (WebSocket) — async connection; receives session.json, SFAT attachment packets
//                     (PLY mesh), then continuous SFWP binary frame stream. No back-pressure.
//                     Auto-reconnects up to MaxReconnectAttempts on connection loss.
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
//   │  SlotGlobalIdx[]     │  │   Zip    │ │  FileIo  │ │    WebSocket       │
//   │  PlayHead            │  │ Backend  │ │ Backend  │ │    Backend         │
//   └──────────┬───────────┘  │          │ │          │ │                    │
//              │ implemented by│bg thread │ │bg thread │ │async WS conn       │
//              ▼              │+ ring buf│ │+ ring buf│ │+ ring buf          │
//   ┌──────────────────────┐  └──────────┘ └──────────┘ └────────────────────┘
//   │  FrameLoaderState    │
//   │  ────────────────    │   All three backends read/write IFrameLoaderState.
//   │  (concrete impl)     │   FrameLoader proxies every property read through
//   │  thread-safe PlayHead│   m_State so callers never touch the state directly.
//   └──────────────────────┘

using System;
using System.Collections.Concurrent;
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
        // Tracks the highest sequence number received; used by live mode to jump to latest.
        int LatestGlobalIndex { get; set; }
        // Set by the live backend when a PLY attachment arrives; polled by Camera.cs.
        ScannedSceneMeshLoadOperation PendingMeshLoad { get; set; }
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

        int m_LatestGlobalIndex = -1;

        public int LatestGlobalIndex
        {
            get => Volatile.Read(ref m_LatestGlobalIndex);
            set => Volatile.Write(ref m_LatestGlobalIndex, value);
        }

        public ScannedSceneMeshLoadOperation PendingMeshLoad { get; set; }

        public FrameLoaderState(int bufSize)
        {
            BufSize = bufSize;
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

        public int LatestGlobalIndex => m_State.LatestGlobalIndex;
        public ScannedSceneMeshLoadOperation PendingMeshLoad => m_State.PendingMeshLoad;
        public void ClearPendingMeshLoad() => m_State.PendingMeshLoad = null;

        public FrameLoader()
        {
            m_State = new FrameLoaderState(0);
        }

        public void Start(ARSensorFlexSession session, int maxFramesToLoad, int framesToWait)
        {
            if (session == null)
                throw new InvalidOperationException("[SF] FrameLoader.Start() requires an active ARSensorFlexSession.");

            // Live mode uses the larger total buffer so pausing doesn't overwrite frames immediately.
            int bufSize = session.SourceMode == ARSensorFlexSession.FrameSourceMode.Live
                ? session.TotalLiveBufferSize
                : maxFramesToLoad;

            var state = new FrameLoaderState(bufSize)
            {
                IsReady = false,
                PlayHead = -1,
                FrameInterval = 1.0 / 30.0
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
                ARSensorFlexSession.FrameSourceMode.Sfz    => new SfzFrameLoaderBackend(),
                ARSensorFlexSession.FrameSourceMode.FileIo => new FileIoFrameLoaderBackend(),
                ARSensorFlexSession.FrameSourceMode.Live   => new LiveWebSocketBackend(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }
    }

    // Live WebSocket backend — connects to the server, receives session.json, optional SFAT
    // attachment packets (PLY mesh), then a continuous SFWP binary frame stream.
    //
    // Protocol sequence (all initiated by server after hello):
    //   1. Server sends session.json as a JSON text message (fps, channels, attachments)
    //   2. Server sends zero or more SFAT binary packets (one per attachment entry)
    //   3. Server streams SFWP binary frame packets continuously
    //
    // Frames are not forwarded to the ring buffer until all expected SFAT packets arrive.
    // Auto-reconnects up to MaxReconnectAttempts consecutive failures; resets on success.
    internal sealed class LiveWebSocketBackend : IFrameLoaderBackend
    {
        const int UploadBatchSize = 3;
        const uint FrameMagic  = 0x50574653; // little-endian "SFWP"
        const uint AttachMagic = 0x54414653; // little-endian "SFAT"
        const int MaxReconnectAttempts = 5;
        const int ReconnectDelayMs = 2000;

        struct PendingFrame
        {
            public int    SeqNum;
            public byte[] Rgb;
            public byte[] Meta;
            public byte[] Depth;
        }

        struct PendingAttachment
        {
            public byte   Type;   // 1 = scene_mesh_ply
            public string Name;
            public byte[] Data;
        }

        [Serializable]
        sealed class HelloMessage
        {
            public string type            = "hello";
            public int    protocolVersion = 2;
            public string mode            = "live";
            public bool   wantDepth;
        }

        WebSocket    m_WebSocket;
        ARSensorFlexSession m_Session;
        IFrameLoaderState   m_State;
        int  m_FramesToWait;
        bool m_Started;
        bool m_Stopping;
        bool m_HasSession;
        bool m_WasConnected;          // true once OnOpen fires for current attempt
        int  m_ConsecutiveFailures;
        int  m_ExpectedAttachments;
        int  m_ReceivedAttachments;
        string m_SessionId;

        readonly ConcurrentQueue<PendingFrame>      m_FrameQueue  = new();
        readonly ConcurrentQueue<PendingAttachment> m_AttachQueue = new();

        public async void Start(ARSensorFlexSession session, IFrameLoaderState state, int framesToWait)
        {
            if (m_Started) return;
            m_Started     = true;
            m_Session     = session;
            m_State       = state;
            m_FramesToWait = framesToWait;

            state.TotalFrames       = int.MaxValue;
            state.LatestGlobalIndex = -1;
            state.AllocateRingBuffer();

            ControlBridge.SetConnectionState(LiveConnectionState.Connecting);
            Debug.Log($"[SF] Live WS connecting to {session.WebSocketUrl}");

            bool firstAttempt = true;
            while (!m_Stopping && m_ConsecutiveFailures < MaxReconnectAttempts)
            {
                if (!firstAttempt)
                {
                    await Task.Delay(ReconnectDelayMs);
                    if (m_Stopping) break;
                    ControlBridge.SetConnectionState(LiveConnectionState.Connecting);
                    Debug.Log($"[SF] Live WS reconnect attempt {m_ConsecutiveFailures}/{MaxReconnectAttempts}");
                }
                firstAttempt = false;

                m_WasConnected       = false;
                m_HasSession         = false;
                m_ExpectedAttachments = 0;
                m_ReceivedAttachments = 0;

                m_WebSocket = new WebSocket(session.WebSocketUrl);
                m_WebSocket.OnOpen    += HandleOpen;
                m_WebSocket.OnError   += e => Debug.LogError("[SF] Live WS error: " + e);
                m_WebSocket.OnClose   += HandleClose;
                m_WebSocket.OnMessage += HandleMessage;

                try   { await m_WebSocket.Connect(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SF] Live WS connect exception: {ex.Message}");
                    if (!m_Stopping)
                        ControlBridge.SetConnectionState(LiveConnectionState.Disconnected);
                }

                if (m_WasConnected)
                    m_ConsecutiveFailures = 0; // successful connection; reset counter
                else
                    m_ConsecutiveFailures++;
            }

            if (!m_Stopping && m_ConsecutiveFailures >= MaxReconnectAttempts)
                Debug.LogError($"[SF] Live WS: max reconnect attempts ({MaxReconnectAttempts}) reached. Use Reconnect to retry.");
        }

        void HandleOpen()
        {
            m_WasConnected = true;
            ControlBridge.SetConnectionState(LiveConnectionState.Live);
            var hello = new HelloMessage { wantDepth = m_Session != null && m_Session.DepthEnabled };
            _ = m_WebSocket.SendText(JsonUtility.ToJson(hello));
            Debug.Log("[SF] Live WS: connected, hello sent.");
        }

        void HandleClose(WebSocketCloseCode code)
        {
            Debug.Log($"[SF] Live WS closed: {code}");
            if (!m_Stopping)
                ControlBridge.SetConnectionState(LiveConnectionState.Disconnected);
        }

        void HandleMessage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            // JSON text frame
            if (bytes[0] == (byte)'{' || bytes[0] == (byte)'[')
            {
                HandleJsonMessage(Encoding.UTF8.GetString(bytes));
                return;
            }

            if (bytes.Length >= 4)
            {
                uint magic = BitConverter.ToUInt32(bytes, 0);
                if (magic == AttachMagic) { HandleAttachmentPacket(bytes); return; }
                if (magic == FrameMagic)  { HandleFramePacket(bytes);      return; }
            }

            Debug.LogWarning("[SF] Live WS: unrecognised binary packet.");
        }

        // The first JSON message from the server is the session.json payload.
        void HandleJsonMessage(string json)
        {
            if (m_HasSession) return; // only parse once per connection

            var sfzSession = JsonUtility.FromJson<ArchiveIOUtils.SfzSessionJson>(json);
            if (sfzSession == null || string.IsNullOrEmpty(sfzSession.version))
            {
                Debug.LogWarning("[SF] Live WS: first JSON message is not a valid session.json.");
                return;
            }

            m_SessionId = sfzSession.session_id ?? "live";
            int fps = sfzSession.tracks?.frames?.metadata?.fps ?? 30;
            m_State.FrameInterval              = 1.0 / Math.Max(1, fps);
            m_State.CoordConvMatrix            = Matrix4x4.identity; // session.json is Unity world space
            m_State.UseNegativeZForwardOpticalAxis = false;

            m_ExpectedAttachments = sfzSession.attachments?.scene_mesh != null ? 1 : 0;
            m_HasSession = true;

            Debug.Log($"[SF] Live WS: session received. id={m_SessionId} fps={fps} expectedAttachments={m_ExpectedAttachments}");
        }

        // SFAT: [4B magic][1B version][1B type][4B nameLen][name...][4B dataLen][data...]
        void HandleAttachmentPacket(byte[] bytes)
        {
            if (bytes.Length < 14)
            {
                Debug.LogWarning("[SF] Live WS: SFAT packet too small.");
                return;
            }

            byte attachType = bytes[5];
            int  nameLen    = BitConverter.ToInt32(bytes, 6);
            int  cursor     = 10;

            if (cursor + nameLen + 4 > bytes.Length)
            {
                Debug.LogWarning("[SF] Live WS: SFAT name overflows packet.");
                return;
            }

            string name = Encoding.UTF8.GetString(bytes, cursor, nameLen);
            cursor += nameLen;

            int dataLen = BitConverter.ToInt32(bytes, cursor);
            cursor += 4;

            if (cursor + dataLen > bytes.Length)
            {
                Debug.LogWarning("[SF] Live WS: SFAT data overflows packet.");
                return;
            }

            var data = new byte[dataLen];
            Buffer.BlockCopy(bytes, cursor, data, 0, dataLen);

            m_AttachQueue.Enqueue(new PendingAttachment { Type = attachType, Name = name, Data = data });
            Debug.Log($"[SF] Live WS: SFAT received. type={attachType} name={name} bytes={dataLen}");
        }

        // SFWP: [4B magic][1B version=1][1B type=1][4B seqNum][4B rgbLen][4B metaLen][4B depthLen][payloads...]
        void HandleFramePacket(byte[] bytes)
        {
            if (bytes.Length < 22)
            {
                Debug.LogWarning("[SF] Live WS: SFWP packet too small.");
                return;
            }

            if (bytes[4] != 1 || bytes[5] != 1)
            {
                Debug.LogWarning("[SF] Live WS: unexpected SFWP version/type.");
                return;
            }

            int seqNum   = BitConverter.ToInt32(bytes, 6);
            int rgbLen   = BitConverter.ToInt32(bytes, 10);
            int metaLen  = BitConverter.ToInt32(bytes, 14);
            int depthLen = BitConverter.ToInt32(bytes, 18);

            if (22 + rgbLen + metaLen + depthLen != bytes.Length)
            {
                Debug.LogWarning("[SF] Live WS: SFWP length mismatch.");
                return;
            }

            int cursor = 22;
            var rgb  = new byte[rgbLen];  Buffer.BlockCopy(bytes, cursor, rgb,  0, rgbLen);  cursor += rgbLen;
            var meta = new byte[metaLen]; Buffer.BlockCopy(bytes, cursor, meta, 0, metaLen); cursor += metaLen;
            byte[] depth = null;
            if (depthLen > 0)
            {
                depth = new byte[depthLen];
                Buffer.BlockCopy(bytes, cursor, depth, 0, depthLen);
            }

            m_FrameQueue.Enqueue(new PendingFrame { SeqNum = seqNum, Rgb = rgb, Meta = meta, Depth = depth });
        }

        public void DrainMainThreadWork()
        {
            // 1. Process SFAT attachment queue (main thread — PLY parse is launched as Task)
            while (m_AttachQueue.TryDequeue(out var att))
            {
                if (att.Type == 1) // scene_mesh_ply
                {
                    m_State.PendingMeshLoad = ScannedSceneMeshLoadOperation.StartFromPlyBytes(
                        att.Data, Matrix4x4.identity, m_SessionId ?? "live");
                    Debug.Log("[SF] Live WS: PLY mesh parse started.");
                }
                m_ReceivedAttachments++;
            }

            // 2. Hold frames until session.json + all SFAT packets have been received
            if (!m_HasSession || m_ReceivedAttachments < m_ExpectedAttachments)
                return;

            // 3. Upload decoded frames into the ring buffer
            int uploaded = 0;
            while (uploaded < UploadBatchSize && m_FrameQueue.TryDequeue(out var pkt))
            {
                int slot = pkt.SeqNum % m_State.BufSize;

                if (m_State.Frames[slot] == null)
                    m_State.Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                m_State.Frames[slot].LoadImage(pkt.Rgb);
                m_State.Frames[slot].Apply();
                m_State.DepthBins[slot] = pkt.Depth;

                if (pkt.Meta != null && pkt.Meta.Length > 0)
                {
                    string json = Encoding.UTF8.GetString(pkt.Meta);
                    var poseVals = ArchiveIOUtils.ExtractFloatsFromField(json, "pose");
                    var intrVals = ArchiveIOUtils.ExtractFloatsFromField(json, "intrinsic");
                    if (poseVals != null && poseVals.Length >= 16)
                        m_State.Poses[slot] = ArchiveIOUtils.FloatsToMatrix4x4(poseVals);
                    if (intrVals != null && intrVals.Length >= 9)
                        m_State.Intrinsics[slot] = new Vector4(intrVals[0], intrVals[4], intrVals[2], intrVals[5]);
                }

                m_State.SlotGlobalIdx[slot] = pkt.SeqNum;
                m_State.SlotReady[slot]     = true;
                m_State.MarkBuffered(m_FramesToWait);

                if (pkt.SeqNum > m_State.LatestGlobalIndex)
                    m_State.LatestGlobalIndex = pkt.SeqNum;

                uploaded++;
            }
        }

        public void Dispatch()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            m_WebSocket?.DispatchMessageQueue();
#endif
        }

        public async Task StopAsync()
        {
            m_Stopping = true;
            ControlBridge.SetConnectionState(LiveConnectionState.Disconnected);

            if (m_WebSocket != null)
            {
                try   { await m_WebSocket.Close(); }
                catch (Exception e) { Debug.LogWarning("[SF] Live WS close: " + e.Message); }
                m_WebSocket = null;
            }

            m_Started = false;
            while (m_FrameQueue.TryDequeue(out _))  { }
            while (m_AttachQueue.TryDequeue(out _)) { }
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

        // Called once at the start of each pass through the frame sequence.
        // Subclasses can open shared resources here (e.g. keep a ZIP archive open for the whole pass).
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
        string     m_ArchivePath;
        ZipArchive m_PassArchive;

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