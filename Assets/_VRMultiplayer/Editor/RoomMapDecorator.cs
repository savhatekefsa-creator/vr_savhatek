using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRMultiplayer.Constructor;
using VRMultiplayer.UI;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Menu 18: arenanin GORSEL katmani. Duvar TRANSFORMLARINA ASLA DOKUNMAZ — duvarlar
    /// gercek dunyanin sinirlari ve elle hizalandi. Bu arac yalnizca:
    ///   - zeminleri UV'li mesh + dokulu malzemeyle yeniden kurar (3 ciplak Plane dahil),
    ///   - duvar YUZEYINE boya/serit ekler (supurgelik, takim rengi bant, kapi cercevesi),
    ///   - oda tabelalari ve zemin isaretleri koyar,
    ///   - siper/pusu proplarini yerlestirir (RoomMap/Props),
    ///   - her seyi static isaretleyip NavMesh'i yeniden bake eder.
    ///
    /// IDEMPOTENT: tekrar calistirmak Decor/Props kapsayicilarini silip yeniden kurar.
    /// GERI ALMA: menu 18b her seyi kaldirip ozgun mesh/malzemeleri geri baglar.
    ///
    /// DIALOG YOK, BILEREK: arac otomasyondan (MCP) da kosuluyor; kip pencere editoru
    /// kilitler. Sonuc raporu konsola yazilir.
    /// </summary>
    public static class RoomMapDecorator
    {
        const string MapRootName = "RoomMap";
        const string DecorName = "Decor";
        const string PropsName = "Props";

        const string TexDir = "Assets/_VRMultiplayer/Textures/Decor";
        const string MatDir = "Assets/_VRMultiplayer/Materials/Decor";
        const string MeshDir = "Assets/_VRMultiplayer/RoomPlans/DecorMeshes";

        const string PaintballPrefabs = "Assets/P A I N T B A L L S E R I ES MK/Content/Prefabs";
        const string LokitPrefabs = "Assets/Standout7/LOKIT_Forest/Prefabs";
        const string ConstructorProps = "Assets/_VRMultiplayer/Resources/ConstructorProps";

        // RoomWall.mat'in ozgun gorunumu (18b geri yuklerken kullanir).
        static readonly Color OriginalWallColor = new Color(0.55f, 0.5f, 0.42f);

        // Takim renkleri: UITheme paletiyle ayni aile.
        static readonly Color TeamBlue = new Color(0.29f, 0.50f, 0.91f);
        static readonly Color TeamOrange = new Color(0.95f, 0.55f, 0.20f);

        // Ciplak Plane'lerin dunya taban dikdortgenleri — sahnede bulunamazlarsa yedek.
        // (olculdu: 2026-08-04, plan asamasi)
        static readonly Rect KoridorRect = Rect.MinMaxRect(-1.22f, -3.91f, -0.04f, 3.46f);
        static readonly Rect BatiRect = Rect.MinMaxRect(-3.67f, 0.44f, -1.19f, 2.71f);
        static readonly Rect GuneyRect = Rect.MinMaxRect(-2.41f, -6.33f, -0.08f, -3.84f);

        // ------------------------------------------------------------------ menu 18
        [MenuItem("Tools/VR Multiplayer/18. Haritayi Guzellestir (Dekor + Prop)")]
        public static void Decorate()
        {
            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null)
            {
                Debug.LogError("[Decor] Sahnede '" + MapRootName + "' yok — once menu 14 ile odayi kur.");
                return;
            }

            var decor = FreshChild(mapRoot.transform, DecorName);
            var props = FreshChild(mapRoot.transform, PropsName);

            EnsureFolder(TexDir);
            EnsureFolder(MatDir);
            EnsureFolder(MeshDir);

            var mats = BuildMaterials();
            int floors = RebuildFloors(mapRoot.transform, decor, mats);
            PaintWalls(mats);
            int trims = AddWallTrim(mapRoot.transform, decor, mats);
            int frames = AddDoorFrames(mapRoot.transform, decor, mats);
            int signs = AddSigns(decor);
            int paints = AddFloorPaint(decor, mats);
            int placed = PlaceProps(props, mats);

            MarkStatic(decor.gameObject);
            MarkStatic(props.gameObject);

            RoomNavMeshSetup.Bake(out string navReport);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Decor] TAMAM: " + floors + " zemin, " + trims + " serit, " + frames +
                      " kapi cercevesi, " + signs + " tabela, " + paints + " zemin isareti, " +
                      placed + " prop.\nNavMesh: " + navReport);
        }

        // ------------------------------------------------------------------ menu 18b
        [MenuItem("Tools/VR Multiplayer/18b. Dekoru Kaldir (geri al)")]
        public static void RemoveDecor()
        {
            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null) { Debug.LogWarning("[Decor] RoomMap yok."); return; }

            var decor = mapRoot.transform.Find(DecorName);
            if (decor != null) Object.DestroyImmediate(decor.gameObject);
            var props = mapRoot.transform.Find(PropsName);
            if (props != null) Object.DestroyImmediate(props.gameObject);

            // Zeminleri ozgun mesh + malzemeye geri bagla.
            var floorMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_VRMultiplayer/Materials/RoomFloor.mat");
            RestoreFloor(mapRoot.transform, "Zemin", "Assets/_VRMultiplayer/RoomPlans/RoomFloorMesh.asset", floorMat);
            RestoreFloor(mapRoot.transform, "Zemin2", "Assets/_VRMultiplayer/RoomPlans/RoomFloorMesh2.asset", floorMat);

            // Ciplak Plane'leri geri ac.
            foreach (string n in new[] { "Plane", "Plane (1)", "Plane (2)" })
            {
                var go = FindSceneObject(n);
                if (go != null) go.SetActive(true);
            }

            // Masa'nin ozgun malzemesi.
            var masa = FindSceneObject("Masa");
            var tableMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_VRMultiplayer/Materials/RoomTable.mat");
            if (masa != null && tableMat != null)
            {
                var mr = masa.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = tableMat;
            }

            // Duvar boyasini ozgun duz renge dondur.
            var wallMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_VRMultiplayer/Materials/RoomWall.mat");
            if (wallMat != null)
            {
                wallMat.SetTexture("_BaseMap", null);
                wallMat.SetColor("_BaseColor", OriginalWallColor);
                wallMat.SetFloat("_Smoothness", 0.1f);
                EditorUtility.SetDirty(wallMat);
            }

            AssetDatabase.SaveAssets();
            RoomNavMeshSetup.Bake(out string navReport);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Decor] Dekor kaldirildi, ozgun gorunum geri yuklendi.\nNavMesh: " + navReport);
        }

        // ================================================================== malzemeler

        class Mats
        {
            public Material floorSport, floorGrass, floorWood, floorMetal;
            public Material skirt, bandBlue, bandOrange, frame, crate;
            public Material paintYellow, paintBlue, paintOrange;
        }

        static Mats BuildMaterials()
        {
            var woodTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/P A I N T B A L L S E R I ES MK/Content/PaintBall_Props/Models/Wood_Props/Materials/Tex/Woods_DefaultMaterial_AlbedoTransparency.png");

            var m = new Mats
            {
                floorSport = LitMat("M_Decor_FloorSport", SportTexture(), new Color(1f, 1f, 1f), 0.5f, 0.15f),
                floorGrass = LitMat("M_Decor_FloorGrass", GrassTexture(), Color.white, 0.35f, 0.05f),
                floorWood = LitMat("M_Decor_FloorWood", woodTex, new Color(0.95f, 0.88f, 0.80f), 0.6f, 0.1f),
                floorMetal = LitMat("M_Decor_FloorMetal", MetalTexture(), Color.white, 0.5f, 0.35f),
                skirt = LitMat("M_Decor_Skirt", null, new Color(0.13f, 0.13f, 0.14f), 1f, 0.3f),
                bandBlue = BandMat("M_Decor_BandBlue", TeamBlue),
                bandOrange = BandMat("M_Decor_BandOrange", TeamOrange),
                frame = LitMat("M_Decor_Frame", null, new Color(0.16f, 0.15f, 0.14f), 1f, 0.25f),
                crate = LitMat("M_Decor_Crate", woodTex, new Color(0.85f, 0.78f, 0.68f), 1.2f, 0.1f),
                paintYellow = UnlitMat("M_Decor_PaintYellow", new Color(0.95f, 0.85f, 0.20f)),
                paintBlue = UnlitMat("M_Decor_PaintBlue", TeamBlue),
                paintOrange = UnlitMat("M_Decor_PaintOrange", TeamOrange),
            };
            AssetDatabase.SaveAssets();
            return m;
        }

        static Material LitMat(string name, Texture2D tex, Color tint, float tiling, float smooth)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", tint);
            m.SetTextureScale("_BaseMap", Vector2.one * tiling);
            m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material BandMat(string name, Color c)
        {
            var m = LitMat(name, null, c, 1f, 0.2f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", c * 0.35f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material UnlitMat(string name, Color c)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ================================================================== dokular
        // Tumu DETERMINISTIK (hash tabanli, Random yok) ve DIKISSIZ (kafes periyodu doku
        // boyunu tam boler). 512x512, sikistirmayi Unity importer halleder.

        static Texture2D SportTexture() => CachedTexture("T_Decor_SportFloor", () =>
            GenTexture((x, y) =>
            {
                float n = FractalNoise(x, y, 1);
                var c = Lerp(new Color(0.15f, 0.17f, 0.21f), new Color(0.19f, 0.21f, 0.26f), n);
                if (x % 256 < 3 || y % 256 < 3) c = new Color(0.10f, 0.11f, 0.14f);   // 1 m'de derz
                if (Hash(x, y, 7) > 0.9993f) c += new Color(0.05f, 0.05f, 0.05f);      // seyrek cizik
                return c;
            }));

        static Texture2D GrassTexture() => CachedTexture("T_Decor_Grass", () =>
            GenTexture((x, y) =>
            {
                float n = FractalNoise(x, y, 2);
                var c = Lerp(new Color(0.23f, 0.40f, 0.17f), new Color(0.32f, 0.51f, 0.22f), n);
                if (FractalNoise(x + 173, y + 91, 3) < 0.30f) c *= 0.86f;              // toprak yamalari
                if (Hash(x, y, 11) > 0.9990f) c = new Color(0.48f, 0.60f, 0.28f);      // acik cim ucu
                return c;
            }));

        static Texture2D MetalTexture() => CachedTexture("T_Decor_MetalGrid", () =>
            GenTexture((x, y) =>
            {
                float n = FractalNoise(x, y, 4);
                var c = Lerp(new Color(0.28f, 0.29f, 0.31f), new Color(0.33f, 0.34f, 0.37f), n);
                if (x % 128 < 3 || y % 128 < 3) c = new Color(0.20f, 0.21f, 0.23f);    // panel derzi
                int px = x % 128, py = y % 128;                                        // kose percini
                if (px >= 8 && px <= 13 && py >= 8 && py <= 13) c = new Color(0.42f, 0.43f, 0.46f);
                return c;
            }));

        static Texture2D PlasterTexture() => CachedTexture("T_Decor_Plaster", () =>
            GenTexture((x, y) =>
            {
                float n = FractalNoise(x, y, 5);
                return Lerp(new Color(0.78f, 0.75f, 0.70f), new Color(0.82f, 0.79f, 0.74f), n);
            }));

        static Texture2D CachedTexture(string name, System.Func<Color32[]> gen)
        {
            string path = TexDir + "/" + name + ".png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            var tex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            tex.SetPixels32(gen());
            tex.Apply();
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Color32[] GenTexture(System.Func<int, int, Color> pixel)
        {
            const int S = 512;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    px[y * S + x] = pixel(x, y);
            return px;
        }

        static Color Lerp(Color a, Color b, float t) => Color.Lerp(a, b, t);

        /// <summary>[0,1) deterministik hash — dokular her makinede ayni cikar.</summary>
        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 974634211);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
            }
        }

        /// <summary>Sarmal (dikissiz) deger gurultusu: 3 oktav, kafes 512'yi tam boler.</summary>
        static float FractalNoise(int x, int y, int seed)
        {
            float sum = 0f, amp = 0.5f;
            for (int oct = 0; oct < 3; oct++)
            {
                int cell = 128 >> oct;                        // 128, 64, 32 piksel kafes
                int gx = x / cell, gy = y / cell;
                int wrap = 512 / cell;
                float fx = (x % cell) / (float)cell, fy = (y % cell) / (float)cell;
                fx = fx * fx * (3f - 2f * fx);                // smoothstep
                fy = fy * fy * (3f - 2f * fy);
                float v00 = Hash(gx % wrap, gy % wrap, seed + oct);
                float v10 = Hash((gx + 1) % wrap, gy % wrap, seed + oct);
                float v01 = Hash(gx % wrap, (gy + 1) % wrap, seed + oct);
                float v11 = Hash((gx + 1) % wrap, (gy + 1) % wrap, seed + oct);
                sum += amp * Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
                amp *= 0.5f;
            }
            return sum;
        }

        // ================================================================== zeminler

        static int RebuildFloors(Transform mapRoot, Transform decor, Mats mats)
        {
            int count = 0;

            // Taranmis iki odanin mevcut zeminleri: ayni poligon, UV'li mesh + yeni malzeme.
            count += RetopFloor(mapRoot, "Zemin", mats.floorSport) ? 1 : 0;
            count += RetopFloor(mapRoot, "Zemin2", mats.floorGrass) ? 1 : 0;

            // Ciplak Plane'ler: dunya sinir kutusundan dikdortgen zemin uret, Plane'i kapat.
            count += PlaneFloor(decor, "Plane", "Zemin_Koridor", KoridorRect, mats.floorMetal) ? 1 : 0;
            count += PlaneFloor(decor, "Plane (1)", "Zemin_Bati", BatiRect, mats.floorWood) ? 1 : 0;
            count += PlaneFloor(decor, "Plane (2)", "Zemin_Guney", GuneyRect, mats.floorWood) ? 1 : 0;
            return count;
        }

        /// <summary>Var olan zeminin poligonunu mesh'inden okuyup UV'li kopyasini uretir.</summary>
        static bool RetopFloor(Transform mapRoot, string name, Material mat)
        {
            var t = mapRoot.Find(name);
            if (t == null) { Debug.LogWarning("[Decor] " + name + " yok, atlandi."); return false; }

            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            // Mesh koseleri = zemin poligonu (dunya uzayinda; TransformPoint kimlikte no-op).
            var world = new List<Vector2>();
            float y = 0f;
            foreach (var v in mf.sharedMesh.vertices)
            {
                var w = t.TransformPoint(v);
                world.Add(new Vector2(w.x, w.z));
                y = w.y;
            }

            var mesh = FloorMesh(world.ToArray(), y, name);
            // Mesh dunya uzayinda uretildi; zemin objesinin donusumu kimlik olmasa bile dogru
            // dursun diye koseler yerel uzaya cevrilir (kimlik donusumde no-op).
            var localVerts = mesh.vertices;
            for (int i = 0; i < localVerts.Length; i++)
                localVerts[i] = t.InverseTransformPoint(localVerts[i]);
            mesh.vertices = localVerts;
            mesh.RecalculateBounds();
            mesh = SaveMesh(mesh, name + "_UV");
            mf.sharedMesh = mesh;
            var mc = t.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = mesh;
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = mat;
            return true;
        }

        /// <summary>Ciplak Plane'in yerine gercek olculu, UV'li zemin dikdortgeni koyar.</summary>
        static bool PlaneFloor(Transform decor, string planeName, string newName, Rect fallback, Material mat)
        {
            Rect r = fallback;
            float y = 0.03f;

            var plane = FindSceneObject(planeName);
            if (plane != null)
            {
                var mr0 = plane.GetComponent<MeshRenderer>();
                // Kapali objede renderer sinir kutusu bayat/sifir olabilir — o zaman yedege dus.
                if (mr0 != null && mr0.bounds.size.x > 0.1f && mr0.bounds.size.z > 0.1f)
                {
                    var b = mr0.bounds;
                    r = Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
                    y = b.max.y;
                }
                plane.SetActive(false);
            }

            var poly = new[]
            {
                new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin),
                new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax),
            };
            var mesh = SaveMesh(FloorMesh(poly, y, newName), newName);

            var go = new GameObject(newName);
            go.transform.SetParent(decor, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return true;
        }

        /// <summary>Kulak-kirpma ucgenlemesi + DUZLEMSEL UV (1 birim = 1 m). RoomScanSetup'in
        /// ureticisinden tek farki UV — dokulu zeminin on kosulu.</summary>
        static Mesh FloorMesh(Vector2[] poly, float y, string name)
        {
            var pts = new List<Vector2>(poly);
            float area = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                area += p.x * q.y - q.x * p.y;
            }
            if (area < 0f) pts.Reverse();

            var remaining = new List<int>();
            for (int i = 0; i < pts.Count; i++) remaining.Add(i);
            var idx = new List<int>();
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
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0f) continue;

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
                if (!clipped) break;
            }
            if (remaining.Count == 3) { idx.Add(remaining[0]); idx.Add(remaining[1]); idx.Add(remaining[2]); }

            var verts = new Vector3[pts.Count];
            var uvs = new Vector2[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                verts[i] = new Vector3(pts[i].x, y, pts[i].y);
                uvs[i] = pts[i];                                  // dunya-metre UV
            }
            var tris = new int[idx.Count];
            for (int i = 0; i < idx.Count; i += 3)
            {
                tris[i] = idx[i]; tris[i + 1] = idx[i + 2]; tris[i + 2] = idx[i + 1];
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.uv = uvs;
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

        static Mesh SaveMesh(Mesh mesh, string name)
        {
            string path = MeshDir + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ================================================================== duvar boyasi

        /// <summary>Duvar malzemesini yerinde iyilestirir: siva dokusu + acik ton. Duvar
        /// GEOMETRISI degismez; 18b ozgun duz renge geri dondurur.</summary>
        static void PaintWalls(Mats mats)
        {
            var wallMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_VRMultiplayer/Materials/RoomWall.mat");
            if (wallMat == null) { Debug.LogWarning("[Decor] RoomWall.mat bulunamadi."); return; }
            wallMat.SetTexture("_BaseMap", PlasterTexture());
            wallMat.SetTextureScale("_BaseMap", Vector2.one * 0.8f);
            wallMat.SetColor("_BaseColor", new Color(0.97f, 0.95f, 0.91f));
            wallMat.SetFloat("_Smoothness", 0.05f);
            EditorUtility.SetDirty(wallMat);
        }

        /// <summary>Her duvar parcasinin hizasina supurgelik (alt) ve takim rengi bant (1.1 m)
        /// serit kutulari koyar. Kutu duvardan 1.25 cm tasar — iki yuzu birden kaplar, "ic yuz
        /// hangisi" hesabina gerek kalmaz. Collider YOK: mermiyi duvar zaten kesiyor.</summary>
        static int AddWallTrim(Transform mapRoot, Transform decor, Mats mats)
        {
            var holder = new GameObject("WallTrim").transform;
            holder.SetParent(decor, false);

            int made = 0;
            foreach (string wallsName in new[] { "Walls", "Walls2" })
            {
                var walls = mapRoot.Find(wallsName);
                if (walls == null) continue;
                foreach (Transform w in walls)
                {
                    if (!w.name.StartsWith("Duvar")) continue;
                    if (w.name.Contains("kapi ustu")) continue;   // lento: ne supurgelik ne bant

                    float bottom = w.position.y - w.lossyScale.y * 0.5f;
                    float len = w.lossyScale.z;

                    Strip(holder, "Supurgelik", w, bottom + 0.04f, 0.08f, len, mats.skirt);
                    var band = w.position.z >= -0.5f ? mats.bandBlue : mats.bandOrange;
                    Strip(holder, "Bant", w, bottom + 1.10f, 0.12f, len, band);
                    made += 2;
                }
            }
            return made;
        }

        static void Strip(Transform parent, string name, Transform wall, float y, float h, float len, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name + " (" + wall.name + ")";
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(wall.position.x, y, wall.position.z);
            go.transform.rotation = wall.rotation;
            go.transform.localScale = new Vector3(0.145f, h, len);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Kapi lentolarinin iki yanina koyu dikmeler — kapilar uzaktan okunur.
        /// Guney odanin lentosuz gecidine de dikme + esik boyasi.</summary>
        static int AddDoorFrames(Transform mapRoot, Transform decor, Mats mats)
        {
            var holder = new GameObject("DoorFrames").transform;
            holder.SetParent(decor, false);
            int made = 0;

            foreach (string wallsName in new[] { "Walls", "Walls2" })
            {
                var walls = mapRoot.Find(wallsName);
                if (walls == null) continue;
                foreach (Transform w in walls)
                {
                    if (!w.name.Contains("kapi ustu")) continue;
                    float bottom = w.position.y - w.lossyScale.y * 0.5f;   // lento alti = kapi yuksekligi
                    float doorHalf = w.lossyScale.z * 0.5f;
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        Object.DestroyImmediate(post.GetComponent<Collider>());
                        post.name = "KapiDikme (" + w.parent.name + "/" + w.name + (side < 0 ? " sol)" : " sag)");
                        post.transform.SetParent(holder, false);
                        post.transform.rotation = w.rotation;
                        // Dikme zeminden lento altina uzanir; lento ekseni boyunca iki uca kayar.
                        var p = w.position + w.rotation * new Vector3(0f, 0f, side * doorHalf);
                        p.y = bottom * 0.5f;
                        post.transform.position = p;
                        post.transform.localScale = new Vector3(0.15f, bottom, 0.09f);
                        post.GetComponent<MeshRenderer>().sharedMaterial = mats.frame;
                        made++;
                    }
                }
            }

            // Guney gecidi (lentosuz): x -1.06..-0.08 @ z -3.77.
            foreach (float x in new[] { -1.02f, -0.12f })
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(post.GetComponent<Collider>());
                post.name = "KapiDikme (guney gecidi)";
                post.transform.SetParent(holder, false);
                post.transform.position = new Vector3(x, 1.25f, -3.77f);
                post.transform.localScale = new Vector3(0.15f, 2.5f, 0.09f);
                post.GetComponent<MeshRenderer>().sharedMaterial = mats.frame;
                made++;
            }
            return made;
        }

        // ================================================================== tabela + boya

        static int AddSigns(Transform decor)
        {
            var holder = new GameObject("Signs").transform;
            holder.SetParent(decor, false);

            // (yazi, konum, yaw) — duvar IC yuzunden ~9 cm iceride. TextMesh, +Z'si
            // OKUYUCUDAN DUVARA dogru bakinca okunur (CIKIS tabelasindaki kural):
            // kuzey duvari icin yaw 0, guney duvari icin 180, bati duvari icin 270.
            var defs = new (string text, Vector3 pos, float yaw)[]
            {
                ("ANA ODA",    new Vector3(2.30f, 2.05f, 3.31f), 0f),
                ("BUYUK ODA",  new Vector3(2.20f, 2.30f, 3.71f), 180f),
                ("KORIDOR",    new Vector3(-1.05f, 2.05f, 0.12f), 270f),
                ("BATI ODASI", new Vector3(-2.50f, 2.05f, 2.64f), 0f),
                ("GUNEY ODA",  new Vector3(-1.24f, 2.05f, -6.28f), 180f),
            };

            foreach (var d in defs)
            {
                var tm = UITheme.MakeText(holder, d.text, UITheme.TextPrimary, 0.16f);
                tm.transform.position = d.pos;
                tm.transform.rotation = Quaternion.Euler(0f, d.yaw, 0f);
                tm.gameObject.name = "Tabela_" + d.text.Replace(' ', '_');
            }
            return defs.Length;
        }

        static int AddFloorPaint(Transform decor, Mats mats)
        {
            var holder = new GameObject("FloorPaint").transform;
            holder.SetParent(decor, false);
            int made = 0;

            // Atis hatti: hedefin karsisinda zemin cizgisi (ana oda).
            made += PaintQuad(holder, "AtisHatti", new Vector3(3.55f, 0.035f, 1.63f), 0f,
                new Vector2(0.06f, 1.30f), mats.paintYellow) ? 1 : 0;

            // Guney gecidi esigi.
            made += PaintQuad(holder, "GuneyEsik", new Vector3(-0.57f, 0.045f, -3.77f), 90f,
                new Vector2(0.10f, 0.94f), mats.paintYellow) ? 1 : 0;

            // Koridor yon oklari: A kuzeyde (mavi), B guneyde (turuncu) — iki quad'lik sevron.
            made += Chevron(holder, "OkA", new Vector3(-0.63f, 0.045f, 0.45f), 0f, mats.paintBlue);
            made += Chevron(holder, "OkB", new Vector3(-0.63f, 0.045f, -1.05f), 180f, mats.paintOrange);

            // Sevron etiketleri. B, guneye YURUYENE okunur dursun diye 180 cevrili.
            FlatLabel(holder, "A", new Vector3(-0.63f, 0.046f, 0.75f), UITheme.TeamBlueText, 0f);
            FlatLabel(holder, "B", new Vector3(-0.63f, 0.046f, -1.35f), new Color(0.95f, 0.62f, 0.30f), 180f);
            made += 2;
            return made;
        }

        static bool PaintQuad(Transform parent, string name, Vector3 pos, float yaw, Vector2 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return true;
        }

        /// <summary>Iki egik quad'dan zemin sevronu; yaw 0 = +Z'yi (kuzeyi) gosterir.</summary>
        static int Chevron(Transform parent, string name, Vector3 tip, float yaw, Material mat)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(parent, false);
            pivot.position = tip;
            pivot.rotation = Quaternion.Euler(0f, yaw, 0f);
            foreach (float side in new[] { -1f, 1f })
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(q.GetComponent<Collider>());
                q.name = side < 0 ? "sol" : "sag";
                q.transform.SetParent(pivot, false);
                q.transform.localPosition = new Vector3(side * 0.09f, 0f, -0.09f);
                q.transform.localRotation = Quaternion.Euler(90f, side * 45f, 0f);
                q.transform.localScale = new Vector3(0.07f, 0.30f, 1f);
                var mr = q.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return 1;
        }

        static void FlatLabel(Transform parent, string text, Vector3 pos, Color color, float yaw)
        {
            var tm = UITheme.MakeText(parent, text, color, 0.30f);
            tm.transform.position = pos;
            tm.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
            tm.gameObject.name = "Etiket_" + text;
        }

        // ================================================================== proplar

        static int PlaceProps(Transform props, Mats mats)
        {
            int n = 0;

            // --- Buyuk oda: zigzag siper hatti (uzun nisan hattini kirar) ---
            n += Place(PaintballPrefab("Barrier_1"), props, new Vector3(2.60f, 0.02f, 5.20f), 90f,
                new Vector2(1.30f, 0f), 1.05f) ? 1 : 0;
            n += Place(PaintballPrefab("Bunkers_2"), props, new Vector3(1.90f, 0.02f, 8.00f), 90f,
                new Vector2(1.80f, 0f), 1.05f) ? 1 : 0;
            n += Place(PaintballPrefab("Barrier_1"), props, new Vector3(3.60f, 0.02f, 10.80f), 90f,
                new Vector2(1.30f, 0f), 1.05f) ? 1 : 0;

            // Kuzey kosede pusu dogasi: kaya kumesi + calilik.
            n += Place(LokitPrefab("Nature/Rock_Cluster_01"), props, new Vector3(2.80f, 0.02f, 13.50f), 15f,
                Vector2.zero, 0f) ? 1 : 0;
            n += Place(LokitPrefab("Vegetation/Bush_02"), props, new Vector3(1.20f, 0.02f, 13.00f), 40f,
                new Vector2(1.50f, 0f), 1.00f) ? 1 : 0;

            // Dogu kapi agzini icten perdeleyen tam boy panel.
            n += Place(PaintballPrefab("Wood_Modular_8"), props, new Vector3(4.93f, 0.02f, 9.35f), 0f,
                Vector2.zero, 0f) ? 1 : 0;

            // --- Ana oda ---
            // Masa gercek siper: kasa gorunumune boya (geometri ayni, gercek masayla eslesiyor).
            var masa = FindSceneObject("Masa");
            if (masa != null)
            {
                var mr = masa.GetComponent<MeshRenderer>();
                if (mr != null) { mr.sharedMaterial = mats.crate; n++; }
            }
            // Capraz pusu paneli. Konum kullanicinin ELLE ayari (2026-08-05, sahnede tasidi):
            // kapinin degil masanin guney hattini tutuyor — degistirirken ona sor.
            n += Place(PaintballPrefab("Wood_Modular_8"), props, new Vector3(1.615f, 0.02f, 0.185f), 135f,
                Vector2.zero, 0f) ? 1 : 0;

            // --- Koridor: SADECE gorsel duvar panolari. Cikinti yapan siper koridoru navmesh
            // vokselinde muhurluyor (1.1 m - 0.6 m panel < 2 x 0.25 ajan) ve fiziksel gecisi de
            // daraltirdi; nisan hattini oda girislerindeki proplar kiriyor. ---
            n += WallBoard(props, "KoridorPano_bati", new Vector3(-1.13f, 1.05f, 0.10f), 0f, mats.crate) ? 1 : 0;
            n += WallBoard(props, "KoridorPano_dogu", new Vector3(-0.08f, 1.05f, -1.50f), 0f, mats.crate) ? 1 : 0;

            // --- Guney oda (B dogusu) ---
            // Bariyer girisin DIBINE degil oda ortasina: giristeki dirsek vokselde kapaniyordu
            // (olculdu: PathPartial 2.3 m'de kesildi). Konum + yukseklik kullanicinin ELLE
            // ayari (2026-08-05): pos.y 0.154 -> dunya y 0.19 (taban oturtma 0.124 dusuyor),
            // kullanici bariyeri bilerek 12 cm kaldirdi.
            n += Place(PaintballPrefab("Barrier_1"), props, new Vector3(-1.224f, 0.154f, -4.536f), 90f,
                new Vector2(0.85f, 0f), 1.00f) ? 1 : 0;
            n += Place(LokitPrefab("Vegetation/Bush_02"), props, new Vector3(-0.50f, 0.03f, -5.85f), 70f,
                new Vector2(1.20f, 0f), 1.00f) ? 1 : 0;

            // --- Bati odasi: cephanelik/pusu odasi ---
            n += Place(PaintballPrefab("Bunkers_1"), props, new Vector3(-3.05f, 0.03f, 0.95f), 30f,
                new Vector2(1.15f, 0f), 1.15f) ? 1 : 0;
            n += PlaceRack(props, new Vector3(-2.55f, 0.03f, 2.58f), 180f) ? 1 : 0;

            // --- Dogus halkalari: SALT GORSEL. Prefablar gercek TeamSpawnZone tasiyor;
            // kapatilmazsa sahneye YINELENEN dogum bolgesi eklenir (navmesh dogrulamasi da
            // yaniltilir — B'yi B'yle karsilastirdigi olculdu). ---
            n += PlaceVisual(ConstructorProps + "/Spawn_A.prefab", props, new Vector3(0.35f, 0.025f, 3.08f)) ? 1 : 0;
            n += PlaceVisual(ConstructorProps + "/Spawn_B.prefab", props, new Vector3(-2.42f, 0.035f, -6.32f)) ? 1 : 0;
            return n;
        }

        /// <summary>Duvara yapisik, COLLIDERSIZ ahsap pano — koridor gibi dar yerlerin gorsel
        /// kirilimi. Navmesh'i ve fiziksel gecisi etkilemez.</summary>
        static bool WallBoard(Transform parent, string name, Vector3 pos, float yaw, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = new Vector3(0.025f, 1.10f, 0.90f);   // duvar boyunca z
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return true;
        }

        static string PaintballPrefab(string name) => PaintballPrefabs + "/" + name + ".prefab";
        static string LokitPrefab(string rel) => LokitPrefabs + "/" + rel + ".prefab";

        /// <summary>
        /// Prefabi yerlestirir. targetFootprint.x > 0 ise genislik o metreye olceklenir
        /// (Y'ye dokunmadan), targetHeight > 0 ise boy ayrica olceklenir. Taban, mesh sinir
        /// kutusundan hesaplanip zemine oturtulur — LOKIT/paintball pivotlari guvenilmez
        /// (bkz. MapBuilder.PivotOffset dersi).
        /// </summary>
        static bool Place(string prefabPath, Transform parent, Vector3 pos, float yaw,
            Vector2 targetFootprint, float targetHeight)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[Decor] Prefab yok: " + prefabPath);
                return false;
            }

            var bounds = PropDef.MeasureLocalBounds(prefab);
            var baseScale = prefab.transform.localScale;
            var size = Vector3.Scale(bounds.size, Abs(baseScale));

            float sx = 1f, sy = 1f;
            if (targetFootprint.x > 0.01f && size.x > 0.01f) sx = targetFootprint.x / size.x;
            if (targetHeight > 0.01f && size.y > 0.01f) sy = targetHeight / size.y;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.localScale = new Vector3(baseScale.x * sx, baseScale.y * sy, baseScale.z * sx);

            // Taban: olcekli sinir kutusunun alti zemine otursun, merkez xz istenen noktada.
            float bottom = (bounds.min.y * baseScale.y) * sy;
            var centerOff = new Vector3(bounds.center.x * baseScale.x * sx, 0f,
                                        bounds.center.z * baseScale.z * sx);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            go.transform.SetPositionAndRotation(
                new Vector3(pos.x, pos.y - bottom, pos.z) - rot * centerOff, rot);
            return true;
        }

        /// <summary>Prefabi yerlestirip uzerindeki TUM betikleri SOKER — gorunum kalir,
        /// davranis kalmaz (dogum halkasi, raf gibi "islevli" prefablarin dekor kopyalari).
        ///
        /// Kapatmak yetmiyor: FindObjectsByType kapali bileseni de bulur, yani devre disi
        /// bir TeamSpawnZone bile menu 23'un dogum-baglanti dogrulamasini yaniltir
        /// (olculdu: B'yi B'yle karsilastirip "0.0 m baglantili" dedi).</summary>
        static bool PlaceVisual(string prefabPath, Transform parent, Vector3 pos,
            float yaw = 0f, Vector2 footprint = default, float height = 0f)
        {
            if (!Place(prefabPath, parent, pos, yaw, footprint, height)) return false;
            var inst = parent.GetChild(parent.childCount - 1);
            foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                Object.DestroyImmediate(mb);
            return true;
        }

        /// <summary>Silah rafi SADECE dekor: yeniden-dogum/slot betikleri kapali, yoksa mac
        /// basinda fazladan silah dogardi.</summary>
        static bool PlaceRack(Transform parent, Vector3 pos, float yaw)
            => PlaceVisual(ConstructorProps + "/Rack_Long_A.prefab", parent, pos, yaw);

        // ================================================================== yardimcilar

        static Transform FreshChild(Transform parent, string name)
        {
            var old = parent.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        /// <summary>GameObject.Find kapali objeleri bulamaz; sahne koklerinden tarar.</summary>
        static GameObject FindSceneObject(string name)
        {
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
                var t = FindInChildren(root.transform, name);
                if (t != null) return t.gameObject;
            }
            return null;
        }

        static Transform FindInChildren(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                var deep = FindInChildren(c, name);
                if (deep != null) return deep;
            }
            return null;
        }

        static void RestoreFloor(Transform mapRoot, string name, string meshPath, Material mat)
        {
            var t = mapRoot.Find(name);
            if (t == null) return;
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null) return;
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = mesh;
            var mc = t.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = mesh;
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null && mat != null) mr.sharedMaterial = mat;
        }

        static void MarkStatic(GameObject root)
        {
            var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
