using System.Collections.Generic;
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
    /// ISIM GIZLILIGI: isim etiketi YALNIZCA ayni takimdaki oyunculara gorunur. Karsi takim
    /// (ve henuz takim secmemis herkes) etiketi gormez; kendi etiketini kimse gormez. Avatarin
    /// TAKIM RENGI herkese acik kalir — dusmani ayirt etmek icin gerekli, gizlenen tek sey isim.
    /// Bkz. <see cref="NameVisibleToLocal"/>.
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

        /// <summary>Bu oyuncunun oldurme / olme sayisi. Sunucu yazar (<see cref="PlayerHealth"/>).
        /// NetworkVariable oldugu icin GEC KATILAN da dogru skoru gorur — ilk senkron otomatik.
        /// Ayri bir MatchManager/NetworkObject KURULMADI: skorun oyuncunun kimliginde yasamasi
        /// sahne ve prefab degisikligini tamamen gereksiz kiliyor.</summary>
        public NetworkVariable<ushort> Kills = new NetworkVariable<ushort>(0);
        public NetworkVariable<ushort> Deaths = new NetworkVariable<ushort>(0);

        // Isim BIR KEZ atanir: oyun ortasinda isim degistirmek kill panelindeki gecmis
        // satirlarla celisir ve kimligi bulandirir.
        bool _nameSet;

        MaterialPropertyBlock _mpb;
        PlayerHealth _health;
        // nameTag'in renderer'i. Isim etiketini takima gore acip kapatan sey bu; her
        // Refresh'te GetComponent cagirmamak icin Awake'te cache'lenir.
        MeshRenderer _nameTagRenderer;

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

        // ISIM GORUNURLUGU TAKIM ESLESMESINE BAGLI oldugu icin karar TEK TARAFLI DEGIL: hem
        // bakilan oyuncunun hem YEREL oyuncunun takimi gerekiyor. Takim spawn'dan SONRA
        // seciliyor (bkz. TeamSelector), dolayisiyla yerel takim degistiginde daha once spawn
        // olmus TUM uzak etiketler yeniden degerlendirilmeli — yoksa yerel takim henuz 0 iken
        // verilmis 'gizle' karari kalici olur. Kayit OnNetworkSpawn/Despawn'da yapilir.
        static readonly List<PlayerIdentity> _all = new List<PlayerIdentity>();
        static PlayerIdentity _local;

        void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            // includeInactive: remoteAvatar prefabda KAPALI baslayip spawn'da aciliyor
            // (bkz. NetworkVRPlayer) — kapaliyken de yakalanmali.
            _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (nameTag != null) _nameTagRenderer = nameTag.GetComponent<MeshRenderer>();
        }

        /// <summary>The color everyone currently sees (team color wins over the unique color).</summary>
        public Color DisplayColor =>
            Team.Value == 1 ? TeamAColor :
            Team.Value == 2 ? TeamBColor : NetColor.Value;

        public string DisplayName =>
            (Team.Value == 1 ? "[A] " : Team.Value == 2 ? "[B] " : "") + NetName.Value;

        public override void OnNetworkSpawn()
        {
            _all.Add(this);
            if (IsOwner) _local = this;

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
            //
            // YEREL oyuncu spawn oldugunda TUM etiketler yeniden degerlendirilir: ondan once
            // spawn olmus uzak oyuncular _local henuz yokken karar vermisti. Yeniden baglanma
            // durumunda takim ZATEN atanmis gelir ve ilk senkron callback tetiklemez, yani
            // burasi olmadan o etiketler kalici gizli kalirdi.
            if (IsOwner) RefreshAll(); else Refresh();

            // Oyuncunun giris ekraninda sectigi isim ve TAKIM sunucuya bildirilir
            // (bkz. UI.PlayerEntryUI). Sunucu ikisini de kendi tarafinda dogrular —
            // istemciden gelen degere guvenilmez.
            //
            // Takim eskiden spawn'dan SONRA TeamSelector paneliyle seciliyordu; artik giris
            // ekraninda secildigi icin burada gonderiliyor. TeamSelector yalnizca yedek yol
            // olarak duruyor (bkz. o dosya).
            if (IsOwner && PlayerProfile.Confirmed)
            {
                if (!string.IsNullOrEmpty(PlayerProfile.Name))
                    SetNameServerRpc(new FixedString32Bytes(PlayerProfile.Name));
                if (PlayerProfile.Team != PlayerProfile.TeamNone)
                    JoinTeamServerRpc(PlayerProfile.Team);
            }
        }

        public override void OnNetworkDespawn()
        {
            NetColor.OnValueChanged -= OnColorChanged;
            NetName.OnValueChanged -= OnNameChanged;
            Team.OnValueChanged -= OnTeamChanged;
            if (_health != null) _health.Dead.OnValueChanged -= OnDeadChanged;

            _all.Remove(this);
            // Yerel oyuncu gidiyorsa geride kalan etiketler artik takim arkadasi
            // referansini kaybetti — hepsi yeniden degerlendirilmeli.
            if (_local == this) { _local = null; RefreshAll(); }
        }

        void OnColorChanged(Color _, Color __) => Refresh();
        void OnNameChanged(FixedString32Bytes _, FixedString32Bytes __) => Refresh();
        // YEREL oyuncunun takimi degistiyse tek basina kendini tazelemek yetmez: herkesin
        // isim etiketi 'benim takimimla ayni mi' sorusuna bagli, o yuzden hepsi tazelenir.
        void OnTeamChanged(byte _, byte __)
        {
            if (IsOwner) RefreshAll(); else Refresh();
        }

        void OnDeadChanged(bool _, bool __) => Refresh();

        static void RefreshAll()
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i] != null) _all[i].Refresh();
        }

        /// <summary>Bu oyuncunun isim etiketi YEREL kamerada gorunmeli mi? Kendi etiketini
        /// kimse gormez (mevcut davranis). Digerleri yalnizca AYNI takimdaysa gorunur.
        /// Takim 0 = henuz secmemis; takim arkadasi SAYILMAZ, yani takim secilene kadar
        /// kimse kimsenin adini gormez.</summary>
        bool NameVisibleToLocal
        {
            get
            {
                if (IsOwner) return false;
                byte mine = Team.Value;
                byte localTeam = _local != null ? _local.Team.Value : (byte)0;
                return mine != 0 && mine == localTeam;
            }
        }

        /// <summary>Owner asks the server to put them on a team (1 = A, 2 = B).</summary>
        [ServerRpc]
        public void JoinTeamServerRpc(byte team)
        {
            // team = 0 KABUL EDILMEZ: takimsiz oyuncu dost-atesi filtresini bypass ederdi
            // (WeaponHitscanServer yalnizca t != 0 && t == shooterTeam ise engelliyor).
            if (team >= 1 && team <= 2)
                Team.Value = team;
        }

        /// <summary>
        /// Sahibin sectigi ismi sunucuya bildirir. SUNUCU SON SOZU SOYLER: gelen metin yeniden
        /// temizlenir (kontrol karakteri, zengin metin etiketi, asiri uzunluk), gecersizse
        /// eski "Oyuncu N" davranisina duser, baskasinda varsa tekillestirilir.
        ///
        /// [ServerRpc] varsayilani RequireOwnership = true, yani bir istemci BASKASININ ismini
        /// degistiremez.
        /// </summary>
        [ServerRpc]
        public void SetNameServerRpc(FixedString32Bytes requested)
        {
            if (_nameSet) return;

            string clean = PlayerProfile.Sanitize(requested.ToString());
            if (!PlayerProfile.IsValidName(clean)) clean = "Oyuncu " + OwnerClientId;

            NetName.Value = new FixedString32Bytes(MakeUnique(clean));
            _nameSet = true;
        }

        /// <summary>Ayni isimden ikinci varsa "-2", "-3" ekler. Kill panelinde iki ayni isim
        /// gorunmesi paneli okunmaz yapardi.</summary>
        string MakeUnique(string wanted)
        {
            if (!IsTaken(wanted)) return wanted;

            for (int n = 2; n <= 32; n++)
            {
                string suffix = "-" + n;
                // Sonek icin yer ac: sinir karakter DEGIL bayt bazli oldugundan kirpmayi
                // Sanitize'a yaptiriyoruz (tek kural, tek yer).
                string baseName = wanted;
                string candidate = PlayerProfile.Sanitize(baseName + suffix);
                while (!candidate.EndsWith(suffix) && baseName.Length > 1)
                {
                    baseName = baseName.Substring(0, baseName.Length - 1);
                    candidate = PlayerProfile.Sanitize(baseName + suffix);
                }

                if (!IsTaken(candidate)) return candidate;
            }
            return wanted;   // 32 cakisma: pes et, ayni isim gorunsun
        }

        bool IsTaken(string name)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                var p = _all[i];
                if (p == null || p == this) continue;
                if (string.Equals(p.NetName.Value.ToString(), name,
                        System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Bir takimin toplam oldurme sayisi. Mevcut <see cref="_all"/> listesi
        /// uzerinden ISTEMCIDE hesaplanir — ag uzerinde ayrica skor tasinmaz.
        ///
        /// BILINEN SINIR: oyuncu cikinca kimligi despawn olur ve skoru toplamdan duser.
        /// Kalici skor tablosu MatchManager isi.</summary>
        public static int TeamScore(byte team)
        {
            int total = 0;
            for (int i = 0; i < _all.Count; i++)
            {
                var p = _all[i];
                if (p != null && p.Team.Value == team) total += p.Kills.Value;
            }
            return total;
        }

        /// <summary>Verilen istemcinin kimligi (yoksa null — oyuncu ayrilmis olabilir).</summary>
        public static PlayerIdentity For(ulong clientId)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                var p = _all[i];
                if (p != null && p.OwnerClientId == clientId) return p;
            }
            return null;
        }

        /// <summary>Yerel oyuncunun kimligi (HUD/kill paneli "ben kimim" sorusunu bununla
        /// cevaplar). Takim henuz secilmemisse Team 0 doner.</summary>
        public static PlayerIdentity Local => _local;

        /// <summary>Su an sahnedeki TUM oyuncu kimlikleri (skorbord bunu tarar).
        /// Kopya DEGIL, canli listenin salt-okunur gorunumu — her acilista dizi alloc'u
        /// yapilmaz. Icinde null olabilir (despawn sirasi); okuyan taraf atlamalidir.</summary>
        public static System.Collections.Generic.IReadOnlyList<PlayerIdentity> All => _all;

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

            // ISIM YALNIZCA TAKIM ARKADASINA GORUNUR. Karar RENDERER seviyesinde veriliyor,
            // DisplayName BILEREK bosaltilmiyor: ServerView masaustu gozlemci etiketlerini
            // ve RoomScanSync 'sentBy' alanini bu ozellikten okuyor, isim bosalirsa onlar da
            // bozulurdu. Ayrica avatarin TAKIM RENGI herkese acik kalir (dusmani ayirt etmek
            // icin gerekli) — gizlenen tek sey ISIM.
            if (_nameTagRenderer != null) _nameTagRenderer.enabled = NameVisibleToLocal;
        }

        // Distinct, well-spread hues using the golden-ratio increment.
        static Color ColorFromId(ulong id)
        {
            float hue = (id * 0.61803398875f) % 1f;
            return Color.HSVToRGB(hue, 0.7f, 0.95f);
        }
    }
}
