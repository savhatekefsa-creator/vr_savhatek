using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Two-point manual colocation calibration. Everyone in the SAME physical room aligns to a
    /// shared physical reference, so real-world distance == in-game distance.
    ///
    /// DORMANT until <see cref="Begin"/> is called (the team selector calls it after the player
    /// picks a team), so the onboarding order is: join -> pick team -> calibrate -> play.
    ///
    /// Flow (each player):
    ///   1) Put the RIGHT controller on physical point A (the shared origin) and pull the TRIGGER.
    ///   2) Move it to physical point B (which defines the forward direction) and pull the TRIGGER.
    /// The rig recenters so A maps to <see cref="sharedOrigin"/> and A->B maps to
    /// <see cref="sharedForward"/>. Pull the trigger again to re-calibrate.
    /// </summary>
    public class CalibrationManager : MonoBehaviour
    {
        [Tooltip("The XR rig to recenter (defaults to this GameObject).")]
        public Transform rig;
        [Tooltip("The right-controller anchor whose world position marks points A and B.")]
        public Transform pointer;
        public TextMesh status;

        [Header("Shared virtual reference (MUST be the same on every headset)")]
        public Vector3 sharedOrigin = Vector3.zero;
        public Vector3 sharedForward = Vector3.forward;

        bool _started;
        int _step;            // 0 = waiting for A, 1 = waiting for B, 2 = done
        Vector3 _a;
        bool _prevTrigger;
        bool _prevY;

        /// <summary>True once this player has completed A/B calibration at least once.
        /// The room-scan sender requires this, otherwise the scan would be recorded in
        /// device-local coordinates instead of the shared frame.</summary>
        public static bool Calibrated { get; private set; }

        void Start()
        {
            if (rig == null) rig = transform;
            if (status != null) status.gameObject.SetActive(false); // hidden until Begin()
        }

        /// <summary>Starts the calibration step (called after the player picked a team).</summary>
        public void Begin()
        {
            if (_started) return;
            _started = true;
            _step = 0;
            SetStatus("KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
        }

        void Update()
        {
            if (!_started) return;

            FollowHead();

            // The trigger only captures points DURING calibration. Once done it is ignored, so
            // an accidental trigger pull mid-game can never ruin the alignment.
            bool trigger = ReadRightTrigger();
            if (trigger && !_prevTrigger && _step < 2)   // rising edge
                CapturePoint();
            _prevTrigger = trigger;

            // Re-calibration is armed only by the LEFT controller's Y button.
            bool y = ReadLeftY();
            if (y && !_prevY && _step == 2)
            {
                _step = 0;
                SetStatus("YENIDEN KALIBRASYON\nSag kumandayi A noktasina koy,\nTETIGE bas.");
            }
            _prevY = y;
        }

        // Keep the instruction panel floating in front of the player's face.
        void FollowHead()
        {
            if (status == null || !status.gameObject.activeSelf) return;
            var r = XRRigReference.Instance;
            if (r == null || r.head == null) return;

            Vector3 fwd = r.head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            status.transform.position = r.head.position + fwd * 1.4f;
            status.transform.rotation = Quaternion.LookRotation(fwd);
        }

        void CapturePoint()
        {
            if (pointer == null) return;
            StopAllCoroutines(); // cancel a pending auto-hide
            Vector3 p = pointer.position;

            switch (_step)
            {
                case 0:
                    _a = p;
                    _step = 1;
                    SetStatus("A alindi.\nSimdi B noktasina koy (yon icin),\nTETIGE bas.");
                    break;
                case 1:
                    Apply(_a, p);
                    break;
            }
        }

        void Apply(Vector3 a, Vector3 b)
        {
            Vector3 dir = b - a; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
            {
                SetStatus("A ve B cok yakin.\nDaha uzak bir B sec, tetige bas.");
                _step = 1;
                return;
            }
            dir.Normalize();

            Vector3 fwd = new Vector3(sharedForward.x, 0f, sharedForward.z).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;

            // Rotate the whole rig around A so the physical A->B direction lines up with forward,
            // then slide (horizontally) so A sits on the shared origin.
            float angle = Vector3.SignedAngle(dir, fwd, Vector3.up);
            rig.RotateAround(a, Vector3.up, angle);
            Vector3 delta = sharedOrigin - a; delta.y = 0f;
            rig.position += delta;

            _step = 2;
            Calibrated = true;
            SetStatus("KALIBRE EDILDI!\nIyi oyunlar.\n(Yeniden kalibre: SOL kumanda Y tusu)");
            StartCoroutine(HideAfter(6f));
        }

        IEnumerator HideAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (status != null) status.gameObject.SetActive(false);
        }

        bool ReadRightTrigger() => XRButtons.Button(XRNode.RightHand, CommonUsages.triggerButton);

        bool ReadLeftY() => XRButtons.Button(XRNode.LeftHand, CommonUsages.secondaryButton);

        void SetStatus(string s)
        {
            if (status != null)
            {
                status.gameObject.SetActive(true);
                status.text = s;
            }
            Debug.Log("[Calibration] " + s);
        }
    }
}
