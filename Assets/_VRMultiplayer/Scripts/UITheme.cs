using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Holds the centralized theme for the game's UI (colors, fonts, materials).
    /// Used by PlayerHUD and OverheadHealthBar to ensure a consistent look.
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

        /// <summary>
        /// Creates a material with a safe, opaque shader that won't turn magenta in builds.
        /// </summary>
        public static Material CreateLitMaterial(Color color)
        {
            // HUD elemanları için ışıklandırmadan etkilenmeyen bir shader kullanalım.
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color"); // URP olmayan projeler için fallback
            if (shader == null) shader = Shader.Find("Sprites/Default"); // Son çare
            var m = new Material(shader);
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
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default"); // Fallback
            var m = new Material(shader);
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