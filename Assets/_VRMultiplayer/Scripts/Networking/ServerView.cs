using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VRMultiplayer
{
    /// <summary>
    /// PC-side spectator view for dedicated-server mode. Activated by LanBootstrap when the
    /// server starts. Provides:
    ///  - a free-fly camera (WASD move, hold RIGHT mouse button to look, Q/E down/up, Shift fast)
    ///  - M toggles a top-down MAP view (WASD pans, Q/E zooms)
    ///  - an on-screen panel: connected player count, per-player team/name/position/ping
    ///  - a colored name label drawn over every player in view
    /// Uses the new Input System (the project is set to Input System only).
    /// </summary>
    public class ServerView : MonoBehaviour
    {
        public float flySpeed = 6f;
        public float fastMultiplier = 4f;
        public float lookSensitivity = 0.15f;

        Camera _cam;
        bool _active;
        bool _topdown;
        float _yaw, _pitch = 20f;
        float _mapHeight = 25f;

        PlayerIdentity[] _players = System.Array.Empty<PlayerIdentity>();
        Transform[] _heads = System.Array.Empty<Transform>();
        float _nextScan;
        UnityTransport _utp;

        public void Activate()
        {
            if (_active) return;
            _active = true;

            // Silence every other camera (the XR rig camera is useless on the server PC).
            foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                c.enabled = false;

            var go = new GameObject("Server Camera");
            go.transform.position = new Vector3(0f, 4f, -8f);
            _cam = go.AddComponent<Camera>();
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 1000f;
            _yaw = 0f; _pitch = 20f;
            ApplyRotation();

            if (NetworkManager.Singleton != null)
                _utp = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;

            Debug.Log("[ServerView] Spectator camera active. WASD + sag fare = bak, M = harita.");
        }

        void Update()
        {
            if (!_active || _cam == null) return;

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null) return;

            if (kb.mKey.wasPressedThisFrame)
                ToggleTopdown();

            float dt = Time.unscaledDeltaTime;
            float speed = flySpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);

            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed) move += Vector3.back;
            if (kb.aKey.isPressed) move += Vector3.left;
            if (kb.dKey.isPressed) move += Vector3.right;

            if (_topdown)
            {
                // Pan across the map; Q/E zooms.
                Vector3 pan = new Vector3(move.x, 0f, move.z) * speed * dt;
                _cam.transform.position += pan;
                if (kb.qKey.isPressed) _mapHeight += speed * dt * 2f;
                if (kb.eKey.isPressed) _mapHeight -= speed * dt * 2f;
                _mapHeight = Mathf.Clamp(_mapHeight, 5f, 80f);
                var p = _cam.transform.position;
                _cam.transform.position = new Vector3(p.x, _mapHeight, p.z);
            }
            else
            {
                if (kb.qKey.isPressed) move += Vector3.down;
                if (kb.eKey.isPressed) move += Vector3.up;
                _cam.transform.position += _cam.transform.rotation * move * speed * dt;

                // Hold right mouse button to look around (so UI clicks stay usable).
                if (mouse != null && mouse.rightButton.isPressed)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    _yaw += d.x * lookSensitivity;
                    _pitch = Mathf.Clamp(_pitch - d.y * lookSensitivity, -89f, 89f);
                    ApplyRotation();
                }
            }

            // Refresh the player list a couple of times per second (cheap, no per-frame Find).
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                _players = FindObjectsByType<PlayerIdentity>(FindObjectsSortMode.None);
                _heads = new Transform[_players.Length];
                for (int i = 0; i < _players.Length; i++)
                    _heads[i] = _players[i].transform.Find("Head");
            }
        }

        void ToggleTopdown()
        {
            _topdown = !_topdown;
            if (_topdown)
            {
                var p = _cam.transform.position;
                _cam.transform.position = new Vector3(p.x, _mapHeight, p.z);
                _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                ApplyRotation();
            }
        }

        void ApplyRotation() => _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

        void OnGUI()
        {
            if (!_active || _cam == null) return;

            // --- Markers over each player ---
            for (int i = 0; i < _players.Length; i++)
            {
                var id = _players[i];
                if (id == null) continue;
                Transform t = _heads[i] != null ? _heads[i] : id.transform;
                Vector3 sp = _cam.WorldToScreenPoint(t.position + Vector3.up * 0.35f);
                if (sp.z <= 0f) continue;
                var rect = new Rect(sp.x - 60f, Screen.height - sp.y - 12f, 120f, 24f);
                GUI.color = id.DisplayColor;
                GUI.Label(rect, "● " + id.DisplayName, CenteredLabel());
            }
            GUI.color = Color.white;

            // --- Server panel ---
            int connected = 0;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                connected = NetworkManager.Singleton.ConnectedClientsList.Count;

            int teamA = 0, teamB = 0;
            foreach (var id in _players)
            {
                if (id == null) continue;
                if (id.Team.Value == 1) teamA++;
                else if (id.Team.Value == 2) teamB++;
            }

            GUILayout.BeginArea(new Rect(20, 20, 380, 320), GUI.skin.box);
            GUILayout.Label("SUNUCU — Bagli oyuncu: " + connected + "   (A: " + teamA + "  B: " + teamB + ")");
            GUILayout.Label(_topdown ? "[M] 3D gorunum • WASD kaydir • Q/E zoom"
                                     : "[M] harita • WASD + sag fare • Q/E in/cik • Shift hizli");
            GUILayout.Space(4);

            foreach (var id in _players)
            {
                if (id == null) continue;
                Transform t = id.transform.Find("Head");
                Vector3 p = t != null ? t.position : id.transform.position;

                ulong rtt = 0;
                if (_utp != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer
                    && id.OwnerClientId != NetworkManager.ServerClientId)
                    rtt = _utp.GetCurrentRtt(id.OwnerClientId);

                GUI.color = id.DisplayColor;
                GUILayout.Label(string.Format("{0}   ({1:0.0}, {2:0.0})   ping: {3} ms",
                    id.DisplayName, p.x, p.z, rtt));
            }
            GUI.color = Color.white;
            GUILayout.EndArea();
        }

        GUIStyle _centered;
        GUIStyle CenteredLabel()
        {
            if (_centered == null)
            {
                _centered = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
            }
            return _centered;
        }
    }
}
