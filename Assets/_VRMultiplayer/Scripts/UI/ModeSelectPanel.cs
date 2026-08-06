using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// MOD SECIMI ekrani: YARATICI (harita tasarla) / OYUNCU (maca katil). Uygulamanin ilk
    /// gordugu panel; oyuncu girisinden (<see cref="PlayerEntryPanel"/>) ONCE gelir.
    ///
    /// AYNI DIL, DAHA KUCUK OLCEK: giris ekraninin katman/z/palet sozlesmesi birebir
    /// kopyalandi — iki ekran arka arkaya goruldugu icin en ufak kayma ucuz durur. Renkler
    /// <see cref="UITheme"/>'den; buraya ham renk YAZMA (palet bir kez kopyalanmisti ve iki
    /// ekran birbirinden kaymisti, o yuzden tek kaynaga alindi).
    ///
    /// ONAY BUTONU YOK: iki buyuk secenek yeterince net, tiklama dogrudan gecirir. Giris
    /// ekranindaki "OYUNA BASLA" oradaki iki asamali zorunluluk (isim + takim) yuzunden var.
    ///
    /// OLCULER dogrudan METRE (giris ekrani gibi tasarim pikseli degil): burada arkasinda bir
    /// mockup yok, ve VR'da okunabilirligi belirleyen sey gorme acisi. 1.4 m'de panel
    /// +-15.5 x +-8.9 derece yer kaplar; en kucuk yazi 0.82 derece (rahat esik ~1 derecenin
    /// hemen altinda ama yalnizca yardimci metinde).
    /// </summary>
    public class ModeSelectPanel : MonoBehaviour
    {
        /// <summary>Bir karta basildi. Sahibi (<see cref="ModeSelectUI"/>) <see cref="AppMode"/>'a
        /// yazar — panel uygulama durumuna dokunmaz, yalnizca niyeti bildirir.</summary>
        public event Action<AppMode.Mode> Selected;

        // ------------------------------------------------------------------ olculer (metre)
        const float PanelW = 0.78f, PanelH = 0.44f, PanelR = 0.020f, PanelEdgeW = 0.0025f;
        const float CardW = 0.32f, CardH = 0.20f, CardR = 0.014f, CardGap = 0.05f;

        // Kart merkezleri: iki kart + bosluk = 0.69 m, panele (0.78) 0.045 m yan payla siger.
        const float CardX = (CardW + CardGap) * 0.5f;   // 0.185
        const float CardY = -0.052f;

        const float TitleY = 0.132f, TitleHalfSpan = 0.17f;
        const float SubtitleY = 0.082f;

        // Kart ici (kart merkezine gore).
        const float IconDy = 0.052f, IconW = 0.026f, IconH = 0.030f;
        const float CardTitleDy = -0.002f, CardDescDy = -0.055f;

        // Satir yukseklikleri — 1.4 m'de sirasiyla 2.05 / 0.82 / 1.72 / 0.82 derece.
        const float TitleSize = 0.050f, SubtitleSize = 0.020f,
                    CardTitleSize = 0.042f, CardDescSize = 0.020f;

        // ------------------------------------------------------------------ katmanlar
        // Overlay malzemesi derinlik testine girmedigi icin sira YALNIZCA renderQueue'dan gelir.
        // Degerler PlayerEntryPanel ile BIREBIR: 3004 tabani, sahnedeki oda geometrisi saydam
        // kuyruk 3000'de ve ayni kuyrukta saydamlar mesafeye gore siralanir — panelden yakin bir
        // duvar zemini uzerine cizip paneli deler.
        const int QBack = 3004, QGlow = 3008, QBorder = 3012, QFill = 3016,
                  QHover = 3020, QIcon = 3026, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f,
                    ZHover = 0.002f, ZIcon = 0.001f, ZText = 0f;

        // ------------------------------------------------------------------ palet
        static readonly Color Backdrop  = UITheme.PanelBg;      // TAM OPAK: yari saydam panel
        static readonly Color PanelEdge = UITheme.PanelEdge;    // aydinlik odada solup ucuz durur
        static readonly Color CardFill  = UITheme.SurfaceFill;
        static readonly Color Muted     = UITheme.TextMuted;
        static readonly Color TitleA    = UITheme.AccentCyan;
        static readonly Color TitleB    = UITheme.AccentPurple;
        static readonly Color HoverCol  = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        // ------------------------------------------------------------------ ogeler
        class Card
        {
            public Vector2 center, size;
            public float radius;
            public AppMode.Mode mode;
            public Color edge;
            public Material fillMat, borderMat, glowMat;
        }

        readonly List<Card> _cards = new List<Card>();

        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;

        // ------------------------------------------------------------------ kurulum

        void Awake()
        {
            BuildBackdrop();
            BuildTitle();

            var sub = UITheme.MakeText(transform, "Bir mod seç", Muted, SubtitleSize,
                TextAnchor.MiddleCenter, QText);
            sub.transform.localPosition = new Vector3(0f, SubtitleY, ZText);

            AddCard(-CardX, AppMode.Mode.Creative, "YARATICI", "Harita tasarla",
                UITheme.AccentPurple, UIMesh.Bolt());
            AddCard(+CardX, AppMode.Mode.Player, "OYUNCU", "Maça katıl",
                UITheme.AccentCyan, UIMesh.Play());

            // Vurgu: TEK obje, mesh'i uzerine gelinen kartin olcusune gore degistirilir
            // (PlayerEntryPanel deseni). Alternatif — kartin malzemesini her kare boyamak —
            // "eski ogeyi geri boyamayi unutma" hatasina acikti.
            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            RefreshCards();
        }

        void BuildBackdrop()
        {
            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, PanelEdge, Backdrop, PanelEdgeW,
                ZBack, QBack, QBack + 1);
        }

        // Baslik HARF HARF: hem genis harf araligi hem camgobegi->mor gecisi icin. Tek TextMesh
        // ile ikisi de olmazdi (harf araligi ayari ve harf basina renk yok). Giris ekranindaki
        // "OYUNCU GIRISI" basligiyla ayni teknik — iki ekran ust uste tutarli gorunsun.
        void BuildTitle()
        {
            const string text = "VR ARENA";
            int n = text.Length;

            for (int i = 0; i < n; i++)
            {
                if (text[i] == ' ') continue;
                float t = n > 1 ? i / (float)(n - 1) : 0f;
                var tm = UITheme.MakeText(transform, text[i].ToString(),
                    Color.Lerp(TitleA, TitleB, t), TitleSize, TextAnchor.MiddleCenter, QText);
                tm.transform.localPosition = new Vector3(
                    Mathf.Lerp(-TitleHalfSpan, TitleHalfSpan, t), TitleY, ZText);
            }
        }

        void AddCard(float x, AppMode.Mode mode, string title, string desc, Color edge, Mesh icon)
        {
            var c = new Vector2(x, CardY);
            var size = new Vector2(CardW, CardH);

            // Secili/uzerine gelinmis kartta beliren yumusak hale (tasarimdaki isima).
            var glow = UITheme.MakeRounded(transform, title + " Glow", c,
                size + Vector2.one * 0.012f, CardR + 0.006f,
                new Color(edge.r, edge.g, edge.b, 0f), ZBorder + 0.001f, QGlow);
            var border = UITheme.MakeRounded(transform, title + " Border", c, size, CardR,
                edge, ZBorder, QBorder);
            var body = UITheme.MakeRounded(transform, title + " Fill", c,
                size - Vector2.one * 0.004f, Mathf.Max(0f, CardR - 0.002f), CardFill,
                ZFill, QFill);

            var ic = UITheme.MakeShape(transform, title + " Icon", icon, edge, QIcon);
            ic.localPosition = new Vector3(c.x, c.y + IconDy, ZIcon);
            ic.localScale = new Vector3(IconW, IconH, 1f);

            var tm = UITheme.MakeText(transform, title, edge, CardTitleSize,
                TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(c.x, c.y + CardTitleDy, ZText);

            var dt = UITheme.MakeText(transform, desc, Muted, CardDescSize,
                TextAnchor.MiddleCenter, QText);
            dt.transform.localPosition = new Vector3(c.x, c.y + CardDescDy, ZText);

            _cards.Add(new Card
            {
                center = c, size = size, radius = CardR, mode = mode, edge = edge,
                fillMat = body.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
                glowMat = glow.GetComponent<MeshRenderer>().sharedMaterial,
            });
        }

        // ------------------------------------------------------------------ surus

        /// <summary>Sahibi her kare cagirir.</summary>
        public void Tick(VRPointer pointer)
        {
            if (pointer == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            int idx = hit ? Find(local) : -1;
            if (idx != _hoverIdx)
            {
                _hoverIdx = idx;
                ApplyHover();
                RefreshCards();
            }

            // Tekrar (repeat) YOK: secim tek atislik, tetik basili tutmak ikinci kez tetiklemez.
            if (_hoverIdx >= 0 && pointer.ClickDown)
            {
                VRPointer.Haptic();
                Selected?.Invoke(_cards[_hoverIdx].mode);
            }
        }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                if (Mathf.Abs(p.x - c.center.x) <= c.size.x * 0.5f &&
                    Mathf.Abs(p.y - c.center.y) <= c.size.y * 0.5f)
                    return i;
            }
            return -1;
        }

        void ApplyHover()
        {
            bool on = _hoverIdx >= 0;
            if (_hover.gameObject.activeSelf != on) _hover.gameObject.SetActive(on);
            if (!on) return;

            var c = _cards[_hoverIdx];
            _hoverMesh.sharedMesh = UIMesh.RoundedRect(c.size.x, c.size.y, c.radius);
            _hover.localPosition = new Vector3(c.center.x, c.center.y, ZHover);
        }

        // Uzerine gelinen kart: dolgusu acilir, kenari parlar, arkasinda hale belirir.
        // PlayerEntryPanel'in secili takim kartiyla ayni davranis.
        void RefreshCards()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                bool on = i == _hoverIdx;

                UITheme.SetMaterialColor(c.borderMat,
                    on ? c.edge : new Color(c.edge.r, c.edge.g, c.edge.b, 0.55f));
                UITheme.SetMaterialColor(c.fillMat,
                    on ? Color.Lerp(CardFill, c.edge, 0.18f) : CardFill);
                UITheme.SetMaterialColor(c.glowMat,
                    new Color(c.edge.r, c.edge.g, c.edge.b, on ? 0.22f : 0f));
            }
        }
    }
}
