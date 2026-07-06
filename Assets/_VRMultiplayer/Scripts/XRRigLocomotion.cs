using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Left-thumbstick smooth locomotion + right-thumbstick snap turn, applied to the XR rig
    /// root. Reads the controller thumbsticks through the built-in UnityEngine.XR input
    /// subsystem (OpenXR compatible, no XR Interaction Toolkit dependency).
    ///
    /// Moving the rig moves the local camera, and because the networked avatar mirrors the
    /// camera, other players see this player walk around the shared world.
    /// </summary>
    public class XRRigLocomotion : MonoBehaviour
    {
        [Tooltip("Head transform. Movement is relative to where the player is looking.")]
        public Transform head;

        [Header("Move")]
        public float moveSpeed = 2.5f;
        [Range(0f, 0.9f)] public float deadzone = 0.15f;

        [Header("Snap Turn")]
        public float snapTurnDegrees = 45f;
        [Range(0.5f, 0.95f)] public float snapActivation = 0.7f;

        bool _snapReady = true;

        void Update()
        {
            HandleMove();
            HandleSnapTurn();
        }

        void HandleMove()
        {
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!left.isValid) return;
            if (!left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis)) return;
            if (axis.magnitude < deadzone) return;

            Transform dir = head != null ? head : transform;
            Vector3 forward = dir.forward;
            Vector3 right = dir.right;
            forward.y = 0f; right.y = 0f;
            forward.Normalize(); right.Normalize();

            Vector3 move = (forward * axis.y + right * axis.x) * (moveSpeed * Time.deltaTime);
            transform.position += move;
        }

        void HandleSnapTurn()
        {
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!right.isValid) return;
            if (!right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis)) return;

            if (Mathf.Abs(axis.x) < snapActivation)
            {
                _snapReady = true;
                return;
            }
            if (!_snapReady) return;

            float sign = Mathf.Sign(axis.x);
            Vector3 pivot = head != null ? head.position : transform.position;
            transform.RotateAround(pivot, Vector3.up, sign * snapTurnDegrees);
            _snapReady = false;
        }
    }
}
