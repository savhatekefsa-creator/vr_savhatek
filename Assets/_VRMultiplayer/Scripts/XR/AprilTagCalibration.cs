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

        [Tooltip("Yaw icin olu bolge (derece).")]
        public float correctionYawDeadzoneDegrees = 1.5f;

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

        void OnDestroy()
        {
            _detector?.Dispose();
            _detector = null;
            if (_panel != null) Destroy(_panel.gameObject);
        }

        void Update()
        {
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

            foreach (var tag in _detector.DetectedTags)
            {
                // Tag'in DUNYA pozu (mevcut, muhtemelen kaymis rig'e gore).
                Vector3 worldPos = camPose.position + camPose.rotation * tag.Position;
                Quaternion worldRot = camPose.rotation * tag.Rotation;
                float dist = tag.Position.magnitude;

                // OLCUM her tag icin yapilir — hangi tag'i gordugumuzu ve ne kadar iyi
                // gordugumuzu bilmek istiyoruz, yerlesimde tanimli olup olmamasi onemsiz.
                RecordMeasurement(tag.ID, dist, worldPos);

                if (learnMode)
                    Learn(tag.ID, dist, worldPos, worldRot);

                // KALIBRASYON adayi: yalnizca yerlesimde TANIMLI tag'ler yarisir.
                if (autoCalibrate)
                {
                    var entry = Find(tag.ID);
                    if (entry != null && dist < bestDist)
                    {
                        bestEntry = entry;
                        bestDist = dist;
                        bestPos = worldPos;
                        bestRot = worldRot;
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

        Vector3 _switchDelta;
        float _switchYawDev;
        int _switchFrom = -1, _switchTo = -1;
        bool _switchPending, _hasSwitch;
        float _diagYawMeasured, _diagYawExpected, _diagYawDev;
        bool _diagValid;


        Transform _rig;
        CalibrationManager _cm;   // rig + CompleteFromTag icin; ilk duzeltmede bir kez bulunur

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
                // ISARETLI: duzeltmeyi uygulayabilmek icin yonu de lazim. yawDev mutlak deger
                // oldugu icin "1.8 derece" hangi yone bilinmiyordu.
                _switchYawDev = Mathf.DeltaAngle(avgYaw, entry.yawDegrees);
                _switchTo = entry.id;
                _hasSwitch = true;
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
            _rig.RotateAround(measuredPos, Vector3.up, yawDelta);

            Vector3 delta = entry.position - measuredPos;
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
                _panel = UI.HeadFollowPanel.Create("AprilTag Olcum", "", Color.white);
                var f = _panel.GetComponent<UI.HeadFollowPanel>();
                if (f != null) f.verticalOffset = 0.35f;   // kalibrasyon panelinin USTUNDE
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
                string mode = (learnMode ? "OGRENME: " + _learnNote + "\n" : "")
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
