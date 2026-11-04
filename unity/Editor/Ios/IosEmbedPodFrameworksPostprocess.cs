#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace UnityCi.Editor.Ios
{
    internal static class IosEmbedPodFrameworksPostprocess
    {
        private const string PhaseName = "Embed Dynamic Pod Frameworks";

        // Pods are linked into UnityFramework, which cannot embed frameworks, so dynamic vendored pod xcframeworks
        // (Amazon DTBiOSSDK, Ogury, StartApp, TaurusX...) never reach the app and dyld fails at launch.
        // Xcode already embeds dynamic SPM frameworks, so anything already in the app's Frameworks folder is skipped.
        private const string Script = @"set -e
DEST=""${TARGET_BUILD_DIR}/${FRAMEWORKS_FOLDER_PATH}""
mkdir -p ""$DEST""
find ""${SRCROOT}/Pods"" -name '*.xcframework' -type d -prune | while read -r XCF; do
  for SLICE in ""$XCF""/ios-*; do
    case ""$SLICE"" in *maccatalyst*) continue ;; esac
    case ""$SLICE"" in
      *simulator*) [ ""$PLATFORM_NAME"" = iphonesimulator ] || continue ;;
      *) [ ""$PLATFORM_NAME"" = iphoneos ] || continue ;;
    esac
    for FW in ""$SLICE""/*.framework; do
      [ -d ""$FW"" ] || continue
      NAME=$(basename ""$FW"" .framework)
      [ -e ""$DEST/$NAME.framework"" ] && continue
      file -b ""$FW/$NAME"" | grep -q 'dynamically linked' || continue
      echo ""Embedding $NAME.framework""
      rsync -a --exclude Headers --exclude PrivateHeaders --exclude Modules ""$FW"" ""$DEST/""
      if [ -n ""$EXPANDED_CODE_SIGN_IDENTITY"" ] && [ ""$CODE_SIGNING_ALLOWED"" != NO ]; then
        codesign --force --sign ""$EXPANDED_CODE_SIGN_IDENTITY"" --preserve-metadata=identifier,entitlements ""$DEST/$NAME.framework""
      fi
    done
  done
done
";

        [PostProcessBuild(100)]
        private static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS || !UnityCiSettings.instance.embedDynamicPodFrameworks) return;

            var projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            if (project.WriteToString().Contains(PhaseName)) return;

            project.AddShellScriptBuildPhase(project.GetUnityMainTargetGuid(), PhaseName, "/bin/sh", Script);
            project.WriteToFile(projectPath);
        }
    }
}
#endif
