using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   50. Build'e Girecek Haritalar — hangi haritalarin BUILD ICINE gomulecegini secer.
    ///
    /// NEDEN GEREKLI: <c>Assets/_VRMultiplayer/Maps/*.json</c> build'e GIRMEZ. Unity yalnizca
    /// sahnelerin referans verdigi assetleri, <c>Resources/</c> ve <c>StreamingAssets/</c>
    /// icerigini paketler. Depoya harita commit etmek, o haritanin build alan makinede
    /// gorunecegi anlamina gelmiyor — sahada tam olarak bu sasirtti: "main'i merge ettim,
    /// build aldim, harita yok."
    ///
    /// Bu pencere secilen haritalari <c>Assets/StreamingAssets/Maps</c>'e kopyalar; oradan
    /// build'e dosya olarak girerler ve ilk acilista <see cref="MapSeeder"/> onlari
    /// yazilabilir klasore tasir.
    ///
    /// HEPSINI DEGIL, SECEREK: harita klasorunde deneme/artik dosyalar birikiyor (DENEME,
    /// OFISDENEME...). Hepsini gomen bir arac, ekibin eline oynanmayacak haritalar tutusturur
    /// ve havuz ekranini kirletir.
    /// </summary>
    public class MapSeedWindow : EditorWindow
    {
        static string SeedDir => Application.dataPath + "/StreamingAssets/" + MapSeeder.SeedFolderName;

        [MenuItem("Tools/VR Multiplayer/50. Build'e Girecek Haritalar")]
        public static void Open()
        {
            var w = GetWindow<MapSeedWindow>("Build Haritalari");
            w.minSize = new Vector2(560f, 420f);
            w.Reload();
        }

        class Satir
        {
            public string ad;
            public int prop, tag;
            public bool havuzda;
            public bool tohumda;      // su an StreamingAssets'te var mi
            public bool secili;       // olmasini istiyor muyuz
            public string tohumFarki; // tohumdaki kopya guncel mi
        }

        readonly List<Satir> _satirlar = new List<Satir>();
        Vector2 _scroll;
        string _rapor = "";

        void OnFocus() => Reload();

        void Reload()
        {
            _satirlar.Clear();
            foreach (string ad in MapLayout.List())
            {
                var m = MapLayout.Load(ad);
                if (m == null) continue;

                string tohumYolu = SeedDir + "/" + ad + ".json";
                bool tohumda = File.Exists(tohumYolu);

                string fark = "";
                if (tohumda)
                {
                    // Icerik karsilastirmasi: tohumdaki kopya bayatladiysa kullanici bilsin,
                    // yoksa "gomdum" deyip eski haritayi dagitir.
                    try
                    {
                        fark = File.ReadAllText(tohumYolu) == File.ReadAllText(MapLayout.PathFor(ad))
                            ? "guncel" : "BAYAT";
                    }
                    catch { fark = "okunamadi"; }
                }

                _satirlar.Add(new Satir
                {
                    ad = ad,
                    prop = m.Count,
                    tag = TagCapture.PlateDerivedTagCount(m),
                    havuzda = m.inPool,
                    tohumda = tohumda,
                    secili = tohumda,
                    tohumFarki = fark,
                });
            }
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Buradan secilen haritalar Assets/StreamingAssets/Maps'e kopyalanir ve BUILD'E " +
                "GIRER. Oyun ilk acildiginda MapSeeder bunlari yazilabilir klasore tasir.\n\n" +
                "Depoya harita commit etmek TEK BASINA yetmez — build'e girmesi icin burada " +
                "secili olmasi gerekir.", MessageType.Info);

            using (var s = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = s.scrollPosition;

                foreach (var r in _satirlar)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        r.secili = EditorGUILayout.ToggleLeft(r.ad, r.secili, GUILayout.Width(160f));
                        EditorGUILayout.LabelField(
                            $"{r.prop} prop   {r.tag} tag" + (r.havuzda ? "   HAVUZDA" : ""),
                            GUILayout.Width(200f));

                        if (!r.tohumda)
                            EditorGUILayout.LabelField("build'de yok", EditorStyles.miniLabel);
                        else if (r.tohumFarki == "BAYAT")
                            EditorGUILayout.LabelField("build'deki kopya BAYAT",
                                EditorStyles.boldLabel);
                        else
                            EditorGUILayout.LabelField("build'de " + r.tohumFarki,
                                EditorStyles.miniLabel);
                    }
                }
            }

            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Secilenleri Build'e Goma (StreamingAssets'i yeniden yaz)"))
                Uygula();

            if (!string.IsNullOrEmpty(_rapor))
                EditorGUILayout.TextArea(_rapor, GUILayout.ExpandHeight(true), GUILayout.MinHeight(80f));
        }

        void Uygula()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(SeedDir);

                // ONCE TEMIZLE: secimden CIKARILAN bir harita build'de kalmamali. Yalnizca
                // ekleyen bir arac, bir kez gomulen haritayi bir daha cikaramazdi.
                foreach (string eski in Directory.GetFiles(SeedDir, "*.json"))
                {
                    string ad = Path.GetFileNameWithoutExtension(eski);
                    bool istenen = _satirlar.Exists(x => x.ad == ad && x.secili);
                    if (istenen) continue;
                    File.Delete(eski);
                    if (File.Exists(eski + ".meta")) File.Delete(eski + ".meta");
                    sb.AppendLine($"  cikarildi : {ad}");
                }

                int n = 0;
                foreach (var r in _satirlar)
                {
                    if (!r.secili) continue;
                    File.Copy(MapLayout.PathFor(r.ad), SeedDir + "/" + r.ad + ".json", true);
                    sb.AppendLine($"  gomuldu   : {r.ad}  ({r.prop} prop, {r.tag} tag)");
                    n++;
                }

                AssetDatabase.Refresh();
                sb.Insert(0, $"{n} harita build'e gomuldu.\n{SeedDir}\n\n");
                sb.AppendLine();
                sb.AppendLine("SIMDI: build al. Oyun ilk acildiginda bu haritalar");
                sb.AppendLine("persistentDataPath/Maps altina kopyalanacak (var olanlar EZILMEZ).");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("HATA: " + e.Message);
            }

            _rapor = sb.ToString();
            Debug.Log("[MapSeedWindow] " + _rapor);
            Reload();
        }
    }
}
