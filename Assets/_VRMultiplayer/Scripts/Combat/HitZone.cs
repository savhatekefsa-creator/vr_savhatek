using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// A single damage region on a player's body (head, torso, an arm/hand, or the legs). The
    /// weapon's server-authoritative raycast reads this off the collider it hits and scales the
    /// weapon's base damage by <see cref="damageMultiplier"/>, so where the shot lands matters.
    ///
    /// Non-visual: the collider is a trigger created at runtime by <see cref="PlayerHitbox"/>.
    /// Never touches PlayerHealth's contract — it just points at the same <see cref="health"/>.
    /// </summary>
    public class HitZone : MonoBehaviour
    {
        [Tooltip("Bu bolgenin canini dusurecegi oyuncunun saglik bileseni.")]
        public PlayerHealth health;

        [Tooltip("Silah temel hasari bununla carpilir (kafa 2.0, govde 1.0, kol/bacak 0.6).")]
        public float damageMultiplier = 1f;

        [Tooltip("Log/teshis icin bolge adi (Kafa, Govde, SolKol...).")]
        public string zoneName = "Govde";
    }
}
