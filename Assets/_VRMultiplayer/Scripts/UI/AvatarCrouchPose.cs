using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// ELLE YAZILMIS COMELME POZUNU UYGULAR. Oyuncu comeldikce, animatorun urettigi bacak
    /// pozundan menu 44 ile yakalanan poza dogru slerp yapar.
    ///
    /// NEDEN BOYLE, IK NEDEN DEGIL: analitik bacak IK'si iki denemede de felaket verdi
    /// (bacaklar 360 derece dondu, ayak zeminin altina girdi) — kok sebep kutup adiminin
    /// 180 derecede belirsiz kalmasi. Burada iki GECERLI poz arasinda slerp var; ara deger de
    /// her zaman gecerli bir poz. Bacagin firildak gibi donmesi matematiksel olarak imkansiz.
    ///
    /// SADECE BACAK KEMIKLERI. Kalca ve omurgaya dokunulmuyor: AvatarIKController kafa kemigini
    /// oyuncunun gozune kilitliyor ve kok yuksekligini o kemikten hesapliyor. Omurgayi cevirmek
    /// bir geri besleme dongusu kuruyor (bir kez yasandi, duruş cizgisi bozuldu). Bacaklar o
    /// zincirin disinda.
    ///
    /// BEDELI: poz tek derinlik icin yazildigi icin ayak zeminden birkac santim kayabilir.
    /// Bilincli takas — ayagi tam oturtmak IK isteyen ve bizi iki kez yakan is.
    ///
    /// Kurulum SIFIR sahne/prefab dokunusu (binder deseni).
    /// </summary>
    public static class AvatarCrouchPoseBinder
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            var go = new GameObject("~AvatarCrouchPose");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AvatarCrouchPoseWatcher>();
        }
    }

    /// <summary>Oyuncular aga zamanla katildigi icin tek seferlik tarama yetmez.</summary>
    public class AvatarCrouchPoseWatcher : MonoBehaviour
    {
        float _next;

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;

            foreach (var ik in FindObjectsByType<AvatarIKController>(FindObjectsSortMode.None))
                if (ik.GetComponent<AvatarCrouchPose>() == null)
                    ik.gameObject.AddComponent<AvatarCrouchPose>();
        }
    }

    // SIRA 20 = AvatarIKController'DAN (0) SONRA: o, kok konumunu ve kafa kemigini LateUpdate'te
    // yaziyor; biz nihai durumu gormeden orani hesaplayamayiz.
    [DefaultExecutionOrder(20)]
    public class AvatarCrouchPose : MonoBehaviour
    {
        [Header("Canli ayar (Play'de oynanabilir)")]
        [Tooltip("Kapatmak icin: bacaklara hic dokunulmaz.")]
        public bool apply = true;
        [Tooltip("Poza gecis yumusatmasi. Yuksek = daha cabuk oturur.")]
        public float smooth = 10f;
        [Tooltip("Hazir CrouchIdle klibini sustur — poz artik burdan geliyor, ikisi kavga etmesin.")]
        public bool disableCrouchClip = true;
        [Tooltip("Ayak zeminin altina inmesin: en alcak temas kemigi tabandan asagi duserse kok " +
                 "tam o kadar kaldirilir. Kapatirsan derin comelmede avatar zeminin altina kayar " +
                 "ve karsidan GORUNMEZ olur. Bedeli: avatarin kafasi senin gercek kafandan " +
                 "yukarida kalir — karsidaki bunu fark etmez.")]
        public bool clampDescent = true;

        [Header("Teshis (salt okunur)")]
        [Tooltip("Oyuncunun su anki boy orani: 1.0 = ayakta.")]
        public float heightRatio = 1f;
        [Tooltip("Pozun uygulanma agirligi: 0 = animator pozu, 1 = yakalanan poz.")]
        public float weight;
        [Tooltip("Sinir devredeyse avatarin ne kadar yukari tutuldugu (m).")]
        public float clampLift;

        CrouchPoseAsset _pose;
        AvatarIKController _ik;
        Animator _anim;
        Transform[] _bones;
        int _crouchLayer = -1;

        float _standHeadY;   // ayaktaki kafa yuksekligi — BIR KEZ olculur
        float _soleOffset;   // ayaktaki en alcak temas kemiginin zeminden payi — BIR KEZ olculur
        bool _measured;
        float _w;            // yumusatilmis agirlik

        // Zemine temas eden kemikler. Dizustu pozda temas AYAK degil DIZ olabilir, o yuzden
        // baldir da listede. Renderer bounds KULLANMIYORUZ: skinned mesh bounds'u guvenilmez
        // ve olcumu 60 cm sasitiyor (bir kez yasandi).
        static readonly HumanBodyBones[] ContactBones =
        {
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg
        };
        Transform[] _contact;

        void Awake()
        {
            _ik = GetComponent<AvatarIKController>();
            _anim = GetComponent<Animator>();
            if (_anim == null || _anim.avatar == null || !_anim.avatar.isHuman) { enabled = false; return; }

            var all = Resources.LoadAll<CrouchPoseAsset>("CrouchPoses");
            if (all == null || all.Length == 0)
            {
                Debug.LogWarning("[ComelmePozu] Resources/CrouchPoses altinda poz asset'i yok — " +
                                 "Tools > VR Multiplayer > 44 ile bir poz yakala. Bilesen kapatildi.");
                enabled = false;
                return;
            }
            _pose = all[0];

            _bones = new Transform[_pose.bones.Length];
            for (int i = 0; i < _pose.bones.Length; i++)
                _bones[i] = _anim.GetBoneTransform(_pose.bones[i].bone);

            _contact = new Transform[ContactBones.Length];
            for (int i = 0; i < ContactBones.Length; i++)
                _contact[i] = _anim.GetBoneTransform(ContactBones[i]);

            if (_anim.runtimeAnimatorController != null)
                _crouchLayer = _anim.GetLayerIndex("Crouch");
        }

        float LowestContactY()
        {
            float lo = float.MaxValue;
            for (int i = 0; i < _contact.Length; i++)
                if (_contact[i] != null) lo = Mathf.Min(lo, _contact[i].position.y);
            return lo;
        }

        /// <summary>Ayak zeminin ALTINA dusmesin: en alcak temas kemigi tabandan asagi inerse
        /// koku tam o kadar kaldir. Poz uygulandiktan SONRA cagrilir.</summary>
        // NEDEN COLLIDER DEGIL: avatarin kemikleri her kare KOD tarafindan yaziliyor, hareketini
        // fizik motoru hesaplamiyor. Collider yalnizca fizigin cozdugu nesneleri durdurur; kodun
        // atadigi bir transform'a "carpisma" olmaz. Ustune kokun yuksekligi oyuncunun GERCEK
        // kafasindan turuyor, ona da fizik itiraz edemez. O yuzden sinir aritmetik olmak zorunda.
        //
        // Orana gore sinir yerine GERCEK AYAK konumuna bakiyoruz: boylece gecis sirasinda da
        // batma olmuyor. (Oran yontemi yalnizca iki uc noktada dogruydu; arada poz DONUS
        // harmanlamasiyla, kok ise DUZ iniyor ve ikisi ortusmuyordu.)
        void ClampToGround()
        {
            clampLift = 0f;
            if (!clampDescent || !_measured || _contact == null) return;

            float lo = LowestContactY();
            if (lo == float.MaxValue) return;

            float floor = _ik.groundY + _soleOffset;
            if (lo < floor)
            {
                clampLift = floor - lo;
                transform.position += Vector3.up * clampLift;
            }
        }

        void LateUpdate()
        {
            if (!apply || _pose == null || _ik == null || _ik.headBone == null) return;

            float headY = _ik.headBone.position.y - _ik.groundY;
            if (headY < 0.5f) return; // takip henuz oturmadi

            // AYAKTA REFERANSI SURELI TAZELENIR — tek karede dondurmak HATAYDI: o kare avatar
            // henuz senin boyuna olceklenirken geciyor (AvatarIKController'in servosu birkac kare
            // suruyor), referans gercek boyundan KUCUK kaydediliyordu. Sonucu: ayakta oran 1'in
            // ustunde (kivrim yok, dogru) ama comelince de 0.94'un altina inmiyor, yani POZ HIC
            // DEVREYE GIRMIYOR ve kok tek basina iniyor -> dizlere kadar yere batma.
            //
            // Cozum: yalnizca poz DEVREDE DEGILKEN (yani ayaktayken) referansi tazele.
            //  - YUKARI hizli: acilista gercek boya bir saniyede oturur.
            //  - ASAGI cok yavas: comelme hareketi referansi pesinden surukleyemez, ama bir
            //    kerelik bozuk okuma zamanla kendini onarir.
            // Comelmeye baslar baslamaz agirlik yukselir ve referans DONAR.
            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            if (!_measured)
            {
                _standHeadY = headY;
                _soleOffset = LowestContactY() - _ik.groundY;
                _measured = true;
            }
            else if (_w < 0.01f)
            {
                _standHeadY = headY > _standHeadY
                    ? Mathf.Lerp(_standHeadY, headY, 1f - Mathf.Exp(-8f * dt))
                    : Mathf.Lerp(_standHeadY, headY, 1f - Mathf.Exp(-0.15f * dt));
                _soleOffset = Mathf.Lerp(_soleOffset, LowestContactY() - _ik.groundY,
                                         1f - Mathf.Exp(-2f * dt));
            }
            _standHeadY = Mathf.Clamp(_standHeadY, 0.9f, 2.2f); // insan disi degere kilitlenmesin

            if (disableCrouchClip && _crouchLayer >= 0)
                _anim.SetLayerWeight(_crouchLayer, 0f);

            heightRatio = _standHeadY > 0.1f ? headY / _standHeadY : 1f;

            // Oran 1.0 -> 0.7133 arasinda 0'dan 1'e cikar. InverseLerp azalan araligi da dogru
            // isler (ikinci sinir birinciden kucuk).
            float target = Mathf.InverseLerp(_pose.startsAtHeightRatio, _pose.appliesAtHeightRatio, heightRatio);
            _w = Mathf.Lerp(_w, Mathf.Clamp01(target), 1f - Mathf.Exp(-smooth * Mathf.Max(0.0001f, Time.deltaTime)));
            weight = _w;

            // Animatorun urettigi poz -> yakalanan poz. Iki gecerli poz arasi slerp; ara deger
            // de gecerli. Kemigin o anki localRotation'i animator cikisidir (sira 20, animator
            // guncellemesinden sonra).
            // AYAKTA/YURURKEN HIC DOKUNMA. Zemin sinirini de buraya aldik: disarida calisirken
            // yurume dongusunde ayak dogal olarak referansin altina iniyor ve sinir her karede
            // koku yukari itiyordu — govde zipliyor, ayak yere basmiyor gibi gorunuyordu
            // (sürüklenme + bozuk diz). Sinir yalnizca pozun devrede oldugu anda gerekli.
            clampLift = 0f;
            if (_w <= 0.001f) return;

            for (int i = 0; i < _bones.Length; i++)
            {
                var t = _bones[i];
                if (t == null) continue;
                t.localRotation = Quaternion.Slerp(t.localRotation, _pose.bones[i].rotation, _w);
            }

            ClampToGround();
        }
    }
}
