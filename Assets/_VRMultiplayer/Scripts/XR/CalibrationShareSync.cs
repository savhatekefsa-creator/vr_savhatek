using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// FAZ 2 — Ortak kalibrasyon cercevesinin (shared anchor GRUP GUID'i) SUNUCU-OTORITER
    /// dagitimi. Bkz. PLAN-kalibrasyon.md.
    ///
    /// Prefab/NetworkObject GEREKTIRMEZ — yalnizca named message kullanir
    /// (<see cref="Weapons.WeaponConfigSyncTool"/> deseninin aynisi), boylece NetworkPrefab
    /// listesi degismez ve ForceSamePrefabs hash'i bozulmaz.
    ///
    /// MIMARI NOT: Netcode sunucusu PC'dir; PC'de gozluk yoktur, dolayisiyla anchor da yoktur.
    /// Anchor kaynagi HER ZAMAN bir gozluktur — sunucu yalnizca dagiticidir.
    ///
    /// KILIT (karar K1): ilk yayinlayan ortak cerceveyi SAHIPLENIR, baskasinin yayini
    /// REDDEDILIR. Sahibi kendi yayinini tazeleyebilir; aksi halde yanlis yapilmis bir ilk
    /// kalibrasyon sunucu yeniden baslatilmadan duzeltilemezdi.
    ///
    ///  - ISTEMCI (pull-first): grup GUID'i yoksa periyodik olarak sunucudan ister. Dusen bir
    ///    push'a ya da "henuz kimse kalibre etmedi"ye karsi ayni guvenlik agi.
    ///  - SUNUCU: yayini kabul edince HERKESE push eder; her baglanan istemciye de gonderir.
    /// </summary>
    public class CalibrationShareSync : MonoBehaviour
    {
        const string MsgSet = "CalibShare";      // sunucu -> istemci: aktif grup GUID
        const string MsgReq = "CalibShareReq";   // istemci -> sunucu: pull
        const string MsgPub = "CalibSharePub";   // istemci -> sunucu: yayinla
        const float PullRetrySeconds = 5f;
        const float LoadRetrySeconds = 10f;

        // --- sunucu durumu (otorite) ---
        static Guid _serverGroupId;
        static ulong _serverPublisher;
        static bool _serverHasPublisher;

        // --- yerel durum ---
        static Guid _activeGroupId;

        /// <summary>Bu cihazin bagli oldugu ortak cerceve GUID'i (bos = yok).</summary>
        public static Guid ActiveGroupId => _activeGroupId;

        /// <summary>Bir cerceve GUID'imiz var mi? (sunucu onaylamis OLMAYABILIR)</summary>
        public static bool HasSharedCalibration => _activeGroupId != Guid.Empty;

        /// <summary>
        /// Cerceve SUNUCU tarafindan onaylandi mi? Yalnizca bu true iken cerceve gercekten
        /// ORTAKTIR. Ag koptugunda her gozluk kendi GUID'ini uretip "ortak" sanabiliyordu —
        /// iki oyuncunun da kendini "ilk kalibre eden" zannetmesinin sebebi buydu.
        /// </summary>
        public static bool ServerConfirmed { get; private set; }

        bool _handlersRegistered;
        bool _serverHooked;
        float _pullRetryAt;
        float _loadRetryAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~CalibrationShareSync");
            DontDestroyOnLoad(go);
            go.AddComponent<CalibrationShareSync>();
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            if (!nm.IsListening)
            {
                // Oturum kapandi: sunucu abonelerini birak (NGO shutdown delegeleri TEMIZLEMEZ,
                // birakmazsak her yeniden baslatmada bir kez daha birikir) ve durumu sifirla.
                if (_serverHooked)
                {
                    nm.OnClientConnectedCallback -= OnClientConnected;
                    nm.OnClientDisconnectCallback -= OnClientDisconnected;
                }
                _handlersRegistered = false;
                _serverHooked = false;
                return;
            }

            RegisterHandlers(nm);

            if (nm.IsServer && !_serverHooked)
            {
                _serverHooked = true;
                nm.OnClientConnectedCallback += OnClientConnected;
                nm.OnClientDisconnectCallback += OnClientDisconnected;
            }

            if (!nm.IsServer) TickClientPull(nm);
        }

        void OnDestroy()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !_serverHooked) return;
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        void RegisterHandlers(NetworkManager nm)
        {
            if (_handlersRegistered || nm.CustomMessagingManager == null) return;

            if (nm.IsServer)
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgReq,
                    (sender, reader) => { if (_serverGroupId != Guid.Empty) SendSetTo(sender); });

                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPub, (sender, reader) =>
                {
                    reader.ReadValueSafe(out string s);
                    if (TryParse(s, out var g)) AcceptPublish(sender, g);
                });
            }
            else
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSet, (sender, reader) =>
                {
                    reader.ReadValueSafe(out string s);
                    if (!TryParse(s, out var g)) return;
                    ServerConfirmed = true;   // sunucudan geldi -> cerceve GERCEKTEN ortak
                    Adopt(g);
                });
            }
            _handlersRegistered = true;
        }

        /// <summary>Grup GUID'i gelene kadar sunucudan iste. Kimse kalibre etmemis olabilir —
        /// o yuzden suresiz ama seyrek (5 sn, birkac bayt).</summary>
        void TickClientPull(NetworkManager nm)
        {
            if (!nm.IsConnectedClient) return;

            // (a) Cerceve onaylandi ama anchor HENUZ SURULMUYOR -> yuklemeyi tekrar dene.
            // Shared anchor yuklemesi gecici olarak basarisiz olabilir (runtime bu odada henuz
            // localize olmadi). Tek denemede birakirsak oyuncu kalici olarak yalniz kalirdi.
            if (ServerConfirmed && HasSharedCalibration && !CalibrationAnchor.Driving)
            {
                if (Time.unscaledTime < _loadRetryAt) return;
                _loadRetryAt = Time.unscaledTime + LoadRetrySeconds;
                Debug.Log($"[CalibShare] Ortak anchor henuz yuklenmedi — yeniden deneniyor ({_activeGroupId:N}).");
                CalibrationAnchor.LoadShared(_activeGroupId);
                return;
            }

            // (b) Hic cerceve yoksa sunucudan iste. Kimse kalibre etmemis olabilir — suresiz
            // ama seyrek (birkac bayt).
            if (HasSharedCalibration) return;
            if (Time.unscaledTime < _pullRetryAt) return;
            _pullRetryAt = Time.unscaledTime + PullRetrySeconds;

            using var w = new FastBufferWriter(4, Allocator.Temp);
            nm.CustomMessagingManager.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w,
                NetworkDelivery.ReliableSequenced);
        }

        // ------------------------------------------------------------------ yayin

        /// <summary>
        /// Anchor'i paylastiktan sonra cagrilir: grup GUID'ini sunucuya bildirir.
        /// Yerel durumu da hemen isaretler, boylece sunucudan donen yayin kendimize
        /// "yeni cerceve geldi" gibi gorunup anchor'i bosuna yeniden yukletmez.
        /// </summary>
        public static void Publish(Guid groupId)
        {
            if (groupId == Guid.Empty) return;
            _activeGroupId = groupId;
            ServerConfirmed = false;   // sunucu geri yayinlayana kadar bu cerceve YEREL sayilir

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                Debug.LogWarning("[CalibShare] AG YOK — grup GUID'i yalnizca yerel tutuldu. " +
                                 "Bu cerceve BASKA OYUNCULARLA ORTAK DEGILDIR.");
                return;
            }

            if (nm.IsServer)
            {
                ServerConfirmed = true;   // otorite biziz
                AcceptPublish(NetworkManager.ServerClientId, groupId);
                return;
            }

            if (SendGuid(MsgPub, groupId, NetworkManager.ServerClientId, false))
                Debug.Log($"[CalibShare] Grup GUID sunucuya yollandi: {groupId:N}");
        }

        /// <summary>Sunucu: yayini kabul et ya da reddet (KILIT, karar K1).</summary>
        static void AcceptPublish(ulong senderId, Guid groupId)
        {
            if (_serverHasPublisher && _serverPublisher != senderId)
            {
                Debug.Log($"[CalibShare] {senderId} yayini REDDEDILDI — ortak cerceve " +
                          $"{_serverPublisher} tarafindan kilitli. (Sahibi yeniden kalibre " +
                          "ederse cerceve guncellenir.)");
                return;
            }

            bool refresh = _serverHasPublisher;
            _serverGroupId = groupId;
            _serverPublisher = senderId;
            _serverHasPublisher = true;

            Debug.Log($"[CalibShare] Ortak cerceve {(refresh ? "TAZELENDI" : "KILITLENDI")} " +
                      $"— sahip {senderId}, grup {groupId:N}. Herkese yayinlaniyor.");
            PushToAll();
        }

        static void SendSetTo(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (_serverGroupId == Guid.Empty) return;
            SendGuid(MsgSet, _serverGroupId, clientId, false);
        }

        static void PushToAll()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (_serverGroupId == Guid.Empty) return;
            SendGuid(MsgSet, _serverGroupId, 0, true);
        }

        /// <summary>
        /// GUID'i named message olarak yollar.
        ///
        /// BUFFER BOYUTU KRITIK: GUID "N" formatinda 32 KARAKTER; WriteValueSafe(string) once
        /// 4 baytlik uzunluk sonra karakter basina 2 bayt yazar = 68 bayt. Ilk surumde buffer
        /// 64 bayt ve BUYUYEMEZ olarak acilmisti; yazma tasip istisna firlatiyor, mesaj hic
        /// gitmiyordu — ustelik hata yalnizca gozlugun log'unda kaldigi icin PC'den gorunmuyordu.
        /// Ucuncu parametre (maxSize) buffer'in buyumesine izin verir.
        /// </summary>
        static bool SendGuid(string msg, Guid id, ulong clientId, bool toAll)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return false;
            try
            {
                using var w = new FastBufferWriter(128, Allocator.Temp, 1024);
                w.WriteValueSafe(id.ToString("N"));
                if (toAll)
                    nm.CustomMessagingManager.SendNamedMessageToAll(msg, w, NetworkDelivery.ReliableSequenced);
                else
                    nm.CustomMessagingManager.SendNamedMessage(msg, clientId, w, NetworkDelivery.ReliableSequenced);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CalibShare] '{msg}' YOLLANAMADI: {e.Message}");
                return false;
            }
        }

        static void OnClientConnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || clientId == NetworkManager.ServerClientId) return;
            SendSetTo(clientId); // pull gelmeden de guvence; dusen push'u istemcinin pull'u toparlar
        }

        /// <summary>
        /// Kilit sahibi oyundan cikarsa SAHIPLIGI birak — aksi halde kilit, artik var olmayan bir
        /// client ID'de takili kalir ve kimse yeni cerceve yayinlayamaz.
        ///
        /// GRUP GUID'I KORUNUR: paylasilan anchor duruyor ve kalan oyuncular onu kullanmaya devam
        /// ediyor; cerceveyi silmek herkesin hizasini bosuna bozardi. Yalnizca "kim tazeleyebilir"
        /// serbest kalir.
        /// </summary>
        static void OnClientDisconnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!_serverHasPublisher || _serverPublisher != clientId) return;

            _serverHasPublisher = false;
            Debug.Log($"[CalibShare] Cerceve sahibi ({clientId}) cikti — SAHIPLIK serbest. " +
                      $"Grup {_serverGroupId:N} gecerli kalmaya devam ediyor; gerekirse " +
                      "baska bir oyuncu yeniden kalibre edip tazeleyebilir.");
        }

        // ------------------------------------------------------------------ alim

        /// <summary>Istemci: aga ait grup GUID'ini benimse ve anchor'i yuklet.</summary>
        static void Adopt(Guid groupId)
        {
            if (groupId == Guid.Empty) return;
            if (groupId == _activeGroupId)
            {
                // Kendi ekomuz ya da tekrar push. Anchor zaten suruluyorsa yapacak sey yok;
                // surulmuyorsa (a) yolundaki yeniden deneme halleder.
                return;
            }
            _activeGroupId = groupId;
            Debug.Log($"[CalibShare] Ortak cerceve agdan alindi: {groupId:N}");
            CalibrationAnchor.LoadShared(groupId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _serverGroupId = Guid.Empty;
            _serverPublisher = 0;
            _serverHasPublisher = false;
            _activeGroupId = Guid.Empty;
            ServerConfirmed = false;
        }

        static bool TryParse(string s, out Guid g) => Guid.TryParseExact(s, "N", out g);
    }
}
