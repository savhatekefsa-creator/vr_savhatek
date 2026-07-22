using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
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

        /// <summary>Server-only. Reduce health; handle death.</summary>
        public void ServerApplyDamage(int amount, ulong attacker)
        {
            if (!IsServer || Dead.Value || amount <= 0) return;
            if (Time.time < _invulnUntil) return; // just revived — brief grace

            Health.Value = Mathf.Max(0, Health.Value - amount);
            _lastDamageTime = Time.time;   // regen bekleme suresini sifirla
            _regenAccumulator = 0f;
            if (Health.Value <= 0)
                Die();
        }

        void Die()
        {
            Dead.Value = true;
            SpawnProgress.Value = 0f;
            InSpawnZone.Value = false;
            _holdTimer = 0f;
            _noZoneTimer = 0f;

            // Elinde ne varsa birakir: olu oyuncu silah tasimaya devam etmemeli, ayrica silah
            // yerde kalirsa baskasi alabilir.
            GrabbableObject.ServerReleaseAllHeldBy(OwnerClientId);
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
