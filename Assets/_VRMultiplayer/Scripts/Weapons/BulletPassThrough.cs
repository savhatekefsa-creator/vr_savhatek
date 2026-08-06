using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// "MERMI GECIRIR" isareti. Bu bilesenin ALTINDA kalan her collider silah isinlarina
    /// gorunmez olur; fiziksel varligi aynen kalir — oyuncu hala icinden gecemez, prop hala
    /// navmesh'i keser, el hala tutunabilir.
    ///
    /// NEDEN COLLIDER'I SILMEK DEGIL. Mermi gecirmesini istedigimiz iki parca da (tel silah
    /// rafi, merdiven) ARADAN GORULEN, ama GECILEMEYEN seyler. Collider'i sokseydik ikisinin
    /// icinden yuruyup gecerdik — insa modunda oyuncunun koydugu rafin icinde durmasi demek
    /// bu. Gecirgenligi mermiye ozel tutmak, "gorunen sey ne ise o" kuralini bozmadan tek
    /// dogru davranisi verir: deligi olan bir sey mermiyi gecirir, govdesi olan bir sey
    /// oyuncuyu durdurur.
    ///
    /// TERSI DE AYNI KURALIN PARCASI: mermiyi durdurmasi gereken her duvarin GERCEK bir
    /// collider'i olmak zorunda. Paintball paketindeki dikenli ucu olan panel (Wood_Modular_6
    /// / _7) collider'siz geliyordu — yani hem mermi hem oyuncu iginden geciyordu. Kural
    /// listesi ve onarimi editor tarafinda: menu 45 (<c>PropBulletRules</c>).
    ///
    /// Isaret KOKE konur, tek tek collider'lara degil: paket prefablari parcali (raf 23
    /// kutu carpisici) ve yeni bir parca eklendiginde unutulmasi kacinilmaz olurdu.
    /// </summary>
    [DisallowMultipleComponent]
    public class BulletPassThrough : MonoBehaviour
    {
        // Sorgu onbellegi: isin YURUYUSU her isabette soruyor ve GetComponentInParent
        // hiyerarsiyi tirmanir. Anahtar collider'in instanceID'si — yok edilmis Unity
        // nesnesini sozlukte tutmamak icin referans degil. Harita yeniden kuruldugunda eski
        // kayitlar bayatlar ama yanlis cevap VERMEZ (yeni collider = yeni id); sozlugun
        // sinirsiz buyumemesi icin Clear esigi var.
        const int CacheLimit = 4096;
        static readonly Dictionary<int, bool> _cache = new Dictionary<int, bool>(256);

        /// <summary>Bu collider'i silah isinlari gormezden gelmeli mi?</summary>
        public static bool Passes(Collider c)
        {
            if (c == null) return false;

            int key = c.GetInstanceID();
            if (_cache.TryGetValue(key, out bool cached)) return cached;

            // includeInactive: prop kapali bir ara dugumun altinda olabilir; isaretin
            // gorunurlugu obje agacinin aktifligine baglanmamali.
            bool pass = c.GetComponentInParent<BulletPassThrough>(true) != null;

            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[key] = pass;
            return pass;
        }

        /// <summary>
        /// Mermi mantigiyla AYNI isin: gecirgen isaretli her sey atlanir, ilk GERCEK engel
        /// doner. Lazer benegi bunu kullanir — benek merminin durdugu yerde olmazsa lazer
        /// nisan almaz, yalan soyler.
        /// </summary>
        public static bool Raycast(Vector3 origin, Vector3 dir, float maxDistance,
            out RaycastHit hit, QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore)
        {
            hit = default;
            int n = Physics.RaycastNonAlloc(origin, dir, _rayHits, maxDistance,
                                            Physics.AllLayers, triggers);
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                if (Passes(_rayHits[i].collider)) continue;
                // Sirali degil: RaycastNonAlloc mesafeye gore siralamaz. Tek bir engel
                // aradigimiz icin siralamak yerine EN YAKINI seciyoruz — 32 elemanda
                // tarama, Array.Sort'un delegate maliyetinden ucuz.
                if (!found || _rayHits[i].distance < hit.distance) { hit = _rayHits[i]; found = true; }
            }
            return found;
        }

        static readonly RaycastHit[] _rayHits = new RaycastHit[32];
    }
}
