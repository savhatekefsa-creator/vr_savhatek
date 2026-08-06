using System;
using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// TEK HARITANIN ISLEMLERI — Harita Yoneticisi'nin ikinci adimi.
    ///
    /// LISTE SECER, BU PANEL ISLER. Islemleri satirin icine sigdirmak (dort kucuk dugme x N
    /// satir) VR'da nisan almayi imkansiz yapardi: 1.4 m'de satir zaten 2.5 derece, dorde
    /// bolununce her dugme yarim dereceye duser. Iki adim, buyuk hedefler.
    ///
    /// HAVUZDAN CIKARMAK SILMEK DEGILDIR: iki islem ayri dugmede ve yalnizca SIL kirmizi.
    /// Semadaki not tam olarak bunu soyluyor — cikan harita kayitli kalir, silme geri donussuz.
    ///
    /// HAVUZA GIREMEYEN HARITADA dugme KAPALI ve sebep ekranda. Basilabilir birakip ardindan
    /// reddetmek, kararin oyuncuda oldugu izlenimini bosa harcardi.
    /// </summary>
    public class MapActionsPanel : MonoBehaviour
    {
        public enum Action { PoolToggle, Rename, Delete, Back }

        public event Action<Action> Chosen;

        const float PanelW = 0.74f, PanelH = 0.50f, PanelR = 0.020f, PanelEdgeW = 0.0025f;
        const float BtnW = 0.46f, BtnH = 0.062f, BtnR = 0.012f, BtnGap = 0.014f;
        const float BtnTopY = 0.020f;

        const float TitleY = 0.190f, StateY = 0.140f, ReasonY = 0.104f;
        const float TitleSize = 0.038f, StateSize = 0.020f, ReasonSize = 0.017f, BtnSize = 0.024f;

        const int QBack = 3004, QBorder = 3012, QFill = 3016, QHover = 3020, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZText = 0f;

        static readonly Color HoverCol = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        class Btn
        {
            public Vector2 center, size;
            public float radius;
            public Action action;
            public Color edge;
            public bool enabled;
            public Material fillMat, borderMat;
        }

        readonly List<Btn> _btns = new List<Btn>();
        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;
        bool _built;

        public void Setup(MapCatalog.Entry e)
        {
            if (_built || e == null) return;
            _built = true;

            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, UITheme.PanelEdge, UITheme.PanelBg,
                PanelEdgeW, ZBack, QBack, QBack + 1);

            var t = UITheme.MakeText(transform, e.displayName, UITheme.AccentCyan, TitleSize,
                TextAnchor.MiddleCenter, QText);
            t.transform.localPosition = new Vector3(0f, TitleY, ZText);

            string durum = (e.inPool ? "HAVUZDA" : "Havuz dışı") + "  •  " + e.propCount + " prop";
            var st = UITheme.MakeText(transform, durum,
                e.inPool ? UITheme.AccentCyan : UITheme.TextMuted, StateSize,
                TextAnchor.MiddleCenter, QText);
            st.transform.localPosition = new Vector3(0f, StateY, ZText);

            // Sebep yalnizca ENGEL varken: her haritada bir uyari satiri, uyariyi gorunmez yapar.
            if (!e.poolEligible && !string.IsNullOrEmpty(e.poolBlockReason))
            {
                var rs = UITheme.MakeText(transform, e.poolBlockReason, UITheme.TeamRedText,
                    ReasonSize, TextAnchor.MiddleCenter, QText);
                rs.transform.localPosition = new Vector3(0f, ReasonY, ZText);
            }

            // Havuzdakini cikarmak HER ZAMAN mumkun; eklemek yalnizca uygunsa.
            bool poolBtnEnabled = e.inPool || e.poolEligible;
            AddButton(0, e.inPool ? "HAVUZDAN ÇIKAR" : "HAVUZA EKLE", Action.PoolToggle,
                UITheme.AccentCyan, poolBtnEnabled);
            AddButton(1, "YENİDEN ADLANDIR", Action.Rename, UITheme.AccentPurple, true);
            AddButton(2, "SİL", Action.Delete, UITheme.TeamRedEdge, true);
            AddButton(3, "GERİ", Action.Back, UITheme.TextMuted, true);

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            Refresh();
        }

        void AddButton(int row, string label, Action action, Color edge, bool enabled)
        {
            var c = new Vector2(0f, BtnTopY - row * (BtnH + BtnGap));
            var size = new Vector2(BtnW, BtnH);
            Color col = enabled ? edge : UITheme.TextDim;

            var border = UITheme.MakeRounded(transform, label + " Border", c, size, BtnR,
                col, ZBorder, QBorder);
            var fill = UITheme.MakeRounded(transform, label + " Fill", c,
                size - Vector2.one * 0.004f, Mathf.Max(0f, BtnR - 0.002f),
                UITheme.SurfaceFill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, label, col, BtnSize,
                TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(c.x, c.y, ZText);

            _btns.Add(new Btn
            {
                center = c, size = size, radius = BtnR, action = action, edge = col,
                enabled = enabled,
                fillMat = fill.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
            });
        }

        public void Tick(VRPointer pointer)
        {
            if (pointer == null || !_built) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            int idx = hit ? Find(local) : -1;
            if (idx != _hoverIdx) { _hoverIdx = idx; ApplyHover(); Refresh(); }

            if (_hoverIdx >= 0 && pointer.ClickDown)
            {
                VRPointer.Haptic();
                Chosen?.Invoke(_btns[_hoverIdx].action);
            }
        }

        // Kapali dugme HOT DEGIL: gorunur ama tiklanmaz.
        int Find(Vector2 p)
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                if (!b.enabled) continue;
                if (Mathf.Abs(p.x - b.center.x) <= b.size.x * 0.5f &&
                    Mathf.Abs(p.y - b.center.y) <= b.size.y * 0.5f)
                    return i;
            }
            return -1;
        }

        void ApplyHover()
        {
            bool on = _hoverIdx >= 0;
            if (_hover.gameObject.activeSelf != on) _hover.gameObject.SetActive(on);
            if (!on) return;

            var b = _btns[_hoverIdx];
            _hoverMesh.sharedMesh = UIMesh.RoundedRect(b.size.x, b.size.y, b.radius);
            _hover.localPosition = new Vector3(b.center.x, b.center.y, ZHover);
        }

        void Refresh()
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                bool on = i == _hoverIdx;
                UITheme.SetMaterialColor(b.borderMat,
                    on ? b.edge : new Color(b.edge.r, b.edge.g, b.edge.b, 0.55f));
                UITheme.SetMaterialColor(b.fillMat,
                    on ? Color.Lerp(UITheme.SurfaceFill, b.edge, 0.18f) : UITheme.SurfaceFill);
            }
        }
    }
}
