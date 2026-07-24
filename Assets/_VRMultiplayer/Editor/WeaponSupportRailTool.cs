using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Turns each two-handed weapon's DEGENERATE support "rail" into a real segment.
    ///
    /// The capture tool records one support point, so every profile in the project shipped with
    /// supportRailLocalStart == supportRailLocalEnd. WeaponHandWeld projects the player's hand
    /// onto that segment — onto a zero-length segment the projection always returns the same
    /// point, so the support wrist is welded to one hard-coded spot on the weapon no matter where
    /// the player is actually holding it. Measured, that forces a two-hand separation of 0.43 m
    /// (HK416) to 0.53 m (Dmr1) against a natural 0.30-0.40 m hold: the wrist gets dragged 10-20
    /// cm past the controller while the arm IK, still aimed at the controller, leaves the elbow
    /// bent. That is the "wrist stretched, arm bent" artefact.
    ///
    /// This tool grows the captured point into a segment ALONG THE BARREL AXIS, keeping its
    /// perpendicular offset (the captured point is on the handguard; sliding along the barrel
    /// stays on the handguard). The segment is clipped so it never leaves the weapon mesh and
    /// never crawls up onto the grip hand. Everything else about the captured pose is untouched.
    ///
    /// Run 43 first and read the report; 44 writes it. Both are undoable.
    /// </summary>
    public static class WeaponSupportRailTool
    {
        const string WeaponFolder = "Assets/_VRMultiplayer/Resources/WeaponPrefabs";

        // How far the support hand may slide either way from the captured point (world m).
        const float MaxHalfWorld = 0.12f;
        // The rail must stay at least this far from the grip anchor (world m) — otherwise the
        // support hand could slide onto the trigger hand.
        const float MinClearWorld = 0.20f;
        // Keep the rail this far inside the weapon's own extent (world m) so the hand never
        // slides off the end of the handguard into thin air.
        const float MeshMarginWorld = 0.03f;
        // Below this grip-to-rail distance a profile is a one-handed hold (pistol, grenade):
        // it has no handguard to slide on, so it is reported and left alone.
        const float MinSpanWorld = 0.15f;

        [MenuItem("Tools/VR Multiplayer/43. Destek Rayi Raporu (yazmaz)")]
        public static void Preview() => Debug.Log(Run(false));

        [MenuItem("Tools/VR Multiplayer/44. Destek Rayini Uret (profillere yazar)")]
        public static void Apply() => Debug.Log(Run(true));

        /// <summary>Runs the pass and RETURNS the report, so it can be read without going through
        /// the console (which shows only the first line of a multi-line entry).</summary>
        public static string Run(bool write)
        {
            var prefabs = LoadWeaponPrefabs();
            var profiles = LoadProfiles();
            var sb = new StringBuilder();
            sb.AppendLine(write ? "=== DESTEK RAYI URETILDI ===" : "=== DESTEK RAYI RAPORU (yazilmadi) ===");
            sb.AppendLine("profil | silah | olcek | ESKI acilik | YENI ray araligi (kabzadan, m)");

            int changed = 0, skipped = 0;
            foreach (var p in profiles)
            {
                GameObject prefab = BestPrefab(p, prefabs);
                if (prefab == null)
                {
                    sb.AppendLine($"{p.name} | -- | ATLANDI: eslesen silah prefabi yok");
                    skipped++;
                    continue;
                }

                float scale = prefab.transform.localScale.x;
                float oldSpan = Vector3.Distance(p.gripLocalPosition, p.supportRailLocalStart) * scale;

                if (Vector3.Distance(p.supportRailLocalStart, p.supportRailLocalEnd) * scale > 0.01f)
                {
                    sb.AppendLine($"{p.name} | {prefab.name} | ATLANDI: zaten gercek bir ray var");
                    skipped++;
                    continue;
                }
                if (oldSpan < MinSpanWorld)
                {
                    sb.AppendLine($"{p.name} | {prefab.name} | ATLANDI: tek elli tutus (acilik {oldSpan:F3} m)");
                    skipped++;
                    continue;
                }

                if (!BuildRail(p, prefab, scale, out Vector3 start, out Vector3 end, out string why))
                {
                    sb.AppendLine($"{p.name} | {prefab.name} | ATLANDI: {why}");
                    skipped++;
                    continue;
                }

                float dStart = Vector3.Distance(p.gripLocalPosition, start) * scale;
                float dEnd = Vector3.Distance(p.gripLocalPosition, end) * scale;
                if (dStart > dEnd) (dStart, dEnd) = (dEnd, dStart);
                sb.AppendLine($"{p.name} | {prefab.name} | x{scale:F2} | {oldSpan:F3} | {dStart:F3} .. {dEnd:F3}");

                if (write)
                {
                    Undo.RecordObject(p, "Destek Rayini Uret");
                    p.supportRailLocalStart = start;
                    p.supportRailLocalEnd = end;
                    EditorUtility.SetDirty(p);
                }
                changed++;
            }

            if (write) AssetDatabase.SaveAssets();
            sb.AppendLine();
            sb.AppendLine(write
                ? $"{changed} profil yazildi, {skipped} atlandi."
                : $"{changed} profil degisecek, {skipped} atlanacak. Yazmak icin menu 44.");
            return sb.ToString();
        }

        /// <summary>
        /// Grow the captured support point into a barrel-axis segment around itself, clipped to
        /// the weapon's own extent and to a safe distance from the grip anchor.
        /// </summary>
        static bool BuildRail(WeaponGripProfile p, GameObject prefab, float scale,
            out Vector3 start, out Vector3 end, out string why)
        {
            start = end = Vector3.zero;
            why = "";
            if (scale <= 0.0001f) { why = "silah olcegi sifir"; return false; }

            Vector3 axis = p.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? p.barrelLocalDirection.normalized
                : Vector3.forward;

            if (!MeshExtentAlong(prefab, axis, out float meshMin, out float meshMax))
            { why = "silahin mesh'i okunamadi"; return false; }

            Vector3 point = p.supportRailLocalStart;
            float tPoint = Vector3.Dot(point, axis);
            float tGrip = Vector3.Dot(p.gripLocalPosition, axis);

            // Which way along the barrel the support hand sits, seen from the grip.
            float dir = Mathf.Sign(tPoint - tGrip);
            if (Mathf.Abs(tPoint - tGrip) < 1e-4f) dir = 1f;

            float half = MaxHalfWorld / scale;
            float lo = tPoint - half;
            float hi = tPoint + half;

            // Stay on the weapon.
            float margin = MeshMarginWorld / scale;
            lo = Mathf.Max(lo, meshMin + margin);
            hi = Mathf.Min(hi, meshMax - margin);

            // Stay off the grip hand.
            float clear = MinClearWorld / scale;
            if (dir > 0f) lo = Mathf.Max(lo, tGrip + clear);
            else hi = Mathf.Min(hi, tGrip - clear);

            if (hi - lo < 0.02f / scale)
            { why = $"ray icin yer kalmadi (mesh {meshMin:F3}..{meshMax:F3}, kabza {tGrip:F3})"; return false; }

            start = point + axis * (lo - tPoint);
            end = point + axis * (hi - tPoint);
            return true;
        }

        /// <summary>Weapon extent along <paramref name="axis"/>, in the prefab ROOT's local space
        /// (which is the space every profile value is authored in).</summary>
        static bool MeshExtentAlong(GameObject prefab, Vector3 axis, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            bool any = false;
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null)
                    any |= Accumulate(mf.sharedMesh.bounds, toRoot * mf.transform.localToWorldMatrix,
                        axis, ref min, ref max);

            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null)
                    any |= Accumulate(smr.sharedMesh.bounds, toRoot * smr.transform.localToWorldMatrix,
                        axis, ref min, ref max);

            return any;
        }

        static bool Accumulate(Bounds b, Matrix4x4 m, Vector3 axis, ref float min, ref float max)
        {
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                float t = Vector3.Dot(m.MultiplyPoint3x4(corner), axis);
                if (t < min) min = t;
                if (t > max) max = t;
            }
            return true;
        }

        static GameObject BestPrefab(WeaponGripProfile p, List<GameObject> prefabs)
        {
            GameObject best = null;
            int bestScore = 0;
            foreach (var go in prefabs)
            {
                int s = p.MatchScore(go.name);
                if (s > bestScore) { bestScore = s; best = go; }
            }
            return best;
        }

        static List<GameObject> LoadWeaponPrefabs()
        {
            var list = new List<GameObject>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { WeaponFolder }))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (go != null) list.Add(go);
            }
            return list;
        }

        static List<WeaponGripProfile> LoadProfiles()
        {
            var list = new List<WeaponGripProfile>();
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null) list.Add(p);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }
    }
}
