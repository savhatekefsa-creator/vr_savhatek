using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRMultiplayer
{
    /// <summary>
    /// Saydam "hayalet" malzeme varyantlari. Olen oyuncunun avatari bunlarla cizilir
    /// (bkz. <see cref="PlayerIdentity"/>): karsidaki oyuncu oldugunu boyle anlar. Yalnizca
    /// ton degistirmek (MaterialPropertyBlock ile gri) kulaklikta yeterince okunmadi —
    /// saydamlik render modunu degistirmeyi gerektirir, MPB bunu YAPAMAZ.
    ///
    /// Varyant KAYNAK malzeme basina BIR kez uretilir; tum oyuncular ayni varyanti paylasir.
    /// Oyuncu basina kopya uretmek SRP batching'i kalici olarak bozar ve despawn'da sizar —
    /// ayni ders icin bkz. <see cref="MaterialDoubleSided"/>.
    /// </summary>
    public static class MaterialGhost
    {
        static readonly Dictionary<Material, Material> _cache = new Dictionary<Material, Material>();

        // Domain reload kapali projede play'e her giriste statikler elle sifirlanir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _cache.Clear();

        /// <summary>Kaynak malzemenin paylasimli saydam varyanti. URP Lit disi (= <c>_Surface</c>
        /// tasimayan) malzemelerde KAYNAGIN KENDISI doner: yarisi saydam yarisi opak bir avatar,
        /// hic donusmemis olandan daha kotu okunur.</summary>
        public static Material Of(Material src)
        {
            if (src == null) return null;
            if (_cache.TryGetValue(src, out var g) && g != null) return g;

            if (!src.HasProperty("_Surface")) { _cache[src] = src; return src; }

            g = new Material(src) { name = src.name + " (Ghost)" };

            g.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
            g.SetFloat("_Blend", 0f);     // 0 = Alpha harmani
            g.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            g.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (g.HasProperty("_SrcBlendAlpha")) g.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (g.HasProperty("_DstBlendAlpha")) g.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);

            // Duz alfa harmani: URP'nin "preserve specular" yolu saydamda parlamayi one
            // cikarip hayaleti hayaletten cok islak plastige benzetiyor.
            if (g.HasProperty("_BlendModePreserveSpecular")) g.SetFloat("_BlendModePreserveSpecular", 0f);

            // ZWrite ACIK birakiliyor. Avatar 10 AYRI parca; saydamda derinlik yazimi kapanirsa
            // parcalar yanlis sirada cizilir ve govdenin ici gorunur (kol, kafanin icinden
            // gecer). Acik kalinca tek katmanli, temiz bir siluet cikar — tam cam etkisi
            // vermez ama hayalet icin istenen zaten bu.
            g.SetFloat("_ZWrite", 1f);

            g.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            g.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            g.renderQueue = (int)RenderQueue.Transparent;

            _cache[src] = g;
            return g;
        }
    }
}
