using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
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
            public uint OwnerId;            // 0 = sahneye elle konmus; >0 = insa modu rafi
        }

        readonly List<RackSlot> _slots = new List<RackSlot>();
        readonly HashSet<GrabbableObject> _registered = new HashSet<GrabbableObject>();
        float _next;

        /// <summary>
        /// Ceiling on weapons a BUILT map may hold, across every rack in it.
        ///
        /// Each one is a NetworkObject with a ClientNetworkTransform, and the constructor's whole
        /// design is the opposite of that — scenery syncs as layout data precisely so a room full
        /// of props costs nothing on the wire. Weapons are the deliberate exception, so the
        /// exception gets a number.
        ///
        /// 16 -> 32 (2026-08-05). 16 "projenin gonderdigi tam takim" diye secilmisti, ama
        /// Rack_Wall prefabinin TEK BASINA 16 yuvasi var: bir duvar rafi koyan oyuncu butceyi
        /// bitiriyordu ve ikinci raf sessizce BOS geliyordu (mesh her peer'da harita verisinden
        /// kuruluyor, silahlari yalnizca sunucu mintliyor). Disaridan "bozuk" gorunuyordu.
        ///
        /// OLCULMEDI: 32 silah = 32 ClientNetworkTransform. Quest'te baglanti ve kare suresine
        /// etkisi cihazda olculmeli; kotu cikarsa cozum sayiyi geri dusurmek DEGIL, Rack_Wall'in
        /// yuva sayisini azaltmak olmali — sinir haritanin tamamina ait, tek bir prefaba degil.
        /// </summary>
        public const int MaxConstructorWeapons = 32;

        /// <summary>Weapon slots currently claimed by build-mode racks.</summary>
        public static int ConstructorWeaponCount { get; private set; }

        static WeaponRackRespawner _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponRackRespawner");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<WeaponRackRespawner>();
        }

        // ------------------------------------------------------------- insa modu raflari

        /// <summary>
        /// Adopts the slots of a rack the player just built, so the refill loop starts feeding
        /// them exactly as it feeds the hand-placed weapons in the scene.
        ///
        /// SERVER ONLY, like everything else here — the rack MESH is built on every peer from
        /// the map layout, but the weapons standing in it are real networked objects and only
        /// the server may mint those. Returns how many slots were taken, which is fewer than
        /// asked for when <see cref="MaxConstructorWeapons"/> is reached.
        /// </summary>
        public static int RegisterConstructorRack(uint instanceId, GameObject builtRack)
        {
            if (_instance == null || builtRack == null || instanceId == 0) return 0;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient && !nm.IsServer) return 0;   // raf sunucunun mali

            int taken = 0;
            foreach (var slot in builtRack.GetComponentsInChildren<WeaponRackSlot>(true))
            {
                if (ConstructorWeaponCount >= MaxConstructorWeapons)
                {
                    Debug.LogWarning($"[WeaponRack] Harita silah siniri dolu " +
                                     $"({MaxConstructorWeapons}) — '{slot.weaponPrefabName}' bos kaldi.");
                    break;
                }

                var prefab = slot.Resolve();
                if (prefab == null)
                {
                    Debug.LogWarning($"[WeaponRack] '{slot.weaponPrefabName}' kalibi yok — yuva atlandi.");
                    continue;
                }

                _instance._slots.Add(new RackSlot
                {
                    Pos = slot.transform.position,
                    Rot = slot.transform.rotation,
                    Prefab = prefab,
                    OwnerId = instanceId,
                    Current = null,          // ilk donguda dolar
                });
                ConstructorWeaponCount++;
                taken++;
            }
            return taken;
        }

        /// <summary>
        /// Drops a built rack's slots and clears out the weapons still sitting in them.
        ///
        /// A weapon a player is HOLDING is left alone: it stopped being the rack's the moment
        /// they picked it up, and yanking it out of someone's hand because a wall was deleted
        /// elsewhere is not a thing a game should do.
        /// </summary>
        public static void UnregisterConstructorRack(uint instanceId)
        {
            if (_instance == null || instanceId == 0) return;

            var slots = _instance._slots;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (slots[i].OwnerId != instanceId) continue;

                DespawnSlotWeapon(slots[i]);

                slots.RemoveAt(i);
                ConstructorWeaponCount = Mathf.Max(0, ConstructorWeaponCount - 1);
            }
        }

        /// <summary>
        /// Forgets every built rack — the map is being torn down and rebuilt.
        ///
        /// YUVADAKI SILAHLAR DA GIDER. Kaydi dusurmek yetmiyordu: silahlar gercek ag objeleri
        /// ve haritanin COCUGU DEGIL, o yuzden <see cref="MapBuilder.Clear"/> onlara dokunmuyor.
        /// Kayit dusup objeler kalinca "yeni harita" bos bir zeminde ONCEKI haritanin
        /// silahlariyla aciliyordu — ve o silahlar artik hicbir rafa bagli olmadigi icin bir
        /// daha toplanmiyorlardi bile.
        /// </summary>
        public static void ClearConstructorRacks()
        {
            if (_instance == null) return;

            var slots = _instance._slots;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (slots[i].OwnerId == 0) continue;   // sahneye elle konmus: haritaya ait degil

                DespawnSlotWeapon(slots[i]);
                slots.RemoveAt(i);
            }
            ConstructorWeaponCount = 0;
        }

        /// <summary>
        /// Yuvadaki silahi ortadan kaldirir.
        ///
        /// TUTULAN SILAHA DOKUNULMAZ: oyuncu aldigi anda o silah rafin olmaktan cikti, ve baska
        /// yerde bir duvar silindi diye elinden almak bir oyunun yapacagi sey degil.
        ///
        /// DESPAWN SUNUCUNUN ISI. Bu metot her peer'da calisan Clear/Unregister yolundan
        /// cagriliyor; istemcide Despawn cagirmak Netcode hatasi verir. Istemci zaten sunucunun
        /// despawn'ini agdan goruyor.
        /// </summary>
        static void DespawnSlotWeapon(RackSlot s)
        {
            var held = s.Current;
            if (held == null || held.IsHeld || !held.isActiveAndEnabled) return;

            var nm = NetworkManager.Singleton;
            var no = held.GetComponent<NetworkObject>();

            if (no != null && no.IsSpawned)
            {
                if (nm != null && nm.IsServer) no.Despawn();
                return;
            }
            Destroy(held.gameObject);
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
            var actives = GrabbableObject.Active; // spawn kayit listesi — sahne taramasi + dizi alloc'u yok
            for (int gi = 0; gi < actives.Count; gi++)
            {
                var g = actives[gi];
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
