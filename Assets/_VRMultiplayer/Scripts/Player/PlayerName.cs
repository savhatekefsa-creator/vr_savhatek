using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Oyuncunun KENDI ismi: yerel depo (<see cref="PlayerPrefs"/>), otomatik isim uretici ve
    /// hem istemcinin hem SUNUCUNUN kullandigi temizleme kurallari.
    ///
    /// Neden tek dosya: temizleme kuralinin iki tarafta AYNI olmasi sart. Istemci "MetaShadow"
    /// gosterip sunucu baska bir seye kirparsa oyuncu adini panelde farkli gorur. Kural burada
    /// bir kez yazilir, <see cref="NameEntryUI"/> ve <see cref="PlayerIdentity"/> ayni metodu
    /// cagirir. Sunucu yine de kendi tarafinda TEKRAR temizler — istemciye guvenilmez.
    ///
    /// Ag alani <c>FixedString32Bytes</c>, yani UTF-8 yuku 29 BAYT. Karakter sayisi degil bayt
    /// sayisi sinirliyor: ASCII disi bir harf 2 bayt yer kaplar. <see cref="Sanitize"/> ikisini
    /// de uygular.
    /// </summary>
    public static class PlayerName
    {
        public const int MinLength = 3;
        public const int MaxLength = 16;

        /// <summary>FixedString32Bytes'in tasiyabildigi UTF-8 yuku.</summary>
        public const int MaxUtf8Bytes = 29;

        const string PrefKey = "vrmp_player_name";

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

        static string _current;
        static bool _loaded;

        /// <summary>Oyuncunun secili ismi. Ilk eriste PlayerPrefs'ten yuklenir; hic kayit
        /// yoksa bos doner (panel o zaman otomatik bir isim onerir).</summary>
        public static string Current
        {
            get
            {
                if (!_loaded)
                {
                    _current = Sanitize(PlayerPrefs.GetString(PrefKey, string.Empty));
                    _loaded = true;
                }
                return _current;
            }
        }

        /// <summary>Oyuncu ismi ONAYLADI mi? <see cref="LanBootstrap"/> bunu bekler: isim
        /// secilmeden oyuna katilim baslamaz.</summary>
        public static bool Confirmed { get; private set; }

        /// <summary>Ismi kalicilastirir ve onayli isaretler. Gecersiz isim kabul edilmez.</summary>
        public static bool Confirm(string name)
        {
            string clean = Sanitize(name);
            if (!IsValid(clean)) return false;

            _current = clean;
            _loaded = true;
            Confirmed = true;
            PlayerPrefs.SetString(PrefKey, clean);
            PlayerPrefs.Save();
            return true;
        }

        public static bool IsValid(string s) =>
            !string.IsNullOrEmpty(s) && s.Length >= MinLength && s.Length <= MaxLength;

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
        /// 2 haneli sonekle doner (IronWolf47) — sonsuza kadar taze his verir.</summary>
        public static string NextGenerated()
        {
            if (_deck.Count == 0 || _deckPos >= _deck.Count) Reshuffle();

            string name = Callsigns[_deck[_deckPos++]];
            // Ilk deste duz isimler verir; 14'u de gorduyse artik sonekle cesitlendirilir.
            if (_cycle > 1) name += Random.Range(10, 100).ToString();

            // Sinira takilirsa sonek atilir; kirpilmis yarim isim gostermektense duzu iyidir.
            if (name.Length > MaxLength) name = Callsigns[_deck[_deckPos - 1]];

            _lastGiven = name;
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
        // bayragi kalirsa isim paneli hic acilmaz.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Confirmed = false;
            _loaded = false;
            _current = null;
            _deck.Clear();
            _deckPos = 0;
            _cycle = 0;
            _lastGiven = null;
        }
    }
}
