using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Rendering;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;
using System.Text;
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
            private WebSocket webSocket;

            private bool isWebSocketPreloadStarted;
            private bool isWebSocketConnected;

            private bool framesAreReady;
            private int framesExpectedCount;
            private int framesReceivedCount;

            public int width = 512;
            public int height = 256;
            public Color backgroundColor = Color.blue;
            public Color textColor = Color.white;
            public Font font;
            public static event System.Action OnFramesReady;

            List<string> frames = new();
            double nextFrameTime;
            double frameInterval = 1.0 / 30.0;
            int index = 0;
            long timestampNs = 0;
            SensorFlexSettings settings; //new settings reference

            Texture2D[] preloadedFrames;
            int maxFramesToLoad;

            private const string LoadingTextureResourcePath = "Loading/loading";
            private Texture2D m_FrameTexture;
            private Texture2D loadingTexture;
            private bool useLoadingTexture = true;
            private int FramesLoaded = 0;
            private int FramesToWait;


            // AR Foundation 6.0+ uses null to indicate using default material
            private Texture2D m_CurrentTexture;
            private Material m_CameraMaterial;
            public override Material cameraMaterial
            {
                get
                {
                    if (m_CameraMaterial == null)
                    {
                        CreateCameraMaterial();
                    }

                    if (m_CameraMaterial != null && m_CurrentTexture != null)
                    {
                        m_CameraMaterial.mainTexture = m_CurrentTexture;
                    }

                    return m_CameraMaterial;
                }
            }

            // Tell ARFoundation that we support rendering the background at all
            public override XRSupportedCameraBackgroundRenderingMode supportedBackgroundRenderingMode
                => XRSupportedCameraBackgroundRenderingMode.Any;

            // Accept whatever render mode ARCameraManager asks for (Any/Before/After Opaques)
            public override XRSupportedCameraBackgroundRenderingMode requestedBackgroundRenderingMode
            {
                get => XRSupportedCameraBackgroundRenderingMode.Any;
                set { /* You could store 'value' if you want to respect it explicitly */ }
            }
            // Report a sane current mode (BeforeOpaques is the usual default)
            public override XRCameraBackgroundRenderingMode currentBackgroundRenderingMode
                => XRCameraBackgroundRenderingMode.BeforeOpaques;


            private void CreateCameraMaterial()
            {
                // Try to find the shader
                Shader shader = Shader.Find("Unlit/Texture");

                if (shader == null)
                {
                    // Try alternative built-in shaders
                    shader = Shader.Find("UI/Default");
                }

                if (shader != null)
                {
                    m_CameraMaterial = new Material(shader);
                    m_CameraMaterial.name = "CustomARCameraMaterial";
                    m_CameraMaterial.renderQueue = 1000; // Background queue (before geometry at 2000)
                    Debug.Log("[CustomAR] Created camera material with shader: " + shader.name);
                }
                else
                {
                    Debug.LogError("[CustomAR] Could not find any suitable shader for camera material!");
                }
            }

            public override bool permissionGranted => true;
            public override Feature currentCamera => Feature.WorldFacingCamera;


            public override Feature requestedCamera
            {
                get => Feature.WorldFacingCamera;
                set { } // Ignore set requests
            }

            public override bool autoFocusEnabled => false;

            public override bool autoFocusRequested
            {
                get => false;
                set { } // Ignore set requests
            }
            private XRCameraConfiguration m_CurrentConfiguration;

            public override XRCameraConfiguration? currentConfiguration => m_CurrentConfiguration;


            public override void Start()
            {
                useLoadingTexture = true;
                FramesLoaded = 0;

                loadingTexture = Resources.Load<Texture2D>(LoadingTextureResourcePath);
                if (loadingTexture == null)
                {
                    Debug.LogError("[SF] Could not load loading texture at Resources/Loading/loading.png");
                }
                else
                {
                    m_CurrentTexture = loadingTexture;
                }


                //Load SensorFlexSettings
                settings = SensorFlexSettings.RuntimeInstance ?? Resources.Load<SensorFlexSettings>("SensorFlexSettings");


                // Setup default camera configuration
                m_CurrentConfiguration = new XRCameraConfiguration(
                    IntPtr.Zero,
                    new Vector2Int(1920, 1080),
                    framerate: 30
                );

                if (settings == null)
                {
                    Debug.LogError("SensorFlexSettings.asset not found in Resources/");
                    return;
                }
                maxFramesToLoad = settings.preloadFrameCount;
                FramesToWait = settings.framesToWaitForLoadingScreen;
                Debug.Log($"[SF] Loaded settings '{settings.name}' (InstanceID={settings.GetInstanceID()}) FramesToWait={FramesToWait}");

                frameInterval = 1.0 / Math.Max(1, settings.targetFPS);
                string folder = settings.imageFolder;
                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem)
                {
                    // Handle absolute or StreamingAssets-relative folder

                    if (!Path.IsPathRooted(folder))
                        folder = Path.Combine(Application.streamingAssetsPath, folder);

                    if (!Directory.Exists(folder))
                    {
                        Debug.LogError($"[SF] folder not found: {folder}");
                        return;
                    }

                    frames.Clear();
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            frames.Add(file);
                        }
                    }

                    frames.Sort(StringComparer.Ordinal);

                    if (frames.Count == 0)
                    {
                        Debug.LogError($"[SF] no image files found in {folder}");
                        return;
                    }
                }
                else
                {
                    // WebSocket mode doesn't need local file list
                    frames.Clear();
                }

                PreloadFrames();

                //LoadFrame(0);
                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;

                Debug.Log($"[SF] found {frames.Count} frames in {folder}");

            }
            Texture2D GenerateTexture(string text)
            {
                // Create RenderTexture
                RenderTexture rt = new RenderTexture(width, height, 0);
                RenderTexture.active = rt;

                // Clear background
                GL.Clear(true, true, backgroundColor);

                // Prepare GUI
                GUIStyle style = new GUIStyle();
                style.font = font;
                style.fontSize = 48;
                style.alignment = TextAnchor.MiddleCenter;
                style.normal.textColor = textColor;

                // Draw text
                GUI.matrix = Matrix4x4.identity;
                GUI.Label(new Rect(0, 0, width, height), text, style);

                // Read pixels into Texture2D
                Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                // Cleanup
                RenderTexture.active = null;
                rt.Release();

                return tex;
            }
            void PreloadFrames()
            {
                framesAreReady = false;
                framesReceivedCount = 0;

                int count = settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem
                    ? Mathf.Min(maxFramesToLoad, frames.Count)
                    : Mathf.Max(0, maxFramesToLoad);

                framesExpectedCount = count;

                preloadedFrames = new Texture2D[count];

                if (settings.frameSourceMode == SensorFlexSettings.FrameSourceMode.FileSystem)
                {
                    for (int frameIndex = 0; frameIndex < count; frameIndex++)
                    {
                        byte[] fileBytes = File.ReadAllBytes(frames[frameIndex]);

                        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        texture.LoadImage(fileBytes);
                        texture.Apply();

                        preloadedFrames[frameIndex] = texture;
                    }

                    framesAreReady = true;
                    Debug.Log($"[SF] Preloaded {count} frames from file system.");
                    return;
                }

                // WebSocket preload path
                StartWebSocketPreload(count);
            }

            void LoadFrame(int i)
            {
                if (preloadedFrames == null || preloadedFrames.Length == 0)
                    return;

                if (i < 0 || i >= preloadedFrames.Length)
                    return;

                m_CurrentTexture = preloadedFrames[i];
                timestampNs += (long)(frameInterval * 1_000_000_000L);
            }

            async void StartWebSocketPreload(int requestedFrameCount)
            {
                if (isWebSocketPreloadStarted)
                {
                    return;
                }

                isWebSocketPreloadStarted = true;

                string webSocketUrl = settings.webSocketUrl;
                Debug.Log($"[SF] Starting WebSocket preload from {webSocketUrl}, requesting {requestedFrameCount} frames.");

                webSocket = new WebSocket(webSocketUrl);

                webSocket.OnOpen += () =>
                {
                    isWebSocketConnected = true;
                    Debug.Log("[SF] WebSocket connected.");

                    // Request frames from server
                    // Protocol: "GET_FRAMES <count>"
                    string requestText = $"GET_FRAMES {requestedFrameCount}";
                    _ = webSocket.SendText(requestText);
                };

                webSocket.OnError += (errorMessage) =>
                {
                    Debug.LogError("[SF] WebSocket error: " + errorMessage);
                };

                webSocket.OnClose += (closeCode) =>
                {
                    isWebSocketConnected = false;
                    Debug.Log("[SF] WebSocket closed: " + closeCode);
                };

                webSocket.OnMessage += (byte[] messageBytes) =>
                {
                    // Binary protocol:
                    // First 4 bytes: frameIndex (Int32 little-endian)
                    // Remaining bytes: encoded image bytes (png/jpg)
                    if (messageBytes == null || messageBytes.Length < 5)
                    {
                        return;
                    }

                    int frameIndex = BitConverter.ToInt32(messageBytes, 0);

                    if (frameIndex < 0 || frameIndex >= preloadedFrames.Length)
                    {
                        Debug.LogWarning($"[SF] Received out-of-range frameIndex={frameIndex} (buffer size {preloadedFrames.Length}).");
                        return;
                    }

                    // Extract image bytes (skip first 4 bytes)
                    int imageByteCount = messageBytes.Length - 4;
                    byte[] imageBytes = new byte[imageByteCount];
                    Buffer.BlockCopy(messageBytes, 4, imageBytes, 0, imageByteCount);

                    // Decode image
                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    bool loaded = texture.LoadImage(imageBytes);

                    if (!loaded)
                    {
                        Debug.LogWarning($"[SF] Failed to decode image for frameIndex={frameIndex}.");
                        UnityEngine.Object.Destroy(texture);
                        return;
                    }

                    texture.Apply();

                    // Only count if this slot was empty
                    if (preloadedFrames[frameIndex] == null)
                    {
                        framesReceivedCount++;
                    }
                    else
                    {
                        // Replace existing texture (optional)
                        UnityEngine.Object.Destroy(preloadedFrames[frameIndex]);
                    }

                    preloadedFrames[frameIndex] = texture;

                    if (framesReceivedCount >= framesExpectedCount)
                    {
                        framesAreReady = true;
                        Debug.Log($"[SF] WebSocket preload complete. Received {framesReceivedCount}/{framesExpectedCount} frames.");
                    }
                };

                try
                {
                    await webSocket.Connect();
                }
                catch (Exception exception)
                {
                    Debug.LogError("[SF] WebSocket connect exception: " + exception);
                }
            }


            void UpdateFrameIfNeeded()
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                webSocket?.DispatchMessageQueue();
#endif

                if (Time.realtimeSinceStartupAsDouble < nextFrameTime)
                    return;

                nextFrameTime = Time.realtimeSinceStartupAsDouble + frameInterval;

                if (useLoadingTexture)
                {
                    FramesLoaded++;

                    bool shouldExitLoadingScreen = FramesLoaded >= FramesToWait && framesAreReady;

                    if (shouldExitLoadingScreen)
                    {
                        useLoadingTexture = false;
                        index = 0;
                        LoadFrame(index);
                    }

                    OnFramesReady?.Invoke();
                    return;
                }

                index++;
                if (index >= preloadedFrames.Length)
                    index = settings.loopSequence ? 0 : preloadedFrames.Length - 1;


                LoadFrame(index);
                OnFramesReady?.Invoke();
            }



            public override bool TryGetFrame(XRCameraParams cameraParams, out XRCameraFrame frame)
            {
                UpdateFrameIfNeeded();

                // 1. Calculate a valid projection matrix
                // We use a default 60-degree FOV and calculate the aspect ratio
                float aspect = (float)cameraParams.screenWidth / cameraParams.screenHeight;
                Matrix4x4 projectionMatrix = Matrix4x4.Perspective(60f, aspect, 0.1f, 100f);

                // 2. Use a simple identity matrix for the display matrix.
                // This tells the background renderer to just stretch-fill the texture.
                // If your texture appears upside down, you might need:
                // Matrix4x4.Scale(new Vector3(1, -1, 1));
                Matrix4x4 displayMatrix = Matrix4x4.identity;

                // 3. Set properties to tell the system what data we are providing
                XRCameraFrameProperties properties = XRCameraFrameProperties.Timestamp |
                                                     XRCameraFrameProperties.ProjectionMatrix |
                                                     XRCameraFrameProperties.DisplayMatrix;

                // 4. Construct the frame with the *correct* data
                frame = new XRCameraFrame(
                        timestampNs,            // <-- Use your calculated timestamp
                        0f,                     // averageBrightness
                        0f,                     // averageColorTemperature
                        Color.black,            // colorCorrection
                        projectionMatrix,       // <-- Use the calculated projection
                        displayMatrix,          // <-- Use the calculated display matrix
                        TrackingState.Tracking, // trackingState
                        IntPtr.Zero,            // nativePtr
                        properties,             // <-- Use the updated properties
                        0f,                     // noiseIntensity
                        0.0,                    // averageIntensityInLumens
                        0f,                     // mainLightIntensityInLumens
                        0f,                     // mainLightTemperature
                        Color.black,            // mainLightColor
                        Vector3.zero,           // mainLightDirection
                        new SphericalHarmonicsL2(), // ambientSphericalHarmonics
                        new XRTextureDescriptor(),  // cameraGrainTextureDescriptor
                        0f);                    // currentExposure

                return true;
            }

            public override bool TryAcquireLatestCpuImage(out XRCpuImage.Cinfo cameraImageCinfo)
            {
                Debug.Log("TryAcquireLatestCpuImage");
                cameraImageCinfo = default;
                return false;
            }

            public override NativeArray<XRTextureDescriptor> GetTextureDescriptors(
                XRTextureDescriptor defaultDescriptor,
                Allocator allocator)
            {

                UpdateFrameIfNeeded();
                if (m_CurrentTexture == null)
                {
                    return new NativeArray<XRTextureDescriptor>(0, allocator);
                }

                // Provide texture descriptor for AR Foundation's background rendering
                var descriptors = new NativeArray<XRTextureDescriptor>(1, allocator);

                var desc = new XRTextureDescriptor(
                    m_CurrentTexture.GetNativeTexturePtr(),
                    m_CurrentTexture.width,
                    m_CurrentTexture.height,
                    m_CurrentTexture.mipmapCount,
                    m_CurrentTexture.format,
                    Shader.PropertyToID("_MainTex"),
                    0,
                    TextureDimension.Tex2D
                );
                descriptors[0] = desc;

                Debug.Log($"[SF] GetTextureDescriptors: ptr={desc.nativeTexture}, {desc.width}x{desc.height}");

                return descriptors;
            }

            /// <summary>
            /// Get the camera intrinsics information.
            /// </summary>
            /// <param name="cameraIntrinsics">The camera intrinsics information returned from the method.</param>
            /// <returns>
            /// <see langword="true"/> if the method successfully gets the camera intrinsics information. Otherwise, <see langword="false"/>.
            /// </returns>
            public override bool TryGetIntrinsics(out XRCameraIntrinsics cameraIntrinsics)
            {
                Debug.Log("TryGetIntrinsics");
                cameraIntrinsics = new XRCameraIntrinsics(
                    new Vector2(935.3f, 935.3f), new Vector2(1920 / 2.0f, 1080 / 2.0f), new Vector2Int(1920, 1080));

                return true;
            }


            public override async void Stop()
            {
                if (webSocket != null)
                {
                    try
                    {
                        await webSocket.Close();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[SF] WebSocket close exception: " + exception);
                    }

                    webSocket = null;
                }

                if (preloadedFrames != null)
                {
                    for (int frameIndex = 0; frameIndex < preloadedFrames.Length; frameIndex++)
                    {
                        if (preloadedFrames[frameIndex] != null)
                        {
                            UnityEngine.Object.Destroy(preloadedFrames[frameIndex]);
                            preloadedFrames[frameIndex] = null;
                        }
                    }
                }

                m_FrameTexture = null;
                m_CurrentTexture = null;
            }

        }



    }
}
