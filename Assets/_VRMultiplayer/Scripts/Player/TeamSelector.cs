using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// YEDEK takim secimi + onboarding'in kalibrasyon adimini baslatan yer.
    ///
    /// Takim artik OYUNA GIRMEDEN ONCE, giris ekraninda seciliyor (bkz. <see cref="UI.PlayerEntryUI"/>)
    /// ve <see cref="PlayerIdentity"/> spawn aninda sunucuya bildiriyor. Yani normal akista bu
    /// panel HIC ACILMAZ: asagida yerel secim varsa dogrudan kalibrasyona geciliyor.
    ///
    /// Panel yine de duruyor cunku bir sey ters giderse (profil okunamadi, oyuncu bir sekilde
    /// takimsiz spawn oldu) oyuncunun takimsiz kalmasi KABUL EDILEMEZ: takimi 0 olan oyuncu
    /// dogum bolgesi bulamaz (PlayerHealth.TickSpawn team==0'da bekler) ve dost atesi filtresi
    /// disinda kalir. Bu yol o durumda A/B ile secim yaptirir.
    ///
    /// KALIBRASYONU BASLATAN YER BURASI: eskiden takim secimi bittiginde cagriliyordu, simdi
    /// takim zaten secili geldigi icin spawn'da cagriliyor. Onboarding zinciri:
    /// giris ekrani (isim + takim) -> baglan -> kalibrasyon -> dogum bolgesine yuru.
    /// </summary>
    public class TeamSelector : NetworkBehaviour
    {
        PlayerIdentity _identity;
        TextMesh _panel;
        bool _armed;   // becomes true once A and B are both seen released
        bool _done;
        bool _prevA, _prevB;

        public override void OnNetworkSpawn()
        {
            _identity = GetComponent<PlayerIdentity>();
            if (!IsOwner || _identity == null)
            {
                enabled = false;
                return;
            }

            // Giris ekraninda takim secildiyse (normal akis) ya da yeniden baglanmada takim
            // zaten atanmissa panel gerekmez — dogrudan siradaki adima, kalibrasyona gec.
            //
            // YEREL secime bakiliyor, ag degerine DEGIL: JoinTeamServerRpc spawn'da yeni
            // gonderildi, Team.Value bu karede hala 0. Ag degerini beklemek paneli bir an
            // yanip sonduren bir yaris yaratirdi.
            if (PlayerProfile.Team != PlayerProfile.TeamNone || _identity.Team.Value != 0)
            {
                _done = true;
                BeginCalibration();
                enabled = false;
                return;
            }

            Debug.LogWarning("[TeamSelector] Giris ekranindan takim gelmedi — yedek A/B secimi aciliyor.");
            CreatePanel();
        }

        public override void OnNetworkDespawn()
        {
            if (_panel != null) Destroy(_panel.gameObject);
        }

        void CreatePanel()
        {
            // "~": passthrough acikken gizlenmesin. Takim secmeden oyuna girilemez.
            _panel = UI.HeadFollowPanel.Create("~Team Select Panel",
                "TAKIM SEC\n\nA tusu = A TAKIMI (mavi)\nB tusu = B TAKIMI (kirmizi)", Color.yellow);
        }

        void Update()
        {
            if (_done) return;

            bool a = XRButtons.Button(XRNode.RightHand, CommonUsages.primaryButton);
            bool b = XRButtons.Button(XRNode.RightHand, CommonUsages.secondaryButton);

            if (!_armed)
            {
                if (!a && !b) _armed = true; // require a clean release first
            }
            else
            {
                if (a && !_prevA) Choose(1);
                else if (b && !_prevB) Choose(2);
            }

            _prevA = a;
            _prevB = b;
        }

        void Choose(byte team)
        {
            _done = true;
            _identity.JoinTeamServerRpc(team);
            if (_panel != null) Destroy(_panel.gameObject);
            enabled = false;

            BeginCalibration();
        }

        /// <summary>Siradaki onboarding adimi: ortak fiziksel alani kalibre et.</summary>
        void BeginCalibration()
        {
            var cal = Object.FindFirstObjectByType<CalibrationManager>();
            if (cal != null) cal.Begin();
        }

        // Editor / desktop fallback so the flow can be tested without a headset.
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti (event basina 2 gecis)
            // yine de odenirdi — mobilde tamamen kapali.
            if (Application.isMobilePlatform) return;
            if (_done || !IsOwner) return;
            GUILayout.BeginArea(new Rect(20, 290, 260, 80), GUI.skin.box);
            GUILayout.Label("Takim sec");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("A Takimi")) Choose(1);
            if (GUILayout.Button("B Takimi")) Choose(2);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
