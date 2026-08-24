using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Oyuncunun BAGLANTIDAN ONCE sectigi karakter gorunumu: kafa, govde (ceket), pantolon
    /// ve kafa aksesuari indeksleri. <see cref="PlayerProfile"/>'in kardesi — isim/takim
    /// orada, gorunum burada. Yerel depo <see cref="PlayerPrefs"/>; ag tarafi
    /// <see cref="PlayerAppearance"/> (spawn'da ServerRpc ile bildirilir, sunucu dogrular).
    ///
    /// INDEKS SOZLESMESI: 0 = varsayilan gorunum (prefabin kendi malzemesi); aksesuarda
    /// 0 = YOK. UST SINIR BURADA BILINMEZ: kac secenek oldugunu yalnizca
    /// <see cref="CharacterCustomizer"/> bilir (secenek listeleri prefabda serilestirilmis).
    /// Burasi yalnizca negatifi keser; ust siniri Apply aninda customizer kirpar. Boylece
    /// secenek eklemek/cikarmak bu dosyaya dokunmayi gerektirmez.
    /// </summary>
    public static class CharacterProfile
    {
        const string HeadKey      = "vrmp_char_head";
        const string JacketKey    = "vrmp_char_jacket";
        const string PantsKey     = "vrmp_char_pants";
        const string AccessoryKey = "vrmp_char_accessory";

        static int _head, _jacket, _pants, _accessory;
        static bool _loaded;

        static void Load()
        {
            if (_loaded) return;
            _head      = Mathf.Max(0, PlayerPrefs.GetInt(HeadKey, 0));
            _jacket    = Mathf.Max(0, PlayerPrefs.GetInt(JacketKey, 0));
            _pants     = Mathf.Max(0, PlayerPrefs.GetInt(PantsKey, 0));
            _accessory = Mathf.Max(0, PlayerPrefs.GetInt(AccessoryKey, 0));
            _loaded = true;
        }

        public static int Head      { get { Load(); return _head; } }
        public static int Jacket    { get { Load(); return _jacket; } }
        public static int Pants     { get { Load(); return _pants; } }
        public static int Accessory { get { Load(); return _accessory; } }

        /// <summary>Secimi kalicilastir. Karakter ekrani HER degisiklikte cagirir — ayri bir
        /// "kaydet" adimi yok: oyuncu uygulamayi kapatsa bile son gordugu neyse odur.</summary>
        public static void Set(int head, int jacket, int pants, int accessory)
        {
            Load();
            _head      = Mathf.Max(0, head);
            _jacket    = Mathf.Max(0, jacket);
            _pants     = Mathf.Max(0, pants);
            _accessory = Mathf.Max(0, accessory);

            PlayerPrefs.SetInt(HeadKey, _head);
            PlayerPrefs.SetInt(JacketKey, _jacket);
            PlayerPrefs.SetInt(PantsKey, _pants);
            PlayerPrefs.SetInt(AccessoryKey, _accessory);
            PlayerPrefs.Save();
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir (PlayerProfile'daki
        // ayni kural): bayat _loaded bir onceki oturumun degerlerini gosterirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            _head = _jacket = _pants = _accessory = 0;
        }
    }
}
