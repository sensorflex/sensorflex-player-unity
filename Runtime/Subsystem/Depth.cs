using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Player.Subsystem
{
    /// <summary>
    /// XROcclusionSubsystem provider that serves environment depth images from
    /// a local folder in sync with the camera subsystem's frame sequence.
    ///
    /// Depth images are expected as PNG or JPG files. The R channel of each
    /// pixel is interpreted as normalized linear depth (0 = near, 1 = far).
    /// For true metric float depth, supply EXR files and extend LoadDepthFrame
    /// to use ImageConversion / a custom loader.
    ///
    /// FileSystem mode: place depth images in StreamingAssets/<depthFolder>/
    ///   named in the same sorted order as the paired colour frames.
    /// WebSocket mode: not yet supported; depth is silently disabled.
    /// </summary>
    public sealed class OcclusionSubsystem : XROcclusionSubsystem
    {
        const string SubsystemId = "SensorFlex-Occlusion";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterDescriptor()
        {
            XROcclusionSubsystemDescriptor.Register(new XROcclusionSubsystemDescriptor.Cinfo
            {
                id = SubsystemId,
                providerType = typeof(DepthDataProvider),
                subsystemTypeOverride = typeof(OcclusionSubsystem),
                // Human segmentation not supported — omit delegates (null → Unsupported)
                humanSegmentationStencilImageSupportedDelegate = null,
                humanSegmentationDepthImageSupportedDelegate   = null,
                // Environment depth is supported
                environmentDepthImageSupportedDelegate             = () => Supported.Supported,
                environmentDepthConfidenceImageSupportedDelegate   = null,
                environmentDepthTemporalSmoothingSupportedDelegate = null,
            });
            Debug.Log("[SF] Occlusion Subsystem registered.");
        }

        class DepthDataProvider : Provider
        {
            const int RawDepthWidth = 256;
            const int RawDepthHeight = 192;
            static readonly int EnvironmentDepthPropertyId = Shader.PropertyToID("_EnvironmentDepth");

            ARSensorFlexSession session;
            Texture2D[] preloadedDepthFrames;
            Texture2D m_CurrentDepthTexture;
            int index;
            bool framesReady;
            bool m_HasBoundSession;
            bool m_LoggedWaitingForSession;
            bool m_LoggedNormalizedDepthScaleWarning;
            float m_CurrentDepthWorldScale = 1f;
            bool m_IsSfzMode;
            Texture2D m_SfzDepthTexture;
            int m_LastSfzPlayHead = -1;

            // ----------------------------------------------------------------
            // Environment depth mode
            // ----------------------------------------------------------------
            EnvironmentDepthMode m_RequestedDepthMode = EnvironmentDepthMode.Fastest;

            public override EnvironmentDepthMode requestedEnvironmentDepthMode
            {
                get => m_RequestedDepthMode;
                set => m_RequestedDepthMode = value;
            }

            public override EnvironmentDepthMode currentEnvironmentDepthMode
                => framesReady ? m_RequestedDepthMode : EnvironmentDepthMode.Disabled;

            // ----------------------------------------------------------------
            // Occlusion preference
            // ----------------------------------------------------------------
            OcclusionPreferenceMode m_RequestedOcclusionMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;

            public override OcclusionPreferenceMode requestedOcclusionPreferenceMode
            {
                get => m_RequestedOcclusionMode;
                set => m_RequestedOcclusionMode = value;
            }

            public override OcclusionPreferenceMode currentOcclusionPreferenceMode
                => m_RequestedOcclusionMode;

            // ----------------------------------------------------------------
            // Lifecycle
            // ----------------------------------------------------------------
            public override void Start()
            {
                CameraSubsystem.CameraDataProvider.OnFramesReady += AdvanceFrame;
                m_HasBoundSession = false;
                m_LoggedWaitingForSession = false;
            }

            public override void Stop()
            {
                CameraSubsystem.CameraDataProvider.OnFramesReady -= AdvanceFrame;
                ReleaseDepthFrames();
                session = null;
                m_HasBoundSession = false;
            }

            public override void Destroy() { }

            // ----------------------------------------------------------------
            // GPU texture access (called by AROcclusionManager each frame)
            // ----------------------------------------------------------------
            public override bool TryGetEnvironmentDepth(out XRTextureDescriptor environmentDepthDescriptor)
            {
                EnsureSessionInitialized();
                return TryBuildCurrentDescriptor(out environmentDepthDescriptor);
            }

            public override NativeArray<XRTextureDescriptor> GetTextureDescriptors(
                XRTextureDescriptor defaultDescriptor, Allocator allocator)
            {
                EnsureSessionInitialized();

                if (!TryBuildCurrentDescriptor(out var descriptor))
                    return new NativeArray<XRTextureDescriptor>(0, allocator);

                var descriptors = new NativeArray<XRTextureDescriptor>(1, allocator);
                descriptors[0] = descriptor;
                return descriptors;
            }

            public override XRResultStatus TryGetFrame(Allocator allocator, out XROcclusionFrame frame)
            {
                EnsureSessionInitialized();

                if (!framesReady || m_CurrentDepthTexture == null)
                {
                    frame = default;
                    return new XRResultStatus(XRResultStatus.StatusCode.ProviderNotStarted);
                }

                ARSensorFlexSession.GetPreferredClipPlanes(out float nearClipPlane, out float farClipPlane);

                var poses = new NativeArray<Pose>(1, allocator);
                poses[0] = PoseBridge.HasPose ? PoseBridge.LatestPose : Pose.identity;

                var textureDims = CameraSubsystem.CameraDataProvider.LatestTextureDimensions;
                var fovs = new NativeArray<XRFov>(1, allocator);
                fovs[0] = BuildFov(CameraSubsystem.CameraDataProvider.LatestIntrinsics, textureDims);

                frame = new XROcclusionFrame(
                    XROcclusionFrameProperties.Timestamp |
                    XROcclusionFrameProperties.NearFarPlanes |
                    XROcclusionFrameProperties.Poses |
                    XROcclusionFrameProperties.Fovs,
                    CameraSubsystem.CameraDataProvider.LatestTimestampNs,
                    new XRNearFarPlanes(nearClipPlane, farClipPlane),
                    poses,
                    fovs);

                return XRResultStatus.unqualifiedSuccess;
            }

            // ----------------------------------------------------------------
            // Helpers
            // ----------------------------------------------------------------
            void EnsureSessionInitialized()
            {
                if (m_HasBoundSession)
                {
                    if (m_IsSfzMode)
                        EnsureSfzLoaderReady();
                    else
                        RefreshScaledDepthIfNeeded();
                    return;
                }

                session = ARSensorFlexSession.ResolveActiveSession();
                if (session == null)
                {
                    if (!m_LoggedWaitingForSession)
                    {
                        Debug.Log("[SF] OcclusionSubsystem: waiting for ARSensorFlexSession to become available.");
                        m_LoggedWaitingForSession = true;
                    }

                    return;
                }

                m_HasBoundSession = true;
                m_LoggedWaitingForSession = false;
                m_CurrentDepthWorldScale = session.EffectiveDepthWorldScale;

                if (!session.DepthEnabled)
                {
                    Debug.Log("[SF] OcclusionSubsystem: depth disabled on the active session.");
                    return;
                }

                if (session.SourceMode == ARSensorFlexSession.FrameSourceMode.Sfz ||
                    session.SourceMode == ARSensorFlexSession.FrameSourceMode.FileIo)
                {
                    m_IsSfzMode = true;
                    Debug.Log("[SF] OcclusionSubsystem: SFZ mode — reading depth from FrameLoader ring buffer.");
                    return;
                }

                if (session.SourceMode != ARSensorFlexSession.FrameSourceMode.FileSystem)
                {
                    Debug.LogWarning("[SF] OcclusionSubsystem: WebSocket depth not yet supported — depth disabled.");
                    return;
                }

                LoadDepthFrames();
            }

            void RefreshScaledDepthIfNeeded()
            {
                if (session == null || !framesReady)
                    return;

                float latestScale = session.EffectiveDepthWorldScale;
                if (Mathf.Approximately(latestScale, m_CurrentDepthWorldScale))
                    return;

                m_CurrentDepthWorldScale = latestScale;
                m_LoggedNormalizedDepthScaleWarning = false;

                ReleaseDepthFrames();
                LoadDepthFrames();
            }

            void LoadDepthFrames()
            {
                string folder = session.DepthFolder;
                if (!Path.IsPathRooted(folder))
                    folder = Path.Combine(Application.streamingAssetsPath, folder);

                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[SF] OcclusionSubsystem: depth folder not found: {folder}");
                    return;
                }

                var files = new List<string>();
                foreach (var file in Directory.GetFiles(folder))
                {
                    if (file.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".bin",  StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                int count = Mathf.Min(session.PreloadFrameCount, files.Count);
                if (count == 0)
                {
                    Debug.LogWarning($"[SF] OcclusionSubsystem: no depth images found in {folder}");
                    return;
                }

                preloadedDepthFrames = new Texture2D[count];
                for (int i = 0; i < count; i++)
                    preloadedDepthFrames[i] = LoadDepthFrame(files[i], m_CurrentDepthWorldScale, ref m_LoggedNormalizedDepthScaleWarning);

                index = 0;
                m_CurrentDepthTexture = preloadedDepthFrames[0];
                framesReady = true;

                Debug.Log($"[SF] OcclusionSubsystem: loaded {count} depth frames from {folder} (worldScale={m_CurrentDepthWorldScale:0.####})");
            }

            void ReleaseDepthFrames()
            {
                if (preloadedDepthFrames != null)
                {
                    foreach (var tex in preloadedDepthFrames)
                    {
                        if (tex != null)
                            UnityEngine.Object.Destroy(tex);
                    }
                    preloadedDepthFrames = null;
                }

                if (m_SfzDepthTexture != null)
                {
                    UnityEngine.Object.Destroy(m_SfzDepthTexture);
                    m_SfzDepthTexture = null;
                }

                m_CurrentDepthTexture = null;
                framesReady = false;
                index = 0;
                m_IsSfzMode = false;
                m_LastSfzPlayHead = -1;
            }

            static Texture2D LoadDepthFrame(string path, float depthWorldScale, ref bool loggedNormalizedDepthScaleWarning)
            {
                if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    return LoadMetricDepthBin(path, depthWorldScale);

                byte[] bytes = File.ReadAllBytes(path);
                // LoadImage always decodes PNG/JPG into RGBA32.
                // The R channel carries normalised depth [0,1].
                if (!Mathf.Approximately(depthWorldScale, 1f) && !loggedNormalizedDepthScaleWarning)
                {
                    Debug.LogWarning(
                        $"[SF] OcclusionSubsystem: world scale is {depthWorldScale:0.####}, " +
                        $"but '{Path.GetFileName(path)}' is normalized PNG/JPG depth. " +
                        "Metric scaling is only applied to raw float depth (.bin).");
                    loggedNormalizedDepthScaleWarning = true;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                tex.Apply();
                return tex;
            }

            static Texture2D LoadMetricDepthBin(string path, float depthWorldScale)
            {
                byte[] bytes = File.ReadAllBytes(path);
                int expectedBytes = RawDepthWidth * RawDepthHeight * sizeof(float);
                if (bytes.Length != expectedBytes)
                {
                    Debug.LogWarning(
                        $"[SF] OcclusionSubsystem: unexpected depth.bin size for '{path}'. " +
                        $"Expected {expectedBytes} bytes, got {bytes.Length}.");
                    return null;
                }

                var depthValues = new float[RawDepthWidth * RawDepthHeight];
                Buffer.BlockCopy(bytes, 0, depthValues, 0, bytes.Length);

                if (!BitConverter.IsLittleEndian)
                {
                    for (int i = 0; i < depthValues.Length; i++)
                    {
                        byte[] valueBytes = BitConverter.GetBytes(depthValues[i]);
                        Array.Reverse(valueBytes);
                        depthValues[i] = BitConverter.ToSingle(valueBytes, 0);
                    }
                }

                if (!Mathf.Approximately(depthWorldScale, 1f))
                {
                    for (int i = 0; i < depthValues.Length; i++)
                    {
                        if (depthValues[i] > 0f)
                            depthValues[i] *= depthWorldScale;
                    }
                }

                var tex = new Texture2D(RawDepthWidth, RawDepthHeight, TextureFormat.RFloat, false);
                tex.SetPixelData(depthValues, 0);
                tex.Apply(false, true);
                return tex;
            }

            bool TryBuildCurrentDescriptor(out XRTextureDescriptor descriptor)
            {
                if (!framesReady || m_CurrentDepthTexture == null)
                {
                    descriptor = default;
                    return false;
                }

                descriptor = new XRTextureDescriptor(
                    m_CurrentDepthTexture.GetNativeTexturePtr(),
                    m_CurrentDepthTexture.width,
                    m_CurrentDepthTexture.height,
                    m_CurrentDepthTexture.mipmapCount,
                    m_CurrentDepthTexture.format,
                    EnvironmentDepthPropertyId,
                    0,
                    XRTextureType.Texture2D
                );

                return true;
            }

            static XRFov BuildFov(Vector4 intrinsics, Vector2Int textureDimensions)
            {
                float fx = intrinsics.x;
                float fy = intrinsics.y;
                float cx = intrinsics.z;
                float cy = intrinsics.w;
                int width = Mathf.Max(1, textureDimensions.x);
                int height = Mathf.Max(1, textureDimensions.y);

                if (fx <= 0f || fy <= 0f)
                {
                    const float fallbackHalfFov = 30f * Mathf.Deg2Rad;
                    return new XRFov(-fallbackHalfFov, fallbackHalfFov, fallbackHalfFov, -fallbackHalfFov);
                }

                float left = -Mathf.Atan(cx / fx);
                float right = Mathf.Atan((width - cx) / fx);
                float up = Mathf.Atan(cy / fy);
                float down = -Mathf.Atan((height - cy) / fy);
                return new XRFov(left, right, up, down);
            }

            void AdvanceFrame()
            {
                if (m_IsSfzMode)
                {
                    UpdateSfzDepthTexture();
                    return;
                }

                if (!framesReady || preloadedDepthFrames == null)
                    return;

                index++;
                if (index >= preloadedDepthFrames.Length)
                    index = session != null && session.LoopSequence ? 0 : preloadedDepthFrames.Length - 1;

                m_CurrentDepthTexture = preloadedDepthFrames[index];
            }

            void EnsureSfzLoaderReady()
            {
                if (framesReady)
                    return;

                var loader = CameraSubsystem.CameraDataProvider.ActiveLoader;
                if (loader == null || !loader.IsReady)
                    return;

                m_SfzDepthTexture = new Texture2D(RawDepthWidth, RawDepthHeight, TextureFormat.RFloat, false);
                framesReady = true;
                Debug.Log("[SF] OcclusionSubsystem: ZIP depth texture allocated.");
            }

            void UpdateSfzDepthTexture()
            {
                EnsureSfzLoaderReady();
                if (!framesReady || m_SfzDepthTexture == null)
                    return;

                var loader = CameraSubsystem.CameraDataProvider.ActiveLoader;
                if (loader?.DepthBins == null)
                    return;

                int playHead = loader.PlayHead;
                if (playHead < 0 || playHead == m_LastSfzPlayHead)
                    return;

                int slot = playHead % loader.BufSize;

                if (loader.SlotReady == null || !loader.SlotReady[slot])
                    return;
                if (loader.SlotGlobalIdx == null || loader.SlotGlobalIdx[slot] != playHead)
                    return;

                byte[] depthBytes = loader.DepthBins[slot];
                if (depthBytes == null)
                {
                    m_CurrentDepthTexture = null;
                    m_LastSfzPlayHead = playHead;
                    return;
                }

                int expectedBytes = RawDepthWidth * RawDepthHeight * sizeof(float);
                if (depthBytes.Length != expectedBytes)
                {
                    Debug.LogWarning(
                        $"[SF] OcclusionSubsystem: ZIP depth slot {slot} wrong size." +
                        $" expected={expectedBytes} actual={depthBytes.Length}");
                    m_CurrentDepthTexture = null;
                    m_LastSfzPlayHead = playHead;
                    return;
                }

                // Fast path: little-endian platform, no world scale — blit raw bytes directly.
                if (BitConverter.IsLittleEndian && Mathf.Approximately(m_CurrentDepthWorldScale, 1f))
                {
                    m_SfzDepthTexture.SetPixelData(depthBytes, 0);
                }
                else
                {
                    var floats = new float[RawDepthWidth * RawDepthHeight];
                    Buffer.BlockCopy(depthBytes, 0, floats, 0, depthBytes.Length);

                    if (!BitConverter.IsLittleEndian)
                    {
                        for (int i = 0; i < floats.Length; i++)
                        {
                            var b = BitConverter.GetBytes(floats[i]);
                            Array.Reverse(b);
                            floats[i] = BitConverter.ToSingle(b, 0);
                        }
                    }

                    if (!Mathf.Approximately(m_CurrentDepthWorldScale, 1f))
                    {
                        for (int i = 0; i < floats.Length; i++)
                            if (floats[i] > 0f)
                                floats[i] *= m_CurrentDepthWorldScale;
                    }

                    m_SfzDepthTexture.SetPixelData(floats, 0);
                }

                m_SfzDepthTexture.Apply(false);
                m_CurrentDepthTexture = m_SfzDepthTexture;
                m_LastSfzPlayHead = playHead;
            }
        }
    }
}
