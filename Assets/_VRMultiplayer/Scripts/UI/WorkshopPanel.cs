using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Silah Atolyesi'nin paneli. Gorsel dil projedeki diger panellerle AYNI
    /// (<see cref="UITheme"/>, <see cref="UIMesh"/>, <see cref="VRPointer"/>).
    ///
    /// TEK GIRDI: lazer + tetik, akor yok.
    ///
    /// BASILI TUTUNCA TEKRAR: bilek ve parmak tuslari tek tiklamayla 1 mm / 1 derece
    /// ilerliyor; 40 mm'lik bir duzeltme 40 tiklama demekti. Kisa bir gecikmeden sonra
    /// tus kendini tekrarliyor ve tekrar araligi hizlaniyor - kaba yerlestirme saniyeler
    /// suruyor, son 1 mm hala tek tikla veriliyor. Yalnizca YON tuslari tekrarlanir;
    /// KAYDET/KAPAT gibi geri donusu olan tuslar asla.
    /// </summary>
    public class WorkshopPanel : MonoBehaviour
    {
        public WeaponWorkshop Host;
        public Vector3 Bench;
        public Vector3 BenchForward;

        const float PanelW = 1.04f, PanelH = 0.90f, PanelR = 0.020f, PanelEdge = 0.0025f;
        const float RowH = 0.054f, RowGap = 0.008f, BtnW = 0.088f;

        // Iki sutun: solda bilek eksenleri, sagda parmaklar.
        const float LeftLabelX = -0.485f, LeftMinusX = -0.145f, LeftPlusX = -0.048f;
        const float RightLabelX = 0.055f, RightMinusX = 0.330f, RightPlusX = 0.427f;

        const int QBorder = 3012, QFill = 3016, QHover = 3020, QText = 3030;
        const float ZBorder = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZText = 0f;

        // Basili tutma: ilk tekrar bu gecikmeden sonra, sonra araliklar kisaliyor.
        const float RepeatDelay = 0.45f, RepeatFast = 0.05f, RepeatSlow = 0.14f, RepeatRamp = 1.2f;

        enum Cmd { PrevWeapon, NextWeapon, Hands, StepSize, Save, Revert, Close, Bench, Side, Axis, Finger }

        class Btn
        {
            public Vector2 center, size;
            public float radius;
            public Cmd cmd;
            public int index, sign;
            public bool rotate, repeatable;
            public Color edge;
            public Material fill, border;
            public TextMesh label;
        }

        static readonly string[] FingerNames = { "BASPARMAK", "ISARET", "ORTA", "YUZUK", "SERCE" };

        readonly List<Btn> _btns = new List<Btn>();
        readonly TextMesh[] _curlText = new TextMesh[5];
        Transform _hover;
        MeshFilter _hoverMesh;
        TextMesh _title, _values, _status, _stepLabel, _handsLabel, _sideLabel;
        int _hoverIdx = -1;
        bool _built;
        float _statusUntil;

        // Basili tutma durumu
        int _heldIdx = -1;
        float _nextRepeat, _repeatInterval;

        public void BuildUI()
        {
            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero, new Vector2(PanelW, PanelH),
                PanelR, UITheme.PanelEdge, UITheme.PanelBg, PanelEdge, 0.006f, 3004, 3008);

            _title = UITheme.MakeText(transform, "SILAH ATOLYESI", UITheme.AccentCyan, 0.034f,
                TextAnchor.MiddleCenter, QText);
            _title.transform.localPosition = new Vector3(0f, 0.395f, ZText);

            AddBtn(new Vector2(-0.40f, 0.318f), new Vector2(0.10f, RowH), Cmd.PrevWeapon, "◀", UITheme.AccentCyan);
            AddBtn(new Vector2(-0.14f, 0.318f), new Vector2(0.10f, RowH), Cmd.NextWeapon, "▶", UITheme.AccentCyan);
            _handsLabel = AddBtn(new Vector2(0.10f, 0.318f), new Vector2(0.26f, RowH), Cmd.Hands,
                "ELLERI KOY", UITheme.AccentPurple);
            AddBtn(new Vector2(0.39f, 0.318f), new Vector2(0.24f, RowH), Cmd.Bench,
                "ONUME GETIR", UITheme.TextMuted);

            _sideLabel = AddBtn(new Vector2(-0.30f, 0.246f), new Vector2(0.30f, RowH), Cmd.Side,
                "DUZENLENEN: SAG", UITheme.AccentCyan);
            _stepLabel = AddBtn(new Vector2(0.04f, 0.246f), new Vector2(0.28f, RowH), Cmd.StepSize,
                "ADIM: INCE", UITheme.TextMuted);

            // --- Sol sutun: bilek
            float y = 0.150f;
            AddSectionTitle(LeftLabelX, y + 0.048f, "BILEK");
            AddAxis(y, "ILERI / GERI", 0, false); y -= RowH + RowGap;
            AddAxis(y, "SAG / SOL", 1, false); y -= RowH + RowGap;
            AddAxis(y, "YUKARI / ASAGI", 2, false); y -= RowH + RowGap + 0.012f;
            AddAxis(y, "YAW", 0, true); y -= RowH + RowGap;
            AddAxis(y, "PITCH", 1, true); y -= RowH + RowGap;
            AddAxis(y, "ROLL", 2, true);

            // --- Sag sutun: parmaklar
            float fy = 0.150f;
            AddSectionTitle(RightLabelX, fy + 0.048f, "PARMAK KIVRIMI");
            for (int f = 0; f < 5; f++)
            {
                AddFingerRow(fy, f);
                fy -= RowH + RowGap;
            }

            _values = UITheme.MakeText(transform, "", UITheme.TextMuted, 0.021f, TextAnchor.MiddleCenter, QText);
            _values.transform.localPosition = new Vector3(-0.26f, -0.300f, ZText);

            AddBtn(new Vector2(-0.40f, -0.395f), new Vector2(0.24f, RowH), Cmd.Save, "KAYDET", UITheme.AccentCyan);
            AddBtn(new Vector2(-0.11f, -0.395f), new Vector2(0.30f, RowH), Cmd.Revert,
                "KAYITLIYA DON", UITheme.TextMuted);
            AddBtn(new Vector2(0.42f, -0.395f), new Vector2(0.18f, RowH), Cmd.Close, "KAPAT", UITheme.TeamRedEdge);

            _status = UITheme.MakeText(transform, "", UITheme.AccentCyan, 0.019f, TextAnchor.MiddleCenter, QText);
            _status.transform.localPosition = new Vector3(0.16f, -0.320f, ZText);

            var h = UITheme.MakeShape(transform, "Hover", UIMesh.RoundedRect(0.1f, 0.05f, 0.012f),
                new Color(0.35f, 0.62f, 0.75f, 0.30f), QHover);
            _hover = h;
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover.gameObject.SetActive(false);

            _built = true;
            Refresh();
        }

        void AddSectionTitle(float x, float y, string text)
        {
            var t = UITheme.MakeText(transform, text, UITheme.AccentCyan, 0.021f, TextAnchor.MiddleLeft, QText);
            t.transform.localPosition = new Vector3(x, y, ZText);
        }

        void AddAxis(float y, string label, int axis, bool rotate)
        {
            var t = UITheme.MakeText(transform, label, UITheme.TextDim, 0.019f, TextAnchor.MiddleLeft, QText);
            t.transform.localPosition = new Vector3(LeftLabelX, y, ZText);
            AddBtn(new Vector2(LeftMinusX, y), new Vector2(BtnW, RowH), Cmd.Axis, "−", UITheme.AccentCyan);
            AddBtn(new Vector2(LeftPlusX, y), new Vector2(BtnW, RowH), Cmd.Axis, "+", UITheme.AccentCyan);
            var a = _btns[_btns.Count - 2]; a.index = axis; a.sign = -1; a.rotate = rotate; a.repeatable = true;
            var b = _btns[_btns.Count - 1]; b.index = axis; b.sign = 1; b.rotate = rotate; b.repeatable = true;
        }

        void AddFingerRow(float y, int finger)
        {
            var t = UITheme.MakeText(transform, FingerNames[finger], UITheme.TextDim, 0.019f,
                TextAnchor.MiddleLeft, QText);
            t.transform.localPosition = new Vector3(RightLabelX, y, ZText);

            _curlText[finger] = UITheme.MakeText(transform, "0.00", UITheme.TextMuted, 0.019f,
                TextAnchor.MiddleCenter, QText);
            _curlText[finger].transform.localPosition = new Vector3(RightLabelX + 0.225f, y, ZText);

            AddBtn(new Vector2(RightMinusX, y), new Vector2(BtnW, RowH), Cmd.Finger, "−", UITheme.AccentPurple);
            AddBtn(new Vector2(RightPlusX, y), new Vector2(BtnW, RowH), Cmd.Finger, "+", UITheme.AccentPurple);
            var a = _btns[_btns.Count - 2]; a.index = finger; a.sign = -1; a.repeatable = true;
            var b = _btns[_btns.Count - 1]; b.index = finger; b.sign = 1; b.repeatable = true;
        }

        TextMesh AddBtn(Vector2 center, Vector2 size, Cmd cmd, string label, Color edge)
        {
            var border = UITheme.MakeRounded(transform, label + " B", center, size, 0.012f, edge, ZBorder, QBorder);
            var fill = UITheme.MakeRounded(transform, label + " F", center,
                size - Vector2.one * 0.004f, 0.010f, UITheme.SurfaceFill, ZFill, QFill);
            var tm = UITheme.MakeText(transform, label, edge, 0.021f, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(center.x, center.y, ZText);

            _btns.Add(new Btn
            {
                center = center, size = size, radius = 0.012f, cmd = cmd, edge = edge, label = tm,
                fill = fill.GetComponent<MeshRenderer>().sharedMaterial,
                border = border.GetComponent<MeshRenderer>().sharedMaterial,
            });
            return tm;
        }

        public void Tick(VRPointer pointer)
        {
            if (!_built || pointer == null || Host == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            int idx = hit ? Find(local) : -1;
            if (idx != _hoverIdx) { _hoverIdx = idx; ApplyHover(); }

            if (_hoverIdx >= 0 && pointer.ClickDown)
            {
                VRPointer.Haptic();
                Run(_btns[_hoverIdx]);
                if (_btns[_hoverIdx].repeatable)
                {
                    _heldIdx = _hoverIdx;
                    _nextRepeat = Time.time + RepeatDelay;
                    _repeatInterval = RepeatSlow;
                }
            }

            // Tetigi birakinca veya isin tustan cikinca tekrar durur - kaza ile
            // baska bir tusa "tasinmasin".
            if (!pointer.ClickHeld || _hoverIdx != _heldIdx) _heldIdx = -1;

            if (_heldIdx >= 0 && Time.time >= _nextRepeat)
            {
                Run(_btns[_heldIdx]);
                _repeatInterval = Mathf.Max(RepeatFast, _repeatInterval / RepeatRamp);
                _nextRepeat = Time.time + _repeatInterval;
            }

            if (_statusUntil > 0f && Time.time > _statusUntil) { _status.text = ""; _statusUntil = 0f; }
            Refresh();
        }

        void Run(Btn b)
        {
            switch (b.cmd)
            {
                case Cmd.PrevWeapon: Host.Step(-1); break;
                case Cmd.NextWeapon: Host.Step(1); break;
                case Cmd.Hands: Host.ToggleHands(); break;
                case Cmd.StepSize: Host.Coarse = !Host.Coarse; break;
                case Cmd.Side: Host.EditLeft = !Host.EditLeft; break;
                case Cmd.Revert: Host.Revert(); Say("kayitli hale donuldu (sag+sol)"); break;
                case Cmd.Save: Say(Host.Save()); break;
                case Cmd.Close: Host.open = false; break;
                case Cmd.Bench: Host.PlaceBench(); break;
                case Cmd.Finger: Host.Curl(b.index, b.sign); break;
                case Cmd.Axis:
                    if (b.rotate) Host.Turn(b.index, b.sign);
                    else Host.Nudge(b.index, b.sign);
                    break;
            }
        }

        void Say(string s) { _status.text = s; _statusUntil = Time.time + 3f; }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                if (Mathf.Abs(p.x - b.center.x) <= b.size.x * 0.5f &&
                    Mathf.Abs(p.y - b.center.y) <= b.size.y * 0.5f) return i;
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
            _title.text = Host.WeaponName + (Host.HasProfile ? "" : "   [PROFIL YOK]");
            _values.text = Host.ValueText();
            _handsLabel.text = Host.HandsPlaced ? "ELLERI KALDIR" : "ELLERI KOY";
            _stepLabel.text = Host.Coarse ? "ADIM: KABA" : "ADIM: INCE";
            _sideLabel.text = Host.EditLeft ? "DUZENLENEN: SOL" : "DUZENLENEN: SAG";
            _sideLabel.color = Host.EditLeft ? UITheme.AccentPurple : UITheme.AccentCyan;

            for (int f = 0; f < 5; f++)
                if (_curlText[f] != null) _curlText[f].text = Host.CurlOf(f).ToString("F2");

            if (Host.UnsavedCount > 0 && string.IsNullOrEmpty(_status.text))
                _status.text = "aktarilmayi bekleyen: " + Host.UnsavedCount;

            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                bool on = i == _hoverIdx;
                UITheme.SetMaterialColor(b.border, on ? b.edge
                    : new Color(b.edge.r, b.edge.g, b.edge.b, 0.55f));
                UITheme.SetMaterialColor(b.fill, on
                    ? Color.Lerp(UITheme.SurfaceFill, b.edge, 0.18f) : UITheme.SurfaceFill);
            }
        }
    }
}
