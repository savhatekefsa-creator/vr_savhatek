using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
// Not: UnityEngine.XR'i "using" yapMIYORUZ — InputSystem ile ayni isimde (InputDevice) tipleri
// var, cakisir. XR tiplerini tam adiyla yaziyoruz (WeaponBeltUI'daki ayni ders).

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Build-mode cursor: casts a ray from the right controller to the floor, snaps it to the
    /// grid, shows a translucent ghost of the selected prop (green = fits, red = does not) and
    /// commits on the trigger.
    ///
    /// RAY, NOT REACH. The player stands in a real room; walking to the far corner to drop a
    /// crate would make building exhausting. Pointing covers the whole room from wherever they
    /// happen to be standing, which is why this is the primary interaction and the dollhouse
    /// view (step 10) is only a convenience on top.
    ///
    /// KAPI: insa modu YALNIZCA yaratici modda acilir (bkz. <see cref="OnModeChosen"/>). Modu
    /// secmek editoru kendiliginden acar; ayrica SOL thumbstick TIKI ile kapatilip acilabilir —
    /// projedeki tek bos baglanti o. Adim 9'da kol saati menusune tasinacak.
    ///
    /// OYUNCU modunda o tus OLUDUR: mac ortasinda kazara basilip duvar orulmesin diye. (Bu not
    /// once "henuz mod sistemi yok" diyordu; mod sistemi geldi, kapi da takildi.)
    /// </summary>
    public class ConstructorPlacer : MonoBehaviour
    {
        [Header("Hayalet")]
        [Tooltip("Hayaletin hedefe yetisme hizi. Dusuk = daha yumusak ama daha gec.")]
        public float followSharpness = 25f;
        public Color validColor = new Color(0.3f, 1f, 0.45f, 0.45f);
        public Color invalidColor = new Color(1f, 0.25f, 0.2f, 0.45f);

        [Header("Giris esikleri")]
        [Range(0.4f, 0.95f)] public float stickPush = 0.7f;
        [Range(0.05f, 0.5f)] public float stickRelease = 0.3f;

        [Tooltip("Ince donuste (freeRotation) stick basili tutulunca tekrarin baslama gecikmesi (sn).")]
        public float rotateRepeatDelay = 0.4f;

        [Tooltip("Basili tutma tekrar hizi (adim/sn). 12 adim/sn, 5 derecelik adimla 60 derece/sn eder.")]
        public float rotateRepeatSteps = 12f;

        [Header("Nisan")]
        [Tooltip("Acik: isin GOZDEN ELE dogru gider (isaret parmagiyla gosterir gibi).\n" +
                 "Kapali: isin kumandanin kendi yonunden cikar, pointerLift ile duzeltilir.\n\n" +
                 "Oyun icinde SOL thumbstick SAG/SOL ile degistirilir.")]
        public bool aimFromEye = true;

        [Tooltip("Baskin gozun kafa merkezine yatay uzakligi (m). + = sag goz. Sol gozu baskin " +
                 "olan biri icin negatif yapilmali.")]
        [Range(-0.05f, 0.05f)] public float dominantEyeOffset = 0.032f;

        [Tooltip("KUMANDA modunun aci duzeltmesi (derece): tutamak ekseninden ne kadar yukari.")]
        [Range(-30f, 80f)] public float pointerLiftDegrees = 40f;

        [Tooltip("GOZ-EL modunun ince ayari (derece). Genelde 0 civari yeter; isin biraz " +
                 "yakina/uzaga dusuyorsa buradan duzeltilir.")]
        [Range(-30f, 30f)] public float eyeTrimDegrees = 0f;

        const string LiftPrefKey = "Constructor.PointerLift";
        const string EyeTrimPrefKey = "Constructor.EyeTrim";
        const string EyeAimPrefKey = "Constructor.AimFromEye";

        /// <summary>Aktif modun aci ayari — SOL stick yukari/asagi bunu degistirir.</summary>
        float AimTrim
        {
            get => aimFromEye ? eyeTrimDegrees : pointerLiftDegrees;
            set
            {
                if (aimFromEye)
                {
                    eyeTrimDegrees = Mathf.Clamp(value, -30f, 30f);
                    PlayerPrefs.SetFloat(EyeTrimPrefKey, eyeTrimDegrees);
                }
                else
                {
                    pointerLiftDegrees = Mathf.Clamp(value, -30f, 80f);
                    PlayerPrefs.SetFloat(LiftPrefKey, pointerLiftDegrees);
                }
            }
        }

        /// <summary>
        /// Tilts a ray up or down around the WORLD horizontal, not around the controller's own
        /// axis.
        ///
        /// This is the fix for the correction drifting sideways: rotating about the controller's
        /// local X means rolling your wrist rolls the correction with it, so the ray swings off
        /// to the side. Pitching about the horizon keeps "up" meaning up no matter how the
        /// controller is held.
        /// </summary>
        public static Vector3 PitchRay(Vector3 dir, float degrees)
        {
            if (Mathf.Abs(degrees) < 0.01f) return dir;
            Vector3 axis = Vector3.Cross(Vector3.up, dir);
            if (axis.sqrMagnitude < 1e-6f) return dir;   // isin tam dikey: egilecek yon yok
            return Quaternion.AngleAxis(-degrees, axis.normalized) * dir;
        }

        public bool BuildMode { get; private set; }

        // Oyuncu insa modu istedi ama oturum (istemcide) henuz hazir degil.
        bool _wantBuildMode;

        /// <summary>True while the cursor ray is landing on a buildable spot this frame.</summary>
        public bool HasCursor => _hasCursor;

        /// <summary>Minimum-corner cell the ghost is snapped to (only meaningful with <see cref="HasCursor"/>).</summary>
        public Vector2Int CursorMinCell => _minCell;

        /// <summary>Whether the current cursor position would accept the selected prop.</summary>
        public bool CursorValid => _valid;

        /// <summary>
        /// Build height, in <see cref="MapLayout.levelHeight"/> steps off the floor.
        ///
        /// Sticky on purpose: you shelve a whole row at one height, so the level survives
        /// placing, changing prop and leaving height mode. It resets when build mode closes.
        /// </summary>
        byte _level;

        /// <summary>
        /// Right stick UP/DOWN changes HEIGHT instead of cycling props while this is on.
        ///
        /// A mode rather than a new binding because there is no binding left: trigger places,
        /// A deletes, B undoes, grip opens the palette, left click toggles build mode, and both
        /// sticks already carry two jobs each. The right stick CLICK was the last free control
        /// on the controller — the same corner the build-mode toggle was painted into.
        /// </summary>
        bool _heightMode;

        // --- secim durumu ---
        int _propIndex;
        byte _rot;
        byte _widthPct = 100;   // yerel X (kalinlik olceklenmez)
        byte _heightPct = 100;  // Y
        bool _scaleWArmed, _scaleHArmed;

        // Yuzdelerin ayarlandigi prop. Baska bir prop gorunmesi, olcegin baskasina ait oldugu
        // anlamina gelir.
        PropDef _scaledFor;

        [Header("Boyut")]
        [Tooltip("BOY adimi (yuzde) — SOL thumbstick yukari/asagi. EN bu adimi KULLANMAZ: " +
                 "genislik tam hucre adimlariyla degisir (bkz. StepWidth), cunku ayak izi " +
                 "zaten yalnizca tam hucrelerde var olabiliyor.")]
        [Range(5, 50)] public int scaleStepPct = 10;
        [Range(10, 100)] public int minScalePct = 25;
        [Range(120, 250)] public int maxScalePct = 250;

        // --- hayalet ---
        GameObject _ghost;
        PropDef _ghostDef;
        GameObject _ghostPrefab;
        Renderer[] _ghostRenderers;
        Material[][] _validSets, _invalidSets;   // renderer basina hazir materyal dizileri
        Material _validMat, _invalidMat;
        bool _ghostShownOnce;
        bool _lastValid = true;

        [Header("Yapisma")]
        /// <summary>
        /// Snap reach, in CELLS. Four rather than two because the cell halved: the magnet has to
        /// feel the same distance away in the room, not the same number of squares.
        /// </summary>
        [Tooltip("Hayaletin komsu bir propun kenarina cekilecegi en buyuk mesafe (hucre). " +
                 "0 = kapali.\n\nHUCRE cinsinden: hucre boyu degisirse bunu da degistir, yoksa " +
                 "miknatisin menzili odada kayar. 0.0625 m hucrede 4 = 25 cm.\n\n" +
                 "Bedeli: bir komsunun bu kadar yakinina KASTEN bosluk birakmak zorlasir.")]
        [Range(0, 8)] public int snapCells = 4;

        // --- imlec durumu ---
        bool _hasCursor;
        Vector2Int _minCell;
        Vector2Int _pointedCell;   // isinin dustugu hucre — yapismadan ETKILENMEZ
        bool _snapped;
        RectInt _rect;
        bool _valid;

        /// <summary>Cursor is on a scanned wall rather than the floor this frame.</summary>
        bool _onWall;

        // FIILEN kullanilan donus ve kat. Zeminde oyuncunun sectikleri (_rot / _level), duvarda
        // duvarin dayattiklari. Yerlestirme, hayalet ve silme UCU DE bunlari okur — biri _rot'u
        // okusaydi duvara yasli prop bir yere, hayaleti baska yere giderdi.
        byte _placeRot;
        byte _placeLevel;
        string _cursorProblem;
        Vector3 _rayOrigin, _rayHit, _rayHitWorld;
        bool _tiltArmed, _aimModeArmed, _prevDollhouse;

        // --- giris kenar takibi ---
        bool _prevTrigger, _prevDelete, _prevToggle, _prevUndo, _prevHeightToggle;
        bool _rotArmed, _cycleArmed, _levelArmed;
        float _nextRotRepeatAt;   // ince donus tekrarinin siradaki adimi (bkz. HandleActions)

        TextMesh _panel;
        float _hidePanelAt = -1f;

        // --- kalibrasyon kapisi (bkz. RequireCalibration) ---
        CalibrationManager _calibration;
        bool _waitingForCalibration;

        ConstructorSession Session => ConstructorSession.Instance;
        ConstructorPaletteUI _palette;

        ConstructorDollhouse _dollhouse;
        ConstructorDollhouse Dollhouse
        {
            get
            {
                if (_dollhouse == null) _dollhouse = GetComponent<ConstructorDollhouse>();
                return _dollhouse;
            }
        }

        ConstructorGridView _gridView;
        ConstructorGridView GridView
        {
            get
            {
                if (_gridView == null) _gridView = GetComponent<ConstructorGridView>();
                return _gridView;
            }
        }

        /// <summary>Room space while the dollhouse is on; null means room space == world space.</summary>
        Transform Space => Dollhouse != null ? Dollhouse.Space : null;

        Ray ToRoomSpace(Ray r)
        {
            var sp = Space;
            if (sp == null) return r;
            // Yon normalize EDILMEZ: olcek yonun boyunu degistirir ama zemin kesisimi
            // t = (floorY - o.y) / d.y seklinde oldugu icin sonuc boydan bagimsizdir.
            return new Ray(sp.InverseTransformPoint(r.origin), sp.InverseTransformDirection(r.direction));
        }

        Vector3 ToWorld(Vector3 roomPoint)
        {
            var sp = Space;
            return sp == null ? roomPoint : sp.TransformPoint(roomPoint);
        }

        // ------------------------------------------------------------- loop

        void Awake()
        {
            _palette = GetComponent<ConstructorPaletteUI>();
            pointerLiftDegrees = PlayerPrefs.GetFloat(LiftPrefKey, pointerLiftDegrees);
            eyeTrimDegrees = PlayerPrefs.GetFloat(EyeTrimPrefKey, eyeTrimDegrees);
            aimFromEye = PlayerPrefs.GetInt(EyeAimPrefKey, aimFromEye ? 1 : 0) != 0;

            // PC ince-ayar editoru (bkz. FreeEditController): yalnizca fare/klavyeli makinede
            // anlamli. Quest'te (Android) hic dogmaz. Sahne bileseni olarak elle eklenmesin
            // diye buradan takiliyor — placer nerede dogarsa editor de orada.
            if (!Application.isMobilePlatform && GetComponent<FreeEditController>() == null)
                gameObject.AddComponent<FreeEditController>();
        }

        void Update()
        {
            if (_hidePanelAt > 0f && Time.time > _hidePanelAt) HidePanel();

            if (TogglePressed()) SetBuildMode(!(BuildMode || _wantBuildMode));

            // Kalibrasyon bitti mi? Insa modunun ILK kapisi buydu; acilis dizisi bastan isler
            // (sirada oturum var). Olay yerine yoklama: tek bool karsilastirmasi, ve statik bir
            // olaya abone olup birakmayi unutma riski yok.
            if (_waitingForCalibration && CalibrationManager.Calibrated)
            {
                _waitingForCalibration = false;
                // "KALIBRE EDILDI" paneli 6 sn daha duracakti; insa modu panelini tam onun
                // ustune acardik.
                if (_calibration != null) _calibration.HideStatus();
                if (_wantBuildMode) SetBuildMode(true);
            }

            // Sunucudan harita gelmis olabilir: bekleyen istek varsa simdi ac.
            if (_wantBuildMode && !BuildMode && Session != null && Session.IsActive)
                Activate(true);

            // Sunucu "harita yok" dediyse oyuncuya sebebi goster (bir kez).
            if (_wantBuildMode && !BuildMode && ConstructorSync.PendingMessage != null)
            {
                Show(ConstructorSync.PendingMessage, 8f);
                ConstructorSync.PendingMessage = null;
                _wantBuildMode = false;
            }

            // Kaydetme sonucu sunucudan geliyor ve insa modu KAPANDIKTAN sonra donebiliyor
            // (kayit, moddan cikarken isteniyor) — bu yuzden asagidaki erken cikistan ONCE.
            if (ConstructorSync.SaveMessage != null)
            {
                Show(ConstructorSync.SaveMessage, 3f);
                ConstructorSync.SaveMessage = null;
            }

            if (!BuildMode || Session == null || !Session.IsActive)
            {
                ShowGhost(false);
                return;
            }

            HandleLeftGrip();

            var def = SelectedProp();
            if (def == null) { ShowGhost(false); return; }

            // Imlecten ve hayaletten ONCE: ikisi de yuzdeleri okuyor, ayni karede sifirlanmis
            // degeri gormeleri gerekiyor.
            ResetScaleIfPropChanged(def);

            EnsureGhost(def);
            UpdateCursor(def);
            // Imlec HESABI kilitliyken de surer (silme ve durum paneli ona bakiyor) —
            // gizlenen yalnizca gorselleri.
            if (PlacementLocked && GridView != null) GridView.HideCursor();
            HandleActions(def);
            HandleLiftAdjust();
            AnimateGhost();
            UpdatePointerVisual();
            UpdateStatusPanel(def);
        }

        /// <summary>
        /// Live tuning of the pointer angle with the LEFT thumbstick.
        ///
        /// The right correction is not one number: it depends on how a person grips the
        /// controller. Shipping a constant would leave some players fighting the ray forever,
        /// and there is no Inspector inside a headset — so the dial is in the game, and the
        /// value is remembered.
        /// </summary>
        /// <summary>
        /// Left thumbstick, two jobs depending on whether the palette is open.
        ///
        /// PALETTE CLOSED it resizes the prop, because that is the thing you reach for
        /// constantly while building. PALETTE OPEN it edits the aim calibration, which you set
        /// once and then forget — burying it behind a menu keeps the everyday control free.
        /// </summary>
        [Tooltip("Sol gripte kisa/uzun bas ayrimi (saniye).")]
        public float longPressSeconds = 0.55f;

        float _gripDownAt;

        /// <summary>
        /// Left grip carries two things, split by hold length: a SHORT press toggles
        /// passthrough, a LONG press toggles the dollhouse.
        ///
        /// Passthrough gets the short press because it is the one you reach for — seeing the
        /// real room is the point of building there, and you need to be able to turn it off
        /// instantly if it misbehaves. The dollhouse is occasional, so it pays the long press.
        /// </summary>
        void HandleLeftGrip()
        {
            bool held = DollhouseHeld();

            if (held && !_prevDollhouse) _gripDownAt = Time.time;

            if (!held && _prevDollhouse)
            {
                if (Time.time - _gripDownAt >= longPressSeconds)
                {
                    if (Dollhouse != null)
                        Show(Dollhouse.Toggle()
                            ? "MAKET MODU\n\nHaritanin tamamina yukaridan bak."
                            : "MAKET KAPALI\n\nHarita odaya geri dondu.", 3f);
                }
                else
                {
                    var pass = GetComponent<ConstructorPassthrough>();
                    if (pass != null)
                    {
                        pass.SetActive(!pass.Active);
                        Show(!pass.Active ? "PASSTHROUGH KAPALI\n\nSanal dunya geri geldi."
                             : pass.CameraOk ? "PASSTHROUGH ACIK\n\nGercek odani goruyorsun."
                             : "PASSTHROUGH ACILAMADI\n\nARCameraManager yok ya da OpenXR\nozelligi kapali (menu 23).", 4f);
                    }
                }
            }

            _prevDollhouse = held;
        }

        void HandleLiftAdjust()
        {
            Vector2 left = LeftStick();
            bool paletteOpen = _palette != null && _palette.IsOpen;

            if (!paletteOpen)
            {
                // DIKEY = BOY, YATAY = EN. Ayri eksenler: bir bariyeri sadece yukseltmek ya
                // da sadece genisletmek isteyebilirsin; ikisini birden buyutmek cogu zaman
                // istenen sey degil.
                if (!_scaleHArmed && Mathf.Abs(left.y) > stickPush)
                {
                    int next = _heightPct + (left.y > 0f ? scaleStepPct : -scaleStepPct);
                    _heightPct = (byte)Mathf.Clamp(next, minScalePct, maxScalePct);
                    _scaleHArmed = true;
                }
                if (Mathf.Abs(left.y) < stickRelease) _scaleHArmed = false;

                if (!_scaleWArmed && Mathf.Abs(left.x) > stickPush)
                {
                    StepWidth(left.x > 0f ? 1 : -1);
                    _scaleWArmed = true;
                }
                if (Mathf.Abs(left.x) < stickRelease) _scaleWArmed = false;

                _tiltArmed = _aimModeArmed = false;
                return;
            }

            // --- palet acikken: nisan kalibrasyonu ---

            // SAG/SOL: nisan modunu degistir. Ikisini de gozlukte iki saniyede deneyebilmek
            // icin — hangisinin dogal geldigi kisiye gore degisir.
            if (!_aimModeArmed && Mathf.Abs(left.x) > stickPush)
            {
                aimFromEye = left.x < 0f;
                PlayerPrefs.SetInt(EyeAimPrefKey, aimFromEye ? 1 : 0);
                _aimModeArmed = true;
            }
            if (Mathf.Abs(left.x) < stickRelease) _aimModeArmed = false;

            // YUKARI/ASAGI: aktif modun aci ayari. Adim 2 derece — "biraz daha yukari" demek
            // isteyen birine 5 derece kaba geliyor.
            if (!_tiltArmed && Mathf.Abs(left.y) > stickPush)
            {
                AimTrim += left.y > 0f ? 2f : -2f;   // stick YUKARI = isin kalksin = daha uzaga
                _tiltArmed = true;
            }
            if (Mathf.Abs(left.y) < stickRelease) _tiltArmed = false;
            _scaleWArmed = _scaleHArmed = false;
        }

        void UpdateCursor(PropDef def)
        {
            _hasCursor = false;
            _snapped = false;
            if (!TryGetRay(out Ray worldRay))
            {
                _cursorProblem = "Isin kaynagi yok: ne XR kumandasi ne cizen kamera bulundu.";
                if (GridView != null) GridView.HideCursor();
                return;
            }

            // Maket modu burada BITIYOR: isini oda uzayina cevirdikten sonra zemin kesisimi,
            // hucre hesabi, gecerlilik ve hayalet hic degismeden calisiyor. Maket, ayri bir
            // yerlestirme sistemi degil, tek bir koordinat donusumu.
            Ray ray = ToRoomSpace(worldRay);
            _rayOrigin = worldRay.origin;   // isin GORSELI dunya uzayinda cizilir

            var grid = Session.Grid;

            // DUVAR mi ZEMIN mi? Ikisi de analitik, ikisi de mesafe veriyor, YAKIN OLAN kazanir.
            // Zemin duzlemi sonsuz oldugu icin gogus hizasina nisan alsan bile odanin cok
            // otesinde bir yerde "kesisiyor"; duvar ise sinirli bir dikdortgen. Yakinlik
            // karsilastirmasi, "duvara bakiyorsam duvar" demenin en dogal hali.
            bool wallHit = TryWallHit(ray, Session.Layout.builtForRoom, grid.FloorY,
                out Vector3 wallPoint, out Vector3 wallFacing, out float wallDist);
            bool floorHit = TryFloorHit(ray, grid.FloorY, out Vector3 floorPoint);
            float floorDist = floorHit ? Vector3.Distance(ray.origin, floorPoint) : float.MaxValue;

            // HISTEREZIS. Duvarin dibine nisan alirken iki mesafe birbirine cok yaklasiyor ve
            // duz bir karsilastirma kare kare mod degistirebiliyor — her degisimde propun hem
            // donusu hem hucresi ziplar, ve "isaret ettigim yere koyamiyorum" tam olarak boyle
            // hissettirir. Girmek icin duvarin acik ara yakin olmasi, cikmak icin zeminin acik
            // ara yakin olmasi gerekiyor; aradaki bantta mod neyse o kalir.
            const float modeMargin = 0.25f;
            _onWall = _onWall
                ? wallHit && wallDist < floorDist + modeMargin      // duvardaysak kalmak kolay
                : wallHit && wallDist < floorDist - modeMargin;     // gecmek icin acik ara lazim

            if (_onWall)
            {
                PlaceOnWall(def, grid, wallPoint, wallFacing);
                return;
            }

            _placeRot = _rot;
            _placeLevel = _level;

            Vector3 hit = floorPoint;
            if (!floorHit)
            {
                // En sik sebep: gozluksuz Editor testinde XR kamerasi rig kokunde, yani TAM
                // zemin seviyesinde duruyor — zemin duzlemine isin atmak matematiksel olarak
                // imkansiz. Sessizce "imlec yok" demek yerine sebebini soyluyoruz; bu cikmaza
                // bir kez girildi, ikinci kez tahmin ettirmeyelim.
                _cursorProblem = Mathf.Abs(ray.origin.y - grid.FloorY) < 0.05f
                    ? "Kamera zemin seviyesinde (y=" + ray.origin.y.ToString("0.00") +
                      "): asagi bakan isin uretilemiyor.\nPC'de test icin SUNUCU baslat: " +
                      "serbest-ucus kamerasi yukaridan bakar."
                    : "Isin zemine ulasmiyor (yukari bakiyor ya da zemin arkada).";
                if (GridView != null) GridView.HideCursor();
                return;
            }

            _cursorProblem = null;
            _rayHit = hit;                  // hesaplar ODA uzayinda kalir
            _rayHitWorld = ToWorld(hit);

            _pointedCell = grid.WorldToCell(hit);
            var size = grid.FootprintSize(def, _rot, _widthPct);

            var raw = RoomGrid.CenterToMin(_pointedCell, size);
            _minCell = grid.SnapToNeighbour(def, raw, _rot, _widthPct, snapCells, _level, _heightPct);
            _snapped = _minCell != raw;

            _rect = new RectInt(_minCell.x, _minCell.y, size.x, size.y);
            // Sinir kutusuna DEGIL, propun gercekten bastigi hucrelere bakiyor: ara acida
            // duran bir duvarin kutu koseleri bostur ve oraya baska bir sey konabilmeli.
            // Kat da hesaba katiliyor — dolu bir hucrenin USTU bos olabilir.
            _valid = grid.CanPlace(def, _minCell, _rot, _widthPct, _level, _heightPct);
            _hasCursor = true;

            // Ayak izini zemine boya: hangi hucreler uyuyor, hangileri cakisiyor. Hayaletin
            // kirmizi olmasi "bir yerde sigmiyor" der, bunu soylemez.
            if (GridView != null)
            {
                GridView.SetCursorCell(_pointedCell);   // kafes penceresi imleci takip etsin
                GridView.ShowCursor(grid.ShapeOf(def, _minCell, _rot, _widthPct), _pointedCell);
            }
        }

        /// <summary>
        /// Puts the cursor on a wall: the prop backs onto the surface, turns to face into the
        /// room, and rides at the height the ray landed.
        ///
        /// AIM DECIDES THE ROTATION, NOT THE HEIGHT. There is exactly one way to hang against a
        /// wall, so the facing is the wall's to dictate. Height used to come from the ray too —
        /// point higher, hang higher — and it read as the prop drifting up whenever the hand
        /// lifted, with no way to hold it still. Height belongs to the player now, the same one
        /// the floor uses, changed deliberately in height mode and otherwise left alone.
        ///
        /// It still lands on the ordinary grid — cell, level, rotation — so occupancy, the
        /// ghost, saving and the network path are the same ones the floor uses. Wall mounting is
        /// a way of CHOOSING a cell, not a second placement system.
        /// </summary>
        void PlaceOnWall(PropDef def, RoomGrid grid, Vector3 wallPoint, Vector3 facing)
        {
            _cursorProblem = null;
            _rayHit = wallPoint;
            _rayHitWorld = ToWorld(wallPoint);

            // Yerel +Z duvarin normaline bakacak sekilde dondur, sonra propun kendi adimina
            // yuvarla: yalnizca ceyrek tur donebilen bir prop duvara ara bir aciyla yaslanamaz.
            float yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            int step = def != null && def.freeRotation ? 1 : MapLayout.QuarterTurnSteps;
            int rotSteps = Mathf.RoundToInt(yaw / MapLayout.RotationStepDegrees / step) * step;
            _placeRot = (byte)(((rotSteps % MapLayout.RotationSteps) + MapLayout.RotationSteps) % MapLayout.RotationSteps);

            // Kat OYUNCUNUN: zeminde neyse duvarda da o. Isindan turetmek, el her
            // kalktiginda propu yukari kaydiriyordu.
            _placeLevel = _level;

            // Prop duvarin ICINE degil ONUNE otursun: yarim kalinligi kadar odaya dogru kaydir.
            var size = grid.FootprintSize(def, _placeRot, _widthPct);
            float thickness = grid.FootprintSize(def, 0, _widthPct).y * grid.CellSize;
            Vector3 centre = wallPoint + facing * (thickness * 0.5f);

            _pointedCell = grid.WorldToCell(centre);

            // CenterToMin BURADA KULLANILMAZ. O, "oyuncunun isaret ettigi hucre" icin yapilmis
            // ve cift sayili ayak izlerinde yarim hucre yanlidir; burada elimizde isaret edilen
            // bir hucre degil, propun oturmasi gereken KESIN merkez var. Dikdortgeni dogrudan o
            // merkeze gore kuruyoruz — aradaki fark tam bir hucre, yani propun duvara gomulmesi
            // ile arada parmak kalinliginda bosluk kalmasi arasindaki fark.
            _minCell = new Vector2Int(
                Mathf.RoundToInt((centre.x - grid.Origin.x) / grid.CellSize - size.x * 0.5f),
                Mathf.RoundToInt((centre.z - grid.Origin.y) / grid.CellSize - size.y * 0.5f));
            _snapped = false;   // duvarin kendisi zaten bir hizalama; ustune komsu cekmesi kafa karistirir

            _rect = new RectInt(_minCell.x, _minCell.y, size.x, size.y);
            _valid = grid.CanPlace(def, _minCell, _placeRot, _widthPct, _placeLevel, _heightPct);
            _hasCursor = true;

            if (GridView != null)
            {
                GridView.SetCursorCell(_pointedCell);
                GridView.ShowCursor(grid.ShapeOf(def, _minCell, _placeRot, _widthPct), _pointedCell);
            }
        }

        void HandleActions(PropDef def)
        {
            // Palet acikken stick ONUNDUR: kategori secerken hayaletin altta donmesi ya da
            // propun degismesi ayni hareketin iki anlama gelmesi olurdu.
            bool paletteOpen = _palette != null && _palette.IsOpen;

            // SAG STICK TIKI: yukseklik modu. Kumandada kalan son bos kontrol buydu.
            if (Edge(HeightModeHeld(), ref _prevHeightToggle) && !paletteOpen)
            {
                _heightMode = !_heightMode;
                Show(_heightMode
                        ? $"YUKSEKLIK MODU\n\nStick yukari/asagi = kat\nSu an: kat {_level}"
                        : "YUKSEKLIK MODU KAPALI\n\nStick yukari/asagi = prop degistir", 2.5f);
            }

            // Dondurme ve prop degistirme: stick histerezisli, yoksa tek itiste onlarca adim doner.
            Vector2 stick = paletteOpen ? Vector2.zero : RightStick();
            if (!_rotArmed && Mathf.Abs(stick.x) > stickPush)
            {
                Rotate(def, stick.x > 0f ? 1 : -1);
                _rotArmed = true;
                _nextRotRepeatAt = Time.time + rotateRepeatDelay;
            }
            // BASILI TUTMAK ince adimi tekrarlar: 5 derecelik adimla 90 derece = 18 itis
            // demekti, adimi inceltmek tekrarsiz kullanilabilirligi geriletirdi. Yalnizca
            // freeRotation proplar — ceyrek turda 4 yon var, tekrar secimin ustunden ucar.
            else if (_rotArmed && Mathf.Abs(stick.x) > stickPush && def != null &&
                     def.freeRotation && Time.time >= _nextRotRepeatAt)
            {
                Rotate(def, stick.x > 0f ? 1 : -1);
                _nextRotRepeatAt = Time.time + 1f / Mathf.Max(1f, rotateRepeatSteps);
            }
            if (Mathf.Abs(stick.x) < stickRelease) _rotArmed = false;

            // DIKEY EKSENIN IKI ANLAMI. Yatay hep dondurur — yuksege koyarken de propu
            // cevirmek istersin — degisen yalnizca dikey: normalde prop secer, yukseklik
            // modunda kat degistirir.
            if (Mathf.Abs(stick.y) > stickPush)
            {
                if (_heightMode)
                {
                    if (!_levelArmed) { StepLevel(stick.y > 0f ? 1 : -1); _levelArmed = true; }
                }
                else if (!_cycleArmed) { CycleProp(stick.y > 0f ? 1 : -1); _cycleArmed = true; }
            }
            if (Mathf.Abs(stick.y) < stickRelease) _cycleArmed = _levelArmed = false;

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.rKey.wasPressedThisFrame) Rotate(def, 1);
                if (kb.xKey.wasPressedThisFrame) CycleProp(1);
                if (kb.zKey.wasPressedThisFrame) CycleProp(-1);
                if (kb.kKey.wasPressedThisFrame) SaveNow();
                // PC'de boyut: + / - (hem ust sira hem numpad)
                // BOY: + / - (yuzde adimi)     EN: ] / [ (BIR HUCRE adimi)
                if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
                    _heightPct = (byte)Mathf.Min(_heightPct + scaleStepPct, maxScalePct);
                if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
                    _heightPct = (byte)Mathf.Max(_heightPct - scaleStepPct, minScalePct);
                if (kb.rightBracketKey.wasPressedThisFrame) StepWidth(1);
                if (kb.leftBracketKey.wasPressedThisFrame) StepWidth(-1);
                // KAT: PageUp / PageDown. PC'de mod gerekmiyor, tuslar bos.
                if (kb.pageUpKey.wasPressedThisFrame) StepLevel(1);
                if (kb.pageDownKey.wasPressedThisFrame) StepLevel(-1);
            }
#endif

            bool place = Edge(PlaceHeld(), ref _prevTrigger) && !paletteOpen;
            if (place)
            {
                // SESSIZ RED YOK. Eskiden kosul "place && _hasCursor && _valid" idi: gecersiz
                // bir konumda tetige basmak HICBIR SEY yapmiyordu — ne yerlestirme, ne log, ne
                // de bir aciklama. Oyuncu tetige basip duruyor ve neden olmadigini bilmiyordu.
                // Cihazda yasandi: masa yerlestirilemedi, sebep hicbir yerde yazmiyordu.
                if (!_hasCursor)
                {
                    Show("YERLESTIRILEMEZ\n\nImlec bir yuzeye bakmiyor", 2f);
                }
                else if (!_valid)
                {
                    Show("YERLESTIRILEMEZ\n\nHucreler dolu ya da alan disi.\n" +
                         "Baska yere bak, [ ile daralt,\nya da KAT degistir.", 3f);
                }
                else if (!Session.TryPlace(def, _minCell, _placeLevel, _placeRot, _widthPct, _heightPct))
                {
                    // Online'da true yalnizca "istek yola cikti" demek — prop, sunucunun yayini
                    // gelince belirir. Yerel red (dolu hucre / prefabsiz prop) false doner.
                    Show("YERLESTIRILEMEZ\n\nIstek reddedildi (Console'a bak)", 3f);
                    Debug.LogWarning("[Constructor] Yerlestirme reddedildi (hucreler dolu olabilir).");
                }
            }

            if (Edge(UndoHeld(), ref _prevUndo) && !paletteOpen)
            {
                if (!Session.Undo())
                    Show("GERI ALINACAK ISLEM YOK", 2f);
            }

            bool del = Edge(DeleteHeld(), ref _prevDelete);
            if (del)
            {
                // Isaret edilen hucrenin sahibini sil — ayak izinin min kosesini degil, imlecin
                // TAM ALTINDAKI hucreyi soruyoruz, yoksa buyuk bir propun kenarina bakarken
                // yanindaki bos hucre sorulup hicbir sey silinmezdi.
                //
                // IMLEC YOKKEN DE SERBEST PROP SILINEBILIR: serbest katman isinla secilir,
                // hucreyle degil — imlecin bir yuzeye oturmasi onun icin sart degil.
                //
                // SESSIZ RED YOK: hicbiri tutmadiysa sebebi yaziliyor. Eskiden hicbir sey
                // olmuyordu ve oyuncu yanlis yere mi baktigini, yanlis katta mi oldugunu
                // bilemiyordu.
                uint id = _hasCursor ? Session.InstanceIdAt(CursorCell(), _placeLevel) : 0u;
                if (id != 0) Session.TryRemove(id);
                else if (TryPickFreeProp(out uint freeId)) Session.TryRemoveFree(freeId);
                else if (!_hasCursor) Show("SILINEMEZ\n\nImlec bir yuzeye bakmiyor", 2f);
                else Show($"SILINECEK SEY YOK\n\nImlecin altinda prop yok (kat {_placeLevel})", 2f);
            }
        }

        /// <summary>
        /// The cell the ray lands on — NOT the footprint's centre.
        ///
        /// Delete has to follow the eye, not the ghost. Once the ghost snaps to a neighbour
        /// (<see cref="RoomGrid.SnapToNeighbour"/>) the two part company, and deriving the
        /// target from the ghost would mean pointing straight at a wall and deleting the piece
        /// next to it.
        /// </summary>
        Vector2Int CursorCell() => _pointedCell;

        /// <summary>
        /// Serbest katmandaki bir propu ISININ CARPTIGI yerden bulur.
        ///
        /// Serbest proplar hucre TUTMAZ (bkz. <see cref="FreePlacedProp"/>), yani
        /// <see cref="ConstructorSession.InstanceIdAt"/> onlari goremez — PC'de serbeste
        /// cevrilmis bir propu gozlukteki oyuncu silemez halde kalirdi, ve yanlislikla
        /// cevrilen bir prop haritada kalici olurdu. Fiziksel isin YALNIZCA silme aninda ve
        /// yalnizca hucre yolu bos donunce atilir; imlecin kendisi analitik kalir.
        /// </summary>
        bool TryPickFreeProp(out uint instanceId)
        {
            instanceId = 0;
            if (Session == null || !Session.IsActive) return false;
            if (!TryGetRay(out Ray worldRay)) return false;
            if (!Physics.Raycast(worldRay, out var hit, 30f)) return false;

            uint id = Session.InstanceIdOf(hit.collider != null ? hit.collider.transform : null);
            // Yalnizca SERBEST prop: isinin carptigi izgara propunu silmek, imlecin gosterdigi
            // hucreden baska bir seyi silmek olurdu.
            if (id == 0 || Session.Layout.FindFree(id) == null) return false;

            instanceId = id;
            return true;
        }

        void CycleProp(int delta)
        {
            var list = CurrentList();
            if (list.Count == 0) return;
            _propIndex = ((_propIndex + delta) % list.Count + list.Count) % list.Count;
        }

        PropDef SelectedProp()
        {
            var list = CurrentList();
            if (list.Count == 0) return null;
            if (_propIndex >= list.Count) _propIndex = 0;
            return list[_propIndex];
        }

        /// <summary>
        /// Puts width and height back to 100% when the selection moves to a DIFFERENT prop —
        /// the percentages are placer state, so otherwise the next prop inherits whatever the
        /// last one was stretched to.
        ///
        /// Keyed on the PROP, not hooked into each way of changing it: the stick cycle, the
        /// palette's category commit and the clamp in <see cref="SelectedProp"/> all pass
        /// through here, so a fourth way cannot forget to reset. Rotation is left alone on
        /// purpose — you keep one angle across several props while walling off a side.
        /// </summary>
        void ResetScaleIfPropChanged(PropDef def)
        {
            if (_scaledFor == def) return;
            _scaledFor = def;
            _widthPct = 100;
            _heightPct = 100;

            // Aci KORUNUR — bir duvari orerken onu propa prop tasimak istersin. Ama yalnizca
            // ceyrek tur donebilen bir propa gecildiyse ara aci onun icin gecersiz: en yakin
            // 90 dereceye oturtuluyor, yoksa hayalet o propta bir daha hizalanmayan bir acida
            // takili kalirdi.
            if (def != null && !def.freeRotation && _rot % MapLayout.QuarterTurnSteps != 0)
                _rot = (byte)(Mathf.RoundToInt((float)_rot / MapLayout.QuarterTurnSteps)
                              * MapLayout.QuarterTurnSteps % MapLayout.RotationSteps);
        }

        /// <summary>
        /// Turns the ghost by one step — a quarter turn normally, 5 degrees for a prop marked
        /// <see cref="PropDef.freeRotation"/>. Holding the stick REPEATS the fine step (see
        /// <see cref="HandleActions"/>): without repeat, a quarter turn would cost 18 flicks.
        ///
        /// The fine step is what makes angled building reachable at all: every input path used
        /// to move in 90-degree jumps, so the in-between angles existed in the data and in the
        /// footprint maths without any way for a player to ask for one. The step narrowed from
        /// 15 to 5 degrees with map file version 2; saved maps migrate on load
        /// (<see cref="MapLayout.FromJson"/>).
        /// </summary>
        void Rotate(PropDef def, int direction)
        {
            int step = def != null && def.freeRotation ? 1 : MapLayout.QuarterTurnSteps;
            int delta = direction > 0 ? step : MapLayout.RotationSteps - step;
            _rot = (byte)((_rot + delta) % MapLayout.RotationSteps);
        }

        /// <summary>
        /// Widens or narrows the prop by exactly ONE CELL.
        ///
        /// The unit is the point. A footprint only ever exists in whole cells, so stepping by a
        /// percentage meant several presses in a row landing on the same cell count and doing
        /// visibly nothing on a wide prop, while one press jumped two cells on a narrow one. And
        /// every percentage in between left the mesh and the rect it reserves disagreeing —
        /// which is exactly the seam that appears when two props are stood side by side. One
        /// press, one cell, mesh and rect in step.
        ///
        /// The percentage is still what gets stored: <see cref="PlacedProp.scalePct"/> is what
        /// saves and syncs, so only the input granularity changed, not the format.
        /// </summary>
        /// <summary>
        /// Raises or lowers the build height by one <see cref="MapLayout.levelHeight"/> step.
        ///
        /// Clamped to the scanned ceiling rather than to the byte: a prop above the real ceiling
        /// is one the player can neither see nor walk under, so stopping there tells them the
        /// room ran out instead of letting the ghost drift away silently.
        /// </summary>
        void StepLevel(int delta)
        {
            if (Session == null || !Session.IsActive) return;

            int next = Mathf.Clamp(_level + delta, 0, Session.MaxLevel);
            if (next == _level) return;
            _level = (byte)next;

            float h = _level * Session.Layout.levelHeight;
            Show($"KAT {_level} / {Session.MaxLevel}   ({h:0.00} m)", 1.5f);
        }

        void StepWidth(int deltaCells)
        {
            var grid = Session != null ? Session.Grid : null;
            var def = SelectedProp();
            if (grid == null || def == null) return;

            int cells = RoomGrid.WidthCells(def, grid.CellSize, _widthPct);
            byte pct = RoomGrid.WidthPctForCells(def, grid.CellSize, cells + deltaCells);
            _widthPct = (byte)Mathf.Clamp(pct, minScalePct, maxScalePct);
        }

        /// <summary>
        /// Props the thumbstick walks through: the palette's active category, or the whole
        /// placeable set when that category turns up empty (a library edited down to nothing in
        /// one category must not leave the player with no prop at all).
        /// </summary>
        IReadOnlyList<PropDef> CurrentList()
        {
            if (_palette != null)
            {
                var inCategory = Session.PlaceableIn(_palette.ActiveCategory);
                if (inCategory.Count > 0) return inCategory;
            }
            return Session.Placeable;
        }

        /// <summary>The palette committed a different category — start from its first prop.</summary>
        public void OnCategoryChanged() => _propIndex = 0;

        // ------------------------------------------------------------- build mode

        /// <summary>
        /// Requests build mode. On a CLIENT the session is not ready synchronously — the room
        /// and the map live on the server and arrive over the network — so the request is
        /// remembered and <see cref="Update"/> opens the mode the moment they land. Without
        /// this the player would press the button, see "not ready", and have to press again at
        /// exactly the right moment.
        /// </summary>
        public void SetBuildMode(bool on)
        {
            if (on)
            {
                _wantBuildMode = true;
                if (BuildMode) return;

                // Kapilar sirayla: ONCE kalibrasyon, sonra oturum. Ikisi de istegi acik birakir.
                if (!RequireCalibration()) return;   // kalibre olunca Update yeniden dener

                if (Session == null || !Session.EnsureStarted())
                {
                    Show("INSA MODU HAZIRLANIYOR\n\n" +
                         (Session != null ? Session.NotStartedReason : "Oturum yok"), 6f);
                    return;   // istek acik kalir; oturum hazir olunca Update acar
                }
                Activate(true);
                return;
            }

            _wantBuildMode = false;
            _waitingForCalibration = false;
            if (!BuildMode) { HidePanel(); return; }
            // Moddan cikarken kaydet: bir oturumluk emek, cikisi unutunca ucmasin.
            //
            // YARATICI AKISTA DEGIL. Orada kaydetme karari cikista SORULUYOR ("Kaydet?" ->
            // isimlendirme | degisiklikleri at, bkz. UI.CreativeFlowUI) ve burada sessizce
            // yazmak "at" secenegini anlamsiz kilardi: atilacak sey coktan diske yazilmis
            // olurdu. Askiyi o akis koyup kaldiriyor.
            if (!ConstructorSession.AutoSaveSuspended) SaveNow();

            Activate(false);
        }

        /// <summary>
        /// Insa modunun ILK kapisi: ortak fiziksel cerceve (<see cref="CalibrationManager"/>).
        ///
        /// NEDEN INSADAN ONCE, sonra degil:
        ///  - Harita kalibre cercevede orulur. Kalibre olmadan konan bir duvar, kalibre olan
        ///    herkeste BASKA bir yere duser — ve hata haritayi acan kisiye hic gorunmez.
        ///  - Kalibrasyon rig'i dondurup kaydiriyor. Insa modu acikken yapilsaydi zemin
        ///    oyuncunun altinda kayardi; ustelik ayni tetik hem "A noktasi" hem "prop koy" olurdu.
        ///
        /// Kapi iki durumda ACIK birakilir, ikisi de bilincli:
        ///  - Kulaklik bagli degil (masaustu testi): A/B noktasini alacak kumanda yok. Constructor
        ///    bilerek PC'de de calisiyor — ulasilamayan bir adima kilitlemek onu oldururdu.
        ///  - Sahnede kalibrasyon bileseni yok ya da kapali (PC sunucusu boyle: LanBootstrap
        ///    kapatir, cunku sunucunun fiziksel oyun alani yok).
        /// </summary>
        /// <returns>Kapi gecildiyse true. False donerse kalibrasyon BASLATILMISTIR ve bitince
        /// <see cref="Update"/> modu kendi acar — oyuncunun tekrar tusa basmasi gerekmez.</returns>
        bool RequireCalibration()
        {
            if (CalibrationManager.Calibrated) return true;
            if (!UnityEngine.XR.XRSettings.isDeviceActive) return true;

            if (_calibration == null) _calibration = FindFirstObjectByType<CalibrationManager>();
            if (_calibration == null || !_calibration.enabled) return true;

            _calibration.Begin("Kalibrasyon biter bitmez INSA MODU kendiliginden acilir.");
            _waitingForCalibration = true;

            // Sebep normalde KALIBRASYON PANELINDE yazar, burada degil: iki panel de kafanin
            // 1.4 m onunde durur, ikisini birden acmak ust uste iki yazi demek. Ama o panel
            // sahnede atanmamissa sebebi BIZ yazmaliyiz — yoksa oyuncu YARATICI'yi secer ve
            // hicbir sey olmamis gibi gorunur.
            if (_calibration.status != null) HidePanel();
            else Show("INSA MODU: ONCE KALIBRASYON\n\n" +
                      "Sag kumandayi A noktasina koy, TETIGE bas;\n" +
                      "sonra B noktasina koy, tekrar bas.\n" +
                      "Bitince insa modu kendiliginden acilir.", 12f);
            return false;
        }

        void Activate(bool on)
        {
            BuildMode = on;

            // Insa modundan cikarken maketi de kapat: harita kucucuk halde havada asili
            // kalmasin, odaya gercek boyutunda geri donsun.
            if (!on && Dollhouse != null) Dollhouse.SetActive(false);

            // Passthrough insa moduyla birlikte gelir gider: insa ederken GERCEK odani
            // gorursun, oyuna donunce sanal dunya geri gelir.
            var pass = GetComponent<ConstructorPassthrough>();
            if (pass != null) pass.SetActive(on);

            // Silah/tutma girislerini sustur. Proje neredeyse tum tuslari kullaniyor: tetik ayni
            // anda hem ates hem "koy", grip hem tutma hem palet olamaz.
            XRButtons.GameplayInputSuppressed = on;

            ShowGhost(on);

            if (GridView != null) GridView.SetVisible(on);

            if (on) { EnsureStatusPanel(); EnsurePointerVisual(); }
            else { DestroyStatusPanel(); DestroyPointerVisual(); }

            int placed = Session != null ? Session.PlacedCount : 0;
            // Insa modundan cikinca kat sifirlanir: bir sonraki oturuma "neden havada
            // duruyor" diye baslamak, hatirlamanin getirdigi kolayliktan daha pahali.
            if (!on) { _level = 0; _heightMode = false; }

            // Serbest alan sessiz kalmamali: oyuncu gercek odasinin duvarlarini bekliyorsa
            // hicbir seyin engellememesi hata gibi gorunur — sebebi burada bir kez yaziyor.
            bool freeSpace = Session != null && Session.IsFreeSpace;

            Show(on
                ? (freeSpace ? "INSA MODU ACIK  (SERBEST ALAN — oda taramasi yok)" : "INSA MODU ACIK") +
                  "\n\nTetik = koy   A = sil   B = geri al\nGrip = palet\n" +
                  "Stick yatay = dondur   SAG STICK TIK = yukseklik"
                : $"INSA MODU KAPALI\n\n{placed} prop.", 4f);
        }

        /// <summary>
        /// Saves the map — from wherever the player happens to be standing.
        ///
        /// The map is the SERVER's: a client writing its own copy leaves a file nobody reads and
        /// the next sync invalidates. That rule used to be enforced by giving up — a client got
        /// here, saw it was not the server, and returned. Which meant that building in a
        /// headset, the normal case, could not save at all. It asks the server now instead of
        /// refusing, and the answer comes back as something the player can read
        /// (<see cref="ConstructorSync.SaveMessage"/>).
        /// </summary>
        void SaveNow()
        {
            if (Session == null || !Session.IsActive) return;

            if (ConstructorSession.IsMapAuthority)
            {
                bool ok = Session.Save();
                Show(ok ? $"KAYDEDILDI\n\n{Session.PlacedCount} prop." : "KAYDEDILEMEDI\n\nConsole'a bak.", 3f);
                return;
            }

            if (!ConstructorSync.ClientRequestSave())
                Show("KAYDEDILEMEDI\n\nSunucuya baglanti yok.", 3f);
        }

        // ------------------------------------------------------------- ghost

        void EnsureGhost(PropDef def)
        {
            if (_ghostDef == def && _ghost != null) return;

            DestroyGhost();

            var prefab = def.Resolve();
            if (prefab == null) return;

            _ghost = Instantiate(prefab);
            _ghost.name = "~ConstructorGhost";
            _ghostPrefab = prefab;   // olcek hesabi prefabin KENDI olcegini de biliyor olmali

            // Hayalet fiziksel DEGIL: carpisicisi mermi durdurur, rigidbody'si yere duserdi.
            foreach (var c in _ghost.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var rb in _ghost.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);

            EnsureGhostMaterials();
            _ghostRenderers = _ghost.GetComponentsInChildren<Renderer>(true);

            // Materyal dizilerini ONCEDEN kur. Gecerli/gecersiz her degistiginde yeni dizi
            // ayirmak, imleci gezdirirken kare basina cop uretmek demekti.
            _validSets = new Material[_ghostRenderers.Length][];
            _invalidSets = new Material[_ghostRenderers.Length][];
            for (int i = 0; i < _ghostRenderers.Length; i++)
            {
                int slots = Mathf.Max(1, _ghostRenderers[i].sharedMaterials.Length);
                _validSets[i] = new Material[slots];
                _invalidSets[i] = new Material[slots];
                for (int s = 0; s < slots; s++)
                {
                    _validSets[i][s] = _validMat;
                    _invalidSets[i][s] = _invalidMat;
                }
                _ghostRenderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            _ghostDef = def;
            _ghostShownOnce = false;
            ApplyGhostMaterial(true);
            _ghost.SetActive(BuildMode);
        }

        void EnsureGhostMaterials()
        {
            if (_validMat == null) _validMat = UITheme.CreateTransparentMaterial(validColor);
            if (_invalidMat == null) _invalidMat = UITheme.CreateTransparentMaterial(invalidColor);
        }

        void ApplyGhostMaterial(bool valid)
        {
            if (_ghostRenderers == null) return;
            var sets = valid ? _validSets : _invalidSets;
            for (int i = 0; i < _ghostRenderers.Length; i++)
                if (_ghostRenderers[i] != null) _ghostRenderers[i].sharedMaterials = sets[i];
            _lastValid = valid;
        }

        void AnimateGhost()
        {
            if (_ghost == null) return;

            if (!_hasCursor || PlacementLocked) { _ghost.SetActive(false); return; }
            _ghost.SetActive(true);

            if (_valid != _lastValid) ApplyGhostMaterial(_valid);

            // Hayalet ODA UZAYINDA yasar. Maket acilinca ayni koke baglanir ve modelin icinde
            // dogru boyda belirir — konum hesabi hic degismez.
            var space = Space;
            if (_ghost.transform.parent != space)
            {
                _ghost.transform.SetParent(space, false);
                _ghostShownOnce = false;   // yeni uzayda sicrayarak degil, dogrudan belirsin
            }

            float yaw = _placeRot * MapLayout.RotationStepDegrees;
            Quaternion targetRot = Quaternion.Euler(0f, yaw, 0f);

            // Hayalet KONULACAK boyutu ve YERI gosterir; yoksa oyuncu ikisini de ancak
            // birakinca ogrenirdi. Hicbirini kendisi HESAPLAMIYOR: yerlestirmeyle ayni
            // fonksiyonlara soruyor, yoksa iki formul birbirinden usulca ayrilir ve hayalet
            // yalan soylemeye baslar.
            var ghostScale = MapBuilder.LocalScaleFor(
                _ghostDef, _ghostPrefab, Session.Grid.CellSize, _widthPct, _heightPct);
            _ghost.transform.localScale = ghostScale;

            // Hayalet KONULACAK yukseklikte durur. Sabit 0 verilseydi kat secen oyuncu propun
            // nereye gidecegini ancak birakinca ogrenirdi.
            Vector3 target = Session.Grid.RectCenter(_rect, _placeLevel, Session.Layout.levelHeight)
                             - MapBuilder.PivotOffset(_ghostDef, ghostScale, yaw);

            if (!_ghostShownOnce)
            {
                // Ilk karede yumusatma YOK: hayalet sahnenin bir kosesinden sure suzulerek
                // gelmesin, dogrudan imlecte belirsin.
                _ghost.transform.localPosition = target;
                _ghost.transform.localRotation = targetRot;
                _ghostShownOnce = true;
                return;
            }

            // Kare hizindan bagimsiz yumusatma. El titremesi hucreden hucreye zipladiginda
            // hayaletin ani sicramasi VR'da gozu yorar.
            float t = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            _ghost.transform.localPosition = Vector3.Lerp(_ghost.transform.localPosition, target, t);
            _ghost.transform.localRotation = Quaternion.Slerp(_ghost.transform.localRotation, targetRot, t);
        }

        void ShowGhost(bool show)
        {
            if (!show) { _ghostShownOnce = false; }
            if (_ghost != null) _ghost.SetActive(show);
        }

        void DestroyGhost()
        {
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
            _ghostDef = null;
            _ghostPrefab = null;
            _ghostRenderers = null;
            _validSets = _invalidSets = null;
        }

        // ------------------------------------------------------------- pointer beam

        LineRenderer _beam;
        Transform _beamDot;
        Material _beamMat, _beamDotMat;

        /// <summary>
        /// A visible ray from the hand to the cell under it, green when the spot takes the prop
        /// and red when it does not.
        ///
        /// This matters more than getting the angle exactly right: with the beam drawn, a
        /// player simply sees where they are pointing and adjusts in a second. Without it, an
        /// invisible ray that sits a few degrees off feels like the game ignoring your hand —
        /// which is exactly how it felt before.
        /// </summary>
        void EnsurePointerVisual()
        {
            if (_beam != null) return;

            var bgo = new GameObject("~ConstructorBeam");
            _beam = bgo.AddComponent<LineRenderer>();
            _beam.positionCount = 2;
            _beam.useWorldSpace = true;
            _beam.startWidth = _beam.endWidth = 0.004f;
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;
            _beamMat = UITheme.CreateOverlayMaterial(validColor, 3100);
            _beam.material = _beamMat;

            // Zemindeki benek: iki yuzlu materyal sayesinde yatik dursa da hep gorunur.
            var dgo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(dgo.GetComponent<Collider>());
            dgo.name = "~ConstructorBeamDot";
            _beamDot = dgo.transform;
            _beamDot.rotation = Quaternion.Euler(90f, 0f, 0f);
            _beamDotMat = UITheme.CreateOverlayMaterial(validColor, 3101);
            var dmr = dgo.GetComponent<MeshRenderer>();
            dmr.sharedMaterial = _beamDotMat;
            dmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dmr.receiveShadows = false;
        }

        void UpdatePointerVisual()
        {
            if (_beam == null) return;

            bool show = BuildMode && _hasCursor && !PlacementLocked;
            if (_beam.enabled != show) _beam.enabled = show;
            if (_beamDot != null && _beamDot.gameObject.activeSelf != show)
                _beamDot.gameObject.SetActive(show);
            if (!show) return;

            Color c = _valid ? validColor : invalidColor;
            UITheme.SetMaterialColor(_beamMat, new Color(c.r, c.g, c.b, 0.5f));
            UITheme.SetMaterialColor(_beamDotMat, new Color(c.r, c.g, c.b, 0.9f));

            _beam.SetPosition(0, _rayOrigin);
            _beam.SetPosition(1, _rayHitWorld);

            if (_beamDot != null)
            {
                // Maket modunda benek de kuculur, yoksa 1:10 modelin ustunde kocaman durur.
                // Alt sinir var: tam olcekli kuculme onu gorunmez yapardi.
                var sp = Space;
                float k = sp != null ? Mathf.Max(0.25f, sp.lossyScale.x) : 1f;
                _beamDot.position = _rayHitWorld + Vector3.up * 0.008f * k;   // zemine gomulmesin
                _beamDot.localScale = Vector3.one * 0.07f * k;
            }
        }

        void DestroyPointerVisual()
        {
            // Isin ve benek dunyada duruyor (bu objenin cocugu degil) — elle silinmeli.
            if (_beam != null) Destroy(_beam.gameObject);
            if (_beamDot != null) Destroy(_beamDot.gameObject);
            _beam = null;
            _beamDot = null;
        }

        void OnEnable()
        {
            AppMode.Chosen += OnModeChosen;
            // Bootstrap sahne yuklenirken dogar, mod ise SONRA secilir — normalde olayi
            // yakalariz. Yine de zaten secilmis olma ihtimaline karsi (bilesen sonradan
            // eklenirse) durumu bir kez okuyoruz.
            OnModeChosen(AppMode.Current);
        }

        void OnDisable()
        {
            AppMode.Chosen -= OnModeChosen;
            DestroyGhost();
            DestroyStatusPanel();
            DestroyPointerVisual();
            // Kapiyi MUTLAKA birak: burada kalirsa oyuncu silahsiz ve elleri tutmaz halde kalir.
            XRButtons.GameplayInputSuppressed = false;
        }

        /// <summary>
        /// Yaratici mod editörun kapisi (bkz. <see cref="AppMode"/> — o dosyanin tarif ettigi
        /// TEK baglanti noktasi burasi).
        ///
        /// YARATICI: once KALIBRASYON (bkz. <see cref="RequireCalibration"/>), sonra insa modu
        /// KENDILIGINDEN acilir; kullanici ne hangi tusun editoru actigini ne de kalibrasyonu
        /// ayrica baslatmayi bilmek zorunda kalsin — modu secmek zaten "harita tasarlayacagim"
        /// demek. Kalibrasyon ya da oturum hazir degilse <see cref="SetBuildMode"/> sebebi
        /// gosterip istegi acik tutar, hazir olunca <see cref="Update"/> modu kendi acar.
        ///
        /// OYUNCU / ANA MENU: acik kalmis insa modu kapatilir (kaydederek). Girisin kendisi
        /// <see cref="TogglePressed"/> icinde ayrica kilitli — yani maci yarida birakip
        /// yanlislikla duvar oren kimse olmaz.
        /// </summary>
        void OnModeChosen(AppMode.Mode m)
        {
            // YARATICI ARTIK DOGRUDAN EDITORE ATMAZ: once kalibrasyon, sonra HARITA MENUSU
            // (bkz. UI.CreativeFlowUI). Editor oradaki "yeni harita" / "mevcut harita" ile
            // aciliyor — yoksa hangi harita uzerinde calisildigi hic sorulmadan tek bir
            // haritaya girilirdi ve menunun var olma sebebi ortadan kalkardi.
            if (m == AppMode.Mode.Creative) return;

            if (BuildMode || _wantBuildMode) SetBuildMode(false);
        }

        // ------------------------------------------------------------- input

        static bool Edge(bool now, ref bool prev)
        {
            bool edge = now && !prev;
            prev = now;
            return edge;
        }

        /// <summary>Right-hand ray in VR; the mouse ray on a PC so this is testable without a headset.</summary>
        bool TryGetRay(out Ray ray)
        {
            var rig = XRRigReference.Instance;
            var hand = rig != null ? rig.rightHand : null;
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (hand != null && dev.isValid)
            {
                if (aimFromEye)
                {
                    // GOZDEN ELE: isin, gozunden elinin icinden gecip zemine iner — tipki bir
                    // seyi isaret parmaginla gostermek gibi.
                    //
                    // Kumandanin ROTASYONUNU hic kullanmaz, dolayisiyla kalibrasyon gerektirmez.
                    // Cihaz pozunun hangi eksene hizali oldugu ve kisinin kumandayi nasil
                    // tuttugu onemsizlesir; nisan bilekle degil kolla alinir.
                    Transform head = XRRigReference.HeadOrCamera;
                    if (head != null)
                    {
                        // Baskin GOZDEN nisan al, kafanin MERKEZINDEN degil: gercek hayatta da
                        // bir seyi tek gozle hizalariz, merkezden cikan isin surekli birkac
                        // derece yana kacar.
                        Vector3 eye = head.position + head.right * dominantEyeOffset;
                        Vector3 d = hand.position - eye;
                        if (d.sqrMagnitude > 0.0004f)   // el gozun icinde degilse
                        {
                            ray = new Ray(hand.position, PitchRay(d.normalized, eyeTrimDegrees));
                            return true;
                        }
                    }
                }

                // Kumanda yonu: ham hand.forward TUTAMAK ekseni boyunca bakar, oyuncunun
                // isaret ettigini sandigi yon degildir; fark aci duzeltmesiyle kapatilir.
                ray = new Ray(hand.position, PitchRay(hand.forward, pointerLiftDegrees));
                return true;
            }

#if ENABLE_INPUT_SYSTEM
            var cam = ResolveCamera();
            var mouse = Mouse.current;
            if (cam != null && mouse != null)
            {
                ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                return true;
            }
#endif
            ray = default;
            return false;
        }

        Camera _cam;

        /// <summary>
        /// The camera actually drawing the screen.
        ///
        /// NOT just <c>Camera.main</c>: in dedicated-server mode <see cref="ServerView"/> turns
        /// the XR camera OFF and installs its own untagged free-fly camera, so Camera.main is
        /// null exactly when the PC is the machine you would want to build from. Falling through
        /// to the live camera makes mouse placement work there too — and that free-fly view is
        /// the better vantage point anyway.
        /// </summary>
        Camera ResolveCamera()
        {
            if (_cam != null && _cam.isActiveAndEnabled) return _cam;

            _cam = Camera.main;
            if (_cam != null && _cam.isActiveAndEnabled) return _cam;

            // allCameras dizi ayirir; bu yuzden yalnizca onbellek dustugunde buraya geliyoruz.
            _cam = null;
            foreach (var c in Camera.allCameras)
                if (c.isActiveAndEnabled) { _cam = c; break; }
            return _cam;
        }

        /// <summary>
        /// Analytic ray/plane hit — no physics, so the floor needs no collider and the cursor
        /// cannot be stolen by a prop the player is pointing through. Public because it is pure
        /// arithmetic worth covering in the self-check (menu 29).
        /// </summary>
        public static bool TryFloorHit(Ray ray, float floorY, out Vector3 hit)
        {
            hit = default;
            if (Mathf.Abs(ray.direction.y) < 1e-4f) return false;   // isin zemine paralel
            float t = (floorY - ray.origin.y) / ray.direction.y;
            if (t <= 0f) return false;                               // zemin ARKADA kaldi
            hit = ray.origin + ray.direction * t;
            return true;
        }

        /// <summary>
        /// Nearest scanned wall the ray lands on, as a point on its surface plus the direction
        /// that surface faces.
        ///
        /// Bounded, unlike the floor: the floor plane is infinite, so a ray aimed at chest
        /// height still "hits" it somewhere far past the room. A wall is a finite rectangle, and
        /// a hit outside its width or below the floor is not a hit — otherwise pointing at open
        /// space would drag the ghost onto a wall behind the player.
        ///
        /// Analytic, like the floor, and for the same reason: the scanned walls carry no
        /// colliders, and giving them some purely to aim at would put geometry in the physics
        /// scene that bullets and grenades would then have to be kept out of.
        /// </summary>
        public static bool TryWallHit(Ray ray, RoomPlan plan, float floorY,
            out Vector3 hit, out Vector3 facing, out float distance)
        {
            hit = default; facing = default; distance = float.MaxValue;
            if (plan == null || plan.walls == null) return false;

            bool found = false;
            foreach (var w in plan.walls)
            {
                if (w == null || w.width <= 0.01f) continue;

                Vector3 n = w.normal;
                n.y = 0f;
                if (n.sqrMagnitude < 1e-6f) continue;
                n.Normalize();

                float denom = Vector3.Dot(ray.direction, n);
                if (Mathf.Abs(denom) < 1e-5f) continue;              // isin duvara paralel

                float t = Vector3.Dot(w.center - ray.origin, n) / denom;
                if (t <= 0f || t >= distance) continue;              // arkada ya da daha uzak

                Vector3 p = ray.origin + ray.direction * t;

                // Duvarin KENDI dikdortgeni icinde mi?
                Vector3 along = Vector3.Cross(Vector3.up, n);        // duvar boyunca yatay eksen
                if (Mathf.Abs(Vector3.Dot(p - w.center, along)) > w.width * 0.5f) continue;
                if (p.y < floorY - 0.05f || p.y > floorY + w.height + 0.05f) continue;

                hit = p;
                facing = n;
                distance = t;
                found = true;
            }
            return found;
        }

        FreeEditController _freeEdit;

        /// <summary>
        /// PC'de koyma KILITLI mi (<b>P</b>). Kilitliyken hayalet, isin ve izgara imleci de
        /// gizlenir: ince ayar yaparken elde koyulmayacak bir prop asili durmasi, "simdi
        /// birakacagim" izlenimi verip ekrani da kapatiyordu.
        ///
        /// <see cref="FreeEditController.BlocksPlacement"/> DEGIL, sadece kilit okunur: o,
        /// fare panelin uzerine gelince de true oluyor ve hayalet her fare hareketinde yanip
        /// sonerdi.
        /// </summary>
        bool PlacementLocked
        {
            get
            {
                if (_freeEdit == null) _freeEdit = GetComponent<FreeEditController>();
                return _freeEdit != null && !_freeEdit.PlaceEnabled;
            }
        }

        bool PlaceHeld()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (dev.isValid && XRButtons.HeldWithAxisFallback(dev,
                    UnityEngine.XR.CommonUsages.triggerButton, UnityEngine.XR.CommonUsages.trigger, 0.6f))
                return true;
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.isPressed) return false;

            // PC'de koyma KILITLENEBILIR (P) ve panel uzerindeyken hep kapali — ince ayar
            // yaparken sol tikin "koy" olmasi istenmeyen prop birakiyordu. Gozlugu
            // ETKILEMEZ: tetik yukaridaki daldan doner.
            if (_freeEdit == null) _freeEdit = GetComponent<FreeEditController>();
            return _freeEdit == null || !_freeEdit.BlocksPlacement;
#else
            return false;
#endif
        }

        bool DeleteHeld()
        {
            if (XRButtons.Button(UnityEngine.XR.XRNode.RightHand, UnityEngine.XR.CommonUsages.primaryButton))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.fKey.isPressed;
#else
            return false;
#endif
        }

        /// <summary>
        /// VR: sag B. PC: U.
        ///
        /// B insa modunda serbest: LanBootstrap onu yalnizca oturum DISINDA (katilma), TeamSelector
        /// da yalnizca takim secilene kadar dinliyor — ikisi de bu noktada susmus oluyor.
        /// </summary>
        bool UndoHeld()
        {
            if (XRButtons.Button(UnityEngine.XR.XRNode.RightHand, UnityEngine.XR.CommonUsages.secondaryButton))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.uKey.isPressed;
#else
            return false;
#endif
        }

        /// <summary>VR: SOL grip. PC: T. Insa modunda sol grip bostur (HandGrabber susturulmus).</summary>
        bool DollhouseHeld()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            if (dev.isValid && XRButtons.HeldWithAxisFallback(dev,
                    UnityEngine.XR.CommonUsages.gripButton, UnityEngine.XR.CommonUsages.grip, 0.5f))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.tKey.isPressed;
#else
            return false;
#endif
        }

        bool ToggleHeld()
        {
            if (XRButtons.Button(UnityEngine.XR.XRNode.LeftHand, UnityEngine.XR.CommonUsages.primary2DAxisClick))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.bKey.isPressed;
#else
            return false;
#endif
        }

        bool TogglePressed()
        {
            // Kenari HER KARE oku, mod ne olursa olsun: yalnizca yaratici modda okusaydik,
            // OYUNCU modunda basili tutulan stick "_prevToggle = false" olarak donar ve moda
            // gecer gecmez HAYALET bir basis uretirdi.
            bool edge = Edge(ToggleHeld(), ref _prevToggle);

            // OYUNCU modunda insa moduna giris YOK. Sinif basindaki "gecici tus duzeni" notu
            // tam bunu bekliyordu: sol stick tiki mac ortasinda kazara basilabilecek bir tustu
            // ve mod sistemi henuz yoktu. Artik var — kapi yalnizca YARATICI modda aciliyor.
            return edge && AppMode.IsCreative;
        }

        /// <summary>
        /// Right thumbstick click. The LEFT one already toggles build mode, so this was the only
        /// control left on the controller — see <see cref="_heightMode"/>.
        /// </summary>
        bool HeightModeHeld()
        {
            if (XRButtons.Button(UnityEngine.XR.XRNode.RightHand, UnityEngine.XR.CommonUsages.primary2DAxisClick))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.hKey.isPressed) return true;
#endif
            return false;
        }

        Vector2 RightStick() => Stick(UnityEngine.XR.XRNode.RightHand);

        /// <summary>Sol stick insa modunda serbest: yurume fiziksel, snap-turn kapali.</summary>
        Vector2 LeftStick() => Stick(UnityEngine.XR.XRNode.LeftHand);

        static Vector2 Stick(UnityEngine.XR.XRNode node)
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            if (dev.isValid &&
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                return axis;
            return Vector2.zero;
        }

        // ------------------------------------------------------------- panel / hud

        void Show(string text, float hideAfter = -1f)
        {
            if (_panel == null)
                // "~": insa modunun kendi UI'si. Passthrough tam da insa modunda aciliyor,
                // yani oneksiz birakmak "aleti kullanirken aletin gostergesini gizlemek" olurdu.
                // Su an yalnizca olusturma SIRASI sayesinde kurtuluyor (HideVirtualWorld
                // etkinlestigi ANDAKI kok objeleri gizliyor) — kirilgan bir tesaduf.
                _panel = HeadFollowPanel.Create("~Constructor Panel", "", new Color(0.5f, 0.9f, 1f));
            _panel.gameObject.SetActive(true);
            _panel.text = text;
            _hidePanelAt = hideAfter > 0f ? Time.time + hideAfter : -1f;
        }

        void HidePanel()
        {
            if (_panel != null) _panel.gameObject.SetActive(false);
            _hidePanelAt = -1f;
        }

        TextMesh _status;
        float _nextStatusAt;

        void EnsureStatusPanel()
        {
            if (_status != null) return;
            _status = HeadFollowPanel.Create("~Constructor Status", "", new Color(0.75f, 0.95f, 1f));
            var follow = _status.GetComponent<HeadFollowPanel>();
            follow.distance = 1.1f;
            follow.heightOffset = -0.45f;   // goz hizasinin ALTINDA: nisan aldigin yeri kapatmasin
            _status.characterSize = 0.055f;
        }

        void DestroyStatusPanel()
        {
            if (_status != null) Destroy(_status.gameObject);
            _status = null;
        }

        /// <summary>
        /// Always-on readout inside the headset.
        ///
        /// The PC's OnGUI panel is invisible from in there, so a player who cannot place
        /// something has no way to see WHETHER the cursor is even landing, which prop is
        /// selected, or why a spot is refused — and neither does anyone trying to help them.
        /// </summary>
        void UpdateStatusPanel(PropDef def)
        {
            if (_status == null || Time.time < _nextStatusAt) return;
            // Her karede string uretmiyoruz: VR'da cop toplayici bedava degil.
            _nextStatusAt = Time.time + 0.15f;

            string cat = _palette != null
                ? ConstructorPaletteUI.CategoryName(_palette.ActiveCategory) : "-";
            var list = CurrentList();

            string snap = _snapped ? "  [YAPISTI]" : "";
            string cursor = _hasCursor
                ? (_valid ? $"UYGUN   hucre ({_minCell.x},{_minCell.y}){snap}"
                          : $"UYGUN DEGIL   ({_minCell.x},{_minCell.y}){snap}")
                : "IMLEC YOK\n" + (_cursorProblem ?? "sebep bilinmiyor");

            // Passthrough durumu panelde SUREKLI gorunur: siyah bir bosluga bakan oyuncu
            // sebebini buradan okur, tahmin etmek zorunda kalmaz.
            string PassthroughLabel()
            {
                var p = GetComponent<ConstructorPassthrough>();
                if (p == null || !p.Active) return "[PT kapali]";
                return p.CameraOk ? "[PT acik]" : "[PT HATA: kamera yok]";
            }

            // Genislik artik YUZDE olarak okunmuyor: adim tam hucre, ve prop rezerve ettigi
            // dikdortgeni tam dolduruyor — yani ayak izi propun gercek boyu. "Yan yana iki tane
            // sigar mi" sorusunun cevabi metre karsiligi, %106 degil.
            float cellM = Session.Grid != null ? Session.Grid.CellSize : RoomGrid.DefaultCellSize;

            string levelText = _placeLevel > 0 || _heightMode || _onWall
                ? $"   kat {_placeLevel}/{Session.MaxLevel} ({_placeLevel * Session.Layout.levelHeight:0.00} m)"
                : "";

            _status.text =
                (_onWall ? "[DUVAR]  " : _heightMode ? "[YUKSEKLIK MODU]  " : "") +
                $"[{cat}] {(def != null ? def.displayName : "-")}   {_propIndex + 1}/{list.Count}\n" +
                $"donus {_placeRot * MapLayout.RotationStepDegrees:0}   boy %{_heightPct}{levelText}\n" +
                $"ayak izi {_rect.width}x{_rect.height} hucre " +
                $"({_rect.width * cellM:0.00} x {_rect.height * cellM:0.00} m)\n" +
                $"{cursor}\n" +
                $"yerlesen: {Session.PlacedCount}   nisan: " +
                (aimFromEye ? "GOZ-EL" : "KUMANDA") + $" {AimTrim:+0;-0;0}" +
                (Space != null ? "  [MAKET]" : "") + "  " + PassthroughLabel();
        }

        /// <summary>
        /// PC-only debug HUD: this whole layer is meant to be exercised in the editor before it
        /// ever reaches a headset. Mobile returns early — IMGUI draws nothing in a headset but
        /// still costs layout every frame (the lesson already recorded in LanBootstrap).
        /// </summary>
        void OnGUI()
        {
            if (Application.isMobilePlatform) return;

            // Kapaliyken tek satir: bu panel her Play oturumunda ciziliyor, constructor'la
            // ilgilenmeyen bir testin ekranini kaplamasin.
            if (!BuildMode || Session == null || !Session.IsActive)
            {
                bool waiting = _wantBuildMode;
                GUILayout.BeginArea(new Rect(Screen.width - 300f, 20f, 280f, waiting ? 70f : 26f), GUI.skin.box);
                GUILayout.Label("[B] Constructor" + (waiting ? "  — BEKLIYOR" : ""));
                if (waiting && _waitingForCalibration) GUILayout.Label("KALIBRASYON bekleniyor (A/B + tetik)");
                else if (waiting && Session != null) GUILayout.Label(Session.NotStartedReason);
                GUILayout.EndArea();
                return;
            }

            var def = SelectedProp();
            GUILayout.BeginArea(new Rect(Screen.width - 340f, 20f, 320f, 250f), GUI.skin.box);
            GUILayout.Label("CONSTRUCTOR — INSA MODU ACIK  [B kapatir]");
            if (PlacementLocked)
                GUILayout.Label("KOYMA KILITLI [P] — hayalet ve isin gizli");
            GUILayout.Label("Kategori: " + ConstructorPaletteUI.CategoryName(
                                _palette != null ? _palette.ActiveCategory : PropCategory.Cover) +
                            "   [C basili tut + ok tuslari]" + (_palette != null && _palette.IsOpen ? "  <ACIK>" : ""));
            GUILayout.Label("Prop: " + (def != null ? def.displayName : "-") +
                            "  (" + (_propIndex + 1) + "/" + CurrentList().Count + ")   [Z/X]");
            float cellM = Session != null && Session.Grid != null
                ? Session.Grid.CellSize : RoomGrid.DefaultCellSize;
            GUILayout.Label($"Donus: {_placeRot * MapLayout.RotationStepDegrees:0}°  [R]   Boy: %{_heightPct}  [+/-]");
            GUILayout.Label($"Kat: {_placeLevel}/{(Session != null && Session.IsActive ? Session.MaxLevel : 0)}" +
                            $"  ({(Session != null && Session.IsActive ? _placeLevel * Session.Layout.levelHeight : 0f):0.00} m)" +
                            "   [PageUp/PageDown]" + (_heightMode ? "   <YUKSEKLIK MODU>" : "") +
                            (_onWall ? "   <DUVARDA — aci ve kat isindan geliyor>" : ""));
            GUILayout.Label($"Ayak izi: {_rect.width}x{_rect.height} hucre " +
                            $"({_rect.width * cellM:0.00} x {_rect.height * cellM:0.00} m)   " +
                            "En: [ / ] (birer hucre)");
            if (_hasCursor)
                GUILayout.Label($"Hucre: ({_minCell.x},{_minCell.y})   " + (_valid ? "UYGUN" : "UYGUN DEGIL") +
                                (_snapped ? "   [komsuya YAPISTI]" : ""));
            else
                GUILayout.Label("IMLEC YOK — " + (_cursorProblem ?? "sebep bilinmiyor"));
            GUILayout.Label($"Yerlestirilen: {Session.PlacedCount}");
            GUILayout.Label($"Sol tik = koy   F = sil   U = geri al ({Session.UndoCount})   K = kaydet");
            GUILayout.EndArea();
        }
    }
}
