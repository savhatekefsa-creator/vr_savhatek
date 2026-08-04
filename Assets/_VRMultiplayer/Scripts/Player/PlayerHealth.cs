using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>Kill panelinin bir satiri. YEREL tasiyici — ag uzerinde
    /// <see cref="PlayerHealth.KillFeedRpc"/> alanlari olarak gider.</summary>
    public struct KillInfo
    {
        public string Killer;
        public string Victim;
        public byte KillerTeam;
        public byte VictimTeam;
        public ulong KillerId;
        public ulong VictimId;
        /// <summary>0 = normal, 1 = kendini oldurdu, 2 = katil bilinmiyor.</summary>
        public byte Kind;

        public bool SelfKill => Kind == 1;
        public bool UnknownKiller => Kind == 2;
    }

    /// <summary>
    /// Server-authoritative health for a player. Weapons call <see cref="ServerApplyDamage"/> on
    /// the server when a shot hits this player's hitbox; friendly fire is filtered by the weapon.
    ///
    /// DOGUM (spawn) MODELI — kolokasyonlu oyunun geregi: oyuncu elenince ISINLANMAZ. Kendi
    /// takiminin <see cref="TeamSpawnZone"/> cemberine FIZIKSEL olarak yurur ve icinde
    /// <see cref="spawnHoldSeconds"/> saniye KESINTISIZ durursa yeniden dogar. Cemberden
    /// cikarsa sayac SIFIRLANIR — cembere degip kacmak ise yaramaz.
    ///
    /// Ayni mekanizma ILK dogus icin de kullanilir: oyuncu <see cref="Dead"/> = true baslar,
    /// takimini secip kalibre ettikten sonra bolgesine yuruyerek oyuna girer. Tek mekanizma,
    /// iki kullanim.
    ///
    /// Bekleyen oyuncu <see cref="Dead"/> oldugu icin hasar alamaz (asagidaki erken cikis) ve
    /// hitbox'lari kapalidir (<see cref="PlayerHitbox"/>) — yani dogum bolgesinde beklerken
    /// vurulamaz.
    ///
    /// Attach to the NetworkPlayer root (next to <see cref="PlayerIdentity"/>).
    /// </summary>
    public class PlayerHealth : NetworkBehaviour
    {
        public const int MaxHealth = 100;

        [Tooltip("Dogum cemberinde KESINTISIZ beklenmesi gereken sure (saniye). Cemberden cikinca sayac sifirlanir.")]
        public float spawnHoldSeconds = 5f;
        [Tooltip("Yeniden dogduktan sonra dokunulmazlik suresi (saniye).")]
        public float reviveInvuln = 2f;
        [Tooltip("GUVENLIK AGI: sahnede bu takima ait dogum bolgesi YOKSA oyuncu bu kadar saniye sonra oldugu yerde dogar. Bolgeler kurulmadan test edilirken oyuncunun sonsuza kadar olu kalmasini onler (bkz. menu 22).")]
        public float noZoneFallbackSeconds = 5f;

        public NetworkVariable<int> Health = new NetworkVariable<int>(MaxHealth);
        public NetworkVariable<bool> Dead = new NetworkVariable<bool>(false);

        /// <summary>Dogum geri sayiminin 0..1 ilerlemesi. Yalnizca <see cref="Dead"/> iken
        /// anlamlidir; HUD halkayi ve sayaci bundan cizer.</summary>
        public NetworkVariable<float> SpawnProgress = new NetworkVariable<float>(0f);

        /// <summary>Oyuncu su an kendi dogum cemberinin icinde mi? HUD metni ("bolgene git" vs
        /// geri sayim) bunu kullanir.</summary>
        public NetworkVariable<bool> InSpawnZone = new NetworkVariable<bool>(false);

        PlayerIdentity _identity;
        Transform _head;            // agdan gelen kafa tasiyicisi (sunucu da gorur)
        float _invulnUntil;
        float _lastDamageTime;      // son hasar ani — regen bekleme suresi buradan sayilir
        float _regenAccumulator;    // kesirli yenilenmeyi biriktirir (Health int oldugu icin)
        float _holdTimer;           // cemberde kesintisiz gecen sure
        float _noZoneTimer;         // bolge yokken isleyen guvenlik agi sayaci

        /// <summary>"Katil bilinmiyor" isareti — kaynagi olmayan bir olum bildirmek isteyen
        /// gelecekteki hasar kaynaklari (dusme, harita disi) bunu gecirir. Gercek bir istemci
        /// kimligi asla bu degeri almaz.</summary>
        public const ulong NoAttacker = ulong.MaxValue;

        public bool IsDead => Dead.Value;
        public byte TeamValue => _identity != null ? _identity.Team.Value : (byte)0;

        /// <summary>Kafanin dunya konumu. Head tasiyicisi owner-authoritative
        /// <see cref="ClientNetworkTransform"/> ile replike oldugu icin SUNUCU da gorur —
        /// cember kontrolu bu yuzden sunucuda yapilabiliyor.</summary>
        public Vector3 HeadPosition => _head != null ? _head.position : transform.position;

        public override void OnNetworkSpawn()
        {
            _identity = GetComponent<PlayerIdentity>();
            // Head/LeftHand/RightHand oyuncu kokunun sabit-isimli cocuklaridir (PlayerHitbox
            // ile ayni sozlesme).
            _head = transform.Find("Head");
            if (_head == null)
                Debug.LogWarning("[PlayerHealth] 'Head' tasiyicisi bulunamadi — dogum cemberi " +
                                 "kontrolu oyuncu KOKUNUN konumuna duser ve muhtemelen hic " +
                                 "tetiklenmez. Prefab yapisini kontrol et (menu 1).");

            // Ayak sesi + kisiye ozel vucuda-mermi sesi: calisma aninda eklenir, prefab
            // degisikligi yok (sihirbaz prefab'i yeniden kursa da bu satir yeniden kurar).
            if (GetComponent<VRMultiplayer.Audio.PlayerAudio>() == null)
                gameObject.AddComponent<VRMultiplayer.Audio.PlayerAudio>();

            if (IsServer)
            {
                Health.Value = MaxHealth;
                // ILK DOGUS da cember mekanizmasindan gecer: oyuncu "bekliyor" baslar.
                Dead.Value = true;
                SpawnProgress.Value = 0f;
                InSpawnZone.Value = false;
                _lastDamageTime = Time.time;
            }
        }

        /// <summary>Yerel oyuncu hasar aldiginda kaynagin dunya noktasi + hasar miktari —
        /// YALNIZCA sahibin istemcisinde tetiklenir. HUD yon flasi bunu dinler; eskiden yon,
        /// baska sistemin tracer LineRenderer'larini sahneden kazimakla tahmin ediliyordu
        /// (yanlis yon + her hasarda FindObjectsOfType maliyeti). Miktar, flasin siddetini
        /// olceklemek icin tasinir: siyrik ile tam isabet ayni kirmizilikta gorunmemeli.</summary>
        public static event System.Action<Vector3, int> LocalDamageFrom;

        /// <summary>Herhangi bir oyuncu oldugunde HER istemcide tetiklenir — kill panelinin
        /// besleyicisi (bkz. <see cref="UI.KillFeedUI"/>). Statik olmasi bilincli: olay
        /// KURBANIN NetworkBehaviour'undan yayinlaniyor ama dinleyen YEREL oyuncunun HUD'u;
        /// aralarinda referans kurmak icin sahneyi taramak gerekirdi.</summary>
        public static event System.Action<KillInfo> KillReported;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            LocalDamageFrom = null;
            KillReported = null;
        }

        /// <summary>Server-only. Reduce health; handle death. Yeniden dogus zamanla DEGIL,
        /// dogum cemberinde beklenerek olur (bkz. <see cref="TickSpawn"/>).</summary>
        public void ServerApplyDamage(int amount, ulong attacker)
            => ServerApplyDamage(amount, attacker, Vector3.zero);

        /// <summary>Server-only. sourcePos = hasarin geldigi dunya noktasi (namlu); sifir
        /// vektor = kaynak bilinmiyor (yon flasi yonsuz halkaya duser).</summary>
        public void ServerApplyDamage(int amount, ulong attacker, Vector3 sourcePos)
        {
            if (!IsServer || Dead.Value || amount <= 0) return;

            // MAC KAPISI: hasar YALNIZCA "Playing" fazinda gecer. Isinmada ve mac sonu ekraninda
            // herkes dolasip ates edebilir (ses, geri tepme, mermi, namlu alevi calisir) ama
            // kimse hasar almaz. Mac katmani hic yoksa MatchManager.DamageAllowed true doner —
            // yani MatchManager'siz oyun eskisi gibi calisir.
            if (!Match.MatchManager.DamageAllowed) return;

            if (Time.time < _invulnUntil) return; // just revived — brief grace

            Health.Value = Mathf.Max(0, Health.Value - amount);
            _lastDamageTime = Time.time;   // regen bekleme suresini sifirla
            _regenAccumulator = 0f;

            if (sourcePos != Vector3.zero)
                DamageSourceOwnerRpc(sourcePos, amount, RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));

            if (Health.Value <= 0)
                Die(attacker);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void DamageSourceOwnerRpc(Vector3 sourcePos, int amount, RpcParams p)
        {
            if (IsOwner) LocalDamageFrom?.Invoke(sourcePos, amount);
        }

        void Die(ulong attacker)
        {
            Dead.Value = true;
            SpawnProgress.Value = 0f;
            InSpawnZone.Value = false;
            _holdTimer = 0f;
            _noZoneTimer = 0f;

            ReportKill(attacker);

            // Elinde ne varsa YOK OLUR (ekip karari). Eskiden yalnizca BIRAKILIYORDU
            // (ServerReleaseAllHeldBy); birakilan silah kinematik govde olarak sahnede kalir ve
            // havada asili donabiliyordu. Dirilen oyuncu carktan kendi silahini secer.
            DeathDisarm.DisarmHolder(OwnerClientId);
        }

        /// <summary>Server-only. Skoru isler ve olumu HERKESE duyurur.
        ///
        /// Bu bilgi daha once VARDI ama atiliyordu: <see cref="ServerApplyDamage"/> katilin
        /// kimligini aliyor, <c>Die()</c> hic kullanmiyordu.</summary>
        void ReportKill(ulong attacker)
        {
            byte victimTeam = TeamValue;
            string victimName = _identity != null
                ? _identity.NetName.Value.ToString()
                : "Oyuncu " + OwnerClientId;

            byte kind = attacker == NoAttacker ? (byte)2
                      : attacker == OwnerClientId ? (byte)1
                      : (byte)0;

            PlayerIdentity killer = kind == 0 ? PlayerIdentity.For(attacker) : null;

            // Katil olumden once oyundan cikmis olabilir: ismini cozemedigimiz bir satiri
            // bos isimle yazmaktansa "oldu" durumuna dusuruyoruz.
            if (kind == 0 && killer == null) kind = 2;

            if (_identity != null)
                _identity.Deaths.Value = (ushort)(_identity.Deaths.Value + 1);

            // Kill YALNIZCA gercek bir katilde sayilir: intihar ve kaynagi bilinmeyen olum
            // kimseye puan yazmaz.
            if (kind == 0 && killer != null)
            {
                killer.Kills.Value = (ushort)(killer.Kills.Value + 1);

                // TAKIM skoru MatchManager'da KALICI olarak yasar. Kisisel Kills burada kaliyor
                // (skorbordun satirlari onu gosteriyor); ama takim toplami oyuncu ciktiginda
                // dusmemeli — kazanan ona bakiyor.
                Match.MatchManager.ServerAddScore(killer.Team.Value);
            }

            string killerName = killer != null ? killer.NetName.Value.ToString() : string.Empty;
            byte killerTeam = killer != null ? killer.Team.Value : (byte)0;

            KillFeedRpc(
                new FixedString32Bytes(killerName), killerTeam, attacker,
                new FixedString32Bytes(victimName), victimTeam, OwnerClientId,
                kind);
        }

        /// <summary>
        /// Olumu her istemciye duyurur. Yayin KURBANIN kendi NetworkBehaviour'undan cikiyor:
        /// her oyuncu zaten spawn'li bir NetworkObject, yani bu ozellik icin yeni sahne objesi,
        /// yeni prefab ya da sihirbaz adimi GEREKMIYOR.
        ///
        /// Isimler kimlik degil METIN olarak tasiniyor: katil oyundan ciktiginda istemci ID'den
        /// isim cozemez, satir bos kalirdi.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        void KillFeedRpc(FixedString32Bytes killer, byte killerTeam, ulong killerId,
                         FixedString32Bytes victim, byte victimTeam, ulong victimId,
                         byte kind)
        {
            KillReported?.Invoke(new KillInfo
            {
                Killer = killer.ToString(),
                KillerTeam = killerTeam,
                KillerId = killerId,
                Victim = victim.ToString(),
                VictimTeam = victimTeam,
                VictimId = victimId,
                Kind = kind,
            });
        }

        void Update()
        {
            if (!IsSpawned || !IsServer) return;

            if (Dead.Value) { TickSpawn(); return; }

            TickRegen();
        }

        // ------------------------------------------------------------- dogum bekleme

        void TickSpawn()
        {
            // AYNI KAPI: mac disinda ne hasar gecer ne dogum isler. Mac bitince olen ayakta
            // dirilmez, sonucu olu izler (ekip karari). Isinmaya gecerken MatchManager zaten
            // herkesi ayaga kaldirdigi icin kimse burada takili kalmaz.
            if (!Match.MatchManager.DamageAllowed)
            {
                ResetSpawnCounters();
                return;
            }

            byte team = TeamValue;

            // Takim henuz secilmedi (TeamSelector paneli acik / kalibrasyon suruyor). Bu asamada
            // hicbir bolge "onun" degildir — sayaclari isletmeden bekle.
            if (team == 0)
            {
                ResetSpawnCounters();
                return;
            }

            var zone = TeamSpawnZone.For(team);
            if (zone == null)
            {
                // Bolgeler henuz kurulmamis. Sonsuza kadar olu kalmak yerine eski zamanli
                // davranisa dus — yoksa menu 22 calistirilmadan oyun test EDILEMEZDI.
                _noZoneTimer += Time.deltaTime;
                SpawnProgress.Value = Mathf.Clamp01(_noZoneTimer / Mathf.Max(0.1f, noZoneFallbackSeconds));
                if (_noZoneTimer >= noZoneFallbackSeconds) Revive();
                return;
            }

            _noZoneTimer = 0f;

            bool inside = zone.Contains(HeadPosition);
            if (InSpawnZone.Value != inside) InSpawnZone.Value = inside;

            // Cemberden CIKINCA sifirlanir: yoksa cembere bir saniye degip kacmak, sonra donup
            // kaldigi yerden devam etmek ise yarardi.
            _holdTimer = inside ? _holdTimer + Time.deltaTime : 0f;

            // Ilerleme ~%2'lik adimlarla yayinlanir. Her kare yazmak 5 saniye boyunca oyuncu
            // basina yuzlerce gereksiz paket demekti; halka bu adimda da gozle purussuz akiyor.
            float p = Mathf.Clamp01(_holdTimer / Mathf.Max(0.1f, spawnHoldSeconds));
            if (Mathf.Abs(p - SpawnProgress.Value) >= 0.02f || (p >= 1f && SpawnProgress.Value < 1f))
                SpawnProgress.Value = p;

            if (_holdTimer >= spawnHoldSeconds) Revive();
        }

        void Revive()
        {
            Health.Value = MaxHealth;
            Dead.Value = false;
            ResetSpawnCounters();
            _invulnUntil = Time.time + reviveInvuln;
            _lastDamageTime = Time.time;
            _regenAccumulator = 0f;
        }

        /// <summary>Server-only. Oyuncuyu mac basi/isinma icin temiz duruma alir: tam can, ayakta,
        /// sayaclar sifir. Dogum cemberinde beklemeyi ATLAR — mac baslarken ya da isinmaya
        /// donerken herkesin ayakta olmasi gerekiyor, 5 saniye beklemesi degil.
        /// Cagiran: <see cref="Match.MatchManager"/>.</summary>
        public void ServerResetForMatch()
        {
            if (!IsServer) return;
            Revive();
        }

        void ResetSpawnCounters()
        {
            _holdTimer = 0f;
            _noZoneTimer = 0f;
            if (SpawnProgress.Value != 0f) SpawnProgress.Value = 0f;
            if (InSpawnZone.Value) InSpawnZone.Value = false;
        }

        // ------------------------------------------------------------- can yenilenme

        // Server-only. After a lull with no damage (CombatConfig.regenDelay), health climbs back up
        // at regenPerSecond toward regenTargetHealth. Health is a NetworkVariable so the bars follow
        // automatically; raising it never triggers the HUD damage flash (that fires only on a drop).
        void TickRegen()
        {
            var cfg = CombatConfig.Instance;
            if (cfg == null || !cfg.regenEnabled) return;

            int target = Mathf.Min(cfg.regenTargetHealth, MaxHealth);
            if (Health.Value >= target) { _regenAccumulator = 0f; return; }
            if (Time.time - _lastDamageTime < cfg.regenDelay) return;

            _regenAccumulator += cfg.regenPerSecond * Time.deltaTime;
            if (_regenAccumulator >= 1f)
            {
                int add = Mathf.FloorToInt(_regenAccumulator);
                _regenAccumulator -= add;
                Health.Value = Mathf.Min(target, Health.Value + add);
            }
        }
    }
}
