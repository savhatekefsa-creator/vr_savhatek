using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Silah secici GALERI: toplanan silahlarin gercek 3B modellerini kameranin onunde yatay bir
    /// sirada gosterir; secili olan biraz buyur ve yavas doner. TEST: TAB basili tutunca acilir,
    /// Sol/Sag ok ile kaydirilir. Ileride: Adim 3 = sag joystick, Adim 4 = birakinca secilen
    /// silah equip edilir.
    ///
    /// Onizleme modellerini <see cref="WeaponInventory"/> uretir; bu script sadece onlari
    /// konumlandirir. Olcek/aci/aralik degerleri Inspector'dan CANLI ayarlanir (saat gibi).
    /// </summary>
    public class WeaponSelectorUI : MonoBehaviour
    {
        [Header("Galeri yerlesimi (kameraya gore — gozle ayarla)")]
        [Tooltip("Galeri kameranin kac metre onunde dursun.")]
        public float distance = 1.0f;
        [Tooltip("Silahlar arasi yatay aralik (metre).")]
        public float spacing = 0.32f;
        [Tooltip("Onizleme modellerinin genel olcegi. Silah cok buyuk/kucuk gelirse ayarla.")]
        public float previewScale = 0.3f;
        [Tooltip("Secili silah bu kat kadar buyur.")]
        public float selectedBoost = 1.4f;
        [Tooltip("Onizlemelerin duruş acisi (silahin yani kameraya baksin diye — genelde Y=90).")]
        public Vector3 previewEuler = new Vector3(0f, 90f, 0f);
        [Tooltip("Secili silahin donme hizi (derece/sn, 0 = donmez).")]
        public float selectedSpin = 40f;

        int _selected;
        bool _open;

        /// <summary>Su an vurgulanan silahin envanterdeki indeksi (Adim 4 equip icin okunacak).</summary>
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

            // TEST girisi: TAB basili = galeri acik, Sol/Sag = kaydir.
            // Adim 3'te bu blok yerine sag kumanda thumbstick'i gelecek.
            bool wantOpen = false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && n > 0)
            {
                wantOpen = kb.tabKey.isPressed;
                if (wantOpen)
                {
                    if (kb.leftArrowKey.wasPressedThisFrame)  _selected = (_selected - 1 + n) % n;
                    if (kb.rightArrowKey.wasPressedThisFrame) _selected = (_selected + 1) % n;
                }
            }
#endif

            if (wantOpen != _open) { _open = wantOpen; ShowPreviews(_open); }
            if (_open)
            {
                _selected = Mathf.Clamp(_selected, 0, n - 1);
                Layout();
            }
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

                Vector3 pos = center + right * ((i - _selected) * spacing) - fwd * (sel ? 0.12f : 0f);
                p.transform.position = pos;
                p.transform.rotation = sel
                    ? face * Quaternion.Euler(0f, Time.time * selectedSpin, 0f)
                    : face;
                p.transform.localScale = Vector3.one * previewScale * (sel ? selectedBoost : 1f);
            }
        }
    }
}
