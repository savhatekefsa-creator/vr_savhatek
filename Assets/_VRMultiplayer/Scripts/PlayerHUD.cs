using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// The LOCAL player's combat HUD: a floating health bar that follows your view, a red flash +
    /// controller rumble when you take damage, and a "down / respawning" overlay when eliminated.
    /// Owner-only; driven purely by <see cref="PlayerHealth"/>'s NetworkVariables. Attach to the
    /// NetworkPlayer root (wired by Tools > VR Multiplayer > 18).
    /// </summary>
    public class PlayerHUD : NetworkBehaviour
    {
        PlayerHealth _health;

        Transform _root;      // billboarded container
        Transform _barFill;
        TextMesh _label;
        MeshRenderer _flash;  // red damage flash
        float _flashUntil = -1f;
        int _lastHealth = PlayerHealth.MaxHealth;

        const float BarWidth = 0.32f;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) { enabled = false; return; }
            _health = GetComponent<PlayerHealth>();
            if (_health == null) { enabled = false; return; }

            BuildHud();
            _lastHealth = _health.Health.Value;
            _health.Health.OnValueChanged += OnHealthChanged;
            _health.Dead.OnValueChanged += OnDeadChanged;
            RefreshBar(_health.Health.Value);
        }

        public override void OnNetworkDespawn()
        {
            if (_health != null)
            {
                _health.Health.OnValueChanged -= OnHealthChanged;
                _health.Dead.OnValueChanged -= OnDeadChanged;
            }
            if (_root != null) Destroy(_root.gameObject);
        }

        void OnHealthChanged(int prev, int now)
        {
            RefreshBar(now);
            if (now < prev)
            {
                // Took damage: red flash + rumble both controllers.
                _flashUntil = Time.time + 0.18f;
                Rumble();
            }
            _lastHealth = now;
        }

        void OnDeadChanged(bool _, bool dead)
        {
            if (_label != null)
                _label.text = dead ? "ELENDIN\nyeniden doguyor..." : "CAN " + _health.Health.Value;
            if (dead) { _flashUntil = Time.time + 0.4f; Rumble(); }
        }

        void Update()
        {
            if (_root == null) return;

            var rig = XRRigReference.Instance;
            if (rig != null && rig.head != null)
            {
                Vector3 fwd = rig.head.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = rig.head.right; right.y = 0f;
                if (right.sqrMagnitude < 0.01f) right = Vector3.right;
                right.Normalize();
                // Bottom-RIGHT of view so it never blocks aim.
                _root.position = rig.head.position + fwd * 1.1f + right * 0.55f - Vector3.up * 0.42f;
                _root.rotation = Quaternion.LookRotation(_root.position - rig.head.position);

                if (_flash != null && _flashUntil > 0f && rig.head != null)
                    _flash.transform.SetPositionAndRotation(
                        rig.head.position + rig.head.forward * 0.4f,
                        rig.head.rotation);
            }

            if (_flash != null)
            {
                bool on = Time.time < _flashUntil;
                if (_flash.enabled != on) _flash.enabled = on;
            }
        }

        void RefreshBar(int hp)
        {
            float ratio = Mathf.Clamp01((float)hp / PlayerHealth.MaxHealth);
            if (_barFill != null)
            {
                _barFill.localScale = new Vector3(BarWidth * ratio, 0.05f, 1f);
                _barFill.localPosition = new Vector3(-BarWidth * 0.5f + BarWidth * ratio * 0.5f, 0f, 0.001f);
                var mr = _barFill.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Color c = ratio > 0.5f ? Color.Lerp(Color.yellow, Color.green, (ratio - 0.5f) * 2f)
                                           : Color.Lerp(Color.red, Color.yellow, ratio * 2f);
                    SetColor(mr, c);
                }
            }
            if (_label != null && (_health == null || !_health.Dead.Value))
                _label.text = "CAN " + hp;
        }

        void Rumble()
        {
            for (int i = 0; i < 2; i++)
            {
                var dev = InputDevices.GetDeviceAtXRNode(i == 0 ? XRNode.LeftHand : XRNode.RightHand);
                if (dev.isValid) dev.SendHapticImpulse(0, 0.8f, 0.15f);
            }
        }

        // ------------------------------------------------------------- build

        void BuildHud()
        {
            _root = new GameObject("Combat HUD").transform;

            _label = MakeText(_root, "CAN 100", new Vector3(0f, 0.06f, 0f));

            var bg = MakeQuad(_root, "BarBg", new Color(0.1f, 0.1f, 0.1f, 0.85f));
            bg.localScale = new Vector3(BarWidth + 0.02f, 0.07f, 1f);
            bg.localPosition = Vector3.zero;

            _barFill = MakeQuad(_root, "BarFill", Color.green);
            _barFill.localScale = new Vector3(BarWidth, 0.05f, 1f);

            var flashGo = MakeQuad(_root, "Damage Flash", new Color(0.9f, 0.05f, 0.05f, 1f));
            flashGo.SetParent(null, true);
            flashGo.localScale = new Vector3(1.4f, 1.4f, 1f);
            _flash = flashGo.GetComponent<MeshRenderer>();
            _flash.enabled = false;
        }

        static TextMesh MakeText(Transform parent, string text, Vector3 localPos)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one * 0.16f;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = 0.06f;
            tm.fontSize = 60;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            return tm;
        }

        static Transform MakeQuad(Transform parent, string name, Color color)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            q.transform.SetParent(parent, false);
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MakeMat(color);
            return q.transform;
        }

        // OPAQUE URP/Lit only. The room materials already use this shader, so it (and its
        // opaque variant) is guaranteed to ship in the build — no magenta. Transparent /
        // Unlit variants get stripped from the build when nothing else references them, which
        // is exactly what turned the HUD quads pink.
        static Material MakeMat(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var m = new Material(shader);
            m.SetFloat("_Smoothness", 0f);
            SetColor(m, color);
            return m;
        }

        static void SetColor(MeshRenderer mr, Color c) => SetColor(mr.material, c);
        static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }
    }
}
