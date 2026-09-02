using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// YERE DUSEN ESYALARIN TEMIZLIGI — sunucuda calisir.
    ///
    /// NEDEN GEREKLI: kemer artik OTOMATIK degil (bkz. <see cref="WeaponInventory"/> sinif
    /// basi). Eskiden birakilan her silah kosulsuz despawn ediliyordu ("cantaya girdi"), yani
    /// sahnede hicbir sey birikmiyordu. Artik kemere KOYMADIGIN sey yere dusuyor — ve
    /// toplanmazsa oyun boyunca orada kaliyor. Uc oyuncunun bir mac boyunca dusurdugu her
    /// silah, her bombasi sahnede canli bir NetworkObject + Rigidbody olarak durur; bu tam da
    /// kullanicinin onceden isaret ettigi FPS sorunudur.
    ///
    /// SAAT KURULMASI ICIN UC KOSUL BIRDEN:
    ///   1) SUNUCU — despawn otoritesi orada.
    ///   2) Obje CALISMA ANINDA dogmus olmali (sahne objesi degil). Sahnedeki taslar/proplar
    ///      kendi <c>_homePos</c> geri donus mantigina sahip; onlari yok etmek kalici kayip olur.
    ///   3) Obje EN AZ BIR KEZ TUTULMUS ve sonra birakilmis olmali. Bu kosul rafta bekleyen
    ///      silahlari korur: raf stogu da calisma aninda dogar (WeaponRackRespawner) ama hic
    ///      tutulmadigi icin sayaci hic kurulmaz. Yalnizca oyuncunun eline alip attigi sey
    ///      temizlenir.
    ///
    /// Tekrar kavranirsa sayac IPTAL olur; yeniden birakilinca SIFIRDAN baslar.
    /// </summary>
    [RequireComponent(typeof(GrabbableObject))]
    public class DroppedItemCleanup : MonoBehaviour
    {
        /// <summary>Yerde bu kadar saniye kalan esya silinir.</summary>
        public const float LifetimeSeconds = 30f;

        GrabbableObject _grab;
        bool _wasHeld;
        double _despawnAt = -1d;   // mutlak SUNUCU zamani; -1 = sayac kurulu degil

        void Awake() => _grab = GetComponent<GrabbableObject>();

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _grab == null || !_grab.IsSpawned) return;

            bool held = _grab.IsHeld;

            if (held)
            {
                _wasHeld = true;
                _despawnAt = -1d;   // elde -> sayac yok
                return;
            }

            // Hic tutulmadiysa dokunma: raf stogu ve sahneye elle serpilmis esyalar boyle
            // korunur (bkz. sinif basi, 3. kosul).
            if (!_wasHeld) return;

            if (_despawnAt < 0d)
            {
                _despawnAt = nm.ServerTime.Time + LifetimeSeconds;
                return;
            }

            if (nm.ServerTime.Time < _despawnAt) return;

            var no = _grab.NetworkObject;
            if (no == null || !no.IsSpawned) { enabled = false; return; }
            Debug.Log($"[Temizlik] Yerde {LifetimeSeconds:0} sn bekledi, siliniyor: {name}");
            no.Despawn(true);
        }

        /// <summary>Sunucuda, CALISMA ANINDA dogmus her kavranabilire takar. Sahne objelerine
        /// dokunmaz (bkz. sinif basi, 2. kosul).</summary>
        public static void AttachIfNeeded(GrabbableObject g)
        {
            if (g == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var no = g.NetworkObject;
            if (no == null || no.IsSceneObject == true) return;

            if (g.GetComponent<DroppedItemCleanup>() == null)
                g.gameObject.AddComponent<DroppedItemCleanup>();
        }
    }
}
