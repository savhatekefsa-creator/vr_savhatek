using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Uzerinde serbestce calisabilecegin BOS bir sahne olusturur: Main Camera + Directional
    /// Light, baska hicbir sey yok (Unity'nin "yeni sahne" sablonuyla ayni). Isik olmadan sahne
    /// kapkara, kamera olmadan Game penceresi bos gorunurdu — o yuzden "bos" bunlari kapsar.
    ///
    /// MEVCUT SAHNENI EZMEZ. Iki ayri koruma var:
    ///  1) Dosya yolunu ONCE sorar, sahneyi SONRA olusturur. Ters sirada olsaydi kullanici
    ///     kaydetme penceresini iptal ettiginde acik sahne coktan kapanmis olurdu.
    ///  2) SampleScene.unity uzerine yazmayi acikca reddeder — orada commit'lenmemis calisma
    ///     olabilir ve Unity'nin standart "ustune yaz?" uyarisi kolayca gecistiriliyor.
    ///
    /// Olusan sahne VR ve ag icermez. Oyun icin hazir hale getirmek istersen uzerinde
    /// "2. Setup Current Scene" calistir; o menu XR rig'i, NetworkManager'i ve LAN bootstrap'i
    /// ekler ve buradaki hazir kamerayi da kendisi devre disi birakip etiketini alir.
    /// </summary>
    public static class NewSceneTool
    {
        const string SceneFolder = "Assets/Scenes";
        const string ProtectedScene = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Tools/VR Multiplayer/24. Yeni Bos Sahne Olustur")]
        public static void CreateEmptyScene()
        {
            // Acik sahnede kaydedilmemis degisiklik varsa once onu sor. Kullanici iptal ederse
            // hicbir sey yapma — yoksa suren isi sessizce kaybolur.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            string path = EditorUtility.SaveFilePanelInProject(
                "Yeni bos sahne",
                "Deneme",
                "unity",
                "Sahne dosyasina bir isim ver.",
                SceneFolder);

            if (string.IsNullOrEmpty(path))
                return; // iptal edildi

            if (path.Replace('\\', '/').Equals(ProtectedScene, System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "SampleScene.unity uzerine yazilmaz — icinde commit'lenmemis calisman " +
                    "olabilir.\n\nBaska bir isim sec.", "Tamam");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);

            if (!EditorSceneManager.SaveScene(scene, path))
            {
                EditorUtility.DisplayDialog("VR Multiplayer",
                    "Sahne kaydedilemedi:\n" + path, "Tamam");
                return;
            }

            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }

            Debug.Log("[VRMultiplayerSetup] Yeni bos sahne olusturuldu ve acildi: " + path);
            EditorUtility.DisplayDialog("VR Multiplayer",
                "Bos sahne hazir ve acildi:\n" + path + "\n\n" +
                "VR + ag eklemek icin: Tools > VR Multiplayer > 2. Setup Current Scene\n\n" +
                "Gozluge build alacaksan sahneyi File > Build Profiles listesine eklemeyi unutma; " +
                "aksi halde build eski sahneyi acar.", "Tamam");
        }
    }
}
