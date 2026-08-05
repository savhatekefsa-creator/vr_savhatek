using System;
using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// KAYITLI HARITA LISTESI — "mevcut harita" dalinin ekrani.
    ///
    /// SAYFALI, KAYDIRMALI DEGIL. Lazer imlecle surekli kaydirma VR'da nisan almayi zorlastirir
    /// (panel dunyaya sabit, el titrer); sabit satirlar ve iki ok, ayni isi titremeye duyarsiz
    /// yapiyor. Sayfa basina 5 satir: 1.4 m'de satir yuksekligi 2.5 derece, rahat hedef.
    ///
    /// LISTEYI KENDI OKUMAZ. Kaynak <see cref="MapCatalog"/>; gozlukte o liste SUNUCUDAN gelir
    /// ve bir kare sonra dolabilir. Bu yuzden panel <see cref="MapCatalog.Changed"/> olayina
    /// abone: veri gelince kendini yeniden cizer, "bos liste" ekrani da o ana kadar dogru kalir.
    ///
    /// HAVUZ DURUMU SATIRDA YAZAR: hangi haritanin oyuna acik oldugunu gormek icin baska ekrana
    /// gitmek gerekmesin. Havuza giremeyecek harita (dogum bolgesi yok) SONUK cizilir — secilir,
    /// ama neden havuza alinamadigi bir tikla ogrenilir.
    /// </summary>
    public class MapListPanel : MonoBehaviour
    {
        /// <summary>Bir haritaya basildi (dosya adi).</summary>
        public event Action<string> Picked;

        /// <summary>GERI'ye basildi.</summary>
        public event Action Back;

        const int RowsPerPage = 5;

        const float PanelW = 0.86f, PanelH = 0.56f, PanelR = 0.020f, PanelEdgeW = 0.0025f;
        const float RowW = 0.78f, RowH = 0.062f, RowR = 0.010f, RowGap = 0.006f;
        const float RowsTopY = 0.115f;

        const float BtnW = 0.20f, BtnH = 0.052f, BtnR = 0.010f, BtnY = -0.222f;

        const float TitleY = 0.215f, TitleSize = 0.036f;
        const float NameSize = 0.024f, InfoSize = 0.017f, BtnSize = 0.020f, EmptySize = 0.022f;

        const int QBack = 3004, QRow = 3012, QFill = 3016, QHover = 3020, QText = 3030;
        const float ZBack = 0.006f, ZRow = 0.004f, ZFill = 0.003f, ZHover = 0.002f, ZText = 0f;

        static readonly Color Backdrop  = UITheme.PanelBg;
        static readonly Color PanelEdge = UITheme.PanelEdge;
        static readonly Color RowFill   = UITheme.SurfaceFill;
        static readonly Color RowEdge   = UITheme.SurfaceEdge;
        static readonly Color Muted     = UITheme.TextMuted;
        static readonly Color Dim       = UITheme.TextDim;
        static readonly Color HoverCol  = new Color(0.35f, 0.62f, 0.75f, 0.30f);

        enum HotKind { Map, Prev, Next, Back }

        class Hot
        {
            public Vector2 center, size;
            public float radius;
            public HotKind kind;
            public string map;
        }

        readonly List<Hot> _hots = new List<Hot>();
        readonly List<Transform> _built = new List<Transform>();

        Transform _hover;
        MeshFilter _hoverMesh;
        TextMesh _title;
        int _hoverIdx = -1;
        int _page;

        /// <summary>
        /// Baslik disaridan degistirilebilir: ayni liste hem "mevcut harita ac" hem "harita
        /// yoneticisi" akisinda kullaniliyor ve oyuncunun HANGI ISTE oldugunu bilmesi gerekiyor.
        /// Iki ayri liste sinifi yazmak, sayfalama ve rozet mantigini iki yerde tutmak olurdu.
        /// </summary>
        public void SetTitle(string text)
        {
            if (_title != null) _title.text = text;
        }

        /// <summary>
        /// Yalnizca HAVUZDAKI haritalari goster. Oyuncu modunda mac haritasi secilirken
        /// kullaniliyor: havuz disi bir harita oynanabilir degil, listede durmasi yalnizca
        /// "neden secemiyorum" sorusunu doguracakti.
        /// </summary>
        public void SetPoolOnly(bool on)
        {
            _poolOnly = on;
            Rebuild();
        }

        bool _poolOnly;

        void Awake()
        {
            UITheme.MakeOutlined(transform, "Backdrop", Vector2.zero,
                new Vector2(PanelW, PanelH), PanelR, PanelEdge, Backdrop, PanelEdgeW,
                ZBack, QBack, QBack + 1);

            _title = UITheme.MakeText(transform, "KAYITLI HARİTALAR", UITheme.AccentCyan,
                TitleSize, TextAnchor.MiddleCenter, QText);
            _title.transform.localPosition = new Vector3(0f, TitleY, ZText);

            var h = UITheme.MakeShape(transform, "Hover",
                UIMesh.RoundedRect(0.01f, 0.01f, 0.002f), HoverCol, QHover);
            _hoverMesh = h.GetComponent<MeshFilter>();
            _hover = h;
            _hover.gameObject.SetActive(false);

            Rebuild();
            _awake = true;
        }

        bool _awake;

        void OnEnable()
        {
            MapCatalog.Changed += Rebuild;

            // GIZLIYKEN KACIRILANI TOPLA. Onde bir ekran varken bu panel kapatiliyor
            // (bkz. CreativeFlowUI.ShowOnlyTop) ve kapaliyken olaya abone degil — arada
            // harita adi degismis ya da havuz durumu donmus olabilir. Ilk kurulusta Awake
            // zaten kuruyor, ikinci kez yapmayalim.
            if (_awake) Rebuild();
        }

        void OnDisable() => MapCatalog.Changed -= Rebuild;

        // ------------------------------------------------------------------ cizim

        /// <summary>
        /// Satirlari sifirdan kurar. Her sayfa/veri degisiminde tamami yikilip yeniden
        /// yaziliyor: menu her kare cizilmiyor, ve "eski satiri guncellemeyi unutma" hatasi
        /// listelerde en kolay yapilan hata.
        /// </summary>
        /// <summary>
        /// Yeniden kurulmayi ISTER — is bir sonraki LateUpdate'te, kare basina BIR kez yapilir.
        ///
        /// NEDEN ERTELENIYOR: kurulus karesinde uc kez cagriliyor (Awake, OnEnable, SetPoolOnly)
        /// ve Destroy Unity'de KARE SONUNA erteleniyor. Hepsi ayni karede kosunca eski satirlar
        /// henuz olmemis oluyor ve liste ucleniyordu. Kirli bayragi hepsini tek kuruluma
        /// indiriyor; arada gecen tek kare panelin bos gorunmesinden ibaret.
        /// </summary>
        void Rebuild() => _needsBuild = true;

        bool _needsBuild;

        void LateUpdate()
        {
            if (!_needsBuild) return;
            _needsBuild = false;
            BuildNow();
        }

        void BuildNow()
        {
            foreach (var t in _built) if (t != null) Destroy(t.gameObject);
            _built.Clear();
            _hots.Clear();
            _hoverIdx = -1;
            if (_hover != null) _hover.gameObject.SetActive(false);

            var all = new List<MapCatalog.Entry>();
            foreach (var e in MapCatalog.All)
                if (!_poolOnly || e.inPool) all.Add(e);

            int pages = Mathf.Max(1, (all.Count + RowsPerPage - 1) / RowsPerPage);
            _page = Mathf.Clamp(_page, 0, pages - 1);

            if (all.Count == 0)
            {
                Add(UITheme.MakeText(transform,
                    _poolOnly ? "Havuzda oynanabilir harita yok."
                              : "Kayıtlı harita yok.\nYENİ ile bir tane tasarla.",
                    Muted, EmptySize, TextAnchor.MiddleCenter, QText).transform,
                    new Vector3(0f, 0f, ZText));
            }
            else
            {
                int start = _page * RowsPerPage;
                int end = Mathf.Min(start + RowsPerPage, all.Count);
                for (int i = start; i < end; i++)
                    BuildRow(all[i], RowsTopY - (i - start) * (RowH + RowGap));
            }

            // Sayfa oklari yalnizca gerektiginde: tek sayfalik listede iki olu dugme durmasin.
            if (pages > 1)
            {
                BuildButton(new Vector2(-0.28f, BtnY), "◄ ÖNCEKİ", HotKind.Prev, _page > 0);
                BuildButton(new Vector2(+0.28f, BtnY), "SONRAKİ ►", HotKind.Next, _page < pages - 1);

                var pg = UITheme.MakeText(transform, (_page + 1) + " / " + pages, Dim, InfoSize,
                    TextAnchor.MiddleCenter, QText);
                Add(pg.transform, new Vector3(0f, BtnY + 0.040f, ZText));
            }

            BuildButton(new Vector2(0f, BtnY), "GERİ", HotKind.Back, true);
        }

        void BuildRow(MapCatalog.Entry e, float y)
        {
            var center = new Vector2(0f, y);
            var size = new Vector2(RowW, RowH);

            Add(UITheme.MakeRounded(transform, "Row " + e.name, center, size, RowR,
                RowEdge, ZRow, QRow), Vector3.zero, false);
            Add(UITheme.MakeRounded(transform, "RowFill " + e.name, center,
                size - Vector2.one * 0.003f, Mathf.Max(0f, RowR - 0.002f), RowFill,
                ZFill, QFill), Vector3.zero, false);

            // Havuza giremeyen harita SONUK: secilebilir kalir ama farki bakinca anlasilir.
            Color nameCol = e.poolEligible ? UITheme.TextPrimary : Dim;

            var nm = UITheme.MakeText(transform, e.displayName, nameCol, NameSize,
                TextAnchor.MiddleLeft, QText);
            Add(nm.transform, new Vector3(-RowW * 0.5f + 0.022f, y + 0.008f, ZText));

            string alt = e.propCount + " prop";
            if (!e.poolEligible) alt += "  •  havuza giremez";
            var info = UITheme.MakeText(transform, alt, Muted, InfoSize, TextAnchor.MiddleLeft, QText);
            Add(info.transform, new Vector3(-RowW * 0.5f + 0.022f, y - 0.016f, ZText));

            if (e.inPool)
            {
                var badge = UITheme.MakeText(transform, "HAVUZDA", UITheme.AccentCyan, InfoSize,
                    TextAnchor.MiddleRight, QText);
                Add(badge.transform, new Vector3(RowW * 0.5f - 0.022f, y, ZText));
            }

            _hots.Add(new Hot { center = center, size = size, radius = RowR,
                                kind = HotKind.Map, map = e.name });
        }

        void BuildButton(Vector2 center, string label, HotKind kind, bool enabled)
        {
            var size = new Vector2(BtnW, BtnH);
            Color edge = enabled ? UITheme.AccentPurple : Dim;

            Add(UITheme.MakeRounded(transform, label + " Border", center, size, BtnR,
                edge, ZRow, QRow), Vector3.zero, false);
            Add(UITheme.MakeRounded(transform, label + " Fill", center,
                size - Vector2.one * 0.003f, Mathf.Max(0f, BtnR - 0.002f), RowFill,
                ZFill, QFill), Vector3.zero, false);

            var tm = UITheme.MakeText(transform, label, enabled ? UITheme.TextPrimary : Dim,
                BtnSize, TextAnchor.MiddleCenter, QText);
            Add(tm.transform, new Vector3(center.x, center.y, ZText));

            // Kapali dugme HOT DEGIL: gorunur ama tiklanmaz — "bastim, olmadi" yerine
            // "basilamaz oldugu belli" davranisi.
            if (enabled)
                _hots.Add(new Hot { center = center, size = size, radius = BtnR, kind = kind });
        }

        void Add(Transform t, Vector3 localPos, bool setPos = true)
        {
            if (t == null) return;
            if (setPos) t.localPosition = localPos;
            _built.Add(t);
        }

        // ------------------------------------------------------------------ surus

        public void Tick(VRPointer pointer)
        {
            if (pointer == null) return;

            bool hit = pointer.Raycast(transform, out Vector2 local, out Vector3 world);
            pointer.Draw(hit, world, transform.forward);

            int idx = hit ? Find(local) : -1;
            if (idx != _hoverIdx) { _hoverIdx = idx; ApplyHover(); }

            if (_hoverIdx < 0 || !pointer.ClickDown) return;

            VRPointer.Haptic();
            var hot = _hots[_hoverIdx];
            switch (hot.kind)
            {
                case HotKind.Map:  Picked?.Invoke(hot.map); break;
                case HotKind.Back: Back?.Invoke(); break;
                case HotKind.Prev: _page--; Rebuild(); break;
                case HotKind.Next: _page++; Rebuild(); break;
            }
        }

        int Find(Vector2 p)
        {
            for (int i = 0; i < _hots.Count; i++)
            {
                var h = _hots[i];
                if (Mathf.Abs(p.x - h.center.x) <= h.size.x * 0.5f &&
                    Mathf.Abs(p.y - h.center.y) <= h.size.y * 0.5f)
                    return i;
            }
            return -1;
        }

        void ApplyHover()
        {
            bool on = _hoverIdx >= 0;
            if (_hover.gameObject.activeSelf != on) _hover.gameObject.SetActive(on);
            if (!on) return;

            var h = _hots[_hoverIdx];
            _hoverMesh.sharedMesh = UIMesh.RoundedRect(h.size.x, h.size.y, h.radius);
            _hover.localPosition = new Vector3(h.center.x, h.center.y, ZHover);
        }
    }
}
