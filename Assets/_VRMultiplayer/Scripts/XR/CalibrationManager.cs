using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Two-point manual colocation calibration. Everyone in the SAME physical room aligns to a
    /// shared physical reference, so real-world distance == in-game distance.
    ///
    /// DORMANT until <see cref="Begin"/> is called, so each flow decides WHEN calibration is due:
    ///   OYUNCU  : join -> pick team -> calibrate -> play   (<see cref="TeamSelector"/>)
    ///   YARATICI: mod sec -> calibrate -> insa modu         (<see cref="Constructor.ConstructorPlacer"/>)
    /// Yaratici tarafta bu bir kolaylik degil SART: harita kalibre cercevede orulur, hem de
    /// insa modu tetigi zaten "prop koy" olarak kullanir.
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
        [Tooltip("YALNIZCA TAG ile kalibre ol; A/B dokunma yolu tamamen kapali.\n\n" +
                 "NEDEN: iki yontem IKI FARKLI FIZIKSEL NOKTAYI sifir kabul ediyor — A/B'de " +
                 "yerdeki A isareti, tag'de duvardaki tag 0'in merkezi. Ikisi ayni yerde " +
                 "olmadigi icin iki ayri dunya cikiyor ve ayni harita, hangi yontemle kalibre " +
                 "olundguna gore metrelerce kayabiliyor.\n\n" +
                 "Harita dosyasi hangi cercevede kuruldugunu KAYDETMIYOR, yani kayma dosyaya " +
                 "bakarak da anlasilamaz. Tek cerceve = tek sifir.\n\n" +
                 "Kapatirsan A/B geri gelir; o zaman A noktasinin tag 0'in ilan edilen " +
                 "konumuyla ayni fiziksel yerde olmasi gerekir.")]
        public bool tagOnly = true;

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
        bool _prevTrigger;
        bool _prevY;
        string _note = "";    // "neden simdi kalibre oluyorum" — panelin altina eklenir

        /// <summary>True once this player has completed A/B calibration at least once.
        /// The room-scan sender requires this, otherwise the scan would be recorded in
        /// device-local coordinates instead of the shared frame.</summary>
        public static bool Calibrated { get; private set; }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir. Calibrated true kalirsa
        // ikinci Play'de hem oda gonderme hem insa modu kapisi "zaten kalibre" der — oysa rig
        // hicbir ortak cerceveye oturmamistir. AppMode.ResetStatics ile ayni gerekce.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Calibrated = false;

        void Start()
        {
            if (rig == null) rig = transform;
            if (status != null) status.gameObject.SetActive(false); // hidden until Begin()
        }

        /// <summary>Starts the calibration step (the team selector calls it after the player
        /// picked a team; the constructor calls it before it opens build mode).</summary>
        /// <param name="note">Bu kalibrasyonun NEDEN istendigi. Panelin altina eklenir ve
        /// kalibrasyon bitince dusulur. Cagiran taraf kendi paneliyle aciklamasin: iki panel de
        /// kafanin 1.4 m onunde durur, ust uste binerler.</param>
        public void Begin(string note = null)
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
                SetStatus("TAG ILE KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
                StartCoroutine(HideAfter(6f));
                Debug.Log("[Calibration] Katilimda zaten kalibreydi (tag) — A/B istenmedi.");
                return;
            }

            _step = 0;
            _note = string.IsNullOrEmpty(note) ? "" : "\n\n" + note;

            // Tetik BASILI halde giriyor olabiliriz: mod paneli de takim karti da tetikle
            // tiklaniyor. Kenari simdiki fiziksel duruma esitlemeden baslarsak, henuz
            // birakilmamis o tetik bir sonraki karede "A alindi" diye okunur ve kalibrasyon
            // oyuncunun eli havadayken, menuye nisan alirken baslar.
            _prevTrigger = ReadRightTrigger();

            SetStatus(tagOnly
                ? "KALIBRASYON\nDuvardaki TAG'e bak\nve 1-2 metre yaklas."
                : "KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
        }

        /// <summary>Paneli hemen gizler (bekleyen otomatik gizlemeyi de iptal eder). Kalibrasyondan
        /// SONRA baska bir panel acacak akislar icin sart — ikisi de kafanin onunde durur.</summary>
        public void HideStatus()
        {
            StopAllCoroutines();
            if (status != null) status.gameObject.SetActive(false);
        }

        void Update()
        {
            if (!_started) return;

            FollowHead();

            // KENARLAR HER KARE OKUNUR, kapi SONRA uygulanir. Insa modu ayni tuslari yeniden
            // anlamlandiriyor (tetik orada "prop koy"), o yuzden asagida susturuluyorlar — ama
            // okumayi da atlasaydik, mod kapanirken basili duran bir tus hayalet bir basis
            // uretirdi. (ConstructorPlacer'in mod kapisindaki ayni ders.)
            bool trigger = ReadRightTrigger();
            bool triggerEdge = trigger && !_prevTrigger;
            _prevTrigger = trigger;

            bool y = ReadLeftY();
            bool yEdge = y && !_prevY;
            _prevY = y;

            if (XRButtons.GameplayInputSuppressed) return;

            // TAG-ONLY: A/B yolu tamamen kapali. Tetik hicbir nokta yakalamaz.
            //
            // NEDEN KAPATILDI: iki yontem IKI FARKLI FIZIKSEL NOKTAYI sifir kabul ediyor —
            // A/B'de yerdeki A isareti, tag'de duvardaki tag 0'in merkezi. Aralarindaki mesafe
            // kadar iki ayri dunya cikiyor ve ayni harita hangi yontemle kalibre olduguna gore
            // metrelerce kayabiliyor. Harita dosyasi hangi cercevede kuruldugunu KAYDETMIYOR,
            // yani kayma dosyaya bakarak da anlasilamaz. Tek cerceve = tek sifir.
            if (!tagOnly && triggerEdge && _step < 2) CapturePoint();

            // Yeniden kalibrasyon: SOL kumanda Y.
            if (yEdge && _step == 2)
            {
                _step = 0;
                if (tagOnly)
                {
                    // Tag surekli duzeltiyor; "yeniden kalibre" burada yalnizca DURUM
                    // bayragini birakmak demek, boylece tag bir sonraki tespitte cerceveyi
                    // bastan kurar ve panel yeniden ilerleme gosterir.
                    Calibrated = false;
                    SetStatus("YENIDEN KALIBRASYON\nDuvardaki TAG'e bak\nve 1-2 metre yaklas.");
                }
                else
                {
                    SetStatus("YENIDEN KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
                }
            }
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

            // Not "neden kalibre oluyoruz" diyordu; is bitti, artik yaniltici olur. Sirayi
            // bekleyen akis (or. insa modu) kendi panelini bundan sonra acar.
            _note = "";
            SetStatus("KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
            StartCoroutine(HideAfter(6f));
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
        /// <c>AvatarIKController.RecalibrateAll()</c> de cagrilmaz: A/B'de oyuncunun B noktasini
        /// alirken dik durdugu BILINIR, tag'e bakarken ne yaptigi (egilmis, uzanmis) bilinmez.
        /// Rastgele pozdan boy olcmek yanlis avatar yuksekligi uretirdi.
        /// </summary>
        public void CompleteFromTag()
        {
            if (Calibrated) return;   // zaten kalibre — tag duzeltmeye devam eder
            _step = 2;
            Calibrated = true;
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
            //
            // "~" oneki: ConstructorPassthrough.HideVirtualWorld cizen kok objeleri gizliyor;
            // bu panel kalibrasyon durumunu ve "yeniden kalibre: SOL Y" talimatini tasidigi
            // icin passthrough acikken de gorunmeli.
            if (status == null)
                status = UI.HeadFollowPanel.Create("~Calibration Panel", "", Color.white);

            status.gameObject.SetActive(true);
            status.text = s + _note;
            Debug.Log("[Calibration] " + s);
        }
    }
}
