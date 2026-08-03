using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Oyuncunun ilk gordugu ekran: ADINI SEC. Klavyeden kendi ismini yazar ya da RASTGELE
    /// ISIM tusuyla hazir cagri adlarindan birini alir. Onaylayana kadar oyuna katilamaz
    /// (<see cref="LanBootstrap"/> B tusunu bekletir).
    ///
    /// BAGLANTIDAN ONCE, tamamen YEREL calisir. Uc sebep:
    ///  - Netcode'a hic dokunmaz; panel acikken spawn/RPC/trafik yok.
    ///  - Oyuncu zaten sunucu beklerken orada duruyor, olu zaman degerlendiriliyor.
    ///  - TETIK CAKISMASI YOK: tetik, baglantidan SONRA CalibrationManager'in oluyor. Baglanti
    ///    oncesi LanBootstrap yalnizca B kullanir, tetik bostadir — imlece verilebilir.
    ///
    /// Sahneye/prefaba DOKUNMAZ: kendini <see cref="RuntimeInitializeOnLoadMethod"/> ile kurar
    /// (ayni desen: <see cref="WeaponSelectorUI"/>, <see cref="SingleAudioListener"/>).
    ///
    /// PANEL DUNYAYA SABIT, kafaya kilitli DEGIL — lazerle nisan alinan bir yuzey her kare
    /// kafayla birlikte kayarsa tusa basmak imkansiz olur. Bunun yerine TEMBEL TAKIP: panel
    /// gorus alanindan cikacak kadar (bkz. <see cref="RecenterAngle"/>) donunce yumusakca
    /// onune geri gelir.
    /// </summary>
    public class NameEntryUI : MonoBehaviour
    {
        [Tooltip("Panelin kafadan uzakligi (metre).")]
        public float distance = 1.4f;
        [Tooltip("Panel merkezinin goz hizasina gore dusuklugu (metre) — masaya bakar gibi.")]
        public float heightDrop = 0.12f;
        [Tooltip("Panelin one dogru yatma acisi (derece).")]
        public float tiltDegrees = 12f;

        /// <summary>Panel bu acidan fazla yana kalirsa onune geri getirilir.</summary>
        const float RecenterAngle = 38f;
        const float RecenterSpeed = 3.5f;

        VRKeyboardPanel _panel;
        VRPointer _pointer;
        bool _recentering;
        bool _placed;      // kafa transformu hazir olana kadar yerlestirme ANLIK kalir
        string _message;
        float _messageUntil;

        // Masaustu yedegi icin (gozluksuz iterasyon).
        string _guiName = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~NameEntryUI");
            DontDestroyOnLoad(go);
            go.AddComponent<NameEntryUI>();
        }

        void Start()
        {
            if (PlayerName.Confirmed) { Destroy(gameObject); return; }

            var panelGo = new GameObject("Name Entry Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<VRKeyboardPanel>();
            _panel.maxLength = PlayerName.MaxLength;

            var pointerGo = new GameObject("UI Pointer");
            pointerGo.transform.SetParent(transform, false);
            _pointer = pointerGo.AddComponent<VRPointer>();

            // Kayitli isim varsa dolu gelir (ikinci acilista tek tusla gecilir); yoksa hazir
            // bir cagri adi onerilir — oyuncu bos ekranla karsilasmaz.
            string start = PlayerName.Current;
            if (string.IsNullOrEmpty(start)) start = PlayerName.NextGenerated();
            _guiName = start;
            _panel.SetText(start);

            _panel.ActionPressed += OnAction;
            _panel.TextChanged += _ => { _guiName = _panel.Text; ShowHint(); };

            _panel.SetSubtitle("Kendi adini yaz ya da RASTGELE ISIM'e bas");
            PlacePanel(instant: true);
            ShowHint();
        }

        void OnDestroy()
        {
            if (_panel != null) _panel.ActionPressed -= OnAction;
        }

        void OnAction(string action)
        {
            switch (action)
            {
                case VRKeyboardPanel.ActionRandom:
                    _panel.SetText(PlayerName.NextGenerated());
                    break;

                case VRKeyboardPanel.ActionClear:
                    _panel.SetText(string.Empty);
                    break;

                case VRKeyboardPanel.ActionConfirm:
                    Confirm(_panel.Text);
                    break;
            }
        }

        void Confirm(string raw)
        {
            string clean = PlayerName.Sanitize(raw);

            if (!PlayerName.IsValid(clean))
            {
                // Reddi SEBEBIYLE birlikte soyle; sessiz reddedilen tus bozuk sanilir.
                Message(clean.Length < PlayerName.MinLength
                    ? "EN AZ " + PlayerName.MinLength + " KARAKTER GEREKLI"
                    : "GECERSIZ ISIM");
                return;
            }

            if (!PlayerName.Confirm(clean))
            {
                Message("ISIM KAYDEDILEMEDI");
                return;
            }

            Debug.Log("[NameEntryUI] Isim onaylandi: " + clean);
            Destroy(gameObject);   // panel + imlec cocuk oldugu icin birlikte gider
        }

        void Message(string s)
        {
            _message = s;
            _messageUntil = Time.unscaledTime + 2.5f;
            if (_panel != null) _panel.SetTitle(s, warning: true);
        }

        void ShowHint()
        {
            if (Time.unscaledTime < _messageUntil) return;
            _message = null;
            if (_panel == null) return;
            _panel.SetTitle("ADINI SEC");
            _panel.SetSubtitle("Kendi adini yaz ya da RASTGELE ISIM'e bas");
        }

        void Update()
        {
            if (_panel == null) return;

            // Isim baska bir yoldan onaylandiysa (kayitli isimle acilis, test/konsol) panel
            // ortada kalmasin — bileşenin degismez kurali "onaylandiysa ben yokum".
            if (PlayerName.Confirmed) { Destroy(gameObject); return; }

            if (_message != null && Time.unscaledTime >= _messageUntil) ShowHint();

            PlacePanel(instant: false);
            _panel.Tick(_pointer);
        }

        // Tembel takip: panel gorus alanindan cikinca (ya da yeniden merkezlenirken) hedefe
        // yumusakca kayar; menzil icindeyken DUNYAYA SABIT kalir ki lazerle tusa basilabilsin.
        void PlacePanel(bool instant)
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 targetPos = head.position + fwd * distance - Vector3.up * heightDrop;
            Quaternion targetRot = Quaternion.LookRotation(fwd) * Quaternion.Euler(tiltDegrees, 0f, 0f);

            var t = _panel.transform;

            // Kafa (XR rig / kamera) Start aninda henuz yoksa panel origin'de kalirdi; ilk
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

            // Hedefe yeterince yaklastiysa sabitlemeye geri don.
            if ((t.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }

        // ------------------------------------------------------- masaustu yedegi (gozluksuz)
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti odenirdi (bkz. TeamSelector).
            if (Application.isMobilePlatform || _panel == null) return;

            GUILayout.BeginArea(new Rect(20, 120, 300, 150), GUI.skin.box);
            GUILayout.Label("Adini sec (3-" + PlayerName.MaxLength + " karakter)");

            GUI.SetNextControlName("nameField");
            _guiName = GUILayout.TextField(_guiName ?? "", PlayerName.MaxLength);

            if (!string.IsNullOrEmpty(_message)) GUILayout.Label(_message);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rastgele"))
            {
                _guiName = PlayerName.NextGenerated();
                _panel.SetText(_guiName);
            }
            if (GUILayout.Button("ONAYLA")) Confirm(_guiName);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
