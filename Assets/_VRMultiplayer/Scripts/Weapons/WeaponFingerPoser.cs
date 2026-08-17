using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.UI;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// SERBEST PARMAK POZLAMA — atolyedeki "Blender pose mode" kipi. Kumandani tezgahtaki
    /// elin bir parmagina goturup GRIP'e basiyorsun, parmak kumandayi takip ediyor; birakinca
    /// oldugu yerde kaliyor. Ucundan tutarsan parmagin TAMAMI, ortasindan tutarsan yalnizca
    /// oraya KADARKI bogumlar bukulur — tuttugun bogumdan sonrasi rijit takilir.
    ///
    /// ---------------------------------------------------------------------------------
    /// BU SINIFIN TEK ONEMLI KARARI: EKSEN URETMIYORUZ.
    ///
    /// Bu projede serbest cozum IKI KEZ denendi ve iki kez de cihazda basarisiz oldu
    /// (bkz. <see cref="FirstPersonFingerCurl"/> sinif basi):
    ///   1. Eksensiz (serbest) CCD  -> araya BURULMA bindi, mesh yamuldu.
    ///   2. Mentese kisitli CCD ama eksenler Cross(uzanim, avuc normali) ile TURETILMIS
    ///      -> dort parmakta calisti, BASPARMAKTA calismadi: basparmak kendi duzleminde
    ///      katlaniyor, avuc duzleminde degil. Parmak isarete yanlis taraftan yaklasti.
    /// Calisan cozum, eksenleri rig'in KENDI anatomik isaretcilerinden okumakti — ve o
    /// cozum zaten <see cref="FirstPersonFingerCurl"/> icinde kurulu duruyor.
    ///
    /// Bu yuzden burada hicbir eksen hesabi YOK. Menteseler
    /// <see cref="FirstPersonFingerCurl.TryGetPoseJoints"/> ile o bilesenden geri okunuyor.
    /// Yeni bir rig gelirse duzeltilecek tek yer yine orasi olur.
    ///
    /// BURULMA MATEMATIKSEL OLARAK IMKANSIZ: bir eklemin lokal rotasyonu her karede
    /// <c>AngleAxis(aci, mentese) * dinlenme</c> olarak SIFIRDAN yazilir; carpim birikmez.
    /// Yani eklemin tek serbestlik derecesi vardir, "yavas yavas burulma" diye bir sey
    /// olusamaz. Kisit bir sonradan-duzeltme degil, veri yapisinin kendisi.
    ///
    /// BEDELI (bilincli): parmaklar YAYILAMAZ (abduksiyon yok), yalnizca kendi duzleminde
    /// katlanir. Gercek parmak zaten boyle calisir; yayilma gerekirse ikinci bir serbestlik
    /// derecesi olarak ayrica eklenmeli — ama once burulmayi geri getirmedigi dogrulanmali.
    /// ---------------------------------------------------------------------------------
    ///
    /// GIRDI — ISINLA SEC, ELLE SURUKLE:
    ///   1. Lazeri boguma dogrult (panelin kullandigi isinin ayni).
    ///   2. GRIP'e bas — o bogum yakalanir.
    ///   3. Elini hareket ettir; bogum elinin OTELENMESINI takip eder.
    ///
    /// ILK SURUM YAKINLIK TABANLIYDI ve cihazda REDDEDILDI: kumandayi tezgahtaki parmagin
    /// 3 cm yakinina goturmek gerekiyordu. Iki ayri sorun vardi — (a) o mesafeye ulasmak
    /// fiziksel olarak zahmetli, (b) menzil disindayken HICBIR geri bildirim yok, yani
    /// kullanici yaklasip yaklasmadigini goremiyor ve arac "calismiyor" gibi hissettiriyor.
    /// Isin ikisini birden cozuyor: uzaktan nisan alinir ve vurgu her an gorunur.
    ///
    /// SURUKLEME NEDEN ISINLA DEGIL DE EL OTELEMESIYLE: isin yonuyle surukleyince bogum
    /// kumandanin etrafinda bir KURE yuzeyinde geziniyor, avuca dogru kivirmak (isin
    /// dogrultusunda derinlik) ancak ikinci bir eksenle (cubuk) mumkun oluyordu. El
    /// otelemesi 1:1 ve uc eksende serbest; ustelik elini nereye koyacagini dusunmen
    /// gerekmiyor, yakalama zaten olmus oluyor.
    ///
    /// Panel TETIGI kullanmaya devam ediyor, yani kip acikken bile panele basip kipten
    /// cikabilirsin — kilitlenme yok.
    /// </summary>
    public class WeaponFingerPoser
    {
        /// <summary>Isin bir boguma bu kadar yakin gecerse aday sayilir (m). Adaylar
        /// arasindan EN YAKIN olan secilir, yani esik cömert olabilir: 2 cm, 0.7 m
        /// mesafeden ~1.6 derecelik nisan hassasiyeti demek.</summary>
        const float RayPickRadius = 0.020f;

        /// <summary>El otelemesinin boguma yansima orani. Parmak hareketleri milimetrik;
        /// 1:1'de el titremesi dogrudan poza giriyor. Panel'deki ADIM tusuna baglandi:
        /// INCE = hassas, KABA = birebir.</summary>
        const float DragScaleFine = 0.40f, DragScaleCoarse = 1f;

        /// <summary>Kare basina CCD yinelemesi. 6 yeterli: tek karede tam yakinsama
        /// gerekmiyor, el zaten her kare cozuluyor ve artik hata sonraki kareye kaliyor.</summary>
        const int Iterations = 6;

        /// <summary>Her yinelemede acinin ne kadari uygulanir. 1.0 hedefe tek adimda
        /// atlar ve el zipzip eder; 0.5 gorunur bir gecikme yaratmadan yumusatiyor.</summary>
        const float Damping = 0.5f;

        /// <summary>Serbest pozun kapanma tavani, sistemin KENDI kapali pozunun kati olarak.
        /// Sabit anatomik tablo yazmak yerine olcuyu rig'in kendisinden aliyoruz: hangi rig
        /// gelirse gelsin sinir onun dogal kapanmasiyla orantili kalir.</summary>
        const float FlexHeadroom = 1.35f;

        /// <summary>Dinlenmenin GERISINE (ters yone) izin verilen aci. Parmagin geriye
        /// kirilmasini engeller; 20 derece dogal esnekligi karsiliyor.</summary>
        const float ExtendLimit = 20f;

        class Joint
        {
            public Transform bone;
            public Vector3 hinge;        // mentese, EBEVEYN uzayinda (+ = kapanma)
            public Quaternion rest;      // dinlenme lokal rotasyonu
            public float angle;          // dinlenmeden sapma (derece)
            public float min, max;
        }

        /// <summary>Tek bir parmak: kokten uca sirali bogumlar.</summary>
        class Finger
        {
            public int id;               // 0 bas .. 4 serce
            public readonly List<Joint> joints = new List<Joint>();
        }

        readonly List<Finger> _fingers = new List<Finger>();
        readonly List<FirstPersonFingerCurl.JointInfo> _scratch =
            new List<FirstPersonFingerCurl.JointInfo>();

        FirstPersonFingerCurl _curl;
        Transform _handRoot;

        // Yakalama durumu
        Finger _grabFinger;
        int _grabIndex = -1;          // parmak icindeki bogum sirasi
        Vector3 _grabHandStart;       // yakalama anindaki EL konumu
        Vector3 _grabTipStart;        // yakalama anindaki bogum UCU
        bool _prevGrip;

        // Aday vurgusu
        Finger _hoverFinger;
        int _hoverIndex = -1;
        Transform _marker;

        public bool Active { get; private set; }
        public bool Dirty { get; private set; }

        static readonly string[] FingerName = { "BASPARMAK", "ISARET", "ORTA", "YUZUK", "SERCE" };
        static readonly string[] JointName = { "kok", "orta", "uc" };

        /// <summary>Panelin gosterdigi tek satirlik durum.</summary>
        public string Status
        {
            get
            {
                if (!Active) return "";
                if (_grabFinger != null)
                    return "TUTULUYOR: " + Label(_grabFinger, _grabIndex) + "  — elini oynat, birak = sabitle";
                if (_hoverFinger != null)
                    return "nisanda: " + Label(_hoverFinger, _hoverIndex) + "  — GRIP = tut";
                return "lazeri bir boguma dogrult, GRIP'e bas";
            }
        }

        static string Label(Finger f, int idx)
        {
            string j = idx >= 0 && idx < JointName.Length ? JointName[idx] : idx.ToString();
            return FingerName[Mathf.Clamp(f.id, 0, 4)] + " / " + j;
        }

        // ------------------------------------------------------------------ yasam dongusu

        /// <summary>
        /// Kipi acar. <paramref name="curl"/> tezgahtaki ELIN kivrim surucusu — menteseler
        /// ondan okunur ve otomatik surucusu susturulur.
        /// </summary>
        public bool Begin(FirstPersonFingerCurl curl, Transform handRoot)
        {
            End();
            if (curl == null) return false;
            if (!curl.TryGetPoseJoints(_scratch)) return false;

            _curl = curl;
            _handRoot = handRoot;
            BuildFingers();
            if (_fingers.Count == 0) { _curl = null; return false; }

            // Otomatik kivrim surucusu susar; yoksa cozdugumuz pozu her karede ezer.
            _curl.PoseSuspended = true;

            // Oyun girisleri susturulur: GRIP burada "parmak tut" demek. Bu kapi olmadan
            // ayni basis HandGrabber'a da gider ve oyuncu tezgahin yanindaki bir silahi
            // kapabilir / elindekini dusurebilirdi. PANEL ETKILENMEZ — VRPointer tetigi
            // dogrudan okuyor, yani kipten cikis dugmesi her zaman calisir.
            XRButtons.GameplayInputSuppressed = true;

            Active = true;
            _prevGrip = ReadGrip(XRNode.LeftHand) || ReadGrip(XRNode.RightHand);  // basili girisi yut
            return true;
        }

        /// <summary>Kipi kapatir. Cagrilmasi SART: susturulan girisler ve donmus surucu
        /// burada geri aciliyor.</summary>
        public void End()
        {
            if (_curl != null) _curl.PoseSuspended = false;
            if (Active) XRButtons.GameplayInputSuppressed = false;

            if (_marker != null) Object.Destroy(_marker.gameObject);
            _marker = null;

            _curl = null;
            _handRoot = null;
            _fingers.Clear();
            _grabFinger = null; _grabIndex = -1;
            _hoverFinger = null; _hoverIndex = -1;
            Active = false;
        }

        void BuildFingers()
        {
            _fingers.Clear();
            Finger cur = null;
            int lastId = -99;

            // TryGetPoseJoints kokten uca, parmak parmak sirali veriyor.
            for (int i = 0; i < _scratch.Count; i++)
            {
                var info = _scratch[i];
                if (info.finger != lastId)
                {
                    cur = new Finger { id = info.finger };
                    _fingers.Add(cur);
                    lastId = info.finger;
                }

                float flex = Mathf.Max(5f, info.refFlexDegrees) * FlexHeadroom;
                var j = new Joint
                {
                    bone = info.bone,
                    hinge = info.hingeParent,
                    rest = Quaternion.identity,
                    min = -ExtendLimit,
                    max = flex,
                };
                if (!_curl.TryGetRest(info.bone, out j.rest)) j.rest = info.bone.localRotation;
                j.angle = CurrentAngle(j);
                cur.joints.Add(j);
            }
        }

        /// <summary>Bogumun SU ANKI sapmasi (derece), mentese ekseni uzerinde isaretli.
        /// Kip acilirken parmaklar zaten bir pozda olabilir; sifirdan baslarsak el ziplar.</summary>
        static float CurrentAngle(Joint j)
        {
            Quaternion rel = j.bone.localRotation * Quaternion.Inverse(j.rest);
            rel.ToAngleAxis(out float deg, out Vector3 axis);
            if (deg > 180f) deg -= 360f;
            float sign = Vector3.Dot(axis.normalized, j.hinge) >= 0f ? 1f : -1f;
            return Mathf.Clamp(deg * sign, j.min, j.max);
        }

        // ------------------------------------------------------------------ kare dongusu

        /// <summary><paramref name="pointer"/> panelin kullandigi lazerin ta kendisi (nisan
        /// kaynagi tek yerde kalsin). <paramref name="coarse"/> panelin ADIM tusu.</summary>
        public void Tick(VRPointer pointer, bool coarse)
        {
            if (!Active || _curl == null) return;

            bool grip = ReadGrip(XRNode.RightHand);   // nisan alan el ayni zamanda tutan el
            Vector3 hand = ControllerPos(XRNode.RightHand);
            // Onceden bildirilir: kisa devre yuzunden (pointer == null) derleyici
            // atanmisligi kanitlayamiyor. TryGetRay false donerken zaten bu varsayilanlari
            // yaziyor, yani deger her yolda gecerli.
            Vector3 origin = Vector3.zero, dir = Vector3.forward;
            bool hasRay = pointer != null && pointer.TryGetRay(out origin, out dir);

            if (_grabFinger == null)
            {
                if (hasRay) FindUnderRay(origin, dir, pointer.MaxDistance, out _hoverFinger, out _hoverIndex);
                else { _hoverFinger = null; _hoverIndex = -1; }

                if (grip && !_prevGrip && _hoverFinger != null)
                    Grab(_hoverFinger, _hoverIndex, hand);
            }
            else if (!grip)
            {
                _grabFinger = null;
                _grabIndex = -1;
            }
            else
            {
                // EL OTELEMESI 1:1 (ya da INCE adimda olcekli). Isin yonu artik onemli
                // degil — yakalandiktan sonra lazeri savurmak pozu bozmaz.
                float scale = coarse ? DragScaleCoarse : DragScaleFine;
                Solve(_grabTipStart + (hand - _grabHandStart) * scale);
                Dirty = true;
            }

            _prevGrip = grip;
            UpdateMarker();
            DrawRay(pointer, hasRay, origin, dir);
        }

        void Grab(Finger f, int index, Vector3 hand)
        {
            _grabFinger = f;
            _grabIndex = index;
            // POP YOK: hedef, yakalama anindaki UCUN kendisi. Ilk karede fark sifir
            // oldugu icin parmak kimildamaz, oradan itibaren elini takip eder.
            _grabHandStart = hand;
            _grabTipStart = TipOf(f, index);
            VRPointer.Haptic();
        }

        /// <summary>
        /// Lazeri cizer. Panel Tick'i, isin panele DEGMEDIGINDE lazeri gizliyor — tam da
        /// ele nisan aldigimiz durum bu. Poser panelden SONRA kostugu icin son sozu o
        /// soyluyor ve isin ele bakarken de gorunur kaliyor.
        /// </summary>
        void DrawRay(VRPointer pointer, bool hasRay, Vector3 origin, Vector3 dir)
        {
            if (pointer == null || !hasRay) return;
            var f = _grabFinger ?? _hoverFinger;
            int idx = _grabFinger != null ? _grabIndex : _hoverIndex;
            if (f == null || idx < 0) return;   // panel ne cizdiyse o kalsin

            Vector3 p = MidOf(f, idx);
            // Tutarken isin YAKALANAN boguma kilitli: el hareket ederken lazerin savrulmasi
            // "kaydi mi?" hissi veriyordu — oysa surukleme isindan bagimsiz.
            pointer.Draw(true, p, (origin - p).normalized);
        }

        /// <summary>
        /// Kisitli CCD. Zincir parmagin KOKUNDEN tutulan boguma kadar; efektor tutulan
        /// bogumun UCU. Tutulan bogumdan sonrasi hic donmez — "ortasindan tutarsam oraya
        /// kadar bukulsun" davranisi tam olarak budur, ayrica bir kural gerekmiyor.
        /// </summary>
        void Solve(Vector3 target)
        {
            var joints = _grabFinger.joints;
            int end = Mathf.Clamp(_grabIndex, 0, joints.Count - 1);

            for (int iter = 0; iter < Iterations; iter++)
            {
                // Uctan koke: yakin eklem once ince ayari yapar, uzak eklem kabayi alir.
                for (int i = end; i >= 0; i--)
                {
                    var j = joints[i];
                    if (j.bone == null || j.bone.parent == null) continue;

                    Vector3 axis = j.bone.parent.rotation * j.hinge;
                    Vector3 pivot = j.bone.position;
                    Vector3 tip = TipOf(_grabFinger, end);

                    // Menteseye DIK duzleme izdusum: eksen disi bilesen zaten cozulemez,
                    // izdusurmeden aci olcmek yanlis buyuklukte donus uretir.
                    Vector3 cur = Vector3.ProjectOnPlane(tip - pivot, axis);
                    Vector3 want = Vector3.ProjectOnPlane(target - pivot, axis);
                    if (cur.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f) continue;

                    float delta = Vector3.SignedAngle(cur, want, axis) * Damping;
                    if (Mathf.Abs(delta) < 0.01f) continue;

                    float a = Mathf.Clamp(j.angle + delta, j.min, j.max);
                    if (Mathf.Approximately(a, j.angle)) continue;
                    j.angle = a;

                    // SIFIRDAN YAZ, carpimi biriktirme: bogumun lokal rotasyonu her zaman
                    // TAM olarak "mentese etrafinda a derece" olur. Burulmanin sizabilecegi
                    // bir aralik kalmiyor (bkz. sinif basi).
                    j.bone.localRotation = Quaternion.AngleAxis(a, j.hinge) * j.rest;
                }
            }
        }

        // ------------------------------------------------------------------ geometri

        /// <summary>Bogumun ucu: bir sonraki bogumun koku, son bogumda kemigin kendi
        /// ucu (Meta rig'inde <c>*_null</c>). Hicbiri yoksa uzanim yonunde tahmin.</summary>
        static Vector3 TipOf(Finger f, int index)
        {
            var joints = f.joints;
            if (index + 1 < joints.Count) return joints[index + 1].bone.position;

            var bone = joints[index].bone;
            for (int c = 0; c < bone.childCount; c++)
            {
                var ch = bone.GetChild(c);
                if (ch.name.StartsWith("b_")) return ch.position;   // kemik; isaretci degil
            }
            // Son care: onceki bogumun boyu kadar ileri.
            Vector3 dir = index > 0
                ? (bone.position - joints[index - 1].bone.position)
                : bone.forward * 0.02f;
            return bone.position + dir;
        }

        /// <summary>Bogumun gorsel ortasi (vurgu kuresi ve isin ucu buraya oturur).</summary>
        static Vector3 MidOf(Finger f, int index)
            => (f.joints[index].bone.position + TipOf(f, index)) * 0.5f;

        /// <summary>
        /// Isinin altindaki BOGUM. Nokta degil PARCA mesafesi olculur — parmagin ortasini
        /// gostermekle ucunu gostermek ayirt edilebilsin. Esigi gecen adaylar arasindan
        /// isina EN YAKIN olan kazanir, yani komsu parmaklar arasinda karar nettir.
        /// </summary>
        void FindUnderRay(Vector3 origin, Vector3 dir, float maxDist,
                          out Finger finger, out int index)
        {
            finger = null;
            index = -1;
            float best = RayPickRadius;
            float keepDist = float.MaxValue;   // halen vurgulu olanin mesafesi

            for (int fi = 0; fi < _fingers.Count; fi++)
            {
                var f = _fingers[fi];
                for (int i = 0; i < f.joints.Count; i++)
                {
                    if (f.joints[i].bone == null) continue;
                    float d = RaySegmentDistance(origin, dir, maxDist,
                                                 f.joints[i].bone.position, TipOf(f, i));
                    if (f == _hoverFinger && i == _hoverIndex) keepDist = d;
                    if (d < best) { best = d; finger = f; index = i; }
                }
            }

            // YAPISKANLIK. Olculdu: parmak DIPLERINDE (bogum 0) komsu adaylar arasindaki
            // ayrim payi 0.5 mm'ye kadar iniyor — orada uc parmagin kokleri yan yana.
            // Saf "en yakin kazanir" kurali, elin dogal titremesiyle vurgunun iki aday
            // arasinda saniyede birkac kez atlamasina yol aciyor ve hangisini yakalayacagini
            // kestiremiyorsun. Mevcut secim bu paydan daha kotu olmadikca korunur; secimi
            // gercekten degistirmek icin lazeri belirgin sekilde otekine cevirmen gerekir.
            const float StickyMargin = 0.004f;
            // Ayni parmagin IKI BOGUMU arasinda da olabilir (dip <-> orta), o yuzden
            // karsilastirma parmak VE indeks uzerinden.
            if (finger != null && _hoverFinger != null &&
                (finger != _hoverFinger || index != _hoverIndex) &&
                keepDist <= RayPickRadius && keepDist - best < StickyMargin)
            {
                finger = _hoverFinger;
                index = _hoverIndex;
            }
        }

        /// <summary>Isin ile dogru parcasi arasindaki EN KISA mesafe. Standart parca-parca
        /// yakinsama: once ikisini de sonsuz dogru sayip cozeriz, sonra sonuclari kendi
        /// araliklarina kelepceleriz.</summary>
        static float RaySegmentDistance(Vector3 origin, Vector3 dir, float maxDist,
                                        Vector3 a, Vector3 b)
        {
            Vector3 v = b - a;
            Vector3 w = origin - a;
            float bb = Vector3.Dot(dir, v);
            float cc = Vector3.Dot(v, v);
            float dd = Vector3.Dot(dir, w);
            float ee = Vector3.Dot(v, w);
            if (cc < 1e-12f) return Vector3.Distance(a, ClosestOnRay(origin, dir, maxDist, a));

            float denom = cc - bb * bb;      // dir birim oldugu icin aa = 1
            float t = Mathf.Abs(denom) < 1e-9f
                ? 0f                          // paralel: parcanin basi yeterli referans
                : Mathf.Clamp01((ee - bb * dd) / denom);

            // Parca uzerindeki nokta sabitlendikten SONRA isin uzerindeki en yakin nokta.
            Vector3 p = a + v * t;
            return Vector3.Distance(p, ClosestOnRay(origin, dir, maxDist, p));
        }

        static Vector3 ClosestOnRay(Vector3 origin, Vector3 dir, float maxDist, Vector3 p)
            => origin + dir * Mathf.Clamp(Vector3.Dot(p - origin, dir), 0f, maxDist);

        static Vector3 ControllerPos(XRNode node)
        {
            var rig = XRRigReference.Instance;
            if (rig != null)
            {
                var t = node == XRNode.LeftHand ? rig.leftHand : rig.rightHand;
                if (t != null) return t.position;
            }
            return Vector3.positiveInfinity;   // takip yoksa hicbir sey yakalanmasin
        }

        static bool ReadGrip(XRNode node)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            return XRButtons.HeldWithAxisFallback(dev, CommonUsages.gripButton,
                                                  CommonUsages.grip, 0.55f);
        }

        // ------------------------------------------------------------------ vurgu

        void UpdateMarker()
        {
            var f = _grabFinger ?? _hoverFinger;
            int idx = _grabFinger != null ? _grabIndex : _hoverIndex;

            if (f == null || idx < 0)
            {
                if (_marker != null) _marker.gameObject.SetActive(false);
                return;
            }

            if (_marker == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "~FingerPoseMarker";
                Object.Destroy(go.GetComponent<Collider>());
                go.GetComponent<MeshRenderer>().sharedMaterial =
                    UITheme.CreateTransparentMaterial(UITheme.AccentCyan);
                _marker = go.transform;
                _marker.localScale = Vector3.one * 0.012f;
            }

            if (!_marker.gameObject.activeSelf) _marker.gameObject.SetActive(true);
            // Bogumun ORTASINA: hangi parcayi tuttugun tek bakista okunsun.
            _marker.position = MidOf(f, idx);
            UITheme.SetMaterialColor(_marker.GetComponent<MeshRenderer>().sharedMaterial,
                _grabFinger != null ? UITheme.AccentPurple : UITheme.AccentCyan);
        }

        // ------------------------------------------------------------------ disari aktarim

        /// <summary>
        /// Cozulen pozu profilin bekledigi bicime cevirir: 15 eklemin DINLENMEDEN sapmasi,
        /// <see cref="HandPoseBones"/> sirasinda. Runtime tam olarak <c>rest * sapma</c>
        /// uyguluyor (bkz. <see cref="FirstPersonFingerCurl.ApplyWeaponPose"/>), yani burada
        /// uretilen sey ekranda gordugun seyin ta kendisi.
        /// </summary>
        public bool TryExport(out Quaternion[] deviations)
        {
            deviations = null;
            if (_curl == null) return false;
            if (!_curl.TryGetPoseBones(out Transform[] bones, out Quaternion[] rests)) return false;

            deviations = new Quaternion[HandPoseBones.JointCount];
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                deviations[j] = bones[j] == null
                    ? Quaternion.identity
                    : Quaternion.Inverse(rests[j]) * bones[j].localRotation;
            }
            return true;
        }

        /// <summary>Butun parmaklari dinlenmeye alir (kipin "sifirla"si).</summary>
        public void ResetToRest()
        {
            for (int fi = 0; fi < _fingers.Count; fi++)
            {
                var f = _fingers[fi];
                for (int i = 0; i < f.joints.Count; i++)
                {
                    var j = f.joints[i];
                    j.angle = 0f;
                    if (j.bone != null) j.bone.localRotation = j.rest;
                }
            }
            Dirty = true;
        }
    }
}
