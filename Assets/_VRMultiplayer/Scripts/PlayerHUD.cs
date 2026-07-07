using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.UI;
using System.Collections;

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
        [Tooltip("Geri sayim icin kullanilacak ozel font. Bos birakilirsa varsayilan kullanilir.")]
        public Font countdownFont;

        PlayerHealth _health;

        Transform _root;      // billboarded container
        Transform _barFill;
        TextMesh _label;
        MeshRenderer _flash;  // red damage flash
        MeshRenderer _deathFade; // Siyah ekran kararması
        float _flashUntil = -1f;
        int _lastHealth = PlayerHealth.MaxHealth;
        Vector3 _initialLabelScale;
        Coroutine _respawnCountdown;

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
            if (dead)
            {
                // Oyuncu elendi: Ekranı karart ve animasyonlu geri sayımı başlat.
                if (_deathFade != null) _deathFade.enabled = true;
                if (_label != null) _label.gameObject.SetActive(true);
                if (_respawnCountdown != null) StopCoroutine(_respawnCountdown);
                _respawnCountdown = StartCoroutine(RespawnCountdownRoutine());
                Rumble();
            }
            else
            {
                // Oyuncu yeniden doğdu: Kararmayı kaldır, geri sayımı durdur ve metni gizle.
                if (_deathFade != null) _deathFade.enabled = false;
                if (_label != null) _label.gameObject.SetActive(false);
                if (_respawnCountdown != null)
                {
                    StopCoroutine(_respawnCountdown);
                    _respawnCountdown = null;
                }
            }

            // if (dead) { _flashUntil = Time.time + 0.4f; Rumble(); }
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
                if (_root.gameObject.activeSelf)
                {
                    _root.position = rig.head.position + fwd * 1.1f + right * 0.55f - Vector3.up * 0.42f;
                    _root.rotation = Quaternion.LookRotation(_root.position - rig.head.position);
                }

                // Ölüm kararması ve hasar flaşı her zaman kamerayı takip eder.
                Transform effectParent = _deathFade != null && _deathFade.enabled ? _deathFade.transform :
                                         _flash != null && _flash.enabled ? _flash.transform : null;
                if (effectParent != null)
                {
                    effectParent.SetPositionAndRotation(
                        rig.head.position + rig.head.forward * 0.5f, // Biraz daha önde
                        rig.head.rotation);
                }
            }

            if (_flash != null)
            {
                bool on = Time.time < _flashUntil;
                if (_flash.enabled != on) _flash.enabled = on;
            }

            // Ölüm ekranı metni her zaman kameranın önünde ve ortasında durur.
            if (_label != null && _label.gameObject.activeSelf && rig != null && rig.head != null)
            {
                _label.transform.position = rig.head.position + rig.head.forward * 1.2f;
                _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - rig.head.position);
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
                    Color c = UITheme.GetHealthColor(ratio);
                    UITheme.SetMaterialColor(mr.material, c);
                }
            }
            // Can barı sadece hayattayken görünür.
            if (_root != null)
                _root.gameObject.SetActive(hp > 0);
        }

        void Rumble()
        {
            for (int i = 0; i < 2; i++)
            {
                var dev = InputDevices.GetDeviceAtXRNode(i == 0 ? XRNode.LeftHand : XRNode.RightHand);
                if (dev.isValid) dev.SendHapticImpulse(0, 0.8f, 0.15f);
            }
        }

        IEnumerator RespawnCountdownRoutine()
        {
            if (_health == null || _label == null) yield break;

            // Her sayı için bir saniyelik animasyon döngüsü
            for (int i = Mathf.FloorToInt(_health.respawnDelay); i > 0; i--)
            {
                _label.text = i.ToString();

                // Vuruş (pulse) animasyonu: 1 saniye içinde büyüyüp küçülme
                float timer = 0f;
                float duration = 1.0f;
                Vector3 startScale = _initialLabelScale * 0.8f; // Biraz küçük başla
                Vector3 peakScale = _initialLabelScale * 1.2f;  // Zirve noktası

                while (timer < duration)
                {
                    timer += Time.deltaTime;
                    float t = timer / duration;
                    // Sinüs eğrisi ile yumuşak bir vuruş efekti (0 -> 1 -> 0)
                    float pulse = Mathf.Sin(t * Mathf.PI);
                    _label.transform.localScale = Vector3.Lerp(startScale, peakScale, pulse);
                    yield return null;
                }
                _label.transform.localScale = startScale; // Sonraki sayı için başlangıç ölçeğine dön
            }
        }

        // ------------------------------------------------------------- build

        void BuildHud()
        {
            _root = new GameObject("Combat HUD").transform;

            _label = MakeText(null, "", Vector3.zero); // Başlangıçta dünya uzayında, ebeveyni yok
            _label.gameObject.SetActive(false);
            _initialLabelScale = _label.transform.localScale;

            var bg = MakeQuad(_root, "BarBg", UITheme.Background);
            bg.localScale = new Vector3(BarWidth + 0.02f, 0.07f, 1f);
            bg.localPosition = Vector3.zero;

            _barFill = MakeQuad(_root, "BarFill", UITheme.HealthFull);
            _barFill.localScale = new Vector3(BarWidth, 0.05f, 1f);

            // Hasar ve ölüm efektleri için tam ekran dörtgenler
            var flashGo = MakeQuad(null, "Damage Flash", UITheme.HealthLow);
            flashGo.SetParent(null, true);
            flashGo.localScale = new Vector3(2f, 2f, 1f);
            _flash = flashGo.GetComponent<MeshRenderer>();
            _flash.enabled = false;

            var deathFadeGo = MakeQuad(null, "Death Fade", new Color(0.05f, 0.05f, 0.05f, 0.85f));
            deathFadeGo.SetParent(null, true);
            deathFadeGo.localScale = new Vector3(2f, 2f, 1f);
            _deathFade = deathFadeGo.GetComponent<MeshRenderer>();
            _deathFade.enabled = false;
        }

        TextMesh MakeText(Transform parent, string text, Vector3 localPos)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one * 0.2f; // Ölüm sayacı için daha büyük
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = 0.06f;
            tm.fontSize = 60;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = UITheme.HealthLow; // Geri sayım rengini kırmızı yap

            // Eğer Inspector'dan özel bir font atandıysa onu kullan
            if (countdownFont != null)
                tm.font = countdownFont;
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
            mr.sharedMaterial = UITheme.CreateLitMaterial(color);
            return q.transform;
        }
    }
}
