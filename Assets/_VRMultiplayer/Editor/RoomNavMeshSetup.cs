using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Odanin YURUNEBILIR ALANINI (NavMesh) bake eder. Dogum bolgesi rotasi bunun uzerinden
    /// hesaplanir (bkz. <see cref="UI.SpawnRouteGuide"/>), boylece rota duvarlarin ve
    /// esyalarin icinden gecmez.
    ///
    /// NEDEN NavMeshSurface DEGIL, DUSUK SEVIYELI API: NavMeshSurface olculeri projenin
    /// AJAN TIPI ayarindan alir; bu projede tek tip var ve yaricapi 0.5. Odalar arasi kapilar
    /// ise 1.10 m — 0.5 yaricapla gecilebilir koridor 1.10 - 2*0.5 = 0.10 m'ye duser ve
    /// voksellemede kapanir, iki oda BAGLANMAZ. Ayrica varsayilan tirmanma 0.75, bu da yolun
    /// masanin ustune cikmasina izin verirdi. Olculer burada acikca veriliyor.
    ///
    /// Bake, duvar kuran menulerin (13/14/17) SONUNA otomatik takilidir — oda degisince
    /// NavMesh'i yenilemek unutulabilecek bir adim olarak birakilmadi. Elle almak icin
    /// menu 23 var.
    /// </summary>
    public static class RoomNavMeshSetup
    {
        // Gercek bir insan icin: omuz yarim genisligi ~0.25 m. Kapilar 1.10 m oldugu icin
        // bu deger gecilebilir 0.60 m'lik koridor birakir.
        public const float AgentRadius = 0.25f;
        // Kapi bosluklarinin yuksekligi ~2.05 m; 2.0'lik ajan voksellemede takilabiliyor.
        public const float AgentHeight = 1.8f;
        // Varsayilan 0.75: yolun masanin/esyanin ustune tirmanmasina izin verirdi.
        public const float AgentClimb = 0.2f;
        public const float AgentSlope = 45f;

        const string MapRootName = "RoomMap";
        const string AssetPath = "Assets/_VRMultiplayer/RoomPlans/RoomNavMesh.asset";

        [MenuItem("Tools/VR Multiplayer/23. Bake Room NavMesh (dogum rotasi)")]
        public static void BakeMenu()
        {
            bool ok = Bake(out string report);
            EditorUtility.DisplayDialog("VR Multiplayer",
                report + (ok ? "\n\nSahneyi Ctrl+S ile KAYDET." : ""), "Tamam");
        }

        /// <summary>Odanin NavMesh'ini kurar, varlik olarak kaydeder ve RoomMap'e
        /// <see cref="RoomNavMesh"/> bileseni takar. Duvar kuran menuler bunu cagirir.</summary>
        public static bool Bake(out string report)
        {
            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null)
            {
                report = "NavMesh alinamadi: sahnede '" + MapRootName + "' yok.\n" +
                         "Once menu 13 ya da 14 ile odayi kur.";
                Debug.LogWarning("[RoomNavMesh] " + report);
                return false;
            }

            var colliders = mapRoot.GetComponentsInChildren<Collider>();
            if (colliders.Length == 0)
            {
                report = "NavMesh alinamadi: " + MapRootName + " altinda collider yok.";
                Debug.LogWarning("[RoomNavMesh] " + report);
                return false;
            }

            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = AgentRadius;
            settings.agentHeight = AgentHeight;
            settings.agentClimb = AgentClimb;
            settings.agentSlope = AgentSlope;
            // Voksel, ajan yaricapinin ~1/3'u olmali; varsayilan cozunurluk 1.10 m'lik kapiyi
            // guvenilir sekilde acmiyor.
            settings.overrideVoxelSize = true;
            settings.voxelSize = AgentRadius / 3f;

            // GORUNTU MESH'I DEGIL COLLIDER toplaniyor: oda gorsellerinde cift tarafli/ince
            // quad'lar ve dekor (cali) var; yurunebilirligi belirleyen sey fiziksel engel.
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(mapRoot.transform, ~0,
                NavMeshCollectGeometry.PhysicsColliders, 0,
                new List<NavMeshBuildMarkup>(), sources);

            var bounds = colliders[0].bounds;
            foreach (var c in colliders) bounds.Encapsulate(c.bounds);
            bounds.Expand(2f);   // kenarlarda kirpilma olmasin

            var data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);

            if (data == null)
            {
                report = "NavMesh uretilemedi (kaynak sayisi: " + sources.Count + ").";
                Debug.LogWarning("[RoomNavMesh] " + report);
                return false;
            }
            data.name = "RoomNavMesh";

            // Var olan varligin UZERINE yaz: GUID korunur, sahnedeki referans kopmaz.
            var existing = AssetDatabase.LoadAssetAtPath<NavMeshData>(AssetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(data, existing);
                data = existing;
                EditorUtility.SetDirty(data);
            }
            else
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AssetPath));
                AssetDatabase.CreateAsset(data, AssetPath);
            }
            AssetDatabase.SaveAssets();

            var comp = mapRoot.GetComponent<RoomNavMesh>();
            if (comp == null) comp = Undo.AddComponent<RoomNavMesh>(mapRoot);
            comp.data = data;
            EditorUtility.SetDirty(comp);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            report = "NavMesh alindi (" + sources.Count + " engel, ajan yaricapi " +
                     AgentRadius + " m).\n" + Verify(data);
            Debug.Log("[RoomNavMesh] " + report);
            return true;
        }

        /// <summary>
        /// Bake'in ISE YARAYIP YARAMADIGINI olcer: iki dogum bolgesi yurunebilir alanda mi ve
        /// aralarinda gercekten yol var mi?
        ///
        /// Bu kontrol bosuna degil — kapi voksellemede kapanirsa bake yine "basarili" doner
        /// ama iki oda AYRI ADALAR olur ve rota sessizce hic cizilmez. Hatayi bake aninda
        /// gormek, cihazda kesfetmekten iyidir.
        /// </summary>
        static string Verify(NavMeshData data)
        {
            var inst = NavMesh.AddNavMeshData(data);
            try
            {
                var zones = Object.FindObjectsByType<TeamSpawnZone>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (zones.Length < 2) return "UYARI: sahnede 2 dogum bolgesi yok, baglanti test edilemedi.";

                var pts = new Vector3[zones.Length];
                for (int i = 0; i < zones.Length; i++)
                {
                    if (!NavMesh.SamplePosition(zones[i].transform.position, out var hit, 2f, NavMesh.AllAreas))
                        return "UYARI: takim " + zones[i].team + " dogum bolgesi yurunebilir alanin " +
                               "DISINDA. Bolgeyi zeminin uzerine tasi.";
                    pts[i] = hit.position;
                }

                var path = new NavMeshPath();
                NavMesh.CalculatePath(pts[0], pts[1], NavMesh.AllAreas, path);
                if (path.status != NavMeshPathStatus.PathComplete)
                    return "UYARI: iki dogum bolgesi arasinda YOL YOK (" + path.status + ").\n" +
                           "Kapi bosluklari kapanmis olabilir — duvarlari kontrol et.";

                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++)
                    len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                return "Iki dogum bolgesi baglantili: " + path.corners.Length + " kose, " +
                       len.ToString("0.0") + " m.";
            }
            finally
            {
                if (inst.valid) inst.Remove();
            }
        }
    }
}
