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
    /// OLU GORUNUMU: <see cref="PlayerHealth.Dead"/> iken avatar griye doner, dirilince eski
    /// haline. Karsidaki oyuncu oldugunu boyle anlar — eskiden olen oyuncu CANLI ile birebir
    /// ayni gorunuyordu (kafa ustunde can gostergesi yok, yalnizca isim etiketi var).
    ///
    /// Attach to the NetworkPlayer root (next to <see cref="NetworkVRPlayer"/>).
    /// </summary>
    public class PlayerIdentity : NetworkBehaviour
    {
        public static readonly Color TeamAColor = new Color(0.25f, 0.5f, 1f);   // mavi
        public static readonly Color TeamBColor = new Color(1f, 0.3f, 0.25f);   // kırmızı
        /// <summary>Olu avatarin tonu. ALFA onemli: saydamligi bu belirler (malzeme
        /// <see cref="MaterialGhost"/> ile saydama cevrilir, opaklik ise buradaki alfadan
        /// gelir). Daha belirgin hayalet icin alfayi yukselt, daha silik icin dusur.</summary>
        public static readonly Color DeadColor = new Color(0.55f, 0.68f, 0.9f, 0.35f);

        [SerializeField] SkinnedMeshRenderer avatarRenderer;
        [SerializeField] TextMesh nameTag;

        public NetworkVariable<Color> NetColor = new NetworkVariable<Color>(Color.white);
        public NetworkVariable<FixedString32Bytes> NetName = new NetworkVariable<FixedString32Bytes>();
        /// <summary>0 = takım yok, 1 = A takımı, 2 = B takımı.</summary>
        public NetworkVariable<byte> Team = new NetworkVariable<byte>(0);

        MaterialPropertyBlock _mpb;
        PlayerHealth _health;
        // Avatarin TUM parcalari (prefabda 10 SkinnedMeshRenderer). Olu tonu hepsine
        // uygulanmali, yoksa avatar yari gri kalir. BIR KEZ cache'lenir: her olum/dirilis
        // icin GetComponentsInChildren cagirmak Quest'te bosuna dizi alloc'u demekti.
        SkinnedMeshRenderer[] _bodyRenderers;

        // Parca basina CANLI ve HAYALET malzeme dizileri. Canli dizi, ilk hayalete gecis
        // aninda renderer'dan OKUNUR — boylece daha once baska bir sistem slotlari
        // degistirdiyse (or. MaterialDoubleSided) dirilis onu birebir geri getirir.
        // Diziler oyuncu basina, ICLERINDEKI malzemeler paylasimli: SRP batching bozulmaz.
        Material[][] _aliveMats;
        Material[][] _ghostMats;
        bool _ghostOn;

        void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            // includeInactive: remoteAvatar prefabda KAPALI baslayip spawn'da aciliyor
            // (bkz. NetworkVRPlayer) — kapaliyken de yakalanmali.
            _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        }

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
            if (_health != null) _health.Dead.OnValueChanged += OnDeadChanged;

            // Initial sync does NOT fire OnValueChanged, so apply the current values now
            // (this is what makes late-joiners show the correct color/name/team — ve olu
            // birinin geç katilana CANLI gorunmesini onleyen sey de budur).
            Refresh();
        }

        public override void OnNetworkDespawn()
        {
            NetColor.OnValueChanged -= OnColorChanged;
            NetName.OnValueChanged -= OnNameChanged;
            Team.OnValueChanged -= OnTeamChanged;
            if (_health != null) _health.Dead.OnValueChanged -= OnDeadChanged;
        }

        void OnColorChanged(Color _, Color __) => Refresh();
        void OnNameChanged(FixedString32Bytes _, FixedString32Bytes __) => Refresh();
        void OnTeamChanged(byte _, byte __) => Refresh();
        void OnDeadChanged(bool _, bool __) => Refresh();

        /// <summary>Owner asks the server to put them on a team (1 = A, 2 = B).</summary>
        [ServerRpc]
        public void JoinTeamServerRpc(byte team)
        {
            if (team <= 2)
                Team.Value = team;
        }

        /// <summary>Avatarin tum parcalarini saydam varyanta cevirir / geri alir. Yalnizca
        /// DURUM DEGISINCE calisir: Refresh() renk/isim/takim degisiminde de cagriliyor,
        /// her seferinde malzeme dizisi yazmak bosuna is olurdu.</summary>
        void SetGhost(bool on)
        {
            if (_ghostOn == on || _bodyRenderers == null) return;

            if (_aliveMats == null)
            {
                _aliveMats = new Material[_bodyRenderers.Length][];
                _ghostMats = new Material[_bodyRenderers.Length][];
                for (int i = 0; i < _bodyRenderers.Length; i++)
                {
                    var r = _bodyRenderers[i];
                    if (r == null) continue;
                    var alive = r.sharedMaterials;
                    var ghost = new Material[alive.Length];
                    for (int s = 0; s < alive.Length; s++) ghost[s] = MaterialGhost.Of(alive[s]);
                    _aliveMats[i] = alive;
                    _ghostMats[i] = ghost;
                }
            }

            for (int i = 0; i < _bodyRenderers.Length; i++)
            {
                var r = _bodyRenderers[i];
                if (r == null || _aliveMats[i] == null) continue;
                r.sharedMaterials = on ? _ghostMats[i] : _aliveMats[i];
            }

            _ghostOn = on;
        }

        void Refresh()
        {
            bool dead = _health != null && _health.Dead.Value;
            Color c = DisplayColor;

            SetGhost(dead);

            if (_bodyRenderers != null)
            {
                _mpb ??= new MaterialPropertyBlock();
                for (int i = 0; i < _bodyRenderers.Length; i++)
                {
                    var r = _bodyRenderers[i];
                    if (r == null) continue;

                    // CANLIYKEN takim tonu yalnizca avatarRenderer'a gider (mevcut davranis
                    // bilerek korundu); diger parcalarin bloklari temizlenir ki dirilis eski
                    // gorunumu birebir geri getirsin.
                    if (!dead && r != avatarRenderer) { r.SetPropertyBlock(null); continue; }

                    Color tone = dead ? DeadColor : c;
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetColor("_BaseColor", tone); // URP Lit
                    _mpb.SetColor("_Color", tone);     // Built-in / fallback
                    r.SetPropertyBlock(_mpb);
                }
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
