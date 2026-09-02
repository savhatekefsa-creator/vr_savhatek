using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Kol erisim kelepcesi: uzak oyuncularin gordugu kol UZAYAMAZ.
    ///
    /// Kural: bilek hedefi omuzdan kolun boyundan uzaktaysa, bilek o yonde en uzak
    /// erisilebilir noktaya konur - kol dumduz kalir ve hedefe DOGRU bakar, ama
    /// hedefe kadar gitmez. Kumanda 2 m otede yerdeyse kol o yone dogrulur ve
    /// sinirinda durur.
    ///
    /// Neden gerekli: <see cref="Weapons.WeaponHandWeld"/> bilegi MUTLAK yaziyordu
    /// (rig cozuldukten sonra), yani silah nerede olursa olsun bilek oraya
    /// isinlaniyordu. Kol yetisemedigi icin el koldan kopmus gorunuyordu.
    ///
    /// Birinci sahis eli bu kelepceye TABI DEGILDIR - orada kural terstir, el her
    /// zaman tam olarak kumandadadir (bkz. <see cref="FirstPersonHandView"/>).
    ///
    /// DIKKAT - kol boyu neden bir kere olculuyor: weld bilegi mutlak yazdigi icin
    /// bilegin ONCEKI karede birakildigi yer, dirsek-bilek mesafesini degistirir.
    /// Boyu her karede canli olcseydik kendi yazdigimiz degeri geri okur, kelepce
    /// kare kare kayardi. Bu yuzden boy bir kere YEREL uzayda olculur (poza ve
    /// weld'e bagisik) ve kullanim aninda avatarin guncel olcegiyle carpilir -
    /// avatarin surekli boy-fit olceklemesi de boylece dogru takip edilir.
    /// </summary>
    public static class ArmReach
    {
        // Tam 1.0'da dirsek tekil hale gelir ve IK'nin dirsek yonu zipliyabilir;
        // biraz altinda birakmak dirsegi tanimli tutar.
        public const float StraightFraction = 0.98f;

        /// <summary>
        /// Kol boyunu YEREL uzayda olcer (omuz->dirsek + dirsek->bilek). Bir kere,
        /// herhangi bir weld calismadan once cagrilmali. Poza ve olcege bagisiktir.
        /// </summary>
        public static float MeasureLocal(Transform upper, Transform lower, Transform hand)
        {
            if (upper == null || lower == null || hand == null) return 0f;
            return lower.localPosition.magnitude + hand.localPosition.magnitude;
        }

        /// <summary>
        /// Hedefi omuzdan itibaren kolun erisebilecegi mesafeye kelepceler.
        /// <paramref name="localLength"/> <see cref="MeasureLocal"/>'den gelir.
        /// Erisim icindeyse hedef AYNEN doner - normal kullanimda hic devreye
        /// girmez, sadece kol yetismedigi anda calisir.
        /// </summary>
        public static Vector3 Clamp(Vector3 target, Transform upper, float localLength)
        {
            if (upper == null || localLength <= 0.001f) return target;

            // Avatar boy-fit ile surekli olcekleniyor; olcegi kullanim aninda al.
            float scale = upper.lossyScale.x;
            if (scale <= 0.0001f) scale = 1f;

            float max = localLength * scale * StraightFraction;
            if (max <= 0.01f) return target;

            Vector3 dir = target - upper.position;
            float dist = dir.magnitude;
            if (dist <= max || dist < 1e-4f) return target;

            return upper.position + dir * (max / dist);
        }
    }
}
