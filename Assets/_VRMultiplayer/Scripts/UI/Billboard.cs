using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Makes a world-space label always face the local player's camera so the name tag stays
    /// readable. Runs locally on every client (not networked).
    ///
    /// If the text appears mirrored/back-to-front on device, toggle <see cref="flip"/>.
    /// </summary>
    public class Billboard : MonoBehaviour
    {
        public bool flip;

        Camera _cam;

        void LateUpdate()
        {
            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null)
                return;

            Vector3 fwd = _cam.transform.forward;
            transform.forward = flip ? -fwd : fwd;
        }
    }
}
