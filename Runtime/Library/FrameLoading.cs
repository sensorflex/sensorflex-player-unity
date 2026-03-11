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
            if (Frames == null) return;

            for (int i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] == null) continue;
                UnityEngine.Object.Destroy(Frames[i]);
                Frames[i] = null;
            }
        }
    }

    internal interface IFrameLoaderBackend
    {
        void Start(SensorFlexSettings settings, IFrameLoaderState state, int framesToWait);
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

        public void Start(SensorFlexSettings settings, int maxFramesToLoad, int framesToWait)
        {
            var state = new FrameLoaderState(maxFramesToLoad)
            {
                IsReady = false,
                PlayHead = 0,
                FrameInterval = 1.0 / Math.Max(1, settings.targetFPS)
            };

            m_Backend = CreateBackend(settings.frameSourceMode);
            m_State = state;
            m_Backend.Start(settings, state, framesToWait);
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

        static IFrameLoaderBackend CreateBackend(SensorFlexSettings.FrameSourceMode mode)
        {
            return mode switch
            {
                SensorFlexSettings.FrameSourceMode.FileSystem => new FileSystemFrameLoaderBackend(),
                SensorFlexSettings.FrameSourceMode.Zip => new ZipFrameLoaderBackend(),
                SensorFlexSettings.FrameSourceMode.WebSocket => new WebSocketFrameLoaderBackend(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }
    }

    internal sealed class FileSystemFrameLoaderBackend : IFrameLoaderBackend
    {
        public void Start(SensorFlexSettings settings, IFrameLoaderState state, int framesToWait)
        {
            string folder = settings.imageFolder;
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
        WebSocket m_WebSocket;
        int m_ExpectedFrames;
        int m_ReceivedFrames;
        bool m_Started;

        public async void Start(SensorFlexSettings settings, IFrameLoaderState state, int framesToWait)
        {
            if (m_Started) return;

            m_Started = true;
            m_ExpectedFrames = state.BufSize;
            state.AllocateLinearFrames(state.BufSize);

            Debug.Log($"[SF] WebSocket connecting to {settings.webSocketUrl}, requesting {state.BufSize} frames.");
            m_WebSocket = new WebSocket(settings.webSocketUrl);

            m_WebSocket.OnOpen += () =>
            {
                Debug.Log("[SF] WebSocket connected.");
                _ = m_WebSocket.SendText($"GET_FRAMES {m_ExpectedFrames}");
            };
            m_WebSocket.OnError += error => Debug.LogError("[SF] WebSocket error: " + error);
            m_WebSocket.OnClose += code => Debug.Log("[SF] WebSocket closed: " + code);
            m_WebSocket.OnMessage += message => HandleMessage(state, message);

            try
            {
                await m_WebSocket.Connect();
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] WebSocket connect exception: " + e);
            }
        }

        void HandleMessage(IFrameLoaderState state, byte[] message)
        {
            if (message == null || message.Length < 5) return;

            int frameIndex = BitConverter.ToInt32(message, 0);
            if (frameIndex < 0 || frameIndex >= state.Frames.Length)
            {
                Debug.LogWarning($"[SF] WS out-of-range frameIndex={frameIndex}");
                return;
            }

            int imageLen = message.Length - 4;
            var imageBytes = new byte[imageLen];
            Buffer.BlockCopy(message, 4, imageBytes, 0, imageLen);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(imageBytes))
            {
                Debug.LogWarning($"[SF] WS failed to decode frame {frameIndex}");
                UnityEngine.Object.Destroy(tex);
                return;
            }

            tex.Apply();

            if (state.Frames[frameIndex] != null)
            {
                UnityEngine.Object.Destroy(state.Frames[frameIndex]);
            }
            else
            {
                m_ReceivedFrames++;
            }

            state.Frames[frameIndex] = tex;
            if (m_ReceivedFrames >= m_ExpectedFrames)
            {
                state.TotalFrames = m_ExpectedFrames;
                state.IsReady = true;
                Debug.Log($"[SF] WebSocket preload complete: {m_ReceivedFrames}/{m_ExpectedFrames} frames.");
            }
        }

        public void DrainMainThreadWork() { }

        public void Dispatch()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            m_WebSocket?.DispatchMessageQueue();
#endif
        }

        public async Task StopAsync()
        {
            if (m_WebSocket == null) return;

            try
            {
                await m_WebSocket.Close();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SF] WebSocket close: " + e);
            }

            m_WebSocket = null;
        }
    }

    internal sealed class ZipFrameLoaderBackend : IFrameLoaderBackend
    {
        struct LoadedFrame
        {
            public int FrameIndex;
            public byte[] Jpg;
            public byte[] Meta;
            public byte[] DepthBin;
        }

        const int UploadBatchSize = 3;

        SensorFlexSettings m_Settings;
        IFrameLoaderState m_State;
        int m_FramesToWait;
        volatile bool m_StopLoading;
        Thread m_LoadThread;
        ConcurrentQueue<LoadedFrame> m_UploadQueue;

        public void Start(SensorFlexSettings settings, IFrameLoaderState state, int framesToWait)
        {
            m_Settings = settings;
            m_State = state;
            m_FramesToWait = framesToWait;
            m_StopLoading = false;

            string path = settings.zipFilePath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Application.streamingAssetsPath, path);

            if (!File.Exists(path))
            {
                Debug.LogError($"[SF] ZIP not found: {path}");
                return;
            }

            string sceneId = ReadMeta(path, state);
            if (string.IsNullOrEmpty(sceneId)) return;

            state.AllocateRingBuffer();

            m_UploadQueue = new ConcurrentQueue<LoadedFrame>();
            m_LoadThread = new Thread(() => LoadFrames(path, sceneId))
            {
                IsBackground = true,
                Name = "SF-ZipLoader"
            };
            m_LoadThread.Start();

            Debug.Log($"[SF] ZIP streaming started. Ring buffer size={state.BufSize}");
        }

        string ReadMeta(string path, IFrameLoaderState state)
        {
            try
            {
                using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
                ZipArchiveEntry metaEntry = null;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name == "meta.json")
                    {
                        metaEntry = entry;
                        break;
                    }
                }

                if (metaEntry == null)
                {
                    Debug.LogError("[SF] meta.json not found in ZIP");
                    return null;
                }

                string json;
                using (var sr = new StreamReader(metaEntry.Open()))
                    json = sr.ReadToEnd();

                var meta = JsonUtility.FromJson<ArchiveIOUtils.SceneMetaJson>(json);
                state.TotalFrames = meta.n_frames;
                state.FrameInterval = 1.0 / Math.Max(1, meta.fps);
                state.CoordConvMatrix = meta.coordinate_system != null
                    ? ArchiveIOUtils.ComputeConversionMatrix(meta.coordinate_system.handedness, meta.coordinate_system.forward)
                    : Matrix4x4.identity;

                Debug.Log($"[SF] ZIP meta: scene={meta.scene_id} frames={state.TotalFrames} fps={meta.fps} " +
                          $"handedness={meta.coordinate_system?.handedness} forward={meta.coordinate_system?.forward}");
                return meta.scene_id;
            }
            catch (Exception e)
            {
                Debug.LogError("[SF] ZIP meta read error: " + e);
                return null;
            }
        }

        void LoadFrames(string path, string sceneId)
        {
            bool looping = m_Settings.loopSequence;
            int iteration = 0;

            while (!m_StopLoading)
            {
                int globalOffset = iteration * m_State.TotalFrames;
                try
                {
                    using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
                    for (int frameIndex = 0; frameIndex < m_State.TotalFrames && !m_StopLoading; frameIndex++)
                    {
                        string prefix = $"{sceneId}/frames/{frameIndex:D6}/";
                        var rgbEntry = archive.GetEntry(prefix + "rgb.jpg");
                        var metaEntry = archive.GetEntry(prefix + "meta.json");
                        var depthEntry = archive.GetEntry(prefix + "depth.bin");
                        if (rgbEntry == null || metaEntry == null) continue;

                        Enqueue(globalOffset + frameIndex, ArchiveIOUtils.ReadEntry(rgbEntry), ArchiveIOUtils.ReadEntry(metaEntry),
                            depthEntry != null ? ArchiveIOUtils.ReadEntry(depthEntry) : null);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[SF] ZIP loader error: " + e);
                    return;
                }

                if (!looping) break;
                iteration++;
            }
        }

        void Enqueue(int globalFrameIndex, byte[] jpg, byte[] meta, byte[] depth)
        {
            while (!m_StopLoading && globalFrameIndex - m_State.PlayHead >= m_State.BufSize)
                Thread.Sleep(1);

            if (!m_StopLoading)
            {
                m_UploadQueue.Enqueue(new LoadedFrame
                {
                    FrameIndex = globalFrameIndex,
                    Jpg = jpg,
                    Meta = meta,
                    DepthBin = depth
                });
            }
        }

        public void DrainMainThreadWork()
        {
            if (m_UploadQueue == null) return;

            int uploaded = 0;
            while (uploaded < UploadBatchSize && m_UploadQueue.TryDequeue(out var item))
            {
                int slot = item.FrameIndex % m_State.BufSize;

                if (m_State.Frames[slot] == null)
                    m_State.Frames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                m_State.Frames[slot].LoadImage(item.Jpg);
                m_State.Frames[slot].Apply();
                m_State.DepthBins[slot] = item.DepthBin;

                if (item.Meta != null && item.Meta.Length > 0)
                {
                    string json = Encoding.UTF8.GetString(item.Meta);
                    var pose = ArchiveIOUtils.ExtractFloatsFromField(json, "pose");
                    var intr = ArchiveIOUtils.ExtractFloatsFromField(json, "intrinsic");
                    if (pose != null && pose.Length >= 16) m_State.Poses[slot] = ArchiveIOUtils.FloatsToMatrix4x4(pose);
                    if (intr != null && intr.Length >= 9) m_State.Intrinsics[slot] = new Vector4(intr[0], intr[4], intr[2], intr[5]);
                }

                m_State.SlotGlobalIdx[slot] = item.FrameIndex;
                m_State.SlotReady[slot] = true;
                m_State.MarkBuffered(m_FramesToWait);
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
}
