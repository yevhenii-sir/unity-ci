using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCi.Editor
{
    public static class WorkflowFiles
    {
        public const string WorkflowPath = ".github/workflows/release.yml";
        public const string ConfigPath = ".github/unity-ci.json";
        private const string WorkflowRef = "yevhenii-sir/unity-ci/.github/workflows/release.yml@v1";

        public static bool Write(UnityCiSettings settings)
        {
            var errors = Validate(settings).ToList();
            if (errors.Count > 0)
                return BuildUtility.Fail("CI setup is incomplete:\n- " + string.Join("\n- ", errors));

            Directory.CreateDirectory(Path.GetDirectoryName(WorkflowPath));
            File.WriteAllText(WorkflowPath, Workflow(settings).Replace("\r\n", "\n"));
            File.WriteAllText(ConfigPath, Config(settings).Replace("\r\n", "\n"));
            Debug.Log($"[Build] Wrote {WorkflowPath} and {ConfigPath}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Unity CI", $"Wrote {WorkflowPath} and {ConfigPath}. Commit and push them.", "OK");
            return true;
        }

        private static IEnumerable<string> Validate(UnityCiSettings settings)
        {
            if (!settings.ciAndroid && !settings.ciIos)
                yield return "enable Android or iOS";
            if (settings.ciAndroid && PlayerSettings.Android.useCustomKeystore && string.IsNullOrEmpty(settings.secretsPrefix))
                yield return "set the secrets prefix (the RunPod secrets with the keystore passwords)";
            if (settings.ciAndroid && settings.playUpload && string.IsNullOrEmpty(settings.secretsPrefix))
                yield return "set the secrets prefix (the RunPod secret with the Play service account)";
            if (settings.ciIos && string.IsNullOrEmpty(PlayerSettings.iOS.appleDeveloperTeamID))
                yield return "set the Apple Team ID in Player Settings > iOS > Other Settings";
        }

        private static string Workflow(UnityCiSettings settings)
        {
            var platforms = settings.ciAndroid && settings.ciIos ? "[android, ios, both]"
                : settings.ciIos ? "[ios]" : "[android]";
            var defaultPlatform = settings.ciAndroid ? "android" : "ios";
            return $@"name: Release
run-name: ""${{{{ inputs.config }}}} ${{{{ inputs.platform }}}} ${{{{ inputs.version || inputs.bump }}}}""

# Written by Build > CI Setup (com.yevhenii.unity-ci); builds through yevhenii-sir/unity-ci.
on:
  workflow_dispatch:
    inputs:
      platform:
        description: ""Platform""
        type: choice
        options: {platforms}
        default: {defaultPlatform}
      config:
        description: ""Build (Debug is Android only)""
        type: choice
        options: [Release, Debug]
        default: Release
      bump:
        description: ""Version bump (Release)""
        type: choice
        options: [patch, minor, major]
        default: patch
      version:
        description: ""Exact version X.Y.Z, overrides the bump (Release)""
        required: false
        default: """"
      play_upload:
        description: ""Upload to Google Play (Android Release)""
        type: boolean
        default: true
      track:
        description: ""Play track""
        type: choice
        options: [internal, alpha, beta, production]
        default: internal
      status:
        description: ""Play release status""
        type: choice
        options: [draft, completed]
        default: draft

permissions:
  contents: write

concurrency:
  group: release
  cancel-in-progress: false

jobs:
  release:
    uses: {WorkflowRef}
    with:
      platform: ${{{{ inputs.platform }}}}
      config: ${{{{ inputs.config }}}}
      bump: ${{{{ inputs.bump }}}}
      version: ${{{{ inputs.version }}}}
      play_upload: ${{{{ inputs.play_upload }}}}
      track: ${{{{ inputs.track }}}}
      status: ${{{{ inputs.status }}}}
    secrets: inherit
";
        }

        private static string Config(UnityCiSettings settings)
        {
            var sections = new List<string>();
            if (settings.ciAndroid)
                sections.Add($@"  ""android"": {{
    ""build_method"": ""{typeof(AndroidBuilder).FullName}.{nameof(AndroidBuilder.BuildFromCommandLine)}"",
    ""build_args"": [""-buildConfig"", ""{{config}}""],
    ""play_upload"": {(settings.playUpload ? "true" : "false")}
  }}");
            if (settings.ciIos)
                sections.Add($@"  ""ios"": {{
    ""build_method"": ""{typeof(IosBuilder).FullName}.{nameof(IosBuilder.BuildFromCommandLine)}"",
    ""team_id"": ""{PlayerSettings.iOS.appleDeveloperTeamID}""
  }}");
            return $@"{{
  ""secrets_prefix"": ""{settings.secretsPrefix.ToUpperInvariant()}"",
  ""timeout_min"": {settings.timeoutMin},
{string.Join(",\n", sections)}
}}
";
        }
    }
}
