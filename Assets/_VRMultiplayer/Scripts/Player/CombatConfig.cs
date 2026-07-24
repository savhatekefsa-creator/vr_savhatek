using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// A body region a shot can land in. Each maps to a damage value in <see cref="CombatConfig"/>.
    /// </summary>
    public enum ZoneType { Head, Torso, Arm, Leg }

    /// <summary>
    /// The single tuning source for combat: how much each body region takes per hit, and how
    /// health regenerates after a lull in damage. Kept as one asset under a Resources folder so
    /// every script reads the SAME numbers via <see cref="Instance"/> at runtime — no per-prefab
    /// wiring, matching how the rest of this project builds its objects in code.
    ///
    /// Server-authoritative: the weapon reads <see cref="DamageFor"/> on the server and PlayerHealth
    /// runs regen on the server. Clients never send these numbers.
    ///
    /// NOT here on purpose: max health stays a const (100) in <see cref="PlayerHealth"/>, because the
    /// health bars derive their fill from PlayerHealth.MaxHealth — a second, divergent max would
    /// desync them. Change combat feel from this asset; leave the 100 alone.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Combat Config", fileName = "CombatConfig")]
    public class CombatConfig : ScriptableObject
    {
        [Header("Bolgesel hasar (mutlak — tek isabette dusen can)")]
        [Tooltip("Kafa vurusu.")] public int headDamage = 10;
        [Tooltip("Govde vurusu.")] public int torsoDamage = 5;
        [Tooltip("Kol/el vurusu.")] public int armDamage = 3;
        [Tooltip("Bacak vurusu.")] public int legDamage = 3;

        [Header("Can yenilenme (kademeli)")]
        [Tooltip("Can yenilenmesi acik mi?")]
        public bool regenEnabled = true;
        [Tooltip("Son hasardan sonra yenilenmenin baslamasi icin gecmesi gereken sure (saniye).")]
        public float regenDelay = 30f;
        [Tooltip("Yenilenme basladiktan sonra saniyede dolan can.")]
        public float regenPerSecond = 10f;
        [Tooltip("Canin dolacagi ust sinir (0-100). PlayerHealth.MaxHealth'i asamaz.")]
        public int regenTargetHealth = 100;

        /// <summary>Absolute damage for a hit on the given region.</summary>
        public int DamageFor(ZoneType zone)
        {
            switch (zone)
            {
                case ZoneType.Head:  return headDamage;
                case ZoneType.Torso: return torsoDamage;
                case ZoneType.Arm:   return armDamage;
                case ZoneType.Leg:   return legDamage;
                default:             return torsoDamage;
            }
        }

        // ------------------------------------------------------------- singleton

        static CombatConfig _instance;

        /// <summary>
        /// The shared config asset, loaded once from any Resources folder ("CombatConfig"). Falls
        /// back to an in-memory default if the asset is missing, so gameplay never nulls out.
        /// </summary>
        public static CombatConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<CombatConfig>("CombatConfig");
                    if (_instance == null)
                    {
                        Debug.LogWarning("[CombatConfig] Resources/CombatConfig.asset bulunamadi — " +
                                         "kod-ici varsayilan degerler kullaniliyor.");
                        _instance = CreateInstance<CombatConfig>();
                    }
                }
                return _instance;
            }
        }
    }
}
