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
        [Tooltip("Saniyede kac kez tespit calissin. Her karede calistirmak pil/CPU yer; " +
                 "duvarda sabit bir marker icin 5-10 fazlasiyla yeter.")]
        public float detectionsPerSecond = 10f;

        [Tooltip("Goruntu kucultme carpani. Buyuk deger = hizli ama menzil/dogruluk duser.")]
        public int decimation = 2;

        [Header("Kalibrasyon")]
        [Tooltip("Ilk saglam tespitte otomatik kalibre et. Kapaliysa yalnizca olcum yapar " +
                 "(FAZ 0 spike modu).")]
        public bool autoCalibrate = true;

        [Tooltip("Dikey ekseni de tag'den duzelt. Tag'in yuksekligi olculmus oldugu icin bu, " +
                 "gozlugun zemin tahminindeki hatayi da duzeltir.")]
        public bool correctVertical = true;

        [Header("Spike olcum paneli")]
        public bool showPanel = true;

        AprilTag.TagDetector _detector;
        WebCamTextureManager _camMgr;
        Color32[] _pixels;
        int _texW, _texH;
        float _nextDetectAt;
        bool _calibrated;

        // Olcum (FAZ 0): son tespitler uzerinden menzil ve jitter
        readonly Queue<Vector3> _recent = new Queue<Vector3>();
        const int RecentMax = 30;
        int _lastId = -1;
        float _lastDistance;
        float _jitterMm;
        float _lastDetectTime = -1f;
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
            _nextDetectAt = Time.time + 1f / Mathf.Max(1f, detectionsPerSecond);

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

            _detector.ProcessImage(_pixels, fovVertical, tagSizeMeters);

            float now = Time.time;
            if (_lastDetectTime > 0f) _detectHz = 1f / Mathf.Max(0.0001f, now - _lastDetectTime);
            _lastDetectTime = now;

            var camPose = PassthroughCameraUtils.GetCameraPoseInWorld(_camMgr.Eye);

            foreach (var tag in _detector.DetectedTags)
            {
                var entry = Find(tag.ID);
                if (entry == null) continue;   // yerlesimde tanimli degil, yok say

                // Tag'in DUNYA pozu (mevcut, muhtemelen kaymis rig'e gore).
                Vector3 worldPos = camPose.position + camPose.rotation * tag.Position;
                Quaternion worldRot = camPose.rotation * tag.Rotation;

                RecordMeasurement(tag.ID, tag.Position.magnitude, worldPos);

                if (autoCalibrate && !_calibrated)
                    Calibrate(entry, worldPos, worldRot);

                break; // tek tag yeter; coklu tag FAZ 5
            }

            TickPanel();
        }

        /// <summary>
        /// Rig'i, olculen tag hedeflenen yerine denk gelecek sekilde hizalar; sonra mevcut
        /// anchor omurgasina devreder. Mantik <see cref="CalibrationManager.Apply"/> ile ayni:
        /// once yaw etrafinda dondur, sonra otele — egim ASLA uygulanmaz.
        /// </summary>
        void Calibrate(TagEntry entry, Vector3 measuredPos, Quaternion measuredRot)
        {
            var cm = FindFirstObjectByType<CalibrationManager>();
            if (cm == null || cm.rig == null) return;
            var rig = cm.rig;

            Quaternion wantedRot = Quaternion.Euler(0f, entry.yawDegrees, 0f);
            float yawDelta = Mathf.DeltaAngle(YawOf(measuredRot), YawOf(wantedRot));

            // Olculen tag konumu etrafinda dondur: o nokta yerinde kalir, geri kalan dunya doner.
            rig.RotateAround(measuredPos, Vector3.up, yawDelta);

            // Sonra tag'i olmasi gereken yere otele.
            Vector3 delta = entry.position - measuredPos;
            if (!correctVertical) delta.y = 0f;
            rig.position += delta;

            _calibrated = true;

            Debug.Log($"[AprilTagCalib] Tag {entry.id} ile kalibre edildi. " +
                      $"Yaw duzeltmesi {yawDelta:0.0} derece, oteleme {delta.magnitude:0.000} m " +
                      $"(dikey {(correctVertical ? delta.y.ToString("0.000") + " m" : "KAPALI")}).");

            // Buradan sonrasi MEVCUT SISTEM: anchor rig'i surer, paylasilir, kalici olur.
            CalibrationAnchor.Bind(rig, cm.SharedTargetPose);
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

            bool seen = _lastDetectTime > 0f && Time.time - _lastDetectTime < 1f && _lastId >= 0;
            _panel.color = seen ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.75f, 0.2f);

            _panel.text = seen
                ? "APRILTAG\n" +
                  $"Tag: {_lastId}\n" +
                  $"Mesafe: {_lastDistance:0.00} m\n" +
                  $"Jitter: {_jitterMm:0.0} mm\n" +
                  $"Tespit: {_detectHz:0.0} Hz\n" +
                  (_calibrated ? "KALIBRE EDILDI" : "olcum modu")
                : "APRILTAG\nTag GORUNMUYOR\n\n(kamera izni verildi mi?\ntag kadrajda mi?)";
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
