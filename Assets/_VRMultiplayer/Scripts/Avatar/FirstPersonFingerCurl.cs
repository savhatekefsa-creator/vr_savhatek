using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Birinci sahis elinin parmaklarini agdaki grip/tetik degerlerinden kivirir.
    ///
    /// Avatarin ellerini <see cref="ProceduralFingerPoser"/> kiviriyor ama o humanoid
    /// iskelete bagli (Animator + HumanBodyBones). Birinci sahis eli Meta'nin KENDI
    /// Generic rig'ini kullaniyor (b_r_index1...), o yuzden kemikleri isimle cozen ayri
    /// bir surucu gerekiyor. Kivrim KURALI paylasimli: <see cref="FingerCurlMath"/> -
    /// iki el ayni matematikle kivrilsin, kural tek yerde dursun.
    ///
    /// Grip -> dort parmak + basparmak. Tetik -> YALNIZ isaret parmagi. Boylece silaha
    /// dokunmadan da tetik parmagi ayri oynar.
    /// </summary>
    [DefaultExecutionOrder(120)]
    public class FirstPersonFingerCurl : MonoBehaviour
    {
        // ProceduralFingerPoser'in varsayilanlariyla ayni - iki el ayni miktarda kapansin.
        public float proximalCurl = 55f;
        public float intermediateCurl = 80f;
        public float distalCurl = 55f;
        // Basparmak digerlerinden AZ kivrilir: fazlasi ucu avucun icine sokuyor.
        public float thumbCurlDegrees = 30f;

        // Basparmak ucunun avuc duzleminden en az bu kadar onde kalmasi gerekir (m).
        // Mesh yaricapi ~12-15 mm; bunun altina inince el ic ice geciyor.
        const float ThumbClearance = 0.014f;
        // Hedefi avuc duzleminden disari kaydirma - basparmak avucun ICINE degil,
        // kivrilmis parmaklarin ONUNDEN gecsin.
        const float ThumbTargetLift = 0.015f;

        // Ham eksen degerleri titrek; poser'daki ile ayni mertebede yumusatma.
        public float smoothing = 14f;
        public float triggerSmoothing = 60f;

        struct Phalanx
        {
            public Transform t;
            public Quaternion open, closed;
            public bool useTrigger;
        }

        readonly List<Phalanx> _phalanges = new List<Phalanx>();
        int _thumbCount;                 // listenin bastaki bu kadar bogumu basparmak
        float _thumbLimit = 1f;          // guvenlik kilidi (bkz. LimitThumb)
        Transform _thumbTip;
        Vector3 _palmPoint, _palmNormal;
        NetworkVRPlayer _net;
        bool _left;
        float _grip, _trigger;
        bool _built;

        public void Init(bool left, NetworkVRPlayer net)
        {
            _left = left;
            _net = net;
            _built = Build();
        }

        bool Build()
        {
            string p = _left ? "b_l_" : "b_r_";
            var map = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name)) map[t.name] = t;

            Transform wrist, idx1, mid1, pky1;
            if (!map.TryGetValue(p + "wrist", out wrist) || !map.TryGetValue(p + "index1", out idx1)
                || !map.TryGetValue(p + "middle1", out mid1) || !map.TryGetValue(p + "pinky1", out pky1))
            {
                Debug.LogWarning("[FirstPersonFingerCurl] Meta el kemikleri bulunamadi (" + p + "*)");
                return false;
            }

            FingerCurlMath.PalmFrame(wrist, idx1, pky1, mid1, _left,
                out _, out Vector3 curlPlane, out Vector3 thumbTarget);

            // curlPlane iki elde de AVUCTAN DISARI bakar (PalmFrame elliligi zaten
            // duzeltiyor). Hedefi o yonde kaldirip biraz da isaret parmagina dogru
            // otelemek, basparmagi avucun ICINE degil ONUNDEN geciriyor.
            Transform idx2;
            map.TryGetValue(p + "index2", out idx2);
            if (idx2 != null) thumbTarget = Vector3.Lerp(thumbTarget, idx2.position, 0.5f);
            thumbTarget += curlPlane * ThumbTargetLift;
            _palmPoint = wrist.position;
            _palmNormal = curlPlane;

            // Basparmak: duzlem YOK (avucun uzerinden capraz gecer).
            AddFinger(map, p + "thumb", thumbTarget, null, false,
                      thumbCurlDegrees, thumbCurlDegrees, thumbCurlDegrees * 0.8f);
            _thumbCount = _phalanges.Count;   // ilk N bogum basparmaga ait
            // Dort parmak: kendi duzleminde katlanir. Isaret parmagi TETIGI izler.
            AddFinger(map, p + "index", wrist.position, curlPlane, true, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "middle", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "ring", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "pinky", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);

            map.TryGetValue(p + "thumb3", out _thumbTip);
            LimitThumb();
            return _phalanges.Count > 0;
        }

        /// <summary>
        /// SERT GUVENLIK KILIDI: basparmak kivrimini, ucu avuc duzleminden
        /// <see cref="ThumbClearance"/> kadar onde kalacak sekilde sinirlar.
        /// Aci ayari bozulsa bile mesh elin icine giremez - cihazda goruldugu gibi
        /// tam kavramada uc, avuc duzleminin 2 mm yakinina kadar iniyordu.
        /// Kilit BIR KEZ, kurulumda hesaplanir; her karede olcum yapilmaz.
        /// </summary>
        void LimitThumb()
        {
            _thumbLimit = 1f;
            if (_thumbTip == null || _thumbCount == 0) return;

            // Kaba tarama yeterli: 1.0'dan asagi inip kisiti saglayan ilk degeri al.
            for (float k = 1f; k > 0.05f; k -= 0.05f)
            {
                for (int i = 0; i < _thumbCount; i++)
                    _phalanges[i].t.localRotation = Quaternion.Slerp(_phalanges[i].open, _phalanges[i].closed, k);
                if (Vector3.Dot(_thumbTip.position - _palmPoint, _palmNormal) >= ThumbClearance)
                {
                    _thumbLimit = k;
                    break;
                }
            }
            // Olcum icin bozulan pozu geri ac.
            for (int i = 0; i < _thumbCount; i++)
                _phalanges[i].t.localRotation = _phalanges[i].open;
        }

        void AddFinger(Dictionary<string, Transform> map, string prefix, Vector3 target,
                       Vector3? plane, bool useTrigger, float c1, float c2, float c3)
        {
            Transform j1, j2, j3;
            map.TryGetValue(prefix + "1", out j1);
            map.TryGetValue(prefix + "2", out j2);
            map.TryGetValue(prefix + "3", out j3);
            if (j1 == null) return;

            // Uzanim yonu bir sonraki eklemden gelir; son bogumda onceki yon surdurulur.
            Vector3 e1 = j2 != null ? (j2.position - j1.position) : j1.forward;
            Add(j1, e1, target, plane, c1, useTrigger);
            if (j2 == null) return;
            Vector3 e2 = j3 != null ? (j3.position - j2.position) : e1;
            Add(j2, e2, target, plane, c2, useTrigger);
            if (j3 != null) Add(j3, e2, target, plane, c3, useTrigger);
        }

        void Add(Transform bone, Vector3 ext, Vector3 target, Vector3? plane, float deg, bool useTrigger)
        {
            // Acik el = modelin KENDI dinlenme pozu. Meta'nin eli duz elle geliyor, yani
            // burada animatorun pozunu ayiklamak gerekmiyor (avatarda gerekiyordu).
            if (!FingerCurlMath.Solve(bone, ext, target, plane, deg, bone.localRotation,
                                      out Quaternion open, out Quaternion closed))
                return;
            _phalanges.Add(new Phalanx { t = bone, open = open, closed = closed, useTrigger = useTrigger });
        }

        void LateUpdate()
        {
            if (!_built || _net == null) return;

            float gripTarget = _left ? _net.LeftGrip01 : _net.RightGrip01;
            float trigTarget = _left ? _net.LeftTrigger01 : _net.RightTrigger01;
            _grip = Mathf.Lerp(_grip, gripTarget, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
            _trigger = Mathf.Lerp(_trigger, trigTarget, 1f - Mathf.Exp(-triggerSmoothing * Time.deltaTime));
            Apply(_grip, _trigger);
        }

        /// <summary>
        /// Kivrimi dogrudan uygular (0..1). Yumusatmadan AYRI durmasi bilincli: olcum
        /// kosumu bunu cagirip sonucu okuyabiliyor. Yumusatma icinden olcmeye calismak
        /// yaniltir - degerler her karede agdaki gercek degere geri cekilir.
        /// </summary>
        public void Apply(float grip, float trigger)
        {
            // Isaret parmagi: grip VE tetigin buyugu. Yalnizca tetige baglasaydik yumruk
            // yaparken isaret parmagi dimdik havada kalirdi; yalnizca grip'e baglasaydik
            // tetik ayri oynayamazdi.
            float index = Mathf.Max(grip, trigger);
            for (int i = 0; i < _phalanges.Count; i++)
            {
                var ph = _phalanges[i];
                float k = ph.useTrigger ? index : grip;
                if (i < _thumbCount) k *= _thumbLimit;   // basparmak guvenlik kilidi
                ph.t.localRotation = Quaternion.Slerp(ph.open, ph.closed, k);
            }
        }
    }
}
