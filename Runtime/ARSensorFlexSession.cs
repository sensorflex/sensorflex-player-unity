using Unity.XR.CoreUtils;
using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Primary host-app integration component for SensorFlex replay sessions.
    /// Attach this to the scene's ARSession or another scene object to:
    /// - drive the replay camera rig from <see cref="PoseBridge"/>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Session")]
    public sealed class ARSensorFlexSession : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] XROrigin m_XROrigin;

        [System.Serializable]
        public class SessionAlignmentSettings
        {
            [Tooltip("Apply this transform to the active XROrigin when the SensorFlex session starts.")]
            public bool enabled = false;

            [Tooltip("Local position offset applied to the XROrigin.")]
            public Vector3 positionOffset = Vector3.zero;

            [Tooltip("Local Euler rotation offset, in degrees, applied to the XROrigin.")]
            public Vector3 rotationEuler = Vector3.zero;

            [Min(0.0001f)]
            [Tooltip("Uniform local scale applied to the XROrigin.")]
            public float uniformScale = 1f;
        }

        public enum FrameSourceMode
        {
            Live = 1,
            Sfz = 2,
            FileIo = 3
        }

        [Header("Frame Source")]
        [SerializeField] FrameSourceMode m_FrameSourceMode = FrameSourceMode.FileIo;
        [Tooltip("WebSocket endpoint used when the frame source mode is WebSocket.")]
        [SerializeField] string m_WebSocketUrl = "ws://localhost:3000";
        [Tooltip("Path to the .sfz archive. Can be absolute or relative to StreamingAssets.")]
        [SerializeField] string m_SfzFilePath = "";
        [Tooltip("Path to the SFZ session directory (containing session.json). Can be absolute or relative to StreamingAssets.")]
        [SerializeField] string m_FileIoPath = "";

        [Header("Playback")]
        [Min(1)]
        [SerializeField] int m_PreloadFrameCount = 120;
        [SerializeField] bool m_LoopSequence = true;
        [Tooltip("Live mode only: total ring buffer capacity. Determines max pause duration before the buffer overflows and jumps to latest.")]
        [Min(1)]
        [SerializeField] int m_TotalLiveBufferSize = 300;

        [Header("Depth (Occlusion)")]
        [Tooltip("Enable the XROcclusionSubsystem to supply environment depth textures.")]
        [SerializeField] bool m_DepthEnabled = false;

        [Header("Session Alignment")]
        [SerializeField] SessionAlignmentSettings m_SessionAlignment = new();

        [Header("Replay Camera")]
        [SerializeField] bool m_DriveReplayCamera = true;
        [SerializeField] Transform m_ReplayTargetOverride;
        [SerializeField] float m_PositionScale = 1f;
        [SerializeField] Vector3 m_PositionOffset = Vector3.zero;

        public static ARSensorFlexSession ActiveSession { get; private set; }

        internal FrameSourceMode SourceMode => m_FrameSourceMode;
        internal string WebSocketUrl => m_WebSocketUrl;
        internal string SfzFilePath => m_SfzFilePath;
        internal string FileIoPath => m_FileIoPath;
        internal int PreloadFrameCount => Mathf.Max(1, m_PreloadFrameCount);
        internal bool LoopSequence => m_LoopSequence;
        internal int TotalLiveBufferSize => Mathf.Max(PreloadFrameCount, m_TotalLiveBufferSize);
        internal bool DepthEnabled => m_DepthEnabled;
        internal float EffectiveDepthWorldScale
        {
            get
            {
                float alignmentScale = 1f;
                if (m_SessionAlignment != null && m_SessionAlignment.enabled)
                    alignmentScale = Mathf.Max(0.0001f, m_SessionAlignment.uniformScale);

                return Mathf.Max(0.0001f, alignmentScale * Mathf.Max(0.0001f, m_PositionScale));
            }
        }

        Transform ReplayTarget
            => m_ReplayTargetOverride != null
                ? m_ReplayTargetOverride
                : m_XROrigin != null && m_XROrigin.CameraFloorOffsetObject != null
                    ? m_XROrigin.CameraFloorOffsetObject.transform
                    : transform;

        void Awake()
        {
            ResolveXROriginReference();
        }

        void OnValidate()
        {
            ResolveXROriginReference();
        }

        void OnEnable()
        {
            ActiveSession = this;
            PoseBridge.OnPoseUpdated += ApplyPose;

            ApplySessionAlignment();

            if (PoseBridge.HasPose)
                ApplyPose(PoseBridge.LatestPose);
        }

        void OnDisable()
        {
            PoseBridge.OnPoseUpdated -= ApplyPose;

            if (ActiveSession == this)
                ActiveSession = null;
        }

        void ApplyPose(Pose pose)
        {
            if (!m_DriveReplayCamera)
                return;

            var target = ReplayTarget;
            if (target == null)
                return;

            var position = pose.position * m_PositionScale + m_PositionOffset;
            target.localPosition = position;
            target.localRotation = pose.rotation;
        }

        internal static void GetPreferredClipPlanes(out float nearClipPlane, out float farClipPlane)
        {
            nearClipPlane = 0.01f;
            farClipPlane = 1000f;

            var activeSession = ResolveActiveSession();
            if (activeSession != null && activeSession.TryGetSceneClipPlanes(out nearClipPlane, out farClipPlane))
            {
                return;
            }

            foreach (var session in Object.FindObjectsByType<ARSensorFlexSession>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (session == null)
                    continue;

                if (!session.TryGetSceneClipPlanes(out nearClipPlane, out farClipPlane))
                    continue;

                return;
            }
        }

        internal static ARSensorFlexSession ResolveActiveSession()
        {
            if (ActiveSession != null && ActiveSession.isActiveAndEnabled)
                return ActiveSession;

            foreach (var session in Object.FindObjectsByType<ARSensorFlexSession>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (session == null || !session.isActiveAndEnabled)
                    continue;

                ActiveSession = session;
                return session;
            }

            return null;
        }

        internal static bool TryGetActiveSession(out ARSensorFlexSession session)
        {
            session = ResolveActiveSession();
            return session != null;
        }

        internal void ApplySessionAlignment()
        {
            var alignment = m_SessionAlignment;
            if (alignment == null)
            {
                Debug.LogWarning("[SF] Session alignment skipped because the alignment block is missing.");
                return;
            }

            if (!alignment.enabled)
                return;

            if (m_XROrigin == null)
            {
                Debug.LogWarning("[SF] Session alignment is enabled, but this ARSensorFlexSession has no XROrigin.");
                return;
            }

            ApplyLocalTransform(m_XROrigin.transform, alignment);
            Debug.Log($"[SF] Applied session alignment to XROrigin '{m_XROrigin.name}': " +
                      $"position={alignment.positionOffset} rotation={alignment.rotationEuler} scale={alignment.uniformScale}");
        }

        static void ApplyLocalTransform(Transform target, SessionAlignmentSettings alignment)
        {
            target.localPosition = alignment.positionOffset;
            target.localRotation = Quaternion.Euler(alignment.rotationEuler);
            target.localScale = Vector3.one * alignment.uniformScale;
        }

        void ResolveXROriginReference()
        {
            if (m_XROrigin != null)
                return;

            m_XROrigin = GetComponent<XROrigin>();
            if (m_XROrigin != null)
                return;

            m_XROrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        }

        bool TryGetSceneClipPlanes(out float nearClipPlane, out float farClipPlane)
        {
            ResolveXROriginReference();

            if (m_XROrigin?.Camera == null)
            {
                nearClipPlane = 0.01f;
                farClipPlane = 1000f;
                return false;
            }

            nearClipPlane = Mathf.Max(0.001f, m_XROrigin.Camera.nearClipPlane);
            farClipPlane = Mathf.Max(nearClipPlane + 0.01f, m_XROrigin.Camera.farClipPlane);
            return true;
        }
    }
}
