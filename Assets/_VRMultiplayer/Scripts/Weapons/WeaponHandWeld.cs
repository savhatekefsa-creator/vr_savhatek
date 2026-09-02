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
            public Vector3 gripLocalPos;    // main-hand anchor (mirror-resolved); unused for support
            public Quaternion gripLocalRot; // mirror-resolved
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

        [Header("Govde temizligi (SADECE baskalarinin gordugu kopya)")]
        [Tooltip("Silah govdenin icine girdiginde disari itilir. YALNIZCA bu avatari SEN " +
                 "surmuyorsan uygulanir — yani karsindaki oyuncunun ekraninda. Kendi elindeki " +
                 "silahin konumuna hicbir kosulda dokunulmaz, bkz. VisualOnly.")]
        public bool bodyClearance = true;
        [Tooltip("Govde ekseni etrafindaki yaricap (m). Silahin kendi kalinligi buna EKLENIR.")]
        public float bodyRadius = 0.22f;
        [Tooltip("Itmenin ust siniri (m). Kol erisimi zaten ikinci bir sinir koyar.")]
        public float maxBodyPush = 0.35f;
        [Tooltip("Itmenin yumusama suresi (s). Sadece gorsel oldugu icin serbestce " +
                 "yumusatilabilir — izleyen oyuncu ani sicrama gormez.")]
        public float clearanceSmoothing = 0.08f;

        HandWeld _left, _right;
        Animator _anim;
        AvatarIKController _ik;
        Transform _leftBone, _rightBone;

        // Omuz (ust kol koku) + TAM kol boyu, BIND pozundan. Sonradan olcmek gerilmis
        // degeri okur: weld el kemiginin dunya pozunu yaziyor, yani hata olustugu anda
        // olcum zaten kirlenmis olur ve sinir her karede biraz daha genisler.
        Transform _upperL, _upperR;
        float _armLocalL, _armLocalR;

        // Govde ekseni (kalca -> boyun). Bas DISARIDA birakilir: nisan alirken silah yuze
        // yaklasir, orayi itmek dogrudan nisan hattini bozuk gosterirdi.
        Transform _hips, _neck;

        // Silahin kaba sekli, tutus basina BIR KEZ olculur (her kare renderer taramak
        // pahali). EL BASINA bir yuva: cift tabancada iki elde iki AYRI silah var; tek
        // onbellek her karede birinden oburune bosalip yeniden olculurdu.
        struct WeaponShape
        {
            public Transform of;
            public Vector3 center, axis;
            public float halfLen, halfThick;
            public bool ok;
        }
        WeaponShape _shapeL, _shapeR;

        // BU KARENIN silah kaydirmasi (govde itmesi + erisim tasmasi), ana el basina.
        //
        // SOZLESME: kaydirma karede BIR KEZ, ERKENDEN hesaplanir (SolveShift) ve hem IK'nin
        // okudugu hedefe (TryGetWristTarget, sira 0) hem weld'in kendi yazimina (sira 110)
        // AYNI deger olarak girer. Onceki surumde itme/tasma yalniz 110'da uygulaniyordu:
        // IK kolu ESKI hedefe cozmus oluyor, weld bilegi YENI hedefe mutlak yaziyordu —
        // aradaki fark deriyi geriyordu. Cift elde fark en buyuktu (ana elin cektigi silah
        // destek rayini da tasir): "iki elle tutunca bilek uzuyor" tam olarak buydu.
        Vector3 _shiftL, _shiftR;
        Transform _pushLogged;   // dogrulama izi tutus basina bir kez
        Vector3 _pushL, _pushR, _pushVelL, _pushVelR;   // yumusatilmis govde itmesi
        int _shiftFrame = -1;
        bool _appliedL, _appliedR;   // silah bu kare kaydirildi mi (cifte uygulama olmasin)

        /// <summary>EDITOR TEST KANCASI (SoloVrTest kullanir): govde temizligini SAHIBIN
        /// avatarinda da zorlar. Solo testte izlenen avatar sahibinin kendisi ve normal kapi
        /// (VisualOnly) temizligi orada kapatiyor — dis gorunus baska turlu dogrulanamazdi.
        /// ACIKKEN SILAH GOZLUKTE DE KAYAR: tek silah var, gozluk ve monitor ayni silahi
        /// ciziyor; birinde yerinde durup digerinde itilmis gorunmesi mumkun degil (deri
        /// karede bir kez pozlaniyor, kamera basina kaydirma eli silahtan koparirdi).
        /// Bu yuzden gercek oyunda sahip icin daima kapali; bu bayrak yalniz test icindir.</summary>
        public static bool ForceClearanceLocal;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ForceClearanceLocal = false;

        // Sahiplik: bir kez aranir. Bulunamazsa (editordeki tutus test ortami, ag yok)
        // gorsel kopya sayilir — orada zaten disaridan bakiyoruz ve oynanis diye bir sey yok.
        NetworkObject _netObj;
        bool _netLooked;

        /// <summary>Bu avatar YEREL oyuncu tarafindan surulmuyor mu?
        ///
        /// Govde temizligi yalnizca burada calisir. Nedeni: temizlik silahin transformunu
        /// oynatiyor, o transform da nisani ve elindeki hissi belirliyor. Sahibinin
        /// makinesinde uygulansa oyuncu silahi govdesine yaklastirdiginda silah kumandasindan
        /// KAYAR — sahada "birden konum degistiriyor, sikisiyor" diye gorulen buydu. Sahip
        /// zaten birinci sahista kendi govdesini gormuyor (NetworkVRPlayer gizliyor), yani
        /// duzeltilecek bir goruntu de yok. Isabet sunucuda silahin gercek transformundan
        /// hesaplandigi icin bu kaydirma tamamen kozmetiktir.</summary>
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
                _upperL = _anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _upperR = _anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
                var lowerL = _anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                var lowerR = _anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
                if (lowerL != null && _leftBone != null)
                    _armLocalL = lowerL.localPosition.magnitude + _leftBone.localPosition.magnitude;
                if (lowerR != null && _rightBone != null)
                    _armLocalR = lowerR.localPosition.magnitude + _rightBone.localPosition.magnitude;

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
            var pose = isSupport ? profile.supportHand : profile.mainHand;
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
                gripLocalPos = mirrored ? WeaponGripMath.MirrorX(profile.gripLocalPosition) : profile.gripLocalPosition,
                gripLocalRot = mirrored ? WeaponGripMath.MirrorX(profile.GripLocalRotation) : profile.GripLocalRotation,
                wristLocalPos = mirrored ? WeaponGripMath.MirrorX(pose.wristLocalPosition) : pose.wristLocalPosition,
                wristLocalRot = mirrored ? WeaponGripMath.MirrorX(Quaternion.Euler(pose.wristLocalEuler)) : Quaternion.Euler(pose.wristLocalEuler),
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
            // WeaponGrip her replike durum degisiminde yeniden uyguladigi icin yalnizca
            // gercekten YENI bir weld'de sifirlanir.
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

            SolveShift();   // IK cagirmadiysa (devre disi vb.) kaydirma yine de hazir olsun

            // ANA EL ONCE. Erisim disinda kalan ana el SILAHI kendine cekiyor (bkz.
            // WeldSide); destek eli de silahin O SON konumuna gore raya oturmali, yoksa
            // bir kare eski konuma gore hesaplanip titrer.
            if (_left.active && !_left.isSupport)
            {
                WeldSide(ref _left, true);
                WeldSide(ref _right, false);
            }
            else
            {
                WeldSide(ref _right, false);
                WeldSide(ref _left, true);
            }
        }

        /// <summary>Bu elin weld hedefi (bilegin gitmesi gereken DUNYA pozu), varsa.
        ///
        /// NEDEN DISARI ACIK: kol IK'si kendi hedefine (kumanda), weld ise kabza cipasina
        /// gidiyordu. Ikisi ayni nokta olmadigi icin el, on kolun bittigi yerden KOPUYOR ve
        /// deri arayi kapatmak icin on kolu geriyordu — sahada "bilek uzuyor / scale up
        /// oluyor" diye gorulen sey buydu. Destek elinde fark en buyugu: IK kumandaya, weld
        /// rayin uzerine gidiyordu ("cift el tutusunda bilek yerinden cikiyor"). Dirsek de
        /// yanlis bilek konumuna gore cozuldugu icin ic tarafa goculuyordu.
        ///
        /// <see cref="AvatarIKController"/> bunu LateUpdate'inde okur (o 0, bu 110 sirasinda,
        /// yani hedef HER ZAMAN taze) ve IK'yi da buraya cozer; boylece kol gercekten kabzaya
        /// UZANIR ve weld'in mutlak yazimi kocaman bir isinma degil, kucuk bir duzeltme olur.
        ///
        /// Sonme (fadingOut) sirasinda false doner: el zaten IK pozuna geri donuyor.</summary>
        public bool TryGetWristTarget(bool left, out Vector3 pos, out Quaternion rot)
        {
            if (left) return TryTarget(ref _left, true, out pos, out rot);
            return TryTarget(ref _right, false, out pos, out rot);
        }

        bool TryTarget(ref HandWeld w, bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (!w.active || w.fadingOut) return false;
            if (w.weapon == null || w.profile == null || w.bone == null) return false;
            SolveShift();
            ComputeTarget(ref w, left, PendingShift(ref w, left), out pos, out rot);
            return true;
        }

        /// <summary>Cipa (ana el: kabza, destek: raydaki en yakin ERISILEBILIR nokta) ->
        /// bilek dunya pozu. Tek kaynak: hem weld'in kendi yazimi hem IK hedefi buradan
        /// gelir, yoksa ikisi yeniden ayrisir.
        ///
        /// shift: bu kare silaha uygulanacak ama HENUZ uygulanmamis kaydirma (govde itmesi +
        /// erisim tasmasi). Hedef, silah sanki coktan kaydirilmis gibi hesaplanir; boylece
        /// sira 0'daki IK ile sira 110'daki weld ayni noktayi gorur.
        ///
        /// Destek eli erisim disina cikinca ray BOYUNCA kayar: eski kod hedefi erisim
        /// kuresine RADYAL kistiriyordu, o nokta rayin DISINDA kaliyordu ve el silahin
        /// yaninda bos havayi kavriyordu ("iki elle tutarken boslugu tutuyor"). Gercek insan
        /// da kolu yetmeyince destek elini ray uzerinde kendine dogru kaydirir; ancak rayin
        /// TAMAMI erisim disindaysa son care yine radyal kistirmadir.</summary>
        void ComputeTarget(ref HandWeld w, bool left, Vector3 shift,
            out Vector3 targetPos, out Quaternion targetRot)
        {
            // Anchor on the (scaled) weapon; the wrist offset is authored in meters
            // (hand-sized, independent of the weapon's scale).
            Quaternion anchorRot = w.weapon.rotation * w.gripLocalRot;
            Vector3 wristOff = anchorRot * w.wristLocalPos;
            targetRot = anchorRot * w.wristLocalRot;

            if (!w.isSupport)
            {
                targetPos = w.weapon.TransformPoint(w.gripLocalPos) + shift + wristOff;
                return;
            }

            // Slide along the rail: project this hand's networked carrier onto the segment,
            // in the weapon's (possibly mirrored) local space.
            Vector3 rs = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalStart) : w.profile.supportRailLocalStart;
            Vector3 re = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalEnd) : w.profile.supportRailLocalEnd;
            Vector3 s = w.weapon.TransformPoint(rs) + shift;
            Vector3 e = w.weapon.TransformPoint(re) + shift;
            Transform carrier = _ik != null ? (left ? _ik.leftHandSource : _ik.rightHandSource) : null;
            Vector3 probe = carrier != null ? carrier.position : w.bone.position;
            float t = WeaponGripMath.RailClosestT(s, e, probe);

            // Bilek hedefi t'de dogrusaldir (uclardaki iki hedef arasinda Lerp), o yuzden
            // erisim kisiti dogrudan t araligina cevrilebilir.
            Vector3 t0 = s + wristOff, t1 = e + wristOff;
            t = SlideWithinReach(t0, t1, t, left, out bool outOfReach);
            targetPos = Vector3.Lerp(t0, t1, t);
            if (outOfReach) targetPos = ClampToReach(targetPos, left);
        }

        /// <summary>Bilegi OMUZDAN itibaren kolun erisebilecegi kureye kistirir.
        ///
        /// Olcum omuzdan ve TAM kol boyuyla yapilir (dirsekten + on kolla degil): dirsegin
        /// yerini IK seciyor, gercek sinir omuz-bilek mesafesidir.
        ///
        /// Boy AWAKE'te onbellege alinmis bind degeri: weld el kemiginin DUNYA pozunu
        /// yazdigi icin sonradan olcmek gerilmis degeri okur ve sinir her karede biraz daha
        /// genisleyerek gerilmeyi hic durdurmazdi.
        ///
        /// %98: tam duz kolda iki-kemik cozucu dirsegin bukulme YONUNU kaybediyor
        /// (dirsegin "birden ice gocmesi"); kucuk bir pay onu onluyor.</summary>
        Vector3 ClampToReach(Vector3 target, bool left)
        {
            Transform up = left ? _upperL : _upperR;
            float lenLocal = left ? _armLocalL : _armLocalR;
            if (up == null || lenLocal < 1e-4f) return target;

            float maxLen = lenLocal * Mathf.Abs(up.lossyScale.x) * 0.98f;
            if (maxLen < 1e-4f) return target;

            Vector3 d = target - up.position;
            float dist = d.magnitude;
            if (dist <= maxLen || dist < 1e-5f) return target;
            return up.position + d * (maxLen / dist);
        }

        // ------------------------- kare basi silah kaydirmasi -------------------------

        /// <summary>Bu karenin silah kaydirmasini ana el basina BIR KEZ hesaplar. Ilk
        /// cagiran hesaplatir: normalde sira 0'da IK (TryGetWristTarget), IK yoksa sira
        /// 110'da weld'in kendisi. Sonuc iki tuketiciye de ayni gider — bilek gerilmesinin
        /// onceki kaynagi tam olarak bu ikisinin ayrismasiydi.</summary>
        void SolveShift()
        {
            if (Time.frameCount == _shiftFrame) return;
            _shiftFrame = Time.frameCount;
            _appliedL = _appliedR = false;
            _shiftL = SolveSideShift(ref _left, true, ref _shapeL, ref _pushL, ref _pushVelL);
            _shiftR = SolveSideShift(ref _right, false, ref _shapeR, ref _pushR, ref _pushVelR);
        }

        Vector3 SolveSideShift(ref HandWeld w, bool left, ref WeaponShape shape,
            ref Vector3 push, ref Vector3 pushVel)
        {
            if (!w.active || w.fadingOut || w.isSupport ||
                w.weapon == null || w.profile == null || w.bone == null)
            {
                push = Vector3.zero; pushVel = Vector3.zero;
                return Vector3.zero;
            }

            // Govde temizligi: yalniz baskalarinin gordugu kopyada (bkz. VisualOnly) —
            // ya da solo test dis gorunusu dogrulamak icin zorladiysa.
            if (bodyClearance && (VisualOnly || ForceClearanceLocal))
            {
                if (shape.of != w.weapon) MeasureWeaponShape(w.weapon, ref shape);
                Vector3 want = BodyClearPush(w.weapon, w.bone, shape);
                push = Vector3.SmoothDamp(push, want, ref pushVel,
                    Mathf.Max(0.01f, clearanceSmoothing));

                // Dogrulama izi: tutus basina EN FAZLA BIR satir, yalniz itme gerektiginde.
                // "Calisiyor mu?" sorusunu tahmin yerine konsol cevaplasin diye.
                if (want.sqrMagnitude > 1e-6f && _pushLogged != w.weapon)
                {
                    _pushLogged = w.weapon;
                    Debug.Log($"[GovdeTemizligi] {w.weapon.name}: itme {want.magnitude * 100f:0} cm " +
                              $"(sahip degil: {VisualOnly}, zorlama: {ForceClearanceLocal})");
                }
            }
            else push = Vector3.zero;

            // Erisim tasmasi ("SILAH ELE GELIR"): itilmis hedef kolun kuresi disindaysa
            // silah tasma kadar geri gelir. Alternatifler sahada kotuydu: bilegi oldugu
            // yere yazmak on kolu gerer, eli kistirmak eli silahtan koparir. Donus
            // degismedigi icin nisan hatti kaymaz; her kare sifirdan hesaplanir
            // (HandGrabber silahi kumandadan yeniden konumlar), birikmez.
            ComputeTarget(ref w, left, push, out Vector3 tp, out _);
            return push + (ClampToReach(tp, left) - tp);
        }

        /// <summary>Bu elin hedefine eklenmesi gereken, HENUZ silaha uygulanmamis kaydirma.
        /// Ana el kendininkini, destek eli AYNI silahi tutan ana elinkini kullanir (cift
        /// tabancada iki ayri ana el, iki ayri kaydirma). Silah bu kare coktan
        /// kaydirildiysa sifir — cifte sayim olmaz.</summary>
        Vector3 PendingShift(ref HandWeld w, bool left)
        {
            if (!w.isSupport)
                return (left ? _appliedL : _appliedR) ? Vector3.zero : (left ? _shiftL : _shiftR);

            bool mainLeft = !left;   // destek solsa ana sag (ve tersi)
            bool mainHolds = mainLeft
                ? _left.active && !_left.isSupport && _left.weapon == w.weapon
                : _right.active && !_right.isSupport && _right.weapon == w.weapon;
            if (!mainHolds) return Vector3.zero;
            if (mainLeft ? _appliedL : _appliedR) return Vector3.zero;
            return mainLeft ? _shiftL : _shiftR;
        }

        /// <summary>Raydaki t'yi, bilek hedefi (t0..t1 dogrusu) omuzun erisim kuresi ICINDE
        /// kalacak sekilde kaydirir. Hic kesisim yoksa outOfReach=true ve rayin omza en
        /// yakin t'si doner (radyal kistirma cagiranin isi).</summary>
        float SlideWithinReach(Vector3 t0, Vector3 t1, float tWant, bool left, out bool outOfReach)
        {
            outOfReach = false;
            Transform up = left ? _upperL : _upperR;
            float lenLocal = left ? _armLocalL : _armLocalR;
            if (up == null || lenLocal < 1e-4f) return tWant;
            float r = lenLocal * Mathf.Abs(up.lossyScale.x) * 0.98f;   // ClampToReach ile AYNI pay

            Vector3 a = t0 - up.position;
            Vector3 b = t1 - t0;
            if ((a + b * tWant).sqrMagnitude <= r * r) return tWant;   // istenen zaten erisimde

            // |a + t*b|^2 = r^2: kurenin ray dogrusunu kestigi t araligi (ikinci derece).
            float bb = Vector3.Dot(b, b);
            if (bb < 1e-8f) { outOfReach = true; return tWant; }       // ray tek nokta
            float ab = Vector3.Dot(a, b);
            float disc = ab * ab - bb * (a.sqrMagnitude - r * r);
            if (disc <= 0f)
            {
                outOfReach = true;                    // dogru kureye hic girmiyor
                return Mathf.Clamp01(-ab / bb);       // omza en yakin ray noktasi
            }
            float sq = Mathf.Sqrt(disc);
            float tLo = Mathf.Max(0f, (-ab - sq) / bb);
            float tHi = Mathf.Min(1f, (-ab + sq) / bb);
            if (tLo > tHi)
            {
                outOfReach = true;                    // kesisim rayin [0,1] parcasi disinda
                return Mathf.Clamp01(-ab / bb);
            }
            return Mathf.Clamp(tWant, tLo, tHi);
        }

        /// <summary>Silahin kaba seklini (merkez, uzun eksen, yari boy, yari kalinlik) silahin
        /// KENDI yerel uzayinda, tutus basina BIR KEZ olcer.
        ///
        /// Renderer.localBounds + kose donusumu kullanilir; Renderer.bounds dunyada eksen-hizali
        /// oldugu icin donmus bir silahta gercekte olmayan bir sisme uretirdi.
        ///
        /// Yari kalinlik neden onemli: onceki denemede yalnizca silahin ORTA CIZGISI test
        /// ediliyordu, dolayisiyla cizgi govdenin disinda kalirken namlu/sarjor govdenin
        /// icinde kalabiliyordu — sahada "itme yetersiz" goruntusunun sebebi buydu. Kalinlik
        /// test yaricapina eklenerek silahin YUZEYI govdeye tegetlenir.</summary>
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

        /// <summary>Silahi govdenin disina cikaracak YATAY itmeyi dondurur (yoksa sifir).
        ///
        /// Govde, kalca-boyun dogru parcasi etrafinda bir SILINDIR olarak modellenir. Silindir
        /// (kapsul degil) cunku uclarin disinda kalan ornekler tamamen yok sayilmali: bacak
        /// hizasindaki bir silah itilmemeli, yuze yaklastirilan bir silah da itilmemeli —
        /// nisan alirken silah zaten yanaga gelir ve orayi itmek nisan hattini bozuk gosterir.
        /// Bu yuzden ust uc BOYUN, bas disarida.
        ///
        /// Itme yonu eksene dik oldugu icin silah yukari/asagi kaymaz: izleyen oyuncu silahin
        /// ayni yukseklikte, govdenin yanina dogru kaydigini gorur.</summary>
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
                float t = (i / (float)(Samples - 1)) * 2f - 1f;   // -1..1, uzun eksen boyunca
                Vector3 p = weapon.TransformPoint(sh.center + sh.axis * (sh.halfLen * t));

                Vector3 rel = p - a;
                float u = Vector3.Dot(rel, ab) / abLen2;
                if (u < 0f || u > 1f) continue;                   // govde bandinin disinda

                Vector3 d = rel - ab * u;                          // eksene DIK
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
                // Silah tam govde ekseninin uzerinde: yon belirsiz. Eli tutan taraf disari.
                Vector3 fallback = hand != null ? hand.position - (a + ab * 0.5f) : transform.right;
                Vector3 axis = ab / Mathf.Sqrt(abLen2);
                fallback -= axis * Vector3.Dot(fallback, axis);
                if (fallback.sqrMagnitude < 1e-6f) return Vector3.zero;
                bestDir = fallback.normalized;
            }

            return bestDir * Mathf.Min(best, maxBodyPush);
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

            // SILAH KAYDIRMASI: SolveShift bu karede coktan hesapladi ve IK ayni degeri
            // gordu (bkz. _shiftL/R). Burada yalnizca silaha BIR KEZ uygulanir; hedef zaten
            // PendingShift ile kaydirilmis geldi.
            if (!w.isSupport)
            {
                if (!w.fadingOut)
                {
                    if (!(left ? _appliedL : _appliedR))
                    {
                        Vector3 shift = left ? _shiftL : _shiftR;
                        if (shift.sqrMagnitude > 1e-10f) w.weapon.position += shift;
                        if (left) _appliedL = true; else _appliedR = true;
                    }

                    // Emniyet: silah sira 0 ile 110 arasinda oynadiysa (HandGrabber'in
                    // LateUpdate sirasi garantili degil) kalan tasma yine silaha uygulanir —
                    // "silah ele gelir" sozu her kosulda tutulur.
                    Vector3 rc = ClampToReach(targetPos, left);
                    Vector3 extra = rc - targetPos;
                    if (extra.sqrMagnitude > 1e-8f)
                    {
                        w.weapon.position += extra;
                        targetPos = rc;
                    }
                }
                else
                {
                    // Birakis sonmesi: ucup giden silaha DOKUNMA (eski kod tasmayi firlatilan
                    // silaha da uygulayip 0.12 sn boyunca ele dogru cekiyordu). El erisimde
                    // kalsin yeter.
                    targetPos = ClampToReach(targetPos, left);
                }
            }
            // Destek eli: erisim, ComputeTarget'taki ray kaydirmasinda cozuldu.

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
