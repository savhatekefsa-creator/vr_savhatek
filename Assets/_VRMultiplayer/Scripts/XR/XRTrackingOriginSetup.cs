using System.Collections.Generic;
using Unity.XR.CoreUtils;
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

        /// <summary>Floor modu verildi mi? null = henuz karar verilmedi. Gozlukte log okunamadigi
        /// icin <see cref="CalibrationAnchor"/> bunu VR panelinde gosterir.</summary>
        public static bool? FloorGranted { get; private set; }

        /// <summary>Gerceklesen tracking origin modunun adi (teshis icin).</summary>
        public static string OriginMode { get; private set; } = "?";

        void Update()
        {
            SubsystemManager.GetSubsystems(_subsystems);
            if (_subsystems.Count == 0)
                return; // subsystem not up yet -- keep waiting, don't burn attempts

            foreach (var s in _subsystems)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                {
                    LogOriginState(s, true);
                    enabled = false;
                    return;
                }
            }

            if (++_attempts >= maxAttempts)
            {
                LogOriginState(_subsystems[0], false);
                enabled = false; // platform keeps refusing Floor; stop retrying every frame
            }
        }

        /// <summary>
        /// Dikey referansin gercekten kurulup kurulmadigini KAYDA GECIRIR. Sebep: "oyuncu zeminden
        /// yukarida/asagida doguyor" sikayetinin iki farkli koku var ve ayirt edilmeleri sart —
        ///   1) SLAM'in dikey origin'i zamanla kaydi  -> DRIFT, CalibrationAnchor bunu duzeltir
        ///   2) Floor modu hic verilmedi              -> KURULUM HATASI, anchor bunu maskeler ama COZMEZ
        /// Floor reddedilirse XROrigin rig'i CameraYOffset kadar (~1.12 m) yukari iter; bu sabit
        /// hatayi drift saniip anchor pesinde kosmak zaman kaybidir.
        /// </summary>
        static void LogOriginState(XRInputSubsystem s, bool floorGranted)
        {
            var origin = FindFirstObjectByType<XROrigin>();
            string offset = origin != null ? origin.CameraYOffset.ToString("0.00") : "?";
            string mode = s != null ? s.GetTrackingOriginMode().ToString() : "?";

            FloorGranted = floorGranted;
            OriginMode = mode;

            if (floorGranted)
                Debug.Log($"[XROrigin] Tracking origin = {mode}, CameraYOffset = {offset}. " +
                          "Dikey referans TAMAM.");
            else
                Debug.LogError($"[XROrigin] Floor modu REDDEDILDI! Gecerli mod = {mode}, " +
                               $"CameraYOffset = {offset}. Oyuncu zeminden yukarida/asagida dogacaktir — " +
                               "bu DRIFT DEGIL, kurulum hatasidir; anchor duzeltmesi bunu cozmez.");
        }
    }
}
