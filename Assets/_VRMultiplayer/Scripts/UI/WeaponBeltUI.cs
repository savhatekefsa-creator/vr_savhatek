using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRMultiplayer.UI
{
    /// <summary>
    /// KEMER HUD'u — kafayi asagi egince gorus alaninin ALTINDA yan yana beliren dairesel silah
    /// yuvalari. Eski silah CARKININ (grip + thumbstick radyal menu) yerini alir; cark ve
    /// stick ile secim TAMAMEN kaldirildi.
    ///
    /// KURALLAR (ekip tasarimi):
    ///  - GORUNURLUK: halkalar HEP VAR. Bel hizasinda dururlar; asagi bakmak onlari acmaz,
    ///    yalnizca goruse sokar. Kavrama da her zaman aciktir — bakmadan, kas hafizasiyla
    ///    silah cekilebilir. Arkasinda beden/zirh modeli YOK: havada duran holografik halkalar.
    ///  - YUVA: 3 halka, kategori bagi YOK — istedigini istedigi halkaya koyarsin. Sinir
    ///    kategori kotasindan gelir (bkz. WeaponInventory.CapOf).
    ///  - RENK: bos/dolu halka MAVI, elin yaklastigi halka TURUNCU-SARI neon.
    ///  - KUSANMA: elini halkanin uzerine goturup GRIP'e basmak silahi dogrudan O ELE verir
    ///    (yerden alir gibi). Grip'i birakinca silah kendi yuvasina doner — bu, HandGrabber'in
    ///    zaten var olan "birakilan silah cantaya gider" davranisinin ta kendisi, ek kod yok.
    ///
    /// Kavrama kapisi <see cref="HandGrabber.TryGrab"/> icinden <see cref="TryGrabFromBelt"/>
    /// ile sorulur; equip <see cref="HandGrabber.EquipIntoHand"/> uzerinden gider (silah HANGI
    /// EL uzandiysa ona dogar). Envanterin kendisine (3 yuvali canta, mermi kaydi) DOKUNULMADI —
    /// bu bilesen yalnizca onun gorunen yuzu.
    ///
    /// NOT (kapsam): kemer yalnizca CANTANDA olani gosterir. "Oyundaki tum silahlar hep kemerde"
    /// olsaydi raf gereksizlesir ve her yuva bedava/mermisi sifirlanmis silah pinari olurdu;
    /// silahlar raftan alinmaya devam ediyor, kemer onlari geri cagiriyor.
    /// </summary>
    public class WeaponBeltUI : MonoBehaviour
    {
        public static WeaponBeltUI Instance { get; private set; }

        // YUVA-KATEGORI BAGI KALDIRILDI (2026-08-17). Eskiden halkalar sabitti — 1. HEAVY,
        // 2. PISTOL, 3. GRENADE — ve esya kategorisine gore OTOMATIK yerlesirdi. Artik her
        // halka her seyi kabul ediyor; sinir kategori KOTASINDAN geliyor (bkz.
        // WeaponInventory.CapOf). Etiket bos yuvada yalnizca sira numarasi.
        static readonly string[] SlotLabel = { "1", "2", "3" };

        // ESIK ACISI ILE beltOffset BIRBIRINE BAGLI — birini degistiren otekini de kontrol
        // etmeli. Kemerin OTURDUGU aci offset'ten cikar: atan(|y| / z).
        //
        //   (-0.520, 0.200) -> atan(0.520/0.200) ≈ 69 derece, kafadan 0.56 m (BEL HIZASI)
        //
        // BOYUN YORGUNLUGU DERSI: esik ile kemerin acisi BIRLIKTE degisir. 58 -> 52
        // gecisinde yalnizca esik indi, kemer 62 derecede kaldi; menu daha erken aciliyordu
        // ama halkalara RAHAT BAKMAK icin yine 62 dereceye egilmek gerekiyordu. Boynu yoran
        // sey esik degil, KEMERIN OTURDUGU ACI.
        //
        // 2026-08-17: kullanici "envanter cok goz onunde aciliyor, daha cok egilelim" dedi.
        // Ayni ders TERS yonde uygulandi — yalnizca esigi 56'ya cikarsaydik menu gec acilir
        // ama kemer yine 53 derecede, yani goz onunde durmaya devam ederdi. Ikisi birlikte
        // indi: esik 44 -> 56, kemer 53 -> 60 derece.
        //
        // AYNI GUN, CIHAZ TESTINDEN SONRA: "halkalar belime yakin durmuyor, daha asagi olmali,
        // belimde gibi hissettirmeli". 60 derece hala GOGUS hizasiydi. Gercek bel, yetiskinde
        // kafadan ~0.50-0.60 m asagida — o yuzden kemer 69 dereceye ve 0.56 m'ye indi.
        // MESAFE ARTTI, yani halkalar uzaklasti ve KUCUK gorunurdu; bu yuzden yaricap da
        // 0.058 -> 0.082'ye buyudu (kullanicinin "halkalari buyutecegiz" demesiyle ayni yone
        // dusuyor: 3 yuvaya inmesinin sebebi de buydu). Gorunen aci boyu 7.7 -> 8.4 derece.
        //
        // GEOMETRI INATCIDIR: kemerin acisini dusurmek demek onu YUKARI (|y| kucuk) ve
        // ILERI (z buyuk) almak demektir. "Govdeye yapisik bel hizasi" ile "az egilerek bak"
        // ayni anda saglanamaz; bu ayarda kemer bel yerine GOGUS hizasinda duruyor.
        // Toplam mesafe (0.43 m) bilerek korundu, yani daha uzak GORUNMEZ.
        // KAFA-EGME ESIGI KALDIRILDI (2026-08-17, cihaz karari).
        //
        // Kemer eskiden bir MENUYDU: belli bir aciDAN fazla asagi bakinca acilir, kafayi
        // kaldirinca kapanirdi. Kullanici bunu reddetti: "halkalar bel hizasinda hep var gibi
        // gorunecek, bi ac bi kapa seklinde olmayacak. Kullanici bakmadan bile elini uzatip
        // bir halkaya denk getirirse oradan grip ile alabilecek."
        //
        // Yani kemer artik bir menu degil, oyuncunun BEDENININ bir parcasi. Halkalar hep var;
        // asagi bakmak onlari ACMIYOR, sadece GORMENI sagliyor. Kavrama da her zaman acik —
        // kas hafizasiyla bakmadan silah cekmek bu sayede mumkun.
        //
        // Esikler silindigi icin "esik ile kemerin acisini birlikte tut" dersi de artik
        // gecersiz: kemerin acisi yalnizca NEREDE durdugunu belirliyor.

        // Konum / aralik / yazi Play'de CANLI degisir (her kare uygulanir). Halkanin yaricapi
        // ve kalinligi mesh'e islendigi icin ilk acilista okunur — onlari degistirdikten sonra
        // Play'i yeniden baslatmak gerekir.
        // OLCEKLER MESAFEYE BAGLI. Kemer 0.49 m'den 0.42 m'ye cekilince asagidaki uzunluklarin
        // hepsi ayni carpanla (0.86) kucultuldu. Sebep: VR'da bir arayuzu YAKINLASTIRIP
        // olculerini sabit tutmak onu buyutur — uc halka gorus alanina yayilir, dis halkalar
        // lensin bulanik kenarina kacar. Ayni carpanla kuculunce GORUNUM birebir korunur,
        // yalnizca daha yakin ve el icin daha kolay olur. Mesafeyi degistiren bu blogu da
        // ayni oranda olceklemeli.
        [Header("Yerlesim (Play'de canli ayarlanir)")]
        [Tooltip("Kemerin KAFAYA gore konumu (metre): x=saga, y=asagi/yukari, z=ileri. " +
                 "Varsayilan GOGUS hizasi, el mesafesinde. |y|'yi kucultmek + z'yi buyutmek " +
                 "kemeri yukari alir (daha az egilirsin); tersi bele indirir (daha cok egilirsin).")]
        public Vector3 beltOffset = new Vector3(0f, -0.620f, 0.155f);
        [Tooltip("Halka merkezleri arasi mesafe (metre). hoverRadius'un IKI KATINDAN buyuk " +
                 "olmali, yoksa komsu halkalarin kavrama bolgeleri cakisir.")]
        public float slotSpacing = 0.215f;
        [Tooltip("Halkanin dis yaricapi (metre). Kemer her indiginde buyutuluyor: uzaklastikca " +
                 "kucuk gorunur, ayrica kullanici acikca 'halkalari buyut' dedi.")]
        public float ringRadius = 0.092f;
        [Tooltip("Halka cizgi kalinligi (metre).")]
        public float ringThickness = 0.008f;
        [Tooltip("Silah adinin halka merkezinin ne kadar altinda duracagi (metre).")]
        public float labelDrop = 0.140f;
        [Tooltip("Yazi satir yuksekligi (metre) — VR'da okunabilirligi punto degil aci belirler.")]
        public float labelHeight = 0.022f;

        [Header("Kavrama")]
        [Tooltip("El halkanin merkezine bu kadar yaklasinca yuva 'elin altinda' sayilir (metre).\n\n" +
                 "slotSpacing'in YARISINDAN kucuk olmali. Onceki ayarda degildi (0.094 vs " +
                 "0.146/2 = 0.073): komsu halkalarin kavrama bolgeleri cakisiyordu ve iki " +
                 "halkanin ortasina uzanan el, hangisine biraz daha yakinsa onu aliyordu — " +
                 "yani yanlis yuvayi kapmak mumkundu. Simdi 0.104 < 0.1075.")]
        public float hoverRadius = 0.104f;

        [Header("Kafa takibi")]
        [Tooltip("Kemer kafa YONUNU takip eder ama pitch'i takip ETMEZ — bakisin degil bedenin " +
                 "onunde durur. Bu aciDAN kucuk kafa cevirmelerinde hic kimildamaz (derece), " +
                 "yoksa uzanirken hedef elin altindan kacar.")]
        public float yawDeadzoneDegrees = 30f;
        [Tooltip("Olu bolge asilinca kemerin yeni yone donme hizi (derece/sn).")]
        public float yawFollowSpeed = 150f;

        [Header("Renkler")]
        [Tooltip("Varsayilan halka: BEYAZ. (Mavi denendi ve cihazda begenilmedi.)")]
        public Color ringIdle = new Color(1f, 1f, 1f, 0.92f);
        [Tooltip("Elin uzandigi halka: parlayan TURUNCU-SARI neon.")]
        public Color ringHot = new Color(1f, 0.78f, 0.34f, 1f);
        [Tooltip("Bos yuva: ayni beyaz, soluk.")]
        public Color ringDim = new Color(1f, 1f, 1f, 0.32f);
        [Tooltip("KOTA DOLU: elindeki turden kemerde yer kalmadi. Faz 2'de (kapasite kurali " +
                 "geldiginde) devreye girer; su an secilmiyor.")]
        public Color ringFull = new Color(1f, 0.42f, 0.45f, 0.95f);
        public Color discIdle = new Color(0.03f, 0.09f, 0.14f, 0.50f);
        public Color discHot = new Color(0.16f, 0.10f, 0.01f, 0.65f);

        [Header("Parlama (yumusak hale)")]
        [Tooltip("Halkanin arkasindaki yumusak hale — 'blur' hissini veren sey. GERCEK blur " +
                 "post-process ister ve Quest'te world-space arayuz icin kare butcesinden yer; " +
                 "bunun yerine tek quad'lik radyal bir gradyan kullaniliyor. 0 = kapali.")]
        [Range(0f, 1f)] public float glowStrength = 0.30f;
        [Tooltip("Halenin halka yaricapina gore yayilimi. 1.7 = hale halkanin 1.7 kati " +
                 "genislikte. Buyutmek komsu halkanin uzerine tasar: yaricap 0.082 ve aralik " +
                 "0.200 iken 2.0'da hale komsunun cizgisine kadar uzanip pus birakiyordu.")]
        public float glowSpread = 1.7f;
        public Color labelIdle = new Color(0.90f, 0.96f, 1f, 1f);
        public Color labelHot = new Color(1f, 0.82f, 0.45f, 1f);
        public Color labelDim = new Color(0.85f, 0.88f, 0.92f, 0.45f);

        [Header("Goze girmeme — kafa acisina gore SOLMA")]
        // Halkalar HEP VAR ve HER ZAMAN kavranabilir (kullanicinin kurali). Ama surekli tam
        // parlaklikta durunca "kafami az egdigimde bile belirgin, gozu rahatsiz ediyor" oldu.
        //
        // Cozum GORUNURLUGU acidan turetmek, VARLIGI degil: kemer duruyor, kavrama calisiyor,
        // yalnizca opakligi kafa asagi indikce artiyor. Boylece hem "bakmadan uzanip alabilme"
        // korunuyor hem de duz bakarken goz alanini kirletmiyor.
        [Tooltip("Bu acinin USTUNDE (kafa daha yukari) halkalar tamamen seffaf. Derece, " +
                 "0 = duz ileri bakis.")]
        public float fadeStartPitch = 40f;
        [Tooltip("Bu aciDA ve altinda (kafa daha asagi) halkalar TAM parlaklikta.")]
        public float fadeFullPitch = 68f;
        [Tooltip("Solmanin en dip degeri. 0 = duz bakarken tamamen gorunmez. Sifirdan buyuk " +
                 "birakmak halkalarin 'orada oldugunu' hafifce hatirlatir.")]
        [Range(0f, 1f)] public float fadeFloor = 0f;

        // Cizim sirasi: disk EN ARKADA ve DERINLIK SINAMALI (silah onizlemesi onun ONUNDE
        // durdugu icin onizlemeyi ORTMEZ), hale onun onunde, halka ve yazi ise her zaman ustte.
        const int QueueDisc = 3000;
        const int QueueGlow = 3001;
        const int QueueRing = 3002;
        const int QueueLabel = 3003;

        /// <summary>
        /// Halkanin arkasindaki yumusak hale dokusu. Merkezde degil HALKA YARICAPINDA
        /// parlar ve iki yona sonumlenir — yani bulanik bir halka gibi gorunur, bulanik bir
        /// disk gibi degil.
        ///
        /// Tek doku, tum halkalarda paylasilir (statik). Boyut kucuk: gradyan zaten yumusak,
        /// 128 piksel VR'da bile bantlanma gostermiyor.
        /// </summary>
        static Texture2D _glowTex;
        static Texture2D GlowTexture(float spread)
        {
            if (_glowTex != null) return _glowTex;

            const int S = 128;
            float peak = 1f / Mathf.Max(1.05f, spread);   // halkanin dokudaki normalize yaricapi
            const float sigma = 0.15f;

            _glowTex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                Vector2 v = new Vector2(x - (S - 1) * 0.5f, y - (S - 1) * 0.5f) / (S * 0.5f);
                float d = v.magnitude;
                float t = (d - peak) / sigma;
                float a = Mathf.Exp(-0.5f * t * t);
                // Kenarda tam sifira in: quad'in koseleri gorunur bir kare kenari birakmasin.
                a *= Mathf.Clamp01((1f - d) / 0.18f);
                _glowTex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
            }
            _glowTex.Apply();
            return _glowTex;
        }

        class Ring
        {
            public Transform root;
            public Material ringMat;
            public Material glowMat;
            public Material discMat;
            public TextMesh label;
            public string labelKey;    // label.text bu anahtardan uretildi (bosuna string uretme)
            // Onizleme olcegi artik HALKAYA degil ESYAYA ait (WeaponInventory.Item.Fit):
            // ayni halkada iki farkli silah durabiliyor.
        }

        readonly Ring[] _rings = new Ring[3];
        Transform _belt;
        bool _open;
        float _yaw;          // kemerin su anki yonu (derece, dunya)
        bool _yawValid;
        int _hovered = -1;
        HandGrabber _grabber;
        float _nextGrabberScan;
        bool _pcOpen;        // gozluksuz test: TAB ile acik tutulur
        float _visibility = 1f;   // kafa acisindan turetilen opaklik carpani (bkz. fadeStartPitch)

        // Elde tutulan silahlarin tur anahtari. TypeKey icerde Object.name okur ve HER cagride
        // yeni bir string uretir; kare basina iki alloc olmasin diye OBJE REFERANSINA gore
        // onbelleklenir (silah degismedikce yeniden hesaplanmaz).
        GrabbableObject _keyObjHeld;
        string _keyHeld;

        string HeldKeyCached(GrabbableObject g, ref GrabbableObject cachedObj, ref string cachedKey)
        {
            if (g == null) { cachedObj = null; cachedKey = null; return null; }
            if (!ReferenceEquals(g, cachedObj)) { cachedObj = g; cachedKey = WeaponInventory.TypeKey(g); }
            return cachedKey;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponBeltUI");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponBeltUI>();
        }

        void Awake() => Instance = this;

        void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ acilma / kapanma

        void Update()
        {
            var cam = Camera.main;
            var inv = WeaponInventory.Instance;

            // Kemer YALNIZCA sahada yasar: mod secimi / oyuncu girisi / kalibrasyon ekranlarinda
            // henuz yerel oyuncu (dolayisiyla HandGrabber) yoktur, kimseye asagi bakarken halka
            // gostermeyelim. Insa modunda ise grip PROP PALETINI aciyor — ayni tus paylasilamaz.
            if (cam == null || inv == null || LocalGrabber() == null ||
                XRButtons.GameplayInputSuppressed || LocalPlayerDead())
            {
                SetOpen(false);
                return;
            }

            Transform head = cam.transform;

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
#endif

            // HALKALAR HEP ACIK — kemer bir menu degil, bedenin parcasi (bkz. sinif basi).
            // Asagi bakmak onu ACMIYOR; yalnizca GORUNURLUGUNU artiriyor (asagida).
            SetOpen(true);

            // Kafa asagi bakis acisi. forward.y negatiflestikce buyur.
            float pitchDown = -Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float t = Mathf.InverseLerp(fadeStartPitch, Mathf.Max(fadeStartPitch + 1f, fadeFullPitch), pitchDown);
            _visibility = Mathf.Lerp(fadeFloor, 1f, Mathf.SmoothStep(0f, 1f, t));

            Layout(head, inv);

#if ENABLE_INPUT_SYSTEM
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) PcEquip(0);
                if (kb.digit2Key.wasPressedThisFrame) PcEquip(1);
                if (kb.digit3Key.wasPressedThisFrame) PcEquip(2);
            }
#endif
        }

        void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            if (open) EnsureRings();
            if (_belt != null) _belt.gameObject.SetActive(open);
            if (!open)
            {
                _hovered = -1;
                _yawValid = false;   // bir dahaki aciliste taze yone otur
                HideAllPreviews();
            }
        }

        // Kapatmayi TEK yoldan yap: _open'i dogrudan sondurmek envanterin onizleme klonlarini
        // sahnede asili birakirdi (carkta ogrenilen ders).
        void OnDisable() { SetOpen(false); }

        static void HideAllPreviews()
        {
            var inv = WeaponInventory.Instance;
            if (inv == null) return;
            foreach (var e in inv.AllItems)
                if (e.Preview != null) e.Preview.SetActive(false);
        }

        bool LocalPlayerDead()
        {
            var g = LocalGrabber();
            if (g == null) return false;
            var h = g.GetComponent<PlayerHealth>();
            return h != null && h.IsDead;
        }

        // ------------------------------------------------------------------ yerlesim

        /// <summary>Kemeri her kare kafanin ONUNE-ALTINA yerlestirir ve yuva durumlarini isler.
        ///
        /// KAFA PITCH'INI TAKIP ETMEZ, bilerek: kemer bakisa civilenseydi asagi baktikca
        /// halkalar da asagi kayar, el hicbir zaman yetisemezdi. Konum kafanin YATAY yonunden
        /// (yaw) turetilir; halkalar da o konumdan kafaya BAKACAK sekilde egilir — yani egim
        /// beltOffset'ten kendiliginden cikar, ayrica ayarlanacak bir aci yok.</summary>
        void Layout(Transform head, WeaponInventory inv)
        {
            // Yatay bakis yonu. Kafa neredeyse dik asagi bakiyorsa forward'in yatay bileseni
            // erir — o durumda kafanin UST ekseni ileriyi gosterir (kemer tam da bu acilarda
            // acik oldugu icin bu dal gercekten calisir).
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = head.up; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            // KEMER GOZLUGU BIREBIR TAKIP EDER — olu bolge ve yumusatma YOK.
            //
            // Eskiden 30 derecelik bir olu bolge ve 150 derece/sn'lik bir donme hizi vardi;
            // gerekcesi "uzanirken hedef elin altindan kacmasin" idi. Kullanici bunu reddetti:
            // "bedenimle beraber hareket etsin, Quest'e gore bak, ne kadar cevirirsem o da
            // donsun DIREKT." Kemer artik bir menu degil bedenin parcasi (bkz. sinif basi) ve
            // bedenin kafayla birlikte donmesi beklenen sey.
            //
            // BEDELI BILINCLI: uzanirken kafani cevirirsen halkalar da doner. Eski olu bolge
            // tam da bunu engelliyordu; geri istenirse yawDeadzoneDegrees yeniden baglanabilir.
            _yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            _yawValid = true;

            Quaternion yawRot = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 pos = head.position + yawRot * beltOffset;

            // YAZI TERSLIGI DERSI (kullanicinin bildirdigi ayna hatasi). Ilk surumde buraya
            // "kemer kafaya BAKSIN" diye LookRotation(kafa - kemer) yazilmisti. Sezgisel duruyor
            // ama YANLIS: Unity'de bir yuzeyin okunur tarafi +Z'nin ARKASIDIR — kamera +Z
            // yonune BAKARAK gorur. +Z'yi kafaya cevirmek paneli arkaya dondurur; yazi ayna
            // gorunur ve yerel +X oyuncunun SOLUNA duser (yuva sirasi da ters donerdi).
            //
            // Dogrusu, eski carkin kullandigi yon: +Z BAKIS YONUYLE AYNI tarafa, yani kafadan
            // kemere dogru. O zaman +X = oyuncunun SAGI, +Y = yukari-ileri; yazi duz okunur,
            // HEAVY solda baslar, -labelDrop yaziyi halkanin altina koyar.
            Vector3 viewDir = pos - head.position;          // kafadan kemere
            if (viewDir.sqrMagnitude < 1e-6f) viewDir = yawRot * Vector3.forward;
            viewDir.Normalize();

            // Yukari ipucu: yatay ileri. viewDir ile neredeyse ayni yone bakiyorsa (kemer
            // duz ILERIDE, hic asagida degil) LookRotation tekillesir — o durumda dunya
            // yukarisi ayni X eksenini verir.
            Vector3 upHint = yawRot * Vector3.forward;
            if (Mathf.Abs(Vector3.Dot(viewDir, upHint)) > 0.999f) upHint = Vector3.up;
            _belt.SetPositionAndRotation(pos, Quaternion.LookRotation(viewDir, upHint));

            // Aralik ve yazi konumu her kare yazilir ki Play'de canli ayarlanabilsin. Elin
            // hangi halkanin uzerinde oldugu BUNDAN SONRA olculur — yoksa aralik degistirilen
            // karede vurgu eski konumlara gore hesaplanirdi.
            float span = (_rings.Length - 1) * 0.5f;
            for (int i = 0; i < _rings.Length; i++)
            {
                _rings[i].root.localPosition = new Vector3((i - span) * slotSpacing, 0f, 0f);
                // -Z = oyuncuya dogru (bkz. yukaridaki yon dersi): yazi diskin onunde kalsin.
                _rings[i].label.transform.localPosition = new Vector3(0f, -labelDrop, -0.004f);
            }

            // El hangi yuvanin uzerinde? Iki el de bakilir; en yakin olan kazanir.
            _hovered = NearestSlot(HandsHoverProbe());

            var grabber = LocalGrabber();

            // ELINDEKININ kategorisi: halkalarin "kabul eder / dolu" rengi buna gore secilir.
            // Bos elle bakarken hicbir halka kirmizi yanmaz — kirmizi bir YASAK degil, "su an
            // tuttugun sey buraya girmez" demek.
            bool holding = false;
            WeaponCategory heldCat = WeaponCategory.Heavy;
            var heldObj = grabber != null ? (grabber.HeldRight != null ? grabber.HeldRight : grabber.HeldLeft) : null;
            if (heldObj != null)
            {
                holding = true;
                heldCat = WeaponInventory.CategoryOf(
                    HeldKeyCached(heldObj, ref _keyObjHeld, ref _keyHeld));
            }

            for (int i = 0; i < _rings.Length; i++)
            {
                var r = _rings[i];
                var items = inv.Slot(i);
                bool hot = _hovered == i;
                bool filled = items.Count > 0;

                // KOTA DOLU: elindekini bu yuva kabul edemiyor (yuva dolu ya da kategori
                // kotasi bitti). Yalnizca ELINDE BIR SEY VARKEN anlamli.
                bool blocked = holding && !inv.CanPlace(heldCat, i);

                Color ringCol = hot ? ringHot
                              : blocked ? ringFull
                              : filled ? ringIdle : ringDim;

                // SOLMA. Kafa yukaridayken halkalar seffaflasir ama VAR OLMAYA ve
                // KAVRANMAYA devam eder. Elin uzandigi halka bundan MUAF: bakmadan uzanip
                // sonra goz atinca hangisinin uzerinde oldugunu gorebilmelisin — sinyalin
                // tam da gerekli oldugu an bu.
                float vis = hot ? 1f : _visibility;
                UITheme.SetMaterialColor(r.ringMat, Fade(ringCol, vis));
                UITheme.SetMaterialColor(r.discMat, Fade(hot ? discHot : discIdle, vis));
                if (r.glowMat != null) UITheme.SetMaterialColor(r.glowMat, Fade(GlowColor(ringCol), vis));

                // Etiket: dolu yuvada esya adi (iki esya varsa "A + B"), bos yuvada sira no.
                string key = LabelKeyOf(items, i);
                if (r.labelKey != key)
                {
                    r.labelKey = key;
                    r.label.text = key;
                }
                r.label.color = Fade(hot ? labelHot : (blocked ? ringFull : filled ? labelIdle : labelDim), vis);

                PlaceSlotPreviews(r, items, hot, vis);
            }
        }

        /// <summary>Yuvanin etiketi. Iki esya varsa ikisi de yazilir — hangi halkada ne
        /// oldugunu kemere bakmadan hatirlamak icin.</summary>
        static string LabelKeyOf(IReadOnlyList<WeaponInventory.Item> items, int slot)
        {
            if (items.Count == 0) return SlotLabel[slot];
            if (items.Count == 1) return DisplayName(items[0].Key);
            return DisplayName(items[0].Key) + " + " + DisplayName(items[1].Key);
        }

        /// <summary>
        /// Yuvadaki esyalari halkanin icine yerlestirir. TEK esya ortada durur; IKI esya
        /// yan yana ve daha kucuk — kullanicinin kurali geregi "el hangisine yakinsa onu
        /// alacak", yani ikisinin AYRI birer konumu olmak zorunda.
        /// </summary>
        void PlaceSlotPreviews(Ring r, IReadOnlyList<WeaponInventory.Item> items, bool hot, float vis)
        {
            // Onizlemeler OPAK mesh'ler (silahin kendi materyali); alfalarini solduramayiz.
            // Bunun yerine gorunurluk esigin altindayken tamamen gizlenirler — kafa yukaridayken
            // bel hizasinda asili duran silahlar goz alanini en cok kirleten seydi.
            bool show = vis > 0.35f;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Preview == null) continue;
                if (it.Preview.activeSelf != show) it.Preview.SetActive(show);
                if (!show) continue;

                bool pair = items.Count > 1;
                // Iki esya: halkanin ic capinin yarisi kadar saga/sola. Tek esya: ortada.
                float side = pair ? (i == 0 ? -1f : 1f) * ringRadius * 0.42f : 0f;
                PlacePreview(r, it, side, pair, hot);
            }
        }

        /// <summary>Silahin GORSEL kopyasini halkanin ortasina, yan gorunumde ve halkaya
        /// SIGACAK olcekte yerlestirir. Olcek/merkez silah basina BIR KEZ olculur (mesh
        /// bounds'u degismez), sonraki karelerde sadece transform yazilir.</summary>
        void PlacePreview(Ring r, WeaponInventory.Item e, float sideOffset, bool pair, bool hot)
        {
            if (e == null || e.Preview == null) return;

            var t = e.Preview.transform;

            // YAN GORUNUM: namlu ekseni (profilden) halkanin SAG eksenine cevrilir — namlusu
            // Z'de de X'te de olsa (orn. HK416) her silah ayni acidan taninir.
            Quaternion rot = _belt.rotation * Quaternion.FromToRotation(e.BarrelDir, Vector3.right);

            // Sigma olcegi ESYA basina olculur (halka basina degil): ayni halkada iki farkli
            // silah durabildigi icin olcum artik halkaya ait olamaz.
            if (!e.FitMeasured)
            {
                e.FitMeasured = true;
                Bounds b = LocalBounds(t);
                float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float inner = (ringRadius - ringThickness) * 2f * 0.86f;   // halkanin ic capi
                e.Fit = longest > 1e-4f ? inner / longest : 1f;
                e.FitCenter = b.center;
            }

            // Ikili yuvada her esya daha kucuk cizilir ki yan yana sigsinlar.
            float scale = e.Fit * (pair ? 0.62f : 1f) * (hot ? 1.15f : 1f);
            // Mesh'in KENDI merkezi halkanin merkezine gelsin: onizlemenin kok pivotu silahin
            // orta noktasi degil (namlu dibi, sarjor vb.) — cikarilmazsa silah yuvadan kacar.
            // -Z = oyuncuya dogru. Onizleme diskin ONUNDE durmali: disk derinlik sinamali
            // cizilir, opak onizleme derinlik yazar ve diski kendi arkasinda gizler.
            Vector3 center = r.root.position + _belt.rotation * new Vector3(sideOffset, 0f, -0.022f);
            t.SetPositionAndRotation(center - rot * (e.FitCenter * scale), rot);
            t.localScale = Vector3.one * scale;
        }

        // ------------------------------------------------------------------ el / kavrama

        struct Probe
        {
            public bool hasL, hasR;
            public Vector3 l, r;
        }

        Probe HandsHoverProbe()
        {
            var p = new Probe();
            var g = LocalGrabber();
            if (g == null || !g.HasHands) return p;
            // AVUC sondasi, kumanda cipasi DEGIL: cipa bilekte duruyor ve halkaya uzanan
            // elin gorunen avucu ondan ~5 cm onde (bkz. HandGrabber.Probe). Cipayla olcmek,
            // el gorunurde halkanin icindeyken yuvayi "uzak" saydiriyordu.
            p.hasL = true; p.l = g.LeftPalm;
            p.hasR = true; p.r = g.RightPalm;
            return p;
        }

        int NearestSlot(Probe p)
        {
            int best = -1;
            float bestD = hoverRadius;
            for (int i = 0; i < _rings.Length; i++)
            {
                Vector3 c = _rings[i].root.position;
                if (p.hasL) { float d = Vector3.Distance(c, p.l); if (d < bestD) { bestD = d; best = i; } }
                if (p.hasR) { float d = Vector3.Distance(c, p.r); if (d < bestD) { bestD = d; best = i; } }
            }
            return best;
        }

        /// <summary>Bu elin merkezine EN YAKIN yuva (yaricap disindaysa -1).</summary>
        int NearestSlot(Vector3 handPos)
        {
            int best = -1;
            float bestD = hoverRadius;
            for (int i = 0; i < _rings.Length; i++)
            {
                float d = Vector3.Distance(_rings[i].root.position, handPos);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>HandGrabber kancasi: bu el, kemerdeki bir yuvayi kavradi mi? Kemer kapaliysa
        /// ya da el hicbir halkanin icinde degilse false doner ve normal yakinlik ile kapma
        /// (yerdeki esya, destek eli) hic degismeden isler.</summary>
        public static bool TryGrabFromBelt(HandGrabber grabber, byte hand, Vector3 handPos)
            => Instance != null && Instance.GrabAt(grabber, hand, handPos);

        bool GrabAt(HandGrabber grabber, byte hand, Vector3 handPos)
        {
            if (!_open || grabber == null) return false;

            int slot = NearestSlot(handPos);
            if (slot < 0) return false;

            var inv = WeaponInventory.Instance;
            if (inv == null) return false;

            // Kullanicinin kurali: "el hangisine yakinsa onu alacak halkanin icerisinden."
            var e = inv.NearestIn(slot, handPos);
            if (e == null) return false;   // bos yuva: kemer eli SAHIPLENMEZ, normal kapma sursun

            // AYNI SILAHTAN IKINCI KOPYA ARTIK SERBEST: kemerde duran her esya kendi mermisini
            // tasiyor ve cekilince yuvadan CIKIYOR, yani iki ornek ayni kayittan beslenemiyor.
            // Cift tabanca bu sayede mumkun (bkz. WeaponInventory sinif basi).

            if (e.Prefab == null)
            {
                Debug.LogWarning($"[Kemer] '{e.Key}' kusanilamaz: Resources/WeaponPrefabs altinda " +
                                 "kalibi yok (Tools > VR Multiplayer > 38 ile uretilebilir).");
                return false;
            }

            if (!grabber.EquipIntoHand(hand, e.Prefab, e.Ammo, e.Spares)) return false;

            // Esya ele gitti -> yuvadan cikar. "Havada asili duran seyi aldin" modeli:
            // kemerde kopya kalmaz, dolayisiyla bedava sarjor uretilemez.
            inv.Take(slot, e);
            return true;
        }

        /// <summary>
        /// EL SU AN BIR HALKANIN UZERINDE MI, ve elindeki oraya konabilir mi?
        /// <see cref="HandGrabber.Release"/> grip birakilirken bunu sorar: evet ise silah
        /// kemere girer, hayir ise yere duser.
        /// </summary>
        public static int PlacementSlot(Vector3 handPos, WeaponCategory cat)
        {
            var self = Instance;
            var inv = WeaponInventory.Instance;
            if (self == null || inv == null || !self._open) return -1;
            int slot = self.NearestSlot(handPos);
            if (slot < 0) return -1;
            return inv.CanPlace(cat, slot) ? slot : -1;
        }

#if ENABLE_INPUT_SYSTEM
        // Gozluksuz test yolu: yuvayi SAG ele ver. Cihazda kullanilmaz.
        void PcEquip(int slot)
        {
            var grabber = LocalGrabber();
            if (grabber == null || slot >= _rings.Length) return;
            GrabAt(grabber, 1, _rings[slot].root.position);
        }
#endif

        /// <summary>Yerel oyuncunun HandGrabber'i. Bulunamadigi surece arama YARIM SANIYEDE BIR
        /// tekrarlanir: bu bilesen her kare kosuyor ve FindObjectsByType tum sahneyi gezip
        /// sonucu heap'e cikariyor — menu ekranlarinda (henuz oyuncu yokken) kare basi tarama
        /// bosuna GC baskisi olurdu.</summary>
        HandGrabber LocalGrabber()
        {
            if (_grabber != null) return _grabber;
            if (Time.time < _nextGrabberScan) return null;
            _nextGrabberScan = Time.time + 0.5f;
            foreach (var hg in FindObjectsByType<HandGrabber>(FindObjectsSortMode.None))
                if (hg.IsOwner) { _grabber = hg; break; }
            return _grabber;
        }

        // ------------------------------------------------------------------ gorseller

        /// <summary>Halkalari BIR KEZ uretir: her yuva icin koyu disk + neon cember + ad yazisi.
        /// Sahne/prefab kurulumu YOK — her sey kodda dogar (projenin diger HUD'lariyla ayni desen).</summary>
        void EnsureRings()
        {
            if (_belt != null) return;

            _belt = new GameObject("WeaponBelt").transform;
            _belt.SetParent(transform, false);

            Mesh discMesh = UITheme.ArcMesh(0.0005f, ringRadius - ringThickness, 0f, 360f, 40);
            Mesh ringMesh = UITheme.ArcMesh(ringRadius - ringThickness, ringRadius, 0f, 360f, 40);

            for (int i = 0; i < _rings.Length; i++)
            {
                // Konum Layout'ta her kare yazilir (Play'de canli aralik ayari).
                var slot = new GameObject("Slot_" + SlotLabel[i]).transform;
                slot.SetParent(_belt, false);

                // Disk DERINLIK SINAMALI (URP/Unlit): silah onizlemesi onun ONUNDE durdugu icin
                // onizlemeyi ortmez. Halka/yazi ise sahnenin USTUNE cizer (GUI/Text Shader) —
                // holografik his ve kolokasyonlu oyunda duvarin arkasinda kaybolmama.
                var disc = MakeMesh(slot, "Disc", discMesh,
                    UITheme.CreateTransparentMaterial(discIdle), QueueDisc, 0f);

                // HALE: halkanin arkasinda, gradyan dokulu tek quad. Halkadan ONCE cizilir
                // (QueueGlow < QueueRing) ve biraz GERIDE durur, yani halkanin keskin cizgisi
                // her zaman halenin ustunde kalir.
                Material glowMat = null;
                if (glowStrength > 0f)
                {
                    float half = ringRadius * glowSpread;
                    glowMat = UITheme.CreateOverlayMaterial(GlowColor(ringIdle));
                    var tex = GlowTexture(glowSpread);
                    if (glowMat.HasProperty("_BaseMap")) glowMat.SetTexture("_BaseMap", tex);
                    if (glowMat.HasProperty("_MainTex")) glowMat.SetTexture("_MainTex", tex);
                    MakeMesh(slot, "Glow", UIMesh.RoundedRect(half * 2f, half * 2f, 0f),
                             glowMat, QueueGlow, -0.002f);
                }

                var ring = MakeMesh(slot, "Ring", ringMesh,
                    UITheme.CreateOverlayMaterial(ringIdle), QueueRing, -0.004f);

                var label = UITheme.MakeText(slot, SlotLabel[i], labelIdle, labelHeight,
                    TextAnchor.UpperCenter, QueueLabel);

                _rings[i] = new Ring
                {
                    root = slot,
                    discMat = disc.GetComponent<MeshRenderer>().sharedMaterial,
                    ringMat = ring.GetComponent<MeshRenderer>().sharedMaterial,
                    glowMat = glowMat,
                    label = label,
                    labelKey = SlotLabel[i],
                };
            }

            _belt.gameObject.SetActive(false);
        }

        /// <summary>Halenin rengi: halkanin rengi, <see cref="glowStrength"/> kadar saydam.
        /// Ayni tonu paylasmalari sart — hale ayri bir renk olsaydi durum degisiminde
        /// (bos -> dolu -> elin altinda) iki ayri sinyal cakisirdi.</summary>
        Color GlowColor(Color ring)
            => new Color(ring.r, ring.g, ring.b, ring.a * glowStrength);

        /// <summary>Rengi verilen gorunurluk carpaniyla soldurur (yalnizca alfa).</summary>
        static Color Fade(Color c, float vis)
            => vis >= 1f ? c : new Color(c.r, c.g, c.b, c.a * Mathf.Clamp01(vis));

        static Transform MakeMesh(Transform parent, string name, Mesh mesh, Material mat,
                                  int queue, float z)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            mat.renderQueue = queue;
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);   // iki yuz

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        /// <summary>"Rifle1_GripProfile" -> "RIFLE1". Yuvanin altinda okunacak ad.</summary>
        static string DisplayName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int cut = key.IndexOf("_GripProfile", System.StringComparison.OrdinalIgnoreCase);
            if (cut > 0) key = key.Substring(0, cut);
            return key.ToUpperInvariant();
        }

        /// <summary>Onizleme hiyerarsisinin KOKE GORE sinirlari. Kokun kendi transformundan
        /// bagimsizdir (worldToLocal ∘ localToWorld yalnizca aradaki zinciri birakir), yani
        /// olcek uygulanmis bir onizlemede tekrar cagrilsa da ayni sonucu verir.</summary>
        static Bounds LocalBounds(Transform root)
        {
            bool any = false;
            var result = new Bounds();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                Bounds mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 p = m.MultiplyPoint3x4(new Vector3(
                        (c & 1) == 0 ? mb.min.x : mb.max.x,
                        (c & 2) == 0 ? mb.min.y : mb.max.y,
                        (c & 4) == 0 ? mb.min.z : mb.max.z));
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                    else result.Encapsulate(p);
                }
            }
            return any ? result : new Bounds(Vector3.zero, Vector3.one);
        }
    }
}
