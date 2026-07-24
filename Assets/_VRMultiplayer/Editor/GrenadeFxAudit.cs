using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Bomba patlama efektlerini Play moduna girmeden denetler: config'lerin explodeFx
    /// referansi gercekten cozuluyor mu, prefabin icinde kac partikul sistemi/renderer var,
    /// hangi materyal hangi shader'i kullaniyor ve o shader URP'de CIZILEBILIR mi.
    ///
    /// "Efekt gorunmuyor" sikayetinin uc olasi sebebi vardir ve ucunu de burada ayirt ederiz:
    ///   1) explodeFx referansi bos/kirik  -> hic Instantiate edilmiyor
    ///   2) materyalin shader'i eksik/legacy -> obje var ama cizilmiyor
    ///   3) partikul sistemi playOnAwake degil -> obje var, cizilebilir ama hic oynamiyor
    /// </summary>
    public static class GrenadeFxAudit
    {
        const string ConfigFolder = "Assets/_VRMultiplayer/Resources/GrenadeConfigs";

        [MenuItem("Tools/VR Multiplayer/41. Bomba Efektlerini Denetle")]
        public static void Audit()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BOMBA EFEKT DENETIMI ===");

            var guids = AssetDatabase.FindAssets("t:GrenadeConfig", new[] { ConfigFolder });
            if (guids.Length == 0)
                sb.AppendLine("! " + ConfigFolder + " altinda hic GrenadeConfig bulunamadi.");

            int problems = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var cfg = AssetDatabase.LoadAssetAtPath<GrenadeConfig>(path);
                if (cfg == null) continue;

                sb.AppendLine();
                sb.AppendLine($"--- {cfg.name}  (tip: {cfg.type}, fxScale: {cfg.fxScale}) ---");

                if (cfg.explodeFx == null)
                {
                    sb.AppendLine("  !! explodeFx BOS ya da KIRIK REFERANS -> patlamada hicbir sey");
                    sb.AppendLine("     olusturulmaz. Inspector'dan bir WarFX prefabi surukle.");
                    problems++;
                    continue;
                }

                string fxPath = AssetDatabase.GetAssetPath(cfg.explodeFx);
                sb.AppendLine($"  explodeFx: {cfg.explodeFx.name}");
                sb.AppendLine($"    yol: {fxPath}");
                sb.AppendLine($"    kok aktif mi: {cfg.explodeFx.activeSelf}");

                // Referans prefabin KOKUNE mi bakiyor? Legacy (ikili) prefablarda fileID kok
                // olmayabilir; o zaman Instantiate yalnizca bir ALT parcayi dogurur ve efektin
                // buyuk kismi hic olusmaz. Bu, "efekt neredeyse yok" tablosunun tipik sebebi.
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(fxPath);
                if (root != null && root != cfg.explodeFx)
                {
                    sb.AppendLine($"    !! Referans prefabin KOKU DEGIL, '{cfg.explodeFx.name}' adli " +
                                  $"ALT objesi. Kok: '{root.name}'. Patlamada efektin yalnizca bu " +
                                  "parcasi olusur. (42 numarali menu onarir.)");
                    problems++;
                }

                problems += AuditPrefab(cfg.explodeFx, sb);
            }

            sb.AppendLine();
            sb.AppendLine(problems == 0
                ? "SONUC: efekt zincirinde sorun BULUNAMADI (referans, shader ve partikuller saglam)."
                : $"SONUC: {problems} sorun bulundu — yukaridaki '!!' satirlarina bak.");

            Debug.Log(sb.ToString());
        }

        /// <summary>explodeFx referanslarini prefabin KOK objesine sabitler. Referans bir alt
        /// objeye bakiyorsa (ya da .asset dosyasindaki guid cozulup nesne null kaliyorsa) dogru
        /// koke yeniden baglanir. Referansin guid'i .asset METNINDEN okunur; boylece Unity
        /// tarafinda null gorunen kirik referanslar da onarilabilir.</summary>
        [MenuItem("Tools/VR Multiplayer/42. Bomba Efekt Referanslarini Onar")]
        public static void Repair()
        {
            var guids = AssetDatabase.FindAssets("t:GrenadeConfig", new[] { ConfigFolder });
            var log = new StringBuilder("=== BOMBA EFEKT REFERANS ONARIMI ===");
            int fixedCount = 0;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var cfg = AssetDatabase.LoadAssetAtPath<GrenadeConfig>(path);
                if (cfg == null) continue;

                string fxGuid = ReadExplodeFxGuid(path);
                if (string.IsNullOrEmpty(fxGuid))
                {
                    log.AppendLine($"\n{cfg.name}: .asset icinde explodeFx guid'i yok — atlandi.");
                    continue;
                }

                string fxPath = AssetDatabase.GUIDToAssetPath(fxGuid);
                var root = string.IsNullOrEmpty(fxPath)
                    ? null : AssetDatabase.LoadAssetAtPath<GameObject>(fxPath);
                if (root == null)
                {
                    log.AppendLine($"\n{cfg.name}: guid {fxGuid} bir prefaba cozulmedi — atlandi.");
                    continue;
                }

                if (cfg.explodeFx == root)
                {
                    log.AppendLine($"\n{cfg.name}: zaten koke bagli ({root.name}).");
                    continue;
                }

                string before = cfg.explodeFx != null ? cfg.explodeFx.name : "<null>";
                Undo.RecordObject(cfg, "Bomba efekt referansi onarimi");
                cfg.explodeFx = root;
                EditorUtility.SetDirty(cfg);
                fixedCount++;
                log.AppendLine($"\n{cfg.name}: '{before}' -> '{root.name}' (kok) olarak duzeltildi.");
            }

            if (fixedCount > 0) AssetDatabase.SaveAssets();
            log.AppendLine($"\nToplam {fixedCount} referans duzeltildi.");
            Debug.Log(log.ToString());
        }

        /// <summary>Config .asset metninden explodeFx alanindaki guid'i okur.</summary>
        static string ReadExplodeFxGuid(string configPath)
        {
            string full = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), configPath);
            if (!System.IO.File.Exists(full)) return null;

            foreach (var line in System.IO.File.ReadAllLines(full))
            {
                if (!line.Contains("explodeFx:")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, "guid: ([0-9a-f]{32})");
                return m.Success ? m.Groups[1].Value : null;
            }
            return null;
        }

        static int AuditPrefab(GameObject prefab, StringBuilder sb)
        {
            int problems = 0;

            var systems = prefab.GetComponentsInChildren<ParticleSystem>(true);
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            sb.AppendLine($"    partikul sistemi: {systems.Length}, renderer: {renderers.Length}");

            if (systems.Length == 0 && renderers.Length == 0)
            {
                sb.AppendLine("    !! Prefabin icinde hic partikul/renderer yok — bos bir obje.");
                return problems + 1;
            }

            int notPlayOnAwake = 0;
            foreach (var ps in systems)
                if (!ps.main.playOnAwake) notPlayOnAwake++;
            if (notPlayOnAwake > 0)
            {
                sb.AppendLine($"    !! {notPlayOnAwake} partikul sisteminde playOnAwake KAPALI — " +
                              "Instantiate edilse bile kendiliginden oynamaz.");
                problems++;
            }

            // Ayni materyali tekrar tekrar yazma; her biri icin shader'i bir kez raporla.
            var seen = new HashSet<Material>();
            foreach (var r in renderers)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null)
                    {
                        sb.AppendLine($"    !! '{r.name}' uzerinde BOS materyal slotu -> cizilmez.");
                        problems++;
                        continue;
                    }
                    if (!seen.Add(mat)) continue;

                    string shaderName = mat.shader != null ? mat.shader.name : "<YOK>";
                    string verdict = ShaderVerdict(mat.shader);
                    sb.AppendLine($"    materyal '{mat.name}' -> shader '{shaderName}' {verdict}");
                    if (verdict.StartsWith("!!")) problems++;
                }
            }

            return problems;
        }

        /// <summary>Shader URP'de cizim uretebilir mi? Eksik shader Unity tarafindan
        /// InternalErrorShader ile degistirilir; yerlesik legacy particle shaderlari ise URP'de
        /// hic yoktur ve sessizce hicbir sey cizmez.</summary>
        static string ShaderVerdict(Shader shader)
        {
            if (shader == null) return "!! SHADER YOK -> cizilmez";

            string n = shader.name;
            if (n.Contains("InternalErrorShader") || n.Contains("Hidden/InternalError"))
                return "!! EKSIK SHADER (Unity hata shader'ina dusurdu) -> cizilmez";

            // Legacy yerlesik particle/diffuse aileleri URP'de yok.
            if (n.StartsWith("Particles/") || n.StartsWith("Mobile/Particles/") ||
                n.StartsWith("Legacy Shaders/"))
                return "!! LEGACY YERLESIK SHADER -> URP'de cizilmez";

            if (!shader.isSupported)
                return "!! shader bu platformda DESTEKLENMIYOR (derleme hatasi olabilir)";

            return "(uygun)";
        }
    }
}
