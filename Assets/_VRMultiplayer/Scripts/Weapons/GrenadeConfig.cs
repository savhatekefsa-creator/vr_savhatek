using UnityEngine;

namespace VRMultiplayer.Weapons
{
    public enum GrenadeType : byte { Frag = 0, Flash = 1, Smoke = 2 }

    /// <summary>
    /// Veri-tabanli el bombasi ayarlari (Resources/GrenadeConfigs/, isim eslesmeli — asset adi
    /// sahnedeki/spawn edilen objenin CleanName'i ile AYNI olmali, orn "Grenade 1"). Silah
    /// sistemindeki profil/config kalibiyla ayni felsefe: yeni bomba = config asset, SIFIR kod.
    /// </summary>
    [CreateAssetMenu(fileName = "Grenade_Config", menuName = "VRMultiplayer/Grenade Config")]
    public class GrenadeConfig : ScriptableObject
    {
        [Header("Tip")]
        public GrenadeType type = GrenadeType.Frag;

        [Header("Funye")]
        [Tooltip("Yere ILK temastan patlamaya kadar gecen sure (sn).")]
        public float fuseSeconds = 1f;

        [Header("Fizik")]
        [Tooltip("Rigidbody kutlesi (kg). Gercek M67 ~0.4.")]
        public float mass = 0.4f;
        [Tooltip("El hizi carpani — VR bilek savurmasi gercek kol firlatmasindan zayif kalir.")]
        public float throwVelocityScale = 1.3f;
        [Tooltip("Firlatma aninda eklenen rastgele takla (derece/sn) — bomba donerek ucar.")]
        public float throwSpinDegPerSec = 540f;
        [Range(0f, 1f)] [Tooltip("Sekme katsayisi — betonda 1-2 kisa sekme icin ~0.25.")]
        public float bounciness = 0.25f;
        public float friction = 0.6f;

        [Header("Frag hasari (yalniz type=Frag)")]
        [Tooltip("Patlama merkezindeki hasar; mesafeyle LINEER azalip yaricapta 0'a iner. " +
                 "Dost atesi kesilir; KENDINE hasar SERBEST (bilincli tasarim karari).")]
        public int centerDamage = 80;
        [Tooltip("Hasar yaricapi (m).")]
        public float damageRadius = 6f;

        [Header("Flash (yalniz type=Flash)")]
        [Tooltip("Korlestirmenin etkili oldugu azami mesafe (m).")]
        public float flashRadius = 15f;
        [Tooltip("Tepe parlaklikta bekleme suresi (sn) — bu sure boyunca ekran tamamen beyaz " +
                 "kalir, sonra sonumlenmeye baslar.")]
        public float flashHoldSeconds = 0.25f;
        [Tooltip("Flash'i TAM yiyen (patlamaya yakin ve donuk bakan) oyuncunun kor kalma suresi " +
                 "(sn, bekleme dahil). Uzaktan ya da sirti donukken yiyen dogal olarak daha kisa " +
                 "surede toparlanir. Korlugu uzatmak icin bu degeri buyut.")]
        public float flashBlindSeconds = 8f;

        [Header("Smoke (yalniz type=Smoke)")]
        [Tooltip("Duman perdesinin EMISYON suresi (sn). Bu sure dolunca yeni duman uretilmez, " +
                 "havadaki duman dogal olarak dagilir — yani perde birkac saniye daha gorunur. " +
                 "0 = prefabin kendi suresi kullanilir.")]
        public float smokeDuration = 30f;

        [Header("Patlama gorseli (prefab referansi — Resources sart degil, referans build'e girer)")]
        [Tooltip("Patlama aninda bu prefab instantiate edilir. War FX prefablarindaki " +
                 "CFX_AutoDestructShuriken bitince kendini yok eder — havuz/temizlik gerekmez.")]
        public GameObject explodeFx;
        [Tooltip("Efekt olcegi — dunya/silahlar 2x olcekli oldugu icin patlamalar da buyutulur, " +
                 "yoksa yari boy gorunup gozden kacar.")]
        public float fxScale = 2f;

        [Header("Carpisma sekli")]
        [Tooltip("Modelin kendi mesh hull'u yerine basit bir KURE collider kullanilir. Bombanin " +
                 "emniyet kolu/pimi hull'u duzensiz yapar; boyle bir sekil yere carpinca tahmin " +
                 "edilemez sekilde ziplar ve saga sola firlar. Kure hem gercekci hem kararlidir.")]
        public bool simpleCollider = true;
        [Tooltip("Kure yaricapi (m, silah-lokal). 0 = otomatik: govde mesh'inin sinirlarindan.")]
        public float colliderRadius = 0f;
        [Tooltip("Kure merkezi (silah-lokal). Sifir = govde mesh'inin merkezi.")]
        public Vector3 colliderCenter = Vector3.zero;
        [Tooltip("Firlattiktan sonra ATANIN kendi collider'lariyla carpismanin kapali kalacagi " +
                 "sure (sn). VR'da bomba elin icinden dogar; bu pencere olmazsa oyuncunun kendi " +
                 "govdesine carpip saçma yonlere firlar ve funye aninda baslar. 0 = kapali.")]
        public float selfCollisionIgnoreSeconds = 0.35f;

        [Header("Pim (bos birakilirsa ad sezgisiyle bulunur: 'ring'/'hook' iceren dugumler)")]
        [Tooltip("Pimi olusturan cocuk dugum adlari. BOS birakilirsa adinda 'ring' ya da 'hook' " +
                 "gecen tum dugumler pim sayilir — paketteki uc bombanin da pimi boyle bulunur, " +
                 "asset doldurmaya gerek yok. Emniyet kolu (handle/flap) bilerek disarida: o " +
                 "bombada kalir.")]
        public string[] pinNodes = new string[0];
        [Tooltip("Pimin cekildikten sonra elde durdugu yer (el anchor'ina gore lokal, metre).")]
        public Vector3 pinHandLocalPosition = Vector3.zero;
        [Tooltip("Pimin elde durus acisi (el anchor'ina gore lokal euler).")]
        public Vector3 pinHandLocalEuler = Vector3.zero;
        [Tooltip("Pimi cekmek icin bos elin bombaya yaklasmasi gereken mesafe (m).")]
        public float pinPullReach = 0.45f;

        [Header("Nisan yayi (bomba eldeyken thumbstick)")]
        [Tooltip("Kirmizi yorunge yayinin varsaydigi firlatma hizi (m/s). El hizi degil; " +
                 "sabit nisan hizi — throwVelocityScale ile carpilir.")]
        public float aimArcSpeed = 9f;

        [Header("Ses (Resources yolu; klip yoksa SESSIZ kalir, hata degil)")]
        public string pinClip = "";
        public string bounceClip = "";
        public string explodeClip = "";
        [Range(0f, 1f)] public float explodeVolume = 1f;
        [Tooltip("Patlamanin duyuldugu azami mesafe (m) — atis seslerinden cok daha uzak.")]
        public float explodeMaxDistance = 300f;
    }
}
