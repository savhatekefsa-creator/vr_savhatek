using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Dunya-uzayi panellerini surmek icin LAZER IMLEC: sag kumandadan cikan ince bir isin,
    /// ucunda bir nokta, tetikle tiklama.
    ///
    /// CARPISMA ANALITIK — collider yok, <see cref="Physics"/> sorgusu yok. Panelin duzlemiyle
    /// isin kesistirilir, sonuc panelin YEREL 2B koordinatina cevrilir, tus izgarasi orada
    /// aranir. Sebebi sadece performans degil: bu oyunda sahne mermi raycast'leri, kavrama
    /// overlap'leri ve bomba linecast'leriyle dolu — UI'a collider koymak o sorgularin hepsine
    /// yeni bir yanlis-pozitif kaynagi eklerdi.
    ///
    /// PANELLE SOZLESME: panelin +Z ekseni oyuncudan UZAGA bakar (projedeki tum paneller boyle;
    /// bkz. <see cref="HeadFollowPanel"/>), yerel +X saga, +Y yukari. Panel kokunun olcegi 1
    /// olmali — yerel koordinatlar dogrudan METRE okunuyor.
    ///
    /// Yalnizca UI acikken yasar: <see cref="NameEntryUI"/> olusturur ve kapaninca yok eder.
    /// Ileride ayarlar menusu / skor tablosu ayni imleci kullanabilir.
    /// </summary>
    public class VRPointer : MonoBehaviour
    {
        [Tooltip("Isinin ulasabilecegi en uzak mesafe (metre).")]
        public float maxDistance = 3f;

        [Header("Nisan yonu")]
        [Tooltip("KAVRAMA pozuna uygulanan duzeltme (derece, + = asagi). Yalnizca cihaz NISAN " +
                 "pozu vermediginde kullanilir. Touch kumandasinin govde ekseni dogal isaret " +
                 "yonunden ~40 derece YUKARI bakar.")]
        public float gripFallbackPitch = 40f;
        [Tooltip("Her iki yola da uygulanan ince ayar (derece, + = asagi). Isin hala yukari " +
                 "bakiyorsa bunu artir, asagi bakiyorsa eksiye al. Play modda canli ayarlanir.")]
        public float aimTrim = 0f;
        [Tooltip("Isin kalinligi (metre). VR'da 4 mm ince ama net gorunur.")]
        public float width = 0.004f;
        [Tooltip("Uc noktasinin capi (metre).")]
        public float dotSize = 0.012f;

        public Color rayColor = new Color(0.35f, 0.85f, 1f, 0.75f);
        public Color dotColor = new Color(0.6f, 0.95f, 1f, 0.95f);

        // Tetik histerezisi: tek esikte tutulan parmak titresirse tus tekrar tekrar basilir.
        const float PressThreshold = 0.6f;
        const float ReleaseThreshold = 0.4f;

        LineRenderer _line;
        Transform _dot;
        bool _pressed;      // histerezisli "tetik basili" durumu
        bool _prevPressed;  // gecen kareki hali — yukselen kenar icin

        /// <summary>Bu karede tetige BASILDI mi (yukselen kenar)?</summary>
        public bool ClickDown { get; private set; }

        /// <summary>Tetik su an basili mi?</summary>
        public bool ClickHeld => _pressed;

        /// <summary>Kumanda takipte mi? Degilse isin gizlenir ve hicbir tus tetiklenemez.</summary>
        public bool HasController => Hand != null;

        /// <summary>Sag el transformu: <see cref="XRDevicePoseDriver"/> tarafindan cihazin
        /// KAVRAMA pozuyla suruluyor.</summary>
        static Transform Hand
        {
            get
            {
                var rig = XRRigReference.Instance;
                return rig != null ? rig.rightHand : null;
            }
        }

        // OpenXR her kumanda icin IKI ayri poz yayinlar:
        //   grip  (devicePosition/deviceRotation) — elin kavradigi nokta, ekseni GOVDE boyunca
        //   aim   (asagidaki ozel kullanimlar)    — kumandanin NISAN aldigi yon
        // Projedeki el transformlari grip pozunu tasiyor (silah tutmak icin dogrusu bu). Ama
        // grip ekseni dogal isaret yonunden ~40 derece YUKARI bakar; menude o eksenle nisan
        // almak kolu asagi bukmeyi zorunlu kilar. Menu icin aim pozu okunur.
        //
        // Bu kullanimlar Unity'nin CommonUsages listesinde YOK ama OpenXR/Oculus surucusu
        // isimle yayinlar. Vermeyen bir cihazda TryGetFeatureValue false doner ve
        // gripFallbackPitch devreye girer — yani kotu durumda bile isin dogru yere bakar.
        static readonly InputFeatureUsage<Vector3> PointerPosUsage =
            new InputFeatureUsage<Vector3>("PointerPosition");
        static readonly InputFeatureUsage<Quaternion> PointerRotUsage =
            new InputFeatureUsage<Quaternion>("PointerRotation");

        bool _logged;

        /// <summary>Isinin baslangici ve yonu. Once NISAN pozu denenir, yoksa kavrama pozuna
        /// egim duzeltmesi uygulanir.</summary>
        bool TryGetAim(out Vector3 origin, out Vector3 dir)
        {
            origin = Vector3.zero;
            dir = Vector3.forward;

            var hand = Hand;
            if (hand == null) return false;

            var dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            Quaternion aimRot;
            float pitch = aimTrim;

            // Aim pozu CIHAZ uzayinda gelir — el transformu rig kokunun dogrudan cocugu
            // oldugundan (bkz. XRDevicePoseDriver) ayni ebeveynle dunyaya cevrilir.
            Transform parent = hand.parent;
            if (parent != null && dev.isValid &&
                dev.TryGetFeatureValue(PointerPosUsage, out Vector3 lp) &&
                dev.TryGetFeatureValue(PointerRotUsage, out Quaternion lr))
            {
                origin = parent.TransformPoint(lp);
                aimRot = parent.rotation * lr;
                Log("nisan pozu");
            }
            else
            {
                origin = hand.position;
                aimRot = hand.rotation;
                pitch += gripFallbackPitch;
                Log("kavrama pozu + " + gripFallbackPitch + " derece duzeltme");
            }

            dir = aimRot * Quaternion.Euler(pitch, 0f, 0f) * Vector3.forward;
            return true;
        }

        void Log(string mode)
        {
            if (_logged) return;
            _logged = true;
            Debug.Log("[VRPointer] Nisan kaynagi: " + mode);
        }

        void Awake()
        {
            var go = new GameObject("Ray");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            _line.widthMultiplier = width;
            _line.numCapVertices = 0;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            // Isin da sahnenin USTUNE cizilir: aksi halde el ile panel arasina giren herhangi
            // bir geometri (masa kenari, silah, oda mesh'i) lazeri ortadan boler.
            _line.sharedMaterial = UITheme.CreateOverlayMaterial(rayColor);

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Dot";
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);   // UI hicbir fizik sorgusuna girmemeli
            q.transform.SetParent(transform, false);
            q.transform.localScale = Vector3.one * dotSize;
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = UITheme.CreateOverlayMaterial(dotColor);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _dot = q.transform;

            SetVisible(false);
        }

        void Update()
        {
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            // Bool tetigi hic vermeyen OpenXR profilleri var; XRButtons analog eksene duser.
            bool raw = XRButtons.HeldWithAxisFallback(dev, CommonUsages.triggerButton,
                CommonUsages.trigger, _pressed ? ReleaseThreshold : PressThreshold);

            _pressed = raw;
            ClickDown = _pressed && !_prevPressed;
            _prevPressed = _pressed;
        }

        /// <summary>
        /// Isinin ham hali (baslangic + yon). Panel disindaki hedeflere nisan almak isteyen
        /// tuketiciler icin — nisan/kavrama pozu secimi ve egim duzeltmesi burada bir kez
        /// yapiliyor, kopyalanmasin (bkz. <see cref="Weapons.WeaponFingerPoser"/>).
        /// </summary>
        public bool TryGetRay(out Vector3 origin, out Vector3 dir) => TryGetAim(out origin, out dir);

        /// <summary>Isinin azami menzili — disaridaki kesisim testleri ayni siniri kullansin.</summary>
        public float MaxDistance => maxDistance;

        /// <summary>
        /// Isini panelin duzlemiyle kesistirir.
        /// </summary>
        /// <param name="panel">Hedef panel koku (+Z oyuncudan uzaga, olcek 1).</param>
        /// <param name="local">Panelin yerel duzlemindeki carpma noktasi (metre, X saga / Y yukari).</param>
        /// <param name="world">Dunya uzayindaki carpma noktasi.</param>
        /// <returns>Isin paneli ONDEN ve menzil icinde kesiyorsa true.</returns>
        public bool Raycast(Transform panel, out Vector2 local, out Vector3 world)
        {
            local = Vector2.zero;
            world = Vector3.zero;

            if (panel == null || !TryGetAim(out Vector3 o, out Vector3 d)) return false;

            Vector3 n = panel.forward;

            float denom = Vector3.Dot(n, d);
            if (Mathf.Abs(denom) < 1e-5f) return false;   // isin duzleme paralel

            float t = Vector3.Dot(panel.position - o, n) / denom;
            if (t <= 0f || t > maxDistance) return false; // arkada kaldi ya da menzil disi

            world = o + d * t;
            Vector3 lp = panel.InverseTransformPoint(world);
            local = new Vector2(lp.x, lp.y);
            return true;
        }

        /// <summary>Isini ciz. <paramref name="hit"/> false ise isin havada maxDistance kadar
        /// uzar ve uc noktasi gizlenir.</summary>
        public void Draw(bool hit, Vector3 hitPoint, Vector3 surfaceNormal)
        {
            if (!TryGetAim(out Vector3 o, out Vector3 d)) { SetVisible(false); return; }

            SetVisible(true);

            Vector3 end = hit ? hitPoint : o + d * maxDistance;
            _line.SetPosition(0, o);
            _line.SetPosition(1, end);

            if (_dot != null)
            {
                _dot.gameObject.SetActive(hit);
                if (hit)
                {
                    // Nokta yuzeyin 1 mm onunde dursun: tam duzlemde z-fighting yapardi.
                    _dot.position = hitPoint - surfaceNormal * 0.001f;
                    _dot.rotation = Quaternion.LookRotation(surfaceNormal);
                }
            }
        }

        public void SetVisible(bool on)
        {
            if (_line != null) _line.enabled = on;
            if (_dot != null && !on) _dot.gameObject.SetActive(false);
        }

        /// <summary>Kisa titresim — tusa bastigini parmakla da hissettirir.</summary>
        public static void Haptic(float amplitude = 0.35f, float duration = 0.03f)
        {
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (dev.isValid) dev.SendHapticImpulse(0, amplitude, duration);
        }
    }
}
