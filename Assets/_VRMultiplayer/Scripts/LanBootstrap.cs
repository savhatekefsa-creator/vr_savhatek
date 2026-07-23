using System.Collections;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Starts the LAN session. The PC runs the room as a dedicated SERVER (on-screen button);
    /// headsets press B to JOIN (the server is found automatically on the LAN), then pick a
    /// team. Shows a world-space status label so players know what to do.
    /// </summary>
    [RequireComponent(typeof(NetworkDiscovery))]
    public class LanBootstrap : MonoBehaviour
    {
        public NetworkDiscovery discovery;
        public TextMesh statusLabel;

        [Tooltip("Game port (must match the host).")]
        public ushort port = 7777;

        [Tooltip("Used only if auto-discovery fails. Leave empty to rely on discovery.")]
        public string manualHostIp = "";

        [Tooltip("Seconds to search for a host before giving up.")]
        public float discoveryTimeout = 10f;

        bool _busy;
        bool _clientStarted;
        bool _wasSessionActive;

        void Reset() => discovery = GetComponent<NetworkDiscovery>();

        void Start()
        {
            if (discovery == null) discovery = GetComponent<NetworkDiscovery>();
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool connected = nm != null && nm.IsConnectedClient;
            bool sessionActive = nm != null && (nm.IsServer || connected);

            // Oturum bitti (sunucu kapandi / baglanti koptu): _busy kilidini birak ki B tusu ve
            // PC'deki SUNUCU butonu yeniden calissin. Onceden kilit basarili katilimdan sonra hic
            // sifirlanmiyordu — panel "B'ye bas" derken tus olu kaliyor, restart gerekiyordu.
            // JoinAsClient'in arama evresini bozmaz: o evrede sessionActive zaten hep false
            // oldugundan true->false gecisi olusmaz.
            if (_wasSessionActive && !sessionActive)
            {
                _busy = false;
                _clientStarted = false;
                SetStatus("Baglanti koptu / oturum bitti.\nB = YENIDEN KATIL");
            }
            _wasSessionActive = sessionActive;

            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            // Step 1 panel: only on a headset, only while not in a session.
            if (!sessionActive && right.isValid)
            {
                EnsureJoinPanel();
                FollowHead(statusLabel);
            }
            else if (statusLabel != null && sessionActive)
            {
                Destroy(statusLabel.gameObject); // joined — the team panel takes over
                statusLabel = null;
            }

            // Detect a failed/dropped connection attempt so B works again.
            if (_clientStarted && nm != null)
            {
                if (connected) _clientStarted = false;
                else if (!nm.IsClient)
                {
                    _clientStarted = false;
                    _busy = false;
                    SetStatus("Baglanti basarisiz.\nB = TEKRAR DENE");
                }
            }

            if (_busy || !right.isValid) return;

            right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b);  // B
            if (b) StartCoroutine(JoinAsClient());
        }

        void EnsureJoinPanel()
        {
            if (statusLabel != null) return;
            var go = new GameObject("Join Panel");
            go.transform.localScale = Vector3.one * 0.16f;
            statusLabel = go.AddComponent<TextMesh>();
            statusLabel.text = "OYUNA KATILMAK ICIN\nB TUSUNA BAS";
            statusLabel.characterSize = 0.1f;
            statusLabel.fontSize = 60;
            statusLabel.anchor = TextAnchor.MiddleCenter;
            statusLabel.alignment = TextAlignment.Center;
            statusLabel.color = Color.white;
        }

        static void FollowHead(TextMesh panel)
        {
            if (panel == null) return;
            var rig = XRRigReference.Instance;
            if (rig == null || rig.head == null) return;

            Vector3 fwd = rig.head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            panel.transform.position = rig.head.position + fwd * 1.4f;
            panel.transform.rotation = Quaternion.LookRotation(fwd);
        }

        // PC screen: the only thing the PC does is run the server.
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti yine de odenirdi;
            // sunucu butonu zaten yalnizca PC icindir — mobilde tamamen kapali.
            if (Application.isMobilePlatform) return;
            if (_busy) return;
            GUILayout.BeginArea(new Rect(20, 20, 260, 90), GUI.skin.box);
            GUILayout.Label("LAN VR Multiplayer");
            if (GUILayout.Button("SUNUCU başlat")) StartAsServer();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Dedicated-server mode for the PC: runs the room WITHOUT spawning a player avatar.
        /// Headsets join with B; the PC gets the spectator/map view (<see cref="ServerView"/>).
        /// </summary>
        public void StartAsServer()
        {
            if (_busy) return;
            _busy = true;

            var nm = NetworkManager.Singleton;
            var utp = nm != null ? nm.GetComponent<UnityTransport>() : null;
            if (nm == null || utp == null)
            {
                SetStatus("Hata: NetworkManager/Transport bulunamadı.");
                _busy = false;
                return;
            }

            string ip = GetLocalIPv4();
            // A socket leaked from a previous play session can still hold the default port
            // (half-finished UTP teardown) — bind-test upward and take the first FREE port.
            // Clients learn the real port from the discovery reply, so drifting is harmless.
            ushort serverPort = FindFreePort(port);
            utp.SetConnectionData(ip, serverPort, "0.0.0.0"); // listen on all interfaces

            // 60 Hz network tick (scene asset says 30): halves the carrier-pose send interval so
            // remote heads/hands interpolate visibly smoother. Set in CODE on both the server and
            // client paths so every peer agrees no matter what the serialized scene value is.
            nm.NetworkConfig.TickRate = 60;

            if (!nm.StartServer())
            {
                // Without this check the label used to claim the server was up while Netcode
                // had already shut down on a transport bind failure.
                SetStatus("SUNUCU BASLATILAMADI!\nSoket/port hatasi — Console'a bakin.");
                _busy = false;
                return;
            }

            if (discovery != null)
            {
                discovery.gamePort = serverPort;
                discovery.StartAdvertising();
            }
            SetStatus("SUNUCU AKTIF (PC)\nIP: " + ip + "  port: " + serverPort + "\nGözlükler B ile katılsın");

            var view = FindFirstObjectByType<ServerView>();
            if (view != null) view.Activate();
            else Debug.LogWarning("[LanBootstrap] ServerView yok — Tools > VR Multiplayer > 6 çalıştır.");

            // The PC has no physical play space or avatar — hide the headset-only calibration UI.
            var cal = FindFirstObjectByType<CalibrationManager>();
            if (cal != null)
            {
                if (cal.status != null) cal.status.gameObject.SetActive(false);
                cal.enabled = false;
            }
        }

        public IEnumerator JoinAsClient()
        {
            if (_busy) yield break;
            _busy = true;
            SetStatus("Sunucu araniyor...");

            string ip = null;
            ushort hostPort = 0; // ADVERTISED game port (0 = unknown -> fall back to `port`)
            if (discovery != null)
            {
                discovery.StartClientDiscovery();
                float t = discoveryTimeout;
                while (t > 0f)
                {
                    if (discovery.TryGetFoundHost(out ip, out hostPort)) break;
                    t -= Time.deltaTime;
                    yield return null;
                }
                discovery.StopDiscovery();
            }

            if (string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(manualHostIp))
                ip = manualHostIp;

            if (string.IsNullOrEmpty(ip))
            {
                SetStatus("Sunucu bulunamadi.\nSunucu acik mi?\nB = TEKRAR DENE");
                _busy = false;
                yield break;
            }

            var nm = NetworkManager.Singleton;
            var utp = nm != null ? nm.GetComponent<UnityTransport>() : null;
            if (nm == null || utp == null)
            {
                SetStatus("Hata: NetworkManager/Transport bulunamadı.");
                _busy = false;
                yield break;
            }

            // Use the port the server ADVERTISED (it may have drifted off the default when a
            // leaked socket held it); the fixed default only covers the manual-IP fallback.
            ushort connectPort = hostPort != 0 ? hostPort : port;
            utp.SetConnectionData(ip, connectPort);
            nm.NetworkConfig.TickRate = 60; // must match the server (see StartAsServer)
            nm.StartClient();
            _clientStarted = true;
            SetStatus("Baglaniliyor: " + ip + ":" + connectPort);
        }

        void SetStatus(string s)
        {
            if (statusLabel != null) statusLabel.text = s;
            Debug.Log("[LanBootstrap] " + s);
        }

        // First port from `preferred` upward that can actually be BOUND — never lands on a
        // port a leaked/foreign socket is squatting on.
        static ushort FindFreePort(ushort preferred)
        {
            for (ushort p = preferred; p < (ushort)(preferred + 20); p++)
            {
                try
                {
                    using (var probe = new UdpClient())
                    {
                        probe.Client.Bind(new IPEndPoint(IPAddress.Any, p));
                        return p;
                    }
                }
                catch { /* dolu — siradakini dene */ }
            }
            return preferred;
        }

        public static string GetLocalIPv4()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Select(ua => ua.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .ToList();

                // Prefer a private LAN address (192.168.x / 10.x / 172.16-31.x).
                var lan = candidates.FirstOrDefault(IsPrivate);
                if (lan != null) return lan.ToString();
                if (candidates.Count > 0) return candidates[0].ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        static bool IsPrivate(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            return false;
        }
    }
}
