using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
// UnityEngine.XR "using" YOK — InputSystem ile InputDevice adi cakisiyor (WeaponBeltUI'daki
// ayni ders). XR tipleri tam adiyla yaziliyor.

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Build-mode prop palette: a radial CATEGORY wheel, deliberately the same idiom as the
    /// weapon wheel — hold, point, release. Muscle memory carries over.
    ///
    /// WHY CATEGORY AND NOT PROP: 44 props do not fit on a wheel, and a wheel with 44 slices is
    /// worse than a list. The wheel picks the category (a handful of slices); the thumbstick
    /// then walks the ~10 props inside it. Two cheap gestures beat one crowded one.
    ///
    /// While the wheel is open it OWNS the thumbstick — <see cref="ConstructorPlacer"/> checks
    /// <see cref="IsOpen"/> and keeps its hands off, so pointing at a slice never also rotates
    /// the ghost underneath.
    /// </summary>
    public class ConstructorPaletteUI : MonoBehaviour
    {
        [Header("Cark yerlesimi")]
        [Tooltip("Carkin kafaya gore konumu (m). Tam karsida: secim yaparken gorusu kapatmasi sorun degil.")]
        public Vector3 viewOffset = new Vector3(0f, 0f, 0.95f);
        public float discRadius = 0.30f;
        public float centerRadius = 0.08f;
        public float sliceGapDegrees = 5f;
        public float previewRadius = 0.185f;
        [Tooltip("Onizlemenin en buyuk kenarinin hedef boyu (m) — her prop ayni buyuklukte gorunsun.")]
        public float previewSize = 0.085f;
        public float selectedBoost = 1.3f;
        public float labelRadius = 0.26f;
        public float labelSize = 0.008f;

        [Header("Giris")]
        [Range(0.1f, 0.9f)] public float pointDeadzone = 0.4f;

        [Header("Renkler (silah carkiyla ayni palet)")]
        public Color sliceColor = new Color(0.06f, 0.06f, 0.08f, 0.80f);
        public Color sliceSelectedColor = new Color(0.10f, 0.47f, 0.53f, 0.90f);
        public Color centerColor = new Color(0.03f, 0.03f, 0.04f, 0.88f);

        public bool IsOpen { get; private set; }

        /// <summary>The selected slice. Empty = the "DIGER" slice (props with no palette).</summary>
        public string ActivePaletteId { get; private set; } = "";

        int _pointed = -1;
        bool _pointedByStick;

        Transform _wheel;
        Material[] _sliceMats;
        TextMesh[] _labels;
        GameObject[] _previews;
        Vector3[] _previewScales;
        int _builtSliceCount = -1;
        string _builtSignature;

        ConstructorSession Session => ConstructorSession.Instance;

        // Placer ve palet birbirini ariyor; hangisi once eklenirse eklensin biri digerini
        // Awake'te bulamaz. Bu tarafi TEMBEL cozuyoruz, boylece ekleme sirasi onemsiz kalir.
        ConstructorPlacer _placer;
        ConstructorPlacer Placer
        {
            get
            {
                if (_placer == null) _placer = GetComponent<ConstructorPlacer>();
                return _placer;
            }
        }

        void OnDisable() => CloseWheel();

        // ------------------------------------------------------------- loop

        void Update()
        {
            bool canOpen = Placer != null && Placer.BuildMode &&
                           Session != null && Session.IsActive && Session.Palettes.Count > 0;
            bool held = canOpen && PaletteHeld();

            if (!held)
            {
                // Grip/C birakildi: klavye akisinin onayi burada islenir (stick hic
                // kullanilmadiysa "merkeze donus" olayi hic gerceklesmez).
                if (IsOpen) CommitAndClose();
                return;
            }

            var sets = Session.Palettes;
            if (!IsOpen) OpenWheel(sets);
            if (_wheel == null) return;

            Vector2 stick = RightStick();
            float mag = stick.magnitude;
            if (mag >= pointDeadzone)
            {
                _pointed = NearestSlice(stick, sets.Count);
                _pointedByStick = true;
            }

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame) StepPointed(-1, sets.Count);
                if (kb.rightArrowKey.wasPressedThisFrame) StepPointed(1, sets.Count);
            }
#endif

            if (_pointed >= sets.Count) _pointed = sets.Count - 1;

            Layout(sets);

            // Silah carkiyla ayni onay: stick MERKEZE DONUNCE islenir. Vurgu aninda islenseydi
            // stick gezerken ustunden gecilen her palet secilirdi.
            if (_pointedByStick && mag < pointDeadzone * 0.6f) CommitAndClose();
        }

        void StepPointed(int delta, int count)
        {
            _pointed = _pointed < 0 ? 0 : ((_pointed + delta) % count + count) % count;
            _pointedByStick = false;   // klavyede onay birakinca gelir
        }

        // ------------------------------------------------------------- open / close

        void OpenWheel(IReadOnlyList<string> sets)
        {
            EnsureWheel(sets);
            if (_wheel == null) return;

            _pointed = IndexOf(sets, ActivePaletteId);   // acilista mevcut palet vurgulu
            if (_pointed < 0) _pointed = 0;
            _pointedByStick = false;
            IsOpen = true;
            _wheel.gameObject.SetActive(true);
        }

        /// <summary>
        /// Selects a palette without going through the wheel. The watch menu (step 9) and any
        /// scripted flow drive selection through here, so "how a palette becomes active" stays
        /// one code path no matter which surface asked for it.
        /// </summary>
        public void SetPalette(string paletteId)
        {
            paletteId = paletteId ?? "";
            if (ActivePaletteId == paletteId) return;
            ActivePaletteId = paletteId;
            if (Session != null) Session.SetPalette(paletteId);
            if (Placer != null) Placer.OnCategoryChanged();
        }

        void CommitAndClose()
        {
            var sets = Session != null && Session.IsActive ? Session.Palettes : null;
            if (sets != null && _pointed >= 0 && _pointed < sets.Count) SetPalette(sets[_pointed]);
            CloseWheel();
        }

        void CloseWheel()
        {
            IsOpen = false;
            _pointed = -1;
            _pointedByStick = false;
            if (_wheel != null) _wheel.gameObject.SetActive(false);
        }

        static int IndexOf(IReadOnlyList<string> list, string id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == id) return i;
            return -1;
        }

        static int NearestSlice(Vector2 stick, int count)
        {
            float a = Mathf.Atan2(stick.y, stick.x) * Mathf.Rad2Deg;
            int best = 0; float bestD = 999f;
            for (int i = 0; i < count; i++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(a, SliceAngle(i, count)));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Ilk dilim TEPEDE, sonrakiler saat yonunde — kac dilim olursa olsun.</summary>
        static float SliceAngle(int index, int count) => 90f - index * (360f / count);

        static Vector3 AngleToPos(float deg, float r)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad) * r, Mathf.Sin(rad) * r, -0.01f);
        }

        // ------------------------------------------------------------- visuals

        void Layout(IReadOnlyList<string> sets)
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;
            _wheel.SetPositionAndRotation(head.position + head.rotation * viewOffset, head.rotation);

            for (int i = 0; i < sets.Count && i < _sliceMats.Length; i++)
            {
                bool sel = _pointed == i;

                if (_sliceMats[i] != null)
                    UITheme.SetMaterialColor(_sliceMats[i], sel ? sliceSelectedColor : sliceColor);

                if (_labels[i] != null)
                    _labels[i].text = Session.Library.PaletteName(sets[i]) + "\n" +
                                      Session.PlaceableIn(sets[i]).Count;

                if (_previews[i] == null) continue;
                float rad = SliceAngle(i, sets.Count) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
                _previews[i].transform.position = _wheel.TransformPoint(dir * previewRadius + Vector3.back * 0.05f);
                _previews[i].transform.rotation = _wheel.rotation * Quaternion.Euler(20f, 35f, 0f);
                _previews[i].transform.localScale = _previewScales[i] * (sel ? selectedBoost : 1f);
            }
        }

        void EnsureWheel(IReadOnlyList<string> sets)
        {
            // Kutuphane degisirse (menu 25/31) dilimler degisebilir — o zaman carki bastan or.
            //
            // IMZA dilim SAYISI degil, dilimlerin KIMLIKLERI: bir paleti silip yerine baskasini
            // koymak sayiyi ayni birakir, ve yalnizca sayiya bakan bir kontrol o durumda carki
            // yenilemez — dilimlerin uzerinde silinmis paletin onizlemeleri durmaya devam
            // ederdi.
            string sig = string.Join("|", sets as string[] ?? new List<string>(sets).ToArray());
            if (_wheel != null && _builtSliceCount == sets.Count && _builtSignature == sig) return;
            if (_wheel != null) Destroy(_wheel.gameObject);

            _wheel = new GameObject("ConstructorPaletteWheel").transform;
            _wheel.SetParent(transform, false);

            int n = sets.Count;
            _sliceMats = new Material[n];
            _labels = new TextMesh[n];
            _previews = new GameObject[n];
            _previewScales = new Vector3[n];

            float span = 360f / n;
            float half = Mathf.Max(2f, (span - sliceGapDegrees) * 0.5f);

            for (int i = 0; i < n; i++)
            {
                float mid = SliceAngle(i, n);

                var go = new GameObject("Slice_" + Session.Library.PaletteName(sets[i]));
                go.transform.SetParent(_wheel, false);
                go.AddComponent<MeshFilter>().sharedMesh =
                    UITheme.ArcMesh(centerRadius + 0.012f, discRadius, mid - half, mid + half, 24);
                _sliceMats[i] = UITheme.CreateOverlayMaterial(sliceColor, 3000);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _sliceMats[i];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                _labels[i] = UITheme.CreateLabel(_wheel, Session.Library.PaletteName(sets[i]),
                    AngleToPos(mid, labelRadius), labelSize, 3002);

                _previews[i] = MakePreview(sets[i]);
                _previewScales[i] = _previews[i] != null ? _previews[i].transform.localScale : Vector3.one;
            }

            var center = new GameObject("Center");
            center.transform.SetParent(_wheel, false);
            center.AddComponent<MeshFilter>().sharedMesh = UITheme.ArcMesh(0.001f, centerRadius, 0f, 360f, 48);
            var cmr = center.AddComponent<MeshRenderer>();
            cmr.sharedMaterial = UITheme.CreateOverlayMaterial(centerColor, 3001);
            cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _builtSliceCount = n;
            _builtSignature = sig;
            _wheel.gameObject.SetActive(IsOpen);
        }

        /// <summary>
        /// A shrunk, collider-free stand-in of the palette's first prop.
        ///
        /// Every preview is normalized to the SAME on-screen size: a flower and a 1.5 m barrier
        /// are otherwise unreadable side by side — one fills its slice, the other is a dot.
        /// </summary>
        GameObject MakePreview(string paletteId)
        {
            var list = Session.PlaceableIn(paletteId);
            if (list.Count == 0) return null;

            var prefab = list[0].Resolve();
            if (prefab == null) return null;

            var go = Instantiate(prefab, _wheel);
            go.name = "Preview_" + Session.Library.PaletteName(paletteId);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            float largest = 0f;
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
                largest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                foreach (var r in renderers)
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            go.transform.localScale = Vector3.one * (largest > 0.001f ? previewSize / largest : previewSize);
            return go;
        }

        public static string CategoryName(PropCategory c)
        {
            switch (c)
            {
                case PropCategory.Cover:  return "SIPER";
                case PropCategory.Wall:   return "DUVAR";
                case PropCategory.Nature: return "DOGA";
                case PropCategory.Ground: return "ZEMIN";
                case PropCategory.Spawn:  return "DOGUS";
                case PropCategory.Target: return "HEDEF";
                default:                  return c.ToString().ToUpperInvariant();
            }
        }

        // ------------------------------------------------------------- input

        /// <summary>VR: sag GRIP. PC: C tusu. Basili tutuldugu surece cark yasar.</summary>
        bool PaletteHeld()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (dev.isValid && XRButtons.HeldWithAxisFallback(dev,
                    UnityEngine.XR.CommonUsages.gripButton, UnityEngine.XR.CommonUsages.grip, 0.5f))
                return true;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.cKey.isPressed;
#else
            return false;
#endif
        }

        Vector2 RightStick()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (dev.isValid &&
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                return axis;
            return Vector2.zero;
        }
    }
}
