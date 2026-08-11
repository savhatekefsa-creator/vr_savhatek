using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// MOD SECIMI ekraninin AKISI: <see cref="ModeSelectPanel"/>'i kurar, lazer imleci besler
    /// ve secimi <see cref="AppMode"/>'a yazar. Uygulamanin ilk ekrani; oyuncu girisi
    /// (<see cref="PlayerEntryUI"/>) artik ancak buradan OYUNCU secilince dogar.
    ///
    /// Sahneye/prefaba DOKUNMAZ: kendini <see cref="RuntimeInitializeOnLoadMethod"/> ile kurar
    /// (ayni desen: <see cref="PlayerEntryUI"/>, <see cref="WeaponBeltUI"/>,
    /// <see cref="SingleAudioListener"/>). Yaratici mod ayri bir dalda gelistigi icin bu ONEMLI:
    /// sahne dosyasina dokunmayan iki dal catismaz.
    ///
    /// KOK OBJE KALICI, panel gecici: <see cref="AppMode.ReturnToModeSelect"/> (semadaki DONUS)
    /// cagrildiginda paneli yeniden kurabilmesi gerekiyor. Bu yuzden secim yapilinca yalnizca
    /// panel + imlec yok edilir, bilesen yasamaya devam eder.
    ///
    /// KIMLERE GOSTERILIR: yalnizca XR sag el cihazi gecerliyse (<see cref="LanBootstrap"/>'in
    /// katilim panelini kapiladigi ayni kosul). PC'de dunya paneli hic dogmaz — PC adanmis
    /// SUNUCU olarak calisiyor, mod secmesi anlamsiz. Gozluksuz iterasyon icin masaustu
    /// <see cref="OnGUI"/> yedegi var; PlayerEntryUI'in kendi yedegi artik yalnizca mod
    /// secildikten SONRA dogdugu icin bu yedek zorunlu, susleme degil.
    ///
    /// PANEL DUNYAYA SABIT, kafaya kilitli DEGIL — lazerle nisan alinan bir yuzey her kare
    /// kafayla kayarsa tusa basmak imkansizlasir. Giris ekranindaki TEMBEL TAKIP kopyalandi.
    /// </summary>
    public class ModeSelectUI : MonoBehaviour
    {
        [Tooltip("Panelin kafadan uzakligi (metre).")]
        public float distance = 1.4f;
        [Tooltip("Panel merkezinin goz hizasina gore dusuklugu (metre). Giris ekranindan az: " +
                 "bu panel 0.44 m yuksek, oteki 0.73.")]
        public float heightDrop = 0.05f;
        [Tooltip("Panelin one dogru yatma acisi (derece).")]
        public float tiltDegrees = 5f;

        /// <summary>Panel bu acidan fazla yana kalirsa onune geri getirilir.</summary>
        const float RecenterAngle = 38f;
        const float RecenterSpeed = 3.5f;

        ModeSelectPanel _panel;
        VRPointer _pointer;
        bool _recentering;
        bool _placed;      // kafa transformu hazir olana kadar yerlestirme ANLIK kalir

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~ModeSelectUI");
            DontDestroyOnLoad(go);
            go.AddComponent<ModeSelectUI>();
        }

        void OnEnable()  => AppMode.Chosen += OnModeChanged;
        void OnDisable() => AppMode.Chosen -= OnModeChanged;

        // DONUS kancasi: mod None'a dondugunde panel yeniden acilir. Su an cagiran yok
        // (MatchManager gelince mac sonu ekrani cagiracak) ama kanca olu degil — calisiyor.
        void OnModeChanged(AppMode.Mode m)
        {
            if (m == AppMode.Mode.None) return;   // panel Update'te yeniden kurulur
            Close();
        }

        void Update()
        {
            if (AppMode.Current != AppMode.Mode.None) return;

            // Kumanda Start aninda henuz gecerli olmayabilir (LanBootstrap de her kare bakiyor).
            if (_panel == null)
            {
                if (!InputDevices.GetDeviceAtXRNode(XRNode.RightHand).isValid) return;
                Open();
            }

            PlacePanel(instant: false);
            _panel.Tick(_pointer);
        }

        void Open()
        {
            var panelGo = new GameObject("Mode Select Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<ModeSelectPanel>();
            _panel.Selected += OnSelected;

            var pointerGo = new GameObject("UI Pointer");
            pointerGo.transform.SetParent(transform, false);
            _pointer = pointerGo.AddComponent<VRPointer>();

            _placed = false;
            _recentering = false;
            PlacePanel(instant: true);
        }

        void Close()
        {
            if (_panel != null)
            {
                _panel.Selected -= OnSelected;
                Destroy(_panel.gameObject);
                _panel = null;
            }
            if (_pointer != null)
            {
                Destroy(_pointer.gameObject);
                _pointer = null;
            }
        }

        // Panel yalnizca NIYETI bildirir; uygulama durumunu burasi yazar. AppMode.Choose
        // olayi tetikler, olay da Close()'u cagirir — panel kendi kendini kapatmaz.
        void OnSelected(AppMode.Mode m) => AppMode.Choose(m);

        // Tembel takip: panel gorus alanindan cikinca (ya da yeniden merkezlenirken) hedefe
        // yumusakca kayar; menzil icindeyken DUNYAYA SABIT kalir ki lazerle tusa basilabilsin.
        // Giris ekranindaki (PlayerEntryUI.PlacePanel) davranisin ayni kopyasi.
        void PlacePanel(bool instant)
        {
            if (_panel == null) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 targetPos = head.position + fwd * distance - Vector3.up * heightDrop;
            Quaternion targetRot = Quaternion.LookRotation(fwd) * Quaternion.Euler(tiltDegrees, 0f, 0f);

            var t = _panel.transform;

            // Kafa (XR rig / kamera) Open aninda henuz yoksa panel origin'de kalirdi; ilk
            // gecerli kafa okumasina kadar yerlestirme ANLIK yapilir.
            if (instant || !_placed)
            {
                t.SetPositionAndRotation(targetPos, targetRot);
                _placed = true;
                _recentering = false;
                return;
            }

            if (!_recentering)
            {
                Vector3 toPanel = t.position - head.position; toPanel.y = 0f;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                if (Vector3.Angle(fwd, toPanel.normalized) < RecenterAngle) return;  // yerinde kalsin
                _recentering = true;
            }

            float k = 1f - Mathf.Exp(-RecenterSpeed * Time.unscaledDeltaTime);
            t.SetPositionAndRotation(
                Vector3.Lerp(t.position, targetPos, k),
                Quaternion.Slerp(t.rotation, targetRot, k));

            if ((t.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }

        // ------------------------------------------------------- masaustu yedegi (gozluksuz)
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti odenirdi (bkz. TeamSelector).
            if (Application.isMobilePlatform) return;
            if (AppMode.Current != AppMode.Mode.None) return;

            // ILK EKRAN HICBIR SEYIN ALTINDA KALMAZ. Gelistirici katmanlari ekranin ALT kenarina
            // gore konumlaniyor (WeaponGripCaptureTool: Screen.height - 370); alcak bir Game
            // view'da o kutu yukari tirmanip tam buranin uzerine biniyordu — butonlar duruyor
            // ama okunmuyor ve nisan alinamiyordu, "moda gecemiyorum"un sebebi buydu.
            //
            // Iki onlem birden, cunku ikisi de tek basina kirilgan: konum artik UST-ORTA (iki
            // dev kutusu da SOL ve SAG kenara yapisik), ve GUI.depth ile bu kutu her seyin
            // ustunde ciziliyor — ileride eklenen bir katman yine ustune binemesin.
            GUI.depth = -1000;

            const float w = 320f, h = 80f;
            GUILayout.BeginArea(new Rect((Screen.width - w) * 0.5f, 20f, w, h), GUI.skin.box);
            GUILayout.Label("Bir mod sec");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("YARATICI")) AppMode.Choose(AppMode.Mode.Creative);
            if (GUILayout.Button("OYUNCU")) AppMode.Choose(AppMode.Mode.Player);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
