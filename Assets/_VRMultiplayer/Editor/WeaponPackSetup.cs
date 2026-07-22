using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Netcode;
using VRMultiplayer;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// FPS Gun Pack 4K'dan sahneye konmus TUM silahlari (el bombalari haric) HK416 duzenine
    /// getirir: geometrik fizik (GunPhysicsSetup) + tutma/ag kablolamasi + dogru yonlu Muzzle +
    /// silah basina tutus profili.
    ///
    /// Profil tabani silah turune gore secilir:
    ///   - Tabancalar (yol "Pistol" icerir)  -> Pistol_GripProfile  (tek el + destek avuc)
    ///   - Digerleri (rifle/smg/shotgun/sniper) -> HK416_GripProfile (kabza + kundak destek)
    /// Parmak pozlari, bilek ofsetleri (metre) ve ates/tepme/sarjor davranisi tabandan aynen
    /// kopyalanir; yalnizca silah-LOKAL degerler (kabza cipasi, namlu ekseni, ray, muzzle) o
    /// silahin kendi geometrisinden hesaplanir. Cipa YONELIMI taban silahin kulaklikta yakalanan
    /// kumanda egiminin namlu cercevesine gore birebir tasinmasidir (Dmr1'dekiyle ayni teknik).
    ///
    /// Guvenlik: "Muzzle" ADLI MESH parcasi olan modellerde (orn. Rifle 1 / AK47_Sopmod namlu
    /// freni) o parca once "MuzzleMesh" yapilir — yoksa NetworkWeapon/menu 16 onu namlu ucu
    /// sanip tasiyabilirdi.
    ///
    /// Kabza ADLI mesh olmayan modellerde (ikinci paket stili: main_frame/stock/trigger — orn.
    /// Rifle 3) cipa tetik parcasinin arka-altindan turetilir. Kablolanmis ama profilsiz kalmis
    /// silahlar otomatik geciste yalnizca profilleri yazilarak tamamlanir (fizik ellenmez).
    ///
    /// Otomatik: derleme sonrasi kablosuz paket silahi varsa BIR kez calisir (GrabbableObject
    /// varligi = o silah bitmis sayilir). Menu 35 ayni isi elle tetikler; menu 36 SECILI silahin
    /// profil degerlerini (kablolamaya dokunmadan) guncel geometriden yeniden yazar.
    /// </summary>
    public static class WeaponPackSetup
    {
        const string PackRoot = "Assets/FPS Gun Pack 4K/";
        const string ProfileFolder = "Assets/_VRMultiplayer/Resources/WeaponGripProfiles";
        const string LongGunBase = "HK416_GripProfile";
        const string PistolBase = "Pistol_GripProfile";

        // Kutu/fizik araciyla ayni kozmetik filtre + bolge kaliplari (iki paket stilini de tanir).
        static readonly string[] IgnorePatterns = { "bullet", "shell", "screw", "bolt", "groove" };
        static readonly string[] GripPatterns = { "grip", "handle" };
        static readonly string[] GripExclude = { "foregrip" };
        static readonly string[] TriggerPatterns = { "trigger" };
        static readonly string[] FrontPatterns = { "muzzle", "nozzle", "barrel", "silencer", "suppressor" };

        // ------------------------------------------------------------------ giris noktalari

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

            foreach (var gun in FindPackGuns())
                if (gun.GetComponent<GrabbableObject>() == null || !HasProfile(gun)) { RunAll(false); return; }
        }

        [MenuItem("Tools/VR Multiplayer/35. Paketteki TUM Silahlari Tutulabilir Yap")]
        public static void RunAllMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Silah Paketi",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }
            RunAll(true);
        }

        [MenuItem("Tools/VR Multiplayer/36. Secili Silahin Profilini Yeniden Hesapla")]
        public static void RecomputeSelectedMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Silah Paketi",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }

            var go = Selection.activeGameObject;
            if (go != null)
            {
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                if (outer != null) go = outer;
            }
            if (go == null || !go.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Silah Paketi",
                    "Once sahnedeki silahi sec, sonra bu menuyu calistir.", "Tamam");
                return;
            }

            // Kulaklikta yakalanmis (elle ayarlanmis) bir profil geometrik tahminle EZILMESIN.
            if (!EditorUtility.DisplayDialog("Silah Paketi",
                "'" + go.name + "' icin profil geometriden YENIDEN hesaplanacak.\n\n"
                + "Bu silahin profili kulaklikta yakalanmis ya da elle ayarlanmis degerler iceriyorsa "
                + "bunlar ezilir (Dmr1 icin yakalanan poz menu 33 ile geri yazilabilir).\n\nDevam?",
                "Evet, yeniden hesapla", "Vazgec"))
                return;

            string log = WriteProfileFor(go, repositionMuzzle: true);
            Debug.Log("[WeaponPackSetup] " + log);
            EditorUtility.DisplayDialog("Silah Paketi", log, "Tamam");
        }

        [MenuItem("Tools/VR Multiplayer/36. Secili Silahin Profilini Yeniden Hesapla", true)]
        static bool RecomputeSelectedValidate() => Selection.activeGameObject != null;

        // ------------------------------------------------------------------ toplu kurulum

        static void RunAll(bool interactive)
        {
            var guns = FindPackGuns();
            if (guns.Count == 0)
            {
                string why = "Sahnede FPS Gun Pack silahi bulunamadi.";
                Debug.LogWarning("[WeaponPackSetup] " + why);
                if (interactive) EditorUtility.DisplayDialog("Silah Paketi", why, "Tamam");
                return;
            }

            int done = 0, skipped = 0, failed = 0;
            var lines = new List<string>();
            foreach (var gun in guns)
            {
                if (gun.GetComponent<GrabbableObject>() != null)
                {
                    // Kablolu ama profilsiz kalmis silah (orn. o gunku geciste kabza parcasi
                    // bulunamamisti): yalnizca profili tamamla, fizik/kablolamaya dokunma.
                    if (HasProfile(gun)) { skipped++; continue; }
                    string fix = WriteProfileFor(gun, repositionMuzzle: false);
                    lines.Add("  " + gun.name + ": profil tamamlandi — " + fix);
                    done++;
                    continue;
                }

                // NGO ic ice NetworkObject yasagi: ust zincirde NetworkObject varsa dokunma.
                if (gun.transform.parent != null &&
                    gun.transform.parent.GetComponentInParent<NetworkObject>(true) != null)
                {
                    lines.Add("  " + gun.name + ": ATLANDI (ust objede NetworkObject var — ic ice yasak)");
                    failed++;
                    continue;
                }

                // 1) Geometrik fizik (bolge kutulari + Rigidbody).
                GunPhysicsSetup.Apply(gun, false);

                // 2) "Muzzle" adli MESH parcasi tuzagini coz.
                RenameMuzzleMeshes(gun.transform);

                // 3) Kablolama: ag + tutma + Muzzle cocugu + NetworkWeapon.
                var geo = Solve(gun.transform);
                WireOne(gun, geo);

                // 4) Tutus profili (kabza parcasi bulunduysa).
                string profLog = WriteProfileFor(gun, repositionMuzzle: false, precomputed: geo);
                lines.Add("  " + gun.name + ": kuruldu — " + profLog);
                EditorUtility.SetDirty(gun);
                EditorSceneManager.MarkSceneDirty(gun.scene);
                done++;
            }

            string msg = "Silah paketi kurulumu: " + done + " kuruldu, " + skipped
                + " zaten hazirdi, " + failed + " atlandi.\n" + string.Join("\n", lines)
                + "\nSahneyi kaydet (Ctrl+S). Ince ayar: profili sec (Scene handle'lari) ya da "
                + "kulaklikta yakalama (thumbstick) + menu 31/33.";
            Debug.Log("[WeaponPackSetup] " + msg);
            if (interactive) EditorUtility.DisplayDialog("Silah Paketi", msg, "Tamam");
        }

        /// <summary>Sahnedeki paket silah kokleri (el bombalari haric).</summary>
        static List<GameObject> FindPackGuns()
        {
            var list = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var go = t.gameObject;
                if (!go.scene.IsValid()) continue;
                if (PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go) continue;
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (string.IsNullOrEmpty(path) || !path.StartsWith(PackRoot)) continue;
                if (path.Contains("Grenade")) continue;
                list.Add(go);
            }
            return list;
        }

        static bool IsPistol(GameObject go)
        {
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return !string.IsNullOrEmpty(path) && path.Contains("Pistol");
        }

        // ------------------------------------------------------------------ geometri cozumu

        struct GunGeo
        {
            public bool valid;
            public Quaternion frame;      // dunya: +Z = namlu, +Y = ust
            public Vector3 fwd, up;       // dunya
            public Vector3 fwdLocal;      // kok-lokal (profil icin)
            public Vector3 anchorW;       // kabza cipasi (dunya); gripFound=false ise anlamsiz
            public bool gripFound;
            public Vector3 muzzleW;       // namlu ucu (dunya)
            public bool muzzleExact;      // namlu adli parcadan mi (yoksa kaba AABB tahmini mi)
        }

        static GunGeo Solve(Transform root)
        {
            var geo = new GunGeo();

            // Kozmetik olmayan tum parcalarin kok-lokal AABB'si.
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            var parts = new List<MeshFilter>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (MatchesAny(mf.name, IgnorePatterns)) continue;
                parts.Add(mf);
                AccumulateCorners(mf, root, ref min, ref max);
            }
            if (parts.Count == 0) return geo; // valid=false

            Vector3 size = max - min;
            Vector3 centerLocal = (min + max) * 0.5f;

            // Namlu ekseni = en uzun AABB ekseni (silahlar namlu boyunca uzundur).
            Vector3 axis = Vector3.right;
            float len = size.x;
            if (size.y > len) { axis = Vector3.up; len = size.y; }
            if (size.z > len) axis = Vector3.forward;

            // Kabza (varsa) hem cipa hem de "on taraf" isaretinin dayanagi.
            MeshFilter grip = FindPart(root, GripPatterns, GripExclude);
            MeshFilter front = FindFrontPart(root, axis, centerLocal);

            float sign;
            if (front != null)
                sign = Mathf.Sign(Vector3.Dot(root.InverseTransformPoint(PartCenter(front)) - centerLocal, axis));
            else if (grip != null)
                sign = Mathf.Sign(Vector3.Dot(centerLocal - root.InverseTransformPoint(PartCenter(grip)), axis));
            else
                sign = 1f;
            if (sign == 0f) sign = 1f;

            geo.fwdLocal = axis * sign;
            geo.fwd = root.TransformDirection(geo.fwdLocal).normalized;
            geo.up = (root.up - geo.fwd * Vector3.Dot(root.up, geo.fwd)).normalized;
            geo.frame = Quaternion.LookRotation(geo.fwd, geo.up);

            // Namlu ucu.
            if (front != null)
            {
                geo.muzzleW = PartTipAlong(front, geo.fwd);
                geo.muzzleExact = true;
            }
            else
            {
                Vector3 tipLocal = centerLocal + axis * (sign * Vector3.Dot(size, Abs(axis)) * 0.5f);
                geo.muzzleW = root.TransformPoint(tipLocal);
                geo.muzzleExact = false;
            }

            // Kabza cipasi: kabza kutusunun ust-orta noktasi (Dmr1'dekiyle ayni recete).
            if (grip != null)
            {
                Quaternion invFrame = Quaternion.Inverse(geo.frame);
                Vector3 gMin, gMax;
                FrameExtents(grip, invFrame, out gMin, out gMax);
                Vector3 anchorF = new Vector3(
                    (gMin.x + gMax.x) * 0.5f,
                    (gMin.y + gMax.y) * 0.5f + (gMax.y - gMin.y) * 0.18f,
                    (gMin.z + gMax.z) * 0.5f);
                geo.anchorW = geo.frame * anchorF;
                geo.gripFound = true;
            }
            else
            {
                // Ikinci paket stilinde kabza adli parca yok (main_frame/stock/trigger — orn.
                // Rifle 3). Tetikten tur: kabza her silahta tetigin hemen arka-altidir; cerceve
                // uzayinda (dunya metresi) 3 cm geri, 5 cm asagi kabza orta-ustune denk gelir.
                MeshFilter trig = FindPart(root, TriggerPatterns, null);
                if (trig != null)
                {
                    Quaternion invFrame = Quaternion.Inverse(geo.frame);
                    Vector3 tMin, tMax;
                    FrameExtents(trig, invFrame, out tMin, out tMax);
                    Vector3 tc = (tMin + tMax) * 0.5f;
                    geo.anchorW = geo.frame * new Vector3(tc.x, tc.y - 0.05f, tc.z - 0.03f);
                    geo.gripFound = true;
                }
            }

            geo.valid = true;
            return geo;
        }

        // ------------------------------------------------------------------ kablolama

        static void WireOne(GameObject go, GunGeo geo)
        {
            Transform root = go.transform;

            if (go.GetComponent<NetworkObject>() == null) go.AddComponent<NetworkObject>();

            var cnt = go.GetComponent<ClientNetworkTransform>();
            if (cnt == null) cnt = go.AddComponent<ClientNetworkTransform>();
            cnt.SyncPositionX = cnt.SyncPositionY = cnt.SyncPositionZ = true;
            cnt.SyncRotAngleX = cnt.SyncRotAngleY = cnt.SyncRotAngleZ = true;
            cnt.SyncScaleX = cnt.SyncScaleY = cnt.SyncScaleZ = false;
            cnt.Interpolate = true;

            var grab = go.GetComponent<GrabbableObject>();
            if (grab == null) grab = go.AddComponent<GrabbableObject>();
            grab.snapToHand = true;

            var muzzleT = root.Find("Muzzle");
            if (muzzleT == null)
            {
                var mgo = new GameObject("Muzzle");
                muzzleT = mgo.transform;
                muzzleT.SetParent(root, false);
            }
            if (geo.valid)
            {
                muzzleT.position = geo.muzzleW;
                muzzleT.rotation = Quaternion.LookRotation(geo.fwd, geo.up);
            }

            var weapon = go.GetComponent<NetworkWeapon>();
            if (weapon == null) weapon = go.AddComponent<NetworkWeapon>();
            weapon.muzzle = muzzleT;
        }

        /// <summary>"Muzzle" adli MESH parcalarini "MuzzleMesh" yapar — NetworkWeapon/menu 16'nin
        /// Find("Muzzle") cagrisi gorseli namlu ucu sanmasin.</summary>
        static void RenameMuzzleMeshes(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "Muzzle" && (t.GetComponent<MeshFilter>() != null || t.GetComponent<Renderer>() != null))
                    t.name = "MuzzleMesh";
        }

        // ------------------------------------------------------------------ profil uretimi

        static string WriteProfileFor(GameObject go, bool repositionMuzzle, GunGeo? precomputed = null)
        {
            bool pistol = IsPistol(go);
            string baseName = pistol ? PistolBase : LongGunBase;
            var baseProfile = FindProfileAsset(baseName);
            if (baseProfile == null) return "profil YOK (" + baseName + " bulunamadi)";

            Transform baseRoot = FindBaseInScene(baseProfile);
            if (baseRoot == null) return "profil YOK (" + baseName + " ile eslesen sahne silahi yok)";

            var geo = precomputed ?? Solve(go.transform);
            if (!geo.valid) return "profil YOK (mesh bulunamadi)";
            if (!geo.gripFound) return "profil YOK (kabza parcasi bulunamadi — legacy tutusla kalir)";

            Transform root = go.transform;

            // Taban silahin namlu cercevesi ve el iliskileri (dunya-metre).
            Vector3 bBarrelLocal = baseProfile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? baseProfile.barrelLocalDirection.normalized : Vector3.forward;
            Vector3 bFwd = baseRoot.TransformDirection(bBarrelLocal).normalized;
            Vector3 bUp = (baseRoot.up - bFwd * Vector3.Dot(baseRoot.up, bFwd)).normalized;
            Quaternion bFrame = Quaternion.LookRotation(bFwd, bUp);

            Quaternion anchorRel = Quaternion.Inverse(bFrame) * (baseRoot.rotation * baseProfile.GripLocalRotation);
            Vector3 bAnchorW = baseRoot.TransformPoint(baseProfile.gripLocalPosition);
            Vector3 railOffset = Quaternion.Inverse(bFrame)
                * (baseRoot.TransformPoint(baseProfile.supportRailLocalStart) - bAnchorW);

            Quaternion anchorRotW = geo.frame * anchorRel;
            Vector3 railW = geo.anchorW + geo.frame * railOffset;

            // Uzun silahta destek eli namlu bolgesinde kalsin: cipa ile namlu ucu arasina kelepce.
            if (!pistol)
            {
                Quaternion invFrame = Quaternion.Inverse(geo.frame);
                Vector3 railF = invFrame * railW;
                float lo = (invFrame * geo.anchorW).z + 0.10f;
                float hi = (invFrame * geo.muzzleW).z - 0.08f;
                if (hi > lo) railF.z = Mathf.Clamp(railF.z, lo, hi);
                railW = geo.frame * railF;
            }

            string cleanName = TrimCopySuffix(WeaponGripBinder.CleanName(go.name));
            string assetPath = ProfileFolder + "/" + cleanName.Replace(" ", "") + "_GripProfile.asset";

            var p = Object.Instantiate(baseProfile);
            p.name = cleanName.Replace(" ", "") + "_GripProfile";
            p.weaponNameEquals = cleanName;
            p.weaponNameContains = cleanName; // kopyalar ("Rifle 1 (1)") da eslessin
            p.gripLocalPosition = root.InverseTransformPoint(geo.anchorW);
            p.gripLocalEuler = (Quaternion.Inverse(root.rotation) * anchorRotW).eulerAngles;
            p.barrelLocalDirection = geo.fwdLocal.normalized;
            p.supportRailLocalStart = root.InverseTransformPoint(railW);
            p.supportRailLocalEnd = p.supportRailLocalStart;
            p.createMuzzleIfMissing = false; // Muzzle cocugu sahnede, dogru yonuyle

            var existing = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(p, assetPath);
            }
            else
            {
                EditorUtility.CopySerialized(p, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(p);
            }
            AssetDatabase.SaveAssets();

            // Ates parametreleri profilden geliyorsa NetworkWeapon alanlarini da esitle.
            var weapon = go.GetComponent<NetworkWeapon>();
            if (weapon != null && baseProfile.overrideFire)
            {
                weapon.fireInterval = baseProfile.fireInterval;
                weapon.range = baseProfile.range;
            }

            if (repositionMuzzle)
            {
                var muzzleT = root.Find("Muzzle");
                if (muzzleT != null)
                {
                    muzzleT.position = geo.muzzleW;
                    muzzleT.rotation = Quaternion.LookRotation(geo.fwd, geo.up);
                }
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }

            return "profil " + (existing == null ? "olusturuldu" : "guncellendi")
                + " (" + (pistol ? "taban: Pistol" : "taban: HK416") + ", "
                + (geo.muzzleExact ? "namlu ucu parcadan" : "namlu ucu KABA tahmin") + ")";
        }

        // ------------------------------------------------------------------ yardimcilar

        /// <summary>Silahin adi (kopya eki atilmis) herhangi bir profille eslesiyor mu?</summary>
        static bool HasProfile(GameObject gun)
        {
            string name = TrimCopySuffix(WeaponGripBinder.CleanName(gun.name));
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null && p.MatchScore(name) > 0) return true;
            }
            return false;
        }

        static WeaponGripProfile FindProfileAsset(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponGripProfile " + name))
            {
                var a = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (a != null && a.name == name) return a;
            }
            return null;
        }

        static Transform FindBaseInScene(WeaponGripProfile profile)
        {
            foreach (var g in Object.FindObjectsByType<GrabbableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (profile.MatchScore(WeaponGripBinder.CleanName(g.name)) > 0)
                    return g.transform;
            return null;
        }

        /// <summary>"Rifle 1 (2)" -> "Rifle 1" (Unity kopya numarasini at).</summary>
        static string TrimCopySuffix(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");
        }

        static bool MatchesAny(string rawName, string[] patterns)
        {
            string n = rawName.ToLowerInvariant();
            foreach (var pat in patterns)
                if (n.Contains(pat)) return true;
            return false;
        }

        static MeshFilter FindPart(Transform root, string[] mustContain, string[] mustNotContain)
        {
            MeshFilter best = null;
            float bestVol = -1f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!MatchesAny(mf.name, mustContain)) continue;
                if (mustNotContain != null && MatchesAny(mf.name, mustNotContain)) continue;
                Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                float vol = Mathf.Abs(s.x * s.y * s.z);
                if (vol > bestVol) { bestVol = vol; best = mf; }
            }
            return best;
        }

        /// <summary>Namlu adli parcalardan, eksen boyunca AABB merkezinden en uzak olani.</summary>
        static MeshFilter FindFrontPart(Transform root, Vector3 axisLocal, Vector3 centerLocal)
        {
            MeshFilter best = null;
            float bestD = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!MatchesAny(mf.name, FrontPatterns)) continue;
                float d = Mathf.Abs(Vector3.Dot(root.InverseTransformPoint(PartCenter(mf)) - centerLocal, axisLocal));
                if (best == null || d > bestD) { bestD = d; best = mf; }
            }
            return best;
        }

        static Vector3 PartCenter(MeshFilter mf)
        {
            return mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
        }

        static void AccumulateCorners(MeshFilter mf, Transform root, ref Vector3 min, ref Vector3 max)
        {
            Bounds b = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3((i & 1) == 0 ? 1 : -1, (i & 2) == 0 ? 1 : -1, (i & 4) == 0 ? 1 : -1);
                Vector3 p = root.InverseTransformPoint(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, sign)));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        static void FrameExtents(MeshFilter mf, Quaternion invFrame, out Vector3 min, out Vector3 max)
        {
            Bounds b = mf.sharedMesh.bounds;
            min = Vector3.positiveInfinity;
            max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3((i & 1) == 0 ? 1 : -1, (i & 2) == 0 ? 1 : -1, (i & 4) == 0 ? 1 : -1);
                Vector3 pf = invFrame * mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, sign));
                min = Vector3.Min(min, pf);
                max = Vector3.Max(max, pf);
            }
        }

        static Vector3 PartTipAlong(MeshFilter mf, Vector3 fwd)
        {
            Bounds b = mf.sharedMesh.bounds;
            Vector3 c = PartCenter(mf);
            float maxOff = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3((i & 1) == 0 ? 1 : -1, (i & 2) == 0 ? 1 : -1, (i & 4) == 0 ? 1 : -1);
                float off = Vector3.Dot(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, sign)) - c, fwd);
                if (off > maxOff) maxOff = off;
            }
            return c + fwd * maxOff;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
