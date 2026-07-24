using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace VRMultiplayer
{
    /// <summary>
    /// Drives a Humanoid avatar so its arms (via Animation Rigging Two-Bone IK) follow the
    /// controllers and its body follows the head. Runs on EVERY client (owner and remote),
    /// because it reads the already-networked Head/LeftHand/RightHand transforms — NOT the
    /// owner-only XRRigReference. So each client solves the same avatar locally with zero
    /// extra networking.
    ///
    /// Body height is CALIBRATED ONCE, not servoed: a steady standing sample sets the uniform
    /// scale that puts the avatar's head bone at the player's eyes, and that scale is then locked
    /// for the session (<see cref="Recalibrate"/> re-runs it). Dropping below the calibrated
    /// standing height bends the knees via the Crouch layer; the body is never resized to follow
    /// the head, and the root always rests on the floor.
    ///
    /// Attach to the Humanoid model root (same GameObject as its Animator + RigBuilder).
    /// The Editor wizard wires all references.
    ///
    /// Execution order 70, which is the only window that works: after HandGrabber (10) has posed
    /// the held weapon AND after WeaponRecoil (60) has kicked it, so a welded hand's IK target is
    /// aimed at the weapon's FINAL pose for the frame (see <see cref="followWeaponWeld"/>) —
    /// solving before the recoil would leave the hands behind on every shot. Still ahead of
    /// WeaponGrip (90), the finger poser (100) and the weld itself (110). Nothing in between
    /// reads the avatar, and the networked hand carriers HandGrabber works from are siblings of
    /// this object rather than children, so moving off order 0 changes nothing for them.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public class AvatarIKController : MonoBehaviour
    {
        [Header("Networked sources (the NetworkPlayer's replicated children)")]
        public Transform headSource;
        public Transform leftHandSource;
        public Transform rightHandSource;

        [Header("IK targets (created under the rig)")]
        public Transform ikLeftHandTarget;
        public Transform ikRightHandTarget;

        [Header("Head bone (driven directly, no constraint)")]
        public Transform headBone;
        public bool driveHeadRotation = true;
        public Vector3 headEulerOffset;
        [Tooltip("Set on the LOCAL player so you don't see the inside of your own head.")]
        public bool hideHead;

        [Header("Grip offsets (controller pose -> hand bone; tune in Play mode)")]
        public Vector3 leftGripPositionOffset;
        public Vector3 leftGripEulerOffset;
        public Vector3 rightGripPositionOffset;
        public Vector3 rightGripEulerOffset;
        [Tooltip("Wrist sits this far behind the controller so the PALM holds it (not the wrist).")]
        public float palmOffset = 0.09f;
        [Tooltip("Learn your real max reach and remap distances so a fully extended real arm fully straightens the avatar's arm. OFF by default: the shipped NetworkPlayer prefab has always run without it, so the code default matches rather than silently differing.")]
        public bool armReachRemap = false;

        [Header("Silah tutusu (WeaponHandWeld ile ortak hedef)")]
        [Tooltip("Bir el silaha kaynaklandiginda o kolun IK hedefini de kaynak noktasina tasi. KAPATMA: kapaliyken bilek silaha cekilir ama kol kumandaya cozulur -- dirsek bukuk kalirken bilek on koldan ayrilir.")]
        public bool followWeaponWeld = true;

        [Header("Dirsek hint'i (DENEYSEL -- once ana duzeltmeyi cihazda dogrula)")]
        [Tooltip("Dirsek hint'ini her kare omuz-el hattindan konumlandir. Kapaliyken hint prefabdaki SABIT noktada kalir ve dirsek duzlemi eller nereye giderse gitsin gövdeye cakili durur.")]
        public bool dynamicElbowHints;
        [Tooltip("Dirsegi omuz-el orta noktasindan bu kadar it (m, avatar-lokal; X disari, Y asagi, Z geri). Olcekle carpilir. Cihazda ayarlanmali.")]
        public Vector3 elbowHintOffset = new Vector3(0.12f, -0.30f, -0.18f);

        [Tooltip("DENEYSEL: IK hedefleri yazildiktan SONRA rig'i ayni karede yeniden coz. Animator 'Normal' modda rig'i LateUpdate'ten ONCE isler, yani kol bir onceki karenin hedefine cozulur. Acmak bu bir kare gecikmeyi kapatir ama kare basina ikinci bir graph degerlendirmesi maliyeti getirir -- Quest'te olcmeden acma.")]
        public bool resolveRigSameFrame;

        [Header("Body")]
        [Tooltip("Feet position relative to the avatar root (measured by the wizard; usually negative).")]
        public float feetOffset = -0.9f;
        [Tooltip("World Y of the floor the players stand on (usually 0).")]
        public float groundY = 0f;
        [Tooltip("Avatar body/IK stays frozen until the head is at least this high (m) above the floor -- prevents the spawn-time 'fly up / sink into floor' glitch while the Floor tracking origin settles. Must sit above table height: a headset resting on a desk used to clear the old 0.4 m gate and get measured as the player.")]
        public float trackingReadyMinHeight = 1f;
        [Tooltip("Fine-tune: nudge the whole body up (+) or down (-).")]
        public float bodyHeightOffset = 0f;
        [Tooltip("Govde, kafa yaw'i bu aciyi asinca donmeye BASLAR. Kucuk goz atmalar govdeyi dondurmesin diye.")]
        public float yawDeadzone = 30f;
        [Tooltip("Donmeye basladiktan sonra govde bu aciya KADAR takip eder (histerezis). Bu alan olmadan donus tam esik acisinda birakiliyordu, yani govde kalici olarak 20-45 derece yamuk duruyordu: olculdu, 25 saniye boyunca sabit 22.5 derece. Omuz govde merkezinden ~17 cm yanda oldugu icin bu, omzu 7-13 cm yanlis yere koyar ve kol bos yere erisim disina dusmus gorunur.")]
        public float yawFollowThrough = 5f;
        public float yawSpeed = 220f;

        [Header("Ground snap (rests feet on the ground collider if there is one)")]
        [Tooltip("Requires ground colliders (Tools > VR Multiplayer > 4. Add Ground Colliders).")]
        public bool snapToGround = true;
        public float groundProbeUp = 2f;
        public float groundProbeDown = 12f;

        [Header("Embodiment (wear the avatar)")]
        [Tooltip("Measure the player's standing height ONCE, then scale the body so its head bone sits at their eyes and LOCK that scale. Crouching bends the knees (Crouch layer) instead of resizing the body.")]
        public bool fitToPlayerHeight = true;
        [Tooltip("Eyes sit this far in FRONT of the head bone, so the body hangs slightly behind the camera.")]
        public float headForwardOffset = 0.07f;
        [Tooltip("Hold a plausible, steady head height for this long (s) before the scale is solved and locked.")]
        public float calibrationSeconds = 2f;
        [Tooltip("The sampling window restarts if the head moves more than this (m) during it -- averaging a crouch or a jump into the sample would lock in a wrong height.")]
        public float calibrationTolerance = 0.12f;
        [Tooltip("Self-heal: a head held this far (m) ABOVE the locked standing height means the lock was taken too low (calibrated mid-crouch). You can never be taller than standing, so this only ever corrects upwards.")]
        public float recalibrateRise = 0.25f;
        [Tooltip("...and held there for this long (s) before the fit is re-solved.")]
        public float recalibrateSeconds = 3f;
        [Tooltip("Human proportions only: a bad height reading must never be able to produce a dwarf or a giant.")]
        public float minScale = 0.85f;
        public float maxScale = 1.25f;

        [Header("Kol uzunlugu (BOY olceginden bagimsiz, tek seferlik)")]
        [Tooltip("Oyuncunun gercek erisimi avatarin kol boyunu asiyorsa kol KEMIKLERINI uzat. Govde olcegine DOKUNULMAZ -- boy ve goz hizasi bozulmaz, el kumandanin tam ustunde kalir, dirsek acisi seninkini takip eder. Yalnizca UZATIR, asla kisaltmaz.")]
        public bool fitArmLength = true;
        [Tooltip("Kol en fazla bu carpanla uzatilabilir. 1.25 = %25. Bozuk bir tracking sicramasi goril kolu yapamasin diye.")]
        public float maxArmStretch = 1.25f;
        [Tooltip("Yeni bir erisim tepesi gelmeden bu kadar saniye gecerse olcu kilitlenir ve kol bir kez uzatilir.")]
        public float reachSettleSeconds = 4f;

        [Header("Locomotion animation")]
        [Tooltip("Animator float parameter fed with the player's horizontal speed (m/s).")]
        public string speedParam = "Speed";
        public float speedSmoothing = 6f;

        [Header("Crouch")]
        [Tooltip("Crouch blending starts when you drop below this fraction of your standing height. Kept clear of 1.0 so ordinary head bobbing while walking doesn't read as a crouch.")]
        public float crouchStartRatio = 0.88f;
        [Tooltip("Full crouch pose at this fraction of your standing height.")]
        public float crouchFullRatio = 0.65f;
        public float crouchSmoothing = 8f;

        // A tracked HMD outside this band is not a worn headset (resting on a table, carried by
        // hand, tracking still settling). Such samples must never reach the height calibration.
        const float StandMinHeight = 1.0f;
        const float StandMaxHeight = 2.2f;
        // Consecutive plausible head poses the startup gate wants before it trusts tracking.
        const int TrackingReadyFrames = 10;

        float _baseScale = 1f;   // authored localScale
        float _baseHeadH;        // head-bone height above the root, at authored scale
        float _scaleK = 1f;      // fit multiplier -- solved ONCE at calibration, then constant
        bool _fitLocked;         // true once the height calibration has produced a scale
        bool _trackingValid;     // false until the first plausible head pose (Floor origin settled)
        int _readyFrames;        // consecutive plausible head poses seen by the startup gate

        // Height-calibration sampling window.
        float _calibSum, _calibTime, _calibMin, _calibMax;
        int _calibFrames;
        float _tallTime;         // how long the head has read above the locked standing height

        Animator _animator;
        int _speedHash;
        bool _speedParamOK;
        Vector3 _lastHeadXZ;
        bool _hasLastHead;
        float _smoothSpeed;
        int _crouchLayer = -1;
        float _standingH;
        float _smoothCrouch;

        // Hand-orientation mapping, measured from the skeleton's own finger bones so the
        // wrists follow the controllers regardless of the model's bone-axis convention.
        bool _leftRotOK, _rightRotOK;
        Quaternion _leftBasisInv = Quaternion.identity;
        Quaternion _rightBasisInv = Quaternion.identity;

        // Arm bones + learned real-world reach (per hand) for the straighten-arms remap.
        Transform _lUpper, _lLower, _lHand, _rUpper, _rLower, _rHand;
        float _maxReachL, _maxReachR;

        // Elbow hints, read off the constraints themselves so no name matching or extra prefab
        // wiring is needed. Only written when dynamicElbowHints is on.
        Transform _lHint, _rHint;

        // Set by WeaponHandWeld when it is added to this avatar (first grab of a profiled
        // weapon). Null the rest of the time — no per-frame GetComponent.
        Weapons.WeaponHandWeld _weld;

        RigBuilder _rigBuilder;

        internal void RegisterWeld(Weapons.WeaponHandWeld w) => _weld = w;
        internal void UnregisterWeld(Weapons.WeaponHandWeld w) { if (_weld == w) _weld = null; }

        void SetupHandOrientation(Animator animator)
        {
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return;

            _leftRotOK = ComputeHandBasis(animator, true, out _leftBasisInv);
            _rightRotOK = ComputeHandBasis(animator, false, out _rightBasisInv);

            _lUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);

            foreach (var c in GetComponentsInChildren<TwoBoneIKConstraint>(true))
            {
                ref var d = ref c.data;
                if (_leftRotOK || _rightRotOK)
                    d.targetRotationWeight = 1f; // wrist now follows the controller

                // Which arm this constraint drives, from its own root bone — no name matching.
                if (d.root == _lUpper) _lHint = d.hint;
                else if (d.root == _rUpper) _rHint = d.hint;
            }
        }

        // Local-space basis of a hand: forward = toward the fingers, up = palm normal.
        static bool ComputeHandBasis(Animator a, bool left, out Quaternion inverseBasis)
        {
            inverseBasis = Quaternion.identity;
            Transform hand = a.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform mid = a.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            if (mid == null)
                mid = a.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform thumb = a.GetBoneTransform(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
            if (hand == null || mid == null || thumb == null)
                return false;

            Vector3 fingers = hand.InverseTransformPoint(mid.position).normalized;
            Vector3 thumbDir = hand.InverseTransformPoint(thumb.position).normalized;
            Vector3 palm = left ? Vector3.Cross(thumbDir, fingers) : Vector3.Cross(fingers, thumbDir);
            if (fingers.sqrMagnitude < 1e-4f || palm.sqrMagnitude < 1e-4f)
                return false;

            inverseBasis = Quaternion.Inverse(Quaternion.LookRotation(fingers, palm));
            return true;
        }

        // Where the WRIST (IK tip) should go: pull back from the controller along the finger
        // direction so the palm holds the grip, then remap the shoulder distance so a fully
        // extended real arm fully straightens the avatar's (longer/shorter) arm.
        Vector3 HandTargetPos(Transform src, bool left, Vector3 gripPosOffset)
        {
            Vector3 pos = src.position + src.rotation * gripPosOffset
                        - src.forward * (palmOffset * _scaleK);
            if (!armReachRemap) return pos;

            Transform up = left ? _lUpper : _rUpper;
            Transform lo = left ? _lLower : _rLower;
            Transform ha = left ? _lHand : _rHand;
            if (up == null || lo == null || ha == null) return pos;

            Vector3 dir = pos - up.position;
            float dist = dir.magnitude;
            if (dist < 0.02f) return pos;

            // Keep learning the player's true max reach — capped to a plausible human reach so
            // a one-frame jump (calibration/teleport) can never poison the mapping.
            float cap = Mathf.Max(0.5f, _standingH * 0.55f);
            if (left) { if (dist > _maxReachL) _maxReachL = Mathf.Min(dist, cap); }
            else { if (dist > _maxReachR) _maxReachR = Mathf.Min(dist, cap); }
            float maxReach = left ? _maxReachL : _maxReachR;

            float armLen = Vector3.Distance(up.position, lo.position)
                         + Vector3.Distance(lo.position, ha.position);
            if (maxReach < 0.3f || armLen < 0.3f) return pos;

            // Near the body the hand matches the controller 1:1; only the LAST stretch of the
            // reach is remapped so a fully extended real arm fully straightens the avatar's arm.
            float r = dist / maxReach;
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.95f, r));
            float scale = Mathf.Lerp(1f, armLen / maxReach, blend);
            float mapped = Mathf.Min(dist * scale, armLen);
            return up.position + dir * (mapped / dist);
        }

        /// <summary>
        /// Where this hand's wrist would go with NO weapon: the plain controller-derived target.
        /// <see cref="Weapons.WeaponHandWeld"/> measures its weld target against this to decide how
        /// far the weapon has dragged the hand away from where the player's hand actually is —
        /// so both systems judge that distance from the same reference.
        /// </summary>
        public bool TryGetFreeHandTargetPos(bool left, out Vector3 pos)
        {
            Transform src = left ? leftHandSource : rightHandSource;
            if (src == null) { pos = Vector3.zero; return false; }
            pos = HandTargetPos(src, left, left ? leftGripPositionOffset : rightGripPositionOffset);
            return true;
        }

        // Natural grip: fingers point along the controller's forward, palm faces inward.
        Quaternion HandRotation(Transform source, bool left, Vector3 trimEuler)
        {
            Vector3 fingersW = source.forward;
            Vector3 palmW = left ? source.right : -source.right;
            Quaternion basisInv = left ? _leftBasisInv : _rightBasisInv;
            return Quaternion.LookRotation(fingersW, palmW) * basisInv * Quaternion.Euler(trimEuler);
        }

        // --- Reach diagnostics (read by AvatarFitDebug) ---
        // Peak shoulder-to-target distance seen while the hand was FREE. A weapon weld drives the
        // target from the weapon rather than the controller, so those frames would measure the
        // profile's geometry instead of the player's reach and are skipped. Only sampled after the
        // height fit locks, so the peak always means "since we knew how tall you are".
        float _peakReachL, _peakReachR;
        bool _newPeak;            // a peak grew this frame -> the settle timer restarts
        float _reachQuiet;        // seconds since the last new peak
        bool _armFitLocked;       // the arm lengthening has been solved at least once
        float _yawError;          // this frame's torso-vs-head yaw error, degrees
        bool _yawTurning;         // inside a committed turn (hysteresis latch)

        // A reach sample is only honest while the torso is square with the head. Twisted, the
        // shoulder is centimetres from where the player's real one is, and the arm would be
        // lengthened to cover the torso's error instead of the player's proportions.
        const float ReachSampleMaxYawError = 12f;
        // Re-solve the stretch if the measured reach later overruns the arm by more than this.
        // The first solve locks reachSettleSeconds after the last new peak, and a player who
        // keeps stretching further after that would otherwise be stuck with the early answer.
        const float RestretchSlack = 0.03f;

        // Authored bone offsets, so the stretch is always computed from the model's own
        // proportions rather than compounding on itself, and Recalibrate can undo it exactly.
        Vector3 _lLowerBase, _lHandBase, _rLowerBase, _rHandBase;
        bool _armBaseCaptured;
        float _armStretchL = 1f, _armStretchR = 1f;

        /// <summary>
        /// This arm's bone length and the distance from its shoulder to its IK target, both live
        /// and scaled, plus the largest free-hand distance seen this session.
        ///
        /// dist >= armLen is the whole diagnosis in one comparison: the two-bone solver has
        /// bottomed out, so the avatar's elbow reads straight no matter how much further the
        /// player's target travels. If PEAK exceeds armLen while your own arm still has travel
        /// left, something is inflating the shoulder-to-target distance -- the palm offset
        /// pointing the wrong way, or the torso yaw deadzone leaving the shoulder behind.
        /// </summary>
        public bool ArmFitLocked => _armFitLocked;
        public float ArmStretch(bool left) => left ? _armStretchL : _armStretchR;

        public bool TryGetReach(bool left, out float armLen, out float dist, out float peak)
        {
            armLen = dist = peak = 0f;
            Transform up = left ? _lUpper : _rUpper;
            Transform lo = left ? _lLower : _rLower;
            Transform ha = left ? _lHand : _rHand;
            Transform tgt = left ? ikLeftHandTarget : ikRightHandTarget;
            if (up == null || lo == null || ha == null || tgt == null) return false;

            armLen = Vector3.Distance(up.position, lo.position) + Vector3.Distance(lo.position, ha.position);
            dist = Vector3.Distance(up.position, tgt.position);
            peak = left ? _peakReachL : _peakReachR;
            return true;
        }

        // --- Height calibration (read by AvatarFitDebug) ---
        public bool FitLocked => _fitLocked;
        public float StandingHeight => _standingH;
        public float FitScale => _baseScale * _scaleK;
        public float CrouchWeight => _smoothCrouch;

        // Raised when the player finishes the room A/B alignment; every avatar this client is
        // simulating re-measures its player's height. An event (not FindObjectsOfType) so only
        // live, enabled controllers are touched.
        static event System.Action RecalibrateRequested;

        /// <summary>Asks every active avatar on this client to re-run its height fit.</summary>
        public static void RecalibrateAll() => RecalibrateRequested?.Invoke();

        void OnEnable() => RecalibrateRequested += Recalibrate;
        void OnDisable() => RecalibrateRequested -= Recalibrate;

        /// <summary>Throws away the locked fit so the next steady standing sample re-solves it.</summary>
        public void Recalibrate()
        {
            _fitLocked = false;
            _standingH = 0f;
            _tallTime = 0f;
            _maxReachL = _maxReachR = 0f;

            // Fresh reach measurement alongside the fresh fit, and the arms go back to the
            // model's authored proportions so the next solve starts from the same baseline
            // instead of compounding on the last stretch.
            _peakReachL = _peakReachR = 0f;
            _newPeak = false;
            _reachQuiet = 0f;
            _armFitLocked = false;
            _armStretchL = _armStretchR = 1f;
            if (_armBaseCaptured)
            {
                _lLower.localPosition = _lLowerBase;
                _lHand.localPosition = _lHandBase;
                _rLower.localPosition = _rLowerBase;
                _rHand.localPosition = _rHandBase;
            }
            ResetCalibrationWindow();

            // Never re-measure out of a squat: the crouch pose would lower the head bone and the
            // fresh sample would lock in a shorter player than the one standing there.
            if (_crouchLayer >= 0 && _animator != null)
            {
                _smoothCrouch = 0f;
                _animator.SetLayerWeight(_crouchLayer, 0f);
            }
        }

        /// <summary>
        /// Collects a window of plausible, STEADY head heights and, once it is long enough,
        /// solves the avatar scale from it and locks it. A window the player moved through is
        /// discarded rather than averaged, so a crouch or a jump can't set the standing height.
        /// </summary>
        void TickCalibration(float ph)
        {
            if (ph < StandMinHeight || ph > StandMaxHeight)
            { ResetCalibrationWindow(); return; }   // on a table / carried / still settling

            if (_calibFrames == 0) { _calibMin = ph; _calibMax = ph; }
            else { _calibMin = Mathf.Min(_calibMin, ph); _calibMax = Mathf.Max(_calibMax, ph); }

            if (_calibMax - _calibMin > calibrationTolerance)
            { ResetCalibrationWindow(); return; }   // player moved mid-sample -- start over

            _calibSum += ph;
            _calibFrames++;
            _calibTime += Time.deltaTime;
            if (_calibTime < calibrationSeconds)
                return;

            _standingH = _calibSum / _calibFrames;

            // Head height of the CURRENT standing pose, normalised to scale 1. Measured live off
            // the skeleton rather than from the bind pose, which sits a few cm higher than the
            // idle the avatar actually stands in.
            float headPerUnit = (headBone.position.y - transform.position.y) / Mathf.Max(0.01f, _scaleK);
            if (headPerUnit < 0.5f) headPerUnit = _baseHeadH;   // pose not evaluated yet

            if (headPerUnit > 0.5f)
            {
                _scaleK = Mathf.Clamp(_standingH / headPerUnit, minScale, maxScale);
                transform.localScale = Vector3.one * (_baseScale * _scaleK);
            }

            _fitLocked = true;
            ResetCalibrationWindow();
        }

        void ResetCalibrationWindow()
        {
            _calibSum = 0f; _calibTime = 0f; _calibFrames = 0; _calibMin = 0f; _calibMax = 0f;
        }

        void Awake()
        {
            _baseScale = transform.localScale.x;
            if (headBone != null)
                _baseHeadH = headBone.position.y - transform.position.y; // at authored scale

            // An EMPTY AnimatorController collapses a humanoid into the hunched "muscle
            // neutral" pose — drop it. A controller WITH clips (e.g. the Idle added by wizard
            // step 7) is kept: it poses the legs/torso while IK drives the arms.
            var animator = GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null &&
                animator.runtimeAnimatorController.animationClips.Length == 0)
                animator.runtimeAnimatorController = null;

            SetupHandOrientation(animator);
            CaptureArmBaseline();   // needs the arm bones SetupHandOrientation just resolved
            _rigBuilder = GetComponent<RigBuilder>();

            // Locomotion blend: feed the Animator's Speed parameter (if the controller has one).
            _animator = animator;
            _speedHash = Animator.StringToHash(speedParam);
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                foreach (var p in animator.parameters)
                    if (p.name == speedParam) { _speedParamOK = true; break; }
                _crouchLayer = animator.GetLayerIndex("Crouch"); // -1 if not added yet
            }
        }

        // LateUpdate: runs after the Animator + rig jobs and after NetworkVRPlayer has written
        // the networked child poses this frame.
        void LateUpdate()
        {
            if (headSource == null)
                return;

            // Startup gate: on session start the XR tracking origin (Floor mode) needs a few
            // frames to settle; until then head-Y reads ~0 and positioning/scaling the body from
            // it launches the avatar into the air or sinks it into the floor ("her giriste
            // ucma/egilme"). Hold ALL body/IK updates until the head pose is a plausible height
            // above the floor. One-shot latch, so later crouching never re-triggers it.
            if (!_trackingValid)
            {
                if (headSource.position.y - groundY < trackingReadyMinHeight)
                { _readyFrames = 0; return; }
                // A run of good frames, not a single one: a lone spike out of a settling origin
                // would otherwise open the gate and feed the height calibration a bogus sample.
                if (++_readyFrames < TrackingReadyFrames)
                    return;
                _trackingValid = true;
            }

            // Real walking -> walking animation: measure the head's horizontal speed and feed
            // it to the Animator (blends Idle <-> Walk in the locomotion blend tree).
            if (_speedParamOK)
            {
                Vector3 h = headSource.position; h.y = 0f;
                if (_hasLastHead && Time.deltaTime > 0.0001f)
                {
                    float v = (h - _lastHeadXZ).magnitude / Time.deltaTime;
                    _smoothSpeed = Mathf.Lerp(_smoothSpeed, Mathf.Min(v, 5f),
                        speedSmoothing * Time.deltaTime);
                    _animator.SetFloat(_speedHash, _smoothSpeed);
                }
                _lastHeadXZ = h;
                _hasLastHead = true;
            }

            float ph = headSource.position.y - groundY;

            // Per-arm max reach, seeded from the CALIBRATED standing height (arm reach ~ 44% of
            // it). _standingH used to be a running Mathf.Max that never decayed, so a single
            // raised-headset frame inflated the reach -- and the crouch ratio below -- for the
            // rest of the session. It is now a locked measurement, so both stay put.
            if (_fitLocked)
            {
                float reachSeed = _standingH * 0.44f;
                float reachCap = Mathf.Max(0.5f, _standingH * 0.55f); // human arms never exceed this
                _maxReachL = Mathf.Clamp(Mathf.MoveTowards(_maxReachL, reachSeed, 0.01f * Time.deltaTime),
                    reachSeed, reachCap); // gentle decay heals a polluted sample
                _maxReachR = Mathf.Clamp(Mathf.MoveTowards(_maxReachR, reachSeed, 0.01f * Time.deltaTime),
                    reachSeed, reachCap);
            }

            // Real crouching -> crouch pose: blend the Crouch layer in as the player drops below
            // their calibrated standing height (knees bend instead of the body shrinking). Gated
            // on the lock: before it, _standingH means nothing and would squat a standing player.
            if (_fitLocked && _crouchLayer >= 0 && _animator != null)
            {
                float ratio = ph / _standingH;
                float crouch = Mathf.Clamp01(
                    (crouchStartRatio - ratio) / Mathf.Max(0.05f, crouchStartRatio - crouchFullRatio));
                _smoothCrouch = Mathf.Lerp(_smoothCrouch, crouch, crouchSmoothing * Time.deltaTime);
                _animator.SetLayerWeight(_crouchLayer, _smoothCrouch);
            }

            if (fitToPlayerHeight && headBone != null)
            {
                // ONE-TIME height calibration -> a LOCKED uniform scale.
                //
                // This used to be a per-frame servo comparing the player's head height with the
                // LIVE POSED head bone. Any pose that lowers that bone -- above all the Crouch
                // layer, which drops it ~0.9 m -- therefore read as "the avatar is too short" and
                // grew the body, which lowered the bone again: a feedback loop that ended with the
                // scale frozen at a wrong value and the avatar buried in, or floating above, the
                // floor. Solved once from a steady standing sample, it cannot drift at all.
                if (!_fitLocked)
                    TickCalibration(ph);
                else if (ph > _standingH + recalibrateRise)
                {
                    // Nobody is taller than their standing height, so a sustained higher head
                    // means the lock was taken while crouched or kneeling. Self-heal rather than
                    // make the player restart the app.
                    _tallTime += Time.deltaTime;
                    if (_tallTime >= recalibrateSeconds) Recalibrate();
                }
                else _tallTime = 0f;

                // Feet on the floor, torso just behind the eyes. The root is NO LONGER derived
                // from the posed head bone: crouching now bends the knees in place instead of
                // sliding the whole avatar up off the ground.
                Vector3 look = headSource.forward; look.y = 0f;
                if (look.sqrMagnitude < 0.01f) look = transform.forward;
                look.Normalize();
                Vector3 xz = headSource.position - look * (headForwardOffset * _scaleK);
                transform.position = new Vector3(xz.x, groundY + bodyHeightOffset, xz.z);
            }
            else
            {
                // Legacy mode: pin the feet to the terrain (raycast), body under the head.
                float g = groundY;
                if (snapToGround)
                {
                    Vector3 origin = new Vector3(
                        headSource.position.x,
                        headSource.position.y + groundProbeUp,
                        headSource.position.z);
                    if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                            groundProbeUp + groundProbeDown, ~0, QueryTriggerInteraction.Ignore))
                        g = hit.point.y;
                }
                transform.position = new Vector3(
                    headSource.position.x,
                    g - feetOffset + bodyHeightOffset,
                    headSource.position.z);
            }

            // Yaw-follow the head, with HYSTERESIS rather than a plain deadzone.
            //
            // A plain deadzone stops the turn at the threshold itself, so the torso comes to rest
            // still twisted by up to the full deadzone and stays there — measured on device: a
            // dead-constant 22.5 degrees for 25 s, and 39-43 degrees at the moments the player was
            // reaching hardest. That is not cosmetic. The shoulder sits ~17 cm off the body's
            // turn axis, so a 22.5 degree twist puts it 6.7 cm from where the player's real
            // shoulder is, and 45 degrees puts it 13.2 cm out. Every centimetre of that is
            // subtracted from the arm's usable reach, which is what made a half-bent real arm
            // read as a fully straight avatar arm.
            //
            // Two thresholds fix it without reintroducing the twitch the deadzone was there to
            // prevent: a glance under yawDeadzone still moves nothing, but once the body commits
            // to a turn it follows all the way down to yawFollowThrough.
            float curYaw = transform.eulerAngles.y;
            float targetYaw = headSource.eulerAngles.y;
            _yawError = Mathf.Abs(Mathf.DeltaAngle(curYaw, targetYaw));
            if (!_yawTurning && _yawError > yawDeadzone) _yawTurning = true;
            if (_yawTurning)
            {
                float newYaw = Mathf.MoveTowardsAngle(curYaw, targetYaw, yawSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Euler(0f, newYaw, 0f);
                _yawError = Mathf.Abs(Mathf.DeltaAngle(newYaw, targetYaw));
                if (_yawError <= Mathf.Max(0.5f, yawFollowThrough)) _yawTurning = false;
            }

            // Hand IK targets (controller pose + grip offset). Rotation is remapped through the
            // skeleton's own hand axes so the wrist follows the controller naturally.
            ReapplyArmStretch();

            ApplyHandTarget(ikLeftHandTarget, leftHandSource, true,
                leftGripPositionOffset, leftGripEulerOffset);
            ApplyHandTarget(ikRightHandTarget, rightHandSource, false,
                rightGripPositionOffset, rightGripEulerOffset);

            TickArmFit();

            if (dynamicElbowHints)
            {
                PlaceElbowHint(_lHint, _lUpper, ikLeftHandTarget, true);
                PlaceElbowHint(_rHint, _rUpper, ikRightHandTarget, false);
            }

            // Close the one-frame arm lag: with Animator update mode Normal the rig is evaluated
            // in PreLateUpdate, i.e. BEFORE this method, so the arms otherwise solve against the
            // targets written last frame while the weld writes the wrist from this frame's weapon
            // pose. Zero delta so the clips don't advance twice — only the constraints re-solve.
            if (resolveRigSameFrame && _rigBuilder != null)
                _rigBuilder.Evaluate(0f);

            // Head: copy the HMD look direction onto the head bone (after the rig ran).
            if (driveHeadRotation && headBone != null)
                headBone.rotation = headSource.rotation * Quaternion.Euler(headEulerOffset);

            // First-person: collapse the local player's own head so it isn't in front of the camera.
            if (hideHead && headBone != null)
                headBone.localScale = new Vector3(0.001f, 0.001f, 0.001f);
        }

        /// <summary>
        /// One arm's IK target: the controller pose, blended toward the weapon anchor by the
        /// weld's own weight when this hand is holding or supporting a profiled weapon.
        ///
        /// This blend is the whole point. WeaponHandWeld writes the wrist bone absolutely after
        /// the rig; if the IK target stayed on the controller, the two would be solving for
        /// different points and the difference would show up as a wrist detached from a bent
        /// forearm. Sharing the target means the arm reaches for the grip and the weld only has
        /// to hold what the arm already got close to.
        /// </summary>
        void ApplyHandTarget(Transform ikTarget, Transform src, bool left,
            Vector3 posOffset, Vector3 eulerOffset)
        {
            if (ikTarget == null || src == null) return;

            Vector3 pos = HandTargetPos(src, left, posOffset);
            Quaternion rot = (left ? _leftRotOK : _rightRotOK)
                ? HandRotation(src, left, eulerOffset)
                : src.rotation * Quaternion.Euler(eulerOffset);

            bool welded = false;
            if (followWeaponWeld && _weld != null &&
                _weld.TryGetWeld(left, out Vector3 wp, out Quaternion wr, out float ww) && ww > 0f)
            {
                pos = Vector3.Lerp(pos, wp, ww);
                rot = Quaternion.Slerp(rot, wr, ww);
                welded = true;
            }

            ikTarget.SetPositionAndRotation(pos, rot);

            // Free-hand reach peak: feeds both the on-headset read-out and the arm-length fit.
            // Gated on the height lock so _standingH is meaningful, and rejected above a reach no
            // human has for their height — one tracking spike must not stretch the arms.
            if (!welded && _fitLocked && _yawError <= ReachSampleMaxYawError)
            {
                Transform shoulder = left ? _lUpper : _rUpper;
                if (shoulder != null)
                {
                    float d = Vector3.Distance(shoulder.position, pos);
                    if (d <= _standingH * 0.5f)
                    {
                        if (left) { if (d > _peakReachL) { _peakReachL = d; _newPeak = true; } }
                        else { if (d > _peakReachR) { _peakReachR = d; _newPeak = true; } }
                    }
                }
            }
        }

        /// <summary>
        /// ARM LENGTH FIT — the answer to "my real elbow is at 130 degrees but the avatar's is
        /// already straight".
        ///
        /// The body scale is not the lever: it is fully determined by your eye height (the head
        /// bone has to land at your eyes), and this model's arm-to-height proportion already
        /// matches an average human to within a millimetre. So when a player's reach still
        /// overruns the arm, the mismatch is in PROPORTION, not size, and one uniform scale
        /// cannot serve both. Scaling the whole avatar up to lengthen the arms would push the
        /// head bone above your eyes and you would be looking out of its chest.
        ///
        /// Two-bone IK leaves only a bad choice when the target is beyond reach: keep the hand on
        /// the controller and bottom the elbow out at 180 (today), or keep the elbow angle honest
        /// and let the hand fall short of the controller (the arm-reach remap). Lengthening the
        /// arm BONES removes the choice — the target stops being out of reach, so the hand stays
        /// exactly on the controller AND the elbow tracks yours.
        ///
        /// Done by pushing the child bone further out rather than scaling the bone transforms:
        /// scaling would enlarge the hand and the fingers with it, while moving the offset simply
        /// stretches the skin between two joints. Applied once, after the height fit has locked
        /// and the measured reach has stopped growing; it can only ever lengthen, and never past
        /// <see cref="maxArmStretch"/>.
        /// </summary>
        void TickArmFit()
        {
            if (!fitArmLength || !_armBaseCaptured || !_fitLocked) return;

            // Still discovering how far this player reaches — hold the measurement open.
            if (_newPeak) { _newPeak = false; _reachQuiet = 0f; return; }
            if (_peakReachL <= 0f && _peakReachR <= 0f) return;

            // Solved, and the arms still cover everything measured since: nothing to do.
            if (_armFitLocked && !(ArmFallsShort(true) || ArmFallsShort(false))) return;

            _reachQuiet += Time.deltaTime;
            if (_reachQuiet < reachSettleSeconds) return;

            SolveArmStretch(true);
            SolveArmStretch(false);
            _armFitLocked = true;
        }

        // True when this arm is measurably shorter than the reach seen since it was solved — and
        // lengthening it further is still allowed. Without the cap check, an arm already pinned at
        // maxArmStretch would re-solve forever without ever changing.
        bool ArmFallsShort(bool left)
        {
            Transform up = left ? _lUpper : _rUpper;
            Transform lo = left ? _lLower : _rLower;
            Transform ha = left ? _lHand : _rHand;
            if (up == null || lo == null || ha == null) return false;
            if ((left ? _armStretchL : _armStretchR) >= Mathf.Max(1f, maxArmStretch) - 0.001f) return false;

            float armLen = Vector3.Distance(up.position, lo.position)
                         + Vector3.Distance(lo.position, ha.position);
            return (left ? _peakReachL : _peakReachR) > armLen + RestretchSlack;
        }

        void SolveArmStretch(bool left)
        {
            Transform up = left ? _lUpper : _rUpper;
            Transform lo = left ? _lLower : _rLower;
            Transform ha = left ? _lHand : _rHand;
            if (up == null || lo == null || ha == null) return;

            float armLen = Vector3.Distance(up.position, lo.position)
                         + Vector3.Distance(lo.position, ha.position);
            float peak = left ? _peakReachL : _peakReachR;
            if (armLen < 0.2f || peak <= 0f) return;

            float k = Mathf.Clamp(peak / armLen, 1f, Mathf.Max(1f, maxArmStretch));
            if (left) _armStretchL = k; else _armStretchR = k;
        }

        // Re-applied every frame, not written once: humanoid retargeting is free to rewrite bone
        // transforms on any Animator evaluation, and four Vector3 assignments are cheaper than
        // finding out the hard way that it did.
        void ReapplyArmStretch()
        {
            if (!_armBaseCaptured) return;
            if (_armStretchL != 1f)
            {
                _lLower.localPosition = _lLowerBase * _armStretchL;
                _lHand.localPosition = _lHandBase * _armStretchL;
            }
            if (_armStretchR != 1f)
            {
                _rLower.localPosition = _rLowerBase * _armStretchR;
                _rHand.localPosition = _rHandBase * _armStretchR;
            }
        }

        void CaptureArmBaseline()
        {
            if (_lLower == null || _lHand == null || _rLower == null || _rHand == null) return;
            _lLowerBase = _lLower.localPosition;
            _lHandBase = _lHand.localPosition;
            _rLowerBase = _rLower.localPosition;
            _rHandBase = _rHand.localPosition;
            _armBaseCaptured = true;
        }

        /// <summary>
        /// Put the elbow hint on the shoulder-to-hand line, pushed out/down/back, so the elbow
        /// swivel follows the hands. The authored alternative is a hint parented to the rig at a
        /// fixed body-relative point, which pins the elbow plane no matter where the hands go —
        /// visible as an elbow stuck bent in the wrong plane when a rifle comes up to eye level.
        /// </summary>
        void PlaceElbowHint(Transform hint, Transform shoulder, Transform ikTarget, bool left)
        {
            if (hint == null || shoulder == null || ikTarget == null) return;
            Vector3 mid = Vector3.Lerp(shoulder.position, ikTarget.position, 0.5f);
            Vector3 o = elbowHintOffset;
            if (left) o.x = -o.x;   // X is authored as "outward", so it mirrors per side
            hint.position = mid + transform.rotation * (o * _scaleK);
        }
    }
}
