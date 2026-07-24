using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Pim geometrisini bombadan ayirip cekenin ELINE takar. Tamamen gorsel: paketteki uc
    /// bombada da pim (halka + kanca) ayri mesh dugumleri ve collider'lari YOK — bombanin tek
    /// MeshCollider'i kokte duruyor, dolayisiyla pimi tasimak fizige hic dokunmaz.
    ///
    /// Parcalar tek bir tutamak altinda toplanir: boylece halka ve kanca birbirine gore
    /// bozulmadan, tek bir offset ile ele yerlestirilir. Emniyet kolu (handle/flap) bilerek
    /// disarida birakilir — o bombada kalir.
    /// </summary>
    public static class GrenadePin
    {
        const string HolderName = "PimTutamaci";

        /// <summary>Pim parcalarini bulur. <paramref name="configured"/> doluysa ad birebir
        /// eslesir; bos ise adinda "ring" ya da "hook" gecen dugumler pim sayilir (paketteki
        /// Grenade 1/2/3'un pimleri bu sezgiyle eksiksiz bulunur).</summary>
        /// <summary>Ad sezgisi kullanilirken bir parcanin pim sayilmasi icin bombaya gore
        /// olabilecegi azami boy orani. Pim halkasi bombanin yaninda minicik kalir (~0.2);
        /// govdeyi saran bir bant ise ~0.7 cikar ve bu esikte elenir — "ring" adli her seyi
        /// koparmayi onleyen tek koruma budur. Ad ACIKCA verilmisse esik uygulanmaz.</summary>
        const float MaxPartSizeRatio = 0.5f;

        public static List<Transform> FindParts(Transform root, string[] configured)
        {
            bool named = configured != null && configured.Length > 0;
            float rootSize = BoundsSize(root);

            var parts = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                if (!Matches(t.name, configured)) continue;

                if (!named && rootSize > 0f && BoundsSize(t) > rootSize * MaxPartSizeRatio)
                {
                    Debug.Log($"[Bomba] '{t.name}' adi pime benziyor ama bombanin buyuk bir " +
                              "parcasi — pim sayilmadi. Yanlissa GrenadeConfig.pinNodes ile acikca yaz.");
                    continue;
                }
                parts.Add(t);
            }

            // Ic ice eslesme olursa (orn "hook" altinda "hook_cylinder") yalnizca EN UST parcalar
            // tasinir; alttakiler zaten onunla birlikte gelir, ayrica tasimak hiyerarsiyi bozar.
            // Eleme SNAPSHOT uzerinden yapilir: RemoveAll listeyi yerinde sikistirdigi icin
            // yordamin kendi listesini okumasi yanlis sonuc verir.
            var all = new List<Transform>(parts);
            parts.RemoveAll(t => IsDescendantOfAny(t, all));
            return parts;
        }

        /// <summary>Bu dugumun (cocuklariyla birlikte) render sinirlarinin kosegen boyu;
        /// renderer yoksa 0. Dunya uzayinda olculur — oran karsilastirmasi oldugu icin
        /// bombanin olcegi sonucu etkilemez.</summary>
        static float BoundsSize(Transform t)
        {
            var rends = t.GetComponentsInChildren<Renderer>(true);
            Bounds b = default;
            bool any = false;
            foreach (var r in rends)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? b.size.magnitude : 0f;
        }

        static bool Matches(string name, string[] configured)
        {
            if (configured != null && configured.Length > 0)
            {
                foreach (var n in configured)
                    if (!string.IsNullOrEmpty(n) && name == n) return true;
                return false;
            }
            string lower = name.ToLowerInvariant();
            return lower.Contains("ring") || lower.Contains("hook");
        }

        /// <summary>Ebeveyn olcegini goturen lokal olcek: sonucun dunya olcegi
        /// <paramref name="want"/> olur. Sifir bilesen bolmeyi patlatmasin diye korunur.</summary>
        static Vector3 InverseScale(Vector3 parent, Vector3 want)
        {
            return new Vector3(
                Mathf.Approximately(parent.x, 0f) ? 1f : want.x / parent.x,
                Mathf.Approximately(parent.y, 0f) ? 1f : want.y / parent.y,
                Mathf.Approximately(parent.z, 0f) ? 1f : want.z / parent.z);
        }

        static bool IsDescendantOfAny(Transform t, List<Transform> others)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (others.Contains(p)) return true;
            return false;
        }

        /// <summary>Pimi bombadan ayirip <paramref name="hand"/> anchor'ina takar ve tutamagi
        /// dondurur (yoksa null). Parcalarin birbirine gore duruslari korunur.</summary>
        public static Transform DetachTo(Transform root, Transform hand, GrenadeConfig cfg)
        {
            if (root == null || hand == null) return null;

            var parts = FindParts(root, cfg != null ? cfg.pinNodes : null);
            if (parts.Count == 0)
            {
                Debug.LogWarning($"[Bomba] '{root.name}' uzerinde pim dugumu bulunamadi — pim " +
                                 "gorseli olmadan devam ediliyor. Dugum adlarini GrenadeConfig." +
                                 "pinNodes'a yazarak duzeltebilirsin.");
                return null;
            }

            // Hangi parcalarin koptugu Console'dan gorulebilsin: yanlis bir parca ucuyorsa
            // duzeltmek icin config'e yazilacak adlar burada hazir duruyor.
            Debug.Log($"[Bomba] '{root.name}' pimi cekildi: " +
                      string.Join(", ", parts.ConvertAll(p => p.name)));

            // Tutamak once BOMBA uzayinda dogar (kimlik lokal transform), parcalar dunya
            // duruslari korunarak icine alinir; sonra tutamak ele tasinir. Boylece pim, bombanin
            // uzerindeki dizilisiyle birebir ayni sekilde elde durur.
            var holder = new GameObject(HolderName).transform;
            holder.SetParent(root, false);
            foreach (var p in parts)
                p.SetParent(holder, true);

            holder.SetParent(hand, false);
            holder.localPosition = cfg != null ? cfg.pinHandLocalPosition : Vector3.zero;
            holder.localRotation = Quaternion.Euler(cfg != null ? cfg.pinHandLocalEuler : Vector3.zero);
            // Bomba ile el farkli olcekte olabilir (silahlar 2x, avatar 1x): pimin DUNYA boyu
            // bombadaki haliyle ayni kalsin, elin olcegi onu buyutup kucultmesin.
            holder.localScale = InverseScale(hand.lossyScale, root.lossyScale);
            return holder;
        }
    }
}
