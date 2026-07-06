using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Drives this transform's LOCAL pose from a tracked XR device node (head or a hand),
    /// using the built-in UnityEngine.XR input subsystem. This works with the OpenXR
    /// plugin on Meta Quest 3 and has no dependency on XR Interaction Toolkit, so it stays
    /// stable across package versions.
    ///
    /// Place the object carrying this component as a direct child of the XR rig root, so the
    /// device-space pose maps to the correct local pose.
    /// </summary>
    public class XRDevicePoseDriver : MonoBehaviour
    {
        [Tooltip("Which tracked device drives this transform: Head, LeftHand or RightHand.")]
        public XRNode node = XRNode.Head;

        public bool trackPosition = true;
        public bool trackRotation = true;

        void Update()
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid)
                return;

            if (trackPosition && device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                transform.localPosition = pos;

            if (trackRotation && device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
                transform.localRotation = rot;
        }
    }
}
