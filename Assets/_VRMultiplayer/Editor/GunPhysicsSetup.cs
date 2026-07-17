using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Cok parcali silah modellerine (FPS Gun Pack 4K vb.) GEOMETRIYE OTURAN fizik kurar:
    /// parcalar islevsel bolgelere ayrilir (govde / namlu / kabza / on kabza / sarjor / dipcik /
    /// nisangah) ve her bolge icin kok-lokal AABB'den BIR BoxCollider uretilir — tek convex
    /// hull'un aksine tetik korkulugu, sarjor ve dipcik arasindaki bosluklar sisirilmez.
    /// Kutular kokte durdugu icin Rigidbody ile bilesik (compound) collider olusur; sihirbazin
    /// "10. Make Selected Grabbable" adimi collider'i VAR gorup dokunmaz, yalnizca ag/tutma
    /// bilesenlerini ekler — akis bozulmaz.
    ///
    /// Kucuk kozmetik parcalar (mermi, kovan, vida, civata, ray yivleri) kutulara katilmaz.
    /// Rigidbody projenin silah standardi: 2 kg, interpolate (HK416/Dmr1 ile ayni).
    ///
    /// Otomatik: derleme sonrasi sahnede COLLIDER'SIZ bir "Rifle 1" varsa bir kez kurulur.
    /// Menu 34: secili silaha (kok ya da alt parcasi secili olabilir) uygular; yeniden
    /// calistirmak koktekI eski BoxCollider'lari silip guncel geometriden yeniden uretir.
    /// </summary>
    public static class GunPhysicsSetup
    {
        const string AutoTargetName = "Rifle 1";

        // Bolge -> isim kaliplari (kucuk harf, Contains). Siralama onemli: ilk eslesen kazanir.
        // Hem AK47_Sopmod tarzi (Main_Grip, Foregrip_Main, Barrel_Upper, Stock_Main1) hem Dmr1
        // tarzi (handle12, nozzle12, stock_cap12, aim_part12) adlari taninir.
        static readonly string[] IgnorePatterns = { "bullet", "shell", "screw", "bolt", "groove" };
        static readonly (string key, string[] patterns)[] Buckets =
        {
            ("on kabza", new[] { "foregrip" }),
            ("kabza",    new[] { "grip", "handle" }),
            ("sarjor",   new[] { "mag" }),
            ("namlu",    new[] { "barrel", "muzzle", "nozzle", "gas_outlet", "rod", "silencer", "suppressor" }),
            ("dipcik",   new[] { "stock" }),
            ("nisangah", new[] { "scope", "sight", "dovetail", "aim" }),
            // eslesmeyen her sey -> govde (catch-all, Apply icinde)
        };
        const string BodyKey = "govde";

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.delayCall += TryAutoRun;
        }

        static void TryAutoRun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryAutoRun;
                return;
            }

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!t.gameObject.scene.IsValid()) continue;
                if (WeaponGripBinder.CleanName(t.name) != AutoTargetName) continue;
                if (t.GetComponentInChildren<Collider>() != null) return; // fizik zaten var
                Apply(t.gameObject, false);
                return;
            }
        }

        [MenuItem("Tools/VR Multiplayer/34. Secili Silaha Geometrik Fizik Ekle")]
        public static void ApplyToSelection()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Silah Fizigi",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }

            var go = Selection.activeGameObject;
            if (go == null || !go.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Silah Fizigi",
                    "Once SAHNEDEKI silahi sec (kok ya da herhangi bir parcasi), sonra bu menuyu calistir.", "Tamam");
                return;
            }

            // Alt parca secildiyse prefab instance'inin kokune cik.
            var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (outer != null) go = outer;

            Apply(go, true);
        }

        [MenuItem("Tools/VR Multiplayer/34. Secili Silaha Geometrik Fizik Ekle", true)]
        static bool ApplyToSelectionValidate() => Selection.activeGameObject != null;

        /// <summary>WeaponPackSetup gibi diger araclar da cagirabilsin diye public.</summary>
        public static void Apply(GameObject go, bool interactive)
        {
            Transform root = go.transform;

            // Yeniden calistirma: koktekI eski kutulari sil, guncel geometriden yeniden uret.
            // Cocuklardaki collider'lara (orn. Dmr1'in MeshCollider'i) dokunulmaz.
            foreach (var old in root.GetComponents<BoxCollider>())
                Object.DestroyImmediate(old);

            // Bolge basina kok-lokal AABB biriktir.
            var min = new Dictionary<string, Vector3>();
            var max = new Dictionary<string, Vector3>();
            int used = 0, skipped = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                string n = mf.name.ToLowerInvariant();

                bool ignore = false;
                foreach (var pat in IgnorePatterns)
                    if (n.Contains(pat)) { ignore = true; break; }
                if (ignore) { skipped++; continue; }

                string key = BodyKey;
                foreach (var (k, patterns) in Buckets)
                {
                    bool hit = false;
                    foreach (var pat in patterns)
                        if (n.Contains(pat)) { hit = true; break; }
                    if (hit) { key = k; break; }
                }

                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 sign = new Vector3((i & 1) == 0 ? 1 : -1, (i & 2) == 0 ? 1 : -1, (i & 4) == 0 ? 1 : -1);
                    Vector3 corner = b.center + Vector3.Scale(b.extents, sign);
                    Vector3 p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!min.ContainsKey(key)) { min[key] = p; max[key] = p; }
                    else { min[key] = Vector3.Min(min[key], p); max[key] = Vector3.Max(max[key], p); }
                }
                used++;
            }

            if (min.Count == 0)
            {
                string why = "'" + go.name + "' altinda mesh bulunamadi — fizik kurulamadi.";
                Debug.LogWarning("[GunPhysicsSetup] " + why);
                if (interactive) EditorUtility.DisplayDialog("Silah Fizigi", why, "Tamam");
                return;
            }

            // Bolge basina kutu. Cok ince kutular ezilmesin diye eksen basina 1.5 cm taban.
            var report = new List<string>();
            foreach (var key in min.Keys)
            {
                Vector3 size = max[key] - min[key];
                Vector3 center = (min[key] + max[key]) * 0.5f;
                size.x = Mathf.Max(size.x, 0.015f);
                size.y = Mathf.Max(size.y, 0.015f);
                size.z = Mathf.Max(size.z, 0.015f);

                var box = go.AddComponent<BoxCollider>();
                box.center = center;
                box.size = size;
                report.Add("  " + key + ": boyut " + size.ToString("F3"));
            }

            // Projenin silah standardi Rigidbody (HK416/Dmr1 ile ayni; sihirbaz 10 ile uyumlu).
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = 2f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(go.scene);

            string msg = "'" + go.name + "' icin geometrik fizik kuruldu.\n"
                + "  Kutu sayisi: " + min.Count + " (" + used + " parca kullanildi, "
                + skipped + " kozmetik parca atlandi)\n"
                + string.Join("\n", report)
                + "\n  Rigidbody: 2 kg, interpolate.\n"
                + "Sahneyi kaydetmeyi unutma (Ctrl+S). Tutulabilir/ateslenebilir yapmak icin: "
                + "menu 10 (collider'i var gorup atlar) + menu 16.";
            Debug.Log("[GunPhysicsSetup] " + msg);
            if (interactive) EditorUtility.DisplayDialog("Silah Fizigi", msg, "Tamam");
        }
    }
}
