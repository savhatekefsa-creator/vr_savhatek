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

        /// <summary>Strip Unity's "(Clone)" suffix (appended to every Instantiate, so
        /// network-spawned weapons arrive as "HK416(Clone)") so weaponNameEquals still matches
        /// the spawned instance, not only the hand-placed scene object.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string clone = "(Clone)";
            while (name.EndsWith(clone))
                name = name.Substring(0, name.Length - clone.Length);
            return name.TrimEnd();
        }

        /// <summary>Best profile for a weapon name (exact beats contains), or null.</summary>
        public static WeaponGripProfile FindProfile(string weaponName)
        {
            weaponName = CleanName(weaponName);
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
