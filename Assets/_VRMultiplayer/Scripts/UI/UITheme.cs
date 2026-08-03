using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Holds the centralized theme for the game's UI (colors, fonts, materials).
    /// Used by PlayerHUD and the UI effects to ensure a consistent look.
    /// </summary>
    public static class UITheme
    {
        // --- Color Palette ---
        public static readonly Color Background = new Color(0.08f, 0.08f, 0.08f, 1f);
        public static readonly Color Text = Color.white;
        public static readonly Color HealthFull = Color.green;
        public static readonly Color HealthMid = Color.yellow;
        public static readonly Color HealthLow = Color.red;

        // --- Fonts ---
        public const float NameCharacterSize = 0.06f;
        public const int NameFontSize = 60;

        /// <summary>Build'de garanti bulunan unlit shader zinciri — calisma aninda malzeme
        /// ureten HER yer bunu kullanmali (4 ayri kopyasi vardi). URP/Unlit sahnede referanssiz
        /// kalirsa build'den strip edilebilir; URP/Lit oda malzemeleri sayesinde hep gemidedir.</summary>
        public static Shader SafeUnlitShader
        {
            get
            {
                var s = Shader.Find("Universal Render Pipeline/Unlit");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
                if (s == null) s = Shader.Find("Unlit/Color");
                if (s == null) s = Shader.Find("Sprites/Default");
                return s;
            }
        }

        /// <summary>
        /// Creates a material with a safe, opaque shader that won't turn magenta in builds.
        /// </summary>
        public static Material CreateLitMaterial(Color color)
        {
            // HUD elemanları için ışıklandırmadan etkilenmeyen bir shader kullanalım.
            var m = new Material(SafeUnlitShader);
            SetMaterialColor(m, color);
            return m;
        }

        /// <summary>
        /// Sets the color on a material, checking for both URP and built-in property names.
        /// </summary>
        public static void SetMaterialColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static Color GetHealthColor(float ratio)
        {
            return ratio > 0.5f
                ? Color.Lerp(HealthMid, HealthFull, (ratio - 0.5f) * 2f)
                : Color.Lerp(HealthLow, HealthMid, ratio * 2f);
        }

        // --- Health Bar Gradient ---

        static Texture2D _healthGradient;

        /// <summary>
        /// Soldan saga kirmizi -> sari -> yesil giden yatay degrade dokusu (bir kez uretilir).
        /// </summary>
        public static Texture2D HealthGradientTexture
        {
            get
            {
                if (_healthGradient == null)
                {
                    const int W = 256;
                    _healthGradient = new Texture2D(W, 1, TextureFormat.RGBA32, false);
                    _healthGradient.wrapMode = TextureWrapMode.Clamp;
                    for (int x = 0; x < W; x++)
                        _healthGradient.SetPixel(x, 0, GetHealthColor(x / (float)(W - 1)));
                    _healthGradient.Apply();
                }
                return _healthGradient;
            }
        }

        /// <summary>
        /// Degrade dokulu can bari materyali olusturur. brightness &lt; 1 verilirse degradenin
        /// karartilmis hali cikar (bos kismi gosteren zemin icin).
        /// </summary>
        public static Material CreateHealthBarMaterial(float brightness = 1f)
        {
            float b = Mathf.Clamp01(brightness);
            var m = CreateLitMaterial(new Color(b, b, b, 1f));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", HealthGradientTexture);
            else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", HealthGradientTexture);
            return m;
        }

        /// <summary>
        /// Alfa ile solabilen (transparan) unlit materyal olusturur — hasar flasi gibi efektler icin.
        /// </summary>
        public static Material CreateTransparentMaterial(Color color)
        {
            var m = new Material(SafeUnlitShader);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f); // Transparent
                m.SetFloat("_Blend", 0f);   // Alpha blend
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            SetMaterialColor(m, color);
            return m;
        }

        // --- Radyal menu parcalari ---
        // NOT: WeaponSelectorUI ayni uc yardimciyi kendi icinde PRIVATE tutuyor (bu ortak surum
        // ondan sonra dogdu). Silah carki calisan ve ince ayarlanmis bir sistem oldugu icin
        // dokunulmadi; ileride oradaki kopyalar buraya baglanabilir.

        /// <summary>
        /// Ic/dis yaricapli yay (pasta dilimi) mesh'i, XY duzleminde. Iki yuzu de cizer —
        /// carkin arkasindan bakildiginda kaybolmasin.
        /// </summary>
        public static Mesh ArcMesh(float innerRadius, float outerRadius, float fromDeg, float toDeg, int segments)
        {
            var m = new Mesh();
            var v = new Vector3[(segments + 1) * 2];
            var t = new int[segments * 12];
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, (float)i / segments);
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v[i * 2] = dir * innerRadius;
                v[i * 2 + 1] = dir * outerRadius;
            }
            for (int i = 0; i < segments; i++)
            {
                int b = i * 2, k = i * 12;
                t[k] = b; t[k + 1] = b + 1; t[k + 2] = b + 2;
                t[k + 3] = b + 1; t[k + 4] = b + 3; t[k + 5] = b + 2;
                t[k + 6] = b + 2; t[k + 7] = b + 1; t[k + 8] = b;
                t[k + 9] = b + 2; t[k + 10] = b + 3; t[k + 11] = b + 1;
            }
            m.vertices = v;
            m.triangles = t;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Yari saydam, isiktan etkilenmeyen, DERINLIK YAZMAYAN overlay materyali.
        /// <see cref="CreateTransparentMaterial"/>'dan farki: renderQueue elle verilebilir, yani
        /// dilim / gobek / yazi katmanlari birbirinin ustune belirli sirayla cizilir.
        /// </summary>
        public static Material CreateOverlayMaterial(Color color, int renderQueue)
        {
            var m = CreateTransparentMaterial(color);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // iki yuz
            m.renderQueue = renderQueue;
            return m;
        }

        /// <summary>
        /// Dunya-uzayi yazi etiketi. Unity 6'da TextMesh VARSAYILAN FONTSUZ gelir — font
        /// atanmazsa yazi hic gorunmez (kol saatinde ogrenilen ders); burada bir kez halledildi.
        /// </summary>
        public static TextMesh CreateLabel(Transform parent, string text, Vector3 localPosition,
            float characterSize, int renderQueue)
        {
            var go = new GameObject("Label_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = characterSize;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Text;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var mr = go.GetComponent<MeshRenderer>();
            if (tm.font != null) mr.material = tm.font.material;
            mr.material.renderQueue = renderQueue;
            return tm;
        }

        /// <summary>
        /// Dokunun yalnizca sol [0..ratio] bolumunu gosterecek sekilde tiling ayarlar; boylece
        /// bar kisaldikca degrade "sıkışmaz", soldan itibaren acilir/kapanir.
        /// </summary>
        public static void SetGradientFill(Material m, float ratio)
        {
            var tiling = new Vector2(Mathf.Max(ratio, 0.0001f), 1f);
            if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", tiling);
            if (m.HasProperty("_MainTex")) m.SetTextureScale("_MainTex", tiling);
        }
    }
}