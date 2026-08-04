using System;
using System.IO;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Tag yerlesimini DISKTE tutar.
    ///
    /// NEDEN SAHNEDE DEGIL: yerlesim degerleri CIHAZDA olculur, sahne ise PC'de duzenlenir.
    /// Sahnede tutuldugu surece dongu suydu — gozlukte olc, sayilari sesli oku, PC'de yaz,
    /// yeniden build al, tekrar bak. Tek bir tag icin saatler, ve arada dogru mu yanlis mi
    /// gorunmuyor. Diskte tutulunca olcum kendi yerine yazilir ve uygulama yeniden
    /// acildiginda orada durur.
    ///
    /// SAHNEDEKI tagLayout ARTIK VARSAYILAN: dosya yoksa o kullanilir. Dosya varsa sahneyi
    /// EZER — cihazda olculen deger, PC'de elle yazilandan daha guvenilirdir.
    ///
    /// Dosyayi PC'den okumak (gozluk USB'de takiliyken):
    ///   adb shell cat /sdcard/Android/data/&lt;paket&gt;/files/TagLayout.json
    /// </summary>
    public static class TagLayoutStore
    {
        [Serializable]
        class Wrapper { public AprilTagCalibration.TagEntry[] tags; }

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "TagLayout.json");

        public static bool Exists() => File.Exists(FilePath);

        /// <summary>Diskteki yerlesim; dosya yoksa ya da bozuksa null (cagiran sahneyi kullanir).</summary>
        public static AprilTagCalibration.TagEntry[] Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                var w = JsonUtility.FromJson<Wrapper>(File.ReadAllText(FilePath));
                if (w == null || w.tags == null || w.tags.Length == 0) return null;
                return w.tags;
            }
            catch (Exception e)
            {
                // Bozuk dosya yuzunden uygulama acilmamazlik etmesin: sahnedeki varsayilana
                // duseriz. Kalibrasyonsuz kalmak, hic baslamamaktan iyidir.
                Debug.LogWarning($"[TagLayoutStore] Okunamadi ({e.Message}) — sahnedeki yerlesim kullanilacak.");
                return null;
            }
        }

        public static bool Save(AprilTagCalibration.TagEntry[] tags)
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(new Wrapper { tags = tags }, true));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagLayoutStore] Yazilamadi: {e.Message}");
                return false;
            }
        }

        public static bool Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagLayoutStore] Silinemedi: {e.Message}");
                return false;
            }
        }
    }
}
