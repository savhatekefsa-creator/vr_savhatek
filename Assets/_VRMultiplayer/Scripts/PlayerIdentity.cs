using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Gives each player a distinct color, a name and a TEAM, synced to everyone (including
    /// late joiners). The server assigns color/name on spawn; the player picks a team via
    /// <see cref="JoinTeamServerRpc"/> (see <see cref="TeamSelector"/>). Team overrides the
    /// unique color (A = blue, B = red) and prefixes the name tag ("[A] Oyuncu 1").
    ///
    /// Attach to the NetworkPlayer root (next to <see cref="NetworkVRPlayer"/>).
    /// </summary>
    public class PlayerIdentity : NetworkBehaviour
    {
        public static readonly Color TeamAColor = new Color(0.25f, 0.5f, 1f);   // mavi
        public static readonly Color TeamBColor = new Color(1f, 0.3f, 0.25f);   // kırmızı

        [SerializeField] SkinnedMeshRenderer avatarRenderer;
        [SerializeField] TextMesh nameTag;

        public NetworkVariable<Color> NetColor = new NetworkVariable<Color>(Color.white);
        public NetworkVariable<FixedString32Bytes> NetName = new NetworkVariable<FixedString32Bytes>();
        /// <summary>0 = takım yok, 1 = A takımı, 2 = B takımı.</summary>
        public NetworkVariable<byte> Team = new NetworkVariable<byte>(0);

        MaterialPropertyBlock _mpb;

        /// <summary>The color everyone currently sees (team color wins over the unique color).</summary>
        public Color DisplayColor =>
            Team.Value == 1 ? TeamAColor :
            Team.Value == 2 ? TeamBColor : NetColor.Value;

        public string DisplayName =>
            (Team.Value == 1 ? "[A] " : Team.Value == 2 ? "[B] " : "") + NetName.Value;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                NetColor.Value = ColorFromId(OwnerClientId);
                NetName.Value = new FixedString32Bytes("Oyuncu " + OwnerClientId);
            }

            NetColor.OnValueChanged += OnColorChanged;
            NetName.OnValueChanged += OnNameChanged;
            Team.OnValueChanged += OnTeamChanged;

            // Initial sync does NOT fire OnValueChanged, so apply the current values now
            // (this is what makes late-joiners show the correct color/name/team).
            Refresh();
        }

        public override void OnNetworkDespawn()
        {
            NetColor.OnValueChanged -= OnColorChanged;
            NetName.OnValueChanged -= OnNameChanged;
            Team.OnValueChanged -= OnTeamChanged;
        }

        void OnColorChanged(Color _, Color __) => Refresh();
        void OnNameChanged(FixedString32Bytes _, FixedString32Bytes __) => Refresh();
        void OnTeamChanged(byte _, byte __) => Refresh();

        /// <summary>Owner asks the server to put them on a team (1 = A, 2 = B).</summary>
        [ServerRpc]
        public void JoinTeamServerRpc(byte team)
        {
            if (team <= 2)
                Team.Value = team;
        }

        void Refresh()
        {
            Color c = DisplayColor;

            if (avatarRenderer != null)
            {
                _mpb ??= new MaterialPropertyBlock();
                avatarRenderer.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", c); // URP Lit
                _mpb.SetColor("_Color", c);     // Built-in / fallback
                avatarRenderer.SetPropertyBlock(_mpb);
            }

            if (nameTag != null)
            {
                nameTag.color = c;
                nameTag.text = DisplayName;
            }
        }

        // Distinct, well-spread hues using the golden-ratio increment.
        static Color ColorFromId(ulong id)
        {
            float hue = (id * 0.61803398875f) % 1f;
            return Color.HSVToRGB(hue, 0.7f, 0.95f);
        }
    }
}
