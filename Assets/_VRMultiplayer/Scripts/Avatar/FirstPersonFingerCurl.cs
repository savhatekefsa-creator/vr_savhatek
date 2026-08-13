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
        // Basparmagin kivrimi ARTIK SABIT ACI DEGIL: kurulumda hedefe SURULEREK
        // cozuluyor (bkz. SolveThumbPinch). Bu deger yalnizca cozum kurulamazsa yedek.
        public float thumbCurlDegrees = 30f;

        // Basparmak ucunun avuc duzleminden en az bu kadar onde kalmasi gerekir (m).
        // Mesh yaricapi ~12-15 mm; bunun altina inince el ic ice geciyor.
        const float ThumbClearance = 0.014f;

        // TAM KAVRAMADA BASPARMAK, ISARET PARMAGIYLA HALKA KAPATIR ("durbun").
        //
        // ACILAR CIHAZDA/ONIZLEMEDE SECILDI, hedefe surulerek COZULMUYOR. Once CCD
        // denendi (once serbest, sonra mentese kisitli) ve iki kez basarisiz oldu:
        //   - Serbest CCD ekseni kisitlamadigi icin araya burulma biniyor, mesh
        //     yamuluyordu. Cihazda goruldu.
        //   - Eksen kisiti burulmayi bitirdi ama YON yanlis kaldi, cunku menteseleri
        //     Cross(uzanim, avuc normali) diye ben turetiyordum. Olculdu: isaret
        //     parmaginin gercek bukum ekseni [0.98,-0.02,-0.18] (neredeyse saf yan
        //     eksen) ama BASPARMAGINKILER oyle degil - cmc [0.04,0.86,-0.51],
        //     mcp [0.02,0.50,-0.86], ip [-0.10,0.39,-0.92]. Yani basparmak kendi
        //     duzleminde katlanir, avuc duzlemine gore degil. Benim eksenlerim
        //     tamamen avuc duzlemindeydi, dolayisiyla basparmak isarete YANLIS
        //     TARAFTAN yaklasiyordu.
        //
        // Cozum: menteseler artik TURETILMIYOR, rig'in kendi anatomik eksen
        // isaretcilerinden okunuyor (thumb_cmc_fe / cmc_aa / mcp_fe / ip_fe). Isaretci
        // konvansiyonu isaret parmaginda dogrulandi: eksen = isaretcinin RIGHT vektoru.
        // Acilar da bu eksenler uzerinde secildi: uc-tirnak 11.9 mm, yastik-yastik
        // 20.0 mm. thumb3 hic dondurulmez - kendi orijinini oynatmaz, tek etkisi
        // burulmadir.
        // ip (uc eklem) bilincli olarak BUYUK: 13 derecede basparmak duz duruyordu.
        // Buyutunce uc tirnaktan kacar, o yuzden cmc/mcp yeniden cozuldu - tarama:
        // ip 13 -> 7.4 mm, ip 25 -> 2.9, ip 45 -> 0.4, ip 65 -> 8.0 (uc tirnagi gecer).
        const float ThumbCmcFlex = -73f;
        const float ThumbCmcAbduct = 1f;
        const float ThumbMcpFlex = 82f;
        const float ThumbIpFlex = 45f;

        // Ham eksen degerleri titrek; poser'daki ile ayni mertebede yumusatma.
        public float smoothing = 14f;
        public float triggerSmoothing = 60f;

        struct Phalanx
        {
            public Transform t;
            public Quaternion open, closed;
            public bool useTrigger;
            public bool thumb;
        }

        readonly List<Phalanx> _phalanges = new List<Phalanx>();
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

            _palmPoint = wrist.position;
            _palmNormal = curlPlane;

            // Dort parmak: kendi duzleminde katlanir. Isaret parmagi TETIGI izler.
            AddFinger(map, p + "index", wrist.position, curlPlane, true, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "middle", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "ring", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);
            AddFinger(map, p + "pinky", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl);

            // Basparmak: rig'in anatomik eksenlerinde sabit acilarla. Eksen isaretcileri
            // yoksa eski sabit-acili kurala dusulur (duzlem YOK, avuc uzerinden capraz).
            if (!BuildThumbPose(map, p))
            {
                Vector3 fallback = thumbTarget;
                Transform idx2;
                if (map.TryGetValue(p + "index2", out idx2)) fallback = Vector3.Lerp(fallback, idx2.position, 0.5f);
                fallback += curlPlane * 0.015f;
                AddFinger(map, p + "thumb", fallback, null, false,
                          thumbCurlDegrees, thumbCurlDegrees, thumbCurlDegrees * 0.8f, true);
            }

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
            if (_thumbTip == null) return;

            // Kaba tarama yeterli: 1.0'dan asagi inip kisiti saglayan ilk degeri al.
            for (float k = 1f; k > 0.05f; k -= 0.05f)
            {
                for (int i = 0; i < _phalanges.Count; i++)
                    if (_phalanges[i].thumb)
                        _phalanges[i].t.localRotation = Quaternion.Slerp(_phalanges[i].open, _phalanges[i].closed, k);
                if (Vector3.Dot(_thumbTip.position - _palmPoint, _palmNormal) >= ThumbClearance)
                {
                    _thumbLimit = k;
                    break;
                }
            }
            // Olcum icin bozulan pozu geri ac.
            for (int i = 0; i < _phalanges.Count; i++)
                if (_phalanges[i].thumb) _phalanges[i].t.localRotation = _phalanges[i].open;
        }

        /// <summary>Meta rig'inin deri isaretcileri "r_"/"l_" onekli (kemikler "b_r_").</summary>
        Transform Marker(Dictionary<string, Transform> map, string suffix)
        {
            Transform t;
            return map.TryGetValue((_left ? "l_" : "r_") + suffix, out t) ? t : null;
        }

        /// <summary>
        /// Basparmagin KAPALI pozunu kurar: rig'in kendi anatomik eksen isaretcileri
        /// etrafinda, yukaridaki sabit acilarla. Menteseler turetilmez - basparmagin
        /// bukum duzlemi avuc duzlemiyle ayni DEGIL, turetmeye calismak parmagi
        /// isarete yanlis taraftan yaklastiriyordu (bkz. sinif basi).
        ///
        /// Eksen = isaretcinin RIGHT vektoru. Bu konvansiyon isaret parmaginda
        /// dogrulandi: index_mcp_fe_axis.right = [0.98,-0.02,-0.18], yani parmagin
        /// gercekten katlandigi yan eksen.
        /// </summary>
        bool BuildThumbPose(Dictionary<string, Transform> map, string p)
        {
            Transform j0, j1, j2;
            map.TryGetValue(p + "thumb0", out j0);
            map.TryGetValue(p + "thumb1", out j1);
            map.TryGetValue(p + "thumb2", out j2);
            Transform cmcFe = Marker(map, "thumb_cmc_fe_axis_marker");
            Transform cmcAa = Marker(map, "thumb_cmc_aa_axis_marker");
            Transform mcpFe = Marker(map, "thumb_mcp_fe_axis_marker");
            Transform ipFe = Marker(map, "thumb_ip_fe_axis_marker");
            if (j0 == null || j1 == null || j2 == null ||
                cmcFe == null || cmcAa == null || mcpFe == null || ipFe == null) return false;

            AddThumbJoint(j0, new[] { cmcAa.right, cmcFe.right }, new[] { ThumbCmcAbduct, ThumbCmcFlex });
            AddThumbJoint(j1, new[] { mcpFe.right }, new[] { ThumbMcpFlex });
            AddThumbJoint(j2, new[] { ipFe.right }, new[] { ThumbIpFlex });
            return true;
        }

        /// <summary>
        /// Bir basparmak eklemini, verilen DUNYA eksenleri etrafinda verilen acilarla
        /// kapali poza koyar. Eksenler kemigin kendi uzayina cevrilir; boylece poz
        /// elin o andaki durusundan bagimsiz olur ve sol elde ayrica bir sey yapmak
        /// gerekmez - sol rig'in isaretcileri zaten aynalanmis gelir.
        /// </summary>
        void AddThumbJoint(Transform bone, Vector3[] worldAxes, float[] degrees)
        {
            Quaternion rest = bone.localRotation;
            Quaternion closed = rest;
            for (int i = 0; i < worldAxes.Length; i++)
            {
                Vector3 local = bone.InverseTransformDirection(worldAxes[i].normalized);
                if (local.sqrMagnitude < 1e-8f) continue;
                closed = closed * Quaternion.AngleAxis(degrees[i], local);
            }
            _phalanges.Add(new Phalanx { t = bone, open = rest, closed = closed, useTrigger = false, thumb = true });
        }

        void AddFinger(Dictionary<string, Transform> map, string prefix, Vector3 target,
                       Vector3? plane, bool useTrigger, float c1, float c2, float c3,
                       bool thumb = false)
        {
            Transform j1, j2, j3;
            map.TryGetValue(prefix + "1", out j1);
            map.TryGetValue(prefix + "2", out j2);
            map.TryGetValue(prefix + "3", out j3);
            if (j1 == null) return;

            // Uzanim yonu bir sonraki eklemden gelir; son bogumda onceki yon surdurulur.
            Vector3 e1 = j2 != null ? (j2.position - j1.position) : j1.forward;
            Add(j1, e1, target, plane, c1, useTrigger, thumb);
            if (j2 == null) return;
            Vector3 e2 = j3 != null ? (j3.position - j2.position) : e1;
            Add(j2, e2, target, plane, c2, useTrigger, thumb);
            if (j3 != null) Add(j3, e2, target, plane, c3, useTrigger, thumb);
        }

        void Add(Transform bone, Vector3 ext, Vector3 target, Vector3? plane, float deg,
                 bool useTrigger, bool thumb)
        {
            // Acik el = modelin KENDI dinlenme pozu. Meta'nin eli duz elle geliyor, yani
            // burada animatorun pozunu ayiklamak gerekmiyor (avatarda gerekiyordu).
            if (!FingerCurlMath.Solve(bone, ext, target, plane, deg, bone.localRotation,
                                      out Quaternion open, out Quaternion closed))
                return;
            _phalanges.Add(new Phalanx { t = bone, open = open, closed = closed, useTrigger = useTrigger, thumb = thumb });
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
                if (ph.thumb) k *= _thumbLimit;          // basparmak guvenlik kilidi
                ph.t.localRotation = Quaternion.Slerp(ph.open, ph.closed, k);
            }
        }
    }
}
