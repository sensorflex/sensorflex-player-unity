using UnityEngine;
using SensorFlex.Player.Library;

namespace SensorFlex.Player
{
    /// <summary>
    /// Attach this component to an empty child under the scene's XROrigin to instantiate
    /// the packaged scanned mesh under that object's transform at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Scene Mesh")]
    public sealed class ARSensorFlexSceneMesh : MonoBehaviour
    {
        [Tooltip("Optional material override used for the scanned scene mesh.")]
        [SerializeField] Material m_Material;
        [Tooltip("Add a MeshCollider to the instantiated scanned mesh.")]
        [SerializeField] bool m_AddMeshCollider;

        GameObject m_Instance;
        MeshFilter m_MeshFilter;
        MeshRenderer m_MeshRenderer;
        MeshCollider m_MeshCollider;
        Material m_RuntimeMaterial;

        void OnEnable()
        {
            ScannedSceneMeshBridge.OnMeshReady += ApplyMesh;
            if (ScannedSceneMeshBridge.HasMesh)
                ApplyMesh(ScannedSceneMeshBridge.LatestMesh);
        }

        void OnDisable()
        {
            ScannedSceneMeshBridge.OnMeshReady -= ApplyMesh;
        }

        void OnDestroy()
        {
            if (m_RuntimeMaterial != null)
                Destroy(m_RuntimeMaterial);
        }

        void ApplyMesh(Mesh mesh)
        {
            if (mesh == null)
                return;

            EnsureInstance();
            m_MeshFilter.sharedMesh = mesh;
            m_MeshRenderer.sharedMaterial = ResolveMaterial();

            if (m_AddMeshCollider)
            {
                m_MeshCollider ??= m_Instance.GetComponent<MeshCollider>() ?? m_Instance.AddComponent<MeshCollider>();
                m_MeshCollider.sharedMesh = mesh;
            }
            else if (m_MeshCollider != null)
            {
                Destroy(m_MeshCollider);
                m_MeshCollider = null;
            }
        }

        void EnsureInstance()
        {
            if (m_Instance != null)
                return;

            m_Instance = new GameObject("SensorFlexScannedSceneMesh");
            m_Instance.transform.SetParent(transform, false);
            m_MeshFilter = m_Instance.AddComponent<MeshFilter>();
            m_MeshRenderer = m_Instance.AddComponent<MeshRenderer>();
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
    }
}
