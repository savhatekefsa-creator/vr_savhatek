using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Match
{
    /// <summary>
    /// MACIN SAHIBI: faz, geri sayim, takim skoru ve kazanan. Sunucu yazar, herkes okur.
    ///
    /// NEDEN AYRI BIR NetworkObject: proje bugune kadar bunu bilerek ERTELEMISTI
    /// (bkz. <see cref="PlayerIdentity"/> Kills yorumu: "skorun oyuncunun kimliginde yasamasi
    /// sahne ve prefab degisikligini tamamen gereksiz kiliyor"). O karar KISISEL skor icin
    /// dogruydu ve hala gecerli. Ama MACIN durumu kimseye ait degil: faz, bitis zamani, kazanan
    /// ve kalici takim skoru bir oyuncu ciktiginda kaybolmamali.
    ///
    /// SAHNEYE YINE DE DOKUNULMADI: bu bilesen Resources'tan yuklenen kucuk bir prefabta yasar
    /// ve sunucu acilinca spawn edilir (bkz. <see cref="MatchBootstrap"/>) — silahlarin
    /// izledigi yolun aynisi.
    ///
    /// GERI SAYIM TEK BIR double ILE TASINIR: <see cref="PhaseEndsAt"/> mutlak SUNUCU zamanidir,
    /// istemci "kalan = PhaseEndsAt - ServerTime" hesaplar. Her kare tick paketi yok, ve GEC
    /// KATILAN da dogru sureyi gorur — goreli sure saklansaydi goremezdi. Ayni desen
    /// <c>NetworkWeapon</c>'in reload sayacinda da kullaniliyor.
    ///
    /// HASAR YALNIZCA <see cref="Phase.Playing"/> FAZINDA GECER (bkz. <see cref="DamageAllowed"/>).
    /// Isinmada ve mac sonunda herkes dolasip ATES EDEBILIR — ses, geri tepme, mermi, namlu alevi
    /// calisir — ama kimse hasar almaz.
    /// </summary>
    public class MatchManager : NetworkBehaviour
    {
        public enum Phase : byte
        {
            /// <summary>Mac baslamadi. PC'deki "MACI BASLAT" bekleniyor. Ates var, hasar yok.</summary>
            Warmup = 0,
            /// <summary>Mac suruyor. Geri sayim isliyor, hasar gecer.</summary>
            Playing = 1,
            /// <summary>Mac bitti, sonuc ekranda. Ates var, hasar yok.</summary>
            Ended = 2,
        }

        public static MatchManager Instance { get; private set; }

        // Tumu SUNUCU yazar. NetworkVariable oldugu icin gec katilanin ilk senkronu otomatik.
        public readonly NetworkVariable<byte> PhaseRaw = new NetworkVariable<byte>((byte)Phase.Warmup);
        /// <summary>Fazin bitecegi MUTLAK sunucu zamani. Warmup'ta anlamsizdir (0).</summary>
        public readonly NetworkVariable<double> PhaseEndsAt = new NetworkVariable<double>(0d);
        public readonly NetworkVariable<ushort> ScoreBlue = new NetworkVariable<ushort>(0);
        public readonly NetworkVariable<ushort> ScoreRed = new NetworkVariable<ushort>(0);
        /// <summary>0 = beraberlik, 1 = mavi, 2 = kizil. Yalnizca <see cref="Phase.Ended"/>'de anlamli.</summary>
        public readonly NetworkVariable<byte> Winner = new NetworkVariable<byte>(0);

        public Phase Current => (Phase)PhaseRaw.Value;
        public bool IsPlaying => Current == Phase.Playing;
        public bool IsEnded => Current == Phase.Ended;

        /// <summary>Kalan sure (saniye). Warmup'ta 0.</summary>
        public float SecondsLeft
        {
            get
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening || Current == Phase.Warmup) return 0f;
                return Mathf.Max(0f, (float)(PhaseEndsAt.Value - nm.ServerTime.Time));
            }
        }

        /// <summary>Hasar su an gecer mi? MatchManager YOKSA true doner — mac katmani olmadan
        /// (tek basina test, eski kayit) oyun eskisi gibi calismaya devam etsin.</summary>
        public static bool DamageAllowed => Instance == null || Instance.IsPlaying;

        /// <summary>Takimin mac skoru. MatchManager varsa KALICI sayac, yoksa oyunculardan
        /// toplanan eski hesap. Eski hesabin bilinen sinirini (oyuncu cikinca skoru dusuyor)
        /// kalici sayac kapatiyor.</summary>
        public static int TeamScore(byte team)
        {
            if (Instance == null) return PlayerIdentity.TeamScore(team);
            return team == PlayerProfile.TeamBlue ? Instance.ScoreBlue.Value : Instance.ScoreRed.Value;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) EnterWarmup();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!IsServer) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return;
            double now = nm.ServerTime.Time;

            if (Current == Phase.Playing)
            {
                var cfg = MatchConfig.Instance;
                bool timeUp = now >= PhaseEndsAt.Value;
                // Skor limiti KAPALI ise (0) yalnizca sure bitirir.
                bool scoreHit = cfg.scoreLimit > 0 &&
                    (ScoreBlue.Value >= cfg.scoreLimit || ScoreRed.Value >= cfg.scoreLimit);
                if (timeUp || scoreHit) EndMatch();
            }
            else if (Current == Phase.Ended && now >= PhaseEndsAt.Value)
            {
                EnterWarmup();
            }
        }

        // ------------------------------------------------------------------ faz gecisleri

        /// <summary>PC'deki "MACI BASLAT" butonu cagirir. Zaten oynaniyorsa hicbir sey yapmaz.</summary>
        public void ServerStartMatch()
        {
            if (!IsServer || Current == Phase.Playing) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return;

            ScoreBlue.Value = 0;
            ScoreRed.Value = 0;
            Winner.Value = 0;
            ResetPlayers(clearPersonalStats: true);

            PhaseRaw.Value = (byte)Phase.Playing;
            PhaseEndsAt.Value = nm.ServerTime.Time + MatchConfig.Instance.matchSeconds;
            Debug.Log("[Match] MAC BASLADI — " + MatchConfig.Instance.matchSeconds + " sn.");
        }

        void EndMatch()
        {
            Winner.Value = ScoreBlue.Value == ScoreRed.Value ? (byte)0
                         : ScoreBlue.Value > ScoreRed.Value ? PlayerProfile.TeamBlue
                         : PlayerProfile.TeamRed;

            PhaseRaw.Value = (byte)Phase.Ended;
            PhaseEndsAt.Value = NetworkManager.Singleton.ServerTime.Time
                              + MatchConfig.Instance.endScreenSeconds;
            Debug.Log("[Match] MAC BITTI — MAVI " + ScoreBlue.Value + " / KIZIL " + ScoreRed.Value
                    + " — kazanan: " + (Winner.Value == 0 ? "beraberlik" : Winner.Value.ToString()));
        }

        void EnterWarmup()
        {
            PhaseRaw.Value = (byte)Phase.Warmup;
            PhaseEndsAt.Value = 0d;
            // Isinma OYNANABILIR olmali: mac sonunda olu kalanlar burada ayaga kalkar, yoksa
            // yeni mac baslayana kadar yerde beklerlerdi. Skorlar burada SIFIRLANMAZ — sonuc
            // ekrani kapandiktan sonra da son macin skoru okunabilsin diye; sifirlama
            // ServerStartMatch'in isi.
            ResetPlayers(clearPersonalStats: false);
        }

        // ------------------------------------------------------------------ skor

        /// <summary>Sunucu, gercek bir oldurmede cagirir (bkz. <see cref="PlayerHealth"/>).
        /// Intihar ve kaynagi bilinmeyen olum puan yazmaz — o kural cagiran tarafta.</summary>
        public static void ServerAddScore(byte killerTeam)
        {
            var m = Instance;
            if (m == null || !m.IsServer || !m.IsPlaying) return;   // mac disi oldurme sayilmaz

            if (killerTeam == PlayerProfile.TeamBlue)
                m.ScoreBlue.Value = (ushort)(m.ScoreBlue.Value + 1);
            else if (killerTeam == PlayerProfile.TeamRed)
                m.ScoreRed.Value = (ushort)(m.ScoreRed.Value + 1);
        }

        // ------------------------------------------------------------------ oyuncular

        static void ResetPlayers(bool clearPersonalStats)
        {
            var all = PlayerIdentity.All;
            for (int i = 0; i < all.Count; i++)
            {
                var id = all[i];
                if (id == null) continue;

                if (clearPersonalStats)
                {
                    id.Kills.Value = 0;
                    id.Deaths.Value = 0;
                }

                var hp = id.GetComponent<PlayerHealth>();
                if (hp != null) hp.ServerResetForMatch();
            }
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir: onceki oturumun yok edilmis
        // ornegine tutunmak ikinci Play'de "MissingReference" verirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;
    }
}
