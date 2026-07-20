using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// <see cref="WeaponCombatConfig"/>'in ag aynasi (DTO). SINIF olmasi bilincli: JsonUtility
    /// deserialize ederken sinifi once varsayilan kuruculariyla olusturur, SONRA mevcut alanlari
    /// yazar — yani ESKI bir payload'da bulunmayan yeni alanlar varsayilanda kalir (struct'ta
    /// SIFIRLANIRLARDI: pelletCount=0 hic ray atmamak, fireVolume=0 sessiz silah demekti).
    /// Yine de <see cref="Sanitize"/> her deserialize sonrasi ZORUNLU calisir — savunma katmani.
    /// </summary>
    [Serializable]
    public class WeaponCombatData
    {
        public string weaponName = "";

        public int fireMode = 0;                 // FireMode enum degeri (int: JSON kararliligi)
        public float fireInterval = 0.18f;
        public int burstCount = 3;
        public float burstShotInterval = 0.06f;
        public float range = 60f;

        public int pelletCount = 1;
        public float pelletSpreadDegrees = 0f;
        public float pelletDamageScale = 1f;

        public int headDamage = 0;
        public int torsoDamage = 0;
        public int armDamage = 0;
        public int legDamage = 0;

        public int magazineSize = 0;
        public int spareMagazines = -1;
        public float reloadDuration = 1.4f;

        public float spreadBase = 0f;
        public float spreadPerShot = 0f;
        public float spreadMax = 3f;
        public float spreadDecayHalfLife = 0.18f;

        public float kickPitchPerShot = 0f;
        public float kickYawJitter = 0f;
        public float kickBackMeters = 0f;
        public float maxAccumPitch = 8f;
        public float maxAccumBack = 0.04f;
        public float recoilDecayHalfLife = 0.12f;
        public float recoilRestDecayHalfLife = 0.07f;
        public float supportRecoilMultiplier = 0.55f;

        public string fireClip = "";
        public string dryFireClip = "";
        public string reloadStartClip = "";
        public string reloadEndClip = "";
        public float fireVolume = 1f;
        public float firePitchMin = 0.95f;
        public float firePitchMax = 1.05f;
        public float dryFireVolume = 0.6f;
        public float reloadVolume = 0.8f;
        public float soundMaxDistance = 120f;

        public static WeaponCombatData FromConfig(WeaponCombatConfig c)
        {
            return new WeaponCombatData
            {
                weaponName = string.IsNullOrEmpty(c.weaponName) ? c.name.Replace("_Combat", "") : c.weaponName,
                fireMode = (int)c.fireMode,
                fireInterval = c.fireInterval,
                burstCount = c.burstCount,
                burstShotInterval = c.burstShotInterval,
                range = c.range,
                pelletCount = c.pelletCount,
                pelletSpreadDegrees = c.pelletSpreadDegrees,
                pelletDamageScale = c.pelletDamageScale,
                headDamage = c.headDamage,
                torsoDamage = c.torsoDamage,
                armDamage = c.armDamage,
                legDamage = c.legDamage,
                magazineSize = c.magazineSize,
                spareMagazines = c.spareMagazines,
                reloadDuration = c.reloadDuration,
                spreadBase = c.spreadBase,
                spreadPerShot = c.spreadPerShot,
                spreadMax = c.spreadMax,
                spreadDecayHalfLife = c.spreadDecayHalfLife,
                kickPitchPerShot = c.kickPitchPerShot,
                kickYawJitter = c.kickYawJitter,
                kickBackMeters = c.kickBackMeters,
                maxAccumPitch = c.maxAccumPitch,
                maxAccumBack = c.maxAccumBack,
                recoilDecayHalfLife = c.recoilDecayHalfLife,
                recoilRestDecayHalfLife = c.recoilRestDecayHalfLife,
                supportRecoilMultiplier = c.supportRecoilMultiplier,
                fireClip = c.fireClip,
                dryFireClip = c.dryFireClip,
                reloadStartClip = c.reloadStartClip,
                reloadEndClip = c.reloadEndClip,
                fireVolume = c.fireVolume,
                firePitchMin = c.firePitchMin,
                firePitchMax = c.firePitchMax,
                dryFireVolume = c.dryFireVolume,
                reloadVolume = c.reloadVolume,
                soundMaxDistance = c.soundMaxDistance,
            };
        }

        /// <summary>Bozuk/eksik payload'un oyunu kirmasini engeller. Deserialize edilen HER kayit
        /// icin zorunlu: 0 pellet hic atis, 0 carpan sifir hasar, ters pitch araligi vb. uretmesin.</summary>
        public void Sanitize()
        {
            if (weaponName == null) weaponName = "";
            fireMode = Mathf.Clamp(fireMode, 0, 2);
            if (fireInterval <= 0.005f) fireInterval = 0.18f;
            burstCount = Mathf.Clamp(burstCount, 1, 16);
            if (burstShotInterval <= 0.005f) burstShotInterval = 0.06f;
            if (range <= 0f) range = 60f;
            pelletCount = Mathf.Clamp(pelletCount, 1, 32);
            pelletSpreadDegrees = Mathf.Clamp(pelletSpreadDegrees, 0f, 45f);
            if (pelletDamageScale <= 0f) pelletDamageScale = 1f;
            headDamage = Mathf.Max(0, headDamage);
            torsoDamage = Mathf.Max(0, torsoDamage);
            armDamage = Mathf.Max(0, armDamage);
            legDamage = Mathf.Max(0, legDamage);
            magazineSize = Mathf.Max(0, magazineSize);
            if (spareMagazines < -1) spareMagazines = -1;
            if (reloadDuration <= 0f) reloadDuration = 1.4f;
            spreadBase = Mathf.Max(0f, spreadBase);
            spreadPerShot = Mathf.Max(0f, spreadPerShot);
            spreadMax = Mathf.Max(0f, spreadMax);
            if (spreadDecayHalfLife <= 0f) spreadDecayHalfLife = 0.18f;
            if (recoilDecayHalfLife <= 0f) recoilDecayHalfLife = 0.12f;
            if (recoilRestDecayHalfLife <= 0f) recoilRestDecayHalfLife = 0.07f;
            if (supportRecoilMultiplier <= 0f) supportRecoilMultiplier = 1f;
            fireVolume = Mathf.Clamp01(fireVolume);
            dryFireVolume = Mathf.Clamp01(dryFireVolume);
            reloadVolume = Mathf.Clamp01(reloadVolume);
            if (firePitchMin <= 0f) firePitchMin = 1f;
            if (firePitchMax < firePitchMin) firePitchMax = firePitchMin;
            if (soundMaxDistance < 1f) soundMaxDistance = 120f;
        }
    }

    /// <summary>
    /// COZULMUS savas degerleri — <see cref="NetworkWeapon"/> ve <see cref="WeaponRecoil"/>'un
    /// her kare okudugu duz anlik-goruntu. Kaynak zinciri (NetworkWeapon.ResolveCombat):
    /// agdan gelen kayit → profile.combat SO → eski profil alanlari → kod varsayilanlari.
    /// </summary>
    public struct CombatValues
    {
        public FireMode fireMode;
        public float fireInterval;
        public int burstCount;
        public float burstShotInterval;
        public float range;

        public int pelletCount;
        public float pelletSpreadDegrees;
        public float pelletDamageScale;

        public int headDamage, torsoDamage, armDamage, legDamage;

        public int magazineSize;
        public int spareMagazines;
        public float reloadDuration;

        public float spreadBase, spreadPerShot, spreadMax, spreadDecayHalfLife;

        public float kickPitchPerShot, kickYawJitter, kickBackMeters;
        public float maxAccumPitch, maxAccumBack;
        public float recoilDecayHalfLife, recoilRestDecayHalfLife;
        public float supportRecoilMultiplier;

        public string fireClip, dryFireClip, reloadStartClip, reloadEndClip;
        public float fireVolume, firePitchMin, firePitchMax, dryFireVolume, reloadVolume;
        public float soundMaxDistance;

        /// <summary>Profilsiz silah: bugunku sabit davranis (inspector kadans/menzil, geri kalani notr).</summary>
        public static CombatValues Defaults(float fireInterval, float range)
        {
            var v = FromData(new WeaponCombatData());
            v.fireInterval = fireInterval;
            v.range = range;
            v.supportRecoilMultiplier = 1f;
            return v;
        }

        public static CombatValues FromData(WeaponCombatData d)
        {
            return new CombatValues
            {
                fireMode = (FireMode)d.fireMode,
                fireInterval = d.fireInterval,
                burstCount = d.burstCount,
                burstShotInterval = d.burstShotInterval,
                range = d.range,
                pelletCount = d.pelletCount,
                pelletSpreadDegrees = d.pelletSpreadDegrees,
                pelletDamageScale = d.pelletDamageScale,
                headDamage = d.headDamage,
                torsoDamage = d.torsoDamage,
                armDamage = d.armDamage,
                legDamage = d.legDamage,
                magazineSize = d.magazineSize,
                spareMagazines = d.spareMagazines,
                reloadDuration = d.reloadDuration,
                spreadBase = d.spreadBase,
                spreadPerShot = d.spreadPerShot,
                spreadMax = d.spreadMax,
                spreadDecayHalfLife = d.spreadDecayHalfLife,
                kickPitchPerShot = d.kickPitchPerShot,
                kickYawJitter = d.kickYawJitter,
                kickBackMeters = d.kickBackMeters,
                maxAccumPitch = d.maxAccumPitch,
                maxAccumBack = d.maxAccumBack,
                recoilDecayHalfLife = d.recoilDecayHalfLife,
                recoilRestDecayHalfLife = d.recoilRestDecayHalfLife,
                supportRecoilMultiplier = d.supportRecoilMultiplier,
                fireClip = d.fireClip,
                dryFireClip = d.dryFireClip,
                reloadStartClip = d.reloadStartClip,
                reloadEndClip = d.reloadEndClip,
                fireVolume = d.fireVolume,
                firePitchMin = d.firePitchMin,
                firePitchMax = d.firePitchMax,
                dryFireVolume = d.dryFireVolume,
                reloadVolume = d.reloadVolume,
                soundMaxDistance = d.soundMaxDistance,
            };
        }

        public static CombatValues FromConfig(WeaponCombatConfig c)
        {
            var d = WeaponCombatData.FromConfig(c);
            d.Sanitize();
            return FromData(d);
        }

        /// <summary>ESKI profil alanlarindan (combat referansi bos profiller — geriye uyum).
        /// overrideFire kapaliysa kadans/menzil inspector degerlerinde kalir; bugunku davranisin
        /// birebir aynisi.</summary>
        public static CombatValues FromLegacyProfile(WeaponGripProfile p, float inspectorInterval, float inspectorRange)
        {
            var v = FromData(new WeaponCombatData());
            v.fireMode = p.fireMode;
            v.fireInterval = p.overrideFire ? p.fireInterval : inspectorInterval;
            v.range = p.overrideFire ? p.range : inspectorRange;
            v.magazineSize = p.magazineSize;
            v.spareMagazines = p.spareMagazines;
            v.reloadDuration = p.reloadDuration;
            v.spreadBase = p.spreadBase;
            v.spreadPerShot = p.spreadPerShot;
            v.spreadMax = p.spreadMax;
            v.spreadDecayHalfLife = p.spreadDecayHalfLife;
            v.kickPitchPerShot = p.kickPitchPerShot;
            v.kickYawJitter = p.kickYawJitter;
            v.kickBackMeters = p.kickBackMeters;
            v.maxAccumPitch = p.maxAccumPitch;
            v.maxAccumBack = p.maxAccumBack;
            v.recoilDecayHalfLife = p.recoilDecayHalfLife;
            v.recoilRestDecayHalfLife = p.recoilRestDecayHalfLife;
            v.supportRecoilMultiplier = p.supportRecoilMultiplier;
            if (v.fireInterval <= 0.005f) v.fireInterval = 0.18f;
            if (v.range <= 0f) v.range = 60f;
            return v;
        }
    }

    /// <summary>Aga giden tam set: tum silahlarin verisi + siralama icin versiyon.</summary>
    [Serializable]
    public class WeaponCombatSet
    {
        /// <summary>Alan SILME/yeniden adlandirmada artirilir (ekleme geriye-guvenli, gerektirmez).
        /// Istemci kendi sabitinden BUYUK gorurse seti reddedip gomulu yedege duser.</summary>
        public const int CurrentSchema = 1;

        public int schemaVersion = CurrentSchema;
        public int version = 0;
        public List<WeaponCombatData> entries = new List<WeaponCombatData>();
    }
}
