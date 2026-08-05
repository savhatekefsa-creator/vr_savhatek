using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// ORTAK TUTUS KALIBRASYONU — cihazda elle ayarlanan birkac "referans" silahtan ogrenilen
    /// duzeltmeyi kalan butun profillere uygular.
    ///
    /// NEDEN: 2026-08-05'te uc silah (Pistol 2, HK416, Smg 2) cihazda birbirinden BAGIMSIZ
    /// ayarlandi. Euler degerleri silah-lokal oldugu icin dogrudan karsilastirilamaz (HK416'nin
    /// namlusu -X, digerlerininki -Z). Ortak cerceveye — namlunun KUMANDA uzayindaki yonune —
    /// cevrilince uc silah da ayni noktaya yakinsadi: ayar oncesi aralarinda 13 dereceye kadar
    /// fark varken, ayar sonrasi 2-5 derece. Yani sapma silaha ozel DEGIL, kumandanin grip
    /// pose ekseninden geliyor ve HERKESTE AYNI.
    ///
    /// Olculen ortak deger: namlu, kumandanin ileri ekseninden ~56 derece ASAGI ve ~33 derece
    /// SOLDA. Gozle kapatilacak bir fark degildi — "iki mavi ekseni ust uste getir" sezgisinin
    /// neden hic tutmadigi da bu.
    ///
    /// NE YAPAR: her hedef profilin namlusunu, referanslardan ogrenilen ortak yone tasiyan
    /// EN KUCUK donusu uygular (<see cref="Quaternion.FromToRotation"/>). En kucuk donus
    /// secilmesinin sebebi, silahin mevcut YATIKLIGINI (roll) olabildigince korumak: bu arac
    /// namlunun nereye baktigini duzeltir, yatikligi degil.
    ///
    /// NE YAPMAZ: <c>gripLocalPosition</c>'a dokunmaz (kabzanin avucta nerede durdugu dogasi
    /// geregi silaha ozeldir) ve el bombalarina dokunmaz (namlusu olmayan bir objede "namluyu
    /// hizala" anlamsizdir, tutusu bozardi).
    ///
    /// GERI ALMA — uc katman:
    ///   1) Ctrl+Z (Undo.RecordObjects ile kaydedilir)
    ///   2) "46. Tutus Kalibrasyonu Geri Al" — uygulamadan ONCE yazilan yedek dosyasindan
    ///      butun profilleri aynen geri yukler
    ///   3) git (profiller izleniyor)
    /// </summary>
    public static class GripCalibration
    {
        const string ProfileFolder = "Assets/_VRMultiplayer/Resources/WeaponGripProfiles";

        /// <summary>Cihazda elle ayarlanmis, DOGRU kabul edilen profiller. Ortak yon bunlarin
        /// GUNCEL degerlerinden hesaplanir — birini yeniden ayarlayip araci tekrar calistirmak
        /// kalibrasyonu tazeler.</summary>
        static readonly string[] Reference = { "Pistol 2_GripProfile", "HK416_GripProfile", "Smg2_GripProfile" };

        /// <summary>Adinda bunlardan biri gecen profil ATLANIR. Bomba bir silah degil; ona
        /// namlu hizalamasi uygulamak tutusu bozar. NetworkPlayer profili de bir silah degil.</summary>
        static readonly string[] SkipContains = { "Grenade", "NetworkPlayer" };

        // Yedek Assets'in DISINA yazilir: icine yazmak her calistirmada asset import'u
        // tetikler ve .meta cöpü uretirdi.
        static string BackupFolder =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GripOlcum", "yedek");

        struct Row
        {
            public WeaponGripProfile profile;
            public Vector3 newEuler;
            public float deltaDegrees;
        }

        [MenuItem("Tools/VR Multiplayer/45. Tutus Kalibrasyonu Uygula (referans silahlardan)")]
        public static void Apply()
        {
            var all = LoadProfiles();
            if (all.Count == 0)
            {
                EditorUtility.DisplayDialog("Tutus kalibrasyonu",
                    "Profil bulunamadi: " + ProfileFolder, "Tamam");
                return;
            }

            // ── Ortak yonu referanslardan ogren ──
            var refDirs = new List<Vector3>();
            var refNames = new List<string>();
            foreach (var p in all)
            {
                if (System.Array.IndexOf(Reference, p.name) < 0) continue;
                if (!BarrelInController(p, out Vector3 v)) continue;
                refDirs.Add(v);
                refNames.Add(p.name);
            }

            if (refDirs.Count < 2)
            {
                EditorUtility.DisplayDialog("Tutus kalibrasyonu",
                    "En az 2 referans profil gerekli, " + refDirs.Count + " bulundu.\n\n" +
                    "Beklenen: " + string.Join(", ", Reference) + "\n\n" +
                    "Once bu silahlari cihazda WeaponGripTuner ile ayarla.", "Tamam");
                return;
            }

            Vector3 canonical = Vector3.zero;
            foreach (var d in refDirs) canonical += d;
            canonical.Normalize();

            // Referanslarin kendi arasindaki dagilim = kalibrasyonun kalitesi. Buyukse
            // ortalama anlamsizdir ve kullanici bunu GORMELI.
            float spread = 0f;
            for (int i = 0; i < refDirs.Count; i++)
                for (int j = i + 1; j < refDirs.Count; j++)
                    spread = Mathf.Max(spread, Vector3.Angle(refDirs[i], refDirs[j]));

            // ── Hedefleri hesapla ──
            var rows = new List<Row>();
            var skipped = new List<string>();
            foreach (var p in all)
            {
                if (System.Array.IndexOf(Reference, p.name) >= 0) { skipped.Add(p.name + " (referans)"); continue; }
                if (IsSkipped(p.name)) { skipped.Add(p.name + " (silah degil)"); continue; }
                if (!BarrelInController(p, out Vector3 v)) { skipped.Add(p.name + " (namlu yonu tanimsiz)"); continue; }

                float delta = Vector3.Angle(v, canonical);
                var R = Quaternion.FromToRotation(v, canonical);
                // Inverse(g') * b = canonical  ve  Inverse(g) * b = v  =>  g' = g * Inverse(R)
                var newRot = Quaternion.Euler(p.gripLocalEuler) * Quaternion.Inverse(R);
                rows.Add(new Row { profile = p, newEuler = newRot.eulerAngles, deltaDegrees = delta });
            }

            // ── Onizlemeyi once konsola bas: dialog 13 satir gosteremez ──
            var log = new StringBuilder();
            log.AppendLine("[GripCalibration] ONIZLEME — henuz hicbir sey degismedi");
            log.AppendLine("  referanslar: " + string.Join(", ", refNames.ToArray()));
            log.AppendFormat("  referans dagilimi: {0:F2} derece (kucuk = guvenilir ortak yon)\n", spread);
            log.AppendFormat("  ortak namlu yonu (kumanda uzayi): yaw {0:F2}  pitch {1:F2}\n",
                Mathf.Atan2(canonical.x, canonical.z) * Mathf.Rad2Deg,
                Mathf.Asin(Mathf.Clamp(canonical.y, -1f, 1f)) * Mathf.Rad2Deg);
            log.AppendLine();
            float maxDelta = 0f, sumDelta = 0f;
            foreach (var r in rows)
            {
                log.AppendFormat("  {0,-28} {1,7:F2} derece donecek\n", r.profile.name, r.deltaDegrees);
                maxDelta = Mathf.Max(maxDelta, r.deltaDegrees);
                sumDelta += r.deltaDegrees;
            }
            if (skipped.Count > 0) log.AppendLine("\n  ATLANAN: " + string.Join(", ", skipped.ToArray()));
            Debug.Log(log.ToString());

            if (rows.Count == 0)
            {
                EditorUtility.DisplayDialog("Tutus kalibrasyonu",
                    "Uygulanacak profil yok (hepsi referans ya da atlandi). Detay Console'da.", "Tamam");
                return;
            }

            float avg = sumDelta / rows.Count;
            bool go = EditorUtility.DisplayDialog("Tutus kalibrasyonu",
                string.Format(
                    "{0} profil guncellenecek.\n\n" +
                    "Referans: {1} silah, aralarindaki dagilim {2:F2} derece\n" +
                    "Ortalama donus: {3:F1} derece   (en buyugu {4:F1})\n\n" +
                    "Atlanan: {5}\n\n" +
                    "Detayli liste Console'da. Uygulamadan once butun profillerin " +
                    "mevcut degerleri yedeklenecek; '46. Geri Al' ile aynen donulebilir " +
                    "(Ctrl+Z de calisir).",
                    rows.Count, refDirs.Count, spread, avg, maxDelta, skipped.Count),
                "Uygula", "Vazgec");
            if (!go) return;

            string backup = WriteBackup(all);

            var objs = new Object[rows.Count];
            for (int i = 0; i < rows.Count; i++) objs[i] = rows[i].profile;
            Undo.RecordObjects(objs, "Tutus kalibrasyonu");

            foreach (var r in rows)
            {
                r.profile.gripLocalEuler = r.newEuler;
                EditorUtility.SetDirty(r.profile);
            }
            AssetDatabase.SaveAssets();

            Debug.Log("[GripCalibration] " + rows.Count + " profil guncellendi. Yedek: " + backup);
            EditorUtility.DisplayDialog("Tutus kalibrasyonu",
                rows.Count + " profil guncellendi.\n\nYedek:\n" + backup +
                "\n\nCihazda dene; begenmezsen '46. Tutus Kalibrasyonu Geri Al'.", "Tamam");
        }

        [MenuItem("Tools/VR Multiplayer/46. Tutus Kalibrasyonu Geri Al (son yedekten)")]
        public static void Restore()
        {
            if (!Directory.Exists(BackupFolder))
            {
                EditorUtility.DisplayDialog("Geri al", "Yedek klasoru yok:\n" + BackupFolder, "Tamam");
                return;
            }

            var files = Directory.GetFiles(BackupFolder, "grip-yedek-*.txt");
            if (files.Length == 0)
            {
                EditorUtility.DisplayDialog("Geri al", "Yedek dosyasi yok:\n" + BackupFolder, "Tamam");
                return;
            }
            System.Array.Sort(files); // isimde tarih-saat var, son eleman en yenisi
            string newest = files[files.Length - 1];

            var values = ParseBackup(newest);
            var all = LoadProfiles();
            int restored = 0, missing = 0;

            var objs = new List<Object>();
            foreach (var p in all) if (values.ContainsKey(p.name)) objs.Add(p);
            if (objs.Count == 0)
            {
                EditorUtility.DisplayDialog("Geri al",
                    "Yedekteki hicbir profil bulunamadi:\n" + newest, "Tamam");
                return;
            }

            if (!EditorUtility.DisplayDialog("Geri al",
                "Yedek: " + Path.GetFileName(newest) + "\n\n" +
                objs.Count + " profil bu yedekteki degerlere DONDURULECEK.\n\n" +
                "Bu, yedekten sonra yaptigin elle ayarlari da siler.",
                "Geri al", "Vazgec")) return;

            Undo.RecordObjects(objs.ToArray(), "Tutus kalibrasyonu geri al");
            foreach (var p in all)
            {
                if (!values.TryGetValue(p.name, out var v)) { missing++; continue; }
                p.gripLocalPosition = v.pos;
                p.gripLocalEuler = v.euler;
                EditorUtility.SetDirty(p);
                restored++;
            }
            AssetDatabase.SaveAssets();

            Debug.Log("[GripCalibration] geri alindi: " + restored + " profil, kaynak " + newest);
            EditorUtility.DisplayDialog("Geri al",
                restored + " profil geri yuklendi." + (missing > 0 ? "\n" + missing + " profil yedekte yoktu, dokunulmadi." : ""),
                "Tamam");
        }

        // ─── Yardimcilar ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Namlunun KUMANDA uzayindaki yonu — silahlar arasi tek karsilastirilabilir buyukluk.
        /// HandGrabber.FollowProfiled'da weaponRot = anchor.rotation * Inverse(gripRot) oldugu
        /// icin, namlunun kumandaya gore yonu Inverse(gripRot) * barrelLocal olur; anchor
        /// sadelesir. Mesh'in kendi eksen duzeni (kimi silahta namlu -X, kimide -Z) boylece
        /// denklemden cikar.
        /// </summary>
        static bool BarrelInController(WeaponGripProfile p, out Vector3 dir)
        {
            dir = Vector3.forward;
            if (p == null) return false;
            Vector3 b = p.barrelLocalDirection;
            if (b.sqrMagnitude < 1e-6f) return false; // namlu ekseni yazilmamis: tahmin etme, atla
            dir = (Quaternion.Inverse(Quaternion.Euler(p.gripLocalEuler)) * b.normalized).normalized;
            return true;
        }

        static bool IsSkipped(string name)
        {
            foreach (var s in SkipContains)
                if (name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static List<WeaponGripProfile> LoadProfiles()
        {
            var list = new List<WeaponGripProfile>();
            if (!AssetDatabase.IsValidFolder(ProfileFolder)) return list;
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile", new[] { ProfileFolder }))
            {
                var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null) list.Add(p);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        /// <summary>Uygulamadan ONCE butun profillerin konum+aci degerlerini yazar. Atlananlar
        /// da yazilir: geri alma "kalibrasyonu geri al" degil, "o ana don" olmali.</summary>
        static string WriteBackup(List<WeaponGripProfile> all)
        {
            Directory.CreateDirectory(BackupFolder);
            string path = Path.Combine(BackupFolder,
                "grip-yedek-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

            var sb = new StringBuilder();
            sb.AppendLine("# Tutus profili yedegi — " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# bicim: ad|posX,posY,posZ|eulerX,eulerY,eulerZ   (ondalik NOKTA)");
            foreach (var p in all)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}|{1},{2},{3}|{4},{5},{6}", p.name,
                    p.gripLocalPosition.x, p.gripLocalPosition.y, p.gripLocalPosition.z,
                    p.gripLocalEuler.x, p.gripLocalEuler.y, p.gripLocalEuler.z));

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static Dictionary<string, (Vector3 pos, Vector3 euler)> ParseBackup(string path)
        {
            var map = new Dictionary<string, (Vector3, Vector3)>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('|');
                if (parts.Length != 3) continue;
                if (TryVec(parts[1], out Vector3 pos) && TryVec(parts[2], out Vector3 euler))
                    map[parts[0]] = (pos, euler);
            }
            return map;
        }

        static bool TryVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            var c = s.Split(',');
            if (c.Length != 3) return false;
            if (!float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }
}
