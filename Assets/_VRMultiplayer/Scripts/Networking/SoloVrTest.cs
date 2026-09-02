#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// EDITORE OZEL tek kisilik test yolu: gozlugu Link'le takip PC ekranindan KENDI
    /// avatarina disaridan bakmak icin. Build'e girmez (#if UNITY_EDITOR).
    ///
    /// NEDEN GEREKLI: LanBootstrap PC'yi ADANMIS SUNUCU sayiyor ve StartServer() cagiriyor
    /// — sunucunun kendisi istemci degil, dolayisiyla PC'ye avatar DOGMUYOR. Kol/dirsek/silah
    /// tutusuna bakmak icin ise ortada bir avatar olmasi sart. Burasi StartHost() kullanir:
    /// ayni surec hem sunucu hem istemci olur, PlayerPrefab spawn edilir ve avatari Link'ten
    /// gelen gercek takip surer.
    ///
    /// EKRAN: XR acikken Game view normalde gozlugun aynasini gosterir. gameViewRenderMode
    /// None yapilinca o ayna kapanir ve stereoTargetEye=None olan seyirci kamerasi PC
    /// ekranina cizer. GOZLUK ETKILENMEZ — ayna yalnizca monitor icindir.
    ///
    /// Kullanim: Play'e bas, PC panelindeki "SOLO TEST (host)" dugmesine bas.
    /// Kamera: sag fare basili = bak, WASD = gez, Q/E = alcal/yuksel, Shift = hizli,
    /// F = avatari yeniden merkeze al, T = omuz ustu / serbest kamera.
    /// </summary>
    public class SoloVrTest : MonoBehaviour
    {
        [Tooltip("Avatara bakarken kameranin ondan uzakligi (metre).")]
        public float orbitDistance = 2.5f;
        [Tooltip("Serbest gezinme hizi (m/s).")]
        public float flySpeed = 3f;
        public float fastMultiplier = 3f;
        public float lookSensitivity = 0.15f;

        [Tooltip("Kayitli TUM silahlar icin oyuncunun etrafinda yuvalar hazirlanir. Silahlar " +
                 "yalnizca paneldeki dugmeye basinca dogar — otomatik yenileme YOK " +
                 "(bkz. FillRack: kacak spawn dongusu kurulamasin diye).")]
        public bool spawnWeaponRack = true;
        [Tooltip("Silah cemberinin yaricapi (metre). Play sirasinda ekrandaki kaydiricidan " +
                 "CANLI degistirilebilir — silahlar aninda yeni cembere kayar.")]
        public float rackRadius = 1.2f;
        [Tooltip("Silahlarin yerden yuksekligi (metre). Otururken test ederken dusur.")]
        public float rackHeight = 0.9f;

        [Tooltip("OTURMA TELAFISI (metre): XR rig'i bu kadar YUKARI kaydirir, yani oyun seni " +
                 "ayakta sanir. " +
                 "NEDEN: boy kalibrasyonu avatari kafa yuksekligine gore olcekliyor ve carpani " +
                 "0.85'te kirpiyor. Otururken kafan ~0.90 m'de kaliyor; avatar daha fazla " +
                 "kuculemedigi icin AYAKTA ~1.4 m'lik bir figur oluyor ve ellerin ona gore " +
                 "alcakta kaliyor — kolu kaldirinca yeterince yukselmis gorunmuyor. Rig'i " +
                 "yukari kaydirmak kafayi da ellerini de birlikte tasidigi icin oranlar duzelir.")]
        public float seatedRise = 0f;

        [Tooltip("'Oturmayi telafi et' dugmesinin hedefledigi ayakta kafa yuksekligi (metre).")]
        public float standingHeadHeight = 1.65f;

        [Tooltip("Solo testte AvatarIKController'in takip kapisi bu esige dusurulur. Oturarak " +
                 "test ederken kafa ~0.90 m'de kalir ve varsayilan 1.00 m esik kapiyi hic " +
                 "acmaz — avatar donar. PREFAB DEGERI DEGISMEZ, yalnizca bu oturum.")]
        public float seatedGateHeight = 0.3f;

        [Tooltip("Govde temizligini KENDI avatarinda da gosterir (dis gorunus dogrulamasi). " +
                 "ACIKKEN silah GOZLUKTE de kayar - tek silah var, iki goruntu ayni silahi " +
                 "ciziyor. His testi yaparken ekrandaki kutudan kapat.")]
        public bool showBodyClearance = true;

        Camera _cam;
        Transform _target;         // yerel oyuncunun kafasi
        bool _bodyShown;           // tam govde bir kez geri acilir

        // Silah rafi: her yuva bir prefab + o yuvadaki mevcut ornek.
        readonly List<GameObject> _rackPrefabs = new List<GameObject>();
        readonly List<Vector3> _rackSlots = new List<Vector3>();
        readonly List<GrabbableObject> _rackItems = new List<GrabbableObject>();
        float _nextRackCheck;
        Vector3 _rackCenter;
        float _lastRadius, _lastHeight;

        // KACAK SPAWN'A KARSI SERT TAVAN. Yenileme artik olay tabanli (yoklama yok), ama
        // beklenmedik bir durumda bile toplam dogum bu sayiyi asamaz — sessizce performans
        // yemek yerine acikca susar. Onceki yoklamali surum saniyede 36 nesne dogurmustu.
        int _totalSpawned;
        int _spawnCap;
        bool _follow = true;       // true = avatari cerceveler, false = serbest ucus
        float _yaw, _pitch = 10f;
        float _nextScan;

        // AYARLAR OTURUMLAR ARASI KALICI. Bu bilesen Play'de runtime'da dogduğu icin
        // Play'den ONCE Inspector'da gorunmuyor; her seferinde yaricapi yeniden ayarlamak
        // zorunda kalmayasin diye degerler EditorPrefs'e yaziliyor. Proje ayari degil,
        // MAKINE ayari: ekip arkadasinin degerlerini bozmaz.
        const string PrefKey = "SoloVrTest.";

        void LoadPrefs()
        {
            rackRadius         = UnityEditor.EditorPrefs.GetFloat(PrefKey + "radius", rackRadius);
            rackHeight         = UnityEditor.EditorPrefs.GetFloat(PrefKey + "height", rackHeight);
            seatedRise         = UnityEditor.EditorPrefs.GetFloat(PrefKey + "rise", seatedRise);
            standingHeadHeight = UnityEditor.EditorPrefs.GetFloat(PrefKey + "standH", standingHeadHeight);
            seatedGateHeight   = UnityEditor.EditorPrefs.GetFloat(PrefKey + "gate", seatedGateHeight);
            orbitDistance      = UnityEditor.EditorPrefs.GetFloat(PrefKey + "orbit", orbitDistance);
            showBodyClearance  = UnityEditor.EditorPrefs.GetBool(PrefKey + "clear", showBodyClearance);
            WeaponHandWeld.ForceClearanceLocal = showBodyClearance;
        }

        void SavePrefs()
        {
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "radius", rackRadius);
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "height", rackHeight);
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "rise", seatedRise);
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "standH", standingHeadHeight);
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "gate", seatedGateHeight);
            UnityEditor.EditorPrefs.SetFloat(PrefKey + "orbit", orbitDistance);
            UnityEditor.EditorPrefs.SetBool(PrefKey + "clear", showBodyClearance);
        }

        /// <summary>Host olarak baslat: sunucu + istemci ayni surecte, avatar spawn olur.</summary>
        public void StartSolo()
        {
            LoadPrefs();

            var nm = NetworkManager.Singleton;
            if (nm == null) { Debug.LogError("[SoloVrTest] NetworkManager yok."); return; }
            if (nm.IsListening) { Debug.LogWarning("[SoloVrTest] Oturum zaten acik."); return; }

            if (!nm.StartHost())
            {
                Debug.LogError("[SoloVrTest] StartHost basarisiz — port dolu olabilir.");
                return;
            }
            Debug.Log("[SoloVrTest] Host baslatildi. Avatar spawn olunca kamera devreye girer.");
            SetupCamera();
        }

        void SetupCamera()
        {
            // Game view'in gozluk aynasini kapat ki seyirci kamerasi monitore cizebilsin.
            // Gozluge giden goruntu bundan ETKILENMEZ.
            XRSettings.gameViewRenderMode = GameViewRenderMode.None;

            var go = new GameObject("~SoloTestKamera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.stereoTargetEye = StereoTargetEyeMask.None;   // XR'a degil MONITORE ciz
            _cam.targetDisplay = 0;
            _cam.depth = 100f;                                  // XR kamerasinin ustunde
            _cam.clearFlags = CameraClearFlags.Skybox;
            // AudioListener EKLENMEZ: sahnede zaten XR rig'inki var, ikincisi Unity'nin
            // "birden fazla AudioListener" uyarisini basar ve sesi bozar.

            _cam.transform.position = transform.position + Vector3.up * 1.5f + Vector3.back * orbitDistance;
        }

        void Update()
        {
            if (_cam == null) return;

            LogDiagnostics();

            // Yerel avatarin kafasini bul (spawn birkac kare surebilir).
            if (_target == null && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 0.5f;
                FindLocalHead();
            }

            // Kaydiricilar oynadiysa cemberi aninda yeniden diz.
            if (!Mathf.Approximately(_lastRadius, rackRadius) ||
                !Mathf.Approximately(_lastHeight, rackHeight))
            {
                LayoutSlots();
                SavePrefs();       // bir dahaki oturumda ayni degerle acilsin
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.tKey.wasPressedThisFrame) _follow = !_follow;
            if (kb.fKey.wasPressedThisFrame) _target = null;   // yeniden ara

            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -85f, 85f);
            }

            if (_follow && _target != null)
            {
                // Avatari cerceve: hedefin etrafinda yaw/pitch ile yorunge.
                var rot = Quaternion.Euler(_pitch, _yaw, 0f);
                _cam.transform.position = _target.position + rot * (Vector3.back * orbitDistance);
                _cam.transform.LookAt(_target.position);
                return;
            }

            // Serbest ucus
            _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            float sp = flySpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f) * Time.unscaledDeltaTime;
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move += _cam.transform.forward;
            if (kb.sKey.isPressed) move -= _cam.transform.forward;
            if (kb.dKey.isPressed) move += _cam.transform.right;
            if (kb.aKey.isPressed) move -= _cam.transform.right;
            if (kb.eKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed) move -= Vector3.up;
            if (move.sqrMagnitude > 1e-6f) _cam.transform.position += move.normalized * sp;
        }

        /// <summary>Yerel oyuncunun kafa kemigini bulur — kamera onu cerceveler.</summary>
        void FindLocalHead()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient) return;

            // YEREL OYUNCUYU NETCODE'DAN AL, PlayerIdentity TARAYARAK DEGIL.
            // Onceki surum sahnedeki PlayerIdentity'leri tarayip IsOwner arıyordu ve hicbir
            // zaman bulamiyordu — dolayisiyla ShowFullBody (ve icindeki oturma kapisi
            // duzeltmesi) HIC calismadi. LocalClient.PlayerObject kanonik kaynak: sunucunun
            // bu istemci icin spawn ettigi nesnenin ta kendisi.
            GameObject root = null;
            if (nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
                root = nm.LocalClient.PlayerObject.gameObject;

            if (root == null)
            {
                // Yedek: sahibi olan NetworkVRPlayer (teshiste calistigi kanitlanan yol).
                foreach (var p in FindObjectsByType<NetworkVRPlayer>(FindObjectsSortMode.None))
                    if (p.IsOwner) { root = p.gameObject; break; }
            }
            if (root == null) return;

            var anim = root.GetComponentInChildren<Animator>(true);
            if (anim != null && anim.isHuman)
            {
                var head = anim.GetBoneTransform(HumanBodyBones.Head);
                _target = head != null ? head : root.transform;
            }
            else _target = root.transform;

            ShowFullBody(root);
            BuildRack(root.transform.position);
            Debug.Log("[SoloVrTest] Yerel avatar bulundu, kamera kilitlendi.");
        }

        /// <summary>Yerel avatarin TAM govdesini geri acar.
        ///
        /// NEDEN GEREKLI: NetworkVRPlayer, sahibi olan avatarda birinci sahis icin FP_Hands
        /// modelini takip DIGER TUM renderer'lari kapatiyor (kafan/govden kamerani kapatmasin
        /// diye — dogru davranis). Ama biz avatara DISARIDAN bakiyoruz, o yuzden geriye
        /// yalnizca eller kaliyordu. Burada hepsini geri aciyor, FP ellerini kapatiyoruz
        /// (ikisi ust uste binerdi). Yalnizca solo testte, yalnizca editorde.</summary>
        void ShowFullBody(GameObject playerRoot)
        {
            if (_bodyShown || playerRoot == null) return;

            int shown = 0;
            foreach (var r in playerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                bool isFp = r.transform.root != null && IsUnder(r.transform, "FP_Hands");
                r.enabled = !isFp;      // govde acilir, FP eller kapanir
                if (!isFp) shown++;
            }

            // Kendi isim etiketini de goster (disaridan bakarken faydali).
            foreach (var tm in playerRoot.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            {
                var mr = tm.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = true;
            }

            // hideHead kemik hilesi de geri alinmali, yoksa kafa 0.001 olcekte kalir.
            var ik = playerRoot.GetComponentInChildren<AvatarIKController>();
            if (ik != null && ik.hideHead)
            {
                ik.hideHead = false;
                if (ik.headBone != null) ik.headBone.localScale = Vector3.one;
            }

            // OTURARAK TEST: AvatarIKController'in takip kapisi kafanin groundY'den en az
            // trackingReadyMinHeight (varsayilan 1.00 m) yukarida olmasini bekliyor. Otururken
            // kafa ~0.90 m'de kaliyor, kapi hic acilmiyor ve avatar DONUYOR — sahada tam boyle
            // goruldu (rigHeadY=0.92).
            //
            // Esik URETIMDE dogru bir koruma: masada duran gozlugu oyuncu sanip avatari havaya
            // firlatmayi onluyor. O yuzden PREFAB DEGERINE DOKUNULMUYOR; yalnizca bu editore
            // ozel solo test oturumunda, yalnizca YEREL avatarda esnetiliyor.
            var ik2 = playerRoot.GetComponentInChildren<AvatarIKController>(true);
            if (ik2 != null && ik2.trackingReadyMinHeight > seatedGateHeight)
            {
                Debug.Log($"[SoloVrTest] Takip kapisi esigi {ik2.trackingReadyMinHeight:0.00} -> " +
                          $"{seatedGateHeight:0.00} (oturarak test icin, yalnizca bu oturumda).");
                ik2.trackingReadyMinHeight = seatedGateHeight;
            }

            _bodyShown = true;
            Debug.Log($"[SoloVrTest] Tam govde acildi ({shown} renderer).");
        }

        static bool IsUnder(Transform t, string ancestorName)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == ancestorName) return true;
            return false;
        }

        /// <summary>Kayitli tum silahlari oyuncunun etrafinda bir cembere dizer. Sunucu
        /// otoriter spawn (host oldugumuz icin buradan yapilabilir); tutus profilini binder
        /// GrabbableObject.AnySpawned uzerinden kendi bagliyor, silaha ozel kod gerekmez.</summary>
        void BuildRack(Vector3 center)
        {
            // Yuvalar avatar bulununca kendiliginden hazirlanir — yaricap kaydiricisi hemen
            // kullanilabilir olsun diye. SILAH DOGURMAZ: doldurma yalnizca dugmeyle
            // (bkz. FillRack), boylece kacak spawn dongusu kurulamaz.
            if (_rackSlots.Count > 0) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var prefabs = WeaponPrefabRegistrar.Prefabs;
            if (prefabs == null || prefabs.Count == 0)
            {
                Debug.LogWarning("[SoloVrTest] Kayitli silah prefabi yok — raf kurulmadi.");
                return;
            }

            _rackCenter = center;
            _spawnCap = prefabs.Count * 6;   // ~6 tur yenileme; asilirsa bir yerde hata var
            for (int i = 0; i < prefabs.Count; i++)
            {
                _rackPrefabs.Add(prefabs[i]);
                _rackSlots.Add(Vector3.zero);
                _rackItems.Add(null);
            }
            LayoutSlots();

            // TEK SEFERLIK OTOMATIK DOLDURMA. Tehlikeli olan sey "otomatik dogurmak" degildi,
            // TEKRARLAYAN TICK'ti: her yarim saniyede yuvalari tarayan surum, yuva bir sekilde
            // bos gorununce saniyede 36 NetworkObject doguran bir kacak dongu kuruyordu.
            // Burada dongu yok — BuildRack zaten oturum basina bir kez calisir.
            FillRack();
        }

        /// <summary>Yuva konumlarini merkez/yaricap/yukseklikten YENIDEN hesaplar ve
        /// yuvadaki (elde OLMAYAN) silahlari oraya tasir. Kaydirici oynatilinca cember
        /// aninda daralir/genisler — otururken silahlarin elinin altina gelmesi icin.</summary>
        void LayoutSlots()
        {
            int n = _rackSlots.Count;
            if (n == 0) return;

            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f;
                var pos = _rackCenter + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rackRadius;
                pos.y = _rackCenter.y + rackHeight;
                _rackSlots[i] = pos;

                // ELDEKI silahi TASIMA: oyuncunun tuttugu seyi yerinden oynatmak tutusu bozar.
                var it = _rackItems[i];
                if (it != null && !it.IsHeld) it.transform.position = pos;
            }
            _lastRadius = rackRadius;
            _lastHeight = rackHeight;
        }

        /// <summary>Cemberi oyuncunun SU ANKI yerine tasir — oturunca ya da yer degistirince.</summary>
        void RecenterRack()
        {
            if (_target == null || _rackSlots.Count == 0) return;
            var c = _target.position;
            c.y -= 0.4f;                 // kafadan govde hizasina in
            _rackCenter = c;
            LayoutSlots();
        }

        /// <summary>Yuvalari BIR KEZ doldurur — dugmeyle cagrilir, kendi kendine DEGIL.
        ///
        /// OTOMATIK YENILEME KALDIRILDI. Onceki surum her yarim saniyede yuvalari tarayip
        /// bos olani dolduruyordu; yuvanin "dolu" sayilmasi tek bir referansa bagliydi ve o
        /// referans beklenmedik sekilde bosalinca saniyede 36 NetworkObject doguran bir KACAK
        /// DONGU olusuyordu. Kare hizi cokuyor, girdi olu gibi hissediliyor, avatar donuyordu —
        /// sahada iki kez boyle goruldu. Bir test kolayligi ugruna bu risk tasinmaz: artik
        /// doldurma yalnizca ELLE, tek seferlik. Dongu kurulamaz.</summary>
        void FillRack()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _rackSlots.Count == 0) return;

            int made = 0;
            for (int i = 0; i < _rackSlots.Count; i++)
            {
                var cur = _rackItems[i];
                if (cur != null && !cur.IsHeld) continue;   // yuva zaten dolu ve serbest

                var go = Instantiate(_rackPrefabs[i], _rackSlots[i], Quaternion.identity);
                var no = go.GetComponentInChildren<NetworkObject>(true);
                if (no != null) no.Spawn();

                // GrabbableObject prefabin KOKUNDE degil, alt objesinde duruyor.
                var grab = go.GetComponentInChildren<GrabbableObject>(true);
                _rackItems[i] = grab;
                _totalSpawned++;
                made++;

                if (grab != null) Watch(i, grab);
            }
            Debug.Log($"[SoloVrTest] Silah rafi dolduruldu: {made} silah.");
        }

        /// <summary>Tek bir XR node'unun canli durumu: cihaz gecerli mi, adi ne, tetik/kavrama
        /// degerleri geliyor mu. Gecerli DEGIL ise sorun oyunda degil — kumanda uyanik degil,
        /// eslesmemis ya da OpenXR profili baglanmamis demektir. Oyunun tamami ayni eski
        /// UnityEngine.XR.InputDevices API'sini kullaniyor (XRDevicePoseDriver, HandGrabber),
        /// yani burada gordugun sey oyunun gordugu seyin ta kendisi.</summary>
        static void DeviceLine(string label, UnityEngine.XR.XRNode node)
        {
            var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            if (!d.isValid)
            {
                GUILayout.Label(label + ": CIHAZ YOK");
                return;
            }

            string s = label + ": " + (string.IsNullOrEmpty(d.name) ? "(adsiz)" : d.name);

            if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float trig))
                s += $"  tetik {trig:0.00}";
            if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float grip))
                s += $"  kavrama {grip:0.00}";
            if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 p))
                s += $"  poz {p.x:0.0},{p.y:0.0},{p.z:0.0}";

            GUILayout.Label(s);
        }

        /// <summary>Avatar donuk kaldiginda tam teshisi KONSOLA basar (2 sn'de bir).
        /// Panelden okuyup aktarmak yerine Editor.log'a dusuyor — zincirin hangi halkasinin
        /// kopuk oldugu tek satirda gorunsun.</summary>
        void LogDiagnostics()
        {
            if (Time.time < _nextDiag) return;
            _nextDiag = Time.time + 2f;

            var nm = NetworkManager.Singleton;
            var rig = XRRigReference.Instance;

            string s = "[SoloTani] ";
            s += "host=" + (nm != null && nm.IsHost);
            s += " client=" + (nm != null && nm.IsClient);
            s += " | rig=" + (rig != null);
            if (rig != null)
                s += " rigHead=" + (rig.head != null) + " rigL=" + (rig.leftHand != null) + " rigR=" + (rig.rightHand != null);

            if (rig != null && rig.head != null)
                s += $" rigHeadY={rig.head.position.y:0.00}";

            // SAHNE GENELINDE ara: _target'a bagli arama, avatar henuz bulunmadiysa
            // yanlislikla "bilesen yok" diye okunuyordu.
            NetworkVRPlayer nvp = null;
            foreach (var p in FindObjectsByType<NetworkVRPlayer>(FindObjectsSortMode.None))
                if (p.IsOwner) { nvp = p; break; }
            s += " | nvp=" + (nvp != null);
            if (nvp != null) s += " owner=" + nvp.IsOwner + " bound=" + nvp.TrackingBound;

            AvatarIKController ikc = null;
            if (nvp != null) ikc = nvp.GetComponentInChildren<AvatarIKController>(true);
            s += " | ik=" + (ikc != null);
            if (ikc != null)
            {
                s += " gate=" + ikc.TrackingValid;
                if (ikc.headSource != null) s += $" srcHeadY={ikc.headSource.position.y:0.00}";
                s += $" esik={ikc.trackingReadyMinHeight:0.00}";
            }

            Debug.Log(s);
        }

        float _nextDiag;

        /// <summary>Oturma telafisini XR rig'e uygular ve boy kalibrasyonunu YENILER.
        /// Rig'i yukari kaydirmak kafayi VE elleri birlikte tasir, dolayisiyla vucut oranlari
        /// bozulmaz — oyun seni ayakta sanar. Kalibrasyon yenilenmezse eski (oturma) olcegi
        /// kilitli kalir ve telafi ise yaramaz.</summary>
        void ApplySeatedRise(float rise)
        {
            var rig = XRRigReference.Instance;
            if (rig == null) return;

            var p = rig.transform.position;
            p.y += rise - _appliedRise;
            rig.transform.position = p;
            _appliedRise = rise;
            seatedRise = rise;

            AvatarIKController.RecalibrateAll();
            SavePrefs();
            Debug.Log($"[SoloVrTest] Oturma telafisi {rise:0.00} m uygulandi, boy kalibrasyonu yenilendi.");
        }

        float _appliedRise;

        /// <summary>Bir yuvadaki silahi izler: ele gecince yerine BIR tane dogurur ve
        /// abonelikten cikar.
        ///
        /// OLAY TABANLI, YOKLAMA DEGIL. Onceki surum her yarim saniyede yuvalari tarayip bos
        /// olani dolduruyordu; "bos" tespiti bir kez yanilinca saniyede 36 NetworkObject
        /// doguran kacak dongu kuruluyordu. Burada her silah yalnizca BIR kez tetikler
        /// (tetikledikten sonra abonelik kesilir), dolayisiyla dongu kurulamaz.</summary>
        void Watch(int slot, GrabbableObject grab)
        {
            System.Action handler = null;
            handler = () =>
            {
                if (grab == null || !grab.IsHeld) return;   // birakma/durum degisimi: ilgilenme
                grab.StateDirty -= handler;                 // BIR KEZ: dongu kurulamasin

                if (_rackSlots.Count <= slot) return;
                _rackItems[slot] = null;

                if (_totalSpawned >= _spawnCap)
                {
                    Debug.LogWarning($"[SoloVrTest] Yenileme tavani ({_spawnCap}) doldu — " +
                                     "yeni silah dogurulmuyor. Yenilemek icin paneldeki dugmeyi kullan.");
                    return;
                }
                SpawnAt(slot);
            };
            grab.StateDirty += handler;
        }

        /// <summary>Tek bir yuvaya silah dogurur (ortak yol: hem ilk doldurma hem yenileme).</summary>
        void SpawnAt(int slot)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var go = Instantiate(_rackPrefabs[slot], _rackSlots[slot], Quaternion.identity);
            var no = go.GetComponentInChildren<NetworkObject>(true);
            if (no != null) no.Spawn();

            var grab = go.GetComponentInChildren<GrabbableObject>(true);
            _rackItems[slot] = grab;
            _totalSpawned++;
            if (grab != null) Watch(slot, grab);
        }

        void OnDestroy()
        {
            // Game view'i normale dondur, yoksa sonraki Play'de ayna kapali kalir.
            XRSettings.gameViewRenderMode = GameViewRenderMode.LeftEye;
            // Test kancasi bu bilesenle yasar - birakilmazsa normal oyunda da zorlanirdi.
            WeaponHandWeld.ForceClearanceLocal = false;
        }

        void OnGUI()
        {
            if (_cam == null) return;
            GUILayout.BeginArea(new Rect(Screen.width - 300, 20, 280, 410), GUI.skin.box);
            GUILayout.Label("SOLO VR TEST");
            GUILayout.Label(_target != null
                ? (_follow ? "Kamera: avatari cerceveliyor" : "Kamera: serbest")
                : "Avatar araniyor...");
            GUILayout.Label("T = mod, F = yeniden bul");
            GUILayout.Label("Sag fare = bak, WASD/QE = gez");

            // KUMANDA TESHISI: "algilamiyor" sikayetini tahminle degil OLCUMLE ayirmak icin.
            // Oyunun tamami eski UnityEngine.XR.InputDevices API'sini kullaniyor
            // (XRDevicePoseDriver, HandGrabber) — burada da onu okuyoruz, yani gordugun sey
            // oyunun gordugu seyin ta kendisi.
            GUILayout.Space(6);
            GUILayout.Label("--- KUMANDA TESHISI ---");
            DeviceLine("Sol ", UnityEngine.XR.XRNode.LeftHand);
            DeviceLine("Sag ", UnityEngine.XR.XRNode.RightHand);
            DeviceLine("Kafa", UnityEngine.XR.XRNode.CenterEye);

            // AVATAR TESHISI: AvatarIKController'in takip kapisi. Kapali oldugu surece
            // LateUpdate en basta doner ve avatar DONAR — kumandalar calissa bile poz
            // degismez. Kapi kafanin groundY'den yuksekligine bakiyor.
            var ikc = _target != null ? _target.GetComponentInParent<AvatarIKController>() : null;
            if (ikc == null && _target != null)
                ikc = _target.root != null ? _target.root.GetComponentInChildren<AvatarIKController>(true) : null;
            if (ikc != null)
            {
                GUILayout.Space(4);
                float hy = ikc.headSource != null ? ikc.headSource.position.y : float.NaN;
                GUILayout.Label($"Kafa Y: {hy:0.00}  esik: {ikc.trackingReadyMinHeight:0.00}");
                GUILayout.Label(ikc.TrackingValid
                    ? "Takip kapisi: ACIK"
                    : "Takip kapisi: KAPALI -> AVATAR DONUK");
            }

            GUILayout.Space(6);
            bool bc = GUILayout.Toggle(showBodyClearance, "Govde temizligi (dis gorunus)");
            if (bc != showBodyClearance)
            {
                showBodyClearance = bc;
                WeaponHandWeld.ForceClearanceLocal = bc;
                SavePrefs();
            }
            if (showBodyClearance)
                GUILayout.Label("(acikken gozlukte de silah kayar)");

            if (_rackSlots.Count > 0)
            {
                GUILayout.Space(8);
                GUILayout.Label($"Silah cemberi ({_rackSlots.Count} silah) — ayarlar kalici");

                GUILayout.Label($"Yaricap: {rackRadius:0.00} m");
                rackRadius = GUILayout.HorizontalSlider(rackRadius, 0.4f, 4f);

                GUILayout.Label($"Yukseklik: {rackHeight:0.00} m");
                rackHeight = GUILayout.HorizontalSlider(rackHeight, 0.2f, 2f);

                if (GUILayout.Button("Cemberi bana getir"))
                    RecenterRack();
                int dolu = 0;
                for (int i = 0; i < _rackItems.Count; i++)
                    if (_rackItems[i] != null) dolu++;

                if (GUILayout.Button($"SILAHLARI GETIR  ({dolu}/{_rackSlots.Count})",
                                     GUILayout.Height(26)))
                    FillRack();
            }

            // --- OTURMA TELAFISI ---
            {
                GUILayout.Space(8);
                GUILayout.Label($"Oturma telafisi: {seatedRise:0.00} m");
                float r = GUILayout.HorizontalSlider(seatedRise, 0f, 1.2f);
                if (!Mathf.Approximately(r, seatedRise)) ApplySeatedRise(r);

                if (GUILayout.Button("Oturmayi telafi et (otomatik)"))
                {
                    var rig = XRRigReference.Instance;
                    if (rig != null && rig.head != null)
                    {
                        // Su anki kafa yuksekligini ayakta hedefe tamamla.
                        float cur = rig.head.position.y - _appliedRise;
                        ApplySeatedRise(Mathf.Clamp(standingHeadHeight - cur, 0f, 1.2f));
                    }
                }
            }
            GUILayout.EndArea();
        }
    }
}
#endif
