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

        /// <summary>Yerel oyuncunun kafasi; VR rig yoksa (Editor'de Game penceresinden test)
        /// ana kamera kafa yerine gecer. Null donebilir (ne rig ne kamera var). HUD/vignette/
        /// saat gibi tuketicilerin her birinde ayni fallback kopyaliydi — tek kaynak burasi.</summary>
        public static Transform HeadOrCamera
        {
            get
            {
                var rig = Instance;
                if (rig != null && rig.head != null) return rig.head;
                var cam = Camera.main;
                return cam != null ? cam.transform : null;
            }
        }

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
