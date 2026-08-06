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

        // Namlunun ICINDE oldugu hitbox'lari yakalamak icin (bkz. MuzzleInsideZone). 16 = ayni
        // noktada ust uste gelebilecek bolge sayisi icin genis tavan.
        static readonly Collider[] _overlap = new Collider[16];

        /// <summary>Tek bir rayin otoriter isabet cozumu (pellet basina bir kez cagrilir).
        /// Donus: bu rayin gordugu dusman hitbox sayisi (teshis logu icin). Hasar pellet
        /// basina pelletDamageScale ile carpilir — pompalida tanesi zayif, hepsi olumcul.
        /// hitFlesh yalnizca HASAR UYGULANAN oyuncu isabetinde true doner (istemciler kan
        /// efektini bundan cizer); dost-atesi blogu ve duvar isabeti false kalir.</summary>
        public static int RaycastOne(Transform weaponRoot, Vector3 origin, Vector3 dir,
            float range, float pelletDamageScale, System.Func<ZoneType, int> damageFor,
            ulong shooter, byte shooterTeam, out Vector3 end, out Vector3 hitNormal,
            out bool hitFlesh)
        {
            end = origin + dir * range;
            // Sifir = mermi izi birakma. YALNIZCA sabit geometri normal doner: hareketli bir
            // oyuncuya dunya-uzayi izi cakarsak oyuncu yurudugunde iz havada asili kalirdi.
            hitNormal = Vector3.zero;
            hitFlesh = false;

            Vector3 start = origin + dir * 0.03f;

            // DIPCIK MESAFESI: Unity'nin raycast'i, baslangic noktasinin ICINDE oldugu collider'i
            // GORMEZ (belgelenmis davranis). Namlu rakibin govdesine gomuldugunde o oyuncunun
            // hitbox'i sessizce atlaniyor ve mermi ARKASINDAKINE gidiyordu — en yakin mesafede
            // atis iskaliyordu. Isini atmadan once baslangici ICINE ALAN bolge var mi bakiyoruz;
            // varsa mesafesi sifir demektir, her raycast isabetinden once gelir.
            int inside = MuzzleInsideZone(weaponRoot, start, origin, pelletDamageScale,
                                          damageFor, shooter, shooterTeam, out hitFlesh);
            if (inside > 0) { end = start; return inside; }

            // NonAlloc + yakindan-uzaga yurume: "kendi govdeni gecip devam et" mantigi hit
            // sirasina baglidir, sort atlanamaz.
            int hitCount = Physics.RaycastNonAlloc(start, dir, _rayHits, range,
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
                    end = h.point;
                    hitFlesh = true; // istemciler bu noktada kan cizer (iz/decal degil)
                    break;
                }

                // MERMI GECIREN GEOMETRI (tel raf, merdiven): isin durmaz, iz de birakmaz.
                // Bkz. BulletPassThrough — collider fiziksel olarak duruyor, yalnizca
                // silah isinlari onu gormuyor.
                if (BulletPassThrough.Passes(h.collider)) continue;

                end = h.point; // first solid/non-player hit stops the ray
                hitNormal = h.normal;
                break;
            }
            return hitboxesSeen;
        }

        /// <summary>ISTEMCI-TARAFI FX ONGORUSU: <see cref="RaycastOne"/> ile AYNI geometri
        /// yuruyusu — HASAR YOK, LOG YOK, sunucu durumu YOK. Sahibin izleri/alev/mermi izi
        /// sunucu gidis-donusunu beklemesin diye var (bkz. NetworkWeapon.Fire: ses zaten
        /// yerel caliyordu, gorseller RPC turundan geliyordu ve otomatikte "gecikmeli ates"
        /// hissi veriyordu). Kurallari degistirirken RaycastOne ile SENKRON tut.</summary>
        public static void PredictOne(Transform weaponRoot, Vector3 origin, Vector3 dir,
            float range, ulong shooter, byte shooterTeam,
            out Vector3 end, out Vector3 hitNormal, out bool hitFlesh)
        {
            end = origin + dir * range;
            hitNormal = Vector3.zero;
            hitFlesh = false;

            Vector3 start = origin + dir * 0.03f;

            // Dipcik mesafesi (namlu hitbox'in ICINDE): RaycastOne'daki kuralin hasarsiz esi.
            int n = Physics.OverlapSphereNonAlloc(start, 0.02f, _overlap,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c == null || c.transform.IsChildOf(weaponRoot)) continue;
                var z = c.GetComponentInParent<HitZone>();
                if (z == null || z.health == null) continue;
                if ((c.ClosestPoint(start) - start).sqrMagnitude > 1e-6f) continue;
                if (z.health.OwnerClientId == shooter) continue;
                byte zt = z.health.TeamValue;
                end = start;
                hitFlesh = zt == 0 || zt != shooterTeam; // dost atesi: durur ama kan yok
                return;
            }

            int hitCount = Physics.RaycastNonAlloc(start, dir, _rayHits, range,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            System.Array.Sort(_rayHits, 0, hitCount, _byDistance);

            for (int hi = 0; hi < hitCount; hi++)
            {
                var h = _rayHits[hi];
                if (h.collider.transform.IsChildOf(weaponRoot)) continue;

                var zone = h.collider.GetComponentInParent<HitZone>();
                if (zone != null && zone.health != null)
                {
                    if (zone.health.OwnerClientId == shooter) continue; // kendi govden gec
                    byte t = zone.health.TeamValue;
                    end = h.point;
                    hitFlesh = t == 0 || t != shooterTeam;
                    return;
                }

                if (BulletPassThrough.Passes(h.collider)) continue; // RaycastOne ile ayni kural

                end = h.point;
                hitNormal = h.normal;
                return;
            }
        }

        /// <summary>Isinin BASLANGICINI icine alan dusman hitbox'i var mi? Varsa hasari uygular
        /// ve gorulen hitbox sayisini doner; yoksa 0. hitFlesh = hasar gercekten uygulandi.</summary>
        // Raycast'in goremedigi tek durum budur: origin bir collider'in ICINDE. Kucuk bir kure
        // ile adaylari toplayip gercekten iceride olani ClosestPoint ile dogruluyoruz — sadece
        // yakininda olanlari saymamak icin. ClosestPoint yalnizca HitZone tasiyan collider'lara
        // cagriliyor (kure/kapsul); disbukey olmayan MeshCollider'da o cagri exception atar.
        static int MuzzleInsideZone(Transform weaponRoot, Vector3 start, Vector3 origin,
            float pelletDamageScale, System.Func<ZoneType, int> damageFor,
            ulong shooter, byte shooterTeam, out bool hitFlesh)
        {
            hitFlesh = false;
            int n = Physics.OverlapSphereNonAlloc(start, 0.02f, _overlap,
                Physics.AllLayers, QueryTriggerInteraction.Collide);

            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c == null || c.transform.IsChildOf(weaponRoot)) continue;

                var zone = c.GetComponentInParent<HitZone>();
                if (zone == null || zone.health == null) continue;

                // Yakininda degil, GERCEKTEN icinde mi: icerideki bir nokta icin ClosestPoint
                // noktanin kendisini doner.
                if ((c.ClosestPoint(start) - start).sqrMagnitude > 1e-6f) continue;

                var health = zone.health;
                if (health.OwnerClientId == shooter) continue; // kendi govden: gec

                byte t = health.TeamValue;
                if (t != 0 && t == shooterTeam)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.Log($"[Silah] Dipcikte isabet ENGELLENDI (ayni takim {t}): atan {shooter} -> {health.OwnerClientId}");
#endif
                    return 1; // dost: hasar yok ama isin burada durur
                }

                int dmg = Mathf.Max(1, Mathf.RoundToInt(damageFor(zone.zoneType) * pelletDamageScale));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[Silah] DIPCIK ISABETI (namlu govdenin icinde)! atan {shooter} -> hedef {health.OwnerClientId}, bolge {zone.zoneName}, {dmg} hasar.");
#endif
                health.ServerApplyDamage(dmg, shooter, origin);
                hitFlesh = true;
                return 1;
            }
            return 0;
        }
    }
}
