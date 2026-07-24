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

        [Header("Teshis (salt okunur)")]
        [Tooltip("Oyuncunun su anki boy orani: 1.0 = ayakta.")]
        public float heightRatio = 1f;
        [Tooltip("Pozun uygulanma agirligi: 0 = animator pozu, 1 = yakalanan poz.")]
        public float weight;

        CrouchPoseAsset _pose;
        AvatarIKController _ik;
        Animator _anim;
        Transform[] _bones;
        int _crouchLayer = -1;

        float _standHeadY;   // ayaktaki kafa yuksekligi — BIR KEZ olculur
        bool _measured;
        float _w;            // yumusatilmis agirlik

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

            if (_anim.runtimeAnimatorController != null)
                _crouchLayer = _anim.GetLayerIndex("Crouch");
        }

        void LateUpdate()
        {
            if (!apply || _pose == null || _ik == null || _ik.headBone == null) return;

            // Ayaktaki kafa yuksekligini BIR KEZ olc. Acilista takip oturmadan olcmemek icin
            // makul yukseklik sarti var; oyuncu dogdugunda ayakta oldugu icin bu referans dogru.
            // (AvatarIKController'in "sadece yukari giden" boy mandalina bilerek bagli degiliz;
            //  ileride giris ekranindaki kalibrasyon bu degerin yerine gececek.)
            float headY = _ik.headBone.position.y - _ik.groundY;
            if (!_measured)
            {
                if (headY < 0.8f) return;
                _standHeadY = headY;
                _measured = true;
            }

            if (disableCrouchClip && _crouchLayer >= 0)
                _anim.SetLayerWeight(_crouchLayer, 0f);

            heightRatio = _standHeadY > 0.1f ? headY / _standHeadY : 1f;

            // Oran 0.94 -> 0.65 arasinda 0'dan 1'e cikar. InverseLerp azalan araligi da dogru
            // isler (ikinci sinir birinciden kucuk).
            float target = Mathf.InverseLerp(_pose.startsAtHeightRatio, _pose.appliesAtHeightRatio, heightRatio);
            _w = Mathf.Lerp(_w, Mathf.Clamp01(target), 1f - Mathf.Exp(-smooth * Mathf.Max(0.0001f, Time.deltaTime)));
            weight = _w;
            if (_w <= 0.001f) return; // ayakta: animatorun pozuna hic dokunma

            // Animatorun urettigi poz -> yakalanan poz. Iki gecerli poz arasi slerp; ara deger
            // de gecerli. Kemigin o anki localRotation'i animator cikisidir (sira 20, animator
            // guncellemesinden sonra).
            for (int i = 0; i < _bones.Length; i++)
            {
                var t = _bones[i];
                if (t == null) continue;
                t.localRotation = Quaternion.Slerp(t.localRotation, _pose.bones[i].rotation, _w);
            }
        }
    }
}
