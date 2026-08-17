using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>Silah kategorisi. Kategori SILAH ADINDAN turetilir (ekip karari: tabanca +
    /// el bombasi + geri kalan her sey): adinda "Pistol" gecen tabanca, "Grenade" gecen bomba,
    /// GERISI uzun namlulu. UzunNamlulu'nun varsayilan olmasi bilincli — yeni eklenen silah
    /// hicbir sey yapilmadan uzun namlulu sayilir.</summary>
    public enum WeaponCategory { Heavy = 0, Pistol = 1, Grenade = 2 }

    /// <summary>
    /// KEMER KILIFI — oyuncunun elleriyle doldurdugu 3 yuva.
    ///
    /// ================== ESKI MODELDEN NE DEGISTI (2026-08-17) ==================
    /// Eskiden bu sinif OTOMATIK bir cantaydi: 0.3 sn'de bir elindeki silahlari tarar,
    /// kategorisine gore SABIT yuvaya yazardi (uzun namlulu -> 1, tabanca -> 2, bomba -> 3).
    /// Silahi birakmak onu "cantaya sokar" (despawn), kemerden cekmek TAZE bir kopya
    /// dogururdu; canta yalnizca TUR ADI + mermi sayilarini tutardi.
    ///
    /// Kullanici bunu kokten degistirdi: "istedigim yuvarlaga istedigimi koyayim, koydugum
    /// sey orada havada asili kalsin, elimi uzatinca alayim."
    ///
    /// Simdiki model:
    ///   - Otomatik tarama YOK. Esya yuvaya ancak SEN koyunca girer (bkz. HandGrabber.Release).
    ///   - Yuva-kategori bagi YOK. Her yuva her seyi kabul eder.
    ///   - Cekmek esyayi yuvadan CIKARIR. Koydugun sey oradaydi, aldin, artik orada degil.
    ///
    /// BU SADELESME BIR HATA SINIFINI TAMAMEN SILIYOR. Eski modelde mermi hafizasi TUR
    /// basinaydi ve ayni turden iki tabanca ayni kayittan beslenirdi; ustelik kemerden cekip
    /// birakmak taze/dolu bir silah uretebildigi icin "bedava sarjor" acigina karsi ayrica
    /// kod gerekiyordu. Artik kemerde duran sey BIR ESYA ve mermisini kendi tasiyor: iki ayni
    /// tabanca dogal olarak iki ayri kayit, ve cekilen silah her zaman KOYULAN silahtir.
    ///
    /// KAPASITE: yuva basina <see cref="SlotCapacity"/>, kategori basina <see cref="CapOf"/>
    /// (1 uzun namlulu, 2 tabanca, 2 bomba). Toplam 5 esya, 3 yuva — yani her zaman yer vardir,
    /// sinir kategoriden gelir.
    ///
    /// Tamamen YEREL (kendi goruntun), ag yok.
    /// </summary>
    public class WeaponInventory : MonoBehaviour
    {
        public static WeaponInventory Instance { get; private set; }

        public const int SlotCount = 3;

        /// <summary>Bir yuvaya en fazla kac esya. 3 yuva x 2 = 6 >= toplam kapasite 5, yani
        /// yuva doldugu icin esya kabul edilemeyen bir durum ancak sen hepsini ayni yuvaya
        /// yiginca olusur.</summary>
        public const int SlotCapacity = 2;

        /// <summary>Kategori basina TOPLAM sinir (hangi yuvada olduklarindan bagimsiz).</summary>
        public static int CapOf(WeaponCategory c)
        {
            switch (c)
            {
                case WeaponCategory.Heavy: return 1;
                case WeaponCategory.Pistol: return 2;
                case WeaponCategory.Grenade: return 2;
                default: return 1;
            }
        }

        /// <summary>Kemerde duran TEK bir esya. Mermi bu kayitta: kemere kac mermiyle girdiyse
        /// o kadarla cikar.</summary>
        public class Item
        {
            public string Key;
            public WeaponCategory Category;
            public GameObject Preview;   // gorsel-only klon (halkada asili duran sey)
            public GameObject Prefab;    // Resources/WeaponPrefabs kalibi; null = spawn edilemez

            /// <summary>Bu esyanin mermisi. -1 = kayit yok (silah dolu dogsun).</summary>
            public int Ammo = -1;
            public int Spares = -1;

            /// <summary>Namlunun silah-LOKAL yonu (profilden). Onizleme bunun gore yan cevrilir.</summary>
            public Vector3 BarrelDir = Vector3.forward;

            // Onizlemenin halkaya sigma olcegi/merkezi — mesh bounds'u degismedigi icin
            // esya basina BIR KEZ olculur.
            public bool FitMeasured;
            public float Fit = 1f;
            public Vector3 FitCenter;
        }

        // Awake'te DEGIL alan baslatmasinda kuruluyor: boylece envantere Awake'ten once
        // dokunan herhangi bir yol (ya da editordeki bir test) null listeye carpmaz.
        static List<Item>[] NewSlots()
        {
            var a = new List<Item>[SlotCount];
            for (int i = 0; i < SlotCount; i++) a[i] = new List<Item>(SlotCapacity);
            return a;
        }

        readonly List<Item>[] _slots = NewSlots();

        /// <summary>Bir yuvadaki esyalar (kokten uca degil, KOYULMA sirasiyla).</summary>
        public IReadOnlyList<Item> Slot(int slot)
            => slot >= 0 && slot < SlotCount ? _slots[slot] : System.Array.Empty<Item>();

        /// <summary>Kemerdeki her sey (onizleme gizleme gibi toplu isler icin).</summary>
        public IEnumerable<Item> AllItems
        {
            get
            {
                for (int s = 0; s < SlotCount; s++)
                    for (int i = 0; i < _slots[s].Count; i++)
                        yield return _slots[s][i];
            }
        }

        /// <summary>Kemer degisince tetiklenir.</summary>
        public event System.Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponInventory");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponInventory>();
        }

        void Awake() => Instance = this;

        void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ sorgular

        public static WeaponCategory CategoryOf(string key)
        {
            if (key == null) return WeaponCategory.Heavy;
            if (key.IndexOf("Pistol", System.StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Pistol;
            if (key.IndexOf("Grenade", System.StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Grenade;
            return WeaponCategory.Heavy;
        }

        /// <summary>Bu kategoriden kemerde kac tane var (tum yuvalar).</summary>
        public int CountOf(WeaponCategory c)
        {
            int n = 0;
            for (int s = 0; s < SlotCount; s++)
                for (int i = 0; i < _slots[s].Count; i++)
                    if (_slots[s][i].Category == c) n++;
            return n;
        }

        /// <summary>Bu kategoriden bir esya SU AN bu yuvaya konabilir mi?
        /// Kemer HUD'u halkayi kirmizi yakmak icin de bunu sorar.</summary>
        public bool CanPlace(WeaponCategory c, int slot)
        {
            if (slot < 0 || slot >= SlotCount) return false;
            if (_slots[slot].Count >= SlotCapacity) return false;
            return CountOf(c) < CapOf(c);
        }

        /// <summary>Elin bu yuvadaki EN YAKIN esyasi (yoksa null). Kullanicinin kurali:
        /// "el hangisine yakinsa onu alacak halkanin icerisinden".</summary>
        public Item NearestIn(int slot, Vector3 handPos)
        {
            var list = _slots[slot];
            Item best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                // Onizleme kapaliyken (kemer kapali) konumu anlamsizdir; o durumda ilk esya.
                if (it.Preview == null) { if (best == null) best = it; continue; }
                float d = Vector3.SqrMagnitude(it.Preview.transform.position - handPos);
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        // ------------------------------------------------------------------ degisiklikler

        /// <summary>
        /// Elindeki silahi bu yuvaya koy. Mermi durumu BURADA yakalanir — silah birazdan
        /// despawn edilecek ve o an kaybolacak tek bilgi bu.
        /// Kapasite dolu / kalip yok ise null doner ve hicbir sey degismez.
        /// </summary>
        public Item Place(GrabbableObject g, int slot)
        {
            if (g == null) return null;
            string key = TypeKey(g);
            var cat = CategoryOf(key);
            if (!CanPlace(cat, slot)) return null;

            var grip = g.GetComponent<WeaponGrip>();
            var nw = g.GetComponent<NetworkWeapon>();
            var it = new Item
            {
                Key = key,
                Category = cat,
                Preview = BuildPreview(g, transform),
                Prefab = FindPrefabFor(key),
                BarrelDir = grip != null && grip.Profile != null &&
                            grip.Profile.barrelLocalDirection.sqrMagnitude > 0.01f
                    ? grip.Profile.barrelLocalDirection.normalized
                    : Vector3.forward,
                Ammo = nw != null && nw.UsesAmmo ? nw.Ammo : -1,
                Spares = nw != null && nw.UsesAmmo ? nw.SpareMagazines : -1,
            };

            _slots[slot].Add(it);
            Debug.Log($"[Kemer] {slot + 1}. yuvaya kondu: {key}" +
                      (it.Ammo >= 0 ? $" ({it.Ammo} mermi)" : "") +
                      $"   [{cat}: {CountOf(cat)}/{CapOf(cat)}]");
            Changed?.Invoke();
            return it;
        }

        /// <summary>Esyayi yuvadan CIKAR (oyuncu aldi). Onizleme yok edilir.</summary>
        public void Take(int slot, Item it)
        {
            if (it == null || slot < 0 || slot >= SlotCount) return;
            if (!_slots[slot].Remove(it)) return;
            if (it.Preview != null) Destroy(it.Preview);
            it.Preview = null;
            Debug.Log($"[Kemer] {slot + 1}. yuvadan alindi: {it.Key}");
            Changed?.Invoke();
        }

        /// <summary>Kemeri tamamen bosaltir (olum / mac sifirlama).</summary>
        public void Clear()
        {
            bool any = false;
            for (int s = 0; s < SlotCount; s++)
            {
                for (int i = 0; i < _slots[s].Count; i++)
                    if (_slots[s][i].Preview != null) Destroy(_slots[s][i].Preview);
                if (_slots[s].Count > 0) any = true;
                _slots[s].Clear();
            }
            if (any) Changed?.Invoke();
        }

        // ------------------------------------------------------------------ yardimcilar

        // Bu turden yeni bir tane uretecek kalibi bul: profili bu turun anahtariyla ayni olan
        // prefab bizim kalibimizdir. Silaha ozel kod yok — klasore yeni silah konunca calisir.
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

        // Silahin "tur"u: varsa tutus profili adi (her tur tek profil), yoksa obje adinin
        // klon/kopya gurultusu temizlenmis hali.
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
            var root = new GameObject("Kemer_" + TypeKey(src));
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
