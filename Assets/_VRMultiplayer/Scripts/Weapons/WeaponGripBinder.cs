using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Attaches a <see cref="WeaponGrip"/> to every spawning <see cref="GrabbableObject"/> whose
    /// name matches a <see cref="WeaponGripProfile"/> — on every client, at runtime, with zero
    /// prefab or scene edits. Profiles are loaded once from Resources/WeaponGripProfiles;
    /// matching prefers exact name over contains. A weapon with no matching profile is left
    /// completely untouched (today's behaviour).
    /// </summary>
    public static class WeaponGripBinder
    {
        static WeaponGripProfile[] _profiles;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            // Statics reset on domain reload, so re-subscribing every play session is safe.
            GrabbableObject.AnySpawned -= OnGrabbableSpawned;
            GrabbableObject.AnySpawned += OnGrabbableSpawned;
            _profiles = null;
        }

        static void OnGrabbableSpawned(GrabbableObject g)
        {
            var profile = FindProfile(g.name);
            if (profile == null) return;

            var grip = g.GetComponent<WeaponGrip>();
            if (grip == null) grip = g.gameObject.AddComponent<WeaponGrip>();
            grip.Bind(profile);
        }

        /// <summary>Best profile for a weapon name (exact beats contains), or null.</summary>
        public static WeaponGripProfile FindProfile(string weaponName)
        {
            if (_profiles == null)
                _profiles = Resources.LoadAll<WeaponGripProfile>("WeaponGripProfiles");

            WeaponGripProfile best = null;
            int bestScore = 0;
            foreach (var p in _profiles)
            {
                if (p == null) continue;
                int s = p.MatchScore(weaponName);
                if (s > bestScore) { bestScore = s; best = p; }
            }
            return best;
        }
    }
}
