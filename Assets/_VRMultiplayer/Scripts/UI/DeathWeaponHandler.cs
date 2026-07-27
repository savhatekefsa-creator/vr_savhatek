using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// OLUNCE ELDEKI SILAH ANINDA KAYBOLUR. Dirilince GERI GELMEZ (ekip karari 24.07.2026):
    /// oyuncu carktan kendi secer ya da raftan yeni bir silah alir. Olumun bir bedeli olsun.
    /// Cantadaki (envanterdeki) silahlar bundan HIC etkilenmez — onlar durur.
    ///
    /// SORUN: olum aninda silahin ne olacagi hicbir yerde tanimli degildi. PlayerHealth yalnizca
    /// Dead=true yaziyor; onu dinleyen iki yer var — PlayerHUD (ekrani karartir) ve PlayerHitbox
    /// (collider'lari kapatir). Silah sistemi olumu hic duymuyor, yani elindeki silah "tanimsiz
    /// durumda" kaliyor. El ile silah arasindaki bag o sirada koparsa (yerel tutus referansi
    /// dususe HandGrabber'in saniyede bir kosan Reconcile'i sunucudan ReleaseServerRpc ile
    /// birakma ister), silah FIRLATILMADAN birakilmis olur — ve birakilan ama firlatilmayan
    /// govdeyi GrabbableObject.FixedUpdate her adimda kinematik yapar. Sonuc: silah oldugun anda
    /// elinin bulundugu noktada, HAVADA ASILI kalir.
    ///
    /// COZUM: olum anini ACIKCA tanimlamak. Elenince eldeki silah(lar) hemen cantaya doner
    /// (despawn = silah secicinin "cantaya girdi" yolunun ta kendisi), boylece ortada asili
    /// kalabilecek bir silah kalmaz. Yeniden dogunca olmeden onceki silah AYNI MERMIYLE geri
    /// gelir — oyuncu her olumden sonra carki acmak zorunda kalmaz.
    ///
    /// Kendi kendine dogar (Bootstrap); sahne/prefab kurulumu yok. YALNIZCA yerel oyuncu icin
    /// calisir ve baska hicbir dosyayi degistirmez: sadece public API kullanir
    /// (HandGrabber.RequestWeaponSwap + WeaponInventory) — silah secici carkiyla ayni yol.
    /// </summary>
    public class DeathWeaponHandler : MonoBehaviour
    {
        [Tooltip("Elenince eldeki silah(lar) cantaya donsun (despawn). Kapatirsan silah elde kalir " +
                 "ve havada asili kalma hatasi geri gelebilir.")]
        public bool bagWeaponsOnDeath = true;

        [Tooltip("VARSAYILAN KAPALI (ekip karari): dirilince silah GERI GELMEZ, oyuncu carktan " +
                 "kendi secer ya da raftan yeni silah alir — olumun bir bedeli olsun. Acarsan " +
                 "olmeden once tuttugun silah ayni mermiyle otomatik geri gelir (her olumden " +
                 "sonra carki acmak zorunda kalmazsin).")]
        public bool restoreOnRespawn;

        PlayerHealth _health;
        HandGrabber _grabber;
        bool _wasDead;
        readonly List<string> _lastLoadout = new List<string>(); // olum aninda eldeki turler
        Coroutine _restore;
        float _nextResolve;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~DeathWeaponHandler");
            DontDestroyOnLoad(go);
            go.AddComponent<DeathWeaponHandler>();
        }

        void Update()
        {
            if (_health == null || _grabber == null) Resolve();
            if (_health == null) return;

            // Olum/dirilis KENARI: NetworkVariable okumasi ucuz, her kare bakmak sorun degil.
            bool dead = _health.IsDead;
            if (dead == _wasDead) return;
            _wasDead = dead;
            if (dead) OnDied(); else OnRevived();
        }

        // Yerel oyuncunun can bileseni + eli. Ikisi de NetworkPlayer kokunde ama ayri ayri
        // araniyor: PC test kosumunda HandGrabber KAPALI tasinabiliyor, biri bulunmadi diye
        // digerini kaybetmeyelim. Bulununca tarama durur.
        void Resolve()
        {
            if (Time.time < _nextResolve) return;
            _nextResolve = Time.time + 0.5f;

            var nm = NetworkManager.Singleton;
            if (nm == null || !(nm.IsServer || nm.IsConnectedClient)) return;

            if (_health == null)
            {
                foreach (var h in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
                {
                    if (!h.IsSpawned || !h.IsOwner) continue;
                    _health = h;
                    _wasDead = h.IsDead; // ilk cozumde sahte kenar tetiklenmesin
                    break;
                }
            }

            if (_grabber == null)
            {
                foreach (var hg in FindObjectsByType<HandGrabber>(FindObjectsSortMode.None))
                    if (hg.IsSpawned && hg.IsOwner) { _grabber = hg; break; }
            }
        }

        void OnDied()
        {
            // Yarim kalmis bir geri-verme varsa once o dursun (liste birazdan yenilenecek).
            if (_restore != null) { StopCoroutine(_restore); _restore = null; }
            _lastLoadout.Clear();

            if (!bagWeaponsOnDeath || _grabber == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            // Elimdekiler ONCE kopyalanir: RequestWeaponSwap despawn tetikler ve
            // GrabbableObject.Active listesi gezerken degisir.
            var mine = new List<GrabbableObject>();
            var actives = GrabbableObject.Active;
            for (int i = 0; i < actives.Count; i++)
                if (actives[i].HolderClientId == nm.LocalClientId) mine.Add(actives[i]);

            var inv = WeaponInventory.Instance;
            foreach (var g in mine)
            {
                // PIMI CEKILMIS BOMBA cantaya GIRMEZ (HandGrabber ile ayni kural): olurken elde
                // kalir ve fitili dolunca patlar. Olmek canli bombayi sondurmenin yolu degil.
                var live = g.GetComponent<GrenadeController>();
                if (live != null && live.Armed) continue;

                string key = WeaponInventory.TypeKey(g);

                // Mermiyi TAM SU AN not al: envanterin 0.3 sn'lik taramasi bayat olabilir, son
                // yarim saniyede atilan mermiler olunce bedavaya geri gelmesin.
                if (inv != null)
                {
                    var e = inv.Find(key);
                    var nw = g.GetComponent<NetworkWeapon>();
                    if (e != null && nw != null && nw.UsesAmmo) { e.Ammo = nw.Ammo; e.Spares = nw.SpareMagazines; }
                }

                _lastLoadout.Add(key);
                _grabber.RequestWeaponSwap(g, null); // prefab yok -> yalniz despawn ("cantaya girdi")
            }

            if (_lastLoadout.Count > 0)
                Debug.Log($"[DeathWeapon] Elendin — cantaya donen: {string.Join(", ", _lastLoadout)}");
        }

        void OnRevived()
        {
            if (!restoreOnRespawn || _grabber == null || _lastLoadout.Count == 0) return;
            if (_restore != null) StopCoroutine(_restore);
            _restore = StartCoroutine(RestoreRoutine());
        }

        IEnumerator RestoreRoutine()
        {
            var inv = WeaponInventory.Instance;
            for (int i = 0; i < _lastLoadout.Count && inv != null; i++)
            {
                string key = _lastLoadout[i];
                if (HeldCount() >= 2) break;      // iki el de doldu
                if (HoldingType(key)) continue;   // bu tur zaten elde (raftan almis olabilir)

                var e = inv.Find(key);
                if (e == null || e.Prefab == null) continue;

                int before = HeldCount();
                _grabber.RequestWeaponSwap(null, e.Prefab, e.Ammo, e.Spares);

                // SIRAYLA: sunucu spawn'layip ele verene kadar (~1 RTT) bekle. Iki istek ayni anda
                // gidersse ikisi de ayni BOS eli secer, ikincisi EquipSpawnedRpc'de iptal edilip
                // yok edilirdi — oyuncu tabancasini geri alamazdi.
                float until = Time.time + 1.5f;
                while (Time.time < until && HeldCount() <= before) yield return null;
            }
            _restore = null;
        }

        // "Sunucuya gore su an kac silah tutuyorum" — silah secicinin MyWeapon'i ile ayni dogru:
        // HandGrabber'in ic durumuna bakmiyoruz (PC test kosumu silahi o bilesen kapaliyken tasir).
        static int HeldCount()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return 0;
            int n = 0;
            var actives = GrabbableObject.Active;
            for (int i = 0; i < actives.Count; i++)
                if (actives[i].HolderClientId == nm.LocalClientId) n++;
            return n;
        }

        static bool HoldingType(string key)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return false;
            var actives = GrabbableObject.Active;
            for (int i = 0; i < actives.Count; i++)
                if (actives[i].HolderClientId == nm.LocalClientId &&
                    WeaponInventory.TypeKey(actives[i]) == key) return true;
            return false;
        }
    }
}
