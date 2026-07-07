// =====================================================================================
// SADECE YEREL TEST ARACI — COMMIT'LENMEZ (.git/info/exclude icinde).
//
// PC'de kulaklik takili degilken oyunu "gercek oyuncu" olarak test etmek icin
// klavye/fare harness'i. Neden gerekli: projedeki tum girdi kodu (LanBootstrap,
// HandGrabber, NetworkWeapon, XRRigLocomotion...) eski UnityEngine.XR.InputDevices
// API'sini okur; XR Device Simulator ise yalnizca yeni Input System cihazlari uretir
// ve o API'ye hic dusmez. Bu harness girdi katmanini tamamen atlayip oyunun GERCEK
// ag yollarini (RequestGrabServerRpc, FireServerRpc, ServerApplyDamage) kullanir.
//
// Tuslar:  H = HOST baslat (PC'de oyuncu avatariyla)   J = CLIENT olarak katil
//          G = en yakin silahi kap                     T = dummy hedef spawn (host)
//          WASD (+Shift kosu) = hareket                Sag tik basili = etrafa bak
//          F veya (sag tik basiliyken sol tik) = ates  F1 = harness'i ac/kapa
// =====================================================================================
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace VRMultiplayer.Combat
{
    public class DevPcTestHarness : MonoBehaviour
    {
        const ulong DummyOwnerId = 9999; // gercek istemci degil: self-hit filtresine takilmasin diye
        const float EyeHeight = 1.65f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~DevPcTestHarness");
            DontDestroyOnLoad(go);
            go.AddComponent<DevPcTestHarness>();
        }

        bool _uiOn = true;
        float _pitch, _yaw;
        bool _rotInit;
        GrabbableObject _weapon;
        Quaternion _weaponRotOffset = Quaternion.identity;
        MethodInfo _fire;
        float _nextFire;
        float _nextTick;
        string _status = "H = HOST baslat, J = CLIENT katil";

        static NetworkManager NM => NetworkManager.Singleton;
        static XRRigReference Rig => XRRigReference.Instance;
        static bool HeadsetPresent => InputDevices.GetDeviceAtXRNode(XRNode.Head).isValid;
        static bool SessionActive => NM != null && (NM.IsServer || NM.IsConnectedClient);

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f1Key.wasPressedThisFrame) _uiOn = !_uiOn;
            if (!_uiOn || HeadsetPresent) return; // gercek kulaklik varsa harness uyur

            if (kb.hKey.wasPressedThisFrame) StartHost();
            if (kb.jKey.wasPressedThisFrame) JoinAsClient();
            if (kb.gKey.wasPressedThisFrame) GrabNearestWeapon();
            if (kb.tKey.wasPressedThisFrame) SpawnDummyTarget();

            MoveRig(kb);
            HandleFire(kb);
            SlowTick();
        }

        void LateUpdate()
        {
            // NetworkVRPlayer LateUpdate'te rig'i avatara kopyalar; silahi ondan sonra
            // kameraya sabitleriz ki ates izi elden ciksin.
            if (!_uiOn || HeadsetPresent) return;
            CarryWeapon();
        }

        // ------------------------------------------------------------- oturum

        void StartHost()
        {
            if (NM == null) { _status = "NetworkManager yok!"; return; }
            if (NM.IsListening) { _status = "Zaten aktif bir oturum var."; return; }

            // Editorde onceki Play oturumundan sizmis bir soket 7777'yi tutabiliyor
            // (UTP teardown'i yarida kalirsa) — bos bir port bulup onu kullaniyoruz.
            var bs = FindFirstObjectByType<LanBootstrap>();
            ushort port = FindFreePort(bs != null ? bs.port : (ushort)7777);
            var utp = NM.GetComponent<UnityTransport>();
            if (utp != null) utp.SetConnectionData(LanBootstrap.GetLocalIPv4(), port, "0.0.0.0");

            if (!NM.StartHost()) // StartServer degil: PC'de de oyuncu avatari spawn olsun
            {
                _status = "Host baslatilamadi — Console'daki hataya bak.";
                return;
            }
            WriteHostPortFile(port); // ayni PC'deki ikinci instance J ile dogru porta baglansin

            // Gozlukler B ile katilabilsin diye sunucuyu LAN'da duyur.
            var disc = FindFirstObjectByType<NetworkDiscovery>();
            if (disc != null) { disc.gamePort = port; disc.StartAdvertising(); }

            TakeOverPcControls();
            _status = "HOST aktif (port " + port + "). Takimi soldaki panelden sec, sonra G ile silah kap.";
        }

        void JoinAsClient()
        {
            if (NM == null || NM.IsListening) return;
            var bs = FindFirstObjectByType<LanBootstrap>();
            if (bs == null) { _status = "LanBootstrap yok!"; return; }
            if (string.IsNullOrEmpty(bs.manualHostIp))
                bs.manualHostIp = "127.0.0.1"; // ayni PC'deki editor/build host'una baglan

            // Host bu makinede farkli bir porta dustuyse (7777 sizintisi) onu kullan.
            if (File.Exists(PortFilePath) &&
                ushort.TryParse(File.ReadAllText(PortFilePath).Trim(), out ushort hostPort))
                bs.port = hostPort;

            bs.StartCoroutine(bs.JoinAsClient());
            TakeOverPcControls();
            _status = "Istemci: sunucu araniyor...";
        }

        // 7777'den yukari dogru gercekten BIND edilebilen ilk portu dondurur; sizinti
        // veya baska surec tarafindan tutulan portlara hic bulasmamis oluruz.
        static ushort FindFreePort(ushort preferred)
        {
            for (ushort p = preferred; p < preferred + 20; p++)
            {
                try
                {
                    using (var probe = new UdpClient())
                        probe.Client.Bind(new IPEndPoint(IPAddress.Any, p));
                    return p;
                }
                catch { /* dolu — siradakini dene */ }
            }
            return preferred;
        }

        // Editor ile ayni PC'deki build ayni persistentDataPath'i paylasir (ayni
        // sirket/urun adi) — host sectigi portu buraya yazar, J ile katilan okur.
        static string PortFilePath => Path.Combine(Application.persistentDataPath, "dev_host_port.txt");

        static void WriteHostPortFile(ushort port)
        {
            try { File.WriteAllText(PortFilePath, port.ToString()); } catch { }
        }

        void OnDestroy()
        {
            // Play'den cikarken soketi mutlaka birak — 7777 sizintisinin panzehiri.
            if (NM != null && NM.IsListening) NM.Shutdown();
        }

        // PC girdisiyle catisabilecek her seyi kapat: XR Device Simulator (bu projede
        // zaten hicbir scripti besleyemiyor) ve kulaklik isteyen kalibrasyon akisi.
        void TakeOverPcControls()
        {
            var sim = GameObject.Find("XR Device Simulator");
            if (sim != null) sim.SetActive(false);
            DisableCalibration();
        }

        static void DisableCalibration()
        {
            var cal = FindFirstObjectByType<CalibrationManager>();
            if (cal == null) return;
            // Begin() bileşen kapaliyken bile paneli geri acabiliyor — her seferinde gizle.
            if (cal.status != null && cal.status.gameObject.activeSelf)
                cal.status.gameObject.SetActive(false);
            if (cal.enabled) cal.enabled = false;
        }

        // Yanlislikla "SUNUCU baslat"a basilirsa ServerView tum kameralari kapatip
        // seyirci kamerasi kurar; oyuncu objemiz varken bunu geri al ve o dugmeyi
        // bir daha cikmasin diye LanBootstrap'i kapat. (Harness'siz saf sunucu
        // modunda PlayerObject olmadigindan bu kod hic calismaz, seyirci modu bozulmaz.)
        void NeutralizeSpectatorTakeover()
        {
            if (NM.LocalClient == null || NM.LocalClient.PlayerObject == null) return;

            var bs = FindFirstObjectByType<LanBootstrap>();
            if (bs != null && bs.enabled) bs.enabled = false;

            var srvCam = GameObject.Find("Server Camera");
            if (srvCam == null) return;
            var sv = FindFirstObjectByType<ServerView>();
            if (sv != null) sv.enabled = false;
            Destroy(srvCam);

            var rig = Rig;
            if (rig != null && rig.head != null)
            {
                var cam = rig.head.GetComponent<Camera>();
                if (cam != null) cam.enabled = true;
            }
            _status = "Seyirci modu kapatildi — oyuncu kamerasindasin.";
        }

        // ------------------------------------------------------------- hareket

        void MoveRig(Keyboard kb)
        {
            var rig = Rig;
            if (rig == null || rig.head == null) return;

            // PC'de kafayi suren cihaz yok — kamerayi bir kez goz hizasina kaldir.
            if (rig.head.localPosition.sqrMagnitude < 0.0001f)
                rig.head.localPosition = new Vector3(0f, EyeHeight, 0f);

            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                if (!_rotInit)
                {
                    Vector3 e = rig.head.localEulerAngles;
                    _yaw = e.y;
                    _pitch = e.x > 180f ? e.x - 360f : e.x;
                    _rotInit = true;
                }
                Vector2 d = mouse.delta.ReadValue();
                _yaw += d.x * 0.15f;
                _pitch = Mathf.Clamp(_pitch - d.y * 0.15f, -85f, 85f);
                rig.head.localRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move.z += 1f;
            if (kb.sKey.isPressed) move.z -= 1f;
            if (kb.dKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed) move.x -= 1f;
            if (move.sqrMagnitude > 0f)
            {
                Vector3 fwd = rig.head.forward; fwd.y = 0f; fwd.Normalize();
                Vector3 right = rig.head.right; right.y = 0f; right.Normalize();
                float speed = kb.leftShiftKey.isPressed ? 4.5f : 2.2f;
                rig.transform.position += (fwd * move.z + right * move.x).normalized * speed * Time.deltaTime;
            }

            // Elleri kameraya gore park et: avatar elleri ve tutus makul dursun.
            Transform h = rig.head;
            if (rig.rightHand != null)
                rig.rightHand.SetPositionAndRotation(
                    h.position + h.forward * 0.35f + h.right * 0.18f - h.up * 0.22f, h.rotation);
            if (rig.leftHand != null)
                rig.leftHand.SetPositionAndRotation(
                    h.position + h.forward * 0.35f - h.right * 0.18f - h.up * 0.22f, h.rotation);
        }

        // ------------------------------------------------------------- silah

        void GrabNearestWeapon()
        {
            if (!SessionActive) { _status = "Once H (host) veya J (katil)."; return; }
            var rig = Rig;
            if (rig == null || rig.head == null) return;

            // Kendi HandGrabber'imiz kapali kalmali: her saniyeki Reconcile, iki VR elinin
            // de tutmadigi objeleri sunucuda birakir ve dev-grab'i aninda dusurur.
            var po = NM.LocalClient != null ? NM.LocalClient.PlayerObject : null;
            var hg = po != null ? po.GetComponent<HandGrabber>() : null;
            if (hg != null && hg.enabled) hg.enabled = false;

            GrabbableObject best = null;
            float bestDist = float.MaxValue;
            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                if (g.IsHeld) continue;
                float d = Vector3.Distance(rig.head.position, g.transform.position);
                if (d < bestDist) { bestDist = d; best = g; }
            }
            if (best == null) { _status = "Sahnede bos silah/obje yok."; return; }

            _weapon = best;
            _weaponRotOffset = ComputeSnapRotOffset(best);
            best.RequestGrabServerRpc(1); // 1 = sag el
            _status = "Kapildi: " + best.name + " — F veya (sag+sol tik) = ates";
        }

        void CarryWeapon()
        {
            if (_weapon == null || NM == null) return;
            if (!_weapon.IsHeld || _weapon.HolderClientId != NM.LocalClientId || !_weapon.IsOwner) return;
            var rig = Rig;
            if (rig == null || rig.head == null) return;

            // FPS tutusu: sag-alt onde, namlu ekseni bakis yonune hizali.
            Transform h = rig.head;
            _weapon.transform.SetPositionAndRotation(
                h.position + h.forward * 0.40f + h.right * 0.15f - h.up * 0.25f,
                h.rotation * _weaponRotOffset);
        }

        // HandGrabber.SnapRotOffset'in birebir kopyasi (orada private): silahin namlu
        // eksenini (mesh'in en uzun ekseni, isaret govdenin yigildigi taraf) bulur ve
        // +Z'ye esler — boylece tufek elde yatay/yan durmaz, baktigin yeri gosterir.
        static Quaternion ComputeSnapRotOffset(GrabbableObject g)
        {
            if (g.gripRotationEuler != Vector3.zero)
                return Quaternion.Euler(g.gripRotationEuler);

            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest == null) return Quaternion.identity;

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
            Vector3 axis = Vector3.right;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) axis = Vector3.forward;

            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            if (sign == 0f) sign = 1f;

            Quaternion childToRoot = Quaternion.Inverse(g.transform.rotation) * biggest.transform.rotation;
            Vector3 axisRoot = childToRoot * (axis * sign);
            return Quaternion.FromToRotation(axisRoot, Vector3.forward);
        }

        void HandleFire(Keyboard kb)
        {
            var mouse = Mouse.current;
            bool aiming = mouse != null && mouse.rightButton.isPressed;
            bool pressed = kb.fKey.wasPressedThisFrame ||
                           (aiming && mouse.leftButton.wasPressedThisFrame);
            if (!pressed || _weapon == null || Time.time < _nextFire) return;
            if (!_weapon.IsHeld || _weapon.HolderClientId != NM.LocalClientId)
            {
                _status = "Silah elde degil (G ile tekrar kap).";
                return;
            }
            var nw = _weapon.GetComponent<NetworkWeapon>();
            if (nw == null) { _status = _weapon.name + " uzerinde NetworkWeapon yok."; return; }

            // FireServerRpc private oldugu icin (NetworkWeapon'a test kodu sokmamak adina)
            // yansimayla cagiriyoruz; sunucu tarafindaki dogrulama/raycast aynen calisir.
            if (_fire == null)
                _fire = typeof(NetworkWeapon).GetMethod("FireServerRpc",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            if (_fire == null) { _status = "FireServerRpc bulunamadi (imza degisti mi?)"; return; }

            _nextFire = Time.time + nw.fireInterval;
            var rig = Rig;
            Vector3 origin = rig.head.position + rig.head.forward * 0.35f;
            _fire.Invoke(nw, new object[] { origin, (Vector3)rig.head.forward, default(RpcParams) });
        }

        // ------------------------------------------------------------- dummy hedef

        // Bolgesel hasari tek basina test etmek icin: oyuncu prefab'ini sahte bir
        // istemci kimligiyle spawn eder. Sahibi biz olmadigimizdan self-hit filtresine
        // takilmaz; takimi 0 oldugundan friendly-fire filtresine de girmez.
        void SpawnDummyTarget()
        {
            if (NM == null || !NM.IsServer) { _status = "Dummy icin HOST olmalisin (H)."; return; }
            var prefab = NM.NetworkConfig != null ? NM.NetworkConfig.PlayerPrefab : null;
            if (prefab == null) { _status = "NetworkManager'da Player Prefab yok."; return; }

            var rig = Rig;
            Vector3 fwd = rig.head.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 pos = new Vector3(rig.head.position.x, rig.transform.position.y, rig.head.position.z) + fwd * 3f;
            var go = Instantiate(prefab, pos, Quaternion.LookRotation(-fwd));
            try
            {
                go.GetComponent<NetworkObject>().SpawnWithOwnership(DummyOwnerId);
                PoseDummy(go.transform, pos, -fwd);
                _status = "Dummy hedef 3 m onunde. Farkli bolgelere ates edip loglari izle.";
            }
            catch (System.Exception e)
            {
                Destroy(go);
                _status = "Dummy spawn olmadi: " + e.Message;
            }
        }

        // Dummy'nin poz tasiyicilarini (Head + eller) kimse surmedigi icin prefab'daki
        // yerlerinde kalir ve IK avatari yerde yumaga doner. Ayakta bir hedef icin:
        // 1) NetworkTransform'lari kapat (sahibi olmayan taraf onlari surekli ilk poza
        //    geri cekmesin), 2) tasiyicilari elle insan pozuna diz.
        static void PoseDummy(Transform root, Vector3 groundPos, Vector3 facing)
        {
            foreach (var nt in root.GetComponentsInChildren<NetworkTransform>(true))
                nt.enabled = false;

            var rot = Quaternion.LookRotation(facing);
            var vr = root.GetComponent<NetworkVRPlayer>();
            Transform head = GetPrivateTransform(vr, "head");
            Transform lh = GetPrivateTransform(vr, "leftHand");
            Transform rh = GetPrivateTransform(vr, "rightHand");
            if (head == null) head = root.Find("Head");

            if (head != null) head.SetPositionAndRotation(groundPos + Vector3.up * 1.65f, rot);
            Vector3 side = rot * Vector3.right;
            if (lh != null) lh.SetPositionAndRotation(groundPos + Vector3.up * 1.05f - side * 0.28f, rot);
            if (rh != null) rh.SetPositionAndRotation(groundPos + Vector3.up * 1.05f + side * 0.28f, rot);
        }

        static Transform GetPrivateTransform(object obj, string field)
        {
            if (obj == null) return null;
            var f = obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(obj) as Transform : null;
        }

        // ------------------------------------------------------------- bakim

        void SlowTick()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 1f;
            if (!SessionActive) return;

            // TeamSelector.Choose() kalibrasyonu yeniden baslatabilir — kapali tut.
            DisableCalibration();

            // "SUNUCU baslat"a yanlis basilmissa seyirci kamerasini geri al.
            NeutralizeSpectatorTakeover();

            // Dev-grab aktifken HandGrabber acilmis olabilir (respawn vb.) — kapali tut.
            if (_weapon != null && _weapon.HolderClientId == NM.LocalClientId)
            {
                var po = NM.LocalClient != null ? NM.LocalClient.PlayerObject : null;
                var hg = po != null ? po.GetComponent<HandGrabber>() : null;
                if (hg != null && hg.enabled) hg.enabled = false;
            }
        }

        // ------------------------------------------------------------- UI

        void OnGUI()
        {
            if (!_uiOn || HeadsetPresent) return;

            GUILayout.BeginArea(new Rect(300, 20, 320, 210), GUI.skin.box);
            GUILayout.Label("PC TEST HARNESS (F1 = gizle/goster)");
            if (!SessionActive)
            {
                if (GUILayout.Button("HOST baslat — PC'de oyuncu olarak (H)")) StartHost();
                if (GUILayout.Button("CLIENT olarak katil (J)")) JoinAsClient();
            }
            else
            {
                if (GUILayout.Button("En yakin silahi kap (G)")) GrabNearestWeapon();
                if (NM.IsServer && GUILayout.Button("Dummy hedef spawn et (T)")) SpawnDummyTarget();
                GUILayout.Label("WASD = hareket (Shift = kosu)\nSag tik basili = etrafa bak\nF veya (sag tik + sol tik) = ates");
            }
            GUILayout.Label(_status);
            GUILayout.EndArea();

            if (SessionActive)
                GUI.Label(new Rect(Screen.width / 2f - 5f, Screen.height / 2f - 10f, 20f, 20f), "+");
        }
    }
}
#endif
