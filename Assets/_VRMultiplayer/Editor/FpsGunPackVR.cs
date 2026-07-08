using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// One-click "make the FPS Gun Pack 4K VR/Quest-ready" tool.
    ///   Tools ▸ VR Multiplayer ▸ 21. FPS Gun Pack -> VR/Quest hazirla
    ///
    /// The pack ships with Built-in (Standard) materials and 4K textures, so on our URP/Quest
    /// project the guns render MAGENTA and the textures would tank the headset framerate. This
    /// does both fixes in one pass, scoped ONLY to the pack folder:
    ///   1) Converts every Standard material to URP/Lit, remapping albedo/normal/metallic/AO/emission.
    ///   2) Adds an Android platform override to every texture: max 1024 + ASTC 6x6 (Quest-friendly).
    ///
    /// Idempotent: already-URP materials are skipped, so re-running is safe. Materials keep their
    /// GUID, so every prefab that references them updates automatically — no prefab editing needed.
    /// </summary>
    public static class FpsGunPackVR
    {
        const string PackRoot = "Assets/FPS Gun Pack 4K";
        const int QuestMaxTextureSize = 1024;

        [MenuItem("Tools/VR Multiplayer/21. FPS Gun Pack -> VR Quest optimize")]
        static void PrepareForVR()
        {
            if (!AssetDatabase.IsValidFolder(PackRoot))
            {
                EditorUtility.DisplayDialog("FPS Gun Pack",
                    "Klasor bulunamadi:\n" + PackRoot +
                    "\n\nPaketi 'Assets/FPS Gun Pack 4K' altina import ettiginden emin ol.", "Tamam");
                return;
            }

            int mats = ConvertMaterialsToURP();
            int texs = OptimizeTexturesForQuest();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string msg = $"FPS Gun Pack VR'a hazir:\n\n" +
                         $"• {mats} materyal URP/Lit'e cevrildi (magenta gitti)\n" +
                         $"• {texs} doku Android/1024/ASTC yapildi (Quest performansi)";
            Debug.Log("[FPS Gun Pack] " + msg.Replace("\n", " "));
            EditorUtility.DisplayDialog("FPS Gun Pack -> VR", msg, "Tamam");
        }

        // ---- 1) Standard -> URP/Lit -------------------------------------------------

        static int ConvertMaterialsToURP()
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null)
            {
                Debug.LogError("[FPS Gun Pack] URP/Lit shader bulunamadi — proje URP degil mi?");
                return 0;
            }

            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { PackRoot });
            int converted = 0;

            foreach (string g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                string sn = mat.shader.name;
                bool isBuiltin = sn == "Standard" || sn == "Standard (Specular setup)" ||
                                 sn == "Autodesk Interactive";
                if (!isBuiltin) continue; // already URP (or custom) — leave it

                // Capture Standard properties BEFORE swapping the shader (some names don't carry over).
                Texture albedo   = Get(mat, "_MainTex");
                Color baseColor  = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                Texture normal   = Get(mat, "_BumpMap");
                Texture metalGl  = Get(mat, "_MetallicGlossMap");
                Texture occl     = Get(mat, "_OcclusionMap");
                Texture emisTex  = Get(mat, "_EmissionMap");
                Color emisColor  = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
                float metallic   = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
                float smoothness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.5f;
                bool emissionOn  = mat.IsKeywordEnabled("_EMISSION") ||
                                   (emisTex != null) || emisColor.maxColorComponent > 0.001f;

                mat.shader = urpLit;

                // Metallic workflow, base surface
                if (mat.HasProperty("_WorkflowMode")) mat.SetFloat("_WorkflowMode", 1f); // 1 = Metallic
                if (albedo != null) mat.SetTexture("_BaseMap", albedo);
                mat.SetColor("_BaseColor", baseColor);
                mat.SetFloat("_Metallic", metallic);
                mat.SetFloat("_Smoothness", smoothness);

                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                }
                if (metalGl != null)
                {
                    mat.SetTexture("_MetallicGlossMap", metalGl);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
                if (occl != null)
                {
                    mat.SetTexture("_OcclusionMap", occl);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                }
                if (emissionOn)
                {
                    if (emisTex != null) mat.SetTexture("_EmissionMap", emisTex);
                    mat.SetColor("_EmissionColor", emisColor);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                }

                EditorUtility.SetDirty(mat);
                converted++;
            }
            return converted;
        }

        static Texture Get(Material m, string prop) =>
            m.HasProperty(prop) ? m.GetTexture(prop) : null;

        // ---- 2) 4K textures -> Quest-friendly (Android override) --------------------

        static int OptimizeTexturesForQuest()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { PackRoot });
            int done = 0;

            foreach (string g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;

                var android = new TextureImporterPlatformSettings
                {
                    name = "Android",
                    overridden = true,
                    maxTextureSize = QuestMaxTextureSize,
                    format = TextureImporterFormat.ASTC_6x6,
                    textureCompression = TextureImporterCompression.Compressed,
                    compressionQuality = 50,
                };
                ti.SetPlatformTextureSettings(android);
                ti.SaveAndReimport();
                done++;
            }
            return done;
        }
    }
}
