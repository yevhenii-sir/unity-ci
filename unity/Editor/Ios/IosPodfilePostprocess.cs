#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace UnityCi.Editor.Ios
{
    internal static class IosPodfilePostprocess
    {
        // Xcode 27 rejects deployment targets below 15.0, but many ad SDK pods still declare 10.0-14.0.
        // Order 45: after EDM4U generates the Podfile (40) and before it runs "pod install" (50).
        [PostProcessBuild(45)]
        private static void OnPostprocessBuild(BuildTarget target, string path)
        {
            var minIosVersion = UnityCiSettings.instance.minIosVersion;
            if (target != BuildTarget.iOS || string.IsNullOrEmpty(minIosVersion)) return;

            var podfilePath = Path.Combine(path, "Podfile");
            if (!File.Exists(podfilePath)) return;

            File.AppendAllText(podfilePath, $@"
post_install do |installer|
  installer.pods_project.targets.each do |target|
    target.build_configurations.each do |config|
      if config.build_settings['IPHONEOS_DEPLOYMENT_TARGET'].to_f < {minIosVersion}
        config.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = '{minIosVersion}'
      end
    end
  end
end
");
        }
    }
}
#endif
