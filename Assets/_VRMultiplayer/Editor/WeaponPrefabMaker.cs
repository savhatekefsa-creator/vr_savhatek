using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Sahnede elle kurulmus bir silahi SPAWN EDILEBILIR bir prefaba cevirir.
    ///
    ///   Tools ▸ VR Multiplayer ▸ 32. Make Spawnable Weapon Prefab (select weapon)
    ///
    /// NEDEN: <see cref="WeaponPrefabRegistrar"/> Resources/WeaponPrefabs altindaki HER prefabi
    /// otomatik olarak NetworkManager'a kaydeder — yani oraya konan silah calisma aninda
    /// spawn edilebilir hale gelir. Sahneye elle konmus bir silah ise tek bir kopyadir:
    /// cogaltilamaz, secici galeriden "yeni bir tane ver" denemez.
    ///
    /// SAHNEYI DEGISTIRMEZ. Objeyi Hierarchy'den klasore SURUKLEMEK, sahnedeki objeyi de yeni
    /// prefaba baglar ve sahne dosyasini kirletir (sahne bu projenin en riskli paylasilan
    /// dosyasi). Bu arac SaveAsPrefabAsset kullanir — SaveAsPrefabAssetAndConnect DEGIL — yani
    /// sahnedeki obje oldugu gibi kalir.
    ///
    /// AD: "Weapon_&lt;silahadi&gt;" (orn. Rifle_HK416 -> Weapon_Rifle_HK416). Tutus profilleri
    /// silahi ADIYLA bulur (HK416 profili "Contains: Rifle_HK416" ile eslesir), bu yuzden ad
    /// korunmali. Arac eslesmeyi ayrica DOGRULAR ve sonucu raporlar.
    /// </summary>
    public static class WeaponPrefabMaker
    {
        const string PrefabFolder = "Assets/_VRMultiplayer/Resources/WeaponPrefabs";

        [MenuItem("Tools/VR Multiplayer/32. Make Spawnable Weapon Prefab (select weapon)")]
        public static void CreateFromSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Warn("Once sahnede bir silah GameObject'i sec, sonra bu menuyu calistir.");
                return;
            }

            // Silah mi? Bu ikisi olmadan ne tutulabilir ne de aga kaydedilebilir.
            if (go.GetComponent<GrabbableObject>() == null || go.GetComponent<NetworkObject>() == null)
            {
                Warn($"'{go.name}' silah degil: uzerinde GrabbableObject + NetworkObject olmali.\n\n" +
                     "Once 'Tools > VR Multiplayer > 10. Make Selected Grabbable' ile kur.");
                return;
            }

            string cleanName = WeaponGripBinder.CleanName(go.name);
            string prefabName = cleanName.StartsWith("Weapon_") ? cleanName : "Weapon_" + cleanName;
            string path = $"{PrefabFolder}/{prefabName}.prefab";

            if (!Directory.Exists(PrefabFolder))
                Directory.CreateDirectory(PrefabFolder);

            if (File.Exists(path) &&
                !EditorUtility.DisplayDialog("Silah Prefabi",
                    $"'{prefabName}.prefab' zaten var. Uzerine yazilsin mi?", "Uzerine yaz", "Vazgec"))
                return;

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path, out bool ok);
            if (!ok || prefab == null)
            {
                Warn($"Prefab olusturulamadi: {path}");
                return;
            }

            AssetDatabase.Refresh();

            // Tutus profili bu ADLA gercekten eslesiyor mu? Eslesmezse silah ele yanlis oturur ve
            // sebebi cok gec anlasilir — o yuzden simdi soyluyoruz.
            var profile = WeaponGripBinder.FindProfile(prefabName);
            string gripLine = profile != null
                ? $"✔ Tutus profili eslesti: {profile.name}"
                : "✖ UYARI: Bu adla eslesen tutus profili YOK — silah ele duzgun oturmaz.\n" +
                  "   Profilin weaponNameContains/Equals alanini kontrol et.";

            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);

            EditorUtility.DisplayDialog("Silah Prefabi Hazir",
                $"{prefabName}.prefab olusturuldu.\n{path}\n\n" +
                $"{gripLine}\n\n" +
                "Bundan sonra otomatik: WeaponPrefabRegistrar bu prefabi aga kaydeder, silah\n" +
                "calisma aninda spawn edilebilir olur. Sahne DEGISMEDI.\n\n" +
                "NOT: Herkeste ayni prefab listesi olmali — birlikte test etmeden once\n" +
                "bu dosyayi commit + push et.", "Tamam");

            Debug.Log($"[WeaponPrefabMaker] {path} olusturuldu. {gripLine}", prefab);
        }

        static void Warn(string msg) => EditorUtility.DisplayDialog("Silah Prefabi", msg, "Tamam");
    }
}
