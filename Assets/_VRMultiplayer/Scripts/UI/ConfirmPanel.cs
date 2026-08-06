using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// IKI SECENEKLI KARAR EKRANI — "Kaydet?", "Degisiklikleri at?", "Havuza eklensin mi?".
    ///
    /// UCU AYRI PANEL DEGIL, TEK PANEL UC KEZ: uc ekranin da isi ayni (bir baslik, bir sebep,
    /// iki dugme). Ayri siniflar yazmak uc ayri yerde ayni olcuyu, ayni paleti ve ayni tiklama
    /// mantigini tutmak demekti; biri degisince digerleri sessizce kayardi.
    ///
    /// GERI DONUSSUZ SECENEK KIRMIZI. "Degisiklikleri at" ile "Kaydet"in ayni renkte olmasi,
    /// yanlis dugmeye basmayi ucuzlatirdi — semada da o kutu kirmizi cizili.
    ///
    /// SETUP AWAKE'TEN ONCE: panel eklenir eklenmez <see cref="Setup"/> cagrilir ve gorsel
    /// orada kurulur. Awake'te kurulsaydi metinleri sonradan yazmak gerekirdi.
    /// </summary>
    public class ConfirmPanel : MonoBehaviour
    {
        /// <summary>true = ONAY dugmesi, false = VAZGEC.</summary>
        public event Action<bool> Answered;

        const float PanelW = 0.72f, PanelH = 0.34f, PanelR = 0.020f, PanelEdgeW = 0.0025f;
        const float BtnW = 0.28f, BtnH = 0.070f, BtnR = 0.012f, BtnY = -0.098f, BtnX = 0.155f;

        const float TitleY = 0.108f, MsgY = 0.020f;

        // MsgSize 0.021 idi: 1.4 m'de 0.86 derece, yani VR'da rahat okuma esiginin (~1 derece,
        // bkz. PlayerEntryPanel olcu notu) altinda. Bu panelin metni sussuz degil — oyuncuya
        // NE YAPMASI gerektigini soyleyen tek cumle o ("harita tasarla", "sunucuyu ac").
        // Baslik zaten dikkat cekiyor; asil okunmasi gereken govdeydi.
        const float TitleSize = 0.040f, MsgSize = 0.026f, BtnSize = 0.026f;

        const int QBack = 3004, QBorder = 3012, QFill = 3016, QHover = 3020, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZText = 0f;

        static readonly Color HoverCol = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        class Btn
        {
            public Vector2 center, size;
            public float radius;
            public bool yes;
            public Color edge;
            public Material fillMat, borderMat;
        }

        readonly List<Btn> _btns = new List<Btn>();
        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;
        bool _built;

        /// <param name="yesEdge">Onay dugmesinin rengi. Geri donussuz islemde KIRMIZI verilir.</param>
        /// <summary>Panelin metinleri — masaustu yedegi bunlari cizer (gozluksuz iterasyon).</summary>
        public string Title { get; private set; }
        public string YesLabel { get; private set; }
        public string NoLabel { get; private set; }

        /// <summary>Masaustu yedeginden cevap: lazer imlec olmadan da akis surulebilsin.</summary>
        public void AnswerFromDesktop(bool yes) => Answered?.Invoke(yes);

        public void Setup(string title, string message, string yesLabel, string noLabel, Color yesEdge)
        {
            if (_built) return;
            _built = true;

            Title = title;
            YesLabel = yesLabel;
            NoLabel = noLabel;

            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, UITheme.PanelEdge, UITheme.PanelBg,
                PanelEdgeW, ZBack, QBack, QBack + 1);

            var t = UITheme.MakeText(transform, title, UITheme.TextPrimary, TitleSize,
                TextAnchor.MiddleCenter, QText);
            t.transform.localPosition = new Vector3(0f, TitleY, ZText);

            if (!string.IsNullOrEmpty(message))
            {
                var m = UITheme.MakeText(transform, message, UITheme.TextMuted, MsgSize,
                    TextAnchor.MiddleCenter, QText);
                m.transform.localPosition = new Vector3(0f, MsgY, ZText);
            }

            AddButton(-BtnX, yesLabel, true, yesEdge);
            AddButton(+BtnX, noLabel, false, UITheme.TextMuted);

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            Refresh();
        }

        void AddButton(float x, string label, bool yes, Color edge)
        {
            var c = new Vector2(x, BtnY);
            var size = new Vector2(BtnW, BtnH);

            var border = UITheme.MakeRounded(transform, label + " Border", c, size, BtnR,
                edge, ZBorder, QBorder);
            var fill = UITheme.MakeRounded(transform, label + " Fill", c,
                size - Vector2.one * 0.004f, Mathf.Max(0f, BtnR - 0.002f),
                UITheme.SurfaceFill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, label, edge, BtnSize,
                TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(c.x, c.y, ZText);

            _btns.Add(new Btn
            {
                center = c, size = size, radius = BtnR, yes = yes, edge = edge,
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
                Answered?.Invoke(_btns[_hoverIdx].yes);
            }
        }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
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
