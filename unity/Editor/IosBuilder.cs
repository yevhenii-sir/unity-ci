using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCi.Editor
{
    // Exports the release Xcode project. Build number = Android version code + upload counter:
    // 1.0.11 -> 1000011.1, a re-upload of the same version -> 1000011.2, the next version starts at .1 again.
    // CI sets IOS_BUILD_NUMBER instead, because its counter change is never committed.
    public static class IosBuilder
    {
        public const string OutputRoot = "Builds/iOS";
        public const string OutputPath = OutputRoot + "/Xcode";
        public const string LastBuildFile = OutputRoot + "/last-build.txt";

        [MenuItem("Build/iOS Release (Xcode Project)", priority = 30)]
        private static void BuildReleaseMenu() => Build();

        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        public static bool Build()
        {
            var previousProfile = BuildProfile.GetActiveBuildProfile();
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            try
            {
                return BuildRelease();
            }
            finally
            {
                BuildUtility.RestoreActiveProfile(previousProfile, previousTarget);
            }
        }

        private static bool BuildRelease()
        {
            if (!BuildUtility.TryParseVersion(PlayerSettings.bundleVersion, out var major, out var minor, out var patch, out var error))
                return BuildUtility.Fail(error);

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS))
                return BuildUtility.Fail("Could not switch to iOS. Is iOS Build Support installed?");

            var code = BuildUtility.VersionCode(major, minor, patch);
            var ciBuildNumber = Environment.GetEnvironmentVariable("IOS_BUILD_NUMBER");
            PlayerSettings.iOS.buildNumber = string.IsNullOrEmpty(ciBuildNumber)
                ? NextBuildNumber(code, PlayerSettings.iOS.buildNumber)
                : ciBuildNumber;
            AssetDatabase.SaveAssets();

            var settings = UnityCiSettings.instance;
            var stackTraces = BuildUtility.ApplyStackTraces(BuildConfig.Release);
            var excludedFolders = BuildUtility.ExcludeFolders(settings.releaseExcludedFolders);
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = BuildUtility.EnabledScenes(),
                    target = BuildTarget.iOS,
                    targetGroup = BuildTargetGroup.iOS,
                    locationPathName = OutputPath,
                    extraScriptingDefines = settings.releaseDefines,
                    options = BuildOptions.None
                });
            }
            finally
            {
                BuildUtility.RestoreStackTraces(stackTraces);
                BuildUtility.RestoreFolders(excludedFolders);
            }

            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                return BuildUtility.Fail($"iOS build {summary.result}: {summary.totalErrors} errors. See Console / build log.");

            File.WriteAllText(LastBuildFile, Path.GetFullPath(OutputPath));
            Debug.Log($"[Build] iOS {PlayerSettings.bundleVersion} ({PlayerSettings.iOS.buildNumber}) OK: {OutputPath}. " +
                      "Open Unity-iPhone.xcworkspace, Product > Archive, Distribute to App Store Connect.");
            if (!Application.isBatchMode)
                EditorUtility.RevealInFinder(OutputPath);
            return true;
        }

        private static string NextBuildNumber(int code, string current)
        {
            var match = Regex.Match(current ?? "", $@"^{code}\.(\d+)$");
            var upload = match.Success ? int.Parse(match.Groups[1].Value) + 1 : 1;
            return $"{code}.{upload}";
        }
    }
}
