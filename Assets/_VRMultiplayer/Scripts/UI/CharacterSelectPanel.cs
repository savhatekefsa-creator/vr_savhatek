using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// KARAKTER DUZENI ekraninin yuzeyi: ERKEK/KADIN sekmeleri + mankenin etrafina dizilmis
    /// ok ciftleri + sayaclar + RASTGELE / TAMAM. <see cref="CharacterSelectUI"/> kurar ve surer; manken bu
    /// panelin ~0.45 m ARKASINDA durur, oklar boylece degistirdikleri parcanin hizasinda
    /// gorunur (aksesuar oklari kafanin ustunde, kafa oklari kafanin yaninda...).
    ///
    /// ZEMIN YOK, panel SAYDAM: ekranin yildizi manken. PlayerEntryPanel'deki gibi opak bir
    /// kart cizilseydi manken kartin arkasinda kalirdi. Ayni sebepten olcu sistemi de farkli:
    /// tasarim pikseli degil DOGRUDAN METRE — konumlar mankenin vucut olculerine (kafa ~1.6 m,
    /// govde ~1.2 m) hizalaniyor, bir mockup'a degil.
    ///
    /// PIVOT ZEMINDE: yerel y dogrudan "yerden yukseklik" okunur, manken hizalamasi carpma
    /// duzlemiyle ayni koordinati kullanir (bkz. <see cref="VRPointer.Raycast"/>).
    /// </summary>
    public class CharacterSelectPanel : MonoBehaviour
    {
        /// <summary>Yuvalar — <see cref="CharacterSelectUI"/> ile paylasilan sozlesme.</summary>
        public const int SlotAccessory = 0, SlotHead = 1, SlotJacket = 2, SlotPants = 3;

        /// <summary>Ok tusu: (yuva, yon). Yon -1 = onceki, +1 = sonraki.</summary>
        public event Action<int, int> StepPressed;
        /// <summary>TAMAM — secim bitti, giris ekranina don.</summary>
        public event Action ReadyPressed;
        public event Action RandomPressed;
        /// <summary>Cinsiyet sekmesi secildi (true = KADIN).</summary>
        public event Action<bool> GenderPressed;

        // ------------------------------------------------------------------ yerlesim (metre)
        // Satir yukseklikleri MANKENIN vucuduna gore: asker ~1.8 m; kafa ustu 1.95, kafa 1.58,
        // govde 1.22, pantolon 0.78. Oklar +-0.55'te — manken silueti (~+-0.35) ile
        // carpismazlar ama parcayla ayni bakista gorulecek kadar yakindirlar.
        const float ArrowX = 0.55f;
        static readonly (int slot, string label, float y)[] Rows =
        {
            (SlotAccessory, "AKSESUAR", 1.95f),
            (SlotHead,      "KAFA",     1.58f),
            (SlotJacket,    "GÖVDE",    1.22f),
            (SlotPants,     "PANTOLON", 0.78f),
        };

        const float ArrowSize = 0.11f, ArrowRadius = 0.022f;

        // Katman sirasi PlayerEntryPanel ile ayni mantik (yalnizca kuyruk numaralari onemli).
        const int QBorder = 3012, QFill = 3016, QHover = 3020, QIcon = 3026, QText = 3030;
        const float ZBorder = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZIcon = 0.001f, ZText = 0f;

        static readonly Color KeyFill  = new Color(0.110f, 0.137f, 0.173f, 0.92f);
        static readonly Color KeyEdge  = new Color(0.063f, 0.086f, 0.118f, 1f);
        static readonly Color KeyText  = new Color(0.90f, 0.93f, 0.96f, 1f);
        static readonly Color LabelCol = UITheme.TextMuted;
        static readonly Color CountCol = UITheme.TextPrimary;
        static readonly Color HoverCol = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        static readonly Color RandomEdge = new Color(0.43f, 0.31f, 0.84f, 1f);
        static readonly Color RandomFill = new Color(0.090f, 0.071f, 0.165f, 0.92f);
        static readonly Color RandomText = new Color(0.73f, 0.66f, 0.96f, 1f);

        static readonly Color TabEdge   = new Color(0.24f, 0.34f, 0.42f, 1f);
        static readonly Color TabFill   = new Color(0.055f, 0.086f, 0.118f, 0.92f);
        static readonly Color TabText   = new Color(0.62f, 0.70f, 0.78f, 1f);
        static readonly Color TabOnEdge = UITheme.AccentPurple;
        static readonly Color TabOnFill = new Color(0.106f, 0.078f, 0.180f, 0.95f);
        static readonly Color TabOnText = new Color(0.84f, 0.78f, 0.99f, 1f);

        static readonly Color ReadyEdge = UITheme.AccentCyan;
        static readonly Color ReadyFill = new Color(0.043f, 0.212f, 0.243f, 0.95f);
        static readonly Color ReadyText = new Color(0.62f, 0.95f, 0.93f, 1f);

        const string ActReady = "ready", ActRandom = "random";
        const string ActMale = "male", ActFemale = "female";

        // ------------------------------------------------------------------ ogeler
        class El
        {
            public Vector2 center, size;
            public float radius;
            public string action;       // null = ok tusu (slot/dir gecerli)
            public int slot, dir;
            public TextMeshPro label;
            public Material fillMat, borderMat;
        }

        readonly List<El> _els = new List<El>();
        readonly TextMeshPro[] _counters = new TextMeshPro[4];
        TextMeshPro _accessoryName;
        El _maleTab, _femaleTab;
        Transform _hover;
        MeshFilter _hoverMesh;
        int _hoverIdx = -1;

        void Awake()
        {
            var title = UITheme.MakeText(transform, "KARAKTERİNİ SEÇ", UITheme.AccentCyan,
                0.052f, TextAnchor.MiddleCenter, QText);
            title.transform.localPosition = new Vector3(0f, 2.40f, ZText);

            // CINSIYET SEKMELERI — en ustte, kafa oklarinin da uzerinde. Kafa secenekleri
            // iki kategoriye ayrildi (bkz. CharacterCustomizer.StepWithinGender): sekme
            // kategoriyi belirler, KAFA oklari yalnizca o kategorinin icinde gezinir.
            _maleTab   = AddButton(new Vector2(-0.20f, 2.25f), new Vector2(0.36f, 0.105f), 0.022f,
                "ERKEK", 0.032f, TabEdge, TabFill, TabText, ActMale);
            _femaleTab = AddButton(new Vector2(+0.20f, 2.25f), new Vector2(0.36f, 0.105f), 0.022f,
                "KADIN", 0.032f, TabEdge, TabFill, TabText, ActFemale);

            var rnd = AddButton(new Vector2(0f, 2.10f), new Vector2(0.30f, 0.075f), 0.018f,
                "RASTGELE", 0.024f, RandomEdge, RandomFill, RandomText, ActRandom);
            IconOn(rnd, UIMesh.Bolt(), RandomText, -0.108f, 0.013f, 0.021f);

            foreach (var row in Rows) BuildRow(row.slot, row.label, row.y);

            // Aksesuar SATIRIN ICINDE adiyla anilir ("KASK A"): sayac tek basina "3/9" derdi
            // ama oyuncu 3'un ne oldugunu ancak mankene bakip anlayabilirdi — kask ile kar
            // maskesi arka gorunumde karisiyor. Diger yuvalarda malzeme degisimi mankende
            // zaten apacik, ad gerekmez.
            _accessoryName = UITheme.MakeText(transform, "YOK", CountCol, 0.030f,
                TextAnchor.MiddleCenter, QText);
            _accessoryName.transform.localPosition = new Vector3(0f, 2.02f, ZText);

            // TAMAM: secimi bitirir ve GIRIS ekranina doner (oyuna sokmaz — oyuna KATIL
            // sokar). Tek cikis tusu: ayri bir GERI tusu ayni isi yapan ikinci tus olurdu.
            AddButton(new Vector2(0f, 0.40f), new Vector2(0.52f, 0.135f), 0.026f,
                "TAMAM", 0.044f, ReadyEdge, ReadyFill, ReadyText, ActReady);

            var hint = UITheme.MakeText(transform, "TAMAM sonrası KATIL ile oyuna girilir.",
                LabelCol, 0.020f, TextAnchor.MiddleCenter, QText);
            hint.transform.localPosition = new Vector3(0f, 0.275f, ZText);

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);
        }

        void BuildRow(int slot, string label, float y)
        {
            AddArrow(new Vector2(-ArrowX, y), slot, -1, left: true);
            AddArrow(new Vector2(+ArrowX, y), slot, +1, left: false);

            // Etiket sag okun ustunde, sayac altinda: satirin kimligi ve kacinci secenekte
            // oldugu ayni bakista okunur. Iki yana da yazmak simetrik olurdu ama ayni bilgiyi
            // iki kez soylemek kalabaliktan baska sey katmiyor.
            var lbl = UITheme.MakeText(transform, label, LabelCol, 0.022f,
                TextAnchor.MiddleCenter, QText);
            lbl.transform.localPosition = new Vector3(ArrowX, y + 0.096f, ZText);

            var cnt = UITheme.MakeText(transform, "-/-", CountCol, 0.024f,
                TextAnchor.MiddleCenter, QText);
            cnt.transform.localPosition = new Vector3(ArrowX, y - 0.092f, ZText);
            _counters[slot] = cnt;
        }

        void AddArrow(Vector2 center, int slot, int dir, bool left)
        {
            var size = new Vector2(ArrowSize, ArrowSize);
            UITheme.MakeRounded(transform, "ArrowB", center, size, ArrowRadius,
                KeyEdge, ZBorder, QBorder);
            UITheme.MakeRounded(transform, "ArrowF", center,
                size - Vector2.one * 0.004f, ArrowRadius - 0.004f, KeyFill, ZFill, QFill);

            var icon = UITheme.MakeShape(transform, "ArrowI", UIMesh.Play(), KeyText, QIcon);
            icon.localPosition = new Vector3(center.x, center.y, ZIcon);
            // Play ucgeni SAGA bakar; sol ok x ekseninde aynalanir. Negatif olcek normalleri
            // ters cevirir ama overlay malzemesi cift yuzlu isikssiz — gorunum etkilenmez.
            icon.localScale = new Vector3(left ? -0.034f : 0.034f, 0.040f, 1f);

            _els.Add(new El { center = center, size = size, radius = ArrowRadius, slot = slot, dir = dir });
        }

        El AddButton(Vector2 center, Vector2 size, float radius, string label, float textH,
                     Color edge, Color fill, Color text, string action)
        {
            var b = UITheme.MakeRounded(transform, "B_" + label, center, size, radius,
                edge, ZBorder, QBorder);
            var f = UITheme.MakeRounded(transform, "K_" + label, center,
                size - Vector2.one * 0.006f, Mathf.Max(0f, radius - 0.004f), fill, ZFill, QFill);

            var tm = UITheme.MakeText(transform, label, text, textH, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = new Vector3(center.x, center.y, ZText);

            var el = new El
            {
                center = center, size = size, radius = radius, action = action, label = tm,
                borderMat = b.GetComponent<MeshRenderer>().sharedMaterial,
                fillMat = f.GetComponent<MeshRenderer>().sharedMaterial,
            };
            _els.Add(el);
            return el;
        }

        /// <summary>Secili cinsiyet sekmesini vurgular. SECILEN PARLAR, DIGERI GERI CEKILIR —
        /// kontrast goreceli oldugu icin ikisi birlikte boyanmali (ayni kural: takim
        /// kartlari, bkz. PlayerEntryPanel).</summary>
        public void SetGender(bool female)
        {
            PaintTab(_maleTab, !female);
            PaintTab(_femaleTab, female);
        }

        static void PaintTab(El tab, bool on)
        {
            if (tab == null) return;
            UITheme.SetMaterialColor(tab.borderMat, on ? TabOnEdge : TabEdge);
            UITheme.SetMaterialColor(tab.fillMat, on ? TabOnFill : TabFill);
            if (tab.label != null) tab.label.color = on ? TabOnText : TabText;
        }

        void IconOn(El el, Mesh mesh, Color color, float dx, float w, float h)
        {
            var t = UITheme.MakeShape(transform, "Icon", mesh, color, QIcon);
            t.localPosition = new Vector3(el.center.x + dx, el.center.y, ZIcon);
            t.localScale = new Vector3(w, h, 1f);
        }

        // ------------------------------------------------------------------ gorunum

        /// <summary>Bir yuvanin sayacini tazeler ("2/5").</summary>
        public void SetCounter(int slot, int index, int count)
        {
            if (slot < 0 || slot >= _counters.Length || _counters[slot] == null) return;
            _counters[slot].text = (index + 1) + "/" + Mathf.Max(1, count);
        }

        /// <summary>Aksesuar satirindaki adi tazeler ("KASK A" / "YOK").</summary>
        public void SetAccessoryName(string label)
        {
            if (_accessoryName != null) _accessoryName.text = label;
        }

        // ------------------------------------------------------------------ surus

        /// <summary>Sahibi her kare cagirir (PlayerEntryPanel.Tick ile ayni desen).</summary>
        public void Tick(VRPointer pointer)
        {
            if (pointer == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            _hoverIdx = hit ? Find(local) : -1;
            ApplyHover();

            if (_hoverIdx < 0 || !pointer.ClickDown) return;

            VRPointer.Haptic();
            var el = _els[_hoverIdx];
            switch (el.action)
            {
                case ActReady:  ReadyPressed?.Invoke(); return;
                case ActRandom: RandomPressed?.Invoke(); return;
                case ActMale:   GenderPressed?.Invoke(false); return;
                case ActFemale: GenderPressed?.Invoke(true); return;
                default:        StepPressed?.Invoke(el.slot, el.dir); return;
            }
        }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _els.Count; i++)
            {
                var e = _els[i];
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
    }
}
