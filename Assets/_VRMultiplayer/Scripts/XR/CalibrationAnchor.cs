using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace VRMultiplayer
{
    /// <summary>
    /// FAZ 1 — Anchor omurgasi (bkz. PLAN-kalibrasyon.md).
    ///
    /// SORUN: <see cref="CalibrationManager"/> rig'i BIR KEZ oturtup birakiyor. Quest'in SLAM
    /// tracking'i zamanla kaydiginda (drift) o tek seferlik hizalamayi geri cekecek hicbir sey
    /// yok — "oyun ortasinda kalibrasyon bozulmasi" tam olarak bu.
    ///
    /// COZUM: Kalibre edilen hedef poza bir ARAnchor birak. Runtime, haritasini yeniden
    /// duzenledikce anchor'i GERCEK dunyadaki yerinde tutmak icin session pozunu gunceller.
    /// Biz de her karede rig'i o anchor'dan YENIDEN turetiriz; kayma otomatik telafi edilir.
    ///
    /// DONGUSEL BAGIMLILIK YOK — kilit nokta budur: anchor'i DUNYA pozundan surmek dongu yaratir
    /// (rig anchor'i tasir, anchor rig'i tasir). Oysa <c>ARAnchor.pose</c> SESSION uzayindadir ve
    /// rig transformundan tamamen bagimsizdir:
    ///     P_s = anchor.pose (session)      T = hedef dunya pozu
    ///     istenen: Rig o P_s = T      =>   Rig = T o P_s^-1
    ///
    /// YALNIZCA YAW + OTELEME uygulanir. Pitch/roll ASLA — dunyayi yan yatirmak mide bulandirir,
    /// zemin zemindir. Dikey (Y) oteleme <see cref="correctVertical"/> ile kapatilabilir.
    ///
    /// GERILEME YOK: anchor olusturulamazsa (destek yok / hata) CalibrationManager'in tek
    /// seferlik hizalamasi aynen gecerli kalir; bugunku davranisa doneriz.
    ///
    /// Kalicilik (kaydet/yukle) bu fazda YOK — sonraki adim.
    /// </summary>
    public class CalibrationAnchor : MonoBehaviour
    {
        public static CalibrationAnchor Instance { get; private set; }

        [Tooltip("Dikey (Y) drift duzeltmesi. Kapaliyken rig'in Y'si kalibrasyon anindaki degerde " +
                 "sabit kalir (bugunku davranis). Cihazda acik/kapali karsilastirmak icin.")]
        public bool correctVertical = true;

        [Tooltip("Tek karede kabul edilen en buyuk konum duzeltmesi (m). Drift yavas birikir; " +
                 "bunu asan sicrama SUPHELIDIR (relocalization vb.) — atlanir ve loglanir.")]
        public float maxJumpMeters = 0.5f;

        [Tooltip("Tek karede kabul edilen en buyuk yaw duzeltmesi (derece).")]
        public float maxJumpDegrees = 20f;

        [Tooltip("Sicrama bu kadar kare ust uste reddedilirse artik GERCEK kabul edilip uygulanir. " +
                 "Kalici kilitlenmeyi onler (duzgun ele alinmasi FAZ 4'te).")]
        public int jumpAcceptAfterFrames = 60;

        [Header("VR durum paneli (gozlukte log okunamadigi icin)")]
        [Tooltip("Panel hic gosterilmesin mi? Maca cikarken kapatmak icin.")]
        public bool showPanel = true;

        [Tooltip("Kalibrasyondan sonra panel kac saniye gorunsun.")]
        public float panelSecondsAfterCalibration = 20f;

        [Tooltip("Toplam duzeltme bu kadar artinca panel kendini kisa sure gosterir (m). " +
                 "0 = kapali. Drift'in GERCEKTEN duzeltildigini gozle gormenin yolu budur. " +
                 "Kaba bir esik (or. 2 cm) drift daha kucukse paneli hic actirmaz ve dogrulama " +
                 "imkansizlasir — bu yuzden 0.5 cm.")]
        public float announceDriftStep = 0.005f;

        [Tooltip("Panel bu araliklarla kendini kisa sure gosterir (saniye). 0 = kapali. " +
                 "Drift hic buyumezse esik tabanli gosterim tetiklenmez; nabiz sayesinde yine de " +
                 "'sistem ayakta, duzeltme su kadar' bilgisini gorursunuz.")]
        public float heartbeatSeconds = 180f;

        [Tooltip("Nabiz gosteriminin suresi (saniye).")]
        public float heartbeatShowSeconds = 5f;

        Transform _rig;
        ARAnchor _anchor;
        Pose _target;
        float _baselineRigY;
        int _rejectedFrames;
        bool _driving;
        bool _busy;

        // Panel + drift olcumu
        Vector3 _rigPosAtCalib;
        float _rigYawAtCalib;
        float _lastAnnouncedDrift;
        TextMesh _panel;
        float _panelHideAt;
        float _nextHeartbeat;
        bool _wasTracking = true;

        /// <summary>Rig su an anchor'dan mi suruluyor? False ise A/B tek seferlik hizalama gecerli.</summary>
        public static bool Driving => Instance != null && Instance._driving && Instance._anchor != null;

        /// <summary>
        /// A/B kalibrasyonu bittiginde cagrilir. <paramref name="targetWorldPose"/> = kalibrasyonun
        /// tanimladigi ortak cerceve (sharedOrigin + sharedForward). Yeniden kalibrasyonda tekrar
        /// cagrilabilir; eski anchor atilir.
        /// </summary>
        public static void Bind(Transform rig, Pose targetWorldPose)
        {
            if (rig == null) return;

            if (Instance == null)
            {
                var go = new GameObject("Calibration Anchor");
                Instance = go.AddComponent<CalibrationAnchor>();
            }
            Instance.Begin(rig, targetWorldPose);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Begin(Transform rig, Pose targetWorldPose)
        {
            _rig = rig;
            _target = targetWorldPose;
            _baselineRigY = rig.position.y;   // dikey duzeltme kapaliyken korunacak deger
            _driving = false;
            _rejectedFrames = 0;

            // Drift olcumunun sifir noktasi: kalibrasyon anindaki rig pozu. Bundan sonraki
            // her sapma = telafi edilen kayma miktari.
            _rigPosAtCalib = rig.position;
            _rigYawAtCalib = rig.eulerAngles.y;
            _lastAnnouncedDrift = 0f;
            _wasTracking = true;
            _nextHeartbeat = Time.time + heartbeatSeconds;

            // Yeniden kalibrasyon: eski anchor artik yanlis yeri isaret ediyor, at.
            if (_anchor != null)
            {
                Destroy(_anchor.gameObject);
                _anchor = null;
            }

            CreateAnchor();
        }

        async void CreateAnchor()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var mgr = EnsureAnchorManager();
                if (mgr == null)
                {
                    Debug.LogWarning("[CalibAnchor] ARAnchorManager kurulamadi — anchor surusu KAPALI. " +
                                     "A/B tek seferlik hizalama gecerli. Editor'de menu 45'i calistirip " +
                                     "gozluge YENIDEN build al.");
                    ShowStatus(panelSecondsAfterCalibration);
                    return;
                }

                var result = await mgr.TryAddAnchorAsync(_target);

                // await sirasinda sahne/obje yok olmus olabilir (Unity sahte-null).
                if (this == null || _rig == null) return;

                if (!result.status.IsSuccess())
                {
                    Debug.LogWarning($"[CalibAnchor] Anchor OLUSTURULAMADI (status={result.status}). " +
                                     "A/B tek seferlik hizalama gecerli — regresyon yok. " +
                                     "Meta anchor OpenXR ozelligi acik mi? (menu 45)");
                    ShowStatus(panelSecondsAfterCalibration);
                    return;
                }

                _anchor = result.value;
                _rejectedFrames = 0;
                _driving = true;
                Debug.Log($"[CalibAnchor] Anchor olusturuldu ({_anchor.trackableId}). " +
                          $"Rig artik anchor'dan SURULUYOR. Dikey duzeltme: " +
                          $"{(correctVertical ? "ACIK" : "KAPALI")}.");
                ShowStatus(panelSecondsAfterCalibration);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Sahnede ARAnchorManager yoksa XROrigin'e runtime'da takar — sahne dosyasina dokunmadan
        /// (projenin binder deseni). OpenXR ozelligi build zamani ayaridir, runtime'da acilamaz;
        /// o yuzden menu 45 gerekli.
        /// </summary>
        static ARAnchorManager EnsureAnchorManager()
        {
            var mgr = FindFirstObjectByType<ARAnchorManager>();
            if (mgr != null) return mgr;

            if (FindFirstObjectByType<ARSession>() == null)
            {
                Debug.LogWarning("[CalibAnchor] Sahnede ARSession yok — anchor calismaz. " +
                                 "Menu 11 (Setup Room Scan) ya da menu 45 bunu kurar.");
                return null;
            }

            var origin = FindFirstObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogWarning("[CalibAnchor] Sahnede XROrigin yok — ARAnchorManager takilamadi.");
                return null;
            }

            mgr = origin.gameObject.AddComponent<ARAnchorManager>();
            Debug.Log("[CalibAnchor] ARAnchorManager sahnede yoktu, XROrigin'e runtime'da eklendi.");
            return mgr;
        }

        void LateUpdate()
        {
            TickHeartbeat();
            TickPanel();

            if (!_driving || _rig == null || _anchor == null) return;

            // Tracking guvenilir degilse DUZELTME YOK — son iyi rig'de kal ("coast"). Guvenilmez
            // bir pozla rig'i surmek dunyayi rastgele oynatir; hic duzeltmemek daha iyidir.
            bool tracking = _anchor.trackingState == TrackingState.Tracking;
            if (tracking != _wasTracking)
            {
                _wasTracking = tracking;
                Debug.Log($"[CalibAnchor] Anchor tracking durumu: {_anchor.trackingState}");
                ShowStatus(6f);   // kullanici anchor'i kaybettigimizi GORSUN
            }
            if (!tracking) return;

            Pose ps = _anchor.pose;   // SESSION uzayi — rig'den bagimsiz, dongu yok

            // Rig = T o P_s^-1, yalnizca yaw bileseniyle.
            float yaw = Mathf.DeltaAngle(YawOf(ps.rotation), YawOf(_target.rotation));
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 pos = _target.position - rot * ps.position;
            if (!correctVertical) pos.y = _baselineRigY;

            // Sicrama korumasi: drift yavas birikir, ani buyuk duzeltme supheli demektir.
            float dPos = Vector3.Distance(pos, _rig.position);
            float dYaw = Mathf.Abs(Mathf.DeltaAngle(_rig.eulerAngles.y, yaw));
            if (dPos > maxJumpMeters || dYaw > maxJumpDegrees)
            {
                if (++_rejectedFrames < jumpAcceptAfterFrames)
                {
                    if (_rejectedFrames == 1)
                        Debug.LogWarning($"[CalibAnchor] Buyuk sicrama reddedildi: {dPos:0.00} m / " +
                                         $"{dYaw:0.0} deg. {jumpAcceptAfterFrames} kare surerse kabul edilecek.");
                    return;
                }
                Debug.LogWarning($"[CalibAnchor] Sicrama {_rejectedFrames} kare surdu — gercek kabul " +
                                 $"edilip uygulaniyor ({dPos:0.00} m / {dYaw:0.0} deg).");
            }
            _rejectedFrames = 0;

            _rig.SetPositionAndRotation(pos, rot);

            // Toplam duzeltme belirgin bir adim daha buyudugunde paneli kisa sure goster.
            // "Drift gercekten duzeltiliyor mu" sorusunun gozle gorulur cevabi budur.
            if (announceDriftStep > 0f)
            {
                float drift = Vector3.Distance(_rig.position, _rigPosAtCalib);
                if (drift - _lastAnnouncedDrift >= announceDriftStep)
                {
                    _lastAnnouncedDrift = drift;
                    ShowStatus(6f);
                }
            }
        }

        // ------------------------------------------------------------------ VR durum paneli

        /// <summary>Durum panelini <paramref name="seconds"/> saniye gosterir. Gozlukte Console
        /// olmadigi icin dogrulamanin tek pratik yolu bu (alternatifi USB + adb logcat).</summary>
        public void ShowStatus(float seconds)
        {
            if (!showPanel) return;
            _panelHideAt = Mathf.Max(_panelHideAt, Time.time + seconds);
            if (_panel == null)
            {
                _panel = UI.HeadFollowPanel.Create("Calibration Status", "", Color.white);
                // Kalibrasyon panelinin ALTINDA dursun — ikisi de kafanin 1.4 m onunde.
                var follow = _panel.GetComponent<UI.HeadFollowPanel>();
                if (follow != null) follow.verticalOffset = -0.40f;
            }
            _panel.gameObject.SetActive(true);
            RefreshPanelText();
        }

        /// <summary>Belirli araliklarla paneli kisa sure gosterir. Sebep: drift esigi (0.5 cm)
        /// hic asilmazsa panel bir daha acilmaz ve kullanici "calisiyor mu, olmus mu" ayrimini
        /// yapamaz. Nabiz bu belirsizligi kaldirir. Kalibrasyondan once calismaz.</summary>
        void TickHeartbeat()
        {
            if (heartbeatSeconds <= 0f || _rig == null) return;
            if (Time.time < _nextHeartbeat) return;
            _nextHeartbeat = Time.time + heartbeatSeconds;
            ShowStatus(heartbeatShowSeconds);
        }

        void TickPanel()
        {
            if (_panel == null) return;
            if (!showPanel || Time.time > _panelHideAt)
            {
                if (_panel.gameObject.activeSelf) _panel.gameObject.SetActive(false);
                return;
            }
            RefreshPanelText();
        }

        void RefreshPanelText()
        {
            if (_panel == null) return;

            string anchorLine;
            Color color;
            if (!_driving || _anchor == null)
            {
                anchorLine = "YOK - A/B yedegi";
                color = new Color(1f, 0.75f, 0.2f);            // turuncu: calisiyor ama korumasiz
            }
            else if (_anchor.trackingState == TrackingState.Tracking)
            {
                anchorLine = "SURULUYOR";
                color = new Color(0.45f, 1f, 0.5f);            // yesil
            }
            else
            {
                anchorLine = "IZLENMIYOR (bekliyor)";
                color = new Color(1f, 0.75f, 0.2f);
            }

            // Dikey referans: "zeminden yukarida/asagida doguyorum" sikayetinin DRIFT mi
            // KURULUM HATASI mi oldugunu ayirt eden satir.
            string floorLine;
            if (XRTrackingOriginSetup.FloorGranted == true)
            {
                floorLine = "Floor TAMAM";
            }
            else if (XRTrackingOriginSetup.FloorGranted == false)
            {
                floorLine = "FLOOR REDDEDILDI! (" + XRTrackingOriginSetup.OriginMode + ")";
                color = new Color(1f, 0.35f, 0.3f);            // kirmizi: anchor bunu cozmez
            }
            else
            {
                floorLine = "belirsiz";
            }

            float dPos = _rig != null ? Vector3.Distance(_rig.position, _rigPosAtCalib) : 0f;
            float dYaw = _rig != null ? Mathf.DeltaAngle(_rigYawAtCalib, _rig.eulerAngles.y) : 0f;

            _panel.color = color;
            _panel.text =
                "KALIBRASYON DURUMU\n" +
                "Anchor: " + anchorLine + "\n" +
                "Dikey ref: " + floorLine + "\n" +
                "Dikey duzeltme: " + (correctVertical ? "ACIK" : "KAPALI") + "\n" +
                $"Toplam duzeltme: {dPos * 100f:0.0} cm / {dYaw:0.0} derece";
        }

        /// <summary>Bir donusun YALNIZCA yatay bilesenini (yaw) derece cinsinden verir; egim atilir.</summary>
        static float YawOf(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-8f)
            {
                // forward tam dikey — yaw'i up vektorunden oku.
                f = q * Vector3.up;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-8f) return 0f;
            }
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
