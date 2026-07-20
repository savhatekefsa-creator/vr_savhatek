using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// SUNUCU-OTORITER silah savas ayarlari — atis sekli (Semi/Auto/Burst), kadans, pellet,
    /// silah-basina bolgesel hasar, sarjor, sacilim, tepme ve sesler tek asset'te.
    ///
    /// Kaynak-dogruluk SUNUCUDUR: sunucu (PC, editorde) bu asset'leri okur ve degerleri aga
    /// yayinlar (<see cref="WeaponConfigSyncTool"/>); istemciler agdan geleni uygular, kendi
    /// gomulu kopyalari yalnizca yedektir. Boylece Play sirasinda Inspector'dan yapilan bir
    /// degisiklik KULAKLIK BUILD'I YENILENMEDEN tum istemcilere ulasir (canli ayar).
    ///
    /// Baglanti: <see cref="WeaponGripProfile.combat"/> alani bu asset'i isaret eder; ikinci bir
    /// isim-eslestirme katmani yoktur. Alan bos birakilirsa profildeki ESKI savas alanlari
    /// gecerli kalir — mevcut silahlar davranis degistirmez.
    ///
    /// Tutus/parmak/ray ve namlu GEOMETRISI bilincli olarak profilde kalir: onlar ayar degil,
    /// yakalama araciyla uretilen istemci-yerel veridir.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Weapon Combat Config", fileName = "WeaponCombatConfig")]
    public class WeaponCombatConfig : ScriptableObject
    {
        [Header("Kimlik (ag payload anahtari — profil eslesme adiyla AYNI olmali)")]
        public string weaponName = "";

        [Header("Ates")]
        [Tooltip("Semi: tetik basina tek atis. Auto: basili tutuldukca tarar. Burst: tetik basina burstCount atis.")]
        public FireMode fireMode = FireMode.Semi;
        [Tooltip("Iki atis (Burst'te iki burst BASLANGICI) arasi minimum sure (s).")]
        public float fireInterval = 0.18f;
        [Tooltip("Burst modunda tetik basina cikan atis sayisi.")]
        public int burstCount = 3;
        [Tooltip("Burst ICINDEKI atislar arasi sure (s).")]
        public float burstShotInterval = 0.06f;
        [Tooltip("Isinin maksimum menzili (m).")]
        public float range = 60f;

        [Header("Pellet (pompali: tek tetikte N sacma)")]
        [Tooltip("Tek tetikte namludan cikan ray sayisi. 1 = normal silah.")]
        public int pelletCount = 1;
        [Tooltip("Pelletlerin sactigi koni yari-acisi (derece) — normal sacilima EK.")]
        public float pelletSpreadDegrees = 0f;
        [Tooltip("Pellet BASINA hasar carpani (pompalida pellet cok, tanesi zayif).")]
        public float pelletDamageScale = 1f;

        [Header("Silah-basina bolgesel hasar (alan 0 ise o bolge CombatConfig varsayilanina duser)")]
        public int headDamage = 0;
        public int torsoDamage = 0;
        public int armDamage = 0;
        public int legDamage = 0;

        [Header("Sarjor (magazineSize 0 = mermi sayilmaz)")]
        public int magazineSize = 0;
        [Tooltip("-1 = sinirsiz yedek.")]
        public int spareMagazines = -1;
        public float reloadDuration = 1.4f;

        [Header("Sacilim (bloom) — derece")]
        public float spreadBase = 0f;
        public float spreadPerShot = 0f;
        public float spreadMax = 3f;
        public float spreadDecayHalfLife = 0.18f;

        [Header("Tepme — aci: derece, mesafe: DUNYA metresi")]
        public float kickPitchPerShot = 0f;
        public float kickYawJitter = 0f;
        public float kickBackMeters = 0f;
        public float maxAccumPitch = 8f;
        public float maxAccumBack = 0.04f;
        public float recoilDecayHalfLife = 0.12f;
        public float recoilRestDecayHalfLife = 0.07f;
        public float supportRecoilMultiplier = 0.55f;

        [Header("Ses (Resources yolu, orn. WeaponSounds/smg1_fire — klip yoksa HATA DEGIL, sessiz)")]
        public string fireClip = "";
        public string dryFireClip = "";
        public string reloadStartClip = "";
        public string reloadEndClip = "";
        [Range(0f, 1f)] public float fireVolume = 1f;
        [Tooltip("Her atista pitch bu araliktan rastgele secilir — tekduzeligi kirar.")]
        public float firePitchMin = 0.95f;
        public float firePitchMax = 1.05f;
        [Range(0f, 1f)] public float dryFireVolume = 0.6f;
        [Range(0f, 1f)] public float reloadVolume = 0.8f;
        [Tooltip("Sesin tamamen sondugu mesafe (m) — Linear rolloff bu noktada 0'a iner.")]
        public float soundMaxDistance = 120f;
    }
}
