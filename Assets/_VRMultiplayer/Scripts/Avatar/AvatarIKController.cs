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
    /// </summary>
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

        // ---- DIRSEK YONLENDIRME ----
        // Kolda uc eklem var, ikisi zorunlu: EL kumandanin oldugu yere civili, OMUZ govdeye.
        // Arada kalan DIRSEK serbest parametre — nereye koyarsan koy el ayni yerde kalir. Yani
        // dirsegi oynatmak BEDAVADIR: oyuncunun kontrol ettigi hicbir sey bozulmaz, silah ve
        // nisan zerre kadar kaymaz. Kolun govdeye girmesini duzeltmek icin dogru yer burasi.
        //
        // Prefabtaki LeftElbowHint/RightElbowHint SABIT bir noktada duruyordu
        // (+-0.367, 0.917, -0.300) ve onu runtime'da kimse oynatmiyordu. Sabit hint elin nerede
        // oldugunu bilmez: eli govdenin KARSI tarafina goturdugunde dirsek hala 30 cm GERIYE
        // cekiliyor ve on kol gogsun ICINDEN geciyordu. Gercekte sag elini sol omzuna koyarken
        // sag dirsegin ONE gider, on kol gogsun ONUNDEN gecer — sabit hint'in tam tersi.
        [Header("Dirsek yonlendirme (kolun govdeye girmesini onler)")]
        [Tooltip("Dirsek hint'ini her kare ele gore konumlandir. KAPATMA: prefabtaki sabit hint " +
                 "noktasina doner, yani el govdenin onunden gecerken kol govdenin icinden gecer.")]
        public bool driveElbowHints = true;

        [Tooltip("Dirsegin asagi sarkma agirligi. Insan dirsegi dinlenirken asagi bakar.")]
        public float elbowDown = 1f;

        [Tooltip("Dirsegin DISARI (govdeden uzaga) agirligi. Kaburgalardan uzak tutan bilesen.")]
        public float elbowOut = 0.55f;

        [Tooltip("El kendi tarafindayken dirsegin GERI agirligi (dogal dinlenme durusu).")]
        public float elbowBack = 0.40f;

        [Tooltip("El govdenin KARSI tarafina gectiginde dirsegin ONE agirligi. Asil duzeltme bu: " +
                 "on kol gogsun icinden degil ONUNDEN gecer.")]
        public float elbowForwardOnCross = 0.70f;

        [Tooltip("Bilek govde merkezini bu kadar gectiginde 'tam capraz' sayilir (metre).")]
        public float crossBlendMeters = 0.12f;

        [Tooltip("Hint'in dirsek-ekseninden uzakligi (metre). Yon belirler, mesafe yalnizca " +
                 "kararlilik icin — buyugu daha stabil.")]
        public float elbowHintDistance = 0.30f;

        [Tooltip("GARANTI: dirsek govdenin dusey ekseninden en az bu kadar disarida kalir " +
                 "(metre). Omuz zaten ~0.20'de, o yuzden bu deger 'tavuk kanadi' yapmaz.")]
        public float minElbowRadius = 0.20f;

        [Header("Body")]
        [Tooltip("Feet position relative to the avatar root (measured by the wizard; usually negative).")]
        public float feetOffset = -0.9f;
        [Tooltip("World Y of the floor the players stand on (usually 0).")]
        public float groundY = 0f;
        [Tooltip("Avatar body/IK stays frozen until the head is at least this high (m) above the floor -- prevents the spawn-time 'fly up / sink into floor' glitch while the Floor tracking origin settles. Must sit above table height: a headset resting on a desk used to clear the old 0.4 m gate and get measured as the player.")]
        public float trackingReadyMinHeight = 1f;
        [Tooltip("Fine-tune: nudge the whole body up (+) or down (-).")]
        public float bodyHeightOffset = 0f;
        [Tooltip("Kafa dosemenin bu kadar altina inerse oyuncu DUSUYOR sayilir ve govde kafayi " +
                 "birebir asagi takip eder (bkz. FallHazard). Comelme bu esige ASLA ulasmaz: " +
                 "comelen bir oyuncunun kafasi zeminin altina inmez, yere yatanin bile ~20 cm " +
                 "ustunde kalir. Kucultursen egilen oyuncunun govdesi yere batmaya baslar.")]
        public float fallFollowDepth = 1f;
        [Tooltip("Body only turns after the head yaw differs by more than this.")]
        public float yawDeadzone = 45f;
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

        // TwoBoneIK constraint'lerinden cozulur (prefabta LeftElbowHint / RightElbowHint).
        Transform _lElbowHint, _rElbowHint;
        float _maxReachL, _maxReachR;

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

                // Dirsek hint'ini KOL KOKUNDEN esliyoruz, prefab ISMINDEN degil: isme guvenmek
                // bir yeniden adlandirmada sessizce kirilir ve kol eski davranisina duserdi.
                if (d.root != null && d.root == _lUpper) _lElbowHint = d.hint;
                else if (d.root != null && d.root == _rUpper) _rElbowHint = d.hint;
            }
        }

        /// <summary>Dirsek yonlendirme ayarlari, tek pakette (saf fonksiyona gecirilebilsin diye).</summary>
        public struct ElbowTuning
        {
            public float down, outward, back, forwardOnCross, crossMeters, hintDistance, minRadius;
        }

        ElbowTuning Tuning => new ElbowTuning
        {
            down = elbowDown,
            outward = elbowOut,
            back = elbowBack,
            forwardOnCross = elbowForwardOnCross,
            crossMeters = crossBlendMeters,
            hintDistance = elbowHintDistance,
            minRadius = minElbowRadius,
        };

        void DriveElbowHint(bool left, Vector3 wrist)
        {
            if (!driveElbowHints) return;

            Transform hint = left ? _lElbowHint : _rElbowHint;
            Transform shoulder = left ? _lUpper : _rUpper;
            if (hint == null || shoulder == null) return;   // hint'siz rig: eski davranis

            hint.position = ElbowHintPos(shoulder.position, wrist,
                                         transform.position, transform.rotation,
                                         left, Tuning, _scaleK);
        }

        /// <summary>
        /// Dirsek hint'inin dunya konumu. SAF FONKSIYON — hicbir uye okumaz, her seyi parametre
        /// alir; boylece editorde sahne kurmadan test edilebilir.
        ///
        /// Dirsek, kolun tek SERBEST parametresidir (el kumandaya, omuz govdeye civili), yani
        /// buradaki hicbir sey oyuncunun kontrol ettigini bozmaz — silah ve nisan hic kaymaz.
        ///
        /// Yon uc bilesenden kurulur: ASAGI (dogal sarkma) + DISARI (kaburgalardan uzak) +
        /// DERINLIK. Derinlik el kendi tarafindayken GERI, karsi tarafa gectikce ONE doner —
        /// asil duzeltme bu: on kol gogsun icinden degil ONUNDEN gecer. Sonra yon kol eksenine
        /// dik bilesene indirilir (hint mesafesi erisimle kavga etmesin) ve son olarak dirsegin
        /// govde ekseninden en az <c>minRadius</c> disarida kalmasi ZORLANIR.
        /// </summary>
        /// <param name="bodyPos">Avatar kokunun konumu; yalnizca x/z'si (govdenin dusey ekseni) kullanilir.</param>
        /// <param name="bodyRot">Avatar kokunun donusu (yaw-only), govde eksenleri buradan.</param>
        /// <param name="scale">Boy kalibrasyonu carpani: mesafeler kisa/uzun oyuncuda olceklenir.</param>
        public static Vector3 ElbowHintPos(Vector3 shoulder, Vector3 wrist,
                                          Vector3 bodyPos, Quaternion bodyRot,
                                          bool left, ElbowTuning t, float scale)
        {
            Vector3 up = bodyRot * Vector3.up;
            Vector3 fwd = bodyRot * Vector3.forward;
            float side = left ? -1f : 1f;
            Vector3 outward = (bodyRot * Vector3.right) * side;

            // Bilek govde merkezini gecti mi? Kendi tarafinda pozitif, karsiya gecince negatif.
            Vector3 wLocal = Quaternion.Inverse(bodyRot) * (wrist - bodyPos);
            float lateral = wLocal.x * side;
            float cross01 = t.crossMeters > 1e-4f
                ? Mathf.Clamp01(-lateral / t.crossMeters)
                : (lateral < 0f ? 1f : 0f);
            float depth = Mathf.Lerp(-t.back, t.forwardOnCross, cross01);

            Vector3 bulge = -up * t.down + outward * t.outward + fwd * depth;
            if (bulge.sqrMagnitude < 1e-6f) bulge = outward;

            Vector3 armDir = wrist - shoulder;
            if (armDir.sqrMagnitude > 1e-6f)
            {
                armDir.Normalize();
                bulge -= armDir * Vector3.Dot(bulge, armDir);
                if (bulge.sqrMagnitude < 1e-6f)
                {
                    // Yon kol eksenine paralel cikti (kol tam o yone uzanmis): disariyi dene,
                    // o da paralelse yukariyi — ikisi ayni anda kol ekseni olamaz.
                    bulge = outward - armDir * Vector3.Dot(outward, armDir);
                    if (bulge.sqrMagnitude < 1e-6f) bulge = up - armDir * Vector3.Dot(up, armDir);
                }
            }
            if (bulge.sqrMagnitude < 1e-6f) return (shoulder + wrist) * 0.5f + outward * t.hintDistance;
            bulge.Normalize();

            Vector3 hint = (shoulder + wrist) * 0.5f + bulge * (t.hintDistance * scale);

            // GARANTI: dirsek govdenin dusey ekseninden en az minRadius kadar disarida.
            float minR = t.minRadius * scale;
            float dx = hint.x - bodyPos.x, dz = hint.z - bodyPos.z;
            float h = Mathf.Sqrt(dx * dx + dz * dz);
            if (h < minR)
            {
                Vector3 o;
                if (h > 1e-4f) o = new Vector3(dx / h, 0f, dz / h);
                else
                {
                    o = new Vector3(outward.x, 0f, outward.z);
                    o = o.sqrMagnitude > 1e-6f ? o.normalized : Vector3.forward;
                }
                hint += o * (minR - h);
            }
            return hint;
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

        // Natural grip: fingers point along the controller's forward, palm faces inward.
        Quaternion HandRotation(Transform source, bool left, Vector3 trimEuler)
        {
            Vector3 fingersW = source.forward;
            Vector3 palmW = left ? source.right : -source.right;
            Quaternion basisInv = left ? _leftBasisInv : _rightBasisInv;
            return Quaternion.LookRotation(fingersW, palmW) * basisInv * Quaternion.Euler(trimEuler);
        }

        // --- Height calibration (read by AvatarFitDebug) ---
        /// <summary>
        /// Oyuncu su an DUSUYOR mu: kafa dosemenin <see cref="fallFollowDepth"/> kadar altinda.
        ///
        /// Disari acilmasinin sebebi <see cref="UI.AvatarCrouchPose"/>: o bilesen avatarin
        /// tabani zeminin altina inince koku yukari itiyor (batmayi onleyen bir sinir) ve
        /// dususte bu sinir avatari her karede catiya geri firlatiyordu — kafa 44 m asagida,
        /// govde catida. Zemin sinirinin gecerli olmadigi TEK durum budur, ve bunu bilen tek
        /// yer burasi.
        /// </summary>
        public bool Falling { get; private set; }

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

            // DUSEN OYUNCUNUN GOVDESI DE DUSMELI. Govde yuksekligi asagida groundY'den
            // turetiliyor, kafadan DEGIL — bu, comelmenin avatari yerden kaldirmasini onleyen
            // bilincli bir karar (bkz. asagidaki "root is NO LONGER derived" notu). Ama catidan
            // dusen oyuncuda ayni karar kafayi 44 m asagi gonderip govdeyi catida BIRAKIYORDU:
            // dusen oyuncu kendi dususunu goruyor, digerleri onu yerinde duruyor goruyordu.
            //
            // AYRIM NET VE UCUZ: comelen bir oyuncunun kafasi zeminin ALTINA inmez, yere yatanin
            // bile ~20 cm ustunde kalir. Kafa dosemenin fallFollowDepth kadar altindaysa bu
            // comelme degil DUSUSTUR.
            //
            // MESAFE AGA VERILMIYOR: kafa zaten replike (owner-authoritative NetworkTransform),
            // ve ayakta olculmus boy (_standingH) ile arasindaki fark dususun TAM kendisidir.
            // Her istemci bu hesabi elindeki veriyle yapar — ek NetworkVariable, ek RPC yok.
            float fallDrop = ph < -fallFollowDepth
                ? (_fitLocked ? _standingH : 0f) - ph
                : 0f;
            bool falling = fallDrop > 0f;
            Falling = falling;

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
                // Dusen oyuncu COMELMIYOR. Ham oran dususte -25 gibi bir sayi oluyor ve
                // comelme katmanini sonuna kadar aciyordu: avatar 44 metre boyunca comelmis
                // bir heykel gibi iniyordu. Dususte oran "ayakta"ya sabitlenir.
                float ratio = falling ? 1f : ph / _standingH;
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
                // Dususten boy olcumu ALINMAZ: -42 m'lik bir ornek kalibrasyonu zehirler ve
                // oyuncu maci yanlis olceklenmis bir avatarla tamamlar.
                if (!_fitLocked)
                {
                    if (!falling) TickCalibration(ph);
                }
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
                transform.position = new Vector3(xz.x, groundY + bodyHeightOffset - fallDrop, xz.z);
            }
            else
            {
                // Legacy mode: pin the feet to the terrain (raycast), body under the head.
                // Varsayilan zemin dususu de HESABA KATAR: isin bir yuzey bulursa (asagida)
                // zaten dogru yeri yazar, bulamazsa oyuncu en azindan catida asili kalmaz.
                float g = groundY - fallDrop;
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

            // Yaw-follow the head past a deadzone so small head turns don't spin the torso.
            float curYaw = transform.eulerAngles.y;
            float targetYaw = headSource.eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(curYaw, targetYaw)) > yawDeadzone)
            {
                float newYaw = Mathf.MoveTowardsAngle(curYaw, targetYaw, yawSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Euler(0f, newYaw, 0f);
            }

            // Hand IK targets (controller pose + grip offset). Rotation is remapped through the
            // skeleton's own hand axes so the wrist follows the controller naturally.
            if (leftHandSource != null && ikLeftHandTarget != null)
            {
                Vector3 wrist = HandTargetPos(leftHandSource, true, leftGripPositionOffset);
                ikLeftHandTarget.SetPositionAndRotation(
                    wrist,
                    _leftRotOK ? HandRotation(leftHandSource, true, leftGripEulerOffset)
                               : leftHandSource.rotation * Quaternion.Euler(leftGripEulerOffset));
                DriveElbowHint(true, wrist);
            }

            if (rightHandSource != null && ikRightHandTarget != null)
            {
                Vector3 wrist = HandTargetPos(rightHandSource, false, rightGripPositionOffset);
                ikRightHandTarget.SetPositionAndRotation(
                    wrist,
                    _rightRotOK ? HandRotation(rightHandSource, false, rightGripEulerOffset)
                                : rightHandSource.rotation * Quaternion.Euler(rightGripEulerOffset));
                DriveElbowHint(false, wrist);
            }

            // Head: copy the HMD look direction onto the head bone (after the rig ran).
            if (driveHeadRotation && headBone != null)
                headBone.rotation = headSource.rotation * Quaternion.Euler(headEulerOffset);

            // First-person: collapse the local player's own head so it isn't in front of the camera.
            if (hideHead && headBone != null)
                headBone.localScale = new Vector3(0.001f, 0.001f, 0.001f);
        }
    }
}
