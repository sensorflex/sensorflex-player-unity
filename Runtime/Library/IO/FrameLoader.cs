// FrameLoader is the single public surface for frame ingestion.
// Callers Start() it with an ARSensorFlexSession, then each Unity frame call
// DrainUploadQueue() (main-thread GPU upload) and DispatchWebSocket() (WebSocket pump).
// Source-specific I/O is hidden behind IFrameLoaderBackend; shared mutable state
// lives in IFrameLoaderState / FrameLoaderState.
//
// Three backends:
//   Sfz     — background thread streams from a .zip archive into a ring buffer.
//   FileIo  — same as Sfz but reads loose files from a session directory.
//   Live    — async WebSocket; receives session.json, optional SFAT (PLY mesh),
//             then continuous SFWP binary frame stream. Auto-reconnects on loss.

using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
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

        public int LatestGlobalIndex      => m_State.LatestGlobalIndex;
        public int PendingDecodeCount     => m_State.PendingDecodeCount;
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
}
