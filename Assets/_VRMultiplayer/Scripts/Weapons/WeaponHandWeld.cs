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
        [Tooltip("Govde ekseni etrafindaki yaricap (m). Silahin kendi kalinligi buna EKLENIR.")]
        public float bodyRadius = 0.22f;
        [Tooltip("Itmenin ust siniri (m). Kol erisimi (ArmReach) zaten ikinci bir sinir koyar.")]
        public float maxBodyPush = 0.35f;
        [Tooltip("Itmenin yumusama suresi (s). Sadece gorsel oldugu icin serbestce " +
                 "yumusatilabilir - izleyen oyuncu ani sicrama gormez.")]
        public float clearanceSmoothing = 0.08f;

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
        Vector3 _pushL, _pushR, _pushVelL, _pushVelR;
        int _shiftFrame = -1;
        bool _appliedL, _appliedR;   // silah bu kare itildi mi (cifte uygulama olmasin)
        Transform _pushLogged;       // dogrulama izi: tutus basina bir satir
        Transform _clampLoggedL, _clampLoggedR;   // GECICI TANI: erisim kelepcesi izi

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

        public void ClearHand(bool left)
        {
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
            SolveSidePush(ref _left, true, ref _shapeL, ref _pushL, ref _pushVelL);
            SolveSidePush(ref _right, false, ref _shapeR, ref _pushR, ref _pushVelR);
        }

        void SolveSidePush(ref HandWeld w, bool left, ref WeaponShape shape,
            ref Vector3 push, ref Vector3 pushVel)
        {
            if (!bodyClearance || !VisualOnly ||
                !w.active || w.fadingOut || w.isSupport ||
                w.weapon == null || w.profile == null || w.bone == null)
            {
                push = Vector3.zero; pushVel = Vector3.zero;
                return;
            }

            if (shape.of != w.weapon) MeasureWeaponShape(w.weapon, ref shape);
            Vector3 want = BodyClearPush(w.weapon, w.bone, shape);
            push = Vector3.SmoothDamp(push, want, ref pushVel, Mathf.Max(0.01f, clearanceSmoothing));

            // Dogrulama izi: tutus basina EN FAZLA BIR satir, yalniz itme gerektiginde.
            if (want.sqrMagnitude > 1e-6f && _pushLogged != w.weapon)
            {
                _pushLogged = w.weapon;
                Debug.Log($"[GovdeTemizligi] {w.weapon.name}: itme {want.magnitude * 100f:0} cm (sahip degil: {VisualOnly})");
            }
        }

        /// <summary>Bu elin hedefine eklenmesi gereken, HENUZ silaha uygulanmamis itme.
        /// Ana el kendininkini, destek eli AYNI silahi tutan ana elinkini kullanir. Silah bu
        /// kare coktan itildiyse sifir - cifte sayim olmaz. Sira bagimsiz: destek eli ana
        /// elden ONCE de SONRA da islense ayni noktayi bulur.</summary>
        Vector3 PendingShift(ref HandWeld w, bool left)
        {
            if (!w.isSupport)
                return (left ? _appliedL : _appliedR) ? Vector3.zero : (left ? _pushL : _pushR);

            bool mainLeft = !left;
            bool mainHolds = mainLeft
                ? _left.active && !_left.isSupport && _left.weapon == w.weapon
                : _right.active && !_right.isSupport && _right.weapon == w.weapon;
            if (!mainHolds) return Vector3.zero;
            if (mainLeft ? _appliedL : _appliedR) return Vector3.zero;
            return mainLeft ? _pushL : _pushR;
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

        /// <summary>Silahi govdenin disina cikaracak YATAY itme (yoksa sifir). Govde, kalca-boyun
        /// dogru parcasi etrafinda bir SILINDIR: uclarin disindaki ornekler yok sayilir - bacak
        /// hizasindaki silah itilmez, yuze yaklastirilan silah da itilmez (nisan hatti). Itme
        /// eksene dik: silah yukari/asagi kaymaz, govdenin yanina kayar.</summary>
        Vector3 BodyClearPush(Transform weapon, Transform hand, in WeaponShape sh)
        {
            if (_hips == null || _neck == null || !sh.ok) return Vector3.zero;

            Vector3 a = _hips.position;
            Vector3 ab = _neck.position - a;
            float abLen2 = ab.sqrMagnitude;
            if (abLen2 < 1e-6f) return Vector3.zero;

            float radius = bodyRadius + sh.halfThick * Mathf.Abs(weapon.lossyScale.x);

            const int Samples = 11;
            float best = 0f;
            Vector3 bestDir = Vector3.zero;
            bool hit = false;

            for (int i = 0; i < Samples; i++)
            {
                float t = (i / (float)(Samples - 1)) * 2f - 1f;
                Vector3 p = weapon.TransformPoint(sh.center + sh.axis * (sh.halfLen * t));

                Vector3 rel = p - a;
                float u = Vector3.Dot(rel, ab) / abLen2;
                if (u < 0f || u > 1f) continue;              // govde bandinin disinda

                Vector3 d = rel - ab * u;                     // eksene DIK
                float dist = d.magnitude;
                float pen = radius - dist;
                if (pen <= 0f || pen <= best) continue;

                best = pen;
                hit = true;
                bestDir = dist > 1e-4f ? d / dist : Vector3.zero;
            }

            if (!hit) return Vector3.zero;

            if (bestDir.sqrMagnitude < 1e-6f)
            {
                // Silah tam eksenin ustunde: yon belirsiz, eli tutan taraf disari.
                Vector3 fallback = hand != null ? hand.position - (a + ab * 0.5f) : transform.right;
                Vector3 axis = ab / Mathf.Sqrt(abLen2);
                fallback -= axis * Vector3.Dot(fallback, axis);
                if (fallback.sqrMagnitude < 1e-6f) return Vector3.zero;
                bestDir = fallback.normalized;
            }

            return bestDir * Mathf.Min(best, maxBodyPush);
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
                w.active = false;
                w.fadingOut = false;
                if (!_left.active && !_right.active) enabled = false;
                return;
            }

            ComputeTarget(ref w, left, PendingShift(ref w, left),
                out Vector3 targetPos, out Quaternion targetRot);

            // GOVDE ITMESI silaha BIR KEZ uygulanir (SolveShift bu karede hesapladi, IK ayni
            // degeri gordu). Yalniz ana el, yalniz sonmuyorsa: birakilan silaha dokunulmaz.
            if (!w.isSupport && !w.fadingOut && !(left ? _appliedL : _appliedR))
            {
                Vector3 push = left ? _pushL : _pushR;
                if (push.sqrMagnitude > 1e-10f) w.weapon.position += push;
                if (left) _appliedL = true; else _appliedR = true;
            }

            // KOL UZAYAMAZ. Bu yazma mutlak (rig'den sonra), dolayisiyla kelepce
            // olmadan bilek silaha isinlaniyor ve el koldan kopmus gorunuyordu.
            // Kelepce yalnizca hedef kol boyunu ASTIGINDA calisir; normal tutusta
            // hicbir sey degismez, yani "destek eli silaha tam guclu kaynakli
            // kalsin" kurali korunur. ROTASYON kelepcelenmez: kol duz kalsa bile
            // el silahin/kumandanin yonune bakmaya devam eder.
            Vector3 preClamp = targetPos;
            targetPos = ArmReach.Clamp(targetPos,
                left ? _leftUpper : _rightUpper,
                left ? _leftArmLen : _rightArmLen);

            // GECICI TANI: kelepce hedefi 2 cm'den fazla oynattiysa tutus basina bir satir.
            // "Destek eli silaha oturmuyor" sikayetini olcumle ayirmak icin.
            if ((targetPos - preClamp).sqrMagnitude > 0.0004f &&
                (left ? _clampLoggedL : _clampLoggedR) != w.weapon)
            {
                if (left) _clampLoggedL = w.weapon; else _clampLoggedR = w.weapon;
                Transform up = left ? _leftUpper : _rightUpper;
                float len = left ? _leftArmLen : _rightArmLen;
                float max = up != null ? len * up.lossyScale.x * ArmReach.StraightFraction : 0f;
                float dist = up != null ? Vector3.Distance(preClamp, up.position) : 0f;
                Debug.Log($"[Erisim] {w.weapon.name} {(left ? "SOL" : "SAG")} {(w.isSupport ? "destek" : "ana")}: " +
                          $"hedef omuzdan {dist:0.00} m, kol {max:0.00} m -> {(targetPos - preClamp).magnitude * 100f:0} cm kistirildi " +
                          $"(sahip degil: {VisualOnly}, olcek {(up != null ? up.lossyScale.x : 0f):0.00})");
            }

            // Engage/release weight. The bone's pose here is this frame's IK/animator result
            // (the weld runs after both), so a partial weight blends between that and the
            // weapon anchor — no one-frame wrist relocation on grab or release.
            float wgt;
            if (w.fadingOut)
            {
                wgt = 1f - Mathf.Clamp01((Time.time - w.fadeOutStart) / WeldBlendSeconds);
                if (wgt <= 0f)
                {
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
