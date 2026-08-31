using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   51. Palet Raporu — hangi prop insa modunda CIKAR, hangisi CIKMAZ ve NEDEN
    ///
    /// WHY THIS EXISTS AS ITS OWN TOOL: the library window answers "what is in the library" and
    /// the wheel shows "what I can place", and for a long time nothing answered the question
    /// between them. The gap had a cost — the KIYAMET set read as 12 in the editor and 10 in the
    /// headset, and the natural conclusion was that the wheel had a limit of ten. It had no
    /// limit; two props were over the footprint ceiling and nothing said so anywhere.
    ///
    /// Every line below comes from <see cref="ConstructorSession.WhyNotPlaceable"/> — the same
    /// method the runtime gate uses — so this report cannot describe a rule the game does not
    /// actually apply.
    /// </summary>
    public static class PropPaletteReport
    {
        [MenuItem("Tools/VR Multiplayer/51. Palet Raporu (ne cikar, ne cikmaz)")]
        public static void RunMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer — Palet Raporu", Run(), "Tamam");

        public static string Run()
        {
            var lib = PropLibrary.Instance;
            if (lib.Count == 0) return "Kutuphane bos — once menu 25 (Prop Kutuphanesi Tara).";

            lib.InvalidateIndex();

            var offered = new Dictionary<string, List<PropDef>>();
            var blocked = new List<PropDef>();
            int retired = 0;

            foreach (var p in lib.props)
            {
                if (p == null) continue;
                string why = ConstructorSession.WhyNotPlaceable(p);
                if (why == null)
                {
                    string key = string.IsNullOrEmpty(p.paletteId) ? "" : p.paletteId;
                    if (!offered.TryGetValue(key, out var list))
                        offered[key] = list = new List<PropDef>();
                    list.Add(p);
                }
                else if (p.hiddenInPalette) retired++;
                else blocked.Add(p);
            }

            var sb = new StringBuilder();
            sb.AppendLine("INSA MODU CARKI");
            sb.AppendLine();

            if (lib.palettes != null)
                foreach (var pal in lib.palettes)
                {
                    if (pal == null) continue;
                    int n = offered.TryGetValue(pal.id, out var l) ? l.Count : 0;
                    sb.AppendLine($"  {pal.displayName,-12}{n,3} prop" +
                                  (n == 0 ? "    <- BOS: carkta dilimi ACILMAZ" : ""));
                }
            if (offered.TryGetValue("", out var loose))
                sb.AppendLine($"  {"DIGER",-12}{loose.Count,3} prop   (palete atanmamislar)");

            sb.AppendLine();
            if (blocked.Count == 0)
            {
                sb.AppendLine("Kutuphanedeki her AKTIF prop palete giriyor — sessizce elenen yok.");
            }
            else
            {
                sb.AppendLine($"PALETTE CIKMAYAN {blocked.Count} PROP (emekliye ayrilmis olanlar haric):");
                foreach (var p in blocked)
                    sb.AppendLine($"  {p.displayName ?? p.id}  [palet: {lib.PaletteName(p.paletteId)}]" +
                                  $"\n      {ConstructorSession.WhyNotPlaceable(p)}");
                sb.AppendLine();
                sb.AppendLine("Ayak izi yuzunden elenen bir propun olcusu cogu zaman YANLIS olcumdur: " +
                              "mesh'in sinir kutusu (dallar, teller, cikintilar) zeminde kapladigi " +
                              "yerden buyuk okunur. Menu 31'den sizeMeters'i gercek tabanina cek — " +
                              "fitToFootprint KAPALIYSA modelin gorunusu hic degismez.");
            }

            sb.AppendLine();
            sb.AppendLine($"Toplam {lib.Count} prop  ·  emekli {retired}  ·  kutuphane surumu {lib.contentVersion}");
            sb.AppendLine();
            sb.AppendLine("NOT: gozlukteki uygulama kendi KOPYASINI tasir. Buradaki degisiklikler " +
                          "cihaza ancak YENIDEN BUILD alip yukleyince gider.");
            return sb.ToString();
        }
    }
}
