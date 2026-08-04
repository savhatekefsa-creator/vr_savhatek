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
        public Vector3 sharedOrigin = Vector3.zero;
        public Vector3 sharedForward = Vector3.forward;

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
            _step = 0;
            _note = string.IsNullOrEmpty(note) ? "" : "\n\n" + note;

            // Tetik BASILI halde giriyor olabiliriz: mod paneli de takim karti da tetikle
            // tiklaniyor. Kenari simdiki fiziksel duruma esitlemeden baslarsak, henuz
            // birakilmamis o tetik bir sonraki karede "A alindi" diye okunur ve kalibrasyon
            // oyuncunun eli havadayken, menuye nisan alirken baslar.
            _prevTrigger = ReadRightTrigger();

            SetStatus("KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
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

            // The trigger only captures points DURING calibration. Once done it is ignored, so
            // an accidental trigger pull mid-game can never ruin the alignment.
            if (triggerEdge && _step < 2) CapturePoint();

            // Re-calibration is armed only by the LEFT controller's Y button.
            if (yEdge && _step == 2)
            {
                _step = 0;
                SetStatus("YENIDEN KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
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
            Vector3 delta = sharedOrigin - a; delta.y = 0f;
            rig.position += delta;

            _step = 2;
            Calibrated = true;

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

        IEnumerator HideAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (status != null) status.gameObject.SetActive(false);
        }

        bool ReadRightTrigger() => XRButtons.Button(XRNode.RightHand, CommonUsages.triggerButton);

        bool ReadLeftY() => XRButtons.Button(XRNode.LeftHand, CommonUsages.secondaryButton);

        void SetStatus(string s)
        {
            if (status != null)
            {
                status.gameObject.SetActive(true);
                status.text = s + _note;
            }
            Debug.Log("[Calibration] " + s);
        }
    }
}
