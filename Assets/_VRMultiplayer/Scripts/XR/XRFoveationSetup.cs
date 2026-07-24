using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Turns on Fixed Foveated Rendering (FFR) on Quest by raising the display subsystem's
    /// foveated-rendering level once the subsystem is running. FFR shades the periphery at a
    /// lower resolution -- nearly imperceptible in the headset and worth ~10-20% GPU headroom,
    /// which also steadies the frame rate (and therefore perceived jitter).
    ///
    /// Requires the OpenXR "Foveated Rendering" feature enabled for the Android target; without
    /// it this is a harmless no-op. Fixed (not eye-tracked): foveatedRenderingFlags is left at
    /// its default so no gaze data is used.
    ///
    /// Self-bootstrapping (no scene/prefab wiring): a hidden host object is created after the
    /// first scene loads, so the shared scene file stays untouched.
    /// </summary>
    public class XRFoveationSetup : MonoBehaviour
    {
        [Range(0f, 1f)]
        [Tooltip("0 = off, 1 = strongest periphery reduction (the usual FFR setting on Quest).")]
        public float foveationLevel = 1f;

        [Tooltip("Stop trying after this many frames where a display subsystem exists but is not running yet.")]
        public int maxAttempts = 600;

        readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();
        int _attempts;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("[XRFoveationSetup]");
            go.AddComponent<XRFoveationSetup>();
            DontDestroyOnLoad(go);
        }

        void Update()
        {
            SubsystemManager.GetSubsystems(_displays);
            if (_displays.Count == 0)
                return; // display subsystem not up yet -- keep waiting, don't burn attempts

            bool applied = false;
            foreach (var d in _displays)
            {
                if (!d.running)
                    continue;
                d.foveatedRenderingLevel = Mathf.Clamp01(foveationLevel);
                applied = true;
            }

            if (applied || ++_attempts >= maxAttempts)
                enabled = false; // the level persists once set
        }
    }
}
