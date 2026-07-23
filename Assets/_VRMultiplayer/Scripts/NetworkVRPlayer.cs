using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Lives on the networked player avatar (root with NetworkObject). On the OWNING client it
    /// copies the local XR rig's head + hand poses onto this prefab's networked child transforms
    /// every frame. Owner-authoritative <see cref="ClientNetworkTransform"/> components on those
    /// children replicate the motion to everyone; remote clients see it interpolated.
    ///
    /// Visibility model:
    ///  - Everyone — including you — sees the full humanoid body (first-person embodiment);
    ///    only your own head is hidden so the camera isn't inside it.
    ///  - Others see your full humanoid avatar (driven by <see cref="AvatarIKController"/>).
    /// </summary>
    // Must write the pose carriers BEFORE AvatarIKController (order 0) reads them in LateUpdate;
    // with both at the default order Unity gives no guarantee, and losing the race adds a
    // one-frame head/hand mismatch that reads as hand jitter.
    [DefaultExecutionOrder(-100)]
    public class NetworkVRPlayer : NetworkBehaviour
    {
        [Header("Networked pose carriers (this prefab's own children)")]
        [SerializeField] Transform head;
        [SerializeField] Transform leftHand;
        [SerializeField] Transform rightHand;

        [Header("Visibility")]
        [Tooltip("Renderers OFF for the local owner, ON for others (e.g. head when there is no humanoid).")]
        [SerializeField] Renderer[] ownerHiddenRenderers;
        [Tooltip("Renderers ON only for the local owner (e.g. the simple hand cubes).")]
        [SerializeField] Renderer[] ownerOnlyRenderers;
        [Tooltip("Renderers OFF for everyone (pose-carrier meshes hidden once a humanoid exists).")]
        [SerializeField] Renderer[] alwaysHiddenRenderers;
        [Tooltip("The humanoid avatar — shown to everyone, including you (first-person body).")]
        [SerializeField] GameObject remoteAvatar;

        [Header("Spawn")]
        [SerializeField] float spawnRingRadius = 0.9f;

        // Hand analog inputs, owner-written and replicated to everyone, so ProceduralFingerPoser
        // can curl each player's fingers on ALL clients. byte = 0..255 quantized grip/trigger;
        // one byte each, cheap. Owner writes directly (no RPC — the player owns this object).
        readonly NetworkVariable<byte> _leftGrip = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _rightGrip = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _leftTrig = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _rightTrig = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public float LeftGrip01 => _leftGrip.Value / 255f;
        public float RightGrip01 => _rightGrip.Value / 255f;
        public float LeftTrigger01 => _leftTrig.Value / 255f;
        public float RightTrigger01 => _rightTrig.Value / 255f;

        Transform _srcHead, _srcLeft, _srcRight;
        bool _bound;

        public override void OnNetworkSpawn()
        {
            ApplyVisibility();

            if (!IsOwner)
                return;

            var rig = XRRigReference.Instance;
            if (rig == null)
            {
                Debug.LogWarning("[NetworkVRPlayer] No XRRigReference in scene — avatar will not follow the headset.");
                return;
            }

            _srcHead = rig.head;
            _srcLeft = rig.leftHand;
            _srcRight = rig.rightHand;
            _bound = _srcHead != null && head != null;

            SpreadSpawn(rig);
        }

        void ApplyVisibility()
        {
            // The primitive Head/Hand carriers are invisible pose sources — hide their meshes.
            HideRenderers(head);
            HideRenderers(leftHand);
            HideRenderers(rightHand);

            // Everyone — including you — sees the full humanoid (first-person embodiment).
            if (remoteAvatar != null)
            {
                remoteAvatar.SetActive(true);

                // IK can pull the hands far outside each piece's authored bounds (arms raised
                // overhead). With stale bounds Unity frustum-culls the piece while it is plainly
                // on screen — hands vanish when lifted. Live bounds keep them visible.
                foreach (var r in remoteAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    r.updateWhenOffscreen = true;

                if (IsOwner)
                {
                    // FIRST-PERSON BODY: prefer the dedicated FP_Hands model (capped wrists,
                    // weights locked to the hand bones — no cuff stretching). When it attaches,
                    // hide EVERY avatar piece incl. glove|watch and show only the FP renderers.
                    // If the asset is missing the old glove|watch path below still works.
                    var fpHands = FirstPersonHands.TryAttach(remoteAvatar);

                    // You see only your own hands — no head in the camera, no torso/legs
                    // blocking the view. Remote players still see the full soldier. Models
                    // without a glove piece fall back to hiding just the head/eye/neck
                    // renderers ("nec" also catches this model's "Necck" typo).
                    var renderers = remoteAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    bool hasHands = false;
                    foreach (var r in renderers)
                        if (r.name.ToLowerInvariant().Contains("glove")) { hasHands = true; break; }

                    bool hidAny = false;
                    foreach (var r in renderers)
                    {
                        if (fpHands != null)
                        {
                            r.enabled = r.transform.IsChildOf(fpHands.transform);
                            hidAny = true;
                            continue;
                        }

                        string rn = r.name.ToLowerInvariant();
                        bool hide = hasHands
                            ? !(rn.Contains("glove") || rn.Contains("watch"))
                            : rn.Contains("head") || rn.Contains("eye") || rn.Contains("nec");
                        if (hide) { r.enabled = false; hidAny = true; continue; }

                        if (hasHands)
                        {
                            // The cuff is an open mesh; single-sided it reads as a hole when you
                            // look into it. Draw both faces so the glove looks solid.
                            MaterialDoubleSided.Apply(r);
                        }
                    }

                    if (!hasHands && !hidAny)
                    {
                        var ik = remoteAvatar.GetComponentInChildren<AvatarIKController>();
                        if (ik != null) ik.hideHead = true; // no separate head mesh — bone trick
                    }

                    // Don't show your own floating name tag either.
                    foreach (var tm in remoteAvatar.GetComponentsInChildren<TextMesh>(true))
                    {
                        var r = tm.GetComponent<MeshRenderer>();
                        if (r != null) r.enabled = false;
                    }
                }
            }
        }

        static void HideRenderers(Transform t)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
                r.enabled = false;
        }

        void SpreadSpawn(XRRigReference rig)
        {
            float angle = (OwnerClientId % 8) * 45f;
            Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * spawnRingRadius;
            rig.transform.position += offset;
        }

        void LateUpdate()
        {
            if (!_bound)
                return;

            head.SetPositionAndRotation(_srcHead.position, _srcHead.rotation);
            if (_srcLeft != null && leftHand != null)
                leftHand.SetPositionAndRotation(_srcLeft.position, _srcLeft.rotation);
            if (_srcRight != null && rightHand != null)
                rightHand.SetPositionAndRotation(_srcRight.position, _srcRight.rotation);

            // Publish the analog grip/trigger for both hands so everyone's finger poser matches.
            WriteInput(XRNode.LeftHand, _leftGrip, _leftTrig);
            WriteInput(XRNode.RightHand, _rightGrip, _rightTrig);
        }

        static void WriteInput(XRNode node, NetworkVariable<byte> grip, NetworkVariable<byte> trig)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return;

            dev.TryGetFeatureValue(CommonUsages.grip, out float g);
            if (g <= 0f && dev.TryGetFeatureValue(CommonUsages.gripButton, out bool gb) && gb) g = 1f;
            dev.TryGetFeatureValue(CommonUsages.trigger, out float t);
            if (t <= 0f && dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool tb) && tb) t = 1f;

            byte gb2 = (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f);
            byte tb2 = (byte)Mathf.RoundToInt(Mathf.Clamp01(t) * 255f);
            // Deadband: only replicate when it actually moves, to avoid per-frame dirtying.
            if (Mathf.Abs(gb2 - grip.Value) > 3) grip.Value = gb2;
            if (Mathf.Abs(tb2 - trig.Value) > 3) trig.Value = tb2;
        }
    }
}
