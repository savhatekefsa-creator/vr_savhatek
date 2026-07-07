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
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default"); // Fallback
            var m = new Material(shader);
            m.SetFloat("_Smoothness", 0f);
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
    }
}