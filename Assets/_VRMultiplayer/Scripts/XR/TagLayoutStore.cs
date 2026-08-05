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

        // ---- KUMANDA OFSETI ---------------------------------------------------------------
        //
        // Ofset kumandanin SABIT fiziksel ozelligi: izlenen noktanin degdirdigin noktaya
        // uzakligi. Her acilista yeniden olcturmenin bir anlami yok, ustelik unutuldugunda
        // sessizce ise yaramaz hale geliyor -- cihazda yasandi: kullanici tag 1 ve 2'yi
        // olctugunu sandi, ofset olmadigi icin tek bir deger bile yazilamadi.
        //
        // ORTALAMA + SAYI saklanir, ornek listesi degil: yeni dokunuslar kaydi iyilestirsin
        // diye. Sayi UST SINIRLI tutulur, yoksa yuzlerce eski ornek birikince kumandayi
        // farkli tutmaya baslasan bile tahmin donmaz.
        public const int MaxOffsetSamples = 10;

        [Serializable]
        class OffsetFile
        {
            public float x, y, z;
            public int count;
            public int refTag;
        }

        static string OffsetPath =>
            Path.Combine(Application.persistentDataPath, "TouchOffset.json");

        public static bool LoadOffset(out Vector3 offsetLocal, out int count, out int refTag)
        {
            offsetLocal = Vector3.zero;
            count = 0;
            refTag = -1;
            try
            {
                if (!File.Exists(OffsetPath)) return false;
                var o = JsonUtility.FromJson<OffsetFile>(File.ReadAllText(OffsetPath));
                if (o == null || o.count <= 0) return false;

                offsetLocal = new Vector3(o.x, o.y, o.z);
                count = Mathf.Min(o.count, MaxOffsetSamples);
                refTag = o.refTag;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TagLayoutStore] Ofset okunamadi: {e.Message}");
                return false;
            }
        }

        public static bool SaveOffset(Vector3 offsetLocal, int count, int refTag)
        {
            try
            {
                File.WriteAllText(OffsetPath, JsonUtility.ToJson(new OffsetFile
                {
                    x = offsetLocal.x, y = offsetLocal.y, z = offsetLocal.z,
                    count = Mathf.Min(count, MaxOffsetSamples),
                    refTag = refTag
                }, true));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagLayoutStore] Ofset yazilamadi: {e.Message}");
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
