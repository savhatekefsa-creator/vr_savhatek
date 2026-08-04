using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// HARITA ISIMLENDIRME: klavye + ad alani + KAYDET / GERI.
    ///
    /// NEDEN AYRI BIR KLAVYE (PlayerEntryPanel'inki dururken): oradaki klavye o ekranin
    /// tasarim-piksel koordinat sistemine (X/Y/S/Box/Dim) ve takim paneliyle ortak eleman
    /// listesine gomulu. Sokup ortak hale getirmek her oturumda kullanilan CALISAN bir ekrani
    /// riske atardi; buradaki klavye metre olculu ve tek isi var. Ikisi ayni gorsel dili
    /// paylasiyor (UITheme), yalnizca yerlesim kodu ayri.
    ///
    /// AD AYNI ZAMANDA DOSYA ADI. Cakisma ve gecersiz ad KAYDET'e basilmadan once soyleniyor:
    /// sunucuya gidip reddedilmeyi beklemek, oyuncunun yazdigini kaybetmesi demek olurdu.
    /// Kontrol MapCatalog uzerinden — gozlukte o liste sunucudan gelmis olandir.
    /// </summary>
    public class NameEntryPanel : MonoBehaviour
    {
        /// <summary>KAYDET'e basildi, ad gecerli.</summary>
        public event Action<string> Confirmed;

        /// <summary>GERI'ye basildi.</summary>
        public event Action Cancelled;

        public const int MaxLength = 20;

        const float PanelW = 0.94f, PanelH = 0.60f, PanelR = 0.020f, PanelEdgeW = 0.0025f;

        const float KeyW = 0.070f, KeyH = 0.058f, KeyR = 0.008f, KeyGapX = 0.008f, KeyGapY = 0.008f;
        const float KeysTopY = 0.070f;

        const float FieldY = 0.190f, FieldW = 0.80f, FieldH = 0.080f;
        const float TitleY = 0.262f;
        const float ActionY = -0.238f;

        const float TitleSize = 0.030f, NameSize = 0.044f, HintSize = 0.018f,
                    KeySize = 0.026f, ActionSize = 0.024f;

        const int QBack = 3004, QBorder = 3012, QFill = 3016, QHover = 3020, QText = 3030;
        const float ZBack = 0.006f, ZBorder = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZText = 0f;

        static readonly Color HoverCol = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        class Key
        {
            public Vector2 center, size;
            public float radius;
            public char ch;          // '\0' = harf degil
            public string action;    // "sil" | "bosluk" | "kaydet" | "geri"
            public Color edge;
            public Material fillMat, borderMat;
        }

        readonly List<Key> _keys = new List<Key>();
        readonly StringBuilder _sb = new StringBuilder(MaxLength);

        TextMesh _nameText, _hintText;
        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;
        bool _built;

        public void Setup(string title, string initialName)
        {
            if (_built) return;
            _built = true;

            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, UITheme.PanelEdge, UITheme.PanelBg,
                PanelEdgeW, ZBack, QBack, QBack + 1);

            var t = UITheme.MakeText(transform, title, UITheme.AccentCyan, TitleSize,
                TextAnchor.MiddleCenter, QText);
            t.transform.localPosition = new Vector3(0f, TitleY, ZText);

            UITheme.MakeOutlined(transform, "Field", new Vector2(0f, FieldY),
                new Vector2(FieldW, FieldH), 0.012f, UITheme.SurfaceEdge, UITheme.SurfaceFill,
                0.0015f, ZBorder, QBorder, QFill);

            _nameText = UITheme.MakeText(transform, "", UITheme.TextPrimary, NameSize,
                TextAnchor.MiddleLeft, QText);
            _nameText.transform.localPosition = new Vector3(-FieldW * 0.5f + 0.024f, FieldY, ZText);

            _hintText = UITheme.MakeText(transform, "", UITheme.TextMuted, HintSize,
                TextAnchor.MiddleCenter, QText);
            _hintText.transform.localPosition = new Vector3(0f, FieldY - 0.062f, ZText);

            BuildKeys();

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            if (!string.IsNullOrEmpty(initialName))
                _sb.Append(initialName.Length > MaxLength
                    ? initialName.Substring(0, MaxLength) : initialName);

            RefreshName();
            RefreshKeys();
        }

        // Sira sira harf/rakam; alt sirada islem tuslari. Sade bir izgara — tasarim pikselli
        // bir mockup'a bagli olmadigi icin dogrudan metre.
        void BuildKeys()
        {
            string[] rows =
            {
                "1234567890",
                "QWERTYUIOP",
                "ASDFGHJKL",
                "ZXCVBNM",
            };

            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                float totalW = row.Length * KeyW + (row.Length - 1) * KeyGapX;
                float x0 = -totalW * 0.5f + KeyW * 0.5f;
                float y = KeysTopY - r * (KeyH + KeyGapY);

                for (int i = 0; i < row.Length; i++)
                {
                    AddKey(new Vector2(x0 + i * (KeyW + KeyGapX), y), new Vector2(KeyW, KeyH),
                        row[i].ToString(), KeySize, UITheme.SurfaceEdge, row[i], null);
                }
            }

            float ry = KeysTopY - rows.Length * (KeyH + KeyGapY);
            AddKey(new Vector2(-0.30f, ry), new Vector2(0.20f, KeyH), "SİL", ActionSize,
                UITheme.TeamRedEdge, '\0', "sil");
            AddKey(new Vector2(-0.05f, ry), new Vector2(0.26f, KeyH), "BOŞLUK", ActionSize,
                UITheme.SurfaceEdge, '\0', "bosluk");
            AddKey(new Vector2(0.24f, ry), new Vector2(0.20f, KeyH), "TEMİZLE", ActionSize,
                UITheme.SurfaceEdge, '\0', "temizle");

            AddKey(new Vector2(-0.17f, ActionY), new Vector2(0.30f, 0.066f), "KAYDET",
                ActionSize + 0.002f, UITheme.AccentCyan, '\0', "kaydet");
            AddKey(new Vector2(+0.19f, ActionY), new Vector2(0.24f, 0.066f), "GERİ",
                ActionSize, UITheme.TextMuted, '\0', "geri");
        }

        void AddKey(Vector2 center, Vector2 size, string label, float textSize, Color edge,
            char ch, string action)
        {
            var border = UITheme.MakeRounded(transform, "K " + label, center, size, KeyR,
                edge, ZBorder, QBorder);
            var fill = UITheme.MakeRounded(transform, "KF " + label, center,
                size - Vector2.one * 0.003f, Mathf.Max(0f, KeyR - 0.002f),
                UITheme.SurfaceFill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, label, UITheme.TextPrimary, textSize,
                TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(center.x, center.y, ZText);

            _keys.Add(new Key
            {
                center = center, size = size, radius = KeyR, ch = ch, action = action, edge = edge,
                fillMat = fill.GetComponent<MeshRenderer>().sharedMaterial,
                borderMat = border.GetComponent<MeshRenderer>().sharedMaterial,
            });
        }

        // ------------------------------------------------------------------ surus

        public void Tick(VRPointer pointer)
        {
            if (pointer == null || !_built) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            int idx = hit ? Find(local) : -1;
            if (idx != _hoverIdx) { _hoverIdx = idx; ApplyHover(); RefreshKeys(); }

            if (_hoverIdx < 0 || !pointer.ClickDown) return;

            VRPointer.Haptic();
            Press(_keys[_hoverIdx]);
        }

        void Press(Key k)
        {
            if (k.ch != '\0')
            {
                if (_sb.Length < MaxLength) _sb.Append(k.ch);
                RefreshName();
                return;
            }

            switch (k.action)
            {
                case "sil":
                    if (_sb.Length > 0) _sb.Length--;
                    RefreshName();
                    break;

                case "bosluk":
                    if (_sb.Length > 0 && _sb.Length < MaxLength) _sb.Append(' ');
                    RefreshName();
                    break;

                case "temizle":
                    _sb.Length = 0;
                    RefreshName();
                    break;

                case "geri":
                    Cancelled?.Invoke();
                    break;

                case "kaydet":
                    // Gecersiz adda SESSIZ KALMA: sebep alanin altinda zaten yaziyor, tiklama
                    // da bosa gitmesin diye burada yalnizca gecerliyse ilerliyoruz.
                    if (Valid(out _)) Confirmed?.Invoke(_sb.ToString().Trim());
                    break;
            }
        }

        /// <summary>Ad kaydedilebilir mi? Degilse <paramref name="sebep"/> alanin altinda yazar.</summary>
        bool Valid(out string sebep)
        {
            string ad = _sb.ToString().Trim();

            if (ad.Length == 0) { sebep = "Bir ad yaz."; return false; }

            if (!MapCatalog.NameAvailable(ad))
            {
                // Ad = dosya adi: "A B" ile "A_B" ayni dosyaya duser, o yuzden cakisma
                // gorundugunden genis (bkz. MapCatalog.NameAvailable).
                sebep = "'" + ad + "' zaten var — baska bir ad seç.";
                return false;
            }

            sebep = null;
            return true;
        }

        void RefreshName()
        {
            bool bos = _sb.Length == 0;
            _nameText.text = bos ? "Harita adı" : _sb.ToString();
            _nameText.color = bos ? UITheme.TextDim : UITheme.TextPrimary;

            Valid(out string sebep);
            _hintText.text = sebep ?? (_sb.Length + " / " + MaxLength);
            _hintText.color = sebep != null ? UITheme.TeamRedText : UITheme.TextMuted;
        }

        int Find(Vector2 p)
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

        void ApplyHover()
        {
            bool on = _hoverIdx >= 0;
            if (_hover.gameObject.activeSelf != on) _hover.gameObject.SetActive(on);
            if (!on) return;

            var k = _keys[_hoverIdx];
            _hoverMesh.sharedMesh = UIMesh.RoundedRect(k.size.x, k.size.y, k.radius);
            _hover.localPosition = new Vector3(k.center.x, k.center.y, ZHover);
        }

        void RefreshKeys()
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                var k = _keys[i];
                bool on = i == _hoverIdx;
                UITheme.SetMaterialColor(k.borderMat,
                    on ? k.edge : new Color(k.edge.r, k.edge.g, k.edge.b, 0.55f));
                UITheme.SetMaterialColor(k.fillMat,
                    on ? Color.Lerp(UITheme.SurfaceFill, k.edge, 0.18f) : UITheme.SurfaceFill);
            }
        }
    }
}
