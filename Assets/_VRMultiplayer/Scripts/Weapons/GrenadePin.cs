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
        /// <param name="profile">ARTIK KULLANILMIYOR. Bir ara pimin yeri buradaki atolye
        /// bilek pozundan turetiliyordu; o yol birakildi cunku pim artik parmak kemigine
        /// SABIT oturuyor (bkz. FitToBone). Atolyede ayarlanan PARMAK POZU yine gecerli,
        /// o ayri bir alan. Imza cagiranlari bozmamak icin duruyor.</param>
        /// <param name="leftHand">Pimi ceken el SOL mu? Atolyedeki ayar SOL ele gore yazildi;
        /// sag elle cekilirse X'te aynalanir.</param>
        public static Transform DetachTo(Transform root, Transform hand, GrenadeConfig cfg,
                                         WeaponGripProfile profile = null, bool leftHand = true)
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

            // BOMBADAKI GERCEK BOY. Pim, cekilmeden onceki buyuklugunde gorunmeli - cihazda
            // "cekilmeden onceki boyutu ile cekildikten sonraki ayni degil" dendi. Olcum
            // BURADA yapilmali: parcalar hala bombanin olcek zincirinde, hicbir sey degismedi.
            Bounds ilkSinir;
            float ilkBoy = 0f;
            if (WorldBounds(holder, out ilkSinir))
                ilkBoy = Mathf.Max(ilkSinir.size.x, Mathf.Max(ilkSinir.size.y, ilkSinir.size.z));

            // ---- PIM ISARET PARMAGININ UCUNA YAPISIR ----
            //
            // NEDEN ARTIK EL CIPASINA BAGLANMIYOR: cipanin olcegi OLCULDU ve
            // (0.080, 0.045, 0.130) cikti - uc eksende bambaska. Pim oraya baglanip olcek
            // terslenince (12.5, 22.2, 7.7) gibi bir carpan gerekiyor; ama pim ayni zamanda
            // DONDURULMUS durumda ve Unity'de duzgun olmayan olcekli bir ebeveynin altinda
            // dondurulmus cocuk EGRILIR - olcek eksenlere dagilir. Cihazda "pim buyuyor"
            // denen sey buydu: kod pimi buyutmuyordu, cipanin olcegi onu carpitiyordu.
            //
            // Parmak kemikleri temiz ve TEKDUZE (1.1) olcekli. Istenen davranis da bu:
            // pim isaret parmagina child olsun ve orada YAPISIK kalsin.
            //
            // ATOLYE POZU ARTIK KULLANILMIYOR (PlaceFromWorkshopPose): pimin yeri kemige
            // sabit. Atolyede ayarlanan PARMAK POZU yine gecerli, o ayri bir alan.
            Transform bone = IndexTip(hand, leftHand);
            if (bone == null)
            {
                // UZAK OYUNCU: FP eli yalnizca sahipte kurulur, kemik yok. Eski yola dus -
                // pim en azindan adamin elinde gorunsun.
                holder.SetParent(hand, false);
                holder.localScale = InverseScale(hand.lossyScale, root.lossyScale);
                holder.localPosition = cfg != null ? cfg.pinHandLocalPosition : Vector3.zero;
                holder.localRotation = Quaternion.Euler(cfg != null ? cfg.pinHandLocalEuler : Vector3.zero);
                return holder;
            }

            holder.SetParent(bone, false);
            holder.localScale = Vector3.one;
            holder.localRotation = Quaternion.Euler(cfg != null ? cfg.pinHandLocalEuler : Vector3.zero);
            FitToBone(holder, bone, cfg, ilkBoy);
            return holder;
        }

        /// <summary>
        /// Pimi kemige OTURTUR: once boyutunu normalize eder, sonra gorsel merkezini kemigin
        /// ucuna tasir.
        ///
        /// NEDEN OLCEK DUZELTMESI GEREKIYOR: pim bombadayken onun olcek zincirinde, elde
        /// ise kemigin zincirinde. Ikisi ayni degil (cipa 0.080/0.045/0.130, kemik
        /// tekduze 1.1), dolayisiyla hicbir sey yapmazsak pim cekilince buyur ya da
        /// kuculur. Cihazda "cekilmeden onceki boyu ile cekildikten sonraki ayni degil"
        /// denen sey buydu.
        ///
        /// HEDEF, BOMBADAKI BOY. Sabit bir sayiya normalize etmek de denendi ve
        /// reddedildi: pim modelin kendi orantisinda kalmali. pinDisplaySize sifirdan
        /// farkli verilirse o boy zorlanir - yalnizca bir modelin pimi gercekten
        /// orantisizsa kullanilmali.
        ///
        /// IKI ADIMLI OLCUM: once olcek 1 iken gercek dunya boyu OLCULUR, sonra hedefe
        /// bolunur. Modelin ic olceklerini ya da kemigin lossyScale'ini tahmin etmeye gerek
        /// kalmaz - ne cikarsa ona gore duzeltilir.
        ///
        /// MERKEZ, ORIJIN DEGIL: tutamagin orijini bombanin orijinidir ve pim parcalari onun
        /// icinde kendi offsetleriyle durur. Orijini kemige koymak pimi parmaga koymaz;
        /// tasinmasi gereken sey GORUNEN kutlenin merkezi. (Ayni tuzak saat ekraninda da
        /// yasandi.)
        /// </summary>
        static void FitToBone(Transform holder, Transform bone, GrenadeConfig cfg, float ilkBoy)
        {
            Bounds b;
            if (!WorldBounds(holder, out b)) return;

            float boy = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

            // HEDEF BOY: config sifirdan farkli bir deger ZORLAMIYORSA bombadaki boy.
            // Kemige baglanmak olcek zincirini degistiriyor (cipa 0.080/0.045/0.130 iken
            // kemik tekduze 1.1), telafi etmezsek pim cekilince buyuyor ya da kuculuyor.
            float cfgHedef = cfg != null ? cfg.pinDisplaySize : 0f;
            float hedef = cfgHedef > 0f ? cfgHedef : ilkBoy;
            if (hedef > 0f && boy > 1e-5f)
            {
                holder.localScale *= hedef / boy;
                if (!WorldBounds(holder, out b)) return;   // olcek degisti, sinirlar da
            }

            Vector3 nokta = bone.position
                          + bone.rotation * (cfg != null ? cfg.pinFingerTipOffset : Vector3.zero);
            holder.position += nokta - b.center;
        }

        /// <summary>
        /// Bu dugumun altindaki mesh'lerin dunya sinir kutusu.
        ///
        /// RENDERER.BOUNDS KULLANILMIYOR — bilerek. Olculdu: yeni yaratilmis ve daha yeni
        /// yeniden parent edilmis bir nesnede Renderer.bounds BOS donebiliyor (merkez sifir,
        /// boyut sifir), cunku Unity onu bir sonraki cizime kadar tazelemiyor. Pim tam da
        /// oyle bir anda olculuyor; bos sinirlarla merkezi kemige tasimak pimi metrelerce
        /// oteye firlatirdi.
        ///
        /// Onun yerine mesh'in KENDI sinir kutusunun sekiz kosesi transform ile dunyaya
        /// tasiniyor. Bu her zaman dogru, cunku hicbir onbellege dayanmiyor.
        /// </summary>
        static bool WorldBounds(Transform t, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                Bounds lb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 k = lb.center + Vector3.Scale(lb.extents, new Vector3(
                        (i & 1) == 0 ? -1f : 1f,
                        (i & 2) == 0 ? -1f : 1f,
                        (i & 4) == 0 ? -1f : 1f));
                    Vector3 w = mf.transform.TransformPoint(k);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; }
                    else b.Encapsulate(w);
                }
            }
            return any;
        }

        /// <summary>Isaret parmagi ucu. Meta elinde gercek bir ucu isaretcisi var; yoksa
        /// son boguma dusulur.</summary>
        static Transform IndexTip(Transform hand, bool leftHand)
        {
            string s = leftHand ? "l" : "r";
            // ISARET PARMAGININ DISTAL UCU. Istenen kemik "Left_IndexDistalEnd" diye soylendi
            // ama o ad FP_Hands.fbx'e (askerin eski eli) ait; oyunda kosan model META eli ve
            // karsiligi "b_l_index_null". Olculdu: Left_* kemikleri calisma aninda YOK.
            var t = FirstPersonHandView.FindBone(hand, "b_" + s + "_index_null");
            if (t == null) t = FirstPersonHandView.FindBone(hand, s + "_index_finger_tip_marker");
            if (t == null) t = FirstPersonHandView.FindBone(hand, "b_" + s + "_index3");
            if (t == null) t = FirstPersonHandView.FindBone(hand, leftHand ? "Left_IndexDistalEnd"
                                                                          : "Right_IndexDistalEnd");
            return t;
        }
    }
}
