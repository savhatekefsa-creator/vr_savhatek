using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   54. Yanan Varil Uret — alev + titreyen isik, KIYAMET paletine
    ///
    /// WHY NOT WarFX, WHICH IS ALREADY IN THE PROJECT: its fire prefabs reference the Desktop
    /// material set and custom <c>WFX/*</c> shaders written for the built-in pipeline. They may
    /// well render under URP, but "may well" is not something to put in a prop the player places
    /// twenty of — and the soft-particle smoke wants a depth texture this mobile renderer does
    /// not produce. A generated flame is smaller, URP-native, and the same call as the sky, the
    /// floor and the shard patch.
    ///
    /// THE LIGHT IS THE EFFECT. See <see cref="FlickerLight"/>: at any distance a fire is mostly
    /// the moving warm light it throws, and that costs one float per frame.
    /// </summary>
    public static class BurningBarrelSetup
    {
        const string PropFolder = "Assets/_VRMultiplayer/Resources/ConstructorProps";
        const string DrumPrefab = "Assets/_VRMultiplayer/Art/apocalypse/Prefabs/Radiation_Drum.prefab";
        const string PrefabName = "Burning_Barrel";
        const string PropId = "burning_barrel";
        const int SpriteSize = 64;

        [MenuItem("Tools/VR Multiplayer/54. Yanan Varil Uret")]
        public static void RunMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Run(), "Tamam");

        public static string Run()
        {
            var log = new StringBuilder();
            log.AppendLine(EnsureFlameMaterial(out Material flameMat));
            log.AppendLine(EnsurePrefab(flameMat));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine();
            log.AppendLine(Register());
            return log.ToString();
        }

        // ------------------------------------------------------------- alev malzemesi

        static string EnsureFlameMaterial(out Material mat)
        {
            const string texPath = PropFolder + "/T_Flame.png";
            const string matPath = PropFolder + "/M_Flame.mat";
            var msg = new StringBuilder();

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                var made = BuildFlameSprite(SpriteSize);
                File.WriteAllBytes(texPath, made.EncodeToPNG());
                Object.DestroyImmediate(made);
                AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);

                var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = TextureImporterType.Default;
                    imp.wrapMode = TextureWrapMode.Clamp;   // kenardan renk cekmesin
                    imp.alphaIsTransparency = true;
                    imp.maxTextureSize = SpriteSize;
                    imp.SaveAndReimport();
                }
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                msg.Append("Alev sprite'i uretildi. ");
            }
            else msg.Append("Alev sprite'i zaten var. ");

            mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat != null) return msg.Append("Alev malzemesi zaten var.").ToString();

            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) { mat = null; return msg.Append("UYARI: URP partikul shader'i yok.").ToString(); }

            mat = new Material(shader);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);

            // TOPLAMALI HARMAN. Alfa harmani alevi zeminin uzerine yapistirir; ates
            // arkasindaki her seye EKLENIR, ust uste binen zerreler birbirini parlatir
            // ve ortasi kendiliginden beyazlar.
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 2f);                     // Additive
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;

            AssetDatabase.CreateAsset(mat, matPath);
            return msg.Append("Alev malzemesi uretildi (toplamali).").ToString();
        }

        /// <summary>
        /// Yumusak, merkezi parlak yuvarlak leke — alev zerresinin govdesi.
        ///
        /// Kenari SERT BITMEMELI: keskin kenarli bir zerre, ust uste binince tek tek daireler
        /// olarak secilir ve ates "kabarcik yigini" gibi gorunur. Yumusak dusus, zerrelerin
        /// birbirinin icinde erimesini saglayan sey.
        /// </summary>
        static Texture2D BuildFlameSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            float[] grain = ProceduralNoise.NoiseField(size, 6f, 2, 811f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float dx = x / (float)(size - 1) - 0.5f;
                    float dy = y / (float)(size - 1) - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;

                    float a = 1f - ProceduralNoise.Step01(0.05f, 1f, d);
                    a *= a;                                   // merkeze dogru yogunlassin
                    a *= 0.75f + grain[i] * 0.5f;             // duz daire olmasin
                    px[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            }

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // ------------------------------------------------------------- prefab

        static string EnsurePrefab(Material flameMat)
        {
            string path = PropFolder + "/" + PrefabName + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return "Prefab zaten var.";
            if (flameMat == null) return "Prefab uretilemedi (alev malzemesi yok).";

            var drum = AssetDatabase.LoadAssetAtPath<GameObject>(DrumPrefab);
            if (drum == null) return "Radiation_Drum.prefab bulunamadi.";

            // Varilin USTUNU olcup alevi oraya koyuyoruz. Sabit bir yukseklik yazmak,
            // model degistiginde alevin havada ya da varilin icinde kalmasi demekti.
            //
            // KOK OLCEGIYLE CARPILIYOR: MeasureLocalBounds kokun kendi localScale'ini
            // DISARIDA birakir (bkz. PropDef). Varil 0.62'ye kucultuldugunde bu carpim
            // atlandi ve alev varilin 40 cm ustunde havada asili kaldi.
            float top = PropDef.MeasureLocalBounds(drum).max.y *
                        Mathf.Abs(drum.transform.localScale.y);

            var root = new GameObject(PrefabName);
            try
            {
                var body = (GameObject)PrefabUtility.InstantiatePrefab(drum, root.transform);
                body.name = "Barrel";
                body.transform.localPosition = Vector3.zero;

                // Alev varille birlikte kuculmeli: sabit boyutlu zerreler 0.62'ye inmis bir
                // varilin uzerinde varilden GENIS bir ates yapardi.
                float s = Mathf.Abs(drum.transform.localScale.y);
                BuildFlame(root.transform, flameMat, top, s);
                BuildLight(root.transform, top, s);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                return $"Prefab uretildi (alev + titreyen isik, varil ustu {top:0.00} m).";
            }
            finally { Object.DestroyImmediate(root); }
        }

        static void BuildFlame(Transform parent, Material mat, float top, float s)
        {
            var go = new GameObject("Flame");
            go.transform.SetParent(parent, false);
            // Agzin biraz ICINDE: alevin tabani kenar tarafindan gizlensin, boylece
            // varilin uzerinde duran degil, icinden cikan bir ates olsun.
            go.transform.localPosition = new Vector3(0f, top - 0.09f * s, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            // YOGUN VE KISA OMURLU. Ilk deger (26/sn, 0.45-0.85 sn omur, 0.18 m boy) aynda
            // ~15 zerre birakiyordu ve bunlar birbirine hic degmiyordu: sonuc alev degil,
            // havada suzulen turuncu noktalardi. Bir alev govdesi UST USTE BINEN zerrelerden
            // olusur — toplamali harmanda ortasi o yuzden beyazlar. Hizli ve kisa omurlu
            // tutmak da zerreleri agzin yakininda tutuyor; uzun omur onlari kivilcim gibi
            // dagitiyordu.
            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.30f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.20f * s, 0.48f * s);
            main.startSize = new ParticleSystem.MinMaxCurve(0.30f * s, 0.52f * s);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = -0.02f;                 // sicak hava yukselir
            main.maxParticles = 60;
            // YEREL uzay: alev varille birlikte tasinsin. Dunya uzayinda, insa modunda
            // varili suruklerken alev geride kalirdi.
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.playOnAwake = true;

            var em = ps.emission;
            em.rateOverTime = 70f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.20f * s;
            shape.radiusThickness = 1f;                    // agzin her yerinden, halka degil

            // SICAK -> SOGUK. Alevin ic mantigi bu: en genc zerre en sicak (beyaza yakin
            // sari), yaslandikca turuncuya, sonra koyu kirmiziya duser ve soner. Ters
            // sirada bir gradyan, ates yerine dagilan bir duman gibi gorunur.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.00f, 0.95f, 0.70f), 0.00f),
                    new GradientColorKey(new Color(1.00f, 0.62f, 0.18f), 0.35f),
                    new GradientColorKey(new Color(0.70f, 0.17f, 0.03f), 0.75f),
                    new GradientColorKey(new Color(0.25f, 0.05f, 0.01f), 1.00f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.15f),
                    new GradientAlphaKey(0.55f, 0.6f), new GradientAlphaKey(0f, 1f),
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            var curve = new AnimationCurve(
                new Keyframe(0f, 0.55f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.25f));
            size.size = new ParticleSystem.MinMaxCurve(1f, curve);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingFudge = -2f;   // varilin agzindan tasan zerreler govdenin onunde cizilsin
        }

        static void BuildLight(Transform parent, float top, float s)
        {
            var go = new GameObject("FireLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, top + 0.18f * s, 0f);

            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.63f, 0.28f);
            // Menzil alevle birlikte kuculmuyor: kucuk bir ates de odayi aydinlatir, ve
            // isik zaten bu propun asil isi.
            l.range = 3.8f;
            l.intensity = 2.2f;
            // GOLGE YOK. Mobil URP'de ek isiklarin golgesi zaten kapali (bkz. Mobile_RPAsset)
            // ve acik olsa da butce golge veren TEK isik icin ayrilmis durumda: temanin gunesi.
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.Auto;

            var f = go.AddComponent<FlickerLight>();
            f.baseIntensity = 2.2f;
            f.amplitude = 0.42f;
            f.speed = 7f;
        }

        // ------------------------------------------------------------- kayit

        static string Register()
        {
            string scan = ConstructorSetup.ScanPropLibrary();
            var lib = PropLibrary.Instance;
            var def = lib.ById(PropId);
            if (def == null) return "Tarama sonrasi '" + PropId + "' bulunamadi.\n\n" + scan;

            def.category = PropCategory.Cover;
            def.paletteId = "kiyamet";
            def.snap = PropSnap.Floor;
            def.networked = false;
            def.hiddenInPalette = false;
            def.freeRotation = true;

            lib.InvalidateIndex();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();

            return $"Kutuphaneye kaydedildi: Cover / KIYAMET, ayak izi {def.sizeMeters.x:0.00} x " +
                   $"{def.sizeMeters.y:0.00} m.\n\n" + scan;
        }
    }
}
