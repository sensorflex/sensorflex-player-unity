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

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_XROrigin"));
            EditorGUILayout.Space();

            var modeProp = serializedObject.FindProperty("m_FrameSourceMode");
            EditorGUILayout.PropertyField(modeProp);
            EditorGUILayout.Space();

            var mode = (ARSensorFlexSession.FrameSourceMode)modeProp.intValue;

            if (mode == ARSensorFlexSession.FrameSourceMode.Sfz)
            {
                var pathProp = serializedObject.FindProperty("m_SfzFilePath");
                EditorGUILayout.BeginHorizontal();
                pathProp.stringValue = EditorGUILayout.TextField("SFZ Archive", pathProp.stringValue);
                bool browseSfz = GUILayout.Button("Browse…", GUILayout.Width(70));
                EditorGUILayout.EndHorizontal();
                if (browseSfz)
                {
                    string picked = EditorUtility.OpenFilePanel("Select SFZ archive", "", "sfz");
                    if (!string.IsNullOrEmpty(picked))
                        pathProp.stringValue = picked;
                }
            }
            else if (mode == ARSensorFlexSession.FrameSourceMode.FileIo)
            {
                var pathProp = serializedObject.FindProperty("m_FileIoPath");
                EditorGUILayout.BeginHorizontal();
                pathProp.stringValue = EditorGUILayout.TextField("Session Directory", pathProp.stringValue);
                bool browseDir = GUILayout.Button("Browse…", GUILayout.Width(70));
                EditorGUILayout.EndHorizontal();
                if (browseDir)
                {
                    string picked = EditorUtility.OpenFolderPanel("Select session directory", "", "");
                    if (!string.IsNullOrEmpty(picked))
                        pathProp.stringValue = picked;
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PreloadFrameCount"),
                new GUIContent("Preload Frame Count"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_LoopSequence"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_AutoPlay"),
                new GUIContent("Auto Play", "Automatically begin playback once attachments and frames have finished loading. Uncheck to start paused."));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("World Alignment", EditorStyles.boldLabel);
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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DepthEnabled"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DriveReplayCamera"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ReplayTargetOverride"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PositionScale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_PositionOffset"));

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
