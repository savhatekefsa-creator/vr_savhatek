using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// Lets the LOCAL player grab <see cref="GrabbableObject"/>s with the GRIP button of either
    /// controller: squeeze near an object to pick it up, release to drop/throw it (hand velocity
    /// is applied, so you can toss objects). A second hand squeezing near a held weapon becomes
    /// the SUPPORT hand: the weapon aims along the line between the two hands (ready stance).
    /// Lives on the NetworkPlayer root; uses the networked hand children so what you hold
    /// matches what others see in your avatar's hand.
    /// </summary>
    [DefaultExecutionOrder(10)] // NetworkVRPlayer (0) writes the hand carriers; read them same-frame
    public class HandGrabber : NetworkBehaviour
    {
        [SerializeField] Transform leftHand;
        [SerializeField] Transform rightHand;

        [Tooltip("Grab reach around the hand, in meters.")]
        public float grabRadius = 0.3f;

        class HandState
        {
            public Transform anchor;
            public XRNode node;
            public byte index;              // 0 = left, 1 = right
            public bool prevGrip;
            public GrabbableObject held;
            public GrabbableObject supporting; // two-hand aim: this hand steadies the OTHER hand's weapon
            public WeaponGrip supportGrip;     // cached profile component of `supporting` (null = legacy)
            public WeaponGrip grip;            // cached profile component of `held` (null = legacy path)
            public Vector3 aimDir;             // filtered two-hand aim direction (zero = not engaged)
            public float requestedAt;       // when the grab RPC was sent
            public bool confirmed;          // server confirmed WE hold it
            public Vector3 posOffset;       // grab-moment offset, hand-local
            public Quaternion rotOffset;
            public readonly Queue<(Vector3 pos, float t)> trail = new Queue<(Vector3, float)>();
        }

        HandState _left, _right;

        HandState Other(HandState h) => h == _left ? _right : _left;

        /// <summary>True while that hand is holding a grabbable (used by the finger poser to
        /// firm up the grip). Non-owner instances never run grab logic, so these stay false —
        /// the finger poser falls back to the networked grip value, which is what we want.</summary>
        public bool HoldingLeft => _left != null && _left.held != null;
        public bool HoldingRight => _right != null && _right.held != null;

        /// <summary>Networked hand anchor transforms (read-only; the weapon-grip system solves
        /// the held weapon's pose from these).</summary>
        public Transform LeftAnchor => leftHand;
        public Transform RightAnchor => rightHand;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) { enabled = false; return; }
            _left = new HandState { anchor = leftHand, node = XRNode.LeftHand, index = 0 };
            _right = new HandState { anchor = rightHand, node = XRNode.RightHand, index = 1 };
        }

        void LateUpdate()
        {
            if (_left != null) UpdateHand(_left);
            if (_right != null) UpdateHand(_right);
            Reconcile();
        }

        void UpdateHand(HandState h)
        {
            if (h.anchor == null) return;

            // Velocity trail (last ~0.15 s) for throwing.
            float now = Time.time;
            h.trail.Enqueue((h.anchor.position, now));
            while (h.trail.Count > 2 && now - h.trail.Peek().t > 0.15f)
                h.trail.Dequeue();

            // Grip: prefer the boolean, but fall back to the analog axis — some OpenXR
            // interaction profiles only deliver the float. Slight hysteresis so a half
            // squeeze doesn't flicker between grab and release.
            var device = InputDevices.GetDeviceAtXRNode(h.node);
            bool grip = false;
            if (device.isValid)
            {
                device.TryGetFeatureValue(CommonUsages.gripButton, out grip);
                if (!grip && device.TryGetFeatureValue(CommonUsages.grip, out float g))
                    grip = g > (h.prevGrip ? 0.35f : 0.55f);
            }

            if (grip && !h.prevGrip) TryGrab(h);
            else if (!grip && h.prevGrip) Release(h);
            h.prevGrip = grip;

            // Two-hand support is only valid while the other hand truly holds that object.
            if (h.supporting != null && h.supporting.HolderClientId != NetworkManager.LocalClientId)
            {
                h.supporting = null;
                h.supportGrip = null;
            }

            // Profiled weapons: if this support hand strays too far from the handguard rail,
            // let go automatically — the visual hand is clamped to the rail, and past the break
            // distance the arm pose would stretch absurdly.
            if (h.supporting != null && h.supportGrip != null && h.supportGrip.Profile != null)
            {
                var p = h.supportGrip.Profile;
                Vector3 rs = p.supportRailLocalStart, re = p.supportRailLocalEnd;
                if (h.supporting.HolderHand == 0) // grip in the LEFT hand -> rail data mirrored
                {
                    rs = WeaponGripMath.MirrorX(rs);
                    re = WeaponGripMath.MirrorX(re);
                }
                Transform wt = h.supporting.transform;
                Vector3 sW = wt.TransformPoint(rs), eW = wt.TransformPoint(re);
                Vector3 closest = Vector3.Lerp(sW, eW, WeaponGripMath.RailClosestT(sW, eW, h.anchor.position));
                if (Vector3.Distance(h.anchor.position, closest) > p.supportBreakDistance)
                {
                    h.supporting = null;
                    h.supportGrip = null;
                }
            }

            // Follow. IMPORTANT: between our grab request and the server's reply (~1 RTT) the
            // object still LOOKS unheld — we must keep waiting, not give up instantly (the old
            // code dropped the local grab here every time, leaving objects ghost-held forever).
            if (h.held != null)
            {
                bool mine = h.held.HolderClientId == NetworkManager.LocalClientId
                            && h.held.HolderHand == h.index;
                if (mine && h.held.IsOwner)
                {
                    h.confirmed = true;

                    if (h.grip != null && h.grip.Profile != null)
                    {
                        // Data-driven pose: authored grip anchor + filtered two-hand aim.
                        FollowProfiled(h, h.grip.Profile);
                    }
                    else
                    {
                        // Two-handed aim: grip hand anchors the weapon, and if the OTHER hand is
                        // clamped on it, the barrel points along the line between the two hands.
                        var sup = Other(h);
                        Vector3 aim = sup != null && sup.supporting == h.held
                            ? sup.anchor.position - h.anchor.position
                            : Vector3.zero;

                        if (aim.sqrMagnitude > 0.0025f)
                            h.held.transform.SetPositionAndRotation(
                                h.anchor.TransformPoint(h.posOffset),
                                Quaternion.LookRotation(aim.normalized, h.anchor.up) * h.rotOffset);
                        else
                            h.held.transform.SetPositionAndRotation(
                                h.anchor.TransformPoint(h.posOffset),
                                h.anchor.rotation * h.rotOffset);
                    }
                }
                else if (h.confirmed && !h.held.IsHeld)
                {
                    h.held = null;             // server dropped it after we truly had it
                    h.confirmed = false;
                }
                else if (!h.confirmed && h.held.IsHeld && !mine)
                {
                    h.held = null;             // arbitration lost: someone else got it first
                }
                else if (!h.confirmed && Time.time - h.requestedAt > 1.5f)
                {
                    h.held = null;             // request lost in transit; reconcile will clean up
                }
            }
        }

        // Data-driven follow for profiled weapons: the weapon is posed so its authored grip
        // anchor sits exactly on the hand anchor (scale-safe), instead of pivot-snapping.
        // With a support hand the barrel follows the FILTERED two-hand line: rock solid inside
        // the deadzone, half-life smoothed outside, seeded from the one-hand barrel direction
        // so engaging support never pops. Roll stays 1:1 with the grip hand (up = hand up).
        void FollowProfiled(HandState h, WeaponGripProfile profile)
        {
            Vector3 gripLocal = profile.gripLocalPosition;
            Quaternion gripLocalRot = profile.GripLocalRotation;
            if (h.index == 0) // grip in the LEFT hand -> mirror the right-hand authored anchor
            {
                gripLocal = WeaponGripMath.MirrorX(gripLocal);
                gripLocalRot = WeaponGripMath.MirrorX(gripLocalRot);
            }

            var sup = Other(h);
            bool hasSupport = sup != null && sup.supporting == h.held;

            // Publish the support hand to everyone (owner-authoritative, like the transform).
            h.held.SetSupportHandOwner(hasSupport ? sup.index : GrabbableObject.NoHand);

            Quaternion weaponRot;
            if (hasSupport)
            {
                if (h.aimDir.sqrMagnitude < 1e-6f)
                    // Engage seed = the weapon's ACTUAL barrel (+Z) this frame, not anchor.forward
                    // (which differs by the grip rake) — so the first support frame never pops.
                    h.aimDir = h.held.transform.rotation * Vector3.forward;
                h.aimDir = WeaponGripMath.FilterAim(
                    h.aimDir, sup.anchor.position - h.anchor.position,
                    profile.aimDeadzoneDegrees, profile.aimSoftKneeDegrees,
                    profile.aimHalfLifeMs, Time.deltaTime);
                weaponRot = Quaternion.LookRotation(h.aimDir, h.anchor.up);
            }
            else
            {
                h.aimDir = Vector3.zero; // next engage re-seeds
                weaponRot = h.anchor.rotation * Quaternion.Inverse(gripLocalRot);
            }

            Vector3 weaponPos = h.anchor.position
                - weaponRot * Vector3.Scale(h.held.transform.lossyScale, gripLocal);
            h.held.transform.SetPositionAndRotation(weaponPos, weaponRot);
        }

        // Self-heal: if the server thinks WE hold something that neither hand is actually
        // holding (lost reply, gave-up request that later won arbitration...), release it so
        // it never stays ghost-held and ungrabbable.
        float _nextReconcile;
        void Reconcile()
        {
            if (Time.time < _nextReconcile) return;
            _nextReconcile = Time.time + 1f;
            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                if (g.HolderClientId != NetworkManager.LocalClientId) continue;
                if ((_left != null && g == _left.held) || (_right != null && g == _right.held)) continue;
                g.ReleaseServerRpc();
            }
        }

        void TryGrab(HandState h)
        {
            if (h.held != null || h.supporting != null) return;

            // If my OTHER hand already holds a snap-style weapon and this hand squeezes near
            // it, this hand becomes the SUPPORT hand (two-handed ready stance) instead of
            // trying to grab something else. Slightly longer reach: the handguard is long.
            var o = Other(h);
            if (o != null && o.held != null && o.confirmed && o.held.snapToHand)
            {
                var wc = o.held.GetComponentInChildren<Collider>();
                if (wc != null &&
                    Vector3.Distance(h.anchor.position, wc.ClosestPoint(h.anchor.position)) < grabRadius * 1.5f)
                {
                    h.supporting = o.held;
                    h.supportGrip = o.grip; // null for legacy weapons — rail logic then stays off
                    return;
                }
            }

            GrabbableObject best = null;
            float bestDist = float.MaxValue;
            // AllLayers: the default mask skips the "Ignore Raycast" layer, which would make
            // objects on that layer silently ungrabbable.
            foreach (var col in Physics.OverlapSphere(h.anchor.position, grabRadius,
                         Physics.AllLayers, QueryTriggerInteraction.Collide))
            {
                var g = col.GetComponentInParent<GrabbableObject>();
                if (g == null || g.IsHeld) continue;
                float d = Vector3.Distance(h.anchor.position, col.ClosestPoint(h.anchor.position));
                if (d < bestDist) { bestDist = d; best = g; }
            }
            if (best == null) return;

            h.held = best;
            h.requestedAt = Time.time;
            h.confirmed = false;
            // Grab-moment lookup only (the binder attached WeaponGrip at spawn); a weapon
            // without a profile keeps the legacy pivot-snap path bit-for-bit.
            h.grip = best.GetComponent<WeaponGrip>();
            if (h.grip != null && h.grip.Profile == null) h.grip = null;
            h.aimDir = Vector3.zero;
            if (best.snapToHand)
            {
                // Pull it into the palm, barrel aligned with where the controller points.
                h.posOffset = Vector3.zero;
                h.rotOffset = SnapRotOffset(best);
            }
            else
            {
                // Keep the grab-moment pose (natural for rocks and props).
                h.posOffset = h.anchor.InverseTransformPoint(best.transform.position);
                h.rotOffset = Quaternion.Inverse(h.anchor.rotation) * best.transform.rotation;
            }
            best.RequestGrabServerRpc(h.index);
        }

        void Release(HandState h)
        {
            // Support hand lets go -> back to one-handed carry, weapon stays in the grip hand.
            if (h.supporting != null)
            {
                h.supporting = null;
                h.supportGrip = null;
                return;
            }

            if (h.held == null) return;

            var g = h.held;
            bool profiled = h.grip != null;
            h.held = null;
            h.grip = null;
            h.aimDir = Vector3.zero;
            var o = Other(h);
            if (o != null && o.supporting == g)
            {
                o.supporting = null; // dropped: support ends too
                o.supportGrip = null;
            }
            if (g.HolderClientId != NetworkManager.LocalClientId) return;

            if (profiled)
                g.SetSupportHandOwner(GrabbableObject.NoHand); // clear while we still own it

            g.ApplyThrow(HandVelocity(h), Vector3.zero);
            g.ReleaseServerRpc();
        }

        // How the object should sit in the hand: use the manual override if given, otherwise
        // find the mesh's LONGEST local axis (a rifle's barrel line), pick the sign that points
        // toward the bulk of the mesh (the muzzle side), and map that axis onto hand-forward.
        static Quaternion SnapRotOffset(GrabbableObject g)
        {
            if (g.gripRotationEuler != Vector3.zero)
                return Quaternion.Euler(g.gripRotationEuler);

            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest == null) return Quaternion.identity;

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
            Vector3 axis = Vector3.right;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) axis = Vector3.forward;

            // Muzzle side = where the mesh's bulk lies relative to the pivot (the grip).
            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            if (sign == 0f) sign = 1f;

            // Into root space, then compute the rotation that maps it onto +Z (hand forward).
            Quaternion childToRoot = Quaternion.Inverse(g.transform.rotation) * biggest.transform.rotation;
            Vector3 axisRoot = childToRoot * (axis * sign);
            return Quaternion.FromToRotation(axisRoot, Vector3.forward);
        }

        Vector3 HandVelocity(HandState h)
        {
            if (h.trail.Count < 2) return Vector3.zero;
            var oldest = h.trail.Peek();
            var newest = h.anchor.position;
            float dt = Time.time - oldest.t;
            if (dt < 0.02f) return Vector3.zero;
            Vector3 v = (newest - oldest.pos) / dt;
            return Vector3.ClampMagnitude(v, 12f);
        }
    }
}
