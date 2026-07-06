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
        int _attempts = 30;

        void Update()
        {
            if (_attempts <= 0)
            {
                enabled = false;
                return;
            }

            _attempts--;

            var subsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            if (subsystems.Count == 0)
                return;

            foreach (var s in subsystems)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                {
                    enabled = false;
                    return;
                }
            }
        }
    }
}
