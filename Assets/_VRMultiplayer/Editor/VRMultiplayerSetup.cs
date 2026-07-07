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
    public static class VRMultiplayerSetup
    {
        const string Root = "Assets/_VRMultiplayer";
        const string PrefabFolder = Root + "/Prefabs";
        const string MatFolder = Root + "/Materials";
        const string AvatarFolder = Root + "/Avatar";
        const string PrefabPath = PrefabFolder + "/NetworkPlayer.prefab";

        // ---------------------------------------------------------------- Prefab
        [MenuItem("Tools/VR Multiplayer/1. Create NetworkPlayer Prefab")]
        public static GameObject CreateNetworkPlayerPrefab()
        {
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

        // ---------------------------------------------------------------- Humanoid avatar
        [MenuItem("Tools/VR Multiplayer/3. Add Humanoid Avatar (select model first)")]
        public static void AddHumanoidAvatar()
        {
            var model = Selection.activeObject as GameObject;
            if (model == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce Project penceresinde Humanoid modelini (FBX) seç, sonra bu menüyü çalıştır.", "Tamam");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
                CreateNetworkPlayerPrefab();

            EnsureFolder(AvatarFolder);

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var headChild = root.transform.Find("Head");
                var leftChild = root.transform.Find("LeftHand");
                var rightChild = root.transform.Find("RightHand");
                if (headChild == null || leftChild == null || rightChild == null)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Prefab'da Head/LeftHand/RightHand yok. Önce '1. Create NetworkPlayer Prefab' çalıştır.", "Tamam");
                    return;
                }

                var existing = root.transform.Find("Avatar");
                if (existing != null) Object.DestroyImmediate(existing.gameObject);

                var avatar = (GameObject)PrefabUtility.InstantiatePrefab(model);
                avatar.transform.SetParent(root.transform, false);
                PrefabUtility.UnpackPrefabInstance(avatar, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                avatar.name = "Avatar";
                avatar.transform.localPosition = Vector3.zero;
                avatar.transform.localRotation = Quaternion.identity;

                var animator = avatar.GetComponentInChildren<Animator>();
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Seçilen model Humanoid değil.\nModeli seç > Inspector > Rig > Animation Type = Humanoid > Apply, sonra tekrar dene.", "Tamam");
                    Object.DestroyImmediate(avatar);
                    return;
                }
                var animGo = animator.gameObject;

                var controllerPath = AvatarFolder + "/EmptyAvatarController.controller";
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                animator.runtimeAnimatorController = controller;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // never freeze the rig

                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                Transform lU = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform lL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                Transform lH = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform rU = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Transform rL = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                Transform rH = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (!head || !lU || !lL || !lH || !rU || !rL || !rH)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Humanoid kemikleri eksik. Rig > Configure ile kol/kafa eşleşmesinin yeşil olduğundan emin ol.", "Tamam");
                    Object.DestroyImmediate(avatar);
                    return;
                }

                // Scale the model so the head bone sits ~1.6 m above the feet, and record the
                // feet position relative to the avatar root (measured from FOOT BONES, which are
                // reliable — SkinnedMeshRenderer.bounds is not, at author time). Bone Transforms
                // are live, so their world positions update after we change the scale.
                Transform lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                float FootY() => Mathf.Min(
                    lFoot != null ? lFoot.position.y : head.position.y,
                    rFoot != null ? rFoot.position.y : head.position.y);

                float modelHeadHeight = head.position.y - FootY();
                if (modelHeadHeight > 0.2f)
                    animGo.transform.localScale *= (1.6f / modelHeadHeight);

                // Ankle is ~0.09 m above the sole; drop to the sole so the feet rest on the floor.
                float feetOffsetValue = (FootY() - animGo.transform.position.y) - 0.09f;

                // --- Animation Rigging: one Two-Bone IK per arm ---
                var rigBuilder = animGo.GetComponent<RigBuilder>();
                if (rigBuilder == null) rigBuilder = animGo.AddComponent<RigBuilder>();
                rigBuilder.layers.Clear();

                var rigGo = new GameObject("UpperBodyRig");
                rigGo.transform.SetParent(animGo.transform, false);
                var rig = rigGo.AddComponent<Rig>();
                rig.weight = 1f;

                var targets = new GameObject("IKTargets");
                targets.transform.SetParent(animGo.transform, false);
                var lTarget = CreateChild(targets.transform, "LeftHandTarget", lH.position, lH.rotation);
                var rTarget = CreateChild(targets.transform, "RightHandTarget", rH.position, rH.rotation);

                Vector3 back = -animGo.transform.forward * 0.25f - animGo.transform.up * 0.35f;
                var lHint = CreateChild(rigGo.transform, "LeftElbowHint", lL.position + back, Quaternion.identity);
                var rHint = CreateChild(rigGo.transform, "RightElbowHint", rL.position + back, Quaternion.identity);

                AddArmIK(rigGo.transform, "IK_LeftArm", lU, lL, lH, lTarget, lHint);
                AddArmIK(rigGo.transform, "IK_RightArm", rU, rL, rH, rTarget, rHint);

                rigBuilder.layers.Add(new RigLayer(rig, true));

                // --- Runtime IK controller (reads the networked children) ---
                var ik = animGo.AddComponent<AvatarIKController>();
                ik.headSource = headChild; ik.leftHandSource = leftChild; ik.rightHandSource = rightChild;
                ik.ikLeftHandTarget = lTarget; ik.ikRightHandTarget = rTarget;
                ik.headBone = head;
                ik.feetOffset = feetOffsetValue; // measured above; keeps feet on the floor

                // --- Procedural finger curl (grip/trigger -> fist), if the rig has fingers ---
                if (animGo.GetComponent<ProceduralFingerPoser>() == null)
                    animGo.AddComponent<ProceduralFingerPoser>();

                // --- Name tag ---
                var tagGo = new GameObject("NameTag");
                tagGo.transform.SetParent(animGo.transform, false);
                tagGo.transform.localPosition = new Vector3(0f, 2.0f, 0f);
                tagGo.transform.localScale = Vector3.one * 0.2f;
                var tm = tagGo.AddComponent<TextMesh>();
                tm.text = "Oyuncu"; tm.characterSize = 0.1f; tm.fontSize = 64;
                tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center; tm.color = Color.white;
                tagGo.AddComponent<Billboard>();

                var skinned = avatar.GetComponentInChildren<SkinnedMeshRenderer>();

                // Force a URP-compatible material on every slot so the model isn't magenta in
                // URP and the per-player color tint (via MaterialPropertyBlock) always applies.
                if (skinned != null)
                {
                    var avatarMat = GetOrCreateMat("Avatar", new Color(0.85f, 0.85f, 0.85f));
                    int slots = Mathf.Max(1, skinned.sharedMaterials.Length);
                    var mats = new Material[slots];
                    for (int i = 0; i < slots; i++) mats[i] = avatarMat;
                    skinned.sharedMaterials = mats;
                }

                // --- Identity (color + name) on the root ---
                var identity = root.GetComponent<PlayerIdentity>();
                if (identity == null) identity = root.AddComponent<PlayerIdentity>();
                var soId = new SerializedObject(identity);
                soId.FindProperty("avatarRenderer").objectReferenceValue = skinned;
                soId.FindProperty("nameTag").objectReferenceValue = tm;
                soId.ApplyModifiedPropertiesWithoutUndo();

                // --- Visibility wiring on NetworkVRPlayer ---
                var nvp = root.GetComponent<NetworkVRPlayer>();
                var soNvp = new SerializedObject(nvp);
                soNvp.FindProperty("remoteAvatar").objectReferenceValue = avatar;
                SetRendererArray(soNvp.FindProperty("ownerOnlyRenderers"),
                    new[] { leftChild.GetComponent<Renderer>(), rightChild.GetComponent<Renderer>() });
                SetRendererArray(soNvp.FindProperty("alwaysHiddenRenderers"),
                    headChild.GetComponentsInChildren<Renderer>());
                soNvp.FindProperty("ownerHiddenRenderers").arraySize = 0;
                soNvp.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("[VRMultiplayerSetup] Humanoid avatar added to NetworkPlayer prefab.");
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Humanoid avatar eklendi!\n\n• Kollar IK ile kumandaları takip eder\n• Diğerleri seni insansı görür; sen kendi küp ellerini görürsün\n• Her oyuncuya otomatik renk + isim\n\nPlay/Build'de test et. Eller ters/kayıksa AvatarIKController'daki Grip Offset'leri, dirsek ters bükülürse ElbowHint konumlarını ayarla.", "Tamam");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static Transform CreateChild(Transform parent, string name, Vector3 worldPos, Quaternion worldRot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(worldPos, worldRot);
            return go.transform;
        }

        static void AddArmIK(Transform rigRoot, string name, Transform upper, Transform lower, Transform hand, Transform target, Transform hint)
        {
            var go = new GameObject(name);
            go.transform.SetParent(rigRoot, false);
            var c = go.AddComponent<TwoBoneIKConstraint>();
            ref var d = ref c.data; // 'data' is a get-only ref-return; mutate in place, do NOT assign back.
            d.root = upper; d.mid = lower; d.tip = hand;
            d.target = target; d.hint = hint;
            d.targetPositionWeight = 1f; d.targetRotationWeight = 0f; d.hintWeight = 1f; // position-only = stable arms
            d.maintainTargetPositionOffset = false; d.maintainTargetRotationOffset = false;
        }

        static void SetRendererArray(SerializedProperty prop, Renderer[] items)
        {
            prop.arraySize = items.Length;
            for (int i = 0; i < items.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        // Adds mesh colliders to the LOKIT ground so avatars (and the ground-snap raycast) can
        // rest feet on the real terrain everywhere, even on uneven parts.
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
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
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
        [MenuItem("Tools/VR Multiplayer/7. Add Idle Animation (select idle FBX first)")]
        public static void AddIdleAnimation()
        {
            var sel = Selection.activeObject;
            string path = sel != null ? AssetDatabase.GetAssetPath(sel) : null;
            if (string.IsNullOrEmpty(path) || !path.ToLower().EndsWith(".fbx"))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce Project'te idle animasyon FBX'ini seç (Mixamo 'Idle', Without Skin), sonra bu menüyü çalıştır.", "Tamam");
                return;
            }

            // Import as Humanoid with a LOOPING clip.
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                if (importer.animationType != ModelImporterAnimationType.Human)
                    importer.animationType = ModelImporterAnimationType.Human;
                var clipDefs = importer.defaultClipAnimations;
                if (clipDefs != null && clipDefs.Length > 0)
                {
                    foreach (var c in clipDefs) c.loopTime = true;
                    importer.clipAnimations = clipDefs;
                }
                importer.SaveAndReimport();
            }

            AnimationClip idle = null;
            foreach (var a in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview")) { idle = clip; break; }
            if (idle == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Bu FBX içinde animasyon klibi bulunamadı. Mixamo'dan 'Idle' animasyonunu 'FBX for Unity, Without Skin' olarak indir.", "Tamam");
                return;
            }

            EnsureFolder(AvatarFolder);
            string ctrlPath = AvatarFolder + "/IdleController.controller";
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
            if (ctrl == null) ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var sm = ctrl.layers[0].stateMachine;
            if (sm.states.Length == 0)
            {
                var st = sm.AddState("Idle");
                st.motion = idle;
                sm.defaultState = st;
            }
            else
            {
                sm.states[0].state.motion = idle;
                sm.defaultState = sm.states[0].state;
            }
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var animator = root.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Prefab'da Animator yok. Önce '3. Add Humanoid Avatar' çalıştır.", "Tamam");
                    return;
                }
                animator.runtimeAnimatorController = ctrl;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Idle animasyonu bağlandı!\n\nBacaklar/gövde doğal duracak, kollar IK ile kumandaları takip etmeye devam edecek.\n\nGözlüklere YENİDEN build al.", "Tamam");
        }

        // Adds a Mixamo walk clip and rebuilds the controller as an Idle<->Walk blend driven
        // by the "Speed" parameter (fed by AvatarIKController from real head movement).
        [MenuItem("Tools/VR Multiplayer/8. Add Walk Animation (select walk FBX first)")]
        public static void AddWalkAnimation()
        {
            var sel = Selection.activeObject;
            string path = sel != null ? AssetDatabase.GetAssetPath(sel) : null;
            if (string.IsNullOrEmpty(path) || !path.ToLower().EndsWith(".fbx"))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce Project'te yürüme FBX'ini seç (Mixamo 'Walking', IN PLACE işaretli, Without Skin).", "Tamam");
                return;
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                if (importer.animationType != ModelImporterAnimationType.Human)
                    importer.animationType = ModelImporterAnimationType.Human;
                var clipDefs = importer.defaultClipAnimations;
                if (clipDefs != null && clipDefs.Length > 0)
                {
                    foreach (var c in clipDefs) c.loopTime = true;
                    importer.clipAnimations = clipDefs;
                }
                importer.SaveAndReimport();
            }

            AnimationClip walk = null;
            foreach (var a in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview")) { walk = clip; break; }
            if (walk == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer", "Bu FBX içinde animasyon klibi yok.", "Tamam");
                return;
            }

            string ctrlPath = AvatarFolder + "/IdleController.controller";
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
            if (ctrl == null || ctrl.animationClips.Length == 0)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce '7. Add Idle Animation' adımını çalıştır (idle klibi gerekli).", "Tamam");
                return;
            }
            AnimationClip idle = ctrl.animationClips[0];

            bool hasParam = false;
            foreach (var p in ctrl.parameters) if (p.name == "Speed") hasParam = true;
            if (!hasParam) ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);

            var sm = ctrl.layers[0].stateMachine;
            foreach (var s in sm.states) sm.RemoveState(s.state);

            var tree = new BlendTree
            {
                name = "Locomotion",
                blendParameter = "Speed",
                blendType = BlendTreeType.Simple1D,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, ctrl);
            tree.AddChild(idle, 0f);
            tree.AddChild(walk, 1.2f); // full walk at ~1.2 m/s (typical walking pace)

            var st = sm.AddState("Locomotion");
            st.motion = tree;
            sm.defaultState = st;

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Yürüme animasyonu bağlandı!\n\nGerçekte yürüyünce avatar adım atacak, durunca idle'a dönecek " +
                "(hıza göre yumuşak geçiş).\n\nGözlüklere YENİDEN build al.", "Tamam");
        }

        // Adds a Mixamo crouch-idle clip as an override LAYER; AvatarIKController drives the
        // layer weight from how far the player has physically ducked below standing height.
        [MenuItem("Tools/VR Multiplayer/9. Add Crouch Animation (select crouch FBX first)")]
        public static void AddCrouchAnimation()
        {
            var sel = Selection.activeObject;
            string path = sel != null ? AssetDatabase.GetAssetPath(sel) : null;
            if (string.IsNullOrEmpty(path) || !path.ToLower().EndsWith(".fbx"))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce Project'te çömelme FBX'ini seç (Mixamo 'Crouch Idle' / 'Crouching Idle', Without Skin).", "Tamam");
                return;
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                if (importer.animationType != ModelImporterAnimationType.Human)
                    importer.animationType = ModelImporterAnimationType.Human;
                var clipDefs = importer.defaultClipAnimations;
                if (clipDefs != null && clipDefs.Length > 0)
                {
                    foreach (var c in clipDefs) c.loopTime = true;
                    importer.clipAnimations = clipDefs;
                }
                importer.SaveAndReimport();
            }

            AnimationClip crouch = null;
            foreach (var a in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview")) { crouch = clip; break; }
            if (crouch == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer", "Bu FBX içinde animasyon klibi yok.", "Tamam");
                return;
            }

            string ctrlPath = AvatarFolder + "/IdleController.controller";
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
            if (ctrl == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Önce '7. Add Idle Animation' adımını çalıştır.", "Tamam");
                return;
            }

            // Remove a previous Crouch layer so re-running stays clean.
            for (int i = ctrl.layers.Length - 1; i >= 1; i--)
                if (ctrl.layers[i].name == "Crouch")
                    ctrl.RemoveLayer(i);

            var sm = new AnimatorStateMachine { name = "Crouch", hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(sm, ctrl);
            var layer = new AnimatorControllerLayer
            {
                name = "Crouch",
                defaultWeight = 0f, // driven at runtime by AvatarIKController
                stateMachine = sm,
            };
            var st = sm.AddState("CrouchIdle");
            st.motion = crouch;
            sm.defaultState = st;
            ctrl.AddLayer(layer);

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Çömelme animasyonu bağlandı!\n\nGerçekte eğildikçe avatarın dizleri bükülecek " +
                "(boyunun ~%92'sinin altında başlar, ~%65'te tam çömelme).\n\nGözlüklere YENİDEN build al.", "Tamam");
        }

        // Makes the selected SCENE objects grabbable (networked physics + grab state) and makes
        // sure the player prefab carries a HandGrabber wired to its networked hands.
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
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
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
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
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

            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in target.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest != null)
            {
                var mesh = biggest.sharedMesh;
                Bounds mb = mesh.bounds;
                Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
                Vector3 axis = Vector3.right;
                float len = Mathf.Abs(size.x);
                if (Mathf.Abs(size.y) > len) { axis = Vector3.up; len = Mathf.Abs(size.y); }
                if (Mathf.Abs(size.z) > len) axis = Vector3.forward;
                float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
                if (sign == 0f) sign = 1f;
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

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var health = root.GetComponent<PlayerHealth>();
                if (health == null) health = root.AddComponent<PlayerHealth>();
                if (root.GetComponent<PlayerHUD>() == null) root.AddComponent<PlayerHUD>();

                var headCarrier = root.transform.Find("Head");

                // Over-head health bar that OTHER players see above this player's head.
                var ohb = root.GetComponent<OverheadHealthBar>();
                if (ohb == null) ohb = root.AddComponent<OverheadHealthBar>();
                var ohbSo = new SerializedObject(ohb);
                ohbSo.FindProperty("head").objectReferenceValue = headCarrier;
                ohbSo.ApplyModifiedPropertiesWithoutUndo();

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
                "• Kendi caniniz SAG ALTTA bir CAN cubugunda\n" +
                "• Rakiplerin cani KAFALARININ USTUNDE gorunur\n" +
                "• Vurulunca kirmizi flas + kumanda titresimi\n\n" +
                "Gozluklere YENIDEN build al (prefab degisti).", "Tamam");
        }

        // Adds the procedural finger poser to the CURRENT prefab's avatar (for an avatar that was
        // set up before this feature existed). Idempotent.
        [MenuItem("Tools/VR Multiplayer/19. Add Finger Poser (mevcut avatara)")]
        public static void AddFingerPoser()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var avatar = root.transform.Find("Avatar");
                var animator = avatar != null ? avatar.GetComponentInChildren<Animator>() : null;
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Prefabda Humanoid Avatar bulunamadi. Once menu 3 ile avatari ekle.", "Tamam");
                    return;
                }
                if (animator.gameObject.GetComponent<ProceduralFingerPoser>() == null)
                    animator.gameObject.AddComponent<ProceduralFingerPoser>();
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Parmak poz sistemi avatara eklendi.\n\n" +
                "• Grip -> orta/yuzuk/serce parmak buker\n" +
                "• Tetik -> isaret parmagi\n" +
                "• Herkes (ag uzerinden) parmak hareketini gorur\n\n" +
                "Play/Build'de test et. Parmaklar TERS bukuluyorsa avatardaki\n" +
                "ProceduralFingerPoser'da 'Invert Curl' isaretle.", "Tamam");
        }

        // Strips the modular soldier's unnecessary gear meshes (flags, extra pouches, magazines,
        // glasses, spare helmets/beards, radios...) down to a core body set so the avatar is
        // Quest-viable. Deletes ONLY leaf SkinnedMeshRenderer GameObjects that match a junk name
        // pattern — never touches the skeleton (bones have no SMR and are never leaves here).
        [MenuItem("Tools/VR Multiplayer/20. Strip Soldier Gear (ayikla)")]
        public static void StripSoldierGear()
        {
            // Junk name fragments (lowercase, substring match). Carefully avoids the core parts
            // (Heads, Eye, Glove, Jaket, Pants, boots, Necck, Belt, Bodyarmour).
            string[] junk =
            {
                "flag", "headphone", "bagmag", "maga", "magazine", "bagbullet", "bullet",
                "bag1", "bag2", "bag3", "bag4", "bag22", "bagakum", "backpack", "dumppouch",
                "pounch", "pouch", "pistolpouch", "kobur", "holster", "glass", "night", "nigt",
                "cap", "hat", "boonie", "helmet", "gasmask", "mask", "balaclava", "eyegas",
                "radio", "racio", "ptt", "antenna", "phone", "logoarm", "shevron", "logohelmet",
                "watch", "scissors", "tourniquet", "pain", "m18", "armbelt", "beard", "kit",
                // Redundant with our own Rifle_HK416 + leftover junk parts:
                "carabine", "gun", "camera", "kopyto", "kjjj", "gofra", "cord", "cylinder",
            };

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var avatar = root.transform.Find("Avatar");
                if (avatar == null)
                {
                    EditorUtility.DisplayDialog("VR Multiplayer", "Prefabda Avatar yok.", "Tamam");
                    return;
                }

                var smrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var toDelete = new System.Collections.Generic.List<GameObject>();
                var keptNames = new System.Collections.Generic.List<string>();

                foreach (var smr in smrs)
                {
                    string n = smr.gameObject.name.ToLowerInvariant();
                    bool strip = false;
                    foreach (var j in junk)
                        if (n.Contains(j)) { strip = true; break; }

                    // Safety: only delete leaf mesh nodes (no children) so a bone chain is never
                    // removed accidentally.
                    if (strip && smr.transform.childCount == 0)
                        toDelete.Add(smr.gameObject);
                    else
                        keptNames.Add(smr.gameObject.name);
                }

                foreach (var go in toDelete)
                    Object.DestroyImmediate(go);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);

                string keptList = keptNames.Count <= 25
                    ? string.Join(", ", keptNames)
                    : string.Join(", ", keptNames.GetRange(0, 25)) + " ...";

                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Asker ayiklandi:\n\n" +
                    "• Silinen gereksiz parca: " + toDelete.Count + "\n" +
                    "• Kalan gorsel parca: " + keptNames.Count + "\n\n" +
                    "Kalanlar: " + keptList + "\n\n" +
                    "Iskelet (kemikler + parmaklar) DOKUNULMADI — IK ve parmaklar calisir.\n" +
                    "Beklenmeyen bir sey silindiyse Ctrl+Z; ya da junk listesinden cikar.\n\n" +
                    "Sonra dokulari ASTC'ye sikistir, sonra build al.", "Tamam");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---------------------------------------------------------------- Helpers
        static void ConfigureNetworkTransform(NetworkTransform t)
        {
            t.SyncPositionX = t.SyncPositionY = t.SyncPositionZ = true;
            t.SyncRotAngleX = t.SyncRotAngleY = t.SyncRotAngleZ = true;
            t.SyncScaleX = t.SyncScaleY = t.SyncScaleZ = false; // avatars don't scale
            t.Interpolate = true;                               // smooth remote avatars
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
