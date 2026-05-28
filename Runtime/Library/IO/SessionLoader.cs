// SessionLoader.cs — session lifecycle coordinator and data model.
//
// SessionLoader is an instance object (owned by Camera.cs) that drives the full
// session lifecycle through a four-state machine:
//
//   Idle    — not started
//   Waiting — backend opened; polling TryGetSessionJson() each Tick()
//   Loading — session.json parsed; streaming frames + loading attachments
//   Ready   — enough frames buffered to begin playback
//
// ISessionBackend is the three-phase contract for all I/O backends:
//   1. Open()              — validate / open the data source
//   2. TryGetSessionJson() — polled until session.json text is available
//   3. StartLoading()      — backend creates its own ring buffer and begins work
//
// Session data model (SfzSessionData) uses generic track and attachment maps so
// new data types can be added without changing this file.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    // ── Session load state ────────────────────────────────────────────────────

    internal enum SessionLoadState { Idle, Waiting, Loading, Ready }

    // ── Backend contract ──────────────────────────────────────────────────────

    internal interface ISessionBackend
    {
        /// <summary>Open / validate the data source. Returns false on hard failure.</summary>
        bool Open(ARSensorFlexSession session);

        /// <summary>
        /// Returns session.json text when available; false if still waiting.
        /// Called each Tick() until it returns true. File backends return true
        /// immediately; the live backend returns false until the server sends JSON.
        /// </summary>
        bool TryGetSessionJson(out string json);

        /// <summary>Allocate ring buffer and start streaming. Called once after parse.</summary>
        void StartLoading(SfzSessionData data, int bufSize, int framesToWait);

        /// <summary>
        /// Returns the raw bytes for a named attachment when available, null otherwise.
        /// Calling this consumes the bytes (subsequent calls return null for the same name).
        /// File backends return bytes immediately on first call; the live backend returns
        /// null until the SFAT packet for that attachment has been received.
        /// </summary>
        byte[] TryGetAttachmentBytes(string attachmentName);

        /// <summary>Ring-buffer state; non-null after StartLoading.</summary>
        IFrameLoaderState State { get; }

        void DrainMainThreadWork();
        void Dispatch();
        Task StopAsync();
    }

    // ── Session data model ────────────────────────────────────────────────────

    /// <summary>Generic metadata for one session track (frames, imu, …).</summary>
    internal sealed class SfzTrackInfo
    {
        public string Name           { get; }
        public double SampleInterval { get; }   // seconds between samples
        public int    RecordCount    { get; }   // 0 when unknown (live / no pre-parsed array)

        internal SfzTrackInfo(string name, double sampleInterval, int recordCount)
        { Name = name; SampleInterval = sampleInterval; RecordCount = recordCount; }
    }

    /// <summary>Generic metadata for one session attachment (scene_mesh, …).</summary>
    internal sealed class SfzAttachmentInfo
    {
        public string Name   { get; }
        public string File   { get; }   // relative path within the session root
        public string Format { get; }   // e.g. "ply", or null if unspecified

        internal SfzAttachmentInfo(string name, string file, string format)
        { Name = name; File = file; Format = format; }
    }

    internal sealed class SfzSessionData
    {
        public string SessionId { get; }

        /// <summary>All tracks keyed by name (e.g. "frames", "imu").</summary>
        public IReadOnlyDictionary<string, SfzTrackInfo> Tracks { get; }

        /// <summary>All attachments keyed by name (e.g. "scene_mesh").</summary>
        public IReadOnlyDictionary<string, SfzAttachmentInfo> Attachments { get; }

        // Raw frame record array for backends that iterate frame-by-frame (SFZ/FileIo).
        internal SfzUtils.SfzFrameRecordJson[] FrameRecords { get; }

        internal SfzSessionData(
            string sessionId,
            IReadOnlyDictionary<string, SfzTrackInfo>      tracks,
            IReadOnlyDictionary<string, SfzAttachmentInfo> attachments,
            SfzUtils.SfzFrameRecordJson[]                  frameRecords)
        {
            SessionId    = sessionId ?? "session";
            Tracks       = tracks      ?? new Dictionary<string, SfzTrackInfo>();
            Attachments  = attachments ?? new Dictionary<string, SfzAttachmentInfo>();
            FrameRecords = frameRecords;
        }
    }

    // ── SessionLoader ─────────────────────────────────────────────────────────

    internal sealed class SessionLoader
    {
        SessionLoadState     m_LoadState = SessionLoadState.Idle;
        ISessionBackend      m_Backend;
        SfzSessionData       m_SessionData;
        int                  m_BufSize;
        int                  m_FramesToWait;
        readonly HashSet<string> m_StartedAttachments = new();

        // ── Public state ──────────────────────────────────────────────────────

        public SessionLoadState LoadState   => m_LoadState;
        public bool             IsReady     => m_LoadState == SessionLoadState.Ready;
        public SfzSessionData   SessionData => m_SessionData;

        /// <summary>In-progress PLY mesh parse; polled and cleared by Camera.cs.</summary>
        public ScannedSceneMeshLoadOperation PendingMeshLoad { get; private set; }
        public void ClearPendingMeshLoad() => PendingMeshLoad = null;

        // ── Frame data — delegated to backend state ───────────────────────────

        public double    FrameInterval                  => m_Backend?.State?.FrameInterval ?? 1.0 / 30;
        public Matrix4x4 CoordConvMatrix                => m_Backend?.State?.CoordConvMatrix ?? Matrix4x4.identity;
        public bool      UseNegativeZForwardOpticalAxis => m_Backend?.State?.UseNegativeZForwardOpticalAxis ?? false;
        public int       TotalFrames                    => m_Backend?.State?.TotalFrames ?? 0;
        public int       BufSize                        => m_Backend?.State?.BufSize ?? 0;
        public Texture2D[]  Frames        => m_Backend?.State?.Frames;
        public byte[][]     DepthBins     => m_Backend?.State?.DepthBins;
        public Matrix4x4[]  Poses         => m_Backend?.State?.Poses;
        public Vector4[]    Intrinsics    => m_Backend?.State?.Intrinsics;
        public bool[]       SlotReady     => m_Backend?.State?.SlotReady;
        public int[]        SlotGlobalIdx => m_Backend?.State?.SlotGlobalIdx;
        public int          LatestGlobalIndex  => m_Backend?.State?.LatestGlobalIndex ?? -1;
        public int          PendingDecodeCount => m_Backend?.State?.PendingDecodeCount ?? 0;

        public int PlayHead
        {
            get => m_Backend?.State?.PlayHead ?? -1;
            set { if (m_Backend?.State != null) m_Backend.State.PlayHead = value; }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start(ARSensorFlexSession session, int maxFramesToLoad, int framesToWait)
        {
            if (session == null)
                throw new InvalidOperationException("[SF] SessionLoader.Start() requires an active ARSensorFlexSession.");

            m_FramesToWait = framesToWait;
            m_BufSize = session.SourceMode == ARSensorFlexSession.FrameSourceMode.Live
                ? session.TotalLiveBufferSize
                : maxFramesToLoad;

            m_StartedAttachments.Clear();
            m_Backend = CreateBackend(session.SourceMode);

            if (!m_Backend.Open(session))
            {
                Debug.LogError("[SF] SessionLoader: backend failed to open.");
                return;
            }

            m_LoadState = SessionLoadState.Waiting;
            Debug.Log($"[SF] SessionLoader: waiting for session data. mode={session.SourceMode}");
        }

        /// <summary>
        /// Drive the state machine. Call once per Unity frame from Camera.cs —
        /// replaces the old DrainUploadQueue() + DispatchWebSocket() pair.
        /// </summary>
        public void Tick()
        {
            m_Backend?.Dispatch();

            switch (m_LoadState)
            {
                case SessionLoadState.Waiting:
                    if (m_Backend.TryGetSessionJson(out var json) && TryParseSession(json, out m_SessionData))
                    {
                        m_Backend.StartLoading(m_SessionData, m_BufSize, m_FramesToWait);
                        m_LoadState = SessionLoadState.Loading;
                        Debug.Log($"[SF] SessionLoader: session loaded. id={m_SessionData.SessionId} " +
                                  $"tracks={m_SessionData.Tracks.Count} attachments={m_SessionData.Attachments.Count}");
                    }
                    break;

                case SessionLoadState.Loading:
                case SessionLoadState.Ready:
                    m_Backend.DrainMainThreadWork();
                    ProcessAttachments();

                    if (m_LoadState == SessionLoadState.Loading && m_Backend.State?.IsReady == true)
                    {
                        m_LoadState = SessionLoadState.Ready;
                        Debug.Log("[SF] SessionLoader: ready.");
                    }
                    break;
            }
        }

        public async Task StopAsync()
        {
            m_LoadState = SessionLoadState.Idle;
            if (m_Backend != null)
                await m_Backend.StopAsync();
        }

        public void DestroyTextures() => m_Backend?.State?.DestroyTextures();

        // ── Attachment orchestration ──────────────────────────────────────────

        // ScanNet++ PLY meshes are in ARKit space (right-handed, -Z forward).
        // Flip Z so positions land in the same Unity world space as the camera poses.
        // TODO: remove once the SFZ exporter writes PLY vertices in Unity world space.
        static readonly Matrix4x4 k_ArkitToUnity = new Matrix4x4(
            new Vector4( 1, 0,  0, 0),
            new Vector4( 0, 1,  0, 0),
            new Vector4( 0, 0, -1, 0),
            new Vector4( 0, 0,  0, 1));

        void ProcessAttachments()
        {
            if (m_SessionData == null) return;

            foreach (var (name, _) in m_SessionData.Attachments)
            {
                if (m_StartedAttachments.Contains(name)) continue;

                var bytes = m_Backend.TryGetAttachmentBytes(name);
                if (bytes == null) continue;

                if (name == "scene_mesh")
                {
                    PendingMeshLoad = ScannedSceneMeshLoadOperation.StartFromPlyBytes(
                        bytes, k_ArkitToUnity, m_SessionData.SessionId);
                    Debug.Log("[SF] SessionLoader: scene_mesh loading started.");
                }

                m_StartedAttachments.Add(name);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static bool TryParseSession(string json, out SfzSessionData data)
        {
            data = null;
            if (string.IsNullOrEmpty(json))
                return false;

            var raw = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(json);
            if (raw == null || string.IsNullOrEmpty(raw.version))
                return false;

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

        static ISessionBackend CreateBackend(ARSensorFlexSession.FrameSourceMode mode) => mode switch
        {
            ARSensorFlexSession.FrameSourceMode.Sfz    => new SfzFrameLoaderBackend(),
            ARSensorFlexSession.FrameSourceMode.FileIo => new FileIoFrameLoaderBackend(),
            ARSensorFlexSession.FrameSourceMode.Live   => new LiveWebSocketBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }
}
