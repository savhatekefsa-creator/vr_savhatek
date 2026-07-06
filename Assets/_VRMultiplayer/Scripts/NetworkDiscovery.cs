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

        string Query => "SAVHATEKS_DISCOVER:" + appId;
        string ReplyPrefix => "SAVHATEKS_HOST:" + appId;

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
            byte[] reply = Encoding.UTF8.GetBytes(ReplyPrefix + ":" + gamePort);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await udp.ReceiveAsync();
                    if (Encoding.UTF8.GetString(res.Buffer) == Query)
                        await udp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
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
            byte[] msg = Encoding.UTF8.GetBytes(ReplyPrefix + ":" + gamePort);
            while (!token.IsCancellationRequested)
            {
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
            lock (_lock) { _foundHostIp = null; }
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
                        lock (_lock) { _foundHostIp = res.RemoteEndPoint.Address.ToString(); }
                        Debug.Log("[NetworkDiscovery] Found server at " + _foundHostIp);
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
