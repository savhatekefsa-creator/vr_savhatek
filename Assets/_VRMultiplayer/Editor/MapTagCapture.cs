using UnityEditor;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   47/48 — plakalari haritanin tag yerlesimine ceviren menulerin EDITOR KABUGU.
    ///
    /// MANTIK BURADA DEGIL: <see cref="TagCapture"/> icinde, cunku ayni cevrim gozlukte de
    /// kosmali (yaratici modda yeni harita acarken tag kurulumu). Burasi yalnizca
    /// "diskten oku -> cevir -> diske yaz -> AssetDatabase.Refresh" yapiyor. Kopyalansaydi
    /// editor yolu ile cihaz yolu sessizce ayrisirdi.
    ///
    /// Yontemin gerekcesi, olculmus sayilar (6,1 cm vakasi, yaw konvansiyonunun uc bagimsiz
    /// dogrulamasi, plakanin beyaz yuzu) <see cref="TagCapture"/> ozetinde.
    /// </summary>
    public static class MapTagCapture
    {
        public const int FirstTagId = TagCapture.FirstTagId;
        public const string PlateId = TagCapture.PlateId;

        [MenuItem("Tools/VR Multiplayer/47. Plakalardan Tag Yerlesimi Uret")]
        public static void CaptureMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer",
                Capture(ConstructorSession.DefaultMapName), "Tamam");

        public static string Capture(string mapName)
        {
            var layout = MapLayout.Load(mapName);
            if (layout == null) return $"'{mapName}' okunamadi.\n\n{MapLayout.PathFor(mapName)}";

            string rapor = TagCapture.Capture(layout, out bool _);

            // Cevrim tag uretemediyse (plaka yok, izgara kurulamadi, kutuphane eksik) YAZMA:
            // bos bir tags dizisini diske basmak calisan bir yerlesimi sessizce silerdi.
            if (layout.tags == null || layout.tags.Length == 0) return rapor;

            if (!layout.Save(mapName)) return "Harita KAYDEDILEMEDI — Console'a bak.";
            AssetDatabase.Refresh();

            return $"'{mapName}' — {rapor}\n" +
                   "Bir plakayi silip yeniden koyarsan sona duser ve ID'si degisir —\n" +
                   "yukaridaki tablo bunun icin basiliyor.";
        }

        [MenuItem("Tools/VR Multiplayer/48. Haritadaki Tag'leri Kalibrasyona Ac")]
        public static void EnableMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer",
                Enable(ConstructorSession.DefaultMapName), "Tamam");

        public static string Enable(string mapName)
        {
            var layout = MapLayout.Load(mapName);
            if (layout == null) return $"'{mapName}' okunamadi.";

            string rapor = TagCapture.Enable(layout, out int acilan);
            if (acilan == 0 && (layout.tags == null || layout.tags.Length == 0)) return rapor;

            if (!layout.Save(mapName)) return "Harita KAYDEDILEMEDI — Console'a bak.";
            AssetDatabase.Refresh();
            return $"'{mapName}': {rapor}";
        }
    }
}
