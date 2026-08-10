using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// "Bu collider KATI DUNYA GEOMETRISI mi (duvar/zemin/siper), yoksa hareketli bir sey mi?"
    /// sorusunun TEK kaynagi. Hem kafa kararmasi (<see cref="UI.HeadWallFade"/>) hem namlu
    /// kilidi (<see cref="Weapons.MuzzleWallBlock"/>) buradan sorar; iki yerde iki farkli kural
    /// olsaydi "kafam duvarda kararmiyor ama silahim kilitleniyor" gibi celiskiler cikardi.
    ///
    /// NEDEN KATMAN MASKESI TEK BASINA YETMIYOR: bu projede ozel katman YOK — duvar da, oyuncu
    /// da, silah da Default'ta. Bu yuzden filtre uc kuralla calisir ve maske bunun USTUNE gelir:
    ///   1) trigger collider'lar elenir  -> oyuncu hitbox'lari (PlayerHitbox) trigger'dir
    ///   2) Rigidbody'si olan elenir     -> silah/bomba/atilabilir prop tasinabilir, duvar degil
    ///   3) verilen kok altindakiler elenir -> silahin kendi collider'i kendini kilitlemesin
    /// Ileride bir "Duvar" katmani acilirsa hicbir kod degismez: maskeyi daraltmak yeter,
    /// sorgu daha da ucuzlar.
    /// </summary>
    public static class WorldSolids
    {
        /// <summary>Bu collider duvar sayilir mi? <paramref name="ignoreRoot"/> altindaki her sey
        /// (silahin kendi govdesi gibi) yok sayilir; null verilebilir.</summary>
        public static bool IsSolid(Collider col, Transform ignoreRoot = null)
        {
            if (col == null) return false;
            if (col.isTrigger) return false;
            if (col.attachedRigidbody != null) return false;
            if (ignoreRoot != null && col.transform.IsChildOf(ignoreRoot)) return false;
            return true;
        }

        /// <summary>Temasin YONU: noktadan collider'a dogru birim vektor. Yalnizca "bu temas
        /// zemin/tavan mi, duvar mi" ayrimi icindir — MESAFE olcusu olarak KULLANILMAZ.
        /// Yon cikarilamazsa (nokta collider'in tam uzerinde) false doner.
        ///
        /// KAPAN — NEDEN MESAFE ICIN KULLANILMAZ: Collider.ClosestPoint, CONVEX OLMAYAN
        /// MeshCollider'da CALISMAZ; oda taramasinin zemini (RoomScanSetup) ve dekor mesh'leri
        /// tam da oyle. Onlarda SINIR KUTUSUNA duseriz ve kutu gercek geometriden cok daha
        /// buyuk olabilir — orn. odanin zemin mesh'inin kutusu tum odayi kapsar. O kutuya
        /// "mesafe/iceride" diye guvenen bir kod, oyuncu odanin ORTASINDA dururken "duvarin
        /// icindesin" derdi. Mesafeyi cagiranlar kure sorgusuyla olcer (kure sorgusu ucgen
        /// mesh'lerde de dogru calisir), bu metot sadece yonu soyler.</summary>
        public static bool TryContactDirection(Collider col, Vector3 point, out Vector3 dir)
        {
            Vector3 closest = col is MeshCollider mc && !mc.convex
                ? col.bounds.ClosestPoint(point)
                : col.ClosestPoint(point);

            Vector3 delta = closest - point;
            float sqr = delta.sqrMagnitude;
            if (sqr < 1e-8f) { dir = Vector3.zero; return false; }
            dir = delta / Mathf.Sqrt(sqr);
            return true;
        }
    }
}
