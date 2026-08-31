using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// "Bu parcanin USTUNE basmak nasil ses cikarir?" — zemin kaplamalarina takilan isaretci.
    ///
    /// WHY IT IS A COMPONENT ON THE PROP AND NOT A TABLE SOMEWHERE: the thing that knows a patch
    /// is broken glass is the patch. A central "surface id -> sound" registry would have to be
    /// kept in step with the prop library by hand, and the first time someone added a gravel
    /// patch without touching the table it would crunch like concrete.
    ///
    /// EVERY PEER ANSWERS THIS QUESTION FOR ITSELF, and that is what makes the whole feature
    /// cost nothing on the network. Placed props are built locally from the map layout on every
    /// client (<see cref="Constructor.MapBuilder"/>), and a player's root position is already
    /// replicated, so each client can work out that a remote player just stepped on glass using
    /// only data it already has. No RPC, no event, no ownership question.
    ///
    /// COLLIDER MUST BE A TRIGGER. <see cref="WorldSolids.IsSolid"/> counts any non-trigger
    /// collider as world geometry, so a solid collider here would make a flat patch of glass
    /// block gunfire (<c>MuzzleWallBlock</c>) and darken the screen when a head passed over it
    /// (<c>HeadWallFade</c>). Trigger colliders are excluded there, which is exactly what this
    /// needs anyway — the query below asks for triggers explicitly.
    /// </summary>
    [DisallowMultipleComponent]
    public class FootSurface : MonoBehaviour
    {
        [Tooltip("Yalnizca teshis/okunabilirlik icin — kod bu adla dallanmaz.")]
        public string surfaceName = "cam";

        [Tooltip("Adim klibi yolu ONEKI (Resources altinda). Varyantlar 1'den baslayarak eklenir: " +
                 "'WeaponSounds/glass_step_' -> glass_step_1, glass_step_2 ...")]
        public string clipPrefix = "WeaponSounds/glass_step_";

        [Min(1)] public int clipVariants = 3;

        /// <summary>
        /// How loud the player's OWN step is. Higher than the normal footstep on purpose: normal
        /// steps are kept quiet because hearing yourself walk in VR is tiring, but the entire
        /// point of glass is that you notice you are standing on it.
        /// </summary>
        [Tooltip("Oyuncunun KENDI adiminin siddeti. Normal adimdan yuksek olmali — bu " +
                 "kaplamanin varlik sebebi, uzerine bastigini FARK ETMEN.")]
        [Range(0f, 1f)] public float ownVolume = 0.55f;

        [Tooltip("BASKALARININ duydugu siddet.")]
        [Range(0f, 1f)] public float otherVolume = 1f;

        /// <summary>
        /// How far the sound carries. THIS IS THE GAME MECHANIC, not a mixing detail.
        ///
        /// Making a player hesitate needs the hesitation to be RATIONAL: if glass only looked
        /// dangerous, avoiding it would be superstition. Carrying further than an ordinary
        /// footstep (22 m) turns it into a real trade — cross here and be heard, or go around
        /// and lose the time. It also composes with the existing speed gate for free: below
        /// <c>MinWalkSpeed</c> no step is counted at all, so moving slowly over glass is quiet
        /// without a single line written for it.
        /// </summary>
        [Tooltip("Sesin tasidigi mesafe (m). Normal adim 22 m — bunun daha uzak olmasi, " +
                 "'buradan gecersem duyulurum' bedelini gercek kilan sey.")]
        [Min(1f)] public float maxDistance = 34f;

        [Tooltip("Basildiginda kumandalara verilen titresim. 0 = kapali.")]
        [Range(0f, 1f)] public float hapticAmplitude = 0.22f;

        [Min(0f)] public float hapticDuration = 0.05f;

        // ------------------------------------------------------------- sorgu

        /// <summary>Ayak cevresinde taranan yaricap (m).</summary>
        const float ProbeRadius = 0.25f;

        /// <summary>Zeminden ne kadar yukarida taranacagi (m) — yassi kaplamalari yakalamak icin.</summary>
        const float ProbeHeight = 0.05f;

        // Adim basina bir kez calisiyor ama yine de tahsissiz: cok oyunculu bir maçta her
        // oyuncu icin ayri ayri ateslenir ve VR'da GC duraklamasi dogrudan hissedilir.
        static readonly Collider[] _hits = new Collider[8];

        /// <summary>
        /// The surface under <paramref name="footPos"/> (the player root, which sits on the
        /// floor), or null for ordinary ground.
        ///
        /// Asks for triggers EXPLICITLY: these patches are trigger-only by design (see the type
        /// summary), and the default query setting would skip every one of them.
        /// </summary>
        public static FootSurface Under(Vector3 footPos)
        {
            int n = Physics.OverlapSphereNonAlloc(
                footPos + Vector3.up * ProbeHeight, ProbeRadius, _hits,
                ~0, QueryTriggerInteraction.Collide);

            for (int i = 0; i < n; i++)
            {
                if (_hits[i] == null) continue;
                var s = _hits[i].GetComponentInParent<FootSurface>();
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>Bu kaplamanin klip yolu, varyant secilmis halde.</summary>
        public string ClipPath(int variantIndex) =>
            clipPrefix + (Mathf.Abs(variantIndex) % Mathf.Max(1, clipVariants) + 1);
    }
}
