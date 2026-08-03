using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Gorusun SOL UST kosesinde duran kill paneli: kim kimi oldurdu, ustunde takim skoru.
    /// <see cref="PlayerHealth.KillReported"/> olayini dinler; sahibe ozel, <see cref="PlayerHUD"/>
    /// tarafindan uretilir.
    ///
    /// KONUM KARARI: panel bilerek gorus merkezinden UZAGA, sol ust kosede duruyor (~-26 yatay,
    /// ~+19 dikey derece). Amaci "goz ucuyla surekli takip" degil — ust uste olumlerde nisan
    /// alanini kapatmasin, merak eden KAFASINI KALDIRIP baksin. Bu yuzden satir sayisi da 7
    /// ile sinirli: sinira dayandiginda liste asagi dogru buyumeyi birakir, en eski satir
    /// dusup yerine yenisi gelir.
    ///
    /// VR'DA GOZ YORMAMA KARARLARI (hepsi ayarlanabilir alan):
    ///  - OPAKLIK METINDEN DEGIL ZEMINDEN kisildi. Solgun metin okunabilirligi zeminden cok daha
    ///    hizli oldurur; zemin geri cekilir, yazi net kalir.
    ///  - Panel kafaya SERT kilitli degil, sonumlu takip ediyor — goze civilenmis HUD yorucudur.
    ///  - Yazi yuksekligi 1.2 m'de ~0.026 m ≈ 1.25 derece gorme acisi. VR'da rahat okuma esigi
    ///    ~1 derece, yani gozluksuz de okunur.
    ///  - DIKKAT: Quest 3'te merkezden ~30 dereceyi gecen icerik lens kenarina duser ve
    ///    bulaniklasir. Panelin SOL kenari su ayarla ~-33 derecede; daha sola cekersen
    ///    (offsetRight'i daha negatif yaparsan) Editor'de okunan yazi cihazda kaybolabilir.
    ///
    /// RENK KURALI (<see cref="viewerRelativeColors"/>): standart FPS yaklasimi — MAVI her zaman
    /// SENIN takimin, KIRMIZI her zaman rakip. Dikkat: oyunun avatar renkleri MUTLAK (A takimi
    /// mavi, B takimi kirmizi), yani B takimindaki bir oyuncu icin panel rengiyle avatar rengi
    /// ters duser. Alan false yapilirsa panel de mutlak renklere gecer.
    ///
    /// ISIM GIZLILIGI: kafa ustu isim etiketi yalnizca takim arkadasina gorunur
    /// (<see cref="PlayerIdentity"/>); panel ise rakip ismini ACIK yazar. Bilincli karar:
    /// panel KIMLIGI acar, KONUMU acmaz — etiketin wallhack riski burada yok.
    /// </summary>
    public class KillFeedUI : MonoBehaviour
    {
        [Header("Yerlesim (kafaya gore, metre)")]
        public float distance = 1.2f;
        [Tooltip("Negatif = SOL. -0.58 @ 1.2 m ≈ 26 derece sola.")]
        public float offsetRight = -0.58f;
        [Tooltip("+0.42 @ 1.2 m ≈ 19 derece yukari.")]
        public float offsetUp = 0.42f;
        [Tooltip("Sonumlu takip hizi. Buyuk deger = kafaya daha sert kilitli.")]
        public float followSpeed = 9f;

        [Header("Panel")]
        public float panelWidth = 0.52f;
        public float rowHeight = 0.040f;
        public float textHeight = 0.026f;
        [Tooltip("Ekrani kapatmasin diye ust sinir. Dolunca en eski satir dusurulur.")]
        public int maxRows = 7;

        [Header("Zamanlama (saniye)")]
        public float rowLifetime = 6f;
        public float fadeTime = 1.2f;
        [Tooltip("Kendi oldurmemde sari parlamanin sonme suresi.")]
        public float killGlowTime = 0.6f;

        [Header("Animasyon")]
        [Tooltip("Yeni satirin soldan kayarak girme suresi.")]
        public float appearTime = 0.22f;
        [Tooltip("Yeni satir bu kadar SOLDAN kayarak gelir (metre).")]
        public float slideDistance = 0.075f;
        [Tooltip("Alttaki satirlarin bir sira asagi kayma hizi. Kaskad hissini bu verir.")]
        public float cascadeSpeed = 14f;

        [Header("Opaklik")]
        [Range(0f, 1f)] public float bgAlpha = 0.25f;
        [Range(0f, 1f)] public float textAlpha = 0.90f;
        [Range(0f, 1f)] public float headerAlpha = 0.45f;

        [Header("Renk")]
        [Tooltip("Acik: mavi = senin takimin, kirmizi = rakip (standart FPS). " +
                 "Kapali: mutlak takim renkleri (A mavi, B kirmizi) — avatar renkleriyle birebir.")]
        public bool viewerRelativeColors = true;

        static readonly Color Friendly = new Color(0.45f, 0.72f, 1f);
        static readonly Color Enemy    = new Color(1f, 0.45f, 0.40f);
        static readonly Color Neutral  = new Color(0.72f, 0.74f, 0.78f);
        static readonly Color Dim      = new Color(0.62f, 0.65f, 0.70f);
        static readonly Color RowBg    = new Color(0.03f, 0.04f, 0.05f);
        static readonly Color KillBg   = new Color(0.85f, 0.72f, 0.15f);   // kendi oldurmem
        static readonly Color DeathBg  = new Color(0.55f, 0.12f, 0.12f);   // kendi olumum

        // Kuyruk 3000'in USTUNDE. Sebebi olculdu: olum perdesi, dusuk-can vinyeti ve hasar
        // flasi (bkz. HeadOverlay) da saydam kuyruk 3000'de ve kafaya ~0.12 m'de duruyor.
        // Ayni kuyrukta saydamlar MESAFEYE gore siralandigi icin perde her zaman en son
        // ciziliyor ve paneli bastiriyordu — yani oldugun an, seni kimin oldurdugunu en cok
        // merak ettigin anda panel soluyordu. Bilincli takas: flash bombasinin beyaz perdesi
        // de artik paneli ortmuyor.
        const int QBg = 3050, QText = 3051;
        const float Pad = 0.012f;

        class Row
        {
            public GameObject go;
            public Material bgMat;
            public TextMesh left, mid, right;
            public bool bound;
            public float animY;      // su anki yerel y — hedefe dogru kayar
            public float appear;     // 0..1 giris animasyonu
        }

        class Entry
        {
            public KillInfo info;
            public float born;
            public bool byMe;
            public bool onMe;
            public Row row;
        }

        readonly List<Entry> _entries = new List<Entry>();
        readonly List<Row> _pool = new List<Row>();

        Transform _panel;
        TextMesh _scoreMine, _scoreSep, _scoreTheirs;
        float _nextScoreRefresh;
        bool _placed;
        byte _colorTeam = 255;   // satir renkleri HANGI yerel takima gore yazildi

        void Awake()
        {
            _panel = new GameObject("Feed Panel").transform;
            _panel.SetParent(transform, false);

            BuildHeader();
            for (int i = 0; i < maxRows; i++) _pool.Add(BuildRow(i));

            PlayerHealth.KillReported += OnKill;
        }

        void OnDestroy() => PlayerHealth.KillReported -= OnKill;

        // ------------------------------------------------------------------- kurulum

        void BuildHeader()
        {
            float y = rowHeight * 1.2f;
            _scoreMine = UITheme.MakeText(_panel, "0", Friendly, textHeight * 0.85f,
                TextAnchor.MiddleRight, QText);
            _scoreMine.transform.localPosition = new Vector3(-0.014f, y, 0f);

            _scoreSep = UITheme.MakeText(_panel, "-", Dim, textHeight * 0.85f,
                TextAnchor.MiddleCenter, QText);
            _scoreSep.transform.localPosition = new Vector3(0f, y, 0f);

            _scoreTheirs = UITheme.MakeText(_panel, "0", Enemy, textHeight * 0.85f,
                TextAnchor.MiddleLeft, QText);
            _scoreTheirs.transform.localPosition = new Vector3(0.014f, y, 0f);
        }

        Row BuildRow(int index)
        {
            var go = new GameObject("Row " + index);
            go.transform.SetParent(_panel, false);

            var bg = UITheme.MakeQuad(go.transform, "Bg", RowBg, QBg);
            bg.localPosition = new Vector3(0f, 0f, 0.002f);   // yazinin ARKASINDA
            bg.localScale = new Vector3(panelWidth, rowHeight - 0.004f, 1f);

            float half = panelWidth * 0.5f;
            var left = UITheme.MakeText(go.transform, "", Neutral, textHeight, TextAnchor.MiddleLeft, QText);
            left.transform.localPosition = new Vector3(-half + Pad, 0f, 0f);

            var mid = UITheme.MakeText(go.transform, "", Dim, textHeight * 0.9f, TextAnchor.MiddleCenter, QText);
            mid.transform.localPosition = Vector3.zero;

            var right = UITheme.MakeText(go.transform, "", Neutral, textHeight, TextAnchor.MiddleRight, QText);
            right.transform.localPosition = new Vector3(half - Pad, 0f, 0f);

            go.SetActive(false);
            return new Row
            {
                go = go,
                bgMat = bg.GetComponent<MeshRenderer>().sharedMaterial,
                left = left, mid = mid, right = right,
            };
        }

        // ------------------------------------------------------------------- olay

        void OnKill(KillInfo info)
        {
            ulong me = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;

            // Sinira dayanmissak once en ESKIYI dusur — satiri serbest kalsin ki yenisi alsin.
            while (_entries.Count >= maxRows) Release(_entries[_entries.Count - 1]);

            var row = TakeFreeRow();
            if (row == null) return;   // olmamali; havuz maxRows kadar

            var e = new Entry
            {
                info = info,
                born = Time.unscaledTime,
                byMe = info.Kind == 0 && info.KillerId == me,
                onMe = info.VictimId == me,
                row = row,
            };

            row.bound = true;
            row.appear = 0f;
            row.animY = 0f;            // en ust sira; asagidakiler kendi hedeflerine kayacak
            row.go.SetActive(true);

            _entries.Insert(0, e);
            WriteRow(e);
        }

        Row TakeFreeRow()
        {
            for (int i = 0; i < _pool.Count; i++)
                if (!_pool[i].bound) return _pool[i];
            return null;
        }

        void Release(Entry e)
        {
            if (e.row != null)
            {
                e.row.bound = false;
                e.row.go.SetActive(false);
            }
            _entries.Remove(e);
        }

        /// <summary>Satirin YAZISINI bir kez yazar. Her kare yazmak TextMesh'in mesh'ini
        /// yeniden urettirirdi — degisen tek sey alfa oldugu icin buna gerek yok.</summary>
        void WriteRow(Entry e)
        {
            var row = e.row;
            var info = e.info;

            if (info.Kind == 0)
            {
                row.left.text = info.Killer;
                row.left.color = TeamColor(info.KillerTeam);
                row.mid.text = ">>";   // ASCII: font/kodlama riski olmayan yon isareti
                row.right.text = info.Victim;
                row.right.color = TeamColor(info.VictimTeam);
            }
            else
            {
                // Intihar / kaynagi bilinmeyen olum: tek isim solda, aciklama sagda.
                row.left.text = info.Victim;
                row.left.color = TeamColor(info.VictimTeam);
                row.mid.text = "";
                row.right.text = info.SelfKill ? "KENDINI OLDURDU" : "OLDU";
                row.right.color = Dim;
            }
        }

        // ------------------------------------------------------------------- guncelleme

        void Update()
        {
            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;

            // Suresi dolanlari at (sondan basa: liste yeni->eski sirali).
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (now - _entries[i].born < rowLifetime) break;
                Release(_entries[i]);
            }

            // Yerel takim sonradan seciliyor (TeamSelector). Degistiginde daha once yazilmis
            // satirlarin dost/dusman renkleri yanlis kalirdi — bir kez yeniden boyanir.
            byte localTeam = LocalTeam();
            if (localTeam != _colorTeam)
            {
                _colorTeam = localTeam;
                for (int i = 0; i < _entries.Count; i++) WriteRow(_entries[i]);
            }

            float k = 1f - Mathf.Exp(-cascadeSpeed * dt);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var row = e.row;

                // Sira kaymasi: yeni satir girince altindakiler hedeflerine dogru akar.
                float targetY = -i * rowHeight;
                row.animY = Mathf.Lerp(row.animY, targetY, k);

                // Giris: soldan kayarak ve belirerek gelir.
                row.appear = Mathf.Min(1f, row.appear + dt / Mathf.Max(0.01f, appearTime));
                float ease = row.appear * row.appear * (3f - 2f * row.appear);   // smoothstep
                row.go.transform.localPosition =
                    new Vector3(-(1f - ease) * slideDistance, row.animY, 0f);

                ApplyFade(e, now, ease);
            }

            if (now >= _nextScoreRefresh)
            {
                _nextScoreRefresh = now + 0.5f;   // skor icin 2 Hz fazlasiyla yeter
                RefreshScore();
            }

            Follow();
        }

        void ApplyFade(Entry e, float now, float appear)
        {
            var row = e.row;
            float age = now - e.born;

            // Omrun son fadeTime saniyesinde soner.
            float life = rowLifetime - fadeTime;
            float out01 = age <= life ? 1f : Mathf.Clamp01(1f - (age - life) / Mathf.Max(0.01f, fadeTime));
            float k = out01 * appear;

            Color bg = RowBg;
            float a = bgAlpha;

            if (e.byMe)
            {
                // Kendi oldurmem: sari zemin, kisa bir parlama ile girer sonra normale oturur.
                float glow = Mathf.Clamp01(1f - age / Mathf.Max(0.01f, killGlowTime));
                bg = KillBg;
                a = Mathf.Lerp(bgAlpha, 0.45f, glow);
            }
            else if (e.onMe)
            {
                bg = DeathBg;
                a = bgAlpha * 1.2f;
            }

            UITheme.SetMaterialColor(row.bgMat, new Color(bg.r, bg.g, bg.b, a * k));

            SetAlpha(row.left, textAlpha * k);
            SetAlpha(row.mid, textAlpha * k * 0.8f);
            SetAlpha(row.right, textAlpha * k);
        }

        static void SetAlpha(TextMesh tm, float a)
        {
            var c = tm.color;
            tm.color = new Color(c.r, c.g, c.b, a);
        }

        static byte LocalTeam()
        {
            var local = PlayerIdentity.Local;
            return local != null ? local.Team.Value : (byte)0;
        }

        void RefreshScore()
        {
            byte mine = LocalTeam();

            // Takim secilmeden "senin takimin" diye bir sey yok — mutlak gosterime dus.
            if (!viewerRelativeColors || mine == 0)
            {
                SetScore(PlayerIdentity.TeamScore(1), PlayerIdentity.TeamScore(2),
                    PlayerIdentity.TeamAColor, PlayerIdentity.TeamBColor);
                return;
            }

            byte theirs = mine == 1 ? (byte)2 : (byte)1;
            SetScore(PlayerIdentity.TeamScore(mine), PlayerIdentity.TeamScore(theirs),
                Friendly, Enemy);
        }

        void SetScore(int a, int b, Color ca, Color cb)
        {
            _scoreMine.text = a.ToString();
            _scoreMine.color = new Color(ca.r, ca.g, ca.b, headerAlpha);
            _scoreSep.color = new Color(Dim.r, Dim.g, Dim.b, headerAlpha * 0.8f);
            _scoreTheirs.text = b.ToString();
            _scoreTheirs.color = new Color(cb.r, cb.g, cb.b, headerAlpha);
        }

        /// <summary>Rengi BAKAN oyuncuya gore secer (bkz. <see cref="viewerRelativeColors"/>).</summary>
        Color TeamColor(byte team)
        {
            if (team == 0) return Neutral;   // takim secmemis (bilinen acik bulgu) — notr kal

            if (!viewerRelativeColors)
                return team == 1 ? PlayerIdentity.TeamAColor : PlayerIdentity.TeamBColor;

            byte mine = LocalTeam();
            if (mine == 0) return Neutral;   // henuz takimim yok: kimse dost/dusman degil
            return team == mine ? Friendly : Enemy;
        }

        // Gorus uzayinda sabit ama SONUMLU: kafa donunce panel gecikmeli yakalar, goze
        // civilenmis hissi olusmaz.
        void Follow()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 target = head.position
                           + head.forward * distance
                           + head.right * offsetRight
                           + head.up * offsetUp;
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
