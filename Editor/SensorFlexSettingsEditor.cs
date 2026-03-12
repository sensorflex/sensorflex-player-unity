using UnityEditor;
using UnityEngine;
using SensorFlex.Player;

namespace SensorFlexPlayer.Editor
{
    [CustomEditor(typeof(ARSensorFlexSession))]
    public class ARSensorFlexSessionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var modeProp = serializedObject.FindProperty("m_FrameSourceMode");
            EditorGUILayout.PropertyField(modeProp);
            EditorGUILayout.Space();

            var mode = (ARSensorFlexSession.FrameSourceMode)modeProp.enumValueIndex;

            if (mode == ARSensorFlexSession.FrameSourceMode.WebSocket)
            {
                EditorGUILayout.LabelField("WebSocket", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_WebSocketUrl"));
            }
            else if (mode == ARSensorFlexSession.FrameSourceMode.Zip)
            {
                EditorGUILayout.LabelField("ZIP", EditorStyles.boldLabel);

                var pathProp = serializedObject.FindProperty("m_ZipFilePath");
                EditorGUILayout.BeginHorizontal();
                pathProp.stringValue = EditorGUILayout.TextField("Archive", pathProp.stringValue);
                if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                {
                    string picked = EditorUtility.OpenFilePanel("Select ZIP archive", "", "zip");
                    if (!string.IsNullOrEmpty(picked))
                        pathProp.stringValue = picked;
                }
                EditorGUILayout.EndHorizontal();
            }
            else // FileSystem
            {
                EditorGUILayout.LabelField("File System", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ImageFolder"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PreloadFrameCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_LoopSequence"));
            if (mode != ARSensorFlexSession.FrameSourceMode.Zip)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetFPS"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Depth (Occlusion)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DepthEnabled"));
            if (mode == ARSensorFlexSession.FrameSourceMode.FileSystem)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DepthFolder"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session Alignment", EditorStyles.boldLabel);
            var sessionAlignmentProp = serializedObject.FindProperty("m_SessionAlignment");
            var enabledProp = sessionAlignmentProp.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(enabledProp, new GUIContent("Apply To XR Origin"));
            if (enabledProp.boolValue)
            {
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("positionOffset"));
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("rotationEuler"));
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("uniformScale"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Camera Clip Planes", EditorStyles.boldLabel);
            var overrideClipPlanesProp = serializedObject.FindProperty("m_OverrideCameraClipPlanes");
            EditorGUILayout.PropertyField(overrideClipPlanesProp);
            if (overrideClipPlanesProp.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_NearClipPlane"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_FarClipPlane"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Replay Camera", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DriveReplayCamera"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ReplayTargetOverride"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_UseLocalSpace"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PositionScale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PositionOffset"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scanned Mesh", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_InstantiateScannedMesh"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MeshRootOverride"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Material"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_AddMeshCollider"));

            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(SensorFlexSettings))]
    public class SensorFlexSettingsAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Session source and playback options now live on the scene's ARSensorFlexSession component. " +
                "This asset remains only for XR Plug-in Management registration.",
                MessageType.Info);
        }
    }
}
