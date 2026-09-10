using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Bir arayuz ogesinin CALISMA ANINDA uretilmis materyalinin sahibi. Obje yok edilince
    /// materyali de yok eder.
    ///
    /// NEDEN VAR: UITheme her panel yuzeyi/disk/halka/yazi icin `new Material` uretiyor ve
    /// hicbir panel bunlari temizlemiyordu. Materyal bir UnityEngine.Object'tir; sahnedeki
    /// obje yok edilse bile materyal Resources.UnloadUnusedAssets cagrilana kadar bellekte
    /// kalir. En kotu ornek NameEntryPanel: 42 tus x 2 = 84 materyal, ve panel her yeniden
    /// adlandirma/kaydetme akisinda Destroy edilip yeniden kuruluyor — her acilista 84 materyal
    /// daha. PlayerEntryPanel ~100 oge. Bir maç boyunca yuzlerce materyal birikiyordu.
    ///
    /// NASIL: materyali ureten yer, materyali KULLANAN objeye bu bileseni takar. Boylece
    /// sahiplik acik ve otomatik olur; panellerin OnDestroy yazmasi gerekmez, unutulamaz.
    /// Paylasilan materyaller (UITheme.SharedFontMaterial onbellegi) bu bilesene HIC verilmez —
    /// onlar birden fazla oge tarafindan kullaniliyor ve yok edilmemeli.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OwnedMaterial : MonoBehaviour
    {
        Material _a, _b;

        /// <summary>Bu objenin sahiplendigi materyali kaydeder (en fazla iki tane; UI ogeleri
        /// pratikte bir, en fazla iki materyal tasiyor).</summary>
        public static void Attach(GameObject go, Material m)
        {
            if (go == null || m == null) return;
            var o = go.GetComponent<OwnedMaterial>();
            if (o == null) o = go.AddComponent<OwnedMaterial>();
            if (o._a == null || o._a == m) { o._a = m; return; }
            if (o._b == null || o._b == m) { o._b = m; return; }
            // Ucuncu materyal: nadir. Sahiplenilmezse eski davranisa doner (sizar), ama
            // sessiz kalmasin.
            Debug.LogWarning($"[OwnedMaterial] {go.name}: ikiden fazla materyal, sonuncusu sahiplenilmedi.");
        }

        void OnDestroy()
        {
            if (_a != null) Destroy(_a);
            if (_b != null) Destroy(_b);
            _a = _b = null;
        }
    }
}
