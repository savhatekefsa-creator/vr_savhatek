using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Sets the XR tracking origin to Floor level so the player's real-world height maps
    /// correctly into the scene (head is at standing height above the rig root). Retries for
    /// a few frames because the input subsystem may not be ready on the very first frame.
    /// </summary>
    public class XRTrackingOriginSetup : MonoBehaviour
    {
        [Tooltip("Give up after this many frames where a subsystem EXISTS but refused Floor mode. Frames spent waiting for the subsystem to come up do NOT count, so a slow platform boot never exhausts this. Prevents a permanent per-frame retry if Floor is genuinely unsupported.")]
        public int maxAttempts = 600;

        // Cached so the per-frame poll doesn't allocate a new List (GC pressure) every frame.
        readonly List<XRInputSubsystem> _subsystems = new List<XRInputSubsystem>();
        int _attempts;

        void Update()
        {
            SubsystemManager.GetSubsystems(_subsystems);
            if (_subsystems.Count == 0)
                return; // subsystem not up yet -- keep waiting, don't burn attempts

            foreach (var s in _subsystems)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                {
                    enabled = false;
                    return;
                }
            }

            if (++_attempts >= maxAttempts)
                enabled = false; // platform keeps refusing Floor; stop retrying every frame
        }
    }
}
