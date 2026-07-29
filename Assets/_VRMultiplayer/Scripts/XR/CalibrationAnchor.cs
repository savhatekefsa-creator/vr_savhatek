using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR.Features.Meta;
// Iki pakette de ayni isimde bir tip var (Unity.XR.CoreUtils ve UnityEngine.XR.ARSubsystems);
// anchor API'sinin bekledigi ARSubsystems olani sabitliyoruz.
using SerializableGuid = UnityEngine.XR.ARSubsystems.SerializableGuid;

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
                 "Kalici kilitlenmeyi onler (duzgun ele alinmasi FAZ 4'te). DIKKAT: dikey eksen " +
                 "bu kurala TABI DEGIL — bkz. maxVerticalCorrection.")]
        public int jumpAcceptAfterFrames = 60;

        [Tooltip("Dikey duzeltmenin kalibrasyon anindaki degerden sapabilecegi EN BUYUK miktar (m). " +
                 "Fiziksel zemin metrelerce oynamaz; anchor oyle diyorsa yanilan ANCHOR'dir. " +
                 "Bu sinir olmadan bir relocalization hatasi oyuncuyu tavana ya da zeminin altina " +
                 "isinlayabiliyor (yasanmis hata).")]
        public float maxVerticalCorrection = 0.5f;

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
        bool _verticalClamped;
        string _shareNote;   // paylasim neden olmadi — gozlukte log okunamadigi icin panele yazilir

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

                ShareAnchor(mgr);   // FAZ 2 — ayni odadaki digerlerine ac
            }
            finally
            {
                _busy = false;
            }
        }

        // ------------------------------------------------------------------ FAZ 2: paylasim

        /// <summary>
        /// Anchor'i bir grup GUID'i altinda paylasir ve GUID'i aga duyurur. Boylece ayni fiziksel
        /// odadaki diger gozlukler AYNI anchor'a kilitlenir — herkes bagimsiz kaydigi icin olusan
        /// ayrisma biter. Desteklenmiyorsa sessizce gecilir: yerel drift duzeltmesi calismaya
        /// devam eder, sadece paylasim olmaz.
        /// </summary>
        async void ShareAnchor(ARAnchorManager mgr)
        {
            if (mgr.subsystem is not MetaOpenXRAnchorSubsystem sub)
            {
                _shareNote = "subsystem Meta degil";
                Debug.LogWarning("[CalibAnchor] Anchor subsystem Meta degil — paylasim yok.");
                ShowStatus(panelSecondsAfterCalibration);
                return;
            }
            if (sub.isSharedAnchorsSupported != Supported.Supported)
            {
                // EN SIK SEBEP: gozlukte "Enhanced Spatial Services" kapali
                // (Ayarlar > Gizlilik ve Guvenlik > Cihaz Izinleri). Bu ayar olmadan Meta
                // paylasilan anchor'a izin vermez. Ayrica paylasim Meta sunucularindan gectigi
                // icin INTERNET de gerekir — internetsiz mekanda bu yol calismaz.
                _shareNote = "DESTEKLENMIYOR — gozlukte 'Enhanced Spatial Services' ac";
                Debug.LogWarning($"[CalibAnchor] Shared anchor DESTEKLENMIYOR " +
                                 $"({sub.isSharedAnchorsSupported}). Gozlukte Ayarlar > Gizlilik ve " +
                                 "Guvenlik > Cihaz Izinleri > Enhanced Spatial Services acik mi? " +
                                 "Ayrica paylasim internet gerektirir. Yerel drift duzeltmesi surüyor.");
                ShowStatus(panelSecondsAfterCalibration);
                return;
            }

            var groupId = Guid.NewGuid();
            sub.sharedAnchorsGroupId = new SerializableGuid(groupId);

            var status = await mgr.TryShareAnchorAsync(_anchor);
            if (this == null) return;

            if (!status.IsSuccess())
            {
                _shareNote = $"paylasim HATASI ({status})";
                Debug.LogWarning($"[CalibAnchor] Anchor PAYLASILAMADI (status={status}). " +
                                 "Enhanced Spatial Services ve internet baglantisini kontrol et.");
                ShowStatus(panelSecondsAfterCalibration);
                return;
            }

            _shareNote = null;
            Debug.Log($"[CalibAnchor] Anchor paylasildi, grup {groupId:N}. Aga duyuruluyor.");
            CalibrationShareSync.Publish(groupId);
            ShowStatus(panelSecondsAfterCalibration);
        }

        /// <summary>
        /// Agdan bir ortak cerceve GUID'i geldiginde cagrilir: o gruptaki anchor'i yukler ve rig'i
        /// ondan surmeye baslar. Bu yolu izleyen oyuncu A/B'ye HIC BASMAZ (karar K2).
        /// </summary>
        public static void LoadShared(Guid groupId)
        {
            var rig = FindRig();
            if (rig == null)
            {
                Debug.LogWarning("[CalibAnchor] Rig bulunamadi — ortak cerceve yuklenemiyor.");
                return;
            }

            if (Instance == null)
            {
                var go = new GameObject("Calibration Anchor");
                Instance = go.AddComponent<CalibrationAnchor>();
            }
            Instance.LoadSharedInternal(rig, groupId);
        }

        async void LoadSharedInternal(Transform rig, Guid groupId)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var mgr = EnsureAnchorManager();
                if (mgr == null) return;
                if (mgr.subsystem is not MetaOpenXRAnchorSubsystem sub)
                {
                    Debug.LogWarning("[CalibAnchor] Anchor subsystem Meta degil — ortak cerceve yuklenemez.");
                    return;
                }

                sub.sharedAnchorsGroupId = new SerializableGuid(groupId);

                var loaded = new List<XRAnchor>();
                var status = await mgr.TryLoadAllSharedAnchorsAsync(loaded, null);
                if (this == null) return;

                if (!status.IsSuccess() || loaded.Count == 0)
                {
                    // Bos donus BASARILI sayilir (API sozlesmesi) — "henuz paylasilmadi" ya da
                    // "runtime bu odada henuz localize olmadi" demektir. A/B yedegi devrede kalir.
                    Debug.LogWarning($"[CalibAnchor] Ortak anchor YUKLENEMEDI " +
                                     $"(status={status}, adet={loaded.Count}). A/B yedegi gecerli.");
                    return;
                }

                // Yuklenen XRAnchor bir VERI yapisi; surus icin ARAnchor BILESENI lazim ve o,
                // manager'in bir sonraki guncellemesinde olusur — birkac kare bekle.
                var id = loaded[0].trackableId;
                ARAnchor comp = null;
                for (int i = 0; i < 180 && comp == null; i++)
                {
                    await Awaitable.NextFrameAsync();
                    if (this == null) return;
                    mgr.trackables.TryGetTrackable(id, out comp);
                }
                if (comp == null)
                {
                    Debug.LogWarning($"[CalibAnchor] Ortak anchor yuklendi ama ARAnchor bileseni " +
                                     $"olusmadi ({id}).");
                    return;
                }

                Adopt(rig, comp);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Agdan gelen anchor'i sahiplen ve surusu baslat. Hedef poz A/B yolundakiyle
        /// AYNI sozlesmedir (CalibrationManager.SharedTargetPose).</summary>
        void Adopt(Transform rig, ARAnchor anchor)
        {
            var cm = FindFirstObjectByType<CalibrationManager>();
            _target = cm != null
                ? cm.SharedTargetPose
                : new Pose(Vector3.zero, Quaternion.identity);

            _rig = rig;
            _baselineRigY = rig.position.y;
            _rigPosAtCalib = rig.position;
            _rigYawAtCalib = rig.eulerAngles.y;
            _lastAnnouncedDrift = 0f;
            _rejectedFrames = 0;
            _wasTracking = true;
            _nextHeartbeat = Time.time + heartbeatSeconds;

            if (_anchor != null && _anchor != anchor) Destroy(_anchor.gameObject);
            _anchor = anchor;
            _driving = true;

            Debug.Log($"[CalibAnchor] ORTAK cerceve benimsendi ({anchor.trackableId}). " +
                      "A/B'ye gerek yok — rig agdan gelen anchor'dan suruluyor.");
            ShowStatus(panelSecondsAfterCalibration);
        }

        static Transform FindRig()
        {
            var cm = FindFirstObjectByType<CalibrationManager>();
            if (cm != null && cm.rig != null) return cm.rig;
            var rigRef = XRRigReference.Instance;
            return rigRef != null ? rigRef.transform : null;
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

            // DIKEY GUVENLIK KILIDI. Yataydan farkli ele alinir: yatayda buyuk bir duzeltme
            // israr ederse "demek gercekmis" deyip kabul ediyoruz, ama dikeyde bu YANLISTIR —
            // zemin yerinde durur. Siniri asan dikey duzeltme kabul edilmez, kirpilir.
            if (!correctVertical)
            {
                pos.y = _baselineRigY;
            }
            else
            {
                float lo = _baselineRigY - maxVerticalCorrection;
                float hi = _baselineRigY + maxVerticalCorrection;
                if (pos.y < lo || pos.y > hi)
                {
                    if (!_verticalClamped)
                    {
                        _verticalClamped = true;
                        Debug.LogWarning($"[CalibAnchor] DIKEY duzeltme sinir disi " +
                                         $"({pos.y - _baselineRigY:0.00} m) — kirpildi. Anchor'in " +
                                         "dikey referansi bozulmus olabilir (relocalization).");
                        ShowStatus(8f);
                    }
                    pos.y = Mathf.Clamp(pos.y, lo, hi);
                }
                else if (_verticalClamped)
                {
                    _verticalClamped = false;
                }
            }

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

            // DIKEY TESHIS — "tavanda doguyorum" sorununun KAYNAGINI ayirir:
            //   Kafa ~= gercek boyun     -> tracking saglam, hata rig Y'sinde (KOD)
            //   Kafa >> gercek boyun     -> gozlugun ZEMIN TAHMINI yanlis (CIHAZ; Alan Kurulumu)
            // Rig Y ayrica anchor surusunun dikeyde ne yaptigini dogrudan gosterir.
            var head = XRRigReference.HeadOrCamera;
            float rigY = _rig != null ? _rig.position.y : 0f;
            float headH = (head != null && _rig != null)
                ? head.position.y - _rig.position.y
                : (head != null ? head.position.y : 0f);

            // FAZ 2: iki gozlugun AYNI grubu gorup gormedigini gozle dogrulamanin tek yolu.
            // Ilk 6 hane karsilastirmak yeterli.
            string sharedLine;
            if (CalibrationShareSync.HasSharedCalibration && CalibrationShareSync.ServerConfirmed)
            {
                sharedLine = "ORTAK " + CalibrationShareSync.ActiveGroupId.ToString("N").Substring(0, 6);
            }
            else if (CalibrationShareSync.HasSharedCalibration)
            {
                // GUID uretildi ama sunucu onaylamadi -> bu cerceve bize OZEL. Eskiden burada da
                // "ORTAK" yaziyordu ve iki oyuncu da kendini ilk kalibre eden saniyordu.
                sharedLine = "YALNIZ SEN — sunucu onayi YOK";
                color = new Color(1f, 0.75f, 0.2f);
            }
            else if (!string.IsNullOrEmpty(_shareNote))
            {
                sharedLine = "YEREL — " + _shareNote;
                color = new Color(1f, 0.75f, 0.2f);
            }
            else
            {
                sharedLine = "yerel (paylasilmadi)";
            }

            _panel.color = color;
            _panel.text =
                "KALIBRASYON DURUMU\n" +
                "Anchor: " + anchorLine + "\n" +
                "Cerceve: " + sharedLine + "\n" +
                "Dikey ref: " + floorLine + "\n" +
                "Dikey duzeltme: " + (correctVertical
                    ? (_verticalClamped ? "ACIK — SINIRA DAYANDI!" : "ACIK")
                    : "KAPALI") + "\n" +
                $"Toplam duzeltme: {dPos * 100f:0.0} cm / {dYaw:0.0} derece\n" +
                $"Rig Y: {rigY:0.00} m   Kafa: {headH:0.00} m";
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
