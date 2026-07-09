using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Curls the humanoid finger bones toward a fist from the NETWORKED grip/trigger values, so
    /// every client sees hands open, close and grip. Writes bone.localRotation directly, AFTER the
    /// arm IK (DefaultExecutionOrder 100).
    ///
    /// Robust curl: each phalanx rotates around Cross(extension, toTarget) — a positive rotation
    /// ALWAYS bends the tip toward its target, so there is no left/right or per-model sign guess.
    /// Fingers target the wrist (into the palm); the thumb targets the index/middle base (it comes
    /// across the palm). A palm roll (auto-signed so both palms face the same way) tilts the wrist.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ProceduralFingerPoser : MonoBehaviour
    {
        [Header("Curl per phalanx (degrees)")]
        public float proximalCurl = 55f;
        public float intermediateCurl = 80f;
        public float distalCurl = 55f;
        public float thumbCurl = 40f;

        [Header("Palm orientation")]
        [Tooltip("SOL el bilek yuvarlamasi (avuc ici avatar ONUNE/ICE baksin). 0 = kapali. Negatif = ters yon.")]
        public float palmRollDegrees = 16f;
        [Tooltip("SAG el bilek yuvarlamasi. Sag avuc sola uymuyorsa BUNU oynat: negatif, 0 veya daha buyuk dene; sol el ile ayni gorunene kadar ayarla.")]
        public float palmRollDegreesRight = 16f;

        [Tooltip("Parmaklar hala ters gelirse isaretle (tum yonu tersine cevirir).")]
        public bool invertCurl = false;
        public float smoothing = 14f;
        [Tooltip("Silah tutarken avucun tam kapanmasi icin grip bu degere clamp'lenir.")]
        public float heldGripMin = 0.85f;

        Animator _anim;
        NetworkVRPlayer _net;
        HandGrabber _grab;

        class Phalanx
        {
            public Transform t;
            public Quaternion open, closed;
            public bool useTrigger;
        }

        readonly List<Phalanx> _left = new List<Phalanx>();
        readonly List<Phalanx> _right = new List<Phalanx>();
        Transform _wristL, _wristR;
        Vector3 _rollAxisL, _rollAxisR;
        float _rollSignL = 1f, _rollSignR = 1f;
        float _gL, _gR, _tL, _tR;

        void Start()
        {
            _anim = GetComponent<Animator>();
            if (_anim == null) _anim = GetComponentInParent<Animator>();
            _net = GetComponentInParent<NetworkVRPlayer>();
            _grab = GetComponentInParent<HandGrabber>();
            if (_anim == null || !_anim.isHuman) { enabled = false; return; }

            BuildHand(true, _left, ref _wristL, ref _rollAxisL, ref _rollSignL);
            BuildHand(false, _right, ref _wristR, ref _rollAxisR, ref _rollSignR);
        }

        void BuildHand(bool left, List<Phalanx> outList, ref Transform wristOut,
            ref Vector3 rollAxisOut, ref float rollSignOut)
        {
            Transform wrist = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform idxP = Bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform litP = Bone(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            Transform midP = Bone(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            if (wrist == null || idxP == null || litP == null || midP == null) return;

            Vector3 fingersDir = (midP.position - wrist.position).normalized;
            Vector3 sideDir = (idxP.position - litP.position).normalized;
            Vector3 palmNormal = Vector3.Cross(fingersDir, sideDir).normalized;

            // Palm roll about the finger-pointing axis; sign chosen so the palm tilts toward the
            // avatar's forward (reads as "inward"), the same visual way on both hands.
            wristOut = wrist;
            rollAxisOut = wrist.InverseTransformDirection(fingersDir).normalized;
            Vector3 fwd = transform.forward;
            Vector3 plus = Quaternion.AngleAxis(10f, fingersDir) * palmNormal;
            Vector3 minus = Quaternion.AngleAxis(-10f, fingersDir) * palmNormal;
            rollSignOut = Vector3.Dot(plus, fwd) >= Vector3.Dot(minus, fwd) ? 1f : -1f;

            // Thumb intentionally NOT curled — only the 4 fingers close on grip (the thumb stays
            // out, resting along the weapon). Reads more natural and matches the request.
            AddFinger(outList, left, wrist.position, true,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, false,
                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, false,
                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, false,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
                HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
                proximalCurl, intermediateCurl, distalCurl);
        }

        void AddFinger(List<Phalanx> outList, bool left, Vector3 target, bool useTrigger,
            HumanBodyBones lP, HumanBodyBones lI, HumanBodyBones lD,
            HumanBodyBones rP, HumanBodyBones rI, HumanBodyBones rD,
            float cP, float cI, float cD)
        {
            Transform p = Bone(left ? lP : rP);
            Transform i = Bone(left ? lI : rI);
            Transform d = Bone(left ? lD : rD);
            if (p == null) return;

            Vector3 pExt = i != null ? (i.position - p.position) : p.forward;
            AddPhalanx(outList, p, pExt, target, cP, useTrigger);
            if (i != null)
            {
                Vector3 iExt = d != null ? (d.position - i.position) : pExt;
                AddPhalanx(outList, i, iExt, target, cI, useTrigger);
                if (d != null)
                    AddPhalanx(outList, d, iExt, target, cD, useTrigger);
            }
        }

        void AddPhalanx(List<Phalanx> outList, Transform bone, Vector3 extWorld, Vector3 target,
            float curlDeg, bool useTrigger)
        {
            if (bone == null || bone.parent == null) return;

            // Rotate around Cross(extension, toTarget): a POSITIVE angle bends the tip straight
            // toward the target. No sign guessing — correct for both hands and the thumb.
            Vector3 toTarget = (target - bone.position).normalized;
            Vector3 hinge = Vector3.Cross(extWorld.normalized, toTarget);
            if (hinge.sqrMagnitude < 1e-6f) return;
            Vector3 axisParent = bone.parent.InverseTransformDirection(hinge.normalized).normalized;

            float deg = curlDeg * (invertCurl ? -1f : 1f);
            outList.Add(new Phalanx
            {
                t = bone,
                open = bone.localRotation,
                closed = Quaternion.AngleAxis(deg, axisParent) * bone.localRotation,
                useTrigger = useTrigger,
            });
        }

        Transform Bone(HumanBodyBones b) => _anim.GetBoneTransform(b);

        void LateUpdate()
        {
            if (_left.Count == 0 && _right.Count == 0) return;

            float gL = 0f, gR = 0f, tL = 0f, tR = 0f;
            if (_net != null)
            {
                gL = _net.LeftGrip01; gR = _net.RightGrip01;
                tL = _net.LeftTrigger01; tR = _net.RightTrigger01;
            }
            if (_grab != null)
            {
                if (_grab.HoldingLeft) gL = Mathf.Max(gL, heldGripMin);
                if (_grab.HoldingRight) gR = Mathf.Max(gR, heldGripMin);
            }

            if (_wristL != null && Mathf.Abs(palmRollDegrees) > 0.01f)
                _wristL.localRotation = _wristL.localRotation * Quaternion.AngleAxis(palmRollDegrees * _rollSignL, _rollAxisL);
            if (_wristR != null && Mathf.Abs(palmRollDegreesRight) > 0.01f)
                _wristR.localRotation = _wristR.localRotation * Quaternion.AngleAxis(palmRollDegreesRight * _rollSignR, _rollAxisR);

            float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _gL = Mathf.Lerp(_gL, gL, k); _gR = Mathf.Lerp(_gR, gR, k);
            _tL = Mathf.Lerp(_tL, tL, k); _tR = Mathf.Lerp(_tR, tR, k);

            Apply(_left, _gL, _tL);
            Apply(_right, _gR, _tR);
        }

        void Apply(List<Phalanx> hand, float grip, float trigger)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                var ph = hand[i];
                if (ph.t == null) continue;
                float raw = ph.useTrigger ? Mathf.Max(grip, trigger) : grip;
                ph.t.localRotation = Quaternion.Slerp(ph.open, ph.closed, Mathf.SmoothStep(0f, 1f, raw));
            }
        }
    }
}
