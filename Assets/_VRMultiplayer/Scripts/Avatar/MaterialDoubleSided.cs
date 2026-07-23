using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Cift-yuzlu (Cull Off) malzeme varyantlari. Eskiden avatar/el kurulumlari
    /// renderer.materials ile yerinde kopyaliyordu: her avatar icin slot basina yeni malzeme
    /// instance'i uretiliyordu — SRP batching avatarlar arasinda kaliciolarak bozuluyor ve
    /// avatar despawn'inda instance'lar yok edilmedigi icin sizip birikiyordu. Varyant artik
    /// KAYNAK malzeme basina BIR kez uretilir; tum avatarlar/eller ayni varyanti paylasir.
    /// Takim tonu MaterialPropertyBlock ile uygulandigi icin paylasim onunla catismaz.
    /// </summary>
    public static class MaterialDoubleSided
    {
        static readonly Dictionary<Material, Material> _cache = new Dictionary<Material, Material>();

        // Domain reload kapali projede play'e her giriste statikler elle sifirlanir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _cache.Clear();

        /// <summary>Renderer'in slotlarini paylasimli cift-yuzlu varyantlarla degistirir
        /// (malzeme _Cull desteklemiyorsa ya da zaten cift yuzluyse dokunmaz).</summary>
        public static void Apply(Renderer r)
        {
            if (r == null) return;
            var slots = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                var m = slots[i];
                if (m == null || !m.HasProperty("_Cull")) continue;
                if (!_cache.TryGetValue(m, out var ds) || ds == null)
                {
                    if (Mathf.Approximately(m.GetFloat("_Cull"), 0f))
                    {
                        _cache[m] = m; // kaynak zaten cift yuzlu — varyant gereksiz
                        continue;
                    }
                    ds = new Material(m) { name = m.name + " (CullOff)" };
                    ds.SetFloat("_Cull", 0f); // 0 = CullMode.Off
                    _cache[m] = ds;
                }
                if (ds != m) { slots[i] = ds; changed = true; }
            }
            if (changed) r.sharedMaterials = slots;
        }
    }
}
