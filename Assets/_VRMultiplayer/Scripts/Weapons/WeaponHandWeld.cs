using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Welds an avatar's wrist bone(s) to a held weapon's grip anchor / support rail, in world
    /// space, AFTER the rig evaluation and the finger poser (execution order 110). Because the
    /// write is absolute, the avatar's height-fit scaling can't drift the hand, and the hand
    /// follows the weapon's interpolated transform — fingers never separate from the grip. Added
    /// to the avatar at runtime by <see cref="WeaponGrip"/>; the NetworkPlayer prefab is never
    /// edited.
    ///
    /// Support hand: the anchor is the closest point on the profile's rail segment to this
    /// hand's networked carrier — owners and remote clients project the same replicated
    /// position, so everyone sees the hand at the same spot on the handguard. NOT: profillerde
    /// ray su an bilerek TEK NOKTA (start == end, izdusum hep o nokta) — cihazda kayan el
    /// "bozuk konum" hissi verdi; kod yolu ileride gerekirse diye duruyor.
    ///
    /// Poses are authored for main=RIGHT / support=LEFT; when the roles are swapped the local
    /// data is mirrored across the weapon's YZ plane. Static (per-hold) values are resolved once
    /// in <see cref="SetHand"/>; only weapon-relative transforms are recomputed per frame.
    ///
    /// THE WELD IS NOT ALONE. An absolute wrist write on its own drags the wrist to the weapon
    /// while the arm IK still solves toward the controller — the elbow stays bent and the wrist
    /// visibly leaves the forearm. So <see cref="Solve"/> is public through
    /// <see cref="TryGetWeld"/>: <see cref="AvatarIKController"/> (execution order 70, i.e. after
    /// HandGrabber and WeaponRecoil have settled the weapon's pose, and before this) aims the
    /// arm's IK target at the very same pose, at the very same weight. One target, one weight,
    /// two writers that agree — and the per-frame cache guarantees they see identical numbers.
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

            // Per-frame solve, cached by frame number. The IK controller asks for it BEFORE this
            // component's LateUpdate; caching guarantees both readers use identical values even
            // though Time.time and the weapon transform are read at two different moments.
            public int solvedFrame;
            public Vector3 solvedPos;
            public Quaternion solvedRot;
            public float solvedWeight;
            public float solvedDivergence;
        }

        // The weld writes the wrist ABSOLUTELY, so switching it on/off used to relocate the
        // hand in a single frame (the "el kayboluyor/geri geliyor" pop, on every client).
        // Ramping the weld weight over this window blends the wrist between the IK pose and
        // the weapon anchor on both engage and release.
        const float WeldBlendSeconds = 0.12f;

        // SAPMA EMNIYETI DENENDI VE GERI ALINDI (2026-07-27, cihaz karari).
        //
        // Bir donem destek kaynagi, oyuncunun gercek eli hedeften uzaklastikca zayiflayip
        // ~22 cm'de tamamen birakiyordu ("bilek kolu terk etmesin" diye) ve profillerdeki ray
        // gercek bir dogru parcasiydi (el kundak boyunca kayiyordu). Cihazda ikisinin toplami
        // "destek eli silahtan kopuyor + konumu bozuk" olarak hissedildi. Karar: destek eli
        // yakalanan noktaya TAM guclu ve SABIT kaynaklanir; profillerde ray tek noktaya
        // dondurulda (start == end -> izdusum o noktayi verir). Sapma hala OLCULUR ama yalnizca
        // kulaklik-ustu gosterge (AvatarFitDebug) icindir — agirliga ASLA karistirma.

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
            // Hand the IK controller a direct reference: it needs the weld target every frame
            // and the weld appears mid-session (first grab), so a per-frame GetComponent would
            // be the alternative.
            if (_ik != null) _ik.RegisterWeld(this);
        }

        void OnDestroy()
        {
            if (_ik != null) _ik.UnregisterWeld(this);
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

        /// <summary>
        /// Where this hand's wrist should sit on the weapon THIS frame, and how strongly it
        /// should be held there (0 = leave it on the controller, 1 = fully on the weapon).
        /// False when the hand isn't welded. Read by <see cref="AvatarIKController"/> so the ARM
        /// solves toward the same pose the weld is about to write.
        /// </summary>
        public bool TryGetWeld(bool left, out Vector3 pos, out Quaternion rot, out float weight)
        {
            if (left) return Solve(ref _left, true, out pos, out rot, out weight);
            return Solve(ref _right, false, out pos, out rot, out weight);
        }

        /// <summary>Diagnostics for the on-headset overlay: how far the weld target has been
        /// dragged from where this hand's controller actually is, and the resulting weight.</summary>
        public bool TryGetDebug(bool left, out float divergence, out float weight, out bool isSupport)
        {
            var w = left ? _left : _right;
            divergence = w.solvedDivergence;
            weight = w.solvedWeight;
            isSupport = w.isSupport;
            return w.active;
        }

        void LateUpdate()
        {
            if (_anim == null || !_anim.isHuman) return;
            WeldSide(ref _left, true);
            WeldSide(ref _right, false);
        }

        // Pure solve (apart from the frame cache): never flips `active`, so the IK controller can
        // call it at order 50 without racing the lifetime bookkeeping WeldSide owns.
        bool Solve(ref HandWeld w, bool left, out Vector3 pos, out Quaternion rot, out float weight)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            weight = 0f;
            if (!w.active || w.weapon == null || w.profile == null || w.bone == null) return false;

            if (w.solvedFrame == Time.frameCount)
            {
                pos = w.solvedPos;
                rot = w.solvedRot;
                weight = w.solvedWeight;
                return true;
            }

            Vector3 anchorLocal;
            Quaternion anchorLocalRot = w.gripLocalRot;

            if (!w.isSupport)
            {
                anchorLocal = w.gripLocalPos;
            }
            else
            {
                // Slide along the rail: project this hand's networked carrier onto the segment,
                // in the weapon's (possibly mirrored) local space. A profile whose rail start and
                // end are the same point has no slide at all — see Tools > VR Multiplayer > 43/44.
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
            pos = anchorPos + anchorRot * w.wristLocalPos;
            rot = anchorRot * w.wristLocalRot;

            // Engage/release ramp — smoothed here (not at the write site) so the IK controller
            // and the weld blend on exactly the same curve.
            float raw = w.fadingOut
                ? 1f - Mathf.Clamp01((Time.time - w.fadeOutStart) / WeldBlendSeconds)
                : Mathf.Clamp01((Time.time - w.blendStart) / WeldBlendSeconds);
            weight = Mathf.SmoothStep(0f, 1f, raw);

            // Yalnizca teshis: kaynak hedefi gercek kontrolcuden ne kadar suruklemis?
            // (AvatarFitDebug gosterir.) Agirliga bilerek KARISMAZ — bkz. ustteki karar notu.
            float divergence = 0f;
            if (w.isSupport && _ik != null && _ik.TryGetFreeHandTargetPos(left, out Vector3 free))
                divergence = Vector3.Distance(pos, free);

            w.solvedFrame = Time.frameCount;
            w.solvedPos = pos;
            w.solvedRot = rot;
            w.solvedWeight = weight;
            w.solvedDivergence = divergence;
            return true;
        }

        void WeldSide(ref HandWeld w, bool left)
        {
            if (!w.active) return;

            // Weapon despawned mid-hold/fade: nothing left to weld to.
            if (w.weapon == null || w.profile == null || w.bone == null) { Deactivate(ref w); return; }

            // End of the release ramp. Judged on the RAMP, never on the final weight: the
            // divergence fade can legitimately sit at zero on a live hold, and tearing the weld
            // down there would drop the grip the moment the player stretched too far.
            if (w.fadingOut && Time.time - w.fadeOutStart >= WeldBlendSeconds) { Deactivate(ref w); return; }

            if (!Solve(ref w, left, out Vector3 targetPos, out Quaternion targetRot, out float wgt)) return;
            if (wgt <= 0f) return;

            // The bone's pose here is this frame's IK/animator result (the weld runs after both),
            // so a partial weight blends between that and the weapon anchor — no one-frame wrist
            // relocation on grab or release.
            if (wgt >= 1f)
            {
                w.bone.SetPositionAndRotation(targetPos, targetRot);
                return;
            }
            w.bone.SetPositionAndRotation(
                Vector3.Lerp(w.bone.position, targetPos, wgt),
                Quaternion.Slerp(w.bone.rotation, targetRot, wgt));
        }

        void Deactivate(ref HandWeld w)
        {
            w.active = false;
            w.fadingOut = false;
            w.solvedFrame = 0;
            w.solvedWeight = 0f;
            w.solvedDivergence = 0f;
            if (!_left.active && !_right.active) enabled = false; // empty tick off
        }
    }
}
