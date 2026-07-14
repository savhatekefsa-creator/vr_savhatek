using System.IO;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Authoring helpers for <see cref="WeaponGripProfile"/> assets — the project's ISDK
    /// HandGrabPose-editor equivalent, without installing ISDK.
    ///
    ///   Tools ▸ VR Multiplayer ▸ 30. Create Weapon Grip Profile (select weapon)
    ///     Builds a skeleton profile from the selected weapon: name match + grip rotation
    ///     initialised from the Muzzle child so the +Z-barrel convention is guaranteed (warns if
    ///     the weapon has no muzzle and the barrel had to be guessed from the longest mesh axis).
    ///
    /// Select the resulting asset to get in-scene handles (grip anchor, wrist, support rail)
    /// drawn relative to a matching weapon in the open scene. Because a profile is a plain asset
    /// (not a scene object), tuning it in Play mode PERSISTS after you stop — tweak on-device via
    /// Quest Link, then keep the values.
    /// </summary>
    public static class WeaponGripProfileAuthoring
    {
        const string ProfileFolder = "Assets/_VRMultiplayer/Resources/WeaponGripProfiles";

        [MenuItem("Tools/VR Multiplayer/30. Create Weapon Grip Profile (select weapon)")]
        public static void CreateFromSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                EditorUtility.DisplayDialog("Weapon Grip Profile",
                    "Once sahnede ya da prefab'da bir silah GameObject'i sec, sonra bu menuyu calistir.", "Tamam");
                return;
            }

            string cleanName = WeaponGripBinder.CleanName(go.name);
            var profile = ScriptableObject.CreateInstance<WeaponGripProfile>();
            profile.weaponNameEquals = cleanName;
            profile.mainHand = WeaponGripProfile.HandPose.Defaults(true);
            profile.supportHand = WeaponGripProfile.HandPose.Defaults(false);

            // Barrel in weapon-local space: prefer a Muzzle child (authoritative), else the
            // longest mesh axis. The grip anchor's +Z is aligned to it so the convention holds
            // even if the model's own +Z isn't the barrel.
            bool guessed;
            Vector3 barrelLocal = BarrelLocal(go.transform, out guessed);
            Vector3 upLocal = Mathf.Abs(Vector3.Dot(barrelLocal, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            profile.gripLocalEuler = Quaternion.LookRotation(barrelLocal, upLocal).eulerAngles;

            Directory.CreateDirectory(ProfileFolder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{ProfileFolder}/{cleanName}_GripProfile.asset");
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);

            if (guessed)
                EditorUtility.DisplayDialog("Weapon Grip Profile",
                    $"Profil olusturuldu:\n{path}\n\nUYARI: '{go.name}' altinda 'Muzzle' child'i yok — namlu ekseni en-uzun mesh ekseninden TAHMIN edildi. " +
                    "Namlu yonu yanlissa ya bir Muzzle child'i ekle ya da profildeki Grip Local Euler'i elle duzelt (kural: +Z = namlu, +Y = ust ray).", "Tamam");
            else
                EditorUtility.DisplayDialog("Weapon Grip Profile",
                    $"Profil olusturuldu:\n{path}\n\nGrip rotasyonu Muzzle'dan +Z-namlu olacak sekilde ayarlandi. " +
                    "Asset'i secip Scene'de kabza/bilek/ray handle'lariyla ince ayar yap (Play mode'da yapilan degisiklikler kalicidir).", "Tamam");
        }

        [MenuItem("Tools/VR Multiplayer/30. Create Weapon Grip Profile (select weapon)", true)]
        static bool CreateFromSelectionValidate() => Selection.activeGameObject != null;

        // Barrel direction in the weapon's local space. Muzzle child wins; otherwise the longest
        // local mesh axis, signed toward the bulk of the mesh (the muzzle side).
        static Vector3 BarrelLocal(Transform weapon, out bool guessed)
        {
            var muzzle = weapon.Find("Muzzle");
            if (muzzle != null)
            {
                guessed = false;
                Vector3 local = weapon.InverseTransformDirection(muzzle.forward);
                return local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.forward;
            }

            guessed = true;
            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in weapon.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest == null) return Vector3.forward;

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
            Vector3 axis = Vector3.right;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) axis = Vector3.forward;
            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            if (sign == 0f) sign = 1f;

            Quaternion childToWeapon = Quaternion.Inverse(weapon.rotation) * biggest.transform.rotation;
            return (childToWeapon * (axis * sign)).normalized;
        }
    }

    /// <summary>Scene-view handles for a selected profile, drawn relative to a matching weapon in
    /// the open scene (found by cleaned name). Drags write back to the asset with Undo.</summary>
    [CustomEditor(typeof(WeaponGripProfile))]
    public class WeaponGripProfileEditor : Editor
    {
        void OnSceneGUI()
        {
            var profile = (WeaponGripProfile)target;
            var weapon = FindWeaponInScene(profile);
            if (weapon == null) return;

            // Grip anchor: position + orientation preview (+Z barrel = blue, +Y rail = green).
            Vector3 gripWorld = weapon.TransformPoint(profile.gripLocalPosition);
            Quaternion gripRot = weapon.rotation * profile.GripLocalRotation;
            EditorGUI.BeginChangeCheck();
            Vector3 newGrip = Handles.PositionHandle(gripWorld, gripRot);
            Handles.color = Color.blue; Handles.ArrowHandleCap(0, gripWorld, gripRot, 0.12f, EventType.Repaint);
            Handles.color = Color.green; Handles.ArrowHandleCap(0, gripWorld, gripRot * Quaternion.Euler(-90, 0, 0), 0.08f, EventType.Repaint);
            Handles.Label(gripWorld, "  Grip (+Z namlu)");

            // Support rail: two draggable endpoints + the segment between them.
            Vector3 rs = weapon.TransformPoint(profile.supportRailLocalStart);
            Vector3 re = weapon.TransformPoint(profile.supportRailLocalEnd);
            Handles.color = Color.yellow;
            Handles.DrawLine(rs, re);
            Vector3 newRs = Handles.FreeMoveHandle(rs, 0.02f, Vector3.zero, Handles.SphereHandleCap);
            Vector3 newRe = Handles.FreeMoveHandle(re, 0.02f, Vector3.zero, Handles.SphereHandleCap);
            Handles.Label(rs, "  Ray baslangic");
            Handles.Label(re, "  Ray bitis");

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(profile, "Tune Weapon Grip Profile");
                profile.gripLocalPosition = weapon.InverseTransformPoint(newGrip);
                profile.supportRailLocalStart = weapon.InverseTransformPoint(newRs);
                profile.supportRailLocalEnd = weapon.InverseTransformPoint(newRe);
                EditorUtility.SetDirty(profile);
            }
        }

        static Transform FindWeaponInScene(WeaponGripProfile profile)
        {
            foreach (var grab in Object.FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
                if (profile.MatchScore(WeaponGripBinder.CleanName(grab.name)) > 0)
                    return grab.transform;
            return null;
        }
    }
}
