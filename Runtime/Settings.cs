using UnityEngine;
using UnityEngine.XR.Management;

namespace SensorFlex.Player
{
    [XRConfigurationData("SensorFlex Player", "SensorFlex.Player.SensorFlexSettings")]
    [CreateAssetMenu(menuName = "SensorFlex Player/Settings", fileName = "SensorFlexSettings")]
    public class SensorFlexSettings : ScriptableObject
    {
        public enum FrameSourceMode
        {
            FileSystem = 0,
            WebSocket = 1,
            Zip = 2
        }

        public FrameSourceMode frameSourceMode = FrameSourceMode.FileSystem;

        public string webSocketUrl = "ws://localhost:3000";

        [Tooltip("Path to the ScanNet++ .zip archive. Can be absolute or relative to StreamingAssets.")]
        public string zipFilePath = "";

        [Min(1)]
        public int preloadFrameCount = 120;

        public bool loopSequence = true;

        public string imageFolder = "DiskCam";

        [Min(1f)]
        public float targetFPS = 30f;

        [Tooltip("Enable the XROcclusionSubsystem to supply environment depth textures.")]
        public bool depthEnabled = false;

        [Tooltip("StreamingAssets-relative folder containing depth images (PNG/JPG, one per color frame, same sorted order).")]
        public string depthFolder = "DiskCamDepth";

        public static SensorFlexSettings RuntimeInstance { get; private set; }

        private void OnEnable()
        {
            // If this settings object is the one loaded by XR Plug-in Management,
            // it will become the runtime instance automatically.
            RuntimeInstance = this;
        }
    }
}
