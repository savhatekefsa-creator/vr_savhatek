using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// WeaponGripCaptures.md'deki kulaklik yakalamalarini silah profillerine dagitir — her silah
    /// kendi OZGUN tutusunu alir.
    ///
    /// Kurallar:
    ///  - Ayni silah + ayni el icin dosyadaki EN SON kayit gecerlidir (deneme kayitlari elenir;
    ///    orn. Dmr1'in 11:17'deki silah-2m-uzakta cop cifti yerine 12:01 cifti yazilir).
    ///  - SAG el = ana el (kabza): gripLocal* + mainHand.wristLocal* birebir yazilir.
    ///  - SOL el = destek: yakalanan nokta ray (start=end) olur; bilek degerleri SOL anchor
    ///    cercevesinden ANA kabza cercevesine cevrilir (WeaponHandWeld destegi ana kabza
    ///    rotasyonuyla uygular — Dmr1 menu 33'teki ayni donusum).
    ///  - Rotasyonlar euler yerine kayittaki HAM QUATERNION'lardan okunur (aracin kendi notu:
    ///    euler kayipli, quaternion gercek olcum).
    ///  - SAG var + SOL yoksa: kayitli destek bilegi ESKI kabza cercevesinden YENI kabza
    ///    cercevesine tasinir. WeaponHandWeld destegi de ana kabza rotasyonuyla cozdugu icin,
    ///    bu tasima olmadan destek eli ana kabza deltasi kadar sessizce kayar (HK416'da 24°).
    ///  - ALTIN profiller (HK416, Weapon_Pistol — elle kaptirilip ayarlanmis referanslar)
    ///    otomatik geciste ATLANIR; menu 37 calistirilirsa tek tek sorulur. Not: 12:04:10'daki
    ///    "Rifle_HK416 (1)" SAG kaydi buyuk olasilikla rafta HK416'nin ustunde duran Shotgun 1'e
    ///    uzanirken en-yakin-silah secimine takilan bir yanlis atama (Shotgun 1'in SAG'i yok,
    ///    HK416'nin SOL'u yok, arada 7 sn) — supheli oldugu icin kendiliginden uygulanmaz.
    ///
    /// Otomatik: derleme sonrasi dosya degistiyse (hash) bir kez calisir. Menu 37 elle tetikler.
    /// Yeni yakalama yaptikca menu 37'yi calistirmak yeterli — sabit kod yok, hep dosyayi okur.
    /// </summary>
    public static class ApplyGripCaptures
    {
        const string HashKey = "ApplyGripCaptures.lastHash";
        static readonly string[] GoldenProfiles = { "HK416_GripProfile", "Pistol_GripProfile" };
        static int _retries;

        static string LogPath => Path.Combine(Application.dataPath, "..", "WeaponGripCaptures.md");

        struct Capture
        {
            public string weapon;   // dosyadaki ad (kopya eki atilmis)
            public bool left;
            public Vector3 gripPos, wristPos;
            public Quaternion gripRot, wristRot;
            public string stamp;
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.delayCall += TryAutoRun;
        }

        static void TryAutoRun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryAutoRun;
                return;
            }
            if (!File.Exists(LogPath)) return;

            string text = File.ReadAllText(LogPath);
            string hash = Hash(text);
            if (EditorPrefs.GetString(HashKey, "") == hash) return; // bu icerik zaten islendi

            int missing = Apply(text, interactive: false, includeGolden: false);
            if (missing > 0 && _retries < 30)
            {
                // Profiller baska bir aracin (WeaponPackSetup) ayni derleme dongusunde uretilmesini
                // bekliyor olabilir — hash'i YAZMADAN kisa sure sonra yeniden dene.
                _retries++;
                EditorApplication.delayCall += TryAutoRun;
                return;
            }
            EditorPrefs.SetString(HashKey, hash);
        }

        [MenuItem("Tools/VR Multiplayer/37. Yakalama Dosyasini Profillere Uygula")]
        public static void RunFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Yakalama Uygula",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }
            if (!File.Exists(LogPath))
            {
                EditorUtility.DisplayDialog("Yakalama Uygula",
                    "WeaponGripCaptures.md bulunamadi:\n" + LogPath, "Tamam");
                return;
            }
            string text = File.ReadAllText(LogPath);
            Apply(text, interactive: true, includeGolden: true);
            EditorPrefs.SetString(HashKey, Hash(text));
        }

        /// <summary>Dosyayi profillere uygular; profili henuz olmayan silah sayisini dondurur.</summary>
        static int Apply(string text, bool interactive, bool includeGolden)
        {
            var latest = Parse(text); // (silah, el) basina en son kayit

            // Silah basina grupla.
            var byWeapon = new Dictionary<string, List<Capture>>();
            foreach (var c in latest.Values)
            {
                if (!byWeapon.ContainsKey(c.weapon)) byWeapon[c.weapon] = new List<Capture>();
                byWeapon[c.weapon].Add(c);
            }

            var profiles = AllProfiles();
            var lines = new List<string>();
            int applied = 0, missing = 0;

            foreach (var kv in byWeapon)
            {
                string weapon = kv.Key;
                Capture? main = null, sup = null;
                foreach (var c in kv.Value)
                {
                    if (c.left) sup = c; else main = c;
                }

                var profile = BestProfile(profiles, weapon);
                if (profile == null)
                {
                    lines.Add("  " + weapon + ": profil YOK — atlandi (once kurulum calismali)");
                    missing++;
                    continue;
                }

                bool golden = System.Array.IndexOf(GoldenProfiles, profile.name) >= 0;
                if (golden)
                {
                    if (!includeGolden)
                    {
                        lines.Add("  " + weapon + ": ALTIN profil (" + profile.name + ") — otomatik gecis dokunmaz");
                        continue;
                    }
                    if (interactive && !EditorUtility.DisplayDialog("Yakalama Uygula",
                        "'" + weapon + "' kaydi ALTIN profili (" + profile.name + ") ezecek.\n\n"
                        + "Bu profil elle kaptirilip ayarlanmis referans (parmak pozlari + ayarli destek).\n\n"
                        + "Uygulanacak kayitlar:\n"
                        + "  ana el : " + (main.HasValue ? main.Value.stamp : "YOK — mevcut deger kalir") + "\n"
                        + "  destek : " + (sup.HasValue ? sup.Value.stamp
                            : "YOK — mevcut deger yeni kabza cercevesine tasinir") + "\n\n"
                        + "Damgayi dogrula: beklemedigin bir kayitsa atla.\n\nUygulansin mi?",
                        "Evet, ez", "Hayir, atla"))
                    {
                        lines.Add("  " + weapon + ": altin profil — kullanici atladi");
                        continue;
                    }
                }

                // Ana el: birebir. (Destek donusumu icin nihai ana rotasyon = varsa yeni, yoksa mevcut.)
                bool rebased = false;
                Quaternion oldMainRot = profile.GripLocalRotation;
                Quaternion mainRot = main.HasValue ? main.Value.gripRot : oldMainRot;
                if (main.HasValue)
                {
                    var m = main.Value;
                    profile.gripLocalPosition = m.gripPos;
                    profile.gripLocalEuler = m.gripRot.eulerAngles;
                    profile.mainHand.wristLocalPosition = m.wristPos;
                    profile.mainHand.wristLocalEuler = m.wristRot.eulerAngles;
                }

                // Destek el: nokta-ray + ana cerceveye cevrilmis bilek.
                if (sup.HasValue)
                {
                    var s = sup.Value;
                    profile.supportRailLocalStart = s.gripPos;
                    profile.supportRailLocalEnd = s.gripPos;
                    Quaternion toMain = Quaternion.Inverse(mainRot) * s.gripRot;
                    profile.supportHand.wristLocalPosition = toMain * s.wristPos;
                    profile.supportHand.wristLocalEuler = (toMain * s.wristRot).eulerAngles;
                }
                else if (main.HasValue && HasSupportPose(profile))
                {
                    // Destek bilegi ANA kabza rotasyonu cercevesinde saklanir — WeaponHandWeld her iki
                    // el icin de anchorRot = weapon.rotation * gripLocalRot kullanir. Ana kabza yeni
                    // kayitla donunce, eslesen SOL kayit yoksa eski destek bilegi tam o delta kadar
                    // kayar (silaha gore sabit kalmasi gerekirken ana elle birlikte doner). Eski
                    // cerceveden yeni cerceveye tasi. Ray weapon-local oldugu icin dokunulmaz.
                    Quaternion delta = Quaternion.Inverse(mainRot) * oldMainRot;
                    if (Quaternion.Angle(Quaternion.identity, delta) > 0.01f)
                    {
                        profile.supportHand.wristLocalPosition = delta * profile.supportHand.wristLocalPosition;
                        profile.supportHand.wristLocalEuler =
                            (delta * Quaternion.Euler(profile.supportHand.wristLocalEuler)).eulerAngles;
                        rebased = true;
                    }
                }

                EditorUtility.SetDirty(profile);
                applied++;
                lines.Add("  " + weapon + " -> " + profile.name + ": "
                    + (main.HasValue ? "ana(" + main.Value.stamp + ") " : "ana YOK — geometrik kaldi ")
                    + (sup.HasValue ? "destek(" + sup.Value.stamp + ")"
                        : rebased ? "destek YOK — eski deger yeni kabza cercevesine tasindi"
                        : "destek YOK — eski deger kaldi"));
            }

            AssetDatabase.SaveAssets();

            string msg = "Yakalama dosyasi profillere uygulandi: " + applied + " profil guncellendi"
                + (missing > 0 ? ", " + missing + " profil henuz yok" : "") + ".\n"
                + string.Join("\n", lines)
                + "\nTest: her silahi tut; destek elde terslik olursa menu 31 MirrorX. "
                + "Yeni yakalamadan sonra menu 37'yi tekrar calistir.";
            Debug.Log("[ApplyGripCaptures] " + msg);
            if (interactive) EditorUtility.DisplayDialog("Yakalama Uygula", msg, "Tamam");
            return missing;
        }

        /// <summary>Profilde gercekten yazilmis bir destek pozu var mi? Bos profilde re-baseleme
        /// anlamsiz olur (sifir bilek deltanin eulerine donusur).</summary>
        static bool HasSupportPose(WeaponGripProfile p)
        {
            return p.supportRailLocalStart != Vector3.zero
                || p.supportRailLocalEnd != Vector3.zero
                || p.supportHand.wristLocalPosition != Vector3.zero
                || p.supportHand.wristLocalEuler != Vector3.zero;
        }

        // ------------------------------------------------------------------ dosya ayristirma

        /// <summary>(silah, el) basina dosyadaki EN SON kaydi cikarir. Dosya kronolojik ekleme
        /// oldugu icin sirayla ustune yazmak yeterli.</summary>
        static Dictionary<string, Capture> Parse(string text)
        {
            var latest = new Dictionary<string, Capture>();

            // Basliklar: "## SAG el — Dmr1 — 2026-07-17 12:01:11"
            var headers = Regex.Matches(text,
                @"^## (SOL|SAG) el — (.+?) — ([\d\-]+ [\d:]+)\s*$",
                RegexOptions.Multiline);

            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i];
                int start = h.Index;
                int end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
                string block = text.Substring(start, end - start);

                // Hata kayitlari deger blogu icermez — sessizce gec.
                Vector3? gPos = ParseVec(block, "gripLocalPosition");
                Vector3? wPos = ParseVec(block, "wristLocalPosition");
                Quaternion? gRot = ParseQuat(block, "gripLocalRotation");
                Quaternion? wRot = ParseQuat(block, "wristLocalRotation");
                if (gRot == null) // ham quaternion yoksa euler'den kur
                {
                    Vector3? e = ParseVec(block, "gripLocalEuler");
                    if (e.HasValue) gRot = Quaternion.Euler(e.Value);
                }
                if (wRot == null)
                {
                    Vector3? e = ParseVec(block, "wristLocalEuler");
                    if (e.HasValue) wRot = Quaternion.Euler(e.Value);
                }
                if (gPos == null || wPos == null || gRot == null || wRot == null) continue;

                var c = new Capture
                {
                    weapon = TrimCopySuffix(WeaponGripBinder.CleanName(h.Groups[2].Value.Trim())),
                    left = h.Groups[1].Value == "SOL",
                    gripPos = gPos.Value,
                    wristPos = wPos.Value,
                    gripRot = gRot.Value,
                    wristRot = wRot.Value,
                    stamp = h.Groups[3].Value,
                };
                latest[c.weapon + "|" + (c.left ? "L" : "R")] = c; // sonraki ayni anahtari ezer
            }
            return latest;
        }

        // "alan: {x: -1,7125, y: -0,3381, z: 1,4975}" — TR ondalik virgulu noktaya cevrilerek.
        static Vector3? ParseVec(string block, string field)
        {
            var m = Regex.Match(block, field + @":\s*\{x:\s*(-?[\d,\.]+)\s*,\s*y:\s*(-?[\d,\.]+)\s*,\s*z:\s*(-?[\d,\.]+)\s*\}");
            if (!m.Success) return null;
            return new Vector3(Num(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value));
        }

        static Quaternion? ParseQuat(string block, string field)
        {
            var m = Regex.Match(block, field + @":\s*\{x:\s*(-?[\d,\.]+)\s*,\s*y:\s*(-?[\d,\.]+)\s*,\s*z:\s*(-?[\d,\.]+)\s*,\s*w:\s*(-?[\d,\.]+)\s*\}");
            if (!m.Success) return null;
            var q = new Quaternion(Num(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value), Num(m.Groups[4].Value));
            return q.normalized; // yazim yuvarlamasina karsi
        }

        static float Num(string s) => float.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

        static string TrimCopySuffix(string name) => Regex.Replace(name, @"\s*\(\d+\)$", "");

        // ------------------------------------------------------------------ profil bulma

        static List<WeaponGripProfile> AllProfiles()
        {
            var list = new List<WeaponGripProfile>();
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null) list.Add(p);
            }
            return list;
        }

        static WeaponGripProfile BestProfile(List<WeaponGripProfile> all, string weaponName)
        {
            WeaponGripProfile best = null;
            int bestScore = 0;
            foreach (var p in all)
            {
                int s = p.MatchScore(weaponName);
                if (s > bestScore) { bestScore = s; best = p; }
            }
            return best;
        }

        static string Hash(string text)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
                var sb = new System.Text.StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
