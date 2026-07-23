using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
// Not: UnityEngine.XR'i "using" yapMIYORUZ — InputSystem ile ayni isimde (InputDevice)
// tipleri var, cakisir. XR tiplerini tam adiyla (UnityEngine.XR.*) yaziyoruz.

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Silah secici CARK (Pavlov tarzi radyal menu): 3 dilim = cantanin 3 yuvasi
    /// (HEAVY ustte, PISTOLS sol-altta, GRENADES sag-altta). Her dilimde o yuvadaki silahin
    /// 3B onizlemesi durur; bos yuvanin dilimi soluk ve secilemez. Cark TAM KARSIDA acilir
    /// (viewOffset) ve kafayla birlikte doner.
    ///
    /// GIRIS — it, goster, BIRAK (ekip tasarimi; onay tusu yok):
    ///  - GRIP BASILI TUTULUR. Cark yalnizca grip basiliyken yasar; birakirsan kapanir ve
    ///    silah cantaya gider (HandGrabber'in normal isi). Grip ORTA parmak, stick BASPARMAK.
    ///  - THUMBSTICK'E BAS (tik) -> cark acilir.
    ///  - Stick'i dilime dogru TUT -> dilim vurgulanir (camgobegi + onizleme buyur).
    ///  - Stick'i BIRAK (merkeze donsun) -> vurguladigin silah ELE GELIR, cark kapanir.
    ///    Hic gostermeden birakirsan degisiklik olmaz — elindeki kalir; CLOSE butonu bu
    ///    yuzden yok, "vazgec" dogal olarak var (cark kimseyi silahsiz birakamaz).
    ///  - PC (gozluksuz test): TAB = ac, oklar = yon (yukari/sol/sag), Enter = sec-kapat.
    ///
    /// Dilim vurgusu stick YONUNDEN gelir (aci -> en yakin dilim) — kaydirma/adim yok,
    /// radyal menunun dogal hissi. Sag stick tamamen bos (snap turn kapali, yurume fiziksel).
    ///
    /// NOT: thumbstick tiki dev build'lerde Melih'in yakalama aracini da tetikleyebilir
    /// (WeaponGripCaptureTool ayni tusu dinler) — dosyaya zararsiz bos kayit dusebilir,
    /// release build'de o arac hic yok.
    ///
    /// ONAY -> EQUIP: elindeki silah yok olur (despawn = "cantaya girdi"), secilen turden TAZE
    /// bir tane sunucuda uretilip eline verilir (HandGrabber.RequestWeaponSwap) — cantaya kac
    /// mermiyle girdiyse o kadarla (bkz. WeaponInventory.Entry.Ammo).
    /// </summary>
    public class WeaponSelectorUI : MonoBehaviour
    {
        [Header("Cark yerlesimi (Play'de canli ayarlanir)")]
        [Tooltip("Carkin KAFAYA gore konumu (metre): x=saga, y=asagi/yukari, z=uzaklik. " +
                 "Ekip karari: TAM KARSIDA (0,0) — cark aciliyken zaten silah secmekle mesgulsun, " +
                 "gorusu kapatmasi sorun degil. Kafayla birlikte doner.")]
        public Vector3 viewOffset = new Vector3(0f, 0f, 0.95f);
        public float discRadius = 0.28f;
        public float centerRadius = 0.075f;
        [Tooltip("Dilimler arasi bosluk (derece) — Pavlov'daki gibi ayrik dursunlar.")]
        public float sliceGapDegrees = 5f;
        [Tooltip("Onizleme modellerinin merkezden uzakligi.")]
        public float previewRadius = 0.165f;
        public float previewScale = 0.2f;
        public float selectedBoost = 1.35f;
        [Tooltip("Kategori yazilarinin merkezden uzakligi (yazi sadece BOS dilimde gorunur).")]
        public float labelRadius = 0.235f;
        [Tooltip("Yazi boyutu — dilimlerin icine sigacak kadar kucuk olmali.")]
        public float labelSize = 0.008f;
        [Tooltip("Otomatik yan cevirmenin USTUNE ek ince ayar (genelde sifir kalir). Yan cevirme " +
                 "profildeki namlu yonunden hesaplanir — HK416 gibi namlusu X'te olan silahlar da " +
                 "boylece digerleri gibi YANDAN gorunur.")]
        public Vector3 previewExtraEuler = Vector3.zero;

        [Header("Giris")]
        [Tooltip("Stick bu kadar itilmisse bir dilimi 'gosteriyor' sayilir; alti = gosterim yok (birakinca degisiklik olmaz).")]
        [Range(0.1f, 0.9f)] public float pointDeadzone = 0.4f;

        [Header("Renkler (Pavlov paleti)")]
        public Color sliceColor = new Color(0.06f, 0.06f, 0.08f, 0.80f);
        public Color sliceSelectedColor = new Color(0.10f, 0.47f, 0.53f, 0.90f); // camgobegi vurgu
        public Color sliceEmptyColor = new Color(0.06f, 0.06f, 0.08f, 0.42f);
        public Color centerColor = new Color(0.03f, 0.03f, 0.04f, 0.88f);
        public Color labelEmptyColor = new Color(1f, 1f, 1f, 0.30f);

        // Dilim merkez acilari (derece; 0 = sag, saat yonu tersi). Index = WeaponCategory.
        static readonly float[] SliceAngle = { 90f, 210f, 330f };   // Heavy, Pistol, Grenade
        static readonly string[] SliceLabel = { "HEAVY", "PISTOLS", "GRENADES" };

        int _selected = -1;          // -1 = gosterim yok, 0..2 = dilim
        bool _open;
        bool _pointed;               // bu acilista stick en az bir kez dilime dogru itildi mi
        bool _clickPrev;
        UnityEngine.XR.InputDevice _rightHand;

        public bool IsOpen => _open;

        // Cark gorselleri (calisma aninda bir kez uretilir)
        Transform _wheel;
        Material[] _sliceMats;
        TextMesh[] _labels;
        Material _centerMat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponSelectorUI");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponSelectorUI>();
        }

        void Update()
        {
            // Canta BOS olsa da cark acilir: uc dilim soluk etiketleriyle gorunur (HEAVY/
            // PISTOLS/GRENADES) — oyuncu daha silah toplamadan duzeni ogrenir. Bos dilime
            // tiklamak bir sey secmez (Confirm null yuvada calismaz), sadece kapanir.
            var inv = WeaponInventory.Instance;
            if (inv == null) { SetOpen(false); return; }

            // Cark sadece grip basiliyken yasar (birakinca HandGrabber silahi cantaya yollar).
            if (!GripHeld()) { SetOpen(false); return; }

            bool click = StickClickDown();
            Vector2 stick = RightStick();

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.tabKey.wasPressedThisFrame) click = true;      // TAB = tik
                if (_open)
                {
                    if (kb.upArrowKey.wasPressedThisFrame)    { _selected = 0; _pointed = true; }
                    if (kb.leftArrowKey.wasPressedThisFrame)  { _selected = 1; _pointed = true; }
                    if (kb.rightArrowKey.wasPressedThisFrame) { _selected = 2; _pointed = true; }
                    if (kb.enterKey.wasPressedThisFrame) click = true; // Enter = sec-kapat
                }
            }
#endif

            if (!_open)
            {
                if (click) SetOpen(true);
                return;
            }

            // Stick yonu dilimi GOSTERIR (aci -> en yakin dilim) ve isaretler. Onay tusu YOK
            // (ekip karari): stick MERKEZE DONUNCE gosterilen silah ele gelir — it, goster,
            // birak. Hic gostermeden birakirsan degisiklik olmaz, elindeki kalir (cark kimseyi
            // silahsiz birakmaz; CLOSE butonu bu yuzden kaldirildi, "vazgec" hala mumkun).
            // ONEMLI: vurgu aninda DEGIL birakinca equip — yoksa stick gezerken ustunden
            // gecilen her dilim aninda ele gelirdi (spam takas).
            float mag = stick.magnitude;
            if (mag >= pointDeadzone)
            {
                _selected = NearestSlice(stick);
                _pointed = true;
            }

            Layout();

            bool commit = click; // tik hala calisir (aliskanlik/yedek), ama sart degil
            if (_pointed && mag < pointDeadzone * 0.6f) commit = true; // birakti -> onay (histerezis)

            if (commit)
            {
                if (_pointed && _selected >= 0)
                {
                    var e = inv.Slot((WeaponCategory)_selected);
                    if (e != null) Confirm(e);
                }
                SetOpen(false);
            }
        }

        static int NearestSlice(Vector2 stick)
        {
            float a = Mathf.Atan2(stick.y, stick.x) * Mathf.Rad2Deg;
            int best = 0; float bestD = 999f;
            for (int i = 0; i < 3; i++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(a, SliceAngle[i]));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            if (open) { EnsureWheel(); _selected = -1; _pointed = false; }
            if (_wheel != null) _wheel.gameObject.SetActive(open);

            // Onizlemeler envanterin mali — cark kapaninca hepsini sakla.
            var inv = WeaponInventory.Instance;
            if (inv != null)
                foreach (var e in inv.Entries)
                    if (e.Preview != null) e.Preview.SetActive(open && _wheel != null);
        }

        // SetOpen uzerinden kapat: _open'i dogrudan sondurmek onizleme klonlarini atlar ve
        // cark acikken bilesen kapanirsa 3D silah onizlemeleri sahnede asili kalirdi.
        void OnDisable() { SetOpen(false); }

        // ---------------------------------------------------------------- gorseller

        /// <summary>Carki kafaya CIVILI tutar (viewOffset: sag-alt kose) ve dilim/onizleme
        /// durumlarini isler. Olum ekraninin (PlayerHUD._deathFade) tarifi: her kare kafayi
        /// takip et — kafani cevirsen de seninle doner, goruşun ortasini kapatmaz.</summary>
        void Layout()
        {
            var cam = Camera.main;
            var inv = WeaponInventory.Instance;
            if (cam == null || inv == null || _wheel == null) return;

            Transform head = cam.transform;
            _wheel.SetPositionAndRotation(head.position + head.rotation * viewOffset, head.rotation);

            for (int i = 0; i < 3; i++)
            {
                var e = inv.Slot((WeaponCategory)i);
                bool filled = e != null;
                bool sel = filled && _selected == i;

                if (_sliceMats[i] != null)
                    UITheme.SetMaterialColor(_sliceMats[i],
                        sel ? sliceSelectedColor : (filled ? sliceColor : sliceEmptyColor));

                // Yazi sadece BOS dilimde: silah geldiyse onizleme konusur, yazi sussun.
                if (_labels[i] != null)
                {
                    _labels[i].gameObject.SetActive(!filled);
                    _labels[i].color = labelEmptyColor;
                }

                if (e == null || e.Preview == null) continue;
                float rad = SliceAngle[i] * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
                // Onizleme dilimin icinde, diskten hafif ONDE (kameraya dogru) durur.
                // YAN gorunum OTOMATIK: silahin namlu ekseni (profilden) carkin SAG eksenine
                // cevrilir — namlusu Z'de de X'te de olsa her silah yandan taninir.
                e.Preview.transform.position = _wheel.TransformPoint(dir * previewRadius + Vector3.back * 0.05f);
                e.Preview.transform.rotation = _wheel.rotation * Quaternion.Euler(previewExtraEuler) *
                                               Quaternion.FromToRotation(e.BarrelDir, Vector3.right);
                e.Preview.transform.localScale = Vector3.one * previewScale * (sel ? selectedBoost : 1f);
            }

        }

        /// <summary>Cark gorsellerini bir kez uretir: 3 dilim (prosedurel yay mesh'i), merkez
        /// dairesi, kategori yazilari. Sahne/prefab kurulumu YOK — her sey kodda dogar.</summary>
        void EnsureWheel()
        {
            if (_wheel != null) return;

            _wheel = new GameObject("SelectorWheel").transform;
            _wheel.SetParent(transform, false);
            _sliceMats = new Material[3];
            _labels = new TextMesh[3];

            float half = (120f - sliceGapDegrees) * 0.5f;
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("Slice_" + SliceLabel[i]);
                go.transform.SetParent(_wheel, false);
                go.AddComponent<MeshFilter>().sharedMesh =
                    ArcMesh(centerRadius + 0.012f, discRadius, SliceAngle[i] - half, SliceAngle[i] + half, 24);
                var mr = go.AddComponent<MeshRenderer>();
                _sliceMats[i] = MakeOverlayMaterial(3000);
                mr.sharedMaterial = _sliceMats[i];

                _labels[i] = MakeLabel(_wheel, SliceLabel[i],
                    AngleToPos(SliceAngle[i], labelRadius), labelSize, 3002);
            }

            // Merkez: sade koyu gobek (estetik). CLOSE butonu YOK — ekip karari: hicbir dilimi
            // gostermeden birakmak zaten "vazgec" demek, elindeki silah aynen kalir.
            var center = new GameObject("Center");
            center.transform.SetParent(_wheel, false);
            center.AddComponent<MeshFilter>().sharedMesh = ArcMesh(0.001f, centerRadius, 0f, 360f, 48);
            var cmr = center.AddComponent<MeshRenderer>();
            _centerMat = MakeOverlayMaterial(3001);
            UITheme.SetMaterialColor(_centerMat, centerColor);
            cmr.sharedMaterial = _centerMat;

            _wheel.gameObject.SetActive(false);
        }

        static Vector3 AngleToPos(float deg, float r)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad) * r, Mathf.Sin(rad) * r, -0.01f);
        }

        /// <summary>Ic/dis yaricapli yay (pasta dilimi) mesh'i, XY duzleminde.</summary>
        static Mesh ArcMesh(float r0, float r1, float a0, float a1, int seg)
        {
            var m = new Mesh();
            var v = new Vector3[(seg + 1) * 2];
            var t = new int[seg * 12]; // iki yuz: onden ve arkadan gorunsun (cull yine kapali)
            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, (float)i / seg);
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v[i * 2] = dir * r0;
                v[i * 2 + 1] = dir * r1;
            }
            for (int i = 0; i < seg; i++)
            {
                int b = i * 2, k = i * 12;
                t[k] = b; t[k + 1] = b + 1; t[k + 2] = b + 2;
                t[k + 3] = b + 1; t[k + 4] = b + 3; t[k + 5] = b + 2;
                t[k + 6] = b + 2; t[k + 7] = b + 1; t[k + 8] = b;
                t[k + 9] = b + 2; t[k + 10] = b + 3; t[k + 11] = b + 1;
            }
            m.vertices = v; m.triangles = t;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Yari saydam, isiktan etkilenmeyen overlay materyali. UITheme.CreateLitMaterial
        /// saydamlik KURMUYOR (olum ekrani opak kaliyor) — blend state'leri burada elle aciliyor.</summary>
        static Material MakeOverlayMaterial(int queue)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = queue;
            return m;
        }

        static TextMesh MakeLabel(Transform parent, string text, Vector3 localPos, float charSize, int queue)
        {
            var go = new GameObject("Label_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = charSize;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            // Unity 6: TextMesh varsayilan fontsuz gelir — atanmazsa yazi HIC gorunmez
            // (kol saatinde ogrenilen ders).
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var mr = go.GetComponent<MeshRenderer>();
            if (tm.font != null) mr.material = tm.font.material;
            mr.material.renderQueue = queue; // disk uzerinde kalsin
            return tm;
        }

        // ---------------------------------------------------------------- giris okuma

        /// <summary>SAG grip basili mi? VR yoksa (PC testi) true doner. HandGrabber ile ayni
        /// okuma: bazi OpenXR profilleri butonu vermez, sadece analog degeri verir.</summary>
        bool GripHeld()
        {
            if (!_rightHand.isValid)
                _rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (!_rightHand.isValid) return true; // VR bagli degil -> sart aranmaz
            if (_rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool b) && b) return true;
            return _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float g) && g > 0.5f;
        }

        /// <summary>SAG thumbstick tiki — kenar (bu kare basildi). Ac ve sec ayni tus.</summary>
        bool StickClickDown()
        {
            bool now = _rightHand.isValid &&
                       _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool c) && c;
            bool edge = now && !_clickPrev;
            _clickPrev = now;
            return edge;
        }

        Vector2 RightStick()
        {
            if (!_rightHand.isValid)
                _rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (_rightHand.isValid &&
                _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                return axis;
            return Vector2.zero;
        }

        // ---------------------------------------------------------------- equip

        HandGrabber _grabber;

        HandGrabber LocalGrabber()
        {
            if (_grabber != null) return _grabber;
            foreach (var hg in FindObjectsByType<HandGrabber>(FindObjectsSortMode.None))
                if (hg.IsOwner) { _grabber = hg; break; }
            return _grabber;
        }

        // Su an tuttugum silah, SUNUCUNUN gercegine gore. HandGrabber'in ic durumuna bilerek
        // bakmiyoruz: PC test araci silahi HandGrabber KAPALIYKEN tasiyor, tek ortak dogru
        // "holder benim mi" sorusu.
        static GrabbableObject MyWeapon()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !(nm.IsServer || nm.IsConnectedClient)) return null;
            var actives = GrabbableObject.Active; // spawn kayit listesi — sahne taramasi + dizi alloc'u yok
            for (int i = 0; i < actives.Count; i++)
                if (actives[i].HolderClientId == nm.LocalClientId) return actives[i];
            return null;
        }

        void Confirm(WeaponInventory.Entry e)
        {
            var grabber = LocalGrabber();
            if (grabber == null || e == null) return;

            // Elindeki silahi tekrar secmek bos islem — ayni silah geri gelirdi. Ayrica mermi
            // sisteminde bu bir ACIK olurdu (cark acip kapamak = bedava sarjor, kol
            // hareketiyle dolumdan hizli), o yuzden kapali.
            var cur = MyWeapon();
            if (cur != null && WeaponInventory.TypeKey(cur) == e.Key) return;

            if (e.Prefab == null)
            {
                Debug.LogWarning($"[WeaponSelector] '{e.Key}' equip edilemez: Resources/WeaponPrefabs " +
                                 "altinda kalibi yok (Tools > VR Multiplayer > 38 ile uretilebilir).");
                return;
            }

            // Elimdekinin mermisini TAM SU AN kaydet. Envanterin 0.3 sn'lik taramasi bayat
            // olabilir; son yarim saniyede atilan mermiler bedavaya geri gelmesin.
            var inv = WeaponInventory.Instance;
            if (cur != null && inv != null)
            {
                var curNw = cur.GetComponent<NetworkWeapon>();
                var curEntry = inv.Find(WeaponInventory.TypeKey(cur));
                if (curNw != null && curNw.UsesAmmo && curEntry != null)
                {
                    curEntry.Ammo = curNw.Ammo;
                    curEntry.Spares = curNw.SpareMagazines;
                }
            }

            Debug.Log($"[WeaponSelector] Equip: {e.Key} — {(e.Ammo < 0 ? "dolu" : e.Ammo + " mermi")}" +
                      $"  (eski: {(cur != null ? cur.name : "yok")})");
            grabber.RequestWeaponSwap(cur, e.Prefab, e.Ammo, e.Spares);
        }
    }
}
