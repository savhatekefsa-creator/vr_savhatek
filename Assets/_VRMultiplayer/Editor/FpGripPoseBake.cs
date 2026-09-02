using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah tutus pozlarini BIRINCI SAHIS eline cevirir.
    ///
    /// Sorun: authored parmak pozu avatarin HUMANOID kemiklerinde saklaniyor
    /// (<see cref="HandPoseBones"/>), FP eli ise Meta'nin Generic rig'ini kullaniyor.
    /// Lokal rotasyonlar iki rig arasinda tasinmaz - dinlenme yonelimleri farkli.
    ///
    /// Tasinabilen sey ACIDIR. Parmak bogumu tek eksende katlanir; o ekseni her iki
    /// rig'de de KENDI geometrisinden turetiyoruz, dolayisiyla aradaki fark onemsizlesiyor:
    ///   - avatarda: aci = authored pozun dinlenmeden sapmasi, isaret o rig'in
    ///     Cross(uzanim, avuc normali) mentesesine gore
    ///   - FP elinde: ayni aci, o rig'in kendi mentesesine uygulanir
    ///     (dort parmakta ayni geometrik kural, BASPARMAKTA rig'in kendi fe-eksen
    ///     isaretcileri - basparmak avuc duzleminde katlanmaz, olculdu)
    ///
    /// Cevrim burada BIR KEZ yapilip profile yazilir; runtime yalnizca uygular.
    /// Poz yeniden yazilirsa (menu 31) bu arac tekrar kosulmali.
    /// </summary>
    public static class FpGripPoseBake
    {
        const string AvatarPrefab = "Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab";
        const string HandPrefab = "FPHands/Meta/OculusHand_";

        /// <summary>FP rig'inde 15 eklemin kemik adi - <see cref="HandPoseBones"/> ile AYNI SIRADA.
        /// Humanoid basparmak 3 bogum, Meta'da 4 var (thumb0 = avuc ici kok): Proximal->thumb0,
        /// Intermediate->thumb1, Distal->thumb2. Meta thumb3 dondurulmez.</summary>
        static readonly string[] FpBone =
        {
            "thumb0", "thumb1", "thumb2",
            "index1", "index2", "index3",
            "middle1", "middle2", "middle3",
            "ring1", "ring2", "ring3",
            "pinky1", "pinky2", "pinky3",
        };

        /// <summary>Basparmak eklemlerinin FP rig'indeki anatomik eksen isaretcileri.
        /// Isaretcinin RIGHT vektoru eksendir (konvansiyon isaret parmaginda dogrulandi).</summary>
        static readonly string[] ThumbAxisMarker =
        {
            "thumb_cmc_fe_axis_marker", "thumb_mcp_fe_axis_marker", "thumb_ip_fe_axis_marker",
        };

        [MenuItem("Tools/VR Multiplayer/50. Silah Tutuslarini FP Eline Cevir")]
        public static void BakeAll()
        {
            var avatar = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefab);
            var animator = avatar != null ? avatar.GetComponentInChildren<Animator>(true) : null;
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogError("[FpGripPoseBake] Humanoid avatar bulunamadi: " + AvatarPrefab);
                return;
            }

            var axesR = FpFrames(false);
            var axesL = FpFrames(true);
            if (axesR == null || axesL == null) return;

            int wrote = 0, skipped = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var profile = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (profile == null) continue;
                bool dirty = false;
                dirty |= BakeHand(ref profile.mainHand, animator, axesR, axesL, ref wrote, ref skipped);
                dirty |= BakeHand(ref profile.supportHand, animator, axesR, axesL, ref wrote, ref skipped);
                if (dirty) EditorUtility.SetDirty(profile);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[FpGripPoseBake] " + wrote + " el pozu cevrildi, " + skipped + " atlandi (authored poz yok).");
        }

        /// <summary>Bir rig'in tasima icin gereken cercevesi: el cercevesi (parmak yonu +
        /// avuc normali) ve 15 kemigin DINLENME dunya yonelimi.</summary>
        class RigFrame
        {
            public Quaternion hand;
            public Quaternion[] boneRest = new Quaternion[HandPoseBones.JointCount];
            public Quaternion[] boneRestLocal = new Quaternion[HandPoseBones.JointCount];
        }

        static bool BakeHand(ref WeaponGripProfile.HandPose pose, Animator animator,
                             RigFrame fpR, RigFrame fpL, ref int wrote, ref int skipped)
        {
            bool dirty = false;
            dirty |= BakeOne(ref pose.rightFingers, false, animator, fpR, ref wrote, ref skipped);
            dirty |= BakeOne(ref pose.leftFingers, true, animator, fpL, ref wrote, ref skipped);
            return dirty;
        }

        static bool BakeOne(ref WeaponGripProfile.FingerPose fp, bool left, Animator animator,
                            RigFrame fpRig, ref int wrote, ref int skipped)
        {
            if (!fp.HasPose) { skipped++; return false; }
            var av = AvatarFrame(animator, left);
            if (av == null) return false;

            var joints = new Quaternion[HandPoseBones.JointCount];
            for (int j = 0; j < HandPoseBones.JointCount; j++)
                joints[j] = Transfer(av, fpRig, j, fp.joints[j]);
            fp.fpJoints = joints;

            if (fp.HasIndexPulled)
            {
                var pulled = new Quaternion[HandPoseBones.IndexJointCount];
                for (int k = 0; k < HandPoseBones.IndexJointCount; k++)
                    pulled[k] = Transfer(av, fpRig, HandPoseBones.IndexFirst + k, fp.indexPulled[k]);
                fp.fpIndexPulledJoints = pulled;
            }
            wrote++;
            return true;
        }

        /// <summary>
        /// Bir eklemin authored sapmasini avatar rig'inden FP rig'ine tasir.
        ///
        /// Sapmanin ACISI korunur, EKSENI cerceve degistirir: kemigin dinlenme cercevesinden
        /// EL cercevesine cikarilir, oradan FP kemiginin dinlenme cercevesine indirilir.
        /// El cercevesi iki rig'de de AYNI formulle kuruluyor (parmak yonu ileri, ham avuc
        /// normali yukari) - sol elde normalin ters bakmasi iki tarafta da ayni oldugu icin
        /// sadelesir, bu yuzden el-lilik duzeltmesi YAPILMAZ.
        /// </summary>
        static Quaternion Transfer(RigFrame av, RigFrame fp, int j, Quaternion authoredLocal)
        {
            Quaternion delta = Quaternion.Inverse(av.boneRestLocal[j]) * authoredLocal;
            delta.ToAngleAxis(out float angle, out Vector3 axisLocal);
            if (float.IsNaN(axisLocal.x) || axisLocal.sqrMagnitude < 1e-8f) return Quaternion.identity;

            Vector3 axisWorld = av.boneRest[j] * axisLocal.normalized;
            Vector3 axisHand = Quaternion.Inverse(av.hand) * axisWorld;      // el cercevesine
            Vector3 axisFpWorld = fp.hand * axisHand;                        // FP elinin cercevesine
            Vector3 axisFpLocal = Quaternion.Inverse(fp.boneRest[j]) * axisFpWorld;
            return Quaternion.AngleAxis(angle, axisFpLocal.normalized);
        }

        static RigFrame AvatarFrame(Animator animator, bool left)
        {
            var wrist = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var idx = animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var mid = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var pky = animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            if (wrist == null || idx == null || mid == null || pky == null) return null;

            var f = new RigFrame { hand = HandFrame(wrist.position, mid.position, idx.position, pky.position) };
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var b = animator.GetBoneTransform(HandPoseBones.Bone(j, left));
                if (b == null) { f.boneRest[j] = Quaternion.identity; f.boneRestLocal[j] = Quaternion.identity; continue; }
                f.boneRest[j] = b.rotation;
                f.boneRestLocal[j] = b.localRotation;
            }
            return f;
        }

        static RigFrame FpFrames(bool left)
        {
            var prefab = Resources.Load<GameObject>(HandPrefab + (left ? "L" : "R"));
            if (prefab == null) { Debug.LogError("[FpGripPoseBake] FP el prefabi yok"); return null; }
            var go = Object.Instantiate(prefab);
            try
            {
                string p = left ? "b_l_" : "b_r_";
                var map = new System.Collections.Generic.Dictionary<string, Transform>();
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (!map.ContainsKey(t.name)) map[t.name] = t;

                Transform wrist, idx, mid, pky;
                if (!map.TryGetValue(p + "wrist", out wrist) || !map.TryGetValue(p + "index1", out idx)
                    || !map.TryGetValue(p + "middle1", out mid) || !map.TryGetValue(p + "pinky1", out pky))
                { Debug.LogError("[FpGripPoseBake] FP kemikleri cozulemedi"); return null; }

                var f = new RigFrame { hand = HandFrame(wrist.position, mid.position, idx.position, pky.position) };
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                {
                    Transform b;
                    if (!map.TryGetValue(p + FpBone[j], out b) || b == null)
                    { f.boneRest[j] = Quaternion.identity; f.boneRestLocal[j] = Quaternion.identity; continue; }
                    f.boneRest[j] = b.rotation;
                    f.boneRestLocal[j] = b.localRotation;
                }
                return f;
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>El cercevesi: ileri = parmak yonu, yukari = HAM avuc normali.
        /// El-lilik duzeltmesi bilincli olarak yok (bkz. Transfer).</summary>
        static Quaternion HandFrame(Vector3 wrist, Vector3 mid, Vector3 idx, Vector3 pky)
        {
            Vector3 fwd = (mid - wrist).normalized;
            Vector3 palm = Vector3.Cross(fwd, (idx - pky).normalized).normalized;
            return Quaternion.LookRotation(fwd, palm);
        }
    }
}

