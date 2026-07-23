using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// SONSUZ RAF: izgaradaki silahlardan biri alininca yerine ANINDA yenisi gelir (ekip
    /// karari: bardaki silahlar sinirsiz). Silah secicinin butun mimarisi de buna dayanir —
    /// canta NESNE degil TUR saklar, cunku ayni turden her zaman yenisi uretilebilir.
    ///
    /// Nasil: sunucu, sahneye ELLE yerlestirilmis silahlari (NetworkObject.IsSceneObject) bir
    /// kez kaydeder — konum + yon + tur kalibi (Resources/WeaponPrefabs eslesmesi). Sonra her
    /// yarim saniyede raf yuvalarina bakar: yuvadaki silah alinmis / yok olmus / yerinden
    /// gitmisse, kaliptan yenisi ayni noktaya spawn edilir. Yenisi de alininca bir yenisi —
    /// sonsuza kadar.
    ///
    /// Kalibi olmayan silahlar (Resources/WeaponPrefabs'ta karsiligi yoksa) atlanir: onlar
    /// yenilenemez, tek kopya kalir. Taslar/esyalar da atlanir (tutus profili yok).
    /// SUNUCUDA calisir; istemciler spawn'lari agdan gorur. HandGrabber/GrabbableObject'e
    /// dokunmaz — sadece public durumlarini okur.
    /// </summary>
    public class WeaponRackRespawner : MonoBehaviour
    {
        class RackSlot
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public GameObject Prefab;
            public GrabbableObject Current; // su an yuvada duran (null = yenisi lazim)
        }

        readonly List<RackSlot> _slots = new List<RackSlot>();
        readonly HashSet<GrabbableObject> _registered = new HashSet<GrabbableObject>();
        float _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponRackRespawner");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponRackRespawner>();
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return; // raf sunucunun mali

            RegisterSceneWeapons();

            foreach (var s in _slots)
            {
                // Yuva dolu mu? Duran, tutulmayan ve YERINDE olan bir silah varsa dokunma.
                if (s.Current != null)
                {
                    bool gone = !s.Current.isActiveAndEnabled ||
                                s.Current.IsHeld ||
                                (s.Current.transform.position - s.Pos).sqrMagnitude > 0.16f; // 40 cm
                    if (!gone) continue;
                }

                // Alinmis/gitmis -> kaliptan yenisi ayni noktaya. (Alinan silah oyuncunun olur;
                // profilli silahlar birakilinca zaten cantaya karisip yok oluyor.)
                var go = Instantiate(s.Prefab, s.Pos, s.Rot);
                var no = go.GetComponent<NetworkObject>();
                if (no == null) { Destroy(go); continue; }
                no.Spawn();
                s.Current = go.GetComponent<GrabbableObject>();
            }
        }

        /// <summary>Sahneye elle konmus, kalibi olan her silahi BIR KEZ raf yuvasi olarak
        /// kaydeder. Calisma aninda spawn edilenler (IsSceneObject degil) raf sayilmaz —
        /// yoksa oyuncunun elinden dusen her silah kendini coğaltirdi.</summary>
        void RegisterSceneWeapons()
        {
            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                if (_registered.Contains(g)) continue;

                var no = g.GetComponent<NetworkObject>();
                if (no == null || !no.IsSpawned || no.IsSceneObject != true) continue;

                _registered.Add(g); // kalipsiz da olsa bir daha bakma

                var prof = WeaponGripBinder.FindProfile(g.name);
                if (prof == null) continue; // profil yok = silah degil (tas/esya) -> raf disi
                var prefab = WeaponInventory.FindPrefabFor(prof.name);
                if (prefab == null)
                {
                    Debug.Log($"[WeaponRack] '{g.name}' icin kalip yok — yenilenemez (Tools > 38 ile uretilebilir).");
                    continue;
                }

                _slots.Add(new RackSlot
                {
                    Pos = g.transform.position,
                    Rot = g.transform.rotation,
                    Prefab = prefab,
                    Current = g,
                });
                Debug.Log($"[WeaponRack] Raf yuvasi: {g.name}  (kalip: {prefab.name})");
            }
        }
    }
}
