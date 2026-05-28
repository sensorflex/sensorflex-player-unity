using System;
using UnityEngine;

namespace SensorFlex.Player
{
    internal static class ScannedSceneMeshBridge
    {
        public static bool HasMesh => LatestMesh != null;
        public static string SceneId { get; private set; }
        public static Mesh LatestMesh { get; private set; }

        public static event Action<Mesh> OnMeshReady;

        internal static void SetMesh(Mesh mesh, string sceneId)
        {
            Clear();
            LatestMesh = mesh;
            SceneId = sceneId;
            OnMeshReady?.Invoke(LatestMesh);
        }

        internal static void Clear()
        {
            if (LatestMesh != null)
                UnityEngine.Object.Destroy(LatestMesh);

            LatestMesh = null;
            SceneId = null;
        }
    }
}
