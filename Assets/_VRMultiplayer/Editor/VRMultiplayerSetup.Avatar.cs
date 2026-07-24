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
    /// <summary>Avatar kurulum menuleri (3/7/8/9/19/20/21): humanoid ekleme, renkli asker
    /// degisimi, idle/walk/crouch animasyonlari, parmak pozlayici, teçhizat ayiklama.
    /// VRMultiplayerSetup'in partial parcasi — 1200+ satirlik tek dosya domain bazli bolundu.</summary>
    public static partial class VRMultiplayerSetup
    {
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

            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
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

                // URP uyumsuz (magenta) slotlara gri yedek malzeme atanir. Modelin KENDI URP
                // malzemeleri korunur — aksi halde renkli asker dokulari duz griye ezilir.
                // Takim tonu MaterialPropertyBlock ile uygulanir; URP/Lit _BaseColor'i destekler.
                if (skinned != null)
                {
                    var avatarMat = GetOrCreateMat("Avatar", new Color(0.85f, 0.85f, 0.85f));
                    var src = skinned.sharedMaterials;
                    int slots = Mathf.Max(1, src.Length);
                    var mats = new Material[slots];
                    for (int i = 0; i < slots; i++)
                    {
                        var m = i < src.Length ? src[i] : null;
                        mats[i] = IsUrpCompatible(m) ? m : avatarMat;
                    }
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
                if (!SilentSetup)
                    EditorUtility.DisplayDialog("VR Multiplayer",
                        "Humanoid avatar eklendi!\n\n• Kollar IK ile kumandaları takip eder\n• Diğerleri seni insansı görür; sen kendi küp ellerini görürsün\n• Her oyuncuya otomatik renk + isim\n\nPlay/Build'de test et. Eller ters/kayıksa AvatarIKController'daki Grip Offset'leri, dirsek ters bükülürse ElbowHint konumlarını ayarla.", "Tamam");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Rebuilds the player avatar from the new colored soldier FBX using the proven "3. Add
        // Humanoid Avatar" pipeline, but head-less (loads + selects the model, suppresses dialogs).
        [MenuItem("Tools/VR Multiplayer/21. Swap Avatar To Colored Soldier")]
        public static void SwapAvatarToColoredSoldier()
        {
            const string coloredPath = "Assets/Soldiers-Pack/Mesh/US-Soldier-Colored.fbx";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(coloredPath);
            if (model == null)
            {
                Debug.LogError("[VRMultiplayerSetup] " + coloredPath + " bulunamadi.");
                return;
            }
            var anim = model.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
            {
                Debug.LogError("[VRMultiplayerSetup] Colored FBX Humanoid degil. Rig > Animation Type = Humanoid > Apply yapip tekrar dene.");
                return;
            }
            Selection.activeObject = model;
            SilentSetup = true;
            try { AddHumanoidAvatar(); }
            finally { SilentSetup = false; }
            Debug.Log("[VRMultiplayerSetup] Colored soldier avatarina gecildi (rig + IK + finger poser yeniden kuruldu).");
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

            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
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
        [MenuItem("Tools/VR Multiplayer/19. Add Finger Poser (mevcut avatara)")]
        public static void AddFingerPoser()
        {
            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
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

            var root = LoadPlayerPrefabOrWarn();
            if (root == null) return;
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
                    "DIKKAT: Bu silme GERI ALINAMAZ (Ctrl+Z calismaz) — geri donus yalnizca git.\n" +
                    "Beklenmeyen bir sey silindiyse prefabi git'ten geri al; ya da junk listesinden cikar.\n\n" +
                    "Sonra dokulari ASTC'ye sikistir, sonra build al.", "Tamam");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
