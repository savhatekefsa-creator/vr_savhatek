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
    /// </summary>
    public static class MapTagCapture
    {
        /// <summary>Ilk plakanin alacagi tag ID'si. 0 origin'in oldugu icin 1'den basliyor.</summary>
        public const int FirstTagId = 1;

        const string PlateId = "tagisaret";

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
                       "Yaratici modda cark > SIPER > TagIsaret, kat 3 (merkez 1.50 m).";

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

                // Onceki yerlesimde bu ID varsa YAW'INI KORU. Plaka 15 derecelik adimlarda
                // duruyor; kameranin olctugu yaw duvarin gercek acisina daha yakin ve kagit
                // duvara duz yapistirildigi icin acisini duvar belirliyor.
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

                sb.AppendLine($"  tag {id}   instanceId {p.instanceId,-4}  " +
                              $"{world.x:0.000} {world.y:0.000} {world.z:0.000}  yaw {yaw:0.0}" +
                              (eski != null ? "  (yaw korundu)" : "  (yaw plakadan, 15 derece adim)") +
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
