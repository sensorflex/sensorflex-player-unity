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
            WebSocket = 1
        }

        [Header("Frame Source")]
        public FrameSourceMode frameSourceMode = FrameSourceMode.FileSystem;

        [Header("WebSocket")]
        public string webSocketUrl = "ws://localhost:3000";

        [Header("Playback / Loading")]
        [Min(1)]
        public int preloadFrameCount = 120;

        [Min(0)]
        public int framesToWaitForLoadingScreen = 10;

        // Keep your existing fields too (used by file-system mode and timing):
        [Header("File System / Timing")]
        public string imageFolder = "DiskCam";

        [Min(1f)]
        public float targetFPS = 30f;

        public bool loopSequence = true;

        public static SensorFlexSettings RuntimeInstance { get; private set; }

        private void OnEnable()
        {
            // If this settings object is the one loaded by XR Plug-in Management,
            // it will become the runtime instance automatically.
            RuntimeInstance = this;
        }
    }
}
