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

        [Tooltip("Can barinin yeni degere kayma hizi (bar boyu / saniye).")]
        public float barSlideSpeed = 1.2f;
        [Tooltip("Can degisince bar bu kadar saniye gorunur kalir, sonra gizlenir.")]
        public float showDuration = 4f;
        [Tooltip("Can bu oranin (0-1) altindayken bar surekli gorunur.")]
        public float alwaysShowBelow = 0.35f;

        PlayerHealth _health;

        Transform _root;      // billboarded container
        Transform _barFill;
        TextMesh _label;
        DamageDirectionFlash _dirFlash; // hasarin geldigi yonde kenar parlamasi
        LowHealthVignette _vignette;    // dusuk canda kenar kizarmasi
        MeshRenderer _deathFade; // Siyah ekran kararması
        int _lastHealth = PlayerHealth.MaxHealth;
        float _targetRatio = 1f;
        float _displayedRatio = -1f;   // -1: ilk deger henuz uygulanmadi (animasyonsuz atanir)
        MeshRenderer _barFillMr;
        float _visibleUntil;           // bar bu ana kadar gorunur kalir
        float _hudScale = 1f;
        Vector3 _initialLabelScale;
        Coroutine _respawnCountdown;

        const float BarWidth = 0.32f;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) { enabled = false; return; }
            _health = GetComponent<PlayerHealth>();
            if (_health == null) { enabled = false; return; }
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
            if (_label != null) Destroy(_label.gameObject);
            if (_deathFade != null) Destroy(_deathFade.gameObject);
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

                // Ölüm kararması her zaman kamerayı takip eder.
                Transform effectParent = _deathFade != null && _deathFade.enabled ? _deathFade.transform : null;
                if (effectParent != null)
                {
                    effectParent.SetPositionAndRotation(
                        head.position + head.forward * 0.5f, // Biraz daha önde
                        head.rotation);
                }

                // Ölüm ekranı metni her zaman kameranın önünde ve ortasında durur.
                if (_label != null && _label.gameObject.activeSelf)
                {
                    _label.transform.position = head.position + head.forward * 1.2f;
                    _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - head.position);
                }
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

            _label = MakeText(null, "", Vector3.zero); // Başlangıçta dünya uzayında, ebeveyni yok
            _label.gameObject.SetActive(false);
            _initialLabelScale = _label.transform.localScale;

            var bg = MakeQuad(_root, "BarBg", UITheme.Background);
            bg.localScale = new Vector3(BarWidth + 0.02f, 0.07f, 1f);
            bg.localPosition = Vector3.zero;

            _barFill = MakeQuad(_root, "BarFill", UITheme.HealthFull);
            _barFill.localScale = new Vector3(BarWidth, 0.05f, 1f);
            _barFillMr = _barFill.GetComponent<MeshRenderer>();
            _barFillMr.sharedMaterial = UITheme.CreateHealthBarMaterial();
            if (_barFill == null)
            {
                Debug.LogError("[PlayerHUD] HATA: _barFill objesi olusturulamadi!");
                return;
            }

            // Hasar yonu flasi: vurulunca hasarin geldigi tarafta kisa bir kenar parlamasi.
            _dirFlash = new GameObject("Damage Direction Flash").AddComponent<DamageDirectionFlash>();

            // Dusuk can vignette'i: can azaldikca gorus kenarlarinda kirmizi kizarma.
            _vignette = new GameObject("Low Health Vignette").AddComponent<LowHealthVignette>();

            var deathFadeGo = MakeQuad(null, "Death Fade", new Color(0.05f, 0.05f, 0.05f, 0.85f));
            deathFadeGo.SetParent(null, true);
            deathFadeGo.localScale = new Vector3(2f, 2f, 1f);
            _deathFade = deathFadeGo.GetComponent<MeshRenderer>();
            _deathFade.enabled = false;
            Debug.Log("[PlayerHUD] BuildHud tamamlandi.");
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
