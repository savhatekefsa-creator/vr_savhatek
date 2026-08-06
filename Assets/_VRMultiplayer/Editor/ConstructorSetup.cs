using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Constructor (calisma zamani harita editoru) veri katmani araclari:
    ///   25. Prop Kutuphanesi Tara — klasorlerdeki prefablardan PropLibrary uretir/gunceller
    ///   26. Izgara Raporu         — RoomPlan.json'dan izgarayi kurar, istatistik + gorsel
    ///   27. Test Haritasi Uret    — deterministik ornek yerlesim, Maps/Test.json
    ///   28. Haritayi Sahneye Kur  — kayitli bir haritayi MapBuilder ile sahneye kurar
    ///   29. Constructor Oz-Denetim — izgara/JSON matematiginin dogrulugunu sinar
    ///
    /// 29 neden birim testi degil: projede hic .asmdef yok, tum kod Assembly-CSharp'ta. Unity
    /// Test Framework'un test derlemesi bir asmdef ister ve asmdef Assembly-CSharp'a referans
    /// VEREMEZ — yani gercek birim testi yazmak once tum projeyi asmdef'lere bolmeyi gerektirir.
    /// O yapisal degisiklige girmek yerine ayni iddialar bir menu altinda kosuyor.
    /// </summary>
    /// <remarks>
    /// Her menu INCE bir kabuk: is yapan <c>RunX()</c> metodu bir rapor metni dondurur, diyalogu
    /// yalnizca menu gosterir. Boylece ayni islem otomasyondan (MCP / baska bir editor araci)
    /// modal diyalog acmadan cagirilabilir — acilan diyalog Unity'yi tikliyana kadar kilitler.
    /// </remarks>
    public static class ConstructorSetup
    {
        const string PlayerPrefabPath = "Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab";
        const string LibraryPath = "Assets/_VRMultiplayer/Resources/PropLibrary.asset";
        const string PlanPath = "Assets/_VRMultiplayer/RoomPlans/RoomPlan.json";
        const string GridVizName = "ConstructorGridViz";
        const string TestMapName = "Test";

        const string GeneratedPropFolder = "Assets/_VRMultiplayer/Resources/ConstructorProps";

        /// <summary>Olculen boyut icin taban (m): sifir genislik ayak izini bozar.</summary>
        const float MinSizeMetres = 0.05f;

        /// <summary>Dogus halkasinin izgarada tuttugu kare kenari (m).</summary>
        const float SpawnFootprintMetres = 1f;

        /// <summary>Test haritasina konabilecek en buyuk prop, eksen basina (m).</summary>
        const float TestMapMaxPropMetres = 1f;

        static readonly string[] DefaultSourceFolders =
        {
            "Assets/P A I N T B A L L S E R I ES MK/Content/Prefabs",
            "Assets/Standout7",
            GeneratedPropFolder,
        };

        // ------------------------------------------------------------- menu 23

        [MenuItem("Tools/VR Multiplayer/23. Constructor Ag Bilesenini Kur")]
        public static void SetupNetworkingMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", SetupNetworking(), "Tamam");

        /// <summary>
        /// Adds <c>ConstructorSync</c> to the player prefab — the same wiring menu 11 does for
        /// the room scanner, and for the same reason: the player object is the only thing that
        /// exists per-connection on both sides, so it is where per-player RPCs belong.
        /// </summary>
        public static string SetupNetworking()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
                return "NetworkPlayer.prefab bulunamadi:\n" + PlayerPrefabPath +
                       "\n\nOnce VR Multiplayer '1' adimini calistir.";

            // ONEMLI: prefab zaten kuruluysa bile DEVAM et. Erken donmek, passthrough
            // ozelliginin hic acilmamasina yol aciyordu — kurulumun ikinci yarisi
            // birincisinin durumuna bakmamali.
            string netResult;
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                if (root.GetComponent<ConstructorSync>() != null)
                {
                    netResult = "Ag: ConstructorSync zaten ekli.";
                }
                else
                {
                    root.AddComponent<ConstructorSync>();
                    PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                    netResult = "Ag: ConstructorSync NetworkPlayer prefabina eklendi.";
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            string passResult = EnablePassthroughFeature();
            AssetDatabase.Refresh();

            return netResult + "\n" + passResult +
                   "\n\nInsa modu ag uzerinden calisir:\n" +
                   " - Gozluk yerlestirir -> PC dogrular -> herkes gorur\n" +
                   " - Sonradan katilan gozluge harita otomatik yollanir\n\n" +
                   "Gozluklere YENIDEN build almayi unutma.";
        }

        /// <summary>
        /// Turns on Unity's own passthrough feature ("Meta Quest: Camera (Passthrough)") for
        /// Android and puts an <c>ARCameraManager</c> on the rig camera.
        ///
        /// Both halves are needed and neither is optional: the OpenXR feature supplies the
        /// camera subsystem AND makes the build generate the required
        /// <c>com.oculus.feature.PASSTHROUGH</c> manifest entry, while the manager is what the
        /// runtime code enables to actually show the room.
        /// </summary>
        static string EnablePassthroughFeature()
        {
            var lines = new List<string>();

            var settings = UnityEngine.XR.OpenXR.OpenXRSettings.GetSettingsForBuildTargetGroup(
                BuildTargetGroup.Android);
            if (settings == null)
            {
                lines.Add("UYARI: Android OpenXR ayarlari bulunamadi.");
            }
            else
            {
                bool found = false;
                foreach (var f in settings.GetFeatures<UnityEngine.XR.OpenXR.Features.OpenXRFeature>())
                {
                    if (f.GetType().Name != "ARCameraFeature") continue;
                    found = true;
                    lines.Add(f.enabled
                        ? "Passthrough: 'Meta Quest: Camera' ozelligi ZATEN acikti."
                        : "Passthrough: 'Meta Quest: Camera' ozelligi ACILDI (Android).");
                    f.enabled = true;
                    EditorUtility.SetDirty(f);
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                    break;
                }
                if (!found) lines.Add("UYARI: 'Meta Quest: Camera (Passthrough)' ozelligi listede yok.");
            }

            // Sahne tarafi: rig kamerasinda ARCameraManager olmali.
            var rig = Object.FindFirstObjectByType<XRRigReference>();
            if (rig == null || rig.head == null)
            {
                lines.Add("UYARI: Sahnede XR Rig/kafa yok — ARCameraManager eklenemedi (menu 2).");
            }
            else if (rig.head.GetComponent<UnityEngine.XR.ARFoundation.ARCameraManager>() == null)
            {
                rig.head.gameObject.AddComponent<UnityEngine.XR.ARFoundation.ARCameraManager>();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
                lines.Add("ARCameraManager kameraya eklendi — SAHNEYI KAYDET (Ctrl+S).");
            }
            else
            {
                lines.Add("ARCameraManager kamerada zaten var.");
            }

            return string.Join("\n", lines);
        }

        // ------------------------------------------------------------- menu 25

        [MenuItem("Tools/VR Multiplayer/25. Prop Kutuphanesi Tara")]
        public static void ScanPropLibraryMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", ScanPropLibrary(), "Tamam");

        public static string ScanPropLibrary()
        {
            var lib = AssetDatabase.LoadAssetAtPath<PropLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<PropLibrary>();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LibraryPath)));
                AssetDatabase.CreateAsset(lib, LibraryPath);
            }

            if (lib.sourceFolders == null || lib.sourceFolders.Length == 0)
                lib.sourceFolders = (string[])DefaultSourceFolders.Clone();

            // Dogus bolgeleri sanat paketlerinden gelmez, kodla uretilir — taramadan ONCE var
            // olmalari gerekiyor ki ayni geciste kutuphaneye girsinler.
            EnsureSpawnPrefabs();
            if (System.Array.IndexOf(lib.sourceFolders, GeneratedPropFolder) < 0)
            {
                var folders2 = new List<string>(lib.sourceFolders) { GeneratedPropFolder };
                lib.sourceFolders = folders2.ToArray();
            }

            var folders = new List<string>();
            foreach (var f in lib.sourceFolders)
                if (AssetDatabase.IsValidFolder(f)) folders.Add(f);
                else Debug.LogWarning("[Constructor] Klasor yok, atlandi: " + f);

            if (folders.Count == 0)
                return "Taranacak gecerli klasor yok.\n\nPropLibrary.asset > sourceFolders alanini doldur.";

            // Elle duzenlenen alanlari KORU: kullanici bir propun kategorisini veya ayak izini
            // duzelttiyse yeniden tarama onu ezmemeli. Sadece prefab/ad/yukseklik tazelenir.
            var existing = new Dictionary<string, PropDef>();
            if (lib.props != null)
                foreach (var p in lib.props)
                    if (p != null && !string.IsNullOrEmpty(p.id)) existing[p.id] = p;

            var found = new List<PropDef>();
            var ids = new HashSet<string>();
            int kept = 0, added = 0, migrated = 0;

            // SEMA GOCU. Boyutlar eskiden HUCRE cinsinden saklaniyordu, artik METRE. Eski
            // kayittaki sayilar yeni alanda anlamsiz (ve alan adi degistigi icin zaten
            // yuklenmediler), o yuzden bu geciste elle duzeltilmis boyutlari koruma kuralini
            // bir kereligine devre disi birakip HEPSINI prefabtan yeniden olcuyoruz.
            bool migrateSizes = lib.schemaVersion < PropLibrary.SizeInMetresSchema;

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", folders.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                if (prefab.GetComponentInChildren<Renderer>() == null) continue; // gorselsiz yardimci prefab

                string id = MakeId(prefab.name);
                if (!ids.Add(id)) continue; // ayni isimde ikinci prefab — ilki kazanir

                Vector2 size = MeasureSize(path, out float height);

                // Dogus bolgesinin GORSEL diski 2.4 m capinda, ama izgara ayak izi kucuk
                // kalmali: olculen haliyle kucuk bir odada tek basina yarim odayi kapatirdi.
                // Halkanin siperlere tasmasi sorun degil — onemli olan oyuncunun icinde
                // durabilmesi, cevresinin bos olmasi degil.
                bool isSpawn = prefab.GetComponent<TeamSpawnZone>() != null;
                if (isSpawn) size = new Vector2(SpawnFootprintMetres, SpawnFootprintMetres);

                if (existing.TryGetValue(id, out var def))
                {
                    def.prefab = prefab;
                    def.displayName = prefab.name;
                    def.height = height;
                    if (migrateSizes) { def.sizeMeters = size; migrated++; }
                    // Dogus halkasinda fit ZORLA kapali — elle duzeltilmis alanlari koruma
                    // kuralinin disinda, cunku bu bir zevk meselesi degil: ayak izini yukarida
                    // KASTEN mesh'ten kucuk yaptik, fit acik kalsa halka o kucuk kareye
                    // buzulurdu.
                    if (isSpawn) def.fitToFootprint = false;
                    kept++;
                }
                else
                {
                    def = new PropDef
                    {
                        id = id,
                        displayName = prefab.name,
                        prefab = prefab,
                        category = isSpawn ? PropCategory.Spawn : GuessCategory(prefab.name),
                        snap = PropSnap.Floor,
                        sizeMeters = size,
                        height = height,
                        fitToFootprint = !isSpawn,
                    };
                    added++;
                }
                found.Add(def);
            }

            // KIMLIGE GORE SIRALA. Ag mesajlari kutuphane INDEKSINI tasiyor; siralama sabit
            // olmazsa AssetDatabase'in dosya sirasi makineden makineye degisip iki istemcinin
            // ayni indeksten farkli prop anlamasina yol acar.
            found.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

            bool orderChanged = lib.props == null || lib.props.Length != found.Count;
            if (!orderChanged)
                for (int i = 0; i < found.Count; i++)
                    if (lib.props[i] == null || lib.props[i].id != found[i].id) { orderChanged = true; break; }

            lib.props = found.ToArray();
            if (orderChanged) lib.contentVersion++;
            lib.schemaVersion = PropLibrary.SizeInMetresSchema;
            lib.InvalidateIndex();

            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var problems = lib.Validate();
            var sb = new StringBuilder();
            sb.AppendLine($"Kutuphane guncellendi: {found.Count} prop ({added} yeni, {kept} korunmus).");
            sb.AppendLine($"Icerik surumu: {lib.contentVersion}" + (orderChanged ? "  (sira degisti, artirildi)" : ""));
            if (migrated > 0)
                sb.AppendLine($"Sema gocu: {migrated} propun boyutu metreye cevrildi " +
                              $"(prefabtan yeniden olculdu). Elle duzelttigin boyutlar varsa " +
                              $"bir kereligine sifirlandi — tekrar gozden gecir.");
            // Fit'in GORUNUR sekilde kalinlastirdigi proplari say. Sorun degil — dik eklemlerin
            // bitisik olmasinin bedeli bu — ama sessiz de olmamali: bir prop hucreden inceyse
            // fit onu bir hucreye getirir ve model gozle fark edilir sekilde sismanlar.
            var thickened = new List<string>();
            foreach (var p in lib.props)
            {
                if (p == null || !p.fitToFootprint) continue;
                if (p.Resolve() == null) continue;
                Vector3 mesh = p.MeshLocalSize;
                if (mesh.x < 0.0001f || mesh.z < 0.0001f) continue;

                var scale = MapBuilder.LocalScaleFor(p, p.Resolve(), RoomGrid.DefaultCellSize, 100, 100);
                float before = mesh.z * Mathf.Abs(p.Resolve().transform.localScale.z);
                float after = mesh.z * Mathf.Abs(scale.z);
                if (before > 0.0001f && after / before > 1.25f)
                    thickened.Add($"{p.id}: {before * 100f:0.0} -> {after * 100f:0.0} cm");
            }

            sb.AppendLine();
            sb.AppendLine(problems.Count == 0 ? "Dogrulama: sorun yok."
                                              : "Dogrulama sorunlari:\n - " + string.Join("\n - ", problems));
            if (thickened.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Fit kalinligi buyutuyor ({thickened.Count} prop). Bir hucreden " +
                              "ince proplar dik eklemde bosluk birakmasin diye hucreyi doldurur; " +
                              "gorunusu begenmezsen o propta fitToFootprint'i kapat:");
                sb.AppendLine(" - " + string.Join("\n - ", thickened));
            }

            Debug.Log("[Constructor] " + sb);
            Selection.activeObject = lib;
            return sb.ToString();
        }

        /// <summary>Where the shared library lives — the manager window edits the same asset.</summary>
        public static string LibraryAssetPath => LibraryPath;

        /// <summary>Prefab name to the stable id saved maps address props by.</summary>
        public static string IdFor(string prefabName) => MakeId(prefabName);

        /// <summary>Category guessed from a prefab's name, same rule the scan uses.</summary>
        public static PropCategory GuessCategoryFor(string prefabName) => GuessCategory(prefabName);

        /// <summary>Ground footprint in metres, measured from the prefab's renderers.</summary>
        public static Vector2 MeasureFootprint(string prefabPath, out float height) =>
            MeasureSize(prefabPath, out height);

        static string MakeId(string prefabName)
        {
            var sb = new StringBuilder(prefabName.Length);
            foreach (char c in prefabName.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString().Trim('_');
        }

        /// <summary>
        /// Generates the two team spawn markers as prefabs.
        ///
        /// They are code-made rather than imported because there is no art to import: a spawn
        /// zone is a component plus a disc. Making them placeable is what lets a player decide
        /// where each team respawns while building the arena — otherwise the map is finished
        /// but everyone still spawns wherever the scene happened to put them.
        /// </summary>
        static void EnsureSpawnPrefabs()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_VRMultiplayer/Resources"))
                AssetDatabase.CreateFolder("Assets/_VRMultiplayer", "Resources");
            if (!AssetDatabase.IsValidFolder(GeneratedPropFolder))
                AssetDatabase.CreateFolder("Assets/_VRMultiplayer/Resources", "ConstructorProps");

            MakeSpawnPrefab("Spawn_A", 1, PlayerIdentity.TeamAColor);
            MakeSpawnPrefab("Spawn_B", 2, PlayerIdentity.TeamBColor);
        }

        static void MakeSpawnPrefab(string name, byte team, Color color)
        {
            string path = GeneratedPropFolder + "/" + name + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;   // bir kez uretilir

            var root = new GameObject(name);
            try
            {
                var zone = root.AddComponent<TeamSpawnZone>();
                zone.team = team;
                zone.fromConstructor = true;   // sahnedeki sabit bolgenin onune gecsin

                // Gorunur disk: TeamSpawnZone'un halkasi YALNIZCA Play modunda ciziliyor, ama
                // hayalet onizlemesi ve editorde gorunurluk icin bir mesh sart.
                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = "Marker";
                Object.DestroyImmediate(disc.GetComponent<Collider>());   // mermi durdurmasin
                disc.transform.SetParent(root.transform, false);
                disc.transform.localScale = new Vector3(zone.radius * 2f, 0.01f, zone.radius * 2f);
                disc.GetComponent<MeshRenderer>().sharedMaterial =
                    EnsureMat("Constructor_" + name, new Color(color.r, color.g, color.b, 0.45f));

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static Material EnsureMat(string name, Color color)
        {
            const string dir = "Assets/_VRMultiplayer/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/_VRMultiplayer", "Materials");

            string path = dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = UI.UITheme.CreateTransparentMaterial(color);
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        static PropCategory GuessCategory(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("tree") || n.Contains("bush") || n.Contains("grass") ||
                n.Contains("flower") || n.Contains("mushroom")) return PropCategory.Nature;
            if (n.Contains("ground") || n.Contains("hill") || n.Contains("floor")) return PropCategory.Ground;
            if (n.Contains("modular") || n.Contains("wall") || n.Contains("fence")) return PropCategory.Wall;
            if (n.Contains("spawn")) return PropCategory.Spawn;
            if (n.Contains("target")) return PropCategory.Target;
            return PropCategory.Cover;   // barrier / bunker / rock / geri kalan her sey
        }

        /// <summary>
        /// Ground area the prefab covers in METRES (see <see cref="PropDef.sizeMeters"/>), from
        /// its render bounds.
        ///
        /// Olcum icin LoadPrefabContents kullaniliyor (InstantiatePrefab DEGIL): ikincisi ornegi
        /// ACIK SAHNEYE kurar ve hemen silinse bile sahneyi kirli isaretler — bir tarama, 40
        /// prefab yuzunden kullanicinin sahnesini "kaydedilmemis" yapardi. LoadPrefabContents
        /// izole bir onizleme sahnesinde calisir, acik sahneye hic dokunmaz.
        /// </summary>
        static Vector2 MeasureSize(string prefabPath, out float height)
        {
            height = 1f;
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null) return Vector2.one;

                var renderers = root.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) return Vector2.one;

                var b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

                height = Mathf.Max(0.05f, b.size.y);
                return new Vector2(
                    Mathf.Max(MinSizeMetres, b.size.x),
                    Mathf.Max(MinSizeMetres, b.size.z));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Constructor] '{prefabPath}' olculemedi: {e.Message}");
                return Vector2.one;
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ------------------------------------------------------------- menu 26

        [MenuItem("Tools/VR Multiplayer/26. Izgara Raporu")]
        public static void GridReportMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", GridReport(), "Tamam");

        public static string GridReport()
        {
            var grid = LoadGrid(out string error);
            if (grid == null) return error;

            var s = grid.Report();
            string msg =
                $"Izgara: {grid.Cols} x {grid.Rows} hucre  ({grid.CellSize:0.00} m)\n" +
                $"Koken: ({grid.Origin.x:0.00}, {grid.Origin.y:0.00})   zemin Y: {grid.FloorY:0.00}\n\n" +
                $"Oda ici (yurunebilir) : {s.free}\n" +
                $"Oda disi (insa edilir): {s.freeOutside}\n" +
                $"Mobilya kapali        : {s.blocked}\n" +
                $"Dolu                  : {s.occupied}\n" +
                $"Toplam hucre          : {s.total}\n\n" +
                $"Oda alani  : {s.roomArea:0.0} m2\n" +
                $"Insa alani : {s.buildableArea:0.0} m2  (oda disi {grid.OutsideMargin:0.0} m pay)";

            Debug.Log("[Constructor] Izgara raporu\n" + msg);
            DrawGridViz(grid);
            return msg + "\n\nSahneye '" + GridVizName + "' gorseli eklendi.";
        }

        /// <summary>
        /// Hucreleri sahnede boyar (yesil = bos, kirmizi = mobilya). Gecici hata ayiklama
        /// gorseli: mesh varliga kaydedilmez, EditorOnly etiketli oldugu icin build'e girmez.
        /// </summary>
        static void DrawGridViz(RoomGrid grid)
        {
            var old = GameObject.Find(GridVizName);
            if (old != null) Object.DestroyImmediate(old);

            var root = new GameObject(GridVizName) { tag = "EditorOnly" };

            AddCellLayer(root.transform, grid, CellState.Free, new Color(0.25f, 0.85f, 0.35f), "OdaIci");
            AddCellLayer(root.transform, grid, CellState.FreeOutside, new Color(0.35f, 0.4f, 0.5f), "OdaDisi");
            AddCellLayer(root.transform, grid, CellState.Blocked, new Color(0.9f, 0.25f, 0.2f), "Mobilya");
            AddCellLayer(root.transform, grid, CellState.Occupied, new Color(0.95f, 0.65f, 0.15f), "Dolu");

            Selection.activeGameObject = root;
        }

        static void AddCellLayer(Transform parent, RoomGrid grid, CellState state, Color color, string label)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // Hucreler arasi ince bosluk: izgara cizgi cizmeden okunur olsun.
            float inset = grid.CellSize * 0.06f;
            float h = grid.CellSize * 0.5f - inset;
            float y = grid.FloorY + 0.015f;

            for (int cz = 0; cz < grid.Rows; cz++)
            {
                for (int cx = 0; cx < grid.Cols; cx++)
                {
                    if (grid.State(cx, cz) != state) continue;
                    Vector3 c = grid.CellCenter(cx, cz);
                    int b = verts.Count;
                    verts.Add(new Vector3(c.x - h, y, c.z - h));
                    verts.Add(new Vector3(c.x - h, y, c.z + h));
                    verts.Add(new Vector3(c.x + h, y, c.z + h));
                    verts.Add(new Vector3(c.x + h, y, c.z - h));
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
            }
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = "GridViz_" + label };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(label + " (" + verts.Count / 4 + ")");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", color);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ------------------------------------------------------------- menu 27

        [MenuItem("Tools/VR Multiplayer/27. Test Haritasi Uret")]
        public static void GenerateTestMapMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", GenerateTestMap(), "Tamam");

        public static string GenerateTestMap()
        {
            var plan = LoadPlan(out string error);
            if (plan == null) return error;

            var grid = RoomGrid.FromPlan(plan);
            if (grid == null) return "Izgara kurulamadi — oda plani gecersiz.";

            var lib = PropLibrary.Instance;
            if (lib.Count == 0)
                return "Prop kutuphanesi bos.\n\nOnce menu 25 (Prop Kutuphanesi Tara) calistir.";

            // Yalnizca zemine oturan, kucuk proplar: test haritasi odayi kapatmasin.
            var usable = new List<PropDef>();
            foreach (var p in lib.props)
                if (p != null && p.snap == PropSnap.Floor && p.category != PropCategory.Ground &&
                    p.sizeMeters.x <= TestMapMaxPropMetres && p.sizeMeters.y <= TestMapMaxPropMetres)
                    usable.Add(p);

            if (usable.Count == 0)
                return "Kutuphanede zemine konabilecek uygun prop yok.";

            var layout = new MapLayout
            {
                name = TestMapName,
                createdBy = "Editor (menu 27)",
                libraryVersion = lib.contentVersion,
                cellSize = grid.CellSize,
                buildMargin = grid.OutsideMargin,
                builtForRoom = plan,
            };

            // SABIT tohum: ayni odada her calistirmada AYNI harita cikar, boylece bir
            // degisikligin sonucu gercekten degisen sey mi yoksa rastgelelik mi ayirt edilir.
            var rng = new System.Random(20260729);
            int placed = 0;

            for (int cz = 1; cz < grid.Rows - 1; cz += 3)
            {
                for (int cx = 1; cx < grid.Cols - 1; cx += 3)
                {
                    // Test haritasi ODA ICINDE kalsin: izgara artik metrelerce disari tasiyor,
                    // rastgele serpistirmek odayla ilgisi olmayan bir alan uretirdi.
                    if (grid.State(cx, cz) != CellState.Free) continue;

                    var def = usable[rng.Next(usable.Count)];
                    byte rot = (byte)(rng.Next(4) * MapLayout.QuarterTurnSteps);   // 0/90/180/270
                    var size = grid.FootprintSize(def, rot);
                    var min = RoomGrid.CenterToMin(new Vector2Int(cx, cz), size);

                    if (!grid.CanPlace(def, min, rot)) continue;

                    layout.Add(def.id, min.x, min.y, 0, rot);
                    grid.Occupy(def, min, rot);
                    placed++;
                }
            }

            if (!layout.Save(TestMapName)) return "Harita kaydedilemedi — Console'a bak.";
            AssetDatabase.Refresh();

            return $"Test haritasi uretildi: {placed} prop\n\n{MapLayout.PathFor(TestMapName)}\n\n" +
                   "Menu 28 ile sahneye kurabilirsin.";
        }

        // ------------------------------------------------------------- menu 28

        [MenuItem("Tools/VR Multiplayer/28. Haritayi Sahneye Kur")]
        public static void BuildMapIntoSceneMenu()
        {
            Directory.CreateDirectory(MapLayout.Directory);
            string path = EditorUtility.OpenFilePanel("Harita sec", MapLayout.Directory, "json");
            if (string.IsNullOrEmpty(path)) return;   // kullanici vazgecti
            EditorUtility.DisplayDialog("VR Multiplayer", BuildMapIntoScene(path), "Tamam");
        }

        /// <summary>Builds a saved map into the open scene. <paramref name="path"/> is a full
        /// file path; pass null to use the map called "Test".</summary>
        public static string BuildMapIntoScene(string path = null)
        {
            if (string.IsNullOrEmpty(path)) path = MapLayout.PathFor(TestMapName);
            if (!File.Exists(path)) return "Harita dosyasi yok:\n" + path;

            var layout = MapLayout.FromJson(File.ReadAllText(path));
            if (layout == null) return "Harita okunamadi:\n" + path;

            var grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize);
            if (grid == null) return "Haritanin icindeki oda plani gecersiz — izgara kurulamadi.";

            grid.ApplyLayout(layout, PropLibrary.Instance);
            var built = MapBuilder.Build(layout, PropLibrary.Instance, MapBuilder.EnsureRoot(), grid);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            return $"'{layout.name}' kuruldu.\n\n" +
                   $"Kurulan: {built.Count} / {layout.Count} prop\n" +
                   $"Kok obje: {MapBuilder.DefaultRootName}\n\n" +
                   (built.Count < layout.Count
                       ? "Eksikler Console'da listelendi (kutuphanede olmayan proplar)."
                       : "Hepsi kuruldu.");
        }

        // ------------------------------------------------------------- menu 29

        [MenuItem("Tools/VR Multiplayer/29. Constructor Oz-Denetim")]
        public static void SelfCheckMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer — Constructor Oz-Denetim", SelfCheck(), "Tamam");

        public static string SelfCheck()
        {
            var fails = new List<string>();
            int checks = 0;

            void Check(bool ok, string what)
            {
                checks++;
                if (!ok) fails.Add(what);
            }

            // --- poligon matematigi: 4x4 m kare, kosesi (0,0) ---
            var square = new[]
            {
                new Vector2(0f, 0f), new Vector2(4f, 0f),
                new Vector2(4f, 4f), new Vector2(0f, 4f),
            };
            Check(RoomGrid.PointInPolygon(new Vector2(2f, 2f), square), "PointInPolygon: merkez icerde olmali");
            Check(!RoomGrid.PointInPolygon(new Vector2(5f, 2f), square), "PointInPolygon: disari icerde sayildi");
            Check(!RoomGrid.PointInPolygon(new Vector2(-1f, -1f), square), "PointInPolygon: negatif kose icerde sayildi");
            Check(Mathf.Abs(RoomGrid.DistanceToPolygonEdge(new Vector2(2f, 2f), square) - 2f) < 0.001f,
                "DistanceToPolygonEdge: merkezin kenara uzakligi 2 m olmali");
            Check(RoomGrid.DistanceToPolygonEdge(new Vector2(0.1f, 2f), square) < 0.2f,
                "DistanceToPolygonEdge: kenara yakin nokta uzak cikti");

            // --- ayak izi dondurme ---
            // 1 m'lik hucre secildi: metre ile hucre birebir ortusunce beklenen degerler
            // dogrudan okunur kalir, cevrim aritmetigi testin konusunu golgelemez.
            const float cell1m = 1f;
            var wide = new PropDef { id = "test", sizeMeters = new Vector2(3f, 1f) };
            Check(RoomGrid.FootprintCells(wide, 0, cell1m) == new Vector2Int(3, 1), "FootprintCells: 0 derece degismemeli");
            Check(RoomGrid.FootprintCells(wide, 18, cell1m) == new Vector2Int(1, 3), "FootprintCells: 90 derece takla atmali");
            Check(RoomGrid.FootprintCells(wide, 36, cell1m) == new Vector2Int(3, 1), "FootprintCells: 180 derece degismemeli");
            Check(RoomGrid.FootprintCells(wide, 54, cell1m) == new Vector2Int(1, 3), "FootprintCells: 270 derece takla atmali");
            // Ara acilarda sinir kutusu donmus dikdortgenin GERCEK kutusu. 45 derecede 3x1'in
            // kosegen acikligi 2.83 m, yani 3 hucre — eski "kare sinir kutusu" kisayoluyla ayni
            // sayi, ama artik tesadufen degil hesaptan geliyor.
            Check(RoomGrid.FootprintCells(wide, 9, cell1m) == new Vector2Int(3, 3), "FootprintCells: 45 derecede 3x1 -> 3x3");
            Check(RoomGrid.FootprintCells(wide, 3, cell1m) == new Vector2Int(4, 2), "FootprintCells: 15 derecede 3x1 -> 4x2 (kare DEGIL)");
            var sq = new PropDef { id = "sq", sizeMeters = new Vector2(2f, 2f) };
            // Eskiden kare ayak izi ara acida hic buyumezdi. Geometrik olarak yanlisti: 2x2 m
            // 45 derece donunce 2.83 m'ye yayilir. Kutu artik dogruyu soyluyor, kaplamayi ise
            // Covers belirledigi icin kutunun buyumesi komsuya yer kaybettirmiyor.
            Check(RoomGrid.FootprintCells(sq, 9, cell1m) == new Vector2Int(3, 3), "FootprintCells: 45 derecede 2x2 -> 3x3");
            Check(RoomGrid.FootprintCells(sq, 0, cell1m) == new Vector2Int(2, 2), "FootprintCells: kare ceyrek turda buyumemeli");

            // --- boyut ayak izini etkilemeli, AMA yalnizca genislik ekseninde ---
            // sq 2x2 m; ikinci bilesen (yerel Z = kalinlik) her durumda 2 kalmali.
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 100) == new Vector2Int(2, 2), "Boyut %100 ayak izini degistirmemeli");
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 200) == new Vector2Int(4, 2), "Boyut %200 yalnizca genisligi iki katina cikarmali");
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 50) == new Vector2Int(1, 2), "Boyut %50 yalnizca genisligi yariya indirmeli");
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 25) == new Vector2Int(1, 2), "Genislik asla 0 olmamali");
            // EN YAKIN hucreye — taban olcunun yuvarlamasiyla ayni kural. Eskiden Ceil'di ve
            // olcekli propu bir hucre sisirip yanina konani uzaklastiriyordu.
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 130) == new Vector2Int(3, 2), "Kesirli boyut en yakina yuvarlanmali (2.6 -> 3)");
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 120) == new Vector2Int(2, 2), "Kesirli boyut en yakina yuvarlanmali (2.4 -> 2)");
            Check(RoomGrid.FootprintCells(wide, 18, cell1m, 200) == new Vector2Int(1, 6), "Boyut ve donus birlikte calismali");

            // --- olcek TAM HUCRE adimlarindan uretiliyor ---
            // Oyuncunun bastigi sey hucre, yuzde yalnizca depolama birimi. Gidis-donus kayipsiz
            // olmali: "bir hucre buyut" iki hucre atlarsa ya da hic bir sey yapmazsa, iki propu
            // yan yana getirmek yine sansa kalir.
            foreach (var probe in new[]
                     {
                         new PropDef { id = "p12", sizeMeters = new Vector2(1.5f, 0.25f) },   // 12 hucre
                         new PropDef { id = "p16", sizeMeters = new Vector2(2.0f, 0.25f) },   // 16 hucre
                         new PropDef { id = "p3",  sizeMeters = new Vector2(0.375f, 0.25f) }, //  3 hucre
                     })
            {
                // Ust sinir yuzdenin BYTE olmasindan geliyor: %255'in otesi saklanamiyor, yani
                // 3 hucrelik bir prop en fazla 7 hucreye cikabiliyor. Testin oradan otesini
                // sinamasi, helper'i format sinirindan sorumlu tutmak olurdu.
                int baseCells = RoomGrid.WidthCells(probe, RoomGrid.DefaultCellSize);
                int reach = Mathf.FloorToInt(baseCells * 2.55f);
                bool roundTrip = true;
                for (int c = 1; c <= reach; c++)
                {
                    byte pct = RoomGrid.WidthPctForCells(probe, RoomGrid.DefaultCellSize, c);
                    if (RoomGrid.WidthCells(probe, RoomGrid.DefaultCellSize, pct) != c) roundTrip = false;
                }
                Check(roundTrip, $"WidthPctForCells/WidthCells: '{probe.id}' gidis-donusu kayipli");
            }

            // Fit: olculecek mesh yoksa prop yuzdelere dusmeli. Bu dal olmasa prefabi
            // cozulemeyen bir prop sahnede SIFIR boyutta belirirdi. (Konsoldaki "ne prefab ne
            // resourcePath var" uyarisi bu testin kendisinden gelir, gercek bir sorun degil.)
            var noMesh = new PropDef { id = "olcusuz", sizeMeters = Vector2.one };
            var noMeshScale = MapBuilder.LocalScaleFor(noMesh, null, RoomGrid.DefaultCellSize, 200, 150);
            Check(Mathf.Abs(noMeshScale.x - 2f) < 0.001f && Mathf.Abs(noMeshScale.y - 1.5f) < 0.001f &&
                  Mathf.Abs(noMeshScale.z - 1f) < 0.001f,
                "LocalScaleFor: olculemeyen prop yuzdelere dusmeli, sifira degil");

            // --- FIT EKSENININ IKI YOLU ---
            const float c125 = 0.125f;
            // 1) Kucuk duzeltme: izgaranin yuvarlamasini emer.
            Check(Mathf.Abs(MapBuilder.FitAxis(1.25f, 1.192f, 1f, c125) - 1.25f / 1.192f) < 0.001f,
                "FitAxis: kucuk duzeltme uygulanmali");
            // 2) Hucre altinda kalan eksen: izgara zaten tam hucre ayirdi, prop ona esitlenir.
            //    Dik yerlesimin bitisik olmasini saglayan tek sey bu.
            Check(Mathf.Abs(MapBuilder.FitAxis(c125, 0.034f, 1f, c125) - c125 / 0.034f) < 0.001f,
                "FitAxis: hucreden ince eksen bir hucreye buyutulmeli");
            // Tek yonlu: KUCULME hucre altinda bile uygulanmamali, yoksa golgesi genis ama
            // ayak izi bir hucre olan bir agac govdesine buzulurdu.
            Check(Mathf.Abs(MapBuilder.FitAxis(c125, 2f, 1f, c125) - 1f) < 0.001f,
                "FitAxis: bir hucreye KUCULTME yapilmamali (agac govdesi tuzagi)");
            // Bir hucreden buyuk hedefte buyume yine toleransa tabi.
            Check(Mathf.Abs(MapBuilder.FitAxis(0.5f, 0.2f, 1f, c125) - 1f) < 0.001f,
                "FitAxis: bir hucreyi asan buyume tolerans disinda kalmali");

            // 3) YARIM HUCREYI GECMEYEN duzeltme — mutlak olcut. Ince kafeste ortaya cikti:
            //    6.25 cm'lik hucrede 8.4 cm'lik bir bariyer %25.6 kuculmek zorunda, yani
            //    oransal kapidan geciremiyor; oysa fark 2.2 cm, yuvarlama butcesinin ucte biri.
            const float c0625 = 0.0625f;
            Check(Mathf.Abs(MapBuilder.FitAxis(c0625, 0.084f, 1f, c0625) - c0625 / 0.084f) < 0.001f,
                "FitAxis: yarim hucreyi gecmeyen KUCULME uygulanmali");
            // Ayni kapi gercek uyusmazliklari gecirmemeli: govdesi bir hucre, tepesi 2 m agac.
            Check(Mathf.Abs(MapBuilder.FitAxis(c0625, 2f, 1f, c0625) - 1f) < 0.001f,
                "FitAxis: yarim hucre kapisi agac govdesi tuzagini gecirmemeli");
            Check(Mathf.Abs(MapBuilder.FitAxis(1f, 2.4f, 1f, c0625) - 1f) < 0.001f,
                "FitAxis: yarim hucre kapisi dogus halkasini gecirmemeli");
            // Aynalanmis prefab: isaret korunmali, yoksa model ters cevrilirdi.
            Check(MapBuilder.FitAxis(c125, 0.034f, -1f, c125) < 0f,
                "FitAxis: negatif olcegin isareti korunmali");

            // Fit KAPALIYKEN eski davranis aynen surmeli: prefabin kendi olcegi carpilir.
            var unfitted = new PropDef { id = "fitsiz", sizeMeters = Vector2.one, fitToFootprint = false };
            var unfittedScale = MapBuilder.LocalScaleFor(unfitted, null, RoomGrid.DefaultCellSize, 200, 100);
            Check(Mathf.Abs(unfittedScale.x - 2f) < 0.001f && Mathf.Abs(unfittedScale.z - 1f) < 0.001f,
                "LocalScaleFor: fit kapaliyken KALINLIK (Z) enden etkilenmemeli");

            // Kalinlik hicbir olcekte buyumemeli: izgara, propun icinde durmadigi hucreyi
            // rezerve ederse yan yana iki bariyer arasinda hayali bir bosluk kalir.
            Check(RoomGrid.FootprintCells(sq, 0, cell1m, 250).y == RoomGrid.FootprintCells(sq, 0, cell1m).y,
                "Ayak izi: KALINLIK ekseni olcekten etkilenmemeli");

            // --- ASIL KAZANIM: fiziksel boy izgara cozunurlugunden bagimsiz ---
            // Ayni 1.5 x 0.25 m bariyer, hucre yariya inince iki kati hucre kaplamali; yani
            // zeminde AYNI yeri tutmali. Boyut hucre cinsinden saklansaydi yerine yari boyuna
            // duser, kayitli haritalardaki her prop kayardi. Bu iki satir o regresyonu yakalar.
            var barrier = new PropDef { id = "barrier", sizeMeters = new Vector2(1.5f, 0.25f) };
            Check(RoomGrid.FootprintCells(barrier, 0, 0.25f) == new Vector2Int(6, 1),
                "0.25 m izgarada 1.5 x 0.25 m bariyer 6x1 hucre olmali");
            Check(RoomGrid.FootprintCells(barrier, 0, 0.125f) == new Vector2Int(12, 2),
                "0.125 m izgarada ayni bariyer 12x2 hucre olmali (fiziksel boy degismedi)");

            // BOY ayak izini etkilememeli: bir sey uzayinca zeminde daha cok yer kaplamaz
            var tallProp = new MapLayout();
            var tall = tallProp.Add("p", 0, 0, 0, 0, 100, 250);
            Check(tall.scalePct == 100 && tall.heightPct == 250, "En ve boy ayri saklanmali");
            Check(Mathf.Abs(tall.ScaleVector.x - 1f) < 0.001f && Mathf.Abs(tall.ScaleVector.y - 2.5f) < 0.001f &&
                  Mathf.Abs(tall.ScaleVector.z - 1f) < 0.001f, "ScaleVector: boy yalnizca Y eksenini buyutmeli");
            var wideProp = tallProp.Add("p", 0, 0, 0, 0, 200, 100);
            Check(Mathf.Abs(wideProp.ScaleVector.x - 2f) < 0.001f && Mathf.Abs(wideProp.ScaleVector.y - 1f) < 0.001f,
                  "ScaleVector: en yalnizca X eksenini buyutmeli");
            Check(Mathf.Abs(wideProp.ScaleVector.z - 1f) < 0.001f,
                  "ScaleVector: KALINLIK (Z) enden etkilenmemeli");
            var oldProp = MapLayout.FromJson("{\"props\":[{\"propId\":\"p\",\"scalePct\":0,\"heightPct\":0}]}");
            Check(oldProp != null && Mathf.Abs(oldProp.props[0].ScaleVector.y - 1f) < 0.001f,
                  "Eski harita (alan yok/0) 1.0 olcek saymali");

            // --- v1 -> v2 donus tasima: adim 15 dereceden 5'e indi, eski rot x3 tasinmali ---
            var v1Map = new MapLayout { version = 1 };
            v1Map.Add("p", 0, 0, 0, 6);   // v1'de 6 adim = 90 derece
            var v2Map = MapLayout.FromJson(v1Map.ToJson());
            Check(v2Map != null && v2Map.Count == 1 && v2Map.props[0].rot == 18,
                "Surum tasima: v1 rot=6 (90 derece) yuklenince 18 adim olmali");
            Check(v2Map != null && v2Map.Count == 1 && Mathf.Abs(v2Map.props[0].Yaw - 90f) < 0.001f,
                "Surum tasima: fiziksel aci degismemeli (90 derece kalmali)");
            Check(v2Map != null && v2Map.version == MapLayout.CurrentVersion,
                "Surum tasima: yuklenen harita guncel surume damgalanmali");
            var v2Again = v2Map != null ? MapLayout.FromJson(v2Map.ToJson()) : null;
            Check(v2Again != null && v2Again.Count == 1 && v2Again.props[0].rot == 18,
                "Surum tasima: v2 harita ikinci yuklemede bir daha carpilmamali");

            // --- v3 serbest katman: gidis-donus, kimlik uzayi, eski surumlerin acilisi ---
            var freeMap = new MapLayout();
            var freeOne = freeMap.AddFree("p", new Vector3(1.234f, 0.056f, -2.789f),
                new Vector3(1.82f, 91.529f, 5.09f), new Vector3(1.125f, 1f, 1f));
            var gridTwin = freeMap.Add("p", 3, 4, 0, 18);
            Check(freeOne.instanceId != gridTwin.instanceId,
                "Serbest katman: izgara ve serbest prop ayni kimlik sayacini paylasmali (cakisma yok)");
            var freeBack = MapLayout.FromJson(freeMap.ToJson());
            Check(freeBack != null && freeBack.FreeCount == 1 && freeBack.Count == 1,
                "Serbest katman: JSON gidis-donusunde iki katman da korunmali");
            Check(freeBack != null &&
                  (freeBack.freeProps[0].position - new Vector3(1.234f, 0.056f, -2.789f)).magnitude < 0.0005f,
                "Serbest katman: konum kayipsiz gidip donmeli (Y dahil)");
            Check(freeBack != null &&
                  (freeBack.freeProps[0].rotationEuler - new Vector3(1.82f, 91.529f, 5.09f)).magnitude < 0.0005f,
                "Serbest katman: uc eksenli aci kayipsiz gidip donmeli");
            Check(freeBack != null && freeBack.version == MapLayout.CurrentVersion,
                "Serbest katman: yeni harita guncel surumu (v3) tasimali");
            Check(freeBack != null && freeBack.FindFree(freeOne.instanceId) != null &&
                  freeBack.RemoveFree(freeOne.instanceId) && freeBack.FreeCount == 0,
                "Serbest katman: FindFree/RemoveFree kimlikle calismali");

            // Gercek v2 kaydin taklidi: freeProps ALANI HIC YOK. Bos listeyle acilmali,
            // izgara proplari aynen kalmali, rot bir daha carpilmamali.
            var v2Raw = MapLayout.FromJson(
                "{\"version\":2,\"props\":[{\"propId\":\"p\",\"rot\":18,\"scalePct\":100,\"heightPct\":100}]}");
            Check(v2Raw != null && v2Raw.freeProps != null && v2Raw.FreeCount == 0 &&
                  v2Raw.Count == 1 && v2Raw.props[0].rot == 18 &&
                  v2Raw.version == MapLayout.CurrentVersion,
                "Surum tasima: alansiz (gercek v2) JSON bos serbest listeyle acilmali, rot degismemeli");

            // Sifir olcek = gorunmez prop tuzagi; yukleme sinirda 1'e duzeltmeli.
            var zeroScale = MapLayout.FromJson(
                "{\"version\":3,\"freeProps\":[{\"propId\":\"p\",\"scale\":{\"x\":0,\"y\":0,\"z\":0}}]}");
            Check(zeroScale != null && zeroScale.FreeCount == 1 &&
                  zeroScale.freeProps[0].scale == Vector3.one,
                "Serbest katman: sifir olcek yuklemede 1'e duzeltilmeli");

            // --- serbest katman: aci yuvarlama (izgaraya geri oturtma) ---
            Check(FreeEditController.NearestRotStep(91.53f) == 18,
                "NearestRotStep: 91.53 derece ceyrek tura (18 adim) yuvarlanmali");
            Check(FreeEditController.NearestRotStep(93f) == 19,
                "NearestRotStep: 93 derece 95'e (19 adim) yuvarlanmali");
            Check(FreeEditController.NearestRotStep(0f) == 0,
                "NearestRotStep: 0 derece 0 adim olmali");
            Check(FreeEditController.NearestRotStep(-5f) == MapLayout.RotationSteps - 1,
                "NearestRotStep: negatif aci mod ile sarilmali");
            Check(FreeEditController.NearestRotStep(359f) == 0,
                "NearestRotStep: 359 derece basa (0) sarilmali");

            // --- surukleme kolu (gizmo) matematigi: isin-eksen ve isin-halka ---
            // Isin (0,1,5)'ten -Z'ye bakiyor; eksen X ekseni, kokeni orijinde.
            // En yakin nokta x=0 olmali (isin YZ duzleminde ilerliyor).
            var axisRay = new Ray(new Vector3(0f, 1f, 5f), Vector3.back);
            Check(FreeEditGizmo.ClosestPointOnAxis(axisRay, Vector3.zero, Vector3.right, out float ax) &&
                  Mathf.Abs(ax) < 0.001f,
                "ClosestPointOnAxis: dik bakista eksen parametresi 0 olmali");

            // Isin x=2'den geciyorsa en yakin nokta x=2'de olmali.
            var axisRay2 = new Ray(new Vector3(2f, 1f, 5f), Vector3.back);
            Check(FreeEditGizmo.ClosestPointOnAxis(axisRay2, Vector3.zero, Vector3.right, out float ax2) &&
                  Mathf.Abs(ax2 - 2f) < 0.001f,
                "ClosestPointOnAxis: kaydirilmis isin ayni kadar kaymis parametre vermeli");

            // Eksene PARALEL isin: en yakin nokta tanimsiz, false donmeli.
            Check(!FreeEditGizmo.ClosestPointOnAxis(new Ray(Vector3.up, Vector3.right),
                    Vector3.zero, Vector3.right, out _),
                "ClosestPointOnAxis: eksene paralel isin false donmeli");

            // Halka: Y normalli duzlem (yatay). Isin yukaridan +X yonundeki bir noktaya insin.
            var ringRay = new Ray(new Vector3(1f, 5f, 0f), Vector3.down);
            Check(FreeEditGizmo.RingAngle(ringRay, Vector3.zero, Vector3.up, out float rAng),
                "RingAngle: yatay duzleme inen isin kesismeli");
            // Ayni yonden gelen ikinci bir isin AYNI aciyi vermeli (kararlilik).
            var ringRay2 = new Ray(new Vector3(2f, 9f, 0f), Vector3.down);
            Check(FreeEditGizmo.RingAngle(ringRay2, Vector3.zero, Vector3.up, out float rAng2) &&
                  Mathf.Abs(Mathf.DeltaAngle(rAng, rAng2)) < 0.001f,
                "RingAngle: ayni yondeki iki isin ayni aciyi vermeli");
            // 90 derece donmus yon 90 derece fark uretmeli.
            var ringRay3 = new Ray(new Vector3(0f, 5f, 1f), Vector3.down);
            Check(FreeEditGizmo.RingAngle(ringRay3, Vector3.zero, Vector3.up, out float rAng3) &&
                  Mathf.Abs(Mathf.Abs(Mathf.DeltaAngle(rAng, rAng3)) - 90f) < 0.001f,
                "RingAngle: dik iki yon arasinda 90 derece olmali");
            // Duzleme PARALEL isin kesismez.
            Check(!FreeEditGizmo.RingAngle(new Ray(new Vector3(0f, 1f, 0f), Vector3.right),
                    Vector3.zero, Vector3.up, out _),
                "RingAngle: duzleme paralel isin false donmeli");

            // Kademe yuvarlama (Ctrl)
            Check(Mathf.Abs(FreeEditGizmo.SnapTo(0.037f, 0.01f) - 0.04f) < 0.0001f,
                "SnapTo: 3.7 cm, 1 cm kademede 4 cm'e yuvarlanmali");
            Check(Mathf.Abs(FreeEditGizmo.SnapTo(37f, 5f) - 35f) < 0.0001f,
                "SnapTo: 37 derece, 5 derece kademede 35'e yuvarlanmali");
            Check(Mathf.Abs(FreeEditGizmo.SnapTo(0.037f, 0f) - 0.037f) < 0.0001f,
                "SnapTo: kademe 0 iken deger degismemeli");

            // --- boy kollari: carpan ve alt sinir ---
            // Kolu KENDI UZUNLUGU kadar disari cekmek iki kat, yerinde durmak aynen birakmali.
            Check(Mathf.Abs(FreeEditGizmo.ScaleFactor(0f, 0.5f) - 1f) < 0.0001f,
                "ScaleFactor: kol oynamadiysa carpan 1 olmali");
            Check(Mathf.Abs(FreeEditGizmo.ScaleFactor(0.5f, 0.5f) - 2f) < 0.0001f,
                "ScaleFactor: kol boyu kadar disari cekmek iki katina cikarmali");
            Check(Mathf.Abs(FreeEditGizmo.ScaleFactor(-0.25f, 0.5f) - 0.5f) < 0.0001f,
                "ScaleFactor: kol boyunun yarisi kadar iceri itmek yariya indirmeli");
            // Mesafeden BAGIMSIZ: uzaktaki prop icin kol uzar, ayni oran ayni carpani vermeli.
            Check(Mathf.Abs(FreeEditGizmo.ScaleFactor(4f, 4f) -
                            FreeEditGizmo.ScaleFactor(0.5f, 0.5f)) < 0.0001f,
                "ScaleFactor: carpan kol uzunluguna degil ORANA bagli olmali");
            // Kolu sonuna kadar iceri itmek bile olcegi ters cevirmemeli.
            Check(FreeEditGizmo.ScaleFactor(-10f, 0.5f) > 0f,
                "ScaleFactor: asiri iceri itmede bile carpan pozitif kalmali");

            Check(Mathf.Abs(FreeEditGizmo.ApplyScale(2f, 1.5f, 0f) - 3f) < 0.0001f,
                "ApplyScale: kademe yokken olcek carpanla dogrudan carpilmali");
            Check(Mathf.Abs(FreeEditGizmo.ApplyScale(1f, 1.13f, 0.05f) - 1.15f) < 0.0001f,
                "ApplyScale: Ctrl kademesi SONUCU yuvarlamali (1.13 -> 1.15)");
            Check(FreeEditGizmo.ApplyScale(1f, 0.0001f, 0f) >= FreeEditGizmo.MinScale,
                "ApplyScale: olcek MinScale altina inmemeli");
            Check(FreeEditGizmo.ApplyScale(1f, -3f, 0f) >= FreeEditGizmo.MinScale,
                "ApplyScale: olcek NEGATIFE dusmemeli (mesh ic ters donerdi)");
            // Kademe, alt siniri deler gibi gorunse de sinir en son sozu soylemeli.
            Check(FreeEditGizmo.ApplyScale(0.01f, 0.1f, 0.05f) >= FreeEditGizmo.MinScale,
                "ApplyScale: kademe sifira yuvarlasa da alt sinir korunmali");

            // N tusu: uc takim da sirayla gelmeli ve basa donmeli.
            Check(FreeEditGizmo.NextMode(FreeEditGizmo.Mode.Move) == FreeEditGizmo.Mode.Rotate &&
                  FreeEditGizmo.NextMode(FreeEditGizmo.Mode.Rotate) == FreeEditGizmo.Mode.Scale &&
                  FreeEditGizmo.NextMode(FreeEditGizmo.Mode.Scale) == FreeEditGizmo.Mode.Move,
                "NextMode: N tusu tasi -> dondur -> boy -> tasi dongusunu kurmali");

            // --- duzlem kollari: kesisim ve eksen esleme ---
            // Y normalli (yatay) duzleme yukaridan inen isin, tam indigi noktada kesmeli.
            Check(FreeEditGizmo.PlanePoint(new Ray(new Vector3(1.5f, 4f, -2f), Vector3.down),
                    Vector3.zero, Vector3.up, out Vector3 ph) &&
                  Mathf.Abs(ph.x - 1.5f) < 0.001f && Mathf.Abs(ph.y) < 0.001f &&
                  Mathf.Abs(ph.z + 2f) < 0.001f,
                "PlanePoint: yatay duzleme inen isin dogru noktada kesmeli");
            Check(!FreeEditGizmo.PlanePoint(new Ray(new Vector3(0f, 1f, 0f), Vector3.right),
                    Vector3.zero, Vector3.up, out _),
                "PlanePoint: duzleme paralel isin false donmeli");
            Check(!FreeEditGizmo.PlanePoint(new Ray(new Vector3(0f, 4f, 0f), Vector3.up),
                    Vector3.zero, Vector3.up, out _),
                "PlanePoint: duzlem ARKADA kaliyorsa false donmeli");

            // Duzlem eksenleri izgarayla ayni X/Y/Z olmali (kademeli kaydirma onlarda anlamli).
            FreeEditGizmo.PlaneAxesFor(1, out Vector3 u1, out Vector3 w1);   // normal Y -> Z ve X
            Check(u1 == Vector3.forward && w1 == Vector3.right,
                "PlaneAxesFor: Y normalli duzlem Z ve X eksenlerinde kaymali");
            FreeEditGizmo.PlaneAxesFor(0, out Vector3 u0, out Vector3 w0);   // normal X -> Y ve Z
            Check(u0 == Vector3.up && w0 == Vector3.forward,
                "PlaneAxesFor: X normalli duzlem Y ve Z eksenlerinde kaymali");
            // --- gizmo dayanagi: kollar propun ON YUZUNUN ONUNDE durmali, ICINDE degil ---
            var box = new Bounds(new Vector3(5f, 1.1f, 2f), new Vector3(2f, 2.2f, 0.25f));
            var camAt = new Vector3(5f, 1.1f, 10f);   // +Z tarafindan bakiyor
            var gizmoAnchor = FreeEditGizmo.AnchorFor(box, camAt, 0.1f);
            Check(gizmoAnchor.z > box.max.z,
                $"AnchorFor: dayanak kutunun ON yuzunun DISINDA olmali (z={gizmoAnchor.z:0.000} > {box.max.z:0.000})");
            Check(Mathf.Abs(gizmoAnchor.z - (box.max.z + 0.1f)) < 0.001f,
                "AnchorFor: dayanak on yuzden tam 'pad' kadar onde olmali");
            Check(!box.Contains(gizmoAnchor), "AnchorFor: dayanak kutunun ICINDE kalmamali");

            // Kamera ters taraftaysa dayanak da ters tarafa gecmeli.
            var anchorBack = FreeEditGizmo.AnchorFor(box, new Vector3(5f, 1.1f, -10f), 0.1f);
            Check(anchorBack.z < box.min.z,
                "AnchorFor: kamera arkadayken dayanak arka yuzun disina gecmeli");

            // Yukaridan bakista da kutunun disinda (ustunde) kalmali.
            var anchorTop = FreeEditGizmo.AnchorFor(box, new Vector3(5f, 20f, 2f), 0.1f);
            Check(anchorTop.y > box.max.y && !box.Contains(anchorTop),
                "AnchorFor: yukaridan bakista dayanak ust yuzun uzerinde olmali");

            // Duzlem eksenleri normale DIK olmali — aksi halde kaydirma duzlemden cikardi.
            for (int i = 0; i < 3; i++)
            {
                FreeEditGizmo.PlaneAxesFor(i, out Vector3 pu, out Vector3 pw);
                Vector3 n = i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
                Check(Mathf.Abs(Vector3.Dot(pu, n)) < 0.0001f && Mathf.Abs(Vector3.Dot(pw, n)) < 0.0001f,
                    $"PlaneAxesFor: eksen {i} icin duzlem eksenleri normale dik olmali");
            }

            // --- merkezleme ---
            Check(RoomGrid.CenterToMin(new Vector2Int(10, 10), Vector2Int.one) == new Vector2Int(10, 10),
                "CenterToMin: 1x1 ayni hucrede kalmali");
            Check(RoomGrid.CenterToMin(new Vector2Int(10, 10), new Vector2Int(3, 3)) == new Vector2Int(9, 9),
                "CenterToMin: 3x3 bir hucre geri kaymali");

            // --- izgara: sentetik 4x4 m oda ---
            var plan = new RoomPlan
            {
                floorY = 0f,
                ceilingY = 2.5f,
                floorPolygon = square,
                walls = new RoomWall[0],
                furniture = new RoomBox[0],
            };
            // Pay YOK (ne duvar ne oda disi): sinirlar tam olsun, hucre indeksleri sabit kalsin.
            var grid = RoomGrid.FromPlan(plan, 0.25f, 0f, 0f);
            Check(grid != null, "RoomGrid.FromPlan: null dondu");
            if (grid != null)
            {
                Check(grid.Cols == 16 && grid.Rows == 16, $"Izgara boyutu 16x16 olmali, {grid.Cols}x{grid.Rows} cikti");
                Check(grid.State(0, 0) == CellState.Free, "Kose hucre bos olmali (pay yokken)");
                Check(grid.State(-1, 0) == CellState.Outside, "Sinir disi hucre Outside donmeli");
                Check(grid.State(99, 99) == CellState.Outside, "Sinir disi hucre Outside donmeli");

                // hucre <-> dunya gidis donus
                var back = grid.WorldToCell(grid.CellCenter(5, 7));
                Check(back == new Vector2Int(5, 7), $"Hucre<->dunya gidis donus bozuk: {back}");

                // doluluk yasam dongusu
                var rect = new RectInt(2, 2, 3, 1);
                Check(grid.CanPlaceCells(rect), "Bos alana yerlestirilebilmeli");
                grid.OccupyCells(rect);
                Check(!grid.CanPlaceCells(rect), "Dolu alana yerlestirilememeli");
                Check(grid.State(3, 2) == CellState.Occupied, "Ayak izi icindeki hucre Occupied olmali");
                Check(grid.State(5, 2) == CellState.Free, "Ayak izi disindaki hucre etkilenmemeli");
                grid.ReleaseCells(rect);
                Check(grid.CanPlaceCells(rect), "Birakilan alan tekrar bos olmali");

                // duvar payi: kose hucre artik "oda ici" sayilmamali (ama hala insa edilebilir)
                var margin = RoomGrid.FromPlan(plan, 0.25f, 0.3f, 0f);
                Check(margin != null && margin.State(0, 0) == CellState.FreeOutside,
                    "Duvar payi kose hucreyi oda DISI saymali");
                Check(margin != null && RoomGrid.IsBuildable(margin.State(0, 0)),
                    "Oda disi hucre yine de insa edilebilir olmali");

                // determinizm: ayni plandan iki izgara ayni sonucu vermeli
                var twin = RoomGrid.FromPlan(plan, 0.25f, 0f, 0f);
                bool same = twin != null && twin.Cols == grid.Cols && twin.Rows == grid.Rows &&
                            twin.Origin == grid.Origin;
                if (same)
                    for (int cz = 0; cz < grid.Rows && same; cz++)
                        for (int cx = 0; cx < grid.Cols && same; cx++)
                            if (twin.State(cx, cz) != grid.State(cx, cz)) same = false;
                Check(same, "Determinizm: ayni plandan iki izgara ayni cikmali");
            }

            // --- ARA ACI: doluluk sinir kutusu DEGIL, donmus dikdortgen ---
            // Kazanimin olculebilir hali. 2.0 x 0.25 m'lik bir duvar 15 derecede eskiden
            // 16x16 hucre (2 x 2 m) rezerve ediyordu ve iki metre yakinina hicbir sey
            // konamiyordu; artik kutu 16x7 ve o kutunun icinde bile yalnizca duvarin gercekten
            // bastigi seritteki hucreler dolu.
            var wallDef = new PropDef { id = "duvar", sizeMeters = new Vector2(2f, 0.25f), freeRotation = true };
            const byte deg15 = 3;   // 15 derece — 5 derecelik adimla 3 adim
            var angledBox = RoomGrid.FootprintCells(wallDef, deg15, 0.125f);
            Check(angledBox.y < angledBox.x,
                $"Ara acili ince duvarin kutusu kare olmamali ({angledBox.x}x{angledBox.y})");

            var wide2 = RoomGrid.FromPlan(plan, 0.125f, 0f, 3f);   // 4x4 m oda + 3 m pay
            Check(wide2 != null, "Ara aci testi icin izgara kurulamadi");
            if (wide2 != null)
            {
                var mid = new Vector2Int(wide2.Cols / 2, wide2.Rows / 2);
                var wallMin = RoomGrid.CenterToMin(mid, angledBox);

                Check(wide2.CanPlace(wallDef, wallMin, deg15), "Ara acili duvar bos alana konabilmeli");
                wide2.Occupy(wallDef, wallMin, deg15);

                int taken = 0;
                for (int cz = wallMin.y; cz < wallMin.y + angledBox.y; cz++)
                    for (int cx = wallMin.x; cx < wallMin.x + angledBox.x; cx++)
                        if (wide2.State(cx, cz) == CellState.Occupied) taken++;

                int boxArea = angledBox.x * angledBox.y;
                Check(taken > 0, "Ara acili duvar hic hucre kapatmadi (merkez testi ince duvari kacirir mi?)");
                Check(taken < boxArea, $"Ara acida doluluk sinir kutusunun tamami olmamali ({taken}/{boxArea})");

                // ASIL KAZANIM: kutunun koseleri bos kaliyor, yani duvarin dibine baska bir
                // parca konabiliyor. Eskiden dordu de doluydu.
                int freeCorners = 0;
                foreach (var corner in new[]
                         {
                             new Vector2Int(wallMin.x, wallMin.y),
                             new Vector2Int(wallMin.x + angledBox.x - 1, wallMin.y),
                             new Vector2Int(wallMin.x, wallMin.y + angledBox.y - 1),
                             new Vector2Int(wallMin.x + angledBox.x - 1, wallMin.y + angledBox.y - 1),
                         })
                    if (wide2.State(corner.x, corner.y) != CellState.Occupied) freeCorners++;
                Check(freeCorners >= 2, $"Sinir kutusunun kosleri ara acida bos kalmali ({freeCorners}/4)");

                // DOLULUK MESH ILE AYNI YONE EGILMELI. Covers, yerel eksenleri elle kuruyor
                // (Y ekseni etrafinda donus +X'i (cos,-sin) yapar); sin'in isareti ters
                // olsaydi bant AYNAda kalir, duvar bir yone egilirken izgara obur yonu
                // kapatirdi. Testin kendisi Unity'nin KENDI donusunu kullaniyor, yani ayni
                // varsayimi tekrar etmiyor.
                var wallRect = new RectInt(wallMin.x, wallMin.y, angledBox.x, angledBox.y);
                Vector3 wallC = wide2.RectCenter(wallRect, 0, 0f);
                var q = Quaternion.Euler(0f, deg15 * MapLayout.RotationStepDegrees, 0f);
                bool onMesh = true;
                foreach (float fx in new[] { -0.9f, -0.5f, 0f, 0.5f, 0.9f })
                    foreach (float fz in new[] { -0.9f, 0f, 0.9f })
                    {
                        // Yari-olculer: 2.0/2 ve 0.25/2 metre.
                        Vector3 w = wallC + q * new Vector3(fx * 1.0f, 0f, fz * 0.125f);
                        var cell = wide2.WorldToCell(w);
                        if (wide2.State(cell.x, cell.y) != CellState.Occupied) onMesh = false;
                    }
                Check(onMesh, "Ara acida doluluk mesh ile ayni yone egilmeli (eksen isareti ters?)");

                wide2.Release(wallDef, wallMin, deg15);
                Check(wide2.CanPlace(wallDef, wallMin, deg15), "Ara acili duvar birakilinca alan bosalmali");
            }

            // --- DIK YERLESIM BITISIK OLMALI ---
            // Bildirilen hata: bir prop, aynisinin 90 derece donmusuyle yan yana konunca arada
            // bosluk kaliyordu. Izgara masumdu — dikdortgenler bitisikti; acik olan, propun
            // DOLDURMADIGI rezerve paydi. Dik eklemde bir propun (fit'li) uzun ekseni otekinin
            // kalinlik eksenine dayanir, yani pay dogrudan gorunur hale gelir.
            //
            // Tek tek eklem simule etmek yerine ALTTAKI DEGISMEZ sinaniyor: fit'li her prop
            // rezerve ettigi dikdortgeni IKI EKSENDE de doldurmali. Bu saglaniyorsa bitisiklik
            // her yonelim icin kendiliginden cikar.
            var fitLib = PropLibrary.Instance;
            if (fitLib != null && fitLib.Count > 0)
            {
                var loose = new List<string>();
                int examined = 0;
                foreach (var p in fitLib.props)
                {
                    if (p == null || !p.fitToFootprint) continue;
                    var prefab = p.Resolve();
                    if (prefab == null) continue;
                    Vector3 mesh = p.MeshLocalSize;
                    if (mesh.x < 0.0001f || mesh.z < 0.0001f) continue;

                    examined++;
                    var cells = RoomGrid.FootprintCells(p, 0, RoomGrid.DefaultCellSize);
                    var scale = MapBuilder.LocalScaleFor(p, prefab, RoomGrid.DefaultCellSize, 100, 100);

                    float slackX = cells.x * RoomGrid.DefaultCellSize - mesh.x * Mathf.Abs(scale.x);
                    float slackZ = cells.y * RoomGrid.DefaultCellSize - mesh.z * Mathf.Abs(scale.z);
                    float worst = Mathf.Max(Mathf.Abs(slackX), Mathf.Abs(slackZ));

                    // Dolduruyor olmak yetmez, DOGRU YERDE dolduracak: pivot kacikligi
                    // giderilmezse model rezervenin icinde kayar ve dik eklem yine acilir.
                    // Duzeltme her aciyi ayni sekilde toparlamali, o yuzden dort ceyrek turda
                    // da mesh merkezi rect merkezine oturmali.
                    for (byte q = 0; q < MapLayout.RotationSteps; q += MapLayout.QuarterTurnSteps)
                    {
                        float yaw = q * MapLayout.RotationStepDegrees;
                        Vector3 placed = -MapBuilder.PivotOffset(p, scale, yaw)
                                         + Quaternion.Euler(0f, yaw, 0f)
                                           * Vector3.Scale(p.MeshLocalCenter, scale);
                        worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(placed.x), Mathf.Abs(placed.z)));
                    }

                    if (worst > 0.001f)
                        loose.Add($"{p.id} ({worst * 100f:0.0} cm)");
                }
                Check(examined > 0, "Dik yerlesim testi: incelenecek fit'li prop bulunamadi");
                Check(loose.Count == 0,
                    "Dik yerlesim: su proplar rezervesini doldurmuyor, dik eklemde bosluk " +
                    "birakirlar — " + string.Join(", ", loose));
            }

            // --- komsuya yapisma ---
            // Kural seti kucuk ama her biri bir kullanim hatasini onluyor; hepsi ayni izgarada
            // sinaniyor ki "yapisti" ile "kaydi" arasindaki fark gorunur olsun.
            var snapGrid = RoomGrid.FromPlan(plan, 0.25f, 0f, 0f);   // 16x16 hucre, 4x4 m oda
            var block = new PropDef { id = "kutu", sizeMeters = new Vector2(0.5f, 0.5f) };  // 2x2 hucre
            Check(snapGrid != null, "Yapisma testi icin izgara kurulamadi");
            if (snapGrid != null)
            {
                var anchor = new Vector2Int(6, 6);
                snapGrid.Occupy(block, anchor, 0);   // (6,6)-(7,7) dolu

                // BOS ALANDA CEKME YOK: magnet'in kendi cazibesi olmamali, yoksa oyuncu
                // odanin ortasina tek bir prop koyamazdi.
                var lonely = new Vector2Int(0, 0);
                Check(snapGrid.SnapToNeighbour(block, lonely, 0, 100, 2) == lonely,
                    "Yapisma: yakininda hicbir sey yokken nisan oynatilmamali");

                // NISAN ALINAN YER KONULABILIYORSA ORAYA KONUR. Yapisma, gecerli bir hedefi
                // komsuya cekmez — oyuncu isaret ettigi kareye koyamayinca izgara karar veren
                // sey olmaktan cikiyordu.
                var nearMiss = new Vector2Int(9, 6);          // bos ve gecerli, arada bir sutun var
                Check(snapGrid.SnapToNeighbour(block, nearMiss, 0, 100, 2) == nearMiss,
                    "Yapisma: gecerli nisan oynatilmamali (isaret eden kazanir)");

                var flush = new Vector2Int(8, 6);
                Check(snapGrid.SnapToNeighbour(block, flush, 0, 100, 2) == flush,
                    "Yapisma: zaten bitisik yerlesim oynatilmamali");

                // CAKISAN nisan gecerli bir komsulua cekilmeli, kirmizi kalmamali.
                var overlap = new Vector2Int(7, 6);           // dolu hucreye biniyor
                Check(!snapGrid.CanPlace(block, overlap, 0), "Yapisma testi: nisan gercekten cakismali");
                var fixedUp = snapGrid.SnapToNeighbour(block, overlap, 0, 100, 2);
                Check(snapGrid.CanPlace(block, fixedUp, 0), "Yapisma: cakisan nisan gecerli bir yere cekilmeli");
                Check(snapGrid.TouchesOccupied(new RectInt(fixedUp.x, fixedUp.y, 2, 2)),
                    "Yapisma: cakisandan cekilen yer de bitisik olmali");

                // YARICAP 0 = kapali.
                Check(snapGrid.SnapToNeighbour(block, nearMiss, 0, 100, 0) == nearMiss,
                    "Yapisma: yaricap 0 iken hicbir sey oynamamali");

                // MENZIL DISI kalan nisan cekilmemeli — magnet uzaktan calismamali.
                var farAway = new Vector2Int(13, 13);
                Check(snapGrid.SnapToNeighbour(block, farAway, 0, 100, 2) == farAway,
                    "Yapisma: yaricapin disindaki nisan cekilmemeli");
            }

            // --- KATLI DOLULUK ---
            // Bildirilen ihtiyac: "havaya bir seyler koyabilmeliyim". Doluluk iki boyutluyken
            // bir hucreyi kapatmak ustundeki butun havayi da kapatiyordu; asagidaki testler
            // dikey ekseni ayirdigimizi ve AYIRIRKEN cakismaya izin vermedigimizi sinar.
            var lowProp = new PropDef { id = "alcak", sizeMeters = new Vector2(0.5f, 0.5f), height = 0.75f };
            var tallProp2 = new PropDef { id = "yuksek", sizeMeters = new Vector2(0.5f, 0.5f), height = 2.2f };
            var lv = RoomGrid.FromPlan(plan, 0.25f, 0f, 0f, 0.5f);
            Check(lv != null, "Katli doluluk testi icin izgara kurulamadi");
            if (lv != null)
            {
                Check(lv.LevelSpan(lowProp) == 2, "LevelSpan: 0.75 m prop 0.5 m adimda 2 kat tutmali");
                Check(lv.LevelSpan(tallProp2) == 5, "LevelSpan: 2.2 m prop 5 kat tutmali");

                var at = new Vector2Int(4, 4);
                Check(lv.CanPlace(lowProp, at, 0, 100, 0), "Bos sutuna kat 0 konabilmeli");
                lv.Occupy(lowProp, at, 0, 100, 0);

                Check(!lv.CanPlace(lowProp, at, 0, 100, 0), "Ayni kata ikinci prop konmamali");
                Check(!lv.CanPlace(lowProp, at, 0, 100, 1), "Propun ICINE (yarisina) konmamali");
                Check(lv.CanPlace(lowProp, at, 0, 100, 2), "Propun USTUNE konabilmeli — ozelligin tamami bu");

                lv.Occupy(lowProp, at, 0, 100, 2);
                Check(lv.State(at.x, at.y) == CellState.Occupied, "Ust uste iki prop varken hucre Occupied olmali");

                // Ustteki gidince ALTTAKI kalmali: hucre durumu sutunun tamamini temsil ediyor.
                lv.Release(lowProp, at, 0, 100, 2);
                Check(lv.State(at.x, at.y) == CellState.Occupied,
                    "Ustteki silinince alttaki prop hucreyi tutmaya devam etmeli");
                Check(lv.CanPlace(lowProp, at, 0, 100, 2), "Bosalan ust kata tekrar konabilmeli");
                Check(!lv.CanPlace(lowProp, at, 0, 100, 0), "Alt kat hala dolu olmali");

                lv.Release(lowProp, at, 0, 100, 0);
                Check(lv.State(at.x, at.y) == CellState.Free, "Son kat da gidince hucre zemine donmeli");
                Check(lv.LevelsAt(at.x, at.y) == 0, "Son kat gidince maske sifirlanmali");

                // Uzun prop butun katlarini tutmali: 2.2 m'lik duvarin ORTASINA raf konamaz.
                var tallAt = new Vector2Int(8, 8);
                lv.Occupy(tallProp2, tallAt, 0, 100, 0);
                Check(!lv.CanPlace(lowProp, tallAt, 0, 100, 3), "2.2 m duvarin ICINE prop konmamali");
                Check(lv.CanPlace(lowProp, tallAt, 0, 100, 5), "Duvarin USTUNE prop konabilmeli");

                // Tavani asan yerlesim reddedilmeli — maske bir byte.
                Check(!lv.CanPlace(tallProp2, new Vector2Int(12, 12), 0, 100, (byte)(RoomGrid.MaxLevels - 2)),
                    "Kat maskesinin ustune tasan yerlesim reddedilmeli");
            }

            // --- DUVAR HEDEFLEME ---
            // 4x4 m odanin dort duvari, normalleri ICERI bakiyor.
            var wallPlan = new RoomPlan
            {
                floorY = 0f,
                ceilingY = 2.5f,
                floorPolygon = square,
                furniture = new RoomBox[0],
                walls = new[]
                {
                    new RoomWall { center = new Vector3(2f, 1.25f, 0f), normal = Vector3.forward, width = 4f, height = 2.5f },
                    new RoomWall { center = new Vector3(2f, 1.25f, 4f), normal = Vector3.back,    width = 4f, height = 2.5f },
                    new RoomWall { center = new Vector3(0f, 1.25f, 2f), normal = Vector3.right,   width = 4f, height = 2.5f },
                    new RoomWall { center = new Vector3(4f, 1.25f, 2f), normal = Vector3.left,    width = 4f, height = 2.5f },
                },
            };
            var roomMid = new Vector3(2f, 1.2f, 2f);

            // Her duvara nisan al: vurmali, ve normali o duvarinki olmali.
            for (int i = 0; i < wallPlan.walls.Length; i++)
            {
                var w = wallPlan.walls[i];
                var dir = (new Vector3(w.center.x, 1.2f, w.center.z) - roomMid).normalized;
                bool wallOk = ConstructorPlacer.TryWallHit(new Ray(roomMid, dir), wallPlan, 0f,
                    out Vector3 p, out Vector3 f, out float d);
                Check(wallOk, $"Duvar {i}: dogrudan nisan alinca vurmali");
                Check(wallOk && Vector3.Dot(f, w.normal) > 0.99f, $"Duvar {i}: donen normal o duvarinki olmali");
                Check(wallOk && d > 0f && d < 3f, $"Duvar {i}: mesafe makul olmali ({d:0.00} m)");
            }

            // ASAGI bakinca duvar ISKALAMALI — zemin duzlemi sonsuz, duvar sinirli.
            Check(!ConstructorPlacer.TryWallHit(new Ray(roomMid, Vector3.down), wallPlan, 0f, out _, out _, out _),
                "Asagi bakan isin duvara carpmamali");

            // Duvarin GENISLIGININ disina denk gelen kesisim sayilmamali, yoksa odanin
            // kosesinden disari nisan almak hayaleti olmayan bir duvara yapistirirdi.
            var narrow = new RoomPlan
            {
                floorY = 0f, ceilingY = 2.5f, floorPolygon = square, furniture = new RoomBox[0],
                walls = new[] { new RoomWall { center = new Vector3(2f, 1.25f, 0f), normal = Vector3.forward, width = 0.4f, height = 2.5f } },
            };
            Check(!ConstructorPlacer.TryWallHit(new Ray(new Vector3(3.5f, 1.2f, 2f), Vector3.back), narrow, 0f, out _, out _, out _),
                "Duvarin genisliginin disindaki kesisim sayilmamali");
            Check(ConstructorPlacer.TryWallHit(new Ray(new Vector3(2f, 1.2f, 2f), Vector3.back), narrow, 0f, out _, out _, out _),
                "Duvarin genisligi icindeki kesisim sayilmali");

            // Duvarin USTUNDEN gecen isin de sayilmamali.
            Check(!ConstructorPlacer.TryWallHit(new Ray(new Vector3(2f, 4f, 2f), Vector3.back), wallPlan, 0f, out _, out _, out _),
                "Duvarin ustunden gecen isin vurmamali");

            // --- mobilya engelleme ---
            var withCouch = new RoomPlan
            {
                floorY = 0f,
                ceilingY = 2.5f,
                floorPolygon = square,
                walls = new RoomWall[0],
                furniture = new[]
                {
                    new RoomBox
                    {
                        label = "Couch",
                        center = new Vector3(2f, 0.4f, 2f),
                        rotation = Quaternion.identity,
                        size = new Vector3(1f, 0.8f, 1f),
                    },
                },
            };
            var blocked = RoomGrid.FromPlan(withCouch, 0.25f, 0f, 0f);
            Check(blocked != null && blocked.State(8, 8) == CellState.Blocked,
                "Mobilyanin altindaki hucre Blocked olmali");
            Check(blocked != null && blocked.State(1, 1) == CellState.Free,
                "Mobilyadan uzak hucre etkilenmemeli");
            // Mobilya hucresini "bosaltmak" onu serbest birakmamali (Release taban durumu yukler)
            if (blocked != null)
            {
                blocked.ReleaseCells(new RectInt(8, 8, 1, 1));
                Check(blocked.State(8, 8) == CellState.Blocked,
                    "Release mobilya hucresini serbest birakmamali");

                // Zemin uygun degilse KAT FARK ETMEZ. Gercek mobilyanin USTUNE prop koymak,
                // oyuncuya oraya ulasmak icin masaya girmesi gereken bir hedef vermek olurdu.
                Check(!blocked.CanPlace(lowProp, new Vector2Int(8, 8), 0, 100, 4),
                    "Mobilya hucresinin USTUNE de prop konmamali");
            }

            // --- oda DISINA insa ---
            // Ad 'wide' DEGIL: yukarida ayak izi testinde ayni adda bir PropDef var.
            var outer = RoomGrid.FromPlan(plan, 0.25f, 0f, 2f);   // 2 m disari pay
            Check(outer != null && outer.Cols == 32 && outer.Rows == 32,
                "Oda disi pay izgarayi her yonde 8 hucre buyutmeli (16 -> 32)");
            if (outer != null)
            {
                // (0,0) artik poligonun 2 m disinda
                Check(outer.State(0, 0) == CellState.FreeOutside, "Oda disi hucre FreeOutside olmali");
                Check(outer.CanPlaceCells(new RectInt(0, 0, 2, 2)), "Oda disina prop KONABILMELI");
                Check(outer.State(16, 16) == CellState.Free, "Oda merkezi hala Free olmali");

                // Oda disina konan prop silinince FreeOutside'a donmeli, Free'ye DEGIL
                var outRect = new RectInt(1, 1, 2, 1);
                outer.OccupyCells(outRect);
                Check(outer.State(1, 1) == CellState.Occupied, "Oda disi hucre dolabilmeli");
                outer.ReleaseCells(outRect);
                Check(outer.State(1, 1) == CellState.FreeOutside,
                    "Release oda disi hucreyi FreeOutside'a geri koymali (Free'ye degil)");

                var st = outer.Report();
                Check(st.roomArea > 0f && st.buildableArea > st.roomArea,
                    "Insa alani oda alanindan buyuk olmali");

                // --- serbest katman sinir koruması: harita hacmi disina kacan prop kaybolmasin ---
                Vector3 inside = outer.CellCenter(16, 16);
                Check((outer.ClampToBounds(inside) - inside).magnitude < 0.0001f,
                    "ClampToBounds: harita icindeki nokta DEGISMEMELI");

                var far = outer.ClampToBounds(new Vector3(9999f, 0f, -9999f));
                Check(far.x < 9999f && far.z > -9999f,
                    "ClampToBounds: uzaga yazilan konum haritaya cekilmeli");
                Check(far.x <= outer.Origin.x + outer.Cols * outer.CellSize + 1f &&
                      far.z >= outer.Origin.y - 1f,
                    "ClampToBounds: kirpma izgara sinirlarinda kalmali");

                Check(Mathf.Abs(outer.ClampToBounds(new Vector3(0f, 500f, 0f)).y -
                                (outer.FloorY + RoomGrid.FreeHeightAboveFloor)) < 0.0001f,
                    "ClampToBounds: yukari tavan uygulanmali");
                Check(Mathf.Abs(outer.ClampToBounds(new Vector3(0f, -500f, 0f)).y -
                                (outer.FloorY - RoomGrid.FreeDepthBelowFloor)) < 0.0001f,
                    "ClampToBounds: asagi taban uygulanmali");
                // Yari gomme bu katmanin VAROLUS sebebi — birkac mm asla kirpilmamali.
                float sunk = outer.FloorY - 0.006f;
                Check(Mathf.Abs(outer.ClampToBounds(new Vector3(0f, sunk, 0f)).y - sunk) < 0.0001f,
                    "ClampToBounds: 6 mm gomulu prop kirpilmamali");
            }

            // --- JSON gidis donus ---
            var layout = new MapLayout { name = "SelfCheck", cellSize = 0.25f, builtForRoom = plan };
            layout.Add("prop_a", 3, 4, 1, 18);   // ceyrek tur (18 adim x 5 derece = 90)
            layout.Add("prop_b", 7, 8, 0, 0);
            var round = MapLayout.FromJson(layout.ToJson());
            Check(round != null, "MapLayout JSON: geri okunamadi");
            if (round != null)
            {
                Check(round.Count == 2, "MapLayout JSON: prop sayisi korunmali");
                Check(round.props[0].propId == "prop_a" && round.props[0].cellX == 3 &&
                      round.props[0].cellZ == 4 && round.props[0].level == 1 && round.props[0].rot == 18,
                      "MapLayout JSON: yerlestirme alanlari korunmali");
                Check(Mathf.Abs(round.props[0].Yaw - 90f) < 0.001f, "PlacedProp.Yaw: 18 adim = 90 derece olmali");
                Check(round.props[1].instanceId != round.props[0].instanceId,
                    "instanceId: her yerlestirme benzersiz olmali");
                Check(round.builtForRoom.floorPolygon.Length == 4, "MapLayout JSON: gomulu oda plani korunmali");
            }

            // --- kimlikle silme ---
            uint id0 = layout.props[0].instanceId;
            Check(layout.Remove(id0), "Remove: var olan kimlik silinebilmeli");
            Check(layout.Count == 1, "Remove: liste kisalmali");
            Check(!layout.Remove(id0), "Remove: olmayan kimlik false donmeli");

            // --- dosya adi temizligi ---
            Check(MapLayout.Sanitize("a/b:c*d") == "a_b_c_d", "Sanitize: gecersiz karakterler temizlenmeli");

            // --- imlec isini (zemin duzlemi kesisimi) ---
            Vector3 hit;
            Check(ConstructorPlacer.TryFloorHit(new Ray(new Vector3(2f, 1.5f, 3f), Vector3.down), 0f, out hit) &&
                  Mathf.Abs(hit.x - 2f) < 0.001f && Mathf.Abs(hit.y) < 0.001f && Mathf.Abs(hit.z - 3f) < 0.001f,
                  "TryFloorHit: dik asagi isin tam altina dusmeli");
            Check(!ConstructorPlacer.TryFloorHit(new Ray(new Vector3(0f, 1.5f, 0f), Vector3.up), 0f, out hit),
                  "TryFloorHit: yukari bakan isin zemine carpmamali");
            Check(!ConstructorPlacer.TryFloorHit(new Ray(new Vector3(0f, 1.5f, 0f), Vector3.forward), 0f, out hit),
                  "TryFloorHit: zemine paralel isin carpmamali");
            // 45 derece asagi/ileri: 1 m yukseklikten 1 m ileri duser
            Check(ConstructorPlacer.TryFloorHit(
                      new Ray(new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 1f).normalized), 0f, out hit) &&
                  Mathf.Abs(hit.z - 1f) < 0.001f,
                  "TryFloorHit: 45 derecelik isin 1 m ileri dusmeli");
            Check(ConstructorPlacer.TryFloorHit(new Ray(new Vector3(0f, 3f, 0f), Vector3.down), 1.2f, out hit) &&
                  Mathf.Abs(hit.y - 1.2f) < 0.001f,
                  "TryFloorHit: sifir olmayan zemin yuksekligi onurlanmali");

            // --- aci duzeltmesi (dunya yataylıgina gore egme) ---
            Check(ConstructorPlacer.PitchRay(Vector3.forward, 0f) == Vector3.forward,
                  "PitchRay: 0 derece yonu degistirmemeli");
            var lifted = ConstructorPlacer.PitchRay(Vector3.forward, 45f);
            Check(lifted.y > 0.6f && lifted.y < 0.75f, "PitchRay: +45 derece isini KALDIRMALI");
            var lowered = ConstructorPlacer.PitchRay(Vector3.forward, -45f);
            Check(lowered.y < -0.6f, "PitchRay: negatif aci isini INDIRMELI");
            // Yana kaçma olmamali: yatay bilesenin YONU korunmali (asil hata buydu — kumandanin
            // kendi ekseni etrafinda dondurunce bilek yatinca isin yana savruluyordu).
            var diag = new Vector3(1f, -0.3f, 1f).normalized;
            var pitched = ConstructorPlacer.PitchRay(diag, 20f);
            var h0 = new Vector3(diag.x, 0f, diag.z).normalized;
            var h1 = new Vector3(pitched.x, 0f, pitched.z).normalized;
            Check(Vector3.Dot(h0, h1) > 0.9999f, "PitchRay: yatay yon degismemeli (yana kacmamali)");
            // Kumanda nasil YATIRILIRSA yatirilsin ayni yone bakan iki isin ayni sonucu vermeli
            Check((ConstructorPlacer.PitchRay(Vector3.forward, 30f) -
                   ConstructorPlacer.PitchRay(Vector3.forward, 30f)).sqrMagnitude < 1e-8f,
                  "PitchRay: deterministik olmali");
            Check(ConstructorPlacer.PitchRay(Vector3.down, 30f) == Vector3.down,
                  "PitchRay: tam dikey isin degismemeli (egilecek eksen yok)");

            // --- kutuphane ---
            var lib = AssetDatabase.LoadAssetAtPath<PropLibrary>(LibraryPath);
            if (lib != null)
            {
                var problems = lib.Validate();
                Check(problems.Count == 0, "PropLibrary.Validate: " + string.Join(" | ", problems));
                if (lib.Count > 0)
                {
                    Check(lib.IndexOf(lib.props[0].id) == 0, "PropLibrary.IndexOf: ilk propun indeksi 0 olmali");
                    Check(lib.IndexOf("bugunlukbukadar_yok") == -1, "PropLibrary.IndexOf: bilinmeyen kimlik -1 donmeli");
                }
            }

            // --- katalog: havuza girme sarti (diske dokunmadan) ---
            //
            // Bu kural oyunu bozabilecek turden: dogum bolgesi olmayan bir harita havuza
            // girerse mac baslar ama oyuncunun yuruyecegi takim alani olmaz — ve hata haritayi
            // yapana degil, o haritaya denk gelen oyuncuya cikar.
            string engel;
            Check(!MapCatalog.CanEnterPool(new MapLayout(), out engel),
                  "CanEnterPool: BOS harita havuza girmemeli");
            Check(!string.IsNullOrEmpty(engel),
                  "CanEnterPool: reddederken sebep yazmali (oyuncuya gosterilecek)");

            var tekTakim = new MapLayout();
            tekTakim.Add("spawn_a", 10, 10, 0, 0, 100, 100);
            Check(!MapCatalog.CanEnterPool(tekTakim, out _),
                  "CanEnterPool: TEK takimin dogum bolgesi yetmemeli");

            var ikiTakim = new MapLayout();
            ikiTakim.Add("spawn_a", 10, 10, 0, 0, 100, 100);
            ikiTakim.Add("spawn_b", 20, 20, 0, 0, 100, 100);
            Check(MapCatalog.CanEnterPool(ikiTakim, out _),
                  "CanEnterPool: iki takimin dogum bolgesi varsa GIREBILMELI");

            // Ad = dosya adi, ve Sanitize bosluklari '_' yapiyor: "A B" ile "A_B" AYNI dosyaya
            // duser. Katalogun cakisma kontrolu bu gercege dayaniyor — degisirse orasi sessizce
            // yanlis calisir, bu yuzden varsayim burada kilitli.
            Check(MapLayout.Sanitize("A B") == MapLayout.Sanitize("A_B"),
                  "Sanitize: 'A B' ile 'A_B' ayni dosya adina dusmeli (katalog cakisma kontrolu buna dayaniyor)");

            string result = fails.Count == 0
                ? $"TUM DENETIMLER GECTI  ({checks} kontrol)"
                : $"{fails.Count}/{checks} DENETIM BASARISIZ:\n\n - " + string.Join("\n - ", fails);

            if (fails.Count == 0) Debug.Log("[Constructor] " + result);
            else Debug.LogError("[Constructor] " + result);

            return result;
        }

        // ------------------------------------------------------------- helpers

        static RoomPlan LoadPlan(out string error)
        {
            error = null;
            string full = Path.GetFullPath(PlanPath);
            if (!File.Exists(full))
            {
                error = "Oda plani bulunamadi:\n" + PlanPath + "\n\n" +
                        "Once gozlukte kalibre olup SOL X ile taramayi PC'ye gonder.";
                return null;
            }
            var plan = JsonUtility.FromJson<RoomPlan>(File.ReadAllText(full));
            if (plan == null || plan.floorPolygon == null || plan.floorPolygon.Length < 3)
            {
                error = "Oda plani bozuk (zemin poligonu yok).";
                return null;
            }
            return plan;
        }

        static RoomGrid LoadGrid(out string error)
        {
            var plan = LoadPlan(out error);
            if (plan == null) return null;

            var grid = RoomGrid.FromPlan(plan);
            if (grid == null) error = "Izgara kurulamadi — oda plani gecersiz.";
            return grid;
        }
    }
}
