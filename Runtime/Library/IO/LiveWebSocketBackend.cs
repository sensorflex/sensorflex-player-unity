// LiveWebSocketBackend — ISessionBackend for live WebSocket streaming.
//
// Three-phase lifecycle:
//   1. Open()              — stores session, starts async WebSocket connect loop
//   2. TryGetSessionJson() — returns false until the server sends session.json
//   3. StartLoading()      — allocates ring buffer, enables frame drain
//
// Protocol (server-initiated after hello):
//   session.json text message  — parsed by SessionLoader via TryGetSessionJson
//   SFAT binary packets        — attachment bytes stored; served via TryGetAttachmentBytes
//   SFWP binary frame stream   — frames held until all expected attachments consumed
//
// Auto-reconnects up to MaxReconnectAttempts consecutive failures; resets on success.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal sealed class LiveWebSocketBackend : ISessionBackend
    {
        const int  UploadBatchSize      = 3;
        const uint FrameMagic           = 0x50574653; // "SFWP" little-endian
        const uint AttachMagic          = 0x54414653; // "SFAT" little-endian
        const int  MaxReconnectAttempts = 5;
        const int  ReconnectDelayMs     = 2000;

        struct PendingFrame
        {
            public int    SeqNum;
            public byte[] Rgb;
            public byte[] Meta;
            public byte[] Depth;
        }

        [Serializable]
        sealed class HelloMessage
        {
            public string type            = "hello";
            public int    protocolVersion = 2;
            public string mode            = "live";
            public bool   wantDepth;
        }

        WebSocket           m_WebSocket;
        ARSensorFlexSession m_Session;
        IFrameLoaderState   m_State;
        int                 m_FramesToWait;

        bool   m_Started;
        bool   m_Stopping;
        bool   m_WasConnected;
        int    m_ConsecutiveFailures;

        // Phase-2 handshake
        string m_PendingSessionJson;          // set by HandleJsonMessage; read by TryGetSessionJson

        // Phase-3 attachment gate
        int  m_ExpectedAttachments;           // set in StartLoading from session data
        int  m_AttachmentsTaken;              // incremented by TryGetAttachmentBytes
        readonly Dictionary<string, byte[]> m_AttachmentBytes = new();  // SFAT name → bytes

        readonly ConcurrentQueue<PendingFrame> m_FrameQueue = new();

        // ── ISessionBackend — Phase 1 ─────────────────────────────────────────

        public bool Open(ARSensorFlexSession session)
        {
            if (m_Started) return true;
            m_Session = session;
            StartConnectLoop();
            return true;
        }

        async void StartConnectLoop()
        {
            m_Started = true;
            ControlBridge.SetConnectionState(LiveConnectionState.Connecting);
            Debug.Log($"[SF] Live WS connecting to {m_Session.WebSocketUrl}");

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
                m_WasConnected = false;

                m_WebSocket = new WebSocket(m_Session.WebSocketUrl);
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

                m_ConsecutiveFailures = m_WasConnected ? 0 : m_ConsecutiveFailures + 1;
            }

            if (!m_Stopping && m_ConsecutiveFailures >= MaxReconnectAttempts)
                Debug.LogError($"[SF] Live WS: max reconnect attempts ({MaxReconnectAttempts}) reached.");
        }

        // ── ISessionBackend — Phase 2 ─────────────────────────────────────────

        public bool TryGetSessionJson(out string json)
        {
            json = m_PendingSessionJson;
            return !string.IsNullOrEmpty(json);
        }

        // ── ISessionBackend — Phase 3 ─────────────────────────────────────────

        public void StartLoading(SfzSessionData data, int bufSize, int framesToWait)
        {
            m_FramesToWait       = framesToWait;
            m_ExpectedAttachments = data.Attachments.Count;
            m_AttachmentsTaken   = 0;

            m_State = new FrameLoaderState(bufSize)
            {
                TotalFrames       = int.MaxValue,
                LatestGlobalIndex = -1,
            };

            bool hasFrames = data.Tracks.TryGetValue("frames", out var framesTrack);
            m_State.FrameInterval = hasFrames ? framesTrack.SampleInterval : 1.0 / 30;
            m_State.CoordConvMatrix               = Matrix4x4.identity;
            m_State.UseNegativeZForwardOpticalAxis = false;
            m_State.AllocateRingBuffer();

            Debug.Log($"[SF] Live WS StartLoading. fps={1.0/m_State.FrameInterval:F0} bufSize={bufSize} expectedAttachments={m_ExpectedAttachments}");
        }

        // ── ISessionBackend — attachment bytes ────────────────────────────────

        public byte[] TryGetAttachmentBytes(string attachmentName)
        {
            if (!m_AttachmentBytes.TryGetValue(attachmentName, out var bytes))
                return null;

            m_AttachmentBytes.Remove(attachmentName);
            m_AttachmentsTaken++;
            return bytes;
        }

        // ── ISessionBackend — runtime ─────────────────────────────────────────

        public IFrameLoaderState State => m_State;

        public void Dispatch()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            m_WebSocket?.DispatchMessageQueue();
#endif
        }

        public void DrainMainThreadWork()
        {
            if (m_State == null) return;

            // Hold frames until all expected attachments have been handed to SessionLoader.
            if (m_AttachmentsTaken < m_ExpectedAttachments) return;

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
                    string json  = Encoding.UTF8.GetString(pkt.Meta);
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
            while (m_FrameQueue.TryDequeue(out _)) { }
            m_AttachmentBytes.Clear();
        }

        // ── WebSocket handlers ────────────────────────────────────────────────

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

        void HandleJsonMessage(string json)
        {
            if (!string.IsNullOrEmpty(m_PendingSessionJson)) return; // already received
            m_PendingSessionJson = json;
            Debug.Log("[SF] Live WS: session.json received.");
        }

        // SFAT: [4B magic][1B version][1B type][4B nameLen][name...][4B dataLen][data...]
        void HandleAttachmentPacket(byte[] bytes)
        {
            if (bytes.Length < 14) { Debug.LogWarning("[SF] Live WS: SFAT packet too small."); return; }

            int nameLen = BitConverter.ToInt32(bytes, 6);
            int cursor  = 10;

            if (cursor + nameLen + 4 > bytes.Length) { Debug.LogWarning("[SF] Live WS: SFAT name overflows."); return; }

            string name = Encoding.UTF8.GetString(bytes, cursor, nameLen);
            cursor += nameLen;

            int dataLen = BitConverter.ToInt32(bytes, cursor);
            cursor += 4;

            if (cursor + dataLen > bytes.Length) { Debug.LogWarning("[SF] Live WS: SFAT data overflows."); return; }

            var data = new byte[dataLen];
            Buffer.BlockCopy(bytes, cursor, data, 0, dataLen);
            m_AttachmentBytes[name] = data;

            Debug.Log($"[SF] Live WS: SFAT received. name={name} bytes={dataLen}");
        }

        // SFWP: [4B magic][1B version=1][1B type=1][4B seqNum][4B rgbLen][4B metaLen][4B depthLen][payloads...]
        void HandleFramePacket(byte[] bytes)
        {
            if (bytes.Length < 22) { Debug.LogWarning("[SF] Live WS: SFWP packet too small."); return; }
            if (bytes[4] != 1 || bytes[5] != 1) { Debug.LogWarning("[SF] Live WS: unexpected SFWP version/type."); return; }

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
            if (depthLen > 0) { depth = new byte[depthLen]; Buffer.BlockCopy(bytes, cursor, depth, 0, depthLen); }

            m_FrameQueue.Enqueue(new PendingFrame { SeqNum = seqNum, Rgb = rgb, Meta = meta, Depth = depth });
        }
    }
}
