using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// A single damage region on a player's body (head, torso, an arm/hand, or the legs). The
    /// weapon's server-authoritative raycast reads which region it hit off this component and looks
    /// the damage up in <see cref="CombatConfig"/>, so where the shot lands matters.
    ///
    /// Non-visual: the collider is a trigger created at runtime by <see cref="PlayerHitbox"/>.
    /// Never touches PlayerHealth's contract — it just points at the same <see cref="health"/>.
    /// </summary>
    public class HitZone : MonoBehaviour
    {
        [Tooltip("Bu bolgenin canini dusurecegi oyuncunun saglik bileseni.")]
        public PlayerHealth health;

        [Tooltip("Vurulan bolge; hasar miktari CombatConfig'den buna gore okunur.")]
        public ZoneType zoneType = ZoneType.Torso;

        [Tooltip("Log/teshis icin bolge adi (Kafa, Govde, SolKol...).")]
        public string zoneName = "Govde";
    }
}
