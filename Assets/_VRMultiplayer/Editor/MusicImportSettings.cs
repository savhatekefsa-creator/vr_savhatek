using UnityEditor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Resources/Music klasorune atilan her ses dosyasini otomatik STREAMING yapar.
    /// Fon muzigi dakikalarca surer; varsayilan DecompressOnLoad tum sarkiyi RAM'e acar
    /// (Quest'te onlarca MB bosuna). Streaming diskten okur, bellek maliyeti sabit ve
    /// kucuktur. Boylece kullanici dosyayi klasore atar, import ayariyla ugrasmaz.
    /// </summary>
    class MusicImportSettings : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            if (!assetPath.Replace('\\', '/').Contains("/Resources/Music/")) return;

            var importer = (AudioImporter)assetImporter;
            var settings = importer.defaultSampleSettings;
            settings.loadType = UnityEngine.AudioClipLoadType.Streaming;
            importer.defaultSampleSettings = settings;
        }
    }
}
