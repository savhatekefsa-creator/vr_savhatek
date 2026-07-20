using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Silah savas config'lerinin calisma-zamani kayit defteri. SUNUCUDA SO asset'lerinden
    /// (Resources/WeaponCombatConfigs) dolar; ISTEMCIDE agdan gelen setle dolar. Sunucu kendi
    /// SO'larini canli okudugu icin (ResolveCombat profile.combat'a bakar) buradaki kayitlarin
    /// asil tuketicisi ISTEMCILERDIR — agdan gelen kayit varsa profile.combat'in onune gecer.
    ///
    /// Asset'lere ASLA yazilmaz: istemcide gelen degerler yalnizca bellekte yasar (editor-istemci
    /// senaryosunda bile disk kirlenmez).
    /// </summary>
    public static class WeaponConfigRegistry
    {
        static readonly Dictionary<string, WeaponCombatData> _byName =
            new Dictionary<string, WeaponCombatData>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Uygulanan setin versiyonu (0 = henuz ag verisi yok).</summary>
        public static int AppliedVersion { get; private set; }

        /// <summary>Agdan yeni bir set uygulandiginda yayilir — NetworkWeapon'lar dinleyip
        /// degerlerini yeniden cozer (canli ayar).</summary>
        public static event Action ConfigsUpdated;

        /// <summary>Silah adina kayit (agdan gelmis) var mi? Ad, profil eslesme adidir.</summary>
        public static bool TryGet(string weaponName, out WeaponCombatData data)
        {
            if (string.IsNullOrEmpty(weaponName)) { data = null; return false; }
            return _byName.TryGetValue(weaponName, out data);
        }

        /// <summary>SUNUCU: Resources'taki tum config asset'lerinden yayin seti kurar. Her
        /// cagrida SO'larin O ANKI degerleri okunur — canli ayar icin dogru kaynak.</summary>
        public static WeaponCombatSet BuildSetFromResources(int version)
        {
            var set = new WeaponCombatSet { version = version };
            foreach (var cfg in Resources.LoadAll<WeaponCombatConfig>("WeaponCombatConfigs"))
            {
                if (cfg == null) continue;
                var d = WeaponCombatData.FromConfig(cfg);
                d.Sanitize();
                set.entries.Add(d);
            }
            return set;
        }

        /// <summary>ISTEMCI: agdan gelen JSON setini uygular. Sema uyumsuzsa (payload bizden
        /// YENI bir semayla yazilmissa) set reddedilir ve gomulu yedekler gecerli kalir —
        /// hicbir kosulda exception firlatmaz. Donus: uygulandi mi.</summary>
        public static bool ApplyJson(string json)
        {
            WeaponCombatSet set;
            try { set = JsonUtility.FromJson<WeaponCombatSet>(json); }
            catch (Exception e)
            {
                Debug.LogWarning("[SilahConfig] Gelen set cozulemedi: " + e.Message);
                return false;
            }
            if (set == null || set.entries == null) return false;

            if (set.schemaVersion > WeaponCombatSet.CurrentSchema)
            {
                Debug.LogWarning($"[SilahConfig] Sunucu daha yeni bir sema yolluyor " +
                    $"({set.schemaVersion} > {WeaponCombatSet.CurrentSchema}) — gomulu degerlerle " +
                    "devam ediliyor. Bu build guncellenmeli.");
                return false;
            }
            if (set.version != 0 && set.version == AppliedVersion) return true; // ayni set zaten uygulandi

            _byName.Clear();
            foreach (var d in set.entries)
            {
                if (d == null) continue;
                d.Sanitize();
                if (!string.IsNullOrEmpty(d.weaponName)) _byName[d.weaponName] = d;
            }
            AppliedVersion = set.version;
            Debug.Log($"[SilahConfig] {_byName.Count} silah config'i uygulandi (v{set.version}).");
            ConfigsUpdated?.Invoke();
            return true;
        }

        /// <summary>SUNUCU: canli ayar yayinindan sonra yerel tuketicileri de tazeler (sunucu
        /// SO'lari dogrudan okur ama silahlarin cozulmus kopyalari yenilenmeli).</summary>
        public static void RaiseUpdatedLocal() => ConfigsUpdated?.Invoke();

        // Enter Play Mode Options ile domain reload kapaliysa statikler onceki Play'den kirli
        // kalir — her Play basinda temiz baslangic garanti edilir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _byName.Clear();
            AppliedVersion = 0;
            ConfigsUpdated = null;
        }
    }
}
