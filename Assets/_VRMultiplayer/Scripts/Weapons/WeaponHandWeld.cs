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
        }

        HandWeld _left, _right;
        Animator _anim;
        AvatarIKController _ik;
        Transform _leftBone, _rightBone;

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _ik = GetComponent<AvatarIKController>();
            if (_anim != null && _anim.isHuman)
            {
                _leftBone = _anim.GetBoneTransform(HumanBodyBones.LeftHand);
                _rightBone = _anim.GetBoneTransform(HumanBodyBones.RightHand);
            }
        }

        /// <summary>Weld one hand onto the weapon (store-only; applied every LateUpdate).</summary>
        public void SetHand(bool left, Transform weapon, WeaponGripProfile profile,
            bool isSupport, bool mirrored)
        {
            var pose = isSupport ? profile.supportHand : profile.mainHand;
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
            };
            if (left) _left = w; else _right = w;
            enabled = true;
        }

        public void ClearHand(bool left)
        {
            if (left) _left.active = false; else _right.active = false;
            if (!_left.active && !_right.active) enabled = false; // stop the empty per-frame tick
        }

        void LateUpdate()
        {
            if (_anim == null || !_anim.isHuman) return;
            WeldSide(ref _left, true);
            WeldSide(ref _right, false);
        }

        void WeldSide(ref HandWeld w, bool left)
        {
            if (!w.active || w.weapon == null || w.profile == null || w.bone == null) return;

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

            // Anchor on the (scaled) weapon; the wrist offset is authored in meters (hand-sized,
            // independent of the weapon's scale).
            Vector3 anchorPos = w.weapon.TransformPoint(anchorLocal);
            Quaternion anchorRot = w.weapon.rotation * anchorLocalRot;
            w.bone.SetPositionAndRotation(
                anchorPos + anchorRot * w.wristLocalPos,
                anchorRot * w.wristLocalRot);
        }
    }
}
