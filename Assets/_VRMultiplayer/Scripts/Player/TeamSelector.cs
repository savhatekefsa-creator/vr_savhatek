using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// After the LOCAL player joins, shows a floating panel in front of them asking which team
    /// to join: A button (right controller) = Team A, B button = Team B. The choice is sent to
    /// the server via <see cref="PlayerIdentity.JoinTeamServerRpc"/> and replicated to everyone.
    ///
    /// The buttons are free at this point: LanBootstrap only uses A/B before a session starts.
    /// We still wait for both buttons to be RELEASED once before accepting input, so the press
    /// that started the host/join can never leak into the team choice.
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

            if (_identity.Team.Value != 0) // already on a team (e.g. reconnect)
            {
                _done = true;
                enabled = false;
                return;
            }

            CreatePanel();
        }

        public override void OnNetworkDespawn()
        {
            if (_panel != null) Destroy(_panel.gameObject);
        }

        void CreatePanel()
        {
            _panel = UI.HeadFollowPanel.Create("Team Select Panel",
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

            // Next onboarding step: calibrate the shared physical space.
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
