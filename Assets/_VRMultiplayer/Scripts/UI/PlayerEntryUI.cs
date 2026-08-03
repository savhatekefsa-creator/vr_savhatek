using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Oyuncunun ilk gordugu ekranin AKISI: <see cref="PlayerEntryPanel"/>'i kurar, lazer
    /// imleci besler, isim/takim secimini <see cref="PlayerProfile"/>'a yazar ve OYUNA BASLA
    /// ile baglantiyi baslatir.
    ///
    /// ISIM VE TAKIM ZORUNLU: ikisi tamamlanmadan OYUNA BASLA pasif; yani oyuna isimsiz ya da
    /// takimsiz girilemez.
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
    public class PlayerEntryUI : MonoBehaviour
    {
        [Tooltip("Panelin kafadan uzakligi (metre).")]
        public float distance = 1.4f;
        [Tooltip("Panel merkezinin goz hizasina gore dusuklugu (metre) — masaya bakar gibi.")]
        public float heightDrop = 0.10f;
        [Tooltip("Panelin one dogru yatma acisi (derece).")]
        public float tiltDegrees = 10f;

        /// <summary>Panel bu acidan fazla yana kalirsa onune geri getirilir.</summary>
        const float RecenterAngle = 38f;
        const float RecenterSpeed = 3.5f;

        PlayerEntryPanel _panel;
        VRPointer _pointer;
        bool _recentering;
        bool _placed;      // kafa transformu hazir olana kadar yerlestirme ANLIK kalir
        float _messageUntil;

        // Masaustu yedegi icin (gozluksuz iterasyon).
        string _guiName = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~PlayerEntryUI");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerEntryUI>();
        }

        void Start()
        {
            if (PlayerProfile.Confirmed) { Destroy(gameObject); return; }

            var panelGo = new GameObject("Player Entry Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<PlayerEntryPanel>();

            var pointerGo = new GameObject("UI Pointer");
            pointerGo.transform.SetParent(transform, false);
            _pointer = pointerGo.AddComponent<VRPointer>();

            // Kayitli isim varsa dolu gelir (ikinci acilista tek tusla gecilir); yoksa hazir
            // bir cagri adi onerilir — oyuncu bos ekranla karsilasmaz.
            string start = PlayerProfile.Name;
            if (string.IsNullOrEmpty(start)) start = PlayerProfile.NextGeneratedName();
            _guiName = start;
            _panel.SetText(start);
            _panel.SetTeam(PlayerProfile.Team);   // takim da hatirlanir

            _panel.ActionPressed += OnAction;
            _panel.Changed += OnChanged;

            PlacePanel(instant: true);
            RefreshHint();
        }

        void OnDestroy()
        {
            if (_panel == null) return;
            _panel.ActionPressed -= OnAction;
            _panel.Changed -= OnChanged;
        }

        void OnChanged()
        {
            _guiName = _panel.Text;
            RefreshHint();
        }

        void OnAction(string action)
        {
            switch (action)
            {
                case PlayerEntryPanel.ActionRandom:
                    _panel.SetText(PlayerProfile.NextGeneratedName());
                    break;

                case PlayerEntryPanel.ActionClear:
                    _panel.SetText(string.Empty);
                    break;

                case PlayerEntryPanel.ActionStart:
                    StartGame(_panel.Text, _panel.SelectedTeam);
                    break;
            }
        }

        void StartGame(string rawName, byte team)
        {
            string clean = PlayerProfile.Sanitize(rawName);

            if (!PlayerProfile.Confirm(clean, team))
            {
                // Reddi SEBEBIYLE birlikte soyle; sessiz reddedilen buton bozuk sanilir.
                Message(!PlayerProfile.IsValidName(clean)
                    ? "En az " + PlayerProfile.MinLength + " harf gir."
                    : "Bir takim sec.");
                return;
            }

            Debug.Log($"[PlayerEntryUI] Giris tamam: '{clean}', takim {team}. Baglanti baslatiliyor.");

            // OYUNA BASLA gercekten OYUNA BASLATIR: ayrica B'ye basmak gerekmez. B tusu
            // LanBootstrap'te yeniden deneme olarak duruyor (baglanti koparsa ise yarar).
            var boot = FindFirstObjectByType<LanBootstrap>();
            if (boot != null) boot.StartCoroutine(boot.JoinAsClient());
            else Debug.LogWarning("[PlayerEntryUI] LanBootstrap yok — baglanti baslatilamadi.");

            Destroy(gameObject);   // panel + imlec cocuk oldugu icin birlikte gider
        }

        void Message(string s)
        {
            _messageUntil = Time.unscaledTime + 2.5f;
            _panel.SetHint(s, warning: true);
        }

        // Alt ipucu her zaman SIRADAKI eksigi soyler; hepsi tamamsa oyuna davet eder.
        void RefreshHint()
        {
            if (_panel == null || Time.unscaledTime < _messageUntil) return;

            if (!PlayerProfile.IsValidName(PlayerProfile.Sanitize(_panel.Text)))
                _panel.SetHint("En az " + PlayerProfile.MinLength + " harf gir.");
            else if (_panel.SelectedTeam == PlayerProfile.TeamNone)
                _panel.SetHint("Bir takim sec.");
            else
                _panel.SetHint("Hazirsin — OYUNA BAŞLA'ya bas.");
        }

        void Update()
        {
            if (_panel == null) return;

            // Giris baska bir yoldan tamamlandiysa (kayitli profille acilis, test/konsol) panel
            // ortada kalmasin — bilesenin degismez kurali "onaylandiysa ben yokum".
            if (PlayerProfile.Confirmed) { Destroy(gameObject); return; }

            if (_messageUntil > 0f && Time.unscaledTime >= _messageUntil)
            {
                _messageUntil = 0f;
                RefreshHint();
            }

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

            if ((t.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }

        // ------------------------------------------------------- masaustu yedegi (gozluksuz)
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti odenirdi (bkz. TeamSelector).
            if (Application.isMobilePlatform || _panel == null) return;

            GUILayout.BeginArea(new Rect(20, 120, 320, 190), GUI.skin.box);
            GUILayout.Label("Oyuncu girisi (isim + takim zorunlu)");

            string typed = GUILayout.TextField(_guiName ?? "", PlayerProfile.MaxLength);
            if (typed != _guiName) { _guiName = typed; _panel.SetText(typed); }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_panel.SelectedTeam == PlayerProfile.TeamBlue ? "[MAVI]" : "MAVI"))
                _panel.SetTeam(PlayerProfile.TeamBlue);
            if (GUILayout.Button(_panel.SelectedTeam == PlayerProfile.TeamRed ? "[KIZIL]" : "KIZIL"))
                _panel.SetTeam(PlayerProfile.TeamRed);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rastgele")) _panel.SetText(PlayerProfile.NextGeneratedName());
            GUI.enabled = _panel.Ready;
            if (GUILayout.Button("OYUNA BASLA")) StartGame(_panel.Text, _panel.SelectedTeam);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
