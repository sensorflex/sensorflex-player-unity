using UnityEditor;
using UnityEngine;
using SensorFlex.Player;

namespace SensorFlexPlayer.Editor
{
    [CustomEditor(typeof(SensorFlexSettings))]
    public class SensorFlexSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var modeProp = serializedObject.FindProperty("frameSourceMode");
            EditorGUILayout.PropertyField(modeProp);
            EditorGUILayout.Space();

            var mode = (SensorFlexSettings.FrameSourceMode)modeProp.enumValueIndex;

            if (mode == SensorFlexSettings.FrameSourceMode.WebSocket)
            {
                EditorGUILayout.LabelField("WebSocket", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("webSocketUrl"));
            }
            else if (mode == SensorFlexSettings.FrameSourceMode.Zip)
            {
                EditorGUILayout.LabelField("ZIP", EditorStyles.boldLabel);

                var pathProp = serializedObject.FindProperty("zipFilePath");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.TextField("Archive", pathProp.stringValue);
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
                EditorGUILayout.PropertyField(serializedObject.FindProperty("imageFolder"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("preloadFrameCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("loopSequence"));
            if (mode != SensorFlexSettings.FrameSourceMode.Zip)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("targetFPS"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Depth (Occlusion)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("depthEnabled"));
            if (mode == SensorFlexSettings.FrameSourceMode.FileSystem)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("depthFolder"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session Alignment", EditorStyles.boldLabel);
            var sessionAlignmentProp = serializedObject.FindProperty("sessionAlignment");
            var enabledProp = sessionAlignmentProp.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(enabledProp, new GUIContent("Apply To XR Origin"));
            if (enabledProp.boolValue)
            {
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("positionOffset"));
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("rotationEuler"));
                EditorGUILayout.PropertyField(sessionAlignmentProp.FindPropertyRelative("uniformScale"));
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
