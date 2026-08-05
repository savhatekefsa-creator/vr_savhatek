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
    /// headsets JOIN from the entry screen's OYUNA BASLA button (the server is found
    /// automatically on the LAN). Shows a world-space status label so players know what is
    /// happening.
    ///
    /// KATILIM ARTIK TUSSUZ. Eskiden B ile katiliniyor / yeniden deneniyordu; B artik
    /// SKORBORD'un (bkz. UI.ScoreboardUI). Kopan baglanti elle degil OTOMATIK geri geliyor
    /// (bkz. ScheduleRetry) — zaten daha iyisiydi: oyuncu paneli okuyup dogru tusa basana
    /// kadar mac akip gidiyordu.
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

        [Tooltip("Baglanti koptuktan/basarisiz olduktan sonra otomatik yeniden denemeye kadar " +
                 "beklenen sure (saniye).")]
        public float retryDelay = 2f;

        bool _busy;
        bool _clientStarted;
        bool _wasSessionActive;

        // Otomatik yeniden baglanmanin zamanlayicisi (0 = bekleyen deneme yok).
        //
        // ESKIDEN B TUSUYDU: oyuncu dusunce "B = YENIDEN KATIL" yazip bekliyorduk. B artik
        // SKORBORD'un (bkz. UI.ScoreboardUI) — ve zaten elle yeniden baglanma kotu bir
        // cozumdu: oyuncu paneli okuyup dogru tusa basana kadar mac akip gidiyordu.
        // unscaledTime kullaniliyor: baglanti koptugunda zaman olceginin ne oldugu belirsiz.
        float _retryAt;

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

            // Oturum bitti (sunucu kapandi / baglanti koptu): _busy kilidini birak ki otomatik
            // yeniden deneme ve PC'deki SUNUCU butonu calissin. Onceden kilit basarili
            // katilimdan sonra hic sifirlanmiyordu — panel "B'ye bas" derken tus olu kaliyor,
            // restart gerekiyordu. JoinAsClient'in arama evresini bozmaz: o evrede sessionActive
            // zaten hep false oldugundan true->false gecisi olusmaz.
            if (_wasSessionActive && !sessionActive)
            {
                _busy = false;
                _clientStarted = false;
                ScheduleRetry("Baglanti koptu / oturum bitti.");
            }
            _wasSessionActive = sessionActive;

            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            // Step 1 panel: only on a headset, only while not in a session.
            if (!sessionActive && right.isValid)
            {
                EnsureJoinPanel();
            }
            else if (statusLabel != null && sessionActive)
            {
                Destroy(statusLabel.gameObject); // joined — the team panel takes over
                statusLabel = null;
            }

            // Detect a failed/dropped connection attempt so the retry timer can fire.
            if (_clientStarted && nm != null)
            {
                if (connected) _clientStarted = false;
                else if (!nm.IsClient)
                {
                    _clientStarted = false;
                    _busy = false;
                    ScheduleRetry("Baglanti basarisiz.");
                }
            }

            // Yaratici moddan cikinca kendiliginden baglanma hakki geri gelir: ana menuye donup
            // tekrar YARATICI'yi secen biri yeniden baglanabilmeli.
            if (!AppMode.IsCreative) _creativeJoinStarted = false;

            // Kumanda gecerliligi ARANMAZ: katilim artik tussuz (B skorbordun oldu), dolayisiyla
            // kumanda uykuya dalmis olsa da baglanti kurulabilmeli.
            if (_busy) return;

            // OYUNCU modu: isim + takim onaylanmadan katilim yok (bkz. AppMode, UI.PlayerEntryUI).
            // Katilimi OYUNA BASLA butonu baslatir; kopan baglantiyi zamanlayici geri getirir
            // (bkz. ScheduleRetry).
            //
            // YARATICI modda da katiliyoruz, ve bu bilincli bir DEGISIKLIK: haritalar PC'de
            // yasiyor (MapCatalog otoritesi), tasarim ise gozlukte yapiliyor. Baglanmayan bir
            // gozluk kendi diskine yazardi ve o harita macta hic gorunmezdi. Yaratici modda
            // profil ARANMAZ — isim/takim ekrani oyuncu akisina ait, harita tasarlarken
            // sorulacak bir sey degil.
            //
            // PC'nin SUNUCU butonu bu kapiya TAKILMAZ: sunucu avatar spawn etmiyor, profile de
            // mod secimine de ihtiyaci yok (bkz. StartAsServer).
            if (AppMode.IsPlayer) { if (!PlayerProfile.Confirmed) return; }
            else if (!AppMode.IsCreative) return;

            // YARATICI SIRASI: ONCE KALIBRASYON, SONRA BAGLANTI.
            //
            // Harita KALIBRE CERCEVEDE oruluyor. Kalibre olmadan baglanmak, sunucunun haritasini
            // gozlugun daha oturmamis cercevesinde kurmak demek: duvarlar gorunur ama gercek
            // odaya gore yanlis yerde durur, ve kalibrasyon sonradan yapildiginda zemin
            // oyuncunun altinda kayar. Insa modu ayni kurala zaten tabi (bkz.
            // ConstructorPlacer.RequireCalibration) — baglantiyi disarida birakmak sirayi yarim
            // birakirdi.
            if (AppMode.IsCreative && !CalibrationManager.Calibrated) return;

            // Kalibrasyon bitti: baglantiyi BIR KEZ kendiliginden baslat. Yaratici modda ag
            // istege bagli degil — haritalar PC'de, baglanmayan gozlukte gosterilecek liste de
            // kaydedilecek yer de yok. Dal bunu "B ile yeniden dene" ile tamamliyordu; B artik
            // yok, o yuzden kopan/basarisiz baglantiyi asagidaki zamanlayici toparliyor
            // (ScheduleRetry yaratici modu da kabul eder).
            if (AppMode.IsCreative && !_creativeJoinStarted && !connected)
            {
                _creativeJoinStarted = true;
                StartCoroutine(JoinAsClient());
                return;
            }

            // Otomatik yeniden baglanma. Kumanda gecerliligi ARANMAZ (panelin aksine): kumanda
            // uykuya daldiginda baglantinin geri gelmemesi icin bir sebep yok.
            if (_retryAt > 0f && Time.unscaledTime >= _retryAt)
            {
                _retryAt = 0f;
                StartCoroutine(JoinAsClient());
            }
        }

        /// <summary>Bir sonraki otomatik denemeyi kurar ve sebebini panele yazar.
        /// OYUNCU modunda profil onayliysa, YARATICI modda kosulsuz anlamli; giris tamamlanmadan
        /// ya da mod secilmeden yeniden baglanmaya calismak yanlis olurdu.
        ///
        /// YARATICI DALI BIRLESIRKEN EKLENDI: o dal kopan baglantiyi "B = yeniden dene" ile
        /// toparliyordu, main ise B'li katilimi tamamen kaldirdi. Yaratici modu buraya almasak
        /// birlesme sonrasi yaratici modda HIC yeniden deneme kalmazdi — _creativeJoinStarted
        /// tek atislik oldugu icin kullanici moddan cikip girene kadar kopuk kalirdi.</summary>
        void ScheduleRetry(string reason)
        {
            bool canRetry = AppMode.IsCreative || (AppMode.IsPlayer && PlayerProfile.Confirmed);
            if (!canRetry)
            {
                SetStatus(reason);
                return;
            }
            _retryAt = Time.unscaledTime + Mathf.Max(0.5f, retryDelay);
            SetStatus(reason + "\nYeniden baglaniliyor...");
        }

        // Yaratici moddaki kendiliginden baglanma bir KEZ denenir; sonrasi oyuncunun elinde
        // (B). Yoksa sunucu kapaliyken her karede yeni bir deneme baslardi.
        bool _creativeJoinStarted;

        /// <summary>
        /// Havuz durumunu keskif yayinina yazar. ANA IS PARCACIGI: yayin dongusu Task uzerinde
        /// kosuyor ve MapCatalog dosya okuyup Unity olayi tetikliyor.
        /// </summary>
        void PushPoolStateToDiscovery()
        {
            if (discovery == null) return;
            discovery.poolHasMaps = !Constructor.MapCatalog.PoolIsEmpty;
        }

        void OnDestroy() => Constructor.MapCatalog.Changed -= PushPoolStateToDiscovery;

        void EnsureJoinPanel()
        {
            // Once MOD SECIMI, sonra GIRIS EKRANI (isim + takim). O ekranlar aciktayken bu
            // paneli kurmayiz: paneller ust uste binerdi.
            //
            // Panel artik TUS ISTEMIYOR, yalnizca DURUM bildiriyor: katilimi OYUNA BASLA
            // baslatiyor, kopan baglantiyi da zamanlayici geri getiriyor (bkz. ScheduleRetry).
            //
            // YARATICI modda panel metni farkli: orada katilim "maca girmek" degil, haritalarin
            // durdugu PC'ye baglanmak demek.
            if (AppMode.IsCreative) { EnsureCreativeJoinPanel(); return; }
            if (!AppMode.IsPlayer || !PlayerProfile.Confirmed) return;
            if (statusLabel != null) return;
            statusLabel = UI.HeadFollowPanel.Create("Join Panel",
                "SUNUCUYA BAGLANILIYOR...", Color.white);
        }

        /// <summary>
        /// Yaratici modun katilim daveti. Baglanmadan harita listesi bos kalir ve kaydedilen
        /// harita hicbir yere gitmez — bu yuzden sebebi yaziyor, "B'ye bas" demekle yetinmiyor.
        /// </summary>
        void EnsureCreativeJoinPanel()
        {
            // KALIBRASYON PANELIYLE YAN YANA DURMAZ: ikisi de kafanin 1.4 m onunde duruyor,
            // ikisini birden acmak ust uste iki yazi demek (aeb92ec'de ayni ders). Sira zaten
            // once kalibrasyon; bu panel ancak o bitince anlamli.
            if (!CalibrationManager.Calibrated) return;
            if (statusLabel != null) return;
            statusLabel = UI.HeadFollowPanel.Create("Join Panel",
                "HARITALAR PC'DE\nBAGLANILIYOR...", Color.white);
        }

        // PC screen: the only thing the PC does is run the server.
        void OnGUI()
        {
            // IMGUI kulaklikta hicbir sey cizmez ama layout maliyeti yine de odenirdi;
            // sunucu butonu zaten yalnizca PC icindir — mobilde tamamen kapali.
            if (Application.isMobilePlatform) return;

            // Sunucu ayaktayken bu panel MAC KUMANDASI olur (bkz. MatchGui). _busy kilidi
            // yalnizca sunucu HENUZ baslamadan onemliydi.
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer) { MatchGui(); return; }

            if (_busy) return;
            GUILayout.BeginArea(new Rect(20, 20, 260, 90), GUI.skin.box);
            GUILayout.Label("LAN VR Multiplayer");
            if (GUILayout.Button("SUNUCU başlat")) StartAsServer();
            GUILayout.EndArea();
        }

        /// <summary>PC'nin mac kumandasi: maci ELLE baslatir (ekip karari — otomatik baslatma yok).
        /// Yalnizca sunucuda cizilir; kulaklikta IMGUI zaten gorunmez.</summary>
        void MatchGui()
        {
            var m = Match.MatchManager.Instance;

            GUILayout.BeginArea(new Rect(20, 20, 300, 150), GUI.skin.box);
            GUILayout.Label("SUNUCU AKTIF");

            if (m == null)
            {
                GUILayout.Label("Mac katmani yok (Match prefabi bulunamadi).");
                GUILayout.EndArea();
                return;
            }

            // Hazir durum: butonu KILITLEMEZ, yalnizca bilgi verir — tek kisiyle test
            // edilebilsin diye (bkz. MatchConfig.minPlayersToStart).
            int blue = 0, red = 0;
            var all = PlayerIdentity.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null) continue;
                if (all[i].Team.Value == PlayerProfile.TeamBlue) blue++;
                else if (all[i].Team.Value == PlayerProfile.TeamRed) red++;
            }
            GUILayout.Label("Oyuncu: " + (blue + red) + "   MAVİ " + blue + " / KIZIL " + red);

            switch (m.Current)
            {
                case Match.MatchManager.Phase.Warmup:
                    GUILayout.Label("ISINMA — ateş var, hasar yok. Oyuncular doğabilir.");
                    if (GUILayout.Button("MAÇI BAŞLAT")) m.ServerStartMatch();
                    break;

                case Match.MatchManager.Phase.Starting:
                    GUILayout.Label("BAŞLIYOR — geri sayım " + Mathf.CeilToInt(m.SecondsLeft) + " sn");
                    break;

                case Match.MatchManager.Phase.Playing:
                    GUILayout.Label("MAÇ SÜRÜYOR — kalan " + Mathf.CeilToInt(m.SecondsLeft) + " sn");
                    GUILayout.Label("Skor:  MAVİ " + m.ScoreBlue.Value + " — KIZIL " + m.ScoreRed.Value);
                    break;

                case Match.MatchManager.Phase.Ended:
                    GUILayout.Label("MAÇ BİTTİ — " + (m.Winner.Value == 0 ? "BERABERE"
                        : m.Winner.Value == PlayerProfile.TeamBlue ? "MAVİ KAZANDI" : "KIZIL KAZANDI"));
                    GUILayout.Label("Isınmaya dönüş: " + Mathf.CeilToInt(m.SecondsLeft) + " sn");
                    if (GUILayout.Button("HEMEN YENİ MAÇ")) m.ServerStartMatch();
                    break;
            }
            GUILayout.EndArea();
        }

        /// <summary>
        /// Dedicated-server mode for the PC: runs the room WITHOUT spawning a player avatar.
        /// Headsets join with B; the PC gets the spectator/map view (<see cref="ServerView"/>).
        ///
        /// MOD SECIMINE VE PROFILE BILEREK TAKILMAZ (bkz. Update'teki kapi): mod secimi ve
        /// isim/takim kulaklik isi, sunucu ise avatar spawn etmiyor. SUNUCU butonu her zaman
        /// calisir — kapiya alinirsa PC hicbir oturum baslatamaz.
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
                PushPoolStateToDiscovery();
                discovery.StartAdvertising();

                // HAVUZ DURUMU YAYINA BINER. Serit 3'un ilk sorusu "havuzda harita var mi?" ve
                // sorunun sorulacagi an gozlugun HENUZ BAGLI OLMADIGI an — bagli olmadan
                // sunucuya soramaz. Keskif yayini zaten dinleniyor, cevap oraya ekleniyor.
                //
                // Olayla besleniyor, her kare degil: PoolIsEmpty listeyi kuruyor ve yayin
                // dongusu arka planda kostugu icin oradan MapCatalog'a dokunmak yasak.
                Constructor.MapCatalog.Changed += PushPoolStateToDiscovery;
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
            // Paneli BURADA kur: cagri OYUNA BASLA'dan gelmis olabilir, o durumda Update
            // henuz panelini kurmamis olur ve durum yazisi hicbir yere yazilamazdi.
            EnsureJoinPanel();
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
                _busy = false;
                ScheduleRetry("Sunucu bulunamadi.\nSunucu acik mi?");
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
