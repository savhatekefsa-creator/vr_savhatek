using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Silah config'lerinin SUNUCU-OTORITER dagitimi. Prefab/NetworkObject GEREKTIRMEZ —
    /// yalnizca named message kullanir (WeaponGripCaptureTool deseninin cift yonlusu), boylece
    /// NetworkPrefab listesi degismez ve ForceSamePrefabs hash'i bozulmaz.
    ///
    ///  - ISTEMCI (pull-first): baglanti kurulunca "WeaponCfgReq" yollar; 3 sn icinde set
    ///    gelmezse yeniden ister. (Connect-push'un kayitsiz handler'a denk gelip dusmesine
    ///    karsi guvenlik agi — named message'lar spawn mesajlari gibi BEKLETILMEZ.)
    ///  - SUNUCU: istege o istemciye tam seti yollar; her baglanan istemciye ayrica push eder;
    ///    CANLI AYARDA (WeaponCombatConfig.OnValidate -> dirty) 0.5 sn debounce ile herkese
    ///    yeniden yayinlar ve yerel silahlari da tazeler.
    ///  - Tasima: JSON + ReliableFragmentedSequenced — tek mesajla ~64KB'a kadar, manuel
    ///    parcalama yok. Set ~10-20KB.
    /// </summary>
    public class WeaponConfigSyncTool : MonoBehaviour
    {
        const string MsgSet = "WeaponCfg";
        const string MsgReq = "WeaponCfgReq";
        const float PullRetrySeconds = 3f;
        const float DirtyDebounceSeconds = 0.5f;

        static int _serverVersion = 1;

        bool _handlersRegistered;
        bool _serverHooked;
        bool _pullSent;
        float _pullRetryAt;
        float _dirtyPushAt = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponConfigSync");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponConfigSyncTool>();
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            if (!nm.IsListening)
            {
                // Oturum kapandi: NGO shutdown handler'lari sildi — sonrakine temiz basla.
                _handlersRegistered = false;
                _serverHooked = false;
                _pullSent = false;
                return;
            }

            RegisterHandlers(nm);

            if (nm.IsServer) TickServer(nm);
            else TickClient(nm);
        }

        void RegisterHandlers(NetworkManager nm)
        {
            if (_handlersRegistered || nm.CustomMessagingManager == null) return;

            if (nm.IsServer)
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgReq,
                    (sender, reader) => SendSetTo(sender));
            }
            else
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSet, (sender, reader) =>
                {
                    reader.ReadValueSafe(out string json);
                    WeaponConfigRegistry.ApplyJson(json);
                });
            }
            _handlersRegistered = true;
        }

        void TickClient(NetworkManager nm)
        {
            if (!nm.IsConnectedClient) { _pullSent = false; return; }
            if (WeaponConfigRegistry.AppliedVersion > 0) return;           // set alindi
            if (_pullSent && Time.unscaledTime < _pullRetryAt) return;     // yanit bekleniyor

            using var w = new FastBufferWriter(4, Allocator.Temp);
            nm.CustomMessagingManager.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w,
                NetworkDelivery.ReliableSequenced);
            _pullSent = true;
            _pullRetryAt = Time.unscaledTime + PullRetrySeconds;
        }

        void TickServer(NetworkManager nm)
        {
            if (!_serverHooked)
            {
                _serverHooked = true;
                nm.OnClientConnectedCallback += OnClientConnected;
            }

            // Canli ayar: Inspector degisikligi dirty isaretledi -> debounce dolunca yayinla.
            if (WeaponCombatConfig.ConsumeDirty())
                _dirtyPushAt = Time.unscaledTime + DirtyDebounceSeconds;
            if (_dirtyPushAt > 0f && Time.unscaledTime >= _dirtyPushAt)
            {
                _dirtyPushAt = -1f;
                PushToAll();
            }
        }

        void OnDestroy()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && _serverHooked) nm.OnClientConnectedCallback -= OnClientConnected;
        }

        static void OnClientConnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || clientId == NetworkManager.ServerClientId) return;
            SendSetTo(clientId); // pull gelmeden de guvence; dusen push'u istemcinin pull'u toparlar
        }

        /// <summary>Sunucu: tam seti tek istemciye yolla. Set her seferinde SO'larin O ANKI
        /// degerlerinden kurulur — canli ayarin kaynagi disk degil bellekteki asset'lerdir.</summary>
        static void SendSetTo(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            string json = JsonUtility.ToJson(WeaponConfigRegistry.BuildSetFromResources(_serverVersion));
            using var w = new FastBufferWriter(1024, Allocator.Temp, 1 << 20);
            w.WriteValueSafe(json);
            nm.CustomMessagingManager.SendNamedMessage(MsgSet, clientId, w,
                NetworkDelivery.ReliableFragmentedSequenced);
        }

        /// <summary>Sunucu: seti HERKESE yayinla (canli ayar debounce'u ve dev "Push" butonu)
        /// ve sunucudaki silahlarin cozulmus degerlerini de tazele.</summary>
        public static void PushToAll()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            _serverVersion++;
            string json = JsonUtility.ToJson(WeaponConfigRegistry.BuildSetFromResources(_serverVersion));
            using var w = new FastBufferWriter(1024, Allocator.Temp, 1 << 20);
            w.WriteValueSafe(json);
            nm.CustomMessagingManager.SendNamedMessageToAll(MsgSet, w,
                NetworkDelivery.ReliableFragmentedSequenced);
            WeaponConfigRegistry.RaiseUpdatedLocal();
            Debug.Log($"[SilahConfig] Set v{_serverVersion} herkese yayinlandi.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _serverVersion = 1; }
    }
}
