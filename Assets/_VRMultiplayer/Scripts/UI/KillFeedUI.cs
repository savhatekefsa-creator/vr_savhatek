using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Gorusun SOL UST kosesinde duran kill paneli: kim kimi oldurdu.
    /// <see cref="PlayerHealth.KillReported"/> olayini dinler; sahibe ozel, <see cref="PlayerHUD"/>
    /// tarafindan uretilir.
    ///
    /// TAKIM SKORU BURADA DEGIL: panelin ustunde bir skor basligi vardi, ayni bilgi iki yerde
    /// yazilir olunca kaldirildi. Skor artik <see cref="ScoreboardUI"/>'da (B ile acilan mac
    /// tablosu); bu panel yalnizca "kim kimi oldurdu" isini yapiyor — temiz sorumluluk ayrimi.
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

        [Header("Ton")]
        // ⚠ BUNLAR ARTIK ALFA DEGIL, KARISIM ORANI. Passthrough bu oyunda uygulamanin ALTINA
        // kompozit ediliyor: kare tamponunun alfasi 1'in altina duserse GERCEK ODA panelin
        // icinden gorunur. Bu yuzden panelde tek bir yari saydam yuzey bile olamaz; "soluk"
        // gorunum rengi ZEMINLE karistirarak elde edilir (bkz. Mix).
        [Tooltip("Satir zemininin koyulugu (0 = tamamen siyah, 1 = tam ton). Alfa DEGIL.")]
        [Range(0f, 1f)] public float bgTint = 0.85f;
        [Tooltip("Yazinin zeminden ne kadar ayrildigi (1 = tam parlak). Alfa DEGIL.")]
        [Range(0f, 1f)] public float textTint = 0.90f;

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

        /// <summary>Rengi ZEMINE dogru karistirir; sonuc HER ZAMAN opak. Yari saydamligin
        /// alfasiz karsiligi — passthrough kompozisyonu icin sart (bkz. bgTint/textTint).</summary>
        static Color Mix(Color c, float t) =>
            new Color(c.r * t, c.g * t, c.b * t, 1f);

        static Color Mix(Color over, Color under, float t) =>
            new Color(Mathf.Lerp(under.r, over.r, t), Mathf.Lerp(under.g, over.g, t),
                      Mathf.Lerp(under.b, over.b, t), 1f);

        class Row
        {
            public GameObject go;
            public Material bgMat;
            public TextMesh left, mid, right;
            public bool bound;
            public float animY;      // su anki yerel y — hedefe dogru kayar
            public float appear;     // 0..1 giris animasyonu
            // Solma ALFAYLA DEGIL renkle yapildigi icin taban renkler saklanir; yoksa her
            // karede solmus renk taban sanilip satir kademeli kararirdi.
            public Color leftBase, midBase, rightBase;
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
        bool _placed;
        byte _colorTeam = 255;   // satir renkleri HANGI yerel takima gore yazildi

        void Awake()
        {
            _panel = new GameObject("Feed Panel").transform;
            _panel.SetParent(transform, false);

            for (int i = 0; i < maxRows; i++) _pool.Add(BuildRow(i));

            PlayerHealth.KillReported += OnKill;
        }

        void OnDestroy() => PlayerHealth.KillReported -= OnKill;

        // ------------------------------------------------------------------- kurulum

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
                row.leftBase = TeamColor(info.KillerTeam);
                row.mid.text = ">>";   // ASCII: font/kodlama riski olmayan yon isareti
                row.midBase = Dim;
                row.right.text = info.Victim;
                row.rightBase = TeamColor(info.VictimTeam);
            }
            else
            {
                // Intihar / kaynagi bilinmeyen olum: tek isim solda, aciklama sagda.
                row.left.text = info.Victim;
                row.leftBase = TeamColor(info.VictimTeam);
                row.mid.text = "";
                row.midBase = Dim;
                row.right.text = info.SelfKill ? "KENDINI OLDURDU" : "OLDU";
                row.rightBase = Dim;
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

                // Giris: soldan kayarak ve acilarak gelir.
                row.appear = Mathf.Min(1f, row.appear + dt / Mathf.Max(0.01f, appearTime));
                float ease = row.appear * row.appear * (3f - 2f * row.appear);   // smoothstep
                row.go.transform.localPosition =
                    new Vector3(-(1f - ease) * slideDistance, row.animY, 0f);

                ApplyFade(e, now, ease);
            }

            Follow();
        }

        /// <summary>Satirin belirme/solma gorunumu. HICBIR YERDE ALFA KULLANILMAZ:
        /// panel yari saydam cizilirse passthrough kare tamponunun alfasindan sizar ve gercek
        /// oda satirlarin icinden gorunur (bkz. bgTint). Bunun yerine
        ///  - zemin ve yazi renkleri SIYAHA dogru karistirilir,
        ///  - satir DIKEYDE olceklenerek acilip kapanir.
        /// Gorsel sonuc "solma" ile ayni, alfa kanali bozulmaz.</summary>
        void ApplyFade(Entry e, float now, float appear)
        {
            var row = e.row;
            float age = now - e.born;

            // Omrun son fadeTime saniyesinde kapanir.
            float life = rowLifetime - fadeTime;
            float out01 = age <= life ? 1f : Mathf.Clamp01(1f - (age - life) / Mathf.Max(0.01f, fadeTime));
            float k = out01 * appear;

            Color bg = RowBg;
            float tint = bgTint;

            if (e.byMe)
            {
                // Kendi oldurmem: sari zemin, kisa bir parlama ile girer sonra normale oturur.
                float glow = Mathf.Clamp01(1f - age / Mathf.Max(0.01f, killGlowTime));
                bg = KillBg;
                tint = Mathf.Lerp(bgTint, 1f, glow);
            }
            else if (e.onMe)
            {
                bg = DeathBg;
                tint = Mathf.Min(1f, bgTint * 1.2f);
            }

            // Zemin OPAK. Satirin kaybolmasi olcekle olur, seffaflasarak degil.
            Color bgOpaque = Mix(bg, tint);
            UITheme.SetMaterialColor(row.bgMat, bgOpaque);

            // Yazi zeminden AYRISIR; solarken zemine geri karisir. Boylece hicbir an
            // yari saydam bir piksel yazilmaz.
            row.left.color  = Mix(row.leftBase,  bgOpaque, textTint * k);
            row.mid.color   = Mix(row.midBase,   bgOpaque, textTint * k * 0.8f);
            row.right.color = Mix(row.rightBase, bgOpaque, textTint * k);

            // Dikey olcek: acilirken buyur, solarken kapan (yazi o an zaten zemine karismis).
            row.go.transform.localScale = new Vector3(1f, k, 1f);
        }

        static byte LocalTeam()
        {
            var local = PlayerIdentity.Local;
            return local != null ? local.Team.Value : (byte)0;
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
