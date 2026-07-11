using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Registers every prefab under Resources/WeaponPrefabs with the NetworkManager so it can be
    /// network-spawned at runtime — no hand-editing of the NetworkPrefabs list. Runs after the
    /// scene loads but BEFORE LanBootstrap starts the session (that waits for user input), so
    /// <see cref="NetworkManager.IsListening"/> is false and AddNetworkPrefab is safe; every
    /// client runs this and registers the identical set (no ForceSamePrefabs mismatch).
    /// </summary>
    public static class WeaponPrefabRegistrar
    {
        static readonly List<GameObject> _prefabs = new List<GameObject>();

        /// <summary>Weapon prefabs discovered under Resources/WeaponPrefabs (for the dev spawner).</summary>
        public static IReadOnlyList<GameObject> Prefabs => _prefabs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Register()
        {
            _prefabs.Clear();
            _prefabs.AddRange(Resources.LoadAll<GameObject>("WeaponPrefabs"));

            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                Debug.LogWarning("[WeaponPrefabRegistrar] Sahnede NetworkManager yok — silahlar kaydedilmedi.");
                return;
            }
            if (nm.IsListening)
            {
                Debug.LogWarning("[WeaponPrefabRegistrar] Ag zaten baslamis — gec kayit atlandi.");
                return;
            }

            foreach (var p in _prefabs)
            {
                if (p == null || p.GetComponent<NetworkObject>() == null) continue;
                // AddNetworkPrefab throws if the same prefab/hash is already registered — harmless
                // here (fresh scene reload = clean list), but guarded so one bad prefab can't abort
                // the rest.
                try { nm.AddNetworkPrefab(p); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[WeaponPrefabRegistrar] '{p.name}' kaydedilemedi: {e.Message}");
                }
            }
        }
    }
}
