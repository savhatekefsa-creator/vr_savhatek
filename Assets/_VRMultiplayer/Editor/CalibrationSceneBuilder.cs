using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Kalibrasyon/animasyon istasyonu sahnesi kurulumu. Oyun haritasini (Map, RoomMap,
    /// RoomPlanTemplate'in duvar/zemin cocuklari) siler; yerine siyah grid bir zemin,
    /// kalibre noktalarinin (A -> B yonunde) tam arkasina uzun bir masa ve masanin ustune
    /// tum silah/bomba prefablarini yan yana dizer.
    ///
    /// Duzeni elle ayarlayip (silah tasima/dondurme, masa boyutu, ekleme/cikarma)
    /// "Duzeni Kaydet" ile kalici hale getirebilirsin: sonraki "Kur" cagrilari kayitli
    /// duzeni BIREBIR geri getirir. Kayit yoksa varsayilan otomatik dizilim kurulur.
    /// Kayit dosyasi: <see cref="LayoutPath"/> (silinirse varsayilana doner).
    ///
    /// A/B kalibre isaretleri, XR Rig, NetworkManager, isik ve volume korunur.
    /// Tum islem Undo'ya kayitlidir (Ctrl+Z ile geri alinabilir).
    /// </summary>
    public static class CalibrationSceneBuilder
    {
        const string WeaponFolder   = "Assets/_VRMultiplayer/Resources/WeaponPrefabs";
        const string MaterialFolder = "Assets/_VRMultiplayer/Materials";
        const string LayoutPath     = "Assets/_VRMultiplayer/Editor/KalibrasyonDuzen.json";

        const string FloorName   = "Kalibrasyon Zemini";
        const string TableName   = "Kalibrasyon Masasi";
        const string WeaponsName = "Masa Silahlari";

        const float TableTopY    = 1.0f;   // masa ust yuzeyi (alcak masa istenmedi)
        const float TableTopThk  = 0.06f;  // tabla kalinligi
        const float LegThk       = 0.09f;
        const float MinSlot      = 0.35f;  // bir silahin masada kapladigi minimum genislik
        const float SlotGap      = 0.22f;  // silahlar arasi bosluk
        const float GapBehindB   = 0.6f;   // masanin on kenari B noktasinin ne kadar arkasinda
        const float FloorSize    = 40f;    // grid zemin kenar uzunlugu (metre)

        // ------------------------------------------------------------- kayit modeli

        [System.Serializable]
        class TrData
        {
            public string name;
            public Vector3 pos;      // floor/tableRoot icin dunya, masa parcalari icin yerel
            public Quaternion rot;
            public Vector3 scale;
        }

        [System.Serializable]
        class WeaponData
        {
            public string prefabPath;
            public Vector3 pos;      // dunya
            public Quaternion rot;   // dunya
            public Vector3 scale;    // yerel
        }

        [System.Serializable]
        class LayoutData
        {
            public TrData floor;
            public TrData tableRoot;
            public List<TrData> tableParts = new List<TrData>();
            public List<WeaponData> weapons = new List<WeaponData>();
        }

        // ------------------------------------------------------------------ kurulum

        [MenuItem("Tools/VR Multiplayer/39. Kalibrasyon Sahnesi Kur (harita sil + masa + silahlar)")]
        public static void Build()
        {
            var scene = SceneManager.GetActiveScene();
            var layout = LoadLayout();

            string mode = layout != null
                ? "- KAYITLI duzen bulundu: masa ve silahlar kaydettigin haliyle BIREBIR kurulacak\n"
                : "- Kayitli duzen yok: varsayilan otomatik dizilim kurulacak\n";
            if (!EditorUtility.DisplayDialog("Kalibrasyon Sahnesi",
                "Bu islem aktif sahnede (" + scene.name + "):\n\n" +
                "- Map, RoomMap ve RoomPlanTemplate'in duvar/zemin cocuklarini SILER\n" +
                "  (A/B kalibre isaretleri korunur)\n" +
                mode +
                "\nCtrl+Z ile geri alinabilir. Devam?", "Kur", "Vazgec"))
                return;

            Undo.SetCurrentGroupName("Kalibrasyon Sahnesi Kur");
            int undoGroup = Undo.GetCurrentGroup();

            DeleteMap(scene);

            if (layout != null)
            {
                BuildFromLayout(layout);
            }
            else
            {
                // Kalibre isaretlerinden paylasilan cerceveyi cikar: A = orijin, A->B = ileri.
                // Isaretler bulunamazsa varsayilan cerceve (0,0,0) / +Z kullanilir.
                GetCalibrationFrame(out Vector3 origin, out Vector3 forward);
                CreateDefaultFloor(origin);
                CreateDefaultTableWithWeapons(origin, forward);
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[CalibrationSceneBuilder] Kalibrasyon sahnesi kuruldu (" +
                      (layout != null ? "kayitli duzen" : "varsayilan dizilim") + ").");
        }

        // ------------------------------------------------------------ duzeni kaydet

        [MenuItem("Tools/VR Multiplayer/40. Kalibrasyon Duzenini Kaydet (sonraki Kur birebir kurar)")]
        public static void SaveLayout()
        {
            var floor = GameObject.Find(FloorName);
            var table = GameObject.Find(TableName);
            var weapons = GameObject.Find(WeaponsName);
            if (floor == null || table == null || weapons == null)
            {
                EditorUtility.DisplayDialog("Kalibrasyon Duzeni",
                    "Sahnede '" + FloorName + "', '" + TableName + "' ve '" + WeaponsName +
                    "' bulunamadi.\nOnce 39 ile sahneyi kur.", "Tamam");
                return;
            }

            var data = new LayoutData
            {
                floor = CaptureWorld(floor.transform),
                tableRoot = CaptureWorld(table.transform),
            };
            foreach (Transform part in table.transform)
                data.tableParts.Add(CaptureLocal(part));

            int skipped = 0;
            foreach (Transform w in weapons.transform)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(w.gameObject);
                string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
                if (string.IsNullOrEmpty(path))
                {
                    // Prefab baglantisi olmayan obje yeniden uretilemez; kayda giremez.
                    Debug.LogWarning("[CalibrationSceneBuilder] '" + w.name +
                                     "' prefab baglantisi yok, kayda alinmadi.");
                    skipped++;
                    continue;
                }
                data.weapons.Add(new WeaponData
                {
                    prefabPath = path,
                    pos = w.position,
                    rot = w.rotation,
                    scale = w.localScale,
                });
            }

            File.WriteAllText(LayoutPath, JsonUtility.ToJson(data, true));
            AssetDatabase.ImportAsset(LayoutPath);
            EditorUtility.DisplayDialog("Kalibrasyon Duzeni",
                "Duzen kaydedildi: " + data.weapons.Count + " silah, " +
                data.tableParts.Count + " masa parcasi." +
                (skipped > 0 ? "\n(" + skipped + " obje prefab baglantisi olmadigi icin atlandi.)" : "") +
                "\n\nBundan sonra '39. Kur' bu duzeni birebir kurar.\n" +
                "Varsayilana donmek icin dosyayi sil:\n" + LayoutPath, "Tamam");
        }

        static TrData CaptureWorld(Transform t) => new TrData
        { name = t.name, pos = t.position, rot = t.rotation, scale = t.localScale };

        static TrData CaptureLocal(Transform t) => new TrData
        { name = t.name, pos = t.localPosition, rot = t.localRotation, scale = t.localScale };

        static LayoutData LoadLayout()
        {
            if (!File.Exists(LayoutPath)) return null;
            try
            {
                var data = JsonUtility.FromJson<LayoutData>(File.ReadAllText(LayoutPath));
                return (data != null && data.floor != null && data.tableRoot != null) ? data : null;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CalibrationSceneBuilder] Kayit dosyasi okunamadi (" + LayoutPath +
                               "): " + e.Message + " — varsayilan dizilim kullanilacak.");
                return null;
            }
        }

        // ------------------------------------------------- kayitli duzenden kurulum

        static void BuildFromLayout(LayoutData data)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = FloorName;
            floor.transform.SetPositionAndRotation(data.floor.pos, data.floor.rot);
            floor.transform.localScale = data.floor.scale;
            floor.isStatic = true;
            floor.GetComponent<Renderer>().sharedMaterial = GetOrCreateGridMaterial();
            Undo.RegisterCreatedObjectUndo(floor, "Grid Zemin");

            var tableMat = GetOrCreateTableMaterial();
            var table = new GameObject(TableName);
            table.transform.SetPositionAndRotation(data.tableRoot.pos, data.tableRoot.rot);
            table.transform.localScale = data.tableRoot.scale;
            table.isStatic = true;
            foreach (var part in data.tableParts)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = part.name;
                cube.transform.SetParent(table.transform, false);
                cube.transform.localPosition = part.pos;
                cube.transform.localRotation = part.rot;
                cube.transform.localScale = part.scale;
                cube.GetComponent<Renderer>().sharedMaterial = tableMat;
            }
            Undo.RegisterCreatedObjectUndo(table, "Kalibrasyon Masasi");

            var weaponsRoot = new GameObject(WeaponsName);
            Undo.RegisterCreatedObjectUndo(weaponsRoot, "Masa Silahlari");
            foreach (var w in data.weapons)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(w.prefabPath);
                if (prefab == null)
                {
                    Debug.LogWarning("[CalibrationSceneBuilder] Prefab bulunamadi, atlandi: " +
                                     w.prefabPath);
                    continue;
                }
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                Undo.RegisterCreatedObjectUndo(inst, "Silah");
                inst.transform.SetParent(weaponsRoot.transform, false);
                inst.transform.SetPositionAndRotation(w.pos, w.rot);
                inst.transform.localScale = w.scale;
            }
        }

        // ---------------------------------------------------------------- harita silme

        static void DeleteMap(Scene scene)
        {
            var roots = scene.GetRootGameObjects();

            bool lightKept = false;
            foreach (var root in roots)
            {
                if (root == null) continue;
                string n = root.name;

                // Onceki kurulumun urettikleri: yeniden calistirmada temizlenir (idempotent).
                if (n == FloorName || n == TableName || n == WeaponsName)
                {
                    Undo.DestroyObjectImmediate(root);
                    continue;
                }

                if (n == "Map" || n == "RoomMap")
                {
                    Undo.DestroyObjectImmediate(root);
                    continue;
                }

                // Sahnede iki kok Directional Light var; biri yeterli.
                if (n == "Directional Light")
                {
                    if (lightKept) Undo.DestroyObjectImmediate(root);
                    else lightKept = true;
                    continue;
                }

                // Sablonun altinda yalnizca A/B kalibre isaretleri kalsin; duvar sablonu,
                // zemin sablonu ve sinir cizgisi sade sahnede gereksiz.
                if (n == "RoomPlanTemplate")
                {
                    var doomed = new List<GameObject>();
                    foreach (Transform child in root.transform)
                        if (!child.name.StartsWith("A noktasi") && !child.name.StartsWith("B yonu"))
                            doomed.Add(child.gameObject);
                    foreach (var go in doomed)
                        Undo.DestroyObjectImmediate(go);
                }
            }
        }

        // ------------------------------------------------------- kalibre cercevesi

        static void GetCalibrationFrame(out Vector3 origin, out Vector3 forward)
        {
            origin = Vector3.zero;
            forward = Vector3.forward;

            var a = FindByPrefix("A noktasi");
            var b = FindByPrefix("B yonu");
            if (a == null || b == null) return;

            origin = a.position; origin.y = 0f;
            Vector3 dir = b.position - a.position; dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) forward = dir.normalized;
        }

        static Transform FindByPrefix(string prefix)
        {
            foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None))
                if (go.name.StartsWith(prefix)) return go;
            return null;
        }

        // ------------------------------------------------------ varsayilan grid zemin

        static void CreateDefaultFloor(Vector3 origin)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = FloorName;
            floor.transform.position = origin;                       // plane pivotu ust yuzeyde
            floor.transform.localScale = Vector3.one * (FloorSize / 10f); // plane 10x10 birim
            floor.isStatic = true;
            floor.GetComponent<Renderer>().sharedMaterial = GetOrCreateGridMaterial();
            Undo.RegisterCreatedObjectUndo(floor, "Grid Zemin");
        }

        static Material GetOrCreateGridMaterial()
        {
            string matPath = MaterialFolder + "/KalibrasyonGrid.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null) return existing;

            var tex = GetOrCreateGridTexture();

            // Grid isiktan etkilenmesin diye Unlit; URP yoksa built-in'e duser.
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", tex);
                mat.SetTextureScale("_BaseMap", Vector2.one * FloorSize); // 1 metre = 1 hucre
            }
            else
            {
                mat.mainTexture = tex;
                mat.mainTextureScale = Vector2.one * FloorSize;
            }
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }

        static Texture2D GetOrCreateGridTexture()
        {
            string texPath = MaterialFolder + "/KalibrasyonGrid.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (existing != null) return existing;

            // Tek hucre: siyah dolgu, kenarlarda gri cizgi. Tekrarlanarak grid olur.
            const int size = 128, line = 4;
            var fill = new Color32(8, 8, 8, 255);
            var grid = new Color32(70, 70, 70, 255);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = (x < line || y < line) ? grid : fill;
            tex.SetPixels32(px);
            tex.Apply();

            File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath);

            var imp = (TextureImporter)AssetImporter.GetAtPath(texPath);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 8;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        }

        // -------------------------------------------- varsayilan masa + silah dizilimi

        static void CreateDefaultTableWithWeapons(Vector3 origin, Vector3 forward)
        {
            // Silahlari once olustur ve olc: masa uzunlugu/derinligi icerige gore belirlenir.
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { WeaponFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => Path.GetFileNameWithoutExtension(p))
                .ToList();
            if (prefabs.Count == 0)
                Debug.LogWarning("[CalibrationSceneBuilder] " + WeaponFolder + " altinda prefab bulunamadi.");

            Quaternion tableRot = Quaternion.LookRotation(forward);

            var weaponsRoot = new GameObject(WeaponsName);
            weaponsRoot.transform.SetPositionAndRotation(origin, tableRot);
            Undo.RegisterCreatedObjectUndo(weaponsRoot, "Masa Silahlari");

            var items = new List<(GameObject go, Bounds b)>();
            float maxDepth = 0.6f;
            foreach (var path in prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                Undo.RegisterCreatedObjectUndo(inst, "Silah");
                inst.transform.SetParent(weaponsRoot.transform, false);

                // Her prefabin modeli kendi uzayinda farkli eksende durur (cogu -Z, HK416 -X,
                // Paintball +X...). Grip profilindeki barrelLocalDirection ile namlu masa ileri
                // yonune hizalanir; +Y yukarida kaldigi icin kabzalar da ayni rotaya gelir.
                inst.transform.SetPositionAndRotation(origin, tableRot * BarrelAlign(inst));

                var b = RendererBounds(inst);
                items.Add((inst, b));
                maxDepth = Mathf.Max(maxDepth, b.size.z);
            }

            // Slot genislikleri: her silahin X kaplamasi + bosluk, alt sinir MinSlot.
            var slots = items.Select(it => Mathf.Max(MinSlot, it.b.size.x + SlotGap)).ToList();
            float span = slots.Sum();
            float tableLen   = Mathf.Max(2.5f, span + 0.6f);
            float tableDepth = maxDepth + 0.3f;

            // B noktasindan GapBehindB kadar geride baslar (A -> B yonunde "arka").
            float distAB = 1f;
            var aT = FindByPrefix("A noktasi"); var bT = FindByPrefix("B yonu");
            if (aT != null && bT != null) distAB = Vector3.Distance(aT.position, bT.position);
            Vector3 tableCenter = origin + forward * (distAB + GapBehindB + tableDepth * 0.5f);

            var table = BuildTable(tableCenter, tableRot, tableLen, tableDepth);
            Undo.RegisterCreatedObjectUndo(table, "Kalibrasyon Masasi");

            // Silahlari masa ustune yan yana diz (masanin yerel X ekseni boyunca).
            float cursor = -span * 0.5f;
            for (int i = 0; i < items.Count; i++)
            {
                var (go, b) = items[i];
                float slotCenter = cursor + slots[i] * 0.5f;
                cursor += slots[i];

                Vector3 target = tableCenter
                               + tableRot * new Vector3(slotCenter, 0f, 0f);
                target.y = TableTopY + 0.01f;

                // Bounds merkezini slota tasi; alt kenari masa yuzeyine oturt.
                Vector3 offset = go.transform.position - b.center;
                Vector3 pos = target + offset;
                pos.y = target.y + (go.transform.position.y - b.min.y);
                go.transform.position = pos;
            }
        }

        static GameObject BuildTable(Vector3 center, Quaternion rot, float length, float depth)
        {
            var root = new GameObject(TableName);
            root.transform.SetPositionAndRotation(new Vector3(center.x, 0f, center.z), rot);
            root.isStatic = true;

            var mat = GetOrCreateTableMaterial();

            var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
            top.name = "Tabla";
            top.transform.SetParent(root.transform, false);
            top.transform.localPosition = new Vector3(0f, TableTopY - TableTopThk * 0.5f, 0f);
            top.transform.localScale = new Vector3(length, TableTopThk, depth);
            top.GetComponent<Renderer>().sharedMaterial = mat;

            float legH = TableTopY - TableTopThk;
            float lx = length * 0.5f - LegThk;
            float lz = depth * 0.5f - LegThk;
            for (int i = 0; i < 4; i++)
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leg.name = "Ayak " + (i + 1);
                leg.transform.SetParent(root.transform, false);
                leg.transform.localPosition = new Vector3((i % 2 == 0 ? -lx : lx), legH * 0.5f,
                                                          (i < 2 ? -lz : lz));
                leg.transform.localScale = new Vector3(LegThk, legH, LegThk);
                leg.GetComponent<Renderer>().sharedMaterial = mat;
            }
            return root;
        }

        static Material GetOrCreateTableMaterial()
        {
            string matPath = MaterialFolder + "/KalibrasyonMasa.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);
            var dark = new Color(0.16f, 0.16f, 0.17f, 1f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", dark);
            else mat.color = dark;
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }

        /// <summary>Silahin yerel namlu yonunu +Z'ye tasiyan duzeltme rotasyonu. Namlu yonu
        /// oyunun kendi grip profilinden okunur (WeaponGripBinder ile ayni eslesme kurali);
        /// profili olmayan silah +Z varsayar ve oldugu gibi kalir.</summary>
        static Quaternion BarrelAlign(GameObject weapon)
        {
            Vector3 barrel = Vector3.forward;
            var profile = WeaponGripBinder.FindProfile(weapon.name);
            if (profile != null && profile.barrelLocalDirection.sqrMagnitude > 0.0001f)
                barrel = profile.barrelLocalDirection.normalized;

            // Dikey namlu (teorik) LookRotation'i bozar; o durumda yardimci eksen degistirilir.
            Vector3 up = Mathf.Abs(Vector3.Dot(barrel, Vector3.up)) > 0.99f ? Vector3.back : Vector3.up;
            return Quaternion.Inverse(Quaternion.LookRotation(barrel, up));
        }

        static Bounds RendererBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>(false)
                          .Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.2f);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }
    }
}
