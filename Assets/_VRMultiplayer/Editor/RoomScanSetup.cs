using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Yol 2 (Quest Space Setup -> Scene API -> PC) tooling:
    ///   11. Setup Room Scan      — OpenXR features, AR managers on the rig, prefab wiring
    ///   12. Import Room Plan     — draws the scanned room as a design template in the scene
    ///   13. Build Walls From Plan — LOKIT bush walls + colliders along the room boundary
    /// </summary>
    public static class RoomScanSetup
    {
        const string PrefabPath = "Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab";
        const string PlanPath = "Assets/_VRMultiplayer/RoomPlans/RoomPlan.json";
        const string TemplateName = "RoomPlanTemplate";
        const string MapRootName = "RoomMap";

        // ------------------------------------------------------------- menu 11
        [MenuItem("Tools/VR Multiplayer/11. Setup Room Scan (Yol 2)")]
        public static void SetupRoomScan()
        {
            // 1) OpenXR features (Android): Session + Planes + Bounding Boxes from the
            //    com.unity.xr.meta-openxr package.
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            int enabled = 0;
            if (settings != null)
            {
                foreach (var f in settings.GetFeatures<OpenXRFeature>())
                {
                    string tn = f.GetType().FullName ?? "";
                    if (!tn.Contains("Features.Meta")) continue;
                    if (tn.Contains("Session") || tn.Contains("Plane") || tn.Contains("BoundingBox"))
                    {
                        if (!f.enabled) { f.enabled = true; enabled++; }
                        EditorUtility.SetDirty(f);
                    }
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            // 2) Scene: XROrigin + AR managers on the existing XR rig, plus an ARSession.
            var rigRef = Object.FindFirstObjectByType<XRRigReference>();
            if (rigRef == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Sahnede XR Rig yok. Once menu 2 (Setup Current Scene) calistir.", "Tamam");
                return;
            }
            var rigGo = rigRef.gameObject;

            var origin = rigGo.GetComponent<XROrigin>();
            if (origin == null) origin = rigGo.AddComponent<XROrigin>();
            origin.Camera = rigRef.head != null ? rigRef.head.GetComponent<Camera>() : null;
            origin.CameraFloorOffsetObject = rigGo;
            // Floor mode + zero offset: if the session ever falls back to eye-level (Device)
            // tracking, XROrigin would otherwise push the rig up by its CameraYOffset
            // (~1.12 m) and the player would float above the ground.
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 0f;

            if (rigGo.GetComponent<ARPlaneManager>() == null) rigGo.AddComponent<ARPlaneManager>();
            if (rigGo.GetComponent<ARBoundingBoxManager>() == null) rigGo.AddComponent<ARBoundingBoxManager>();

            if (Object.FindFirstObjectByType<ARSession>() == null)
                new GameObject("AR Session").AddComponent<ARSession>();

            // 3) Player prefab: RoomScanSync (owner presses X to send the room to the PC).
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                if (root.GetComponent<RoomScanSync>() == null)
                {
                    root.AddComponent<RoomScanSync>();
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Oda tarama kurulumu tamam (" + enabled + " OpenXR ozelligi acildi).\n\n" +
                "AKIS:\n" +
                "1) Gozlukte bir kez Ayarlar > Fiziksel Alan > Alan Kurulumu yap.\n" +
                "2) Oyunda: katil -> takim sec -> KALIBRE OL.\n" +
                "3) SOL kumandada X tusuna bas -> 'uzamsal veri' iznini onayla.\n" +
                "4) PC konsolunda 'ODA PLANI KAYDEDILDI' gorunce menu 12'yi calistir.\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve gozluklere YENIDEN build al.", "Tamam");
        }

        // ------------------------------------------------------------- menu 12
        [MenuItem("Tools/VR Multiplayer/12. Import Room Plan")]
        public static void ImportRoomPlan()
        {
            var plan = LoadPlan();
            if (plan == null) return;

            var old = GameObject.Find(TemplateName);
            if (old != null) Object.DestroyImmediate(old);

            var tpl = new GameObject(TemplateName) { tag = "EditorOnly" }; // stripped from builds

            // Floor footprint (semi-transparent green).
            var floorMesh = TriangulatePolygon(plan.floorPolygon, plan.floorY + 0.01f);
            if (floorMesh != null)
            {
                var floor = new GameObject("Floor Template");
                floor.transform.SetParent(tpl.transform, false);
                floor.AddComponent<MeshFilter>().sharedMesh = floorMesh;
                floor.AddComponent<MeshRenderer>().sharedMaterial =
                    TransparentMat(new Color(0.2f, 0.9f, 0.3f, 0.25f));
            }

            // Boundary outline (yellow line).
            var lineGo = new GameObject("Boundary Line");
            lineGo.transform.SetParent(tpl.transform, false);
            var line = lineGo.AddComponent<LineRenderer>();
            line.loop = true;
            line.widthMultiplier = 0.03f;
            line.useWorldSpace = true;
            line.positionCount = plan.floorPolygon.Length;
            for (int i = 0; i < plan.floorPolygon.Length; i++)
                line.SetPosition(i, new Vector3(plan.floorPolygon[i].x, plan.floorY + 0.03f, plan.floorPolygon[i].y));
            var lineMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            lineMat.SetColor("_BaseColor", Color.yellow);
            line.sharedMaterial = lineMat;

            // Wall planes (semi-transparent blue, double sided).
            var wallMat = TransparentMat(new Color(0.25f, 0.5f, 1f, 0.22f));
            for (int i = 0; i < plan.walls.Length; i++)
            {
                var w = plan.walls[i];
                var quad = new GameObject("Wall " + (i + 1));
                quad.transform.SetParent(tpl.transform, false);
                quad.transform.position = w.center;
                Vector3 n = w.normal; n.y = 0f;
                if (n.sqrMagnitude < 0.01f) n = Vector3.forward;
                quad.transform.rotation = Quaternion.LookRotation(n.normalized);
                quad.AddComponent<MeshFilter>().sharedMesh = DoubleSidedQuad(w.width, w.height);
                quad.AddComponent<MeshRenderer>().sharedMaterial = wallMat;
            }

            // Furniture boxes (semi-transparent orange + label).
            var boxMat = TransparentMat(new Color(1f, 0.6f, 0.15f, 0.3f));
            foreach (var b in plan.furniture)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(box.GetComponent<Collider>());
                box.name = "Esya: " + b.label;
                box.transform.SetParent(tpl.transform, false);
                box.transform.SetPositionAndRotation(b.center, b.rotation);
                box.transform.localScale = b.size;
                box.GetComponent<MeshRenderer>().sharedMaterial = boxMat;

                var label = new GameObject("Label");
                label.transform.SetParent(tpl.transform, false);
                label.transform.position = b.center + Vector3.up * (b.size.y * 0.5f + 0.15f);
                var tm = label.AddComponent<TextMesh>();
                tm.text = b.label;
                tm.characterSize = 0.05f;
                tm.fontSize = 48;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = Color.white;
            }

            // A/B calibration markers so you always know where the tape points map to.
            Marker(tpl.transform, "A noktasi (origin)", Vector3.zero + Vector3.up * 0.02f, Color.red);
            Marker(tpl.transform, "B yonu (+Z)", new Vector3(0f, 0.02f, 1f), new Color(0.3f, 0.6f, 1f));

            // The root you hand-design under (this one DOES ship in the build).
            if (GameObject.Find(MapRootName) == null) new GameObject(MapRootName);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = tpl;
            SceneView.lastActiveSceneView?.FrameSelected();

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Oda plani sahneye cizildi (" + plan.floorPolygon.Length + " kose, " +
                Mathf.Abs(plan.Area()).ToString("0.0") + " m2, " + plan.walls.Length + " duvar, " +
                plan.furniture.Length + " esya — " + plan.capturedAt + ").\n\n" +
                "• Yesil taban + sari cizgi = odanin sinirlari\n" +
                "• Mavi levhalar = gercek duvarlar, turuncu kutular = esyalar\n" +
                "• Bu sablon 'EditorOnly' — build'e girmez, sadece tasarim referansi\n\n" +
                "Simdi menu 13 ile duvarlari uret, sonra '" + MapRootName + "' altina\n" +
                "LOKIT dekorlarini elle yerlestir. Bitince Ctrl+S + build.", "Tamam");
        }

        // ------------------------------------------------------------- menu 13
        [MenuItem("Tools/VR Multiplayer/13. Build Walls From Plan")]
        public static void BuildWallsFromPlan()
        {
            var plan = LoadPlan();
            if (plan == null) return;
            if (plan.floorPolygon.Length < 3)
            {
                EditorUtility.DisplayDialog("VR Multiplayer", "Plandaki zemin poligonu gecersiz.", "Tamam");
                return;
            }

            string[] bushPaths =
            {
                "Assets/Standout7/LOKIT_Forest/Prefabs/Vegetation/Bush_01.prefab",
                "Assets/Standout7/LOKIT_Forest/Prefabs/Vegetation/Bush_02.prefab",
                "Assets/Standout7/LOKIT_Forest/Prefabs/Vegetation/Bush_03.prefab",
            };
            var bushes = new List<GameObject>();
            foreach (var p in bushPaths)
            {
                var b = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (b != null) bushes.Add(b);
            }
            if (bushes.Count == 0)
            {
                EditorUtility.DisplayDialog("VR Multiplayer", "LOKIT Bush prefablari bulunamadi.", "Tamam");
                return;
            }

            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null) mapRoot = new GameObject(MapRootName);
            var oldWalls = mapRoot.transform.Find("Walls");
            if (oldWalls != null) Object.DestroyImmediate(oldWalls.gameObject);
            var walls = new GameObject("Walls");
            walls.transform.SetParent(mapRoot.transform, false);

            int bushCount = 0;
            var pts = plan.floorPolygon;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 a = new Vector3(pts[i].x, plan.floorY, pts[i].y);
                Vector3 b = new Vector3(pts[(i + 1) % pts.Length].x, plan.floorY, pts[(i + 1) % pts.Length].y);
                float len = Vector3.Distance(a, b);
                if (len < 0.05f) continue;
                Vector3 dir = (b - a) / len;

                // Invisible physics wall so thrown rocks stay inside the arena.
                var col = new GameObject("WallCollider " + (i + 1));
                col.transform.SetParent(walls.transform, false);
                col.transform.position = (a + b) * 0.5f + Vector3.up * 1.25f;
                col.transform.rotation = Quaternion.LookRotation(dir);
                var box = col.AddComponent<BoxCollider>();
                box.size = new Vector3(0.2f, 2.5f, len);

                // Dense bush row along the edge (deterministic variation, no randomness).
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 0.55f));
                for (int k = 0; k < n; k++)
                {
                    float t = (k + 0.5f) / n;
                    var prefab = bushes[(i * 7 + k) % bushes.Count];
                    var bush = (GameObject)PrefabUtility.InstantiatePrefab(prefab, walls.transform);
                    float wobble = Mathf.Sin((i * 13 + k * 7) * 1.7f);
                    bush.transform.position = Vector3.Lerp(a, b, t)
                        + Quaternion.Euler(0f, 90f, 0f) * dir * (wobble * 0.06f);
                    bush.transform.rotation = Quaternion.Euler(0f, (i * 53 + k * 91) % 360, 0f);
                    bush.transform.localScale *= 1.05f + 0.3f * Mathf.Abs(Mathf.Sin((i + k) * 2.3f));
                    bushCount++;
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Sinir duvarlari uretildi: " + bushCount + " cali + " + pts.Length + " gorunmez collider\n" +
                "(hepsi " + MapRootName + "/Walls altinda — begenmedigini sil/tasi).\n\n" +
                "Icini elle dekore et, Ctrl+S ile kaydet ve gozluklere build al.", "Tamam");
        }

        // ------------------------------------------------------------- menu 14
        // Solid, room-matching geometry that SHIPS in the build: flat walls along the boundary
        // (inner face on the real wall line) and a stand-in box for each scanned furniture item
        // (real table = virtual table, so colocated players can even rest rocks on it).
        [MenuItem("Tools/VR Multiplayer/14. Build Room Walls + Furniture")]
        public static void BuildSolidRoom()
        {
            var plan = LoadPlan();
            if (plan == null) return;

            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null) mapRoot = new GameObject(MapRootName);

            var wallMat = SavedMat("RoomWall", new Color(0.55f, 0.5f, 0.42f));      // warm stone
            var floorMat = SavedMat("RoomFloor", new Color(0.42f, 0.62f, 0.38f));   // grass tone
            var tableMat = SavedMat("RoomTable", new Color(0.52f, 0.36f, 0.2f));    // wood
            var sofaMat = SavedMat("RoomSofa", new Color(0.3f, 0.42f, 0.32f));      // muted green
            var stuffMat = SavedMat("RoomStuff", new Color(0.45f, 0.45f, 0.5f));    // gray

            // ---- walls along the boundary polygon ----
            var oldWalls = mapRoot.transform.Find("Walls");
            if (oldWalls != null) Object.DestroyImmediate(oldWalls.gameObject);
            var walls = new GameObject("Walls");
            walls.transform.SetParent(mapRoot.transform, false);

            var pts = plan.floorPolygon;
            float wallH = Mathf.Clamp(plan.ceilingY - plan.floorY, 2f, 3.2f);
            const float thickness = 0.12f;
            const float doorWidth = 1.1f;   // real door ~0.9 m + margin
            const float doorHeight = 2.05f;

            // The A/B tape points sit at the real door, and A = shared origin (0,0). Find the
            // boundary edge closest to the origin — that's where the exit opening goes.
            int doorEdge = -1;
            float doorAt = 0f, bestDoorDist = float.MaxValue;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a2 = pts[i], b2 = pts[(i + 1) % pts.Length];
                Vector2 e = b2 - a2;
                float len = e.magnitude;
                if (len < doorWidth + 0.3f) continue; // edge too short to host a door
                float t = Mathf.Clamp(Vector2.Dot(-a2, e / len), 0f, len); // project origin
                float d = (a2 + e / len * t).magnitude;
                if (d < bestDoorDist) { bestDoorDist = d; doorEdge = i; doorAt = t; }
            }

            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a2 = pts[i], b2 = pts[(i + 1) % pts.Length];
                float len = Vector2.Distance(a2, b2);
                if (len < 0.05f) continue;

                Vector2 dir2 = (b2 - a2) / len;
                Vector2 n2 = new Vector2(-dir2.y, dir2.x);
                // Make sure the normal points OUT of the room, so the wall's inner face sits
                // exactly on the real wall line.
                Vector2 mid2 = (a2 + b2) * 0.5f;
                if (PointInPolygon(mid2 + n2 * 0.1f, pts)) n2 = -n2;

                if (i != doorEdge)
                {
                    WallBox(walls.transform, "Duvar " + (i + 1), a2, b2, n2,
                        plan.floorY, wallH, thickness, len + thickness, wallMat);
                    continue;
                }

                // Door edge: left piece + right piece + lintel above the opening.
                float t0 = Mathf.Clamp(doorAt - doorWidth * 0.5f, 0f, len - doorWidth);
                float t1 = t0 + doorWidth;

                if (t0 > 0.15f)
                    WallBox(walls.transform, "Duvar " + (i + 1) + " (kapi solu)",
                        a2, a2 + dir2 * t0, n2, plan.floorY, wallH, thickness, t0, wallMat);
                if (len - t1 > 0.15f)
                    WallBox(walls.transform, "Duvar " + (i + 1) + " (kapi sagi)",
                        a2 + dir2 * t1, b2, n2, plan.floorY, wallH, thickness, len - t1, wallMat);
                if (wallH - doorHeight > 0.05f)
                    WallBox(walls.transform, "Duvar " + (i + 1) + " (kapi ustu)",
                        a2 + dir2 * t0, a2 + dir2 * t1, n2,
                        plan.floorY + doorHeight, wallH - doorHeight, thickness, doorWidth, wallMat);

                // Green EXIT sign facing into the room, right above the opening.
                Vector2 doorMid2 = a2 + dir2 * ((t0 + t1) * 0.5f);
                var sign = new GameObject("CIKIS Tabelasi");
                sign.transform.SetParent(walls.transform, false);
                sign.transform.position = new Vector3(
                    doorMid2.x - n2.x * 0.10f,
                    plan.floorY + doorHeight - 0.18f,
                    doorMid2.y - n2.y * 0.10f);
                sign.transform.rotation = Quaternion.LookRotation(new Vector3(n2.x, 0f, n2.y));
                var tm = sign.AddComponent<TextMesh>();
                tm.text = "CIKIS";
                tm.characterSize = 0.04f;
                tm.fontSize = 64;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = new Color(0.2f, 1f, 0.35f);
                tm.fontStyle = FontStyle.Bold;
            }

            // ---- floor slab inside the polygon (its own mesh, saved as an asset so it
            //      survives scene reloads and ships in the build) ----
            var oldFloor = mapRoot.transform.Find("Zemin");
            if (oldFloor != null) Object.DestroyImmediate(oldFloor.gameObject);
            var floorMesh = TriangulatePolygon(pts, plan.floorY + 0.02f);
            if (floorMesh != null)
            {
                string meshPath = "Assets/_VRMultiplayer/RoomPlans/RoomFloorMesh.asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(floorMesh, meshPath);
                var floorGo = new GameObject("Zemin");
                floorGo.transform.SetParent(mapRoot.transform, false);
                floorGo.AddComponent<MeshFilter>().sharedMesh = floorMesh;
                floorGo.AddComponent<MeshRenderer>().sharedMaterial = floorMat;
                floorGo.AddComponent<MeshCollider>().sharedMesh = floorMesh;
            }

            // ---- furniture stand-ins from the scan ----
            var oldFurniture = mapRoot.transform.Find("Furniture");
            if (oldFurniture != null) Object.DestroyImmediate(oldFurniture.gameObject);
            var furniture = new GameObject("Furniture");
            furniture.transform.SetParent(mapRoot.transform, false);

            int tables = 0;
            foreach (var b in plan.furniture)
            {
                string label = b.label ?? "";
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.transform.SetParent(furniture.transform, false);
                box.transform.SetPositionAndRotation(b.center, b.rotation);
                box.transform.localScale = b.size;

                if (label.Contains("Table"))
                {
                    box.name = "Masa";
                    box.GetComponent<MeshRenderer>().sharedMaterial = tableMat;
                    tables++;
                }
                else if (label.Contains("Couch") || label.Contains("Seat"))
                {
                    box.name = "Koltuk";
                    box.GetComponent<MeshRenderer>().sharedMaterial = sofaMat;
                }
                else
                {
                    box.name = "Esya (" + label + ")";
                    box.GetComponent<MeshRenderer>().sharedMaterial = stuffMat;
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Oda insa edildi (RoomMap altinda, build'e girer):\n" +
                "• Oda zemini (cim tonu, collider'li)\n" +
                "• " + pts.Length + " duz duvar (" + wallH.ToString("0.0") + " m, ic yuzu gercek duvar hizasinda)\n" +
                (doorEdge >= 0
                    ? "• A/B noktasindaki gercek kapiya CIKIS boslugu acildi (1.1 m, yesil tabelali)\n"
                    : "• UYARI: kapi icin uygun kenar bulunamadi, cikis acilamadi\n") +
                "• " + plan.furniture.Length + " esya karsiligi (" + tables + " masa — ahsap, ustune tas konabilir)\n\n" +
                "Istedigini sil/boya/tasi, Ctrl+S ile kaydet ve 3 gozluge YENIDEN build al.", "Tamam");
        }

        static void WallBox(Transform parent, string name, Vector2 from2, Vector2 to2, Vector2 outN,
            float baseY, float height, float thickness, float boxLen, Material mat)
        {
            Vector2 mid2 = (from2 + to2) * 0.5f + outN * (thickness * 0.5f);
            Vector2 d2 = (to2 - from2).normalized;
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(parent, false);
            wall.transform.position = new Vector3(mid2.x, baseY + height * 0.5f, mid2.y);
            wall.transform.rotation = Quaternion.LookRotation(new Vector3(d2.x, 0f, d2.y));
            wall.transform.localScale = new Vector3(thickness, height, boxLen);
            wall.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static Material SavedMat(string name, Color color)
        {
            string dir = "Assets/_VRMultiplayer/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/_VRMultiplayer", "Materials");
            string path = dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                m.SetColor("_BaseColor", color);
                m.SetFloat("_Smoothness", 0.1f);
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------- helpers
        static RoomPlan LoadPlan()
        {
            string full = Path.GetFullPath(PlanPath);
            if (!File.Exists(full))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Oda plani bulunamadi:\n" + PlanPath + "\n\n" +
                    "Once gozlukte kalibre olup SOL X ile taramayi PC'ye gonder\n" +
                    "(sunucu Play modunda calisirken).", "Tamam");
                return null;
            }
            var plan = JsonUtility.FromJson<RoomPlan>(File.ReadAllText(full));
            if (plan == null || plan.floorPolygon == null || plan.floorPolygon.Length < 3)
            {
                EditorUtility.DisplayDialog("VR Multiplayer", "RoomPlan.json okunamadi ya da bozuk.", "Tamam");
                return null;
            }

            // Older scans could carry the headset's arbitrary vertical origin (calibration only
            // fixes the horizontal axes). The floor defines y=0 — normalize if it drifted.
            if (Mathf.Abs(plan.floorY) > 0.25f)
            {
                float yShift = -plan.floorY;
                plan.floorY = 0f;
                plan.ceilingY += yShift;
                foreach (var w in plan.walls) w.center.y += yShift;
                foreach (var f in plan.furniture) f.center.y += yShift;
                Debug.Log($"[RoomScan] Plan dikeyde {yShift:0.00} m normalize edildi (zemin -> 0).");
            }
            return plan;
        }

        static void Marker(Transform parent, string label, Vector3 pos, Color color)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(m.GetComponent<Collider>());
            m.name = label;
            m.transform.SetParent(parent, false);
            m.transform.position = pos;
            m.transform.localScale = Vector3.one * 0.09f;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", color);
            m.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var lg = new GameObject("Label");
            lg.transform.SetParent(parent, false);
            lg.transform.position = pos + Vector3.up * 0.2f;
            var tm = lg.AddComponent<TextMesh>();
            tm.text = label;
            tm.characterSize = 0.05f;
            tm.fontSize = 48;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = color;
        }

        static Material TransparentMat(Color color)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetFloat("_Surface", 1f); // transparent
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", color);
            return m;
        }

        // Ear-clipping triangulation of the (possibly concave) room polygon.
        static Mesh TriangulatePolygon(Vector2[] poly, float y)
        {
            if (poly == null || poly.Length < 3) return null;

            var idx = new List<int>();
            var pts = new List<Vector2>(poly);

            // Ensure counter-clockwise winding.
            float area = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                area += p.x * q.y - q.x * p.y;
            }
            if (area < 0f) pts.Reverse();

            var remaining = new List<int>();
            for (int i = 0; i < pts.Count; i++) remaining.Add(i);

            int guard = pts.Count * pts.Count + 10;
            while (remaining.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int i0 = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int i1 = remaining[i];
                    int i2 = remaining[(i + 1) % remaining.Count];
                    Vector2 a = pts[i0], b = pts[i1], c = pts[i2];

                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0f)
                        continue; // reflex corner, not an ear

                    bool contains = false;
                    foreach (int j in remaining)
                    {
                        if (j == i0 || j == i1 || j == i2) continue;
                        if (PointInTriangle(pts[j], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;

                    idx.Add(i0); idx.Add(i1); idx.Add(i2);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // degenerate polygon; fall through with what we have
            }
            if (remaining.Count == 3)
            {
                idx.Add(remaining[0]); idx.Add(remaining[1]); idx.Add(remaining[2]);
            }

            var verts = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) verts[i] = new Vector3(pts[i].x, y, pts[i].y);

            // Flip triangles so they face up (+Y).
            var tris = new int[idx.Count];
            for (int i = 0; i < idx.Count; i += 3)
            {
                tris[i] = idx[i];
                tris[i + 1] = idx[i + 2];
                tris[i + 2] = idx[i + 1];
            }

            var mesh = new Mesh { name = "RoomFloor" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b)
            => (a.x - p.x) * (b.y - p.y) - (a.y - p.y) * (b.x - p.x);

        static Mesh DoubleSidedQuad(float w, float h)
        {
            float x = w * 0.5f, y = h * 0.5f;
            var mesh = new Mesh { name = "WallQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-x, -y, 0), new Vector3(x, -y, 0),
                new Vector3(x, y, 0), new Vector3(-x, y, 0),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
