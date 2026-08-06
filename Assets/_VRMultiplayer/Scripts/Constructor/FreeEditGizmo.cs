using UnityEngine;
using VRMultiplayer.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// PC'de fareyle surukleme kolu: uc tasima oku ve uc donus halkasi — Unity Editor'daki
    /// gizmonun oyun ici karsiligi. <see cref="FreeEditController"/> secili SERBEST prop icin
    /// acar; surukleme boyunca <see cref="ConstructorSession.TryMoveFree"/>'ye commit'siz
    /// kareler, birakildiginda commit'li kesin deger gider (bkz. Faz B canli akis).
    ///
    /// CARPISICI YOK, ISIN MATEMATIGI VAR. Kollari collider'la kurmak, mermilerin ve el
    /// tutmanin kacinmasi gereken yeni fizik nesneleri demekti — projedeki her nisan yolu
    /// (zemin, duvar, imlec) zaten analitik. Isin-dogru ve isin-duzlem testleri hem daha ucuz
    /// hem de gizmonun her zaman "en ustte" cizilmesiyle tutarli.
    ///
    /// EKRAN-SABIT BOYUT: kol uzunlugu kameraya olan uzaklikla olceklenir, yoksa uzaktaki bir
    /// propun kolu birkac piksel kalir ve tutulamaz.
    /// </summary>
    public class FreeEditGizmo : MonoBehaviour
    {
        /// <summary>
        /// Kollar. Duzlem kollari (7..9) IKI EKSENDE birden kaydirir: normali X olan kol YZ
        /// duzleminde, Y olan XZ'de, Z olan XY'de. Unity'deki kucuk kareler bunlar — bir duvari
        /// "zeminde surumek" tek okla iki hamle, duzlem koluyla tek hareket.
        /// </summary>
        public enum Handle : byte
        {
            None,
            MoveX, MoveY, MoveZ,
            RotX, RotY, RotZ,
            PlaneX, PlaneY, PlaneZ,   // normali X/Y/Z olan duzlem
        }

        [Tooltip("Kol uzunlugu — kameraya olan uzakligin orani (ekran-sabit boyut).")]
        [Range(0.05f, 0.4f)] public float sizeRatio = 0.17f;

        [Tooltip("Kol uzunlugunun alt siniri (m): burnunu propa dayasan da kollar tutulabilir kalsin.")]
        public float minSize = 0.35f;

        [Tooltip("Gizmonun propun on yuzunden ne kadar ONDE duracagi (m). Kollarin gövdenin " +
                 "icinde kaybolmamasi icin.")]
        public float frontPad = 0.1f;

        /// <summary>
        /// Hangi kol takimi gorunur: TASIMA (oklar + duzlem kareleri) ya da DONDURME (halkalar).
        ///
        /// Unity de ikisini ayirir (W/E) ve sebebi burada olculdu: uc ok, uc kare, uc halka ve
        /// dis cember ayni anda ciziliyken hangi kolun nerede bittigi okunmuyor, ustelik tutma
        /// testleri birbirinin uzerine tasiyor.
        /// </summary>
        public enum Mode : byte { Move, Rotate }
        public Mode EditMode { get; set; } = Mode.Move;

        [Tooltip("Tutma toleransi — kol uzunlugunun orani. Buyutmek kollari yakalamayi " +
                 "kolaylastirir ama komsu kola tasma riskini artirir.")]
        [Range(0.04f, 0.25f)] public float grabTolerance = 0.1f;

        [Tooltip("Ctrl basiliyken tasima kademesi (m).")]
        public float snapMeters = 0.01f;

        [Tooltip("Ctrl basiliyken donus kademesi (derece).")]
        public float snapDegrees = 5f;

        static readonly Color XColor = new Color(1f, 0.32f, 0.32f);
        static readonly Color YColor = new Color(0.45f, 1f, 0.45f);
        static readonly Color ZColor = new Color(0.4f, 0.6f, 1f);
        static readonly Color HotColor = new Color(1f, 0.9f, 0.25f);
        static readonly Color OuterColor = new Color(1f, 1f, 1f, 0.5f);

        const int RingSegments = 48;

        /// <summary>Halka yaricapi, kol uzunlugunun kati. Oklardan BUYUK: ust uste binerlerse
        /// hem gorsel karisir hem tutma testi komsu kola tasar.</summary>
        const float RingScale = 1.18f;

        // Duzlem karesinin eksenler uzerindeki ic ve dis siniri (kol uzunlugunun orani).
        // Ictekiler merkezden uzak tutuluyor ki ok govdeleriyle cakismasin.
        const float PlaneInner = 0.22f;
        const float PlaneOuter = 0.5f;

        /// <summary>Suruklenen kol; None ise serbest.</summary>
        public Handle Dragging { get; private set; }

        /// <summary>Farenin uzerinde durdugu kol (surukleme yokken).</summary>
        public Handle Hover { get; private set; }

        /// <summary>Gizmo fareyi TUTUYOR: sol tik prop koymamali (bkz. FreeEditController).</summary>
        public bool Busy => Dragging != Handle.None || Hover != Handle.None;

        /// <summary>Surukleme sonucu: dunya konumu/donusu ve jestin bittigi (commit) bilgisi.</summary>
        public System.Action<Vector3, Quaternion, bool> Moved;

        Camera _cam;

        // Gizmonun CIZILDIGI yer (propun gorsel merkezi) ile propun PIVOTU ayri tutulur.
        // Unity'nin "Center" modundaki gibi: kollar nesnenin ortasinda durur, cunku pivot
        // cogu propta tabanda ve gizmo zemine gomulup gorunmez oluyordu. Tasima ikisini
        // birlikte kaydirir; DONUS pivot etrafinda kalir — veri modeli (rotationEuler) onu
        // oyle tanimliyor ve merkez etrafinda dondurmek konumu da degistirirdi.
        Vector3 _targetPos;     // kollarin cizildigi yer (propun on yuzunun onu)
        Vector3 _pivotPos;      // propun transform.position'u
        Quaternion _targetRot;
        Bounds _bounds;         // propun gorsel sinir kutusu — dayanak noktasi bundan turer
        bool _visible;

        // Surukleme baslangic durumu — delta hep BASLANGICA gore, kare kare birikmez:
        // birikimli toplama, kacirilan bir karede kaymayi kalici hale getirirdi.
        Vector3 _startPos;
        Vector3 _startPivot;
        Quaternion _startRot;
        float _startAxisT;
        float _startAngle;
        Vector3 _startPlanePoint;

        readonly LineRenderer[] _arms = new LineRenderer[3];
        readonly LineRenderer[] _rings = new LineRenderer[3];
        readonly Transform[] _tips = new Transform[3];
        readonly Material[] _armMats = new Material[3];
        readonly Material[] _ringMats = new Material[3];
        readonly Material[] _tipMats = new Material[3];
        readonly Transform[] _planes = new Transform[3];
        readonly Material[] _planeMats = new Material[3];
        LineRenderer _outerRing;   // kameraya bakan dis cember (Unity'deki beyaz halka)
        static Mesh _coneMesh;

        static Vector3 AxisOf(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
        static Color ColorOf(int i) => i == 0 ? XColor : i == 1 ? YColor : ZColor;

        /// <summary>Duzlem karesinin rengi: NORMALININ rengi, saydam. Unity'de de boyle —
        /// "hangi eksende KALIYORUM" sorusunun cevabi.</summary>
        static Color PlaneColor(int i)
        {
            var c = ColorOf(i);
            return new Color(c.r, c.g, c.b, 0.3f);
        }

        // ------------------------------------------------------------- disari acik

        /// <summary>
        /// Gizmoyu bir hedefe kilitler (dunya uzayinda).
        /// <paramref name="visualBounds"/> propun gorsel sinir kutusu — kollar bunun KAMERAYA
        /// BAKAN YUZUNUN ONUNDE cizilir (bkz. <see cref="AnchorFor"/>);
        /// <paramref name="pivotWorld"/> ise transformun kendi konumu, sonuc oradan verilir.
        /// </summary>
        public void Show(Vector3 pivotWorld, Quaternion worldRot, Bounds visualBounds)
        {
            _pivotPos = pivotWorld;
            _targetRot = worldRot;
            _bounds = visualBounds;
            if (!_visible)
            {
                _visible = true;
                _targetPos = visualBounds.center;   // ilk kare; hemen ardindan one alinir
                SetActive(true);
            }
        }

        /// <summary>
        /// Kollarin duracagi nokta: sinir kutusunun KAMERAYA BAKAN yuzunun biraz onu.
        ///
        /// Merkeze koymak yetmedi — bir duvarin ortasi gövdenin ICI demek ve uc koldan yalnizca
        /// disari tasan biri goruluyordu. Destek fonksiyonu (kutunun bakis yonundeki izdusumu)
        /// tam yuzeyi verir; ustune kucuk bir pay ile gizmo her acidan gövdenin onunde kalir.
        /// Kamera dondukce dayanak da doner, cunku "on yuz" kameraya gore tanimli.
        /// </summary>
        public static Vector3 AnchorFor(Bounds b, Vector3 camPos, float pad)
        {
            Vector3 toCam = camPos - b.center;
            if (toCam.sqrMagnitude < 1e-6f) return b.center;
            toCam.Normalize();

            float reach = Mathf.Abs(toCam.x) * b.extents.x +
                          Mathf.Abs(toCam.y) * b.extents.y +
                          Mathf.Abs(toCam.z) * b.extents.z;
            return b.center + toCam * (reach + pad);
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            Dragging = Hover = Handle.None;
            SetActive(false);
        }

        void OnDisable() => Hide();

        void OnDestroy()
        {
            for (int i = 0; i < 3; i++)
            {
                if (_arms[i] != null) Destroy(_arms[i].gameObject);
                if (_rings[i] != null) Destroy(_rings[i].gameObject);
                if (_tips[i] != null) Destroy(_tips[i].gameObject);
                if (_planes[i] != null) Destroy(_planes[i].gameObject);
            }
            if (_outerRing != null) Destroy(_outerRing.gameObject);
        }

        // ------------------------------------------------------------- dongu

        void LateUpdate()
        {
            if (!_visible) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            // Dayanak yalnizca surukleme YOKKEN tazelenir: jest sirasinda kamera oynasa bile
            // kollar elin altindan kaymasin.
            if (Dragging == Handle.None)
                _targetPos = AnchorFor(_bounds, cam.transform.position, frontPad);

            float size = Mathf.Max(minSize, Vector3.Distance(cam.transform.position, _targetPos) * sizeRatio);

#if ENABLE_INPUT_SYSTEM
            HandleMouse(cam, size);
#endif
            Draw(size);
        }

#if ENABLE_INPUT_SYSTEM
        void HandleMouse(Camera cam, float size)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool held = mouse.leftButton.isPressed;

            if (Dragging == Handle.None)
            {
                Hover = Pick(ray, size);
                if (Hover != Handle.None && mouse.leftButton.wasPressedThisFrame) BeginDrag(ray, size);
                return;
            }

            if (!held) { EndDrag(); return; }
            DragTo(ray, size);
        }
#endif

        void BeginDrag(Ray ray, float size)
        {
            Dragging = Hover;
            _startPos = _targetPos;
            _startPivot = _pivotPos;
            _startRot = _targetRot;

            int axis = AxisIndex(Dragging);
            if (IsMove(Dragging))
                ClosestPointOnAxis(ray, _targetPos, WorldAxis(axis), out _startAxisT);
            else if (IsPlane(Dragging))
                PlanePoint(ray, _targetPos, WorldAxis(axis), out _startPlanePoint);
            else
                RingAngle(ray, _targetPos, WorldAxis(axis), out _startAngle);
        }

        void EndDrag()
        {
            if (Dragging == Handle.None) return;
            Dragging = Handle.None;
            Moved?.Invoke(_pivotPos, _targetRot, true);   // jest bitti: kesin deger
        }

        void DragTo(Ray ray, float size)
        {
            int axis = AxisIndex(Dragging);
            Vector3 a = WorldAxis(axis);
            bool snap = SnapHeld();

            if (IsMove(Dragging))
            {
                if (!ClosestPointOnAxis(ray, _startPos, a, out float t)) return;
                float delta = t - _startAxisT;
                if (snap) delta = SnapTo(delta, snapMeters);
                _targetPos = _startPos + a * delta;    // gorsel merkez
                _pivotPos = _startPivot + a * delta;   // ayni kadar, propun pivotu
            }
            else if (IsPlane(Dragging))
            {
                if (!PlanePoint(ray, _startPos, a, out Vector3 p)) return;
                Vector3 d = p - _startPlanePoint;
                PlaneAxesFor(axis, out Vector3 u, out Vector3 w);
                float du = Vector3.Dot(d, u), dw = Vector3.Dot(d, w);
                if (snap) { du = SnapTo(du, snapMeters); dw = SnapTo(dw, snapMeters); }
                Vector3 move = u * du + w * dw;
                _targetPos = _startPos + move;
                _pivotPos = _startPivot + move;
            }
            else
            {
                if (!RingAngle(ray, _startPos, a, out float ang)) return;
                float delta = Mathf.DeltaAngle(_startAngle, ang);
                if (snap) delta = SnapTo(delta, snapDegrees);
                // Donus PIVOT etrafinda: veri modeli aciyi oyle tanimliyor, merkez etrafinda
                // dondurmek propun konumunu da degistirirdi.
                _targetRot = Quaternion.AngleAxis(delta, a) * _startRot;
            }

            Moved?.Invoke(_pivotPos, _targetRot, false);   // surukleme karesi
        }

        static bool SnapHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
#else
            return false;
#endif
        }

        /// <summary>Kademeli yuvarlama; adim 0 ya da negatifse deger oldugu gibi doner.</summary>
        public static float SnapTo(float value, float step) =>
            step > 0.0001f ? Mathf.Round(value / step) * step : value;

        // ------------------------------------------------------------- secim (hit-test)

        Handle Pick(Ray ray, float size)
        {
            float tol = size * grabTolerance;

            // Yalnizca AKTIF takim test edilir; kapali olan kollar gorunmedigi gibi
            // tutulamaz da (bkz. EditMode).
            if (EditMode == Mode.Rotate)
            {
                for (int i = 0; i < 3; i++)
                    if (RingHit(ray, _targetPos, WorldAxis(i), size * RingScale, tol))
                        return (Handle)(4 + i);
                return Handle.None;
            }

            // DUZLEMLER EN ONCE: kucuk ve kesin bir alan kapliyorlar, ustelik ok govdeleriyle
            // ayni bolgede duruyorlar. Ok once denenseydi duzlem karesi neredeyse hic
            // tutulamazdi.
            for (int i = 0; i < 3; i++)
            {
                PlaneAxesFor(i, out Vector3 pu, out Vector3 pw);
                if (PlaneHit(ray, _targetPos, WorldAxis(i), pu, pw,
                        size * PlaneInner, size * PlaneOuter))
                    return (Handle)(7 + i);
            }

            for (int i = 0; i < 3; i++)
            {
                if (!ClosestPointOnAxis(ray, _targetPos, WorldAxis(i), out float t)) continue;
                if (t < 0f || t > size) continue;
                Vector3 onAxis = _targetPos + WorldAxis(i) * t;
                if (DistanceToRay(ray, onAxis) <= tol) return (Handle)(1 + i);
            }
            return Handle.None;
        }

        static float DistanceToRay(Ray ray, Vector3 p)
        {
            float t = Mathf.Max(0f, Vector3.Dot(p - ray.origin, ray.direction));
            return Vector3.Distance(p, ray.origin + ray.direction * t);
        }

        /// <summary>
        /// Isin ile eksen dogrusunun en yakin oldugu noktanin eksen uzerindeki parametresi.
        /// Public static: saf aritmetik, oz-denetim (menu 29) kapsiyor.
        /// </summary>
        public static bool ClosestPointOnAxis(Ray ray, Vector3 origin, Vector3 axis, out float t)
        {
            t = 0f;
            Vector3 w0 = origin - ray.origin;
            float a = Vector3.Dot(axis, axis);
            float b = Vector3.Dot(axis, ray.direction);
            float c = Vector3.Dot(ray.direction, ray.direction);
            float d = Vector3.Dot(axis, w0);
            float e = Vector3.Dot(ray.direction, w0);

            float denom = a * c - b * b;
            if (Mathf.Abs(denom) < 1e-6f) return false;   // isin eksene paralel
            t = (b * e - c * d) / denom;
            return true;
        }

        /// <summary>
        /// Isinin halka duzlemini kestigi noktanin merkez etrafindaki acisi (derece).
        /// Public static: saf aritmetik, oz-denetim kapsiyor.
        /// </summary>
        public static bool RingAngle(Ray ray, Vector3 center, Vector3 normal, out float angle)
        {
            angle = 0f;
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-4f) return false;   // isin duzleme paralel

            float t = Vector3.Dot(center - ray.origin, normal) / denom;
            if (t <= 0f) return false;                    // duzlem arkada

            Vector3 v = ray.origin + ray.direction * t - center;
            PlaneAxes(normal, out Vector3 u, out Vector3 w);
            angle = Mathf.Atan2(Vector3.Dot(v, w), Vector3.Dot(v, u)) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>Isinin bir duzlemi kestigi nokta. Public static: oz-denetim kapsiyor.</summary>
        public static bool PlanePoint(Ray ray, Vector3 center, Vector3 normal, out Vector3 hit)
        {
            hit = default;
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-4f) return false;   // isin duzleme paralel
            float t = Vector3.Dot(center - ray.origin, normal) / denom;
            if (t <= 0f) return false;                    // duzlem arkada
            hit = ray.origin + ray.direction * t;
            return true;
        }

        /// <summary>Kesisim, duzlem karesinin ic/dis sinirlari arasinda mi (iki eksende de).</summary>
        static bool PlaneHit(Ray ray, Vector3 center, Vector3 normal, Vector3 u, Vector3 w,
            float inner, float outer)
        {
            if (!PlanePoint(ray, center, normal, out Vector3 p)) return false;
            Vector3 d = p - center;
            float du = Vector3.Dot(d, u), dw = Vector3.Dot(d, w);
            return du >= inner && du <= outer && dw >= inner && dw <= outer;
        }

        /// <summary>
        /// Bir eksene dik duran DIGER IKI DUNYA EKSENI — duzlem kolunun kendi eksenleri.
        /// <see cref="PlaneAxes"/>'ten farki: o keyfi bir referans seciyor, bu ise izgarayla
        /// ayni X/Y/Z eksenlerini veriyor (kademeli kaydirma onlarda anlamli).
        /// </summary>
        public static void PlaneAxesFor(int normalAxis, out Vector3 u, out Vector3 w)
        {
            u = AxisOf((normalAxis + 1) % 3);
            w = AxisOf((normalAxis + 2) % 3);
        }

        static bool RingHit(Ray ray, Vector3 center, Vector3 normal, float radius, float tol)
        {
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-4f) return false;
            float t = Vector3.Dot(center - ray.origin, normal) / denom;
            if (t <= 0f) return false;
            float r = Vector3.Distance(ray.origin + ray.direction * t, center);
            return Mathf.Abs(r - radius) <= tol;
        }

        /// <summary>Bir normale dik iki referans eksen — halka acisinin sifir yonu icin.</summary>
        static void PlaneAxes(Vector3 normal, out Vector3 u, out Vector3 w)
        {
            Vector3 seed = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
            u = Vector3.Normalize(Vector3.Cross(seed, normal));
            w = Vector3.Cross(normal, u);
        }

        static bool IsMove(Handle h) => h == Handle.MoveX || h == Handle.MoveY || h == Handle.MoveZ;
        static bool IsPlane(Handle h) => h == Handle.PlaneX || h == Handle.PlaneY || h == Handle.PlaneZ;

        /// <summary>Kol -> eksen indeksi. Uc grup da (tasi/dondur/duzlem) ucerli oldugu icin
        /// tek mod islemi yetiyor.</summary>
        static int AxisIndex(Handle h) => ((int)h - 1) % 3;
        static Vector3 WorldAxis(int i) => AxisOf(i);

        // ------------------------------------------------------------- cizim

        void EnsureVisuals()
        {
            if (_arms[0] != null) return;

            for (int i = 0; i < 3; i++)
            {
                Color c = ColorOf(i);

                _armMats[i] = UITheme.CreateOverlayMaterial(c, 3200);
                _arms[i] = MakeLine("~GizmoArm" + i, 2, _armMats[i], 0.01f);

                _ringMats[i] = UITheme.CreateOverlayMaterial(c, 3200);
                _rings[i] = MakeLine("~GizmoRing" + i, RingSegments + 1, _ringMats[i], 0.008f);

                // KONI uc, kup degil: Unity'nin tasima kolu boyle okunuyor ve yon belli oluyor.
                var tip = new GameObject("~GizmoTip" + i);
                tip.AddComponent<MeshFilter>().sharedMesh = ConeMesh();
                _tipMats[i] = UITheme.CreateOverlayMaterial(c, 3201);
                var mr = tip.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _tipMats[i];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _tips[i] = tip.transform;

                // Duzlem karesi: iki eksenin arasinda duran yari saydam quad.
                var pl = GameObject.CreatePrimitive(PrimitiveType.Quad);
                pl.name = "~GizmoPlane" + i;
                Destroy(pl.GetComponent<Collider>());
                _planeMats[i] = UITheme.CreateOverlayMaterial(PlaneColor(i), 3198);
                var pmr = pl.GetComponent<MeshRenderer>();
                pmr.sharedMaterial = _planeMats[i];
                pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pmr.receiveShadows = false;
                _planes[i] = pl.transform;
            }

            var outerMat = UITheme.CreateOverlayMaterial(OuterColor, 3199);
            _outerRing = MakeLine("~GizmoOuterRing", RingSegments + 1, outerMat, 0.006f);
        }

        /// <summary>
        /// Ok ucu konisi: taban yaricapi 0.5, boy 1, +Y yonunde, tabani kapali. Unity'de hazir
        /// koni primitifi yok; 16 dilim yeterince yuvarlak gorunuyor ve mesh bir kez uretilip
        /// uc kolda paylasiliyor.
        /// </summary>
        static Mesh ConeMesh()
        {
            if (_coneMesh != null) return _coneMesh;

            const int seg = 16;
            var verts = new Vector3[seg * 2 + 2];
            var tris = new int[seg * 6];

            verts[0] = Vector3.zero;              // taban merkezi
            verts[1] = Vector3.up;                // tepe
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var p = new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f);
                verts[2 + i] = p;                 // taban cemberi
                verts[2 + seg + i] = p;           // yan yuz icin ikinci kopya (sert kenar)
            }

            int t = 0;
            for (int i = 0; i < seg; i++)
            {
                int n = (i + 1) % seg;
                // taban (asagi bakar)
                tris[t++] = 0; tris[t++] = 2 + i; tris[t++] = 2 + n;
                // yan yuz
                tris[t++] = 1; tris[t++] = 2 + seg + n; tris[t++] = 2 + seg + i;
            }

            _coneMesh = new Mesh { name = "~GizmoCone" };
            _coneMesh.SetVertices(verts);
            _coneMesh.SetTriangles(tris, 0);
            _coneMesh.RecalculateNormals();
            _coneMesh.RecalculateBounds();
            return _coneMesh;
        }

        static LineRenderer MakeLine(string name, int points, Material mat, float width)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = points;
            lr.useWorldSpace = true;
            lr.startWidth = lr.endWidth = width;
            lr.material = mat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        void SetActive(bool on)
        {
            if (on) EnsureVisuals();
            for (int i = 0; i < 3; i++)
            {
                if (_arms[i] != null) _arms[i].gameObject.SetActive(on);
                if (_rings[i] != null) _rings[i].gameObject.SetActive(on);
                if (_tips[i] != null) _tips[i].gameObject.SetActive(on);
                if (_planes[i] != null) _planes[i].gameObject.SetActive(on);
            }
            if (_outerRing != null) _outerRing.gameObject.SetActive(on);
        }

        void Draw(float size)
        {
            EnsureVisuals();
            float ringR = size * RingScale;
            float tipLen = size * 0.26f;
            Handle hot = Dragging != Handle.None ? Dragging : Hover;
            bool move = EditMode == Mode.Move;

            // Kapali takimi gizle — Unity'de W/E ile olan ayrim.
            for (int i = 0; i < 3; i++)
            {
                _arms[i].gameObject.SetActive(move);
                _tips[i].gameObject.SetActive(move);
                _planes[i].gameObject.SetActive(move);
                _rings[i].gameObject.SetActive(!move);
            }
            _outerRing.gameObject.SetActive(!move);

            // Kalinliklar da MESAFEYLE olceklenir: sabit dunya genisligi uzakta sac teline,
            // yakinda boruya donuyordu.
            float armW = size * 0.038f;
            float ringW = size * 0.026f;

            for (int i = 0; i < 3; i++)
            {
                Vector3 a = WorldAxis(i);
                bool armHot = hot == (Handle)(1 + i);
                bool ringHot = hot == (Handle)(4 + i);

                // Govde ucta biter, koni oradan baslar — cizgi koninin icinden gecmesin.
                _arms[i].startWidth = _arms[i].endWidth = armW;
                _arms[i].SetPosition(0, _targetPos);
                _arms[i].SetPosition(1, _targetPos + a * (size - tipLen));
                UITheme.SetMaterialColor(_armMats[i], armHot ? HotColor : ColorOf(i));

                _tips[i].position = _targetPos + a * (size - tipLen);
                _tips[i].rotation = Quaternion.FromToRotation(Vector3.up, a);
                _tips[i].localScale = new Vector3(tipLen * 0.45f, tipLen, tipLen * 0.45f);
                UITheme.SetMaterialColor(_tipMats[i], armHot ? HotColor : ColorOf(i));

                PlaneAxes(a, out Vector3 u, out Vector3 w);
                _rings[i].startWidth = _rings[i].endWidth = ringHot ? ringW * 1.6f : ringW;
                for (int s = 0; s <= RingSegments; s++)
                {
                    float ang = s * Mathf.PI * 2f / RingSegments;
                    _rings[i].SetPosition(s, _targetPos + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * ringR);
                }
                UITheme.SetMaterialColor(_ringMats[i], ringHot ? HotColor : ColorOf(i));

                // Duzlem karesi iki eksenin ORTASINDA durur; quad'in normali +Z oldugu icin
                // LookRotation dogrudan kullanilabiliyor.
                PlaneAxesFor(i, out Vector3 pu, out Vector3 pw);
                float mid = size * (PlaneInner + PlaneOuter) * 0.5f;
                float side = size * (PlaneOuter - PlaneInner);
                bool planeHot = hot == (Handle)(7 + i);

                _planes[i].position = _targetPos + (pu + pw) * mid;
                _planes[i].rotation = Quaternion.LookRotation(a);
                _planes[i].localScale = new Vector3(side, side, 1f);
                var pc = planeHot ? HotColor : ColorOf(i);
                UITheme.SetMaterialColor(_planeMats[i],
                    new Color(pc.r, pc.g, pc.b, planeHot ? 0.65f : 0.3f));
            }

            if (!move) DrawOuterRing(ringR * 1.12f, ringW * 0.8f);
        }

        /// <summary>
        /// Kameraya bakan dis cember — Unity'nin donus gizmosundaki beyaz halka. Tutma kolu
        /// DEGIL, cerceve: uc renkli halka ust uste bindiginde hangi kurenin uzerinde
        /// oldugunu okumayi kolaylastiriyor.
        /// </summary>
        void DrawOuterRing(float radius, float width)
        {
            var cam = ResolveCamera();
            if (_outerRing == null || cam == null) return;

            Vector3 n = (cam.transform.position - _targetPos).normalized;
            PlaneAxes(n, out Vector3 u, out Vector3 w);
            _outerRing.startWidth = _outerRing.endWidth = width;
            for (int s = 0; s <= RingSegments; s++)
            {
                float ang = s * Mathf.PI * 2f / RingSegments;
                _outerRing.SetPosition(s, _targetPos + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * radius);
            }
        }

        Camera ResolveCamera()
        {
            if (_cam != null && _cam.isActiveAndEnabled) return _cam;
            _cam = Camera.main;
            if (_cam != null && _cam.isActiveAndEnabled) return _cam;
            _cam = null;
            foreach (var c in Camera.allCameras)
                if (c.isActiveAndEnabled) { _cam = c; break; }
            return _cam;
        }
    }
}
