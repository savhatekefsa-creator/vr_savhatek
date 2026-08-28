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

        // Silahin kaba sekli, tutus basina BIR KEZ olculur (her kare renderer taramak pahali).
        Vector3 _shapeCenter, _shapeAxis;
        float _shapeHalfLen, _shapeHalfThick;
        bool _shapeOK;
        Transform _shapeOf;

        Vector3 _push, _pushVel;   // yumusatilmis itme

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
            if (!prev.active && !isSupport) { _push = Vector3.zero; _pushVel = Vector3.zero; }

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
            ComputeTarget(ref w, left, out pos, out rot);
            return true;
        }

        /// <summary>Cipa (ana el: kabza, destek: ray uzerindeki en yakin nokta) -> bilek
        /// dunya pozu. Tek kaynak: hem weld'in kendi yazimi hem IK hedefi buradan gelir,
        /// yoksa ikisi yeniden ayrisir.</summary>
        void ComputeTarget(ref HandWeld w, bool left, out Vector3 targetPos, out Quaternion targetRot)
        {
            Vector3 anchorLocal;
            Quaternion anchorLocalRot = w.gripLocalRot;

            if (!w.isSupport)
            {
                anchorLocal = w.gripLocalPos;
            }
            else
            {
                // Slide along the rail: project this hand's networked carrier onto the segment,
                // in the weapon's (possibly mirrored) local space.
                Vector3 rs = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalStart) : w.profile.supportRailLocalStart;
                Vector3 re = w.mirrored ? WeaponGripMath.MirrorX(w.profile.supportRailLocalEnd) : w.profile.supportRailLocalEnd;
                Vector3 s = w.weapon.TransformPoint(rs);
                Vector3 e = w.weapon.TransformPoint(re);
                Transform carrier = _ik != null ? (left ? _ik.leftHandSource : _ik.rightHandSource) : null;
                Vector3 probe = carrier != null ? carrier.position : w.bone.position;
                float t = WeaponGripMath.RailClosestT(s, e, probe);
                anchorLocal = Vector3.Lerp(rs, re, t);
            }

            // Anchor on the (scaled) weapon; the wrist offset is authored in meters (hand-sized,
            // independent of the weapon's scale).
            Vector3 anchorPos = w.weapon.TransformPoint(anchorLocal);
            Quaternion anchorRot = w.weapon.rotation * anchorLocalRot;
            targetPos = anchorPos + anchorRot * w.wristLocalPos;
            targetRot = anchorRot * w.wristLocalRot;
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
        void MeasureWeaponShape(Transform weapon)
        {
            _shapeOf = weapon;
            _shapeOK = false;
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

            _shapeCenter = (lo + hi) * 0.5f;
            Vector3 ext = (hi - lo) * 0.5f;
            int ax = ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2);
            _shapeAxis = ax == 0 ? Vector3.right : ax == 1 ? Vector3.up : Vector3.forward;
            _shapeHalfLen = ext[ax];
            _shapeHalfThick = (ext[(ax + 1) % 3] + ext[(ax + 2) % 3]) * 0.5f;
            _shapeOK = _shapeHalfLen > 1e-3f;
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
        Vector3 BodyClearPush(Transform weapon, Transform hand)
        {
            if (_hips == null || _neck == null || !_shapeOK) return Vector3.zero;

            Vector3 a = _hips.position;
            Vector3 ab = _neck.position - a;
            float abLen2 = ab.sqrMagnitude;
            if (abLen2 < 1e-6f) return Vector3.zero;

            float radius = bodyRadius + _shapeHalfThick * Mathf.Abs(weapon.lossyScale.x);

            const int Samples = 11;
            float best = 0f;
            Vector3 bestDir = Vector3.zero;
            bool hit = false;

            for (int i = 0; i < Samples; i++)
            {
                float t = (i / (float)(Samples - 1)) * 2f - 1f;   // -1..1, uzun eksen boyunca
                Vector3 p = weapon.TransformPoint(_shapeCenter + _shapeAxis * (_shapeHalfLen * t));

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

            ComputeTarget(ref w, left, out Vector3 targetPos, out Quaternion targetRot);

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

            // ERISIM SINIRI. Uc secenek vardi ve ikisi de sahada kotu goruldu:
            //   (a) el kemigini oldugu yere yaz  -> on kol gerilir ("bilek uzuyor")
            //   (b) eli erisime kistir           -> el silahtan kopar, silah havada kalir
            //   (c) SILAHI ELE GETIR             -> ikisi de olmaz  <-- secilen
            // Ana el silahi tasiyor: hedef erisim disindaysa silah, tasma kadar geri
            // cekilir ve el tam uzerinde kalir. Donus DEGISMEZ, yani nisan hatti kaymaz;
            // silah yalnizca kumandanin birkac cm gerisinde durur. Duzeltme her karede
            // sifirdan hesaplanir (HandGrabber silahi kumandadan yeniden konumluyor),
            // dolayisiyla birikmez.
            // GOVDE TEMIZLIGI. Yalnizca ana el ve yalnizca BASKASININ ekraninda (VisualOnly).
            // Erisim kistirmasindan ONCE: kol itilen silaha yetisemiyorsa asagidaki kistirma
            // silahi geri cekiyor, yani itme kolun erisimiyle kendiliginden sinirlanir ve
            // silah elden kopmus gibi havada kalmaz.
            if (bodyClearance && !w.isSupport && VisualOnly)
            {
                if (_shapeOf != w.weapon) MeasureWeaponShape(w.weapon);
                Vector3 want = BodyClearPush(w.weapon, w.bone);
                _push = Vector3.SmoothDamp(_push, want, ref _pushVel,
                    Mathf.Max(0.01f, clearanceSmoothing));
                if (_push.sqrMagnitude > 1e-8f)
                {
                    w.weapon.position += _push;
                    targetPos += _push;
                }
            }

            Vector3 reachClamped = ClampToReach(targetPos, left);
            Vector3 excess = reachClamped - targetPos;
            if (excess.sqrMagnitude > 1e-8f)
            {
                if (!w.isSupport) w.weapon.position += excess;   // silah ele gelir
                targetPos = reachClamped;                        // el her halukarda erisimde
            }

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
