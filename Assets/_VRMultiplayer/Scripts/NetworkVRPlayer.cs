using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Lives on the networked player avatar (root with NetworkObject). On the OWNING client it
    /// copies the local XR rig's head + hand poses onto this prefab's networked child transforms
    /// every frame. Owner-authoritative <see cref="ClientNetworkTransform"/> components on those
    /// children replicate the motion to everyone; remote clients see it interpolated.
    ///
    /// Visibility model:
    ///  - You (owner) see simple hand cubes; you do NOT see your own humanoid body/head.
    ///  - Others see your full humanoid avatar (driven by <see cref="AvatarIKController"/>).
    /// </summary>
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
                if (IsOwner)
                {
                    var ik = remoteAvatar.GetComponentInChildren<AvatarIKController>();
                    if (ik != null) ik.hideHead = true; // don't show your own head from inside

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
        }
    }
}
