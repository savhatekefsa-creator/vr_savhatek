using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   47. Plakalardan Tag Yerlesimi Uret — yaratici modda konmus <c>tagisaret</c>
    /// plakalarini haritanin KENDI tag yerlesimine (<see cref="MapLayout.tags"/>) cevirir.
    ///
    /// NEDEN PLAKA: tag'in yerini OLCMEK yerine TANIMLAMAK. Ucu bagimsiz olcum yolu (kamera
    /// ortalamasi, kumanda dokunusu, gozle hizalama) 16 cm'e yayilan uc ayri cevap veriyordu
    /// ve hangisinin dogru oldugunu ayirt etmenin yolu yoktu. Plaka konup KAGIT ONUN USTUNE
    /// yapistirilinca soru ortadan kalkiyor: plaka nereye duserse orasi dogru oluyor. Izgara
    /// kuantalamasi (6.25 cm) da bu yuzden zararsiz.
    ///
    /// AMA YALNIZCA KAGIT SONRA YAPISTIRILIRSA. "Plaka dogruyu tanimlar" zinciri kagidin
    /// plakayi TAKIP etmesine dayaniyor. Kagit ZATEN DUVARDAYSA yon tersine doner: plaka
    /// kagidi kovalamak zorunda kalir ve 6.25 cm'lik izgaraya tam oturamaz. Olculdu
    /// (2026-08-11, ayni fiziksel tag): kamerayla olculmus tag 2 ile plakadan turetileni
    /// arasinda 6,1 cm ve 1,82 derece fark cikti — kuantalamanin tam beklenen buyuklugu.
    ///
    /// KURAL: kagidi asili olan bir tag'i plakadan YENIDEN turetmeyin, kamera olcumu daha
    /// iyidir. Plaka yontemi YENI tag icindir.
    ///
    /// ID NEREDEN GELIYOR: plakanin KOYULMA SIRASINDAN. Her yerlestirme artan bir
    /// <see cref="PlacedProp.instanceId"/> aliyor (<see cref="MapLayout.AddWithId"/>), yani
    /// sira zaten veride. Ilk konan plaka <see cref="FirstTagId"/>, sonraki bir sonraki.
    ///
    /// TAG 0 PLAKADAN GELMEZ, cunku o ORIGIN'in TANIMI: konumu (0, y, 0) olmak zorunda, y de
    /// kagidin zeminden yuksekligi. Bir plakadan turetilseydi origin kendi kendini tarif
    /// etmeye calisirdi. Mevcut yerlesimdeki tag 0 kaydi oldugu gibi tasiniyor.
    ///
    /// SIRA POZISYON DEGIL: bir plakayi silip yeniden koyarsan sona duser ve ID'si degisir.
    /// Rapor esleme tablosunu her seferinde basiyor — kaydetmeden once bakilsin diye.
    ///
    /// PLAKANIN YUZU: kagit BEYAZ yuze (prefabin -Z'si) yapistirilir; kirmizi yuz DUVARA bakar.
    /// Isaretsizken iki 14x14 yuz birbirinin ayni gorunuyordu ve kagidin hangi yuze gittigi
    /// kisiden kisiye degisebiliyordu — yazilimla duzeltilemeyen tek hata turu bu, cunku her
    /// plaka ayri yone bakabilir.
    ///
    /// YAW KONVANSIYONU DOGRULANDI (2026-08-11) — uc bagimsiz gozlem ayni yeri gosterdi:
    ///
    ///   1. Cihazda plaka konurken BEYAZ yuz oyuncuya, yani ODAYA bakiyor. Demek ki plakanin
    ///      +Z'si (yani p.Yaw yonu) DUVARIN ICINE bakiyor.
    ///   2. HARITA2'de tag 1'in kamerayla olculmus yaw'i 270,3 derece; ayni noktadaki plakanin
    ///      urettigi yaw 270,0 derece, konum farki 0,000 m. Yani kameranin olctugu yaw da
    ///      duvarin icine bakiyor — ikisi ayni yon, 180 derece kayma YOK.
    ///   3. Tag 0 tek yaw referansidir (yawFromReferenceOnly + offsetReferenceTagId = 0,
    ///      bkz. AprilTagCalibration satir 834). Yaw'i 180 derece yanlis olsaydi her yaw
    ///      duzeltmesi dunyayi 180 derece dondururdu; sistem calistigina gore dogru.
    ///
    /// SONUC: asagidaki "yaw = p.Yaw" dogru, plakadan gelen tag'e duzeltme EKLEMEYIN.
    /// yawDegrees kagidin baktigi yon DEGIL, tersi — duvarin icini gosteriyor.
    /// </summary>
    public static class MapTagCapture
    {
        /// <summary>Ilk plakanin alacagi tag ID'si. 0 origin'in oldugu icin 1'den basliyor.</summary>
        public const int FirstTagId = 1;

        // Public: Tag Kurulum Merkezi (menu 49) plaka sayisini gostermek icin ayni kimligi
        // kullaniyor — kopyalansaydi iki sabit sessizce ayrisabilirdi.
        public const string PlateId = "tagisaret";

        [MenuItem("Tools/VR Multiplayer/47. Plakalardan Tag Yerlesimi Uret")]
        public static void CaptureMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer",
                Capture(ConstructorSession.DefaultMapName), "Tamam");

        public static string Capture(string mapName)
        {
            var layout = MapLayout.Load(mapName);
            if (layout == null) return $"'{mapName}' okunamadi.\n\n{MapLayout.PathFor(mapName)}";

            var lib = PropLibrary.Instance;
            var def = lib != null ? lib.ById(PlateId) : null;
            if (def == null) return $"Kutuphanede '{PlateId}' yok — plaka cevrilemez.";

            var grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
            if (grid == null) return "Haritanin oda planindan izgara kurulamadi.";

            // KOYMA SIRASI = instanceId sirasi. Bosluklu olabilir (silinenler), onemi yok:
            // ID'yi sayinin kendisi degil SIRASI belirliyor.
            var plates = new List<PlacedProp>();
            foreach (var p in layout.props)
                if (p != null && p.propId == PlateId) plates.Add(p);
            plates.Sort((a, b) => a.instanceId.CompareTo(b.instanceId));

            if (plates.Count == 0)
                return $"'{mapName}' haritasinda hic plaka yok.\n\n" +
                       "Yaratici modda cark > SIPER > TagIsaret, kat 3 (merkez 1.50 m).\n" +
                       "Plakayi BEYAZ yuzu odaya bakacak sekilde koyun — kagit oraya gidiyor.";

            // Tag 0 KORUNUR: origin'in tanimi, plakadan turetilemez. Once haritada varsa
            // oradan, yoksa sahnedeki kalibrasyondan alinir.
            var zero = FindTag(layout.tags, 0) ?? FindTag(SceneLayout(), 0);

            var tags = new List<AprilTagCalibration.TagEntry>();
            if (zero != null) tags.Add(zero);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"'{mapName}' — {plates.Count} plaka cevrildi.");
            sb.AppendLine();
            sb.AppendLine("ESLEME (koyma sirasi):");

            if (zero != null)
                sb.AppendLine($"  tag 0   (origin, plakadan degil)   " +
                              $"{zero.position.x:0.000} {zero.position.y:0.000} {zero.position.z:0.000}");
            else
                sb.AppendLine("  tag 0   YOK — origin tanimi bulunamadi, once onu ayarla");

            int id = FirstTagId;
            foreach (var p in plates)
            {
                var rect = grid.FootprintRect(def, p.cellX, p.cellZ, p.rot, p.scalePct);
                // Plakanin pivotu kupun MERKEZINDE ve MapBuilder dikeyde duzeltme yapmiyor,
                // yani RectCenter dogrudan plakanin merkezini veriyor.
                Vector3 world = grid.RectCenter(rect, p.level, layout.levelHeight);

                // Onceki yerlesimde bu ID varsa YAW'INI KORU. Plaka 5 derecelik adimlarda
                // duruyor (RotationStepDegrees), yani kuantalama en fazla 2,5 derece hata
                // birakir; kameranin olctugu yaw duvarin gercek acisina daha yakin ve kagit
                // duvara duz yapistirildigi icin acisini duvar belirliyor. Olculdu: ayni
                // fiziksel tag icin kamera 271,82 derece derken plaka 270,00 verdi (1,82).
                var eski = FindTag(layout.tags, id) ?? FindTag(SceneLayout(), id);
                float yaw = eski != null ? eski.yawDegrees : p.Yaw;

                // YENI tag KAPALI dogar: kagit henuz plakanin uzerinde olmayabilir, ve
                // dogrulanmadan kalibrasyona giren bir tag dogru olanlarin kurdugu cerceveyi
                // de bozar.
                //
                // VAR OLAN tag'in durumu KORUNUR. Bu arac zincirleme calistiriliyor — buyuk
                // mekanda tag'ler tek tek ekleniyor, cunku her yeni tag bir oncekinin
                // capasiyla, yani suruklenmemis bir cercevede konmali. Hepsini kapatsaydi
                // ikinci tur, calisan tag'leri sessizce dusurur ve oyuncu 48'i unuttugunda
                // kalibrasyon sebepsiz bozulurdu.
                bool acik = eski != null && eski.useForCalibration;

                tags.Add(new AprilTagCalibration.TagEntry
                {
                    id = id,
                    position = world,
                    yawDegrees = yaw,
                    useForCalibration = acik,
                });

                // NE KADAR OYNADI: izgara 6,25 cm'lik hucrelere kuantalıyor, yani plakayi
                // ZATEN DUVARDA OLAN bir kagida gore koymak birkac cm sapma birakir. Bu sayi
                // yazilmadan fark ancak iki haritayi elle karsilastirinca goruluyordu —
                // cihazda yasandi: ayni fiziksel tag icin iki harita 6,1 cm ayristi.
                string oynama = "";
                if (eski != null)
                {
                    float cm = (world - eski.position).magnitude * 100f;
                    oynama = cm < 0.05f ? "  (konum ayni)" : $"  KONUM {cm:0.0} cm OYNADI";
                }

                sb.AppendLine($"  tag {id}   instanceId {p.instanceId,-4}  " +
                              $"{world.x:0.000} {world.y:0.000} {world.z:0.000}  yaw {yaw:0.0}" +
                              (eski != null ? "  (yaw korundu)" : "  (yaw plakadan, 5 derece adim)") +
                              oynama +
                              (acik ? "  ACIK kaldi" : "  KAPALI"));
                id++;
            }

            layout.tags = tags.ToArray();
            if (!layout.Save(mapName)) return "Harita KAYDEDILEMEDI — Console'a bak.";
            AssetDatabase.Refresh();

            sb.AppendLine();
            sb.AppendLine("Yeni tag'ler KAPALI kaydedildi; kagitlari plakalarin uzerine");
            sb.AppendLine("yapistirdiktan sonra menu 48 ile acilir. Zaten acik olanlar acik kaldi.");
            sb.AppendLine();
            sb.AppendLine("Bir plakayi silip yeniden koyarsan sona duser ve ID'si degisir —");
            sb.AppendLine("yukaridaki tablo bunun icin basiliyor.");
            return sb.ToString();
        }

        /// <summary>Yerlesimdeki ID; yoksa null.</summary>
        static AprilTagCalibration.TagEntry FindTag(AprilTagCalibration.TagEntry[] list, int id)
        {
            if (list == null) return null;
            foreach (var t in list)
                if (t != null && t.id == id) return t;
            return null;
        }

        /// <summary>Sahnedeki kalibrasyonun yerlesimi — tag 0'in ve eski yaw'larin kaynagi.</summary>
        static AprilTagCalibration.TagEntry[] SceneLayout()
        {
            var cal = Object.FindFirstObjectByType<AprilTagCalibration>(FindObjectsInactive.Include);
            return cal != null ? cal.tagLayout : null;
        }

        // ------------------------------------------------------------------ menu 48

        [MenuItem("Tools/VR Multiplayer/48. Haritadaki Tag'leri Kalibrasyona Ac")]
        public static void EnableMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer",
                Enable(ConstructorSession.DefaultMapName), "Tamam");

        /// <summary>
        /// Haritadaki tag'lerin hepsini kalibrasyona acar. Ayri bir adim, cunku aradaki is
        /// FIZIKSEL: kagidin plakanin uzerine yapistirilmasi. Once acilsaydi kalibrasyon
        /// tag'i olmadigi bir yerde arardi.
        /// </summary>
        public static string Enable(string mapName)
        {
            var layout = MapLayout.Load(mapName);
            if (layout == null) return $"'{mapName}' okunamadi.";
            if (layout.tags == null || layout.tags.Length == 0)
                return $"'{mapName}' haritasinda tag yerlesimi yok — once menu 47.";

            int n = 0;
            foreach (var t in layout.tags)
                if (t != null && !t.useForCalibration) { t.useForCalibration = true; n++; }

            if (!layout.Save(mapName)) return "Harita KAYDEDILEMEDI — Console'a bak.";
            AssetDatabase.Refresh();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"'{mapName}': {n} tag kalibrasyona acildi ({layout.tags.Length} tag toplam).");
            foreach (var t in layout.tags)
                sb.AppendLine($"  tag {t.id}   {t.position.x:0.000} {t.position.y:0.000} {t.position.z:0.000}" +
                              $"  yaw {t.yawDegrees:0.0}   {(t.useForCalibration ? "ACIK" : "KAPALI")}");
            return sb.ToString();
        }
    }
}
