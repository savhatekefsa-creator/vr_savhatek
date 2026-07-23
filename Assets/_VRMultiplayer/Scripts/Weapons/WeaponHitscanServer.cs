using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Sunucu-otoriter isin (hitscan) cozumu. NetworkWeapon'dan ayrildi: ag/NetworkBehaviour
    /// durumu tutmaz, tum girdiler parametre — sahnesiz birim testinde dogrudan cagrilabilir.
    /// Hasar miktarini cagiran cozer (damageFor): bolge hasari config zinciri silahin isidir,
    /// isin taramasinin degil.
    /// </summary>
    public static class WeaponHitscanServer
    {
        // Sabit tampon + cache'li karsilastirici: RaycastAll her pellet'te sonuc dizisi,
        // sort lambda'si da delegate alloc ediyordu. 64 = tek isinin ayni anda kestigi
        // collider sayisi icin genis tavan (asilirsa fazlasi sessizce dusar).
        static readonly RaycastHit[] _rayHits = new RaycastHit[64];
        static readonly IComparer<RaycastHit> _byDistance =
            Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

        /// <summary>Tek bir rayin otoriter isabet cozumu (pellet basina bir kez cagrilir).
        /// Donus: bu rayin gordugu dusman hitbox sayisi (teshis logu icin). Hasar pellet
        /// basina pelletDamageScale ile carpilir — pompalida tanesi zayif, hepsi olumcul.</summary>
        public static int RaycastOne(Transform weaponRoot, Vector3 origin, Vector3 dir,
            float range, float pelletDamageScale, System.Func<ZoneType, int> damageFor,
            ulong shooter, byte shooterTeam, out Vector3 end, out Vector3 hitNormal)
        {
            end = origin + dir * range;
            // Sifir = mermi izi birakma. YALNIZCA sabit geometri normal doner: hareketli bir
            // oyuncuya dunya-uzayi izi cakarsak oyuncu yurudugunde iz havada asili kalirdi.
            hitNormal = Vector3.zero;

            // NonAlloc + yakindan-uzaga yurume: "kendi govdeni gecip devam et" mantigi hit
            // sirasina baglidir, sort atlanamaz.
            int hitCount = Physics.RaycastNonAlloc(origin + dir * 0.03f, dir, _rayHits, range,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            System.Array.Sort(_rayHits, 0, hitCount, _byDistance);

            int hitboxesSeen = 0;
            for (int hi = 0; hi < hitCount; hi++)
            {
                var h = _rayHits[hi];
                if (h.collider.transform.IsChildOf(weaponRoot)) continue; // own weapon

                // Regional damage: the ray hits a HitZone (head/torso/arm/leg); the per-region
                // amount is resolved on the SERVER (clients can't send damage values — security).
                var zone = h.collider.GetComponentInParent<HitZone>();
                if (zone != null && zone.health != null)
                {
                    var health = zone.health;
                    // Never hit yourself; keep going past your own body.
                    if (health.OwnerClientId == shooter) continue;
                    hitboxesSeen++;
                    byte t = health.TeamValue;
                    if (t != 0 && t == shooterTeam)
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        Debug.Log($"[Silah] Isabet ENGELLENDI (ayni takim {t}): atan {shooter} -> {health.OwnerClientId}");
#endif
                        end = h.point; break; // block, no damage (friendly fire off)
                    }
                    int dmg = Mathf.Max(1, Mathf.RoundToInt(damageFor(zone.zoneType) * pelletDamageScale));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.Log($"[Silah] ISABET! atan {shooter} (takim {shooterTeam}) -> hedef {health.OwnerClientId} (takim {t}), bolge {zone.zoneName}, {dmg} hasar. Kalan: {Mathf.Max(0, health.Health.Value - dmg)}");
#endif
                    // origin = atisin ciktigi namlu noktasi: kurbanin HUD'u yon flasini bundan cizer.
                    health.ServerApplyDamage(dmg, shooter, origin);
                    end = h.point; break;
                }

                end = h.point; // first solid/non-player hit stops the ray
                hitNormal = h.normal;
                break;
            }
            return hitboxesSeen;
        }
    }
}
