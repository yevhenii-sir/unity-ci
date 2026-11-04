using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCi.Editor
{
    // Unity forgets keystore passwords on restart; reload them so any Android build is signed with the upload key.
    [InitializeOnLoad]
    public class KeystorePasswordLoader : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        static KeystorePasswordLoader() => EditorApplication.delayCall += () => AndroidBuilder.EnsureKeystorePasswords();

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android && !AndroidBuilder.EnsureKeystorePasswords())
                Debug.LogWarning("[Build] Keystore passwords missing, the build will not be signed with the release keystore");
        }
    }
}
