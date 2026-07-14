using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
// Not: UnityEngine.XR'i "using" yapMIYORUZ — InputSystem ile ayni isimde (InputDevice)
// tipleri var, cakisir. XR tiplerini tam adiyla (UnityEngine.XR.*) yaziyoruz.

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Silah secici GALERI: toplanan silahlarin gercek 3B modellerini kameranin onunde yatay bir
    /// sirada gosterir; secili olan biraz buyur ve yavas doner.
    ///
    /// GIRIS:
    ///  - VR: SAG kumanda thumbstick'i yana itilince galeri acilir; sag/sol itince silahlar
    ///    gecer (birakinca merkeze donunce SECIM onaylanir).
    ///  - PC (gozluksuz test): TAB basili = ac, Sol/Sag ok = kaydir.
    ///
    /// Onizleme modellerini <see cref="WeaponInventory"/> uretir. Olcek/aci/aralik Inspector'dan
    /// CANLI ayarlanir. Adim 4: onay aninda secilen silah ele equip edilecek (su an sadece log).
    /// </summary>
    public class WeaponSelectorUI : MonoBehaviour
    {
        [Header("Galeri yerlesimi (kameraya gore — gozle ayarla)")]
        public float distance = 1.0f;
        public float spacing = 0.32f;
        public float previewScale = 0.3f;
        public float selectedBoost = 1.4f;
        [Tooltip("Onizlemelerin duruş acisi (silahin yani kameraya baksin — genelde Y=90).")]
        public Vector3 previewEuler = new Vector3(0f, 90f, 0f);
        public float selectedSpin = 40f;

        [Header("Joystick")]
        [Tooltip("Galeri bu esigin uzerinde thumbstick yana itilince acilir.")]
        public float openThreshold = 0.4f;
        [Tooltip("Silah gecisi icin gereken itme miktari.")]
        public float stepThreshold = 0.65f;
        [Tooltip("Iki gecis arasi bekleme (sn) — basili tutunca cok hizli gecmesin.")]
        public float stepCooldown = 0.28f;

        int _selected;
        bool _open;
        float _nextStep;
        UnityEngine.XR.InputDevice _rightHand;

        public int SelectedIndex => _selected;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponSelectorUI");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponSelectorUI>();
        }

        void Update()
        {
            var inv = WeaponInventory.Instance;
            int n = inv != null ? inv.Entries.Count : 0;
            if (n == 0) { if (_open) { _open = false; ShowPreviews(false); } return; }

            bool wantOpen = false;
            int dir = 0;

            // --- VR: SAG thumbstick ---
            float x = RightStickX();
            if (Mathf.Abs(x) > openThreshold)
            {
                wantOpen = true;
                if (Mathf.Abs(x) > stepThreshold && Time.time >= _nextStep)
                {
                    dir = x > 0f ? +1 : -1;
                    _nextStep = Time.time + stepCooldown;
                }
            }

            // --- PC yedek: TAB + oklar (gozluksuz test) ---
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.isPressed)
            {
                wantOpen = true;
                if (kb.leftArrowKey.wasPressedThisFrame)  dir = -1;
                if (kb.rightArrowKey.wasPressedThisFrame) dir = +1;
            }
#endif

            if (dir != 0) _selected = (_selected + dir + n) % n;

            if (wantOpen != _open)
            {
                _open = wantOpen;
                ShowPreviews(_open);
                if (!_open) Confirm(); // galeri kapandi -> secimi onayla
            }
            if (_open) { _selected = Mathf.Clamp(_selected, 0, n - 1); Layout(); }
        }

        float RightStickX()
        {
            if (!_rightHand.isValid)
                _rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (_rightHand.isValid &&
                _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                return axis.x;
            return 0f;
        }

        void Confirm()
        {
            var inv = WeaponInventory.Instance;
            if (inv == null || _selected < 0 || _selected >= inv.Entries.Count) return;
            // ADIM 4: burada secilen silah ele equip edilecek. Simdilik sadece log.
            Debug.Log($"[WeaponSelector] Secim onaylandi (equip Adim 4): {inv.Entries[_selected].Key}");
        }

        void ShowPreviews(bool visible)
        {
            var inv = WeaponInventory.Instance;
            if (inv == null) return;
            foreach (var e in inv.Entries)
                if (e.Preview != null) e.Preview.SetActive(visible);
        }

        void Layout()
        {
            var cam = Camera.main;
            var inv = WeaponInventory.Instance;
            if (cam == null || inv == null) return;
            var list = inv.Entries;

            Vector3 fwd = cam.transform.forward, right = cam.transform.right, up = cam.transform.up;
            Vector3 center = cam.transform.position + fwd * distance;
            Quaternion face = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(previewEuler);

            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i].Preview;
                if (p == null) continue;
                bool sel = i == _selected;

                p.transform.position = center + right * ((i - _selected) * spacing) - fwd * (sel ? 0.12f : 0f);
                p.transform.rotation = sel
                    ? face * Quaternion.Euler(0f, Time.time * selectedSpin, 0f)
                    : face;
                p.transform.localScale = Vector3.one * previewScale * (sel ? selectedBoost : 1f);
            }
        }
    }
}
