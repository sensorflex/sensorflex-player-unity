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

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace SensorFlex.Player.Library
{
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

            var sfzSession = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(json);
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
                    var poseVals = SfzUtils.ExtractFloatsFromField(json, "pose");
                    var intrVals = SfzUtils.ExtractFloatsFromField(json, "intrinsic");
                    if (poseVals != null && poseVals.Length >= 16)
                        m_State.Poses[slot] = SfzUtils.FloatsToMatrix4x4(poseVals);
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

            m_State.PendingDecodeCount = m_FrameQueue.Count;
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
}
