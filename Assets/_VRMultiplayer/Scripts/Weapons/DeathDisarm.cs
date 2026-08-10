using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// OLUM SILAHSIZLANDIRMA — oyuncu olur olmaz elindeki (ve sahip oldugu) silah/bombalar
    /// SUNUCUDA yok edilir. <see cref="PlayerHealth.Die"/> dogrudan cagirir.
    ///
    /// NEDEN DESPAWN, NEDEN "BIRAK" DEGIL: birakilan silah sahnede kinematik govde olarak kalir
    /// ve HAVADA ASILI donabiliyordu (kullanicinin bildirdigi bug). Ayrica ekip karari: "oldugum
    /// anda elimdeki silah yok olacak" — olu oyuncu dirilince kemerden kendi secer.
    ///
    /// NEDEN SUNUCU: istemci-yerel bir cozumde (eski DeathWeaponHandler) olum ani ile el-silah
    /// baginin kopmasi yarisiyordu; kaybedince silah "birakilmis ama firlatilmamis" halde
    /// donuyordu. Sunucu Dead durumunu zaten yetkili tutuyor, kaynaginda hallediyoruz.
    ///
    /// Bu, "olunce hicbir eylem yapamazsin" kuralinin bir ayagi: silah yoksa ates de yok.
    /// Diger ayaklar: ates (NetworkWeapon.HolderIsDead), kapma (GrabbableObject grab RPC) ve
    /// kemer HUD'u (HandGrabber swap RPC) — ikisi de <see cref="IsClientDead"/> sorar.
    /// Fiziksel hareket serbest: olu oyuncu dogum bolgesine yuruyebilir.
    /// </summary>
    public static class DeathDisarm
    {
        // Despawn listeyi degistirir; once topla sonra yok et. Olum basina bir kez kosar,
        // paylasilan tampon yeterli.
        static readonly List<NetworkObject> _toKill = new List<NetworkObject>();

        /// <summary>Sunucu-tarafi: bu istemci su an olu mu? Grab/swap gibi sunucu-otoriter
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

        /// <summary>SUNUCU: bu oyuncunun ELINDE TUTTUGU + SAHIP OLDUGU (attigi bomba dahil)
        /// tum silah/bombalari yok et.</summary>
        // Neden sahiplige de bakiyoruz: silah/bomba kapinca sahiplik oyuncuya geciyor
        // (ChangeOwnership) ve BIRAKINCA geri donmuyor — yani attigin bomba HALA senin
        // sahipligindedir. Yalnizca "elimde tuttugum"a baksaydik, oldugunde havadaki bomba
        // temizlenmez ve donup kalirdi.
        //
        // ARMED BOMBA DA DAHIL: olmek canli bombayi da temizler. Havada donuk bomba kalmasin
        // (ekip karari) — bunun bedeli: dusmana attigin bomba, o seni once oldururse patlamaz.
        public static void DisarmHolder(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            _toKill.Clear();
            var actives = GrabbableObject.Active;
            for (int i = 0; i < actives.Count; i++)
            {
                var g = actives[i];
                if (g == null) continue;

                var no = g.NetworkObject;
                if (no == null || !no.IsSpawned) continue;

                if (g.HolderClientId != clientId && no.OwnerClientId != clientId) continue;

                // Yalnizca silah/bomba — rastgele esyaya (tas vb.) dokunma.
                if (g.GetComponent<NetworkWeapon>() == null &&
                    g.GetComponent<Weapons.GrenadeController>() == null) continue;

                _toKill.Add(no);
            }

            for (int i = 0; i < _toKill.Count; i++)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[OlumSilahsizlandirma] Oyuncu {clientId} oldu — yok edildi: {_toKill[i].name}");
#endif
                _toKill[i].Despawn(true);
            }
            _toKill.Clear();
        }
    }
}
