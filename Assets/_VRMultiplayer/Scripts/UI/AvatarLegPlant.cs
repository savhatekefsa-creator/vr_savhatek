using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// BACAK OTURTMA — comelirken avatarin kendi icine katlanmasini duzeltir.
    ///
    /// SORUN NEYDI: comelme olculen bir sey degil, HAZIR BIR POZ. AvatarIKController boy
    /// oranina gore "CrouchIdle" klibini karistiriyor; ama senin gercek comelmen surekli bir
    /// deger, klip ise tek bir donmus poz. Aradaki fark bir yere gitmek zorunda ve KOKE gidiyor:
    /// kok yuksekligi KAFADAN cozuluyor (kok = gozun - kafa kemigi yuksekligi), ayaktan degil.
    /// Sonuc: ayaklar yerden kopuyor ya da zemine gomuluyor, kalca-uyluk torsonun icine katlaniyor.
    ///
    /// COZUM: pozu ENGELLEYEREK degil, DOGRU YAPARAK. Kafa gozde kalmaya devam ediyor (o
    /// gorusle eslesmenin sarti), ayaklar zemine cakiliyor, diz de aradaki farki kapatacak
    /// KADAR bukuluyor. Comelme derinligin ne olursa olsun poz dogru cikiyor, cunku artik
    /// hazir klip degil hesaplanan aci.
    ///
    /// Fizik/collider YOK. Uyluk torsoya carptigi icin durmuyor — zaten oraya hic gitmiyor.
    /// Kollara, silaha ve TUTUSLARA hic dokunulmaz.
    ///
    /// AvatarIKController'a tek satir eklemedik: bu bilesen ondan SONRA (sira 20) calisip
    /// bacak kemiklerini duzeltiyor. Boylece o dosyada baskasiyla cakismiyoruz ve
    /// istemedigimizde tek kutuyla kapaniyor.
    ///
    /// ── KAPSAM NOTU ────────────────────────────────────────────────────────────────────
    /// Bir tur boyunca dort ek ozellik denendi ve HEPSI GERI ALINDI (23.07.2026): govde one
    /// egme, diz disa acma, ayak yere kilitleme, diz katlanma siniri. Dordu ayni anda
    /// eklendigi icin hangisinin ne bozdugu ayirt edilemedi; sonuc duruş cizgisinin bozulmasi
    /// ve dizlerin ice cokmesi oldu. Tekrar denenecekse TEK TEK eklenmeli. Bilinen tuzaklar:
    ///  - Govde egme, AvatarIKController ile GERI BESLEME DONGUSU kuruyor: o, kok yuksekligini
    ///    kafa kemiginin koke gore yuksekliginden hesapliyor; govdeyi egmek o yuksekligi
    ///    degistiriyor, o koku kaydiriyor, biz tekrar egiyoruz.
    ///  - KALCAYI itmek ise hicbir sey yapmaz: kalca iskeletin KOK kemigi, onu itince bacaklar
    ///    da gider ve koku geri alinca net etki sifir olur.
    ///  - Diz kutbunu o anki DIZ konumuna baglamak kendi hatasini besliyor (diz bir kere ice
    ///    kaydiysa daha da iceri cokuyor); kutup KALCAYA baglanmali.
    ///  - Diz katlanma sinirini kosinus teoremiyle hesaplarken karekokun ici NEGATIF olabiliyor
    ///    (bilek kalcanin ustunde kalirsa) -> NaN -> belden asagisi tamamen kayboluyor.
    /// ───────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public static class AvatarLegPlantBinder
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            var go = new GameObject("~AvatarLegPlant");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AvatarLegPlantWatcher>();
        }
    }

    /// <summary>Sahneye giren her avatara bileseni takar. Oyuncular ag uzerinden zamanla
    /// beliriyor, o yuzden tek seferlik tarama yetmez — yarim saniyede bir bakiyoruz.</summary>
    public class AvatarLegPlantWatcher : MonoBehaviour
    {
        float _next;

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;

            foreach (var ik in FindObjectsByType<AvatarIKController>(FindObjectsSortMode.None))
                if (ik.GetComponent<AvatarLegPlant>() == null)
                    ik.gameObject.AddComponent<AvatarLegPlant>();
        }
    }

    // SIRA 20 = AvatarIKController'DAN (sira 0) SONRA. O, kok konumunu ve el hedeflerini
    // LateUpdate'te yaziyor; biz nihai kok konumunu gormeden bacagi cozemeyiz.
    [DefaultExecutionOrder(20)]
    public class AvatarLegPlant : MonoBehaviour
    {
        [Header("Canli ayar (Play'de oynanabilir)")]
        [Tooltip("Kapatmak icin: bacaklara hic dokunulmaz, eski davranisa doner.")]
        public bool plantFeet = true;
        [Tooltip("Hazir 'CrouchIdle' klibini sustur. Diz artik hesapla bukuluyor; klip de " +
                 "acik kalirsa iki cozum birbiriyle kavga eder.")]
        public bool disableCrouchClip = true;
        [Tooltip("Ayak hedefi yumusatmasi — yuksek = daha cabuk oturur, dusuk = daha akiskan.")]
        public float smooth = 14f;
        [Tooltip("Dizin bukulecegi yon, govde ilerisine gore. Diz geri bukuluyorsa isaretini cevir.")]
        public float kneeForward = 1f;
        [Tooltip("Zemin aramasi: ayagin bu kadar ustunden asagi isin atilir (m).")]
        public float groundProbeUp = 0.8f;
        public float groundProbeDown = 3f;

        AvatarIKController _ik;
        Animator _anim;
        Transform _lUp, _lLo, _lFt, _rUp, _rLo, _rFt;
        int _crouchLayer = -1;

        // Bilek TABANDAN ne kadar yukarida — bir kez olculur, olcum anindaki olcege gore normalize.
        float _ankleLift;
        float _measureScale = 1f;
        Quaternion _footLocalL, _footLocalR; // ayagi duz tutmak icin kok-goreli referans durus
        bool _measured;

        Vector3 _tgtL, _tgtR;
        bool _hasTgt;

        void Awake()
        {
            _ik = GetComponent<AvatarIKController>();
            _anim = GetComponent<Animator>();
            if (_anim == null || _anim.avatar == null || !_anim.avatar.isHuman)
            {
                enabled = false; // humanoid degilse yapacak bir sey yok
                return;
            }

            _lUp = _anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            _lLo = _anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            _lFt = _anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            _rUp = _anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            _rLo = _anim.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            _rFt = _anim.GetBoneTransform(HumanBodyBones.RightFoot);

            if (_lUp == null || _lLo == null || _lFt == null ||
                _rUp == null || _rLo == null || _rFt == null)
            {
                Debug.LogWarning("[BacakOturtma] Bacak kemikleri bulunamadi — bilesen kapatildi.");
                enabled = false;
                return;
            }

            if (_anim.runtimeAnimatorController != null)
                _crouchLayer = _anim.GetLayerIndex("Crouch");
        }

        void LateUpdate()
        {
            if (!plantFeet || _ik == null) return;
            if (!_measured && !TryMeasure()) return;

            // Hazir comelme pozunu sustur. AvatarIKController sira 0'da agirligi yaziyor,
            // biz sira 20'de sifirliyoruz — bir sonraki animator degerlendirmesinde bizimki gecerli.
            if (disableCrouchClip && _crouchLayer >= 0)
                _anim.SetLayerWeight(_crouchLayer, 0f);

            float k = _measureScale > 0.0001f ? transform.localScale.x / _measureScale : 1f;
            float lift = _ankleLift * k;
            float t = 1f - Mathf.Exp(-smooth * Mathf.Max(0.0001f, Time.deltaTime));

            SolveLeg(_lUp, _lLo, _lFt, lift, _footLocalL, ref _tgtL, k, t);
            SolveLeg(_rUp, _rLo, _rFt, lift, _footLocalR, ref _tgtR, k, t);
            _hasTgt = true;
        }

        void SolveLeg(Transform up, Transform lo, Transform ft, float lift,
                      Quaternion footLocal, ref Vector3 smoothTarget, float k, float t)
        {
            // Ayagin YATAY yeri animasyondan gelsin (adim genisligi dogal kalsin); biz sadece
            // YUKSEKLIGINI zemine oturtuyoruz.
            Vector3 ankle = ft.position;
            float ground = ProbeGround(ankle);
            Vector3 target = new Vector3(ankle.x, ground + lift, ankle.z);

            // NaN KALKANI: bozuk tek bir hedef kemik donuslerini NaN yapar, skinned mesh patlar
            // ve belden asagisi tamamen kaybolur. Daha kotusu smoothTarget de NaN'a kilitlenip
            // bir daha kendine gelmez. Sayi bozuksa o kareyi atla ve yumusatmayi sifirla.
            if (!IsFinite(target)) { _hasTgt = false; return; }
            if (_hasTgt && !IsFinite(smoothTarget)) smoothTarget = target;

            smoothTarget = _hasTgt ? Vector3.Lerp(smoothTarget, target, t) : target;

            // Diz ileri buksun: kutup noktasi dizin ONUNDE bir yer.
            Vector3 hint = lo.position + transform.forward * (kneeForward * 0.6f * k);
            TwoBoneIK(up, lo, ft, smoothTarget, hint);

            // Ayak tabani duz kalsin ve govdeyle ayni yone baksin. Duz zeminde dogru sonuc verir;
            // egimli zemin icin zemin normali gerekir (simdilik kapsam disi).
            ft.rotation = transform.rotation * footLocal;
        }

        float ProbeGround(Vector3 ankle)
        {
            Vector3 origin = ankle + Vector3.up * groundProbeUp;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                    groundProbeUp + groundProbeDown, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return _ik.groundY; // zemin collider'i yoksa duz dunya varsayimi
        }

        /// <summary>Bilegin TABANDAN yuksekligini bir kez olc. Renderer'larin en alt noktasi
        /// = ayak tabani; avatar o an havada duruyor olsa bile ARADAKI FARK dogru cikar,
        /// cunku iki deger de ayni kadar kayar.</summary>
        bool TryMeasure()
        {
            // Takip oturmadan olcme: acilista kafa Y'si ~0 okunur ve iskelet daha yerine oturmamistir.
            if (_ik.headBone == null || _ik.headBone.position.y - _ik.groundY < 0.8f) return false;

            float lowest = float.MaxValue;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                lowest = Mathf.Min(lowest, r.bounds.min.y);
            }
            if (lowest == float.MaxValue) return false;

            _ankleLift = Mathf.Clamp((_lFt.position.y + _rFt.position.y) * 0.5f - lowest, 0.02f, 0.25f);
            _measureScale = Mathf.Max(0.0001f, transform.localScale.x);
            _footLocalL = Quaternion.Inverse(transform.rotation) * _lFt.rotation;
            _footLocalR = Quaternion.Inverse(transform.rotation) * _rFt.rotation;
            _measured = true;
            return true;
        }

        /// <summary>Analitik iki-kemik IK (kalca -> diz -> bilek). Animation Rigging kisitlamasi
        /// EKLEMIYORUZ: onlar RigBuilder'a kurulum aninda baglanir, calisma aninda eklenemez.
        /// Bu yuzden kemikleri LateUpdate'te dogrudan yaziyoruz — animator bir sonraki karede
        /// uzerine yazar, biz tekrar duzeltiriz; IK'nin standart calisma sekli budur.</summary>
        static void TwoBoneIK(Transform a, Transform b, Transform c, Vector3 target, Vector3 hint)
        {
            Vector3 A = a.position, B = b.position, C = c.position;
            float lab = Vector3.Distance(A, B);
            float lcb = Vector3.Distance(C, B);
            if (lab < 1e-4f || lcb < 1e-4f) return;

            // Hedef bacaktan uzunsa bacak duzlesir, cok yakinsa tamamen katlanir — ikisi de
            // gecerli poz; NaN uretmesin diye ulasilabilir araliga kirpiyoruz.
            float lat = Mathf.Clamp(Vector3.Distance(A, target), 1e-3f, lab + lcb - 1e-3f);

            float ac_ab_0 = Angle(C - A, B - A);
            float ba_bc_0 = Angle(A - B, C - B);
            float ac_at_0 = Angle(C - A, target - A);

            float ac_ab_1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float ba_bc_1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            Vector3 bendAxis = Vector3.Cross(C - A, B - A);
            if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = Vector3.Cross(C - A, Vector3.up);
            bendAxis.Normalize();

            Vector3 aimAxis = Vector3.Cross(C - A, target - A);
            if (aimAxis.sqrMagnitude < 1e-8f) aimAxis = bendAxis;
            aimAxis.Normalize();

            // Once diz acisini duzelt, sonra tum bacagi hedefe nisanla.
            a.rotation = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, bendAxis) * a.rotation;
            b.rotation = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, bendAxis) * b.rotation;
            a.rotation = Quaternion.AngleAxis(ac_at_0 * Mathf.Rad2Deg, aimAxis) * a.rotation;

            // Kutup: bacagi kalca-bilek EKSENI etrafinda dondurup dizi ipucuna cevir. Bu eksen
            // etrafindaki donus bilegi YERINDEN OYNATMAZ, o yuzden en sona birakilabilir.
            Vector3 ac = (c.position - a.position).normalized;
            Vector3 bProj = Vector3.ProjectOnPlane(b.position - a.position, ac);
            Vector3 hProj = Vector3.ProjectOnPlane(hint - a.position, ac);
            if (bProj.sqrMagnitude > 1e-8f && hProj.sqrMagnitude > 1e-8f)
                a.rotation = Quaternion.FromToRotation(bProj, hProj) * a.rotation;
        }

        static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        static float Angle(Vector3 u, Vector3 v) =>
            Mathf.Acos(Mathf.Clamp(Vector3.Dot(u.normalized, v.normalized), -1f, 1f));
    }
}
