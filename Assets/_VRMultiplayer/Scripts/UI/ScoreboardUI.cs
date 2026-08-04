using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// SKORBORD: sag B ile acilan mac tablosu — iki takim sutunu (isim / K / O), ortada sure,
    /// altta oyuncunun KENDI bilgileri buyuk puntoyla.
    ///
    /// UST TASKBAR'IN YERINE GECTI. Skor+sure once surekli gorunen bir bar olarak denenmisti
    /// (MatchBarUI); surekli gorunen bir sey nisan hattini kapatmamak icin +28 derecede durmak
    /// zorundaydi ve orasi Quest lens kenarina (~30 derece) tehlikeli yakindi. Istege bagli
    /// acilan bir panel MERKEZE konabilir: hem okunakli hem oyun sirasinda hic yer kaplamiyor.
    ///
    /// YENI AG KATMANI YOK. Isim/takim/K/O zaten <see cref="PlayerIdentity"/>'de replike, olu
    /// durumu <see cref="PlayerHealth.Dead"/>'de, saat <c>ServerTime</c> ile senkron. Salt okur.
    ///
    /// ACILIS ANIMASYONU: panel ortada yatay bir CIZGI olarak dogar, dikeyde buyuyerek acilir.
    /// Yazilar dikeyde EZILMEZ — animasyonun son bolumunde alfa ile belirirler; olcekle acilan
    /// yazi acilirken okunmaz ve cirkin gorunur.
    ///
    /// TASARIM/ANIMASYON ALFASI AYRI TUTULUR (<see cref="El"/>): olu satirin soluklugu ve
    /// zemin opakligi "tasarim" alfasidir, animasyon onu CARPAR. Ikisi ayni alanda tutulsaydi
    /// her tazeleme animasyon alfasini taban sanip degerleri kademeli sondururdu.
    ///
    /// KUYRUK 3052+: kill panelinin (3050/3051) ustunde, olum perdesinin (3000) cok ustunde.
    /// Oldugun an skoru en cok merak ettigin andir — perde skorbordu ortmemeli.
    /// </summary>
    public class ScoreboardUI : MonoBehaviour
    {
        [Header("Yerlesim (kafaya gore, metre)")]
        public float distance = 1.3f;
        [Tooltip("Sonumlu takip hizi. Buyuk deger = kafaya daha sert kilitli.")]
        public float followSpeed = 9f;

        [Header("Tus")]
        [Tooltip("Acik: B BASILI TUTUNCA gorunur, birakinca kapanir (referans FPS davranisi). " +
                 "Kapali: B'ye her basista ac/kapa. VR'da acik unutulan panel nisani kapatir — " +
                 "cihazda deneyip karar ver.")]
        public bool holdToShow = true;

        [Header("Animasyon (saniye)")]
        public float openTime = 0.22f;
        public float closeTime = 0.14f;

        [Header("Opaklik")]
        [Tooltip("Panel zemini. Tam opak DEGIL: oyun ici hizli bakis, menu degil.")]
        [Range(0f, 1f)] public float bgAlpha = 0.72f;

        // ------------------------------------------------------------------ olculer (metre)
        const float PanelW = 0.84f, PanelH = 0.56f, PanelR = 0.018f, PanelEdgeW = 0.0025f;

        const float TimeW = 0.20f, TimeH = 0.11f, TimeY = 0.205f;
        const float HeadY = 0.108f, HeadH = 0.055f, HeadW = 0.40f;
        const float ColLabelY = 0.062f;
        const float RowTop = 0.026f, RowH = 0.036f;
        const int MaxRows = 8;

        /// <summary>Sutun merkezleri (panel yarisi 0.42).</summary>
        const float ColX = 0.208f;
        // Sutun ici sabitler (sutun merkezine gore).
        const float NameX = -0.185f, DeadX = -0.020f, KillX = 0.108f, DeathX = 0.170f;

        const float StripW = 0.46f, StripH = 0.075f, StripGap = 0.030f;

        const float TimeLabelSize = 0.016f, TimeSize = 0.048f;
        const float TeamNameSize = 0.026f, TeamScoreSize = 0.040f;
        const float ColLabelSize = 0.015f, RowSize = 0.024f;
        const float StripNameSize = 0.034f, StripStatSize = 0.030f;
        const float ChipSize = 0.018f;

        // ------------------------------------------------------------------ katmanlar
        const int QBg = 3052, QRow = 3054, QText = 3056;

        // ------------------------------------------------------------------ palet
        static readonly Color Muted = UITheme.TextMuted;
        static readonly Color Primary = UITheme.TextPrimary;
        /// <summary>Kendi satirimin vurgusu: giris ekraninin hover rengi — ayni gorsel dil.</summary>
        static readonly Color MineHighlight = new Color(0.35f, 0.62f, 0.75f, 0.30f);
        static readonly Color RowFill = UITheme.SurfaceFill;

        /// <summary>Olu oyuncunun satiri bu kadar soluklasir.</summary>
        const float DeadAlpha = 0.45f;

        /// <summary>Animasyonla soluklastirilabilen bir oge. <see cref="design"/> tasarim
        /// rengidir (olu solukluğu, zemin opakligi burada); <see cref="Apply"/> onu animasyon
        /// carpaniyla ekrana yazar. Tasarim rengi EKRANDAN geri okunmaz — okunsaydi animasyon
        /// alfasi taban sanilip degerler her karede biraz daha sonerdi.</summary>
        class El
        {
            public TextMesh tm;      // ya yazi...
            public Material mat;     // ...ya yuzey
            public Color design;

            public void Apply(float fade)
            {
                var c = new Color(design.r, design.g, design.b, design.a * fade);
                if (tm != null) tm.color = c;
                else if (mat != null) UITheme.SetMaterialColor(mat, c);
            }
        }

        class Row
        {
            public GameObject go;
            public El bg, name, kills, deaths, dead;
        }

        readonly List<El> _els = new List<El>();
        readonly List<Row> _rows = new List<Row>();          // 0..MaxRows-1 mavi, sonrasi kizil
        readonly List<PlayerIdentity> _sorted = new List<PlayerIdentity>();

        Transform _panel;
        El _time, _scoreBlue, _scoreRed, _overflowBlue, _overflowRed;
        El _stripName, _stripKills, _stripDeaths, _stripChip;

        float _open;           // 0 = kapali, 1 = tam acik
        bool _want;
        bool _prevButton;
        bool _placed;
        float _nextRefresh;
        bool _externalTime;

        /// <summary>Sure alanini disaridan yaz (MatchManager geri sayimi). Cagrildigi an
        /// panelin kendi gecen-sure sayaci susar — iki kaynak ayni alani yazip yanip sonmesin.
        /// Ust taskbar'dan devralinan kanca; MatchManager gelince TEK dokunulacak yer.</summary>
        public void SetTime(string s)
        {
            _externalTime = true;
            Write(_time, s);
        }

        /// <summary>Panel su an gorunur mu (animasyon dahil)?</summary>
        public bool Visible => _open > 0.001f;

        // ------------------------------------------------------------------ kurulum

        void Awake()
        {
            _panel = new GameObject("Scoreboard Panel").transform;
            _panel.SetParent(transform, false);

            Build();
            ApplyOpen(0f);   // kapali dogar
        }

        void Build()
        {
            Shape("Bg", UIMesh.RoundedRect(PanelW, PanelH, PanelR),
                Alpha(UITheme.PanelBg, bgAlpha), QBg, Vector2.zero, 0.004f);
            Shape("Edge", UIMesh.RoundedRectOutline(PanelW, PanelH, PanelR, PanelEdgeW),
                Alpha(UITheme.PanelEdge, 0.9f), QBg + 1, Vector2.zero, 0.0038f);

            BuildTimeCard();
            BuildTeam(-1f, "MAVİ TAKIM", UITheme.TeamBlueEdge, PlayerIdentity.TeamAColor,
                out _scoreBlue, out _overflowBlue);
            BuildTeam(+1f, "KIZIL TAKIM", UITheme.TeamRedEdge, PlayerIdentity.TeamBColor,
                out _scoreRed, out _overflowRed);

            // Iki sutunu ayiran dikey cizgi.
            Shape("Divider", UIMesh.RoundedRect(0.0015f, 0.30f, 0f),
                UITheme.SurfaceEdge, QRow, new Vector2(0f, -0.058f), 0.0032f);

            BuildPersonalStrip();
        }

        void BuildTimeCard()
        {
            Shape("Time Card", UIMesh.RoundedRect(TimeW, TimeH, 0.012f),
                UITheme.SurfaceFill, QRow, new Vector2(0f, TimeY), 0.0034f);
            Shape("Time Edge", UIMesh.RoundedRectOutline(TimeW, TimeH, 0.012f, 0.0015f),
                UITheme.SurfaceEdge, QRow + 1, new Vector2(0f, TimeY), 0.0033f);

            Text("SÜRE", Muted, TimeLabelSize, new Vector2(0f, TimeY + 0.028f));
            _time = Text("--:--", Primary, TimeSize, new Vector2(0f, TimeY - 0.014f));
        }

        void BuildTeam(float dir, string label, Color barColor, Color chipColor,
                       out El score, out El overflow)
        {
            float cx = dir * ColX;

            // Gorseldeki renkli takim basligi.
            Shape(label + " Bar", UIMesh.RoundedRect(HeadW, HeadH, 0.008f),
                Alpha(barColor, 0.9f), QRow, new Vector2(cx, HeadY), 0.0034f);

            Shape(label + " Chip", UIMesh.RoundedRect(ChipSize, ChipSize, ChipSize * 0.28f),
                chipColor, QText, new Vector2(cx - HeadW * 0.5f + 0.024f, HeadY), 0.003f);

            Text(label, Primary, TeamNameSize,
                new Vector2(cx - HeadW * 0.5f + 0.042f, HeadY), TextAnchor.MiddleLeft);
            score = Text("0", Primary, TeamScoreSize,
                new Vector2(cx + HeadW * 0.5f - 0.028f, HeadY), TextAnchor.MiddleRight);

            Text("İsim", Muted, ColLabelSize, new Vector2(cx + NameX, ColLabelY), TextAnchor.MiddleLeft);
            Text("K", Muted, ColLabelSize, new Vector2(cx + KillX, ColLabelY));
            Text("Ö", Muted, ColLabelSize, new Vector2(cx + DeathX, ColLabelY));

            for (int i = 0; i < MaxRows; i++) _rows.Add(BuildRow(cx, i));

            // Tasma ozeti: 8'den fazla oyuncuyu SESSIZCE kirpmak yerine soyler.
            overflow = Text("", Muted, ColLabelSize,
                new Vector2(cx + NameX, RowTop - MaxRows * RowH - 0.006f), TextAnchor.MiddleLeft);
        }

        Row BuildRow(float cx, int index)
        {
            var go = new GameObject("Row " + index);
            go.transform.SetParent(_panel, false);
            go.transform.localPosition = new Vector3(cx, RowTop - index * RowH, 0f);

            var row = new Row
            {
                go = go,
                bg = Shape("Bg", UIMesh.RoundedRect(HeadW, RowH - 0.005f, 0.005f),
                    Alpha(RowFill, 0.6f), QRow, Vector2.zero, 0.003f, go.transform),
                name = Text("", Primary, RowSize, new Vector2(NameX, 0f), TextAnchor.MiddleLeft, go.transform),
                dead = Text("", UITheme.TeamRedText, ColLabelSize, new Vector2(DeadX, 0f),
                    TextAnchor.MiddleLeft, go.transform),
                kills = Text("", Primary, RowSize, new Vector2(KillX, 0f), TextAnchor.MiddleCenter, go.transform),
                deaths = Text("", Muted, RowSize, new Vector2(DeathX, 0f), TextAnchor.MiddleCenter, go.transform),
            };
            go.SetActive(false);
            return row;
        }

        // Alt serit: kendi skorunu tabloyu taramadan tek bakista okumak icin (kullanici istegi).
        void BuildPersonalStrip()
        {
            float y = -PanelH * 0.5f - StripGap - StripH * 0.5f;

            Shape("Strip Bg", UIMesh.RoundedRect(StripW, StripH, 0.012f),
                Alpha(UITheme.PanelBg, bgAlpha), QBg, new Vector2(0f, y), 0.004f);
            Shape("Strip Edge", UIMesh.RoundedRectOutline(StripW, StripH, 0.012f, PanelEdgeW),
                Alpha(UITheme.PanelEdge, 0.9f), QBg + 1, new Vector2(0f, y), 0.0038f);

            _stripChip = Shape("Strip Chip", UIMesh.RoundedRect(0.020f, 0.020f, 0.006f),
                Muted, QText, new Vector2(-StripW * 0.5f + 0.028f, y), 0.003f);

            _stripName = Text("", Primary, StripNameSize,
                new Vector2(-StripW * 0.5f + 0.052f, y), TextAnchor.MiddleLeft);
            _stripKills = Text("K 0", Primary, StripStatSize,
                new Vector2(StripW * 0.5f - 0.140f, y), TextAnchor.MiddleLeft);
            _stripDeaths = Text("Ö 0", Muted, StripStatSize,
                new Vector2(StripW * 0.5f - 0.030f, y), TextAnchor.MiddleRight);
        }

        // ------------------------------------------------------------------ yardimcilar

        El Text(string s, Color c, float size, Vector2 pos,
                TextAnchor anchor = TextAnchor.MiddleCenter, Transform parent = null)
        {
            var tm = UITheme.MakeText(parent != null ? parent : _panel, s, c, size, anchor, QText);
            tm.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
            var el = new El { tm = tm, design = c };
            _els.Add(el);
            return el;
        }

        El Shape(string name, Mesh mesh, Color c, int queue, Vector2 pos, float z,
                 Transform parent = null)
        {
            var t = UITheme.MakeShape(parent != null ? parent : _panel, name, mesh, c, queue);
            t.localPosition = new Vector3(pos.x, pos.y, z);
            var el = new El { mat = t.GetComponent<MeshRenderer>().sharedMaterial, design = c };
            _els.Add(el);
            return el;
        }

        static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        static void Write(El el, string s)
        {
            if (el != null && el.tm != null && el.tm.text != s) el.tm.text = s;
        }

        // ------------------------------------------------------------------ giris

        void Update()
        {
            ReadButton();

            float target = _want ? 1f : 0f;
            if (!Mathf.Approximately(_open, target))
            {
                float dur = _want ? openTime : closeTime;
                _open = Mathf.MoveTowards(_open, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, dur));
                ApplyOpen(_open);
            }

            if (_open <= 0.001f) return;   // kapaliyken hicbir sey hesaplanmaz

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.5f;   // 2 Hz yeter
                Refresh();
                ApplyOpen(_open);          // yeni tasarim renkleri animasyon carpaniyla yazilsin
            }
        }

        void ReadButton()
        {
            // Insa modu tuslari yeniden anlamlandiriyor (bkz. XRButtons.GameplayInputSuppressed);
            // o sirada skorbord acilmamali.
            if (XRButtons.GameplayInputSuppressed) { _want = false; _prevButton = false; return; }

            // Takim secilmeden skorbord anlamsiz. Ayrica TeamSelector'in YEDEK paneli (takimsiz
            // spawn olunca acilan hata yolu) hala A/B kullaniyor — iki tuketici cakismasin diye
            // takimsizken B okunmuyor.
            var local = PlayerIdentity.Local;
            if (local == null || local.Team.Value == 0) { _want = false; _prevButton = false; return; }

            bool now = XRButtons.Button(XRNode.RightHand, CommonUsages.secondaryButton);
#if UNITY_EDITOR
            now |= _guiHeld;   // gozluksuz iterasyon: ekrandaki tus basili kumanda gibi davranir
#endif

            if (holdToShow) _want = now;
            else if (now && !_prevButton) _want = !_want;   // yukselen kenarda ac/kapa

            _prevButton = now;
        }

#if UNITY_EDITOR
        // Masaustu yedegi (gozluksuz iterasyon). IMGUI kulaklikta hicbir sey cizmez.
        bool _guiHeld;

        void OnGUI()
        {
            if (Application.isMobilePlatform) return;
            GUILayout.BeginArea(new Rect(20, 400, 240, 62), GUI.skin.box);
            GUILayout.Label("Skorbord — B" + (holdToShow ? " (basili tut)" : " (ac/kapa)"));
            _guiHeld = GUILayout.RepeatButton(holdToShow ? "GOSTER (bas ve tut)" : "AC / KAPA");
            GUILayout.EndArea();
        }
#endif

        // ------------------------------------------------------------------ animasyon

        /// <summary>CIZGIDEN ACILMA: panel dikeyde 0'dan tam boya buyur (yatayda hep tam boy).
        /// Yazilar son %40'ta alfa ile belirir.</summary>
        void ApplyOpen(float t)
        {
            bool active = t > 0.001f;
            if (_panel.gameObject.activeSelf != active) _panel.gameObject.SetActive(active);
            if (!active) return;

            float ease = t * t * (3f - 2f * t);                  // smoothstep
            _panel.localScale = new Vector3(1f, Mathf.Lerp(0.03f, 1f, ease), 1f);

            float fade = Mathf.Clamp01((t - 0.6f) / 0.4f);
            fade = fade * fade * (3f - 2f * fade);

            for (int i = 0; i < _els.Count; i++)
            {
                var el = _els[i];
                el.Apply(el.tm != null ? fade : ease);   // yuzeyler olcekle, yazilar gec fade ile
            }
        }

        // ------------------------------------------------------------------ veri

        void Refresh()
        {
            FillTeam(PlayerProfile.TeamBlue, 0, _scoreBlue, _overflowBlue);
            FillTeam(PlayerProfile.TeamRed, MaxRows, _scoreRed, _overflowRed);
            if (!_externalTime) Write(_time, ElapsedText());
            RefreshStrip();
        }

        void FillTeam(byte team, int rowOffset, El score, El overflow)
        {
            _sorted.Clear();
            var all = PlayerIdentity.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p != null && p.Team.Value == team) _sorted.Add(p);
            }

            // Cok oldurenden aza; esitse az olene; esitse isme gore (kararli siralama —
            // yoksa esit skorlu oyuncular her tazelemede yer degistirir).
            _sorted.Sort(CompareRank);

            Write(score, PlayerIdentity.TeamScore(team).ToString());

            var local = PlayerIdentity.Local;
            int shown = Mathf.Min(_sorted.Count, MaxRows);

            for (int i = 0; i < MaxRows; i++)
            {
                var row = _rows[rowOffset + i];
                bool on = i < shown;
                if (row.go.activeSelf != on) row.go.SetActive(on);
                if (!on) continue;

                var p = _sorted[i];
                bool mine = p == local;
                var health = p.GetComponent<PlayerHealth>();
                bool isDead = health != null && health.Dead.Value;
                float a = isDead ? DeadAlpha : 1f;

                Write(row.name, p.NetName.Value.ToString());
                Write(row.kills, p.Kills.Value.ToString());
                Write(row.deaths, p.Deaths.Value.ToString());
                Write(row.dead, isDead ? "ÖLÜ" : "");

                // Kendi satirim camgobegi (giris ekraninin vurgu rengi) + acik zemin.
                row.name.design = Alpha(mine ? UITheme.AccentCyan : Primary, a);
                row.kills.design = Alpha(Primary, a);
                row.deaths.design = Alpha(Muted, a);
                row.dead.design = Alpha(UITheme.TeamRedText, isDead ? 1f : 0f);
                row.bg.design = mine ? MineHighlight : Alpha(RowFill, isDead ? 0.35f : 0.6f);
            }

            int extra = _sorted.Count - shown;
            Write(overflow, extra > 0 ? "+" + extra + " oyuncu daha" : "");
        }

        static int CompareRank(PlayerIdentity a, PlayerIdentity b)
        {
            int c = b.Kills.Value.CompareTo(a.Kills.Value);
            if (c != 0) return c;
            c = a.Deaths.Value.CompareTo(b.Deaths.Value);
            if (c != 0) return c;
            return string.CompareOrdinal(a.NetName.Value.ToString(), b.NetName.Value.ToString());
        }

        void RefreshStrip()
        {
            var me = PlayerIdentity.Local;
            if (me == null)
            {
                Write(_stripName, "—");
                Write(_stripKills, "K 0");
                Write(_stripDeaths, "Ö 0");
                return;
            }

            Write(_stripName, me.NetName.Value.ToString());
            Write(_stripKills, "K " + me.Kills.Value);
            Write(_stripDeaths, "Ö " + me.Deaths.Value);
            _stripChip.design = me.DisplayColor;
        }

        static string ElapsedText()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return "--:--";
            double t = nm.ServerTime.Time;
            if (t < 0d) t = 0d;
            return ((int)(t / 60d)).ToString("00") + ":" + ((int)(t % 60d)).ToString("00");
        }

        // ------------------------------------------------------------------ takip

        // Acilirken panel kafanin ONUNE bir kez oturur, sonra sonumlu takip eder (killfeed
        // deseni). Sert kilit VR'da yorucudur. Kapaninca _placed sifirlanir ki bir sonraki
        // acilis nereye bakiyorsan orada olsun.
        void LateUpdate()
        {
            if (_open <= 0.001f) { _placed = false; return; }

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 target = head.position + head.forward * distance;
            Quaternion rot = Quaternion.LookRotation(target - head.position, head.up);

            if (!_placed)
            {
                _panel.SetPositionAndRotation(target, rot);
                _placed = true;
                return;
            }

            float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            _panel.SetPositionAndRotation(
                Vector3.Lerp(_panel.position, target, k),
                Quaternion.Slerp(_panel.rotation, rot, k));
        }
    }
}
