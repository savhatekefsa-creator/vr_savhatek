using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.UI;

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

        [Tooltip("Can barinin yeni degere kayma hizi (bar boyu / saniye).")]
        public float barSlideSpeed = 1.2f;
        [Tooltip("Can degisince bar bu kadar saniye gorunur kalir, sonra gizlenir.")]
        public float showDuration = 4f;
        [Tooltip("Can bu oranin (0-1) altindayken bar surekli gorunur.")]
        public float alwaysShowBelow = 0.35f;

        PlayerHealth _health;
        PlayerIdentity _identity;

        Transform _root;      // billboarded container
        Transform _barFill;
        DamageDirectionFlash _dirFlash; // hasarin geldigi yonde kenar parlamasi
        LowHealthVignette _vignette;    // dusuk canda kenar kizarmasi
        RespawnGuide _respawnGuide;     // olu/bekleyen ekrani: gri perde + bolge yonlendirmesi
        int _lastHealth = PlayerHealth.MaxHealth;
        float _targetRatio = 1f;
        float _displayedRatio = -1f;   // -1: ilk deger henuz uygulanmadi (animasyonsuz atanir)
        MeshRenderer _barFillMr;
        float _visibleUntil;           // bar bu ana kadar gorunur kalir
        float _hudScale = 1f;

        const float BarWidth = 0.32f;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) { enabled = false; return; }
            _health = GetComponent<PlayerHealth>();
            if (_health == null) { enabled = false; return; }
            _identity = GetComponent<PlayerIdentity>();
            Debug.Log("[PlayerHUD] OnNetworkSpawn calisti. Can degeri: " + _health.Health.Value);

            BuildHud();
            _lastHealth = _health.Health.Value;
            _health.Health.OnValueChanged += OnHealthChanged;
            _health.Dead.OnValueChanged += OnDeadChanged;
            RefreshBar(_health.Health.Value);
            _visibleUntil = Time.time + showDuration; // girista bir kez gosterip gizle
        }

        public override void OnNetworkDespawn()
        {
            if (_health != null)
            {
                _health.Health.OnValueChanged -= OnHealthChanged;
                _health.Dead.OnValueChanged -= OnDeadChanged;
            }
            if (_root != null) Destroy(_root.gameObject);
            // Ebeveynsiz efekt objeleri de temizlenmeli, yoksa oyuncu ayrilinca sahnede kalirlar.
            if (_respawnGuide != null) Destroy(_respawnGuide.gameObject);
            if (_dirFlash != null) Destroy(_dirFlash.gameObject);
            if (_vignette != null) Destroy(_vignette.gameObject);
        }

        void OnHealthChanged(int prev, int now)
        {
            _visibleUntil = Time.time + showDuration;
            RefreshBar(now);
            if (now < prev)
            {
                // Hasar: geldigi yonde kenar flasi + iki kontrolcuye titresim.
                if (_dirFlash != null) _dirFlash.TriggerFromRecentShot(transform.position);
                Rumble();
            }
            _lastHealth = now;
        }

        // Perde/metin her kare Update'te beslenir (mesafe ve geri sayim surekli degisiyor);
        // burada yalnizca ANLIK geri bildirim kalir.
        void OnDeadChanged(bool _, bool dead)
        {
            if (dead) Rumble();
        }

        void Update()
        {
            if (_root == null) return;

#if UNITY_EDITOR
            DebugTestKeys();
#endif

            // Bar, hedef degere dogru surgu gibi yumusakca kayar.
            if (_displayedRatio >= 0f && _displayedRatio != _targetRatio)
                ApplyBarRatio(Mathf.MoveTowards(_displayedRatio, _targetRatio, barSlideSpeed * Time.deltaTime));

            UpdateHudVisibility();

            // Olu / henuz oyuna girmemis oyuncunun ekrani. Mesafe ve geri sayim her kare
            // degistigi icin durum olay bazli degil, surekli beslenir.
            if (_respawnGuide != null && _health != null)
            {
                _respawnGuide.SetState(
                    _health.Dead.Value,
                    _identity != null ? _identity.Team.Value : (byte)0,
                    _health.InSpawnZone.Value,
                    _health.SpawnProgress.Value,
                    _health.spawnHoldSeconds);
            }

            // VR rig yoksa (Editor'de Game penceresinden test) ana kamera kafa yerine gecer.
            var rig = XRRigReference.Instance;
            Transform head = rig != null && rig.head != null ? rig.head
                           : (Camera.main != null ? Camera.main.transform : null);
            if (head != null)
            {
                Vector3 fwd = head.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = head.right; right.y = 0f;
                if (right.sqrMagnitude < 0.01f) right = Vector3.right;
                right.Normalize();
                // Bottom-RIGHT of view so it never blocks aim.
                if (_root.gameObject.activeSelf)
                {
                    _root.position = head.position + fwd * 1.1f + right * 0.55f - Vector3.up * 0.42f;
                    _root.rotation = Quaternion.LookRotation(_root.position - head.position);
                }

                // Olum perdesi ve metni kendi LateUpdate'inde kafayi takip eder (RespawnGuide).
            }
        }

        // Bar surekli gorunmez: can degistikten sonra bir sure ve can dusukken gorunur;
        // diger zamanlarda kuculerek kaybolur (VR'da ekran kalabaligini azaltir).
        void UpdateHudVisibility()
        {
            bool alive = _targetRatio > 0f;
            bool want = alive && (_targetRatio <= alwaysShowBelow || Time.time < _visibleUntil);
            _hudScale = Mathf.MoveTowards(_hudScale, want ? 1f : 0f, Time.deltaTime / 0.25f);
            _root.localScale = Vector3.one * _hudScale;
            bool active = _hudScale > 0f;
            if (_root.gameObject.activeSelf != active) _root.gameObject.SetActive(active);
        }

        void RefreshBar(int hp)
        {
            _targetRatio = Mathf.Clamp01((float)hp / PlayerHealth.MaxHealth);
            if (_vignette != null) _vignette.SetHealthRatio(_targetRatio);

            // Ilk deger animasyonsuz uygulanir; sonrakiler Update icinde surgu gibi kayar.
            // Gorunurluk UpdateHudVisibility'de yonetilir.
            if (_displayedRatio < 0f)
                ApplyBarRatio(_targetRatio);
        }

        void ApplyBarRatio(float ratio)
        {
            _displayedRatio = ratio;
            if (_barFill == null) return;
            _barFill.localScale = new Vector3(BarWidth * ratio, 0.05f, 1f);
            _barFill.localPosition = new Vector3(-BarWidth * 0.5f + BarWidth * ratio * 0.5f, 0f, -0.001f);
            if (_barFillMr != null)
                UITheme.SetGradientFill(_barFillMr.sharedMaterial, ratio);
        }

        void Rumble()
        {
            for (int i = 0; i < 2; i++)
            {
                var dev = InputDevices.GetDeviceAtXRNode(i == 0 ? XRNode.LeftHand : XRNode.RightHand);
                if (dev.isValid) dev.SendHapticImpulse(0, 0.8f, 0.15f);
            }
        }

        // Eski sabit geri sayim (respawnDelay saniye) KALDIRILDI: dogum artik sureye degil
        // dogum cemberinde durmaya bagli, geri sayimi RespawnGuide sunucudan gelen
        // SpawnProgress ile cizer.

#if UNITY_EDITOR
        // ------------------------------------------------------ editor test kisayollari
        // Ikinci oyuncu / VR gozluk olmadan arayuzu test icin:
        //   H = kendine 10 hasar ver (yalnizca Host'ta calisir; bar, dusuk can, olum,
        //       geri sayim ve yeniden dogma dahil tum gercek akisi tetikler)
        //   J = rastgele bir yonden vurulmus gibi yonlu flas onizlemesi (hasarsiz)
        void DebugTestKeys()
        {
            bool damageKey = false, flashKey = false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                damageKey = kb.hKey.wasPressedThisFrame;
                flashKey = kb.jKey.wasPressedThisFrame;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            damageKey = Input.GetKeyDown(KeyCode.H);
            flashKey = Input.GetKeyDown(KeyCode.J);
#endif
            if (damageKey && _health != null)
            {
                if (IsServer) _health.ServerApplyDamage(10, OwnerClientId);
                else Debug.Log("[PlayerHUD] Test hasari (H) sadece Host olarak oynarken uygulanabilir.");
            }
            if (flashKey && _dirFlash != null)
            {
                Vector2 r = Random.insideUnitCircle.normalized;
                Vector3 src = transform.position + new Vector3(r.x, 0f, r.y) * 5f + Vector3.up * 1.5f;
                Debug.Log($"[PlayerHUD] Test flasi (J): kaynak yonu {src - transform.position}");
                _dirFlash.FlashFrom(src);
            }
        }
#endif

        // ------------------------------------------------------------- build

        void BuildHud()
        {
            Debug.Log("[PlayerHUD] BuildHud baslatildi.");
            _root = new GameObject("Combat HUD").transform;

            // Yuzen can bari kaldirildi (can artik kol saati ekraninda gosteriliyor).
            // _root bos bir tasiyici olarak kalir; olum ekrani/flas/vignette ondan bagimsizdir.

            // Hasar yonu flasi: vurulunca hasarin geldigi tarafta kisa bir kenar parlamasi.
            _dirFlash = new GameObject("Damage Direction Flash").AddComponent<DamageDirectionFlash>();

            // Dusuk can vignette'i: can azaldikca gorus kenarlarinda kirmizi kizarma.
            _vignette = new GameObject("Low Health Vignette").AddComponent<LowHealthVignette>();

            // Olu / dogum bekleyen ekrani: gri perde + takim bolgesine yonlendirme + geri sayim.
            _respawnGuide = new GameObject("Respawn Guide").AddComponent<RespawnGuide>();
            _respawnGuide.SetFont(countdownFont);
            Debug.Log("[PlayerHUD] BuildHud tamamlandi.");
        }
    }
}

