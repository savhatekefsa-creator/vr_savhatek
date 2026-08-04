using UnityEngine;

namespace VRMultiplayer.Match
{
    /// <summary>
    /// Mac ayarlari TEK KAYNAK. <see cref="CombatConfig"/> ile ayni desen: asset
    /// <c>Resources/MatchConfig</c>'ten yuklenir, yoksa kod-ici varsayilanla devam edilir —
    /// yani asset olusturulmadan da oyun calisir.
    ///
    /// Degerler YALNIZCA SUNUCUDA okunur. Istemciye ayrilan sey sonuctur (faz + bitis zamani),
    /// ayarin kendisi degil: boylece bir istemcinin asseti farkli olsa bile mac herkeste ayni
    /// aninda biter.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Match Config", fileName = "MatchConfig")]
    public class MatchConfig : ScriptableObject
    {
        [Header("Sure (saniye)")]
        [Tooltip("Bir macin uzunlugu. Sematik varsayilan 3:00.")]
        public float matchSeconds = 180f;

        [Tooltip("Mac bitince sonuc ekraninin ekranda kalma suresi. Sonrasinda isinmaya donulur.")]
        public float endScreenSeconds = 12f;

        [Header("Bitis kosulu")]
        [Tooltip("0 = KAPALI (yalnizca sure). >0 ise bir takim bu skora ulasinca mac erken biter.")]
        public int scoreLimit = 0;

        [Header("Baslatma")]
        [Tooltip("Maci baslatmak icin ONERILEN en az oyuncu sayisi. Butonu KILITLEMEZ — yalnizca " +
                 "PC'deki hazir durum yazisini besler. Tek kisiyle test edilebilsin diye kilit yok.")]
        public int minPlayersToStart = 2;

        // ------------------------------------------------------------- singleton

        static MatchConfig _instance;

        /// <summary>Paylasilan config. Asset yoksa kod-ici varsayilana duser — oyun asla
        /// null'a carpmaz (bkz. <see cref="CombatConfig.Instance"/>).</summary>
        public static MatchConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<MatchConfig>("MatchConfig");
                    if (_instance == null)
                    {
                        Debug.LogWarning("[MatchConfig] Resources/MatchConfig.asset bulunamadi — " +
                                         "kod-ici varsayilan degerler kullaniliyor.");
                        _instance = CreateInstance<MatchConfig>();
                    }
                }
                return _instance;
            }
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir; yok edilmis assete tutunmus
        // referans ikinci Play'de MissingReference verirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _instance = null;
    }
}
