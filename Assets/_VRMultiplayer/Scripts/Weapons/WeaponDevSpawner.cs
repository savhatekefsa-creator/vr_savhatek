#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// DEV-ONLY (editor + development builds): a server-side on-screen panel that spawns any
    /// registered weapon prefab in front of the host, so you can test weapons that aren't
    /// hand-placed in the scene (e.g. the paintball marker). Compiled out of release builds.
    /// Spawns authoritatively on the server; the grip binder attaches the profile on every
    /// client via GrabbableObject.AnySpawned, so no weapon-specific spawn code is needed.
    /// </summary>
    public class WeaponDevSpawner : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponDevSpawner");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponDevSpawner>();
        }

        void OnGUI()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var prefabs = WeaponPrefabRegistrar.Prefabs;
            if (prefabs == null || prefabs.Count == 0) return;

            const float w = 230f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 10f, 10f, w, 320f), GUI.skin.box);
            GUILayout.Label("SILAH SPAWN (dev / server)");
            for (int i = 0; i < prefabs.Count; i++)
            {
                var p = prefabs[i];
                if (p == null) continue;
                if (GUILayout.Button($"Spawn {p.name}"))
                    SpawnInFront(p);
            }
            GUILayout.EndArea();
        }

        static void SpawnInFront(GameObject prefab)
        {
            // Drop it within grab reach in front of the host's view (HMD, or the PC test camera).
            Vector3 pos = new Vector3(0f, 1.2f, 1f);
            var rig = XRRigReference.Instance;
            if (rig != null && rig.head != null)
            {
                Vector3 fwd = rig.head.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                pos = rig.head.position + fwd.normalized * 0.5f;
                pos.y = Mathf.Max(0.3f, rig.head.position.y - 0.3f);
            }

            var go = Instantiate(prefab, pos, Quaternion.identity);
            var no = go.GetComponent<NetworkObject>();
            if (no != null) no.Spawn();
            else Destroy(go);
        }
    }
}
#endif
