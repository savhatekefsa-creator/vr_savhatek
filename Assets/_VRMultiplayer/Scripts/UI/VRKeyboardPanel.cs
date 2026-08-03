using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Dunya-uzayi QWERTY klavye: baslik seridi + isim alani + tus izgarasi + eylem butonlari.
    /// <see cref="VRPointer"/> ile surulur (lazer + tetik).
    ///
    /// NEDEN KENDI KLAVYEMIZ: bu projede XR Interaction Toolkit ve Meta Core SDK yok, yalnizca
    /// com.unity.xr.openxr + meta-openxr var. Bu kurulumda Quest'in sistem klavyesi (OVR overlay
    /// keyboard) cagirilamiyor, <c>TouchScreenKeyboard</c> de acilmiyor.
    ///
    /// TUS TAKIMI BILEREK ASCII (A-Z, 0-9, bosluk): ag alani <c>FixedString32Bytes</c> ve
    /// Turkce harf 2 bayt yer kapliyor; ayrica izgara sadelesiyor. Bkz. <see cref="PlayerName"/>.
    ///
    /// GORSEL DIL: teal vurgu + koyu cukur yuzeyler, <see cref="WatchScreenUI"/> kol saati
    /// ekraniyla ayni palet — oyuncu iki arayuzu de ayni cihazin parcasi gibi okusun. Derinlik
    /// gercek golge olmadan uretiliyor: her tusun ARKASINDA bir tik buyuk koyu "bevel" quad'i
    /// var, panelin arkasinda ince bir vurgu cercevesi. VR'da bu iki katman duz renkli
    /// dikdortgenleri "buton" gibi okutmaya yetiyor.
    ///
    /// OLCU SOZLESMESI: panel kokunun olcegi 1, her sey METRE. <see cref="VRPointer"/> carpma
    /// noktasini bu kokun yerel duzleminde veriyor, tus aramasi dogrudan metre uzerinden.
    /// </summary>
    public class VRKeyboardPanel : MonoBehaviour
    {
        // --- eylem kimlikleri (owner bunlari dinler) ---
        public const string ActionConfirm = "confirm";
        public const string ActionClear   = "clear";
        public const string ActionRandom  = "random";

        /// <summary>Eylem tusuna basildi (yukaridaki sabitlerden biri).</summary>
        public event Action<string> ActionPressed;
        /// <summary>Yazi degisti (harf/silme/temizleme sonrasi).</summary>
        public event Action<string> TextChanged;

        public int maxLength = 16;

        // ------------------------------------------------------------------ olculer (metre)
        const float KeyW = 0.046f, KeyH = 0.046f, Gap = 0.007f;
        const float Pitch = KeyW + Gap;          // 0.053
        const float WideW = KeyW * 2f + Gap;     // 0.099 — SIL / harf kilidi / bosluk
        const float Bevel = 0.0035f;             // tus cevresindeki koyu cerceve kalinligi

        const float PanelW = 0.62f, PanelH = 0.48f, PanelCenterY = 0.005f;
        const float BorderPad = 0.005f;

        const float HeaderY = 0.212f, HeaderH = 0.056f;
        const float HeaderLineY = 0.180f;
        const float FieldY = 0.120f, FieldW = 0.44f, FieldH = 0.060f;
        const float FieldLineY = 0.086f;

        const float RowDigits = 0.030f;
        const float RowTop    = -0.023f;
        const float RowHome   = -0.076f;
        const float RowBottom = -0.129f;
        const float RowAction = -0.194f;

        // Z katmanlari: oyuncu -Z tarafinda durur, yani KUCUK z = ONDE. Panel 1.4 m'de
        // oldugundan 1 mm'lik farklar z-fighting yapmaz. renderQueue de veriliyor ki saydam
        // siralamasi kameranin mesafe tahminine degil bize bagli olsun.
        const float ZBorder = 0.006f, ZPanel = 0.005f, ZBevel = 0.004f,
                    ZFace = 0.003f, ZAccent = 0.002f, ZHover = 0.0015f, ZText = 0f;
        const int QBorder = 2998, QPanel = 3000, QBevel = 3001,
                  QFace = 3002, QAccent = 3003, QHover = 3004, QText = 3005;

        // ------------------------------------------------------------------ palet
        // Kol saati ekraniyla ayni teal dil (bkz. WatchScreenUI).
        static readonly Color Accent    = new Color(0.40f, 0.80f, 0.72f);
        static readonly Color Border    = new Color(0.40f, 0.80f, 0.72f, 0.28f);
        static readonly Color PanelBg   = new Color(0.035f, 0.055f, 0.060f, 0.93f);
        static readonly Color HeaderBg  = new Color(0.020f, 0.035f, 0.040f, 0.96f);
        static readonly Color KeyFace   = new Color(0.105f, 0.140f, 0.150f, 0.96f);
        static readonly Color KeyBevel  = new Color(0.015f, 0.030f, 0.035f, 0.96f);
        static readonly Color KeyHover  = new Color(0.30f, 0.72f, 0.66f, 0.95f);
        static readonly Color KeyText   = new Color(0.90f, 0.96f, 0.95f, 1f);
        static readonly Color FieldBg   = new Color(0.012f, 0.026f, 0.030f, 0.98f);
        static readonly Color FieldText = new Color(0.62f, 0.97f, 0.88f, 1f);
        static readonly Color TitleCol  = new Color(0.88f, 0.95f, 0.94f, 1f);
        static readonly Color Muted     = new Color(0.42f, 0.56f, 0.55f, 1f);
        static readonly Color ConfirmBg = new Color(0.09f, 0.40f, 0.36f, 0.96f);
        static readonly Color RandomBg  = new Color(0.075f, 0.20f, 0.26f, 0.96f);
        static readonly Color ClearBg   = new Color(0.26f, 0.11f, 0.11f, 0.96f);
        static readonly Color Warn      = new Color(0.95f, 0.45f, 0.35f, 1f);

        // ------------------------------------------------------------------ durum
        class Key
        {
            public Vector2 center, size;
            public char ch;             // '\0' = harf tusu degil
            public string action;       // null = harf tusu
            public TextMesh label;
            public bool letter;         // buyuk/kucuk degisiminden etkilenir mi
            public bool caseToggle;
        }

        readonly List<Key> _keys = new List<Key>();
        readonly StringBuilder _sb = new StringBuilder();

        Transform _highlight;
        TextMesh _fieldText, _titleText, _subtitleText, _counterText;
        Key _caseKey;
        int _hover = -1;
        bool _upper = true;

        // SIL basili tutulunca tekrar: 0.45 sn sonra saniyede 10 karakter.
        float _repeatAt;
        const float RepeatDelay = 0.45f, RepeatInterval = 0.1f;

        public string Text => _sb.ToString();

        public void SetTitle(string s, bool warning = false)
        {
            if (_titleText == null) return;
            _titleText.text = s;
            _titleText.color = warning ? Warn : TitleCol;
        }

        public void SetSubtitle(string s)
        {
            if (_subtitleText != null) _subtitleText.text = s;
        }

        public void SetText(string s)
        {
            _sb.Clear();
            if (!string.IsNullOrEmpty(s))
                _sb.Append(s.Length > maxLength ? s.Substring(0, maxLength) : s);
            RefreshField();
            TextChanged?.Invoke(Text);
        }

        // ------------------------------------------------------------------ kurulum

        void Awake()
        {
            // Vurgu cercevesi: panelin bir tik disinda, panelin ARKASINDA. VR'da duz bir
            // dikdortgeni "yuzey" gibi okutan en ucuz ipucu.
            Rect("Border", new Vector2(0f, PanelCenterY),
                 new Vector2(PanelW + BorderPad * 2f, PanelH + BorderPad * 2f), Border, ZBorder, QBorder);
            Rect("Panel", new Vector2(0f, PanelCenterY), new Vector2(PanelW, PanelH), PanelBg, ZPanel, QPanel);

            // Baslik seridi + altindaki vurgu cizgisi
            Rect("HeaderBar", new Vector2(0f, HeaderY), new Vector2(PanelW, HeaderH), HeaderBg, ZBevel, QBevel);
            Rect("HeaderLine", new Vector2(0f, HeaderLineY), new Vector2(PanelW * 0.94f, 0.0025f),
                 Accent, ZAccent, QAccent);

            _titleText = UITheme.MakeText(transform, "ADINI SEC", TitleCol, 0.030f, TextAnchor.MiddleCenter, QText);
            _titleText.transform.localPosition = new Vector3(0f, HeaderY + 0.007f, ZText);

            _subtitleText = UITheme.MakeText(transform, "", Muted, 0.0145f, TextAnchor.MiddleCenter, QText);
            _subtitleText.transform.localPosition = new Vector3(0f, HeaderY - 0.019f, ZText);

            // Isim alani: cukur zemin + altinda vurgu cizgisi (kutu yerine "yazi satiri" hissi)
            Rect("FieldBg", new Vector2(0f, FieldY), new Vector2(FieldW, FieldH), FieldBg, ZBevel, QBevel);
            Rect("FieldLine", new Vector2(0f, FieldLineY), new Vector2(FieldW, 0.002f), Accent, ZAccent, QAccent);

            _fieldText = UITheme.MakeText(transform, "", FieldText, 0.036f, TextAnchor.MiddleCenter, QText);
            _fieldText.transform.localPosition = new Vector3(0f, FieldY, ZText);

            _counterText = UITheme.MakeText(transform, "", Muted, 0.0145f, TextAnchor.MiddleRight, QText);
            _counterText.transform.localPosition = new Vector3(PanelW * 0.5f - 0.016f, FieldLineY - 0.014f, ZText);

            // Vurgu TEK bir quad ve tasiniyor. Alternatif (uzerine gelinen tusun malzemesini
            // her kare boyamak) her vurgu degisiminde malzeme yazar, SRP batcher'i bozar ve
            // "eski tusu geri boyamayi unutma" hatasina acik olurdu.
            _highlight = UITheme.MakeQuad(transform, "Hover", KeyHover, QHover);
            _highlight.gameObject.SetActive(false);

            BuildRow("1234567890", RowDigits, letters: false);
            BuildRow("QWERTYUIOP", RowTop, letters: true);
            BuildHomeRow();
            BuildBottomRow();
            BuildActionRow();

            RefreshField();
            RefreshCaseKey();
        }

        Transform Rect(string name, Vector2 center, Vector2 size, Color color, float z, int queue)
        {
            var t = UITheme.MakeQuad(transform, name, color, queue);
            t.localPosition = new Vector3(center.x, center.y, z);
            t.localScale = new Vector3(size.x, size.y, 1f);
            return t;
        }

        void BuildRow(string chars, float y, bool letters)
        {
            float total = chars.Length * Pitch - Gap;
            float x = -total * 0.5f + KeyW * 0.5f;
            foreach (char c in chars)
            {
                AddKey(new Vector2(x, y), new Vector2(KeyW, KeyH), c.ToString(), KeyFace, c, null, letters);
                x += Pitch;
            }
        }

        void BuildHomeRow()
        {
            const string chars = "ASDFGHJKL";
            float total = chars.Length * Pitch + WideW;   // + SIL
            float x = -total * 0.5f + KeyW * 0.5f;
            foreach (char c in chars)
            {
                AddKey(new Vector2(x, RowHome), new Vector2(KeyW, KeyH), c.ToString(), KeyFace, c, null, true);
                x += Pitch;
            }
            x += (WideW - KeyW) * 0.5f;
            AddKey(new Vector2(x, RowHome), new Vector2(WideW, KeyH), "SIL", KeyFace, '\0', "backspace", false, 0.020f);
        }

        void BuildBottomRow()
        {
            const string chars = "ZXCVBNM";
            float total = WideW + Gap + chars.Length * Pitch + WideW;
            float x = -total * 0.5f + WideW * 0.5f;

            _caseKey = AddKey(new Vector2(x, RowBottom), new Vector2(WideW, KeyH), "AB", KeyFace, '\0', "case", false, 0.020f);
            _caseKey.caseToggle = true;
            x += WideW * 0.5f + Gap + KeyW * 0.5f;

            foreach (char c in chars)
            {
                AddKey(new Vector2(x, RowBottom), new Vector2(KeyW, KeyH), c.ToString(), KeyFace, c, null, true);
                x += Pitch;
            }

            x += (WideW - KeyW) * 0.5f;
            AddKey(new Vector2(x, RowBottom), new Vector2(WideW, KeyH), "BOSLUK", KeyFace, ' ', null, false, 0.0145f);
        }

        void BuildActionRow()
        {
            const float RandomW = 0.23f, ClearW = 0.14f, ConfirmW = 0.19f, H = 0.054f, G = 0.012f;
            float total = RandomW + G + ClearW + G + ConfirmW;
            float x = -total * 0.5f;

            x += RandomW * 0.5f;
            AddKey(new Vector2(x, RowAction), new Vector2(RandomW, H), "RASTGELE ISIM", RandomBg, '\0', ActionRandom, false, 0.020f);
            x += RandomW * 0.5f + G + ClearW * 0.5f;
            AddKey(new Vector2(x, RowAction), new Vector2(ClearW, H), "TEMIZLE", ClearBg, '\0', ActionClear, false, 0.020f);
            x += ClearW * 0.5f + G + ConfirmW * 0.5f;
            AddKey(new Vector2(x, RowAction), new Vector2(ConfirmW, H), "ONAYLA", ConfirmBg, '\0', ActionConfirm, false, 0.024f);
        }

        Key AddKey(Vector2 center, Vector2 size, string label, Color face, char ch, string action,
                   bool letter, float textHeight = 0.026f)
        {
            // Bevel ONCE (arkada): tusun cevresinde ince koyu bir cerceve birakir, tusler
            // birbirine yapisik duz lekeler yerine ayri ayri kabartma gibi okunur.
            Rect("B_" + label, center, size + Vector2.one * Bevel * 2f, KeyBevel, ZBevel, QBevel);
            Rect("K_" + label, center, size, face, ZFace, QFace);

            var tm = UITheme.MakeText(transform, label, KeyText, textHeight, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(center.x, center.y, ZText);

            var k = new Key
            {
                center = center, size = size, ch = ch, action = action,
                label = tm, letter = letter,
            };
            _keys.Add(k);
            return k;
        }

        // ------------------------------------------------------------------ surus

        /// <summary>Sahibi her kare cagirir. Imlec paneli kesiyorsa vurgu ve tiklama islenir.</summary>
        public void Tick(VRPointer pointer)
        {
            RefreshCaret();

            if (pointer == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            _hover = hit ? FindKey(local) : -1;
            ApplyHighlight();

            if (_hover < 0) { _repeatAt = 0f; return; }

            var key = _keys[_hover];

            if (pointer.ClickDown)
            {
                Press(key);
                // Tekrar YALNIZCA SIL'de: harf tusunda tutmak ayni harfi yagdirirdi.
                _repeatAt = key.action == "backspace" ? Time.unscaledTime + RepeatDelay : 0f;
            }
            else if (_repeatAt > 0f && pointer.ClickHeld && key.action == "backspace")
            {
                if (Time.unscaledTime >= _repeatAt)
                {
                    Press(key);
                    _repeatAt = Time.unscaledTime + RepeatInterval;
                }
            }
            else if (!pointer.ClickHeld)
            {
                _repeatAt = 0f;
            }
        }

        int FindKey(Vector2 p)
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                var k = _keys[i];
                if (Mathf.Abs(p.x - k.center.x) <= k.size.x * 0.5f &&
                    Mathf.Abs(p.y - k.center.y) <= k.size.y * 0.5f)
                    return i;
            }
            return -1;
        }

        void ApplyHighlight()
        {
            bool on = _hover >= 0;
            if (_highlight.gameObject.activeSelf != on) _highlight.gameObject.SetActive(on);
            if (!on) return;

            var k = _keys[_hover];
            _highlight.localPosition = new Vector3(k.center.x, k.center.y, ZHover);
            _highlight.localScale = new Vector3(k.size.x, k.size.y, 1f);
        }

        void Press(Key k)
        {
            VRPointer.Haptic();

            if (k.action == null)
            {
                if (_sb.Length >= maxLength) return;         // sinirda sessizce yut
                _sb.Append(_upper ? char.ToUpperInvariant(k.ch) : char.ToLowerInvariant(k.ch));
                RefreshField();
                TextChanged?.Invoke(Text);
                return;
            }

            switch (k.action)
            {
                case "backspace":
                    if (_sb.Length == 0) return;
                    _sb.Length--;
                    RefreshField();
                    TextChanged?.Invoke(Text);
                    return;

                case "case":
                    _upper = !_upper;
                    RefreshLetterLabels();
                    RefreshCaseKey();
                    return;

                default:
                    ActionPressed?.Invoke(k.action);
                    return;
            }
        }

        void RefreshLetterLabels()
        {
            foreach (var k in _keys)
            {
                if (!k.letter || k.label == null) continue;
                k.label.text = _upper
                    ? char.ToUpperInvariant(k.ch).ToString()
                    : char.ToLowerInvariant(k.ch).ToString();
            }
        }

        // Kilit tusu HANGI modda oldugunu kendi yaziyla gosterir; ayri bir gosterge gerekmez.
        void RefreshCaseKey()
        {
            if (_caseKey?.label == null) return;
            _caseKey.label.text = _upper ? "AB" : "ab";
            _caseKey.label.color = _upper ? KeyText : Accent;
        }

        // Yanip sonen imlec. Genislik SABIT kalsin diye sonuk fazda BOSLUK yaziliyor —
        // yoksa yazi her yarim saniyede saga sola oynardi.
        void RefreshCaret()
        {
            if (_fieldText == null) return;
            bool on = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
            _fieldText.text = _sb.ToString() + (on ? "|" : " ");
        }

        void RefreshField()
        {
            RefreshCaret();
            if (_fieldText != null)
                _fieldText.color = _sb.Length == 0 ? Muted : FieldText;

            if (_counterText != null)
            {
                _counterText.text = _sb.Length + "/" + maxLength;
                _counterText.color = _sb.Length >= maxLength ? Warn : Muted;
            }
        }
    }
}
