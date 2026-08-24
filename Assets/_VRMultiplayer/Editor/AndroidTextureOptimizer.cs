using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah paketi dokularina ANDROID platform override'i basar: max 1024 + ASTC 6x6.
    ///
    /// Neden: paket dokulari "DefaultTexturePlatform" ayarinda 4096 + textureCompression 0
    /// (SIKISTIRMASIZ) geliyor ve hicbirinde Android override'i yok. Tek bir 4096 RGBA doku
    /// bellekte mipmap'lerle ~85 MB tutuyor; sahnedeki 11 silah bunu gigabaytlara cikariyor.
    /// Sonuc: APK 2.39 GB ve gozlukte sahne yuklenirken Android uygulamayi OOM ile olduruyor
    /// (siyah ekran). 1024 + ASTC 6x6 ile doku basina ~0.5 MB'a iniyor.
    ///
    /// VR'da silahlar elde tutuluyor — 1024 fazlasiyla yeterli, 4K zaten ayirt edilemiyor.
    ///
    /// Menu 38 uygular, menu 39 sadece RAPORLAR (hicbir dosyaya dokunmaz).
    /// Degisiklik .png.meta dosyalarina yazilir; geri almak icin:
    ///   git checkout -- "Assets/FPS Gun Pack 4K" "Assets/Gece Studio"
    /// </summary>
    public static class AndroidTextureOptimizer
    {
        // 2.3 GB'lik agirligi tasiyan sanat klasorleri. Proje UI/HUD dokularina dokunulmuyor —
        // onlar zaten kucuk ve dusuk cozunurluk metinde bozulma yapar.
        static readonly string[] TargetRoots =
        {
            "Assets/FPS Gun Pack 4K",
            "Assets/Gece Studio",
        };

        const int MaxSize = 1024;
        const TextureImporterFormat Format = TextureImporterFormat.ASTC_6x6;
        const string Platform = "Android";

        // ------------------------------------------------------------------ giris noktalari

        [MenuItem("Tools/VR Multiplayer/38. Silah Dokularini Android'e Optimize Et (1024 ASTC)")]
        public static void ApplyMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Android Doku Optimizasyonu",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }

            var paths = FindTextures();
            if (paths.Count == 0)
            {
                Debug.LogWarning("[AndroidTextureOptimizer] Hedef klasorlerde doku bulunamadi.");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("Android Doku Optimizasyonu",
                paths.Count + " dokuya Android override'i yazilacak (max " + MaxSize + ", ASTC 6x6).\n\n" +
                "Bu islem .meta dosyalarini degistirir ve yeniden import gerektirir — " +
                "buyuk pakette birkac dakika surebilir.\n\n" +
                "Geri almak icin: git checkout -- \"Assets/FPS Gun Pack 4K\" \"Assets/Gece Studio\"",
                "Uygula", "Vazgec");
            if (!ok) return;

            int changed = 0, skipped = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Android Doku Optimizasyonu",
                            paths[i], (float)i / paths.Count))
                        break;

                    if (ApplyTo(paths[i])) changed++;
                    else skipped++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            Debug.Log("[AndroidTextureOptimizer] Bitti — " + changed + " doku guncellendi, " +
                      skipped + " atlandi (zaten dogru ayarda).");
        }

        [MenuItem("Tools/VR Multiplayer/39. Doku Raporu (degisiklik yapmaz)")]
        public static void ReportMenu()
        {
            var paths = FindTextures();
            int noOverride = 0, oversized = 0, uncompressed = 0;
            long estimatedBytes = 0;

            foreach (var path in paths)
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                var android = importer.GetPlatformTextureSettings(Platform);
                if (!android.overridden) noOverride++;

                var effective = android.overridden
                    ? android
                    : importer.GetDefaultPlatformTextureSettings();

                if (effective.maxTextureSize > MaxSize) oversized++;
                if (effective.textureCompression == TextureImporterCompression.Uncompressed) uncompressed++;

                // Kaba bellek tahmini: sikistirmasiz 4 bayt/piksel, ASTC 6x6 ~0.445 bayt/piksel,
                // mipmap zinciri icin x1.33.
                int side = effective.maxTextureSize;
                double perPixel = effective.textureCompression == TextureImporterCompression.Uncompressed
                    ? 4.0 : 0.445;
                estimatedBytes += (long)(side * (double)side * perPixel * 1.33);
            }

            Debug.Log("[AndroidTextureOptimizer] RAPOR (" + paths.Count + " doku)\n" +
                      "  Android override YOK      : " + noOverride + "\n" +
                      "  " + MaxSize + "'den buyuk : " + oversized + "\n" +
                      "  Sikistirmasiz             : " + uncompressed + "\n" +
                      "  Tahmini GPU bellegi       : " + (estimatedBytes / 1024f / 1024f).ToString("N0") + " MB");
        }

        // ------------------------------------------------------------------ is mantigi

        static List<string> FindTextures()
        {
            var roots = TargetRoots.Where(AssetDatabase.IsValidFolder).ToArray();
            if (roots.Length == 0) return new List<string>();

            return AssetDatabase.FindAssets("t:Texture2D", roots)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        /// <summary>Override'i yazar. Zaten istenen ayardaysa dokunmaz ve false doner.</summary>
        static bool ApplyTo(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;

            var current = importer.GetPlatformTextureSettings(Platform);
            if (current.overridden &&
                current.maxTextureSize == MaxSize &&
                current.format == Format)
                return false;

            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = Platform,
                overridden = true,
                maxTextureSize = MaxSize,
                format = Format,
                textureCompression = TextureImporterCompression.Compressed,
                compressionQuality = 50,
                allowsAlphaSplitting = false,
            });

            importer.SaveAndReimport();
            return true;
        }
    }
}
