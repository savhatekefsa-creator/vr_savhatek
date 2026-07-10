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
    /// data is mirrored across the weapon's YZ plane.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class WeaponHandWeld : MonoBehaviour
    {
        class HandWeld
        {
            public Transform weapon;
            public WeaponGripProfile profile;
            public bool isSupport;
            public bool mirrored;
        }

        HandWeld _left, _right;
        Animator _anim;
        AvatarIKController _ik;

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _ik = GetComponent<AvatarIKController>();
        }

        /// <summary>Weld one hand onto the weapon (store-only; applied every LateUpdate).</summary>
        public void SetHand(bool left, Transform weapon, WeaponGripProfile profile,
            bool isSupport, bool mirrored)
        {
            var w = new HandWeld { weapon = weapon, profile = profile, isSupport = isSupport, mirrored = mirrored };
            if (left) _left = w; else _right = w;
        }

        public void ClearHand(bool left)
        {
            if (left) _left = null; else _right = null;
        }

        void LateUpdate()
        {
            if (_anim == null || !_anim.isHuman) return;
            WeldSide(_left, true);
            WeldSide(_right, false);
        }

        void WeldSide(HandWeld w, bool left)
        {
            if (w == null || w.weapon == null || w.profile == null) return;
            var bone = _anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (bone == null) return;

            Vector3 anchorLocal;
            Quaternion anchorLocalRot = w.profile.GripLocalRotation;
            WeaponGripProfile.HandPose pose;

            if (!w.isSupport)
            {
                anchorLocal = w.profile.gripLocalPosition;
                pose = w.profile.mainHand;
            }
            else
            {
                // Slide along the rail: project this hand's networked carrier onto the segment.
                Vector3 s = w.weapon.TransformPoint(w.profile.supportRailLocalStart);
                Vector3 e = w.weapon.TransformPoint(w.profile.supportRailLocalEnd);
                Transform carrier = _ik != null ? (left ? _ik.leftHandSource : _ik.rightHandSource) : null;
                Vector3 probe = carrier != null ? carrier.position : bone.position;
                float t = WeaponGripMath.RailClosestT(s, e, probe);
                anchorLocal = Vector3.Lerp(w.profile.supportRailLocalStart, w.profile.supportRailLocalEnd, t);
                pose = w.profile.supportHand;
            }

            Vector3 wristLocalPos = pose.wristLocalPosition;
            Quaternion wristLocalRot = Quaternion.Euler(pose.wristLocalEuler);

            if (w.mirrored)
            {
                anchorLocal = WeaponGripMath.MirrorX(anchorLocal);
                anchorLocalRot = WeaponGripMath.MirrorX(anchorLocalRot);
                wristLocalPos = WeaponGripMath.MirrorX(wristLocalPos);
                wristLocalRot = WeaponGripMath.MirrorX(wristLocalRot);
            }

            // Anchor on the (scaled) weapon; the wrist offset is authored in meters (hand-sized,
            // independent of the weapon's scale).
            Vector3 anchorPos = w.weapon.TransformPoint(anchorLocal);
            Quaternion anchorRot = w.weapon.rotation * anchorLocalRot;
            bone.SetPositionAndRotation(
                anchorPos + anchorRot * wristLocalPos,
                anchorRot * wristLocalRot);
        }
    }
}
