using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   50. Cam Kirigi Yamasi Uret — mesh + malzeme + prefab + citirti sesleri, sonra kutuphaneye kayit
    ///
    /// WHY EVERYTHING IS GENERATED: there is no shard model and no glass audio in this project,
    /// and the two together are what the feature is. Same call as the procedural sky and floor —
    /// generated art that can be replaced later beats a feature that cannot be tried at all.
    ///
    /// THE HESITATION IS THE SOUND, NOT THE MESH. A player looking down sees a smear of small
    /// bright specks and walks on; a player who HEARS glass break under their boot stops. The
    /// visual only has to say "there is something there" so that the sound is not a surprise
    /// from nowhere; the crunch does the rest, and <see cref="FootSurface.maxDistance"/> is what
    /// turns stopping into a decision instead of a reflex.
    ///
    /// Menu ince bir kabuk (bkz. ConstructorSetup): is <see cref="Run"/> icinde, diyalogu
    /// yalnizca menu acar.
    /// </summary>
    public static class GlassShardSetup
    {
        const string PropFolder = "Assets/_VRMultiplayer/Resources/ConstructorProps";
        const string SoundFolder = "Assets/_VRMultiplayer/Resources/WeaponSounds";
        const string PrefabName = "Glass_Shards";
        const string PropId = "glass_shards";

        /// <summary>Yamanin bir kenari (m). Izgara ayak izi de bu.</summary>
        const float PatchSize = 1.5f;

        const int ShardCount = 110;
        const int ClipCount = 3;
        const int SampleRate = 44100;

        /// <summary>Uretimin TEKRARLANABILIR olmasi icin sabit tohum.</summary>
        const int Seed = 20260812;

        [MenuItem("Tools/VR Multiplayer/50. Cam Kirigi Yamasi Uret")]
        public static void RunMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Run(), "Tamam");

        public static string Run()
        {
            var log = new StringBuilder();

            EnsureFolder(PropFolder);
            EnsureFolder(SoundFolder);

            log.AppendLine(EnsureClips());
            log.AppendLine(EnsureMeshAndMaterial(out Mesh mesh, out Material mat));
            log.AppendLine(EnsurePrefab(mesh, mat));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine();
            log.AppendLine(Register());
            return log.ToString();
        }

        // ------------------------------------------------------------- mesh + malzeme

        static string EnsureMeshAndMaterial(out Mesh mesh, out Material mat)
        {
            string meshPath = PropFolder + "/" + PrefabName + "_Mesh.asset";
            string matPath = PropFolder + "/M_Glass_Shards.mat";
            var msg = new StringBuilder();

            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = BuildShardMesh();
                AssetDatabase.CreateAsset(mesh, meshPath);
                msg.Append($"Mesh uretildi ({ShardCount} kirik, {mesh.triangles.Length / 3} ucgen). ");
            }
            else msg.Append("Mesh zaten var. ");

            mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                // LIT, Simple Lit DEGIL — zeminin tersine. Bu parcanin tek isi PARLAMAK:
                // camı cam yapan sey rengi degil, kafa oynadikca yer degistiren keskin
                // yansima, ve onu veren Lit'in dar spekuler tepesi.
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) { mat = null; return msg.Append("UYARI: URP/Lit yok.").ToString(); }

                mat = new Material(shader);

                // KOYU GOVDE, KESKIN PARLAMA. Ilk deger (0.70) camı yukaridan bakildiginda
                // kagit parcasina cevirdi: gercek cam isigin cogunu GECIRIR, goze gelen sey
                // govdesi degil kenarindan ve yuzunden sekmis keskin yansimadir. Albedo'yu
                // dusurmek parlamayan kirigi sonduruyor, parlayani one cikariyor — ama
                // tamamen karartilmiyor, cunku oyuncunun UZERINE BASMADAN once fark etmesi
                // gerek; goremedigi bir seyden cekinemez.
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.46f, 0.52f, 0.55f));
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.95f);
                AssetDatabase.CreateAsset(mat, matPath);
                msg.Append("Malzeme uretildi (URP/Lit, parlak). ");
            }
            else msg.Append("Malzeme zaten var. ");

            return msg.ToString();
        }

        /// <summary>
        /// Scattered flat shards, each tilted a little.
        ///
        /// THE TILT IS THE WHOLE VISUAL. Shards lying perfectly flat all share one normal, so
        /// they all catch the sun at once or none of them do — a uniform grey smear either way.
        /// A few degrees of random tilt means only a handful are aligned to reflect at any given
        /// moment, and WHICH handful changes as the player moves their head. That travelling
        /// sparkle is what the eye reads as broken glass.
        ///
        /// Front and back faces get SEPARATE vertices: a shared vertex would have the two
        /// opposing face normals averaged into nothing by RecalculateNormals, and the shards
        /// would light as if they had no surface at all.
        /// </summary>
        static Mesh BuildShardMesh()
        {
            var rnd = new System.Random(Seed);

            var verts = new Vector3[ShardCount * 6];
            var uvs = new Vector2[ShardCount * 6];
            var tris = new int[ShardCount * 6];
            float half = PatchSize * 0.5f;

            for (int s = 0; s < ShardCount; s++)
            {
                // Merkez: yamaya duzgun dagilmis, kenarlara dogru biraz seyrelerek —
                // kare bir cam lekesi yapay durur, kenari dagilan bir leke dogal.
                float cx = (float)(rnd.NextDouble() * 2.0 - 1.0);
                float cz = (float)(rnd.NextDouble() * 2.0 - 1.0);
                cx *= half * (0.55f + 0.45f * (float)rnd.NextDouble());
                cz *= half * (0.55f + 0.45f * (float)rnd.NextDouble());

                float size = 0.012f + (float)rnd.NextDouble() * 0.043f;
                float yaw = (float)rnd.NextDouble() * Mathf.PI * 2f;
                float tilt = (float)rnd.NextDouble() * 15f;          // derece
                float tiltDir = (float)rnd.NextDouble() * Mathf.PI * 2f;
                float y = 0.002f + (float)rnd.NextDouble() * 0.006f;

                var rot = Quaternion.AngleAxis(tilt,
                              new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir))) *
                          Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);

                int b = s * 6;
                for (int k = 0; k < 3; k++)
                {
                    // Duzgun ucgen degil: her kose kendi yaricapiyla, kirik cam koseli olur.
                    float a = (k / 3f) * Mathf.PI * 2f + (float)rnd.NextDouble() * 0.9f;
                    float r = size * (0.55f + (float)rnd.NextDouble() * 0.75f);
                    Vector3 local = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    Vector3 p = new Vector3(cx, y, cz) + rot * local;
                    verts[b + k] = p;
                    verts[b + 3 + k] = p;          // arka yuz icin AYRI kose
                    uvs[b + k] = Vector2.zero;
                    uvs[b + 3 + k] = Vector2.zero;
                }

                tris[b + 0] = b + 0; tris[b + 1] = b + 1; tris[b + 2] = b + 2;
                tris[b + 3] = b + 3; tris[b + 4] = b + 5; tris[b + 5] = b + 4;  // ters sarim
            }

            var mesh = new Mesh { name = PrefabName + "_Mesh" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------- prefab

        static string EnsurePrefab(Mesh mesh, Material mat)
        {
            string path = PropFolder + "/" + PrefabName + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return "Prefab zaten var.";
            if (mesh == null || mat == null) return "Prefab uretilemedi (mesh/malzeme yok).";

            var root = new GameObject(PrefabName);
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = mat;

                // TRIGGER — bkz. FootSurface. Kati birakilsaydi yassi bir cam lekesi
                // mermileri durdurur ve uzerinden gecen kafayi karartirdi.
                var box = root.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(PatchSize, 0.09f, PatchSize);
                box.center = new Vector3(0f, 0.045f, 0f);

                root.AddComponent<FootSurface>();

                PrefabUtility.SaveAsPrefabAsset(root, path);
                return "Prefab uretildi (tetikleyici collider + FootSurface).";
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ------------------------------------------------------------- ses

        static string EnsureClips()
        {
            int made = 0;
            for (int i = 1; i <= ClipCount; i++)
            {
                string path = SoundFolder + "/glass_step_" + i + ".wav";
                if (File.Exists(path)) continue;
                File.WriteAllBytes(path, WavWriter.ToWav(Crunch(Seed + i * 977), SampleRate));
                made++;
            }
            if (made == 0) return "Citirti klipleri zaten var.";
            AssetDatabase.Refresh();
            return $"{made} citirti klibi sentezlendi (glass_step_1..{ClipCount}).";
        }

        /// <summary>
        /// Synthesises one glass crunch.
        ///
        /// Glass does not have a pitch, it has a HANDFUL OF SIMULTANEOUS ONES: a break is many
        /// small pieces each ringing at its own frequency and dying within a few milliseconds.
        /// So the clip is a burst of noise for the fracture itself plus a dozen short decaying
        /// tones scattered over the first fifth of a second, each with its own frequency, start
        /// time and decay. A single tone reads as a bell and a plain noise burst reads as a
        /// footstep on gravel; it is the spread of tones over a very short window that reads as
        /// glass.
        ///
        /// PLACEHOLDER QUALITY, and meant to be. Swapping in recorded audio needs nothing but
        /// dropping files with the same names into Resources/WeaponSounds.
        /// </summary>
        static float[] Crunch(int seed)
        {
            var rnd = new System.Random(seed);
            int n = (int)(0.40f * SampleRate);
            var s = new float[n];

            // 1) Kirilma ani: genis bantli, cok hizli sonen gurultu.
            float burstTau = 0.010f + (float)rnd.NextDouble() * 0.008f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float env = Mathf.Exp(-t / burstTau);
                if (env < 0.001f) break;
                s[i] += (float)(rnd.NextDouble() * 2.0 - 1.0) * env * 0.55f;
            }

            // 2) Parcalarin cinlamasi: her biri kendi frekansi, baslangici ve sonumuyle.
            int grains = 14 + rnd.Next(9);
            for (int g = 0; g < grains; g++)
            {
                int start = (int)(rnd.NextDouble() * 0.18f * SampleRate);
                float freq = 1500f + (float)rnd.NextDouble() * 5800f;
                float tau = 0.005f + (float)rnd.NextDouble() * 0.022f;
                float amp = 0.10f + (float)rnd.NextDouble() * 0.34f;
                float phase = (float)(rnd.NextDouble() * Math.PI * 2.0);

                int len = (int)(tau * 6f * SampleRate);
                for (int k = 0; k < len; k++)
                {
                    int i = start + k;
                    if (i >= n) break;
                    float t = k / (float)SampleRate;
                    float env = Mathf.Exp(-t / tau);
                    s[i] += Mathf.Sin(2f * Mathf.PI * freq * t + phase) * env * amp;

                    // Her cinlamanin basinda cok kisa bir tirmalama: temiz sinus "cam"
                    // degil "zil" gibi duyuluyor.
                    if (k < SampleRate / 400)
                        s[i] += (float)(rnd.NextDouble() * 2.0 - 1.0) * env * amp * 0.5f;
                }
            }

            // Tepe degerine normalize: klipler arasi seviye farki, ayni yuzeyin bazi
            // adimlarda daha sert basilmis gibi duyulmasina yol acardi.
            WavWriter.Normalize(s, 0.92f);

            // Son 15 ms'de kapan: ani kesilen bir klip "tak" diye tiklar.
            int fade = (int)(0.015f * SampleRate);
            for (int i = 0; i < fade; i++)
                s[n - 1 - i] *= i / (float)fade;

            return s;
        }


        // ------------------------------------------------------------- kutuphane kaydi

        /// <summary>
        /// Runs the library scan, then stamps the fields the scan cannot guess.
        ///
        /// The scan gets the prefab, the name and the measured footprint right on its own, and
        /// it PRESERVES hand-edited fields on re-scan — but it has never seen this prop before,
        /// so its defaults apply once: <c>GuessCategory</c> reads "Glass_Shards" and lands on
        /// Cover (nothing in the name says ground), and the palette starts empty, which means
        /// "every palette". Setting them here rather than telling someone to open menu 31 is
        /// what makes the whole thing one button.
        /// </summary>
        static string Register()
        {
            string scan = ConstructorSetup.ScanPropLibrary();

            var lib = PropLibrary.Instance;
            var def = lib.ById(PropId);
            if (def == null)
                return "Tarama sonrasi '" + PropId + "' kutuphanede bulunamadi.\n\n" + scan;

            def.category = PropCategory.Ground;
            def.paletteId = "kiyamet";
            def.snap = PropSnap.Floor;
            def.networked = false;
            def.hiddenInPalette = false;
            // Serbest donus: ayni yamayi farkli acilarla koyabilmek, tek bir mesh'in
            // tekrarlandigini gizlemenin en ucuz yolu.
            def.freeRotation = true;
            // Fit ACIK: yan yana konan iki yama arasinda bosluk kalmasin (bkz. PropDef).
            def.fitToFootprint = true;

            lib.InvalidateIndex();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();

            var problems = lib.Validate();
            return "Kutuphaneye kaydedildi: kategori=Ground, palet=KIYAMET, serbest donus acik.\n\n"
                   + scan
                   + (problems.Count == 0 ? "" : "\n\nSORUNLAR:\n - " + string.Join("\n - ", problems));
        }

        // ------------------------------------------------------------- yardimci

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
