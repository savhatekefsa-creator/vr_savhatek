using UnityEngine;
using VRMultiplayer.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// PC'nin ince-ayar editoru: fareyle prop sec, klavyeyle milimetrik it, panele sayi yazarak
    /// Inspector hassasiyetinde yerlestir. Akis su: gozluk izgarayla kaba yerlesimi yapar,
    /// PC'deki kisi onun yonlendirmesiyle propu serbest katmana cevirip (J) son santimetreleri
    /// ceker — her degisiklik <see cref="ConstructorSession.TryMoveFree"/> uzerinden aninda
    /// herkese yayilir.
    ///
    /// YALNIZCA PC: <see cref="ConstructorPlacer"/> Awake'te mobil olmayan platformda ekler.
    /// Quest'te hic dogmaz — kumandada karsiligi yok, OnGUI paneli orada zaten cizilmiyor.
    ///
    /// SAG TIK secer — sol tik yerlestiricinin "koy"u oldugu icin. Esc birakir, Delete siler.
    /// Izgara propunda tek islem J ile SERBESTE cevirmek: kurulu objenin transformu birebir
    /// kopyalanir (gordugun neyse veriye o gecer), izgara kaydi silinir, serbest kaydi acilir.
    /// Iki islem oldugu icin B'ye iki kez basmak donusumu tamamen geri alir.
    /// </summary>
    public class FreeEditController : MonoBehaviour
    {
        [Tooltip("Ok tuslari itme adimi (m). Shift ile onda biri (mm hassasiyeti).")]
        public float nudgeStep = 0.01f;

        [Tooltip("Q/E ve Alt+ok donus adimi (derece). Shift ile onda biri.")]
        public float rotateStep = 1f;

        ConstructorPlacer _placer;
        ConstructorPaletteUI _palette;
        FreeEditGizmo _gizmo;
        static ConstructorSession Session => ConstructorSession.Instance;

        uint _selectedId;
        GameObject _marker;
        Material _markerMat;
        Camera _cam;

        // Panel tamponlari: kullanici yazarken canli degerler ustune binmesin diye yalnizca
        // secim degisince ve bizim islemlerimizden sonra tazelenir. (Bedeli: baska bir esin
        // tasidigi prop secili kalirsa panel eski degeri gosterir — yeniden secmek tazeler.)
        readonly string[] _fields = new string[9];

        // Donusum sonrasi yeniden secim: kimligi SUNUCU verir ve yayin asenkron doner —
        // beklenen konuma bir serbest prop dusunce otomatik secilir.
        bool _awaitReselect;
        Vector3 _reselectPos;
        float _reselectUntil;

        public uint SelectedId => _selectedId;

        /// <summary>
        /// PC'de SOL TIK ile prop koymak acik mi. <b>P</b> ile degistirilir.
        ///
        /// Ince ayar yaparken kapatilir: secim (sag tik), panele deger yazmak ve klavyeyle
        /// itmek surerken sol tikin hala "koy" olmasi, her duzenlemede istenmeyen proplar
        /// birakiyordu.
        /// </summary>
        public bool PlaceEnabled { get; private set; } = true;

        /// <summary>Fare ince-ayar panelinin uzerinde mi (bu karede).</summary>
        public bool PointerOverPanel { get; private set; }

        /// <summary>
        /// Yerlestiriciye "bu kare koyma" diyen tek kapi: kilit kapaliysa, fare panelin
        /// uzerindeyse ya da gizmo kolu tutuyorsa. Uc kontrol de AYRI: kilidi acik unutan biri
        /// de panele tiklayinca ya da kolu suruklerken prop birakmasin — gizmo sol tikla
        /// suruklendigi icin bu sonuncusu olmadan her surukleme bir prop birakirdi.
        /// </summary>
        public bool BlocksPlacement =>
            !PlaceEnabled || PointerOverPanel || (_gizmo != null && _gizmo.Busy);

        // Panelin ekran dikdortgeni (OnGUI ile AYNI degerler; tek yerden okunuyor).
        static readonly Rect PanelRect = new Rect(10f, 20f, 300f, 360f);

        void Awake()
        {
            _placer = GetComponent<ConstructorPlacer>();
            _palette = GetComponent<ConstructorPaletteUI>();

            _gizmo = gameObject.AddComponent<FreeEditGizmo>();
            _gizmo.Moved += OnGizmoMoved;
            _gizmo.Scaled += OnGizmoScaled;
        }

        void OnDisable() => Deselect();

        void Update()
        {
            if (Application.isMobilePlatform) return;

            var s = Session;
            bool active = _placer != null && _placer.BuildMode && s != null && s.IsActive;
            if (!active)
            {
                if (_selectedId != 0) Deselect();
                PointerOverPanel = false;
                return;
            }

            UpdatePointerOverPanel();

            // Secili prop baska biri tarafindan silinmis olabilir (gozlukten silme, undo).
            if (_selectedId != 0 && s.InstanceOf(_selectedId) == null) Deselect();

            if (_awaitReselect) TryReselect(s);

#if ENABLE_INPUT_SYSTEM
            bool paletteOpen = _palette != null && _palette.IsOpen;
            if (!paletteOpen)
            {
                HandleMouse(s);
                HandleKeyboard(s);
            }
#endif
            UpdateMarker(s);
            UpdateGizmo(s);
        }

        // ------------------------------------------------------------- secim

        public void Select(uint id)
        {
            if (id == 0 || _selectedId == id) return;
            _selectedId = id;
            RefreshFields();
            EnsureMarker();
        }

        public void Deselect()
        {
            _selectedId = 0;
            _awaitReselect = false;
            if (_marker != null) { Destroy(_marker); _marker = null; }
            if (_gizmo != null) _gizmo.Hide();
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>
        /// Fare panelin uzerinde mi? Input System ekranin SOL ALT kosesinden olcuyor, IMGUI ise
        /// SOL UST'ten — donusum atlanirsa panel ekranin ters yarisinda "korunmus" olurdu.
        /// </summary>
        void UpdatePointerOverPanel()
        {
            PointerOverPanel = false;
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 p = mouse.position.ReadValue();
            PointerOverPanel = PanelRect.Contains(new Vector2(p.x, Screen.height - p.y));
#endif
        }

        void HandleMouse(ConstructorSession s)
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.wasPressedThisFrame) return;
            if (PointerOverPanel) return;   // panele sag tik secimi bozmasin
            if (_gizmo != null && _gizmo.Dragging != FreeEditGizmo.Handle.None) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 200f))
            {
                uint id = s.InstanceIdOf(hit.collider != null ? hit.collider.transform : null);
                if (id != 0) { Select(id); return; }
            }
            Deselect();   // bosluga sag tik = birak
        }

        void HandleKeyboard(ConstructorSession s)
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // P her zaman calisir — secim olmasa da kilit acilip kapanabilmeli.
            if (kb.pKey.wasPressedThisFrame) PlaceEnabled = !PlaceEnabled;

            if (_selectedId == 0) return;

            if (kb.escapeKey.wasPressedThisFrame) { Deselect(); return; }

            bool isFree = s.Layout.FindFree(_selectedId) != null;
            if (!isFree)
            {
                // Izgara propuna klavye itmesi bilerek yok: izgara hucre dilinde konusur.
                // Ince ayar isteniyorsa once J ile serbeste cevrilir.
                if (kb.jKey.wasPressedThisFrame) ConvertSelectedToFree();
                return;
            }

            if (kb.deleteKey.wasPressedThisFrame)
            {
                uint id = _selectedId;
                Deselect();
                s.TryRemoveFree(id);
                return;
            }

            // L = izgaraya geri oturt (J'nin tersi). K DEGIL: placer'da K kaydetme.
            if (kb.lKey.wasPressedThisFrame) { SnapSelectedToGrid(); return; }

            // N: gizmo takimi (tasi -> dondur -> boy -> tasi). Unity'de W/E/R ama ucu de burada
            // DOLU: E yaw ince ayari, W serbest-ucus kamerasinin ileri tusu, R ise placer'da.
            // Tek tusla cevirmek ayri tuslar aramaktan da hizli cikti.
            if (kb.nKey.wasPressedThisFrame && _gizmo != null)
                _gizmo.EditMode = FreeEditGizmo.NextMode(_gizmo.EditMode);

            float fine = (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) ? 0.1f : 1f;
            bool tilt = kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
            float m = nudgeStep * fine;
            float r = rotateStep * fine;

            Vector3 dPos = Vector3.zero;
            Vector3 dEul = Vector3.zero;

            if (tilt)
            {
                // Alt+ok: yatirma/devirme — uc eksen serbestligi klavyeden de erisilir.
                if (kb.upArrowKey.wasPressedThisFrame) dEul.x -= r;
                if (kb.downArrowKey.wasPressedThisFrame) dEul.x += r;
                if (kb.leftArrowKey.wasPressedThisFrame) dEul.z += r;
                if (kb.rightArrowKey.wasPressedThisFrame) dEul.z -= r;
            }
            else
            {
                // Oda eksenlerinde itme (paneldeki sayilarla ayni dil). Kamera goreli degil:
                // "X'i 2 cm arttir" diyen gozlukteki kisiyle ayni koordinati konusmali.
                if (kb.upArrowKey.wasPressedThisFrame) dPos.z += m;
                if (kb.downArrowKey.wasPressedThisFrame) dPos.z -= m;
                if (kb.leftArrowKey.wasPressedThisFrame) dPos.x -= m;
                if (kb.rightArrowKey.wasPressedThisFrame) dPos.x += m;
            }
            if (kb.gKey.wasPressedThisFrame) dPos.y += m;
            if (kb.vKey.wasPressedThisFrame) dPos.y -= m;
            if (kb.qKey.wasPressedThisFrame) dEul.y -= r;
            if (kb.eKey.wasPressedThisFrame) dEul.y += r;

            if (dPos == Vector3.zero && dEul == Vector3.zero) return;

            var f = s.Layout.FindFree(_selectedId);
            MoveSelected(f.position + dPos, f.rotationEuler + dEul, f.scale);
        }
#endif

        // ------------------------------------------------------------- islemler

        /// <summary>Panel ve klavyenin ortak uygulayicisi: commit'li tasima + tampon tazeleme.</summary>
        public bool MoveSelected(Vector3 position, Vector3 rotationEuler, Vector3 scale)
        {
            var s = Session;
            if (s == null || _selectedId == 0) return false;
            bool ok = s.TryMoveFree(_selectedId, position, rotationEuler, scale, true);
            if (ok) RefreshFields();
            return ok;
        }

        /// <summary>
        /// Secili IZGARA propunu serbest katmana tasir: kurulu objenin transformu birebir
        /// kopyalanir (pivot duzeltmesi ve fit olcegi COKTAN icinde — MapBuilder oyle kurdu),
        /// izgara kaydi silinir, ayni gorunumle serbest kaydi acilir.
        /// </summary>
        public bool ConvertSelectedToFree()
        {
            var s = Session;
            if (s == null || _selectedId == 0) return false;

            var placed = s.Layout.Find(_selectedId);
            if (placed == null) return false;   // zaten serbest ya da yok

            var def = s.Library.ById(placed.propId);
            var go = s.InstanceOf(_selectedId);
            if (def == null || go == null) return false;

            Vector3 pos = go.transform.localPosition;
            Vector3 eul = go.transform.localEulerAngles;
            Vector3 scl = go.transform.localScale;

            uint oldId = _selectedId;
            Deselect();
            if (!s.TryRemove(oldId)) return false;
            if (!s.TryPlaceFree(def, pos, eul, scl)) return false;

            _awaitReselect = true;
            _reselectPos = pos;
            _reselectUntil = Time.time + 3f;
            return true;
        }

        /// <summary>
        /// En yakin donus adimi (0..RotationSteps-1). Saf aritmetik, oz-denetim (menu 29)
        /// kapsiyor: 91.53 derece 90'a, 93 derece 95'e yuvarlanir.
        /// </summary>
        public static byte NearestRotStep(float yawDegrees)
        {
            int step = Mathf.RoundToInt(yawDegrees / MapLayout.RotationStepDegrees);
            return (byte)(((step % MapLayout.RotationSteps) + MapLayout.RotationSteps) % MapLayout.RotationSteps);
        }

        /// <summary>
        /// Secili SERBEST propu izgaraya geri oturtur — <see cref="ConvertSelectedToFree"/>'nin
        /// tersi. Aci en yakin 5 dereceye, konum en yakin hucreye ve kata yuvarlanir.
        ///
        /// OLCEK %100'e DONER ve bu kacinilmaz: izgara kaydi olcegi YUZDE tutuyor (ve kalinligi
        /// hic olceklemiyor), serbest kayit ise mutlak bir localScale. Ikisi ayni sayi degil;
        /// uydurmaktansa varsayilana donmek durust olani, panel de bunu yaziyor.
        ///
        /// Yer doluysa HICBIR SEY yapmaz: once silip sonra koyamamak, propu tamamen kaybetmek
        /// olurdu.
        /// </summary>
        public bool SnapSelectedToGrid()
        {
            var s = Session;
            if (s == null || _selectedId == 0) return false;

            var f = s.Layout.FindFree(_selectedId);
            if (f == null) return false;   // zaten izgarada

            var def = s.Library.ById(f.propId);
            if (def == null) return false;

            byte rot = NearestRotStep(f.rotationEuler.y);
            var size = s.Grid.FootprintSize(def, rot);
            var min = RoomGrid.CenterToMin(s.Grid.WorldToCell(f.position), size);
            byte level = (byte)Mathf.Clamp(
                Mathf.RoundToInt((f.position.y - s.Grid.FloorY) / Mathf.Max(0.01f, s.Layout.levelHeight)),
                0, s.MaxLevel);

            if (!s.CanPlace(def, min, rot, 100, level, 100)) return false;   // hucreler dolu

            uint oldId = _selectedId;
            Deselect();
            if (!s.TryRemoveFree(oldId)) return false;
            return s.TryPlace(def, min, level, rot);
        }

        void TryReselect(ConstructorSession s)
        {
            if (Time.time > _reselectUntil) { _awaitReselect = false; return; }
            foreach (var f in s.Layout.freeProps)
            {
                if (f == null || (f.position - _reselectPos).sqrMagnitude > 1e-6f) continue;
                _awaitReselect = false;
                Select(f.instanceId);
                return;
            }
        }

        // ------------------------------------------------------------- vurgu kutusu

        void EnsureMarker()
        {
            if (_marker != null) return;

            _marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _marker.name = "~FreeEditMarker";
            Destroy(_marker.GetComponent<Collider>());
            if (_markerMat == null)
                _markerMat = UITheme.CreateTransparentMaterial(new Color(0.3f, 0.8f, 1f, 0.18f));
            var mr = _marker.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _markerMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // ------------------------------------------------------------- gizmo

        /// <summary>
        /// Gizmo YALNIZCA serbest propta cikar: izgara propunun konumu hucreye ait, onu
        /// fareyle surukletmek izgarayla celiskili bir soz verirdi. Once J ile serbeste
        /// cevrilir.
        /// </summary>
        void UpdateGizmo(ConstructorSession s)
        {
            if (_gizmo == null) return;

            var go = _selectedId != 0 ? s.InstanceOf(_selectedId) : null;
            var f = _selectedId != 0 ? s.Layout.FindFree(_selectedId) : null;
            if (go == null || f == null) { _gizmo.Hide(); return; }

            // Surukleme sirasinda hedefi TAZELEMIYORUZ: gizmo kendi hesabini surukleme
            // baslangicindaki poza gore yapiyor, her kare geri yazmak jesti kendi kuyruguna
            // kilitlerdi.
            //
            // Olcek VERI MODELINDEN okunuyor (go.transform.localScale degil): ikisi normalde
            // ayni ama tek dogru kaynak kayit — jest de sonucu oraya yaziyor.
            if (_gizmo.Dragging == FreeEditGizmo.Handle.None)
                _gizmo.Show(go.transform.position, go.transform.rotation, VisualBounds(go), f.scale);
        }

        /// <summary>
        /// Propun gorsel sinir kutusu — gizmo kollarini bunun KAMERAYA BAKAN yuzunun onune
        /// koyuyor (bkz. <see cref="FreeEditGizmo.AnchorFor"/>).
        ///
        /// Once pivot denendi: cogu propta taban oldugu icin gizmo zemine gomuluyordu. Sonra
        /// merkez denendi: bu kez bir duvarin ORTASI gövdenin ici demek oldu ve uc koldan
        /// yalnizca disari tasan biri goruluyordu. Mesh yoksa pivot etrafinda kucuk bir kutu
        /// uydurulur — hicbir sey cizmemekten iyidir.
        /// </summary>
        static Bounds VisualBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.2f);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        /// <summary>
        /// Gizmodan gelen DUNYA pozunu oda uzayina cevirip oturuma verir. Kok (oda uzayi)
        /// maket modunda kucultulup tasindigi icin donusum sart — dunya degeri dogrudan
        /// yazilsaydi maket acikken prop haritanin disina firlardi.
        /// </summary>
        void OnGizmoMoved(Vector3 worldPos, Quaternion worldRot, bool commit)
        {
            var s = Session;
            if (s == null || _selectedId == 0) return;
            var f = s.Layout.FindFree(_selectedId);
            if (f == null) return;

            var root = s.Root;
            Vector3 pos = root != null ? root.InverseTransformPoint(worldPos) : worldPos;
            Quaternion rot = root != null ? Quaternion.Inverse(root.rotation) * worldRot : worldRot;

            s.TryMoveFree(_selectedId, pos, rot.eulerAngles, f.scale, commit);
            if (commit) RefreshFields();   // panel kesin degeri gostersin
        }

        /// <summary>
        /// Gizmodan gelen YENI olcegi oturuma verir. <see cref="OnGizmoMoved"/>'in aksine uzay
        /// donusumu YOK: olcek bir kat sayisi, kokun maket kuculmesinden etkilenmez — donusum
        /// uygulansaydi maket acikken her jest propu ayrica kucultuyor olurdu.
        ///
        /// Konum ve aci KAYITTAN oldugu gibi geri yazilir: bu jest onlara dokunmuyor.
        /// </summary>
        void OnGizmoScaled(Vector3 scale, bool commit)
        {
            var s = Session;
            if (s == null || _selectedId == 0) return;
            var f = s.Layout.FindFree(_selectedId);
            if (f == null) return;

            s.TryMoveFree(_selectedId, f.position, f.rotationEuler, scale, commit);
            if (commit) RefreshFields();   // panel kesin degeri gostersin
        }

        void UpdateMarker(ConstructorSession s)
        {
            if (_marker == null) return;
            var go = _selectedId != 0 ? s.InstanceOf(_selectedId) : null;
            if (go == null) { _marker.SetActive(false); return; }

            // Sinir kutusu her kare tazelenir: prop tasindikca kutu takip etsin. PC'de,
            // yalnizca secim varken calisiyor — GetComponents maliyeti burada dert degil.
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { _marker.SetActive(false); return; }
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            _marker.SetActive(true);
            _marker.transform.position = b.center;
            _marker.transform.rotation = Quaternion.identity;
            _marker.transform.localScale = b.size + Vector3.one * 0.02f;
        }

        // ------------------------------------------------------------- panel

        void RefreshFields()
        {
            var s = Session;
            var f = s != null ? s.Layout.FindFree(_selectedId) : null;
            if (f == null) return;   // izgara secimi: panelde alan gosterilmiyor
            _fields[0] = f.position.x.ToString("0.###");
            _fields[1] = f.position.y.ToString("0.###");
            _fields[2] = f.position.z.ToString("0.###");
            _fields[3] = f.rotationEuler.x.ToString("0.##");
            _fields[4] = f.rotationEuler.y.ToString("0.##");
            _fields[5] = f.rotationEuler.z.ToString("0.##");
            _fields[6] = f.scale.x.ToString("0.###");
            _fields[7] = f.scale.y.ToString("0.###");
            _fields[8] = f.scale.z.ToString("0.###");
        }

        /// <summary>Turkce klavyede virgul yazilir — ikisi de kabul, ayrastirma hep sabit kultur.</summary>
        static bool ParseF(string t, out float v) =>
            float.TryParse((t ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);

        void ApplyFields()
        {
            var v = new float[9];
            for (int i = 0; i < 9; i++)
                if (!ParseF(_fields[i], out v[i])) { RefreshFields(); return; }   // bozuk sayi: geri tazele
            MoveSelected(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]),
                new Vector3(v[6], v[7], v[8]));
        }

        void OnGUI()
        {
            if (Application.isMobilePlatform) return;
            var s = Session;
            if (_placer == null || !_placer.BuildMode || s == null || !s.IsActive) return;

            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            GUILayout.Label("INCE AYAR (PC)  —  sag tik = sec");
            GUILayout.Label(PlaceEnabled
                ? "[P] KOYMA ACIK — sol tik prop birakir, hayalet gorunur"
                : "[P] KOYMA KILITLI — hayalet ve isin gizli");

            if (_selectedId == 0)
            {
                GUILayout.Label("Secili prop yok.\n\nSag tikla bir propa:\n- IZGARA propu J ile serbeste cevrilir\n- SERBEST prop dogrudan duzenlenir");
                GUILayout.EndArea();
                return;
            }

            bool isFree = s.Layout.FindFree(_selectedId) != null;
            var go = s.InstanceOf(_selectedId);
            GUILayout.Label("Secili: " + (go != null ? go.name : _selectedId.ToString()) +
                            "   [" + (isFree ? "SERBEST" : "IZGARA") + "]");

            if (!isFree)
            {
                GUILayout.Label("[J] serbeste cevir — gorunum birebir korunur.\n" +
                                "Sonra ok/Q-E/panel ile milimetrik ayarlanir.\n" +
                                "(Geri almak icin B'ye iki kez.)");
                GUILayout.EndArea();
                return;
            }

            DrawRow("Konum", 0);
            DrawRow("Aci", 3);
            DrawRow("Olcek", 6);

            var ev = Event.current;
            bool enter = ev != null && ev.type == EventType.KeyDown &&
                         (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);
            if (GUILayout.Button("Uygula (Enter)") || enter) ApplyFields();

            GUILayout.Label("[N] kollar: " + ModeLabel());
            GUILayout.Label("Kollari SOL TIK ile surukle (Ctrl = kademeli)\n" +
                            "Boy: kolu disari cek buyur, iceri it kucul\n" +
                            "     ortadaki BEYAZ kup uc ekseni birden\n" +
                            "Ok: X/Z ±1 cm   G/V: yukari/asagi   Shift: mm\n" +
                            "Q/E: aci ±1°   Alt+Ok: yatir/devir\n" +
                            "[L] izgaraya oturt (olcek %100'e doner)\n" +
                            "Delete: sil   Esc: birak   B/U: geri al");
            GUILayout.EndArea();
        }

        /// <summary>Panelde okunan takim adi — parantez ici hangi SEKLI arayacagini soyluyor.</summary>
        string ModeLabel()
        {
            if (_gizmo == null) return "TASI (oklar + kareler)";
            switch (_gizmo.EditMode)
            {
                case FreeEditGizmo.Mode.Rotate: return "DONDUR (halkalar)";
                case FreeEditGizmo.Mode.Scale: return "BOY (kupler)";
                default: return "TASI (oklar + kareler)";
            }
        }

        void DrawRow(string label, int i0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(56f));
            for (int i = i0; i < i0 + 3; i++)
                _fields[i] = GUILayout.TextField(_fields[i] ?? "", GUILayout.Width(70f));
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------- kamera

        /// <summary>
        /// Cizen kamera — ConstructorPlacer.ResolveCamera ile ayni gerekce: adanmis sunucuda
        /// Camera.main yok, ServerView kendi etiketlenmemis kamerasini kurar.
        /// </summary>
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
