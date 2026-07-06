using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Marks the single LOCAL (non-networked) XR rig in the scene and exposes its head and
    /// hand transforms. The networked avatar (<see cref="NetworkVRPlayer"/>) looks this up on
    /// the owning client to copy real tracking data into the networked head/hands.
    /// </summary>
    public class XRRigReference : MonoBehaviour
    {
        public static XRRigReference Instance { get; private set; }

        [Tooltip("The camera transform (player head).")]
        public Transform head;
        public Transform leftHand;
        public Transform rightHand;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
