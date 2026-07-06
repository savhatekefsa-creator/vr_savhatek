using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Server-authoritative health for a player. Weapons call <see cref="ServerApplyDamage"/> on
    /// the server when a shot hits this player's hitbox; friendly fire is filtered by the weapon.
    /// At 0 health the player is "down" for <see cref="respawnDelay"/> seconds, then revived to
    /// full health with a short invulnerability window. Health/Dead are NetworkVariables so the
    /// owner's HUD and everyone's visuals stay in sync (including late joiners).
    ///
    /// Attach to the NetworkPlayer root (next to <see cref="PlayerIdentity"/>).
    /// </summary>
    public class PlayerHealth : NetworkBehaviour
    {
        public const int MaxHealth = 100;

        [Tooltip("Elenince yeniden dogana kadar gecen sure (saniye).")]
        public float respawnDelay = 4f;
        [Tooltip("Yeniden dogduktan sonra dokunulmazlik suresi (saniye).")]
        public float reviveInvuln = 2f;

        public NetworkVariable<int> Health = new NetworkVariable<int>(MaxHealth);
        public NetworkVariable<bool> Dead = new NetworkVariable<bool>(false);

        PlayerIdentity _identity;
        float _invulnUntil;

        public bool IsDead => Dead.Value;
        public byte TeamValue => _identity != null ? _identity.Team.Value : (byte)0;

        public override void OnNetworkSpawn()
        {
            _identity = GetComponent<PlayerIdentity>();
            if (IsServer)
            {
                Health.Value = MaxHealth;
                Dead.Value = false;
            }
        }

        /// <summary>Server-only. Reduce health; handle death + scheduled revive.</summary>
        public void ServerApplyDamage(int amount, ulong attacker)
        {
            if (!IsServer || Dead.Value || amount <= 0) return;
            if (Time.time < _invulnUntil) return; // just revived — brief grace

            Health.Value = Mathf.Max(0, Health.Value - amount);
            if (Health.Value <= 0)
            {
                Dead.Value = true;
                StartCoroutine(RespawnAfter());
            }
        }

        IEnumerator RespawnAfter()
        {
            yield return new WaitForSeconds(respawnDelay);
            Health.Value = MaxHealth;
            Dead.Value = false;
            _invulnUntil = Time.time + reviveInvuln;
        }
    }
}
