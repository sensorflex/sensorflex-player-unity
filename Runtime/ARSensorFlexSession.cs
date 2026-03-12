using Unity.XR.CoreUtils;
using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Primary host-app integration component for SensorFlex replay sessions.
    /// Attach this to the scene's <see cref="XROrigin"/> to:
    /// - drive the replay camera rig from <see cref="PoseBridge"/>
    /// - instantiate the packaged scanned mesh
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Session")]
    public sealed class ARSensorFlexSession : MonoBehaviour
    {
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
            FileSystem = 0,
            WebSocket = 1,
            Zip = 2
        }

        [Header("Frame Source")]
        [SerializeField] FrameSourceMode m_FrameSourceMode = FrameSourceMode.FileSystem;
        [Tooltip("WebSocket endpoint used when the frame source mode is WebSocket.")]
        [SerializeField] string m_WebSocketUrl = "ws://localhost:3000";
        [Tooltip("Path to the ScanNet++ .zip archive. Can be absolute or relative to StreamingAssets.")]
        [SerializeField] string m_ZipFilePath = "";
        [Tooltip("StreamingAssets-relative or absolute folder containing replay RGB frames.")]
        [SerializeField] string m_ImageFolder = "DiskCam";

        [Header("Playback")]
        [Min(1)]
        [SerializeField] int m_PreloadFrameCount = 120;
        [SerializeField] bool m_LoopSequence = true;
        [Min(1f)]
        [SerializeField] float m_TargetFPS = 30f;

        [Header("Depth (Occlusion)")]
        [Tooltip("Enable the XROcclusionSubsystem to supply environment depth textures.")]
        [SerializeField] bool m_DepthEnabled = false;
        [Tooltip("StreamingAssets-relative or absolute folder containing depth images aligned to the color frames.")]
        [SerializeField] string m_DepthFolder = "DiskCamDepth";

        [Header("Session Alignment")]
        [SerializeField] SessionAlignmentSettings m_SessionAlignment = new();

        [Header("Camera Clip Planes")]
        [SerializeField] bool m_OverrideCameraClipPlanes = true;
        [SerializeField] float m_NearClipPlane = 0.01f;
        [SerializeField] float m_FarClipPlane = 1000f;

        [Header("Replay Camera")]
        [SerializeField] bool m_DriveReplayCamera = true;
        [SerializeField] Transform m_ReplayTargetOverride;
        [SerializeField] bool m_UseLocalSpace = true;
        [SerializeField] float m_PositionScale = 1f;
        [SerializeField] Vector3 m_PositionOffset = Vector3.zero;

        [Header("Scanned Mesh")]
        [SerializeField] bool m_InstantiateScannedMesh = true;
        [SerializeField] Transform m_MeshRootOverride;
        [SerializeField] Material m_Material;
        [SerializeField] bool m_AddMeshCollider;

        XROrigin m_XROrigin;
        GameObject m_MeshInstance;
        MeshFilter m_MeshFilter;
        MeshRenderer m_MeshRenderer;
        MeshCollider m_MeshCollider;
        Material m_RuntimeMaterial;

        public static ARSensorFlexSession ActiveSession { get; private set; }

        internal FrameSourceMode SourceMode => m_FrameSourceMode;
        internal string WebSocketUrl => m_WebSocketUrl;
        internal string ZipFilePath => m_ZipFilePath;
        internal string ImageFolder => m_ImageFolder;
        internal int PreloadFrameCount => Mathf.Max(1, m_PreloadFrameCount);
        internal bool LoopSequence => m_LoopSequence;
        internal float TargetFPS => Mathf.Max(1f, m_TargetFPS);
        internal bool DepthEnabled => m_DepthEnabled;
        internal string DepthFolder => m_DepthFolder;

        Transform ReplayTarget
            => m_ReplayTargetOverride != null
                ? m_ReplayTargetOverride
                : m_XROrigin != null && m_XROrigin.CameraFloorOffsetObject != null
                    ? m_XROrigin.CameraFloorOffsetObject.transform
                    : transform;

        Transform MeshRoot => m_MeshRootOverride != null ? m_MeshRootOverride : transform;

        void Awake()
        {
            m_XROrigin = GetComponent<XROrigin>();
        }

        void OnEnable()
        {
            ActiveSession = this;
            PoseBridge.OnPoseUpdated += ApplyPose;
            ScannedSceneMeshBridge.OnMeshReady += ApplyMesh;

            ApplySessionAlignment();
            ApplyCameraClipPlanes();

            if (PoseBridge.HasPose)
                ApplyPose(PoseBridge.LatestPose);
            if (ScannedSceneMeshBridge.HasMesh)
                ApplyMesh(ScannedSceneMeshBridge.LatestMesh);
        }

        void OnDisable()
        {
            PoseBridge.OnPoseUpdated -= ApplyPose;
            ScannedSceneMeshBridge.OnMeshReady -= ApplyMesh;

            if (ActiveSession == this)
                ActiveSession = null;
        }

        void OnDestroy()
        {
            if (m_RuntimeMaterial != null)
                Destroy(m_RuntimeMaterial);
        }

        void ApplyPose(Pose pose)
        {
            if (!m_DriveReplayCamera)
                return;

            var target = ReplayTarget;
            if (target == null)
                return;

            var position = pose.position * m_PositionScale + m_PositionOffset;
            if (m_UseLocalSpace)
            {
                target.localPosition = position;
                target.localRotation = pose.rotation;
                return;
            }

            target.SetPositionAndRotation(position, pose.rotation);
        }

        void ApplyMesh(Mesh mesh)
        {
            if (!m_InstantiateScannedMesh || mesh == null)
                return;

            EnsureMeshInstance();
            m_MeshFilter.sharedMesh = mesh;
            m_MeshRenderer.sharedMaterial = ResolveMaterial();

            if (m_AddMeshCollider)
            {
                m_MeshCollider ??= m_MeshInstance.GetComponent<MeshCollider>() ?? m_MeshInstance.AddComponent<MeshCollider>();
                m_MeshCollider.sharedMesh = mesh;
            }
            else if (m_MeshCollider != null)
            {
                Destroy(m_MeshCollider);
                m_MeshCollider = null;
            }
        }

        void EnsureMeshInstance()
        {
            if (m_MeshInstance != null)
                return;

            m_MeshInstance = new GameObject("SensorFlexScannedSceneMesh");
            m_MeshInstance.transform.SetParent(MeshRoot, false);
            m_MeshFilter = m_MeshInstance.AddComponent<MeshFilter>();
            m_MeshRenderer = m_MeshInstance.AddComponent<MeshRenderer>();
        }

        Material ResolveMaterial()
        {
            if (m_Material != null)
                return m_Material;

            if (m_RuntimeMaterial != null)
                return m_RuntimeMaterial;

            Shader shader = Shader.Find("SensorFlex/VertexColor")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
            if (shader == null)
                return null;

            m_RuntimeMaterial = new Material(shader)
            {
                name = "SensorFlexScannedSceneMeshMaterial"
            };

            if (m_RuntimeMaterial.HasProperty("_Color"))
                m_RuntimeMaterial.color = new Color(0.75f, 0.78f, 0.82f, 1f);

            return m_RuntimeMaterial;
        }

        void ApplyCameraClipPlanes()
        {
            if (!m_OverrideCameraClipPlanes || m_XROrigin?.Camera == null)
                return;

            var camera = m_XROrigin.Camera;
            camera.nearClipPlane = Mathf.Max(0.001f, m_NearClipPlane);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 0.01f, m_FarClipPlane);
        }

        internal static void GetPreferredClipPlanes(out float nearClipPlane, out float farClipPlane)
        {
            nearClipPlane = 0.01f;
            farClipPlane = 1000f;

            var activeSession = ResolveActiveSession();
            if (activeSession != null && activeSession.m_OverrideCameraClipPlanes)
            {
                nearClipPlane = Mathf.Max(0.001f, activeSession.m_NearClipPlane);
                farClipPlane = Mathf.Max(nearClipPlane + 0.01f, activeSession.m_FarClipPlane);
                return;
            }

            foreach (var session in Object.FindObjectsByType<ARSensorFlexSession>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (session == null || !session.m_OverrideCameraClipPlanes)
                    continue;

                nearClipPlane = Mathf.Max(0.001f, session.m_NearClipPlane);
                farClipPlane = Mathf.Max(nearClipPlane + 0.01f, session.m_FarClipPlane);
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
    }
}
