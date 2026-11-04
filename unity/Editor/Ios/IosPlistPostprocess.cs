#if UNITY_IOS
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace UnityCi.Editor.Ios
{
    internal static class IosPlistPostprocess
    {
        private const string BaseLanguage = "en";
        private const string TrackingKey = "NSUserTrackingUsageDescription";

        // Declared localizations drive the "Languages" line on the App Store page and the language of system
        // dialogs (ATT, StoreKit); the ATT purpose string is localized through <lang>.lproj/InfoPlist.strings.
        private static readonly Dictionary<string, string> TrackingTexts = new()
        {
            ["en"] = "This lets us show you ads that better match your interests. Ads help support the game.",
            ["ru"] = "Это позволит показывать рекламу, которая лучше соответствует вашим интересам. Реклама помогает развивать игру.",
            ["uk"] = "Це дозволить показувати рекламу, яка краще відповідає вашим інтересам. Реклама допомагає розвивати гру.",
            ["es"] = "Esto nos permite mostrarte anuncios más acordes con tus intereses. Los anuncios ayudan a mantener el juego.",
            ["pt"] = "Isso nos permite mostrar anúncios mais adequados aos seus interesses. Os anúncios ajudam a manter o jogo.",
            ["fr"] = "Cela nous permet de vous montrer des publicités plus adaptées à vos centres d'intérêt. La publicité aide à faire vivre le jeu.",
            ["de"] = "So können wir dir Werbung zeigen, die besser zu deinen Interessen passt. Werbung hilft, das Spiel weiterzuentwickeln.",
            ["it"] = "Così possiamo mostrarti annunci più in linea con i tuoi interessi. Gli annunci aiutano a sostenere il gioco.",
            ["pl"] = "Dzięki temu pokażemy Ci reklamy lepiej dopasowane do Twoich zainteresowań. Reklamy pomagają rozwijać grę.",
            ["tr"] = "Bu sayede ilgi alanlarınıza daha uygun reklamlar gösterebiliriz. Reklamlar oyunun gelişmesine destek olur.",
            ["id"] = "Ini memungkinkan kami menampilkan iklan yang lebih sesuai dengan minat Anda. Iklan membantu mendukung game ini.",
            ["hi"] = "इससे हम आपकी रुचि के अनुसार बेहतर विज्ञापन दिखा पाएँगे। विज्ञापन गेम को आगे बढ़ाने में मदद करते हैं।",
            ["ar"] = "يتيح لنا ذلك عرض إعلانات أكثر ملاءمة لاهتماماتك. تساعد الإعلانات في دعم اللعبة.",
            ["zh-Hans"] = "这样我们可以向你展示更符合你兴趣的广告。广告有助于支持游戏的持续开发。",
            ["ja"] = "あなたの興味により合った広告を表示するために使用します。広告はゲームの運営を支えています。",
            ["ko"] = "관심사에 더 잘 맞는 광고를 보여 드리는 데 사용됩니다. 광고는 게임 운영에 도움이 됩니다.",
            ["cs"] = "Díky tomu vám můžeme zobrazovat reklamy, které lépe odpovídají vašim zájmům. Reklamy pomáhají hru dál vyvíjet.",
            ["el"] = "Έτσι μπορούμε να σας δείχνουμε διαφημίσεις που ταιριάζουν καλύτερα στα ενδιαφέροντά σας. Οι διαφημίσεις στηρίζουν το παιχνίδι.",
            ["sv"] = "Det gör att vi kan visa annonser som passar dina intressen bättre. Annonser hjälper till att utveckla spelet.",
            ["th"] = "ช่วยให้เราแสดงโฆษณาที่ตรงกับความสนใจของคุณมากขึ้น โฆษณาช่วยสนับสนุนการพัฒนาเกม",
            ["ro"] = "Astfel îți putem afișa reclame mai potrivite intereselor tale. Reclamele ajută la dezvoltarea jocului.",
        };

        [PostProcessBuild(100)]
        private static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;

            var settings = UnityCiSettings.instance;
            var plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            // Home screen name: the brand before the store subtitle ("Magnet Rings: Ball Sort 3D" -> "Magnet Rings")
            if (settings.shortDisplayName)
                plist.root.SetString("CFBundleDisplayName", PlayerSettings.productName.Split(':')[0].Trim());
            if (settings.noNonExemptEncryption)
                plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
            if (settings.trackingDescription)
            {
                plist.root.SetString(TrackingKey, TrackingTexts[BaseLanguage]);
                var languages = plist.root.CreateArray("CFBundleLocalizations");
                foreach (var code in TrackingTexts.Keys)
                    languages.AddString(code);
            }
            plist.WriteToFile(plistPath);

            if (settings.trackingDescription)
                AddLocalizations(path);
        }

        private static void AddLocalizations(string path)
        {
            var projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            project.ClearKnownRegions();
            project.AddKnownRegion("Base");

            var utf8 = new UTF8Encoding(false);
            foreach (var (code, text) in TrackingTexts)
            {
                project.AddKnownRegion(code);
                var folder = $"{code}.lproj";
                Directory.CreateDirectory(Path.Combine(path, folder));
                File.WriteAllText(Path.Combine(path, folder, "InfoPlist.strings"),
                    $"\"{TrackingKey}\" = \"{text.Replace("\"", "\\\"")}\";\n", utf8);
                // Unity's trampoline variant group already has an "en" entry (a second one breaks the Xcode build).
                // AddLocaleVariantFile moves that group to the project root, so its relative en.lproj path must
                // exist there too, which the file written above provides.
                if (code == BaseLanguage) continue;
                project.AddLocaleVariantFile("InfoPlist.strings", code, $"{folder}/InfoPlist.strings");
            }

            project.WriteToFile(projectPath);
        }
    }
}
#endif
