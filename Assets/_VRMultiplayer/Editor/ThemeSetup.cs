using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer;
using VRMultiplayer.Constructor;
using static VRMultiplayer.EditorTools.ProceduralNoise;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Dunya gorunumu (tema) veri katmani araclari:
    ///   49.  Tema Kutuphanesi Uret/Guncelle — ThemeLibrary + gokyuzu materyallerini yazar
    ///   49b. Temayi Onizle                  — temayi SAHNEYE uygular, gozluge girmeden bak
    ///   49c. Onizlemeyi Kaldir              — sahnenin kendi gorunumune don
    ///   49d. Secili Haritaya Tema Ata       — Maps/*.json icine themeId yazar
    ///
    /// Menuler ConstructorSetup ile ayni sozlesmede: is yapan <c>RunX()</c> bir RAPOR METNI
    /// dondurur, diyalogu yalnizca menu acar. Boylece ayni islem otomasyondan (MCP) modal
    /// diyalog acmadan cagirilabilir — acilan diyalog Unity'yi tiklanana kadar kilitler.
    /// </summary>
    public static class ThemeSetup
    {
        const string LibraryPath = "Assets/_VRMultiplayer/Resources/ThemeLibrary.asset";
        const string SkyFolder = "Assets/_VRMultiplayer/Resources/Themes";
        const string MapsFolder = "Assets/_VRMultiplayer/Maps";

        /// <summary>Kurulacak temanin kimligi — 49b/49d bunu kullanir.</summary>
        const string KiyametId = "kiyamet";

        // ------------------------------------------------------------- 49

        [MenuItem("Tools/VR Multiplayer/49. Tema Kutuphanesi Uret-Guncelle")]
        public static void BuildLibraryMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", BuildLibrary(), "Tamam");

        /// <summary>
        /// Creates or updates the theme library and the skybox materials it names.
        ///
        /// UPDATES IN PLACE and never rewrites the array wholesale: a theme's numbers are meant
        /// to be tuned in the Inspector, and regenerating the asset would throw that tuning away
        /// every time someone ran the menu to add the NEXT theme. Only a theme that does not
        /// exist yet is written; existing ids are reported and left alone.
        /// </summary>
        public static string BuildLibrary()
        {
            var log = new StringBuilder();

            EnsureFolder("Assets/_VRMultiplayer/Resources");
            EnsureFolder(SkyFolder);

            var lib = AssetDatabase.LoadAssetAtPath<ThemeLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<ThemeLibrary>();
                AssetDatabase.CreateAsset(lib, LibraryPath);
                log.AppendLine("ThemeLibrary.asset olusturuldu.");
            }
            else log.AppendLine("Mevcut ThemeLibrary.asset guncelleniyor.");

            log.AppendLine(EnsureKiyametSky());
            log.AppendLine(EnsureKiyametFloor());

            var themes = new List<ThemeDef>(lib.themes ?? new ThemeDef[0]);
            var existing = lib.ById(KiyametId);
            if (existing == null)
            {
                themes.Add(MakeKiyamet());
                lib.themes = themes.ToArray();
                log.AppendLine("'kiyamet' temasi eklendi.");
            }
            else
            {
                // SADECE BOS ALANLARI DOLDUR. Menu yeniden calistirildiginda elle ayarlanmis
                // renkleri/acilari ezmek, temayi Inspector'dan ayarlamayi imkansiz kilardi;
                // ama temaya SONRADAN eklenen bir alan (zemin gibi) eski girdide bos durur ve
                // hicbir zaman dolmazdi. Ikisinin arasindaki tek dogru kural bu.
                var fresh = MakeKiyamet();
                var filled = new List<string>();
                if (string.IsNullOrEmpty(existing.skyboxPath))
                { existing.skyboxPath = fresh.skyboxPath; filled.Add("skyboxPath"); }
                if (string.IsNullOrEmpty(existing.floorMaterialPath))
                { existing.floorMaterialPath = fresh.floorMaterialPath; filled.Add("floorMaterialPath"); }
                existing.InvalidateCache();

                log.AppendLine(filled.Count == 0
                    ? "'kiyamet' temasi zaten tam — hicbir degerine dokunulmadi."
                    : "'kiyamet' temasinda BOS olan alanlar dolduruldu: " + string.Join(", ", filled)
                      + " (ayarlanmis degerlere dokunulmadi).");
            }

            lib.InvalidateCaches();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var problems = lib.Validate();
            log.AppendLine();
            log.AppendLine(problems.Count == 0
                ? $"Kutuphane saglikli — {lib.Count} tema."
                : "SORUNLAR:\n - " + string.Join("\n - ", problems));

            log.AppendLine();
            log.AppendLine("Sonraki adim: menu 49b ile sahnede onizle.");
            return log.ToString();
        }

        /// <summary>
        /// The apocalypse look: a low amber sun, thick dusty air, brown-grey ambient.
        ///
        /// A LOW SUN IS THE EFFECT — BUT 26 DEGREES, NOT 14. Long shadows are most of the mood,
        /// and the first pass put the sun at 14 for exactly that reason. In the headset it fails:
        /// a horizontal floor receives light by the SINE of the elevation, so at 14 degrees the
        /// ground gets a quarter of the sun the props' vertical faces get, and it reads as a
        /// black void under a lit scene. That is tolerable in a screenshot framed at the horizon
        /// and not tolerable in room-scale VR, where a standing player spends much of the match
        /// looking down at the floor around their feet. 26 degrees still throws shadows about
        /// twice an object's height and roughly doubles what reaches the ground.
        ///
        /// <see cref="ThemeDef.ambientSky"/> is the floor's other light source and is raised for
        /// the same reason: an up-facing normal takes its ambient from the sky band.
        /// </summary>
        static ThemeDef MakeKiyamet() => new ThemeDef
        {
            id = KiyametId,
            displayName = "KIYAMET",
            skyboxPath = "Themes/Sky_Kiyamet",
            floorMaterialPath = "Themes/M_Kiyamet_Floor",

            sunColor = new Color(1.00f, 0.70f, 0.45f),
            sunIntensity = 1.05f,
            sunPitch = 26f,
            sunYaw = 35f,
            sunShadows = true,

            ambientSky = new Color(0.44f, 0.36f, 0.29f),
            ambientEquator = new Color(0.34f, 0.28f, 0.23f),
            ambientGround = new Color(0.18f, 0.15f, 0.13f),

            fogEnabled = true,
            // Ufuk rengiyle AYNI aile: sis ile gokyuzu ayrisirsa uzaktaki geometri
            // gokyuzune karismak yerine onunde yuzuyormus gibi durur.
            fogColor = new Color(0.44f, 0.33f, 0.25f),
            fogDensity = 0.055f,
        };

        /// <summary>
        /// Writes the procedural skybox material.
        ///
        /// PROCEDURAL AND NOT A CUBEMAP, on purpose. A cubemap is art that does not exist yet,
        /// and at 1024 per face it is ~8 MB of Quest memory per theme; the built-in procedural
        /// sky is a few instructions, needs no texture, and — because it reads
        /// <see cref="RenderSettings.sun"/> — puts its sun disk exactly where the theme's
        /// shadows say it is. When there IS painted sky art, only <c>skyboxPath</c> changes.
        /// </summary>
        static string EnsureKiyametSky()
        {
            const string path = SkyFolder + "/Sky_Kiyamet.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return "Sky_Kiyamet.mat zaten var — dokunulmadi.";

            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
                return "UYARI: 'Skybox/Procedural' shader'i bulunamadi — gokyuzu yazilamadi.";

            var mat = new Material(shader);
            mat.SetColor("_SkyTint", new Color(0.58f, 0.42f, 0.32f));
            mat.SetColor("_GroundColor", new Color(0.20f, 0.17f, 0.15f));
            mat.SetFloat("_AtmosphereThickness", 2.2f);   // kalin, tozlu hava
            mat.SetFloat("_Exposure", 0.90f);
            mat.SetFloat("_SunDisk", 1f);                 // Simple
            mat.SetFloat("_SunSize", 0.05f);
            mat.SetFloat("_SunSizeConvergence", 3f);

            AssetDatabase.CreateAsset(mat, path);
            return "Sky_Kiyamet.mat olusturuldu (Skybox/Procedural).";
        }

        // ------------------------------------------------------------- zemin

        /// <summary>Uretilen zemin dokusunun kenari (px).</summary>
        const int FloorTexSize = 512;

        /// <summary>
        /// How many metres one repeat of the floor texture covers.
        ///
        /// 3 m, arrived at by looking. The scene's grid material repeats every metre, which at
        /// this texture size is ~2 mm per texel — far more detail than a floor seen from standing
        /// height can use, and a pattern that small reads as noise rather than as concrete. Going
        /// the other way, the tighter the repeat the more copies are in view at once and the
        /// sooner the eye catches the lattice; at 2 m it was still catchable across an open
        /// floor. 3 m keeps ~6 mm per texel and halves the number of repeats in a room.
        ///
        /// A SINGLE TILED TEXTURE HAS A CEILING HERE. Far enough out, any one repeating texture
        /// shows its grid; killing it properly needs a second layer at a different scale, which
        /// is a shader change and not this phase's job. It matters less than this dev scene
        /// suggests: the real arena is a scanned room a few metres across, not a 40 m plane.
        /// </summary>
        const float FloorMetresPerTile = 3f;

        /// <summary>
        /// Writes the apocalypse ground: a seamless texture and a LIT material to carry it.
        ///
        /// GENERATED, NOT PAINTED — the same call as the procedural sky. There is no ground art
        /// in this project (the eight Ground-category entries are 40x40 m landscape tiles from
        /// the forest pack, all retired), so the choice was a generated texture or a flat colour,
        /// and a flat colour under a low sun looks like a missing texture.
        ///
        /// SIMPLE LIT, not Lit. This is one 40 m plane filling the bottom half of both eyes —
        /// pure fill rate, the one thing a Quest has least of. Simple Lit drops the metallic and
        /// the BRDF work that a dusty non-metal floor has no use for anyway.
        /// </summary>
        static string EnsureKiyametFloor()
        {
            const string texPath = SkyFolder + "/T_Kiyamet_Floor.png";
            const string matPath = SkyFolder + "/M_Kiyamet_Floor.mat";

            var msg = new StringBuilder();

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                var generated = GenerateGroundTexture(FloorTexSize);
                File.WriteAllBytes(texPath, generated.EncodeToPNG());
                Object.DestroyImmediate(generated);
                AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
                ConfigureGroundTexture(texPath);
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                msg.Append("T_Kiyamet_Floor.png uretildi (" + FloorTexSize + "px, kesintisiz). ");
            }
            else msg.Append("T_Kiyamet_Floor.png zaten var. ");

            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
                return msg.Append("M_Kiyamet_Floor.mat zaten var.").ToString();

            var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return msg.Append("UYARI: URP shader bulunamadi.").ToString();

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            // Kuru toz/beton: parlaklik neredeyse yok. Yuksek smoothness alcak bir gunesle
            // birlestiginde zemini islak asfalta cevirir.
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.06f);
            if (mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", new Color(0.12f, 0.11f, 0.10f));

            // Ground duzlemi 40 m ve UV'si 0..1 -> bir tekrar FloorMetresPerTile metre olsun.
            float tiling = 40f / FloorMetresPerTile;
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));

            AssetDatabase.CreateAsset(mat, matPath);
            return msg.Append("M_Kiyamet_Floor.mat olusturuldu (Simple Lit, " +
                              FloorMetresPerTile + " m/tekrar).").ToString();
        }

        static void ConfigureGroundTexture(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;
            imp.textureType = TextureImporterType.Default;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            // MIPMAP SART: zemin neredeyse hep dar acidan goruluyor, mipmapsiz bir zemin
            // uzakta cizir cizir titrer ve VR'da bu titreme bas agritir.
            imp.mipmapEnabled = true;
            imp.maxTextureSize = FloorTexSize;
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.sRGBTexture = true;
            imp.SaveAndReimport();
        }

        /// <summary>
        /// Seamless dusty-asphalt: large patches, fine grain, soot stains and thin cracks.
        ///
        /// TILEABLE BY CONSTRUCTION. <see cref="Mathf.PerlinNoise"/> does not repeat on its own,
        /// so every sample is the blend of four copies shifted by exactly the domain width — at
        /// u=0 the blend is entirely the unshifted copy and at u=1 entirely the shifted one, and
        /// those two are the same value. A texture that does not tile would put a visible seam
        /// every 2 m across the whole floor.
        /// </summary>
        static Texture2D GenerateGroundTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var px = new Color[size * size];

            var wet = new Color(0.225f, 0.205f, 0.180f);   // koyu, nemli asfalt
            var dry = new Color(0.400f, 0.358f, 0.305f);   // kuru beton
            var dust = new Color(0.580f, 0.510f, 0.410f);  // toz / kul
            var crackCol = new Color(0.130f, 0.116f, 0.100f);

            // HER ALAN KENDI ARALIGINA GERILIYOR (bkz. NoiseField). Ilk surum bunu yapmiyordu
            // ve doku DUMDUZ KAHVERENGI cikti: hem fBm oktavlarin ortalamasi hem de kesintisiz
            // harman dort kopyanin ortalamasi oldugu icin degerler 0.5 etrafinda dar bir bantta
            // toplaniyor, elle yazilmis 0.34/0.62 gibi esikler ise o bandin DISINDA kaliyordu.
            float[] patch = NoiseField(size, 5f, 4, 71f);
            float[] grain = NoiseField(size, 34f, 3, 11f);
            float[] soot = NoiseField(size, 3f, 3, 133f);
            float[] speck = NoiseField(size, 90f, 2, 307f);
            float[] crack = RidgeField(size, 9f, 3, 211f);

            for (int i = 0; i < px.Length; i++)
            {
                // BUYUK OLCEKLI LEKELER BILEREK ZAYIF. Tekrari ele veren sey dusuk frekans:
                // 2 m'de bir tekrarlayan bir doku, ince tanesiyle degil iri acik lekeleriyle
                // yakalanir — goz o lekelerin izgarasini gorur. Ince detay ise tekrarlarken
                // fark edilmez, o yuzden kontrast tane ve catlaklarda tutuluyor.
                Color c = Color.Lerp(wet, dry, Step01(0.28f, 0.85f, patch[i]));
                c = Color.Lerp(c, dust, Step01(0.68f, 0.96f, patch[i]) * 0.42f);

                // Tane: renk CARPANI olarak, boylece koyu bolge koyu kalir.
                c *= 0.86f + grain[i] * 0.30f;

                // Is / kararma lekeleri
                c = Color.Lerp(c, wet * 0.55f, Step01(0.76f, 0.99f, soot[i]) * 0.50f);

                // Catlaklar — INCE agsi cizgiler. Esik bandi dar tutuluyor: 2 m'lik bir
                // tekrarda genis bir band, zeminde 10 cm'lik siyah seritler demek olur.
                c = Color.Lerp(c, crackCol, Step01(0.955f, 0.996f, crack[i]) * 0.85f);

                // Cakil benekleri
                c = Color.Lerp(c, dust * 1.15f, Step01(0.90f, 0.99f, speck[i]) * 0.45f);

                c.a = 1f;
                px[i] = c;
            }

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // ------------------------------------------------------------- 49b / 49c

        [MenuItem("Tools/VR Multiplayer/49b. Temayi Onizle (KIYAMET)")]
        public static void PreviewMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Preview(KiyametId), "Tamam");

        /// <summary>
        /// Applies a theme to the open scene so it can be judged without entering play mode.
        ///
        /// THIS DIRTIES THE SCENE'S LIGHTING SETTINGS, and the report says so. Sky, ambient and
        /// fog live in the scene asset, so saving the scene after a preview BAKES the theme in
        /// as the scene's authored look — at which point <see cref="WorldTheme"/> would capture
        /// the themed values as its baseline and "restore" would stop meaning anything. 49c
        /// undoes it; saving without running 49c is what to avoid.
        /// </summary>
        public static string Preview(string themeId)
        {
            var lib = ThemeLibrary.Instance;
            if (lib.Count == 0)
                return "Tema kutuphanesi bos.\n\nOnce menu 49 calistir.";

            if (lib.ById(themeId) == null)
                return $"'{themeId}' temasi kutuphanede yok.\n\nMenu 49 calistir.";

            WorldTheme.Apply(themeId);

            return $"'{lib.NameOf(themeId)}' sahneye uygulandi.\n\n" +
                   "DIKKAT: gokyuzu/ortam/sis SAHNEYE, zemin materyali de zemin NESNESINE ait " +
                   "ayarlardir. Onizlemeden sonra sahneyi KAYDEDERSEN tema sahnenin kendi " +
                   "gorunumu olarak yapisir.\n\n" +
                   "Bakmayi bitirince menu 49c ile geri al.";
        }

        [MenuItem("Tools/VR Multiplayer/49c. Tema Onizlemesini Kaldir")]
        public static void RestoreMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Restore(), "Tamam");

        public static string Restore()
        {
            WorldTheme.Restore();
            return "Sahnenin kendi gorunumune donuldu.\n\n" +
                   "Not: geri alma kaydi BELLEKTE tutulur ve script derlemesinde silinir. " +
                   "49b ile 49c arasinda kod degistirip Unity'yi derletirsen kayit ucar; o " +
                   "durumda sahnenin isik ayarlari temada kalmis olabilir — 'git diff " +
                   "Assets/Scenes' ile bak, tek satirlik bir fark bile olsa geri al.";
        }

        // ------------------------------------------------------------- 49d

        [MenuItem("Tools/VR Multiplayer/49d. Secili Haritaya Tema Ata (KIYAMET)")]
        public static void AssignToMapMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", AssignToSelectedMap(KiyametId), "Tamam");

        /// <summary>
        /// Stamps <see cref="MapLayout.themeId"/> into the selected map file.
        ///
        /// GOES THROUGH FromJson/ToJson INSTEAD OF EDITING TEXT: loading migrates the file to
        /// the current schema on the way in, so a v2 map picks up its rotation fix here too and
        /// what gets written back is always a current-version map. A textual patch would leave
        /// the version stamp lying.
        /// </summary>
        public static string AssignToSelectedMap(string themeId)
        {
            var obj = Selection.activeObject;
            string path = obj != null ? AssetDatabase.GetAssetPath(obj) : "";

            if (string.IsNullOrEmpty(path) || !path.EndsWith(".json") ||
                !path.Replace('\\', '/').StartsWith(MapsFolder))
            {
                return "Once Project penceresinde bir HARITA sec.\n\n" +
                       $"Haritalar: {MapsFolder}/*.json\n\n" +
                       "Mevcut haritalar:\n - " + string.Join("\n - ", MapNames());
            }

            var layout = MapLayout.FromJson(File.ReadAllText(path));
            if (layout == null) return $"'{path}' okunamadi — gecerli bir harita degil.";

            var lib = ThemeLibrary.Instance;
            if (lib.ById(themeId) == null)
                return $"'{themeId}' temasi kutuphanede yok.\n\nOnce menu 49 calistir.";

            string before = layout.themeId ?? "";
            layout.themeId = themeId;
            File.WriteAllText(path, layout.ToJson());
            AssetDatabase.Refresh();

            return $"'{layout.name}' haritasinin temasi: " +
                   $"{(before == "" ? "TEMASIZ" : before)} -> {lib.NameOf(themeId)}\n\n" +
                   "Haritayi acan her istemci bu gorunumde acar.";
        }

        static IEnumerable<string> MapNames()
        {
            if (!Directory.Exists(MapsFolder)) return new[] { "(klasor yok)" };
            var names = new List<string>();
            foreach (var f in Directory.GetFiles(MapsFolder, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
            if (names.Count == 0) names.Add("(kayitli harita yok)");
            return names;
        }

        // ------------------------------------------------------------- yardimci

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
