using System.Threading;
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
        // Frames received over the network but not yet uploaded to the ring buffer.
        int PendingDecodeCount { get; set; }
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

        public int PendingDecodeCount { get; set; }
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
}
