using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace UnityCi.Editor
{
    public enum BuildConfig
    {
        Debug,
        Release
    }

    public static class BuildUtility
    {
        private const string ExcludedFolderSuffix = "_ExcludedFromBuild";

        public static bool TryParseVersion(string version, out int major, out int minor, out int patch, out string error)
        {
            major = minor = patch = 0;
            var match = Regex.Match(version ?? "", @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})$");
            if (!match.Success)
            {
                error = $"Version '{version}' must be MAJOR.MINOR.PATCH, each 0-999 (e.g. 1.0.1 -> version code 1000001)";
                return false;
            }

            major = int.Parse(match.Groups[1].Value);
            minor = int.Parse(match.Groups[2].Value);
            patch = int.Parse(match.Groups[3].Value);
            error = null;
            return true;
        }

        public static int VersionCode(int major, int minor, int patch) => major * 1000000 + minor * 1000 + patch;

        public static string[] EnabledScenes() =>
            EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        public static Dictionary<LogType, StackTraceLogType> ApplyStackTraces(BuildConfig config)
        {
            var original = Enum.GetValues(typeof(LogType)).Cast<LogType>()
                .ToDictionary(t => t, PlayerSettings.GetStackTraceLogType);
            var verbose = config == BuildConfig.Debug;
            PlayerSettings.SetStackTraceLogType(LogType.Log, verbose ? StackTraceLogType.ScriptOnly : StackTraceLogType.None);
            PlayerSettings.SetStackTraceLogType(LogType.Warning, verbose ? StackTraceLogType.ScriptOnly : StackTraceLogType.None);
            PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
            PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
            PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
            return original;
        }

        public static void RestoreStackTraces(Dictionary<LogType, StackTraceLogType> original)
        {
            foreach (var pair in original)
                PlayerSettings.SetStackTraceLogType(pair.Key, pair.Value);
        }

        public static List<string> ExcludeFolders(IEnumerable<string> folders)
        {
            var excluded = new List<string>();
            foreach (var folder in folders.Where(AssetDatabase.IsValidFolder))
            {
                var error = AssetDatabase.MoveAsset(folder, folder + ExcludedFolderSuffix);
                if (string.IsNullOrEmpty(error))
                    excluded.Add(folder);
                else
                    Debug.LogWarning($"[Build] Could not exclude {folder}: {error}");
            }
            return excluded;
        }

        public static void RestoreFolders(IEnumerable<string> folders)
        {
            foreach (var folder in folders)
            {
                var error = AssetDatabase.MoveAsset(folder + ExcludedFolderSuffix, folder);
                if (!string.IsNullOrEmpty(error))
                    Debug.LogError($"[Build] Could not restore {folder}: {error}. Rename {folder}{ExcludedFolderSuffix} back manually.");
            }
        }

        public static void RestoreActiveProfile(BuildProfile profile, BuildTarget target)
        {
            if (BuildProfile.GetActiveBuildProfile() != profile)
                BuildProfile.SetActiveBuildProfile(profile);

            if (profile == null && EditorUserBuildSettings.activeBuildTarget != target)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target);
        }

        public static bool Fail(string message)
        {
            Debug.LogError($"[Build] {message}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Build failed", message, "OK");
            return false;
        }

        public static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
