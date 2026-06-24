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
                WarmingUpFrames,
                Playing
            }

            public static event Action OnFramesReady;

            double nextFrameTime;
            double frameInterval = 1.0 / 30.0;
            long timestampNs = 0;

            const bool EnableProgrammaticLoadingOverlay = true;
            // If live lag exceeds this, snap to latest rather than catching up sequentially.
            // Set to ~0.5 s of source content (30 frames at 60 fps).
            const int k_LiveSnapLag = 30;

            bool showLoadingScreen = true;
            int FramesLoaded = 0;
            int FramesToWait;
            LoadingScreenOverlay m_LoadingOverlay;
            StartupStage m_StartupStage;

            ARSensorFlexSession session;
            int maxFramesToLoad;

            // Live mode is temporarily disabled — always false until ARSensorFlexLiveSession is introduced.
            bool IsLiveMode => false;

            Texture m_CurrentTexture;
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
            double m_LiveLastAdvanceTime;

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
                    m_CameraMaterial.SetInt("_DepthVizMode", ControlBridge.DepthVisualizationEnabled ? 1 : 0);
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
                SfzSessionStore.Clear();
                m_StartupStage = StartupStage.WarmingUpFrames;
                nextFrameTime = Time.realtimeSinceStartupAsDouble;

                ControlBridge.Clear();
                SubscribeControlBridge();
            }

            void UpdateFrameIfNeeded()
            {
                if (!EnsureSessionInitialized())
                    return;

                if (!SfzSessionStore.IsActive)
                    return;

                SfzSessionStore.Tick();

                // Step-forward only applies in replay mode.
                if (!IsLiveMode && m_StartupStage == StartupStage.Playing && m_StepForwardPending)
                {
                    m_StepForwardPending = false;
                    ExecuteBufferedStep();
                    return;
                }

                // Pause applies to all modes: freeze PlayHead, keep receiving frames.
                if (m_StartupStage == StartupStage.Playing && !ControlBridge.IsPlaying)
                    return;

                if (IsLiveMode || (SfzSessionStore.IsVideoMode && m_StartupStage == StartupStage.Playing))
                {
                    // Live mode and video SFZ have their own clocks; run every AR update.
                    UpdateBufferedFrame();
                }
                else
                {
                    double effectiveInterval = frameInterval / Math.Max(0.05, ControlBridge.PlaybackSpeed);
                    if (Time.realtimeSinceStartupAsDouble < nextFrameTime)
                        return;
                    nextFrameTime = Time.realtimeSinceStartupAsDouble + effectiveInterval;
                    UpdateBufferedFrame();
                }
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

                BeginFrameWarmup();

                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;
                return true;
            }

            void UpdateBufferedFrame()
            {
                if (m_StartupStage == StartupStage.WarmingUpFrames)
                {
                    if (IsLiveMode)
                    {
                        if (EnableProgrammaticLoadingOverlay)
                            UpdateLoadingScreenText();

                        // Live: wait for PreloadFrameCount frames before starting.
                        if (!SfzSessionStore.IsReady)
                            return;

                        if (!m_LoggedPreloadComplete)
                        {
                            Debug.Log($"[SF] Live preload complete. LatestSeq={SfzSessionStore.LatestGlobalIndex} Preloaded={FramesToWait}");
                            m_LoggedPreloadComplete = true;
                        }

                        showLoadingScreen = false;
                        if (EnableProgrammaticLoadingOverlay)
                            m_LoadingOverlay?.Hide();

                        m_StartupStage = StartupStage.Playing;
                        if (session?.AutoPlay == false)
                            ControlBridge.Pause();
                        m_LiveLastAdvanceTime = Time.realtimeSinceStartupAsDouble;

                        // Start from the latest buffered frame — the preload just ensures
                        // the ring buffer is warm so there are no stalls at startup.
                        int latest = SfzSessionStore.LatestGlobalIndex;
                        if (latest >= 0)
                        {
                            int startSlot = latest % SfzSessionStore.BufSize;
                            if (SfzSessionStore.SlotReady[startSlot] && SfzSessionStore.SlotGlobalIdx[startSlot] == latest)
                            {
                                SfzSessionStore.PlayHead = latest;
                                PlayBufferedSlot(startSlot);
                                OnFramesReady?.Invoke();
                            }
                        }
                        return;
                    }

                    if (SfzSessionStore.IsVideoMode)
                    {
                        // Video SFZ: no frame preloading. Hide the loading screen immediately
                        // and wait silently for the VideoPlayers to finish Prepare().
                        if (showLoadingScreen)
                        {
                            showLoadingScreen = false;
                            if (EnableProgrammaticLoadingOverlay)
                                m_LoadingOverlay?.Hide();
                        }

                        if (!SfzSessionStore.IsReady)
                            return;
                    }
                    else
                    {
                        // Non-video (ZIP or FileIo): preload frames behind a loading screen.
                        if (EnableProgrammaticLoadingOverlay)
                            UpdateLoadingScreenText();

                        FramesLoaded++;

                        if (FramesLoaded < FramesToWait || !SfzSessionStore.IsReady)
                            return;

                        showLoadingScreen = false;
                        if (EnableProgrammaticLoadingOverlay)
                            m_LoadingOverlay?.Hide();
                    }

                    // ── Warmup complete — transition to Playing ──────────────────
                    if (!m_LoggedPreloadComplete)
                    {
                        Debug.Log(
                            $"[SF] Preload complete ({session.SourceMode}). " +
                            $"FramesLoaded={FramesLoaded} FramesToWait={FramesToWait} " +
                            $"TotalFrames={SfzSessionStore.TotalFrames} PlayHead={SfzSessionStore.PlayHead}");
                        m_LoggedPreloadComplete = true;
                    }

                    m_StartupStage = StartupStage.Playing;
                    Debug.Log($"[SF] Warmup → Playing. AutoPlay={session?.AutoPlay} IsVideoMode={SfzSessionStore.IsVideoMode}");
                    if (session?.AutoPlay == false)
                        ControlBridge.Pause();

                    if (SfzSessionStore.IsVideoMode)
                    {
                        // Wire up the per-frame callback — drives PlayHead independently of TryGetFrame.
                        SfzSessionStore.SetVideoFrameReadyCallback(HandleVideoFrameReady);

                        SfzSessionStore.SetVideoLooping(session?.LoopSequence ?? true);
                        SfzSessionStore.SetVideoPlaybackSpeed(ControlBridge.PlaybackSpeed);

                        // Seek to frame 0 before playing — the VideoPlayer may have drifted
                        // from frame 0 during the prepare phase due to platform auto-play.
                        SfzSessionStore.SeekVideoFrame(0);

                        if (session?.AutoPlay != false)
                            SfzSessionStore.PlayVideo();

                        // Show frame 0 as the initial still.
                        SfzSessionStore.PlayHead = 0;
                        SetCurrentTexture(SfzSessionStore.VideoRgbTexture);
                        OnFramesReady?.Invoke();
                        return;
                    }

                    int firstGlobalFrameIndex = 0;
                    int firstSlot = firstGlobalFrameIndex % SfzSessionStore.BufSize;
                    if (!SfzSessionStore.SlotReady[firstSlot] || SfzSessionStore.SlotGlobalIdx[firstSlot] != firstGlobalFrameIndex)
                        return;

                    SfzSessionStore.PlayHead = firstGlobalFrameIndex;
                    PlayBufferedSlot(firstSlot);
                    OnFramesReady?.Invoke();
                    return;
                }

                // ── Playing stage ────────────────────────────────────────────────────

                if (IsLiveMode)
                {
                    int latest = SfzSessionStore.LatestGlobalIndex;
                    if (latest < 0) return;

                    int lag = latest - SfzSessionStore.PlayHead;
                    if (lag == 0) return; // at live edge, waiting for next frame

                    if (lag > k_LiveSnapLag)
                    {
                        // Too far behind (paused too long or display rate << server rate).
                        // Snap to latest and reset the advance clock.
                        Debug.LogWarning($"[SF] Live lag={lag} exceeded snap threshold — jumping to latest.");
                        int snapSlot = latest % SfzSessionStore.BufSize;
                        if (SfzSessionStore.SlotReady[snapSlot] && SfzSessionStore.SlotGlobalIdx[snapSlot] == latest)
                        {
                            SfzSessionStore.PlayHead = latest;
                            m_LiveLastAdvanceTime = Time.realtimeSinceStartupAsDouble;
                            PlayBufferedSlot(snapSlot);
                            OnFramesReady?.Invoke();
                        }
                        return;
                    }

                    // Time-proportional advance: consume as many frames as real time dictates so
                    // playback rate matches server rate regardless of display frame rate.
                    double now = Time.realtimeSinceStartupAsDouble;
                    int stepsElapsed   = (int)Math.Floor((now - m_LiveLastAdvanceTime) / frameInterval);
                    int stepsToAdvance = Math.Min(stepsElapsed, lag);

                    if (stepsToAdvance <= 0) return;

                    m_LiveLastAdvanceTime += stepsToAdvance * frameInterval;

                    int lastPlayedSlot = -1;
                    for (int i = 0; i < stepsToAdvance; i++)
                    {
                        int nextSeqNum   = SfzSessionStore.PlayHead + 1;
                        int liveNextSlot = nextSeqNum % SfzSessionStore.BufSize;
                        if (!SfzSessionStore.SlotReady[liveNextSlot] || SfzSessionStore.SlotGlobalIdx[liveNextSlot] != nextSeqNum)
                            break; // frame not decoded yet — stop here
                        SfzSessionStore.PlayHead = nextSeqNum;
                        lastPlayedSlot    = liveNextSlot;
                    }

                    if (lastPlayedSlot >= 0)
                    {
                        PlayBufferedSlot(lastPlayedSlot);
                        OnFramesReady?.Invoke();
                    }
                    return;
                }

                // Video SFZ: frame updates are driven by HandleVideoFrameReady (frameReady event),
                // not by this polling path.
                if (SfzSessionStore.IsVideoMode)
                    return;

                // Replay: advance one frame at a time
                int nextGlobalFrameIndex = SfzSessionStore.PlayHead + 1;
                if (SfzSessionStore.TotalFrames != int.MaxValue && nextGlobalFrameIndex >= SfzSessionStore.TotalFrames)
                {
                    if (session.LoopSequence && SfzSessionStore.TotalFrames > 0)
                        nextGlobalFrameIndex = 0;
                    else
                        return;
                }

                int nextSlot = nextGlobalFrameIndex % SfzSessionStore.BufSize;
                if (!SfzSessionStore.SlotReady[nextSlot] || SfzSessionStore.SlotGlobalIdx[nextSlot] != nextGlobalFrameIndex)
                    return;

                SfzSessionStore.PlayHead = nextGlobalFrameIndex;
                PlayBufferedSlot(nextSlot);
                OnFramesReady?.Invoke();
            }

            void PlayBufferedSlot(int slot)
            {
                if (SfzSessionStore.IsVideoMode)
                {
                    // Don't seek — the VideoPlayer is playing naturally. Just bind the RT
                    // and look up metadata for the current frame index (slot == frame index).
                    SetCurrentTexture(SfzSessionStore.VideoRgbTexture);
                    // v2.0: decode the LZ4 depth block for this frame (no-op for v1.1).
                    SfzSessionStore.UpdateVideoDepthForFrame(slot);
                }
                else
                {
                    SetCurrentTexture(SfzSessionStore.Frames[slot]);
                }

                timestampNs += (long)(frameInterval * 1_000_000_000L);
                SfzSessionStore.LatestTimestampNs = timestampNs;

                if (SfzSessionStore.Intrinsics != null &&
                    slot >= 0 &&
                    slot < SfzSessionStore.Intrinsics.Length &&
                    SfzSessionStore.Intrinsics[slot] != Vector4.zero)
                {
                    m_CurrentIntrinsics = SfzSessionStore.Intrinsics[slot];
                    SfzSessionStore.LatestIntrinsics = m_CurrentIntrinsics;
                }

                if (SfzSessionStore.Poses != null &&
                    slot >= 0 &&
                    slot < SfzSessionStore.Poses.Length &&
                    SfzSessionStore.Poses[slot] != Matrix4x4.zero)
                {
                    PoseBridge.SetUnityPose(
                        MathUtils.ConvertToUnityPose(
                            SfzSessionStore.Poses[slot],
                            SfzSessionStore.CoordConvMatrix,
                            SfzSessionStore.UseNegativeZForwardOpticalAxis));
                }
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

                SfzSessionStore.StartSession(session, maxFramesToLoad, FramesToWait);
                frameInterval = SfzSessionStore.FrameInterval;

                Debug.Log($"[SF] Mode={session.SourceMode} FramesToWait={FramesToWait} BufferSize={maxFramesToLoad} FrameInterval={frameInterval:F4}s");
                if (EnableProgrammaticLoadingOverlay)
                    UpdateLoadingScreenText();
            }

            void SetCurrentTexture(Texture texture)
            {
                m_CurrentTexture = texture;
                if (m_CurrentTexture != null)
                    SfzSessionStore.LatestTextureDimensions = new Vector2Int(m_CurrentTexture.width, m_CurrentTexture.height);

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

                if (IsLiveMode)
                {
                    int buffered = SfzSessionStore.IsActive ? Math.Max(0, SfzSessionStore.LatestGlobalIndex + 1) : 0;
                    string msg = ControlBridge.ConnectionState switch
                    {
                        LiveConnectionState.Connecting => "Live Mode\nConnecting...",
                        LiveConnectionState.Live => $"Live Mode\nPreloading... ({buffered}/{FramesToWait})",
                        _ => "Live Mode\nDisconnected"
                    };
                    m_LoadingOverlay.Show(msg);
                    return;
                }

                string source = session.SourceMode.ToString();

                if (SfzSessionStore.IsLoadingAttachments)
                {
                    m_LoadingOverlay.Show($"Loading attachment: scene mesh\nSource: {source}");
                    return;
                }

                int progress = Math.Min(FramesLoaded, FramesToWait);
                m_LoadingOverlay.Show($"Loading SensorFlex frames...\nSource: {source}\nWarmup: {progress}/{FramesToWait}");
            }

            public override bool TryGetFrame(XRCameraParams cameraParams, out XRCameraFrame frame)
            {
                if (!m_LoggedFirstTryGetFrame)
                {
                    Debug.Log($"[SF] TryGetFrame started. Screen={cameraParams.screenWidth}x{cameraParams.screenHeight} Loading={showLoadingScreen} Ready={SfzSessionStore.IsReady}");
                    m_LoggedFirstTryGetFrame = true;
                }

                UpdateFrameIfNeeded();

                ARSensorFlexSession.GetPreferredClipPlanes(out float nearClipPlane, out float farClipPlane);
                int projectionWidth = m_CurrentTexture != null ? m_CurrentTexture.width : 1920;
                int projectionHeight = m_CurrentTexture != null ? m_CurrentTexture.height : 1440;
                var projectionMatrix = MathUtils.ComputeProjectionMatrix(
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
                        Debug.Log($"[SF] GetTextureDescriptors returned empty. Loading={showLoadingScreen} Ready={SfzSessionStore.IsReady}");
                        m_LoggedEmptyTextureDescriptors = true;
                    }

                    return new NativeArray<XRTextureDescriptor>(0, allocator);
                }

                var descriptors = new NativeArray<XRTextureDescriptor>(1, allocator);
                var texFormat = m_CurrentTexture is Texture2D t2d ? t2d.format : TextureFormat.RGBA32;
                descriptors[0] = new XRTextureDescriptor(
                    m_CurrentTexture.GetNativeTexturePtr(),
                    m_CurrentTexture.width,
                    m_CurrentTexture.height,
                    m_CurrentTexture.mipmapCount,
                    texFormat,
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
                SfzSessionStore.LatestIntrinsics = m_CurrentIntrinsics;
                cameraIntrinsics = new XRCameraIntrinsics(
                    new Vector2(m_CurrentIntrinsics.x, m_CurrentIntrinsics.y),
                    new Vector2(m_CurrentIntrinsics.z, m_CurrentIntrinsics.w),
                    new Vector2Int(1920, 1440));
                return true;
            }

            void SubscribeControlBridge()
            {
                ControlBridge.OnStepForward               += HandleStepForward;
                ControlBridge.OnPlayStateChanged          += HandlePlayStateChanged;
                ControlBridge.OnSpeedChanged              += HandleSpeedChanged;
                ControlBridge.OnRestart                   += HandleRestart;
                ControlBridge.OnDepthVisualizationChanged += HandleDepthVisualizationChanged;
            }

            void UnsubscribeControlBridge()
            {
                ControlBridge.OnStepForward               -= HandleStepForward;
                ControlBridge.OnPlayStateChanged          -= HandlePlayStateChanged;
                ControlBridge.OnSpeedChanged              -= HandleSpeedChanged;
                ControlBridge.OnRestart                   -= HandleRestart;
                ControlBridge.OnDepthVisualizationChanged -= HandleDepthVisualizationChanged;
            }

            void HandleStepForward() => m_StepForwardPending = true;

            void HandleDepthVisualizationChanged(bool enabled)
            {
                m_CameraMaterial?.SetInt("_DepthVizMode", enabled ? 1 : 0);
            }

            void HandlePlayStateChanged(bool isPlaying)
            {
                if (SfzSessionStore.IsVideoMode)
                {
                    if (isPlaying) SfzSessionStore.PlayVideo();
                    else           SfzSessionStore.PauseVideo();
                }

                if (isPlaying)
                {
                    // Reset timers on unpause to avoid burst of catch-up frames.
                    nextFrameTime = Time.realtimeSinceStartupAsDouble;
                    if (IsLiveMode)
                        m_LiveLastAdvanceTime = Time.realtimeSinceStartupAsDouble;
                }
            }

            void HandleSpeedChanged(float speed)
            {
                if (SfzSessionStore.IsVideoMode)
                    SfzSessionStore.SetVideoPlaybackSpeed(speed);
            }

            // Called on the Unity main thread by VideoPlayer.frameReady — once per rendered frame.
            void HandleVideoFrameReady(int frameIdx)
            {
                if (m_StartupStage != StartupStage.Playing) return;
                if (!ControlBridge.IsPlaying) return;
                if (frameIdx < 0 || frameIdx >= SfzSessionStore.TotalFrames)
                {
                    Debug.LogWarning($"[SF] HandleVideoFrameReady: frameIdx={frameIdx} out of range [0,{SfzSessionStore.TotalFrames})");
                    return;
                }
                if (frameIdx == SfzSessionStore.PlayHead) return;

                SfzSessionStore.PlayHead = frameIdx;
                PlayBufferedSlot(frameIdx);
                OnFramesReady?.Invoke();
            }

            async void HandleRestart()
            {
                m_StepForwardPending = false;
                await SfzSessionStore.StopSessionAsync();
                ScannedSceneMeshBridge.Clear();

                BeginFrameWarmup();
            }

            // Advance playhead by one frame without waiting for the frame timer.
            // Only called when paused, in response to ControlBridge.OnStepForward.
            void ExecuteBufferedStep()
            {
                if (SfzSessionStore.IsVideoMode)
                {
                    int next = SfzSessionStore.PlayHead + 1;
                    if (SfzSessionStore.TotalFrames != int.MaxValue && next >= SfzSessionStore.TotalFrames)
                        return;
                    SfzSessionStore.SeekVideoFrame(next);
                    SfzSessionStore.PlayHead = next;
                    PlayBufferedSlot(next);
                    OnFramesReady?.Invoke();
                    return;
                }

                int nextSlot = SfzSessionStore.PlayHead + 1;
                if (SfzSessionStore.TotalFrames != int.MaxValue && nextSlot >= SfzSessionStore.TotalFrames)
                    return;

                int slot = nextSlot % SfzSessionStore.BufSize;
                if (!SfzSessionStore.SlotReady[slot] || SfzSessionStore.SlotGlobalIdx[slot] != nextSlot)
                    return;

                SfzSessionStore.PlayHead = nextSlot;
                PlayBufferedSlot(slot);
                OnFramesReady?.Invoke();
            }

            public override async void Stop()
            {
                SfzSessionStore.SetVideoFrameReadyCallback(null);
                UnsubscribeControlBridge();
                ControlBridge.Clear();

                await SfzSessionStore.StopSessionAsync();
                session = null;

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
