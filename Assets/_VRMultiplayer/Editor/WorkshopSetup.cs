using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah Atolyesi'ni sahneye ekler. Atolye bir sahne nesnesidir cunku cihazda build
    /// icinde yasamasi gerekiyor; prefaba gommek yerine sahneye eklemek, sevkiyat build'inde
    /// unutulup kalma riskini azaltir (nesne sahnede goze carpar).
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
                    "\"open\" kutusunu isaretle, sevkiyattan once KALDIR.", "Tamam");
                return;
            }

            var go = new GameObject(ObjectName);
            var ws = go.AddComponent<WeaponWorkshop>();
            ws.open = true;
            Undo.RegisterCreatedObjectUndo(go, "Silah Atolyesi ekle");
            Selection.activeGameObject = go;
            EditorUtility.SetDirty(go);
            Debug.Log("[Atolye] Sahneye eklendi ve acildi. Build alip kulaklikta kullan; " +
                      "sevkiyat build'inden ONCE bu nesneyi sahneden kaldir.");
        }
    }
}
