using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Two-point manual colocation calibration. Everyone in the SAME physical room aligns to a
    /// shared physical reference, so real-world distance == in-game distance.
    ///
    /// DORMANT until <see cref="Begin"/> is called (the team selector calls it after the player
    /// picks a team), so the onboarding order is: join -> pick team -> calibrate -> play.
    ///
    /// Flow (each player):
    ///   1) Put the RIGHT controller on physical point A (the shared origin) and pull the TRIGGER.
    ///   2) Move it to physical point B (which defines the forward direction) and pull the TRIGGER.
    /// The rig recenters so A maps to <see cref="sharedOrigin"/> and A->B maps to
    /// <see cref="sharedForward"/>. Pull the trigger again to re-calibrate.
    /// </summary>
    public class CalibrationManager : MonoBehaviour
    {
        [Tooltip("The XR rig to recenter (defaults to this GameObject).")]
        public Transform rig;
        [Tooltip("The right-controller anchor whose world position marks points A and B.")]
        public Transform pointer;
        public TextMesh status;

        [Header("Shared virtual reference (MUST be the same on every headset)")]
        public Vector3 sharedOrigin = Vector3.zero;
        public Vector3 sharedForward = Vector3.forward;

        [Header("Dikey gercek (Quest'in zemin tahmini yanilirsa)")]
        [Tooltip("ACIK ise A noktasinin OLCULEN yuksekligi dikey referans olur ve gozlugun kendi " +
                 "zemin tahmini EZILIR. Quest zemini bazen metrelerce yanlis biliyor (olculen: " +
                 "kafa 2.47 m goruluyordu). KAPALIYKEN eski davranis: dikey hic duzeltilmez.\n\n" +
                 "ACMADAN ONCE pointAHeight'i metreyle olcup girin — yanlis olcum HERKESI bozar, " +
                 "cunku duzeltme shared anchor uzerinden digerlerine de gecer.")]
        public bool useMeasuredPointAHeight = false;

        [Tooltip("A noktasinin ZEMINDEN olculmus yuksekligi (metre). Metreyle olcun, tahmin etmeyin. " +
                 "Ornek: duvarda gogus hizasinda bir isaret icin ~1.00.")]
        public float pointAHeight = 1.00f;

        /// <summary>
        /// Ortak cerceve hedefi: fiziksel A noktasi <see cref="sharedOrigin"/>'e, A->B yonu
        /// <see cref="sharedForward"/>'a eslenir. HEM A/B yolu HEM de agdan gelen shared anchor
        /// ayni hedefi kullanir — plan tasarim kurali #4 (ortak cerceve sozlesmesi degismez).
        /// </summary>
        public Pose SharedTargetPose
        {
            get
            {
                Vector3 fwd = new Vector3(sharedForward.x, 0f, sharedForward.z).normalized;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                return new Pose(sharedOrigin, Quaternion.LookRotation(fwd, Vector3.up));
            }
        }

        bool _started;
        int _step;            // 0 = waiting for A, 1 = waiting for B, 2 = done
        Vector3 _a;
        [Tooltip("Takim secildikten sonra ORTAK kalibrasyonun agdan gelmesi icin beklenecek sure " +
                 "(saniye). Bu sure boyunca A/B istenmez. Gelmezse A/B'ye dusulur.")]
        public float sharedWaitSeconds = 12f;

        bool _prevTrigger;
        bool _prevY;
        bool _manualOverride;   // oyuncu Y ile bilerek yeniden kalibrasyon istedi
        bool _waitingShared;    // ortak cerceve bekleniyor, A/B henuz istenmedi
        float _sharedDeadline;

        /// <summary>True once this player has completed A/B calibration at least once.
        /// The room-scan sender requires this, otherwise the scan would be recorded in
        /// device-local coordinates instead of the shared frame.</summary>
        public static bool Calibrated { get; private set; }

        void Start()
        {
            if (rig == null) rig = transform;
            if (status != null) status.gameObject.SetActive(false); // hidden until Begin()
        }

        /// <summary>Starts the calibration step (called after the player picked a team).</summary>
        public void Begin()
        {
            if (_started) return;
            _started = true;

            // FAZ 3: TAG oyuncu katilmadan ONCE kalibre etmis olabilir — AprilTagCalibration
            // sahne basindan beri calisir, takim secimini beklemez. Asagidaki sifirlama o
            // durumda oyuncuyu bekleme ekranina ve A/B'ye GERI DONDURUYORDU (Calibrated true
            // kalsa bile _step 0'a dusunce tetik yeniden A/B noktasi yakalamaya baslardi).
            if (Calibrated)
            {
                _step = 2;
                _waitingShared = false;
                SetStatus("TAG ILE KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
                StartCoroutine(HideAfter(6f));
                Debug.Log("[Calibration] Katilimda zaten kalibreydi (tag) — A/B istenmedi.");
                return;
            }

            _step = 0;

            // FAZ 2: once ORTAK cerceveyi bekle. Sunucuda hazir bir kalibrasyon varken oyuncuyu
            // bos yere A noktasina yollamak yanlisti — ustelik ortak cerceve sonradan gelince
            // oyuncu iki kez kalibre etmis oluyordu.
            _waitingShared = true;
            _sharedDeadline = Time.time + sharedWaitSeconds;
            SetStatus("ORTAK KALIBRASYON BEKLENIYOR...\n\nBirisi kalibre ettiyse otomatik gelecek.\n" +
                      "Gelmezse A/B istenecek.");
        }

        void Update()
        {
            if (!_started) return;

            FollowHead();

            // FAZ 2 (karar K2): ortak cerceve agdan geldiyse A/B'ye HIC BASMA. Anchor zaten rig'i
            // suruyorsa kalibrasyon tamamdir; oyuncuyu bos yere A noktasina yollamanin anlami yok.
            // _manualOverride: oyuncu Y ile BILEREK yeniden kalibrasyon istediyse geri snap etme.
            // ServerConfirmed sart: onaylanmamis (ag yok) bir cerceveyle A/B atlanirsa oyuncu
            // "ortak kalibrasyon geldi" sanip aslinda yalniz kalir.
            bool sharedReady = CalibrationAnchor.Driving &&
                               CalibrationShareSync.HasSharedCalibration &&
                               CalibrationShareSync.ServerConfirmed;

            if (_step < 2 && !_manualOverride && sharedReady)
            {
                _waitingShared = false;
                AdoptShared();
                return;
            }

            // Bekleme penceresi: bu sure boyunca TETIK DINLENMEZ, oyuncudan A/B istenmez.
            if (_waitingShared)
            {
                if (Time.time < _sharedDeadline) return;
                _waitingShared = false;
                SetStatus("ORTAK KALIBRASYON GELMEDI\n\nSag kumandayi A noktasina koy,\nTETIGE bas.");
                Debug.Log("[Calibration] Ortak cerceve gelmedi — A/B yedegine dusuldu.");
            }

            // The trigger only captures points DURING calibration. Once done it is ignored, so
            // an accidental trigger pull mid-game can never ruin the alignment.
            bool trigger = ReadRightTrigger();
            if (trigger && !_prevTrigger && _step < 2)   // rising edge
                CapturePoint();
            _prevTrigger = trigger;

            // Re-calibration is armed only by the LEFT controller's Y button.
            bool y = ReadLeftY();
            if (y && !_prevY && _step == 2)
            {
                _step = 0;
                _manualOverride = true;   // agdan gelen cerceve bu istegi ezmesin
                SetStatus("YENIDEN KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
            }
            _prevY = y;
        }

        // Panel takibi HeadFollowPanel bileseninde (obje inaktifken calismaz — eski
        // activeSelf kontrolu ile ayni davranis); burada yalnizca bir kez takilir.
        void FollowHead() => UI.HeadFollowPanel.Attach(status);

        void CapturePoint()
        {
            if (pointer == null) return;
            StopAllCoroutines(); // cancel a pending auto-hide
            Vector3 p = pointer.position;

            switch (_step)
            {
                case 0:
                    _a = p;
                    _step = 1;
                    SetStatus("A alindi.\nSimdi B noktasina koy (yon icin),\nTETIGE bas.");
                    break;
                case 1:
                    Apply(_a, p);
                    break;
            }
        }

        void Apply(Vector3 a, Vector3 b)
        {
            Vector3 dir = b - a; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
            {
                SetStatus("A ve B cok yakin.\nDaha uzak bir B sec, tetige bas.");
                _step = 1;
                return;
            }
            dir.Normalize();

            Vector3 fwd = new Vector3(sharedForward.x, 0f, sharedForward.z).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;

            // Rotate the whole rig around A so the physical A->B direction lines up with forward,
            // then slide (horizontally) so A sits on the shared origin.
            float angle = Vector3.SignedAngle(dir, fwd, Vector3.up);
            rig.RotateAround(a, Vector3.up, angle);

            Vector3 delta = sharedOrigin - a;
            if (useMeasuredPointAHeight)
            {
                // DIKEY GERCEK: kumanda su an fiziksel A noktasinda, ve A'nin zeminden yuksekligini
                // METREYLE olcup biliyoruz. Gozluk "a.y" icin baska bir sey soyluyorsa yanilan
                // gozlugun ZEMIN TAHMINIDIR — olcumu ustun tutariz.
                // Sonucta dunya y=0 GERCEK zemin olur; anchor da oraya konuldugu icin bu duzeltme
                // shared anchor uzerinden diger oyunculara da gecer.
                delta.y = (sharedOrigin.y + pointAHeight) - a.y;
                Debug.Log($"[Calibration] Dikey duzeltme: A olculen {pointAHeight:0.00} m, " +
                          $"gozlugun dedigi {a.y:0.00} m -> rig {delta.y:+0.00;-0.00} m kaydirildi.");
            }
            else
            {
                delta.y = 0f;   // eski davranis: dikeyi Floor tracking origin'e birak
            }
            rig.position += delta;

            _step = 2;
            Calibrated = true;

            // FAZ 1: tek seferlik hizalama bitti — anchor omurgasi devralir ve rig'i bundan
            // sonra her karede anchor'in SESSION pozundan yeniden turetir, boylece SLAM
            // kaymasi (drift) surekli telafi edilir. Anchor olusturulamazsa (destek yok /
            // ozellik kapali) yukaridaki tek seferlik sonuc aynen gecerli kalir — regresyon yok.
            CalibrationAnchor.Bind(rig, SharedTargetPose);

            // The player is standing, headset on, holding still to take point B — the one moment
            // we can be sure a height sample is a STANDING sample. Re-run the avatar height fit
            // here so a session that started with the headset on a table (or was calibrated
            // while kneeling) corrects itself without a restart.
            AvatarIKController.RecalibrateAll();

            SetStatus("KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
            StartCoroutine(HideAfter(6f));
        }

        /// <summary>
        /// FAZ 2 / karar K2 — ortak cerceve agdan geldi, A/B atlanir.
        ///
        /// A/B yolundan farkli olarak <c>AvatarIKController.RecalibrateAll()</c> BILEREK
        /// cagrilmaz: orada oyuncunun B noktasini alirken dik durdugu bilinir, burada ne yaptigi
        /// bilinmez. Rastgele bir pozdan boy olcumu yapmak yanlis avatar yuksekligi uretirdi.
        /// </summary>
        void AdoptShared()
        {
            _step = 2;
            Calibrated = true;
            StopAllCoroutines();
            SetStatus("KALIBRASYON AGDAN GELDI\nA/B'ye gerek yok.\n(Yeniden kalibre: SOL kumanda Y tusu)");
            StartCoroutine(HideAfter(6f));
            Debug.Log("[Calibration] Ortak cerceve agdan alindi — A/B atlandi.");
        }

        /// <summary>
        /// FAZ 3 — cerceve TAG'den kuruldu. <see cref="AprilTagCalibration"/> rig'i ZATEN
        /// hizaladi; burada yalnizca kalibrasyon DURUMU tamamlanir, rig'e dokunulmaz.
        ///
        /// Neden gerekli: d1176d6 <c>CalibrationAnchor.Bind</c> cagrisini cihazda dogrulayarak
        /// KALDIRDI (anchor tracking 'None' iken ise yaramiyor, ustelik LateUpdate'te tag'in
        /// duzeltmesini eziyordu). Ama <see cref="Calibrated"/> bayragini kaldiran tek yol o
        /// zincirdi (Bind -> paylas -> sunucu -> geri push -> AdoptShared). Zincir kopunca tag
        /// dogru hizalasa bile oyun "kalibre degil" sanip A/B ekraninda bekletiyordu.
        ///
        /// <see cref="CalibrationAnchor.Bind"/> BILEREK cagrilmaz — kaldirilma gerekcesi hala
        /// gecerli. Tag'in KENDISI surekli referans; anchor omurgasi uyandirilmaz.
        ///
        /// <c>AvatarIKController.RecalibrateAll()</c> de cagrilmaz, <see cref="AdoptShared"/>
        /// ile ayni gerekce: A/B'de oyuncunun B noktasini alirken dik durdugu BILINIR, tag'e
        /// bakarken ne yaptigi (egilmis, uzanmis) bilinmez. Rastgele pozdan boy olcmek yanlis
        /// avatar yuksekligi uretirdi.
        /// </summary>
        public void CompleteFromTag()
        {
            if (Calibrated) return;   // zaten kalibre (A/B ya da agdan) — tag duzeltmeye devam eder
            _step = 2;
            Calibrated = true;
            _waitingShared = false;
            if (_started)
            {
                StopAllCoroutines();
                SetStatus("TAG ILE KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
                StartCoroutine(HideAfter(6f));
            }
            Debug.Log("[Calibration] Cerceve TAG'den kuruldu — A/B atlandi.");
        }

        IEnumerator HideAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (status != null) status.gameObject.SetActive(false);
        }

        bool ReadRightTrigger() => XRButtons.Button(XRNode.RightHand, CommonUsages.triggerButton);

        bool ReadLeftY() => XRButtons.Button(XRNode.LeftHand, CommonUsages.secondaryButton);

        void SetStatus(string s)
        {
            // Panel sahnede ATANMAMISSA runtime'da olustur. Aksi halde kalibrasyon mesajlari
            // yalnizca log'a giderdi; gozlukte Console olmadigi icin oyuncu HICBIR SEY gormez
            // ve "kalibre olmus gibi" sanip yanlis cerceveyle oynardi (yasanmis).
            if (status == null)
                // "~": kalibrasyon durumunu ve "yeniden kalibre: SOL Y" talimatini tasiyor.
                status = UI.HeadFollowPanel.Create("~Calibration Panel", "", Color.white);

            status.gameObject.SetActive(true);
            status.text = s;
            Debug.Log("[Calibration] " + s);
        }
    }
}
