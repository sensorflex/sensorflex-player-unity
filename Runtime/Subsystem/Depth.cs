using System;
using System.Collections.Generic;
using System.IO;
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
            SensorFlexSettings settings;
            Texture2D[] preloadedDepthFrames;
            Texture2D m_CurrentDepthTexture;
            int index;
            bool framesReady;

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
                settings = SensorFlexSettings.RuntimeInstance
                    ?? Resources.Load<SensorFlexSettings>("SensorFlexSettings");

                if (settings == null)
                {
                    Debug.LogError("[SF] OcclusionSubsystem: SensorFlexSettings not found.");
                    return;
                }

                if (!settings.depthEnabled)
                {
                    Debug.Log("[SF] OcclusionSubsystem: depth disabled in settings.");
                    return;
                }

                if (settings.frameSourceMode != SensorFlexSettings.FrameSourceMode.FileSystem)
                {
                    Debug.LogWarning("[SF] OcclusionSubsystem: WebSocket depth not yet supported — depth disabled.");
                    return;
                }

                LoadDepthFrames();

                // Advance depth index in lock-step with camera frames
                CameraSubsystem.CameraDataProvider.OnFramesReady += AdvanceFrame;
            }

            public override void Stop()
            {
                CameraSubsystem.CameraDataProvider.OnFramesReady -= AdvanceFrame;

                if (preloadedDepthFrames != null)
                {
                    foreach (var tex in preloadedDepthFrames)
                        if (tex != null) UnityEngine.Object.Destroy(tex);

                    preloadedDepthFrames = null;
                }

                m_CurrentDepthTexture = null;
                framesReady = false;
            }

            public override void Destroy() { }

            // ----------------------------------------------------------------
            // GPU texture access (called by AROcclusionManager each frame)
            // ----------------------------------------------------------------
            public override bool TryGetEnvironmentDepth(out XRTextureDescriptor environmentDepthDescriptor)
            {
                if (!framesReady || m_CurrentDepthTexture == null)
                {
                    environmentDepthDescriptor = default;
                    return false;
                }

                environmentDepthDescriptor = new XRTextureDescriptor(
                    m_CurrentDepthTexture.GetNativeTexturePtr(),
                    m_CurrentDepthTexture.width,
                    m_CurrentDepthTexture.height,
                    m_CurrentDepthTexture.mipmapCount,
                    m_CurrentDepthTexture.format,
                    Shader.PropertyToID("_EnvironmentDepth"),
                    0,
                    TextureDimension.Tex2D
                );

                return true;
            }

            // ----------------------------------------------------------------
            // Helpers
            // ----------------------------------------------------------------
            void LoadDepthFrames()
            {
                string folder = settings.depthFolder;
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
                        file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                int count = Mathf.Min(settings.preloadFrameCount, files.Count);
                if (count == 0)
                {
                    Debug.LogWarning($"[SF] OcclusionSubsystem: no depth images found in {folder}");
                    return;
                }

                preloadedDepthFrames = new Texture2D[count];
                for (int i = 0; i < count; i++)
                    preloadedDepthFrames[i] = LoadDepthFrame(files[i]);

                index = 0;
                m_CurrentDepthTexture = preloadedDepthFrames[0];
                framesReady = true;

                Debug.Log($"[SF] OcclusionSubsystem: loaded {count} depth frames from {folder}");
            }

            static Texture2D LoadDepthFrame(string path)
            {
                byte[] bytes = File.ReadAllBytes(path);
                // LoadImage always decodes PNG/JPG into RGBA32.
                // The R channel carries normalised depth [0,1].
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                tex.Apply();
                return tex;
            }

            void AdvanceFrame()
            {
                if (!framesReady || preloadedDepthFrames == null)
                    return;

                index++;
                if (index >= preloadedDepthFrames.Length)
                    index = settings != null && settings.loopSequence ? 0 : preloadedDepthFrames.Length - 1;

                m_CurrentDepthTexture = preloadedDepthFrames[index];
            }
        }
    }
}
