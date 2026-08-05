using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// LAN discovery over UDP so clients find the server's IP automatically. Works BOTH ways
    /// for robustness:
    ///  - CLIENT PULL: the client broadcasts a query on <see cref="discoveryPort"/>; the server
    ///    replies directly to the sender.
    ///  - SERVER PUSH: the server also broadcasts an announcement every second on
    ///    <see cref="announcePort"/>, which clients listen for.
    /// Broadcasts are sent to 255.255.255.255 AND to every interface's directed broadcast address
    /// (e.g. 192.168.1.255) — some routers/APs drop one but pass the other.
    ///
    /// On Android/Quest a WifiManager MulticastLock is held while active (required, or incoming
    /// broadcast is dropped) — needs CHANGE_WIFI_MULTICAST_STATE permission (see README).
    /// On Windows, allow the app through the firewall (inbound UDP) or nothing gets in.
    /// </summary>
    public class NetworkDiscovery : MonoBehaviour
    {
        [Tooltip("UDP port the SERVER listens on for discovery queries.")]
        public ushort discoveryPort = 47777;

        [Tooltip("UDP port CLIENTS listen on for server announcements.")]
        public ushort announcePort = 47778;

        [Tooltip("The game port the server listens on (sent to clients).")]
        public ushort gamePort = 7777;

        [Tooltip("Must be identical on every device so different apps don't cross-talk.")]
        public string appId = "savhateks-vr";

        UdpClient _queryUdp;    // server: query/reply socket • client: query sender + reply listener
        UdpClient _announceUdp; // server: announce sender    • client: announce listener
        CancellationTokenSource _cts;
        readonly object _lock = new object();
        string _foundHostIp;
        ushort _foundHostPort;

        string Query => "SAVHATEKS_DISCOVER:" + appId;
        string ReplyPrefix => "SAVHATEKS_HOST:" + appId;

        // ---------------- havuz durumu (Serit 3) ----------------

        /// <summary>Sunucunun havuzunda oynanabilir harita var mi — istemcinin BAGLANMADAN
        /// once ogrenebildigi tek sey.</summary>
        public enum PoolHint { Unknown, Empty, HasMaps }

        /// <summary>
        /// SUNUCU: yayina eklenecek havuz durumu.
        ///
        /// DISARIDAN BESLENIR, BURADA OKUNMAZ: yayin dongusleri arka planda (Task) kosuyor ve
        /// MapCatalog dosya okuyup Unity olayi tetikliyor — ana is parcacigi disinda cagirmak
        /// yasak. Sunucu tarafi bunu ana is parcacigindan set eder (bkz. LanBootstrap).
        ///
        /// volatile: iki is parcacigi arasinda paylasilan tek bir bayrak; kilit kurmaya degmez.
        /// </summary>
        public volatile bool poolHasMaps;

        /// <summary>
        /// ISTEMCI: bulunan sunucunun havuz durumu. <see cref="PoolHint.Unknown"/> = sunucu bu
        /// bilgiyi yollamiyor (eski surum) — o durumda ENGELLEME, karari sunucuya birak.
        /// </summary>
        public PoolHint FoundHostPool { get { lock (_lock) return _foundHostPool; } }

        PoolHint _foundHostPool = PoolHint.Unknown;

        // Havuz alani PORTUN ONUNE giriyor. Sira onemli: eski bir istemci portu
        // LastIndexOf(':') ile okuyor, yani son alan port kalmali — yoksa eski bir gozluk
        // build'i havuz bayragini port sanip baglanamaz. Bu sekilde eski istemci yeni sunucuya
        // sorunsuz baglanir, alani gormezden gelir.
        const string PoolYes = "MAP1", PoolNo = "MAP0";

        string ReplyMessage => ReplyPrefix + ":" + (poolHasMaps ? PoolYes : PoolNo) + ":" + gamePort;

        // ---------------- SERVER ----------------
        public void StartAdvertising()
        {
            StopDiscovery();
            AcquireMulticastLock();
            _cts = new CancellationTokenSource();
            _queryUdp = NewBroadcastClient(discoveryPort);
            _announceUdp = NewBroadcastClient(0);
            _ = ServerReplyLoop(_queryUdp, _cts.Token);
            _ = ServerAnnounceLoop(_announceUdp, _cts.Token);
            Debug.Log("[NetworkDiscovery] Advertising on UDP " + discoveryPort +
                      " + announcing on " + announcePort);
        }

        async Task ServerReplyLoop(UdpClient udp, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await udp.ReceiveAsync();
                    if (Encoding.UTF8.GetString(res.Buffer) == Query)
                    {
                        // Cevap HER SEFERINDE kuruluyor: havuz durumu oyun sirasinda degisiyor
                        // (tasarimci harita ekliyor/cikariyor) ve bir kez hazirlanan mesaj
                        // bayat bir cevabi sonsuza kadar yayinlardi.
                        byte[] reply = Encoding.UTF8.GetBytes(ReplyMessage);
                        await udp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
                    }
                }
                catch
                {
                    if (token.IsCancellationRequested) return;
                    await Task.Delay(50);
                }
            }
        }

        async Task ServerAnnounceLoop(UdpClient udp, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                byte[] msg = Encoding.UTF8.GetBytes(ReplyMessage);   // her turda taze (bkz. yukarisi)
                foreach (var target in BroadcastTargets(announcePort))
                {
                    try { await udp.SendAsync(msg, msg.Length, target); } catch { }
                }
                try { await Task.Delay(1000, token); } catch { return; }
            }
        }

        // ---------------- CLIENT ----------------
        public void StartClientDiscovery()
        {
            StopDiscovery();
            AcquireMulticastLock();
            lock (_lock) { _foundHostIp = null; _foundHostPort = 0; _foundHostPool = PoolHint.Unknown; }
            _cts = new CancellationTokenSource();

            _queryUdp = NewBroadcastClient(0); // ephemeral port
            _ = ClientQueryLoop(_queryUdp, _cts.Token);
            _ = ClientReceiveLoop(_queryUdp, _cts.Token);

            try
            {
                _announceUdp = NewBroadcastClient(announcePort); // hear server announcements
                _ = ClientReceiveLoop(_announceUdp, _cts.Token);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkDiscovery] Announce listener unavailable: " + e.Message);
            }

            Debug.Log("[NetworkDiscovery] Searching for a server on the LAN...");
        }

        async Task ClientQueryLoop(UdpClient udp, CancellationToken token)
        {
            byte[] query = Encoding.UTF8.GetBytes(Query);
            while (!token.IsCancellationRequested)
            {
                lock (_lock) { if (_foundHostIp != null) return; }
                foreach (var target in BroadcastTargets(discoveryPort))
                {
                    try { await udp.SendAsync(query, query.Length, target); } catch { }
                }
                try { await Task.Delay(800, token); } catch { return; }
            }
        }

        async Task ClientReceiveLoop(UdpClient udp, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await udp.ReceiveAsync();
                    string msg = Encoding.UTF8.GetString(res.Buffer);
                    if (msg.StartsWith(ReplyPrefix))
                    {
                        // Cevap: "SAVHATEKS_HOST:<appId>[:MAP0|MAP1]:<gamePort>". PORT SON ALAN
                        // ve oyle kalmali: sizmis bir soket sunucuyu varsayilan porttan
                        // kaydirabiliyor, istemci onu takip etmezse sabit 7777'ye baglanmaya
                        // calisip sonsuza kadar bekler.
                        ushort advertised = 0;
                        int colon = msg.LastIndexOf(':');
                        if (colon >= 0) ushort.TryParse(msg.Substring(colon + 1), out advertised);

                        // Havuz alani portun ONUNDE. Yoksa (eski sunucu) Unknown kalir ve
                        // istemci kimseyi engellemez — karar sunucuda verilir.
                        var pool = PoolHint.Unknown;
                        if (colon > 0)
                        {
                            int prev = msg.LastIndexOf(':', colon - 1);
                            if (prev >= 0)
                            {
                                string tok = msg.Substring(prev + 1, colon - prev - 1);
                                if (tok == PoolYes) pool = PoolHint.HasMaps;
                                else if (tok == PoolNo) pool = PoolHint.Empty;
                            }
                        }

                        lock (_lock)
                        {
                            _foundHostIp = res.RemoteEndPoint.Address.ToString();
                            _foundHostPort = advertised;
                            _foundHostPool = pool;
                        }
                        Debug.Log("[NetworkDiscovery] Found server at " + _foundHostIp + ":" + advertised +
                                  "  havuz=" + pool);
                        return;
                    }
                }
                catch
                {
                    if (token.IsCancellationRequested) return;
                    await Task.Delay(50);
                }
            }
        }

        // ---------------- Helpers ----------------

        // 255.255.255.255 plus each interface's directed broadcast (e.g. 192.168.1.255).
        // Routers/APs that filter one usually pass the other.
        static List<IPEndPoint> BroadcastTargets(ushort port)
        {
            var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, port) };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        try
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (IPAddress.IsLoopback(ua.Address)) continue;
                            var mask = ua.IPv4Mask;
                            if (mask == null || mask.Equals(IPAddress.Any)) continue;

                            byte[] ip = ua.Address.GetAddressBytes();
                            byte[] m = mask.GetAddressBytes();
                            var bc = new byte[4];
                            for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~m[i]);
                            var ep = new IPEndPoint(new IPAddress(bc), port);
                            if (!targets.Exists(t => t.Address.Equals(ep.Address)))
                                targets.Add(ep);
                        }
                        catch { /* some platforms don't expose IPv4Mask — skip that entry */ }
                    }
                }
            }
            catch { }
            return targets;
        }

        static UdpClient NewBroadcastClient(ushort port)
        {
            var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return udp;
        }

        public bool TryGetFoundHost(out string ip)
        {
            lock (_lock) { ip = _foundHostIp; }
            return !string.IsNullOrEmpty(ip);
        }

        /// <summary>Found host ip + the ADVERTISED game port (0 if the reply carried none).</summary>
        public bool TryGetFoundHost(out string ip, out ushort gamePort)
        {
            lock (_lock) { ip = _foundHostIp; gamePort = _foundHostPort; }
            return !string.IsNullOrEmpty(ip);
        }

        public void StopDiscovery()
        {
            try { _cts?.Cancel(); } catch { }
            try { _queryUdp?.Close(); } catch { }
            try { _announceUdp?.Close(); } catch { }
            _queryUdp = null;
            _announceUdp = null;
            _cts = null;
            ReleaseMulticastLock();
        }

        void OnDestroy() => StopDiscovery();

        // ---------------- Android MulticastLock ----------------
        // Quest/Android drops incoming broadcast UDP unless a MulticastLock is held.
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _multicastLock;

        void AcquireMulticastLock()
        {
            if (_multicastLock != null) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                    _multicastLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "savhateks-discovery");
                    _multicastLock.Call("setReferenceCounted", true);
                    _multicastLock.Call("acquire");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkDiscovery] Could not acquire MulticastLock: " + e.Message);
            }
        }

        void ReleaseMulticastLock()
        {
            if (_multicastLock == null) return;
            try { _multicastLock.Call("release"); } catch { }
            _multicastLock = null;
        }
#else
        void AcquireMulticastLock() { }
        void ReleaseMulticastLock() { }
#endif
    }
}
