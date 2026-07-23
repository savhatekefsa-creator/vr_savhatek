using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.Weapons;

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
    ///
    /// Weapon holds can OVERRIDE a hand with a <see cref="WeaponGripProfile"/> hand pose (ISDK
    /// "Fingers Freedom" equivalent): thumb/middle/ring/pinky lock to the authored curls, the
    /// index finger optionally stays Free and follows the networked trigger axis. With no
    /// override set the behaviour is exactly the legacy grip-driven curl.
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
        [Tooltip("El bileklerini yuvarlar (avuc ici avatar ONUNE/ICE baksin). 0 = kapali. Negatif = ters yon.")]
        public float palmRollDegrees = 16f;

        [Tooltip("Parmaklar hala ters gelirse isaretle (tum yonu tersine cevirir).")]
        public bool invertCurl = false;
        public float smoothing = 14f;
        [Tooltip("Tetik parmagi icin AYRI (cok daha hizli) yumusatma. Tetik cekisi anlik hissedilmeli; genel poz yumusatmasi (smoothing) burada gecikme yaratir. Yalnizca authored pozda kullanilir.")]
        public float triggerSmoothing = 60f;
        [Tooltip("Silah tutarken avucun tam kapanmasi icin grip bu degere clamp'lenir.")]
        public float heldGripMin = 0.85f;

        [Tooltip("Bilek roll yonu: 0 = otomatik, +1/-1 = elle sabitle (bir el ters donuyorsa degistir).")]
        public int rollSignOverrideLeft = 0;
        public int rollSignOverrideRight = 0;

        [Tooltip("Eksen/poz yakalamasini bu kadar kare ertele ki idle animasyonu + kol IK'si otursun.")]
        public int captureDelayFrames = 3;

        Animator _anim;
        NetworkVRPlayer _net;
        HandGrabber _grab;

        class Phalanx
        {
            public Transform t;
            public Quaternion open, closed;
            public bool useTrigger;
            public int finger;              // 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky
        }

        readonly List<Phalanx> _left = new List<Phalanx>();
        readonly List<Phalanx> _right = new List<Phalanx>();
        readonly Dictionary<Transform, Quaternion> _restPose = new Dictionary<Transform, Quaternion>();
        Transform _wristL, _wristR;
        Vector3 _rollAxisL, _rollAxisR;
        float _rollSignL = 1f, _rollSignR = 1f;
        float _gL, _gR, _tL, _tR;
        int _framesUntilBuild;

        // Weapon-hold overrides (per hand). Store-only setters: safe to call before Start.
        WeaponGripProfile _ovrProfileL, _ovrProfileR;
        bool _ovrSupportL, _ovrSupportR;
        readonly float[] _fingersL = new float[5];
        readonly float[] _fingersR = new float[5];

        // Authored pozun CANLI durumu. Kemikten okumak yerine burada tutulmasi sart: Animator
        // (idle klibi) her kare parmak kemiklerine kendi pozunu yaziyor, dolayisiyla
        // "Slerp(bone.localRotation, hedef, k)" her kare animatorun pozundan yeniden basliyor,
        // birikmiyor ve hedefe HIC varmiyor (parmaklar titrer ve duz kalir). Durumu kendimiz
        // tutup kemige MUTLAK yazinca animatorun yazdigi degerin onemi kalmaz.
        readonly Quaternion[] _authL = new Quaternion[HandPoseBones.JointCount];
        readonly Quaternion[] _authR = new Quaternion[HandPoseBones.JointCount];
        bool _authSeededL, _authSeededR;

        void Start()
        {
            _anim = GetComponent<Animator>();
            if (_anim == null) _anim = GetComponentInParent<Animator>();
            _net = GetComponentInParent<NetworkVRPlayer>();
            _grab = GetComponentInParent<HandGrabber>();
            if (_anim == null || !_anim.isHuman) { enabled = false; return; }

            // TWO-PHASE capture. Phase 1 (now, before the first animation eval): snapshot every
            // finger bone's REST local rotation — the FBX T-pose, straight and mirror-symmetric.
            // The idle clip poses fingers asymmetrically (right hand half-curled), so a live
            // snapshot must never be used as the "open hand".
            foreach (HumanBodyBones b in FingerBones())
            {
                var t = Bone(b);
                if (t != null) _restPose[t] = t.localRotation;
            }

            // Phase 2 is DEFERRED to LateUpdate: hinge axes / roll signs need the SETTLED pose
            // (idle + arm IK), because on the spawn frame the geometry can still be contorted.
            _framesUntilBuild = Mathf.Max(1, captureDelayFrames);
        }

        static IEnumerable<HumanBodyBones> FingerBones()
        {
            for (var b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++)
                yield return b;
        }

        /// <summary>Lock this hand's fingers to the profile's static curls (main grip or support
        /// rail set). The index finger keeps following the networked trigger when the profile
        /// says so. Passing a null profile is the same as <see cref="ClearHandOverride"/>.</summary>
        public void SetHandOverride(bool leftHand, WeaponGripProfile profile, bool isSupportHand)
        {
            if (leftHand)
            {
                _ovrProfileL = profile;
                _ovrSupportL = isSupportHand;
                SeedFingers(_fingersL, _gL, _tL);
                _authSeededL = false; // yeni tutus -> authored blend mevcut pozdan yeniden basla
            }
            else
            {
                _ovrProfileR = profile;
                _ovrSupportR = isSupportHand;
                SeedFingers(_fingersR, _gR, _tR);
                _authSeededR = false;
            }
        }

        /// <summary>Back to the grip-driven procedural curl for that hand.</summary>
        public void ClearHandOverride(bool leftHand)
        {
            if (leftHand) { _ovrProfileL = null; _authSeededL = false; }
            else { _ovrProfileR = null; _authSeededR = false; }
        }

        // Start the override blend from the hand's current curl so the transition is continuous.
        static void SeedFingers(float[] fingers, float grip, float trigger)
        {
            for (int i = 0; i < fingers.Length; i++)
                fingers[i] = i == 1 ? Mathf.Max(grip, trigger) : grip;
        }

        // Eksenler T-POSE'dan olculur, canli pozdan DEGIL. Bu kritik: olcum karesine gelindiginde
        // idle klibi parmaklari coktan kivirmis oluyor (sag el yariya kadar), ve kivrik bir
        // parmakta uzama yonu (i.position - p.position) T-pose'dakinden bambaska bir yeri
        // gosteriyor. Menteşe ekseni oradan turetilince capraz cikiyor, ama `open` T-pose'dan
        // geliyor — yani T-pose'u YANLIS eksen etrafinda donduruyorduk ve parmak yana/asagi
        // capraz kapaniyordu. Kemikleri gecici olarak rest rotasyonuna alip olcuyoruz; geri
        // koymak da bedava, Apply() hemen ardindan localRotation'lari zaten yeniden yaziyor.
        void BuildHands()
        {
            var live = new Dictionary<Transform, Quaternion>(_restPose.Count);
            foreach (var kv in _restPose)
            {
                if (kv.Key == null) continue;
                live[kv.Key] = kv.Key.localRotation;
                kv.Key.localRotation = kv.Value;
            }

            BuildHand(true, _left, ref _wristL, ref _rollAxisL, ref _rollSignL);
            BuildHand(false, _right, ref _wristR, ref _rollAxisR, ref _rollSignR);

            foreach (var kv in live)
                if (kv.Key != null) kv.Key.localRotation = kv.Value;
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
            // Analytic: Dot(fingersDir x palmNormal, fwd) is the derivative of that tilt, so unlike
            // the old ±10° sampling a near-perpendicular pose can't flip one hand's sign. If even
            // the derivative is degenerate, use the mirror pair; a per-hand override beats both.
            wristOut = wrist;
            rollAxisOut = wrist.InverseTransformDirection(fingersDir).normalized;
            float toward = Vector3.Dot(Vector3.Cross(fingersDir, palmNormal), transform.forward);
            rollSignOut = Mathf.Abs(toward) > 0.05f ? Mathf.Sign(toward) : (left ? 1f : -1f);
            int overrideSign = left ? rollSignOverrideLeft : rollSignOverrideRight;
            if (overrideSign != 0) rollSignOut = Mathf.Sign(overrideSign);

            Vector3 thumbTarget = (idxP.position + midP.position) * 0.5f; // across the palm

            // Palm-FACING direction for the curl planes. sideDir (index→little) makes the raw
            // cross face out of the right palm but out of the LEFT hand's back — flip it there.
            Vector3 curlPlaneNormal = left ? -palmNormal : palmNormal;

            AddFinger(outList, left, thumbTarget, 0, false, null,
                HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
                HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
                thumbCurl, thumbCurl, thumbCurl * 0.8f);
            AddFinger(outList, left, wrist.position, 1, true, curlPlaneNormal,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, 2, false, curlPlaneNormal,
                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, 3, false, curlPlaneNormal,
                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                proximalCurl, intermediateCurl, distalCurl);
            AddFinger(outList, left, wrist.position, 4, false, curlPlaneNormal,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
                HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
                proximalCurl, intermediateCurl, distalCurl);
        }

        void AddFinger(List<Phalanx> outList, bool left, Vector3 target, int finger, bool useTrigger,
            Vector3? planeNormal,
            HumanBodyBones lP, HumanBodyBones lI, HumanBodyBones lD,
            HumanBodyBones rP, HumanBodyBones rI, HumanBodyBones rD,
            float cP, float cI, float cD)
        {
            Transform p = Bone(left ? lP : rP);
            Transform i = Bone(left ? lI : rI);
            Transform d = Bone(left ? lD : rD);
            if (p == null) return;

            Vector3 pExt = i != null ? (i.position - p.position) : p.forward;
            AddPhalanx(outList, p, pExt, target, cP, finger, useTrigger, planeNormal);
            if (i != null)
            {
                Vector3 iExt = d != null ? (d.position - i.position) : pExt;
                AddPhalanx(outList, i, iExt, target, cI, finger, useTrigger, planeNormal);
                if (d != null)
                    AddPhalanx(outList, d, iExt, target, cD, finger, useTrigger, planeNormal);
            }
        }

        void AddPhalanx(List<Phalanx> outList, Transform bone, Vector3 extWorld, Vector3 target,
            float curlDeg, int finger, bool useTrigger, Vector3? planeNormal)
        {
            if (bone == null || bone.parent == null) return;

            // FOUR FINGERS (planeNormal set): hinge = Cross(extension, palm normal) — every
            // finger folds parallel in its OWN plane like a real fist. Curling them toward one
            // shared point made the fingers converge into the palm centre and clip.
            // THUMB (planeNormal null): hinge = Cross(extension, toTarget) — a positive angle
            // bends it straight toward the target, sweeping ACROSS the palm.
            Vector3 hinge = planeNormal.HasValue
                ? Vector3.Cross(extWorld.normalized, planeNormal.Value)
                : Vector3.Cross(extWorld.normalized, (target - bone.position).normalized);
            if (hinge.sqrMagnitude < 1e-6f) return;
            Vector3 axisParent = bone.parent.InverseTransformDirection(hinge.normalized).normalized;

            float deg = curlDeg * (invertCurl ? -1f : 1f);
            // Open pose = the REST snapshot (T-pose), not the live pose: by capture time the idle
            // clip has already curled some fingers and that curl must not leak into "open".
            Quaternion open = _restPose.TryGetValue(bone, out var rest) ? rest : bone.localRotation;
            outList.Add(new Phalanx
            {
                t = bone,
                open = open,
                closed = Quaternion.AngleAxis(deg, axisParent) * open,
                useTrigger = useTrigger,
                finger = finger,
            });
        }

        Transform Bone(HumanBodyBones b) => _anim.GetBoneTransform(b);

        // Authored poz yolu icin kemik cache'i: ApplyAuthored her kare her eklem icin
        // Animator.GetBoneTransform cagiriyordu (el basina ~15 engine cagrisi, her oyuncu,
        // her kare). Iskelet spawn'dan sonra degismez — bir kez cozulur. (Legacy yol zaten
        // Phalanx.t ile cache'liydi; authored yol ayni muameleyi hic gormemisti.)
        Transform[] _authBonesL, _authBonesR;

        Transform[] AuthoredBones(bool left)
        {
            var arr = left ? _authBonesL : _authBonesR;
            if (arr == null)
            {
                arr = new Transform[HandPoseBones.JointCount];
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                    arr[j] = Bone(HandPoseBones.Bone(j, left));
                if (left) _authBonesL = arr; else _authBonesR = arr;
            }
            return arr;
        }

        void LateUpdate()
        {
            // Runs after AvatarIKController (execution order 100), so on the capture frame the
            // idle pose + arm IK of this frame are already final — a sane pose to measure from.
            if (_framesUntilBuild > 0 && --_framesUntilBuild == 0) BuildHands();
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
            if (_wristR != null && Mathf.Abs(palmRollDegrees) > 0.01f)
                _wristR.localRotation = _wristR.localRotation * Quaternion.AngleAxis(palmRollDegrees * _rollSignR, _rollAxisR);

            float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            // Tetik ekseni cok daha hizli takip edilir: parmak tetikle birlikte ANLIK hareket
            // etmeli. Sifir yumusatma da olmaz — ag uzerinden gelen deger adim adim geliyor ve
            // uzaktaki oyuncularda zipliyor; bu kadari adimlari yutar ama gecikme hissettirmez.
            float kTrig = 1f - Mathf.Exp(-Mathf.Max(smoothing, triggerSmoothing) * Time.deltaTime);
            _gL = Mathf.Lerp(_gL, gL, k); _gR = Mathf.Lerp(_gR, gR, k);
            _tL = Mathf.Lerp(_tL, tL, kTrig); _tR = Mathf.Lerp(_tR, tR, kTrig);

            UpdateFingerTargets(_fingersL, _ovrProfileL, _ovrSupportL, _tL, k);
            UpdateFingerTargets(_fingersR, _ovrProfileR, _ovrSupportR, _tR, k);

            ApplyHand(true, _left, _gL, _tL, _ovrProfileL, _ovrSupportL, _fingersL, k);
            ApplyHand(false, _right, _gR, _tR, _ovrProfileR, _ovrSupportR, _fingersR, k);
        }

        // Profilde bu EL icin authored poz varsa o kazanir; yoksa eski prosedurel curl.
        void ApplyHand(bool left, List<Phalanx> hand, float grip, float trigger,
            WeaponGripProfile profile, bool isSupport, float[] fingers, float k)
        {
            if (profile != null)
            {
                var pose = isSupport ? profile.supportHand : profile.mainHand;
                var fp = pose.Fingers(left);
                if (fp.HasPose) { ApplyAuthored(left, fp, pose.indexFollowsTrigger, trigger, k); return; }
            }
            Apply(hand, grip, trigger, profile != null, fingers);
        }

        // Kaydedilmis lokal rotasyonlari yazar — eksen turetme, curl, tahmin YOK.
        // Yumusatma kendi durumumuz uzerinde yapilir, kemikten OKUYARAK degil (bkz. _authL/_authR):
        // animator her kare kemige kendi pozunu yazdigi icin geri-okuma hedefe hic ulasmaz.
        void ApplyAuthored(bool left, WeaponGripProfile.FingerPose fp, bool indexFollowsTrigger,
            float trigger, float k)
        {
            var cur = left ? _authL : _authR;
            bool seeded = left ? _authSeededL : _authSeededR;
            bool pulled = indexFollowsTrigger && fp.HasIndexPulled;
            var bones = AuthoredBones(left);

            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var t = bones[j];
                if (t == null) continue;

                Quaternion target = fp.joints[j];

                // Isaret parmagi: birakili poz -> cekili poz arasi, ag uzerinden gelen tetik
                // ekseniyle. Uzaktakiler de tetik parmaginin hareketini gorur.
                if (pulled && HandPoseBones.IsIndex(j))
                {
                    target = Quaternion.Slerp(target, fp.indexPulled[j - HandPoseBones.IndexFirst], trigger);
                    // Poz yumusatmasini ATLA ve dogrudan yaz: tetik ekseni zaten kendi hizli
                    // filtresinden geciyor, uzerine bir de poz yumusatmasi binince tetik cekisi
                    // gecikmeli hissediliyor. Parmak tetikle ayni anda hareket etmeli.
                    cur[j] = target;
                    t.localRotation = target;
                    continue;
                }

                // Ilk kare: animatorun o anki pozundan tohumla ki silahi kavrarken pop olmasin.
                if (!seeded) cur[j] = t.localRotation;
                cur[j] = Quaternion.Slerp(cur[j], target, k);
                t.localRotation = cur[j];
            }

            if (left) _authSeededL = true; else _authSeededR = true;
        }

        // Ease each overridden finger toward its authored curl; the index finger optionally
        // follows the (already network-replicated) trigger axis, so remote players see the
        // trigger finger move too.
        static void UpdateFingerTargets(float[] fingers, WeaponGripProfile profile, bool isSupport,
            float trigger, float k)
        {
            if (profile == null) return;
            var pose = isSupport ? profile.supportHand : profile.mainHand;
            for (int f = 0; f < 5; f++)
            {
                float target = pose.Curl(f);
                if (f == 1 && pose.indexFollowsTrigger)
                {
                    // Trigger pull is a SMALL travel — cap the full-pull curl so the finger
                    // squeezes the trigger instead of balling into a fist. 0 = legacy asset
                    // without the field -> behave like 1 (uncapped).
                    float max = pose.indexTriggerMaxCurl > 0f ? pose.indexTriggerMaxCurl : 1f;
                    target = Mathf.Lerp(target, Mathf.Max(target, max), trigger);
                }
                fingers[f] = Mathf.Lerp(fingers[f], target, k);
            }
        }

        void Apply(List<Phalanx> hand, float grip, float trigger, bool overridden, float[] fingers)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                var ph = hand[i];
                if (ph.t == null) continue;
                float raw = overridden
                    ? fingers[ph.finger]
                    : (ph.useTrigger ? Mathf.Max(grip, trigger) : grip);
                ph.t.localRotation = Quaternion.Slerp(ph.open, ph.closed, Mathf.SmoothStep(0f, 1f, raw));
            }
        }
    }
}
