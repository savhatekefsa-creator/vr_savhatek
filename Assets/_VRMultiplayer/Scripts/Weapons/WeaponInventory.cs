using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
   
    public enum WeaponCategory { Heavy = 0, Pistol = 1, Grenade = 2 }

       public class WeaponInventory : MonoBehaviour
    {
        public static WeaponInventory Instance { get; private set; }

        public class Entry
        {
            public string Key;         // tur anahtari (dedup + equip icin)
            public WeaponCategory Category;
            public GameObject Preview;  // gorsel-only klon (kemerde gosterilir), pasif baslar
            public GameObject Prefab;   // Resources/WeaponPrefabs'taki kalip; secilince BUNDAN
           
            public int Ammo = -1;    // -1 = kayit yok -> silah dolu dogsun
            public int Spares = -1;  // -1 = dokunma (profilde sinirsiz olabilir)
            public Vector3 BarrelDir = Vector3.forward;
        }
        readonly Entry[] _slots = new Entry[3];
        readonly List<Entry> _view = new List<Entry>();
        bool _viewDirty = true;
        float _nextScan;

        /// <summary>Dolu yuvalar, SABIT kategori sirasiyla (uzun namlulu, tabanca, bomba).</summary>
        public IReadOnlyList<Entry> Entries
        {
            get
            {
                if (_viewDirty)
                {
                    _view.Clear();
                    foreach (var e in _slots) if (e != null) _view.Add(e);
                    _viewDirty = false;
                }
                return _view;
            }
        }

        /// <summary>Silah adindan kategori: "Pistol" gecen tabanca, "Grenade" gecen bomba,
        /// GERISI uzun namlulu (varsayilan). Ekip karari — bkz. <see cref="WeaponCategory"/>.</summary>
        public static WeaponCategory CategoryOf(string key)
        {
            if (key == null) return WeaponCategory.Heavy;
            if (key.IndexOf("Pistol", System.StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Pistol;
            if (key.IndexOf("Grenade", System.StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Grenade;
            return WeaponCategory.Heavy;
        }

        /// <summary>Yeni silah eklenince tetiklenir.</summary>
        public event System.Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponInventory");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponInventory>();
        }

        void Awake() => Instance = this;

        void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 0.3f;

            var nm = NetworkManager.Singleton;
            if (nm == null || !(nm.IsServer || nm.IsConnectedClient)) return;
            ulong me = nm.LocalClientId;

            var actives = GrabbableObject.Active; // spawn kayit listesi — sahne taramasi + dizi alloc'u yok
            for (int gi = 0; gi < actives.Count; gi++)
            {
                var g = actives[gi];
                if (g.HolderClientId != me) continue; // sadece SU AN benim tuttugum silahlar

                // PIMI CEKILMIS BOMBAYI EKLEME. Armed bomba "kullanimda" — atilacak, saklanacak
                // esya degil. PullPin cantadan zaten cikardi; bu tarama 0.3 sn'de bir kosuyor ve
                // armed bombayi elde tutarken GERI EKLERDI, atinca da listede kalirdi (kullanicinin
                // gordugu "atilan bomba envanterde duruyor" sorunu). Armed'i atlayinca kayit gitmis
                // kalir.
                var gc = g.GetComponent<GrenadeController>();
                if (gc != null && gc.Armed) continue;

                string key = TypeKey(g);
                int slot = (int)CategoryOf(key);
                var cur = _slots[slot];

                if (cur == null || cur.Key != key)
                {
                    // Yuva bos -> yeni silah. Yuva doluysa AYNI KATEGORIDEN farkli bir silah
                    // alindi demektir: eskisi cantadan cikar (onizlemesiyle birlikte), yenisi
                    // yuvaya oturur. Her kategoriden EN FAZLA BIR silah — ekip karari.
                    if (cur != null && cur.Preview != null) Destroy(cur.Preview);
                    var grip = g.GetComponent<WeaponGrip>();
                    _slots[slot] = new Entry
                    {
                        Key = key,
                        Category = (WeaponCategory)slot,
                        Preview = BuildPreview(g, transform),
                        Prefab = FindPrefabFor(key),
                        BarrelDir = grip != null && grip.Profile != null && grip.Profile.barrelLocalDirection.sqrMagnitude > 0.01f
                            ? grip.Profile.barrelLocalDirection.normalized
                            : Vector3.forward,
                    };
                    _viewDirty = true;
                    Debug.Log($"[WeaponInventory] {(WeaponCategory)slot} yuvasi: {key}" +
                              (cur != null ? $"  (eski: {cur.Key} cikti)" : "  (yeni)"));
                    Changed?.Invoke();
                }

                // Elde tuttugum silahin mermisini SUREKLI not al. Silah yok oldugu anda (birakinca
                // ya da takasta) son bilinen deger cantada kalir; geri cagirinca ayni mermiyle gelir.
                var nw = g.GetComponent<NetworkWeapon>();
                if (nw != null && nw.UsesAmmo)
                {
                    var e = _slots[slot];
                    if (e != null) { e.Ammo = nw.Ammo; e.Spares = nw.SpareMagazines; }
                }
            }
        }

        /// <summary>Bu turun canta kaydi (yoksa null).</summary>
        public Entry Find(string key)
        {
            foreach (var e in _slots) if (e != null && e.Key == key) return e;
            return null;
        }

        /// <summary>Kategorinin yuvasindaki silah (bos yuva = null). Kemer her halkayi buradan okur.</summary>
        public Entry Slot(WeaponCategory c) => _slots[(int)c];

        /// <summary>Bu turu cantadan CIKAR. El bombasi TUKETILEBILIR: atilinca (kullanilinca)
        /// cantadaki kaydi silinir, kemer bir daha o bombayi sunmaz. Yeni bomba raftan alinir.
        /// Silahlar icin cagrilmaz — onlar tekrar kullanilabilir, cantada kalir.</summary>
        public void RemoveType(string key)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var e = _slots[i];
                if (e == null || e.Key != key) continue;
                if (e.Preview != null) Destroy(e.Preview);
                _slots[i] = null;
                _viewDirty = true;
                Debug.Log($"[WeaponInventory] {(WeaponCategory)i} yuvasi bosaldi: {key} (kullanildi)");
                Changed?.Invoke();
                return;
            }
        }

        // Bu turden yeni bir tane uretecek kalibi bul: Resources/WeaponPrefabs'taki her prefabin
        // tutuş profili isim eslesmesiyle bulunur (WeaponGripBinder ile AYNI kural), profili bu
        // turun anahtariyla ayni olan prefab bizim kalibimizdir. Boylece silaha ozel kod yok:
        // klasore yeni silah konunca burasi da kendiliginden calisir.
        // PUBLIC: sonsuz raf (WeaponRackRespawner) da ayni haritalamayi kullanir.
        public static GameObject FindPrefabFor(string key)
        {
            var prefabs = WeaponPrefabRegistrar.Prefabs;
            if (prefabs == null) return null;
            foreach (var p in prefabs)
            {
                if (p == null) continue;
                var prof = WeaponGripBinder.FindProfile(p.name);
                if (prof != null && prof.name == key) return p;
            }
            return null;
        }

        // Silahin "tur"u: varsa tutuş profili adi (her tur tek profil), yoksa obje adinin
        // klon/kopya gurultusu temizlenmis hali. Ayni tur iki kez alinirsa tek kayit.
        public static string TypeKey(GrabbableObject g)
        {
            var grip = g.GetComponent<WeaponGrip>();
            if (grip != null && grip.Profile != null) return grip.Profile.name;
            string n = g.name.Replace("(Clone)", "").Trim();
            int paren = n.IndexOf(" (");
            return paren > 0 ? n.Substring(0, paren) : n;
        }

        // Silahin SADECE gorsel kopyasi (mesh + materyal). Her mesh, silah kokune GORE
        // yerlestirilir (ic ice olceklere dayanikli), boylece kemerde tek parca gibi durur.
        static GameObject BuildPreview(GrabbableObject src, Transform parent)
        {
            var root = new GameObject("Preview_" + TypeKey(src));
            root.transform.SetParent(parent, false);

            foreach (var mf in src.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;

                var child = new GameObject(mf.name);
                child.transform.SetParent(root.transform, false);
                Matrix4x4 rel = src.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                child.transform.localPosition = rel.GetColumn(3);
                child.transform.localRotation = rel.rotation;
                child.transform.localScale = rel.lossyScale;
                child.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                child.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
            }

            root.SetActive(false); // kemer acilana kadar gizli
            return root;
        }
    }
}
