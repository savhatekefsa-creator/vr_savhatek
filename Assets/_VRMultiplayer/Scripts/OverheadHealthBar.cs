using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.UI;
using System.Collections;

namespace VRMultiplayer
{
    /// <summary>
    /// A world-space health bar floating above a player's head, visible to EVERYONE (so you can
    /// see the enemy you are shooting lose health). Runs on every instance; hidden above your own
    /// head (you have the bottom-right HUD for that). Driven by <see cref="PlayerHealth"/>.
    ///
    /// Lives on the NetworkPlayer root, follows the networked Head carrier. Wired by
    /// Tools > VR Multiplayer > 18.
    /// </summary>
    public class OverheadHealthBar : NetworkBehaviour
    {
        [Tooltip("Networked head carrier to float above.")]
        public Transform head;
        [Tooltip("Kafanin ne kadar ustunde dursun (metre).")]
        public float heightAbove = 0.35f;

        const float BarWidth = 0.34f;

        PlayerHealth _health;
        PlayerIdentity _identity;
        Transform _root, _fill;
        TextMesh _name;
        MeshRenderer _fillMr;
        int _shownHp = int.MinValue;   // last value pushed to the bar
        bool _shownDead;
        Coroutine _respawnCountdown;

        public override void OnNetworkSpawn()
        {
            _health = GetComponent<PlayerHealth>();
            _identity = GetComponent<PlayerIdentity>();
            if (_health == null) { enabled = false; return; }

            // Don't show a bar above your OWN head — only above other players.
            if (IsOwner) { enabled = false; return; }

            Build();
        }

        public override void OnNetworkDespawn()
        {
            if (_root != null) Destroy(_root.gameObject);
            if (_respawnCountdown != null) StopCoroutine(_respawnCountdown);
        }

        void LateUpdate()
        {
            if (_root == null || head == null || _health == null) return;

            // Poll the networked health every frame — never depends on an event firing, so the
            // enemy's bar always mirrors their true (server-authoritative) health.
            int hp = _health.Health.Value;
            bool dead = _health.Dead.Value;
            if (hp != _shownHp || dead != _shownDead)
            {
                _shownHp = hp;
                _shownDead = dead;
                Refresh(hp);
            }

            _root.position = head.position + Vector3.up * heightAbove;

            // Billboard toward the local camera.
            var rig = XRRigReference.Instance;
            Transform cam = rig != null && rig.head != null ? rig.head
                          : (Camera.main != null ? Camera.main.transform : null);
            if (cam != null)
            {
                Vector3 to = _root.position - cam.position; to.y = 0f;
                if (to.sqrMagnitude > 0.0001f)
                    _root.rotation = Quaternion.LookRotation(to.normalized);
            }
        }

        void Refresh(int hp)
        {
            float ratio = Mathf.Clamp01((float)hp / PlayerHealth.MaxHealth);
            bool dead = _health != null && _health.Dead.Value;

            if (_fill != null)
            {
                _fill.localScale = new Vector3(BarWidth * ratio, 0.05f, 1f);
                _fill.localPosition = new Vector3(-BarWidth * 0.5f + BarWidth * ratio * 0.5f, 0f, 0.001f);
                if (_fillMr != null)
                {
                    Color c = UITheme.GetHealthColor(ratio);
                    UITheme.SetMaterialColor(_fillMr.material, c);
                }
            }
            if (_name != null)
            {
                if (dead && !_shownDead) // Oyuncu yeni elendi
                {
                    if (_respawnCountdown != null) StopCoroutine(_respawnCountdown);
                    _respawnCountdown = StartCoroutine(RespawnCountdownRoutine());
                }
                else if (!dead && _shownDead) // Oyuncu yeni doğdu
                {
                    if (_respawnCountdown != null) StopCoroutine(_respawnCountdown);
                    _respawnCountdown = null;
                    _name.text = _identity != null ? _identity.DisplayName : "";
                }
            }
        }

        IEnumerator RespawnCountdownRoutine()
        {
            float endTime = Time.time + _health.respawnDelay;
            while (Time.time < endTime)
            {
                _name.text = $"Yeniden doguyor: {Mathf.CeilToInt(endTime - Time.time)}";
                yield return new WaitForSeconds(0.2f);
            }
        }

        void Build()
        {
            _root = new GameObject("Overhead HP").transform;

            var name = new GameObject("Name");
            name.transform.SetParent(_root, false);
            name.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            name.transform.localScale = Vector3.one * 0.13f; // This scale seems to work well with the font settings
            _name = name.AddComponent<TextMesh>();
            _name.characterSize = UITheme.NameCharacterSize;
            _name.fontSize = UITheme.NameFontSize;
            _name.anchor = TextAnchor.MiddleCenter;
            _name.alignment = TextAlignment.Center;
            _name.color = UITheme.Text;

            var bg = MakeQuad(_root, "Bg", UITheme.Background);
            bg.localScale = new Vector3(BarWidth + 0.02f, 0.07f, 1f);

            _fill = MakeQuad(_root, "Fill", UITheme.HealthFull);
            _fill.localScale = new Vector3(BarWidth, 0.05f, 1f);
            _fillMr = _fill.GetComponent<MeshRenderer>();
        }

        static Transform MakeQuad(Transform parent, string name, Color color)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            q.transform.SetParent(parent, false);
            q.GetComponent<MeshRenderer>().sharedMaterial = UITheme.CreateLitMaterial(color);
            return q.transform;
        }
    }
}
