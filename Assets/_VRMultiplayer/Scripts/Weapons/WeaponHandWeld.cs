using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Welds an avatar's wrist bone(s) to a held weapon's grip anchor / support rail, in world
    /// space, AFTER the rig evaluation and the finger poser (execution order 110). Because the
    /// write is absolute, the avatar's continuous height-fit scaling can't drift the hand, and
    /// the hand follows the weapon's interpolated transform — fingers never separate from the
    /// grip. Added to the avatar at runtime by <see cref="WeaponGrip"/>; the NetworkPlayer
    /// prefab is never edited.
    ///
    /// Support hand: the anchor is the closest point on the profile's rail segment to this
    /// hand's networked carrier — owners and remote clients project the same replicated
    /// position, so everyone sees the hand at the same spot on the handguard.
    ///
    /// Poses are authored for main=RIGHT / support=LEFT; when the roles are swapped the local
    /// data is mirrored across the weapon's YZ plane. Static (per-hold) values are resolved once
    /// in <see cref="SetHand"/>; only weapon-relative transforms are recomputed per frame.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class WeaponHandWeld : MonoBehaviour
    {
        // Struct + active flag: no per-event heap allocation. Mirroring and Euler→Quaternion
        // are done once here (static per hold), not every LateUpdate.
        struct HandWeld
        {
            public bool active;
            public Transform weapon;
            public WeaponGripProfile profile;
            public bool isSupport;
            public Transform bone;
            public Vector3 wristLocalPos;   // mirror-resolved
            public Quaternion wristLocalRot;// mirror-resolved
            public bool mirrored;
            public float blendStart;        // engage ramp start (weight 0 -> 1)
            public bool fadingOut;          // release ramp (weight 1 -> 0), then inactive
            public float fadeOutStart;
        }

        // The weld writes the wrist ABSOLUTELY, so switching it on/off used to relocate the
        // hand in a single frame (the "el kayboluyor/geri geliyor" pop, on every client).
        // Ramping the weld weight over this window blends the wrist between the IK pose and
        // the weapon anchor on both engage and release.
        const float WeldBlendSeconds = 0.12f;

        HandWeld _left, _right;
        Animator _anim;
        AvatarIKController _ik;
        Transform _leftBone, _rightBone;
        // Erisim kelepcesi icin kol kemikleri (bkz. ArmReach): silah nerede olursa
        // olsun kol boyundan uzagini yazmiyoruz.
        Transform _leftUpper, _leftLower, _rightUpper, _rightLower;
        float _leftArmLen, _rightArmLen;   // yerel uzayda, Awake'te bir kere olculur

        [Header("Govde temizligi (SADECE baskalarinin gordugu kopya)")]
        [Tooltip("Silah govdenin icine girdiginde disari itilir. YALNIZCA bu avatari SEN " +
                 "surmuyorsan uygulanir - yani karsindaki oyuncunun ekraninda. Kendi elindeki " +
                 "silahin konumuna hicbir kosulda dokunulmaz (bkz. VisualOnly).")]
        public bool bodyClearance = true;
        [Tooltip("Govdenin YAN yari genisligi (m, omuz/kaburga). Silahin kalinligi buna EKLENIR.")]
        public float bodyRadius = BodyVolume.Radius;
        [Tooltip("Govdenin ON/ARKA yari derinligi (m, gogus + yelek). Daire yerine ELIPS, " +
                 "cunku gogus onu yanlardan dardir - daire (22 cm) nisan alinan tabancayi " +
                 "iceride sayip one itiyordu.\n\n" +
                 "Deger avatar mesh'inden OLCULDU (bkz. BodyVolume.Depth): 0.14 fazla kucuktu, " +
                 "elips montun icinde kaliyor ve karna dayali tufek hic itilmiyordu.")]
        public float bodyDepth = BodyVolume.Depth;
        [Tooltip("Itmenin ust siniri (m). Kol erisimi (ArmReach) zaten ikinci bir sinir koyar.")]
        public float maxBodyPush = 0.35f;
        [Tooltip("Itmenin yumusama suresi (s). Sadece gorsel oldugu icin serbestce " +
                 "yumusatilabilir - izleyen oyuncu ani sicrama gormez.")]
        public float clearanceSmoothing = 0.08f;
        [Tooltip("Itme, govde bandinin UCLARINDA (ust bacak ve boyun) bu oran boyunca sifira " +
                 "iner.\n\n" +
                 "Bant artik kalcanin altina uzaniyor (BodyVolume.HipExtend) ki kalca hizasinda " +
                 "tutulan silah kapsama girsin - eskiden orasi bandin DISINDAYDI ve silah hic " +
                 "itilmiyordu. Uzatilan kisim bacak hizasi oldugu icin govde orada daha dar: " +
                 "0.18 ile itme kalcadan asagi dogru sonuyor, kalca hizasinda tam guc kaliyor. " +
                 "Ust uc de sonuyor - nisan hattindaki silah yuze dogru itilmemeli.")]
        public float bandEndFade = 0f;

        [Tooltip("IZLEYICI TARAFI: kol silaha yetismiyorsa eli silahtan koparmak (radyal " +
                 "kelepce) yerine GORUNEN silahi tasma kadar govdeye yaklastirir - iki el de " +
                 "silahin ustunde kalir, nisan hatti degismez (yalniz oteleme). Sahibin " +
                 "elindeki silaha dokunulmaz (VisualOnly).")]
        public bool reachPull = true;
        [Tooltip("Erisim cekmesinin ust siniri (m).")]
        public float maxReachPull = 0.45f;

        // Govde ekseni (kalca -> boyun). Bas DISARIDA: nisan alirken silah yuze yaklasir,
        // orayi itmek dogrudan nisan hattini bozuk gosterirdi.
        Transform _hips, _neck;

        // Silahin kaba sekli, tutus basina BIR KEZ olculur (her kare renderer taramak
        // pahali). EL BASINA bir yuva: cift tabancada iki elde iki ayri silah var.
        struct WeaponShape
        {
            public Transform of;
            public Vector3 center, axis;
            public float halfLen, halfThick;
            public bool ok;
        }
        WeaponShape _shapeL, _shapeR;

        // BU KARENIN itmesi, ana el basina. SOZLESME: karede BIR KEZ, ERKENDEN hesaplanir
        // (SolveShift) ve hem IK'nin okudugu hedefe (TryGetWristTarget, sira 0) hem weld'in
        // kendi yazimina (sira 110) AYNI deger girer. Yalniz 110'da uygulansaydi IK kolu
        // eski hedefe cozer, weld bilegi yeni hedefe yazar, aradaki fark deriyi gererdi.
        Vector3 _pushL, _pushR, _pushVelL, _pushVelR;   // govde itmesi (yumusatma durumu)
        Vector3 _shiftL, _shiftR;                        // bu karenin TOPLAM kaydirmasi: itme + erisim cekmesi
        int _shiftFrame = -1;
        bool _appliedL, _appliedR;   // silah bu kare itildi mi (cifte uygulama olmasin)
        float _pushLogAt;            // dogrulama izi: en son ne zaman yazildi (saniye)
        float _lateLogAt;            // gec kelepce izi

        // GORSEL KAYDIRMA KAYDI. Izleyici kaydirmasi (govde itmesi + erisim cekmesi) silahin
        // transformuna yaziliyor; sunucu da bir izleyici ve MuzzleWallBlock/NetworkWeapon
        // sunucuda KENDI kopyasinin namlusunu okuyor. Kaydirma oraya sizmasin diye burada
        // "silah su an bu konumdayken su kadar kaydirilmis" tutulur. Ag konumu yeniden
        // yazildiysa (konum kayittakinden farkli) kaydirma zaten transformda degildir -> sifir.
        struct VisualShiftEntry { public Vector3 at; public Vector3 shift; }
        static readonly System.Collections.Generic.Dictionary<Transform, VisualShiftEntry> _visualShift =
            new System.Collections.Generic.Dictionary<Transform, VisualShiftEntry>();

        /// <summary>Bu silahin transformunda SU AN duran izleyici kaydirmasi (yoksa sifir).
        /// Sunucu-otorite okumalar (namlu-duvar, atis gozlemi) bunu cikararak mantiksal
        /// konuma doner; gorsel okumalar (alev/iz) cikarmaz.</summary>
        public static Vector3 VisualShiftOf(Transform weapon)
        {
            if (weapon == null) return Vector3.zero;
            VisualShiftEntry e;
            if (!_visualShift.TryGetValue(weapon, out e)) return Vector3.zero;
            return (weapon.position - e.at).sqrMagnitude < 1e-8f ? e.shift : Vector3.zero;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _visualShift.Clear(); }

        static void RecordVisualShift(Transform weapon, Vector3 shift)
        {
            if (weapon == null) return;
            if (shift.sqrMagnitude < 1e-12f) { _visualShift.Remove(weapon); return; }
            _visualShift[weapon] = new VisualShiftEntry { at = weapon.position, shift = shift };
        }

        // Sahiplik bir kez aranir. Bulunamazsa (ag yok: editor tezgahi) gorsel kopya
        // sayilir - orada zaten disaridan bakiliyor, oynanis diye bir sey yok.
        NetworkObject _netObj;
        bool _netLooked;

        /// <summary>Bu avatar YEREL oyuncu tarafindan surulmuyor mu?
        ///
        /// Govde temizligi yalnizca burada calisir. Temizlik silahin transformunu oynatiyor;
        /// o transform nisani ve elindeki hissi belirliyor. Sahibinin makinesinde uygulansa
        /// silah kumandadan KAYARDI (sahada denendi: "birden konum degistiriyor, sikisiyor").
        /// Sahip birinci sahista kendi govdesini zaten gormuyor, duzeltilecek goruntu yok.
        /// Isabet sunucuda silahin gercek transformundan hesaplanir: kaydirma kozmetiktir.</summary>
        bool VisualOnly
        {
            get
            {
                // SUNUCUDA DA CALISIR. Bir denemede burada "sunucuda asla kaydirma" kapisi
                // vardi; gerekce, kaydirmanin silahin AG KOKUNE yazilmasi ve collider'i da
                // tasimasiydi. Ama PC ekrani (yani izlenen goruntu) SUNUCUNUN kendi render'i:
                // kapi acikken destek eli kundaktan geride kaliyor ve dis gorunus bozuluyordu.
                //
                // Collider endisesi bu arada BASKA duzeltmelerle karsilandi: hitscan artik
                // aticinin tasidigi silahlari atliyor (WeaponHitscanServer.IsHeldBy) ve bomba
                // siper testi yalnizca KATI dunyayi sayiyor (WorldSolids.IsSolid, rigidbody'liyi
                // eler) — yani tasinan silahin kaymis collider'i bu iki yolu da etkilemiyor.
                // Namlu okumalari zaten VisualShiftOf ile telafi ediliyor.
                //
                // KALAN PAY: hedefin elindeki silah isini hala fiziksel engel sayiyor ve o silah
                // sunucuda 0.80 m'ye kadar kaymis olabilir. Bunu tamamen bitirmenin yolu
                // kaydirmayi ag kokune degil silahin altindaki GORSEL bir cocuk transformuna
                // uygulamak; o 18 prefabta yapisal degisiklik, ayri bir is.
                if (!_netLooked) { _netObj = GetComponentInParent<NetworkObject>(); _netLooked = true; }
                return _netObj == null || !_netObj.IsOwner;
            }
        }

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _ik = GetComponent<AvatarIKController>();
            if (_anim != null && _anim.isHuman)
            {
                _leftBone = _anim.GetBoneTransform(HumanBodyBones.LeftHand);
                _rightBone = _anim.GetBoneTransform(HumanBodyBones.RightHand);
                _leftUpper = _anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _leftLower = _anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _rightUpper = _anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
                _rightLower = _anim.GetBoneTransform(HumanBodyBones.RightLowerArm);

                // Boy BIR KERE, henuz hicbir weld calismadan olculuyor - yoksa
                // kendi yazdigimiz bilek konumunu geri okurduk (bkz. ArmReach).
                _leftArmLen = ArmReach.MeasureLocal(_leftUpper, _leftLower, _leftBone);
                _rightArmLen = ArmReach.MeasureLocal(_rightUpper, _rightLower, _rightBone);

                _hips = _anim.GetBoneTransform(HumanBodyBones.Hips);
                _neck = _anim.GetBoneTransform(HumanBodyBones.Neck);
                if (_neck == null) _neck = _anim.GetBoneTransform(HumanBodyBones.UpperChest);
                if (_neck == null) _neck = _anim.GetBoneTransform(HumanBodyBones.Chest);
            }
        }

        /// <summary>Weld one hand onto the weapon (store-only; applied every LateUpdate).</summary>
        public void SetHand(bool left, Transform weapon, WeaponGripProfile profile,
            bool isSupport, bool mirrored)
        {
            // POZ SECIMI VE AYNALAMA TEK KARARDIR. Silah ters elde tutuluyorsa profilde o
            // duruma AYRI bir poz yazilmis olabilir; o zaman aynalanmaz, oldugu gibi kullanilir.
            // Yazilmamissa eski yola dusulur (sag-el pozunu aynala). Ikisini ayri yerlerde
            // karar vermek, elin silahin icinde durmasina yol acardi.
            bool poseMirror;
            var pose = profile.PoseFor(isSupport, mirrored, out poseMirror);
            // WeaponGrip re-applies on every replicated state change — keep an in-progress
            // engage ramp instead of restarting it, but a fresh weld (or a re-grab caught
            // mid-fade-out) ramps in from now.
            var prev = left ? _left : _right;
            var w = new HandWeld
            {
                active = true,
                weapon = weapon,
                profile = profile,
                isSupport = isSupport,
                bone = left ? _leftBone : _rightBone,
                wristLocalPos = poseMirror ? WeaponGripMath.MirrorX(pose.wristLocalPosition) : pose.wristLocalPosition,
                wristLocalRot = poseMirror ? WeaponGripMath.MirrorX(Quaternion.Euler(pose.wristLocalEuler)) : Quaternion.Euler(pose.wristLocalEuler),
                mirrored = mirrored,
                // Fade-out ORTASINDA yakalanan yeniden tutus: ramp sifirdan baslasaydi agirlik
                // o karede (or.) 0.4'ten 0'a dusup bilek bir karelik IK pozuna sicrardi.
                // Baslangic, kesilen fade'in GUNCEL agirligina denk gelecek sekilde geri
                // tarihlenir — agirlik surekli kalir.
                blendStart = prev.active && !prev.fadingOut ? prev.blendStart
                    : prev.active && prev.fadingOut
                        ? Time.time - Mathf.Clamp01(1f - (Time.time - prev.fadeOutStart) / WeldBlendSeconds) * WeldBlendSeconds
                        : Time.time,
            };
            // Taze tutus: onceki silahin itmesi devralinmasin (izleyende bir karelik sicrama).
            if (!prev.active && !isSupport)
            {
                if (left) { _pushL = Vector3.zero; _pushVelL = Vector3.zero; }
                else { _pushR = Vector3.zero; _pushVelR = Vector3.zero; }
            }

            if (left) _left = w; else _right = w;
            enabled = true;
        }

        /// <summary>Bu elin weld'ini sondurur. <paramref name="weapon"/> verilirse yalnizca
        /// yuva GERCEKTEN o silaha aitse calisir.
        ///
        /// NEDEN: eski imza yalnizca eli aliyordu. A silahi kemere konup (despawn) AYNI karede
        /// B ayni ele alindiginda, once B'nin WeaponGrip'i SetHand yapiyor, sonra A'nin
        /// temizligi kosup B'NIN yuvasini fadingOut ediyordu; B'nin grip'i _dirty=false oldugu
        /// icin bir daha Evaluate etmiyor -> el silaha hic yapismiyor, parmaklar acik kaliyor
        /// ve tutus bitene kadar duzelmiyordu ("bazen el silaha yapismiyor").</summary>
        public void ClearHand(bool left, Transform weapon = null)
        {
            if (weapon != null)
            {
                ref HandWeld cur = ref (left ? ref _left : ref _right);
                if (cur.active && cur.weapon != weapon) return;   // yuva baskasinin
            }

            // Don't cut the weld in one frame — fade the wrist back to its IK/animator pose.
            // The weld stays "active" (and this component enabled) until the fade finishes.
            if (left)
            {
                if (_left.active && !_left.fadingOut) { _left.fadingOut = true; _left.fadeOutStart = Time.time; }
            }
            else
            {
                if (_right.active && !_right.fadingOut) { _right.fadingOut = true; _right.fadeOutStart = Time.time; }
            }
        }

        void LateUpdate()
        {
            if (_anim == null || !_anim.isHuman) return;
            SolveShift();   // IK cagirmadiysa bile bu karenin itmesi hazir olsun
            WeldSide(ref _left, true);
            WeldSide(ref _right, false);
        }

        /// <summary>
        /// Bilegin GIDECEGI poz. <see cref="WeldSide"/> bunu uygular; <see cref="AvatarIKController"/>
        /// ise IK hedefini buna kurmak icin ONCEDEN sorar.
        ///
        /// NEDEN AYRI: eskiden kol IK'si kumandadan turetilen bir hedefe cozuluyor, weld ise
        /// bilegi silaha MUTLAK yaziyordu. Iki sistem farkli yerleri isteyince ust kol/on kol
        /// bir poza, bilek baska poza gidiyor ve arada kalan deri geriliyordu — silah tutunca
        /// gorulen bozulma buydu. IK hedefi de buraya kurulunca kol zaten bilegin varacagi yere
        /// cozuluyor, weld'in duzeltecek bir seyi kalmiyor.
        /// </summary>
        public bool TryGetWristTarget(bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            ref HandWeld w = ref (left ? ref _left : ref _right);

            // Sonmekte olan weld'e IK'yi baglamayiz: fade'in VARIS noktasi zaten kumandadan
            // turetilen poz, oraya cekilmesi dogru.
            if (!w.active || w.fadingOut) return false;
            if (w.weapon == null || w.profile == null || w.bone == null) return false;

            SolveShift();
            ComputeTarget(ref w, left, PendingShift(ref w, left), out pos, out rot);
            return true;
        }

        /// <summary>
        /// Elin silah uzerinde OTURDUGU nokta (bilek offset'i uygulanmadan onceki
        /// tutamak/ray noktasi). Birinci sahis eli bunu kullanir: destek eli
        /// kundaga yapisik kalsin diye. Bilek hedefinden farkli olarak burada
        /// avatarin bilek kemigi konvansiyonu yok, dolayisiyla FP gorseli icin
        /// donus donusturmesi gerekmez.
        /// </summary>
        public bool TryGetHandAnchor(bool left, out Vector3 pos, out bool isSupport)
            => TryGetHandAnchor(left, out pos, out _, out isSupport);

        /// <summary>
        /// Cipanin konumu VE yonelimi. Birinci sahis eli yonelime de ihtiyac duyuyor:
        /// eli SILAHIN cercevesine gore yerlestiriyor, kumandanin cercevesine gore degil.
        /// Boylece elin kabzadaki yeri authored bir veri oluyor (fpWristLocal*) ve
        /// avatarin bilek konvansiyonuna bagimlilik kalkiyor.
        /// </summary>
        public bool TryGetHandAnchor(bool left, out Vector3 pos, out Quaternion rot, out bool isSupport)
        {
            pos = Vector3.zero; rot = Quaternion.identity; isSupport = false;
            ref HandWeld w = ref (left ? ref _left : ref _right);
            if (!w.active || w.fadingOut) return false;
            if (w.weapon == null || w.profile == null) return false;
            isSupport = w.isSupport;
            ComputeAnchor(ref w, left, Vector3.zero, false, out pos, out rot);
            return true;
        }

        /// <summary>
        /// Bu elin su an hangi profille, hangi rolde kaynakli oldugu. Birinci sahis eli
        /// parmak pozunu buradan aliyor - authored tutus pozu tek kaynak olsun diye
        /// (avatarin elleri de ayni profili kullaniyor, bkz. ProceduralFingerPoser).
        /// </summary>
        public bool TryGetHandProfile(bool left, out WeaponGripProfile profile, out bool isSupport)
            => TryGetHandProfile(left, out profile, out isSupport, out _);

        /// <summary>
        /// Profil + rol + AYNALANMIS MI. Aynalama, silah ters elle (ana el SOL) tutuldugunda
        /// devreye girer: profildeki degerler sag-el yazimidir, weld hepsini MirrorX'ten
        /// gecirir. Birinci sahis elinin kendi offsetleri (fpWristLocal*) de ayni islemden
        /// gecmeli - gecmezse aynalanmis bir cipaya aynalanmamis bir offset biner ve el
        /// ters durur (cihazda goruldu: basparmak asagi bakiyor).
        /// </summary>
        public bool TryGetHandProfile(bool left, out WeaponGripProfile profile, out bool isSupport,
                                      out bool mirrored)
        {
            ref HandWeld w = ref (left ? ref _left : ref _right);
            profile = w.profile;
            isSupport = w.isSupport;
            mirrored = w.mirrored;
            return w.active && !w.fadingOut && profile != null;
        }

        /// <summary>Bu elin su an kaynakli oldugu silah (teshis icin).</summary>
        public bool TryGetHeldWeapon(bool left, out Transform weapon)
        {
            ref HandWeld w = ref (left ? ref _left : ref _right);
            weapon = w.weapon;
            return w.active && !w.fadingOut && weapon != null;
        }

        /// <summary>shift: bu kare silaha uygulanacak ama HENUZ uygulanmamis govde itmesi.
        /// Hedef, silah sanki coktan itilmis gibi hesaplanir; sira 0'daki IK ile sira
        /// 110'daki weld ayni noktayi gorur.</summary>
        void ComputeTarget(ref HandWeld w, bool left, Vector3 shift,
            out Vector3 targetPos, out Quaternion targetRot)
        {
            ComputeAnchor(ref w, left, shift, true, out Vector3 anchorPos, out Quaternion anchorRot);
            // Anchor on the (scaled) weapon; the wrist offset is authored in meters (hand-sized,
            // independent of the weapon's scale).
            targetPos = anchorPos + anchorRot * w.wristLocalPos;
            targetRot = anchorRot * w.wristLocalRot;
        }

        /// <summary>slideToReach: destek elinin ray noktasini kolun ERISIMI icinde tutacak
        /// sekilde ray boyunca kaydir (avatar bilegi). Birinci sahis eli icin kapali: onun
        /// kolu yok, kumandanin izdusumunde kalmasi dogru.</summary>
        void ComputeAnchor(ref HandWeld w, bool left, Vector3 shift, bool slideToReach,
            out Vector3 anchorPos, out Quaternion anchorRot)
        {
            // Cerceve karari PROFILDE (bkz. WeaponGripProfile.GripAnchorLocal) - tezgah da
            // ayni yardimcilari cagiriyor, boylece ikisi ayrisamiyor.
            Vector3 anchorLocal;
            Quaternion anchorLocalRot = w.profile.AnchorLocalRotation(w.mirrored);

            if (!w.isSupport)
            {
                anchorLocal = w.profile.GripAnchorLocal();
            }
            else
            {
                // Slide along the rail: project this hand's networked carrier onto the segment,
                // in the weapon's (possibly mirrored) local space.
                Vector3 rs, re;
                w.profile.SupportRailLocal(out rs, out re);
                Vector3 s = w.weapon.TransformPoint(rs) + shift;
                Vector3 e = w.weapon.TransformPoint(re) + shift;
                Transform carrier = _ik != null ? (left ? _ik.leftHandSource : _ik.rightHandSource) : null;
                Vector3 probe = carrier != null ? carrier.position : w.bone.position;
                float t = WeaponGripMath.RailClosestT(s, e, probe);

                // ERISIM DISINDA RAY BOYUNCA KAY. Eski yol: hedef, WeldSide'daki ArmReach
                // kelepcesiyle omuza dogru RADYAL kistiriliyordu; o nokta rayin DISINDA kalir,
                // el handguard'dan kopup geriye kayar ("silahi ileri uzatinca ikinci el
                // yerinden cikip geri gidiyor"). Gercek insan kolu yetmeyince destek elini
                // ray uzerinde kendine dogru kaydirir - ayni sey. Bilek hedefi t'de dogrusal
                // (uclardaki iki hedef arasinda Lerp), bu yuzden erisim kisiti dogrudan t
                // araligina cevrilir. Rayin TAMAMI erisim disindaysa radyal kelepce yine
                // son care olarak WeldSide'da kalir.
                if (slideToReach)
                {
                    Vector3 wristOff = (w.weapon.rotation * anchorLocalRot) * w.wristLocalPos;
                    t = SlideWithinReach(s + wristOff, e + wristOff, t, left);
                }
                anchorLocal = Vector3.Lerp(rs, re, t);
            }

            anchorPos = w.weapon.TransformPoint(anchorLocal) + shift;
            anchorRot = w.weapon.rotation * anchorLocalRot;
        }

        // ------------------------- kare basi govde itmesi -------------------------

        /// <summary>Bu karenin itmesini ana el basina BIR KEZ hesaplar. Ilk cagiran
        /// hesaplatir: normalde sira 0'da IK (TryGetWristTarget), IK yoksa weld'in kendisi.</summary>
        void SolveShift()
        {
            if (Time.frameCount == _shiftFrame) return;
            _shiftFrame = Time.frameCount;
            _appliedL = _appliedR = false;
            _shiftL = SolveSidePush(ref _left, true, ref _shapeL, ref _pushL, ref _pushVelL);
            _shiftR = SolveSidePush(ref _right, false, ref _shapeR, ref _pushR, ref _pushVelR);
        }

        /// <summary>Bu ana elin silahi icin karenin toplam kaydirmasi: govde itmesi (yumusatilmis)
        /// + erisim cekmesi (her kare sifirdan, birikmez). Yalniz izleyen kopyada.</summary>
        Vector3 SolveSidePush(ref HandWeld w, bool left, ref WeaponShape shape,
            ref Vector3 push, ref Vector3 pushVel)
        {
            if (!VisualOnly ||
                !w.active || w.fadingOut || w.isSupport ||
                w.weapon == null || w.profile == null || w.bone == null)
            {
                push = Vector3.zero; pushVel = Vector3.zero;
                return Vector3.zero;
            }

            Vector3 want = Vector3.zero;
            if (bodyClearance)
            {
                if (shape.of != w.weapon) MeasureWeaponShape(w.weapon, ref shape);
                want = BodyClearPush(w.weapon, w.bone, shape);
            }
            push = Vector3.SmoothDamp(push, want, ref pushVel, Mathf.Max(0.01f, clearanceSmoothing));
            Vector3 shift = push;

            // ERISIM CEKMESI ("silah ele gelir", izleyici). Radyal kelepce eli silahtan
            // koparip omuz-hedef cizgisinde asili birakiyordu: Sniper1'de nisan pozunda
            // destek noktasi sol kolun erisimini 12-28 cm asiyor, el dürbun hizasinda
            // havada kaliyordu (kare sonu sondasiyla olculdu). Silah tasma kadar geri
            // gelirse el silahin ustunde kalir. Iki el icin iki gecis: once ana el, sonra
            // AYNI silahi tutan destek eli, sonra tekrar ana el (biri digerini bozmasin).
            if (reachPull)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    ComputeTarget(ref w, left, shift, out Vector3 tm, out _);
                    shift += ArmReach.Clamp(tm, left ? _leftUpper : _rightUpper,
                                            left ? _leftArmLen : _rightArmLen) - tm;

                    ref HandWeld o = ref (left ? ref _right : ref _left);
                    if (o.active && !o.fadingOut && o.isSupport && o.weapon == w.weapon &&
                        o.profile != null && o.bone != null)
                    {
                        ComputeTarget(ref o, !left, shift, out Vector3 ts, out _);
                        shift += ArmReach.Clamp(ts, left ? _rightUpper : _leftUpper,
                                                left ? _rightArmLen : _leftArmLen) - ts;
                    }
                }
                Vector3 pull = shift - push;
                if (pull.sqrMagnitude > maxReachPull * maxReachPull)
                    shift = push + pull.normalized * maxReachPull;
            }

            // SON SOZ ANA ELDE - SINIR ELE DEGIL ITMEYE KONUR.
            //
            // Sahada gorulen: "tetik eli yanlis gozukuyor, geride duruyor". Sebep, kaydirmanin
            // silahi kol erisiminin DISINA tasiyabilmesiydi. Oyle olunca WeldSide'daki
            // ArmReach.Clamp devreye giriyor ve BILEGI omza dogru geri cekiyor - ama silah
            // itilmis yerinde kaliyor. Sonuc: el kabzadan kopar, silahin gerisinde havada asili
            // kalir. Kelepcenin kendisi dogru (kol uzayamaz); yanlis olan, kozmetik bir
            // duzeltmenin TUTUSU bozabilmesiydi.
            //
            // Ustteki iki onlem bunu garanti etmiyordu: govde itmesi yalnizca maxBodyPush ile,
            // erisim cekmesi yalnizca maxReachPull ile sinirli - ikisi de kola degil sabit bir
            // sayiya bakan tavanlar. Cekme tavana dayandigi anda artik kalir.
            //
            // Burada ana elin erisimi SON kisit olarak uygulanir. Ana el icin hedef, kaydirmanin
            // AFIN fonksiyonudur (cipa silah-yerel sabit: GripAnchorLocal), yani
            // hedef(kaydirma) = taban + kaydirma. Bu yuzden kelepce farkini kaydirmaya eklemek
            // hedefi TAM erisim kuresine oturtur - tek adimda, yinelemesiz. Boylece WeldSide'daki
            // kelepce ana el icin islevsiz kalir (ayni girdi, zaten kelepcelenmis hedef) ve el
            // kabzadan kopamaz. Destek eli icin ayni sey gecerli degil: onun cipasi ray uzerinde
            // kaydigi icin bagimlilik afin degil, o yuzden dongu yukarida kaliyor.
            //
            // maxReachPull tavanini asabilir: tutusun bozulmamasi tavandan onceliklidir.
            ComputeTarget(ref w, left, shift, out Vector3 fin, out _);
            Vector3 kirpma = ArmReach.Clamp(fin, left ? _leftUpper : _rightUpper,
                                            left ? _leftArmLen : _rightArmLen) - fin;
            shift += kirpma;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // DOGRULAMA IZI — yalniz editor/gelistirme build'inde.
            //
            // ESKI HALI YANILTICIYDI: tutus basina TEK satir yaziyordu (_pushLogged), yani
            // silahin govdeye EN COK girdigi ani degil, ILK degdigi ani kaydediyordu. Kayitta
            // "itme 0-1 cm" gorunmesinin sebebi buydu; silahin kemerde govdeyi kestigi an hic
            // loglanmamisti. Artik itme ISTENDIGI surece saniyede bir yaziliyor.
            //
            // OLCU DOGRUDAN OLMALI. Ilk surumde "feda" diye istenen-uygulanan farki
            // yaziliyordu ve bu YANLIS SEYI olcuyordu: aradaki farkin buyuk kismi erisim
            // kirpmasi degil, itmenin SmoothDamp gecikmesiydi (push, want'a 0.08 s'de yetisir).
            // Sahada her satirda sabit "feda 3 cm" cikmasinin sebebi buydu - buyuklukten
            // bagimsiz sabit bir fark, kirpma degil gecikme imzasidir. Ustelik reachPull
            // ekleme de yapabildigi icin uygulanan bazen istenenden BUYUK cikiyor ve fark
            // negatife dusuyordu.
            //
            // Artik kirpma, hesaplandigi yerden dogrudan aliniyor (yukaridaki "kirpma").
            // Gecikmeyi de ayrica gostermek ise gereksiz: onemli olan kolun ne kadarini
            // yuttugu.
            float istenen = want.magnitude * 100f;
            float uygulanan = shift.magnitude * 100f;
            float erisimKirpmasi = kirpma.magnitude * 100f;
            float bekle = erisimKirpmasi > 3f ? 1f : 10f;
            if (istenen > 2f && Time.time - _pushLogAt > bekle)
            {
                _pushLogAt = Time.time;
                Debug.Log($"[GovdeTemizligi] {w.weapon.name} ({(left ? "sol" : "sag")} el): " +
                          $"istenen {istenen:0} cm, uygulanan {uygulanan:0} cm, " +
                          $"kol kirpmasi {erisimKirpmasi:0} cm");
            }
#endif
            return shift;
        }

        /// <summary>Bu elin hedefine eklenmesi gereken, HENUZ silaha uygulanmamis itme.
        /// Ana el kendininkini, destek eli AYNI silahi tutan ana elinkini kullanir. Silah bu
        /// kare coktan itildiyse sifir - cifte sayim olmaz. Sira bagimsiz: destek eli ana
        /// elden ONCE de SONRA da islense ayni noktayi bulur.</summary>
        Vector3 PendingShift(ref HandWeld w, bool left)
        {
            if (!w.isSupport)
                return (left ? _appliedL : _appliedR) ? Vector3.zero : (left ? _shiftL : _shiftR);

            bool mainLeft = !left;
            bool mainHolds = mainLeft
                ? _left.active && !_left.isSupport && _left.weapon == w.weapon
                : _right.active && !_right.isSupport && _right.weapon == w.weapon;
            if (!mainHolds) return Vector3.zero;
            if (mainLeft ? _appliedL : _appliedR) return Vector3.zero;
            return mainLeft ? _shiftL : _shiftR;
        }

        /// <summary>Silahin kaba sekli (merkez, uzun eksen, yari boy, yari kalinlik), silahin
        /// KENDI yerel uzayinda. Renderer.localBounds + kose donusumu: Renderer.bounds dunyada
        /// eksen-hizali oldugu icin donmus silahta sahte sisme uretirdi. Kalinlik test
        /// yaricapina EKLENIR: yalniz orta cizgi test edilseydi namlu/sarjor iceride kalirdi.</summary>
        void MeasureWeaponShape(Transform weapon, ref WeaponShape sh)
        {
            sh.of = weapon;
            sh.ok = false;
            if (weapon == null) return;

            bool any = false;
            Vector3 lo = Vector3.zero, hi = Vector3.zero;
            var rends = weapon.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                var lb = r.localBounds;
                Vector3 c = lb.center, e = lb.extents;
                for (int k = 0; k < 8; k++)
                {
                    var corner = c + new Vector3(
                        (k & 1) == 0 ? -e.x : e.x,
                        (k & 2) == 0 ? -e.y : e.y,
                        (k & 4) == 0 ? -e.z : e.z);
                    Vector3 p = weapon.InverseTransformPoint(r.transform.TransformPoint(corner));
                    if (!any) { lo = hi = p; any = true; }
                    else { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                }
            }
            if (!any) return;

            sh.center = (lo + hi) * 0.5f;
            Vector3 ext = (hi - lo) * 0.5f;
            int ax = ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2);
            sh.axis = ax == 0 ? Vector3.right : ax == 1 ? Vector3.up : Vector3.forward;
            sh.halfLen = ext[ax];
            sh.halfThick = (ext[(ax + 1) % 3] + ext[(ax + 2) % 3]) * 0.5f;
            sh.ok = sh.halfLen > 1e-3f;
        }

        /// <summary>Silahi govdenin disina cikaracak YATAY itme (yoksa sifir).
        ///
        /// Govde: kalca-boyun ekseni etrafinda ELIPS kesitli silindir - yan yari genisligi
        /// bodyRadius, on/arka yari derinligi bodyDepth (avatar kokunun ileri yonu). Daire
        /// kesit gogus onunu de "icerisi" sayiyordu: iki elle gogus onunde tutulan tabanca
        /// 18 cm one itiliyor, sonra erisim cekmesi geri cekiyor, silah havada kaliyordu.
        /// Uclarin disindaki ornekler yok sayilir (bacak hizasi itilmez, yuze yaklastirilan
        /// silah da itilmez - nisan hatti). Itme eksene dik: silah yukari/asagi kaymaz.</summary>
        Vector3 BodyClearPush(Transform weapon, Transform hand, in WeaponShape sh)
        {
            if (!sh.ok) return Vector3.zero;

            // GOVDE OLCUSU TEK YERDEN: BodyVolume. Ayni cerceveyi dirsek yonlendirmesi ve
            // bos el temizligi de kullaniyor; eskiden her biri kendi sayisini tasidigi icin
            // olculer sessizce ayrismisti (dirsek 0.20, silah 0.22).
            // Bant KALCADA biter (HipExtend kullanilmiyor): uzatma denendi, itmeleri buyuttu
            // ve tutusun tamami oyuncunun elinden uzaklasti. Kalca hizasindaki silahin govdeye
            // girmesi kabul ediliyor.
            var frame = BodyVolume.Make(_hips, _neck, transform.forward);
            if (!frame.ok) return Vector3.zero;

            float thick = sh.halfThick * Mathf.Abs(weapon.lossyScale.x);
            float ra = bodyRadius + thick;                                // yan
            float rb = (frame.ellipse ? bodyDepth : bodyRadius) + thick;  // on/arka

            const int Samples = 11;
            float best = 0f;
            Vector3 bestPush = Vector3.zero;
            bool hit = false;

            for (int i = 0; i < Samples; i++)
            {
                float t = (i / (float)(Samples - 1)) * 2f - 1f;
                Vector3 p = weapon.TransformPoint(sh.center + sh.axis * (sh.halfLen * t));

                float pen;
                Vector3 push = BodyVolume.PushOut(in frame, p, ra, rb, out pen);
                if (pen <= best) continue;

                best = pen;
                hit = true;
                bestPush = push;
            }

            if (!hit) return Vector3.zero;

            if (bestPush.sqrMagnitude < 1e-8f)
            {
                // Silah tam eksenin ustunde: yon belirsiz, eli tutan taraf disari.
                Vector3 axis = frame.axisVec.normalized;
                Vector3 fallback = hand != null
                    ? hand.position - (frame.basePoint + frame.axisVec * 0.5f)
                    : transform.right;
                fallback -= axis * Vector3.Dot(fallback, axis);
                if (fallback.sqrMagnitude < 1e-6f) return Vector3.zero;
                bestPush = fallback.normalized * best;
            }

            if (bestPush.sqrMagnitude > maxBodyPush * maxBodyPush)
                bestPush = bestPush.normalized * maxBodyPush;
            return bestPush;
        }

        /// <summary>Raydaki t'yi, bilek hedefi (t0..t1 dogrusu) omuzun erisim kuresi ICINDE
        /// kalacak sekilde kaydirir. Yaricap ArmReach.Clamp ile AYNI formul. Hic kesisim yoksa
        /// rayin omza en yakin t'si doner (radyal kelepce sonra devreye girer).</summary>
        float SlideWithinReach(Vector3 t0, Vector3 t1, float tWant, bool left)
        {
            Transform up = left ? _leftUpper : _rightUpper;
            float lenLocal = left ? _leftArmLen : _rightArmLen;
            if (up == null || lenLocal <= 0.001f) return tWant;
            float scale = up.lossyScale.x;
            if (scale <= 0.0001f) scale = 1f;
            float r = lenLocal * scale * ArmReach.StraightFraction;
            if (r <= 0.01f) return tWant;

            Vector3 a = t0 - up.position;
            Vector3 b = t1 - t0;
            if ((a + b * tWant).sqrMagnitude <= r * r) return tWant;   // istenen zaten erisimde

            // |a + t*b|^2 = r^2: kurenin ray dogrusunu kestigi t araligi (ikinci derece).
            float bb = Vector3.Dot(b, b);
            if (bb < 1e-8f) return tWant;                              // ray tek nokta
            float ab = Vector3.Dot(a, b);
            float disc = ab * ab - bb * (a.sqrMagnitude - r * r);
            if (disc <= 0f) return Mathf.Clamp01(-ab / bb);          // hic girmiyor: en yakin
            float sq = Mathf.Sqrt(disc);
            float tLo = Mathf.Max(0f, (-ab - sq) / bb);
            float tHi = Mathf.Min(1f, (-ab + sq) / bb);
            if (tLo > tHi) return Mathf.Clamp01(-ab / bb);            // kesisim [0,1] disinda
            return Mathf.Clamp(tWant, tLo, tHi);
        }

        void WeldSide(ref HandWeld w, bool left)
        {
            if (!w.active) return;
            if (w.weapon == null || w.profile == null || w.bone == null)
            {
                // Weapon despawned mid-hold/fade: nothing left to weld to.
                if ((object)w.weapon != null) _visualShift.Remove(w.weapon);
                w.active = false;
                w.fadingOut = false;
                if (!_left.active && !_right.active) enabled = false;
                return;
            }

            ComputeTarget(ref w, left, PendingShift(ref w, left),
                out Vector3 targetPos, out Quaternion targetRot);

            // KOL UZAYAMAZ. Bu yazma mutlak (rig'den sonra), dolayisiyla kelepce olmadan
            // bilek silaha isinlaniyor ve el koldan kopmus gorunuyordu. Kelepce yalnizca
            // hedef kol boyunu ASTIGINDA calisir; normal tutusta hicbir sey degismez.
            // ROTASYON kelepcelenmez: kol duz kalsa bile el silahin yonune bakar.
            Vector3 kelepce = ArmReach.Clamp(targetPos,
                left ? _leftUpper : _rightUpper,
                left ? _leftArmLen : _rightArmLen) - targetPos;

            // SON KELEPCEYI EL DEGIL SILAH ODER (yalniz izleyen kopyada, yalniz ana el).
            //
            // SolveShift (sira 70) kaydirmayi, ana elin hedefi TAM erisim kuresine otursun
            // diye coziyor - yani buradaki kelepcenin islevsiz kalmasi gerekirdi. Ama arada
            // OMUZ OYNUYOR: AvatarCrouchPose (sira 90) comelme pozunu yaziyor ve ardindan
            // ayagi zemine oturtmak icin KOKU dusey kaydiriyor (ClampToGround). 70'te
            // verilen garanti 110'a gelindiginde gecersiz; kelepce devreye girip BILEGI
            // omza cekiyor, silah ise itilmis yerinde kaliyor -> el kabzadan kopuyor.
            // (Kesin yer: AvatarCrouchPose.ClampToGround, "transform.position += up * lift" -
            // bacak pozu degisince kok yukari itiliyor, omuz da onunla birlikte.)
            // Sahada "tetik eli yine bozuldu" diye goruldu; govde derinligi olculen degere
            // cekilince itmeler 3 cm'den 23 cm'e ciktigi icin artik gozle gorulur oldu.
            //
            // Cozum sirayi degistirmek DEGIL (comelme pozunun weld'den once kosmasi dogru).
            // Izleyen kopyada silahin gorunen konumu ZATEN bizim elimizde: kalan tasmayi
            // silaha yazariz, el de silahla birlikte gelir. Iki sart da korunur - kol
            // erisimin icinde, el kabzada.
            //
            // Sahibin kopyasinda ve DESTEK elinde eski yol gecerli: orada silahin konumu
            // bizim degil (VisualOnly false), destek elinin cipasi ise ray uzerinde kaydigi
            // icin zaten kendi kacisina sahip (SlideWithinReach).
            // GERI ALINDI. Bir tur boyunca kalan tasma silaha yaziliyordu ("el kabzada kalsin")
            // ama olcum gosterdi ki el zaten kopmuyordu: sapma 0.000 m. Yanlis hastaligi tedavi
            // ediyordu ve silahi oyuncunun elinden daha da uzaklastiriyordu. Kelepce yine
            // klasik yoldan ELE uygulanir.
            bool kelepceyiSilahaYaz = false;

            // GOVDE ITMESI silaha BIR KEZ uygulanir (SolveShift bu karede hesapladi, IK ayni
            // degeri gordu). Yalniz ana el, yalniz sonmuyorsa: birakilan silaha dokunulmaz.
            if (!w.isSupport && !w.fadingOut && !(left ? _appliedL : _appliedR))
            {
                Vector3 shift = (left ? _shiftL : _shiftR) + (kelepceyiSilahaYaz ? kelepce : Vector3.zero);
                if (shift.sqrMagnitude > 1e-10f) w.weapon.position += shift;
                RecordVisualShift(w.weapon, shift);
                if (left) _appliedL = true; else _appliedR = true;
            }

            // Hedef her iki yolda da kelepce kadar kayar: silaha yazildiysa el silahla
            // birlikte gitti, yazilmadiysa klasik kelepce (el kabzadan kopar) uygulandi.
            targetPos += kelepce;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // GEC KELEPCE IZI. SolveShift'teki kirpma (sira 70) ile BU kelepce (sira 110)
            // AYRI seylerdir: aradaki omuz hareketi yuzunden ikincisi sifir olmayabilir ve
            // tetik elinin kabzadan kopup kopmadigini belirleyen sayi tam olarak budur.
            // "silaha yazildi" ise el kabzada kalmistir; "ELE yazildi" ise kopmustur.
            float gec = kelepce.magnitude * 100f;
            if (gec > 1f && Time.time - _lateLogAt > 1f)
            {
                _lateLogAt = Time.time;
                Debug.Log($"[GecKelepce] {w.weapon.name} ({(left ? "sol" : "sag")} el, " +
                          $"{(w.isSupport ? "destek" : "ANA")}): {gec:0} cm — " +
                          (kelepceyiSilahaYaz ? "silaha yazildi (el kabzada kalir)"
                                              : "ELE yazildi (el kabzadan kopar)"));
            }
#endif

            // Engage/release weight. The bone's pose here is this frame's IK/animator result
            // (the weld runs after both), so a partial weight blends between that and the
            // weapon anchor — no one-frame wrist relocation on grab or release.
            float wgt;
            if (w.fadingOut)
            {
                wgt = 1f - Mathf.Clamp01((Time.time - w.fadeOutStart) / WeldBlendSeconds);
                if (wgt <= 0f)
                {
                    if (!w.isSupport && (object)w.weapon != null) _visualShift.Remove(w.weapon);
                    w.active = false;
                    w.fadingOut = false;
                    if (!_left.active && !_right.active) enabled = false; // empty tick off
                    return;
                }
            }
            else
                wgt = Mathf.Clamp01((Time.time - w.blendStart) / WeldBlendSeconds);

            if (wgt >= 1f)
            {
                w.bone.SetPositionAndRotation(targetPos, targetRot);
                return;
            }
            wgt = Mathf.SmoothStep(0f, 1f, wgt);
            w.bone.SetPositionAndRotation(
                Vector3.Lerp(w.bone.position, targetPos, wgt),
                Quaternion.Slerp(w.bone.rotation, targetRot, wgt));
        }
    }
}
