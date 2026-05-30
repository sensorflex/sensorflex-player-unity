// FrameLoader.cs — shared session contracts used by SfzSessionStore and future LiveSessionStore.
//
// Ring-buffer state:  IBackendState / BackendState
// Backend contract:   ISessionBackend (three-phase: Open / TryGetSessionJson / StartLoading)
// Session load state: SessionLoadState enum
// Session data model: SfzSessionData, SfzTrackInfo, SfzAttachmentInfo

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    // ── Ring buffer state ─────────────────────────────────────────────────────

    internal interface IBackendState
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

    internal sealed class BackendState : IBackendState
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

    // ── Session load state ────────────────────────────────────────────────────

    internal enum SessionLoadState { Idle, Waiting, Loading, Ready }

    // ── Backend contract ──────────────────────────────────────────────────────

    internal interface ISessionBackend
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

    // ── Session data model ────────────────────────────────────────────────────

    /// <summary>Generic metadata for one session track (frames, imu, …).</summary>
    internal sealed class SfzTrackInfo
    {
        public string Name           { get; }
        public double SampleInterval { get; }
        public int    RecordCount    { get; }

        internal SfzTrackInfo(string name, double sampleInterval, int recordCount)
        { Name = name; SampleInterval = sampleInterval; RecordCount = recordCount; }
    }

    /// <summary>Generic metadata for one session attachment (scene_mesh, …).</summary>
    internal sealed class SfzAttachmentInfo
    {
        public string Name   { get; }
        public string File   { get; }
        public string Format { get; }

        internal SfzAttachmentInfo(string name, string file, string format)
        { Name = name; File = file; Format = format; }
    }

    internal sealed class SfzSessionData
    {
        public string SessionId { get; }

        public IReadOnlyDictionary<string, SfzTrackInfo>       Tracks      { get; }
        public IReadOnlyDictionary<string, SfzAttachmentInfo>  Attachments { get; }

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
}
