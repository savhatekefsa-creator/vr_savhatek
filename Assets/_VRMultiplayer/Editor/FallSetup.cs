using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// 47. Dusme Kurulumu — sahneye <see cref="FallHazard"/> ilanini koyar ve oyun alaninin
    /// BOSLUK HARITASINI cikarir.
    ///
    /// NEDEN OLCUM DE YAPIYOR, SADECE BILESEN EKLEMIYOR. Bu ozelligin en kotu hatasi sessiz
    /// olani: oyuncunun fiziksel olarak durdugu yerin altinda cati OLMAMASI. O durumda oyuncu
    /// hicbir sey yapmadan duser, kalkar, yine duser — ve sahneye bakarak bunu gormek imkansiz,
    /// cunku gozle "cati orada" gorunuyor. Izgara sondasi tam bu soruyu soruyor: gercek odanin
    /// dustugu alanin yuzde kaci bosluk?
    ///
    /// Arac SAHNEYI KAYDETMEZ, yalnizca kirli isaretler — ne degistigini gorup kendin kaydet.
    /// </summary>
    public static class FallSetup
    {
        /// <summary>Izgaranin orijinden her yone uzanimi (m). Gercek oda ~4,9 x 3,7 m; 4 m
        /// yaricap odanin tamamini ve biraz fazlasini kapsar.</summary>
        const float ScanRadius = 4f;

        /// <summary>Izgara adimi (m). 0,25 bir ayak genisliginden kucuk — daha kabasi dar bir
        /// kopruyu tamamen atlayabilirdi.</summary>
        const float ScanStep = 0.25f;

        /// <summary>Yurunen seviyeyi tahmin ederken yuksekliklerin yuvarlandigi hassasiyet (m).</summary>
        const float LevelBucket = 0.05f;

        [MenuItem("Tools/VR Multiplayer/47. Dusme Kurulumu (cati boslugu)")]
        public static void Setup()
            => EditorUtility.DisplayDialog("Dusme Kurulumu", Apply(), "Tamam");

        /// <summary>
        /// Isin kendisi — kurar, olcer, raporu DONDURUR. Diyalogdan ayri durmasi bilincli:
        /// bu hali betikten (ve MCP'den) cagrilabilir, modal bir pencere editoru kilitlemez.
        /// </summary>
        public static string Apply()
        {
            // KAPALI ilan da aranir. Harita anahtari (menu 56) eski zemine gecerken ilani
            // kapatiyor; yalnizca aciklara baksaydik burasi onu goremez, IKINCI bir "Fall
            // Hazard" olusturur ve sahne "birden fazla ilan" uyarisiyla calisirdi.
            var hazard = Object.FindFirstObjectByType<FallHazard>(FindObjectsInactive.Include);
            bool created = false;

            if (hazard == null)
            {
                var go = new GameObject("Fall Hazard");
                hazard = go.AddComponent<FallHazard>();
                Undo.RegisterCreatedObjectUndo(go, "Dusme ilani");
                created = true;
            }

            // Yurunen seviye TAHMIN EDILMEZ, OLCULUR: orijin cevresindeki sondalarin en sik
            // rastlanan yuksekligi alinir. "0 yazariz" demek, haritayi bir gun 20 cm kaydiran
            // kisiyi sessizce yaniltmak olurdu.
            float level;
            if (TryMeasureWalkableLevel(out level))
            {
                Undo.RecordObject(hazard, "Dusme ilani");
                hazard.walkableLevel = level;
                EditorUtility.SetDirty(hazard);
            }

            string report = BuildReport(hazard, created, level);
            Debug.Log(report);
            EditorSceneManager.MarkSceneDirty(hazard.gameObject.scene);
            Selection.activeGameObject = hazard.gameObject;
            return report;
        }

        // ------------------------------------------------------------------ olcum

        /// <summary>Orijin cevresindeki sondalarin EN SIK yuksekligi — yurunen seviye budur.
        /// En yukseki almak yanlis olurdu: orijinde duran bir sandik seviyeyi kendi ustune
        /// tasirdi.</summary>
        static bool TryMeasureWalkableLevel(out float level)
        {
            var counts = new Dictionary<int, int>();
            int best = 0, bestCount = 0;

            for (float x = -2f; x <= 2f; x += 0.5f)
                for (float z = -2f; z <= 2f; z += 0.5f)
                {
                    float y;
                    if (!TrySolidBelow(new Vector3(x, 60f, z), 200f, out y)) continue;

                    int bucket = Mathf.RoundToInt(y / LevelBucket);
                    int n = counts.TryGetValue(bucket, out int c) ? c + 1 : 1;
                    counts[bucket] = n;
                    if (n > bestCount) { bestCount = n; best = bucket; }
                }

            level = best * LevelBucket;
            return bestCount > 0;
        }

        static string BuildReport(FallHazard hazard, bool created, float level)
        {
            var sb = new StringBuilder();
            sb.AppendLine(created
                ? "Sahneye 'Fall Hazard' eklendi — bu harita artik ucurumlu."
                : "Sahnedeki 'Fall Hazard' guncellendi.");

            // Kurulmus ama kapali bir ilan, kurulmamis olanla ayni sonucu verir. Bunu soylemek
            // zorundayiz: aksi halde "kurdum, hala dusmuyorum" diye saatlerce aranir.
            if (!hazard.gameObject.activeInHierarchy || !hazard.enabled)
                sb.AppendLine("DIKKAT: ilan su an KAPALI — dusme calismaz. Menu 55 (Rooftop " +
                              "Arena'ya Gec) onu geri acar.");
            sb.AppendLine($"Yurunen seviye olculdu: y = {level:0.00} m " +
                          $"(ucurum esigi: {level - hazard.maxStepDown:0.00} m).");
            sb.AppendLine();

            int total = 0, voidCells = 0;
            float deepest = 0f;
            var map = new StringBuilder();

            for (float z = ScanRadius; z >= -ScanRadius; z -= ScanStep)
            {
                for (float x = -ScanRadius; x <= ScanRadius; x += ScanStep)
                {
                    total++;
                    // Sonda kafa hizasindan yapilir; bilesenin oyunda kullandigi sorgunun
                    // AYNISI cagriliyor, ki rapor ile davranis ayrisamasin.
                    bool ground = hazard.HasGroundUnder(new Vector3(x, level + 1.7f, z));
                    if (!ground)
                    {
                        voidCells++;
                        float landing;
                        if (hazard.TryFindLanding(new Vector3(x, level + 1.7f, z), out landing))
                            deepest = Mathf.Max(deepest, level - landing);
                    }
                    map.Append(ground ? '#' : '.');
                }
                map.AppendLine();
            }

            float pct = total > 0 ? voidCells * 100f / total : 0f;
            sb.AppendLine($"Bosluk taramasi ({ScanRadius * 2:0} x {ScanRadius * 2:0} m, {ScanStep:0.00} m adim):");
            sb.AppendLine($"  bosluk orani: %{pct:0.0}   en derin dusus: {deepest:0.0} m " +
                          $"({Mathf.Sqrt(2f * Mathf.Max(0.01f, deepest) / hazard.gravity):0.00} sn)");

            bool originVoid = !hazard.HasGroundUnder(new Vector3(0f, level + 1.7f, 0f));
            if (originVoid)
                sb.AppendLine("  DIKKAT: ORIJININ ALTI BOSLUK. Oyuncu kalibrasyondan hemen sonra " +
                              "duser. Ya haritayi kaydir ya da dogum bolgesini catinin uzerine kur.");

            sb.AppendLine();
            sb.AppendLine("Harita ('#' = basilabilir, '.' = bosluk), ust = +Z, sol = -X:");
            sb.Append(map);

            var zones = Object.FindObjectsByType<TeamSpawnZone>(FindObjectsSortMode.None);
            if (zones.Length == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Sahnede dogum bolgesi YOK (menu 22). Dusen oyuncu olu kalir ve " +
                              "geri donecek bir yeri olmaz — bolgeleri kurmadan sahada deneme.");
            }
            else
            {
                foreach (var z in zones)
                    if (!hazard.HasGroundUnder(z.transform.position + Vector3.up * 1.7f))
                        sb.AppendLine($"  DIKKAT: takim {z.team} dogum bolgesi BOSLUGUN uzerinde " +
                                      $"({z.transform.position}) — orada dirilen aninda duser.");
            }

            return sb.ToString();
        }

        static bool TrySolidBelow(Vector3 origin, float distance, out float y)
        {
            y = float.NegativeInfinity;
            bool found = false;
            var hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0,
                                          QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (!WorldSolids.IsSolid(h.collider)) continue;
                if (found && h.point.y <= y) continue;
                y = h.point.y;
                found = true;
            }
            return found;
        }
    }
}
