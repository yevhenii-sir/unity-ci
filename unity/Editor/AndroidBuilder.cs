using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityCi.Editor
{
    public static class AndroidBuilder
    {
        public const string OutputRoot = "Builds/Android";
        public const string LastBuildFile = OutputRoot + "/last-build.txt";
        private const string SecretsFile = "UserSettings/AndroidKeystore.json";

        private static UnityCiSettings Settings => UnityCiSettings.instance;

        [MenuItem("Build/Android Debug (APK)", priority = 1)]
        private static void BuildDebugMenu() => Build(BuildConfig.Debug, false, false);

        [MenuItem("Build/Android Debug (APK) + Run on Device", priority = 2)]
        private static void BuildDebugAndRunMenu() => Build(BuildConfig.Debug, false, true);

        [MenuItem("Build/Android Release (AAB)", priority = 20)]
        private static void BuildReleaseMenu() => Build(BuildConfig.Release, false, false);

        [MenuItem("Build/Android Release (AAB, +1 Patch Version)", priority = 21)]
        private static void BuildReleaseBumpMenu() => Build(BuildConfig.Release, true, false);

        [MenuItem("Build/Bump Patch Version", priority = 40)]
        private static void BumpPatchMenu()
        {
            if (TryBumpPatch(out var error))
                Debug.Log($"[Build] Version {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode})");
            else
                BuildUtility.Fail(error);
        }

        [MenuItem("Build/Open Builds Folder", priority = 41)]
        private static void OpenBuildsFolder()
        {
            Directory.CreateDirectory(OutputRoot);
            EditorUtility.RevealInFinder(OutputRoot + "/");
        }

        public static void BuildFromCommandLine()
        {
            var configArg = BuildUtility.GetArgument("-buildConfig");
            var config = configArg != null
                ? (BuildConfig)Enum.Parse(typeof(BuildConfig), configArg, true)
                : BuildConfig.Debug;
            var bump = Environment.GetCommandLineArgs().Contains("-bumpPatch");

            var success = Build(config, bump, false);
            EditorApplication.Exit(success ? 0 : 1);
        }

        public static bool Build(BuildConfig config, bool bumpPatch, bool runOnDevice)
        {
            var previousProfile = BuildProfile.GetActiveBuildProfile();
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousAppBundle = EditorUserBuildSettings.buildAppBundle;
            try
            {
                return BuildPlayer(config, bumpPatch, runOnDevice);
            }
            finally
            {
                EditorUserBuildSettings.buildAppBundle = previousAppBundle;
                BuildUtility.RestoreActiveProfile(previousProfile, previousTarget);
            }
        }

        private static bool BuildPlayer(BuildConfig config, bool bumpPatch, bool runOnDevice)
        {
            var profilePath = $"{Settings.buildProfilesFolder}/Android {config}.asset";
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(profilePath);
            if (profile == null && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                return BuildUtility.Fail("Could not switch to Android. Is Android Build Support installed?");

            if (!EnsureKeystorePasswords())
                return BuildUtility.Fail($"Keystore passwords missing. Enter them once in Player Settings > Publishing Settings and rebuild from the Build menu (they will be saved to {SecretsFile}), or set ANDROID_KEYSTORE_PASS / ANDROID_KEYALIAS_PASS.");

            EnsureGradleHeap();

            var versionError = "";
            if (bumpPatch ? !TryBumpPatch(out versionError) : !TrySyncVersionCode(out versionError))
                return BuildUtility.Fail(versionError);

            var isRelease = config == BuildConfig.Release;
            var outputPath = GetOutputPath(config, isRelease ? "aab" : "apk");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var stackTraces = BuildUtility.ApplyStackTraces(config);
            var startedAt = DateTime.Now;
            var excludedFolders = isRelease ? BuildUtility.ExcludeFolders(Settings.releaseExcludedFolders) : new List<string>();
            BuildReport report;
            try
            {
                var options = runOnDevice ? BuildOptions.AutoRunPlayer : BuildOptions.None;
                if (profile != null)
                {
                    report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
                    {
                        buildProfile = profile,
                        locationPathName = outputPath,
                        options = options
                    });
                }
                else
                {
                    EditorUserBuildSettings.buildAppBundle = isRelease;
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = BuildUtility.EnabledScenes(),
                        target = BuildTarget.Android,
                        targetGroup = BuildTargetGroup.Android,
                        locationPathName = outputPath,
                        extraScriptingDefines = isRelease ? Settings.releaseDefines : null,
                        options = isRelease ? options : options | BuildOptions.Development
                    });
                }
            }
            finally
            {
                BuildUtility.RestoreStackTraces(stackTraces);
                BuildUtility.RestoreFolders(excludedFolders);
            }

            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                return BuildUtility.Fail($"{config} build {summary.result}: {summary.totalErrors} errors. See Console / build log.");

            File.WriteAllText(LastBuildFile, Path.GetFullPath(outputPath));
            WriteSizeReport(config, outputPath);
            Debug.Log($"[Build] {config} OK: {outputPath} ({summary.totalSize / (1024f * 1024f):F1} MB, {summary.totalTime:mm\\:ss})");

            if (isRelease && Settings.uploadCrashlyticsSymbols)
                UploadCrashlyticsSymbols(Path.GetDirectoryName(outputPath), startedAt);

            if (!Application.isBatchMode && !runOnDevice)
                EditorUtility.RevealInFinder(outputPath);

            return true;
        }

        private static bool TryBumpPatch(out string error)
        {
            if (!BuildUtility.TryParseVersion(PlayerSettings.bundleVersion, out var major, out var minor, out var patch, out error))
                return false;

            PlayerSettings.bundleVersion = $"{major}.{minor}.{patch + 1}";
            return TrySyncVersionCode(out error);
        }

        private static bool TrySyncVersionCode(out string error)
        {
            if (!BuildUtility.TryParseVersion(PlayerSettings.bundleVersion, out var major, out var minor, out var patch, out error))
                return false;

            var code = BuildUtility.VersionCode(major, minor, patch);
            if (PlayerSettings.Android.bundleVersionCode != code)
            {
                PlayerSettings.Android.bundleVersionCode = code;
                AssetDatabase.SaveAssets();
            }
            return true;
        }

        private static void WriteSizeReport(BuildConfig config, string outputPath)
        {
            const double mb = 1024 * 1024;
            var sb = new StringBuilder();
            sb.AppendLine($"{PlayerSettings.productName} {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode}) {config}, {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"File: {Path.GetFileName(outputPath)}  {new FileInfo(outputPath).Length / mb:F1} MB");
            sb.AppendLine();

            sb.AppendLine("== Package contents (compressed MB | raw MB) ==");
            if (outputPath.EndsWith(".aab"))
                sb.AppendLine("BUNDLE-METADATA is not downloaded by players; texture packs are delivered in one format per device.");
            using (var zip = ZipFile.OpenRead(outputPath))
            {
                var groups = zip.Entries
                    .GroupBy(e => GetPackageGroup(e.FullName))
                    .Select(g => (name: g.Key, packed: g.Sum(e => e.CompressedLength), raw: g.Sum(e => e.Length)))
                    .OrderByDescending(g => g.packed);
                foreach (var g in groups.Where(g => g.packed >= 100 * 1024))
                    sb.AppendLine($"{g.packed / mb,8:F1} | {g.raw / mb,8:F1}  {g.name}");
            }

            var reportPath = Path.ChangeExtension(outputPath, ".report.txt");
            File.WriteAllText(reportPath, sb.ToString());
            Debug.Log($"[Build] Size report: {reportPath}");
        }

        private static string GetPackageGroup(string entry)
        {
            var parts = entry.Split('/');
            if (entry.EndsWith(".so")) return $"{(parts[0] == "lib" ? "" : parts[0] + "/")}lib {parts[parts.Length - 1]}";
            if (entry.EndsWith(".dex")) return $"{(parts.Length > 1 ? parts[0] + "/" : "")}dex";
            if (parts[0] == "BUNDLE-METADATA" && parts.Length > 1) return $"BUNDLE-METADATA/{parts[1]}";
            if (parts.Length > 2 && parts[1] == "assets") return $"{parts[0]}/assets";
            return parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : parts[0];
        }

        private static string GetOutputPath(BuildConfig config, string extension)
        {
            var name = Regex.Replace(PlayerSettings.productName, "[^A-Za-z0-9]", "");
            var suffix = config == BuildConfig.Debug ? "_dev" : "";
            return $"{OutputRoot}/{config}/{name}_{PlayerSettings.bundleVersion}_{PlayerSettings.Android.bundleVersionCode}{suffix}.{extension}";
        }

        public static bool EnsureKeystorePasswords()
        {
            if (!PlayerSettings.Android.useCustomKeystore)
                return true;

            if (HasPasswords())
            {
                SaveSecrets();
                return true;
            }

            var secrets = File.Exists(SecretsFile)
                ? JsonUtility.FromJson<KeystoreSecrets>(File.ReadAllText(SecretsFile))
                : new KeystoreSecrets();

            PlayerSettings.Android.keystorePass = Environment.GetEnvironmentVariable("ANDROID_KEYSTORE_PASS") ?? secrets.keystorePass;
            PlayerSettings.Android.keyaliasPass = Environment.GetEnvironmentVariable("ANDROID_KEYALIAS_PASS") ?? secrets.keyaliasPass;
            return HasPasswords();
        }

        private static bool HasPasswords() =>
            !string.IsNullOrEmpty(PlayerSettings.Android.keystorePass) &&
            !string.IsNullOrEmpty(PlayerSettings.Android.keyaliasPass);

        private static void SaveSecrets()
        {
            var secrets = new KeystoreSecrets
            {
                keystorePass = PlayerSettings.Android.keystorePass,
                keyaliasPass = PlayerSettings.Android.keyaliasPass
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SecretsFile));
            File.WriteAllText(SecretsFile, JsonUtility.ToJson(secrets, true));
        }

        private static void UploadCrashlyticsSymbols(string outputFolder, DateTime builtAfter)
        {
            var zip = new DirectoryInfo(outputFolder).GetFiles("*.symbols.zip")
                .Where(f => f.LastWriteTime >= builtAfter)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (zip == null)
            {
                Debug.LogWarning("[Build] symbols.zip not found, Crashlytics upload skipped");
                return;
            }

            var appId = GetFirebaseAppId();
            if (string.IsNullOrEmpty(appId))
            {
                Debug.LogWarning($"[Build] Firebase app id for {PlayerSettings.applicationIdentifier} not found in {Settings.googleServicesFile}, Crashlytics upload skipped");
                return;
            }

            var symbolsDir = Path.Combine(Path.GetTempPath(), "crashlytics-symbols", Path.GetFileNameWithoutExtension(zip.Name));
            if (Directory.Exists(symbolsDir))
                Directory.Delete(symbolsDir, true);
            ZipFile.ExtractToDirectory(zip.FullName, symbolsDir);
            foreach (var file in Directory.GetFiles(symbolsDir, "*.so.sym", SearchOption.AllDirectories))
                File.Move(file, file.Substring(0, file.Length - ".sym".Length));

            var command = $"firebase crashlytics:symbols:upload --app={appId} \"{symbolsDir}\"";
            var windows = Application.platform == RuntimePlatform.WindowsEditor;
            EditorUtility.DisplayProgressBar("Crashlytics", "Uploading native symbols...", 0.5f);
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = windows ? "cmd.exe" : "/bin/bash",
                    Arguments = windows ? $"/c {command}" : $"-lc '{command}'",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                    Debug.Log($"[Build] Crashlytics symbols uploaded\n{output}");
                else
                    Debug.LogWarning($"[Build] Crashlytics symbols upload failed (install Firebase CLI and run 'firebase login'):\n{output}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Build] Crashlytics symbols upload failed: {e.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static string GetFirebaseAppId()
        {
            if (!File.Exists(Settings.googleServicesFile))
                return null;

            var services = JsonUtility.FromJson<GoogleServices>(File.ReadAllText(Settings.googleServicesFile));
            return services.client?
                .FirstOrDefault(c => c.client_info.android_client_info.package_name == PlayerSettings.applicationIdentifier)?
                .client_info.mobilesdk_app_id;
        }

        // Via reflection: UnityEditor.Android is missing when the Android module is not installed (e.g. iOS-only Mac).
        private static void EnsureGradleHeap()
        {
            var settings = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Android.AndroidExternalToolsSettings"))
                .FirstOrDefault(t => t != null);
            var heap = settings?.GetProperty("maxJvmHeapSize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (heap != null && (int)heap.GetValue(null) < Settings.minGradleHeapMb)
                heap.SetValue(null, Settings.minGradleHeapMb);
        }

        [Serializable]
        private class KeystoreSecrets
        {
            public string keystorePass;
            public string keyaliasPass;
        }

        [Serializable]
        private class GoogleServices
        {
            public GoogleServicesClient[] client;
        }

        [Serializable]
        private class GoogleServicesClient
        {
            public GoogleServicesClientInfo client_info;
        }

        [Serializable]
        private class GoogleServicesClientInfo
        {
            public string mobilesdk_app_id;
            public AndroidClientInfo android_client_info;
        }

        [Serializable]
        private class AndroidClientInfo
        {
            public string package_name;
        }
    }
}
