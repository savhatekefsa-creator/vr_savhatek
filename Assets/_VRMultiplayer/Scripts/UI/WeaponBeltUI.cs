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
    ///  - GORUNURLUK: kafa pitch'i <see cref="openPitchDegrees"/> altina inince acilir,
    ///    <see cref="closePitchDegrees"/> ustune cikinca kapanir (histerezis — esikte titremesin).
    ///    Arkasinda beden/zirh modeli YOK: bos havada duran holografik halkalar.
    ///  - YUVA: her kategorinin (uzun namlulu / tabanca / bomba) KENDI SABIT halkasi var; sira
    ///    hic degismez, boylece kas hafizasi kurulur. Bos yuva soluk kalir ve kategori adini yazar.
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

        // Yuva sirasi SABIT — index = ekrandaki soldan saga sira. Canta ileride buyurse
        // (WeaponInventory yuva sayisi) bu iki dizi birlikte buyutulur.
        static readonly WeaponCategory[] Slots =
            { WeaponCategory.Heavy, WeaponCategory.Pistol, WeaponCategory.Grenade };
        static readonly string[] SlotLabel = { "HEAVY", "PISTOL", "GRENADE" };

        // ESIK ACISI ILE beltOffset BIRBIRINE BAGLI — birini degistiren otekini de kontrol
        // etmeli. Kemerin OTURDUGU aci offset'ten cikar: atan(|y| / z).
        //
        //   varsayilan (-0.37, 0.20) -> atan(0.37/0.20) ≈ 62 derece, kafadan 0.42 m
        //
        // Esik 52: kemer acildigi anda ust kenari neredeyse bakis hizasinda belirir, birkac
        // derece daha egilince tam ortaya oturur. Aradaki fark BILEREK kucuk tutuluyor —
        // ilk surumdeki 30 derecelik esikte kemer 54 derecede duruyordu: hem kaza ile
        // aciliyor hem de acildiginda gorusun cok altinda kaliyordu.
        //
        // GEOMETRI INATCIDIR: "govdeye yakin" (kucuk z) + "bel hizasi" (buyuk |y|) zorunlu
        // olarak DIK bir aci verir. Esigi indirmek TEK BASINA ise yaramaz — |y| de kismen
        // azaltilmali, yoksa menu acilir ama gorusun altinda kalir. 58 -> 52 gecisinde
        // y de -0.44'ten -0.37'ye alindi, tam da bu yuzden.
        [Header("Acilma — kafa egme")]
        [Tooltip("Kafa bu aciDAN fazla asagi egilince kemer acilir (derece, 0 = duz ileri). " +
                 "52 = belirgin bir 'asagi bak' hareketi ama boyun zorlamaz. Degistirirsen " +
                 "beltOffset'in acisini (atan(|y|/z)) da yakin tut, yoksa menu goruse girmez.")]
        public float openPitchDegrees = 52f;
        [Tooltip("Kafa bu acinin ustune cikinca kapanir. openPitch'ten KUCUK olmali (histerezis). " +
                 "14 derecelik bant: uzanip kavrarken kafa biraz oynasa da kemer kacmaz.")]
        public float closePitchDegrees = 38f;

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
                 "Varsayilan gogus-bel arasi, GOVDEYE YAKIN: elin dogal olarak durdugu yer. " +
                 "z'yi kucultmek kemeri govdeye yapistirir, buyutmek havaya iter.")]
        public Vector3 beltOffset = new Vector3(0f, -0.37f, 0.20f);
        [Tooltip("Halka merkezleri arasi mesafe (metre).")]
        public float slotSpacing = 0.146f;
        [Tooltip("Halkanin dis yaricapi (metre).")]
        public float ringRadius = 0.058f;
        [Tooltip("Halka cizgi kalinligi (metre).")]
        public float ringThickness = 0.005f;
        [Tooltip("Silah adinin halka merkezinin ne kadar altinda duracagi (metre).")]
        public float labelDrop = 0.084f;
        [Tooltip("Yazi satir yuksekligi (metre) — VR'da okunabilirligi punto degil aci belirler.")]
        public float labelHeight = 0.017f;

        [Header("Kavrama")]
        [Tooltip("El halkanin merkezine bu kadar yaklasinca yuva 'elin altinda' sayilir (metre). " +
                 "Halka yaricapinin ~1.6 kati: isabetsizlige pay birakir ama komsu yuvayi " +
                 "calmaz. Kemer yakinlastiginda bu da ayni oranda kuculdu.")]
        public float hoverRadius = 0.094f;

        [Header("Kafa takibi")]
        [Tooltip("Kemer kafa YONUNU takip eder ama pitch'i takip ETMEZ — bakisin degil bedenin " +
                 "onunde durur. Bu aciDAN kucuk kafa cevirmelerinde hic kimildamaz (derece), " +
                 "yoksa uzanirken hedef elin altindan kacar.")]
        public float yawDeadzoneDegrees = 30f;
        [Tooltip("Olu bolge asilinca kemerin yeni yone donme hizi (derece/sn).")]
        public float yawFollowSpeed = 150f;

        [Header("Renkler")]
        [Tooltip("Varsayilan halka: MAVI.")]
        public Color ringIdle = new Color(0.25f, 0.74f, 1f, 0.95f);
        [Tooltip("Elin uzandigi halka: parlayan TURUNCU-SARI neon.")]
        public Color ringHot = new Color(1f, 0.70f, 0.16f, 1f);
        [Tooltip("Bos yuva (ya da silahi su an ELDE olan yuva): ayni mavi, soluk.")]
        public Color ringDim = new Color(0.25f, 0.74f, 1f, 0.30f);
        public Color discIdle = new Color(0.02f, 0.07f, 0.11f, 0.55f);
        public Color discHot = new Color(0.16f, 0.10f, 0.01f, 0.65f);
        public Color labelIdle = new Color(0.90f, 0.96f, 1f, 1f);
        public Color labelHot = new Color(1f, 0.82f, 0.45f, 1f);
        public Color labelDim = new Color(0.62f, 0.78f, 0.90f, 0.45f);

        // Cizim sirasi: disk EN ARKADA ve DERINLIK SINAMALI (silah onizlemesi onun ONUNDE
        // durdugu icin onizlemeyi ORTMEZ), halka ve yazi ise her zaman ustte.
        const int QueueDisc = 3000;
        const int QueueRing = 3001;
        const int QueueLabel = 3002;

        class Ring
        {
            public Transform root;
            public Material ringMat;
            public Material discMat;
            public TextMesh label;
            public string labelKey;    // label.text bu anahtardan uretildi (bosuna string uretme)
            public string fitKey;      // previewFit / previewCenter bu silah icin olculdu
            public float previewFit = 1f;
            public Vector3 previewCenter;
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

        // Elde tutulan silahlarin tur anahtari. TypeKey icerde Object.name okur ve HER cagride
        // yeni bir string uretir; kare basina iki alloc olmasin diye OBJE REFERANSINA gore
        // onbelleklenir (silah degismedikce yeniden hesaplanmaz).
        GrabbableObject _keyObjL, _keyObjR;
        string _keyL, _keyR;

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
            // Gozluksuz test (Editor): TAB kemeri acik tutar, 1/2/3 yuvayi SAG ele verir.
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.wasPressedThisFrame) _pcOpen = !_pcOpen;
#endif

            // Asagi bakis acisi: forward.y negatiflestikce buyur. Kafa pitch'i, bakisin
            // KENDISI — kemer bu esikle acilir ama konumu pitch'i TAKIP ETMEZ (bkz. Layout).
            float pitchDown = -Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            bool want = _open
                ? pitchDown > closePitchDegrees
                : pitchDown > openPitchDegrees;
            SetOpen(want || _pcOpen);

            if (!_open) return;

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
            foreach (var e in inv.Entries)
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

            float targetYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            if (!_yawValid) { _yaw = targetYaw; _yawValid = true; }
            else if (Mathf.Abs(Mathf.DeltaAngle(_yaw, targetYaw)) > yawDeadzoneDegrees)
                _yaw = Mathf.MoveTowardsAngle(_yaw, targetYaw, yawFollowSpeed * Time.deltaTime);

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
            string keyL = HeldKeyCached(grabber != null ? grabber.HeldLeft : null, ref _keyObjL, ref _keyL);
            string keyR = HeldKeyCached(grabber != null ? grabber.HeldRight : null, ref _keyObjR, ref _keyR);

            for (int i = 0; i < _rings.Length; i++)
            {
                var r = _rings[i];
                var e = inv.Slot(Slots[i]);
                bool hot = _hovered == i;

                // Silah SU AN elde ise yuvasi bos gorunur — "nerede?" sorusunu dogru yanitlar
                // (canta kaydi duruyor ama silah cantada degil).
                bool inHand = e != null && (e.Key == keyL || e.Key == keyR);
                bool filled = e != null && !inHand;

                UITheme.SetMaterialColor(r.ringMat, hot ? ringHot : (filled ? ringIdle : ringDim));
                UITheme.SetMaterialColor(r.discMat, hot ? discHot : discIdle);

                string key = filled ? e.Key : SlotLabel[i];
                if (r.labelKey != key)
                {
                    r.labelKey = key;
                    r.label.text = filled ? DisplayName(e.Key) : SlotLabel[i];
                }
                r.label.color = hot ? labelHot : (filled ? labelIdle : labelDim);

                PlacePreview(r, e, filled, hot);
            }
        }

        /// <summary>Silahin GORSEL kopyasini halkanin ortasina, yan gorunumde ve halkaya
        /// SIGACAK olcekte yerlestirir. Olcek/merkez silah basina BIR KEZ olculur (mesh
        /// bounds'u degismez), sonraki karelerde sadece transform yazilir.</summary>
        void PlacePreview(Ring r, WeaponInventory.Entry e, bool filled, bool hot)
        {
            if (e == null || e.Preview == null) return;
            if (!filled) { e.Preview.SetActive(false); return; }

            var t = e.Preview.transform;
            if (!e.Preview.activeSelf) e.Preview.SetActive(true);

            // YAN GORUNUM: namlu ekseni (profilden) halkanin SAG eksenine cevrilir — namlusu
            // Z'de de X'te de olsa (orn. HK416) her silah ayni acidan taninir.
            Quaternion rot = _belt.rotation * Quaternion.FromToRotation(e.BarrelDir, Vector3.right);

            if (r.fitKey != e.Key)
            {
                r.fitKey = e.Key;
                Bounds b = LocalBounds(t);
                float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float inner = (ringRadius - ringThickness) * 2f * 0.86f;   // halkanin ic capi
                r.previewFit = longest > 1e-4f ? inner / longest : 1f;
                r.previewCenter = b.center;
            }

            float scale = r.previewFit * (hot ? 1.15f : 1f);
            // Mesh'in KENDI merkezi halkanin merkezine gelsin: onizlemenin kok pivotu silahin
            // orta noktasi degil (namlu dibi, sarjor vb.) — cikarilmazsa silah yuvadan kacar.
            // -Z = oyuncuya dogru. Onizleme diskin ONUNDE durmali: disk derinlik sinamali
            // cizilir, opak onizleme derinlik yazar ve diski kendi arkasinda gizler.
            Vector3 center = r.root.position + _belt.rotation * new Vector3(0f, 0f, -0.022f);
            t.SetPositionAndRotation(center - rot * (r.previewCenter * scale), rot);
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
            if (g == null) return p;
            if (g.LeftAnchor != null) { p.hasL = true; p.l = g.LeftAnchor.position; }
            if (g.RightAnchor != null) { p.hasR = true; p.r = g.RightAnchor.position; }
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
            var e = inv != null ? inv.Slot(Slots[slot]) : null;
            if (e == null) return false;   // bos yuva: kemer eli SAHIPLENMEZ, normal kapma sursun

            // AYNI SILAHTAN IKINCI KOPYA YOK. Canta tur basina TEK mermi sayisi tutuyor; iki
            // ornek ayni kayittan beslenir ve kemer bedava sarjor pinarina donerdi.
            if (e.Key == HeldKeyCached(grabber.HeldLeft, ref _keyObjL, ref _keyL) ||
                e.Key == HeldKeyCached(grabber.HeldRight, ref _keyObjR, ref _keyR)) return false;

            if (e.Prefab == null)
            {
                Debug.LogWarning($"[Kemer] '{e.Key}' kusanilamaz: Resources/WeaponPrefabs altinda " +
                                 "kalibi yok (Tools > VR Multiplayer > 38 ile uretilebilir).");
                return false;
            }

            Debug.Log($"[Kemer] {Slots[slot]} yuvasi -> {(hand == 1 ? "SAG" : "SOL")} el: {e.Key} " +
                      $"({(e.Ammo < 0 ? "dolu" : e.Ammo + " mermi")})");
            return grabber.EquipIntoHand(hand, e.Prefab, e.Ammo, e.Spares);
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
                var ring = MakeMesh(slot, "Ring", ringMesh,
                    UITheme.CreateOverlayMaterial(ringIdle), QueueRing, -0.004f);

                var label = UITheme.MakeText(slot, SlotLabel[i], labelIdle, labelHeight,
                    TextAnchor.UpperCenter, QueueLabel);

                _rings[i] = new Ring
                {
                    root = slot,
                    discMat = disc.GetComponent<MeshRenderer>().sharedMaterial,
                    ringMat = ring.GetComponent<MeshRenderer>().sharedMaterial,
                    label = label,
                    labelKey = SlotLabel[i],
                };
            }

            _belt.gameObject.SetActive(false);
        }

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
