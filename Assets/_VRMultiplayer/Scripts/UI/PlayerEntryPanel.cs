using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// OYUNCU GIRISI ekrani: isim klavyesi + takim secimi + OYUNA BASLA. Oyuncunun oyuna
    /// girmeden once gordugu tek ekran; <see cref="VRPointer"/> ile (lazer + tetik) surulur.
    ///
    /// ISIM DE TAKIM DA ZORUNLU — ikisi tamamlanmadan OYUNA BASLA pasif kalir.
    ///
    /// NEDEN KENDI KLAVYEMIZ: projede XR Interaction Toolkit ve Meta Core SDK yok, yalnizca
    /// com.unity.xr.openxr + meta-openxr var. Bu kurulumda Quest'in sistem klavyesi
    /// cagirilamiyor, <c>TouchScreenKeyboard</c> de acilmiyor.
    ///
    /// TUS TAKIMI ASCII (A-Z, 0-9, bosluk) — tasarimdaki dizilimle birebir. Ag alani
    /// <c>FixedString32Bytes</c> ve Turkce harf 2 bayt yer kapiyor; ARAYUZ yazilari Turkce
    /// (font destekliyor), YAZILAN isim ASCII.
    ///
    /// OLCU SOZLESMESI — tasarim 1280x722 mockup'indan piksel piksel alindi. Dikdortgenler
    /// tasarim pikseliyle yaziliyor, <see cref="PX"/> ile metreye ceviriliyor. Boylece yerlesim
    /// mockup'la karsilastirilabilir kaliyor: bir kutuyu kaydirmak icin tasarimdaki pikseli
    /// degistirmek yeterli. Panel kokunun olcegi 1; <see cref="VRPointer"/> carpma noktasini
    /// bu kokun yerel duzleminde METRE olarak veriyor.
    ///
    /// YAZI BOYUTLARI tasarim oraniyla DEGIL gorme acisiyla secildi: mockup'in 18 piksellik tus
    /// yazisi bu olcekte ~0.7 dereceye denk duser ve VR'da okunmaz (rahat esik ~1 derece).
    /// Yazilar buyutuldu, kutular tasarimdaki gibi birakildi.
    /// </summary>
    public class PlayerEntryPanel : MonoBehaviour
    {
        public const string ActionStart  = "start";
        public const string ActionRandom = "random";
        public const string ActionClear  = "clear";

        /// <summary>Eylem tusuna basildi (yukaridaki sabitlerden biri).</summary>
        public event Action<string> ActionPressed;
        /// <summary>Isim ya da takim degisti.</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ olcek
        /// <summary>Tasarim pikseli -> metre. 914 px icerik genisligi ~1.15 m eder; 1.4 m
        /// mesafede panel yaklasik +-23 derece yer kaplar (menu icin uygun).</summary>
        const float PX = 0.00126f;

        // Tasarim ic koordinat sistemi: mockup 1280x722, icerik alani x 183..1097 / y 95..625.
        const float OriginX = 640f, OriginY = 360f;

        static float X(float px) => (px - OriginX) * PX;
        static float Y(float px) => (OriginY - px) * PX;   // tasarimda y asagi, dunyada yukari
        static float S(float px) => px * PX;
        static Vector2 Box(float x0, float y0, float x1, float y1) =>
            new Vector2(X((x0 + x1) * 0.5f), Y((y0 + y1) * 0.5f));
        static Vector2 Dim(float x0, float y0, float x1, float y1) =>
            new Vector2(S(x1 - x0), S(y1 - y0));

        // ------------------------------------------------------------------ katmanlar
        // Overlay malzemesi derinlik testine girmedigi icin sira YALNIZCA renderQueue'dan gelir.
        //
        // QBack neden 3000 DEGIL: sahnedeki oda geometrisi (RoomPlanTemplate duvarlari, masa)
        // da saydam kuyruk 3000'de. Ayni kuyrukta saydamlar MESAFEYE gore siralandigindan
        // panelden kameraya daha yakin bir duvar, panelin zeminini uzerine cizip deliyordu —
        // tusler (3012+) durdugu icin "arkaplani delik panel" gorunuyordu.
        const int QBack = 3004, QGlow = 3008, QBorder = 3012, QFill = 3016,
                  QHover = 3020, QIcon = 3026, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f,
                    ZHover = 0.002f, ZIcon = 0.001f, ZText = 0f;

        // ------------------------------------------------------------------ palet
        // Ortak renkler UITheme'de TEK KAYNAK; olum ekrani da oradan besleniyor. Buraya
        // yeni bir ham renk yazmadan once oraya bak — iki ekranin birbirinden kaymasi
        // tam olarak renklerin kopyalanmasindan cikmisti.
        //
        // Zemin TAM OPAK: saydam birakmak passthrough'da odayi gostermeye devam ederdi ama
        // arkadaki parlak yuzeyler paneli soldurup yaziyi okunmaz yapiyordu.
        static readonly Color Backdrop   = UITheme.PanelBg;
        static readonly Color PanelEdge  = UITheme.PanelEdge;

        static readonly Color FieldFill  = UITheme.SurfaceFill;
        static readonly Color FieldEdge  = UITheme.SurfaceEdge;
        static readonly Color NameCol    = UITheme.TextPrimary;
        static readonly Color Placeholder= UITheme.TextDim;
        static readonly Color Counter    = UITheme.TextMuted;
        static readonly Color TinyLabel  = new Color(0.24f, 0.29f, 0.34f, 1f);

        static readonly Color KeyFill    = new Color(0.110f, 0.137f, 0.173f, 1f);
        static readonly Color KeyEdge    = new Color(0.063f, 0.086f, 0.118f, 1f);
        static readonly Color KeyText    = new Color(0.90f, 0.93f, 0.96f, 1f);

        static readonly Color TitleA     = UITheme.AccentCyan;
        static readonly Color TitleB     = UITheme.AccentPurple;

        static readonly Color RandomEdge = new Color(0.43f, 0.31f, 0.84f, 1f);
        static readonly Color RandomFill = new Color(0.090f, 0.071f, 0.165f, 1f);
        static readonly Color RandomText = new Color(0.73f, 0.66f, 0.96f, 1f);

        static readonly Color DelEdge    = new Color(0.72f, 0.23f, 0.30f, 1f);
        static readonly Color DelFill    = new Color(0.110f, 0.051f, 0.067f, 1f);
        static readonly Color DelText    = new Color(0.90f, 0.50f, 0.56f, 1f);

        static readonly Color RedEdge    = UITheme.TeamRedEdge;
        static readonly Color RedFill    = new Color(0.090f, 0.035f, 0.051f, 1f);
        static readonly Color RedText    = UITheme.TeamRedText;

        static readonly Color BlueEdge   = UITheme.TeamBlueEdge;
        static readonly Color BlueFill   = new Color(0.039f, 0.067f, 0.125f, 1f);
        static readonly Color BlueText   = UITheme.TeamBlueText;

        static readonly Color StartOff   = new Color(0.078f, 0.102f, 0.133f, 1f);
        static readonly Color StartOffTx = new Color(0.31f, 0.36f, 0.42f, 1f);
        static readonly Color StartOnEdge= UITheme.AccentCyan;
        static readonly Color StartOnFill= new Color(0.043f, 0.212f, 0.243f, 1f);
        static readonly Color StartOnTx  = new Color(0.62f, 0.95f, 0.93f, 1f);

        static readonly Color Hint       = UITheme.TextMuted;
        static readonly Color SectionLbl = new Color(0.42f, 0.48f, 0.53f, 1f);
        static readonly Color HoverCol   = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        // ------------------------------------------------------------------ ogeler
        class El
        {
            public Vector2 center, size;
            public float radius;
            public char ch;            // '\0' = harf tusu degil
            public string action;      // null ve ch=='\0' ve team==0 ise tiklanamaz
            public byte team;          // 0 = takim karti degil
            public TextMesh label;
            public Material fillMat, borderMat, glowMat;
        }

        readonly List<El> _els = new List<El>();
        readonly StringBuilder _sb = new StringBuilder();

        Transform _hover, _startIcon;
        MeshFilter _hoverMesh;
        TextMesh _nameText, _counterText, _hintText, _startLabel;
        Material _startFillMat, _startBorderMat, _startIconMat;
        El _redCard, _blueCard;

        int _hoverIdx = -1;
        byte _team = PlayerProfile.TeamNone;

        // SIL basili tutulunca tekrar
        float _repeatAt;
        const float RepeatDelay = 0.45f, RepeatInterval = 0.1f;

        public string Text => _sb.ToString();
        public byte SelectedTeam => _team;
        public bool Ready => PlayerProfile.IsReady(PlayerProfile.Sanitize(Text), _team);

        public void SetText(string s)
        {
            _sb.Clear();
            if (!string.IsNullOrEmpty(s))
                _sb.Append(s.Length > PlayerProfile.MaxLength
                    ? s.Substring(0, PlayerProfile.MaxLength) : s);
            RefreshName();
            Changed?.Invoke();
        }

        public void SetTeam(byte team)
        {
            _team = team;
            RefreshTeamCards();
            RefreshStart();
            Changed?.Invoke();
        }

        public void SetHint(string s, bool warning = false)
        {
            if (_hintText == null) return;
            _hintText.text = s;
            _hintText.color = warning ? RedText : Hint;
        }

        // ------------------------------------------------------------------ kurulum

        void Awake()
        {
            BuildBackdrop();
            BuildTitle();
            BuildNameField();
            BuildKeyboard();
            BuildActionRow();
            BuildTeamPanel();
            BuildStartButton();

            // Vurgu: TEK obje, mesh'i uzerine gelinen ogenin olcusune gore degistirilir.
            // UIMesh boyuta gore onbellekledigi icin isinma sonrasi tahsis yapmaz. Alternatif
            // (ogenin malzemesini her kare boyamak) "eski ogeyi geri boyamayi unutma" hatasina
            // acikti.
            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            RefreshName();
            RefreshTeamCards();
            RefreshStart();
        }

        void BuildBackdrop()
        {
            // Tasarimin dis cercevesi. Arkaplan DESENI bilerek yok (kullanici istegi):
            // passthrough'da gercek oda gorunmeye devam etsin.
            UITheme.MakeRounded(transform, "Backdrop", Box(160, 72, 1120, 648),
                Dim(160, 72, 1120, 648), S(18f), Backdrop, ZBack, QBack);
        }

        // Baslik HARF HARF ciziliyor: hem tasarimdaki genis harf araligi hem camgobegi->mor
        // gecisi icin. Tek TextMesh ile ikisi de olmazdi (harf araligi ayari ve harf basina
        // renk yok).
        void BuildTitle()
        {
            const string text = "OYUNCU GİRİŞİ";
            const float left = 478f, right = 790f, y = 110f;
            int n = text.Length;

            for (int i = 0; i < n; i++)
            {
                if (text[i] == ' ') continue;
                float t = n > 1 ? i / (float)(n - 1) : 0f;
                var tm = UITheme.MakeText(transform, text[i].ToString(),
                    Color.Lerp(TitleA, TitleB, t), 0.046f, TextAnchor.MiddleCenter, QText);
                tm.transform.localPosition =
                    new Vector3(X(Mathf.Lerp(left, right, t)), Y(y), ZText);
            }
        }

        void BuildNameField()
        {
            UITheme.MakeOutlined(transform, "Field", Box(183, 143, 1097, 233),
                Dim(183, 143, 1097, 233), S(16f), FieldEdge, FieldFill, S(2f),
                ZBorder, QBorder, QFill);

            // Sol kenardaki minik DIKEY "AD" etiketi (tasarimda oldugu gibi).
            var ad = UITheme.MakeText(transform, "AD", TinyLabel, 0.014f, TextAnchor.MiddleCenter, QText);
            ad.transform.localPosition = new Vector3(X(211f), Y(188f), ZText);
            ad.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

            _nameText = UITheme.MakeText(transform, "", NameCol, 0.048f, TextAnchor.MiddleLeft, QText);
            _nameText.transform.localPosition = new Vector3(X(232f), Y(188f), ZText);

            _counterText = UITheme.MakeText(transform, "", Counter, 0.018f, TextAnchor.MiddleRight, QText);
            _counterText.transform.localPosition = new Vector3(X(1075f), Y(188f), ZText);
        }

        // Tus izgarasi: tasarimda 58x49 tus, 70 px adim, klavye alani x 183..872.
        const float KeyW = 58f, KeyH = 49f, Pitch = 70f, KeyR = 10f;

        // Sira baslangiclari tasarimdan BIREBIR alindi (ortalanmis DEGIL): alt iki sira
        // klasik klavyedeki gibi kademeli kayiyor — ASDF bir tik, ZXCV daha fazla saga.
        void BuildKeyboard()
        {
            Row("1234567890", 214f, 272f);
            Row("QWERTYUIOP", 214f, 326f);
            Row("ASDFGHJKL",  242f, 377f);
            Row("ZXCVBNM",    288f, 429f);
        }

        /// <param name="cx0">Ilk tusun MERKEZ x'i (tasarim pikseli).</param>
        void Row(string chars, float cx0, float cy)
        {
            float cx = cx0;
            foreach (char c in chars)
            {
                AddButton(new Vector2(X(cx), Y(cy)), new Vector2(S(KeyW), S(KeyH)), S(KeyR),
                    c.ToString(), 0.030f, KeyEdge, KeyFill, KeyText, c, null);
                cx += Pitch;
            }
        }

        void BuildActionRow()
        {
            const float y0 = 468f, y1 = 517f;

            var rnd = AddButton(Box(183, y0, 326, y1), Dim(183, y0, 326, y1), S(KeyR),
                "Rastgele ad", 0.021f, RandomEdge, RandomFill, RandomText, '\0', ActionRandom);
            IconOn(rnd, UIMesh.Bolt(), RandomText, -S(52f), 0.014f, 0.022f);

            AddButton(Box(334, y0, 690, y1), Dim(334, y0, 690, y1), S(KeyR),
                "Boşluk", 0.024f, KeyEdge, KeyFill, KeyText, ' ', null);

            var del = AddButton(Box(698, y0, 775, y1), Dim(698, y0, 775, y1), S(KeyR),
                "Sil", 0.021f, DelEdge, DelFill, DelText, '\0', "backspace");
            IconOn(del, UIMesh.Backspace(), DelText, -S(20f), 0.018f, 0.014f);

            AddButton(Box(783, y0, 872, y1), Dim(783, y0, 872, y1), S(KeyR),
                "Temizle", 0.021f, KeyEdge, KeyFill, KeyText, '\0', ActionClear);
        }

        void BuildTeamPanel()
        {
            UITheme.MakeOutlined(transform, "TeamPanel", Box(884, 248, 1097, 517),
                Dim(884, 248, 1097, 517), S(14f), PanelEdge, Backdrop, S(1.5f),
                ZBorder, QBorder, QFill);

            var lbl = UITheme.MakeText(transform, "TAKIM SEÇ", SectionLbl, 0.016f,
                TextAnchor.MiddleCenter, QText);
            lbl.transform.localPosition = new Vector3(X(990f), Y(268f), ZText);

            _redCard = AddTeamCard(900, 285, 1081, 387, "KIZIL", RedEdge, RedFill, RedText,
                PlayerProfile.TeamRed);
            _blueCard = AddTeamCard(900, 399, 1081, 501, "MAVİ", BlueEdge, BlueFill, BlueText,
                PlayerProfile.TeamBlue);
        }

        El AddTeamCard(float x0, float y0, float x1, float y1, string title,
                       Color edge, Color fill, Color text, byte team)
        {
            Vector2 c = Box(x0, y0, x1, y1), size = Dim(x0, y0, x1, y1);

            // Secilince beliren yumusak hale (tasarimdaki isima).
            var glow = UITheme.MakeRounded(transform, title + " Glow", c,
                size + Vector2.one * S(10f), S(16f),
                new Color(edge.r, edge.g, edge.b, 0f), ZBorder + 0.001f, QGlow);
            var border = UITheme.MakeRounded(transform, title + " Border", c, size, S(10f),
                edge, ZBorder, QBorder);
            var body = UITheme.MakeRounded(transform, title + " Fill", c,
                size - Vector2.one * S(3f), S(8f), fill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, title, text, 0.034f, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(c.x, c.y + S(8f), ZText);

            var sub = UITheme.MakeText(transform, "Takım",
                new Color(text.r, text.g, text.b, 0.75f), 0.016f, TextAnchor.MiddleCenter, QText);
            sub.transform.localPosition = new Vector3(c.x, c.y - S(22f), ZText);

            var el = new El
            {
                center = c, size = size, radius = S(10f), team = team, label = tm,
                fillMat = body.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
                glowMat = glow.GetComponent<MeshRenderer>().sharedMaterial,
            };
            _els.Add(el);
            return el;
        }

        void BuildStartButton()
        {
            Vector2 c = Box(183, 533, 1097, 594), size = Dim(183, 533, 1097, 594);

            var border = UITheme.MakeRounded(transform, "Start Border", c, size, S(12f),
                StartOff, ZBorder, QBorder);
            var fill = UITheme.MakeRounded(transform, "Start Fill", c,
                size - Vector2.one * S(3f), S(10f), StartOff, ZFill, QFill);
            _startBorderMat = border.GetComponent<MeshRenderer>().sharedMaterial;
            _startFillMat = fill.GetComponent<MeshRenderer>().sharedMaterial;

            _startIcon = UITheme.MakeShape(transform, "Start Icon", UIMesh.Play(), StartOffTx, QIcon);
            _startIcon.localPosition = new Vector3(c.x - S(96f), c.y, ZIcon);
            _startIcon.localScale = new Vector3(0.020f, 0.024f, 1f);
            _startIconMat = _startIcon.GetComponent<MeshRenderer>().sharedMaterial;

            _startLabel = UITheme.MakeText(transform, "OYUNA BAŞLA", StartOffTx, 0.034f,
                TextAnchor.MiddleCenter, QText);
            _startLabel.transform.localPosition = new Vector3(c.x + S(14f), c.y, ZText);

            _els.Add(new El { center = c, size = size, radius = S(12f), action = ActionStart });

            _hintText = UITheme.MakeText(transform, "En az " + PlayerProfile.MinLength + " harf gir.",
                Hint, 0.018f, TextAnchor.MiddleCenter, QText);
            _hintText.transform.localPosition = new Vector3(X(640f), Y(616f), ZText);
        }

        El AddButton(Vector2 center, Vector2 size, float radius, string label, float textH,
                     Color edge, Color fill, Color text, char ch, string action)
        {
            var border = UITheme.MakeRounded(transform, "B_" + label, center, size, radius,
                edge, ZBorder, QBorder);
            var body = UITheme.MakeRounded(transform, "K_" + label, center,
                size - Vector2.one * S(2f), Mathf.Max(0f, radius - S(2f)), fill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, label, text, textH, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(center.x, center.y, ZText);

            var el = new El
            {
                center = center, size = size, radius = radius, ch = ch, action = action,
                label = tm,
                fillMat = body.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
            };
            _els.Add(el);
            return el;
        }

        // Ikonu ogenin soluna koyar ve yaziyi bir tik saga kaydirir (tasarimdaki hizalama).
        void IconOn(El el, Mesh mesh, Color color, float dx, float w, float h)
        {
            var t = UITheme.MakeShape(transform, "Icon", mesh, color, QIcon);
            t.localPosition = new Vector3(el.center.x + dx, el.center.y, ZIcon);
            t.localScale = new Vector3(w, h, 1f);
            el.label.transform.localPosition += new Vector3(S(10f), 0f, 0f);
        }

        // ------------------------------------------------------------------ surus

        /// <summary>Sahibi her kare cagirir.</summary>
        public void Tick(VRPointer pointer)
        {
            RefreshCaret();
            if (pointer == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            _hoverIdx = hit ? Find(local) : -1;
            ApplyHover();

            if (_hoverIdx < 0) { _repeatAt = 0f; return; }
            var el = _els[_hoverIdx];

            if (pointer.ClickDown)
            {
                Press(el);
                // Tekrar YALNIZCA SIL'de: harf tusunda tutmak ayni harfi yagdirirdi.
                _repeatAt = el.action == "backspace" ? Time.unscaledTime + RepeatDelay : 0f;
            }
            else if (_repeatAt > 0f && pointer.ClickHeld && el.action == "backspace")
            {
                if (Time.unscaledTime >= _repeatAt)
                {
                    Press(el);
                    _repeatAt = Time.unscaledTime + RepeatInterval;
                }
            }
            else if (!pointer.ClickHeld)
            {
                _repeatAt = 0f;
            }
        }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _els.Count; i++)
            {
                var e = _els[i];
                if (e.ch == '\0' && e.action == null && e.team == 0) continue;   // tiklanamaz
                if (Mathf.Abs(p.x - e.center.x) <= e.size.x * 0.5f &&
                    Mathf.Abs(p.y - e.center.y) <= e.size.y * 0.5f)
                    return i;
            }
            return -1;
        }

        void ApplyHover()
        {
            bool on = _hoverIdx >= 0;
            if (_hover.gameObject.activeSelf != on) _hover.gameObject.SetActive(on);
            if (!on) return;

            var e = _els[_hoverIdx];
            _hoverMesh.sharedMesh = UIMesh.RoundedRect(e.size.x, e.size.y, e.radius);
            _hover.localPosition = new Vector3(e.center.x, e.center.y, ZHover);
        }

        void Press(El el)
        {
            VRPointer.Haptic();

            if (el.team != 0) { SetTeam(el.team); return; }

            if (el.action == null)
            {
                if (_sb.Length >= PlayerProfile.MaxLength) return;   // sinirda sessizce yut
                _sb.Append(el.ch);
                RefreshName();
                Changed?.Invoke();
                return;
            }

            switch (el.action)
            {
                case "backspace":
                    if (_sb.Length == 0) return;
                    _sb.Length--;
                    RefreshName();
                    Changed?.Invoke();
                    return;

                case ActionStart:
                    if (!Ready) return;             // pasif buton: basmak bir sey yapmaz
                    ActionPressed?.Invoke(ActionStart);
                    return;

                default:
                    ActionPressed?.Invoke(el.action);
                    return;
            }
        }

        // ------------------------------------------------------------------ gorunum

        // Yanip sonen imlec. Genislik SABIT kalsin diye sonuk fazda bosluk yaziliyor —
        // yoksa yazi her yarim saniyede oynardi.
        void RefreshCaret()
        {
            if (_nameText == null || _sb.Length == 0) return;
            bool on = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
            _nameText.text = _sb.ToString() + (on ? "|" : " ");
        }

        void RefreshName()
        {
            if (_nameText != null)
            {
                bool empty = _sb.Length == 0;
                _nameText.text = empty ? "Adını yaz" : _sb.ToString();
                _nameText.color = empty ? Placeholder : NameCol;
            }

            if (_counterText != null)
            {
                _counterText.text = _sb.Length + "/" + PlayerProfile.MaxLength;
                _counterText.color = _sb.Length >= PlayerProfile.MaxLength ? RedText : Counter;
            }

            RefreshStart();
        }

        void RefreshTeamCards()
        {
            Paint(_redCard, RedEdge, RedFill, _team == PlayerProfile.TeamRed);
            Paint(_blueCard, BlueEdge, BlueFill, _team == PlayerProfile.TeamBlue);
        }

        // Secili kart: dolgusu acilir, kenari parlar, arkasinda hale belirir.
        static void Paint(El card, Color edge, Color fill, bool selected)
        {
            if (card == null) return;

            UITheme.SetMaterialColor(card.borderMat,
                selected ? edge : new Color(edge.r, edge.g, edge.b, 0.55f));
            UITheme.SetMaterialColor(card.fillMat,
                selected ? Color.Lerp(fill, edge, 0.22f) : fill);
            UITheme.SetMaterialColor(card.glowMat,
                new Color(edge.r, edge.g, edge.b, selected ? 0.22f : 0f));
        }

        void RefreshStart()
        {
            bool ready = Ready;
            UITheme.SetMaterialColor(_startBorderMat, ready ? StartOnEdge : StartOff);
            UITheme.SetMaterialColor(_startFillMat, ready ? StartOnFill : StartOff);

            var tx = ready ? StartOnTx : StartOffTx;
            if (_startLabel != null) _startLabel.color = tx;
            if (_startIconMat != null) UITheme.SetMaterialColor(_startIconMat, tx);
        }
    }
}
