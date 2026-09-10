using System.Collections.Generic;
using UnityEngine;
using TMPro;
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

        enum Cmd { PrevWeapon, NextWeapon, Hands, StepSize, Save, Revert, Close, Bench, Side, Role, Axis,
                   Finger, FingerPose, FingerReset }

        class Btn
        {
            public Vector2 center, size;
            public float radius;
            public Cmd cmd;
            public int index, sign;
            public bool rotate, repeatable;
            public Color edge;
            public Material fill, border;
            public TextMeshPro label;
        }

        // Sol-ana kipinin rengi: panelin camgobegi/mor ikilisinin DISINDA bir ton. Amac
        // "bu kip acikken buradasin" uyarisi vermek - kipte kaldigini fark etmeden sag-el
        // pozlarini bozmak en kolay hata.
        static readonly Color RoleOn = new Color(0.98f, 0.68f, 0.28f, 1f);

        static readonly string[] FingerNames = { "BAŞPARMAK", "İŞARET", "ORTA", "YÜZÜK", "SERÇE" };

        readonly List<Btn> _btns = new List<Btn>();
        readonly TextMeshPro[] _curlText = new TextMeshPro[5];
        Transform _hover;
        MeshFilter _hoverMesh;
        TextMeshPro _title, _values, _status, _stepLabel, _handsLabel, _sideLabel, _roleLabel;
        TextMeshPro _poseLabel, _poseHint;
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

            _title = UITheme.MakeText(transform, "SİLAH ATÖLYESİ", UITheme.AccentCyan, 0.034f,
                TextAnchor.MiddleCenter, QText);
            _title.transform.localPosition = new Vector3(0f, 0.395f, ZText);

            AddBtn(new Vector2(-0.40f, 0.318f), new Vector2(0.10f, RowH), Cmd.PrevWeapon, "◀", UITheme.AccentCyan);
            AddBtn(new Vector2(-0.14f, 0.318f), new Vector2(0.10f, RowH), Cmd.NextWeapon, "▶", UITheme.AccentCyan);
            _handsLabel = AddBtn(new Vector2(0.10f, 0.318f), new Vector2(0.26f, RowH), Cmd.Hands,
                "ELLERİ KOY", UITheme.AccentPurple);
            AddBtn(new Vector2(0.39f, 0.318f), new Vector2(0.24f, RowH), Cmd.Bench,
                "ÖNÜME GETİR", UITheme.TextMuted);

            _sideLabel = AddBtn(new Vector2(-0.30f, 0.246f), new Vector2(0.30f, RowH), Cmd.Side,
                "DÜZENLENEN: SAĞ", UITheme.AccentCyan);
            _stepLabel = AddBtn(new Vector2(0.04f, 0.246f), new Vector2(0.28f, RowH), Cmd.StepSize,
                "ADIM: İNCE", UITheme.TextMuted);

            // SOL ELIN ROLU. Pozlar sag-el-ana varsayimiyla yazildi; solak oyuncuda roller
            // ters doner ve o pozlar AYRI alanlarda durur. Bu dugme hangi alan ciftine
            // yazdigimizi secer - tezgahin geri kalani (kabza cipasi, ray, pim, parmak kipi)
            // role baktigi icin kip degisince kendiliginden yer degistirir.
            _roleLabel = AddBtn(new Vector2(0.36f, 0.246f), new Vector2(0.30f, RowH), Cmd.Role,
                "SOL EL: DESTEK", UITheme.TextMuted);

            // --- Sol sutun: bilek
            float y = 0.150f;
            AddSectionTitle(LeftLabelX, y + 0.048f, "BİLEK");
            AddAxis(y, "İLERİ / GERİ", 0, false); y -= RowH + RowGap;
            AddAxis(y, "SAĞ / SOL", 1, false); y -= RowH + RowGap;
            AddAxis(y, "YUKARI / AŞAĞI", 2, false); y -= RowH + RowGap + 0.012f;
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

            // Serbest poz kipi: parmak sutununun ALTINDA, cunku kivrim tuslarinin
            // alternatifi. Yan yana iki dugme — kipi ac/kapa ve parmaklari duzle.
            //
            // SAG KENAR TURETILIYOR. Elle yazilan 0.46 + 0.16/2 = 0.54 idi, panelin kendisi
            // 0.52'de bitiyor: DUZLE cerceveden 2 cm disari tasiyordu. Artik ikisi de
            // ustlerindeki "+" tusunun sag kenarina hizalaniyor, yani sutun kayarsa birlikte
            // kayiyorlar.
            const float SagKenar = RightPlusX + BtnW / 2f;
            const float DuzleW = 0.15f, KipW = 0.28f, Aralik = 0.012f;
            float duzleX = SagKenar - DuzleW / 2f;
            float kipX = SagKenar - DuzleW - Aralik - KipW / 2f;

            _poseLabel = AddBtn(new Vector2(kipX, fy - 0.010f), new Vector2(KipW, RowH), Cmd.FingerPose,
                "PARMAK KİPİ", UITheme.AccentPurple);
            AddBtn(new Vector2(duzleX, fy - 0.010f), new Vector2(DuzleW, RowH), Cmd.FingerReset,
                "DÜZLE", UITheme.TextMuted);

            _poseHint = UITheme.MakeText(transform, "", UITheme.AccentPurple, 0.018f,
                TextAnchor.MiddleCenter, QText);
            _poseHint.transform.localPosition = new Vector3(0.24f, fy - 0.066f, ZText);

            _values = UITheme.MakeText(transform, "", UITheme.TextMuted, 0.021f, TextAnchor.MiddleCenter, QText);
            _values.transform.localPosition = new Vector3(-0.26f, -0.300f, ZText);

            AddBtn(new Vector2(-0.40f, -0.395f), new Vector2(0.24f, RowH), Cmd.Save, "KAYDET", UITheme.AccentCyan);
            AddBtn(new Vector2(-0.11f, -0.395f), new Vector2(0.30f, RowH), Cmd.Revert,
                "KAYITLIYA DÖN", UITheme.TextMuted);
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

            // SAGA YASLI, tusun sol kenarindan turetilmis. Ortalanmisken 0,00 yazisi
            // "−" tusunun uzerine 1,2 cm biniyordu; ustelik ortalama, metin uzunlugu
            // degisince (0.00 / 1.00 / — ) tasma miktarini da degistiriyordu. Saga
            // yaslayinca sag kenar sabit kaliyor, sayi ne olursa olsun tusa girmiyor -
            // sayi sutunu da boylece hizali okunuyor.
            _curlText[finger] = UITheme.MakeText(transform, "0.00", UITheme.TextMuted, 0.019f,
                TextAnchor.MiddleRight, QText);
            _curlText[finger].transform.localPosition =
                new Vector3(RightMinusX - BtnW / 2f - 0.010f, y, ZText);

            AddBtn(new Vector2(RightMinusX, y), new Vector2(BtnW, RowH), Cmd.Finger, "−", UITheme.AccentPurple);
            AddBtn(new Vector2(RightPlusX, y), new Vector2(BtnW, RowH), Cmd.Finger, "+", UITheme.AccentPurple);
            var a = _btns[_btns.Count - 2]; a.index = finger; a.sign = -1; a.repeatable = true;
            var b = _btns[_btns.Count - 1]; b.index = finger; b.sign = 1; b.repeatable = true;
        }

        TextMeshPro AddBtn(Vector2 center, Vector2 size, Cmd cmd, string label, Color edge)
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

            // 8 Hz. Refresh 13 metin yaziyor, Host.ValueText() string.Format (6 float boxing)
            // ve 5 parmak icin ToString("F2") uretiyordu — panel acikken kare basina ~8 heap
            // allocation, surekli GC. Elle ayar icin 8 Hz fazlasiyla yeterli (ayni desen:
            // ScoreboardUI 2 Hz).
            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + 0.125f;
            Refresh();
        }

        float _nextRefresh;

        void Run(Btn b)
        {
            switch (b.cmd)
            {
                case Cmd.PrevWeapon: Host.Step(-1); break;
                case Cmd.NextWeapon: Host.Step(1); break;
                case Cmd.Hands: Host.ToggleHands(); break;
                case Cmd.StepSize: Host.Coarse = !Host.Coarse; break;
                case Cmd.Side: Host.EditLeft = !Host.EditLeft; break;
                case Cmd.Role: Host.LeftIsMain = !Host.LeftIsMain; break;
                case Cmd.Revert: Host.Revert(); Say("kayitli hale donuldu (sag+sol)"); break;
                case Cmd.Save: Say(Host.Save()); break;
                case Cmd.Close: Host.open = false; break;
                case Cmd.Bench: Host.PlaceBench(); break;
                case Cmd.Finger: Host.Curl(b.index, b.sign); break;
                case Cmd.FingerPose:
                    if (!Host.HandsPlaced) { Say("önce ELLERİ KOY"); break; }
                    Host.FingerPoseMode = !Host.FingerPoseMode;
                    Say(Host.FingerPoseMode
                        ? "parmak kipi AÇIK — GRIP ile parmağı tut, sürükle"
                        : "parmak kipi kapandi, poz yazildi");
                    break;
                case Cmd.FingerReset:
                    Host.ResetFingers();
                    Say("parmaklar duzlendi");
                    break;
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
            _title.text = Host.WeaponName + (Host.HasProfile ? "" : "   [PROFİL YOK]");
            _values.text = Host.ValueText();
            _handsLabel.text = Host.HandsPlaced ? "ELLERİ KALDIR" : "ELLERİ KOY";
            _stepLabel.text = Host.Coarse ? "ADIM: KABA" : "ADIM: İNCE";
            _sideLabel.text = Host.EditLeft ? "DÜZENLENEN: SOL" : "DÜZENLENEN: SAĞ";
            _sideLabel.color = Host.EditLeft ? UITheme.AccentPurple : UITheme.AccentCyan;

            // Sol-ana kipi ALISILMISIN DISI oldugu icin turuncu; sonmuk griyle yazsak
            // kullanici kipte kaldigini fark etmeden sag-el pozlarini bozardi.
            _roleLabel.text = Host.LeftIsMain ? "SOL EL: ANA" : "SOL EL: DESTEK";
            _roleLabel.color = Host.LeftIsMain ? RoleOn : UITheme.TextMuted;

            bool posing = Host.FingerPoseMode;
            _poseLabel.text = posing ? "PARMAK KİPİ: AÇIK" : "PARMAK KİPİ";
            _poseLabel.color = posing ? UITheme.AccentCyan : UITheme.AccentPurple;
            _poseHint.text = Host.FingerPoseStatus;

            // Kip acikken kivrim sayilari YANILTICI olur: parmaklar artik tek bir kivrim
            // degerinden degil eklem eklem cozumden geliyor. Sayi yerine tire gosteriyoruz.
            for (int f = 0; f < 5; f++)
                if (_curlText[f] != null)
                    _curlText[f].text = posing ? "—" : Host.CurlOf(f).ToString("F2");

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
