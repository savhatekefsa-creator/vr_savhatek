using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Audio;
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
        float _lastFire = float.NegativeInfinity;

        // Burst kuyrugu (yalnizca tutan istemcide): tetik cekilince baslar, tetik birakilsa da
        // tamamlanir; silah birakilir/mermi biter/dolum baslarsa iptal olur.
        int _burstRemaining;
        float _burstNextAt;
        XRNode _burstNode = XRNode.RightHand;

        // Sunucu kadans zorlamasi: TOKEN BUCKET (fixed-window degil — pencere sinirinda 2x
        // patlama acigi olmasin). Kapasite Semi/Auto'da 1, Burst'te burstCount.
        float _srvTokens = 1f;
        float _srvLastRefill;
        float _srvLastShot = float.NegativeInfinity;

        // Sunucu ret sayaci: config-oncesi pencerede sessizce yenen atislar ve olasi kadans
        // hileleri buradan gorunur (teshis + telemetri). Log seli olmasin diye 2 sn'de bir.
        int _srvRejects;
        float _srvNextRejectLog;

        static bool IsFinite(Vector3 v)
        {
            float s = v.x + v.y + v.z;
            return !float.IsNaN(s) && !float.IsInfinity(s);
        }

        void LogReject(string reason)
        {
            _srvRejects++;
            if (Time.time < _srvNextRejectLog) return;
            _srvNextRejectLog = Time.time + 2f;
            Debug.Log($"[Silah] {name}: atis reddedildi ({reason}) — toplam ret {_srvRejects}");
        }
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

        // Effects: iz cizgileri havuzdan (pellet = ayni anda birden cok ucan iz), alev tek.
        readonly List<LineRenderer> _tracers = new List<LineRenderer>();
        Material _tracerMat;
        Light _flash;

        // Mermi izleri: silahin ALTINDA DEGIL, dunya kokunde duran bir havuz. Silahin cocugu
        // olsalardi silah her dondugunde izler de suruklenirdi. Havuz dolunca en eski iz geri
        // donusur — sinirsiz GameObject birikmez.
        const int DecalCount = 48;
        Transform _decalRoot;
        Transform[] _decals;
        int _decalNext;

        // Ucan ates izleri: her atis/pellet dunya uzayinda saklanir, her kare ilerletilir.
        // Havuz MaxTracers ile sinirli — asilirsa en eski iz devrilir (pompali seri atis).
        struct ShotFx
        {
            public Vector3 origin, dir, end, normal;
            public float dist, firedAt;
            public bool impactShown;
        }
        // Volley basina pellet tavani; iz havuzu ARDISIK volley'ler ust uste binebildigi icin
        // (otomatik ates) bunun iki kati tutulur.
        const int MaxPellets = 16;
        const int MaxTracers = MaxPellets * 2;
        readonly List<ShotFx> _flights = new List<ShotFx>();
        bool _tracersOn;
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

        /// <summary>Savas degerlerini kaynak zincirinden cozer:
        /// (1) agdan gelen sunucu kaydi (canli ayar) -> (2) yerel combat SO (sunucuda canli
        /// kaynak, istemcide gomulu yedek) -> (3) eski profil alanlari -> (4) varsayilanlar.
        /// Awake'te ve her config guncellemesinde cagrilir.</summary>
        void ResolveCombat()
        {
            // Ag kaydi anahtari = profilin kanonik adi (HK416 gibi Equals'i bos profillerde
            // Contains'e duser — config'in weaponName'i de ayni kuralla uretildi). PROFILSIZ
            // silah GameObject adiyla dener: build'inde profil olmayan eski istemci bile
            // sunucunun kadans/pellet degerlerini agdan alir (tutus gorselleri haric).
            string netKey = _profile == null ? WeaponGripBinder.CleanName(name)
                : !string.IsNullOrEmpty(_profile.weaponNameEquals) ? _profile.weaponNameEquals
                : _profile.weaponNameContains;
            if (!string.IsNullOrEmpty(netKey) && WeaponConfigRegistry.TryGet(netKey, out var netData))
                _cv = CombatValues.FromData(netData);
            else if (_profile != null && _profile.combat != null)
                _cv = CombatValues.FromConfig(_profile.combat);
            else if (_profile != null)
                _cv = CombatValues.FromLegacyProfile(_profile, fireInterval, range);
            else
                _cv = CombatValues.Defaults(fireInterval, range);
        }

        // Canli ayar: yeni set uygulaninca (istemci) ya da sunucu yayin yapinca degerler
        // yeniden cozulur; sonradan kick acilan silaha tepme bileseni de takilir. Devre
        // disiyken kacirilan yayinlar icin enable aninda bir kez yeniden cozulur.
        void OnEnable()
        {
            WeaponConfigRegistry.ConfigsUpdated += OnConfigsUpdated;
            if (_grab != null) { ResolveCombat(); AttachRecoil(); }
        }

        void OnDisable() { WeaponConfigRegistry.ConfigsUpdated -= OnConfigsUpdated; }

        void OnConfigsUpdated()
        {
            int prevMag = _cv.magazineSize;
            ResolveCombat();
            AttachRecoil();

            // Canli ayarla mermi sistemi SONRADAN acilan silah olu dogmasin: spawn'da
            // tohumlanmamis _ammo/_spares (0/0) "bos sarjor + yedek yok" kilidine dusuyordu.
            if (IsServer && IsSpawned && prevMag <= 0 && _cv.magazineSize > 0)
            {
                _ammo.Value = _cv.magazineSize;
                _spares.Value = _cv.spareMagazines;
            }
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

            // Dolum baslangic sesi RPC'siz: _reloadDoneAt 0'dan pozitife gecince HER istemcide
            // duyulur (NetworkVariable degisimi zaten herkese replike).
            _reloadDoneAt.OnValueChanged += OnReloadStateChanged;
        }

        public override void OnNetworkDespawn()
        {
            _reloadDoneAt.OnValueChanged -= OnReloadStateChanged;
            base.OnNetworkDespawn();
        }

        void OnReloadStateChanged(double prev, double now)
        {
            // 0 -> pozitif = dolum basladi. Pozitif -> 0 hem bitis hem iptal olabilir: bitis
            // sesi ReloadDoneClientRpc'den gelir, iptal (silah birakildi) sessiz kalir.
            if (prev <= 0d && now > 0d)
                WeaponAudioPlayer.PlayAt(_cv.reloadStartClip, transform.position, _cv.reloadVolume,
                    1f, 1f, _cv.soundMaxDistance, priority: true);
        }

        /// <summary>Sunucu: silahin mermi durumunu belirli bir degere kur. Silah secici bunu
        /// cantadan geri cagirirken kullanir — silah cantaya kac mermiyle girdiyse o kadarla
        /// cikmali. Yoksa galeriyi acip kapamak BEDAVA SARJOR olurdu ve savurarak dolum
        /// mekanigini kimse kullanmazdi (yedekler de sifirlanacagi icin cephane sinirsiz olurdu).
        /// Negatif deger = dokunma (spawn'daki dolu hali kalir).</summary>
        public void SetAmmoStateServer(int ammo, int spares)
        {
            if (!IsServer || !UsesAmmo) return;
            if (ammo >= 0) _ammo.Value = Mathf.Clamp(ammo, 0, MagazineSize);
            // Yedek kaynagi artik cozulmus config (_cv) — profil null olabilir (config'li ama
            // profilsiz silah) ve canli ayar da _cv'den akar. -1 (sinirsiz) ise dokunma.
            if (spares >= -1 && _cv.spareMagazines >= 0) _spares.Value = spares;
        }

        void Update()
        {
            UpdateFx(); // herkeste, silah elde olmasa da: ucan iz sahibi birakinca da tamamlanir

            // Dolumu sunucu bitirir ve sunucu silahi tutan kisi OLMAYABILIR — bu yuzden
            // asagidaki "sadece tutan oyuncu" cikislarindan ONCE isletilmeli.
            if (IsSpawned && IsServer) TickReloadServer();

            if (!IsSpawned || _grab == null || !_grab.IsHeld) { _prevTrigger = false; _bloom = 0f; _burstRemaining = 0; ResetFlick(); return; }
            if (NetworkManager == null || _grab.HolderClientId != NetworkManager.LocalClientId)
            {
                // Elden-ele geciste (birak+kap ayni tick'e sigarsa !IsHeld karesi hic gorunmez)
                // eski tutanin burst kuyrugu/bloom'u hayalet atis uretmesin.
                _burstRemaining = 0;
                _bloom = 0f;
                ResetFlick();
                return;
            }

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
            // Burst: tetik cekilisi bir kuyrugu baslatir (asagida); kuyruk bitmeden yenisi baslamaz.
            bool wantsFire = IsAuto ? trig : (trig && !_prevTrigger);
            if (_burstRemaining > 0) wantsFire = false;

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

                if (_cv.fireMode == FireMode.Burst && _cv.burstCount > 1)
                {
                    _burstRemaining = _cv.burstCount - 1;
                    _burstNextAt = Time.time + _cv.burstShotInterval;
                    _burstNode = firedNode;
                }
            }

            // Kuyruktaki burst atislari: tetik birakilsa da tamamlanir; mermi biterse ya da
            // dolum baslarsa SESSIZCE iptal (her atis icin ayri kuru-tetik tiklamasi olmasin).
            if (_burstRemaining > 0 && Time.time >= _burstNextAt)
            {
                if (UsesAmmo && (_ammo.Value <= 0 || IsReloading))
                    _burstRemaining = 0;
                else
                {
                    _burstRemaining--;
                    _burstNextAt = Time.time + _cv.burstShotInterval;
                    Fire(InputDevices.GetDeviceAtXRNode(_burstNode), _burstNode);
                    // Sunucu min-gap'i SON atistan olcer; istemci de yeni burst'u son atistan
                    // en az bir burst-ici aralik sonra baslatsin ki mesru atis reddedilmesin.
                    if (_burstRemaining == 0)
                        _nextFire = Mathf.Max(_nextFire, Time.time + _cv.burstShotInterval);
                }
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

            // Pellet: nisan-sacilimi TABAN yone bir kez uygulanir (yukarida), pelletler o
            // tabanin etrafina kendi konileriyle sacilir. Tek pellet = eski tek-ray davranisi.
            int pellets = Mathf.Clamp(_cv.pelletCount, 1, MaxPellets);
            var dirs = new Vector3[pellets];
            for (int i = 0; i < pellets; i++)
                dirs[i] = pellets == 1 ? dir : ApplySpread(dir, _cv.pelletSpreadDegrees);

            FireServerRpc(origin, dirs);

            // Ates sesi OWNER'da ANINDA calar: sunucu gidis-donusunu bekleyen FX yolundan
            // gelseydi tetik-ses gecikmesi VR'da hissedilirdi. Digerleri ShowVolley'de duyar.
            WeaponAudioPlayer.PlayAt(_cv.fireClip, origin, _cv.fireVolume,
                _cv.firePitchMin, _cv.firePitchMax, _cv.soundMaxDistance);

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
        void FireServerRpc(Vector3 origin, Vector3[] dirs, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != _grab.HolderClientId) return; // only the holder fires
            if (dirs == null || dirs.Length == 0) return;
            // NaN/Infinity filtresi: "sqrMagnitude < 0.5" NaN'da FALSE doner (NaN karsilastirmasi
            // hep false) ve bozuk vektor tum istemcilerin FX'ine yayilirdi.
            if (!IsFinite(origin)) { LogReject("bozuk origin"); return; }

            // Mermi otoritesi burada: ele gecirilmis bir istemci istedigi kadar FireServerRpc
            // cagirsin, bos sarjorle ya da dolum ortasinda atis cikmaz. Istemcideki ayni
            // kontrol sadece hisdir, guvenlik degil.
            if (UsesAmmo && (_ammo.Value <= 0 || IsReloading)) { LogReject("bos sarjor/dolum"); return; }

            // Kadansi istemciye guvenmeden sunucu zorlar: ele gecirilmis bir istemci
            // FireServerRpc'yi her karede cagirsa da uzun-vadeli atis hizi config'in uzerine
            // cikamaz. TOKEN BUCKET: kapasite Semi/Auto'da 1 (eski davranisa birebir iner),
            // Burst'te burstCount; burst ICI atislar arasina ayrica min-gap konur. %15
            // tolerans ag jitter'inda mesru atisin dusmesini onler.
            float now = Time.time;
            float cap = _cv.fireMode == FireMode.Burst ? Mathf.Max(1, _cv.burstCount) : 1f;
            float refill = cap / Mathf.Max(0.005f, _cv.fireInterval * 0.85f);
            _srvTokens = Mathf.Min(cap, _srvTokens + (now - _srvLastRefill) * refill);
            _srvLastRefill = now;
            float minGap = _cv.fireMode == FireMode.Burst ? _cv.burstShotInterval * 0.85f : 0f;
            if (_srvTokens < 1f || now - _srvLastShot < minGap) { LogReject("kadans"); return; }
            _srvTokens -= 1f;
            _srvLastShot = now;

            // Kadans kapisini gectik = atis GERCEKTEN cikiyor; mermi tetik basina BIR duser
            // (pellet sayisi kac olursa olsun).
            if (UsesAmmo) _ammo.Value--;

            // Pellet sayisi GERCEKTEN sunucu-otoriter: fazla yon KIRPILIR (500-yon saldirisi
            // imkansiz), EKSIK yon SUNUCUDA tamamlanir — bayat config'li istemci tek yon
            // gonderse bile sacma sayisi configteki kadar cikar (canli pellet ayari yeni
            // kulaklik build'i istemez; FX yayini herkese 7 izi birlikte tasir).
            int pellets = Mathf.Clamp(_cv.pelletCount, 1, MaxPellets);
            if (dirs.Length < pellets)
            {
                Vector3 baseDir = Vector3.zero;
                foreach (var d0 in dirs)
                    if (IsFinite(d0) && d0.sqrMagnitude >= 0.5f) { baseDir = d0.normalized; break; }
                if (baseDir == Vector3.zero)
                    baseDir = (transform.rotation * _barrelLocal).normalized;
                var padded = new Vector3[pellets];
                for (int i = 0; i < pellets; i++)
                    padded[i] = i < dirs.Length ? dirs[i] : ApplySpread(baseDir, _cv.pelletSpreadDegrees);
                dirs = padded;
            }
            ulong shooter = _grab.HolderClientId;
            byte shooterTeam = TeamOf(shooter);

            // GOZLEM (simdilik LOG-ONLY): origin sunucudaki namlu ucundan cok uzaksa ya da yon
            // sunucudaki namlu ekseninden cok sapmissa kaydet. VR bilek flikleri +
            // ClientNetworkTransform gecikmesi MESRU sapma uretir — esikler once Quest verisiyle
            // olculur, ret kapisina ancak ondan sonra cevrilir.
            {
                Vector3 srvOrigin = muzzle != null ? muzzle.position : transform.TransformPoint(_muzzleLocal);
                Vector3 srvBarrelLocal = _profile != null && _profile.barrelLocalDirection.sqrMagnitude > 1e-6f
                    ? _profile.barrelLocalDirection.normalized
                    : _barrelLocal;
                Vector3 srvBarrel = (transform.rotation * srvBarrelLocal).normalized;
                Vector3 obsDir = dirs[0].sqrMagnitude > 0.5f ? dirs[0].normalized : srvBarrel;
                float originDist = Vector3.Distance(origin, srvOrigin);
                float aimDelta = Vector3.Angle(srvBarrel, obsDir);
                if (originDist > 0.5f || aimDelta > _cv.spreadMax + _cv.pelletSpreadDegrees + 25f)
                    Debug.Log($"[Silah][gozlem] {name}: origin sapmasi {originDist:0.00}m, aci sapmasi {aimDelta:0.0} derece (holder {shooter})");
            }

            var ends = new Vector3[pellets];
            var normals = new Vector3[pellets];
            int hitboxesSeen = 0;
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = dirs[i];
                if (!IsFinite(dir) || dir.sqrMagnitude < 0.5f) { ends[i] = origin; continue; }
                dir.Normalize();
                hitboxesSeen += RaycastOne(origin, dir, shooter, shooterTeam,
                    out ends[i], out normals[i]);
            }

            if (hitboxesSeen == 0)
                Debug.Log($"[Silah] Ates edildi ama HIC OYUNCU HITBOX'ina denk gelmedi ({pellets} pellet).");

            FireFxClientRpc(origin, ends, normals);
        }

        /// <summary>Tek bir rayin otoriter isabet cozumu (pellet basina bir kez cagrilir).
        /// Donus: bu rayin gordugu dusman hitbox sayisi (teshis logu icin). Hasar pellet
        /// basina pelletDamageScale ile carpilir — pompalida tanesi zayif, hepsi birden olumcul.</summary>
        int RaycastOne(Vector3 origin, Vector3 dir, ulong shooter, byte shooterTeam,
            out Vector3 end, out Vector3 hitNormal)
        {
            end = origin + dir * _cv.range;
            // Sifir = mermi izi birakma. YALNIZCA sabit geometri normal doner: hareketli bir
            // oyuncuya dunya-uzayi izi cakarsak oyuncu yurudugunde iz havada asili kalirdi.
            hitNormal = Vector3.zero;

            var hits = Physics.RaycastAll(origin + dir * 0.03f, dir, _cv.range,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            int hitboxesSeen = 0;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue; // own weapon

                // Regional damage: the ray hits a HitZone (head/torso/arm/leg); the per-region
                // amount is resolved on the SERVER (clients can't send damage values — security).
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
                    int dmg = Mathf.Max(1, Mathf.RoundToInt(DamageFor(zone.zoneType) * _cv.pelletDamageScale));
                    Debug.Log($"[Silah] ISABET! atan {shooter} (takim {shooterTeam}) -> hedef {health.OwnerClientId} (takim {t}), bolge {zone.zoneName}, {dmg} hasar. Kalan: {Mathf.Max(0, health.Health.Value - dmg)}");
                    health.ServerApplyDamage(dmg, shooter);
                    end = h.point; break;
                }

                end = h.point; // first solid/non-player hit stops the ray
                hitNormal = h.normal;
                break;
            }
            return hitboxesSeen;
        }

        /// <summary>Bolge hasari, SILAH-basina: config alani doluysa (>0) o deger, degilse
        /// CombatConfig'in global bolge varsayilani. Fallback ALAN-BASINA bilincli — yalnizca
        /// kafa hasari doldurulmus bir config diger bolgeleri sifirlamasin. Sunucuda cozulur.</summary>
        int DamageFor(ZoneType zone)
        {
            int global = CombatConfig.Instance.DamageFor(zone);
            switch (zone)
            {
                case ZoneType.Head:  return _cv.headDamage  > 0 ? _cv.headDamage  : global;
                case ZoneType.Torso: return _cv.torsoDamage > 0 ? _cv.torsoDamage : global;
                case ZoneType.Arm:   return _cv.armDamage   > 0 ? _cv.armDamage   : global;
                case ZoneType.Leg:   return _cv.legDamage   > 0 ? _cv.legDamage   : global;
                default:             return global;
            }
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
        void FireFxClientRpc(Vector3 origin, Vector3[] ends, Vector3[] normals)
        {
            ShowVolley(origin, ends, normals);
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
            // Dolum bitis sesi HERKESTE (tutan filtresinden ONCE); titresim yalniz tutana.
            WeaponAudioPlayer.PlayAt(_cv.reloadEndClip, transform.position, _cv.reloadVolume,
                1f, 1f, _cv.soundMaxDistance, priority: true);

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

        // Kuru tetik: kisa/zayif titresim + (varsa) bos-tetik sesi. v1'de yalniz TUTAN duyar —
        // bos tetik tutanin geri bildirimidir, RPC maliyeti gerektirmez.
        void DryFire(InputDevice dev)
        {
            if (dev.isValid) dev.SendHapticImpulse(0, 0.25f, 0.03f);
            Vector3 pos = muzzle != null ? muzzle.position : transform.position;
            WeaponAudioPlayer.PlayAt(_cv.dryFireClip, pos, _cv.dryFireVolume,
                1f, 1f, _cv.soundMaxDistance);
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
            _tracerMat = new Material(FindShaderSafe());
            Color tc = TracerColor;
            if (_tracerMat.HasProperty("_BaseColor")) _tracerMat.SetColor("_BaseColor", tc);
            else _tracerMat.color = tc;
            EnsureTracers(1); // ilk iz hazir; pellet gelirse havuz lazily buyur

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

        LineRenderer NewTracer()
        {
            var go = new GameObject("Tracer");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.widthMultiplier = TracerWidth;
            lr.material = _tracerMat;
            lr.enabled = false;
            return lr;
        }

        void EnsureTracers(int n)
        {
            n = Mathf.Min(n, MaxTracers);
            while (_tracers.Count < n) _tracers.Add(NewTracer());
        }

        void ShowVolley(Vector3 origin, Vector3[] ends, Vector3[] normals)
        {
            if (ends == null || ends.Length == 0) return;

            for (int i = 0; i < ends.Length; i++)
            {
                Vector3 d = ends[i] - origin;
                float dist = d.magnitude;
                var f = new ShotFx
                {
                    origin = origin,
                    end = ends[i],
                    normal = normals != null && i < normals.Length ? normals[i] : Vector3.zero,
                    dir = dist > 1e-4f ? d / dist : transform.forward,
                    dist = dist,
                    firedAt = Time.time,
                };
                if (_flights.Count >= MaxTracers) _flights.RemoveAt(0);
                _flights.Add(f);
            }

            // Ates sesi: tutan oyuncu KENDI atisini Fire()'da anında duydu — burada yalnizca
            // DIGERLERI duyar (cift ses olmasin). Pellet sayisi kac olursa olsun TEK ses.
            bool localHolderHere = _grab != null && _grab.IsHeld && NetworkManager != null &&
                _grab.HolderClientId == NetworkManager.LocalClientId;
            if (!localHolderHere)
                WeaponAudioPlayer.PlayAt(_cv.fireClip, origin, _cv.fireVolume,
                    _cv.firePitchMin, _cv.firePitchMax, _cv.soundMaxDistance);

            // Namlu alevi: pellet sayisi kac olursa olsun TEK parlama. Alev silahin cocugu:
            // dunya noktasi bir kez yazilir, sonra silahla birlikte hareket eder.
            if (_flash != null)
            {
                _flash.transform.position = origin;
                _flash.enabled = true;
                _flashOffAt = Time.time + FlashDuration;
            }

            UpdateFx(); // ilk kareyi hemen ciz: bir kare gecikmeyle baslamasin
        }

        // Izleri namludan hedefe dogru UCURUR (pellet basina bir iz). Eskiden tam boy cizgi
        // aninda cizilip 70 ms duruyordu: silahi cevirirken donuk cizgi namludan kopuk kaliyor
        // ve atis sapmis gibi gorunuyordu.
        void UpdateFx()
        {
            if (_flashOffAt > 0f && Time.time > _flashOffAt)
            {
                _flashOffAt = -1f;
                if (_flash != null) _flash.enabled = false;
            }

            if (_flights.Count == 0)
            {
                if (_tracersOn)
                {
                    for (int i = 0; i < _tracers.Count; i++) _tracers[i].enabled = false;
                    _tracersOn = false;
                }
                return;
            }
            _tracersOn = true;

            float speed = TracerSpeed;
            float len = Mathf.Max(0.1f, TracerLength);

            // GECIS 1 — ilerlet/temizle: biten ucuslar listeden cikar, varista kivilcim.
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                bool done;
                if (speed <= 0f)
                {
                    // Hiz 0 = eski davranis: aninda tam boy cizgi, ~70 ms sonra soner.
                    if (!f.impactShown) { ShowImpact(f.end, f.normal); f.impactShown = true; }
                    done = Time.time - f.firedAt > 0.07f;
                }
                else
                {
                    float travelled = (Time.time - f.firedAt) * speed;
                    // Kivilcim izin ucu hedefe VARDIGINDA parlar, atisla ayni anda degil.
                    if (travelled >= f.dist && !f.impactShown)
                    {
                        ShowImpact(f.end, f.normal);
                        f.impactShown = true;
                    }
                    done = travelled - len >= f.dist;
                }
                if (done) _flights.RemoveAt(i);
                else _flights[i] = f;
            }

            // GECIS 2 — ciz: kalan ucuslar temiz index eslesmesiyle havuza yazilir (silme
            // sonrasi ayni karede cizim yapildigi icin 1-karelik iz kaymasi olmaz).
            EnsureTracers(_flights.Count);
            int drawn = Mathf.Min(_flights.Count, _tracers.Count);
            for (int i = 0; i < drawn; i++)
            {
                var f = _flights[i];
                var t = _tracers[i];
                if (speed <= 0f)
                {
                    t.SetPosition(0, f.origin);
                    t.SetPosition(1, f.end);
                }
                else
                {
                    float travelled = (Time.time - f.firedAt) * speed;
                    float head = Mathf.Min(travelled, f.dist);
                    float tail = Mathf.Max(0f, travelled - len);
                    t.SetPosition(0, f.origin + f.dir * tail);
                    t.SetPosition(1, f.origin + f.dir * head);
                }
                t.enabled = true;
            }
            for (int i = drawn; i < _tracers.Count; i++)
                _tracers[i].enabled = false;
        }

        // Carptigi yerde KALICI bir iz birakir. Havuzdaki en eski iz geri donusur.
        void ShowImpact(Vector3 end, Vector3 normal)
        {
            // Normal sifir = hicbir seye carpmadi ya da bir OYUNCUYA carpti: iz birakma.
            // Yuruyen bir oyuncuya cakilan dunya-uzayi izi havada asili kalirdi.
            if (_decals == null || normal.sqrMagnitude < 0.5f) return;

            var d = _decals[_decalNext];
            _decalNext = (_decalNext + 1) % _decals.Length;

            // Silindirin ekseni LOKAL Y; onu yuzey normaline hizala. Olcek: mesh yaricapi 0.5
            // (yani cap = scale.x) ve yuksekligi 2 (yani kalinlik = 2 * scale.y).
            float s = ImpactSize;
            d.SetPositionAndRotation(end + normal * 0.001f,
                Quaternion.FromToRotation(Vector3.up, normal));
            d.localScale = new Vector3(s, 0.0015f, s); // kalinlik 3 mm: yuzeye gomulu dursun
            d.gameObject.SetActive(true);
        }
    }
}
