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
            bool busy = tagFresh && !_alignedNow;          // gorunuyor ama duzeltme gerekiyor
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

            foreach (var tag in _detector.DetectedTags)
            {
                // Tag'in DUNYA pozu (mevcut, muhtemelen kaymis rig'e gore).
                Vector3 worldPos = camPose.position + camPose.rotation * tag.Position;
                Quaternion worldRot = camPose.rotation * tag.Rotation;

                // OLCUM her tag icin yapilir — spike'ta hangi tag'i gordugumuzu ve ne kadar
                // iyi gordugumuzu bilmek istiyoruz, yerlesimde tanimli olup olmamasi onemsiz.
                RecordMeasurement(tag.ID, tag.Position.magnitude, worldPos);

                if (learnMode)
                    Learn(tag.ID, tag.Position.magnitude, worldPos, worldRot);

                // KALIBRASYON: yalnizca yerlesimde TANIMLI tag ile. Tek seferlik DEGIL — tag her
                // gorulduginde hiza kontrol edilir, gerekiyorsa duzeltilir. Boylece uyku / konum
                // degisimi / drift sonrasi kendini onarir. (Eski tek-seferlik kilit, uyku sonrasi
                // yeniden kalibrasyonu blokluyordu ve panel "KALIBRE EDILDI" yalanini gosteriyordu.)
                if (autoCalibrate)
                {
                    var entry = Find(tag.ID);
                    if (entry != null)
                        ContinuousCorrect(entry, tag.Position.magnitude, worldPos, worldRot);
                }

                break; // tek tag yeter; coklu tag FAZ 5
            }

            TickPanel();
        }

        // Kayan pencere: son yakin olcumler (ortalanir). Duzeltme uygulaninca temizlenir —
        // cunku duzeltme rig'i oynatir, eski ornekler eski cerceveye aittir.
        readonly List<Vector3> _calibPos = new List<Vector3>();
        readonly List<float> _calibYaw = new List<float>();
        int _calibId = -1;
        string _calibNote = "";
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
            if (_calibId >= 0 && entry.id != _calibId) { _calibPos.Clear(); _calibYaw.Clear(); }
            _calibId = entry.id;

            // Kayan pencereye ekle, en fazla calibrateSampleCount tut.
            _calibPos.Add(worldPos);
            _calibYaw.Add(YawOf(worldRot));
            while (_calibPos.Count > calibrateSampleCount) { _calibPos.RemoveAt(0); _calibYaw.RemoveAt(0); }

            int need = Mathf.Min(5, calibrateSampleCount);
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

            if (!CalibrationManager.Calibrated)
            {
                _learnNote = "once A/B ile kalibre ol";
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
                string mode = learnMode     ? "OGRENME: " + _learnNote
                            : autoCalibrate ? "TAG: " + _calibNote
                                            : "olcum modu";

                _panel.text =
                    "APRILTAG\n" +
                    $"Tag: {_lastId}\n" +
                    $"Mesafe: {_lastDistance:0.00} m\n" +
                    $"Jitter: {_jitterMm:0.0} mm\n" +
                    $"Tespit: {_detectHz:0.0} Hz\n" +
                    mode +
                    (_learnDone
                        ? $"\npos {_learnedPos.x:0.00} {_learnedPos.y:0.00} {_learnedPos.z:0.00}" +
                          $"\nyaw {_learnedYaw:0.0}"
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
                    (cameraRunning ? "kamera: calisiyor" : "KAMERA YOK (izin?)");
            }
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
