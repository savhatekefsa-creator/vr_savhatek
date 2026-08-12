using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Welds an avatar's wrist bone(s) to a held weapon's grip anchor / support rail, in world
    /// space, AFTER the rig evaluation and the finger poser (execution order 110). Because the
    /// write is absolute, the avatar's continuous height-fit scaling can't drift the hand, and
    /// the hand follows the weapon's interpolated transform — fingers never separate from the
    /// grip. Added to the avatar at runtime by <see cref="WeaponGrip"/>; the NetworkPlayer
    /// prefab is never edited.
    ///
    /// Support hand: the anchor is the closest point on the profile's rail segment to this
    /// hand's networked carrier — owners and remote clients project the same replicated
    /// position, so everyone sees the hand at the same spot on the handguard.
    ///
    /// Poses are authored for main=RIGHT / support=LEFT; when the roles are swapped the local
    /// data is mirrored across the weapon's YZ plane. Static (per-hold) values are resolved once
    /// in <see cref="SetHand"/>; only weapon-relative transforms are recomputed per frame.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class WeaponHandWeld : MonoBehaviour
    {
        // Struct + active flag: no per-event heap allocation. Mirroring and Euler→Quaternion
        // are done once here (static per hold), not every LateUpdate.
        struct HandWeld
        {
            public bool active;
            public Transform weapon;
            public WeaponGripProfile profile;
            public bool isSupport;
            public Transform bone;
            public Vector3 gripLocalPos;    // main-hand anchor (mirror-resolved); unused for support
            public Quaternion gripLocalRot; // mirror-resolved
            public Vector3 wristLocalPos;   // mirror-resolved
            public Quaternion wristLocalRot;// mirror-resolved
            public bool mirrored;
            public float blendStart;        // engage ramp start (weight 0 -> 1)
            public bool fadingOut;          // release ramp (weight 1 -> 0), then inactive
            public float fadeOutStart;
        }

        // The weld writes the wrist ABSOLUTELY, so switching it on/off used to relocate the
        // hand in a single frame (the "el kayboluyor/geri geliyor" pop, on every client).
        // Ramping the weld weight over this window blends the wrist between the IK pose and
        // the weapon anchor on both engage and release.
        const float WeldBlendSeconds = 0.12f;

        HandWeld _left, _right;
        Animator _anim;
        AvatarIKController _ik;
        Transform _leftBone, _rightBone;
        // Erisim kelepcesi icin kol kemikleri (bkz. ArmReach): silah nerede olursa
        // olsun kol boyundan uzagini yazmiyoruz.
        Transform _leftUpper, _leftLower, _rightUpper, _rightLower;
        float _leftArmLen, _rightArmLen;   // yerel uzayda, Awake'te bir kere olculur

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _ik = GetComponent<AvatarIKController>();
            if (_anim != null && _anim.isHuman)
            {
                _leftBone = _anim.GetBoneTransform(HumanBodyBones.LeftHand);
                _rightBone = _anim.GetBoneTransform(HumanBodyBones.RightHand);
                _leftUpper = _anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _leftLower = _anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _rightUpper = _anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
                _rightLower = _anim.GetBoneTransform(HumanBodyBones.RightLowerArm);

                // Boy BIR KERE, henuz hicbir weld calismadan olculuyor - yoksa
                // kendi yazdigimiz bilek konumunu geri okurduk (bkz. ArmReach).
                _leftArmLen = ArmReach.MeasureLocal(_leftUpper, _leftLower, _leftBone);
                _rightArmLen = ArmReach.MeasureLocal(_rightUpper, _rightLower, _rightBone);
            }
        }

        /// <summary>Weld one hand onto the weapon (store-only; applied every LateUpdate).</summary>
        public void SetHand(bool left, Transform weapon, WeaponGripProfile profile,
            bool isSupport, bool mirrored)
        {
            var pose = isSupport ? profile.supportHand : profile.mainHand;
            // WeaponGrip re-applies on every replicated state change — keep an in-progress
            // engage ramp instead of restarting it, but a fresh weld (or a re-grab caught
            // mid-fade-out) ramps in from now.
            var prev = left ? _left : _right;
            var w = new HandWeld
            {
                active = true,
                weapon = weapon,
                profile = profile,
                isSupport = isSupport,
                bone = left ? _leftBone : _rightBone,
                gripLocalPos = mirrored ? WeaponGripMath.MirrorX(profile.gripLocalPosition) : profile.gripLocalPosition,
                gripLocalRot = mirrored ? WeaponGripMath.MirrorX(profile.GripLocalRotation) : profile.GripLocalRotation,
                wristLocalPos = mirrored ? WeaponGripMath.MirrorX(pose.wristLocalPosition) : pose.wristLocalPosition,
                wristLocalRot = mirrored ? WeaponGripMath.MirrorX(Quaternion.Euler(pose.wristLocalEuler)) : Quaternion.Euler(pose.wristLocalEuler),
                mirrored = mirrored,
                // Fade-out ORTASINDA yakalanan yeniden tutus: ramp sifirdan baslasaydi agirlik
                // o karede (or.) 0.4'ten 0'a dusup bilek bir karelik IK pozuna sicrardi.
                // Baslangic, kesilen fade'in GUNCEL agirligina denk gelecek sekilde geri
                // tarihlenir — agirlik surekli kalir.
                blendStart = prev.active && !prev.fadingOut ? prev.blendStart
                    : prev.active && prev.fadingOut
                        ? Time.time - Mathf.Clamp01(1f - (Time.time - prev.fadeOutStart) / WeldBlendSeconds) * WeldBlendSeconds
                        : Time.time,
            };
            if (left) _left = w; else _right = w;
            enabled = true;
        }

        public void ClearHand(bool left)
        {
            // Don't cut the weld in one frame — fade the wrist back to its IK/animator pose.
            // The weld stays "active" (and this component enabled) until the fade finishes.
            if (left)
            {
                if (_left.active && !_left.fadingOut) { _left.fadingOut = true; _left.fadeOutStart = Time.time; }
            }
            else
            {
                if (_right.active && !_right.fadingOut) { _right.fadingOut = true; _right.fadeOutStart = Time.time; }
            }
        }

        void LateUpdate()
        {
            if (_anim == null || !_anim.isHuman) return;
            WeldSide(ref _left, true);
            WeldSide(ref _right, false);
        }

        /// <summary>
        /// Bilegin GIDECEGI poz. <see cref="WeldSide"/> bunu uygular; <see cref="AvatarIKController"/>
        /// ise IK hedefini buna kurmak icin ONCEDEN sorar.
        ///
        /// NEDEN AYRI: eskiden kol IK'si kumandadan turetilen bir hedefe cozuluyor, weld ise
        /// bilegi silaha MUTLAK yaziyordu. Iki sistem farkli yerleri isteyince ust kol/on kol
        /// bir poza, bilek baska poza gidiyor ve arada kalan deri geriliyordu — silah tutunca
        /// gorulen bozulma buydu. IK hedefi de buraya kurulunca kol zaten bilegin varacagi yere
        /// cozuluyor, weld'in duzeltecek bir seyi kalmiyor.
        /// </summary>
        public bool TryGetWristTarget(bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            ref HandWeld w = ref (left ? ref _left : ref _right);

            // Sonmekte olan weld'e IK'yi baglamayiz: fade'in VARIS noktasi zaten kumandadan
            // turetilen poz, oraya cekilmesi dogru.
            if (!w.active || w.fadingOut) return false;
            if (w.weapon == null || w.profile == null || w.bone == null) return false;

            ComputeTarget(ref w, left, out pos, out rot);
            return true;
        }

        /// <summary>
        /// Elin silah uzerinde OTURDUGU nokta (bilek offset'i uygulanmadan onceki
        /// tutamak/ray noktasi). Birinci sahis eli bunu kullanir: destek eli
        /// kundaga yapisik kalsin diye. Bilek hedefinden farkli olarak burada
        /// avatarin bilek kemigi konvansiyonu yok, dolayisiyla FP gorseli icin
        /// donus donusturmesi gerekmez.
        /// </summary>
        public bool TryGetHandAnchor(bool left, out Vector3 pos)
        {
            pos = Vector3.zero;
            ref HandWeld w = ref (left ? ref _left : ref _right);
            if (!w.active || w.fadingOut) return false;
            if (w.weapon == null || w.profile == null) return false;
            ComputeAnchor(ref w, left, out pos, out _);
            return true;
        }

        void ComputeTarget(ref HandWeld w, bool left, out Vector3 targetPos, out Quaternion targetRot)
        {
            ComputeAnchor(ref w, left, out Vector3 anchorPos, out Quaternion anchorRot);
            // Anchor on the (scaled) weapon; the wrist offset is authored in meters (hand-sized,
            // independent of the weapon's scale).
            targetPos = anchorPos + anchorRot * w.wristLocalPos;
            targetRot = anchorRot * w.wristLocalRot;
        }

        void ComputeAnchor(ref HandWeld w, bool left, out Vector3 anchorPos, out Quaternion anchorRot)
        {
            Vector3 anchorLocal;
            Quaternion anchorLocalRot = w.gripLocalRot;

            if (!w.isSupport)
            {
                anchorLocal = w.gripLocalPos;
            }
            else
            {
                // Slide along the rail: project this hand's networked carrier onto the segment,
                // in the weapon's (possibly mirrored) local space.
                Vector3 rs = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalStart) : w.profile.supportRailLocalStart;
                Vector3 re = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalEnd) : w.profile.supportRailLocalEnd;
                Vector3 s = w.weapon.TransformPoint(rs);
                Vector3 e = w.weapon.TransformPoint(re);
                Transform carrier = _ik != null ? (left ? _ik.leftHandSource : _ik.rightHandSource) : null;
                Vector3 probe = carrier != null ? carrier.position : w.bone.position;
                float t = WeaponGripMath.RailClosestT(s, e, probe);
                anchorLocal = Vector3.Lerp(rs, re, t);
            }

            anchorPos = w.weapon.TransformPoint(anchorLocal);
            anchorRot = w.weapon.rotation * anchorLocalRot;
        }

        void WeldSide(ref HandWeld w, bool left)
        {
            if (!w.active) return;
            if (w.weapon == null || w.profile == null || w.bone == null)
            {
                // Weapon despawned mid-hold/fade: nothing left to weld to.
                w.active = false;
                w.fadingOut = false;
                if (!_left.active && !_right.active) enabled = false;
                return;
            }

            ComputeTarget(ref w, left, out Vector3 targetPos, out Quaternion targetRot);

            // KOL UZAYAMAZ. Bu yazma mutlak (rig'den sonra), dolayisiyla kelepce
            // olmadan bilek silaha isinlaniyor ve el koldan kopmus gorunuyordu.
            // Kelepce yalnizca hedef kol boyunu ASTIGINDA calisir; normal tutusta
            // hicbir sey degismez, yani "destek eli silaha tam guclu kaynakli
            // kalsin" kurali korunur. ROTASYON kelepcelenmez: kol duz kalsa bile
            // el silahin/kumandanin yonune bakmaya devam eder.
            targetPos = ArmReach.Clamp(targetPos,
                left ? _leftUpper : _rightUpper,
                left ? _leftArmLen : _rightArmLen);

            // Engage/release weight. The bone's pose here is this frame's IK/animator result
            // (the weld runs after both), so a partial weight blends between that and the
            // weapon anchor — no one-frame wrist relocation on grab or release.
            float wgt;
            if (w.fadingOut)
            {
                wgt = 1f - Mathf.Clamp01((Time.time - w.fadeOutStart) / WeldBlendSeconds);
                if (wgt <= 0f)
                {
                    w.active = false;
                    w.fadingOut = false;
                    if (!_left.active && !_right.active) enabled = false; // empty tick off
                    return;
                }
            }
            else
                wgt = Mathf.Clamp01((Time.time - w.blendStart) / WeldBlendSeconds);

            if (wgt >= 1f)
            {
                w.bone.SetPositionAndRotation(targetPos, targetRot);
                return;
            }
            wgt = Mathf.SmoothStep(0f, 1f, wgt);
            w.bone.SetPositionAndRotation(
                Vector3.Lerp(w.bone.position, targetPos, wgt),
                Quaternion.Slerp(w.bone.rotation, targetRot, wgt));
        }
    }
}
