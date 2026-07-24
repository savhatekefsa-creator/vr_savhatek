using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>Savas kurulum menuleri (16/18): silaha ates mekanigi ekleme ve can/hasar
    /// kablolamasi. VRMultiplayerSetup'in partial parcasi.</summary>
    public static partial class VRMultiplayerSetup
    {
        [MenuItem("Tools/VR Multiplayer/16. Add Weapon Fire (Rifle)")]
        public static void AddWeaponFire()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur (degisiklikler kalici olmaz).", "Tamam");
                return;
            }

            GameObject target = Selection.activeGameObject;
            if (target == null || target.GetComponent<GrabbableObject>() == null)
            {
                target = null;
                foreach (var g in Object.FindObjectsByType<GrabbableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (g.name.Contains("Rifle")) { target = g.gameObject; break; }
            }
            if (target == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Silah bulunamadi. Sahnede tutulabilir (GrabbableObject) silahi sec ve tekrar dene.", "Tamam");
                return;
            }

            if (target.GetComponent<NetworkWeapon>() == null) target.AddComponent<NetworkWeapon>();

            // Precise muzzle point: average the mesh vertices in the last few percent of the
            // barrel axis — that slab contains only the barrel tip, so its centroid IS the
            // muzzle (bounding-box math alone lands below the barrel because the grip and
            // magazine drag the box's center down).
            var muzzleT = target.transform.Find("Muzzle");
            if (muzzleT == null)
            {
                var mgo = new GameObject("Muzzle");
                muzzleT = mgo.transform;
                muzzleT.SetParent(target.transform, false);
            }

            // Namlu sozlesmesi runtime ile AYNI kaynaktan (WeaponGeometry) — kopyalar
            // birbirinden saparsa editorde kurulan Muzzle ile atis ekseni ayrisirdi.
            var biggest = WeaponGeometry.FindBiggestMesh(target.transform);
            if (biggest != null)
            {
                var mesh = biggest.sharedMesh;
                Bounds mb = mesh.bounds;
                Vector3 axis = WeaponGeometry.LongestLocalAxis(mb, biggest.transform.lossyScale, out _);
                float sign = WeaponGeometry.BulkSign(mb, axis);
                Vector3 a = axis * sign;

                var verts = mesh.vertices;
                float maxD = float.MinValue;
                foreach (var v in verts) maxD = Mathf.Max(maxD, Vector3.Dot(v, a));
                float axisLen = Mathf.Abs(Vector3.Dot(mb.size, a));
                float slab = maxD - axisLen * 0.03f;

                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (var v in verts)
                    if (Vector3.Dot(v, a) >= slab) { sum += v; n++; }
                if (n > 0)
                {
                    muzzleT.position = biggest.transform.TransformPoint(sum / n);
                    muzzleT.rotation = Quaternion.LookRotation((biggest.transform.rotation * a).normalized);
                }
            }

            var wso = new SerializedObject(target.GetComponent<NetworkWeapon>());
            wso.FindProperty("muzzle").objectReferenceValue = muzzleT;
            wso.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(target);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                "'" + target.name + "' ates kurulumu tamam:\n\n" +
                "• Namlu ucu ('Muzzle' cocugu) mesh noktalarindan hassas hesaplandi\n" +
                "  — iz artik tam namlunun ucundan cikar. Gerekirse Muzzle'i elle tasi.\n" +
                "• GRIP ile tut, TETIK ile ates et (iki elin tetigi de calisir)\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve 3 gozluge YENIDEN build al.", "Tamam");
        }

        // Adds the combat layer (health + damage + HUD) to the player prefab: PlayerHealth,
        // PlayerHUD and a capsule-trigger Hitbox child wired to the networked Head carrier.
        [MenuItem("Tools/VR Multiplayer/18. Setup Combat (can/hasar)")]
        public static void SetupCombat()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }

            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
            try
            {
                var health = root.GetComponent<PlayerHealth>();
                if (health == null) health = root.AddComponent<PlayerHealth>();
                if (root.GetComponent<PlayerHUD>() == null) root.AddComponent<PlayerHUD>();

                var headCarrier = root.transform.Find("Head");

                // Kafa-ustu can bari kaldirildi (gorunur can bari istenmiyor; can kol saatinde).

                var hitboxT = root.transform.Find("Hitbox");
                GameObject hitbox = hitboxT != null ? hitboxT.gameObject : null;
                if (hitbox == null)
                {
                    hitbox = new GameObject("Hitbox");
                    hitbox.transform.SetParent(root.transform, false);
                }
                if (hitbox.GetComponent<CapsuleCollider>() == null)
                    hitbox.AddComponent<CapsuleCollider>();
                var hb = hitbox.GetComponent<PlayerHitbox>();
                if (hb == null) hb = hitbox.AddComponent<PlayerHitbox>();

                var so = new SerializedObject(hb);
                so.FindProperty("health").objectReferenceValue = health;
                so.FindProperty("head").objectReferenceValue = headCarrier;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Savas sistemi eklendi (oyuncu prefabina):\n\n" +
                "• Can: 100 — vurulunca 25 duser (silahta ayarlanir)\n" +
                "• Takim arkadasina ates HASAR VERMEZ (A/B)\n" +
                "• Can bitince ELENIR, 4 sn sonra tam canla yeniden dogar\n" +
                "• Can kol saati ekraninda gosterilir (gorunur can bari yok)\n" +
                "• Vurulunca kirmizi flas + kumanda titresimi\n\n" +
                "Gozluklere YENIDEN build al (prefab degisti).", "Tamam");
        }

        // Adds the procedural finger poser to the CURRENT prefab's avatar (for an avatar that was
        // set up before this feature existed). Idempotent.
    }
}
