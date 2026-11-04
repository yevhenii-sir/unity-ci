using UnityEditor;
using UnityEngine;

namespace UnityCi.Editor
{
    [FilePath("ProjectSettings/UnityCiSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class UnityCiSettings : ScriptableSingleton<UnityCiSettings>
    {
        [Header("Build")]
        [Tooltip("Folder with the 'Android Debug' and 'Android Release' build profiles. Without them Android builds use the scenes from Build Settings.")]
        public string buildProfilesFolder = "Assets/Settings/Build Profiles";
        [Tooltip("Scripting defines of Release builds without a build profile (iOS, Android without profiles).")]
        public string[] releaseDefines = { };
        [Tooltip("Folders moved out of the project during Release builds, e.g. debug-only Resources.")]
        public string[] releaseExcludedFolders = { };
        public int minGradleHeapMb = 8192;
        [Tooltip("Upload native symbols of Android Release builds with the Firebase CLI.")]
        public bool uploadCrashlyticsSymbols;
        public string googleServicesFile = "Assets/google-services.json";

        [Header("iOS")]
        [Tooltip("Raise pod deployment targets to this version (empty: leave them).")]
        public string minIosVersion = "15.0";
        [Tooltip("Embed dynamic xcframeworks of pods into the app.")]
        public bool embedDynamicPodFrameworks = true;
        [Tooltip("Home screen name: the product name before ':'.")]
        public bool shortDisplayName = true;
        public bool noNonExemptEncryption = true;
        [Tooltip("Localized App Tracking Transparency purpose string; its languages become the app's declared localizations.")]
        public bool trackingDescription = true;

        [Header("CI")]
        [Tooltip("Prefix of the project's RunPod secrets (PREFIX_KEYSTORE_PASS, ...).")]
        public string secretsPrefix = "";
        public bool ciAndroid = true;
        public bool ciIos = true;
        [Tooltip("Upload Android Release builds to Google Play.")]
        public bool playUpload = true;
        public int timeoutMin = 110;

        private void OnEnable() => hideFlags &= ~HideFlags.NotEditable;

        public void SaveSettings() => Save(true);
    }
}
