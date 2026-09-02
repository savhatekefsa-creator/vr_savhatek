using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah Atolyesi'ni sahneye ekler. Atolye bir sahne nesnesidir cunku cihazda build
    /// icinde yasamasi gerekiyor.
    ///
    /// SAHNEDEN KALDIRMAK GEREKMIYOR: WeaponWorkshop yalnizca editorde ve GELISTIRME
    /// build'inde uyaniyor, normal build'de Awake'te kendini kapatiyor. Eskiden koruma
    /// "kaldirmayi unutma" idi; unutulunca oyuncu atolyeyi acabiliyordu.
    /// </summary>
    public static class WorkshopSetup
    {
        const string ObjectName = "SilahAtolyesi";

        [MenuItem("Tools/VR Multiplayer/53. Silah Atolyesini Sahneye Ekle")]
        public static void Add()
        {
            var existing = GameObject.Find(ObjectName);
            if (existing != null)
            {
                Selection.activeGameObject = existing;
                EditorUtility.DisplayDialog("Atolye zaten var",
                    "Sahnede " + ObjectName + " zaten duruyor. Acmak icin bileşendeki " +
                    "\"open\" kutusunu isaretle. Sahnede kalmasi zararsiz: atolye " +
                    "yalnizca editorde ve GELISTIRME build'inde calisir.", "Tamam");
                return;
            }

            var go = new GameObject(ObjectName);
            var ws = go.AddComponent<WeaponWorkshop>();
            ws.open = true;
            Undo.RegisterCreatedObjectUndo(go, "Silah Atolyesi ekle");
            Selection.activeGameObject = go;
            EditorUtility.SetDirty(go);
            Debug.Log("[Atolye] Sahneye eklendi ve acildi. Kulaklikta kullanmak icin " +
                      "GELISTIRME build'i (Development Build) al - normal build'de atolye " +
                      "kendini kapatir.");
        }
    }
}
