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
    /// <summary>Tutulabilir obje menuleri (10/15): secili objeyi grabbable yapma ve
    /// silah-disi grabbable temizligi. VRMultiplayerSetup'in partial parcasi.</summary>
    public static partial class VRMultiplayerSetup
    {
        [MenuItem("Tools/VR Multiplayer/10. Make Selected Grabbable")]
        public static void MakeSelectedGrabbable()
        {
            var objs = Selection.gameObjects;
            if (objs == null || objs.Length == 0)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce SAHNEDEKİ tutulabilir olacak objeleri seç (örn. kayalar), sonra bu menüyü çalıştır.", "Tamam");
                return;
            }

            int done = 0, skippedNested = 0;
            foreach (var go in objs)
            {
                if (!go.scene.IsValid()) continue; // scene objects only

                // NGO forbids a NetworkObject nested under another NetworkObject — that breaks
                // spawning for the whole hierarchy. Skip objects that would create nesting.
                bool nested = false;
                var parent = go.transform.parent;
                if (parent != null && parent.GetComponentInParent<NetworkObject>(true) != null)
                    nested = true;
                foreach (var no in go.GetComponentsInChildren<NetworkObject>(true))
                    if (no.gameObject != go) { nested = true; break; }
                if (nested) { skippedNested++; continue; }

                // Collider (convex, so it can carry a Rigidbody and land on the terrain).
                if (go.GetComponentInChildren<Collider>() == null)
                {
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf != null)
                    {
                        var mc = mf.gameObject.AddComponent<MeshCollider>();
                        mc.convex = true;
                    }
                    else
                    {
                        go.AddComponent<BoxCollider>();
                    }
                }

                var rb = go.GetComponent<Rigidbody>();
                if (rb == null) rb = go.AddComponent<Rigidbody>();
                rb.mass = 2f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                if (go.GetComponent<NetworkObject>() == null) go.AddComponent<NetworkObject>();
                var cnt = go.GetComponent<ClientNetworkTransform>();
                if (cnt == null) cnt = go.AddComponent<ClientNetworkTransform>();
                ConfigureNetworkTransform(cnt);
                // NOTE: no NetworkRigidbody — its authority-based kinematic switching fights
                // GrabbableObject's frozen-at-rest design (gravity kicks in mid-hold on
                // ownership transfer). Grabbables are kinematic on every client by default;
                // only the owner runs physics, and only during a throw.
                if (go.GetComponent<GrabbableObject>() == null) go.AddComponent<GrabbableObject>();

                EditorUtility.SetDirty(go);
                done++;
            }

            // HandGrabber on the player prefab, wired to the networked hand children.
            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
            try
            {
                var grabber = root.GetComponent<HandGrabber>();
                if (grabber == null) grabber = root.AddComponent<HandGrabber>();
                var so = new SerializedObject(grabber);
                so.FindProperty("leftHand").objectReferenceValue = root.transform.Find("LeftHand");
                so.FindProperty("rightHand").objectReferenceValue = root.transform.Find("RightHand");
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                done + " obje tutulabilir yapıldı." +
                (skippedNested > 0
                    ? "\n\nUYARI: " + skippedNested + " obje ATLANDI çünkü iç içe NetworkObject " +
                      "oluşturacaktı (NGO yasaklar). Kümeleri değil TEK objeleri seç."
                    : "") +
                "\n\nKullanım: elini objeye yaklaştır, KAVRAMA (grip) tuşunu sık → tut; bırakınca düşer, " +
                "hızlı bırakırsan fırlar. Herkes objeyi elinde görür.\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve gözlüklere YENİDEN build al.", "Tamam");
        }

        // Removes the grabbable/network setup from EVERYTHING except the weapon (name contains
        // "Rifle"). Fixes the nested-NetworkObject rocks poisoning scene spawn, and bumps the
        // prefab's grab radius to match the new default.
        [MenuItem("Tools/VR Multiplayer/15. Keep Only Weapon Grabbable (temizlik)")]
        public static void KeepOnlyWeaponGrabbable()
        {
            int stripped = 0, kept = 0;
            foreach (var g in Object.FindObjectsByType<GrabbableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var go = g.gameObject;
                if (go.name.Contains("Rifle"))
                {
                    kept++;
                    // The weapon keeps its grab setup, but NetworkRigidbody must go — it
                    // re-enables gravity on ownership transfer and yanks it out of the hand.
                    var nrb = go.GetComponent<NetworkRigidbody>();
                    if (nrb != null) { Object.DestroyImmediate(nrb); EditorUtility.SetDirty(go); }
                    continue;
                }

                foreach (var c in new System.Type[]
                {
                    typeof(GrabbableObject), typeof(NetworkRigidbody),
                    typeof(ClientNetworkTransform), typeof(NetworkTransform),
                    typeof(NetworkObject), typeof(Rigidbody),
                })
                {
                    var comp = go.GetComponent(c);
                    if (comp != null) Object.DestroyImmediate(comp);
                }
                EditorUtility.SetDirty(go);
                stripped++;
            }

            // Match the prefab's serialized grab radius to the new, more forgiving default.
            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
            try
            {
                var grabber = root.GetComponent<HandGrabber>();
                if (grabber != null && grabber.grabRadius < 0.3f)
                {
                    grabber.grabRadius = 0.3f;
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                stripped + " objeden tutulabilirlik/ağ bileşenleri söküldü.\n" +
                kept + " silah tutulabilir kaldı.\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve 3 gözlüğe YENİDEN build al.", "Tamam");
        }

        // Adds trigger-fire capability to the weapon (selected object, or the Rifle found in
        // the scene): hitscan from the muzzle + tracer/flash/spark effects, server-validated.
    }
}
