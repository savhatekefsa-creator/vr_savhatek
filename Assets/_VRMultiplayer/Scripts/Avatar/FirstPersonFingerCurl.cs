using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// Birinci sahis elinin parmaklarini kivirir. Iki kaynak var:
    ///
    /// 1. SILAH TUTARKEN: silahin profilindeki AUTHORED tutus pozu (tek kaynak - avatarin
    ///    elleri de ayni profili kullaniyor, bkz. <see cref="ProceduralFingerPoser"/>).
    ///    Poz avatarin humanoid kemiklerinde yazildigi icin dogrudan tasinamaz; editorde
    ///    mentese acisina cevrilip profile yazilir (menu 50, FpGripPoseBake) ve burada
    ///    FP rig'inin kendi eksenlerine uygulanir.
    /// 2. BOS EL (veya poz cevrilmemis silah): agdaki grip/tetik degerlerinden prosedurel
    ///    kivrim. Kural paylasimli: <see cref="FingerCurlMath"/>.
    ///
    /// Avatarin ellerini ayri bir surucu kiviriyor cunku o humanoid iskelete bagli
    /// (Animator + HumanBodyBones); FP eli Meta'nin Generic rig'ini kullaniyor
    /// (b_r_index1...), kemikleri isimle cozmek gerekiyor.
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
            /// <summary>0 bas, 1 isaret, 2 orta, 3 yuzuk, 4 serce. Parmak BASINA kivrim
            /// (atolyede elle yazilan fpCurls) bunu kullaniyor.</summary>
            public int finger;
        }

        readonly List<Phalanx> _phalanges = new List<Phalanx>();

        // ---- Silah tutus pozu (authored) ----
        // 15 eklem, HandPoseBones sirasinda. Humanoid basparmak 3 bogum, Meta'da 4 var
        // (thumb0 = avuc ici kok): Proximal->thumb0, Intermediate->thumb1, Distal->thumb2.
        // Meta thumb3 dondurulmez. Sira FpGripPoseBake ile AYNI olmali.
        static readonly string[] PoseBone =
        {
            "thumb0", "thumb1", "thumb2",
            "index1", "index2", "index3",
            "middle1", "middle2", "middle3",
            "ring1", "ring2", "ring3",
            "pinky1", "pinky2", "pinky3",
        };
        Transform[] _poseBone;
        Quaternion[] _poseRest;
        WeaponGripProfile _weaponProfile;
        bool _weaponSupport;
        float _weaponWeight;
        // Silaha yapisma/birakma tek karede olursa parmaklar ziplar - weld ile ayni harman.
        const float WeaponBlendSeconds = 0.12f;

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
            AddFinger(map, p + "index", wrist.position, curlPlane, true, proximalCurl, intermediateCurl, distalCurl, false, 1);
            AddFinger(map, p + "middle", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl, false, 2);
            AddFinger(map, p + "ring", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl, false, 3);
            AddFinger(map, p + "pinky", wrist.position, curlPlane, false, proximalCurl, intermediateCurl, distalCurl, false, 4);

            // Basparmak: rig'in anatomik eksenlerinde sabit acilarla. Eksen isaretcileri
            // yoksa eski sabit-acili kurala dusulur (duzlem YOK, avuc uzerinden capraz).
            if (!BuildThumbPose(map, p))
            {
                Vector3 fallback = thumbTarget;
                Transform idx2;
                if (map.TryGetValue(p + "index2", out idx2)) fallback = Vector3.Lerp(fallback, idx2.position, 0.5f);
                fallback += curlPlane * 0.015f;
                AddFinger(map, p + "thumb", fallback, null, false,
                          thumbCurlDegrees, thumbCurlDegrees, thumbCurlDegrees * 0.8f, true, 0);
            }

            map.TryGetValue(p + "thumb3", out _thumbTip);
            LimitThumb();
            BuildPoseJoints(map, p);
            BuildIndexHinges();
            return _phalanges.Count > 0;
        }

        // Tetigin isaret parmagina EKLEYECEGI kivrimin ekseni ve olcegi. Menteseler
        // TryGetPoseJoints ile ayni sekilde geri okunuyor (yeni eksen turetilmiyor).
        readonly Vector3[] _indexHinge = new Vector3[HandPoseBones.IndexJointCount];
        readonly float[] _indexRefFlex = new float[HandPoseBones.IndexJointCount];
        bool _indexHingesReady;

        /// <summary>Tam tetik cekisinin, bogumun DOGAL kapanmasinin ne kadarini harcadigi.
        /// Tetik kucuk bir harekettir; 1.0 yumruk yapardi. Silah basina
        /// <see cref="WeaponGripProfile.HandPose.indexTriggerMaxCurl"/> ile olceklenir.</summary>
        const float TriggerFlexFraction = 0.30f;

        void BuildIndexHinges()
        {
            _indexHingesReady = false;
            int k = 0;
            for (int i = 0; i < _phalanges.Count && k < HandPoseBones.IndexJointCount; i++)
            {
                var ph = _phalanges[i];
                if (ph.finger != 1) continue;   // 1 = isaret
                Quaternion d = ph.closed * Quaternion.Inverse(ph.open);
                d.ToAngleAxis(out float deg, out Vector3 axis);
                if (deg < 0.01f || axis.sqrMagnitude < 1e-8f) return;
                _indexHinge[k] = axis.normalized;   // EBEVEYN uzayinda, + = kapanma
                _indexRefFlex[k] = deg;
                k++;
            }
            _indexHingesReady = k == HandPoseBones.IndexJointCount;
        }

        /// <summary>
        /// Authored silah pozunun uygulanacagi 15 eklemi ve DINLENME rotasyonlarini toplar.
        /// Profildeki deger bu dinlenmeden SAPMA olarak saklandigi icin burada eksen hesabi
        /// yok - cevrim editorde yapildi (FpGripPoseBake), runtime yalnizca `rest * sapma`
        /// yaziyor. Dinlenme kurulumda okunur; poz surulduktan sonra okunursa poz pozun
        /// uzerine biner.
        /// </summary>
        void BuildPoseJoints(Dictionary<string, Transform> map, string p)
        {
            _poseBone = new Transform[HandPoseBones.JointCount];
            _poseRest = new Quaternion[HandPoseBones.JointCount];
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                Transform bone;
                if (!map.TryGetValue(p + PoseBone[j], out bone) || bone == null) continue;
                _poseBone[j] = bone;
                _poseRest[j] = bone.localRotation;
            }
        }

        /// <summary>Silah tutuldugunda cagrilir; profil authored poz tasiyorsa parmaklar
        /// ona gecer. Profil null ise prosedurel kivrima donulur.</summary>
        public void SetWeaponPose(WeaponGripProfile profile, bool isSupport)
        {
            _weaponProfile = profile;
            _weaponSupport = isSupport;
        }

        public void ClearWeaponPose() => _weaponProfile = null;

        /// <summary>
        /// PARMAK BASINA kivrim (0..1; bas, isaret, orta, yuzuk, serce). Atolyede elle
        /// yazilan degerlerin yolu: cevrim yok, rig'in kendi mentese kurali uygulanir -
        /// yani ekranda gordugun sey yazdigin seyin ta kendisi.
        /// </summary>
        public void ApplyFingerCurls(float[] curls, float weight)
        {
            if (curls == null || curls.Length < 5 || weight <= 0f) return;
            for (int i = 0; i < _phalanges.Count; i++)
            {
                var ph = _phalanges[i];
                float k = Mathf.Clamp01(curls[Mathf.Clamp(ph.finger, 0, 4)]);
                if (ph.thumb) k *= _thumbLimit;
                Quaternion target = Quaternion.Slerp(ph.open, ph.closed, k);
                ph.t.localRotation = weight >= 1f
                    ? target
                    : Quaternion.Slerp(ph.t.localRotation, target, weight);
            }
        }

        /// <summary>Butun parmaklari DUZ (dinlenme) pozuna alir - atolyede ayara buradan
        /// baslaniyor: kivrilmis bir elde neyin dogru neyin yanlis oldugu secilemiyor.</summary>
        public void ApplyFlat()
        {
            for (int i = 0; i < _phalanges.Count; i++) _phalanges[i].t.localRotation = _phalanges[i].open;
        }

        /// <summary>15 eklemin DINLENMEDEN sapmasini dogrudan yazar (profil alanindan
        /// bagimsiz). Atolye, kaydedilmis serbest pozu tezgahta gostermek icin kullanir —
        /// runtime'in <see cref="ApplyWeaponPose"/> icinde yaptigi islemin aynisi.</summary>
        public void ApplyPoseJoints(Quaternion[] deviations)
        {
            if (deviations == null || deviations.Length != HandPoseBones.JointCount) return;
            if (_poseBone == null || _poseRest == null) return;
            for (int j = 0; j < HandPoseBones.JointCount; j++)
                if (_poseBone[j] != null) _poseBone[j].localRotation = _poseRest[j] * deviations[j];
        }

        // ------------------------------------------------------------------ serbest poz

        /// <summary>
        /// ATOLYENIN PARMAK KIPI icin tek kapi: ACIKKEN bu bilesen parmaklara HIC dokunmaz.
        /// Sifirlanmadan birakilirsa el donar - <see cref="WeaponFingerPoser"/> kipten
        /// cikarken kendisi kapatir.
        ///
        /// Neden bayrak, neden bileseni devre disi birakmak degil: <c>enabled = false</c>
        /// LateUpdate'i durdurur ama <see cref="ApplyFingerCurls"/> gibi disaridan cagrilan
        /// yollari durdurmaz; atolyenin Drive() dongusu tam da onu her karede cagiriyor.
        /// </summary>
        public bool PoseSuspended { get; set; }

        /// <summary>
        /// Serbest poz cozumunun bir eklem hakkinda bilmesi gereken her sey.
        ///
        /// KRITIK: <see cref="hingeParent"/> BURADA TURETILMEZ, bu bilesenin ZATEN kurdugu
        /// acik/kapali pozdan geri okunur. Sebep sinif basindaki ders: mentese eksenini
        /// yeniden turetmek iki kez basarisiz oldu (serbest CCD burulma bindirdi, turetilmis
        /// eksen basparmagi yanlis taraftan yaklastirdi). Cozum rig'in kendi anatomik
        /// isaretcileriydi ve o cozum <see cref="_phalanges"/> icinde duruyor - yeni bir
        /// tahmin uretmek yerine ondan geri cikariyoruz.
        /// </summary>
        public struct JointInfo
        {
            public Transform bone;
            /// <summary>Mentese ekseni, EBEVEYN uzayinda. Pozitif aci = KAPANMA yonu.</summary>
            public Vector3 hingeParent;
            /// <summary>Sistemin kendi kapali pozunun acisi (derece). Serbest pozun
            /// sinirlari bundan turetilir - sabit tablo yok, olcu rig'in kendisinden gelir.</summary>
            public float refFlexDegrees;
            /// <summary>0 bas, 1 isaret, 2 orta, 3 yuzuk, 4 serce.</summary>
            public int finger;
        }

        /// <summary>
        /// Eklemleri parmak parmak, KOKTEN UCA sirali verir. <see cref="_phalanges"/> zaten bu
        /// sirada dolduruluyor (AddFinger j1->j2->j3, AddThumbJoint j0->j1->j2).
        ///
        /// Iki kurulum konvansiyonu var ve ikisi de ayni formulle geri okunuyor:
        ///   dort parmak: closed = AngleAxis(d, eksenEbeveyn) * rest
        ///   basparmak  : closed = rest * AngleAxis(d, eksenKemik)
        /// Ikincisi eslenik ozdesligiyle birincisine esittir
        /// (rest * A(d,u) = A(d, rest*u) * rest), yani her iki durumda da
        /// <c>closed * Inverse(open)</c> menteseyi EBEVEYN uzayinda verir. Tek kod yolu.
        /// </summary>
        public bool TryGetPoseJoints(List<JointInfo> into)
        {
            if (into == null) return false;
            into.Clear();
            if (!_built) return false;

            for (int i = 0; i < _phalanges.Count; i++)
            {
                var ph = _phalanges[i];
                if (ph.t == null || ph.t.parent == null) continue;

                Quaternion d = ph.closed * Quaternion.Inverse(ph.open);
                d.ToAngleAxis(out float deg, out Vector3 axis);
                // ToAngleAxis aciyi [0,180]'e getirip ekseni gerektiginde cevirir; yani
                // eksen HER ZAMAN kapanma yonunu gosterir. Basparmagin negatif yazilmis
                // acilari (ThumbCmcFlex = -73) da bu sayede dogru isaretle geliyor.
                if (deg < 0.01f || axis.sqrMagnitude < 1e-8f) continue;

                into.Add(new JointInfo
                {
                    bone = ph.t,
                    hingeParent = axis.normalized,
                    refFlexDegrees = deg,
                    finger = ph.finger,
                });
            }
            return into.Count > 0;
        }

        /// <summary>Bir eklemin DINLENME (acik) lokal rotasyonu - "duz ele don" ve poz
        /// farkini hesaplamak icin.</summary>
        public bool TryGetRest(Transform bone, out Quaternion rest)
        {
            for (int i = 0; i < _phalanges.Count; i++)
                if (_phalanges[i].t == bone) { rest = _phalanges[i].open; return true; }
            rest = Quaternion.identity;
            return false;
        }

        /// <summary>15 eklemin poz sirasindaki kemikleri (HandPoseBones duzeni) ve
        /// DINLENME rotasyonlari. Serbest pozu profile yazarken sapma bu dinlenmeye
        /// gore hesaplanir - runtime da tam olarak `rest * sapma` uyguluyor.</summary>
        public bool TryGetPoseBones(out Transform[] bones, out Quaternion[] rests)
        {
            bones = _poseBone;
            rests = _poseRest;
            return _built && _poseBone != null && _poseRest != null;
        }

        /// <summary>Su an tutulan silahin BU ELE ait el pozu (yoksa null).</summary>
        WeaponGripProfile.HandPose? ActiveHandPose()
        {
            if (_weaponProfile == null || _poseBone == null) return null;
            return _weaponSupport ? _weaponProfile.supportHand : _weaponProfile.mainHand;
        }

        /// <summary>Bu el icin cevrilmis authored poz var mi (yoksa prosedurel kivrim surer).</summary>
        bool TryGetFpPose(out WeaponGripProfile.FingerPose fp, out bool indexFollowsTrigger)
        {
            fp = default;
            indexFollowsTrigger = false;
            if (_weaponProfile == null || _poseBone == null) return false;
            var pose = _weaponSupport ? _weaponProfile.supportHand : _weaponProfile.mainHand;
            fp = pose.Fingers(_left);
            indexFollowsTrigger = pose.indexFollowsTrigger;
            return fp.HasFpJoints;
        }

        /// <summary>
        /// Authored pozu uygular. Prosedurel kivrimin UZERINE harmanlanir: gecis aninda
        /// parmaklar bulundugu yerden pozuna kayar, ziplama olmaz. Okuma ayni karede
        /// yazilan degerden yapiliyor - kareler arasi geri besleme yok.
        /// </summary>
        public void ApplyWeaponPose(float trigger, float weight)
        {
            WeaponGripProfile.FingerPose fp;
            bool follows;
            if (weight <= 0f || !TryGetFpPose(out fp, out follows)) return;

            // TETIGIN ISARET PARMAGINA ETKISI — IKI AYRI YOL, TABANIN KAYNAGINA GORE.
            //
            //  (a) CEVRILMIS taban (menu 50): eski fpIndexPulledJoints ile harmanlanir.
            //      Iki veri de ayni cevrimden ciktigi icin tutarlilar; olculdu, dogru
            //      yonde calisiyor (+5, +3, +1, 0, -3 derece).
            //
            //  (b) ELLE POZLANAN taban (atolye parmak kipi): eski cekili poz KULLANILAMAZ.
            //      O poz dinlenmeye gore MUTLAK yazildi ve CEVRILMIS tabana gore anlamliydi;
            //      elle pozlanan taban cok daha kivrik oldugu icin harman parmagi ACIYOR.
            //      Olculdu: Rifle1 -43, Smg1 -35, Pistol3 -33, Smg 3 -27, Pistol4 -19,
            //      Sniper1 -18, Rifle 2 -15, Rifle3 -11 derece. Yani tetige basinca parmak
            //      aciliyordu. Bunun yerine tetik, tabanin USTUNE kivrim EKLER — parmagin
            //      kendi dogrulanmis mentesesi etrafinda, tabanin ne kadar kivrik oldugundan
            //      BAGIMSIZ olarak. Boylece hangi taban gelirse gelsin yon her zaman dogru.
            bool authored = fp.fpJointsAuthored;
            bool blendPulled = follows && fp.HasFpIndexPulled && !authored;
            bool addFlex = follows && authored && _indexHingesReady;

            var hand = ActiveHandPose();
            float maxCurl = hand.HasValue && hand.Value.indexTriggerMaxCurl > 0f
                ? hand.Value.indexTriggerMaxCurl : 1f;
            float t = Mathf.Clamp01(trigger);

            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var bone = _poseBone[j];
                if (bone == null) continue;

                Quaternion delta = fp.fpJoints[j];
                bool isIndex = HandPoseBones.IsIndex(j);
                if (blendPulled && isIndex)
                    delta = Quaternion.Slerp(delta, fp.fpIndexPulledJoints[j - HandPoseBones.IndexFirst], t);

                Quaternion target = _poseRest[j] * delta;

                if (addFlex && isIndex)
                {
                    int k = j - HandPoseBones.IndexFirst;
                    float deg = t * maxCurl * _indexRefFlex[k] * TriggerFlexFraction;
                    // Mentese EBEVEYN uzayinda, yani on-carpim (poz cozucusuyle ayni kurulum).
                    target = Quaternion.AngleAxis(deg, _indexHinge[k]) * target;
                }

                bone.localRotation = Quaternion.Slerp(bone.localRotation, target, weight);
            }
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
            _phalanges.Add(new Phalanx { t = bone, open = rest, closed = closed, useTrigger = false, thumb = true, finger = 0 });
        }

        void AddFinger(Dictionary<string, Transform> map, string prefix, Vector3 target,
                       Vector3? plane, bool useTrigger, float c1, float c2, float c3,
                       bool thumb, int finger)
        {
            Transform j1, j2, j3;
            map.TryGetValue(prefix + "1", out j1);
            map.TryGetValue(prefix + "2", out j2);
            map.TryGetValue(prefix + "3", out j3);
            if (j1 == null) return;

            // Uzanim yonu bir sonraki eklemden gelir; son bogumda onceki yon surdurulur.
            Vector3 e1 = j2 != null ? (j2.position - j1.position) : j1.forward;
            Add(j1, e1, target, plane, c1, useTrigger, thumb, finger);
            if (j2 == null) return;
            Vector3 e2 = j3 != null ? (j3.position - j2.position) : e1;
            Add(j2, e2, target, plane, c2, useTrigger, thumb, finger);
            if (j3 != null) Add(j3, e2, target, plane, c3, useTrigger, thumb, finger);
        }

        void Add(Transform bone, Vector3 ext, Vector3 target, Vector3? plane, float deg,
                 bool useTrigger, bool thumb, int finger)
        {
            // Acik el = modelin KENDI dinlenme pozu. Meta'nin eli duz elle geliyor, yani
            // burada animatorun pozunu ayiklamak gerekmiyor (avatarda gerekiyordu).
            if (!FingerCurlMath.Solve(bone, ext, target, plane, deg, bone.localRotation,
                                      out Quaternion open, out Quaternion closed))
                return;
            _phalanges.Add(new Phalanx { t = bone, open = open, closed = closed, useTrigger = useTrigger, thumb = thumb, finger = finger });
        }

        void LateUpdate()
        {
            // Parmak kipi acikken otomatik surucu susar: yoksa cozulen poz her karede
            // kivrim pozuyla ezilir ve el titrer.
            if (!_built || _net == null || PoseSuspended) return;

            float gripTarget = _left ? _net.LeftGrip01 : _net.RightGrip01;
            float trigTarget = _left ? _net.LeftTrigger01 : _net.RightTrigger01;
            _grip = Mathf.Lerp(_grip, gripTarget, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
            _trigger = Mathf.Lerp(_trigger, trigTarget, 1f - Mathf.Exp(-triggerSmoothing * Time.deltaTime));
            Apply(_grip, _trigger);

            // Authored silah pozu prosedurel kivrimin USTUNE gelir; agirlik harmanla
            // yuruyor, boylece silahi alip birakirken parmaklar ziplamiyor.
            //
            // ONCELIK — uc kaynak, guvenilirlik sirasina gore:
            //
            //  1. ELLE POZLANAN eklemler (atolye parmak kipi, fpJointsAuthored). Eklem basina
            //     tam rotasyon; ayrica tetik animasyonu da bu yoldan geciyor.
            //  2. ELLE YAZILAN kivrimlar (fpCurls). Parmak basina TEK sayi - daha kaba, ama
            //     cevrilmis pozdan iyi.
            //  3. AVATARDAN CEVRILEN poz (fpJoints). Sadakati olculdu ve yetersiz bulundu;
            //     en sona kaldi.
            //
            // 1 ve 3 ayni alani (fpJoints) kullaniyor, ayirt eden tek sey fpJointsAuthored.
            var hand = ActiveHandPose();
            WeaponGripProfile.FingerPose fp;
            bool hasFp = TryGetFpPose(out fp, out _);
            bool authored  = hasFp && fp.fpJointsAuthored;
            bool manual    = !authored && hand.HasValue && hand.Value.HasFpCurls;
            bool converted = !authored && !manual && hasFp;

            _weaponWeight = Mathf.MoveTowards(_weaponWeight,
                                              (authored || manual || converted) ? 1f : 0f,
                                              Time.deltaTime / WeaponBlendSeconds);
            if (_weaponWeight <= 0f) return;
            if (manual) ApplyFingerCurls(hand.Value.fpCurls, _weaponWeight);
            else ApplyWeaponPose(_trigger, _weaponWeight);
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
