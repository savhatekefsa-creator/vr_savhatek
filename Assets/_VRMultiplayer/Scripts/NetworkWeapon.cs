using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// Turns a held <see cref="GrabbableObject"/> into a firing weapon: while YOU hold it, the
    /// holding hand's TRIGGER fires a hitscan ray from the muzzle along the barrel. The server
    /// validates (only the holder may fire, rate-limited) and raycasts authoritatively, then
    /// everyone sees the same tracer + muzzle flash + impact spark; the shooter's controller
    /// buzzes. The muzzle and barrel direction are auto-detected from the mesh (longest axis),
    /// matching how HandGrabber aligns the weapon in the hand.
    /// </summary>
    [RequireComponent(typeof(GrabbableObject))]
    public class NetworkWeapon : NetworkBehaviour
    {
        [Tooltip("Iki atis arasi minimum sure (saniye).")]
        public float fireInterval = 0.18f;
        [Tooltip("Isinin maksimum menzili (metre).")]
        public float range = 60f;

        [Tooltip("Namlu ucu noktasi (ates izi buradan, bakis yonunde cikar). Bos birakilirsa 'Muzzle' adli cocuk aranir, o da yoksa otomatik hesaplanir.")]
        public Transform muzzle;

        GrabbableObject _grab;
        WeaponGripProfile _profile;
        WeaponRecoil _recoil;
        Vector3 _muzzleLocal;
        Vector3 _barrelLocal = Vector3.forward;
        float _nextFire;
        float _srvNextFire;
        float _lastFire = float.NegativeInfinity;
        float _bloom;   // birikmis sapma (derece), owner-lokal
        bool _prevTrigger;

        // Cozulmus savas degerleri — kaynak zinciri ResolveCombat()'ta. Kadans/menzil/sarjor/
        // sacilim/tepme tuketiminin tamami buradan okur; config guncellemesi gelince yeniden
        // cozulur, boylece canli ayar calisan silaha aninda yansir.
        CombatValues _cv;

        /// <summary>WeaponRecoil'un her kare okudugu anlik savas degerleri.</summary>
        public CombatValues Combat => _cv;

        // Sarjor sayaci SILAHIN uzerinde durur, oyuncunun degil: silah el degistirse de, yere
        // atilsa da yarim sarjor onunla kalir ve iki silah birbirinden bagimsiz sayar — hicbiri
        // icin ek kod yok. Sunucu yazar, herkes okur (NetworkVariable varsayilani).
        readonly NetworkVariable<int> _ammo = new NetworkVariable<int>();
        readonly NetworkVariable<int> _spares = new NetworkVariable<int>();
        // 0 = dolum yok. Sunucu SAATIYLE yazilir ki dolum her istemcide ayni anda bitsin.
        readonly NetworkVariable<double> _reloadDoneAt = new NetworkVariable<double>();

        // Sarjor degistirme hareketi — yalnizca silahi TUTAN istemcide islenir.
        int _flickPhase;            // 0 bekle, 1 asagi iniyor, 2 yukari donuyor
        float _flickPrevY, _flickTopY, _flickLowY;
        float _flickStartedAt;
        float _nextReloadRequest;
        bool _dryFired;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Dev harness kancasi (yalnizca editor/development build). PC'de hicbir XR
        /// kumandasi gecerli olmadigi icin <see cref="ReadTrigger"/> hep false doner ve Update
        /// ates edemez. Harness bunu doldurunca tetik SIMULE edilir ve Update'in gercek yolu
        /// calisir: Semi/Auto ayrimi, kadans, sapma, tepme, hepsi uretim koduyla test edilir.
        /// Silah basina cagrilir; argument = tetigi sorulan silah.</summary>
        public static System.Func<NetworkWeapon, bool> DevTriggerOverride;
#endif

        // Savas sayilari cozulmus degerlerden; FX/haptik profilde kalir (kozmetik).
        bool IsAuto => _cv.fireMode == FireMode.Auto;
        float HapticAmplitude => _profile != null ? _profile.hapticAmplitude : 0.7f;
        float HapticDuration => _profile != null ? _profile.hapticDuration : 0.08f;
        float SupportHapticAmplitude => _profile != null ? _profile.supportHapticAmplitude : 0f;

        /// <summary>Sarjor kapasitesi; 0 = bu silahta mermi HIC sayilmaz. Profilsiz/configsiz
        /// silah da buraya duser, yani bugunku sinirsiz davranis aynen korunur.</summary>
        public int MagazineSize => _cv.magazineSize;
        public bool UsesAmmo => MagazineSize > 0;
        public int Ammo => _ammo.Value;
        public int SpareMagazines => _spares.Value;
        public bool IsReloading => _reloadDoneAt.Value > 0d;
        float ReloadDuration => _cv.reloadDuration;

        // Effects (created once, reused per shot)
        LineRenderer _tracer;
        Light _flash;

        // Mermi izleri: silahin ALTINDA DEGIL, dunya kokunde duran bir havuz. Silahin cocugu
        // olsalardi silah her dondugunde izler de suruklenirdi. Havuz dolunca en eski iz geri
        // donusur — sinirsiz GameObject birikmez.
        const int DecalCount = 48;
        Transform _decalRoot;
        Transform[] _decals;
        int _decalNext;

        // Ucan ates izi: atis dunya uzayinda saklanir, her kare ilerletilir.
        Vector3 _shotOrigin, _shotDir, _shotEnd, _shotNormal;
        float _shotDist;
        float _shotFiredAt;
        bool _shotFlying;
        bool _impactShown;
        float _flashOffAt = -1f;

        Color TracerColor => _profile != null ? _profile.tracerColor : new Color(1f, 0.45f, 0.12f);
        float TracerSpeed => _profile != null ? _profile.tracerSpeed : 260f;
        float TracerLength => _profile != null ? _profile.tracerLength : 2.5f;
        float TracerWidth => _profile != null ? _profile.tracerWidth : 0.03f;
        float FlashDuration => _profile != null ? _profile.flashDuration : 0.035f;
        Color ImpactColor => _profile != null ? _profile.impactColor : new Color(0.03f, 0.03f, 0.04f, 1f);
        float ImpactSize => _profile != null ? _profile.impactSize : 0.022f;

        void Awake()
        {
            _grab = GetComponent<GrabbableObject>();
            if (muzzle == null) muzzle = transform.Find("Muzzle");
            ApplyProfile();
            ResolveCombat();
            AttachRecoil();
            ComputeBarrel();
            CreateFx();
        }

        /// <summary>Savas degerlerini kaynak zincirinden cozer: profile.combat SO -> eski profil
        /// alanlari -> kod varsayilanlari. (Ag katmani geldiginde zincirin basina agdan gelen
        /// kayit eklenir.) Awake'te ve her config guncellemesinde cagrilir.</summary>
        void ResolveCombat()
        {
            if (_profile != null && _profile.combat != null)
                _cv = CombatValues.FromConfig(_profile.combat);
            else if (_profile != null)
                _cv = CombatValues.FromLegacyProfile(_profile, fireInterval, range);
            else
                _cv = CombatValues.Defaults(fireInterval, range);
        }

        // Tepme bileseni yalnizca degerler gercekten kick istiyorsa takilir (profil de sart:
        // pivot/namlu geometrisi oradan gelir). Dokunulmamis silah LateUpdate maliyeti gormez.
        // Canli ayarla kick sonradan acilirsa config guncellemesi bunu yeniden cagirir.
        void AttachRecoil()
        {
            if (_profile == null || _recoil != null) return;
            if (_cv.kickPitchPerShot == 0f && _cv.kickYawJitter == 0f && _cv.kickBackMeters == 0f)
                return;
            _recoil = gameObject.AddComponent<WeaponRecoil>();
            _recoil.Init(_grab, _profile, this);
        }

        // Optional data-driven overrides from the same profile the grip system uses. Only the
        // firing NUMBERS and an optional muzzle spawn come from here — the FireServerRpc path,
        // hitscan and damage stay exactly as authored. A weapon with no profile is untouched.
        void ApplyProfile()
        {
            var profile = WeaponGripBinder.FindProfile(name);
            if (profile == null) return;
            _profile = profile;

            if (muzzle == null && profile.createMuzzleIfMissing)
            {
                var m = new GameObject("Muzzle").transform;
                m.SetParent(transform, false);
                m.localPosition = profile.muzzleLocalPosition;
                // Muzzle forward = profilin namlu ekseni. Identity (+Z) varsaymak, paketin
                // -Z namlulu modellerinde ters yone bakan bir muzzle uretir.
                Vector3 b = profile.barrelLocalDirection;
                m.localRotation = b.sqrMagnitude > 1e-6f
                    ? Quaternion.LookRotation(b.normalized,
                        Mathf.Abs(b.normalized.y) > 0.9f ? Vector3.forward : Vector3.up)
                    : Quaternion.identity;
                muzzle = m;
            }
        }

        public override void OnNetworkSpawn()
        {
            // Silah dolu sarjorle dogar. Yalnizca sunucu yazar; degerler NetworkVariable ile
            // herkese (ve sonradan katilana) kendiliginden tasinir.
            if (IsServer && UsesAmmo)
            {
                _ammo.Value = MagazineSize;
                _spares.Value = _cv.spareMagazines;
            }
        }

        void Update()
        {
            UpdateFx(); // herkeste, silah elde olmasa da: ucan iz sahibi birakinca da tamamlanir

            // Dolumu sunucu bitirir ve sunucu silahi tutan kisi OLMAYABILIR — bu yuzden
            // asagidaki "sadece tutan oyuncu" cikislarindan ONCE isletilmeli.
            if (IsSpawned && IsServer) TickReloadServer();

            if (!IsSpawned || _grab == null || !_grab.IsHeld) { _prevTrigger = false; _bloom = 0f; ResetFlick(); return; }
            if (NetworkManager == null || _grab.HolderClientId != NetworkManager.LocalClientId) { ResetFlick(); return; }

            TickReloadGesture();

            // Ates kesilince koni daralir.
            if (_bloom > 0f)
                _bloom *= Mathf.Pow(2f, -Time.deltaTime / Mathf.Max(0.001f, _cv.spreadDecayHalfLife));

            // EITHER controller's trigger fires while you hold the weapon — grip hand or
            // support hand, so two-handed players can use their front-hand trigger too.
            bool trig = ReadTrigger(XRNode.RightHand, out var rDev);
            var firedDev = rDev;
            var firedNode = XRNode.RightHand;
            if (!trig)
            {
                trig = ReadTrigger(XRNode.LeftHand, out var lDev);
                firedDev = lDev;
                firedNode = XRNode.LeftHand;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (DevTriggerOverride != null) trig = DevTriggerOverride(this);
#endif

            // Semi: her atis tetigin yeniden cekilmesini ister. Auto: basili tutuldukca tarar.
            bool wantsFire = IsAuto ? trig : (trig && !_prevTrigger);

            // Bos sarjor / dolum sirasinda tetik. Buradaki kontrol YALNIZCA his icindir —
            // otorite FireServerRpc'de. Kuru tetik titresimi tetigin her cekilisinde bir kez
            // verilir; auto'da parmak basili dururken kumandayi surekli titretmemek icin.
            if (wantsFire && UsesAmmo && (_ammo.Value <= 0 || IsReloading))
            {
                if (!_dryFired) { DryFire(firedDev); _dryFired = true; }
                wantsFire = false;
            }
            if (!trig) _dryFired = false;

            if (wantsFire && Time.time >= _nextFire)
            {
                // Kadans kareye degil saate baglanir: taramada frame quantization birikip
                // atis hizini dusurmez. Uzun aradan sonra ise tam bir aralik beklenir —
                // yoksa geride kalmis _nextFire bir sonraki karede bedava ikinci atis verir.
                _nextFire += _cv.fireInterval;
                if (_nextFire < Time.time) _nextFire = Time.time + _cv.fireInterval;
                Fire(firedDev, firedNode);
            }
            _prevTrigger = trig;

            // Tarama sonerken tepme yavas dinsin, tetik kesilince hizla toparlansin.
            if (_recoil != null)
                _recoil.SetSustainedFire(trig && Time.time - _lastFire < _cv.fireInterval * 2f);
        }

        void Fire(InputDevice firedDev, XRNode firedNode)
        {
            _lastFire = Time.time;

            Vector3 origin;
            Vector3 dir;
            if (muzzle != null)
            {
                origin = muzzle.position;   // precise barrel tip placed in the editor
                dir = muzzle.forward;
                // Profil namlu eksenini biliyorsa YON her zaman profilden gelir. Kurulum
                // aracinin koydugu Muzzle profil ekseniyle CELISIYORSA (Smg 1: arac muzzle'i
                // dipcik ucuna koyup -Z'ye dondurdu, gercek namlu +Z) o muzzle'a NOKTA olarak
                // da guvenilmez — cikis noktasi ComputeBarrel'in profil-hizali ucundan gelir.
                if (_profile != null && _profile.barrelLocalDirection.sqrMagnitude > 1e-6f)
                {
                    Vector3 pdir = (transform.rotation * _profile.barrelLocalDirection).normalized;
                    if (Vector3.Dot(dir, pdir) < 0f)
                        origin = transform.TransformPoint(_muzzleLocal);
                    dir = pdir;
                }
            }
            else
            {
                origin = transform.TransformPoint(_muzzleLocal);
                dir = (transform.rotation * _barrelLocal).normalized;
            }

            // Sacilim owner'da uygulanir ve RPC'ye SACILMIS yon girer: tracer, sunucu isabeti
            // ve hasar hepsi ayni yonu paylasir, ayrica bir senkron gerekmez. (Configsiz silahta
            // spreadBase/PerShot sifirdir — blok no-op, eski davranis birebir.)
            float mult = _grab.SupportHand != GrabbableObject.NoHand
                ? _cv.supportRecoilMultiplier
                : 1f;
            dir = ApplySpread(dir, Mathf.Min(_cv.spreadBase + _bloom, _cv.spreadMax));
            _bloom = Mathf.Min(_bloom + _cv.spreadPerShot * mult, _cv.spreadMax);

            FireServerRpc(origin, dir);

            // Yon YUKARIDA okundu: bu atis mevcut (onceki karenin tepmis) pozunu kullanir,
            // yeni kick bir sonraki atisi kaldirir.
            if (_recoil != null) _recoil.AddKick();

            if (firedDev.isValid)
                firedDev.SendHapticImpulse(0, HapticAmplitude, HapticDuration);

            // Destek eli de silahta: ona da hafif bir vurus. Tetigi ceken el tam siddeti
            // zaten aldi — ayni kumandayi ikinci kez titretme.
            byte sup = _grab.SupportHand;
            if (sup != GrabbableObject.NoHand && SupportHapticAmplitude > 0f)
            {
                XRNode supNode = sup == 0 ? XRNode.LeftHand : XRNode.RightHand;
                if (supNode != firedNode)
                {
                    var supDev = InputDevices.GetDeviceAtXRNode(supNode);
                    if (supDev.isValid)
                        supDev.SendHapticImpulse(0, SupportHapticAmplitude, HapticDuration);
                }
            }
        }

        /// <summary>Yonu, yari-acisi `degrees` olan koninin icinde rastgele bir yone kaydirir.
        /// insideUnitCircle teget duzlemde duzgun dagilir, yani atislar koni icinde kumelenmeden
        /// esit yayilir.</summary>
        static Vector3 ApplySpread(Vector3 dir, float degrees)
        {
            if (degrees <= 0f) return dir;
            dir.Normalize();

            Vector3 right = Vector3.Cross(dir, Vector3.up);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(dir, Vector3.right); // namlu dike yakin
            right.Normalize();
            Vector3 up = Vector3.Cross(right, dir);

            Vector2 d = Random.insideUnitCircle * Mathf.Tan(degrees * Mathf.Deg2Rad);
            return (dir + right * d.x + up * d.y).normalized;
        }

        static bool ReadTrigger(XRNode node, out InputDevice dev)
        {
            dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return false;
            dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trig);
            if (!trig && dev.TryGetFeatureValue(CommonUsages.trigger, out float t))
                trig = t > 0.6f;
            return trig;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void FireServerRpc(Vector3 origin, Vector3 dir, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != _grab.HolderClientId) return; // only the holder fires
            if (dir.sqrMagnitude < 0.5f) return;

            // Mermi otoritesi burada: ele gecirilmis bir istemci istedigi kadar FireServerRpc
            // cagirsin, bos sarjorle ya da dolum ortasinda atis cikmaz. Istemcideki ayni
            // kontrol sadece hisdir, guvenlik degil.
            if (UsesAmmo && (_ammo.Value <= 0 || IsReloading)) return;

            // Kadansi istemciye guvenmeden sunucu zorlar: ele gecirilmis bir istemci
            // FireServerRpc'yi her karede cagirsa da atis hizi profilin uzerine cikamaz.
            // %15 tolerans, ag jitter'inda mesru atisin dusmesini onler.
            if (Time.time < _srvNextFire) return;
            _srvNextFire = Time.time + _cv.fireInterval * 0.85f;

            // Kadans kapisini gectik = atis GERCEKTEN cikiyor; mermi tam burada duser.
            if (UsesAmmo) _ammo.Value--;

            dir.Normalize();

            // Authoritative hit: nearest thing the ray touches that is neither the weapon nor
            // the shooter's own body. Player hitboxes are triggers, so include triggers.
            ulong shooter = _grab.HolderClientId;
            byte shooterTeam = TeamOf(shooter);

            Vector3 end = origin + dir * _cv.range;
            // Sifir = mermi izi birakma. YALNIZCA sabit geometri normal doner: hareketli bir
            // oyuncuya dunya-uzayi izi cakarsak oyuncu yurudugunde iz havada asili kalirdi.
            Vector3 hitNormal = Vector3.zero;
            var hits = Physics.RaycastAll(origin + dir * 0.03f, dir, _cv.range,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            int hitboxesSeen = 0;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue; // own weapon

                // Regional damage: the ray hits a HitZone (head/torso/arm/leg); the per-region
                // amount is looked up in CombatConfig. GetComponentInParent reaches the zone whether
                // the collider carries it directly or on a child. The amount is resolved on the
                // SERVER (clients can't send damage values — security).
                var zone = h.collider.GetComponentInParent<HitZone>();
                if (zone != null && zone.health != null)
                {
                    var health = zone.health;
                    // Never hit yourself; keep going past your own body.
                    if (health.OwnerClientId == shooter) continue;
                    hitboxesSeen++;
                    byte t = health.TeamValue;
                    if (t != 0 && t == shooterTeam)
                    {
                        Debug.Log($"[Silah] Isabet ENGELLENDI (ayni takim {t}): atan {shooter} -> {health.OwnerClientId}");
                        end = h.point; break; // block, no damage (friendly fire off)
                    }
                    int dmg = CombatConfig.Instance.DamageFor(zone.zoneType);
                    Debug.Log($"[Silah] ISABET! atan {shooter} (takim {shooterTeam}) -> hedef {health.OwnerClientId} (takim {t}), bolge {zone.zoneName}, {dmg} hasar. Kalan: {Mathf.Max(0, health.Health.Value - dmg)}");
                    health.ServerApplyDamage(dmg, shooter);
                    end = h.point; break;
                }

                end = h.point; // first solid/non-player hit stops the ray
                hitNormal = h.normal;
                break;
            }

            if (hitboxesSeen == 0)
                Debug.Log($"[Silah] Ates edildi ama HIC OYUNCU HITBOX'ina denk gelmedi. Toplam collider: {hits.Length}. Ilk carpan: {(hits.Length > 0 ? hits[0].collider.name : "hicbir sey")}");

            FireFxClientRpc(origin, end, hitNormal);
        }

        byte TeamOf(ulong clientId)
        {
            if (NetworkManager != null &&
                NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) &&
                c.PlayerObject != null)
            {
                var id = c.PlayerObject.GetComponent<PlayerIdentity>();
                if (id != null) return id.Team.Value;
            }
            return 0;
        }

        [Rpc(SendTo.Everyone)]
        void FireFxClientRpc(Vector3 origin, Vector3 end, Vector3 hitNormal)
        {
            ShowShot(origin, end, hitNormal);
        }

        // ------------------------------------------------------------- sarjor

        /// <summary>Sarjor degistirme istegi — hareketi yakalayan istemci gonderir. Istemciye
        /// hicbir konuda guvenilmez: gonderen gercekten silahi tutuyor mu, sarjor zaten dolu mu,
        /// yedek kaldi mi, zaten dolum var mi — hepsini sunucu dogrular.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ReloadServerRpc(RpcParams p = default)
        {
            if (!UsesAmmo) return;
            if (p.Receive.SenderClientId != _grab.HolderClientId) return;
            if (IsReloading) return;
            if (_ammo.Value >= MagazineSize) return; // dolu sarjor degistirilmez
            if (_spares.Value == 0) return;          // -1 = sinirsiz, 0 = yedek bitti

            _reloadDoneAt.Value = NetworkManager.ServerTime.Time + ReloadDuration;
        }

        /// <summary>Sunucu: suresi dolan dolumu tamamlar. Silah dolum ortasinda birakilirsa
        /// iptal edilir — yoksa yerde duran silah kendi kendine dolardi.</summary>
        void TickReloadServer()
        {
            if (_reloadDoneAt.Value <= 0d) return;

            if (_grab == null || !_grab.IsHeld) { _reloadDoneAt.Value = 0d; return; }
            if (NetworkManager.ServerTime.Time < _reloadDoneAt.Value) return;

            _reloadDoneAt.Value = 0d;
            _ammo.Value = MagazineSize;
            if (_spares.Value > 0) _spares.Value--; // -1 (sinirsiz) oldugu gibi kalir
            ReloadDoneClientRpc();
        }

        [Rpc(SendTo.Everyone)]
        void ReloadDoneClientRpc()
        {
            if (NetworkManager == null || _grab == null || !_grab.IsHeld) return;
            if (_grab.HolderClientId != NetworkManager.LocalClientId) return;
            Buzz(_grab.HolderHand == 0 ? XRNode.LeftHand : XRNode.RightHand, 0.75f, 0.09f);
        }

        /// <summary>Silahi asagi savirip geri kaldirma hareketi (yalnizca tutan istemcide).
        ///
        /// Buradaki filtrelerin tamami kazara sarjor degisimine karsidir. Silahi indirip
        /// kaldirmak bir nisanci oyununda yapilan EN dogal harekettir — yururken, sipere
        /// cokerken, asagi nisan alirken surekli olur. Bu yuzden hareket sayilmak icin
        /// HIZLI olmali (yavas indirmek sayilmaz), asagi ve yukari fazlarin her biri yeterli
        /// YOL katetmeli ve ikisi kisa bir PENCERE icinde bitmeli. Sarjor doluysa hic bakilmaz.
        /// </summary>
        void TickReloadGesture()
        {
            if (!UsesAmmo || _profile == null) { ResetFlick(); return; }

            float y = transform.position.y;
            float dt = Time.deltaTime;
            if (dt <= 0f) { _flickPrevY = y; return; }
            float vy = (y - _flickPrevY) / dt;
            _flickPrevY = y;

            // Dolu sarjor / zaten dolum var / az once istendi -> hareketi izlemeye bile gerek yok.
            if (_ammo.Value >= MagazineSize || IsReloading || Time.time < _nextReloadRequest)
            {
                _flickPhase = 0;
                return;
            }

            float speed = _profile.reloadFlickSpeed;
            float travel = _profile.reloadFlickTravel;
            bool expired = Time.time - _flickStartedAt > _profile.reloadFlickWindow;

            switch (_flickPhase)
            {
                case 0: // bekle: yeterince hizli bir ASAGI hareket baslatir
                    if (vy <= -speed)
                    {
                        _flickPhase = 1;
                        _flickTopY = y;
                        _flickLowY = y;
                        _flickStartedAt = Time.time;
                    }
                    break;

                case 1: // asagi iniyor: dip noktayi takip et, yeterince indiyse donusu bekle
                    if (expired) { _flickPhase = 0; break; }
                    if (y < _flickLowY) _flickLowY = y;
                    if (_flickTopY - _flickLowY >= travel && vy >= speed) _flickPhase = 2;
                    break;

                case 2: // yukari donuyor: dipten yeterince kalktiysa hareket tamamlandi
                    if (expired) { _flickPhase = 0; break; }
                    if (y - _flickLowY >= travel)
                    {
                        _flickPhase = 0;
                        _nextReloadRequest = Time.time + 0.6f; // tek harekette tek istek
                        ReloadServerRpc();
                        // Hareket KABUL edildi. Tusa basmadigin icin bunu hissetmen sart:
                        // yoksa oyuncu algilandi mi bilemez ve silahi sallamaya devam eder.
                        Buzz(_grab.HolderHand == 0 ? XRNode.LeftHand : XRNode.RightHand, 0.5f, 0.05f);
                    }
                    break;
            }
        }

        // Silah elde degilken de tazelenir: yoksa silahi kaptigin ILK karede eski/sifir
        // yukseklikle devasa bir sahte hiz cikar ve hareket kendiliginden tetiklenir.
        void ResetFlick()
        {
            _flickPhase = 0;
            _flickPrevY = transform.position.y;
        }

        // Ses varligi yok; kuru tetik yalnizca titresimle bildirilir. Atis titresiminden
        // belirgin sekilde kisa ve zayif — "atesledim" sanilmasin diye.
        static void DryFire(InputDevice dev)
        {
            if (dev.isValid) dev.SendHapticImpulse(0, 0.25f, 0.03f);
        }

        static void Buzz(XRNode node, float amplitude, float duration)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (dev.isValid) dev.SendHapticImpulse(0, amplitude, duration);
        }

        // ------------------------------------------------------------- barrel

        // Longest mesh axis = barrel line; the sign toward the mesh's bulk = muzzle side.
        // Same convention as HandGrabber's snap alignment, so the shot goes where you aim.
        void ComputeBarrel()
        {
            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest == null) { _muzzleLocal = Vector3.forward * 0.4f; return; }

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
            Vector3 axis = Vector3.right;
            float extent = mb.extents.x;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; extent = mb.extents.y; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) { axis = Vector3.forward; extent = mb.extents.z; }

            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            if (sign == 0f) sign = 1f;

            // Profil namlu eksenini soyluyorsa tahmin ONUNLA hizalanir: eksen, profil yonune
            // en yakin ana eksene, isaret de profil isaretine cekilir. "Kutle tarafi = namlu"
            // varsayimi, kutlesi dipcik/govdede toplanan modellerde (SMG gibi) ters secebiliyor.
            if (_profile != null && _profile.barrelLocalDirection.sqrMagnitude > 1e-6f)
            {
                Vector3 axisMesh = Quaternion.Inverse(biggest.transform.rotation)
                    * (transform.rotation * _profile.barrelLocalDirection.normalized);
                axis = Vector3.right; extent = mb.extents.x; float a = Mathf.Abs(axisMesh.x);
                if (Mathf.Abs(axisMesh.y) > a) { axis = Vector3.up; extent = mb.extents.y; a = Mathf.Abs(axisMesh.y); }
                if (Mathf.Abs(axisMesh.z) > a) { axis = Vector3.forward; extent = mb.extents.z; }
                sign = Mathf.Sign(Vector3.Dot(axisMesh, axis));
                if (sign == 0f) sign = 1f;
            }

            Vector3 muzzleChild = mb.center + axis * (sign * extent);
            _muzzleLocal = transform.InverseTransformPoint(biggest.transform.TransformPoint(muzzleChild));
            _barrelLocal = (Quaternion.Inverse(transform.rotation) * biggest.transform.rotation) * (axis * sign);
        }

        // ------------------------------------------------------------- effects

        // Runtime-created materials need a shader that is guaranteed to be IN the build.
        // URP/Unlit may get stripped (nothing in the scene references it); URP/Lit always
        // ships because the room materials use it.
        static Shader FindShaderSafe()
        {
            var s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Unlit/Color");
            return s;
        }

        void CreateFx()
        {
            var tracerGo = new GameObject("Tracer");
            tracerGo.transform.SetParent(transform, false);
            _tracer = tracerGo.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 2;
            _tracer.widthMultiplier = TracerWidth;
            var mat = new Material(FindShaderSafe());
            Color tc = TracerColor;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tc);
            else mat.color = tc;
            _tracer.material = mat;
            _tracer.enabled = false;

            var flashGo = new GameObject("Muzzle Flash");
            flashGo.transform.SetParent(transform, false);
            _flash = flashGo.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.8f, 0.4f);
            _flash.intensity = 3f;
            _flash.range = 4f;
            _flash.enabled = false;

            if (ImpactSize <= 0f) return; // iz istenmiyorsa havuzu hic kurma

            var imat = new Material(FindShaderSafe());
            Color ic = ImpactColor;
            if (imat.HasProperty("_BaseColor")) imat.SetColor("_BaseColor", ic);
            else imat.color = ic;

            // Kok seviyesinde: silahla birlikte tasinmamalilar.
            _decalRoot = new GameObject(name + " Bullet Holes").transform;
            _decals = new Transform[DecalCount];
            for (int i = 0; i < DecalCount; i++)
            {
                // Yassilastirilmis SILINDIR = yuvarlak disk (kursun deligi kare degil yuvarlak).
                // Silindir de kup gibi simetrik, yani normalin isareti yanlis olsa bile gorunmez
                // yuze donmez (Quad'in tek yuzu var, ters donerse hic cizilmez).
                var d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                d.name = "Bullet Hole";
                Destroy(d.GetComponent<Collider>());
                d.GetComponent<MeshRenderer>().sharedMaterial = imat;
                d.transform.SetParent(_decalRoot, false);
                d.SetActive(false);
                _decals[i] = d.transform;
            }
        }

        public override void OnDestroy()
        {
            // Havuz silahin cocugu olmadigi icin onunla birlikte yok olmaz — elle temizle.
            if (_decalRoot != null) Destroy(_decalRoot.gameObject);
            base.OnDestroy();
        }

        void ShowShot(Vector3 origin, Vector3 end, Vector3 hitNormal)
        {
            _shotOrigin = origin;
            _shotEnd = end;
            _shotNormal = hitNormal;
            Vector3 d = end - origin;
            _shotDist = d.magnitude;
            _shotDir = _shotDist > 1e-4f ? d / _shotDist : transform.forward;
            _shotFiredAt = Time.time;
            _shotFlying = true;
            _impactShown = false;

            // Namlu alevi silahin cocugu: dunya noktasi bir kez yazilir, sonra silahla birlikte
            // hareket eder — namludan ayrilmaz.
            if (_flash != null)
            {
                _flash.transform.position = origin;
                _flash.enabled = true;
                _flashOffAt = Time.time + FlashDuration;
            }

            UpdateFx(); // ilk kareyi hemen ciz: bir kare gecikmeyle baslamasin
        }

        // Izi namludan hedefe dogru UCURUR. Eskiden tam boy cizgi aninda cizilip 70 ms duruyordu:
        // silahi cevirirken donuk cizgi namludan kopuk kaliyor ve atis sapmis gibi gorunuyordu.
        void UpdateFx()
        {
            if (_flashOffAt > 0f && Time.time > _flashOffAt)
            {
                _flashOffAt = -1f;
                if (_flash != null) _flash.enabled = false;
            }

            if (!_shotFlying) return;

            float speed = TracerSpeed;
            if (speed <= 0f)
            {
                // Hiz 0 = eski davranis: aninda tam boy cizgi.
                if (_tracer != null)
                {
                    _tracer.SetPosition(0, _shotOrigin);
                    _tracer.SetPosition(1, _shotEnd);
                    _tracer.enabled = true;
                }
                ShowImpact();
                if (Time.time - _shotFiredAt > 0.07f)
                {
                    _shotFlying = false;
                    if (_tracer != null) _tracer.enabled = false;
                }
                return;
            }

            float travelled = (Time.time - _shotFiredAt) * speed;
            float head = Mathf.Min(travelled, _shotDist);
            float tail = Mathf.Max(0f, travelled - Mathf.Max(0.1f, TracerLength));

            // Kivilcim izin ucu hedefe VARDIGINDA parlar, atisla ayni anda degil.
            if (travelled >= _shotDist) ShowImpact();

            if (tail >= _shotDist)
            {
                _shotFlying = false;
                if (_tracer != null) _tracer.enabled = false;
                return;
            }

            if (_tracer != null)
            {
                _tracer.SetPosition(0, _shotOrigin + _shotDir * tail);
                _tracer.SetPosition(1, _shotOrigin + _shotDir * head);
                _tracer.enabled = true;
            }
        }

        // Carptigi yerde KALICI bir iz birakir. Havuzdaki en eski iz geri donusur.
        void ShowImpact()
        {
            if (_impactShown) return;
            _impactShown = true;

            // Normal sifir = hicbir seye carpmadi ya da bir OYUNCUYA carpti: iz birakma.
            // Yuruyen bir oyuncuya cakilan dunya-uzayi izi havada asili kalirdi.
            if (_decals == null || _shotNormal.sqrMagnitude < 0.5f) return;

            var d = _decals[_decalNext];
            _decalNext = (_decalNext + 1) % _decals.Length;

            // Silindirin ekseni LOKAL Y; onu yuzey normaline hizala. Olcek: mesh yaricapi 0.5
            // (yani cap = scale.x) ve yuksekligi 2 (yani kalinlik = 2 * scale.y).
            float s = ImpactSize;
            d.SetPositionAndRotation(_shotEnd + _shotNormal * 0.001f,
                Quaternion.FromToRotation(Vector3.up, _shotNormal));
            d.localScale = new Vector3(s, 0.0015f, s); // kalinlik 3 mm: yuzeye gomulu dursun
            d.gameObject.SetActive(true);
        }
    }
}
