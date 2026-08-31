using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRMultiplayer.Constructor;
using static VRMultiplayer.EditorTools.ProceduralNoise;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   52. Zemin Yamalari Uret — moloz yigini + is lekesi, KIYAMET paletine
    ///
    /// The other half of the floor. A themed ground material makes the whole room one surface;
    /// these are what break that surface up where the player decides it should be broken —
    /// rubble where a wall came down, soot where something burned. Together with the glass
    /// shards they are the reason <see cref="PropCategory.Ground"/> had to stop meaning
    /// "terrain, not a prop".
    ///
    /// DELIBERATELY SILENT, unlike the glass. Rubble underfoot would reasonably crunch, but the
    /// crunch is the thing that makes a player hesitate and hesitation has to MEAN something:
    /// if every second floor patch is loud, the glass stops being a decision and becomes
    /// scenery. These two are here to be looked at.
    /// </summary>
    public static class GroundPatchSetup
    {
        const string PropFolder = "Assets/_VRMultiplayer/Resources/ConstructorProps";
        const string FloorTexPath = "Assets/_VRMultiplayer/Resources/Themes/T_Kiyamet_Floor.png";

        const int Seed = 20260813;

        [MenuItem("Tools/VR Multiplayer/52. Zemin Yamalari Uret (moloz + is lekesi)")]
        public static void RunMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Run(), "Tamam");

        public static string Run()
        {
            var log = new StringBuilder();
            log.AppendLine(EnsureRubble());
            log.AppendLine(EnsureScorch());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine();
            log.AppendLine(Register());
            return log.ToString();
        }

        // ------------------------------------------------------------- moloz

        const float RubblePatchSize = 1.2f;
        const int RubbleChunks = 60;

        static string EnsureRubble()
        {
            const string name = "Rubble_Patch";
            string meshPath = PropFolder + "/" + name + "_Mesh.asset";
            string matPath = PropFolder + "/M_Rubble.mat";
            string prefabPath = PropFolder + "/" + name + ".prefab";

            var msg = new StringBuilder();

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = BuildRubbleMesh();
                AssetDatabase.CreateAsset(mesh, meshPath);
                msg.Append($"Moloz mesh uretildi ({RubbleChunks} parca, {mesh.triangles.Length / 3} ucgen). ");
            }
            else msg.Append("Moloz mesh zaten var. ");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader == null) return msg.Append("UYARI: URP/Simple Lit yok.").ToString();

                mat = new Material(shader);

                // ZEMININ KENDI DOKUSUNU KULLANIYOR. Moloz zaten o zeminin kirilmis hali;
                // ayri bir doku uretmek hem fazladan bellek hem de iki ayri beton rengi
                // demek olurdu. Parcalarin UV'si yerel konumlarindan turetildigi icin her
                // parca dokunun baska bir yerine denk geliyor, yani hepsi ayni desende
                // cikmiyor.
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(FloorTexPath);
                if (tex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1.00f, 0.97f, 0.93f));
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);

                AssetDatabase.CreateAsset(mat, matPath);
                msg.Append("Moloz malzemesi uretildi (zemin dokusunu paylasiyor). ");
            }
            else msg.Append("Moloz malzemesi zaten var. ");

            msg.Append(EnsurePatchPrefab(prefabPath, name, mesh, mat, castsShadow: true));
            return msg.ToString();
        }

        /// <summary>
        /// Scattered angular chunks — irregular tetrahedra sitting on the ground.
        ///
        /// TETRAHEDRA, NOT QUADS. The glass shards are flat because glass IS flat; rubble read
        /// as stickers the moment it was tried that way. Four faces is the cheapest solid that
        /// still casts a believable shadow and shows a lit face and a dark face at the same
        /// time, which is what makes a 6 cm object read as a lump of concrete rather than a
        /// grey mark on the floor.
        /// </summary>
        static Mesh BuildRubbleMesh()
        {
            var rnd = new System.Random(Seed);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float half = RubblePatchSize * 0.5f;

            for (int c = 0; c < RubbleChunks; c++)
            {
                float cx = (float)(rnd.NextDouble() * 2.0 - 1.0) * half * 0.92f;
                float cz = (float)(rnd.NextDouble() * 2.0 - 1.0) * half * 0.92f;

                float r = 0.022f + (float)rnd.NextDouble() * 0.055f;
                float h = r * (0.45f + (float)rnd.NextDouble() * 1.1f);
                float yaw = (float)rnd.NextDouble() * Mathf.PI * 2f;

                // Taban ucgeni + tepe: dordu de duzensiz, yoksa altmis parca ayni piramit olur.
                var p = new Vector3[4];
                for (int k = 0; k < 3; k++)
                {
                    float a = yaw + k * (Mathf.PI * 2f / 3f) + (float)(rnd.NextDouble() - 0.5) * 0.8f;
                    float rr = r * (0.6f + (float)rnd.NextDouble() * 0.8f);
                    p[k] = new Vector3(cx + Mathf.Cos(a) * rr, 0f, cz + Mathf.Sin(a) * rr);
                }
                p[3] = new Vector3(
                    cx + (float)(rnd.NextDouble() - 0.5) * r * 0.7f,
                    h,
                    cz + (float)(rnd.NextDouble() - 0.5) * r * 0.7f);

                // Dort yuz, her biri KENDI koseleriyle: paylasilan kose, RecalculateNormals'ta
                // komsu yuzlerin normallerini ortalayip keskin bir tasi yumusatirdi.
                //
                // Sarim yonu ELLE DEGIL HESAPLA veriliyor (bkz. AddFace): dortyuzlunun hangi
                // kose sirasinin disari baktigi tabanin rastgele acisina gore degisiyor ve
                // ilk surumde yarisi ters cikti — parcalar isigi arkadan alip DUZ SIYAH
                // ucgenler olarak, zemine yapistirilmis cikartma gibi gorundu.
                Vector3 mid = (p[0] + p[1] + p[2] + p[3]) * 0.25f;
                AddFace(verts, uvs, tris, p[0], p[1], p[2], mid);
                AddFace(verts, uvs, tris, p[0], p[1], p[3], mid);
                AddFace(verts, uvs, tris, p[1], p[2], p[3], mid);
                AddFace(verts, uvs, tris, p[2], p[0], p[3], mid);
            }

            var mesh = new Mesh { name = "Rubble_Patch_Mesh" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Adds one triangle, wound so its normal points AWAY from <paramref name="inside"/>.
        ///
        /// The caller passes the chunk's centre and the winding is derived from it, because on a
        /// convex solid "outward" is exactly "away from the middle" and that is a computation,
        /// not something to get right by hand for four faces built from randomised points.
        /// </summary>
        static void AddFace(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                            Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - inside) < 0f)
                (b, c) = (c, b);

            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            // UV yerel konumdan: her parca dokunun baska bir bolgesine dusuyor.
            uvs.Add(new Vector2(a.x, a.z) * 0.8f);
            uvs.Add(new Vector2(b.x, b.z) * 0.8f);
            uvs.Add(new Vector2(c.x, c.z) * 0.8f);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }

        // ------------------------------------------------------------- is lekesi

        const float ScorchSize = 1.4f;
        const int ScorchTexSize = 256;

        static string EnsureScorch()
        {
            const string name = "Scorch_Mark";
            string texPath = PropFolder + "/T_Scorch.png";
            string matPath = PropFolder + "/M_Scorch.mat";
            string meshPath = PropFolder + "/" + name + "_Mesh.asset";
            string prefabPath = PropFolder + "/" + name + ".prefab";

            var msg = new StringBuilder();

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                var made = BuildScorchTexture(ScorchTexSize);
                File.WriteAllBytes(texPath, made.EncodeToPNG());
                Object.DestroyImmediate(made);
                AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);

                var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = TextureImporterType.Default;
                    // KENARINA KADAR SEFFAF olmali, o yuzden Clamp: Repeat'te bilinear
                    // filtreleme karsi kenardan renk cekip lekenin cevresine hayalet bir
                    // cerceve cizer.
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.alphaIsTransparency = true;
                    imp.mipmapEnabled = true;
                    imp.maxTextureSize = ScorchTexSize;
                    imp.SaveAndReimport();
                }
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                msg.Append($"Is lekesi dokusu uretildi ({ScorchTexSize}px, alfali). ");
            }
            else msg.Append("Is lekesi dokusu zaten var. ");

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = BuildQuad(ScorchSize, 0.006f);
                AssetDatabase.CreateAsset(mesh, meshPath);
                msg.Append("Is lekesi mesh'i uretildi. ");
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader == null) return msg.Append("UYARI: URP/Simple Lit yok.").ToString();

                mat = new Material(shader);
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.02f);
                MakeTransparent(mat);
                AssetDatabase.CreateAsset(mat, matPath);
                msg.Append("Is lekesi malzemesi uretildi (saydam). ");
            }
            else msg.Append("Is lekesi malzemesi zaten var. ");

            msg.Append(EnsurePatchPrefab(prefabPath, name, mesh, mat, castsShadow: false));
            return msg.ToString();
        }

        /// <summary>
        /// URP'de saydamlik tek bir alanla acilmiyor: yuzey tipi, harman modu, derinlik yazimi,
        /// kuyruk ve shader anahtari birlikte kurulmali. Biri eksik kalirsa malzeme ya opak
        /// cizilir ya da Inspector'da saydam gorunup build'de opak cikar.
        /// </summary>
        static void MakeTransparent(Material mat)
        {
            mat.SetFloat("_Surface", 1f);                       // 0 opak, 1 saydam
            mat.SetFloat("_Blend", 0f);                         // alfa harmani
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
            // Yassi bir leke golge dusurmez; golge gecisi acik kalsa zeminin uzerinde
            // ince bir karartı olarak gorunurdu.
            mat.SetShaderPassEnabled("ShadowCaster", false);
        }

        /// <summary>
        /// Soot: dark in the middle, ragged at the edge, blotchy throughout.
        ///
        /// THE EDGE IS THE WHOLE JOB. A round gradient reads as an airbrushed circle — the thing
        /// that says "something burned here" is an outline that wanders. So the radius each
        /// pixel is compared against is itself perturbed by noise, and the alpha is multiplied
        /// by a second, finer field so the interior is patchy rather than solid.
        /// </summary>
        static Texture2D BuildScorchTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];

            float[] edge = NoiseField(size, 4f, 3, 401f);    // kenari dalgalandiran
            float[] blot = NoiseField(size, 9f, 3, 503f);    // ic dokusu
            float[] fine = NoiseField(size, 26f, 2, 607f);   // ince tane

            var soot = new Color(0.045f, 0.040f, 0.036f);
            var ash = new Color(0.115f, 0.100f, 0.088f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float dx = x / (float)(size - 1) - 0.5f;
                    float dy = y / (float)(size - 1) - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;      // 0 merkez, 1 kenar

                    // Yaricapi gurultuyle boz: dairenin kendisi gorunmesin.
                    float dPerturbed = d + (edge[i] - 0.5f) * 0.42f;

                    float a = 1f - Step01(0.30f, 0.92f, dPerturbed);
                    a *= 0.55f + blot[i] * 0.55f;                       // benekli ic
                    a *= 0.82f + fine[i] * 0.30f;
                    a = Mathf.Clamp01(a);

                    Color c = Color.Lerp(soot, ash, blot[i] * 0.8f);
                    c.a = a;
                    px[i] = c;
                }
            }

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        static Mesh BuildQuad(float side, float y)
        {
            float h = side * 0.5f;
            var mesh = new Mesh { name = "Scorch_Mark_Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-h, y, -h), new Vector3(-h, y, h),
                new Vector3( h, y,  h), new Vector3( h, y, -h),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------- ortak

        static string EnsurePatchPrefab(string path, string name, Mesh mesh, Material mat,
                                        bool castsShadow)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return name + " prefabi zaten var.";
            if (mesh == null || mat == null) return name + " prefabi uretilemedi.";

            var root = new GameObject(name);
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = root.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;

                // Moloz GOLGE DUSURUR: alcak gunes altinda 5 cm'lik bir parcanin 10 cm'lik
                // golgesi, onu zemine oturtan sey. Yassi is lekesi dusurmez — dusurebilecegi
                // bir hacmi yok, acik kalsa zeminin uzerinde ince bir karartı cizerdi.
                mr.shadowCastingMode = castsShadow
                    ? UnityEngine.Rendering.ShadowCastingMode.On
                    : UnityEngine.Rendering.ShadowCastingMode.Off;

                // COLLIDER YOK. Bu ikisi yalnizca gorsel; bir collider (kati olsaydi duvar
                // sayilirdi, tetikleyici olsaydi FootSurface sorgusunu mesgul ederdi) hicbir
                // ise yaramadan iki sistemin isini zorlastirirdi.
                PrefabUtility.SaveAsPrefabAsset(root, path);
                return name + " prefabi uretildi.";
            }
            finally { Object.DestroyImmediate(root); }
        }

        static string Register()
        {
            string scan = ConstructorSetup.ScanPropLibrary();
            var lib = PropLibrary.Instance;

            var ids = new[] { "rubble_patch", "scorch_mark" };
            var done = new List<string>();
            foreach (string id in ids)
            {
                var def = lib.ById(id);
                if (def == null) { done.Add(id + ": BULUNAMADI"); continue; }

                def.category = PropCategory.Ground;
                def.paletteId = "kiyamet";
                def.snap = PropSnap.Floor;
                def.networked = false;
                def.hiddenInPalette = false;
                def.freeRotation = true;
                def.fitToFootprint = true;
                done.Add(id + ": Ground / KIYAMET");
            }

            lib.InvalidateIndex();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();

            return "Kutuphaneye kaydedildi -> " + string.Join(", ", done) + "\n\n" + scan;
        }
    }
}
