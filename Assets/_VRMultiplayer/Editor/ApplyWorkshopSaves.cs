using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah Atolyesi'nde CIHAZDA yapilan kayitlari profillere isler.
    ///
    /// Neden gerekli: profil bir ScriptableObject. Editorde Play modunda degistirince asset
    /// gercekten degisir, ama CIHAZDA degisiklik yalnizca calisan uygulamada yasar. Atolye
    /// her kaydi persistentDataPath/GripOlcum/atolye.md dosyasina da yazar; dosya adb ile
    /// cekilip proje kokune konur, bu menu de icerigi profillere dagitir.
    ///
    /// Dosyayi cekmek (adb PATH'te degil, Unity'nin Android SDK'sinda):
    ///   adb pull /sdcard/Android/data/&lt;paket&gt;/files/GripOlcum/atolye.md
    ///
    /// Bicim (satir basina bir kayit, ayni profil birden fazla kez gecebilir - SONUNCU gecerli):
    ///   ProfilAdi|main|posX|posY|posZ|eulerX|eulerY|eulerZ|kivrimlar
    /// Son alan bes parmagin kivrimi (bas,isaret,orta,yuzuk,serce), virgulle ayrik.
    /// Bos birakilabilir - o zaman profildeki kivrimlara dokunulmaz.
    /// </summary>
    public static class ApplyWorkshopSaves
    {
        static string LogPath => Path.Combine(Application.dataPath, "..", "atolye.md");

        [MenuItem("Tools/VR Multiplayer/52. Atolye Kayitlarini Profillere Uygula")]
        public static void Apply()
        {
            string path = Path.GetFullPath(LogPath);
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("Atolye kaydi yok",
                    "atolye.md bulunamadi:\n" + path +
                    "\n\nCihazdan adb pull ile cekip proje koküne koy.", "Tamam");
                return;
            }

            int applied = 0, skipped = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split('|');
                if (p.Length < 8) { skipped++; continue; }

                var profile = Find(p[0]);
                if (profile == null) { skipped++; Debug.LogWarning("[Atolye] profil yok: " + p[0]); continue; }

                if (!TryVec(p, 2, out Vector3 pos) || !TryVec(p, 5, out Vector3 eul)) { skipped++; continue; }

                float[] curls = p.Length > 8 ? ParseCurls(p[8]) : null;

                bool support = p[1].Trim().ToLowerInvariant() == "support";
                // HandPose bir STRUCT: kopyaya yazip profildeki alana GERI koymak sart.
                var hand = support ? profile.supportHand : profile.mainHand;
                hand.fpWristLocalPosition = pos;
                hand.fpWristLocalEuler = eul;
                if (curls != null) hand.fpCurls = curls;
                if (support) profile.supportHand = hand; else profile.mainHand = hand;

                EditorUtility.SetDirty(profile);
                applied++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[Atolye] " + applied + " kayit islendi, " + skipped + " atlandi. Dosya: " + path);
        }

        /// <summary>Bes parmagin kivrimi. Eksik/bozuk alan sessizce yok sayilir - yarim
        /// yazilmis bir satir yuzunden profildeki saglam degeri bozmayalim.</summary>
        static float[] ParseCurls(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(',');
            if (parts.Length != 5) return null;
            var v = new float[5];
            for (int i = 0; i < 5; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            return v;
        }

        static bool TryVec(string[] p, int i, out Vector3 v)
        {
            v = Vector3.zero;
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(p[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(p[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        static WeaponGripProfile Find(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var pr = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (pr != null && pr.name == name) return pr;
            }
            return null;
        }
    }
}
