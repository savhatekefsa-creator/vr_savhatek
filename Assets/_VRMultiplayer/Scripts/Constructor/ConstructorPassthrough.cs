using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Shows the REAL room through the headset cameras while building, then puts the game world
    /// back when you leave build mode.
    ///
    /// WHY: the arena is built to fit a physical room, but laid out blind the virtual scenery is
    /// in the way — a crate that looked well placed ends up inside your real sofa. With
    /// passthrough you position cover against the furniture and walls you can actually see.
    ///
    /// USES AR FOUNDATION, NOT A HAND-ROLLED LAYER. Passthrough on Quest is provided by the
    /// "Meta Quest: Camera (Passthrough)" OpenXR feature, which supplies an XRCameraSubsystem
    /// that <see cref="ARCameraManager"/> drives. That path also gets the required
    /// <c>com.oculus.feature.PASSTHROUGH</c> manifest entry generated for the build. An earlier
    /// attempt drove a passthrough composition layer and set the blend mode by hand; it needed
    /// project plumbing the package already does, and produced a black void when any part of it
    /// was missing.
    ///
    /// STILL TWO HALVES. Enabling the camera is not enough on its own: the scene camera must
    /// stop clearing to an opaque colour AND the virtual scenery has to be hidden, or the room
    /// is simply painted over and the player sees no difference.
    /// </summary>
    public class ConstructorPassthrough : MonoBehaviour
    {
        [Tooltip("Kapatilirsa insa modu sanal dunyada calisir (gercek oda gosterilmez).")]
        public bool enablePassthrough = true;

        [Tooltip("Insa modunda GIZLENMEYECEK kok objeler. Isim tam eslesir.")]
        public string[] alwaysKeep = { "Music" };

        public bool Active { get; private set; }

        /// <summary>
        /// Whether the passthrough camera actually came up. False means the OpenXR feature is
        /// off or the device does not provide it — see Tools > VR Multiplayer > 23.
        /// </summary>
        public bool CameraOk { get; private set; }

        ARCameraManager _arCamera;
        Camera _cam;
        CameraClearFlags _prevClear;
        Color _prevBackground;
        bool _cameraStored;

        readonly List<GameObject> _hidden = new List<GameObject>();

        /// <summary>
        /// PASSTHROUGH VARSAYILAN OLARAK KAPALI — yalnizca INSA MODU acar.
        ///
        /// Sahnedeki <see cref="ARCameraManager"/> acik kayitli (menu 23 boyle ekliyor) ve
        /// "Meta Quest: Camera (Passthrough)" OpenXR ozelligi Android'de acik. Ikisi birlikte
        /// uygulamayi TUM OTURUM boyunca alfa-harmanli kompozisyona sokuyordu: kare tamponunun
        /// alfasi 1'in altina dustugu HER YERDE gercek oda goruluyordu. Sonuc, oyuncu modunda
        /// sisin, patlamanin, hasar flasinin, olum ekraninin ve panellerin icinden gercek odanin
        /// sizmasiydi — ustelik bu efektlerin hicbiri bozuk degildi, yalnizca saydamdilar.
        ///
        /// Kullanici karari (2026-08-04): OYUNCU MODUNDA PASSTHROUGH ISTENMIYOR. Kolokasyon
        /// guvenligi (olu oyuncunun gercek odayi gormesi) bilerek feda edildi.
        ///
        /// Burada kapatmak KOK COZUM: ozellik acik kalabilir, insa modu <see cref="SetActive"/>
        /// ile istedigi an geri acar. Malzeme tarafindaki alfa duzeltmeleri (bkz.
        /// <c>UITheme.CompositeAlphaOver</c>) ikinci savunma hatti olarak durur — passthrough
        /// bir gun oyun icinde de acilirsa efektler yine delik acmaz.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void DisableAtStartup()
        {
            var cam = FindFirstObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (cam == null || !cam.enabled) return;
            cam.enabled = false;
            Debug.Log("[Passthrough] Baslangicta KAPATILDI — yalnizca insa modu acar.");
        }

        void OnDisable() => SetActive(false);

        public void SetActive(bool on)
        {
            if (Active == on) return;
            if (on && !enablePassthrough) return;

            Active = on;

            if (on)
            {
                CameraOk = EnableArCamera(true);
                MakeCameraTransparent(true);

                // GUVENLIK: passthrough kalkmadiysa sanal dunyayi GIZLEME. Gizleseydik oyuncu
                // kapkara bir boslukta kalir ve neyin bozuk oldugunu anlayamazdi.
                if (CameraOk) HideVirtualWorld();
                else Debug.LogWarning("[Constructor] Passthrough acilamadi: ARCameraManager yok " +
                                      "ya da 'Meta Quest: Camera (Passthrough)' OpenXR ozelligi " +
                                      "kapali (Tools > VR Multiplayer > 23). Sanal dunya gizlenmedi.");
            }
            else
            {
                EnableArCamera(false);
                CameraOk = false;
                MakeCameraTransparent(false);
                RestoreVirtualWorld();
            }
        }

        // ------------------------------------------------------------- passthrough camera

        bool EnableArCamera(bool on)
        {
            if (_arCamera == null) _arCamera = FindFirstObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (_arCamera == null) return false;

            _arCamera.enabled = on;
            return on;
        }

        // ------------------------------------------------------------- camera

        /// <summary>
        /// Stops the camera painting an opaque background, so passthrough is visible wherever
        /// nothing virtual is drawn. The original setting is restored exactly — leaving a
        /// transparent background behind would make the game render onto a black void.
        /// </summary>
        void MakeCameraTransparent(bool transparent)
        {
            var head = XRRigReference.HeadOrCamera;
            if (_cam == null && head != null) _cam = head.GetComponent<Camera>();
            if (_cam == null) return;

            if (transparent)
            {
                if (!_cameraStored)
                {
                    _prevClear = _cam.clearFlags;
                    _prevBackground = _cam.backgroundColor;
                    _cameraStored = true;
                }
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
            else if (_cameraStored)
            {
                _cam.clearFlags = _prevClear;
                _cam.backgroundColor = _prevBackground;
                _cameraStored = false;
            }
        }

        // ------------------------------------------------------------- world

        /// <summary>
        /// Hides the virtual scenery that would otherwise cover the real room.
        ///
        /// Only roots that actually DRAW something are touched — hiding a manager or an audio
        /// object achieves nothing and risks breaking the session. What we hid is remembered
        /// exactly, so leaving build mode restores the world even if the scene changed
        /// meanwhile (destroyed entries are skipped).
        /// </summary>
        void HideVirtualWorld()
        {
            _hidden.Clear();

            // Bootstrap objesi DontDestroyOnLoad sahnesinde yasiyor; gizlenecek sanal dunya
            // AKTIF sahnede. O yuzden kendi sahnemize degil aktif sahneye bakiyoruz.
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                if (ShouldKeep(root)) continue;
                if (root.GetComponentInChildren<Renderer>(true) == null) continue;  // cizmiyorsa bosuna

                root.SetActive(false);
                _hidden.Add(root);
            }
        }

        void RestoreVirtualWorld()
        {
            foreach (var go in _hidden)
                if (go != null) go.SetActive(true);
            _hidden.Clear();
        }

        bool ShouldKeep(GameObject root)
        {
            // Constructor'in kendi calisma zamani objeleri (~ ile baslar) ve harita koku.
            if (root.name.StartsWith("~")) return true;
            if (root.name == MapBuilder.DefaultRootName) return true;

            // Oyuncular: ellerini ve takim arkadasini gormeye devam etmelisin.
            //
            // Ama YALNIZCA calisma zamaninda dogan ag objeleri. Sahneye elle konmus silahlar da
            // NetworkObject tasiyor (InScenePlaced) — onlari tutmak, gercek odayi gormek icin
            // acilan passthrough'u yerde duran 23 silahla doldururdu.
            var netObj = root.GetComponentInChildren<NetworkObject>(true);
            if (netObj != null && !netObj.InScenePlaced) return true;

            // XR rig (kamera ve el capalari) asla kapatilmaz.
            var rig = XRRigReference.Instance;
            if (rig != null && rig.transform.root == root.transform) return true;

            if (alwaysKeep != null)
                foreach (var n in alwaysKeep)
                    if (root.name == n) return true;

            return false;
        }

        void OnDestroy() => RestoreVirtualWorld();
    }
}
