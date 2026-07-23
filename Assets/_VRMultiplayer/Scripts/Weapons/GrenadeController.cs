using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Audio;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// El bombasi davranisi (GrenadeBinder runtime'da takar — sahne/prefab duzenlemesi yok).
    ///
    /// Akis: kavrayinca pim cekilmis sayilir (ilerde sol kumandayla pim animasyonu icin
    /// genisleme noktasi: Arm()) → firlatinca GrabbableObject fizigi zaten aciyor, bu bilesen
    /// hizi olcekler + takla ekler → yere ILK temasta OWNER sunucuya named message yollar
    /// (fizik yalniz owner'da kosar; capture/sync araclariyla ayni yol cunku runtime'da
    /// takilan bilesende NGO RPC'si olamaz) → sunucu funye suresini sayip patlatir.
    ///
    /// Patlama sunucu-otoriter: Frag mesafeyle azalan hasar + siper (LOS) kontrolu; dost
    /// atesi NetworkWeapon'daki kuralla kesilir, KENDINE hasar SERBEST (bilincli karar —
    /// bombayi ayagina dusuren bedelini oder). Flash/Smoke hasarsiz; gorsel etkileri sonraki
    /// adimlarda baglanacak (simdilik log + ses cagrisi; klipler eklenince kendiliginden calar).
    ///
    /// Dikkat: pim cekilmis bombayi yavasca yere birakmak da patlatir — pim geri takilmiyor.
    /// </summary>
    public class GrenadeController : MonoBehaviour
    {
        const string MsgImpact = "GrenadeImpact";   // owner → sunucu: yere temas etti
        const string MsgExplode = "GrenadeExplode"; // sunucu → herkes: patlama fx

        GrenadeConfig _cfg;
        GrabbableObject _grab;
        NetworkObject _netObj;
        Rigidbody _rb;

        bool _wasKinematic = true;
        bool _armed;       // pim cekildi (ilk kavrama)
        bool _live;        // firlatildi/birakildi, temas bekliyor (owner tarafi)
        bool _impactSent;  // ucus basina tek haber
        bool _fuseRunning; // sunucu funyesi sayiyor
        bool _exploded;    // gizleme/fx bir kez
        ulong _lastHolder = GrabbableObject.NoHolder; // sunucuda: patlamanin saldirgani

        public void Bind(GrenadeConfig cfg)
        {
            _cfg = cfg;
            _grab = GetComponent<GrabbableObject>();
            _netObj = GetComponent<NetworkObject>();
            _rb = GetComponent<Rigidbody>();

            if (_rb != null)
            {
                _rb.mass = Mathf.Max(0.05f, cfg.mass);
                // Sekme icin runtime fizik materyali — asset gerektirmez, config'ten gelir.
                // Zemin materyalsiz (bounciness 0) oldugundan Maximum birlestirme sart, yoksa
                // ortalama alinir ve sekme olur.
                var pm = new PhysicsMaterial("BombaSekme")
                {
                    bounciness = cfg.bounciness,
                    dynamicFriction = cfg.friction,
                    staticFriction = cfg.friction,
                    bounceCombine = PhysicsMaterialCombine.Maximum,
                };
                foreach (var col in GetComponentsInChildren<Collider>(true))
                    col.sharedMaterial = pm;
            }

            if (_grab != null)
            {
                _grab.StateDirty -= OnGrabState; // Bind tekrar cagrilirsa cift abonelik olmasin
                _grab.StateDirty += OnGrabState;

                // Kirmizi nisan yayi (thumbstick) — yalniz yerel sahibin ekraninda cizilir.
                var arc = GetComponent<GrenadeAimArc>();
                if (arc == null) arc = gameObject.AddComponent<GrenadeAimArc>();
                arc.Init(cfg, _grab);
            }
        }

        void OnDestroy()
        {
            if (_grab != null) _grab.StateDirty -= OnGrabState;
        }

        void OnGrabState()
        {
            if (_grab == null) return;
            ulong h = _grab.HolderClientId;
            if (h == GrabbableObject.NoHolder) return;

            _lastHolder = h; // her makinede izlenir; sunucu patlamada saldirgan olarak kullanir
            if (!_armed)
            {
                _armed = true; // pim cekildi — simdilik kavrama ile; ilerde Arm() disaridan cagrilir
                if (_cfg != null)
                    WeaponAudioPlayer.PlayAt(_cfg.pinClip, transform.position, 0.7f, 0.98f, 1.02f, 20f);
            }
        }

        void FixedUpdate()
        {
            EnsureHandlers();
            if (_rb == null || _grab == null || _exploded) return;

            // Firlatma anini kinematik → dinamik gecisinden yakala (ApplyThrow'un actigi tek
            // pencere). GrabbableObject'e dokunmadan hiz olcekleme + takla burada eklenir.
            bool kin = _rb.isKinematic;
            if (_wasKinematic && !kin && _grab.IsOwner && !_grab.IsHeld)
                OnThrownOwner();
            _wasKinematic = kin;
        }

        void OnThrownOwner()
        {
            if (_cfg == null) return;

            if (_rb.linearVelocity.sqrMagnitude > 0.01f)
            {
                _rb.linearVelocity *= _cfg.throwVelocityScale;
                _rb.angularVelocity = Random.onUnitSphere * (_cfg.throwSpinDegPerSec * Mathf.Deg2Rad);
            }
            // Pim cekiliyse artik canli: yavas birakma da (hiz ~0, duz duser) yere degince patlar.
            if (_armed) { _live = true; _impactSent = false; }
        }

        void OnCollisionEnter(Collision c)
        {
            // Fizik yalniz owner'da kosar (digerlerinde kinematik — zaten event gelmez).
            if (!_live || _impactSent || _exploded) return;
            if (_grab == null || !_grab.IsOwner) return;

            _impactSent = true;
            Vector3 p = c.GetContact(0).point;
            if (_cfg != null)
                WeaponAudioPlayer.PlayAt(_cfg.bounceClip, p, 0.5f, 0.95f, 1.05f, 30f);
            SendImpactToServer(p);
        }

        void SendImpactToServer(Vector3 pos)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || _netObj == null) return;

            if (nm.IsServer) { ServerOnImpact(_netObj.NetworkObjectId, pos, nm.LocalClientId); return; }

            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(_netObj.NetworkObjectId);
            w.WriteValueSafe(pos);
            nm.CustomMessagingManager.SendNamedMessage(MsgImpact, NetworkManager.ServerClientId, w,
                NetworkDelivery.ReliableSequenced);
        }

        // ------------------------------ SUNUCU ------------------------------

        static void ServerOnImpact(ulong objId, Vector3 pos, ulong sender)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(objId, out var no)) return;

            var gc = no.GetComponent<GrenadeController>();
            if (gc == null || gc._cfg == null || gc._exploded || gc._fuseRunning) return;

            // Otorite kontrolleri: haberi yalniz objenin sahibi verebilir ve bildirilen nokta
            // replike pozisyondan kopuk olamaz (8 m tolerans — hizli ucusta replikasyon geri
            // kalabilir).
            if (sender != no.OwnerClientId) return;
            if ((pos - no.transform.position).sqrMagnitude > 64f) return;

            gc.StartCoroutine(gc.ServerFuse());
        }

        IEnumerator ServerFuse()
        {
            _fuseRunning = true;
            yield return new WaitForSeconds(_cfg.fuseSeconds);
            _fuseRunning = false;
            if (!_exploded) ServerExplode();
        }

        void ServerExplode()
        {
            // Patlama noktasi funye SONUNDAKI konum — bomba temastan sonra yuvarlanmis olabilir.
            Vector3 pos = transform.position;
            ulong attacker = _lastHolder;

            // Kendi collider'larimiz overlap/LOS sorgularini kirletmesin.
            foreach (var col in GetComponentsInChildren<Collider>(true)) col.enabled = false;

            if (_cfg.type == GrenadeType.Frag)
                ServerFragDamage(pos, attacker);

            // Herkese fx haberi. Handler sunucuda calismaz (asagida) — host cift oynatmasin
            // diye sunucu kendi fx'ini dogrudan cagirir.
            var nm = NetworkManager.Singleton;
            using (var w = new FastBufferWriter(64, Allocator.Temp))
            {
                w.WriteValueSafe(_netObj.NetworkObjectId);
                w.WriteValueSafe((byte)_cfg.type);
                w.WriteValueSafe(pos);
                nm.CustomMessagingManager.SendNamedMessageToAll(MsgExplode, w,
                    NetworkDelivery.ReliableSequenced);
            }
            LocalExplode(this, _cfg.type, pos);

            // Dinamik spawn edilen kopya (spawner sistemleri) mesaj dagitildiktan sonra agdan
            // kaldirilir; sahne objesi gizli kalir (spawner yenisini getirir).
            if (_netObj != null && !_netObj.IsSceneObject.GetValueOrDefault())
                StartCoroutine(ServerDespawnAfter(1.5f));
        }

        IEnumerator ServerDespawnAfter(float sec)
        {
            yield return new WaitForSeconds(sec);
            if (_netObj != null && _netObj.IsSpawned) _netObj.Despawn(true);
        }

        void ServerFragDamage(Vector3 pos, ulong attacker)
        {
            byte attackerTeam = TeamOf(attacker);

            // Yaricap icindeki oyunculari topla; oyuncu basina EN YAKIN collider noktasi uzerinden
            // mesafe hesapla (kol disarida kalan siperdeki adam govdesinden degil kolundan olculur).
            var nearest = new Dictionary<PlayerHealth, Vector3>();
            foreach (var col in Physics.OverlapSphere(pos, _cfg.damageRadius, ~0, QueryTriggerInteraction.Collide))
            {
                var health = col.GetComponentInParent<PlayerHealth>();
                if (health == null || health.IsDead) continue;
                Vector3 p = col.ClosestPoint(pos);
                if (!nearest.TryGetValue(health, out var prev) ||
                    (p - pos).sqrMagnitude < (prev - pos).sqrMagnitude)
                    nearest[health] = p;
            }

            foreach (var kv in nearest)
            {
                var health = kv.Key;
                Vector3 target = kv.Value;
                float dist = Vector3.Distance(pos, target);
                if (dist >= _cfg.damageRadius) continue;

                bool isSelf = health.OwnerClientId == attacker;
                byte t = health.TeamValue;
                // Dost atesi kapali (NetworkWeapon kurali) — ama KENDINE hasar serbest.
                if (!isSelf && t != 0 && t == attackerTeam) continue;

                // Siper: patlama ile hedef arasinda baska bir sey varsa hasar yok. Aradaki
                // BASKA oyuncu da siperdir (gercekci). Trigger'lar sorguya girmez.
                if (Physics.Linecast(pos + Vector3.up * 0.05f, target, out var lh, ~0,
                        QueryTriggerInteraction.Ignore) &&
                    lh.collider.GetComponentInParent<PlayerHealth>() != health)
                    continue;

                int dmg = Mathf.Max(1, Mathf.RoundToInt(_cfg.centerDamage * (1f - dist / _cfg.damageRadius)));
                health.ServerApplyDamage(dmg, attacker);
                Debug.Log($"[Bomba] PATLAMA! atan {attacker} (takim {attackerTeam}) -> hedef " +
                          $"{health.OwnerClientId} (takim {t}), mesafe {dist:F1}m, {dmg} hasar" +
                          (isSelf ? " (KENDINE)" : "") +
                          $". Kalan: {Mathf.Max(0, health.Health.Value - dmg)}");
            }
        }

        static byte TeamOf(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.ConnectedClients.TryGetValue(clientId, out var cc) &&
                cc.PlayerObject != null)
            {
                var h = cc.PlayerObject.GetComponent<PlayerHealth>();
                if (h != null) return h.TeamValue;
            }
            return 0;
        }

        // ------------------------------ HERKESTE ------------------------------

        static void LocalExplode(GrenadeController gc, GrenadeType type, Vector3 pos)
        {
            // gc null olabilir (obje coktan despawn olduysa) — o nadir yarista fx/ses atlanir,
            // hasar zaten sunucuda coktan islenmistir.
            if (gc != null)
            {
                if (gc._exploded) return;
                gc._exploded = true;
                gc._live = false;
                gc.HideSelf();
                if (gc._cfg != null)
                {
                    WeaponAudioPlayer.PlayAt(gc._cfg.explodeClip, pos, gc._cfg.explodeVolume,
                        0.95f, 1.05f, gc._cfg.explodeMaxDistance, priority: true);
                    // War FX prefabi kendi rotasyonuyla dogar (duman gibi efektler +90X ister)
                    // ve CFX_AutoDestructShuriken ile kendini temizler.
                    if (gc._cfg.explodeFx != null)
                    {
                        var fx = Instantiate(gc._cfg.explodeFx, pos, gc._cfg.explodeFx.transform.rotation);
                        if (gc._cfg.fxScale != 1f) fx.transform.localScale *= gc._cfg.fxScale;
                    }
                }
            }

            // Korluk YEREL kamerayla hesaplanir (bakis acisi + mesafe + siper) — gc despawn
            // yarisina yenildiyse varsayilan yaricapla yine uygulanir.
            if (type == GrenadeType.Flash)
                UI.FlashBlindEffect.TriggerAt(pos,
                    gc != null && gc._cfg != null ? gc._cfg.flashRadius : 15f);

            Debug.Log($"[Bomba] {type} patladi @ {pos}");
        }

        void HideSelf()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var col in GetComponentsInChildren<Collider>(true)) col.enabled = false;
            if (_rb != null) _rb.isKinematic = true;
        }

        // ------------------------------ MESAJ KAYDI ------------------------------

        static bool _handlers;

        static void EnsureHandlers()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) { _handlers = false; return; } // oturum bitti → tazele
            if (_handlers || nm.CustomMessagingManager == null) return;

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgImpact, (sender, reader) =>
            {
                reader.ReadValueSafe(out ulong id);
                reader.ReadValueSafe(out Vector3 pos);
                ServerOnImpact(id, pos, sender);
            });

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgExplode, (sender, reader) =>
            {
                var local = NetworkManager.Singleton;
                if (local == null) return;
                if (sender != NetworkManager.ServerClientId) return; // yalniz sunucudan
                if (local.IsServer) return; // sunucu/host kendi fx'ini dogrudan cagirdi
                reader.ReadValueSafe(out ulong id);
                reader.ReadValueSafe(out byte t);
                reader.ReadValueSafe(out Vector3 pos);
                GrenadeController gc = null;
                if (local.SpawnManager != null &&
                    local.SpawnManager.SpawnedObjects.TryGetValue(id, out var no))
                    gc = no.GetComponent<GrenadeController>();
                LocalExplode(gc, (GrenadeType)t, pos);
            });

            _handlers = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _handlers = false; }
    }
}
