using System;
using System.Collections.Generic;
using PassthroughCameraSamples;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// FAZ 3 koprusu — duvardaki AprilTag'i okuyup ortak cerceveyi kurar.
    /// Bkz. PLAN-apriltag-uyarlama.md
    ///
    /// MEVCUT SISTEME ETKISI YOK: bu bilesen yalnizca A/B DOKUNMASININ yerine geciyor.
    /// Tag gorulunce rig hizalanir ve <see cref="CalibrationAnchor.Bind"/> cagrilir; oradan
    /// sonrasi (anchor surusu, paylasim, kalicilik) aynen mevcut sistemdir.
    ///
    /// Boru hatti:
    ///   1. WebCamTextureManager'dan kamera karesi
    ///   2. PassthroughCameraUtils'ten kamera pozu + intrinsics
    ///   3. TagDetector.ProcessImage  -> tag'in KAMERAYA gore pozu
    ///   4. Dunyaya cevir             -> tag'in DUNYA pozu
    ///   5. "Bu tag nerede olmaliydi" ile karsilastir -> rig duzeltmesi
    ///   6. CalibrationAnchor.Bind(...)
    ///
    /// Ayrica FAZ 0 SPIKE olcumleri icin panel: menzil, jitter, tespit hizi.
    /// </summary>
    public class AprilTagCalibration : MonoBehaviour
    {
        [Serializable]
        public class TagEntry
        {
            [Tooltip("Basili tag'in ID'si (tag36h11 ailesinde).")]
            public int id;

            [Tooltip("Tag'in ORTAK CERCEVEDEKI konumu (metre). Tek tag ile baslarken bu, " +
                     "tag'in zeminden yuksekligi ve origin'e gore yeridir.")]
            public Vector3 position = new Vector3(0f, 1.0f, 0f);

            [Tooltip("Tag'in baktigi yon — ortak cercevenin +Z'sine gore derece.")]
            public float yawDegrees;

            [Tooltip("Bu tag KALIBRASYONDA kullanilsin mi.\n\n" +
                     "KAPALIYKEN tag yine gorulur, olculur ve panelde gorunur — ama rig'i " +
                     "OYNATMAZ. Yeni asilan bir tag icin yerlesim degerleri gozle dogrulanana " +
                     "kadar KAPALI tutulur: yanlis olculmus tek bir tag, dogru olanlarin " +
                     "kurdugu cerceveyi de bozar ve hangisinin sucu oldugu anlasilmaz.")]
            public bool useForCalibration = true;
        }

        [Header("Tag")]
        [Tooltip("Basili tag'in SIYAH KARESININ dis kenar uzunlugu (metre). Cetvelle olcun — " +
                 "yazicilar olcek kaydirir ve bu deger dogrudan mesafe dogrulugunu belirler.")]
        public float tagSizeMeters = 0.14f;

        [Tooltip("Hangi tag nerede. Tek tag ile baslamak yeterli.")]
        public TagEntry[] tagLayout = { new TagEntry() };

        [Header("Tespit")]
        [Tooltip("Duzeltme GEREKIRKEN saniyede kac tespit (tag gorunuyor ama hiza bozuk). " +
                 "Tespit pahalidir: her turda tam cozunurluklu GetPixels32 + tag arama.")]
        public float detectionsPerSecond = 3f;

        [Tooltip("BOSTAKI hiz: tag gorunmuyorken ya da hiza zaten iyiyken. Macin buyuk kisminda " +
                 "tag'e bakilmaz — o sure boyunca tam hizda taramak bosuna CPU/pil yakar. " +
                 "Tag'e bakildiginda ~1 sn icinde fark edilir, sonra otomatik hizlanir.")]
        public float idleDetectionsPerSecond = 1f;

        [Tooltip("Goruntu kucultme carpani. Buyuk deger = hizli ama menzil/dogruluk duser.")]
        public int decimation = 2;

        [Header("Ogrenme modu — tag'in yerini SISTEM olcsun")]
        [Tooltip("ACIK ise: A/B ile kalibre olduktan sonra tag'e bakin, sistem tag'in ortak " +
                 "cercevedeki konumunu ve yonunu OLCUP loglar. Cikan sayilari Tag Layout'a " +
                 "yazip bu modu kapatirsiniz — bir daha A/B'ye gerek kalmaz.\n\n" +
                 "Elle olcmekten cok daha hassas: 1 m'de jitter 3 mm.")]
        public bool learnMode = false;

        [Tooltip("YALNIZCA bu ID'li tag ogrenilir. -1 = ilk uygun tag (tek tag'li kurulum icin).\n\n" +
                 "COKLU TAG'DE MUTLAKA DOLDURUN: ogrenme ilk uygun tag'e KILITLENIR ve bir daha " +
                 "birakmaz. Tag 0 menzildeyken acarsaniz onu ogrenir, yeni tag'i degil — ve " +
                 "duzeltmenin tek yolu uygulamayi kapatip acmaktir.")]
        public int learnTargetId = -1;

        [Tooltip("Ogrenme icin en fazla bu mesafeden olcum kabul edilir (m). Jitter mesafenin " +
                 "karesiyle buyudugu icin uzaktan ogrenmek hatayi kalici hale getirir.")]
        public float learnMaxDistance = 1.5f;

        [Tooltip("Ogrenme icin kac olcumun ortalamasi alinsin. Titremeyi bastirir.")]
        public int learnSampleCount = 30;

        [Header("Yerlesim isaretcisi")]
        [Tooltip("Her tag'in ILAN EDILEN yerine tag boyutunda bir plaka ciz.\n\n" +
                 "Gercek tag'e bakip plakanin uzerine oturup oturmadigina bakarsin — yerlesimin " +
                 "dogrulugu boylece sayilarla degil GOZLE denetlenir. Yesil plaka kalibrasyonda " +
                 "kullanilan tag, sari plaka dogrulama bekleyen tag.")]
        public bool showTagMarkers = true;

        [Tooltip("Ogrenme modunda passthrough'u AC.\n\n" +
                 "Plakanin gercek tag'in ustune oturup oturmadigini gormek icin ikisini AYNI " +
                 "ANDA gormek gerekir; sanal dunya aciksa gercek tag zaten gorunmez. Sanal " +
                 "dunyayi da gizler — isaretciler ve olcum paneli '~' onekli oldugu icin " +
                 "ayakta kalir.")]
        public bool learnPassthrough = true;

        [Header("Kalibrasyon")]
        [Tooltip("Ilk saglam tespitte otomatik kalibre et. Kapaliysa yalnizca olcum yapar " +
                 "(FAZ 0 spike modu).")]
        public bool autoCalibrate = true;

        [Tooltip("Dikey ekseni de tag'den duzelt. Tag'in yuksekligi olculmus oldugu icin bu, " +
                 "gozlugun zemin tahminindeki hatayi da duzeltir.")]
        public bool correctVertical = true;

        [Tooltip("Kalibrasyon icin kac kare ortalanacak. TEK KARE titrek olabilir ve kalibrasyonu " +
                 "o hatayla kilitler (yasanmis: bir dogru, bir 15 cm kayma). Ortalama bunu bastirir.")]
        public int calibrateSampleCount = 15;

        [Tooltip("Kalibrasyon yalnizca tag bu mesafeden YAKINken yapilir (m). Jitter mesafeyle " +
                 "buyudugu icin yakindan kalibre etmek cok daha dogru — spike: 1 m'de 3 mm, 2 m'de 15 mm.")]
        public float calibrateMaxDistance = 2f;

        [Tooltip("Tag olmasi gereken yerden bu kadar SAPINCA rig duzeltilir (m). Altinda dokunulmaz " +
                 "— jitter'dan surekli snap olmasin. Ust sinir yoksa uyku sonrasi buyuk sapmayi da toparlar.")]
        public float correctionDeadzoneMeters = 0.02f;

        [Tooltip("Yaw icin olu bolge (derece).\n\n" +
                 "MESAFEDE BASKIN HATA BUDUR: yaw sapmasi tag'den uzaklastikca dogrusal olarak " +
                 "yer degistirmeye donusur. 1,5 derece 4 metrede 10 cm demek. Olculdu: iki " +
                 "olcum kosusu arasinda tag 1 tam bu yuzden 23 cm oynadi ve yer degistirme " +
                 "tag 0'dan cikan yaricapa DIK cikti — yani konum degil, donme.")]
        public float correctionYawDeadzoneDegrees = 0.4f;

        [Tooltip("KUCUK sapmalarda duzeltmenin ne kadari bir seferde uygulanir (0-1).\n\n" +
                 "1 = aninda (eski davranis, dar olu bolgede dunya zipliyor). 0,25 = birkac " +
                 "tespitte yakinsar, titreme gorunmez. Dar olu bolgeyi kullanilabilir kilan sey budur.")]
        [Range(0.05f, 1f)]
        public float smallCorrectionRate = 0.25f;

        [Tooltip("Bu sapmanin USTU 'buyuk' sayilir ve ANINDA duzeltilir (m). Uyku sonrasi ya da " +
                 "takip kaybinda dunya hemen yerine otursun; suzulerek gelmesi cok daha kotudur.")]
        public float snapThresholdMeters = 0.10f;

        [Tooltip("Buyuk sapma esiginin yaw karsiligi (derece).")]
        public float snapThresholdDegrees = 3f;

        [Header("Spike olcum paneli")]
        public bool showPanel = true;

        AprilTag.TagDetector _detector;
        WebCamTextureManager _camMgr;
        Color32[] _pixels;
        int _texW, _texH;
        float _nextDetectAt;
        bool _alignedNow;   // son olcumde hiza olu bolge icinde miydi (tespit hizini belirler)

        // Olcum (FAZ 0): son tespitler uzerinden menzil ve jitter
        readonly Queue<Vector3> _recent = new Queue<Vector3>();
        const int RecentMax = 30;
        int _lastId = -1;
        float _lastDistance;
        float _jitterMm;

        // DIKKAT — iki AYRI zaman: tespit turu her seferinde calisir, ama tag her turda
        // BULUNMAZ. Ilk surumde ikisi karistirilmisti ve panel, arada duvar olsa bile
        // "goruyorum" deyip son mesafeyi donduruyordu.
        float _lastPassTime = -1f;   // tespit turu (Hz hesabi icin)
        float _lastTagTime = -1f;    // YALNIZCA tag gercekten bulundugunda
        float _detectHz;

        TextMesh _panel;

        void Start()
        {
            // DISK SAHNEYI EZER: cihazda olculmus deger, PC'de elle yazilandan guvenilirdir.
            // Dosya yoksa sahnedeki yerlesim varsayilan olarak kalir.
            var stored = TagLayoutStore.Load();
            if (stored != null)
            {
                tagLayout = stored;
                Debug.Log($"[AprilTagCalib] Yerlesim DISKTEN yuklendi ({stored.Length} tag): " +
                          TagLayoutStore.FilePath);
            }
            RebuildMarkers();
        }

        void OnDestroy()
        {
            _detector?.Dispose();
            _detector = null;
            if (_panel != null) Destroy(_panel.gameObject);
            ClearMarkers();
        }

        void Update()
        {
            // HER KAREDE okunur. Tespit turu 1-3 Hz'de calisir; buton okumasi o kapinin
            // ARDINDA kalsaydi basislarin cogu kacardi (saniyede bir ornekleme).
            TickLearnInput();
            TickTouch();   // her karede: en yakin yaklasmayi kacirmamak icin
            TickFloor();

            if (Time.time < _nextDetectAt) { TickPanel(); return; }

            // UYARLANIR HIZ: tespit pahali (tam cozunurluklu GetPixels32 + tag arama, ana
            // is parcaciginda). Macin buyuk kisminda tag kadrajda degil ya da hiza zaten iyi —
            // o sure boyunca tam hizda taramak bosuna. Is varken hizlan, yokken yavasla.
            bool tagFresh = _lastTagTime > 0f && Time.time - _lastTagTime < 2f;
            // HENUZ KALIBRE DEGILSEK tam hizda tara. tagFresh ilk tespitte zorunlu olarak
            // false (tag daha hic gorulmedi), yani ilk tur idle hizinda geciyordu: 1 Hz =
            // oyuncunun tag'e bakip bosuna bekledigi 1 saniye. Tasarruf edilecek bir sey yok,
            // oyuncu zaten duvara bakmis bekliyor.
            bool busy = (tagFresh && !_alignedNow) || !CalibrationManager.Calibrated;
            float rate = busy ? detectionsPerSecond : idleDetectionsPerSecond;
            _nextDetectAt = Time.time + 1f / Mathf.Max(0.2f, rate);

            var tex = GetCameraTexture();
            if (tex == null || tex.width <= 16) { TickPanel(); return; }

            EnsureDetector(tex.width, tex.height);
            tex.GetPixels32(_pixels);

            // FOV: PoseEstimationJob "focalLength = height / 2 / tan(fov/2)" hesapliyor, yani
            // DIKEY fov bekliyor. Kaynak projede YATAY fov veriliyordu; 1280x960 gibi kare
            // olmayan bir goruntude bu %33 odak uzakligi (dolayisiyla mesafe) hatasi demek.
            // Dogrusu: fy'den turetilen dikey fov.
            var intr = PassthroughCameraUtils.GetCameraIntrinsics(_camMgr.Eye);
            float fy = intr.FocalLength.y > 1f ? intr.FocalLength.y : intr.FocalLength.x;

            float fovVertical = 2f * Mathf.Atan(tex.height / (2f * fy));

            // DIKKAT — cozunurluk dusurulecekse burasi da degismeli. fy sabit bir referans
            // cozunurluge gore verilir; tex.height ise okudugumuz doku. Su an ikisi de
            // maksimum oldugu icin dogru calisiyor (spike: 1 m -> 1.01, cihazda dogrulandi).
            // RequestedResolution kucultulurse tex.height duser ama fy dusmez, fovVertical
            // yanlis cikar ve TUM mesafeler kayar — tag 1 m'de "2 m'den uzak" gorunup
            // calibrateMaxDistance esigine takilir, hic kalibre etmez.
            // Denendi: refHeight'i intr.Resolution.y yapmak — cihazda mesafeyi bozdu, geri alindi.
            // Dogrusu once GetOutputSizes'tan gercek referansi OLCUP dogrulamak.

            _detector.ProcessImage(_pixels, fovVertical, tagSizeMeters);

            float now = Time.time;
            if (_lastPassTime > 0f) _detectHz = 1f / Mathf.Max(0.0001f, now - _lastPassTime);
            _lastPassTime = now;

            var camPose = PassthroughCameraUtils.GetCameraPoseInWorld(_camMgr.Eye);

            // COKLU TAG: olcum HER tag icin yapilir, kalibrasyon YALNIZCA BIR tag ile.
            //
            // Neden tek tag ile duzeltiyoruz: ContinuousCorrect rig'i OYNATIR. Ayni karede
            // ikinci bir tag'le daha duzeltmek, ikincinin hesabini birincinin tasidigi yeni
            // cerceveye uygulamak demektir -- ust uste binen duzeltmeler sapma uretir.
            //
            // Secim olcutu EN YAKIN: jitter mesafeyle karesel buyuyor (olculdu: 1 m'de 3 mm,
            // 2 m'de 15 mm), yani en yakin tag her zaman en guvenilir olandir. Agirlikli
            // fuzyon (birden fazla tag'i birlestirme) gerekmiyor; gerekirse sonraki faz.
            TagEntry bestEntry = null;
            float bestDist = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            Quaternion bestRot = Quaternion.identity;
            _checkDist = float.MaxValue;   // her turda yeniden secilir

            foreach (var tag in _detector.DetectedTags)
            {
                // Tag'in DUNYA pozu (mevcut, muhtemelen kaymis rig'e gore).
                Vector3 worldPos = camPose.position + camPose.rotation * tag.Position;
                Quaternion worldRot = camPose.rotation * tag.Rotation;
                float dist = tag.Position.magnitude;

                // OLCUM her tag icin yapilir — hangi tag'i gordugumuzu ve ne kadar iyi
                // gordugumuzu bilmek istiyoruz, yerlesimde tanimli olup olmamasi onemsiz.
                RecordMeasurement(tag.ID, dist, worldPos);

                // OLCULEN poz saklanir — dokunus yakinlik testi bunu kullanir. Bkz. TouchAnchor:
                // ilan edilen degere baglanirsa yanlis tahmin dokunusu hic tetiklemez.
                _seenPos[tag.ID] = worldPos;
                _seenYaw[tag.ID] = YawOf(worldRot);
                _seenTime[tag.ID] = Time.time;

                if (learnMode)
                    Learn(tag.ID, dist, worldPos, worldRot);

                // KALIBRASYON adayi: yalnizca yerlesimde TANIMLI tag'ler yarisir.
                if (autoCalibrate)
                {
                    var entry = Find(tag.ID);
                    if (entry != null && entry.useForCalibration && dist < bestDist)
                    {
                        bestEntry = entry;
                        bestDist = dist;
                        bestPos = worldPos;
                        bestRot = worldRot;
                    }

                    // KAPALI tag'in de sapmasi OLCULUR, sadece uygulanmaz. Yoksa yeni bir tag'i
                    // dogrulamak imkansiz olurdu: plakanin kaydigini gorursun ama NE KADAR
                    // kaydigini bilemezsin, dolayisiyla yerlesimi duzeltemezsin.
                    // Duzeltme acik tag'den geldigi icin bu sayi guvenilir bir cerçevede olculur.
                    if (entry != null && !entry.useForCalibration && dist < _checkDist)
                    {
                        _checkId = entry.id;
                        _checkDist = dist;
                        _checkDelta = entry.position - worldPos;
                        _checkYawDev = Mathf.DeltaAngle(YawOf(worldRot), entry.yawDegrees);
                        _hasCheck = true;
                    }
                }
            }

            // Tek seferlik DEGIL — tag her gorulduginde hiza kontrol edilir, gerekiyorsa
            // duzeltilir. Boylece uyku / konum degisimi / drift sonrasi kendini onarir.
            // Kazanan tag degisirse ContinuousCorrect kayan pencereyi zaten temizler
            // (_calibId kontrolu), yani tag'ler arasi gecis pozu bozmaz.
            if (bestEntry != null)
                ContinuousCorrect(bestEntry, bestDist, bestPos, bestRot);

            TickPanel();
        }

        // Kayan pencere: son yakin olcumler (ortalanir). Duzeltme uygulaninca temizlenir —
        // cunku duzeltme rig'i oynatir, eski ornekler eski cerceveye aittir.
        readonly List<Vector3> _calibPos = new List<Vector3>();
        readonly List<float> _calibYaw = new List<float>();
        int _calibId = -1;
        string _calibNote = "";

        // ---- TESHIS (gecici, sorun cozulunce kaldirilabilir) ------------------------------
        // Belirti: panel "duzeltildi -> 0/5 -> duzeltildi" dongusune giriyor, yani duzeltme
        // uygulaniyor ama sapma bir turlu olu bolgenin altina inmiyor. Duzeltmeyi geri alan
        // birileri var; hangi EKSENDE oldugunu bilmeden dogru yeri aramak tahmin olur.
        //
        // Bu uc deger her degerlendirmede yazilir ve panelde KALICI durur (_calibNote gibi
        // aninda ezilmez), boylece duzeltme sonrasi sapmanin kapanip kapanmadigi okunabilir.
        //   dy kapanmiyor  -> dikeyi ezen var (XROrigin floor offset supheli)
        //   dx/dz kapanmiyor -> yatayda baska bir yazici var
        //   hepsi kapaniyor ama yine duzeltiyor -> esik/gurultu sorunu
        Vector3 _diagDelta;

        // Tag gecisi olcumu: bir tag'den otekine gecerken olusan sapma = iki tag'in
        // yerlesimdeki degerlerinin BIRBIRIYLE uyusmazligi. Test C'nin sayisal karsiligi.
        Transform _rightHandDiag;   // nokta okuyucu (gecici, bkz. ProbeLine)

        // KAPALI tag olcumu: useForCalibration=0 olan tag'in yerlesim degerinden ne kadar
        // saptigi. Rig'e dokunmaz, yalnizca panelde gosterilir — yeni tag'in yerlesimini
        // duzeltmek icin gereken sayi budur.
        Vector3 _checkDelta;
        float _checkYawDev, _checkDist = float.MaxValue;
        int _checkId = -1;
        bool _hasCheck;

        Vector3 _switchDelta;
        float _switchYawDev;
        int _switchFrom = -1, _switchTo = -1;
        bool _switchPending, _hasSwitch;
        float _diagYawMeasured, _diagYawExpected, _diagYawDev;
        bool _diagValid;


        Transform _rig;
        CalibrationManager _cm;   // rig + CompleteFromTag icin; ilk duzeltmede bir kez bulunur
        float _nextStateDiagAt;   // teshis yazimini kisitlar (bkz. ApplyCorrection)

        /// <summary>
        /// SUREKLI, kendini onaran hizalama. Tag her gorulduginde:
        ///   - yakin degilse "yaklas" (jitter mesafeyle buyur, uzaktan hizalama kotu)
        ///   - birkac kare biriktir + ortala (tek titrek kareye guvenme)
        ///   - tag olmasi gereken yerden SAPMISSA (olu bolgeden fazla) rig'i duzelt
        ///   - sapma olu bolge icindeyse DOKUNMA (jitter'dan snap yapmasin)
        /// Tek seferlik kilit YOK: uyku / konum degisimi / drift sonrasi kendini toparlar.
        /// </summary>
        void ContinuousCorrect(TagEntry entry, float distance, Vector3 worldPos, Quaternion worldRot)
        {
            if (distance > calibrateMaxDistance)
            {
                _calibNote = $"yaklas ({distance:0.00} > {calibrateMaxDistance:0.00} m)";
                _calibPos.Clear(); _calibYaw.Clear();
                _alignedNow = true;   // bu mesafede yapilacak is yok -> tespit hizlanmasin
                return;
            }
            // TAG GECISI: kazanan tag degisti. Pencere temizlenir (eski ornekler oteki tag'e
            // ait). Ayrica gecisten SONRAKI ilk sapmayi yakalamak istiyoruz — o sayi iki tag'in
            // BIRBIRINE ne kadar uymadigini dogrudan olcer. Duzeltme uygulandiktan sonraki
            // sapma hep ~0 cikar (hizalanmis olur) ve bu bilgiyi gizler.
            bool justSwitched = _calibId >= 0 && entry.id != _calibId;
            if (justSwitched)
            {
                _calibPos.Clear(); _calibYaw.Clear();
                _switchFrom = _calibId;
                _switchPending = true;
            }
            _calibId = entry.id;

            // Kayan pencereye ekle, en fazla calibrateSampleCount tut.
            _calibPos.Add(worldPos);
            _calibYaw.Add(YawOf(worldRot));
            while (_calibPos.Count > calibrateSampleCount) { _calibPos.RemoveAt(0); _calibYaw.RemoveAt(0); }

            // ILK hizalamada AZ ornek yeter, sonrakilerde cok.
            // Ilk duzeltme metre mertebesindedir — 3 mm'lik ornekleme hatasi yaninda gurultu
            // bile sayilmaz, ortalama almak bosa beklemektir. Kalibre olduktan SONRA duzeltmeler
            // cm mertebesine iner ve ortalama gercekten degerli olur; orada 5 ornek kalir.
            // Kotu bir ilk hizalama zaten kendini onarir: bir sonraki tespit duzeltir.
            int need = CalibrationManager.Calibrated ? Mathf.Min(5, calibrateSampleCount) : 2;
            if (_calibPos.Count < need)
            {
                _calibNote = $"olculuyor {_calibPos.Count}/{need}";
                return;
            }

            // Ortalanmis olculen tag pozu.
            Vector3 avgPos = Vector3.zero;
            foreach (var p in _calibPos) avgPos += p;
            avgPos /= _calibPos.Count;
            Vector2 dir = Vector2.zero;
            foreach (var y in _calibYaw)
                dir += new Vector2(Mathf.Sin(y * Mathf.Deg2Rad), Mathf.Cos(y * Mathf.Deg2Rad));
            float avgYaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;

            // Tag olmasi gereken yerden ne kadar sapmis?
            float dev = Vector3.Distance(avgPos, entry.position);
            float yawDev = Mathf.Abs(Mathf.DeltaAngle(avgYaw, entry.yawDegrees));

            // TESHIS: eksen bazli sapma. Duzeltme yapilsin yapilmasin yazilir — "duzeltti ama
            // kapanmadi" durumunu ancak duzeltme sonrasi deger okunarak gorulur.
            _diagDelta = entry.position - avgPos;
            _diagYawMeasured = avgYaw;
            _diagYawExpected = entry.yawDegrees;
            _diagYawDev = yawDev;
            _diagValid = true;

            // Gecisten sonraki ILK olcum: iki tag'in uyusmazligi. Duzeltme uygulanmadan
            // once yakalanir ve KALICI durur — oyuncunun okumaya vakti olsun.
            if (_switchPending)
            {
                _switchPending = false;
                _switchDelta = _diagDelta;
                // GECIS = iki tag'in yerlesim degerlerinin BIRBIRIYLE uyusmazligi. Panelde
                // gorunuyordu ama diske yazilmiyordu; en cok ihtiyac duyulan sayi bu.
                // ISARETLI: duzeltmeyi uygulayabilmek icin yonu de lazim. yawDev mutlak deger
                // oldugu icin "1.8 derece" hangi yone bilinmiyordu.
                _switchYawDev = Mathf.DeltaAngle(avgYaw, entry.yawDegrees);
                _switchTo = entry.id;
                _hasSwitch = true;

                WriteDiag($"GECIS  tag {_switchFrom} -> {_switchTo}  " +
                          $"dx {_switchDelta.x:+0.000;-0.000} dy {_switchDelta.y:+0.000;-0.000} " +
                          $"dz {_switchDelta.z:+0.000;-0.000}  toplam {_switchDelta.magnitude:0.000} m  " +
                          $"yaw {_switchYawDev:+0.00;-0.00}");
            }

            if (dev <= correctionDeadzoneMeters && yawDev <= correctionYawDeadzoneDegrees)
            {
                _calibNote = $"HIZALI ({dev * 100f:0.0} cm)";
                _alignedNow = true;    // is yok -> tespit yavaslasin
                return;   // tolerans icinde — dokunma, jitter'dan snap olmasin
            }

            _alignedNow = false;       // duzeltme gerekiyor -> tespit hizlansin
            ApplyCorrection(entry, avgPos, avgYaw, dev);

            // Rig oynadi: pencere artik eski cerceveye ait, temizle — yeni cercevede dolsun.
            _calibPos.Clear(); _calibYaw.Clear();
        }

        /// <summary>
        /// Rig'i, olculen (ortalanmis) tag olmasi gereken yere denk gelecek sekilde hizalar.
        /// Mantik <see cref="CalibrationManager.Apply"/> ile ayni: once yaw etrafinda dondur,
        /// sonra otele — egim ASLA uygulanmaz. Anchor'a devretmez; tag'in KENDISI surekli
        /// referans (anchor tracking'i 'None' oldugunda ise yaramiyordu, ustelik LateUpdate'te
        /// tag'in duzeltmesini eziyordu).
        /// </summary>
        void ApplyCorrection(TagEntry entry, Vector3 measuredPos, float measuredYaw, float dev)
        {
            if (_cm == null) _cm = FindFirstObjectByType<CalibrationManager>();
            if (_rig == null)
            {
                _rig = _cm != null ? _cm.rig : null;
                if (_rig == null) return;
            }

            float yawDelta = Mathf.DeltaAngle(measuredYaw, entry.yawDegrees);

            // KUCUK duzeltme YUMUSAK, BUYUK duzeltme ANINDA.
            //
            // Olu bolgeyi daraltmak dogrulugu artirir ama tek basina kotu bir takas: duzeltme
            // her tespitte tam uygulaninca dunya saniyede 1-3 kez zipliyor. Olcum gurultusu
            // ~0,3-0,5 derece oldugu icin dar bir esik neredeyse her turda tetiklenir ve
            // 4 metrede 0,4 derece 3 cm'lik gorunur bir kayma demektir — dunya yuzer.
            //
            // Sapmanin bir ORANINI uygulamak ayni dogruluga TITREMEDEN goturur: ust uste
            // birkac tespitte yakinsar, gurultu de ortalanmis olur. Boylece esigi gercekten
            // kucuk tutabiliyoruz.
            //
            // Buyuk sapma yumusatilmaz: uyku sonrasi ya da takip kaybinda oyuncu dunyanin
            // HEMEN yerine oturmasini ister, saniyelerce suzulmesini degil.
            bool snap = dev > snapThresholdMeters || Mathf.Abs(yawDelta) > snapThresholdDegrees;
            float rate = snap ? 1f : Mathf.Clamp01(smallCorrectionRate);

            // Duzeltmeler saniyede 3'e kadar tetiklenir; hepsini yazmak dosyayi bogar.
            // SNAP her zaman yazilir (nadir ve onemli), normal hiza 5 saniyede bir.
            if (snap)
            {
                WriteDiag($"SNAP   tag {entry.id}  sapma {dev * 100f:0.0} cm  yaw {yawDelta:+0.00;-0.00}");
            }
            else if (Time.time >= _nextStateDiagAt)
            {
                _nextStateDiagAt = Time.time + 5f;
                WriteDiag($"HIZA   tag {entry.id}  sapma {dev * 100f:0.0} cm  yaw {yawDelta:+0.00;-0.00}");
            }

            _rig.RotateAround(measuredPos, Vector3.up, yawDelta * rate);

            Vector3 delta = (entry.position - measuredPos) * rate;
            if (!correctVertical) delta.y = 0f;
            _rig.position += delta;

            // Rig hizalandi -> kalibrasyon DURUMUNU da tamamla. Bu satir olmadan tag dogru
            // hizalasa bile oyun "kalibre degil" sanip A/B ekraninda bekletiyordu: bayragi
            // kaldiran tek yol Bind zinciriydi ve o zincir d1176d6'da (hakli olarak) koparildi.
            // CompleteFromTag rig'e DOKUNMAZ ve anchor'i uyandirmaz — yalnizca durumu isaretler,
            // zaten kalibreyse hicbir sey yapmaz. Boylece tag tek referans olmaya devam eder.
            if (_cm != null) _cm.CompleteFromTag();

            _calibNote = $"duzeltildi ({dev * 100f:0.0} cm)";
            Debug.Log($"[AprilTagCalib] Tag {entry.id} duzeltme: sapma {dev * 100f:0.0} cm, " +
                      $"yaw {yawDelta:0.0} derece, oteleme {delta.magnitude:0.000} m.");
        }

        // ------------------------------------------------------------------ ogrenme modu

        readonly List<Vector3> _learnPos = new List<Vector3>();
        readonly List<float> _learnYaw = new List<float>();
        int _learnId = -1;
        bool _learnDone;

        /// <summary>
        /// Tag'in ORTAK CERCEVEDEKI yerini olcup raporlar. Kullanici tag'i elle tarif etmek
        /// zorunda kalmasin diye: kamera zaten mm hassasiyetinde olcuyor, elle koordinat
        /// yazmak o hassasiyeti çöpe atmak olurdu.
        ///
        /// SART: once A/B ile kalibre olunmus olmali — olculen konum, o ANDAKI cerceveye
        /// goredir. Kalibre olunmadan ogrenilen deger anlamsizdir.
        /// </summary>
        void Learn(int id, float distance, Vector3 worldPos, Quaternion worldRot)
        {
            if (_learnDone) return;

            // Hedef ID verilmisse baska tag'e BAKMA. Bu satir olmadan ogrenme, menzile giren
            // ILK tag'e kilitleniyor ve birakmiyordu: coklu tag kurulumunda tag 0 yakindayken
            // acinca onu olcuyor, yeni tag'i degil.
            if (learnTargetId >= 0 && id != learnTargetId)
            {
                _learnNote = $"tag {learnTargetId} bekleniyor (gorulen: {id})";
                return;
            }

            if (!CalibrationManager.Calibrated)
            {
                _learnNote = "once kalibre ol (tag 0'a bak)";
                return;
            }
            if (distance > learnMaxDistance)
            {
                _learnNote = $"yaklas ({distance:0.00} > {learnMaxDistance:0.00} m)";
                return;
            }
            if (_learnId >= 0 && id != _learnId)
                return;   // ogrenme sirasinda tek tag'e odaklan

            _learnId = id;
            _learnPos.Add(worldPos);
            _learnYaw.Add(YawOf(worldRot));
            _learnNote = $"olculuyor {_learnPos.Count}/{learnSampleCount}";

            if (_learnPos.Count < Mathf.Max(5, learnSampleCount)) return;

            // Ortalama: tek olcumun titremesini bastirir.
            Vector3 pos = Vector3.zero;
            foreach (var p in _learnPos) pos += p;
            pos /= _learnPos.Count;

            // Yaw ortalamasi aciyi vektore cevirerek — 359/1 derece sarmasinda duz ortalama
            // yanlis sonuc verir.
            Vector2 dir = Vector2.zero;
            foreach (var y in _learnYaw)
                dir += new Vector2(Mathf.Sin(y * Mathf.Deg2Rad), Mathf.Cos(y * Mathf.Deg2Rad));
            float yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;

            _learnedPos = pos;
            _learnedYaw = yaw;
            _learnDone = true;
            _learnNote = "TAMAM";

            Debug.Log(
                "[AprilTagCalib] === TAG YERI OGRENILDI ===\n" +
                $"  id   = {id}\n" +
                $"  pos  = ({pos.x:0.000}, {pos.y:0.000}, {pos.z:0.000})\n" +
                $"  yaw  = {yaw:0.0} derece\n" +
                $"  ({_learnPos.Count} olcumun ortalamasi)\n" +
                "  Bu degerleri Inspector'da Tag Layout'a yaz, Learn Mode'u KAPAT, " +
                "Auto Calibrate'i AC. Artik A/B'ye gerek yok.");
        }

        string _learnNote = "";
        Vector3 _learnedPos;
        float _learnedYaw;

        // ---- OLC -> UYGULA -> GOZLE DOGRULA -----------------------------------------------
        //
        // Eskiden olcum sonucu panelde SAYI olarak kalirdi; yerlesime gecirmek PC'de sahneyi
        // duzenleyip yeniden build almak demekti. Tek bir tag icin saatler suren ve arada
        // dogru mu yanlis mi gorulemeyen bir dongu. Sag kumandanin B tusu bunu kapatir:
        //
        //   olc  ->  B  ->  yerlesime yazilir + diske kaydedilir  ->  plaka tag'in ustune atlar
        //
        // Olcum BITMEMISKEN B basmak olcumu sifirlar — yanlis yerden olcmeye basladiysan
        // uygulamayi kapatip acmak zorunda kalmayasin diye.
        //
        // TUS SECIMI: yalnizca learnMode ACIKKEN okunur. B oyunda baska is yapiyor; ogrenme
        // bir kurulum modu oldugu icin cakisma pratikte olusmaz.
        bool _applyPrev;

        void TickLearnInput()
        {
            if (!learnMode) { _applyPrev = false; _nudgePrev = Vector2.zero; return; }

            EnsureLearnPassthrough();
            TickNudge();

            // TUS SECIMI — sag A.
            //
            // B KULLANILAMAZ: LanBootstrap'ta "oyuna katil" tusu ve sunucuya baglanmamisken
            // CANLI (LanBootstrap.cs:90). Olcumu yerlesime yazarken ayni anda sunucu aramaya
            // baslamasi kabul edilemez.
            //
            // SAG A her iki durumda da guvenli: TeamSelector de A okur ama o AG OYUNCUSUNDA
            // yasar — sunucusuz hic var olmaz, baglandiktan sonra da takim secilince _done ile
            // susar. ConstructorPlacer'in A'si yalnizca insa modunda calisir.
            // (Sol X denenmedi: RoomScanSync onu tutuyor ve oyuna katilinca canlaniyor.)
            bool a = XRButtons.Button(UnityEngine.XR.XRNode.RightHand,
                                      UnityEngine.XR.CommonUsages.primaryButton);
            bool pressed = a && !_applyPrev;
            _applyPrev = a;
            if (!pressed) return;

            // SOL GRIP basiliyken A: kalibrasyon iznini cevir. Ayri bir tusa yer yok —
            // projede bos yuz tusu kalmadi, ikisini ayirmanin yolu degistirici tus.
            var lh = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);

            if (XRButtons.HeldWithAxisFallback(lh, UnityEngine.XR.CommonUsages.gripButton,
                                               UnityEngine.XR.CommonUsages.grip, 0.5f))
            {
                ToggleUseForSeenTag();
                return;
            }

            // SOL TETIK + A: konumu KUMANDA DOKUNUSUNDAN yaz. Kamera poz kestirimi hic
            // kullanilmaz — A/B'nin verdigi guveni tag sistemini bozmadan verir.
            if (XRButtons.HeldWithAxisFallback(lh, UnityEngine.XR.CommonUsages.triggerButton,
                                               UnityEngine.XR.CommonUsages.trigger, 0.5f))
            {
                ApplyTouchDerived();
                return;
            }

            if (!_learnDone)
            {
                ResetLearn();
                _learnNote = "sifirlandi — bastan olculuyor";
                return;
            }

            ApplyLearned();
        }

        /// <summary>Son GORULEN tag'in kalibrasyon iznini cevirir ve diske yazar.</summary>
        void ToggleUseForSeenTag()
        {
            var entry = Find(_lastId);
            if (entry == null)
            {
                _learnNote = $"tag {_lastId} yerlesimde yok — once olcup B ile ekle";
                return;
            }

            entry.useForCalibration = !entry.useForCalibration;
            bool saved = TagLayoutStore.Save(tagLayout);
            RebuildMarkers();

            _learnNote = $"tag {entry.id} kalibrasyon " +
                         (entry.useForCalibration ? "ACIK" : "KAPALI") +
                         (saved ? "" : " — DISKE YAZILAMADI");

            Debug.Log($"[AprilTagCalib] Tag {entry.id} useForCalibration = {entry.useForCalibration} " +
                      $"({(saved ? "diske yazildi" : "DISKE YAZILAMADI")}).");
        }

        // ---- ELLE INCE AYAR ---------------------------------------------------------------
        //
        // Yeniden olcmek yalnizca RASTGELE hatayi duzeltir. Sapma SISTEMATIKSE — belli bir
        // aciyla bakildiginda poz kestiriminin kaydigi durum — her tekrar ayni yanlisi verir
        // ve olcumu tekrarlamak yalnizca yanlisi daha kararli hale getirir. Plakayi elle
        // gercek tag'in ustune oturtmak bagimsiz bir olcumdur: gozun yakindan hizalamasi
        // kameranin poz kestirim hatasini PAYLASMAZ.
        //
        // SOL cubuk: sag cubuk silah carkina ve insa paletine bagli, solun ekseni bostadir
        // (yalnizca tiklamasi insa moduna geciriyor).
        //
        //   sol cubuk saga/sola      -> tag duzleminde yatay (duvar boyunca)
        //   sol cubuk yukari/asagi   -> yukseklik
        //   sol GRIP + saga/sola     -> yaw
        //   sol TETIK + yukari/asagi -> duvara dik (ileri/geri)
        //
        // AYRIK ADIM, basili tutunca tekrar: surekli kaydirmada dogru noktada durmak zordur.
        const float NudgeStep = 0.01f;          // m
        const float NudgeYawStep = 0.5f;        // derece
        const float NudgeRepeatDelay = 0.35f;   // ilk adimdan sonra tekrara gecis
        const float NudgeRepeatRate = 12f;      // adim/sn

        Vector2 _nudgePrev;
        float _nudgeNextAt;
        bool _nudgeDirty;

        void TickNudge()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            if (!dev.isValid) return;

            if (!dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 ax))
                ax = Vector2.zero;

            // Olu bolge + TEK EKSENE kilit: capraz itiste hem yatay hem dikey oynarsa
            // duzelttigini sanip baska bir ekseni bozarsin.
            const float dz = 0.6f;
            int ix = (Mathf.Abs(ax.x) > dz && Mathf.Abs(ax.x) >= Mathf.Abs(ax.y)) ? (int)Mathf.Sign(ax.x) : 0;
            int iy = (Mathf.Abs(ax.y) > dz && Mathf.Abs(ax.y) >  Mathf.Abs(ax.x)) ? (int)Mathf.Sign(ax.y) : 0;

            if (ix == 0 && iy == 0)
            {
                // BIRAKILINCA yazilir. Her adimda diske yazmak, basili tutulan cubukta
                // saniyede 12 dosya yazmasi demek olurdu.
                if (_nudgeDirty)
                {
                    bool ok = TagLayoutStore.Save(tagLayout);
                    _nudgeDirty = false;
                    _learnNote = ok ? "ince ayar kaydedildi" : "ince ayar DISKE YAZILAMADI";
                }
                _nudgePrev = Vector2.zero;
                return;
            }

            bool first = _nudgePrev == Vector2.zero;
            _nudgePrev = new Vector2(ix, iy);

            if (first) _nudgeNextAt = Time.time + NudgeRepeatDelay;
            else if (Time.time < _nudgeNextAt) return;
            else _nudgeNextAt = Time.time + 1f / NudgeRepeatRate;

            ApplyNudge(dev, ix, iy);
        }

        void ApplyNudge(UnityEngine.XR.InputDevice dev, int ix, int iy)
        {
            var entry = Find(_lastId);
            if (entry == null)
            {
                _learnNote = $"tag {_lastId} yerlesimde yok — once olcup B ile ekle";
                return;
            }

            bool grip = XRButtons.HeldWithAxisFallback(dev,
                UnityEngine.XR.CommonUsages.gripButton, UnityEngine.XR.CommonUsages.grip, 0.5f);
            bool trigger = XRButtons.HeldWithAxisFallback(dev,
                UnityEngine.XR.CommonUsages.triggerButton, UnityEngine.XR.CommonUsages.trigger, 0.5f);

            // Eksenler tag'in KENDI cercevesinde: duvara yapisik bir tag'i dunya X/Z ile
            // itmek onu duvarin icine/disina kaydirir, oysa duzeltmek istedigin sey
            // neredeyse her zaman duvar BOYUNCA kaymadir.
            Quaternion rot = Quaternion.Euler(0f, entry.yawDegrees, 0f);
            string ne;

            if (grip && ix != 0)
            {
                entry.yawDegrees = Mathf.Repeat(entry.yawDegrees + ix * NudgeYawStep + 180f, 360f) - 180f;
                ne = "yaw";
            }
            else if (trigger && iy != 0)
            {
                entry.position += (rot * Vector3.forward) * (iy * NudgeStep);
                ne = "derinlik";
            }
            else if (ix != 0)
            {
                entry.position += (rot * Vector3.right) * (ix * NudgeStep);
                ne = "yatay";
            }
            else
            {
                entry.position += Vector3.up * (iy * NudgeStep);
                ne = "yukseklik";
            }

            _nudgeDirty = true;
            SyncMarkerPoses();

            _learnNote = $"tag {entry.id} {ne}: " +
                         $"{entry.position.x:0.00} {entry.position.y:0.00} {entry.position.z:0.00}" +
                         $" / yaw {entry.yawDegrees:0.0}";
        }

        // ---- KUMANDA DOKUNUSU -------------------------------------------------------------
        //
        // Yerlesimi denetleyecek BAGIMSIZ bir olcu lazim; kameradan gelen her sayi ayni
        // biaslari paylasiyor. Serit metre bunu verirdi ama tag'ler duvarda yuksekte, aralari
        // olculemiyor. Kumanda verebilir: takibi tag tespitinden AYRI bir sistem, dolayisiyla
        // poz kestirim hatasini paylasmaz.
        //
        // KUMANDANIN KENDI OFSETI SORUN DEGIL: izlenen nokta parmak ucunda degil, ama iki
        // tag'e de ayni sekilde degdigin icin ofset FARKTAN duser. Olculen ara ile ilan
        // edilen arayi karsilastirmak bu yuzden gecerli.
        //
        // EN YAKIN YAKLASMA kaydedilir, anlik deger degil: tag'e degerken paneli okuyamazsin,
        // kolunu indirdikten sonra okursun.
        readonly Dictionary<int, Vector3> _touchPos = new Dictionary<int, Vector3>();
        readonly Dictionary<int, Quaternion> _touchRot = new Dictionary<int, Quaternion>();
        readonly Dictionary<int, float> _touchTime = new Dictionary<int, float>();
        int _approachId = -1;
        float _approachBest;
        Vector3 _approachPos;
        Quaternion _approachRot;

        const float TouchEnter = 0.45f;   // altina inince yaklasma baslar
        const float TouchExit = 0.70f;    // ustune cikinca biter, en yakin nokta saklanir

        // Kameranin SON OLCTUGU tag pozlari. Dokunusun yakinlik testi bunlara baglidir.
        readonly Dictionary<int, Vector3> _seenPos = new Dictionary<int, Vector3>();
        readonly Dictionary<int, float> _seenYaw = new Dictionary<int, float>();
        readonly Dictionary<int, float> _seenTime = new Dictionary<int, float>();
        readonly List<int> _touchCandidates = new List<int>();

        /// <summary>
        /// Dokunus icin tag'in NEREDE OLDUGU: taze bir kamera olcumu varsa O, yoksa ilan
        /// edilen konum.
        ///
        /// OLCUM ONCELIKLI OLMAK ZORUNDA. Ilan edilen deger yeni bir tag icin ya hic yoktur
        /// ya da tamamen yanlistir — kagit yeniden asilmis olabilir. Yakinlik testini yanlis
        /// bir tahmine baglarsak dokunus HIC tetiklenmez ve tag'i kaydetmenin yolu kalmaz.
        /// Kamera ise tag'i KIMLIGIYLE taniyor ve her tespitte nerede oldugunu soyluyor;
        /// konumu yanlis bilse de "su anda su tag'e bakiyorum" bilgisi dogrudur.
        /// </summary>
        bool TouchAnchor(int id, out Vector3 anchor)
        {
            if (_seenTime.TryGetValue(id, out float t) && Time.time - t < 3f)
            {
                anchor = _seenPos[id];
                return true;
            }
            var e = Find(id);
            if (e != null) { anchor = e.position; return true; }

            anchor = Vector3.zero;
            return false;
        }

        void TickTouch()
        {
            if (_rightHandDiag == null)
            {
                var rigRef = XRRigReference.Instance;
                _rightHandDiag = rigRef != null ? rigRef.rightHand : null;
                if (_rightHandDiag == null) return;
            }
            Vector3 p = _rightHandDiag.position;

            // Aday tag'ler: yerlesimde OLANLAR + su anda GORULENLER. Ikincisi sart —
            // yerlesimde hic bulunmayan yeni bir tag ancak boyle kaydedilebilir.
            _touchCandidates.Clear();
            if (tagLayout != null)
                foreach (var t in tagLayout)
                    if (t != null && !_touchCandidates.Contains(t.id)) _touchCandidates.Add(t.id);
            foreach (var kv in _seenTime)
                if (Time.time - kv.Value < 3f && !_touchCandidates.Contains(kv.Key))
                    _touchCandidates.Add(kv.Key);

            int near = -1;
            float nd = float.MaxValue;
            foreach (int id in _touchCandidates)
            {
                if (!TouchAnchor(id, out Vector3 a)) continue;
                float d = Vector3.Distance(p, a);
                if (d < nd) { nd = d; near = id; }
            }
            if (near < 0) return;

            if (_approachId < 0)
            {
                if (nd < TouchEnter)
                {
                    _approachId = near; _approachBest = nd;
                    _approachPos = p; _approachRot = _rightHandDiag.rotation;
                }
                return;
            }

            if (near == _approachId && nd < _approachBest)
            {
                _approachBest = nd;
                _approachPos = p; _approachRot = _rightHandDiag.rotation;
            }

            if (nd > TouchExit || near != _approachId)
            {
                _touchPos[_approachId] = _approachPos;
                _touchRot[_approachId] = _approachRot;
                _touchTime[_approachId] = Time.time;

                // REFERANS tag'e dokunulduysa ofset havuzuna ekle.
                //
                // ISKALAYI ALMA: yaklasma mesafesi iyi bir dokunusta ofset buyuklugu
                // kadardir (~5-7 cm). 15 cm'nin ustu tag'e degmemissin demektir; boyle bir
                // ornegi havuza katmak ortalamayi bozar. (Cihazda goruldu: 16,9 cm'lik bir
                // iskalanin ardindan gelen dogru dokunus 6,7 cm'ydi.)
                var refEntry = Find(_approachId);
                if (refEntry != null && refEntry.useForCalibration)
                {
                    if (_approachBest <= 0.15f)
                    {
                        _refId = _approachId;
                        _refOffsets.Add(Quaternion.Inverse(_approachRot) *
                                        (refEntry.position - _approachPos));
                        WriteDiag($"OFSET  ornek {_refOffsets.Count}  sacilma {RefOffsetSpread() * 100f:0.0} cm");
                    }
                    else
                    {
                        WriteDiag($"OFSET  ORNEK ATILDI (yaklasma {_approachBest * 100f:0.0} cm > 15)");
                    }
                }
                Debug.Log($"[AprilTagCalib] Tag {_approachId} dokunuldu: {_approachPos} " +
                          $"(en yakin yaklasma {_approachBest * 100f:0.0} cm).");

                var de = Find(_approachId);
                string turetilen = TouchDerived(_approachId, out Vector3 dp)
                    ? $"  turetilen {dp.x:0.000} {dp.y:0.000} {dp.z:0.000}" : "";
                string ilan = de != null
                    ? $"  ilan {de.position.x:0.000} {de.position.y:0.000} {de.position.z:0.000}" : "  (yerlesimde YOK)";
                WriteDiag($"DOKUNUS tag {_approachId}  ham {_approachPos.x:0.000} {_approachPos.y:0.000} {_approachPos.z:0.000}" +
                          turetilen + ilan + $"  yaklasma {_approachBest * 100f:0.0} cm");
                _approachId = -1;
            }
        }

        /// <summary>
        /// Tag konumunu KUMANDADAN turetir — kamera poz kestirimi devre disi.
        ///
        /// Kumanda takibi tag tespitinden cok daha dogru; tek engeli izlenen noktanin
        /// parmak ucunda olmamasi. Yani tag'e degdiginde okunan konum = tag'in konumu +
        /// sabit bir OFSET. Ofset REFERANS tag'de olculur (onun konumu zaten bir TANIM,
        /// dogru kabul edilir) ve digerlerinden dusulur. A/B'nin verdigi seyi verir:
        /// kameraya hic guvenmeyen bir konum.
        ///
        /// OFSET KUMANDANIN KENDI CERCEVESINDE saklanir. Dunya cercevesinde saklansaydi
        /// iki tag'e farkli acilarla degdiginde ofset donmez, aradaki fark oldugu gibi
        /// hataya donusurdu.
        ///
        /// YAW VERMEZ: tek dokunus yon tasimaz. Yaw kameradan ya da sol cubuktan gelir.
        /// </summary>
        // Referans tag'e yapilan TUM dokunuslardan cikan ofsetler. Ortalanir.
        //
        // NEDEN ORTALAMA, NEDEN SONUNCUSU DEGIL: ofset kumandanin SABIT fiziksel ozelligi —
        // izlenen noktanin degdirdigin noktaya uzakligi. Ama her dokunus onu ~3 cm gurultuyle
        // olcuyor (cihazda goruldu: ayni tag'e iki dokunus y'de 1,472 ve 1,442 verdi). Tek
        // ornekle yeniden olcmek, o gurultuyu SONRAKI TUM tag'lere kalici olarak gecirir —
        // yasandi: tag 1 tam bu yuzden 2,4 cm yuksek yazildi. Ortalama gurultuyü kok-N ile
        // bastirir ve her yeni referans dokunusu tahmini IYILESTIRIR, bozmaz.
        readonly List<Vector3> _refOffsets = new List<Vector3>();
        int _refId = -1;

        /// <summary>Referans tag'de olculen kumanda ofseti — KUMANDANIN kendi cercevesinde.</summary>
        bool TouchOffsetLocal(out Vector3 offsetLocal, out int refId)
        {
            offsetLocal = Vector3.zero;
            refId = _refId;
            if (_refOffsets.Count == 0) return false;

            foreach (var o in _refOffsets) offsetLocal += o;
            offsetLocal /= _refOffsets.Count;
            return true;
        }

        /// <summary>Ortalamadan ortalama sapma (m) — ofsetin ne kadar oturdugunu soyler.</summary>
        float RefOffsetSpread()
        {
            if (_refOffsets.Count < 2) return 0f;
            TouchOffsetLocal(out Vector3 mean, out _);
            float s = 0f;
            foreach (var o in _refOffsets) s += (o - mean).magnitude;
            return s / _refOffsets.Count;
        }

        /// <summary>Kumanda pozunu, DEGDIRILEN noktanin konumuna cevirir.</summary>
        Vector3 ApplyTouchOffset(Vector3 pos, Quaternion rot)
        {
            return TouchOffsetLocal(out Vector3 off, out _) ? pos + rot * off : pos;
        }

        bool TouchDerived(int id, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!_touchPos.ContainsKey(id)) return false;
            if (!TouchOffsetLocal(out Vector3 offsetLocal, out int refId)) return false;
            if (refId == id) return false;   // referansin kendisi turetilemez

            pos = _touchPos[id] + _touchRot[id] * offsetLocal;
            return true;
        }

        // ---- ZEMIN OLCUMU -----------------------------------------------------------------
        //
        // Kumandayi yere YATIRMAK ise yaramiyor: IR halkasi gozlugun kameralarindan gizlenince
        // takip kopuyor ve poz yarim metre siciyor (cihazda olculdu: 0.06 -> 0.56). Dogrusu
        // kumandayi ELDE TUTUP ucunu yere degdirmek, halkasi gorunur kalsin.
        //
        // Olcume tag'lerdeki AYNI ofset uygulanir, yani raporlanan sey "kumandanin izlenen
        // noktasi" degil GERCEKTEN DEGDIRDIGIN nokta olur.
        float _floorBest = float.MaxValue;
        Vector3 _floorPos;
        Quaternion _floorRot;
        bool _floorTracking, _hasFloor;
        float _floorY, _floorRaw;

        void TickFloor()
        {
            if (_rightHandDiag == null) return;
            Vector3 p = _rightHandDiag.position;

            if (p.y < 0.25f)
            {
                if (!_floorTracking || p.y < _floorBest)
                {
                    _floorTracking = true;
                    _floorBest = p.y;
                    _floorPos = p;
                    _floorRot = _rightHandDiag.rotation;
                }
            }
            else if (_floorTracking && p.y > 0.5f)
            {
                _floorTracking = false;
                _floorRaw = _floorPos.y;
                _floorY = ApplyTouchOffset(_floorPos, _floorRot).y;
                _hasFloor = true;
                _floorBest = float.MaxValue;
                WriteDiag($"ZEMIN  ham {_floorRaw:0.000}  ofsetli {_floorY:0.000}");
            }
        }

        /// <summary>
        /// Teshis satirini DISKE yazar.
        ///
        /// NEDEN LOGCAT DEGIL: bu build'de Unity'nin managed logu logcat'e hic akmiyor —
        /// uygulama 2700+ satir basiyor ama hepsi native katmandan. Panelden sayi okuyup
        /// sesli aktarmak hem yavas hem hataya acik. Dosya iki tarafta da guvenilir ve
        /// adb ile dogrudan cekilebiliyor.
        /// </summary>
        static void WriteDiag(string line)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Application.persistentDataPath, "TagDiag.log"),
                    $"[{Time.time:0.0}] {line}\n");
            }
            catch { /* teshis yazamamak oyunu durdurmamali */ }
        }

        /// <summary>Son dokunulan (referans olmayan) tag'in konumunu kumandadan yazar.</summary>
        void ApplyTouchDerived()
        {
            int best = -1;
            float bestT = -1f;
            foreach (var kv in _touchTime)
            {
                var e = Find(kv.Key);
                if (e != null && e.useForCalibration) continue;   // referansa yazilmaz
                if (kv.Value > bestT) { bestT = kv.Value; best = kv.Key; }
            }
            if (best < 0) { _learnNote = "once kumandayla bir tag'e degdir"; return; }

            Vector3 pos;
            if (!TouchDerived(best, out pos))
            {
                _learnNote = "once REFERANS tag'e (tag 0) degdir — ofset oradan olculuyor";
                return;
            }

            var entry = Find(best);
            bool isNew = entry == null;
            if (isNew)
            {
                // Yerlesimde HIC OLMAYAN tag: dokunusla sifirdan dogar. Boylece tag 1 ve 2 icin
                // onceden bir tahmin tutmaya gerek kalmiyor — kagidi nereye asarsan oraya yazilir.
                // KAPALI dogar: dogrulanmadan kalibrasyona giren yanlis bir tag, dogru olanlarin
                // kurdugu cerceveyi de bozar.
                entry = new TagEntry { id = best, useForCalibration = false };
                var list = new List<TagEntry>(tagLayout ?? Array.Empty<TagEntry>());
                list.Add(entry);
                tagLayout = list.ToArray();
            }

            Vector3 before = entry.position;
            entry.position = pos;

            // YAW dokunustan gelmez (tek nokta yon tasimaz) — kameradan alinir. Yeni tag'de
            // sifir birakmak plakayi tamamen yanlis yone cevirir ve dogrulamayi imkansiz kilar.
            bool yawFromCam = _seenTime.TryGetValue(best, out float st) && Time.time - st < 3f;
            if (yawFromCam) entry.yawDegrees = _seenYaw[best];

            bool saved = TagLayoutStore.Save(tagLayout);
            if (isNew) RebuildMarkers(); else SyncMarkerPoses();

            _learnNote = (isNew ? $"tag {best} KUMANDADAN olusturuldu"
                                : $"tag {best} KUMANDADAN yazildi ({(pos - before).magnitude * 100f:0} cm oynadi)")
                       + (yawFromCam ? "" : "  [yaw YOK — tag'e bak]")
                       + (saved ? "" : "  — DISKE YAZILAMADI");

            Debug.Log($"[AprilTagCalib] Tag {best} konumu kumanda dokunusundan turetildi: " +
                      $"{before} -> {pos}, yaw {entry.yawDegrees:0.0} ({(yawFromCam ? "kameradan" : "eski")}). " +
                      $"Kamera poz kestirimi konumda kullanilmadi.");

            WriteDiag($"YAZILDI tag {best} {(isNew ? "(YENI)" : "")}  " +
                      $"{pos.x:0.000} {pos.y:0.000} {pos.z:0.000}  yaw {entry.yawDegrees:0.0}" +
                      $"  once {before.x:0.000} {before.y:0.000} {before.z:0.000}");
        }

        string TouchLine()
        {
            var sb = new System.Text.StringBuilder();

            // ZEMIN: ofset uygulanmis deger 0'a ne kadar yakin. Oyunun zemini gercek zeminle
            // ortusuyorsa ~0.00 cikar.
            if (_hasFloor)
                sb.Append($"\n\n=== ZEMIN ===  {_floorY:+0.000;-0.000} m   (ham {_floorRaw:+0.000;-0.000})");

            // OFSET saglami: sacilma kucukse turetilen konumlara guvenilir. Buyukse
            // dokunuslar tutarsiz demektir ve o tutarsizlik TUM tag'lere geciyordur.
            if (_refOffsets.Count > 0)
            {
                TouchOffsetLocal(out Vector3 off, out int rid);
                sb.Append($"\n\n=== OFSET (tag {rid}) ===  {off.magnitude * 100f:0.0} cm   " +
                          $"{_refOffsets.Count} olcum   sacilma {RefOffsetSpread() * 100f:0.0} cm");
            }

            if (_touchPos.Count == 0) return sb.ToString();

            sb.Append("\n\n=== DOKUNMA (kumandayla tag'e degdir) ===");
            foreach (var kv in _touchPos)
                sb.Append($"\ntag {kv.Key}  {kv.Value.x:0.00} {kv.Value.y:0.00} {kv.Value.z:0.00}" +
                          $"   {Time.time - _touchTime[kv.Key]:0} sn once");

            // Iki dokunus varsa serit metrenin yaptigi is: OLCULEN ara ile ILAN EDILEN ara.
            // Fark buyukse yerlesim yanlis — kameranin ne kadar temiz gorundugu onemsiz.
            var ids = new List<int>(_touchPos.Keys);
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var a = Find(ids[i]);
                    var b = Find(ids[j]);
                    if (a == null || b == null) continue;

                    float olculen = Vector3.Distance(_touchPos[ids[i]], _touchPos[ids[j]]);
                    float ilan = Vector3.Distance(a.position, b.position);
                    sb.Append($"\n{ids[i]}-{ids[j]}  olculen {olculen:0.000}  ilan {ilan:0.000}" +
                              $"  fark {(olculen - ilan) * 100f:+0.0;-0.0} cm");
                }

            // KUMANDADAN turetilen konum — kameranin hic karismadigi deger.
            foreach (var id in ids)
            {
                Vector3 d;
                if (!TouchDerived(id, out d)) continue;
                var e = Find(id);
                float fark = e != null ? Vector3.Distance(d, e.position) : 0f;
                sb.Append($"\ntag {id} KUMANDADAN {d.x:0.00} {d.y:0.00} {d.z:0.00}" +
                          $"  (ilandan {fark * 100f:0} cm)  [sol tetik+A = yaz]");
            }
            return sb.ToString();
        }

        Constructor.ConstructorPassthrough _pt;

        /// <summary>
        /// Ogrenme modunda GERCEK odayi goster.
        ///
        /// Bu bir konfor ayari degil, sart: isaretci plakasi tag'in IDDIA EDILEN yerini
        /// cizer, dogrulamak icin GERCEK tag'le ayni karede gorulmesi gerekir. Sanal dunya
        /// aciksa gercek tag zaten gorunmez ve plakanin dogru olup olmadigi anlasilamaz.
        ///
        /// Her karede kontrol edilir: insa moduna girip cikmak passthrough'u kapatabilir,
        /// bu da onu geri acar. SetActive ayni degerde erken donuyor, bosuna is olmuyor.
        /// </summary>
        void EnsureLearnPassthrough()
        {
            if (!learnPassthrough) return;
            if (_pt == null)
            {
                _pt = FindFirstObjectByType<Constructor.ConstructorPassthrough>();
                if (_pt == null) return;
            }
            if (!_pt.Active) _pt.SetActive(true);
        }

        /// <summary>
        /// Tag'ler arasi ILAN EDILEN mesafeler — serit metreyle karsilastirmak icin.
        ///
        /// Kamerayi denetleyen TEK bagimsiz olcu bu. Kalibrasyon kaymasi, poz kestirim biasi,
        /// drift — hepsi tag'lerin konumlarini BIRLIKTE kaydirabilir ve hicbiri panelde
        /// belli olmaz. Ama aralarindaki mesafe fiziksel bir gercektir ve serit metre onu
        /// 5 mm'de verir. Tutmuyorsa yerlesim yanlistir, sayilar ne kadar duzgun gorunurse
        /// gorunsun.
        /// </summary>
        string PairLine()
        {
            if (tagLayout == null || tagLayout.Length < 2) return "";

            var sb = new System.Text.StringBuilder("\n\n=== ARALIK (serit metre ile karsilastir) ===");
            for (int i = 0; i < tagLayout.Length; i++)
                for (int j = i + 1; j < tagLayout.Length; j++)
                {
                    if (tagLayout[i] == null || tagLayout[j] == null) continue;
                    float d = Vector3.Distance(tagLayout[i].position, tagLayout[j].position);
                    sb.Append($"\n{tagLayout[i].id}-{tagLayout[j].id}   {d:0.000} m");
                }
            return sb.ToString();
        }

        void ResetLearn()
        {
            _learnPos.Clear();
            _learnYaw.Clear();
            _learnId = -1;
            _learnDone = false;
        }

        /// <summary>
        /// Olculen pozu yerlesime yazar, diske kaydeder, isaretciyi tazeler.
        /// </summary>
        void ApplyLearned()
        {
            if (_learnId < 0) return;

            var list = new List<TagEntry>(tagLayout ?? Array.Empty<TagEntry>());
            var entry = list.Find(t => t != null && t.id == _learnId);

            // DONGUSEL OLCUM ENGELI: su an kalibrasyonu SUREN tag'i olcmek, onu kendi
            // cercevesinde olcmek demektir — sonuc zorunlu olarak mevcut degerin ta kendisidir
            // (olu bolge kadar sapmayla). Uzerine yazmak bilgi katmaz, olu bolge hatasini
            // yerlesime kalici olarak isler. Baska bir tag referansken olcmek anlamlidir.
            if (entry != null && entry.useForCalibration && _calibId == entry.id)
            {
                _learnNote = $"tag {entry.id} SU AN referans — kendini olcemez";
                Debug.LogWarning($"[AprilTagCalib] Tag {entry.id} yazilmadi: kalibrasyon su an " +
                                 "bu tag'den geliyor, kendi cercevesinde olculen deger dongusel olur.");
                return;
            }

            bool isNew = entry == null;
            if (isNew)
            {
                // YENI tag KAPALI dogar. Yanlis olculmus tek bir tag, dogru olanlarin kurdugu
                // cerceveyi de bozar ve hangisinin sucu oldugu anlasilmaz — once dogrulanir.
                entry = new TagEntry { id = _learnId, useForCalibration = false };
                list.Add(entry);
            }

            Vector3 before = entry.position;
            float beforeYaw = entry.yawDegrees;

            entry.position = _learnedPos;
            entry.yawDegrees = _learnedYaw;
            tagLayout = list.ToArray();

            bool saved = TagLayoutStore.Save(tagLayout);
            RebuildMarkers();

            float moved = (entry.position - before).magnitude;
            float yawMoved = Mathf.Abs(Mathf.DeltaAngle(beforeYaw, entry.yawDegrees));

            _learnNote = isNew
                ? (saved ? $"TAG {_learnId} EKLENDI (kapali)" : $"TAG {_learnId} EKLENDI — DISKE YAZILAMADI")
                : (saved ? $"UYGULANDI ({moved * 100f:0} cm, {yawMoved:0.0} derece oynadi)"
                         : "UYGULANDI — DISKE YAZILAMADI");

            Debug.Log($"[AprilTagCalib] Tag {_learnId} yerlesimi guncellendi.\n" +
                      $"  once : {before}  yaw {beforeYaw:0.0}\n" +
                      $"  simdi: {entry.position}  yaw {entry.yawDegrees:0.0}\n" +
                      $"  fark : {moved * 100f:0.0} cm, {yawMoved:0.0} derece\n" +
                      $"  dosya: {TagLayoutStore.FilePath} ({(saved ? "yazildi" : "YAZILAMADI")})");

            ResetLearn();
        }

        // ---- yerlesim isaretcileri --------------------------------------------------------
        //
        // Yerlesimin dogrulugu sayilarla denetlenemez: "-1.12" dogru mu yanlis mi, ekranda
        // bakarak anlasilmaz. Plakayi ILAN EDILEN yere cizip gercek tag'e bakmak ise tek
        // bakista soyler — ustune oturuyorsa dogru, kaymissa ne kadar kaydigini da gosterir.

        readonly List<GameObject> _markers = new List<GameObject>();

        void ClearMarkers()
        {
            foreach (var m in _markers) if (m != null) Destroy(m);
            _markers.Clear();
        }

        /// <summary>
        /// Isaretcileri yeniden KURMADAN yalnizca yerlerini tazeler. Ince ayar saniyede 12
        /// adim atiyor; her adimda obje yikip yeniden yaratmak bosuna cop uretir.
        /// </summary>
        void SyncMarkerPoses()
        {
            if (tagLayout == null) return;
            for (int i = 0; i < _markers.Count && i < tagLayout.Length; i++)
            {
                if (_markers[i] == null || tagLayout[i] == null) continue;
                _markers[i].transform.SetPositionAndRotation(
                    tagLayout[i].position, Quaternion.Euler(0f, tagLayout[i].yawDegrees, 0f));
            }
        }

        void RebuildMarkers()
        {
            ClearMarkers();
            if (!showTagMarkers || tagLayout == null) return;

            foreach (var t in tagLayout)
            {
                if (t == null) continue;
                _markers.Add(BuildMarker(t));
            }
        }

        GameObject BuildMarker(TagEntry t)
        {
            // "~" ONEKI SART: passthrough acilinca HideVirtualWorld cizen tum kok objeleri
            // kapatiyor. Oneksiz birakinca isaretci tam da ise yarayacagi anda kaybolurdu —
            // plakayi gercek tag'le karsilastirmak icin ikisini AYNI ANDA gormek gerekiyor.
            var root = new GameObject($"~TagIsaretci_{t.id}");
            root.transform.SetPositionAndRotation(t.position, Quaternion.Euler(0f, t.yawDegrees, 0f));

            // Yesil = kalibrasyonda kullaniliyor. Sari = dogrulama bekliyor.
            Color c = t.useForCalibration ? new Color(0.2f, 1f, 0.35f) : new Color(1f, 0.85f, 0.15f);

            // PLAKA: tag boyutunda, gercek tag'in tam ustune oturmali.
            // Quad DEGIL ince kutu — Quad'in tek yuzu var, arkadan bakilinca kaybolur ve
            // "plaka yok" ile "plaka yanlis yerde" ayirt edilemez hale gelirdi.
            MakePart(root.transform, "plaka", c,
                     new Vector3(tagSizeMeters, tagSizeMeters, 0.004f), Vector3.zero);

            // BURUN: yaw'i gorunur kilar. Duz bir plaka 10 derece donukken de duz gorunur;
            // duvara dik duran bir cubuk egriligi hemen ele verir.
            //
            // IKI YONE birden uzar (merkezde, tek tarafa degil): tespitin yaw konvansiyonu
            // duvarin ICINE bakiyorsa tek tarafli cubuk duvarda kaybolur ve hicbir sey
            // gostermez. Simetrik cubuk hangi konvansiyon olursa olsun gorunur kalir.
            MakePart(root.transform, "burun", c,
                     new Vector3(0.008f, 0.008f, tagSizeMeters * 2.5f), Vector3.zero);

            return root;
        }

        static void MakePart(Transform parent, string name, Color c, Vector3 scale, Vector3 localPos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            // Carpisan bir teshis objesi mermileri durdurur ve oyuncuyu takar — gorsel olmali.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Paint(go, c);
        }

        static void Paint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;

            // Isiktan bagimsiz: teshis isaretcisi karanlikta da okunabilmeli.
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) return;

            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            r.sharedMaterial = m;
        }

        TagEntry Find(int id)
        {
            if (tagLayout == null) return null;
            foreach (var t in tagLayout) if (t != null && t.id == id) return t;
            return null;
        }

        WebCamTexture GetCameraTexture()
        {
            if (_camMgr == null) _camMgr = FindFirstObjectByType<WebCamTextureManager>();
            return _camMgr != null ? _camMgr.WebCamTexture : null;
        }

        void EnsureDetector(int w, int h)
        {
            if (_detector != null && w == _texW && h == _texH) return;
            _detector?.Dispose();
            _detector = new AprilTag.TagDetector(w, h, AprilTag.Interop.TagFamily.Tag36h11, decimation);
            _pixels = new Color32[w * h];
            _texW = w; _texH = h;
            Debug.Log($"[AprilTagCalib] Dedektor kuruldu: {w}x{h}, decimation={decimation}, " +
                      $"tag={tagSizeMeters:0.000} m, aile=Tag36h11.");
        }

        // ------------------------------------------------------------------ olcum (FAZ 0)

        /// <summary>Spike olcumleri: menzil ve JITTER. Sabit dururken konumun ne kadar
        /// oynadigi, buyuk alandaki hata butcesini dogrudan belirler.</summary>
        void RecordMeasurement(int id, float distance, Vector3 worldPos)
        {
            _lastId = id;
            _lastDistance = distance;
            _lastTagTime = Time.time;   // tag GERCEKTEN bulundu

            _recent.Enqueue(worldPos);
            while (_recent.Count > RecentMax) _recent.Dequeue();

            if (_recent.Count < 5) { _jitterMm = 0f; return; }

            Vector3 mean = Vector3.zero;
            foreach (var p in _recent) mean += p;
            mean /= _recent.Count;

            float maxDev = 0f;
            foreach (var p in _recent) maxDev = Mathf.Max(maxDev, Vector3.Distance(p, mean));
            _jitterMm = maxDev * 1000f;
        }

        void TickPanel()
        {
            if (!showPanel) { if (_panel != null) _panel.gameObject.SetActive(false); return; }
            if (_panel == null)
            {
                // "~" ONEKI SART: ConstructorPassthrough.HideVirtualWorld cizen TUM kok
                // objeleri kapatir, panel de kok bir obje. Oneksiz birakinca passthrough
                // acildigi anda panel kaybolurdu — yani sayilari tam da gercek dunyayi
                // gordugun anda kaybederdin.
                _panel = UI.HeadFollowPanel.Create("~AprilTag Olcum", "", Color.white);
                var f = _panel.GetComponent<UI.HeadFollowPanel>();
                if (f != null) f.heightOffset = 0.35f;   // kalibrasyon panelinin USTUNDE
            }
            _panel.gameObject.SetActive(true);

            // Tag SU AN goruluyor mu — olcut: SON TESPIT TURU onu buldu mu. Sabit zaman
            // penceresi KULLANILMAZ: uyarlanir hizda (hizaliyken 1 Hz) 0.4 sn'lik pencere
            // tespitler ARASINDA doluyordu, tag gozunuzun onunde dururken panel saniyede bir
            // "GORUNMUYOR" diye yanip soner ve olmayan bir sorun varmis gibi gorunurdu.
            bool seen = _lastTagTime > 0f && _lastTagTime >= _lastPassTime;
            bool cameraRunning = _lastPassTime > 0f && Time.time - _lastPassTime < 2f;

            // Tag kaybolduysa jitter kuyrugunu bosalt: eski konumlar, tag geri gelince
            // olcumu kirletir ve olmayan bir titreme gosterir.
            // 2 sn: bos gezerken tespit araligi 1 sn oldugu icin 1 sn'lik esik her turda
            // tetikleniyordu — kuyruk surekli bosalip birkac ornekle dolunca panel gercekte
            // olandan daha kotu bir jitter gosteriyordu.
            if (!seen && _recent.Count > 0 && Time.time - _lastTagTime > 2f)
            {
                _recent.Clear();
                _jitterMm = 0f;
            }

            _panel.color = seen ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.75f, 0.2f);

            if (seen)
            {
                // Canli durum — tek seferlik "KALIBRE EDILDI" degil, surekli hiza:
                //   "HIZALI (1.2 cm)" / "duzeltildi (6.3 cm)" / "yaklas" / "olculuyor X/5"
                // IKISI BIRDEN gosterilir. Eskiden learnMode acikken kalibrasyon satiri
                // gizleniyordu: "HIZALI" hic gorunmuyor sanilip kalibrasyon bozuk zannedildi,
                // oysa calisiyordu. Coklu tag kurulumunda ikisi ayni anda kullaniliyor
                // (tag 0 kalibre eder, tag 1 olculur) — ikisini de gormek sart.
                // B tusunun ne yapacagi ANLIK duruma bagli (yaz / sifirla) — ekranda yazmazsa
                // kullanici hangi halde oldugunu bilemez ve olcumu yanlislikla siler.
                // Passthrough istendi ama kamera kalkmadiysa SOYLE. Yoksa oyuncu sanal
                // dunyayi gorup "isaretci yanlis yerde" sanir; oysa gordugu sey gercek oda
                // bile degildir.
                string ptWarn = (learnPassthrough && _pt != null && _pt.Active && !_pt.CameraOk)
                    ? "PASSTHROUGH ACILAMADI (OpenXR ozelligi kapali?)\n" : "";

                string mode = ptWarn
                            + (learnMode
                                  ? "OGRENME: " + _learnNote + "\n"
                                    + (_learnDone ? ">> A = YERLESIME YAZ <<\n"
                                                  : "(A = olcumu sifirla)\n")
                                    + "(sol grip+A = kalib ac/kapat | sol tetik+A = kumandadan yaz)\n"
                                    + "(sol cubuk = ince ayar)\n"
                                  : "")
                            + (autoCalibrate ? "TAG: " + _calibNote
                                             : (learnMode ? "" : "olcum modu"));

                _panel.text =
                    "APRILTAG\n" +
                    // Tek satirda: hangi tag, ne kadar uzakta, ne kadar titrek, ne hizda.
                    $"Tag {_lastId}   {_lastDistance:0.00} m   {_jitterMm:0.0} mm   {_detectHz:0.0} Hz\n" +
                    mode +
                    // Sapma: konum eksen bazli + yaw. "HIZALI" icin IKISI birden esigin
                    // altinda olmali (2 cm / 1.5 derece) — biri tutmazsa duzeltme tetiklenir.
                    (_diagValid
                        ? $"\nsapma  dx {_diagDelta.x:+0.00;-0.00} dy {_diagDelta.y:+0.00;-0.00} dz {_diagDelta.z:+0.00;-0.00}" +
                          $"\nyaw    olc {_diagYawMeasured:0.0}  bek {_diagYawExpected:0.0}  sapma {_diagYawDev:0.0}"
                        : "") +
                    // OGRENME sonucu — yerlesime yazilacak sayilar. En altta ve ayrik dursun.
                    // TAG GECIS SAPMASI — Test C'nin sayisi. Iki tag'in yerlesim degerleri
                    // birbirini tutuyorsa bu ~0 olmali; buyukse tag'lerden biri yanlis olculmus.
                    ProbeLine() +
                    // Yerlesimi kameradan BAGIMSIZ denetleyen sayilar.
                    PairLine() +
                    TouchLine() +
                    // KAPALI tag kontrolu: yerlesim degerini duzeltmek icin gereken sayilar.
                    // "duzeltilmis deger = mevcut - sapma" olacak sekilde okunur.
                    (_hasCheck
                        ? $"\n\n=== TAG {_checkId} KONTROL (kapali) ===" +
                          $"\nsapma  dx {_checkDelta.x:+0.00;-0.00} dy {_checkDelta.y:+0.00;-0.00} dz {_checkDelta.z:+0.00;-0.00}" +
                          $"\ntoplam {_checkDelta.magnitude:0.00} m   yaw {_checkYawDev:+0.0;-0.0}"
                        : "") +
                    (_hasSwitch
                        ? $"\n\n=== TAG {_switchFrom} -> {_switchTo} GECISI ===" +
                          $"\nsapma  dx {_switchDelta.x:+0.00;-0.00} dy {_switchDelta.y:+0.00;-0.00} dz {_switchDelta.z:+0.00;-0.00}" +
                          $"\ntoplam {_switchDelta.magnitude:0.00} m   yaw {_switchYawDev:+0.0;-0.0}"
                        : "") +
                    (_learnDone
                        ? $"\n\n=== TAG {_learnId} OLCULDU ===" +
                          $"\npos  {_learnedPos.x:0.00}  {_learnedPos.y:0.00}  {_learnedPos.z:0.00}" +
                          $"\nyaw  {_learnedYaw:0.0}"
                        : "");
            }
            else
            {
                string since = _lastTagTime > 0f
                    ? $"son gorulme: {Time.time - _lastTagTime:0} sn once"
                    : "hic gorulmedi";
                _panel.text =
                    "APRILTAG\n" +
                    "Tag GORUNMUYOR\n" +
                    since + "\n" +
                    (cameraRunning ? "kamera: calisiyor" : "KAMERA YOK (izin?)") +
                    // Olcum sonucu tag kadrajdan ciksa da gorunsun — sayilari not ederken
                    // oyuncu tag'e bakmayi surdurmek zorunda kalmasin.
                    // TAG GECIS SAPMASI — Test C'nin sayisi. Iki tag'in yerlesim degerleri
                    // birbirini tutuyorsa bu ~0 olmali; buyukse tag'lerden biri yanlis olculmus.
                    ProbeLine() +
                    // Yerlesimi kameradan BAGIMSIZ denetleyen sayilar.
                    PairLine() +
                    TouchLine() +
                    // KAPALI tag kontrolu: yerlesim degerini duzeltmek icin gereken sayilar.
                    // "duzeltilmis deger = mevcut - sapma" olacak sekilde okunur.
                    (_hasCheck
                        ? $"\n\n=== TAG {_checkId} KONTROL (kapali) ===" +
                          $"\nsapma  dx {_checkDelta.x:+0.00;-0.00} dy {_checkDelta.y:+0.00;-0.00} dz {_checkDelta.z:+0.00;-0.00}" +
                          $"\ntoplam {_checkDelta.magnitude:0.00} m   yaw {_checkYawDev:+0.0;-0.0}"
                        : "") +
                    (_hasSwitch
                        ? $"\n\n=== TAG {_switchFrom} -> {_switchTo} GECISI ===" +
                          $"\nsapma  dx {_switchDelta.x:+0.00;-0.00} dy {_switchDelta.y:+0.00;-0.00} dz {_switchDelta.z:+0.00;-0.00}" +
                          $"\ntoplam {_switchDelta.magnitude:0.00} m   yaw {_switchYawDev:+0.0;-0.0}"
                        : "") +
                    (_learnDone
                        ? $"\n\n=== TAG {_learnId} OLCULDU ===" +
                          $"\npos  {_learnedPos.x:0.00}  {_learnedPos.y:0.00}  {_learnedPos.z:0.00}" +
                          $"\nyaw  {_learnedYaw:0.0}"
                        : "");
            }
        }

        /// <summary>
        /// Sag kumandanin DUNYA konumu — bilinen bir fiziksel noktaya degdirip okumak icin.
        ///
        /// SU ANKI SORU (2026-07-31): tag 0'in yuksekligi ELLE 1.52 girildi, tag'in MERKEZI
        /// olculerek. Ama kumandayla bakildiginda 1.52 tag'in UST KENARINA denk geliyor —
        /// yani ~7 cm (tag yarisi) kayma var. Iki ihtimal:
        ///   a) Tespit edilen tag pozu merkez degil, baska bir nokta
        ///   b) Oyunun y=0'i gercek zeminden ~7 cm farkli (gozlugun zemin tahmini)
        ///
        /// AYIRT EDEN TEST: kumandayi ZEMINE degdir.
        ///   y ~ 0.00  -> zemin dogru, sorun tag pozunda (a)
        ///   y ~ 0.07  -> oyunun zemini gercek zeminden 7 cm yukarida (b)
        /// (b) ise HER SEY dikeyde kayar; (a) ise yalnizca tag'lerin y'si.
        /// </summary>
        string ProbeLine()
        {
            if (_rightHandDiag == null)
            {
                var rigRef = XRRigReference.Instance;
                _rightHandDiag = rigRef != null ? rigRef.rightHand : null;
                if (_rightHandDiag == null) return "";
            }
            Vector3 p = _rightHandDiag.position;
            return $"\nkumanda {p.x:+0.00;-0.00} {p.y:+0.00;-0.00} {p.z:+0.00;-0.00}";
        }

        static float YawOf(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-8f)
            {
                f = q * Vector3.up; f.y = 0f;
                if (f.sqrMagnitude < 1e-8f) return 0f;
            }
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
