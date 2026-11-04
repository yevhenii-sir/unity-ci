using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCi.Editor
{
    internal class UnityCiSettingsProvider : SettingsProvider
    {
        private const string SettingsPath = "Project/Unity CI";
        private SerializedObject serialized;

        private UnityCiSettingsProvider() : base(SettingsPath, SettingsScope.Project) { }

        [SettingsProvider]
        private static SettingsProvider Create() => new UnityCiSettingsProvider
        {
            keywords = new HashSet<string> { "build", "ci", "release", "github", "runpod" }
        };

        [MenuItem("Build/CI Setup...", priority = 60)]
        private static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement) =>
            serialized = new SerializedObject(UnityCiSettings.instance);

        public override void OnGUI(string searchContext)
        {
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            var property = serialized.GetIterator();
            property.NextVisible(true);
            while (property.NextVisible(false))
                EditorGUILayout.PropertyField(property, true);
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                UnityCiSettings.instance.SaveSettings();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"Writes {WorkflowFiles.WorkflowPath} and {WorkflowFiles.ConfigPath}. Commit them, add the repo secrets " +
                "(RUNPOD_API_KEY; for iOS ASC_KEY_ID, ASC_ISSUER_ID, ASC_KEY_P8, IOS_DIST_P12, IOS_DIST_P12_PASSWORD) " +
                "and run Actions > Release.", MessageType.Info);
            if (GUILayout.Button("Write GitHub workflow files"))
                WorkflowFiles.Write(UnityCiSettings.instance);
        }
    }
}
