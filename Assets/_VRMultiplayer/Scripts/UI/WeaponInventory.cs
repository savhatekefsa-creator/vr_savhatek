using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Yerel silah envanteri: yerel oyuncunun bu oturumda TOPLADIGI her farkli silahi, alis
    /// sirasiyla hatirlar. Her silah icin bir GORSEL onizleme kopyasi (mesh+materyal, script/
    /// fizik/ag YOK) uretir — silah secici galerisi (<see cref="WeaponSelectorUI"/>) bunlari
    /// gosterir.
    ///
    /// Tamamen YEREL (kendi goruntun), ag yok. Her ~0.3sn elde tuttugun grabbable'lari tarar,
    /// bu yuzden HandGrabber / GrabbableObject'e HIC dokunmaz (sadece public alanlari okur) ve
    /// kendini otomatik olusturur.
    /// </summary>
    public class WeaponInventory : MonoBehaviour
    {
        public static WeaponInventory Instance { get; private set; }

        public class Entry
        {
            public string Key;         // tur anahtari (dedup + equip icin)
            public GameObject Preview;  // gorsel-only klon (galeride gosterilir), pasif baslar
            public GameObject Prefab;   // Resources/WeaponPrefabs'taki kalip; secilince BUNDAN
                                        // yeni bir tane uretilir. null = bu silah spawn edilemez
                                        // (kalibi yok) -> galeride durur ama equip edilemez.

            // Silah cantaya kac mermiyle girdi. Geri cagirinca bu deger yeni silaha yazilir —
            // yoksa taze silah DOLU dogar (NetworkWeapon.OnNetworkSpawn) ve galeriyi acip
            // kapamak savurarak dolumdan hizli bir BEDAVA SARJOR olurdu. Cantanin bir NESNEYI
            // saklamasina gerek yok, bu iki SAYI yetiyor.
            public int Ammo = -1;    // -1 = kayit yok -> silah dolu dogsun
            public int Spares = -1;  // -1 = dokunma (profilde sinirsiz olabilir)
        }

        readonly List<Entry> _entries = new List<Entry>();
        readonly HashSet<string> _seen = new HashSet<string>();
        float _nextScan;

        /// <summary>Toplanan silahlar (anahtar + onizleme), alis sirasiyla.</summary>
        public IReadOnlyList<Entry> Entries => _entries;

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

            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                if (g.HolderClientId != me) continue; // sadece SU AN benim tuttugum silahlar
                string key = TypeKey(g);
                if (_seen.Add(key))
                {
                    _entries.Add(new Entry
                    {
                        Key = key,
                        Preview = BuildPreview(g, transform),
                        Prefab = FindPrefabFor(key),
                    });
                    Debug.Log($"[WeaponInventory] Yeni silah: {key}  (toplam {_entries.Count})");
                    Changed?.Invoke();
                }

                // Elde tuttugum silahin mermisini SUREKLI not al. Silah yok oldugu anda (birakinca
                // ya da takasta) son bilinen deger cantada kalir; geri cagirinca ayni mermiyle gelir.
                var nw = g.GetComponent<NetworkWeapon>();
                if (nw != null && nw.UsesAmmo)
                {
                    var e = Find(key);
                    if (e != null) { e.Ammo = nw.Ammo; e.Spares = nw.SpareMagazines; }
                }
            }
        }

        /// <summary>Bu turun canta kaydi (yoksa null).</summary>
        public Entry Find(string key)
        {
            foreach (var e in _entries) if (e.Key == key) return e;
            return null;
        }

        // Bu turden yeni bir tane uretecek kalibi bul: Resources/WeaponPrefabs'taki her prefabin
        // tutuş profili isim eslesmesiyle bulunur (WeaponGripBinder ile AYNI kural), profili bu
        // turun anahtariyla ayni olan prefab bizim kalibimizdir. Boylece silaha ozel kod yok:
        // klasore yeni silah konunca burasi da kendiliginden calisir.
        static GameObject FindPrefabFor(string key)
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
        // yerlestirilir (ic ice olceklere dayanikli), boylece galeride tek parca gibi durur.
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

            root.SetActive(false); // galeri acilana kadar gizli
            return root;
        }
    }
}
