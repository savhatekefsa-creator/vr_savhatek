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
    /// <summary>
    /// One-click setup so you don't have to wire VR + networking by hand.
    ///   Tools ▸ VR Multiplayer ▸ 1. Create NetworkPlayer Prefab
    ///   Tools ▸ VR Multiplayer ▸ 2. Setup Current Scene
    ///
    /// Open the scene you want (e.g. the LOKIT Forest "Demo" scene) and run step 2 — it adds
    /// the local XR rig, the NetworkManager (+ LAN discovery + bootstrap), and a status label.
    /// Step 1 is run automatically by step 2 if the prefab doesn't exist yet.
    /// </summary>
    public static partial class VRMultiplayerSetup
    {
        const string Root = "Assets/_VRMultiplayer";
        const string PrefabFolder = Root + "/Prefabs";
        const string MatFolder = Root + "/Materials";
        const string AvatarFolder = Root + "/Avatar";
        const string PrefabPath = PrefabFolder + "/NetworkPlayer.prefab";

        // When true, AddHumanoidAvatar skips the modal success dialog so it can run head-less
        // (e.g. driven from the "21. Swap Avatar" wrapper via MCP).
        public static bool SilentSetup;

        // ---------------------------------------------------------------- Prefab
        [MenuItem("Tools/VR Multiplayer/1. Create NetworkPlayer Prefab")]
        public static GameObject CreateNetworkPlayerPrefab()
        {
            // KORUMA: Bu menu prefabi SIFIRDAN kurar. Mevcut prefabta sonradan eklenen tum
            // bilesenler (PlayerHealth, TeamSelector, HandGrabber, avatar...) kaybolur.
            // Prefab zaten varsa kullaniciya sorulmadan asla ezilmez.
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "NetworkPlayer Prefab zaten var",
                    "Bu islem prefabi SIFIRDAN kurar: PlayerHealth, TeamSelector, HandGrabber, " +
                    "avatar gibi sonradan eklenen TUM bilesenler SILINIR.\n\n" +
                    "Avatar degistirmek icin bunun yerine Adim 3'u kullan.\n\n" +
                    "Geri donus yolu yoktur (yalnizca git).",
                    "SIFIRDAN KUR (bilesenleri sil)", "Vazgec");
                if (!overwrite)
                {
                    Selection.activeObject = existing;
                    return existing;
                }
            }

            EnsureFolder(PrefabFolder);

            var root = new GameObject("NetworkPlayer");
            root.AddComponent<NetworkObject>();

            var head = BuildHead(root.transform);
            var left = BuildHand(root.transform, "LeftHand", new Color(0.25f, 0.55f, 1f));
            var right = BuildHand(root.transform, "RightHand", new Color(1f, 0.45f, 0.2f));

            ConfigureNetworkTransform(head.AddComponent<ClientNetworkTransform>());
            ConfigureNetworkTransform(left.AddComponent<ClientNetworkTransform>());
            ConfigureNetworkTransform(right.AddComponent<ClientNetworkTransform>());

            var vr = root.AddComponent<NetworkVRPlayer>();
            var so = new SerializedObject(vr);
            so.FindProperty("head").objectReferenceValue = head.transform;
            so.FindProperty("leftHand").objectReferenceValue = left.transform;
            so.FindProperty("rightHand").objectReferenceValue = right.transform;

            var headRenderers = head.GetComponentsInChildren<Renderer>();
            var hidden = so.FindProperty("ownerHiddenRenderers");
            hidden.arraySize = headRenderers.Length;
            for (int i = 0; i < headRenderers.Length; i++)
                hidden.GetArrayElementAtIndex(i).objectReferenceValue = headRenderers[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log("[VRMultiplayerSetup] NetworkPlayer prefab created at " + PrefabPath);
            Selection.activeObject = prefab;
            return prefab;
        }

        // ---------------------------------------------------------------- Scene
        [MenuItem("Tools/VR Multiplayer/2. Setup Current Scene")]
        public static void SetupCurrentScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) prefab = CreateNetworkPlayerPrefab();

            // --- NetworkManager + transport + discovery + bootstrap ---
            var nm = Object.FindFirstObjectByType<NetworkManager>();
            GameObject nmGo;
            if (nm == null)
            {
                nmGo = new GameObject("NetworkManager");
                nm = nmGo.AddComponent<NetworkManager>();
            }
            else
            {
                nmGo = nm.gameObject;
            }

            var utp = nmGo.GetComponent<UnityTransport>();
            if (utp == null) utp = nmGo.AddComponent<UnityTransport>();

            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.PlayerPrefab = prefab;

            var discovery = nmGo.GetComponent<NetworkDiscovery>();
            if (discovery == null) discovery = nmGo.AddComponent<NetworkDiscovery>();

            var boot = nmGo.GetComponent<LanBootstrap>();
            if (boot == null) boot = nmGo.AddComponent<LanBootstrap>();

            // --- Local XR rig ---
            if (Object.FindFirstObjectByType<XRRigReference>() == null)
                BuildXRRig();

            boot.discovery = discovery;

            EditorUtility.SetDirty(nm);
            EditorUtility.SetDirty(boot);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log("[VRMultiplayerSetup] Scene setup complete. Save the scene (Ctrl+S), then build to Quest.");
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Sahne hazır!\n\n• XR Rig + kamera eklendi\n• NetworkManager + LAN keşfi eklendi\n• Avatar prefab'ı atandı\n\nSahneyi kaydet (Ctrl+S). Sonra XR Plug-in Management'ta OpenXR'ı aç ve Meta Quest'e build al.\n\nDetaylar: Assets/_VRMultiplayer/README_VR_Multiplayer.md",
                "Tamam");
        }

        [MenuItem("Tools/VR Multiplayer/4. Add Ground Colliders (LOKIT)")]
        public static void AddGroundColliders()
        {
            int count = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                string n = mf.gameObject.name;
                bool isGround = n.Contains("Ground") || n.Contains("Hill")
                             || n.Contains("Terrain") || n.Contains("Trail");
                if (!isGround) continue;
                if (mf.sharedMesh == null) continue;

                // LOKIT ships some tiles with a broken TerrainCollider (missing TerrainData) —
                // it collides with NOTHING but used to make us skip the tile. Remove it.
                foreach (var tc in mf.GetComponents<TerrainCollider>())
                    if (tc.terrainData == null)
                        Object.DestroyImmediate(tc);

                if (mf.GetComponent<MeshCollider>() != null) continue;
                mf.gameObject.AddComponent<MeshCollider>();
                count++;
            }
            Debug.Log("[VRMultiplayerSetup] Added " + count + " ground colliders.");
            EditorUtility.DisplayDialog("VR Multiplayer",
                count + " zemin collider'ı eklendi.\n\nAvatarlar artık araziye tam oturacak. " +
                "AvatarIKController > Snap To Ground açık olmalı (sihirbaz artık otomatik açar). " +
                "Sahneyi kaydet (Ctrl+S) ve build al.", "Tamam");
        }

        // Turns off joystick locomotion (pure physical walking) and adds two-point colocation
        // calibration so real-world distance == in-game distance for players in the same room.
        [MenuItem("Tools/VR Multiplayer/5. Setup Colocation (physical walking)")]
        public static void SetupColocation()
        {
            var rigRef = Object.FindFirstObjectByType<XRRigReference>();
            if (rigRef == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Sahnede XR Rig yok. Önce '2. Setup Current Scene' çalıştır.", "Tamam");
                return;
            }
            var rigGo = rigRef.gameObject;

            // Pure physical walking: disable joystick move + snap turn.
            var loco = rigGo.GetComponent<XRRigLocomotion>();
            if (loco != null) loco.enabled = false;

            // Calibration status label (world-space, faces the camera).
            var labelGo = GameObject.Find("Calibration Label");
            TextMesh tm;
            if (labelGo == null)
            {
                labelGo = new GameObject("Calibration Label");
                labelGo.transform.position = new Vector3(0f, 1.4f, 1.6f);
                labelGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                labelGo.transform.localScale = Vector3.one * 0.16f;
                tm = labelGo.AddComponent<TextMesh>();
                tm.characterSize = 0.1f;
                tm.fontSize = 60;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = Color.yellow;
                labelGo.AddComponent<Billboard>();
            }
            else
            {
                tm = labelGo.GetComponent<TextMesh>();
            }

            var cal = rigGo.GetComponent<CalibrationManager>();
            if (cal == null) cal = rigGo.AddComponent<CalibrationManager>();
            cal.rig = rigGo.transform;
            cal.pointer = rigRef.rightHand;
            cal.status = tm;
            cal.sharedOrigin = Vector3.zero;
            cal.sharedForward = Vector3.forward;

            EditorUtility.SetDirty(rigGo);
            if (loco != null) EditorUtility.SetDirty(loco);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Colocation eklendi:\n\n• Analog hareket KAPATILDI (saf fiziksel yürüme)\n" +
                "• İki-nokta kalibrasyonu eklendi\n\n" +
                "Kullanım: bağlandıktan sonra sağ kumandayı A noktasına koy TETİĞE bas, " +
                "sonra B noktasına koy TETİĞE bas. Herkes AYNI fiziksel noktaya kalibre olmalı.\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve build al.", "Tamam");
        }

        // PC dedicated-server mode: spectator/map view on the PC + team selection on headsets.
        [MenuItem("Tools/VR Multiplayer/6. Setup Server Mode (PC)")]
        public static void SetupServerMode()
        {
            // Spectator view object in the scene (idle until the server starts).
            if (Object.FindFirstObjectByType<ServerView>() == null)
                new GameObject("Server View").AddComponent<ServerView>();

            // Team selection prompt on the player prefab.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "NetworkPlayer prefab yok. Önce '1'/'2'/'3' adımlarını çalıştır.", "Tamam");
                return;
            }
            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
            try
            {
                if (root.GetComponent<TeamSelector>() == null)
                {
                    root.AddComponent<TeamSelector>();
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Sunucu modu eklendi:\n\n" +
                "• PC: Play > 'SUNUCU başlat' → avatar doğmaz, seyirci kamerası açılır\n" +
                "   (WASD + sağ fare = gez, M = kuşbakışı harita, Q/E = alçal/yüksel)\n" +
                "• Ekranda: bağlı oyuncu sayısı, takımlar, konumlar ve ping\n" +
                "• Gözlükler: B ile katılır → 'TAKIM SEÇ' paneli çıkar (A tuşu = A, B tuşu = B)\n\n" +
                "Sahneyi kaydet (Ctrl+S) ve gözlüklere YENİDEN build al (prefab değişti).", "Tamam");
        }

        // ---------------------------------------------------------------- Builders
        static GameObject BuildXRRig()
        {
            // Only the XR camera should render and be the main camera. Remove stray audio
            // listeners and disable/untag any pre-existing cameras (e.g. the template's).
            foreach (var al in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                Object.DestroyImmediate(al);
            foreach (var oldCam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                oldCam.enabled = false;
                if (oldCam.CompareTag("MainCamera")) oldCam.tag = "Untagged";
            }

            var rig = new GameObject("XR Rig");
            var rigRef = rig.AddComponent<XRRigReference>();
            var loco = rig.AddComponent<XRRigLocomotion>();
            rig.AddComponent<XRTrackingOriginSetup>();

            // Camera = the player's head (driven directly by the HMD pose).
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(rig.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1000f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<XRDevicePoseDriver>().node = XRNode.Head;

            // Invisible hand source transforms (the visible hands come from the networked avatar).
            var lh = new GameObject("LeftHand Anchor");
            lh.transform.SetParent(rig.transform, false);
            lh.AddComponent<XRDevicePoseDriver>().node = XRNode.LeftHand;

            var rh = new GameObject("RightHand Anchor");
            rh.transform.SetParent(rig.transform, false);
            rh.AddComponent<XRDevicePoseDriver>().node = XRNode.RightHand;

            rigRef.head = camGo.transform;
            rigRef.leftHand = lh.transform;
            rigRef.rightHand = rh.transform;
            loco.head = camGo.transform;

            return rig;
        }

        static GameObject BuildHead(Transform parent)
        {
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            Object.DestroyImmediate(head.GetComponent<Collider>());
            head.transform.SetParent(parent, false);
            head.transform.localScale = Vector3.one * 0.24f;
            Paint(head, "Head", new Color(0.92f, 0.82f, 0.72f));

            // A "nose" cube so other players can read which way you're facing.
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Nose";
            Object.DestroyImmediate(nose.GetComponent<Collider>());
            nose.transform.SetParent(head.transform, false);
            nose.transform.localScale = new Vector3(0.3f, 0.3f, 0.55f);
            nose.transform.localPosition = new Vector3(0f, -0.1f, 0.5f);
            Paint(nose, "Nose", new Color(0.2f, 0.2f, 0.25f));
            return head;
        }

        static GameObject BuildHand(Transform parent, string name, Color color)
        {
            var hand = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hand.name = name;
            Object.DestroyImmediate(hand.GetComponent<Collider>());
            hand.transform.SetParent(parent, false);
            hand.transform.localScale = new Vector3(0.08f, 0.045f, 0.13f);
            Paint(hand, name, color);
            return hand;
        }

        // Wires a Mixamo idle FBX (Humanoid, looping) into the avatar's Animator so the legs
        // and torso hold a natural standing pose while the arm IK follows the controllers.
        // ---------------------------------------------------------------- Helpers
        static void ConfigureNetworkTransform(NetworkTransform t)
        {
            t.SyncPositionX = t.SyncPositionY = t.SyncPositionZ = true;
            t.SyncRotAngleX = t.SyncRotAngleY = t.SyncRotAngleZ = true;
            t.SyncScaleX = t.SyncScaleY = t.SyncScaleZ = false; // avatars don't scale
            t.Interpolate = true;                               // smooth remote avatars
        }

        /// <summary>NetworkPlayer.prefab'i duzenlemek icin acar; yoksa exception yerine
        /// aciklayici diyalog gosterip null doner (cagiran erken cikmali).</summary>
        static GameObject LoadPlayerPrefabOrWarn()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                EditorUtility.DisplayDialog("NetworkPlayer prefab yok",
                    "NetworkPlayer.prefab bulunamadi.\nOnce '1. Create NetworkPlayer Prefab' ve '2. Setup Current Scene' adimlarini calistir.",
                    "Tamam");
                return null;
            }
            return PrefabUtility.LoadPrefabContents(PrefabPath);
        }

        /// <summary>Malzeme URP'de duzgun cizilir mi? (magenta/eksik shader tespiti)</summary>
        static bool IsUrpCompatible(Material m)
        {
            if (m == null || m.shader == null) return false;
            string n = m.shader.name;
            if (n == "Hidden/InternalErrorShader") return false;
            return n.Contains("Universal Render Pipeline") || n.StartsWith("Shader Graphs/") || n.Contains("URP");
        }

        static Material GetOrCreateMat(string key, Color color)
        {
            EnsureFolder(MatFolder);
            string path = MatFolder + "/Mat_" + key + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            m.color = color;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void Paint(GameObject go, string key, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetOrCreateMat(key, color);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace("\\", "/");
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
