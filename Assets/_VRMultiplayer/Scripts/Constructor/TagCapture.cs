using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Plaka -> tag yerlesimi cevrimi. CALISMA ZAMANI: gozlukte de kosar.
    ///
    /// NEDEN EDITOR'DEN CIKTI: mantik <c>MapTagCapture</c> icindeydi ve <c>UnityEditor</c>
    /// kullandigi icin cihazda CAGRILAMIYORDU. Oysa isin ozu editore hic bagli degil —
    /// <see cref="MapLayout"/>, <see cref="PropLibrary"/> ve <see cref="RoomGrid"/> yeterli;
    /// editore bagli tek sey <c>AssetDatabase.Refresh</c> idi ve o zaten cihazda anlamsiz.
    /// Bu ayrim, "yaratici modda yeni harita acarken tag kurulumu" akisinin on kosulu:
    /// o akis APK yeniden derlemeden tag konabilmesini istiyor.
    ///
    /// BURASI DISKE YAZMAZ. Verilen <see cref="MapLayout"/> nesnesini yerinde degistirir,
    /// kaydetmeyi cagirana birakir. Sebep yetki: dosyayi HER ZAMAN sunucu yazar
    /// (bkz. <see cref="ConstructorSync.ClientRequestSave"/>); gozlukte yazilan kopyayi
    /// kimse okumaz ve ilk senkron ezer.
    ///
    /// ---- YONTEM ----------------------------------------------------------------------
    ///
    /// NEDEN PLAKA: tag'in yerini OLCMEK yerine TANIMLAMAK. Ucu bagimsiz olcum yolu (kamera
    /// ortalamasi, kumanda dokunusu, gozle hizalama) 16 cm'e yayilan uc ayri cevap veriyordu
    /// ve hangisinin dogru oldugunu ayirt etmenin yolu yoktu. Plaka konup KAGIT ONUN USTUNE
    /// yapistirilinca soru ortadan kalkiyor: plaka nereye duserse orasi dogru oluyor. Izgara
    /// kuantalamasi (6.25 cm) da bu yuzden zararsiz.
    ///
    /// AMA YALNIZCA KAGIT SONRA YAPISTIRILIRSA. Zincir kagidin plakayi TAKIP etmesine
    /// dayaniyor. Kagit ZATEN DUVARDAYSA yon tersine doner: plaka kagidi kovalamak zorunda
    /// kalir ve izgaraya oturamaz. Olculdu (2026-08-11, ayni fiziksel tag): kamerayla
    /// olculmus tag ile plakadan turetileni arasinda 6,1 cm ve 1,82 derece fark cikti.
    /// KURAL: kagidi asili olan bir tag'i plakadan YENIDEN turetmeyin.
    ///
    /// PLAKANIN YUZU: kagit BEYAZ yuze (prefabin -Z'si) yapistirilir; kirmizi yuz DUVARA
    /// bakar. Isaretsizken iki 14x14 yuz ayniydi ve kagidin hangi yuze gittigi kisiden
    /// kisiye degisebiliyordu — yazilimla duzeltilemeyen tek hata turu bu.
    ///
    /// YAW KONVANSIYONU DOGRULANDI (2026-08-11), uc bagimsiz gozlem:
    ///   1. Cihazda plaka konurken BEYAZ yuz ODAYA bakiyor, yani +Z (p.Yaw yonu) DUVARIN
    ///      ICINE bakiyor.
    ///   2. Tag 1'in kamerayla olculmus yaw'i 270,3; ayni noktadaki plakanin urettigi 270,0,
    ///      konum farki 0,000 m. Ikisi ayni yon, 180 derece kayma YOK.
    ///   3. Tag 0 tek yaw referansi (AprilTagCalibration satir 834); 180 yanlis olsaydi her
    ///      duzeltme dunyayi ters cevirirdi.
    /// SONUC: "yaw = p.Yaw" dogru, duzeltme EKLEMEYIN. yawDegrees kagidin baktigi yon DEGIL.
    ///
    /// ID NEREDEN GELIYOR: plakanin KOYULMA SIRASINDAN (<see cref="PlacedProp.instanceId"/>).
    /// Bir plakayi silip yeniden koyarsan sona duser ve ID'si DEGISIR — rapor esleme tablosunu
    /// bu yuzden her seferinde basiyor.
    /// </summary>
    public static class TagCapture
    {
        /// <summary>Ilk plakanin alacagi tag ID'si. 0 origin'in oldugu icin 1'den basliyor.</summary>
        public const int FirstTagId = 1;

        public const string PlateId = "tagisaret";

        /// <summary>
        /// Tag 0 kagidinin MERKEZININ zeminden yuksekligi (metre). SABIT KABUL EDILIYOR.
        ///
        /// NEDEN SABIT: kurulumu yapan kisiye her seferinde bir sayi sordurmak, en cok yapilan
        /// isi en kolay yanlis yapilan is haline getiriyordu — ve yanlis girilen yukseklik
        /// sessizce butun cerceveyi dikeyde kaydirir. 1,50 m ayrica plakanin kat 3'e konmasiyla
        /// birebir ayni sayi (kat yuksekligi 0,5 m x 3), yani origin ile plakalar ayni hatta.
        ///
        /// DEGISTIRMEK ICIN: burayi degistirin, tek yer burasi. Degistirdikten sonra CIHAZDAKI
        /// haritalarda tag 0 kendiliginden guncellenmez — yeni harita akisi yeni degeri yazar,
        /// var olan haritalar icin menu 49'daki "Origin'i Yaz" dugmesi kullanilir.
        /// </summary>
        public const float DefaultOriginHeight = 1.5f;

        /// <summary>Haritadaki plaka sayisi.</summary>
        public static int PlateCount(MapLayout layout)
        {
            if (layout == null || layout.props == null) return 0;
            int n = 0;
            foreach (var p in layout.props)
                if (p != null && p.propId == PlateId) n++;
            return n;
        }

        /// <summary>Plakadan turemis (yani 0 olmayan) tag sayisi.</summary>
        public static int PlateDerivedTagCount(MapLayout layout)
        {
            if (layout == null || layout.tags == null) return 0;
            int n = 0;
            foreach (var t in layout.tags)
                if (t != null && t.id != 0) n++;
            return n;
        }

        /// <summary>Yerlesimdeki ID; yoksa null.</summary>
        public static AprilTagCalibration.TagEntry FindTag(
            AprilTagCalibration.TagEntry[] list, int id)
        {
            if (list == null) return null;
            foreach (var t in list)
                if (t != null && t.id == id) return t;
            return null;
        }

        /// <summary>
        /// Sahnedeki kalibrasyonun yerlesimi — tag 0'in ve eski yaw'larin yedek kaynagi.
        /// Cihazda da gecerli: sahnede AprilTagCalibration her zaman var.
        /// </summary>
        public static AprilTagCalibration.TagEntry[] SceneLayout()
        {
            var cal = Object.FindFirstObjectByType<AprilTagCalibration>(FindObjectsInactive.Include);
            return cal != null ? cal.tagLayout : null;
        }

        /// <summary>
        /// Plakalari <paramref name="layout"/>.tags'e cevirir. Diske YAZMAZ.
        /// </summary>
        /// <param name="changed">
        /// Yerlesim gercekten degistiyse true. Cagiran buna bakip gereksiz kaydetmeyi
        /// atlayabilir — gozlukte her kaydetme bir RPC turu demek.
        /// </param>
        public static string Capture(MapLayout layout, out bool changed)
        {
            changed = false;
            if (layout == null) return "Harita yok.";

            var lib = PropLibrary.Instance;
            var def = lib != null ? lib.ById(PlateId) : null;
            if (def == null) return $"Kutuphanede '{PlateId}' yok — plaka cevrilemez.";

            var grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
            if (grid == null) return "Haritanin oda planindan izgara kurulamadi.";

            // KOYMA SIRASI = instanceId sirasi. Bosluklu olabilir (silinenler), onemi yok:
            // ID'yi sayinin kendisi degil SIRASI belirliyor.
            var plates = new List<PlacedProp>();
            if (layout.props != null)
                foreach (var p in layout.props)
                    if (p != null && p.propId == PlateId) plates.Add(p);
            plates.Sort((a, b) => a.instanceId.CompareTo(b.instanceId));

            if (plates.Count == 0)
                return "Haritada hic plaka yok.\n\n" +
                       "Yaratici modda cark > SIPER > TagIsaret, kat 3 (merkez 1.50 m).\n" +
                       "Plakayi BEYAZ yuzu odaya bakacak sekilde koyun — kagit oraya gidiyor.";

            // Tag 0 KORUNUR: origin'in tanimi, plakadan turetilemez.
            //
            // KOPYALANIR, REFERANS ALINMAZ. Sahnedeki yerlesimden dusuldugunde eskiden
            // SAHNENIN TagEntry nesnesi dogrudan haritanin dizisine giriyordu; ikisi ayni
            // nesne olunca haritada yapilan her degisiklik sahneyi de -- yani prefab
            // override'ini -- sessizce degistiriyordu. Yasandi: bir harita uzerinde origin
            // yuksekligi denenince sahnedeki tag 0 da 1,50'den 1,62'ye kaydi ve bunu kimse
            // istememisti.
            var kaynak = FindTag(layout.tags, 0) ?? FindTag(SceneLayout(), 0);
            AprilTagCalibration.TagEntry zero = kaynak == null ? null
                : new AprilTagCalibration.TagEntry
                {
                    id = 0,
                    position = kaynak.position,
                    yawDegrees = kaynak.yawDegrees,
                    useForCalibration = kaynak.useForCalibration,
                };

            var tags = new List<AprilTagCalibration.TagEntry>();
            if (zero != null) tags.Add(zero);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{plates.Count} plaka cevrildi.");
            sb.AppendLine();
            sb.AppendLine("ESLEME (koyma sirasi):");

            if (zero != null)
                sb.AppendLine("  tag 0   (origin, plakadan degil)   " +
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

                // Onceki yerlesimde bu ID varsa YAW'INI KORU: kameranin olctugu yaw duvarin
                // gercek acisina plakanin 5 derecelik adimlarindan daha yakin.
                var eski = FindTag(layout.tags, id) ?? FindTag(SceneLayout(), id);

                // (-180, 180]'e cek: plaka yaw'i hep pozitif (270), kamera olcumu Atan2'den
                // negatif (-90) geliyordu ve ayni yon iki sayi olarak yaziliyordu.
                float yaw = eski != null ? eski.yawDegrees : p.Yaw;
                if (yaw > 180f) yaw -= 360f;

                // YENI tag KAPALI dogar (kagit henuz yerinde olmayabilir); VAR OLANIN durumu
                // korunur (arac zincirleme calistiriliyor, calisan tag'ler dusmemeli).
                bool acik = eski != null && eski.useForCalibration;

                var yeni = new AprilTagCalibration.TagEntry
                {
                    id = id,
                    position = world,
                    yawDegrees = yaw,
                    useForCalibration = acik,
                };
                tags.Add(yeni);

                string oynama = "";
                if (eski != null)
                {
                    float cm = (world - eski.position).magnitude * 100f;
                    oynama = cm < 0.05f ? "  (konum ayni)" : $"  KONUM {cm:0.0} cm OYNADI";
                    if (cm >= 0.05f || !Mathf.Approximately(eski.yawDegrees, yaw)) changed = true;
                }
                else changed = true;

                sb.AppendLine($"  tag {id}   instanceId {p.instanceId,-4}  " +
                              $"{world.x:0.000} {world.y:0.000} {world.z:0.000}  yaw {yaw:0.0}" +
                              (eski != null ? "  (yaw korundu)" : "  (yaw plakadan, 5 derece adim)") +
                              oynama +
                              (acik ? "  ACIK kaldi" : "  KAPALI"));
                id++;
            }

            // Tag sayisi degistiyse de degisiklik var (plaka silinmis olabilir).
            if (layout.tags == null || layout.tags.Length != tags.Count) changed = true;
            layout.tags = tags.ToArray();

            sb.AppendLine();
            sb.AppendLine("Yeni tag'ler KAPALI. Kagitlari plakalarin BEYAZ yuzune");
            sb.AppendLine("yapistirdiktan sonra acilir. Zaten acik olanlar acik kaldi.");
            return sb.ToString();
        }

        /// <summary>
        /// Haritadaki tag'lerin hepsini kalibrasyona acar. Diske YAZMAZ.
        ///
        /// Ayri bir adim, cunku aradaki is FIZIKSEL: kagidin plakanin uzerine yapistirilmasi.
        /// Once acilsaydi kalibrasyon tag'i olmadigi bir yerde arardi.
        /// </summary>
        public static string Enable(MapLayout layout, out int opened)
        {
            opened = 0;
            if (layout == null) return "Harita yok.";
            if (layout.tags == null || layout.tags.Length == 0)
                return "Haritada tag yerlesimi yok — once plakalardan uret.";

            foreach (var t in layout.tags)
                if (t != null && !t.useForCalibration) { t.useForCalibration = true; opened++; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{opened} tag kalibrasyona acildi ({layout.tags.Length} tag toplam).");
            foreach (var t in layout.tags)
                sb.AppendLine($"  tag {t.id}   {t.position.x:0.000} {t.position.y:0.000} {t.position.z:0.000}" +
                              $"  yaw {t.yawDegrees:0.0}   {(t.useForCalibration ? "ACIK" : "KAPALI")}");
            return sb.ToString();
        }

        /// <summary>
        /// Origin'i (tag 0) yerlesime yazar: konum (0, h, 0), kalibrasyonda ACIK.
        ///
        /// XZ'si SIFIR OLMAK ZORUNDA, cunku tag 0 sifir noktasinin TANIMI. Bir plakadan
        /// turetilseydi origin kendi kendini tarif etmeye calisirdi.
        ///
        /// YAW'A DOKUNULMAZ. Tag 0 tek yaw referansi (AprilTagCalibration satir 834), yani
        /// onun yaw'i cercevenin KENDISINI dondurur ve dogrulanmis tum tag konumlarini birden
        /// yanlislar. Yeni bir mekanda deger serbesttir (cerceveyi o tanimlar); var olan bir
        /// mekanda degistirmek bilincli bir karardir, akis ici bir ayar degil.
        /// </summary>
        public static void SetOrigin(MapLayout layout, float heightMeters = DefaultOriginHeight)
        {
            if (layout == null) return;

            var list = new List<AprilTagCalibration.TagEntry>(
                layout.tags ?? new AprilTagCalibration.TagEntry[0]);

            var zero = FindTag(layout.tags, 0);
            if (zero == null)
            {
                zero = FindTag(SceneLayout(), 0);
                zero = zero != null
                    ? new AprilTagCalibration.TagEntry
                      { id = 0, yawDegrees = zero.yawDegrees, useForCalibration = true }
                    : new AprilTagCalibration.TagEntry { id = 0, useForCalibration = true };
                list.Insert(0, zero);
                layout.tags = list.ToArray();
            }

            zero.position = new Vector3(0f, heightMeters, 0f);
            zero.useForCalibration = true;
        }
    }
}
