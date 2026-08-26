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
        /// <param name="profile">Bombanin tutus profili. Doluysa pimin yeri ATOLYEDE ayarlanan
        /// bilek pozundan turetilir (bkz. <see cref="PlaceFromWorkshopPose"/>); yoksa
        /// config'in pinHandLocal* degerlerine dusulur.</param>
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

            holder.SetParent(hand, false);
            // Bomba ile el farkli olcekte olabilir (silahlar 2x, avatar 1x): pimin DUNYA boyu
            // bombadaki haliyle ayni kalsin, elin olcegi onu buyutup kucultmesin.
            holder.localScale = InverseScale(hand.lossyScale, root.lossyScale);

            if (!PlaceFromWorkshopPose(holder, hand, profile, leftHand))
            {
                // Yedek: atolye ayari yok ya da FP eli bulunamadi (uzak oyuncu).
                holder.localPosition = cfg != null ? cfg.pinHandLocalPosition : Vector3.zero;
                holder.localRotation = Quaternion.Euler(cfg != null ? cfg.pinHandLocalEuler : Vector3.zero);
            }

            // Poz oturduktan SONRA pimi isaret parmaginin ucuna kancala. Yukaridaki
            // yerlestirme pimin DONUSUNU verir, bu adim da onu parmak ucuna oturtup
            // parmaga bagli hale getirir. Basarisiz olursa (uzak oyuncu: FP eli yok)
            // eski davranis aynen kalir.
            HookOnIndexTip(holder, hand, leftHand, cfg);
            return holder;
        }

        /// <summary>
        /// PIMI ATOLYEDE AYARLANAN YERE KOYAR.
        ///
        /// Atolyede pim tezgahta sabit durur ve SOL EL ona gore ayarlanir; kaydedilen sey
        /// "bilegin pime gore durusu" (<c>supportHand.fpWristLocal*</c>):
        ///     bilekPoz = pimPoz + pimDonus * offsetPoz
        ///     bilekDonus = pimDonus * offsetDonus
        ///
        /// Oyunda ise elimizde BILEK var, pimi ariyoruz — yani ayni bagintinin TERSI:
        ///     pimDonus = bilekDonus * offsetDonus⁻¹
        ///     pimPoz   = bilekPoz  - pimDonus * offsetPoz
        ///
        /// NEDEN CIPAYA DEGIL DE BILEGE GORE: ayar birinci sahis elinin bilegine gore yapildi.
        /// Kumanda cipasi ile bilek ayni yer degil (el modeli cipanin altinda, kendi
        /// offsetiyle oturuyor); cipayi referans alsaydik ayarladigin poz oyunda kayardi.
        ///
        /// Tutamak yine EL CIPASINA parent kalir — pim boylece uzak oyuncularin gordugu
        /// avatarin elinde de durur. Burada yalnizca yerel poz cipa uzayina cevriliyor.
        /// </summary>
        static bool PlaceFromWorkshopPose(Transform holder, Transform hand,
                                          WeaponGripProfile profile, bool leftHand)
        {
            if (profile == null) return false;

            var hp = profile.supportHand;   // atolyede pim eli = SOL = destek eli
            Vector3 offPos = hp.fpWristLocalPosition;
            Quaternion offRot = hp.FpWristRotation;
            if (offPos == Vector3.zero && offRot == Quaternion.identity) return false;  // ayarlanmamis

            // Ayar SOL ele gore yazildi. Sag elle cekilirse aynalanir — silah tutuslarinda
            // kullanilan kuralin aynisi.
            if (!leftHand)
            {
                offPos = WeaponGripMath.MirrorX(offPos);
                offRot = WeaponGripMath.MirrorX(offRot);
            }

            Transform wrist = FirstPersonHandView.FindWrist(hand, leftHand);
            if (wrist == null) return false;   // uzak oyuncu: FP eli yok

            Quaternion pinRot = wrist.rotation * Quaternion.Inverse(offRot);
            Vector3 pinPos = wrist.position - pinRot * offPos;
            holder.SetPositionAndRotation(pinPos, pinRot);
            return true;
        }

        /// <summary>
        /// Pimi ISARET PARMAGININ UCUNA kancalar: gorsel merkezi parmak ucu isaretcisine
        /// parent edilir — boylece parmak kivrildikce pim de onunla gider.
        ///
        /// KONUMA/DONUSE DOKUNULMAZ. Once bu adim pimin gorsel merkezini zorla parmak
        /// ucuna tasiyordu; sonuc, ATOLYEDE ayarlanan pozla cakisti (konumu kod, donusu
        /// ayar belirleyince pim caprazlasip birkac cm kaydi). Nerede duracagina karar
        /// veren tek yer atolye olmali — oyuncu onu panelden GOREREK ayarliyor, kod
        /// tahmin yurutmemeli. Burasi yalnizca "hangi kemige bagli" sorusunu cevaplar.
        ///
        /// Ince ayar icin GrenadeConfig.pinFingerTipOffset var: parmak ucu kemiginin
        /// uzayinda kucuk bir kaydirma, Play modunda canli surukleyerek denenebilir.
        ///
        /// Uzak oyuncuda FP eli yoktur, kemik bulunamaz ve false doner — pim eski
        /// haliyle el cipasina bagli kalir.
        /// </summary>
        static bool HookOnIndexTip(Transform holder, Transform hand, bool leftHand,
                                   GrenadeConfig cfg)
        {
            Transform tip = IndexTip(hand, leftHand);
            if (tip == null) return false;

            holder.SetParent(tip, true);   // DUNYA durusu korunur: atolye ayari aynen kalir
            if (cfg != null && cfg.pinFingerTipOffset != Vector3.zero)
                holder.localPosition += cfg.pinFingerTipOffset;   // parmak ucu uzayinda ince ayar
            return true;
        }

        /// <summary>Isaret parmagi ucu. Meta elinde gercek bir ucu isaretcisi var; yoksa
        /// son boguma dusulur.</summary>
        static Transform IndexTip(Transform hand, bool leftHand)
        {
            string s = leftHand ? "l" : "r";
            var t = FirstPersonHandView.FindBone(hand, s + "_index_finger_tip_marker");
            if (t == null) t = FirstPersonHandView.FindBone(hand, "b_" + s + "_index_null");
            if (t == null) t = FirstPersonHandView.FindBone(hand, "b_" + s + "_index3");
            return t;
        }
    }
}
