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
            enum StartupStage
            {
                LoadingSceneMesh,
                WarmingUpFrames,
                Playing
            }

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

            private const bool EnableProgrammaticLoadingOverlay = true;
            private bool showLoadingScreen = true;
            private int  FramesLoaded = 0;
            private int  FramesToWait;
            private LoadingScreenOverlay m_LoadingOverlay;
            private StartupStage m_StartupStage;
            private ScannedSceneMeshLoadOperation m_ScannedSceneMeshLoadOperation;

            // ── Settings & loader ────────────────────────────────────────────────

            private SensorFlexSettings settings;
            private int maxFramesToLoad;
            private FrameLoader m_Loader;

            // ── Camera state ─────────────────────────────────────────────────────

            private Texture2D m_CurrentTexture;
            private Material  m_CameraMaterial;
            private Vector4   m_CurrentIntrinsics = new Vector4(935.3f, 935.3f, 960f, 720f);
            private XRCameraConfiguration m_CurrentConfiguration;
            private bool m_LoggedFirstTryGetFrame;
            private bool m_LoggedPreloadComplete;
            private bool m_LoggedFirstTextureSet;
            private bool m_LoggedEmptyTextureDescriptors;
            private bool m_LoggedNonEmptyTextureDescriptors;


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
                Debug.Log("[SF] CameraDataProvider.Start()");

                showLoadingScreen = true;
                FramesLoaded = 0;
                m_LoggedFirstTryGetFrame = false;
                m_LoggedPreloadComplete = false;
                m_LoggedFirstTextureSet = false;
                m_LoggedEmptyTextureDescriptors = false;
                m_LoggedNonEmptyTextureDescriptors = false;
                if (EnableProgrammaticLoadingOverlay)
                {
                    m_LoadingOverlay ??= new LoadingScreenOverlay();
                    m_LoadingOverlay.Show("Loading SensorFlex frames...");
                }

                settings = SensorFlexSettings.RuntimeInstance ?? Resources.Load<SensorFlexSettings>("SensorFlexSettings");
                m_CurrentConfiguration = new XRCameraConfiguration(IntPtr.Zero, new Vector2Int(1920, 1440), framerate: 60);

                if (settings == null) { Debug.LogError("[SF] SensorFlexSettings.asset not found in Resources/"); return; }

                maxFramesToLoad = settings.preloadFrameCount;
                FramesToWait    = Math.Max(1, maxFramesToLoad / 4);

                ScannedSceneMeshBridge.Clear();
                m_ScannedSceneMeshLoadOperation = ScannedSceneMeshLoadOperation.Start(settings);
                if (m_ScannedSceneMeshLoadOperation != null)
                {
                    m_StartupStage = StartupStage.LoadingSceneMesh;
                    if (EnableProgrammaticLoadingOverlay)
                        m_LoadingOverlay.Show("Loading scanned mesh...");
                }
                else
                {
                    BeginFrameWarmup();
                }

                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;
            }


            // ── Update loop ──────────────────────────────────────────────────────

            void UpdateFrameIfNeeded()
            {
                if (m_StartupStage == StartupStage.LoadingSceneMesh)
                {
                    TryCompleteSceneMeshLoad();
                    return;
                }

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
                if (m_StartupStage == StartupStage.WarmingUpFrames && showLoadingScreen)
                {
                    FramesLoaded++;
                    if (EnableProgrammaticLoadingOverlay)
                        UpdateLoadingScreenText();
                    if (FramesLoaded >= FramesToWait && m_Loader.IsReady)
                    {
                        if (!m_LoggedPreloadComplete)
                        {
                            Debug.Log($"[SF] Preload complete (FileSystem/WebSocket). FramesLoaded={FramesLoaded} FramesToWait={FramesToWait} TotalFrames={m_Loader.Frames?.Length ?? 0}");
                            m_LoggedPreloadComplete = true;
                        }

                        showLoadingScreen = false;
                        if (EnableProgrammaticLoadingOverlay)
                            m_LoadingOverlay?.Hide();
                        m_StartupStage = StartupStage.Playing;
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

                if (m_StartupStage == StartupStage.WarmingUpFrames && showLoadingScreen)
                {
                    FramesLoaded++;
                    if (EnableProgrammaticLoadingOverlay)
                        UpdateLoadingScreenText();
                    if (FramesLoaded >= FramesToWait && m_Loader.IsReady)
                    {
                        if (!m_LoggedPreloadComplete)
                        {
                            Debug.Log($"[SF] Preload complete (ZIP). FramesLoaded={FramesLoaded} FramesToWait={FramesToWait} TotalFrames={m_Loader.TotalFrames} PlayHead={m_Loader.PlayHead}");
                            m_LoggedPreloadComplete = true;
                        }

                        showLoadingScreen = false;
                        if (EnableProgrammaticLoadingOverlay)
                            m_LoadingOverlay?.Hide();
                        m_StartupStage = StartupStage.Playing;
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
                SetCurrentTexture(frames[i]);
                timestampNs += (long)(frameInterval * 1_000_000_000L);
            }

            void PlayZipSlot(int slot)
            {
                SetCurrentTexture(m_Loader.Frames[slot]);
                timestampNs        += (long)(frameInterval * 1_000_000_000L);
                m_CurrentIntrinsics = m_Loader.Intrinsics[slot];
                PoseBridge.SetUnityPose(ArchiveIOUtils.ConvertToUnityPose(
                    m_Loader.Poses[slot],
                    m_Loader.CoordConvMatrix,
                    m_Loader.UseScanNetPoseOpticalAxisFix));
            }

            void TryCompleteSceneMeshLoad()
            {
                if (m_ScannedSceneMeshLoadOperation == null)
                {
                    BeginFrameWarmup();
                    return;
                }

                if (!m_ScannedSceneMeshLoadOperation.TryComplete(out var mesh))
                    return;

                if (mesh != null)
                {
                    ScannedSceneMeshBridge.SetMesh(mesh, m_ScannedSceneMeshLoadOperation.SceneId);
                    Debug.Log($"[SF] Scanned mesh ready: vertices={mesh.vertexCount} triangles={mesh.triangles.Length / 3}");
                }

                m_ScannedSceneMeshLoadOperation = null;
                BeginFrameWarmup();
            }

            void BeginFrameWarmup()
            {
                m_StartupStage = StartupStage.WarmingUpFrames;
                showLoadingScreen = true;

                m_Loader = new FrameLoader();
                m_Loader.Start(settings, maxFramesToLoad, FramesToWait);
                ActiveLoader = m_Loader;

                frameInterval = settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.Zip
                    ? m_Loader.FrameInterval
                    : 1.0 / Math.Max(1, settings.targetFPS);

                Debug.Log($"[SF] Mode={settings.frameSourceMode} FramesToWait={FramesToWait} BufferSize={maxFramesToLoad} FrameInterval={frameInterval:F4}s");
                if (EnableProgrammaticLoadingOverlay)
                    UpdateLoadingScreenText();
            }

            void SetCurrentTexture(Texture2D texture)
            {
                m_CurrentTexture = texture;
                if (!m_LoggedFirstTextureSet && m_CurrentTexture != null)
                {
                    Debug.Log($"[SF] First playback texture set: {m_CurrentTexture.width}x{m_CurrentTexture.height} name={m_CurrentTexture.name}");
                    m_LoggedFirstTextureSet = true;
                }

                if (m_CameraMaterial != null)
                    m_CameraMaterial.mainTexture = m_CurrentTexture;
            }

            void UpdateLoadingScreenText()
            {
                if (m_LoadingOverlay == null || settings == null) return;

                string source = settings.frameSourceMode.ToString();
                int progress = Math.Min(FramesLoaded, FramesToWait);
                if (m_StartupStage == StartupStage.LoadingSceneMesh)
                {
                    m_LoadingOverlay.Show($"Loading scanned mesh...\nSource: {source}");
                    return;
                }

                m_LoadingOverlay.Show($"Loading SensorFlex frames...\nSource: {source}\nWarmup: {progress}/{FramesToWait}");
            }


            // ── XR Provider overrides ────────────────────────────────────────────

            public override bool TryGetFrame(XRCameraParams cameraParams, out XRCameraFrame frame)
            {
                if (!m_LoggedFirstTryGetFrame)
                {
                    Debug.Log($"[SF] TryGetFrame started. Screen={cameraParams.screenWidth}x{cameraParams.screenHeight} Loading={showLoadingScreen} Ready={m_Loader?.IsReady ?? false}");
                    m_LoggedFirstTryGetFrame = true;
                }

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
                {
                    if (!m_LoggedEmptyTextureDescriptors)
                    {
                        Debug.Log($"[SF] GetTextureDescriptors returned empty. Loading={showLoadingScreen} Ready={m_Loader?.IsReady ?? false}");
                        m_LoggedEmptyTextureDescriptors = true;
                    }

                    return new NativeArray<XRTextureDescriptor>(0, allocator);
                }

                var descriptors = new NativeArray<XRTextureDescriptor>(1, allocator);
                descriptors[0] = new XRTextureDescriptor(
                    m_CurrentTexture.GetNativeTexturePtr(),
                    m_CurrentTexture.width, m_CurrentTexture.height, m_CurrentTexture.mipmapCount,
                    m_CurrentTexture.format, Shader.PropertyToID("_MainTex"), 0, XRTextureType.Texture2D);
                if (!m_LoggedNonEmptyTextureDescriptors)
                {
                    Debug.Log($"[SF] GetTextureDescriptors publishing {descriptors[0].width}x{descriptors[0].height} texture.");
                    m_LoggedNonEmptyTextureDescriptors = true;
                }
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
                m_ScannedSceneMeshLoadOperation = null;

                if (m_Loader != null)
                {
                    await m_Loader.StopAsync();
                    m_Loader.DestroyTextures();
                    m_Loader = null;
                }

                showLoadingScreen = false;
                if (EnableProgrammaticLoadingOverlay)
                {
                    m_LoadingOverlay?.Dispose();
                    m_LoadingOverlay = null;
                }
                SetCurrentTexture(null);
                ScannedSceneMeshBridge.Clear();
                PoseBridge.Clear();
            }
        }

        sealed class LoadingScreenOverlay : IDisposable
        {
            readonly GameObject m_GameObject;
            readonly LoadingScreenOverlayBehaviour m_Behaviour;

            public LoadingScreenOverlay()
            {
                m_GameObject = new GameObject("SensorFlexLoadingOverlay")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                UnityEngine.Object.DontDestroyOnLoad(m_GameObject);
                m_Behaviour = m_GameObject.AddComponent<LoadingScreenOverlayBehaviour>();
            }

            public void Show(string message)
            {
                if (m_Behaviour == null) return;
                m_Behaviour.Message = message;
                m_Behaviour.Visible = true;
            }

            public void Hide()
            {
                if (m_Behaviour != null)
                    m_Behaviour.Visible = false;
            }

            public void Dispose()
            {
                if (m_GameObject != null)
                    UnityEngine.Object.Destroy(m_GameObject);
            }
        }

        sealed class LoadingScreenOverlayBehaviour : MonoBehaviour
        {
            GUIStyle m_LabelStyle;
            GUIStyle m_BackgroundStyle;

            public bool Visible { get; set; }
            public string Message { get; set; } = "Loading...";

            void OnGUI()
            {
                if (!Visible) return;

                EnsureStyles();

                var fullRect = new Rect(0f, 0f, Screen.width, Screen.height);
                GUI.Box(fullRect, GUIContent.none, m_BackgroundStyle);

                var labelRect = new Rect(0f, 0f, Screen.width, Screen.height);
                GUI.Label(labelRect, Message, m_LabelStyle);
            }

            void EnsureStyles()
            {
                if (m_LabelStyle != null && m_BackgroundStyle != null) return;

                m_LabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = Mathf.Max(24, Screen.height / 24),
                    wordWrap = true,
                    richText = false
                };
                m_LabelStyle.normal.textColor = Color.white;

                var background = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
                background.Apply();

                m_BackgroundStyle = new GUIStyle(GUI.skin.box);
                m_BackgroundStyle.normal.background = background;
                m_BackgroundStyle.border = new RectOffset(0, 0, 0, 0);
            }

            void OnDestroy()
            {
                if (m_BackgroundStyle?.normal.background != null)
                    UnityEngine.Object.Destroy(m_BackgroundStyle.normal.background);
            }
        }
    }
}
