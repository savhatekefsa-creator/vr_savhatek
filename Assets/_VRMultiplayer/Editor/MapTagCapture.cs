using UnityEditor;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Plaka -> tag cevriminin DISK KABUGU: "oku -> cevir -> yaz -> AssetDatabase.Refresh".
    ///
    /// MENUSU YOK. Eski 47 ve 48 maddeleri kaldirildi, cunku ikisinin de yaptigini
    /// <see cref="TagSetupWindow"/> (menu 49) tek pencerede ve on kosullari denetleyerek
    /// yapiyor: 47 kagit yapistirilmadan cagrilabiliyordu, 48 ise haritada kac tag'in
    /// plakadan turedigini soylemeden hepsini aciyordu. Cihazda ayni isi yaratici modun
    /// tag kurulumu adimi yapiyor. Bu sinif ikisinin de ortak govdesi olarak duruyor.
    ///
    /// MANTIK BURADA DEGIL: <see cref="TagCapture"/> icinde, cunku ayni cevrim gozlukte de
    /// kosmali. Kopyalansaydi editor yolu ile cihaz yolu sessizce ayrisirdi.
    ///
    /// Yontemin gerekcesi ve olculmus sayilar (6,1 cm vakasi, yaw konvansiyonunun uc bagimsiz
    /// dogrulamasi, plakanin beyaz yuzu) <see cref="TagCapture"/> ozetinde.
    /// </summary>
    public static class MapTagCapture
    {
        public const int FirstTagId = TagCapture.FirstTagId;
        public const string PlateId = TagCapture.PlateId;

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
