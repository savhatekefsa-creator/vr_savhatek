using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Oyuncunun BAGLANTIDAN ONCE sectigi kimlik: isim ve takim. Yerel depo
    /// (<see cref="PlayerPrefs"/>), otomatik isim uretici ve hem istemcinin hem SUNUCUNUN
    /// kullandigi temizleme kurallari burada.
    ///
    /// Neden tek dosya: temizleme kuralinin iki tarafta AYNI olmasi sart. Istemci "MetaShadow"
    /// gosterip sunucu baska bir seye kirparsa oyuncu adini panelde farkli gorur. Kural burada
    /// bir kez yazilir, <see cref="UI.PlayerEntryPanel"/> ve <see cref="PlayerIdentity"/> ayni
    /// metodu cagirir. Sunucu yine de kendi tarafinda TEKRAR temizler — istemciye guvenilmez.
    ///
    /// Ag alani <c>FixedString32Bytes</c>, yani UTF-8 yuku 29 BAYT. Karakter sayisi degil bayt
    /// sayisi sinirliyor: ASCII disi bir harf 2 bayt yer kaplar. <see cref="Sanitize"/> ikisini
    /// de uygular.
    ///
    /// ISIM DE TAKIM DA ZORUNLU: ikisi secilmeden <see cref="IsReady"/> false kalir,
    /// "OYUNA BASLA" pasif durur ve oyuna giris baslamaz.
    /// </summary>
    public static class PlayerProfile
    {
        /// <summary>Tasarimdaki "En az 2 harf gir." ile ayni.</summary>
        public const int MinLength = 2;
        public const int MaxLength = 16;

        /// <summary>FixedString32Bytes'in tasiyabildigi UTF-8 yuku.</summary>
        public const int MaxUtf8Bytes = 29;

        // Takim numaralari PlayerIdentity ile AYNI olmali: 1 = A (mavi), 2 = B (kirmizi).
        public const byte TeamNone = 0, TeamBlue = 1, TeamRed = 2;

        const string NameKey = "vrmp_player_name";
        const string TeamKey = "vrmp_player_team";

        /// <summary>Otomatik isim havuzu. Bilerek ASCII: klavyede Turkce tus yok, ag alani
        /// bayt bazli ve kirik glif riski sifir kalsin. Begenmedigin ismi bu satirlarda
        /// degistirmek yeterli — baska hicbir yere dokunmuyorlar.</summary>
        static readonly string[] Callsigns =
        {
            "MetaShadow",  "NightFalcon", "IronWolf",    "SilentFox",
            "BlackRaven",  "GhostLynx",   "RedViper",    "SteelHawk",
            "StormBreaker","FrostByte",   "NightHunter", "CobraStrike",
            "ThunderBolt", "VoidWalker",
        };

        // Karistirilmis DESTE: saf rastgelede ayni isim ust uste cikip sinir bozuyor.
        // Deste tukenmeden hicbir isim tekrar gelmez.
        static readonly List<int> _deck = new List<int>();
        static int _deckPos;
        static int _cycle;          // kacinci deste: 1 = duz isimler, 2+ = 2 haneli sonekli tur
        static string _lastGiven;

        static string _name;
        static byte _team;
        static bool _loaded;

        static void Load()
        {
            if (_loaded) return;
            _name = Sanitize(PlayerPrefs.GetString(NameKey, string.Empty));
            byte t = (byte)PlayerPrefs.GetInt(TeamKey, TeamNone);
            _team = t <= TeamRed ? t : TeamNone;
            _loaded = true;
        }

        /// <summary>Secili isim. Hic kayit yoksa bos doner (panel o zaman otomatik bir isim
        /// onerir).</summary>
        public static string Name { get { Load(); return _name; } }

        /// <summary>Secili takim: 0 = yok, 1 = MAVI, 2 = KIZIL.</summary>
        public static byte Team { get { Load(); return _team; } }

        /// <summary>Oyuncu girisi TAMAMLANDI mi? <see cref="LanBootstrap"/> bunu bekler.</summary>
        public static bool Confirmed { get; private set; }

        /// <summary>Isim ve takim birlikte gecerli mi — "OYUNA BASLA" bununla aktiflesir.</summary>
        public static bool IsReady(string name, byte team) => IsValidName(name) && team != TeamNone;

        /// <summary>
        /// Onayi GERI AL — oyuncu maca girmekten vazgecip ana menuye donunce.
        ///
        /// ISIM VE TAKIM SILINMEZ, yalnizca bayrak duser: oyuncu tekrar OYUNCU modunu secince
        /// giris ekrani DOLU geri gelir, bastan yazmasi gerekmez. Bayragi dusurmek SART, cunku
        /// <see cref="UI.PlayerEntryUI"/> "onaylandiysa ben yokum" kuralina gore calisiyor —
        /// onay asili kalirsa giris ekrani hic acilmaz ve oyuncu bos ekranda kalir.
        /// </summary>
        public static void Unconfirm() => Confirmed = false;

        /// <summary>Girisi kalicilastirir ve onayli isaretler. Eksik/gecersizse kabul edilmez.</summary>
        public static bool Confirm(string name, byte team)
        {
            string clean = Sanitize(name);
            if (!IsReady(clean, team)) return false;

            Load();
            _name = clean;
            _team = team;
            Confirmed = true;

            PlayerPrefs.SetString(NameKey, clean);
            PlayerPrefs.SetInt(TeamKey, team);
            PlayerPrefs.Save();
            return true;
        }

        public static bool IsValidName(string s) =>
            !string.IsNullOrEmpty(s) && s.Length >= MinLength && s.Length <= MaxLength;

        // ------------------------------------------------------------- kullanimdaki isimler

        // Sunucuda SU AN oynayan isimler. Bos liste "kimse yok" DEMEK DEGIL — bkz. TakenKnown.
        static readonly List<string> _taken = new List<string>();

        /// <summary>
        /// Sunucudaki isim listesi ELIMIZDE mi?
        ///
        /// AYRIM SART, cunku giris ekrani BAGLANMADAN once aciliyor: liste bos olabilir
        /// (sunucuda gercekten kimse yok) ya da hic gelmemis olabilir (sunucu bulunamadi,
        /// eski surum, yayin dusmus). Ikisini bir sayarsak "gelmedi" durumunda her ismi
        /// serbest sanardik — ya da tersini yapip her ismi reddederdik. Liste yoksa
        /// ENGELLEME: karar sunucuya kalir (havuz ipucundaki ayni kural).
        /// </summary>
        public static bool TakenKnown { get; private set; }

        /// <summary>
        /// Sunucuda kullanilan isimleri kaydet. Kaynak kesif yayini
        /// (bkz. <see cref="NetworkDiscovery"/>) — oyuncu daha baglanmadan once ogrenilen
        /// tek sey bu kanaldan geliyor.
        /// </summary>
        public static void SetTakenNames(IReadOnlyList<string> names)
        {
            _taken.Clear();
            if (names != null)
                for (int i = 0; i < names.Count; i++)
                    if (!string.IsNullOrEmpty(names[i])) _taken.Add(names[i]);

            TakenKnown = names != null;
        }

        /// <summary>Liste hic gelmemis say (baglanti koptu / menuye donuldu).</summary>
        public static void ForgetTakenNames()
        {
            _taken.Clear();
            TakenKnown = false;
        }

        /// <summary>
        /// Bu isim sunucuda KULLANILIYOR mu? Liste yoksa her zaman false — emin olmadan
        /// oyuncuyu engellemeyiz.
        ///
        /// SON SOZ BURASI DEGIL: iki oyuncu ayni anda ayni ismi yazip ayni anda baglanabilir,
        /// ya da elimizdeki liste bir saniye bayat olabilir. Sunucu spawn aninda tekillestirmeyi
        /// yine yapiyor (bkz. <see cref="PlayerIdentity.MakeUnique"/>). Buradaki kontrol o
        /// sessiz yeniden adlandirmayi oyuncunun BASINA GELMEDEN onlemek icin.
        /// </summary>
        public static bool IsNameTaken(string name)
        {
            if (!TakenKnown || string.IsNullOrEmpty(name)) return false;

            string clean = Sanitize(name);
            for (int i = 0; i < _taken.Count; i++)
                if (string.Equals(_taken[i], clean, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Ortak temizleme: kirp, izinsiz karakterleri at, ic bosluklari teke indir, karakter
        /// VE bayt sinirina kirp. Istemci de sunucu da bunu cagirir.
        /// </summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            bool lastWasSpace = true;   // bastaki boslugu da yutar

            foreach (char c in raw)
            {
                if (char.IsControl(c)) continue;

                if (c == ' ' || c == '\t')
                {
                    if (lastWasSpace) continue;   // ic bosluklari teke indir
                    sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                // Harf/rakam serbest; ayirici olarak yalnizca - ve _ . Geri kalan her sey
                // (emoji, kontrol dizisi, zengin metin etiketi) DUSER — kill panelindeki
                // <color> etiketlerini bir oyuncu adiyla enjekte edememeli.
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') continue;

                sb.Append(c);
                lastWasSpace = false;
            }

            string s = sb.ToString().TrimEnd();
            if (s.Length > MaxLength) s = s.Substring(0, MaxLength);

            // Bayt kirpmasi karakter kirpmasindan SONRA: ASCII disi harf 2 bayt yer kaplar,
            // 16 karakter 29 bayta sigmayabilir.
            while (s.Length > 0 && Encoding.UTF8.GetByteCount(s) > MaxUtf8Bytes)
                s = s.Substring(0, s.Length - 1);

            return s.TrimEnd();
        }

        /// <summary>Siradaki otomatik isim. Deste bitince yeniden karistirilir ve isimler
        /// 2 haneli sonekle doner (IronWolf47) — sonsuza kadar taze his verir.
        ///
        /// SUNUCUDA KULLANILAN ISMI ONERMEZ (bkz. <see cref="IsNameTaken"/>): RASTGELE'ye basan
        /// oyuncuya "bu isim kullaniliyor" uyarisi cikmasi sacma olurdu — tusun isi zaten
        /// KULLANILABILIR bir isim bulmak. Deste boyunca dolu isimler ATLANIR; hepsi doluysa
        /// sonekli tura gecilir ve orada bir bosluk kesin bulunur (14 isim x 90 sonek).</summary>
        public static string NextGeneratedName()
        {
            // Ust sinir: deste uzunlugu x 3 tur. Sonsuz dongu koruyucusu — liste bayatsa ve
            // her sey dolu gorunuyorsa isim URETMEK, hic isim vermemekten iyidir.
            int attempts = Mathf.Max(1, Callsigns.Length * 3);

            string name = null;
            for (int i = 0; i < attempts; i++)
            {
                name = DrawName();
                if (!IsNameTaken(name)) break;
            }

            _lastGiven = name;
            return name;
        }

        /// <summary>Desteden tek bir cekilis — dolu olup olmadigina BAKMADAN.</summary>
        static string DrawName()
        {
            if (_deck.Count == 0 || _deckPos >= _deck.Count) Reshuffle();

            string name = Callsigns[_deck[_deckPos++]];
            // Ilk deste duz isimler verir; 14'u de gorduyse artik sonekle cesitlendirilir.
            if (_cycle > 1) name += Random.Range(10, 100).ToString();

            // Sinira takilirsa sonek atilir; kirpilmis yarim isim gostermektense duzu iyidir.
            if (name.Length > MaxLength) name = Callsigns[_deck[_deckPos - 1]];

            return name;
        }

        static void Reshuffle()
        {
            _deck.Clear();
            for (int i = 0; i < Callsigns.Length; i++) _deck.Add(i);

            // Fisher-Yates
            for (int i = _deck.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_deck[i], _deck[j]) = (_deck[j], _deck[i]);
            }

            // Yeni destenin ILK ismi, bir onceki destenin SON verdigi isim olmasin — oyuncu
            // tusa basip ayni ismi tekrar gorurse tus bozuk sanir.
            if (_deck.Count > 1 && _lastGiven != null && Callsigns[_deck[0]] == _lastGiven)
                (_deck[0], _deck[1]) = (_deck[1], _deck[0]);

            _deckPos = 0;
            _cycle++;
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir: onceki oturumun "onaylandi"
        // bayragi kalirsa giris paneli hic acilmaz.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Confirmed = false;
            _loaded = false;
            _name = null;
            _team = TeamNone;
            _deck.Clear();
            _deckPos = 0;
            _cycle = 0;
            _lastGiven = null;
            _taken.Clear();
            TakenKnown = false;
        }
    }
}
