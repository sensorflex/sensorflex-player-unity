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

            public static event Action OnFramesReady;

            internal static FrameLoader ActiveLoader { get; private set; }
            internal static long LatestTimestampNs { get; private set; }
            internal static Vector4 LatestIntrinsics { get; private set; } = new(935.3f, 935.3f, 960f, 720f);
            internal static Vector2Int LatestTextureDimensions { get; private set; } = new(1920, 1440);

            double nextFrameTime;
            double frameInterval = 1.0 / 30.0;
            int index = 0;
            long timestampNs = 0;

            const bool EnableProgrammaticLoadingOverlay = true;
            bool showLoadingScreen = true;
            int FramesLoaded = 0;
            int FramesToWait;
            LoadingScreenOverlay m_LoadingOverlay;
            StartupStage m_StartupStage;
            ScannedSceneMeshLoadOperation m_ScannedSceneMeshLoadOperation;

            ARSensorFlexSession session;
            int maxFramesToLoad;
            FrameLoader m_Loader;

            Texture2D m_CurrentTexture;
            Material m_CameraMaterial;
            Vector4 m_CurrentIntrinsics = new Vector4(935.3f, 935.3f, 960f, 720f);
            XRCameraConfiguration m_CurrentConfiguration;
            bool m_LoggedFirstTryGetFrame;
            bool m_LoggedPreloadComplete;
            bool m_LoggedFirstTextureSet;
            bool m_LoggedEmptyTextureDescriptors;
            bool m_LoggedNonEmptyTextureDescriptors;
            bool m_LoggedWaitingForSession;
            bool m_StepForwardPending;

            public override Material cameraMaterial
            {
                get
                {
                    if (m_CameraMaterial == null)
                        CreateCameraMaterial();

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
                Shader shader = Shader.Find("SensorFlex/CameraBackground")
                    ?? Shader.Find("Unlit/Texture")
                    ?? Shader.Find("UI/Default");

                if (shader != null)
                {
                    m_CameraMaterial = new Material(shader)
                    {
                        name = "CustomARCameraMaterial",
                        renderQueue = (int)RenderQueue.Background
                    };
                    Debug.Log("[CustomAR] Created camera material with shader: " + shader.name);
                }
                else
                {
                    Debug.LogError("[CustomAR] Could not find any suitable shader for camera material!");
                }
            }

            public override bool permissionGranted => true;
            public override Feature currentCamera => Feature.WorldFacingCamera;
            public override Feature requestedCamera { get => Feature.WorldFacingCamera; set { } }
            public override bool autoFocusEnabled => false;
            public override bool autoFocusRequested { get => false; set { } }
            public override XRCameraConfiguration? currentConfiguration => m_CurrentConfiguration;

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
                m_LoggedWaitingForSession = false;

                if (EnableProgrammaticLoadingOverlay)
                {
                    m_LoadingOverlay ??= new LoadingScreenOverlay();
                    m_LoadingOverlay.Show("Loading SensorFlex frames...");
                }

                m_CurrentConfiguration = new XRCameraConfiguration(IntPtr.Zero, new Vector2Int(1920, 1440), framerate: 60);
                LatestTimestampNs = 0;
                LatestIntrinsics = m_CurrentIntrinsics;
                LatestTextureDimensions = new Vector2Int(1920, 1440);
                m_StartupStage = StartupStage.WarmingUpFrames;
                nextFrameTime = Time.realtimeSinceStartupAsDouble;

                ControlBridge.Clear();
                SubscribeControlBridge();
            }

            void UpdateFrameIfNeeded()
            {
                if (!EnsureSessionInitialized())
                    return;

                if (m_StartupStage == StartupStage.LoadingSceneMesh)
                {
                    TryCompleteSceneMeshLoad();
                    return;
                }

                if (m_Loader == null)
                    return;

                if (session.SourceMode == ARSensorFlexSession.FrameSourceMode.WebSocket)
                {
                    m_Loader.DispatchWebSocket();
                    m_Loader.DrainUploadQueue();
                }

                if (session.SourceMode == ARSensorFlexSession.FrameSourceMode.Sfz ||
                    session.SourceMode == ARSensorFlexSession.FrameSourceMode.FileIo)
                    m_Loader.DrainUploadQueue();

                // Execute a pending step-forward immediately when paused.
                if (m_StartupStage == StartupStage.Playing && m_StepForwardPending && UsesBufferedPlayback())
                {
                    m_StepForwardPending = false;
                    ExecuteBufferedStep();
                    return;
                }

                // Respect pause during active playback; warmup always proceeds.
                if (m_StartupStage == StartupStage.Playing && !ControlBridge.IsPlaying)
                    return;

                double effectiveInterval = frameInterval / Math.Max(0.05, ControlBridge.PlaybackSpeed);
                if (Time.realtimeSinceStartupAsDouble < nextFrameTime)
                    return;

                nextFrameTime = Time.realtimeSinceStartupAsDouble + effectiveInterval;

                if (UsesBufferedPlayback())
                {
                    UpdateBufferedFrame();
                    return;
                }

                var frames = m_Loader.Frames;
                if (frames == null || frames.Length == 0)
                    return;

                index++;
                if (index >= frames.Length)
                    index = session.LoopSequence ? 0 : frames.Length - 1;

                LoadFrame(index);
                OnFramesReady?.Invoke();
            }

            bool UsesBufferedPlayback()
            {
                return session != null &&
                       (session.SourceMode == ARSensorFlexSession.FrameSourceMode.Sfz ||
                        session.SourceMode == ARSensorFlexSession.FrameSourceMode.FileIo ||
                        session.SourceMode == ARSensorFlexSession.FrameSourceMode.WebSocket);
            }

            bool EnsureSessionInitialized()
            {
                if (session != null)
                    return true;

                session = ARSensorFlexSession.ResolveActiveSession();
                if (session == null)
                {
                    if (!m_LoggedWaitingForSession)
                    {
                        Debug.Log("[SF] Waiting for ARSensorFlexSession to become available.");
                        m_LoggedWaitingForSession = true;
                    }

                    return false;
                }

                m_LoggedWaitingForSession = false;
                session.ApplySessionAlignment();

                maxFramesToLoad = session.PreloadFrameCount;
                FramesToWait = Math.Max(1, maxFramesToLoad / 4);

                ScannedSceneMeshBridge.Clear();
                m_ScannedSceneMeshLoadOperation = ScannedSceneMeshLoadOperation.Start(session);
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
                return true;
            }

            void UpdateBufferedFrame()
            {
                if (m_StartupStage == StartupStage.WarmingUpFrames && showLoadingScreen)
                {
                    FramesLoaded++;
                    if (EnableProgrammaticLoadingOverlay)
                        UpdateLoadingScreenText();

                    if (FramesLoaded >= FramesToWait && m_Loader.IsReady)
                    {
                        if (!m_LoggedPreloadComplete)
                        {
                            Debug.Log(
                                $"[SF] Preload complete ({session.SourceMode}). " +
                                $"FramesLoaded={FramesLoaded} FramesToWait={FramesToWait} " +
                                $"TotalFrames={m_Loader.TotalFrames} PlayHead={m_Loader.PlayHead}");
                            m_LoggedPreloadComplete = true;
                        }

                        showLoadingScreen = false;
                        if (EnableProgrammaticLoadingOverlay)
                            m_LoadingOverlay?.Hide();

                        m_StartupStage = StartupStage.Playing;

                        int firstGlobalFrameIndex = 0;
                        int firstSlot = firstGlobalFrameIndex % m_Loader.BufSize;
                        if (!m_Loader.SlotReady[firstSlot] || m_Loader.SlotGlobalIdx[firstSlot] != firstGlobalFrameIndex)
                            return;

                        m_Loader.PlayHead = firstGlobalFrameIndex;
                        PlayBufferedSlot(firstSlot);
                        OnFramesReady?.Invoke();
                    }

                    return;
                }

                int nextGlobalFrameIndex = m_Loader.PlayHead + 1;
                if (m_Loader.TotalFrames != int.MaxValue && nextGlobalFrameIndex >= m_Loader.TotalFrames)
                {
                    if (session.LoopSequence && m_Loader.TotalFrames > 0)
                        nextGlobalFrameIndex = 0;
                    else
                        return;
                }

                int nextSlot = nextGlobalFrameIndex % m_Loader.BufSize;
                if (!m_Loader.SlotReady[nextSlot] || m_Loader.SlotGlobalIdx[nextSlot] != nextGlobalFrameIndex)
                    return;

                m_Loader.PlayHead = nextGlobalFrameIndex;
                PlayBufferedSlot(nextSlot);
                OnFramesReady?.Invoke();
            }

            void LoadFrame(int frameIndex)
            {
                var frames = m_Loader?.Frames;
                if (frames == null || frameIndex < 0 || frameIndex >= frames.Length)
                    return;

                SetCurrentTexture(frames[frameIndex]);
                timestampNs += (long)(frameInterval * 1_000_000_000L);
                LatestTimestampNs = timestampNs;
            }

            void PlayBufferedSlot(int slot)
            {
                SetCurrentTexture(m_Loader.Frames[slot]);

                timestampNs += (long)(frameInterval * 1_000_000_000L);
                LatestTimestampNs = timestampNs;

                if (m_Loader.Intrinsics != null &&
                    slot >= 0 &&
                    slot < m_Loader.Intrinsics.Length &&
                    m_Loader.Intrinsics[slot] != Vector4.zero)
                {
                    m_CurrentIntrinsics = m_Loader.Intrinsics[slot];
                    LatestIntrinsics = m_CurrentIntrinsics;
                }

                if (m_Loader.Poses != null &&
                    slot >= 0 &&
                    slot < m_Loader.Poses.Length &&
                    m_Loader.Poses[slot] != Matrix4x4.zero)
                {
                    PoseBridge.SetUnityPose(
                        ArchiveIOUtils.ConvertToUnityPose(
                            m_Loader.Poses[slot],
                            m_Loader.CoordConvMatrix,
                            m_Loader.UseNegativeZForwardOpticalAxis));
                }
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

                session = ARSensorFlexSession.ResolveActiveSession();
                if (session == null)
                {
                    Debug.LogError("[SF] Cannot begin frame warmup because no active ARSensorFlexSession is available.");
                    showLoadingScreen = false;
                    if (EnableProgrammaticLoadingOverlay)
                        m_LoadingOverlay?.Hide();
                    m_StartupStage = StartupStage.Playing;
                    return;
                }

                m_Loader = new FrameLoader();
                m_Loader.Start(session, maxFramesToLoad, FramesToWait);
                ActiveLoader = m_Loader;

                frameInterval = UsesBufferedPlayback()
                    ? m_Loader.FrameInterval
                    : 1.0 / Math.Max(1, session.TargetFPS);

                Debug.Log($"[SF] Mode={session.SourceMode} FramesToWait={FramesToWait} BufferSize={maxFramesToLoad} FrameInterval={frameInterval:F4}s");
                if (EnableProgrammaticLoadingOverlay)
                    UpdateLoadingScreenText();
            }

            void SetCurrentTexture(Texture2D texture)
            {
                m_CurrentTexture = texture;
                if (m_CurrentTexture != null)
                    LatestTextureDimensions = new Vector2Int(m_CurrentTexture.width, m_CurrentTexture.height);

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
                if (m_LoadingOverlay == null || session == null)
                    return;

                string source = session.SourceMode.ToString();
                int progress = Math.Min(FramesLoaded, FramesToWait);

                if (m_StartupStage == StartupStage.LoadingSceneMesh)
                {
                    m_LoadingOverlay.Show($"Loading scanned mesh...\nSource: {source}");
                    return;
                }

                m_LoadingOverlay.Show($"Loading SensorFlex frames...\nSource: {source}\nWarmup: {progress}/{FramesToWait}");
            }

            public override bool TryGetFrame(XRCameraParams cameraParams, out XRCameraFrame frame)
            {
                if (!m_LoggedFirstTryGetFrame)
                {
                    Debug.Log($"[SF] TryGetFrame started. Screen={cameraParams.screenWidth}x{cameraParams.screenHeight} Loading={showLoadingScreen} Ready={m_Loader?.IsReady ?? false}");
                    m_LoggedFirstTryGetFrame = true;
                }

                UpdateFrameIfNeeded();

                ARSensorFlexSession.GetPreferredClipPlanes(out float nearClipPlane, out float farClipPlane);
                int projectionWidth = m_CurrentTexture != null ? m_CurrentTexture.width : 1920;
                int projectionHeight = m_CurrentTexture != null ? m_CurrentTexture.height : 1440;
                var projectionMatrix = ArchiveIOUtils.ComputeProjectionMatrix(
                    m_CurrentIntrinsics,
                    projectionWidth,
                    projectionHeight,
                    nearClipPlane,
                    farClipPlane);

                frame = new XRCameraFrame(
                    timestampNs, 0f, 0f, Color.black,
                    projectionMatrix,
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
                    m_CurrentTexture.width,
                    m_CurrentTexture.height,
                    m_CurrentTexture.mipmapCount,
                    m_CurrentTexture.format,
                    Shader.PropertyToID("_MainTex"),
                    0,
                    XRTextureType.Texture2D);

                if (!m_LoggedNonEmptyTextureDescriptors)
                {
                    Debug.Log($"[SF] GetTextureDescriptors publishing {descriptors[0].width}x{descriptors[0].height} texture.");
                    m_LoggedNonEmptyTextureDescriptors = true;
                }

                return descriptors;
            }

            public override bool TryGetIntrinsics(out XRCameraIntrinsics cameraIntrinsics)
            {
                LatestIntrinsics = m_CurrentIntrinsics;
                cameraIntrinsics = new XRCameraIntrinsics(
                    new Vector2(m_CurrentIntrinsics.x, m_CurrentIntrinsics.y),
                    new Vector2(m_CurrentIntrinsics.z, m_CurrentIntrinsics.w),
                    new Vector2Int(1920, 1440));
                return true;
            }

            void SubscribeControlBridge()
            {
                ControlBridge.OnStepForward      += HandleStepForward;
                ControlBridge.OnPlayStateChanged += HandlePlayStateChanged;
            }

            void UnsubscribeControlBridge()
            {
                ControlBridge.OnStepForward      -= HandleStepForward;
                ControlBridge.OnPlayStateChanged -= HandlePlayStateChanged;
            }

            void HandleStepForward() => m_StepForwardPending = true;

            void HandlePlayStateChanged(bool isPlaying)
            {
                // Reset the timer on unpause so there is no burst of catch-up frames.
                if (isPlaying)
                    nextFrameTime = Time.realtimeSinceStartupAsDouble;
            }

            // Advance playhead by one frame without waiting for the frame timer.
            // Only called when paused, in response to ControlBridge.OnStepForward.
            void ExecuteBufferedStep()
            {
                int next = m_Loader.PlayHead + 1;
                if (m_Loader.TotalFrames != int.MaxValue && next >= m_Loader.TotalFrames)
                    return;

                int slot = next % m_Loader.BufSize;
                if (!m_Loader.SlotReady[slot] || m_Loader.SlotGlobalIdx[slot] != next)
                    return;

                m_Loader.PlayHead = next;
                PlayBufferedSlot(slot);
                OnFramesReady?.Invoke();
            }

            public override async void Stop()
            {
                UnsubscribeControlBridge();
                ControlBridge.Clear();

                ActiveLoader = null;
                m_ScannedSceneMeshLoadOperation = null;
                session = null;

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
                if (m_Behaviour == null)
                    return;

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
                if (!Visible)
                    return;

                EnsureStyles();

                var fullRect = new Rect(0f, 0f, Screen.width, Screen.height);
                GUI.Box(fullRect, GUIContent.none, m_BackgroundStyle);

                var labelRect = new Rect(0f, 0f, Screen.width, Screen.height);
                GUI.Label(labelRect, Message, m_LabelStyle);
            }

            void EnsureStyles()
            {
                if (m_LabelStyle != null && m_BackgroundStyle != null)
                    return;

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