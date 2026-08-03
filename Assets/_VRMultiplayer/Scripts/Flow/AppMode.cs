using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Uygulamanin ILK karari: YARATICI mi OYUNCU mu? Akis semasinin serit 1'indeki bu secim
    /// oyuncu girisinden (isim + takim) ONCE yapilir ve gerisini belirler.
    ///
    /// NEDEN AYRI BIR STATIK: yaratici mod ayri bir dalda gelistiriliyor. Iki tarafin ortak bir
    /// dosyayi duzenlemesi gerekseydi birlesme her seferinde satir catismasi uretirdi. Bunun
    /// yerine tek bir BAGLANTI NOKTASI var: <see cref="Chosen"/> olayi. Yaratici taraf ona abone
    /// olur, bu dosyaya kimse dokunmaz.
    ///
    /// Yaratici tarafin yapmasi gereken TEK sey:
    /// <code>
    /// void OnEnable()  => AppMode.Chosen += OnModeChosen;
    /// void OnDisable() => AppMode.Chosen -= OnModeChosen;
    /// void OnModeChosen(AppMode.Mode m) { if (m == AppMode.Mode.Creative) OpenCreativeMenu(); }
    /// </code>
    ///
    /// TUKETICILER: <see cref="UI.ModeSelectUI"/> secimi yapar, <see cref="UI.PlayerEntryUI"/>
    /// yalnizca <see cref="Mode.Player"/> gelince dogar, <see cref="LanBootstrap"/> yaratici
    /// modda ag katilimini kapatir (adanmis SUNUCU bu kapiya takilmaz — bkz. orada).
    /// </summary>
    public static class AppMode
    {
        public enum Mode
        {
            /// <summary>Henuz secilmedi — mod paneli acik.</summary>
            None,
            /// <summary>Harita tasarlama akisi (ayri dal).</summary>
            Creative,
            /// <summary>Isim + takim -> baglan -> kalibrasyon -> mac (bugunku akis).</summary>
            Player,
        }

        public static Mode Current { get; private set; }

        public static bool IsPlayer   => Current == Mode.Player;
        public static bool IsCreative => Current == Mode.Creative;

        /// <summary>Mod degistiginde tetiklenir. Yaratici taraf BUNA abone olur.
        /// <see cref="Mode.None"/> gelmesi "ana menuye donuldu" demektir; acik akislar
        /// kendilerini toplamali.</summary>
        public static event System.Action<Mode> Chosen;

        /// <summary>Modu sec. Panel (ya da masaustu yedegi) cagirir.
        /// Ayni mod tekrar secilirse SESSIZCE doner: abonelerin ikinci kez kurulmasi
        /// (iki giris ekrani, iki menu) bu tek satirla engelleniyor.</summary>
        public static void Choose(Mode m)
        {
            if (m == Current) return;
            Current = m;
            Debug.Log("[AppMode] Mod: " + m);
            Chosen?.Invoke(m);
        }

        /// <summary>Semadaki DONUS: mac/islem bitince ana menuye (mod secimi) don.
        /// Su an cagiran yok — <c>MatchManager</c> geldiginde mac sonu ekrani cagiracak.
        /// Kanca bos degil, CALISIR: <see cref="UI.ModeSelectUI"/> panelini yeniden acar.</summary>
        public static void ReturnToModeSelect() => Choose(Mode.None);

        // Domain reload kapaliyken statikler oyunlar arasi tasinir. Current kalirsa mod paneli
        // hic acilmaz; DAHA KOTUSU, Chosen'daki ABONELIKLER de tasinir ve ikinci Play'de her
        // olay iki kez islenir (iki giris ekrani). Ikisi de burada temizleniyor.
        //
        // SIRA GUVENLIGI: SubsystemRegistration, abonelerin kayit oldugu AfterSceneLoad'dan
        // ONCE calisir — yani hicbir abone silinmis bir listeye yazmaz.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = Mode.None;
            Chosen = null;
        }
    }
}
