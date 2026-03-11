using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARSubsystems;
using SensorFlex.Player.Library;

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
            // ── Events ───────────────────────────────────────────────────────────

            /// <summary>Fires on the main thread each time a new frame is displayed.</summary>
            public static event Action OnFramesReady;

            /// <summary>
            /// The active <see cref="FrameLoader"/> for the current session.
            /// Other subsystems (e.g. OcclusionSubsystem) can read depth and
            /// pose data here without performing any additional IO.
            /// Null when the subsystem is not running.
            /// </summary>
            internal static FrameLoader ActiveLoader { get; private set; }

            // ── Playback state ───────────────────────────────────────────────────

            private double nextFrameTime;
            private double frameInterval = 1.0 / 30.0;
            private int    index = 0;
            private long   timestampNs = 0;

            // ── Loading screen ───────────────────────────────────────────────────

            private const string LoadingTextureResourcePath = "Loading/loading";
            private Texture2D loadingTexture;
            private bool useLoadingTexture = true;
            private int  FramesLoaded = 0;
            private int  FramesToWait;

            // ── Settings & loader ────────────────────────────────────────────────

            private SensorFlexSettings settings;
            private int maxFramesToLoad;
            private FrameLoader m_Loader;

            // ── Camera state ─────────────────────────────────────────────────────

            private Texture2D m_CurrentTexture;
            private Material  m_CameraMaterial;
            private Vector4   m_CurrentIntrinsics = new Vector4(935.3f, 935.3f, 960f, 720f);
            private XRCameraConfiguration m_CurrentConfiguration;


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

            void CreateCameraMaterial()
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

            public override bool    permissionGranted  => true;
            public override Feature currentCamera      => Feature.WorldFacingCamera;
            public override Feature requestedCamera    { get => Feature.WorldFacingCamera; set { } }
            public override bool    autoFocusEnabled   => false;
            public override bool    autoFocusRequested { get => false; set { } }

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

                if (settings == null) { Debug.LogError("[SF] SensorFlexSettings.asset not found in Resources/"); return; }

                maxFramesToLoad = settings.preloadFrameCount;
                FramesToWait    = Math.Max(1, maxFramesToLoad / 4);

                m_Loader = new FrameLoader();
                m_Loader.Start(settings, maxFramesToLoad, FramesToWait);
                ActiveLoader = m_Loader;

                // ZIP: FPS is read from meta.json inside the archive
                frameInterval = settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.Zip
                    ? m_Loader.FrameInterval
                    : 1.0 / Math.Max(1, settings.targetFPS);

                Debug.Log($"[SF] Mode={settings.frameSourceMode} FramesToWait={FramesToWait} BufferSize={maxFramesToLoad} FrameInterval={frameInterval:F4}s");

                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;
            }


            // ── Update loop ──────────────────────────────────────────────────────

            void UpdateFrameIfNeeded()
            {
                if (m_Loader == null) return;

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.WebSocket)
                    m_Loader.DispatchWebSocket();

                if (Time.realtimeSinceStartupAsDouble < nextFrameTime) return;
                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.Zip)
                {
                    UpdateZipFrame();
                    return;
                }

                // FileSystem / WebSocket
                if (useLoadingTexture)
                {
                    FramesLoaded++;
                    if (FramesLoaded >= FramesToWait && m_Loader.IsReady)
                    {
                        useLoadingTexture = false;
                        index = 0;
                        LoadFrame(0);
                        OnFramesReady?.Invoke();
                    }
                    return;
                }

                index++;
                var frames = m_Loader.Frames;
                if (index >= frames.Length)
                    index = settings.loopSequence ? 0 : frames.Length - 1;

                LoadFrame(index);
                OnFramesReady?.Invoke();
            }

            void UpdateZipFrame()
            {
                m_Loader.DrainUploadQueue();

                if (useLoadingTexture)
                {
                    FramesLoaded++;
                    if (FramesLoaded >= FramesToWait && m_Loader.IsReady)
                    {
                        useLoadingTexture = false;
                        PlayZipSlot(0);
                        OnFramesReady?.Invoke();
                    }
                    return;
                }

                int next = m_Loader.PlayHead + 1;
                if (m_Loader.TotalFrames != int.MaxValue && next >= m_Loader.TotalFrames) return;

                int nextSlot = next % m_Loader.BufSize;
                if (!m_Loader.SlotReady[nextSlot] || m_Loader.SlotGlobalIdx[nextSlot] != next) return;

                m_Loader.PlayHead = next;
                PlayZipSlot(nextSlot);
                OnFramesReady?.Invoke();
            }

            void LoadFrame(int i)
            {
                var frames = m_Loader?.Frames;
                if (frames == null || i < 0 || i >= frames.Length) return;
                m_CurrentTexture = frames[i];
                timestampNs += (long)(frameInterval * 1_000_000_000L);
            }

            void PlayZipSlot(int slot)
            {
                m_CurrentTexture    = m_Loader.Frames[slot];
                timestampNs        += (long)(frameInterval * 1_000_000_000L);
                m_CurrentIntrinsics = m_Loader.Intrinsics[slot];
                PoseBridge.SetUnityPose(ArchiveIOUtils.ConvertToUnityPose(m_Loader.Poses[slot], m_Loader.CoordConvMatrix));
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
                // Debug.Log($"[SF] GetTextureDescriptors: {descriptors[0].width}x{descriptors[0].height}");
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
                ActiveLoader = null;

                if (m_Loader != null)
                {
                    await m_Loader.StopAsync();
                    m_Loader.DestroyTextures();
                    m_Loader = null;
                }

                m_CurrentTexture = null;
                PoseBridge.Clear();
            }
        }
    }
}
