using Unity.Netcode;
using UnityEngine;

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
                    Color c = ratio > 0.5f ? Color.Lerp(Color.yellow, Color.green, (ratio - 0.5f) * 2f)
                                           : Color.Lerp(Color.red, Color.yellow, ratio * 2f);
                    SetColor(_fillMr.material, c);
                }
            }
            if (_name != null)
                _name.text = dead ? "ELENDI" : (_identity != null ? _identity.DisplayName : "");
        }

        void Build()
        {
            _root = new GameObject("Overhead HP").transform;

            var name = new GameObject("Name");
            name.transform.SetParent(_root, false);
            name.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            name.transform.localScale = Vector3.one * 0.13f;
            _name = name.AddComponent<TextMesh>();
            _name.characterSize = 0.06f;
            _name.fontSize = 60;
            _name.anchor = TextAnchor.MiddleCenter;
            _name.alignment = TextAlignment.Center;
            _name.color = Color.white;

            var bg = MakeQuad(_root, "Bg", new Color(0.08f, 0.08f, 0.08f, 1f));
            bg.localScale = new Vector3(BarWidth + 0.02f, 0.07f, 1f);

            _fill = MakeQuad(_root, "Fill", Color.green);
            _fill.localScale = new Vector3(BarWidth, 0.05f, 1f);
            _fillMr = _fill.GetComponent<MeshRenderer>();
        }

        // OPAQUE URP/Lit — guaranteed in the build (room uses it), so no magenta.
        static Transform MakeQuad(Transform parent, string name, Color color)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            q.transform.SetParent(parent, false);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var m = new Material(shader);
            m.SetFloat("_Smoothness", 0f);
            SetColor(m, color);
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            return q.transform;
        }

        static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }
    }
}
