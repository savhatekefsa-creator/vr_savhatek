using Unity.Netcode;
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
    /// GIRIS — grip'i tut, stick ile gez, stick ile sec:
    ///  - GRIP BASILI: galeri sadece grip basiliyken yasar. Birakirsan kapanir ve silah
    ///    (HandGrabber'in normal isi) cantaya gider. Bu sart, secim boyunca elin hep "tutuyor"
    ///    kalmasini garantiler — grip'in yarim kaldigi ara durumlarda silah havada asili
    ///    kaliyordu. Grip ORTA parmak, stick BASPARMAK: ayni anda rahat kullanilir.
    ///  - Stick'i YANA it -> galeri acilir; her YENI itis TEK adim kaydirir. Merkeze donmeden
    ///    ikinci adim sayilmaz (snap-turn'un _snapReady deseni). Onceden itili tuttukca surekli
    ///    kayiyordu: 2-3 silahla A>B>A>B doner, secim yapilamazdi.
    ///  - Stick'i YUKARI it -> secer ve galeri kapanir. Sag stick tamamen bos: snap-turn
    ///    kapatildi (XRRigLocomotion.snapTurnEnabled), yurume sol stick'te. Grip SECIM tusu
    ///    YAPILAMAZ — o kapma/birakma tusu; denendi, ara durumlarda silah havada asili kaldi.
    ///  - PC (gozluksuz test): TAB acar, Sol/Sag ok kaydirir, Enter secer (grip sarti aranmaz).
    ///
    /// Onizleme modellerini <see cref="WeaponInventory"/> uretir. Olcek/aci/aralik Inspector'dan
    /// CANLI ayarlanir.
    ///
    /// ONAY -> EQUIP: elindeki silah yok olur (despawn = "cantaya girdi"), secilen turden TAZE bir
    /// tane sunucuda uretilip eline verilir (HandGrabber.RequestWeaponSwap). Silahlar rafta
    /// sinirsiz oldugu icin cantanin belirli bir NESNEYI saklamasi gerekmez — tur yeter.
    /// Mermi sistemi gelirse buraya eklenecek tek sey Entry'de bir mermi sayisi olacak.
    /// </summary>
    public class WeaponSelectorUI : MonoBehaviour
    {
        [Header("Galeri yerlesimi (kameraya gore — Play'de canli ayarlanir)")]
        public float distance = 1.0f;
        [Tooltip("Silahlar arasi mesafe. previewScale'i buyutursen bunu da buyut, yoksa ic ice girerler.")]
        public float spacing = 0.75f;
        public float previewScale = 0.8f;
        [Tooltip("Secili silah bu kat kadar buyur.")]
        public float selectedBoost = 1.6f;
        [Tooltip("Onizlemelerin duruş acisi (silahin yani kameraya baksin — genelde Y=90).")]
        public Vector3 previewEuler = new Vector3(0f, 90f, 0f);
        public float selectedSpin = 40f;

        [Header("Joystick")]
        [Tooltip("Thumbstick bu kadar itilince sayilir: yana = ac/kaydir, yukari = sec.")]
        public float openThreshold = 0.4f;

        int _selected;
        bool _open;
        bool _stickReady = true;  // itis basina TEK adim: merkeze donmeden ikincisi sayilmaz
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
            if (n == 0) { SetOpen(false); return; }

            // Galeri SADECE grip basiliyken yasar. Birakirsan kapanir ve HandGrabber kendi
            // normal isini yapar (silah cantaya gider).
            if (!GripHeld()) { SetOpen(false); return; }

            Vector2 stick = RightStick();
            int dir = 0;
            bool flick = false;

            // ITIS BASINA TEK ADIM: merkeze donmeden ikinci adim sayilmaz.
            if (Mathf.Abs(stick.x) < openThreshold * 0.5f) _stickReady = true;
            else if (_stickReady && Mathf.Abs(stick.x) > openThreshold)
            {
                _stickReady = false;
                flick = true;
                if (_open) dir = stick.x > 0f ? +1 : -1; // ilk itis ACAR, sonrakiler kaydirir
            }

            // --- PC yedek: TAB acar, oklar kaydirir (gozluksuz test) ---
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.tabKey.wasPressedThisFrame) flick = true;
                if (_open && kb.leftArrowKey.wasPressedThisFrame)  { dir = -1; flick = true; }
                if (_open && kb.rightArrowKey.wasPressedThisFrame) { dir = +1; flick = true; }
            }
#endif

            if (flick)
            {
                if (!_open) SetOpen(true);
                else if (dir != 0) _selected = (_selected + dir + n) % n;
            }

            if (!_open) return;
            _selected = Mathf.Clamp(_selected, 0, n - 1);
            Layout();

            // YUKARI = SEC. Sag stick tamamen bos (snap-turn kapali, yurume sol stick'te).
            if (stick.y > openThreshold) { Confirm(); SetOpen(false); return; }
#if ENABLE_INPUT_SYSTEM
            if (kb != null && kb.enterKey.wasPressedThisFrame) { Confirm(); SetOpen(false); }
#endif
        }

        // Not: galeri acikken hareket ENGELLENMIYOR — silah secerken de yuruyebilirsin. Bir ara
        // XRRigLocomotion'i gecici kapatiyorduk ama o sadece snap-turn cakismasi icindi (sag
        // stick hem donduruyor hem seciyordu); snap-turn tamamen kapatilinca gerek kalmadi.
        void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            ShowPreviews(open);
        }

        void OnDisable() => _open = false;

        /// <summary>SAG grip basili mi? VR yoksa (PC testi) true doner. HandGrabber ile ayni
        /// okuma: bazi OpenXR profilleri butonu vermez, sadece analog degeri verir.</summary>
        bool GripHeld()
        {
            if (!_rightHand.isValid)
                _rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (!_rightHand.isValid) return true; // VR bagli degil -> sart aranmaz
            if (_rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool b) && b) return true;
            return _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float g) && g > 0.5f;
        }

        Vector2 RightStick()
        {
            if (!_rightHand.isValid)
                _rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (_rightHand.isValid &&
                _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                return axis;
            return Vector2.zero;
        }

        HandGrabber _grabber;

        HandGrabber LocalGrabber()
        {
            if (_grabber != null) return _grabber;
            foreach (var hg in FindObjectsByType<HandGrabber>(FindObjectsSortMode.None))
                if (hg.IsOwner) { _grabber = hg; break; }
            return _grabber;
        }

        // Su an tuttugum silah, SUNUCUNUN gercegine gore. HandGrabber'in ic durumuna bilerek
        // bakmiyoruz: PC test araci silahi HandGrabber KAPALIYKEN tasiyor, tek ortak dogru
        // "holder benim mi" sorusu.
        static GrabbableObject MyWeapon()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !(nm.IsServer || nm.IsConnectedClient)) return null;
            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
                if (g.HolderClientId == nm.LocalClientId) return g;
            return null;
        }

        void Confirm()
        {
            var inv = WeaponInventory.Instance;
            if (inv == null || _selected < 0 || _selected >= inv.Entries.Count) return;
            var e = inv.Entries[_selected];

            var grabber = LocalGrabber();
            if (grabber == null) return;

            // Elindeki silahi tekrar secmek bos islem — ayni silah geri gelirdi. Mermi sistemi
            // gelince bu ayni zamanda bir ACIK olurdu (galeriyi acip kapamak = bedava sarjor,
            // kol hareketiyle reload'dan hizli), o yuzden simdiden kapali.
            var cur = MyWeapon();
            if (cur != null && WeaponInventory.TypeKey(cur) == e.Key) return;

            if (e.Prefab == null)
            {
                Debug.LogWarning($"[WeaponSelector] '{e.Key}' equip edilemez: Resources/WeaponPrefabs " +
                                 "altinda kalibi yok (Tools > VR Multiplayer > 38 ile uretilebilir).");
                return;
            }

            // Elimdekinin mermisini TAM SU AN kaydet. Envanterin 0.3 sn'lik taramasi bayat
            // olabilir; son yarim saniyede attigin mermiler bedavaya geri gelmesin.
            if (cur != null)
            {
                var curNw = cur.GetComponent<NetworkWeapon>();
                var curEntry = inv.Find(WeaponInventory.TypeKey(cur));
                if (curNw != null && curNw.UsesAmmo && curEntry != null)
                {
                    curEntry.Ammo = curNw.Ammo;
                    curEntry.Spares = curNw.SpareMagazines;
                }
            }

            Debug.Log($"[WeaponSelector] Equip: {e.Key} — {(e.Ammo < 0 ? "dolu" : e.Ammo + " mermi")}" +
                      $"  (eski: {(cur != null ? cur.name : "yok")})");
            grabber.RequestWeaponSwap(cur, e.Prefab, e.Ammo, e.Spares);
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
