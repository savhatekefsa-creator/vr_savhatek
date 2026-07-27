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

    // SIRA 90 = AvatarIKController'DAN (70) SONRA: o, kok konumunu ve kafa kemigini LateUpdate'te
    // yaziyor; biz nihai durumu gormeden orani hesaplayamayiz.
    [DefaultExecutionOrder(90)]
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
        [Tooltip("Ne olup bittigi — poz calismiyorsa sebebi burada yazar.")]
        public string durum = "-";

        CrouchPoseAsset _pose;
        AvatarIKController _ik;
        Animator _anim;
        Transform[] _bones;
        int _crouchLayer = -1;

        // Ayakta boy referansi burada TUTULMUYOR: AvatarIKController.StandingHeight'tan okunuyor.
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

            // AYAKTA BOY REFERANSI ARTIK BIZDEN DEGIL. AvatarIKController boyu tek seferlik
            // KALIBRE ediyor (StandingHeight) ve kilit comelmisken alinmissa kendi kendini
            // yukari onariyor. Onceden burada kendi olcumumuz vardi; iki ayri yer ayni seyi
            // bagimsiz hesaplayinca kacinilmaz olarak celisiyorlar. Tek dogru kaynak onunki.
            if (!_ik.FitLocked) { durum = "kalibrasyon bekleniyor"; return; }
            float standH = _ik.StandingHeight;
            if (standH < 0.5f) { durum = "StandingHeight gecersiz: " + standH.ToString("F2"); return; }

            float headY = _ik.headBone.position.y - _ik.groundY;

            // TABAN PAYI bize ozel (zemin siniri icin): ayaktaki en alcak temas kemiginin
            // zeminden yuksekligi. Kemik konumu mesh yuzeyi degil — parmak kemigi tabandan
            // ~3 cm yukarida — o farki koruyoruz. Bir kez, ayakta olcmek yeterli.
            if (!_measured && _w < 0.01f)
            {
                _soleOffset = LowestContactY() - _ik.groundY;
                _measured = true;
            }

            heightRatio = headY / standH;

            // Oran 1.0 -> 0.7133 arasinda 0'dan 1'e cikar. InverseLerp azalan araligi da dogru
            // isler (ikinci sinir birinciden kucuk).
            float target = Mathf.InverseLerp(_pose.startsAtHeightRatio, _pose.appliesAtHeightRatio, heightRatio);
            _w = Mathf.Lerp(_w, Mathf.Clamp01(target), 1f - Mathf.Exp(-smooth * Mathf.Max(0.0001f, Time.deltaTime)));
            weight = _w;

            // ONUN COMELME KLIBINI ORANSAL SONDUR — koşulsuz sifirlamak TEHLIKELIYDI: bizim poz
            // herhangi bir sebeple devreye girmezse (kalibrasyon bitmemis, asset yok, kemik
            // bulunamamis) ortada HIC comelme kalmiyordu, yani temel halden bile kotu.
            // Simdi tam bizim agirligimiz kadar soner: biz %0 iken onunki tam calisir, biz
            // %100 iken tamamen susar, arada ikisi toplamda bir eder. Ustelik biz erken
            // return edersek bu satira hic gelinmez ve onunki hic bozulmaz.
            if (disableCrouchClip && _crouchLayer >= 0)
                _anim.SetLayerWeight(_crouchLayer, _anim.GetLayerWeight(_crouchLayer) * (1f - _w));

            // Animatorun urettigi poz -> yakalanan poz. Iki gecerli poz arasi slerp; ara deger
            // de gecerli. Kemigin o anki localRotation'i animator cikisidir (sira 20, animator
            // guncellemesinden sonra).
            // AYAKTA/YURURKEN HIC DOKUNMA. Zemin sinirini de buraya aldik: disarida calisirken
            // yurume dongusunde ayak dogal olarak referansin altina iniyor ve sinir her karede
            // koku yukari itiyordu — govde zipliyor, ayak yere basmiyor gibi gorunuyordu
            // (sürüklenme + bozuk diz). Sinir yalnizca pozun devrede oldugu anda gerekli.
            clampLift = 0f;
            if (_w <= 0.001f)
            {
                durum = $"ayakta (oran {heightRatio:F2}, esik {_pose.startsAtHeightRatio:F2})";
                return;
            }
            durum = $"poz %{_w * 100f:F0} (oran {heightRatio:F2})";

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
