using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Match
{
    /// <summary>
    /// <see cref="MatchManager"/>'i ag prefabi olarak KAYDEDER ve sunucu acilinca SPAWN eder.
    ///
    /// Sahneye ve DefaultNetworkPrefabs listesine dokunulmaz — silahlarin izledigi yolun aynisi
    /// (bkz. <c>WeaponPrefabRegistrar</c>). Iki ayri an var, ikisi de kritik:
    ///
    ///  1) KAYIT, AfterSceneLoad'da. <c>AddNetworkPrefab</c> yalnizca <c>IsListening</c> false
    ///     iken guvenlidir; oturum basladiktan sonra prefab eklemek ForceSamePrefabs hash'ini
    ///     bozar ve istemciler baglanamaz. HER PEER ayni anda ayni prefabi kaydeder.
    ///
    ///  2) SPAWN, yalnizca SUNUCUDA, OnServerStarted'da. Boylece hem PC'nin adanmis sunucusu
    ///     hem de host yolu tek yerden kapaniyor ve <see cref="LanBootstrap"/>'e hic
    ///     dokunulmuyor.
    /// </summary>
    public static class MatchBootstrap
    {
        const string PrefabPath = "MatchPrefabs/Match";

        static GameObject _prefab;
        static NetworkManager _subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Register()
        {
            _prefab = Resources.Load<GameObject>(PrefabPath);
            if (_prefab == null)
            {
                Debug.LogWarning("[MatchBootstrap] Resources/" + PrefabPath + ".prefab yok — " +
                                 "mac katmani devre disi (oyun eski davranista calisir).");
                return;
            }

            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                Debug.LogWarning("[MatchBootstrap] Sahnede NetworkManager yok — mac prefabi kaydedilmedi.");
                return;
            }

            if (!nm.IsListening)
            {
                // Unity'nin editor araci NetworkObject tasiyan yeni prefablari
                // DefaultNetworkPrefabs listesine KENDILIGINDEN ekliyor. O listede olan bir
                // prefabi tekrar eklemek NGO'da kirmizi "duplicate GlobalObjectIdHash" hatasi
                // bastiriyor. Zaten kayitliysa sessizce atla — ayni ders
                // WeaponPrefabRegistrar'da da yazili.
                if (!nm.NetworkConfig.Prefabs.Contains(_prefab))
                {
                    try { nm.AddNetworkPrefab(_prefab); }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("[MatchBootstrap] Match prefabi kaydedilemedi: " + e.Message);
                    }
                }
            }

            // Domain reload kapaliyken onceki oturumun aboneligi tasinabilir: once cikar, sonra gir.
            _subscribed = nm;
            nm.OnServerStarted -= SpawnMatch;
            nm.OnServerStarted += SpawnMatch;
        }

        static void SpawnMatch()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _prefab == null) return;
            if (MatchManager.Instance != null) return;   // zaten var (yeniden baslatma)

            var go = Object.Instantiate(_prefab);
            var no = go.GetComponent<NetworkObject>();
            if (no == null)
            {
                Debug.LogError("[MatchBootstrap] Match prefabinda NetworkObject yok.");
                Object.Destroy(go);
                return;
            }

            no.Spawn();
            Object.DontDestroyOnLoad(go);
            Debug.Log("[MatchBootstrap] MatchManager spawn edildi (sunucu).");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            if (_subscribed != null) _subscribed.OnServerStarted -= SpawnMatch;
            _subscribed = null;
            _prefab = null;
        }
    }
}
