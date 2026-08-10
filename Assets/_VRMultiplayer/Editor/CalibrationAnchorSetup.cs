using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// FAZ 1 kurulumu (bkz. PLAN-kalibrasyon.md) — kalibrasyon drift duzeltmesi icin gereken
    /// iki sey:
    ///   1) Meta "Anchors" OpenXR ozelligi (Android) — BUILD ZAMANI ayaridir, runtime'da acilamaz
    ///   2) Sahnede ARSession + rig uzerinde ARAnchorManager
    ///
    /// (2) runtime'da <see cref="CalibrationAnchor"/> tarafindan da eklenebiliyor, ama (1) icin
    /// bu menu ZORUNLU: ozellik kapaliyken anchor olusturma sessizce basarisiz olur ve sistem
    /// A/B tek seferlik hizalamaya duser.
    /// </summary>
    public static class CalibrationAnchorSetup
    {
        [MenuItem("Tools/VR Multiplayer/45. Setup Calibration Anchor (drift duzeltme)")]
        public static void SetupCalibrationAnchor()
        {
            // ---------------------------------------------------------- 1) OpenXR ozelligi
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            bool anchorFeatureOn = false;
            bool anchorFeatureFound = false;

            if (settings != null)
            {
                foreach (var f in settings.GetFeatures<OpenXRFeature>())
                {
                    string tn = f.GetType().FullName ?? "";
                    if (!tn.Contains("Features.Meta")) continue;

                    // Session ozelligi anchor icin de sart (subsystem onun uzerinde kosuyor).
                    bool isAnchor = tn.Contains("ARAnchorFeature");
                    bool isSession = tn.Contains("Session");
                    if (!isAnchor && !isSession) continue;

                    if (isAnchor) anchorFeatureFound = true;
                    if (!f.enabled)
                    {
                        f.enabled = true;
                        EditorUtility.SetDirty(f);
                    }
                    if (isAnchor) anchorFeatureOn = f.enabled;
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            if (!anchorFeatureFound)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Meta 'Anchors' OpenXR ozelligi BULUNAMADI.\n\n" +
                    "com.unity.xr.meta-openxr paketi kurulu mu kontrol et\n" +
                    "(Project Settings > XR Plug-in Management > OpenXR > Android).", "Tamam");
                return;
            }

            // ---------------------------------------------------------- 2) Sahne
            var rigRef = Object.FindFirstObjectByType<XRRigReference>();
            if (rigRef == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Sahnede XR Rig yok. Once menu 2 (Setup Current Scene) calistir.", "Tamam");
                return;
            }

            // XROrigin: anchor'lar (trackable'lar) bunun altinda yasar. Menu 11 bunu zaten
            // kuruyor; burada yoksa kurmuyoruz — dikey referans ayarlari (Floor mode +
            // CameraYOffset = 0) orada, ikiye bolmek hataya davetiye.
            var origin = rigRef.GetComponent<XROrigin>();
            if (origin == null)
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Rig uzerinde XROrigin yok.\n\n" +
                    "Once menu 11 (Setup Room Scan) calistir — XROrigin'i dogru\n" +
                    "dikey ayarlarla (Floor mode, CameraYOffset = 0) o kuruyor.", "Tamam");
                return;
            }

            bool addedSession = false;
            if (Object.FindFirstObjectByType<ARSession>() == null)
            {
                new GameObject("AR Session").AddComponent<ARSession>();
                addedSession = true;
            }

            bool addedMgr = false;
            if (rigRef.GetComponent<ARAnchorManager>() == null)
            {
                rigRef.gameObject.AddComponent<ARAnchorManager>();
                addedMgr = true;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            EditorUtility.DisplayDialog("VR Multiplayer",
                "Kalibrasyon anchor kurulumu tamam.\n\n" +
                "• Meta 'Anchors' OpenXR ozelligi: " + (anchorFeatureOn ? "ACIK" : "ACILAMADI") + "\n" +
                "• ARSession: " + (addedSession ? "eklendi" : "zaten vardi") + "\n" +
                "• ARAnchorManager: " + (addedMgr ? "rig'e eklendi" : "zaten vardi") + "\n\n" +
                "SIMDI: Ctrl+S ile sahneyi kaydet ve gozluge YENIDEN BUILD al.\n" +
                "(OpenXR ozelligi build zamani ayaridir — build almadan etkisi olmaz.)\n\n" +
                "CIHAZDA KONTROL — Unity Console / logcat:\n" +
                "  [XROrigin]    Floor modu verildi mi (dikey referans teshisi)\n" +
                "  [CalibAnchor] Kalibre olunca 'Anchor olusturuldu' satiri", "Tamam");
        }
    }
}
