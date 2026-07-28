using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// OLUM SILAHSIZLANDIRMA — oyuncu olur olmaz elindeki silah(lar) SUNUCUDA yok edilir.
    ///
    /// NEDEN SUNUCU, NEDEN ISTEMCI DEGIL: onceki cozum (DeathWeaponHandler) istemci-yereldi ve
    /// bir yaris vardi — olum aninda el-silah bagi despawn'dan ONCE koparsa, silah "birakilmis
    /// ama firlatilmamis" kinematik govde olarak HAVADA ASILI kaliyordu. Sunucu Dead durumunu
    /// zaten yetkili tutuyor; olumu burada, kaynaginda yakalayip silahi ayni anda yok edince
    /// hicbir istemci yarisi kalmiyor — havada kalma imkansiz.
    ///
    /// Ayrica bu, "olunce hicbir eylem yapamazsin" kuralinin bir ayagi: silah yoksa ates de yok.
    /// Diger ayaklar: ates (NetworkWeapon.HolderIsDead), kapma (GrabbableObject grab RPC'sinde
    /// <see cref="IsClientDead"/>). Fiziksel hareket serbest — olu oyuncu dogum bolgesine yuruyebilir.
    ///
    /// ARMED (pimi cekilmis) BOMBA HARIC: onu yok etmiyoruz. Pim cekmek bombayi baglar; olmek
    /// canli bombayi sondurmenin yolu OLMAMALI — elde kalir ve fitili dolunca patlar (onceki
    /// tasarim karari, korunuyor).
    ///
    /// Kurulum SIFIR sahne/prefab dokunusu (bootstrap deseni).
    /// </summary>
    public static class DeathDisarm
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            var go = new GameObject("~DeathDisarm");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DeathDisarmServer>();
        }

        /// <summary>Sunucu-tarafi: bu istemci su an olu mu? Grab/ates gibi sunucu-otoriter
        /// engellerin ortak sorgusu. Sunucu disinda daima false (istemcide zaten yerel bakilir).</summary>
        public static bool IsClientDead(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return false;
            if (nm.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null)
            {
                var h = c.PlayerObject.GetComponent<PlayerHealth>();
                return h != null && h.IsDead;
            }
            return false;
        }
    }

    public class DeathDisarmServer : MonoBehaviour
    {
        // Her oyuncunun SON BILINEN olum durumu — false->true kenarinda silahsizlandiriyoruz.
        // OnValueChanged yerine yoklama: oyuncu sayisi az, abonelik/abonelik-iptali yasam dongusu
        // yonetmekten daha basit ve daha az hata-yatkin.
        readonly Dictionary<PlayerHealth, bool> _wasDead = new Dictionary<PlayerHealth, bool>();
        readonly List<PlayerHealth> _gone = new List<PlayerHealth>();
        float _nextScan;

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            // Yeni katilan oyuncular icin ara ara tara (ucuz).
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 0.5f;
                foreach (var h in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
                    if (h != null && h.IsSpawned && !_wasDead.ContainsKey(h))
                        _wasDead[h] = h.IsDead;
            }

            // Olum KENARINI yakala.
            _gone.Clear();
            foreach (var kv in _wasDead)
            {
                var h = kv.Key;
                if (h == null || !h.IsSpawned) { _gone.Add(h); continue; }

                bool dead = h.IsDead;
                if (dead && !kv.Value) DisarmHolder(h.OwnerClientId);
                _wasDead[h] = dead;
            }
            foreach (var h in _gone) _wasDead.Remove(h);
        }

        /// <summary>Bu oyuncunun ELINDE TUTTUGU + SAHIP OLDUGU (attigi bomba dahil) tum
        /// silah/bombalari yok et.</summary>
        // Neden "sahip oldugu" da: bomba/silah kapinca sahiplik oyuncuya geciyor (ChangeOwnership)
        // ve BIRAKINCA geri donmuyor — yani attigin bomba HALA senin sahipligindedir. Sadece
        // "elimde tuttugum"a bakarsak, oldugunde havadaki attigin bomba temizlenmez ve DONUP
        // KALIR (kullanicinin gordugu havada asili bomba bug'i). Sahiplige de bakinca o da gider.
        //
        // ARMED BOMBA DA DAHIL: onceki tasarim armed bombayi haric tutuyordu (patlasin diye) ama
        // kullanici olunce elindeki/attigi HER SEYIN gitmesini istedi — havada donuk bomba
        // kalmasin. Olmek artik canli bombayi da temizler.
        static void DisarmHolder(ulong clientId)
        {
            var actives = GrabbableObject.Active;
            // Despawn listeyi degistirebilir; once topla sonra yok et.
            var toKill = new List<NetworkObject>();
            for (int i = 0; i < actives.Count; i++)
            {
                var g = actives[i];
                if (g == null) continue;

                var no = g.NetworkObject;
                if (no == null || !no.IsSpawned) continue;

                bool mine = g.HolderClientId == clientId || no.OwnerClientId == clientId;
                if (!mine) continue;

                // Yalnizca silah/bomba — rastgele esyaya (tas vb.) dokunma.
                if (g.GetComponent<NetworkWeapon>() == null &&
                    g.GetComponent<Weapons.GrenadeController>() == null) continue;

                toKill.Add(no);
            }

            foreach (var no in toKill)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[OlumSilahsizlandirma] Oyuncu {clientId} oldu — yok edildi: {no.name}");
#endif
                no.Despawn(true);
            }
        }
    }
}
