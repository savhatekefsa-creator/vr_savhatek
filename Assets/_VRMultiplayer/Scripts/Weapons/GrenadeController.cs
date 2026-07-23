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
    /// Akis: bombayi bir ele al → BOS oteki eli bombaya yaklastirip grip'e bas, pim cekilir
    /// (<see cref="PullPin"/>; pim geometrisi o ele takilir ve o el grip'i birakana kadar orada
    /// durur) → pimi cekilmis bombada grip birakmak ARTIK cantaya koymaz, el hiziyla firlatir
    /// (bkz. HandGrabber.Release) → firlatinca GrabbableObject fizigi zaten aciyor, bu bilesen
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
    /// Pimi CEKILMEMIS bomba zararsizdir: cantaya girer, firlatilsa bile patlamaz.
    /// </summary>
    public class GrenadeController : MonoBehaviour
    {
        const string MsgImpact = "GrenadeImpact";   // owner → sunucu: yere temas etti
        const string MsgExplode = "GrenadeExplode"; // sunucu → herkes: patlama fx
        const string MsgPin = "GrenadePin";         // owner → sunucu: pim cekildi/birakildi
        const string MsgPinAll = "GrenadePinAll";   // sunucu → herkes: pim durumu

        /// <summary>Pim mesajinda "el yok" = pim birakildi (yok olur).</summary>
        const byte PinDropped = GrabbableObject.NoHand;

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
        Transform _pinHolder; // ayrilmis pim (cekenin elinde); null = pim hala bombada

        /// <summary>Pim cekildi mi — HandGrabber bunu okuyup birakisin cantaya mi yoksa
        /// firlatmaya mi gidecegine karar verir.</summary>
        public bool Armed => _armed;

        /// <summary>Bos elin pimi cekebilmek icin bombaya yaklasmasi gereken mesafe (m).</summary>
        public float PinPullReach => _cfg != null ? _cfg.pinPullReach : 0.45f;

        public void Bind(GrenadeConfig cfg)
        {
            _cfg = cfg;
            _grab = GetComponent<GrabbableObject>();
            _netObj = GetComponent<NetworkObject>();
            _rb = GetComponent<Rigidbody>();

            if (_rb != null)
            {
                _rb.mass = Mathf.Max(0.05f, cfg.mass);

                // Sekil once sadelestirilir, fizik materyali SONRA dagitilir ki yeni kure de
                // sekme ayarlarini alsin.
                if (cfg.simpleCollider) BuildSimpleCollider(cfg);

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

        /// <summary>Modelin mesh hull'unu kapatip yerine govdeye oturan basit bir sekil koyar:
        /// yuvarlak govdeye KURE, silindirik govdeye (flash/duman kutusu) KAPSUL. Bombanin
        /// emniyet kolu ve pimi hull'a ince cikintilar ekliyor; boyle bir sekil yere carpinca
        /// bir kosesi uzerinde donup ongorulemez yonlere sekiyor.
        ///
        /// Olculer dunya sinirlarindan degil MESH'IN KENDI sinirlarindan alinir: dunya bounds'u
        /// eksene hizali oldugu icin bomba donukken hangi eksenin "uzun" oldugunu yanlis
        /// soylerdi. Govde = en buyuk hacimli mesh parcasi.</summary>
        void BuildSimpleCollider(GrenadeConfig cfg)
        {
            MeshFilter body = null;
            float bestVolume = 0f;
            foreach (var mf in GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                float vol = Mathf.Abs(s.x * s.y * s.z);
                if (vol > bestVolume) { bestVolume = vol; body = mf; }
            }
            if (body == null) return;

            Bounds mb = body.sharedMesh.bounds;

            // Govde olculeri BU objenin lokal uzayinda (collider degerleri burada yazilir).
            Vector3 lossy = transform.lossyScale;
            float uniform = Mathf.Max(0.0001f,
                Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z))));
            Vector3 worldSize = Vector3.Scale(mb.size, body.transform.lossyScale);
            Vector3 size = worldSize / uniform;

            Vector3 center = cfg.colliderCenter;
            if (center == Vector3.zero)
                center = transform.InverseTransformPoint(body.transform.TransformPoint(mb.center));

            // En uzun eksen belirgin sekilde one cikiyorsa cisim silindiriktir.
            int longAxis = 0;
            if (size.y > size[longAxis]) longAxis = 1;
            if (size.z > size[longAxis]) longAxis = 2;
            float longest = size[longAxis];
            float widest = 0f;
            for (int i = 0; i < 3; i++)
                if (i != longAxis) widest = Mathf.Max(widest, size[i]);

            float radius = cfg.colliderRadius > 0f ? cfg.colliderRadius : widest * 0.5f;
            if (radius <= 0f) return;

            foreach (var old in GetComponentsInChildren<Collider>(true))
                old.enabled = false;

            bool elongated = longest > widest * 1.35f;
            if (elongated)
            {
                var capsule = GetComponent<CapsuleCollider>();
                if (capsule == null) capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.enabled = true;
                capsule.isTrigger = false;
                capsule.direction = longAxis;
                capsule.radius = radius;
                capsule.height = longest;
                capsule.center = center;
            }
            else
            {
                var sphere = GetComponent<SphereCollider>();
                if (sphere == null) sphere = gameObject.AddComponent<SphereCollider>();
                sphere.enabled = true;
                sphere.isTrigger = false;
                sphere.radius = radius;
                sphere.center = center;
            }
        }

        void LateUpdate()
        {
            // Pim eldeyken durusu HER KARE config'ten tazelenir: Play modunda GrenadeConfig
            // asset'indeki pinHandLocal* degerlerini surukleyerek pimi canli ayarlayabilirsin.
            // Asset bir ScriptableObject oldugu icin degerler Play'den cikinca da korunur.
            if (_pinHolder == null || _cfg == null) return;
            _pinHolder.localPosition = _cfg.pinHandLocalPosition;
            _pinHolder.localRotation = Quaternion.Euler(_cfg.pinHandLocalEuler);
        }

        void OnDestroy()
        {
            if (_grab != null) _grab.StateDirty -= OnGrabState;
            // Pim artik bombanin cocugu degil (elin altinda): bomba yok olurken onunla birlikte
            // silinmez, elde oksuz kalir. Burada temizlenmezse her patlama bir pim biriktirir.
            if (_pinHolder != null) Destroy(_pinHolder.gameObject);
        }

        void OnGrabState()
        {
            if (_grab == null) return;
            ulong h = _grab.HolderClientId;
            if (h == GrabbableObject.NoHolder) return;

            // Sadece saldirgan takibi: kavramak bombayi ARTIK kurmaz, pim ayri bir hareket
            // (bos elle grip → PullPin). Boylece bomba envanterde guvenle tasinabilir.
            _lastHolder = h; // her makinede izlenir; sunucu patlamada saldirgan olarak kullanir
        }

        // ------------------------------ PIM ------------------------------

        /// <summary>Owner tarafi: pimi ceker (HandGrabber, bos el bombaya yaklasip grip'e
        /// basinca cagirir). Yerel etki hemen uygulanir, digerlerine sunucu uzerinden yayilir.</summary>
        public void PullPin(byte hand)
        {
            if (_armed || _exploded || _grab == null || !_grab.IsOwner) return;
            ApplyPin(hand);
            SendPinToServer(hand);
        }

        /// <summary>Owner tarafi: pimi tutan el grip'i birakti — pim yok olur. Bomba KURULU
        /// kalir (pim geri takilmaz), yalnizca gorsel gider.</summary>
        public void DropPin()
        {
            // Kosul _pinHolder degil _armed: pim bu makinede takilamamis olsa bile (el bulunamadi)
            // DIGERLERINDE takili olabilir — haber gitmezse orada sonsuza kadar elde kalirdi.
            if (_grab == null || !_grab.IsOwner || !_armed) return;
            ApplyPin(PinDropped);
            SendPinToServer(PinDropped);
        }

        /// <summary>Her makinede ayni is: pimi ele tak (hand 0/1) ya da yok et
        /// (<see cref="PinDropped"/>). Tekrar cagrilmasi zararsizdir.</summary>
        void ApplyPin(byte hand)
        {
            if (hand == PinDropped)
            {
                if (_pinHolder != null) Destroy(_pinHolder.gameObject);
                _pinHolder = null;
                return;
            }

            if (_armed) return; // pim zaten cekilmis (owner yerel uyguladi, mesaj geri dondu)
            _armed = true;

            Transform anchor = PinHandAnchor(hand);
            if (anchor != null)
                _pinHolder = GrenadePin.DetachTo(transform, anchor, _cfg);
            else
                Debug.LogWarning("[Bomba] Pimi cekenin eli bulunamadi — pim bombada birakildi.");

            if (_cfg != null)
                WeaponAudioPlayer.PlayAt(_cfg.pinClip, transform.position, 0.7f, 0.98f, 1.02f, 20f);
        }

        /// <summary>Pimi ceken elin anchor'i: pimi ceken, bombayi TUTAN oyuncunun ta kendisi,
        /// dolayisiyla el sahibin avatarindan okunur — boylece uzaktaki oyuncular da pimi
        /// adamin elinde gorur.</summary>
        Transform PinHandAnchor(byte hand)
        {
            if (_grab == null) return null;

            var po = PlayerObjectOf(_grab.HolderClientId);
            if (po == null) po = PlayerObjectOf(_lastHolder);
            if (po == null) return null;

            var grabber = po.GetComponent<HandGrabber>();
            if (grabber == null) grabber = po.GetComponentInChildren<HandGrabber>(true);
            if (grabber == null) return null;

            return hand == 0 ? grabber.LeftAnchor : grabber.RightAnchor;
        }

        void SendPinToServer(byte hand)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || _netObj == null) return;

            if (nm.IsServer) { ServerOnPin(_netObj.NetworkObjectId, hand, nm.LocalClientId); return; }

            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(_netObj.NetworkObjectId);
            w.WriteValueSafe(hand);
            nm.CustomMessagingManager.SendNamedMessage(MsgPin, NetworkManager.ServerClientId, w,
                NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Sunucu: pim haberini dogrulayip herkese dagitir. Yalniz objenin SAHIBI
        /// pimini cekebilir — baskasinin elindeki bombayi kuran sahte mesaj burada duser.</summary>
        static void ServerOnPin(ulong objId, byte hand, ulong sender)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(objId, out var no)) return;
            if (sender != no.OwnerClientId) return;

            var gc = no.GetComponent<GrenadeController>();
            if (gc == null || gc._exploded) return;

            // Sunucunun kendi kopyasi da guncellenir: funye yalniz KURULU bombada baslar.
            gc.ApplyPin(hand);

            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(objId);
            w.WriteValueSafe(hand);
            nm.CustomMessagingManager.SendNamedMessageToAll(MsgPinAll, w,
                NetworkDelivery.ReliableSequenced);
        }

        void FixedUpdate()
        {
            EnsureHandlers();
            if (_rb == null || _grab == null || _exploded) return;

            // Firlatma anini kinematik → dinamik gecisinden yakala (ApplyThrow'un actigi tek
            // pencere). GrabbableObject'e dokunmadan hiz olcekleme + takla burada eklenir.
            //
            // IsHeld'e BAKILMAZ: ReleaseServerRpc sunucudan donene kadar (~1 RTT) obje hala
            // "tutuluyor" gorunur, ama fizik o anda coktan acilmistir. Kenari o pencerede
            // elemek bombayi ucurur ama hic kurmazdi (istemcide patlamaz, host'ta patlar —
            // tam da yakalanmasi zor tur). Kinematigi acan tek yol zaten owner'in ApplyThrow'u.
            bool kin = _rb.isKinematic;
            if (_wasKinematic && !kin && _grab.IsOwner)
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

            if (_cfg.selfCollisionIgnoreSeconds > 0f) StartCoroutine(IgnoreThrowerBriefly());
        }

        /// <summary>Firlatmadan hemen sonra ATANIN kendi collider'lari kisa sure yok sayilir.
        /// VR'da bomba elin — yani govdenin — icinden dogar; bu pencere olmadan bomba cikar
        /// cikmaz oyuncunun kendi collider'ina carpip saçma yonlere savruluyor, ustelik bu
        /// "temas" sayildigi icin funye daha havalanmadan basliyordu.</summary>
        IEnumerator IgnoreThrowerBriefly()
        {
            var mine = GetComponentsInChildren<Collider>(true);
            var theirs = ThrowerColliders();
            if (mine.Length == 0 || theirs == null || theirs.Length == 0) yield break;

            SetIgnore(mine, theirs, true);
            yield return new WaitForSeconds(_cfg.selfCollisionIgnoreSeconds);
            SetIgnore(mine, theirs, false);
        }

        static void SetIgnore(Collider[] a, Collider[] b, bool ignore)
        {
            foreach (var x in a)
            {
                if (x == null) continue;
                foreach (var y in b)
                {
                    if (y == null || y == x) continue;
                    Physics.IgnoreCollision(x, y, ignore);
                }
            }
        }

        Collider[] ThrowerColliders()
        {
            var po = PlayerObjectOf(_grab != null ? _grab.HolderClientId : GrabbableObject.NoHolder);
            if (po == null) po = PlayerObjectOf(_lastHolder);
            return po != null ? po.GetComponentsInChildren<Collider>(true) : null;
        }

        static GameObject PlayerObjectOf(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null || clientId == GrabbableObject.NoHolder)
                return null;
            var players = nm.SpawnManager.PlayerObjects;
            for (int i = 0; i < players.Count; i++)
            {
                var po = players[i];
                if (po != null && po.OwnerClientId == clientId) return po.gameObject;
            }
            return null;
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
            // Pimi cekilmemis bomba patlamaz: sunucu bunu KENDI kopyasindan bilir (pim haberi
            // buraya varmadan temas haberi islenemez, ikisi de ReliableSequenced).
            if (!gc._armed) return;

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
            {
                var cfg = gc != null ? gc._cfg : null;
                UI.FlashBlindEffect.TriggerAt(pos,
                    cfg != null ? cfg.flashRadius : 15f,
                    cfg != null ? cfg.flashHoldSeconds : 0.25f,
                    cfg != null ? cfg.flashBlindSeconds : 8f);
            }

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

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPin, (sender, reader) =>
            {
                reader.ReadValueSafe(out ulong id);
                reader.ReadValueSafe(out byte hand);
                ServerOnPin(id, hand, sender);
            });

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPinAll, (sender, reader) =>
            {
                var local = NetworkManager.Singleton;
                if (local == null) return;
                if (sender != NetworkManager.ServerClientId) return; // yalniz sunucudan
                if (local.IsServer) return; // sunucu kendi kopyasini ServerOnPin'de guncelledi
                reader.ReadValueSafe(out ulong id);
                reader.ReadValueSafe(out byte hand);
                if (local.SpawnManager == null ||
                    !local.SpawnManager.SpawnedObjects.TryGetValue(id, out var no)) return;
                var gc = no.GetComponent<GrenadeController>();
                if (gc != null) gc.ApplyPin(hand);
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
