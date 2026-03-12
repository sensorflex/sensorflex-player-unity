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
            PoseBridge.OnPoseUpdated += ApplyPose;
            ScannedSceneMeshBridge.OnMeshReady += ApplyMesh;

            ApplySessionAlignment(SensorFlexSettings.RuntimeInstance ?? Resources.Load<SensorFlexSettings>("SensorFlexSettings"));
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

            foreach (var session in Object.FindObjectsByType<ARSensorFlexSession>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (session == null || !session.m_OverrideCameraClipPlanes)
                    continue;

                nearClipPlane = Mathf.Max(0.001f, session.m_NearClipPlane);
                farClipPlane = Mathf.Max(nearClipPlane + 0.01f, session.m_FarClipPlane);
                return;
            }
        }

        internal static void ApplySessionAlignment(SensorFlexSettings settings)
        {
            if (settings == null)
            {
                Debug.LogWarning("[SF] Session alignment skipped because SensorFlexSettings is null.");
                return;
            }

            var alignment = settings.sessionAlignment;
            if (alignment == null)
            {
                Debug.LogWarning("[SF] Session alignment skipped because the sessionAlignment block is missing.");
                return;
            }

            if (!alignment.enabled)
            {
                Debug.Log("[SF] Session alignment is disabled in SensorFlexSettings.");
                return;
            }

            var origins = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (origins == null || origins.Length == 0)
            {
                Debug.LogWarning("[SF] Session alignment is enabled, but no XROrigin was found in the scene.");
                return;
            }

            XROrigin fallbackOrigin = null;
            foreach (var origin in origins)
            {
                if (origin == null)
                    continue;

                fallbackOrigin ??= origin;
                if (!origin.gameObject.activeInHierarchy)
                    continue;

                ApplyLocalTransform(origin.transform, alignment);
                Debug.Log($"[SF] Applied session alignment to XROrigin '{origin.name}': " +
                          $"position={alignment.positionOffset} rotation={alignment.rotationEuler} scale={alignment.uniformScale}");
                return;
            }

            if (fallbackOrigin != null)
            {
                ApplyLocalTransform(fallbackOrigin.transform, alignment);
                Debug.Log($"[SF] Applied session alignment to inactive XROrigin '{fallbackOrigin.name}' before activation: " +
                          $"position={alignment.positionOffset} rotation={alignment.rotationEuler} scale={alignment.uniformScale}");
                return;
            }

            Debug.LogWarning("[SF] Session alignment is enabled, but no usable XROrigin was found in the scene.");
        }

        static void ApplyLocalTransform(Transform target, SensorFlexSettings.SessionAlignmentSettings alignment)
        {
            target.localPosition = alignment.positionOffset;
            target.localRotation = Quaternion.Euler(alignment.rotationEuler);
            target.localScale = Vector3.one * alignment.uniformScale;
        }
    }
}
