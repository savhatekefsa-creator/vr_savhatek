using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// YARATICI ANA MENU: Yeni harita / Mevcut harita / Havuzu yonet.
    ///
    /// Yaratici modun MERKEZI. Akis semasindaki "her islem sonu tum dallar bu menuye geri
    /// doner" kurali buradan isliyor: editorden cikan da, yoneticiden cikan da buraya duser.
    /// Once bu menu yoktu ve YARATICI'yi secmek dogrudan editore atiyordu — tek harita, geri
    /// donusu olmayan tek dal.
    ///
    /// GORSEL DIL <see cref="ModeSelectPanel"/> ILE AYNI, bilerek: iki ekran arka arkaya
    /// goruluyor (mod secimi -> bu menu), en ufak kayma ucuz durur. Renkler <see cref="UITheme"/>
    /// uzerinden; buraya ham renk YAZMA.
    ///
    /// UC KART, IKI DEGIL: kartlar daraldi (0.22 m) ve panel genisledi (0.86 m) — 1.4 m'de
    /// kart basina 9 derece, hala rahat nisan alinan bir hedef.
    /// </summary>
    public class CreativeMenuPanel : MonoBehaviour
    {
        public enum Choice { NewMap, ExistingMap, ManagePool, Exit }

        /// <summary>Bir karta basildi. Panel uygulama durumuna DOKUNMAZ, niyeti bildirir.</summary>
        public event Action<Choice> Selected;

        // ------------------------------------------------------------------ olculer (metre)
        const float PanelW = 0.86f, PanelH = 0.44f, PanelR = 0.020f, PanelEdgeW = 0.0025f;
        const float CardW = 0.22f, CardH = 0.20f, CardR = 0.014f, CardGap = 0.04f;
        const float CardX = CardW + CardGap;      // 0.26 — orta kart 0'da, yanlar +-0.26
        const float CardY = -0.052f;

        const float TitleY = 0.132f, TitleHalfSpan = 0.20f;
        const float SubtitleY = 0.082f;

        // GERI DUGMESI: panelin SOL UST kosesinde kucuk bir ok. Bilerek kart degil ve
        // bilerek kenarda — "yeni/mevcut/havuz" bir is secimi, bu ise akistan cikis. Once
        // kartlarin altina genis bir serit konmustu; kullanici onu istemedi, koseye kucuk
        // bir ikon istedi (2026-08-31).
        // Panel 0.86 x 0.44, yani sol ust kose (-0.43, +0.22). Baslik x -0.20..+0.20
        // arasinda, dolayisiyla carpisma yok.
        const float BackX = -0.375f, BackY = 0.163f;
        const float BackW = 0.070f, BackH = 0.058f, BackR = 0.012f;
        const float BackIconW = 0.026f, BackIconH = 0.024f;

        const float IconDy = 0.052f, IconW = 0.026f, IconH = 0.030f;
        const float CardTitleDy = -0.004f, CardDescDy = -0.056f;

        const float TitleSize = 0.050f, SubtitleSize = 0.020f,
                    CardTitleSize = 0.034f, CardDescSize = 0.018f;

        // Katman/z sozlesmesi ModeSelectPanel ile BIREBIR (gerekcesi orada yaziyor).
        const int QBack = 3004, QGlow = 3008, QBorder = 3012, QFill = 3016,
                  QHover = 3020, QIcon = 3026, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f,
                    ZHover = 0.002f, ZIcon = 0.001f, ZText = 0f;

        static readonly Color Backdrop  = UITheme.PanelBg;
        static readonly Color PanelEdge = UITheme.PanelEdge;
        static readonly Color CardFill  = UITheme.SurfaceFill;
        static readonly Color Muted     = UITheme.TextMuted;
        static readonly Color HoverCol  = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        class Card
        {
            public Vector2 center, size;
            public float radius;
            public Choice choice;
            public Color edge;
            public Material fillMat, borderMat, glowMat;
        }

        readonly List<Card> _cards = new List<Card>();

        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;

        void Awake()
        {
            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, PanelEdge, Backdrop, PanelEdgeW,
                ZBack, QBack, QBack + 1);

            BuildTitle();

            var sub = UITheme.MakeText(transform, "Ne yapmak istiyorsun?", Muted, SubtitleSize,
                TextAnchor.MiddleCenter, QText);
            sub.transform.localPosition = new Vector3(0f, SubtitleY, ZText);

            AddCard(-CardX, Choice.NewMap, "YENİ", "Boş zeminde tasarla",
                UITheme.AccentPurple, UIMesh.Bolt());
            AddCard(0f, Choice.ExistingMap, "MEVCUT", "Kayıtlıyı aç",
                UITheme.AccentCyan, UIMesh.Play());
            AddCard(+CardX, Choice.ManagePool, "HAVUZ", "Oyuna açılanlar",
                UITheme.TeamBlueEdge, UIMesh.Arrow());

            // Yaratici moddan cikis. Onceden hic yoktu: harita kaydedildikten sonra da,
            // yanlislikla yaratici mod secildiginde de bu menude kilitli kaliniyordu -
            // oyuna girmenin tek yolu uygulamayi yeniden baslatmakti (cihazda bildirildi
            // 2026-08-31). AppMode.ReturnToModeSelect zaten vardi ama cagiran yoktu.
            AddBackButton();

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            RefreshCards();
        }

        // Baslik: ModeSelectPanel'deki ayni teknik (harf araligi + renk gecisi), tek objede.
        void BuildTitle()
        {
            var tm = UITheme.MakeTitle(transform, "HARİTA TASARIMI",
                UITheme.AccentCyan, UITheme.AccentPurple, TitleSize, TitleHalfSpan, QText);
            tm.transform.localPosition = new Vector3(0f, TitleY, ZText);
        }

        void AddCard(float x, Choice choice, string title, string desc, Color edge, Mesh icon)
        {
            var c = new Vector2(x, CardY);
            var size = new Vector2(CardW, CardH);

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
                center = c, size = size, radius = CardR, choice = choice, edge = edge,
                fillMat = body.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
                glowMat = glow.GetComponent<MeshRenderer>().sharedMaterial,
            });
        }

        /// <summary>Sol ust kosedeki geri dugmesi. Kartlarla AYNI vurgu/tiklama yolunu
        /// kullanir (ayni _cards listesi), yalnizca gorunumu farkli: yazi yok, tek ikon.</summary>
        void AddBackButton()
        {
            var c = new Vector2(BackX, BackY);
            var size = new Vector2(BackW, BackH);
            Color edge = Muted;

            var glow = UITheme.MakeRounded(transform, "Back Glow", c,
                size + Vector2.one * 0.010f, BackR + 0.005f,
                new Color(edge.r, edge.g, edge.b, 0f), ZBorder + 0.001f, QGlow);
            var border = UITheme.MakeRounded(transform, "Back Border", c, size, BackR,
                edge, ZBorder, QBorder);
            var body = UITheme.MakeRounded(transform, "Back Fill", c,
                size - Vector2.one * 0.004f, Mathf.Max(0f, BackR - 0.002f), CardFill,
                ZFill, QFill);

            // Ok SAGA bakiyor (UIMesh.Arrow); geri icin X'te aynalanir.
            var ic = UITheme.MakeShape(transform, "Back Icon", UIMesh.Arrow(),
                UITheme.TextPrimary, QIcon);
            ic.localPosition = new Vector3(c.x, c.y, ZIcon);
            ic.localScale = new Vector3(-BackIconW, BackIconH, 1f);

            _cards.Add(new Card
            {
                center = c, size = size, radius = BackR, choice = Choice.Exit, edge = edge,
                fillMat = body.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
                glowMat = glow.GetComponent<MeshRenderer>().sharedMaterial,
            });
        }

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

            if (_hoverIdx >= 0 && pointer.ClickDown)
            {
                VRPointer.Haptic();
                Selected?.Invoke(_cards[_hoverIdx].choice);
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
