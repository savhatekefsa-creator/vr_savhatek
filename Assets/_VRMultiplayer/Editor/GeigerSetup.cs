using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   53. Geiger Sayacini Kur — tikirti sesini uretir, Radiation_Drum prefabina bileseni takar
    ///
    /// Idempotent: klip ve bilesen zaten varsa hicbir sey degistirmez, yani menuye tekrar
    /// basmak zararsiz.
    /// </summary>
    public static class GeigerSetup
    {
        const string DrumPrefab = "Assets/_VRMultiplayer/Art/apocalypse/Prefabs/Radiation_Drum.prefab";
        const string SoundFolder = "Assets/_VRMultiplayer/Resources/WeaponSounds";
        const string ClipPath = SoundFolder + "/geiger_tick.wav";
        const int SampleRate = 44100;

        [MenuItem("Tools/VR Multiplayer/53. Geiger Sayacini Kur (Radiation_Drum)")]
        public static void RunMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Run(), "Tamam");

        public static string Run()
        {
            var log = new StringBuilder();
            log.AppendLine(EnsureClip());
            log.AppendLine(AttachToDrum());
            return log.ToString();
        }

        static string EnsureClip()
        {
            if (File.Exists(ClipPath)) return "geiger_tick.wav zaten var.";

            File.WriteAllBytes(ClipPath, WavWriter.ToWav(Tick(), SampleRate));
            AssetDatabase.ImportAsset(ClipPath, ImportAssetOptions.ForceUpdate);

            var imp = AssetImporter.GetAtPath(ClipPath) as AudioImporter;
            if (imp != null)
            {
                var s = imp.defaultSampleSettings;
                // 8 ms'lik bir klip: sikistirmanin kazandiracagi bellek yok, cozme
                // gecikmesinin kaybettirecegi ani var. Saniyede on kez calabiliyor.
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.PCM;
                s.preloadAudioData = true;   // platform basina ayar; AudioImporter.preloadAudioData eskidi
                imp.defaultSampleSettings = s;
                imp.forceToMono = true;
                imp.SaveAndReimport();
            }
            return "geiger_tick.wav sentezlendi (8 ms).";
        }

        /// <summary>
        /// One Geiger click: a hard noise transient with a short ring on top.
        ///
        /// A COUNTER TICK IS ALMOST NOTHING — a few milliseconds of broadband snap from the tube
        /// discharging, with a faint metallic ring after it. Anything longer stops being a click
        /// and starts being a beep, and a beep reads as a UI sound rather than a device in the
        /// room.
        /// </summary>
        static float[] Tick()
        {
            var rnd = new System.Random(5150);
            int n = (int)(0.008f * SampleRate);
            var s = new float[n];

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float snap = Mathf.Exp(-t / 0.0006f);          // sert vurus
                float ring = Mathf.Exp(-t / 0.0028f);          // kisa metalik cinlama
                s[i] = (float)(rnd.NextDouble() * 2.0 - 1.0) * snap * 0.9f
                     + Mathf.Sin(2f * Mathf.PI * 2600f * t) * ring * 0.35f;
            }

            // Son yarim ms'de kapan: ani kesilen klip kendi tikini ekler.
            int fade = Mathf.Max(4, n / 16);
            for (int i = 0; i < fade; i++) s[n - 1 - i] *= i / (float)fade;

            WavWriter.Normalize(s, 0.85f);
            return s;
        }

        /// <summary>
        /// Adds the component to the drum prefab.
        ///
        /// Goes through <see cref="PrefabUtility.LoadPrefabContents"/> rather than instantiating:
        /// instantiating puts a copy in the OPEN SCENE and marks it dirty even if it is deleted
        /// a line later — the same reason the library scan measures prefabs this way.
        /// </summary>
        static string AttachToDrum()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(DrumPrefab) == null)
                return "Radiation_Drum.prefab bulunamadi: " + DrumPrefab;

            var contents = PrefabUtility.LoadPrefabContents(DrumPrefab);
            try
            {
                if (contents.GetComponent<GeigerTicker>() != null)
                    return "Radiation_Drum'da GeigerTicker zaten var — dokunulmadi.";

                contents.AddComponent<GeigerTicker>();
                PrefabUtility.SaveAsPrefabAsset(contents, DrumPrefab);
                return "Radiation_Drum'a GeigerTicker eklendi.\n\n" +
                       "Haritada duran her varil, O ISTEMCININ oyuncusuna olan mesafesine gore " +
                       "tikirdayacak. Ag trafigi yok.";
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
    }
}
