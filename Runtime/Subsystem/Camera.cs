using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using UnityEngine.Rendering;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;
using NativeWebSocket;

namespace SensorFlex.Player.Subsystem
{
    public sealed class CameraSubsystem : XRCameraSubsystem
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterDescriptor()
        {
            Debug.Log("[SF] RegisterDescriptor() called");
            var cinfo = new XRCameraSubsystemDescriptor.Cinfo
            {
                id = SubsystemId,
                providerType = typeof(CameraDataProvider),
                subsystemTypeOverride = typeof(CameraSubsystem),
                supportsAverageBrightness = false,
                supportsAverageColorTemperature = false,
                supportsColorCorrection = false,
                supportsDisplayMatrix = true,
                supportsProjectionMatrix = true,
                supportsTimestamp = true,
                supportsCameraConfigurations = false,
                supportsCameraImage = true,
                supportsAverageIntensityInLumens = false,
                supportsFocusModes = false,
                supportsFaceTrackingAmbientIntensityLightEstimation = false,
                supportsFaceTrackingHDRLightEstimation = false,
                supportsWorldTrackingAmbientIntensityLightEstimation = false,
                supportsWorldTrackingHDRLightEstimation = false,
                supportsCameraGrain = false
            };
            XRCameraSubsystemDescriptor.Register(cinfo);
        }

        const string SubsystemId = "SensorFlex-Camera";

        public class CameraDataProvider : Provider
        {
            // ── WebSocket state ──────────────────────────────────────────────────
            private WebSocket webSocket;
            private bool isWebSocketPreloadStarted;
            private int framesExpectedCount;
            private int framesReceivedCount;

            // ── Shared playback state ────────────────────────────────────────────
            public static event System.Action OnFramesReady;

            private bool framesAreReady;
            private List<string> frames = new();
            private double nextFrameTime;
            private double frameInterval = 1.0 / 30.0;
            private int index = 0;        // FileSystem / WebSocket frame index
            private long timestampNs = 0;
            private SensorFlexSettings settings;

            // FileSystem / WebSocket preload array
            private Texture2D[] preloadedFrames;
            private int maxFramesToLoad;

            // Loading screen
            private const string LoadingTextureResourcePath = "Loading/loading";
            private Texture2D loadingTexture;
            private bool useLoadingTexture = true;
            private int FramesLoaded = 0;
            private int FramesToWait;

            // Per-frame metadata (populated in TarGz mode)
            private Matrix4x4[] m_FramePoses;
            private Vector4[]   m_FrameIntrinsics; // (fx, fy, cx, cy) per ring-buffer slot
            private Vector4     m_CurrentIntrinsics = new Vector4(935.3f, 935.3f, 960f, 720f);

            private Texture2D m_CurrentTexture;
            private Material  m_CameraMaterial;

            // ── TarGz streaming ring buffer ──────────────────────────────────────
            private int         m_BufSize;          // = preloadFrameCount
            private bool[]      m_SlotReady;         // slot has valid frame data
            private int[]       m_SlotGlobalIdx;     // which global frame index is in each slot
            private volatile int m_PlayHead;         // global frame index currently displayed
            private int         m_TotalFrames = int.MaxValue; // unknown until loader finishes

            // Background loader
            private struct LoadedFrame { public int FrameIndex; public byte[] Jpg; public byte[] Meta; }
            private ConcurrentQueue<LoadedFrame> m_UploadQueue;
            private Thread   m_LoadThread;
            private volatile bool m_StopLoading;
            private int      m_FilledSlots; // how many distinct slots have been filled (for loading screen)

            // ── Material / camera ────────────────────────────────────────────────
            public override Material cameraMaterial
            {
                get
                {
                    if (m_CameraMaterial == null) CreateCameraMaterial();
                    if (m_CameraMaterial != null && m_CurrentTexture != null)
                        m_CameraMaterial.mainTexture = m_CurrentTexture;
                    return m_CameraMaterial;
                }
            }

            public override XRSupportedCameraBackgroundRenderingMode supportedBackgroundRenderingMode
                => XRSupportedCameraBackgroundRenderingMode.Any;

            public override XRSupportedCameraBackgroundRenderingMode requestedBackgroundRenderingMode
            {
                get => XRSupportedCameraBackgroundRenderingMode.Any;
                set { }
            }

            public override XRCameraBackgroundRenderingMode currentBackgroundRenderingMode
                => XRCameraBackgroundRenderingMode.BeforeOpaques;

            private void CreateCameraMaterial()
            {
                Shader shader = Shader.Find("Unlit/Texture") ?? Shader.Find("UI/Default");
                if (shader != null)
                {
                    m_CameraMaterial = new Material(shader)
                    {
                        name = "CustomARCameraMaterial",
                        renderQueue = 1000
                    };
                    Debug.Log("[CustomAR] Created camera material with shader: " + shader.name);
                }
                else
                {
                    Debug.LogError("[CustomAR] Could not find any suitable shader for camera material!");
                }
            }

            public override bool permissionGranted    => true;
            public override Feature currentCamera     => Feature.WorldFacingCamera;
            public override Feature requestedCamera   { get => Feature.WorldFacingCamera; set { } }
            public override bool autoFocusEnabled     => false;
            public override bool autoFocusRequested   { get => false; set { } }

            private XRCameraConfiguration m_CurrentConfiguration;
            public override XRCameraConfiguration? currentConfiguration => m_CurrentConfiguration;


            // ── Start ────────────────────────────────────────────────────────────

            public override void Start()
            {
                useLoadingTexture = true;
                FramesLoaded = 0;

                loadingTexture = Resources.Load<Texture2D>(LoadingTextureResourcePath);
                if (loadingTexture == null)
                {
                    Debug.LogWarning("[SF] Loading texture not found at Resources/Loading/loading — skipping loading screen.");
                    useLoadingTexture = false;
                }
                else
                {
                    m_CurrentTexture = loadingTexture;
                }

                settings = SensorFlexSettings.RuntimeInstance ?? Resources.Load<SensorFlexSettings>("SensorFlexSettings");

                m_CurrentConfiguration = new XRCameraConfiguration(IntPtr.Zero, new Vector2Int(1920, 1440), framerate: 60);

                if (settings == null)
                {
                    Debug.LogError("[SF] SensorFlexSettings.asset not found in Resources/");
                    return;
                }

                maxFramesToLoad = settings.preloadFrameCount;
                FramesToWait    = settings.framesToWaitForLoadingScreen;
                frameInterval   = 1.0 / Math.Max(1, settings.targetFPS);
                Debug.Log($"[SF] Settings loaded. Mode={settings.frameSourceMode} FramesToWait={FramesToWait} BufferSize={maxFramesToLoad}");

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem)
                {
                    string folder = settings.imageFolder;
                    if (!Path.IsPathRooted(folder))
                        folder = Path.Combine(Application.streamingAssetsPath, folder);

                    if (!Directory.Exists(folder)) { Debug.LogError($"[SF] folder not found: {folder}"); return; }

                    frames.Clear();
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        if (file.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                            frames.Add(file);
                    }
                    frames.Sort(StringComparer.Ordinal);
                    if (frames.Count == 0) { Debug.LogError($"[SF] no image files found in {folder}"); return; }
                    Debug.Log($"[SF] found {frames.Count} frames in {folder}");
                }
                else if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.TarGz)
                {
                    frames.Clear();
                }
                else
                {
                    frames.Clear(); // WebSocket
                }

                PreloadFrames();
                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;
            }


            // ── Preload / streaming init ─────────────────────────────────────────

            void PreloadFrames()
            {
                framesAreReady = false;
                framesReceivedCount = 0;

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.TarGz)
                {
                    StartTarGzStreaming();
                    return;
                }

                int count = settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem
                    ? Mathf.Min(maxFramesToLoad, frames.Count)
                    : Mathf.Max(0, maxFramesToLoad);

                framesExpectedCount = count;
                preloadedFrames = new Texture2D[count];

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        tex.LoadImage(File.ReadAllBytes(frames[i]));
                        tex.Apply();
                        preloadedFrames[i] = tex;
                    }
                    framesAreReady = true;
                    Debug.Log($"[SF] Preloaded {count} frames from file system.");
                    return;
                }

                StartWebSocketPreload(count);
            }


            // ── TarGz streaming ──────────────────────────────────────────────────

            void StartTarGzStreaming()
            {
                string path = settings.tarGzFilePath;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Application.streamingAssetsPath, path);

                if (!File.Exists(path))
                {
                    Debug.LogError($"[SF] tar.gz not found: {path}");
                    return;
                }

                m_BufSize       = maxFramesToLoad;
                m_PlayHead      = 0;
                m_TotalFrames   = int.MaxValue;
                m_FilledSlots   = 0;
                m_StopLoading   = false;

                preloadedFrames  = new Texture2D[m_BufSize];
                m_FramePoses     = new Matrix4x4[m_BufSize];
                m_FrameIntrinsics = new Vector4[m_BufSize];
                m_SlotReady      = new bool[m_BufSize];
                m_SlotGlobalIdx  = new int[m_BufSize];
                for (int i = 0; i < m_BufSize; i++) m_SlotGlobalIdx[i] = -1;

                m_UploadQueue = new ConcurrentQueue<LoadedFrame>();

                m_LoadThread = new Thread(() => TarGzLoadThread(path)) { IsBackground = true, Name = "SF-TarGzLoader" };
                m_LoadThread.Start();

                Debug.Log($"[SF] TarGz streaming started. Ring buffer size={m_BufSize}");
            }

            // Runs on background thread
            void TarGzLoadThread(string path)
            {
                bool looping = settings.loopSequence;
                int iteration = 0;

                while (!m_StopLoading)
                {
                    int globalOffset = iteration == 0 ? 0 : m_TotalFrames * iteration;

                    try
                    {
                        using var fs = File.OpenRead(path);
                        using var gz = new GZipStream(fs, CompressionMode.Decompress);

                        int lastLocalFi = -1;
                        int pendingFi   = -1;
                        byte[] pendingJpg  = null;
                        byte[] pendingMeta = null;

                        foreach (var (entryPath, data) in ReadTarEntries(gz))
                        {
                            if (m_StopLoading) return;

                            var parts = entryPath.Split('/');
                            if (parts.Length < 4 || parts[1] != "frames") continue;
                            if (!int.TryParse(parts[2], out int localFi)) continue;

                            lastLocalFi = localFi;
                            string filename = parts[3];

                            // New frame folder — flush the previous one
                            if (localFi != pendingFi)
                            {
                                if (pendingFi >= 0 && pendingJpg != null && pendingMeta != null)
                                    EnqueueWithBackpressure(globalOffset + pendingFi, pendingJpg, pendingMeta);
                                pendingFi   = localFi;
                                pendingJpg  = null;
                                pendingMeta = null;
                            }

                            if (filename == "rgb.jpg")   pendingJpg  = data;
                            else if (filename == "meta.bin") pendingMeta = data;

                            if (pendingJpg != null && pendingMeta != null)
                            {
                                EnqueueWithBackpressure(globalOffset + pendingFi, pendingJpg, pendingMeta);
                                pendingFi = -1; pendingJpg = null; pendingMeta = null;
                            }
                        }

                        // Flush final frame
                        if (pendingFi >= 0 && pendingJpg != null && pendingMeta != null)
                            EnqueueWithBackpressure(globalOffset + pendingFi, pendingJpg, pendingMeta);

                        if (iteration == 0)
                            m_TotalFrames = lastLocalFi + 1;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[SF] TarGz loader error: " + e);
                        return;
                    }

                    if (!looping) break;
                    iteration++;
                }
            }

            void EnqueueWithBackpressure(int globalFi, byte[] jpg, byte[] meta)
            {
                // Wait until the player has consumed enough to free a slot
                while (!m_StopLoading && globalFi - m_PlayHead >= m_BufSize)
                    Thread.Sleep(1);

                if (!m_StopLoading)
                    m_UploadQueue.Enqueue(new LoadedFrame { FrameIndex = globalFi, Jpg = jpg, Meta = meta });
            }

            // Called every frame on main thread — upload up to N pending frames
            const int UploadBatchSize = 3;
            void DrainUploadQueue()
            {
                int uploaded = 0;
                while (uploaded < UploadBatchSize && m_UploadQueue.TryDequeue(out var item))
                {
                    int slot = item.FrameIndex % m_BufSize;

                    // Create texture slot on first use, reuse thereafter
                    if (preloadedFrames[slot] == null)
                        preloadedFrames[slot] = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                    preloadedFrames[slot].LoadImage(item.Jpg);
                    preloadedFrames[slot].Apply();

                    if (item.Meta != null && item.Meta.Length >= 160)
                    {
                        m_FramePoses[slot]      = ParseMatrix4x4(item.Meta, 0);
                        m_FrameIntrinsics[slot] = ParseIntrinsics(item.Meta, 64);
                    }

                    m_SlotGlobalIdx[slot] = item.FrameIndex;
                    m_SlotReady[slot]     = true;

                    m_FilledSlots++;
                    if (!framesAreReady && m_FilledSlots >= FramesToWait)
                        framesAreReady = true;

                    uploaded++;
                }
            }


            // ── Frame decoding helpers ───────────────────────────────────────────

            static Matrix4x4 ParseMatrix4x4(byte[] src, int offset)
            {
                var f = new float[16];
                Buffer.BlockCopy(src, offset, f, 0, 64);
                var m = new Matrix4x4();
                m.m00=f[0];  m.m01=f[1];  m.m02=f[2];  m.m03=f[3];
                m.m10=f[4];  m.m11=f[5];  m.m12=f[6];  m.m13=f[7];
                m.m20=f[8];  m.m21=f[9];  m.m22=f[10]; m.m23=f[11];
                m.m30=f[12]; m.m31=f[13]; m.m32=f[14]; m.m33=f[15];
                return m;
            }

            // K = [fx 0 cx; 0 fy cy; 0 0 1] row-major → fx@+0, cx@+8, fy@+16, cy@+20
            static Vector4 ParseIntrinsics(byte[] src, int offset)
            {
                float fx = BitConverter.ToSingle(src, offset);
                float cx = BitConverter.ToSingle(src, offset + 8);
                float fy = BitConverter.ToSingle(src, offset + 16);
                float cy = BitConverter.ToSingle(src, offset + 20);
                return new Vector4(fx, fy, cx, cy);
            }


            // ── Minimal tar reader ───────────────────────────────────────────────

            static IEnumerable<(string path, byte[] data)> ReadTarEntries(Stream stream)
            {
                var header = new byte[512];
                while (true)
                {
                    if (!ReadFull(stream, header, 512)) yield break;
                    if (header[0] == 0) yield break; // end-of-archive zero block

                    string name  = Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0');
                    string magic = Encoding.ASCII.GetString(header, 257, 6);
                    if (magic.StartsWith("ustar", StringComparison.Ordinal))
                    {
                        string prefix = Encoding.UTF8.GetString(header, 345, 155).TrimEnd('\0');
                        if (!string.IsNullOrEmpty(prefix)) name = prefix + "/" + name;
                    }

                    string sizeStr = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                    long size = string.IsNullOrEmpty(sizeStr) ? 0 : Convert.ToInt64(sizeStr.Trim(), 8);

                    char typeflag  = (char)header[156];
                    long dataBlocks = (size + 511) / 512;

                    if (typeflag == '0' || typeflag == '\0')
                    {
                        var data = new byte[size];
                        if (size > 0) ReadFull(stream, data, (int)size);
                        SkipStream(stream, dataBlocks * 512 - size);
                        yield return (name, data);
                    }
                    else
                    {
                        SkipStream(stream, dataBlocks * 512);
                    }
                }
            }

            static bool ReadFull(Stream s, byte[] buf, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int n = s.Read(buf, total, count - total);
                    if (n <= 0) return total > 0;
                    total += n;
                }
                return true;
            }

            static void SkipStream(Stream s, long count)
            {
                var buf = new byte[4096];
                while (count > 0)
                {
                    int n = s.Read(buf, 0, (int)Math.Min(count, buf.Length));
                    if (n <= 0) break;
                    count -= n;
                }
            }


            // ── Frame load (FileSystem / WebSocket) ──────────────────────────────

            void LoadFrame(int i)
            {
                if (preloadedFrames == null || i < 0 || i >= preloadedFrames.Length) return;
                m_CurrentTexture = preloadedFrames[i];
                timestampNs += (long)(frameInterval * 1_000_000_000L);
            }


            // ── WebSocket preload ────────────────────────────────────────────────

            async void StartWebSocketPreload(int requestedFrameCount)
            {
                if (isWebSocketPreloadStarted) return;
                isWebSocketPreloadStarted = true;

                Debug.Log($"[SF] Starting WebSocket preload from {settings.webSocketUrl}, requesting {requestedFrameCount} frames.");
                webSocket = new WebSocket(settings.webSocketUrl);

                webSocket.OnOpen  += () => { Debug.Log("[SF] WebSocket connected."); _ = webSocket.SendText($"GET_FRAMES {requestedFrameCount}"); };
                webSocket.OnError += (e) => Debug.LogError("[SF] WebSocket error: " + e);
                webSocket.OnClose += (c) => Debug.Log("[SF] WebSocket closed: " + c);

                webSocket.OnMessage += (byte[] msg) =>
                {
                    if (msg == null || msg.Length < 5) return;
                    int fi = BitConverter.ToInt32(msg, 0);
                    if (fi < 0 || fi >= preloadedFrames.Length) { Debug.LogWarning($"[SF] WS out-of-range frameIndex={fi}"); return; }

                    int imgLen = msg.Length - 4;
                    var imgBytes = new byte[imgLen];
                    Buffer.BlockCopy(msg, 4, imgBytes, 0, imgLen);

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(imgBytes)) { Debug.LogWarning($"[SF] WS failed to decode frame {fi}"); UnityEngine.Object.Destroy(tex); return; }
                    tex.Apply();

                    if (preloadedFrames[fi] != null) UnityEngine.Object.Destroy(preloadedFrames[fi]);
                    else framesReceivedCount++;

                    preloadedFrames[fi] = tex;
                    if (framesReceivedCount >= framesExpectedCount)
                    {
                        framesAreReady = true;
                        Debug.Log($"[SF] WebSocket preload complete. {framesReceivedCount}/{framesExpectedCount} frames.");
                    }
                };

                try { await webSocket.Connect(); }
                catch (Exception e) { Debug.LogError("[SF] WebSocket connect exception: " + e); }
            }


            // ── Update loop ──────────────────────────────────────────────────────

            void UpdateFrameIfNeeded()
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                webSocket?.DispatchMessageQueue();
#endif
                if (Time.realtimeSinceStartupAsDouble < nextFrameTime) return;
                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.TarGz)
                {
                    UpdateTarGzFrame();
                    return;
                }

                if (useLoadingTexture)
                {
                    FramesLoaded++;
                    if (FramesLoaded >= FramesToWait && framesAreReady)
                    {
                        useLoadingTexture = false;
                        index = 0;
                        LoadFrame(index);
                        OnFramesReady?.Invoke();
                    }
                    return;
                }

                index++;
                if (index >= preloadedFrames.Length)
                    index = settings.loopSequence ? 0 : preloadedFrames.Length - 1;

                LoadFrame(index);
                OnFramesReady?.Invoke();
            }

            void UpdateTarGzFrame()
            {
                // Upload pending decoded frames (max UploadBatchSize per tick)
                DrainUploadQueue();

                if (useLoadingTexture)
                {
                    FramesLoaded++;
                    if (FramesLoaded >= FramesToWait && framesAreReady)
                    {
                        useLoadingTexture = false;
                        PlayTarGzSlot(0);
                        OnFramesReady?.Invoke();
                    }
                    return;
                }

                int next = m_PlayHead + 1;
                if (m_TotalFrames != int.MaxValue && next >= m_TotalFrames)
                    return; // end of session (no loop yet)

                int nextSlot = next % m_BufSize;
                if (!m_SlotReady[nextSlot] || m_SlotGlobalIdx[nextSlot] != next)
                    return; // stall — frame not ready yet

                m_PlayHead = next;
                PlayTarGzSlot(nextSlot);
                OnFramesReady?.Invoke();
            }

            void PlayTarGzSlot(int slot)
            {
                m_CurrentTexture = preloadedFrames[slot];
                timestampNs += (long)(frameInterval * 1_000_000_000L);
                PoseBridge.SetARKitPose(m_FramePoses[slot]);
                m_CurrentIntrinsics = m_FrameIntrinsics[slot];
            }


            // ── XR Provider overrides ────────────────────────────────────────────

            public override bool TryGetFrame(XRCameraParams cameraParams, out XRCameraFrame frame)
            {
                UpdateFrameIfNeeded();

                float aspect = (float)cameraParams.screenWidth / cameraParams.screenHeight;
                frame = new XRCameraFrame(
                    timestampNs, 0f, 0f, Color.black,
                    Matrix4x4.Perspective(60f, aspect, 0.1f, 100f),
                    Matrix4x4.identity,
                    TrackingState.Tracking, IntPtr.Zero,
                    XRCameraFrameProperties.Timestamp | XRCameraFrameProperties.ProjectionMatrix | XRCameraFrameProperties.DisplayMatrix,
                    0f, 0.0, 0f, 0f, Color.black, Vector3.zero,
                    new SphericalHarmonicsL2(), new XRTextureDescriptor(), 0f);
                return true;
            }

            public override bool TryAcquireLatestCpuImage(out XRCpuImage.Cinfo cameraImageCinfo)
            {
                cameraImageCinfo = default;
                return false;
            }

            public override NativeArray<XRTextureDescriptor> GetTextureDescriptors(XRTextureDescriptor defaultDescriptor, Allocator allocator)
            {
                if (m_CurrentTexture == null)
                    return new NativeArray<XRTextureDescriptor>(0, allocator);

                var descriptors = new NativeArray<XRTextureDescriptor>(1, allocator);
                descriptors[0] = new XRTextureDescriptor(
                    m_CurrentTexture.GetNativeTexturePtr(),
                    m_CurrentTexture.width, m_CurrentTexture.height, m_CurrentTexture.mipmapCount,
                    m_CurrentTexture.format, Shader.PropertyToID("_MainTex"), 0, TextureDimension.Tex2D);
                Debug.Log($"[SF] GetTextureDescriptors: {descriptors[0].width}x{descriptors[0].height}");
                return descriptors;
            }

            public override bool TryGetIntrinsics(out XRCameraIntrinsics cameraIntrinsics)
            {
                cameraIntrinsics = new XRCameraIntrinsics(
                    new Vector2(m_CurrentIntrinsics.x, m_CurrentIntrinsics.y),
                    new Vector2(m_CurrentIntrinsics.z, m_CurrentIntrinsics.w),
                    new Vector2Int(1920, 1440));
                return true;
            }

            public override async void Stop()
            {
                // Stop background loader first
                m_StopLoading = true;
                if (m_LoadThread != null && m_LoadThread.IsAlive)
                {
                    m_LoadThread.Join(500); // wait up to 500ms
                    m_LoadThread = null;
                }
                m_UploadQueue = null;

                if (webSocket != null)
                {
                    try { await webSocket.Close(); }
                    catch (Exception e) { Debug.LogWarning("[SF] WebSocket close exception: " + e); }
                    webSocket = null;
                }

                if (preloadedFrames != null)
                {
                    for (int i = 0; i < preloadedFrames.Length; i++)
                    {
                        if (preloadedFrames[i] != null) { UnityEngine.Object.Destroy(preloadedFrames[i]); preloadedFrames[i] = null; }
                    }
                }

                m_FramePoses      = null;
                m_FrameIntrinsics = null;
                m_CurrentTexture  = null;
                PoseBridge.Clear();
            }
        }
    }
}
