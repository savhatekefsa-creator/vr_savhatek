using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using VRMultiplayer;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Sahnedeki "Dmr1" silahina HK416'nin tutusunu tasir — tek seferlik kurulum + menu ile
    /// yeniden hesaplama.
    ///
    /// Neler yapilir:
    ///  1. Sahne objesi HK416 ile ayni sekilde kablolanir (collider, Rigidbody, NetworkObject,
    ///     ClientNetworkTransform, GrabbableObject, NetworkWeapon + dogru yonlu Muzzle cocugu).
    ///     HK416 tek mesh oldugu icin collider'i kokteydi; Dmr1 40+ parcali, o yuzden convex
    ///     MeshCollider EN BUYUK parcaya (govde) takilir — kavrama/collider davranisi ayni kalir.
    ///  2. Dmr1_GripProfile.asset uretilir: HK416 profilinin KOPYASI (parmak pozlari, bilek
    ///     ofsetleri, ates/tepme/sarjor... hepsi ayni). Yalnizca silah-LOKAL degerler Dmr1
    ///     geometrisine gore yeniden hesaplanir:
    ///       - Namlu ekseni: nozzle-stock parcalarindan (kok-lokal dominant eksene oturtulur).
    ///       - Kabza cipasi: handle12 parcasinin ust-orta noktasi. Cipanin YONELIMI ise HK416'da
    ///         kulaklikta yakalanan kumanda egiminin namlu-cercevesine gore birebir tasinmasidir
    ///         (tutusun ozu budur). Bilek ofsetleri metre cinsinden el boyu oldugu icin aynen
    ///         kalir (WeaponHandWeld boyle tuketiyor).
    ///       - Destek rayi: HK416'da cipaya gore dunya-metre ofseti korunur, Dmr1'in kundagi
    ///         (front_pattern) icine kelepcelenir.
    ///  3. Sahne kirli isaretlenir — kontrol edip Ctrl+S ile kaydet (NGO GlobalObjectIdHash
    ///     kayitta uretilir).
    ///
    /// Otomatik calisma: derleme sonrasi sahnede kablosuz bir "Dmr1" veya eksik profil varsa BIR
    /// kez calisir; her sey tamamsa hicbir seye dokunmaz. Elle ince ayar sonrasi degerlerin
    /// ezilmemesi icin otomatik yol profili yeniden HESAPLAMAZ — bunu yalnizca menu yapar.
    ///
    /// KULAKLIK YAKALAMASI (menu 33): asagidaki Cap* sabitleri GripCapture araciyla kulaklikta
    /// alinan GERCEK Dmr1 pozu (sag el kabzada = ana, sol el kundakta = destek). Bir kez otomatik
    /// uygulanir (EditorPrefs kilidi), sonrasi elle ayara acik; menu 32 yeniden hesaplasa bile
    /// yakalanan poz ustune geri yazilir. Yeni yakalama yapilirsa sabitleri guncelle ve
    /// CaptureLatchKey surumunu artir (v1 -> v2) ki bir kez daha otomatik uygulansin.
    /// </summary>
    public static class Dmr1GripSetup
    {
        const string WeaponName = "Dmr1";
        const string ProfilePath = "Assets/_VRMultiplayer/Resources/WeaponGripProfiles/Dmr1_GripProfile.asset";
        const string SourceProfileName = "HK416_GripProfile";

        // ---- Kulaklikta yakalanan poz (2026-07-17, WeaponGripCaptureTool ciktisi) ----------
        // Capture araci wrist'i HER ELIN KENDI anchor cercevesinde yazar; WeaponHandWeld ise
        // destek bilegini ANA kabza rotasyonu cercevesinde uygular — bu yuzden destek wrist
        // degerleri uygulanirken asagida ana cerceveye cevrilir (toMain).
        const string CaptureLatchKey = "Dmr1GripSetup.capturedPose.v1";
        static readonly Vector3 CapMainGripPos = new Vector3(0.0084f, 0.1047f, 0.3244f);
        static readonly Vector3 CapMainGripEuler = new Vector3(323.7489f, 225.7748f, 332.3877f);
        static readonly Vector3 CapMainWristPos = new Vector3(0f, -0.0001f, -0.0851f);
        static readonly Vector3 CapMainWristEuler = new Vector3(321.9171f, 274.5795f, 225.6053f);
        static readonly Vector3 CapSupGripPos = new Vector3(0.1355f, 0.0788f, -0.1398f);
        static readonly Vector3 CapSupGripEuler = new Vector3(6.8972f, 182.0183f, 103.5676f);
        static readonly Vector3 CapSupWristPos = new Vector3(0.0002f, 0f, -0.0848f);
        static readonly Vector3 CapSupWristEuler = new Vector3(327.4185f, 84.8939f, 133.3258f);

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
                EditorApplication.delayCall += TryAutoRun; // import/derleme bitince tekrar dene
                return;
            }

            var dmr = FindSceneWeapon();
            if (dmr == null) return; // bu sahnede Dmr1 yok — sessizce cik

            bool wired = dmr.GetComponent<GrabbableObject>() != null
                      && dmr.GetComponent<NetworkWeapon>() != null
                      && dmr.transform.Find("Muzzle") != null;
            bool hasProfile = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(ProfilePath) != null;
            if (!wired || !hasProfile) Run(false);

            // Kulaklikta yakalanan poz bir kez otomatik yazilir; sonrasinda elle ayara acik.
            if (!EditorPrefs.GetBool(CaptureLatchKey, false))
                ApplyCapturedPose(false);
        }

        [MenuItem("Tools/VR Multiplayer/32. Dmr1 - HK416 Tutusunu Uygula (yeniden hesapla)")]
        public static void RunFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Dmr1 Tutus",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }
            Run(true);
        }

        [MenuItem("Tools/VR Multiplayer/33. Dmr1 - Yakalanan Pozu Yeniden Yaz")]
        public static void ApplyCapturedPoseMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Dmr1 Tutus",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }
            ApplyCapturedPose(true);
        }

        /// <summary>Kulaklikta yakalanan ana/destek pozunu Dmr1 profiline yazar. Ana el ([SAG])
        /// degerleri birebir gecer; destek elin ([SOL]) bilek ofseti, calisma zamaninda ana kabza
        /// rotasyonuyla uygulandigi icin sol anchor cercevesinden ana cerceveye cevrilir.</summary>
        static void ApplyCapturedPose(bool interactive)
        {
            var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(ProfilePath);
            if (p == null)
            {
                Fail(interactive, "Dmr1_GripProfile.asset yok — once kurulum calismali (menu 32).");
                return;
            }

            p.gripLocalPosition = CapMainGripPos;
            p.gripLocalEuler = CapMainGripEuler;
            p.mainHand.wristLocalPosition = CapMainWristPos;
            p.mainHand.wristLocalEuler = CapMainWristEuler;

            // Destek eli yakalanan noktaya sabitle (nokta-ray: baslangic = bitis).
            p.supportRailLocalStart = CapSupGripPos;
            p.supportRailLocalEnd = CapSupGripPos;

            Quaternion toMain = Quaternion.Inverse(Quaternion.Euler(CapMainGripEuler)) * Quaternion.Euler(CapSupGripEuler);
            p.supportHand.wristLocalPosition = toMain * CapSupWristPos;
            p.supportHand.wristLocalEuler = (toMain * Quaternion.Euler(CapSupWristEuler)).eulerAngles;

            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(CaptureLatchKey, true);

            string msg = "Kulaklikta yakalanan Dmr1 pozu profile yazildi.\n"
                + "  Ana el (SAG, kabza): cipa " + CapMainGripPos.ToString("F4")
                + " / euler " + CapMainGripEuler.ToString("F2") + "\n"
                + "  Destek (SOL, kundak) ray noktasi: " + CapSupGripPos.ToString("F4") + "\n"
                + "  Destek bilek (ana cerceveye cevrildi): "
                + p.supportHand.wristLocalPosition.ToString("F4")
                + " / euler " + p.supportHand.wristLocalEuler.ToString("F2") + "\n"
                + "Test: Play'de Dmr1'i SAG elle kabzadan tut, SOL eli kundaga getirip grip'e bas. "
                + "Destek elde terslik olursa menu 31'deki MirrorX duzeltmesiyle devam.";
            Debug.Log("[Dmr1GripSetup] " + msg);
            if (interactive) EditorUtility.DisplayDialog("Dmr1 Tutus", msg, "Tamam");
        }

        static void Run(bool interactive)
        {
            // ---- Kaynaklar -------------------------------------------------------------
            var hkProfile = FindProfileAsset(SourceProfileName);
            if (hkProfile == null) { Fail(interactive, "HK416_GripProfile.asset bulunamadi."); return; }

            var dmr = FindSceneWeapon();
            if (dmr == null) { Fail(interactive, "Sahnede '" + WeaponName + "' objesi bulunamadi."); return; }

            Transform hkRoot = FindHkInScene(hkProfile);
            if (hkRoot == null) { Fail(interactive, "Sahnede HK416 silahi bulunamadi (profil eslesmesi bos dondu)."); return; }

            Transform root = dmr.transform;

            // ---- Dmr1 geometrisi (hepsi DUNYA uzayinda, sonra kok-lokale cevrilir) -------
            MeshFilter handle = FindPart(root, "handle", null);
            MeshFilter trigger = FindPart(root, "trigger", "guard");
            MeshFilter frontPat = FindPart(root, "front_pattern", null);
            if (handle == null) { Fail(interactive, "Dmr1 altinda 'handle' parcasi yok — kabza bulunamadi."); return; }

            // Namlu yonu: en ondeki nozzle parcasi ile en arkadaki stock parcasi arasindaki dogru.
            Vector3 preAxis = (PartCenter(FindPart(root, "nozzle", null)) - PartCenter(handle)).normalized;
            MeshFilter nozzle = FindExtremePart(root, "nozzle", preAxis, true);
            MeshFilter stock = FindExtremePart(root, "stock", preAxis, false);
            if (nozzle == null) { Fail(interactive, "Dmr1 altinda 'nozzle' parcasi yok — namlu bulunamadi."); return; }

            Vector3 rearRef = stock != null ? PartCenter(stock) : PartCenter(handle);
            Vector3 fwd = (PartCenter(nozzle) - rearRef).normalized;

            // Model duz durdugu surece namlu kok eksenlerinden birine oturur — kucuk olcum
            // sapmasini at, gercekten egikse dokunma.
            Vector3 fl = root.InverseTransformDirection(fwd);
            Vector3 snapped = DominantAxis(fl);
            if (Vector3.Angle(fl, snapped) <= 12f) fl = snapped;
            fwd = root.TransformDirection(fl).normalized;

            Vector3 up = (root.up - fwd * Vector3.Dot(root.up, fwd)).normalized;
            Quaternion frame = Quaternion.LookRotation(fwd, up);          // Dmr1 namlu cercevesi
            Quaternion invFrame = Quaternion.Inverse(frame);

            // ---- HK416'dan tasinacak el iliskileri (dunya-metre; olcek TransformPoint'te) ---
            Vector3 hkBarrelLocal = hkProfile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? hkProfile.barrelLocalDirection.normalized : Vector3.forward;
            Vector3 hkFwd = hkRoot.TransformDirection(hkBarrelLocal).normalized;
            Vector3 hkUp = (hkRoot.up - hkFwd * Vector3.Dot(hkRoot.up, hkFwd)).normalized;
            Quaternion hkFrame = Quaternion.LookRotation(hkFwd, hkUp);

            // Kumandanin namluya gore yakalanmis egimi — tutusun kalbi, birebir tasinir.
            Quaternion anchorRel = Quaternion.Inverse(hkFrame) * (hkRoot.rotation * hkProfile.GripLocalRotation);

            Vector3 hkAnchorW = hkRoot.TransformPoint(hkProfile.gripLocalPosition);
            Vector3 railOffset = Quaternion.Inverse(hkFrame)
                * (hkRoot.TransformPoint(hkProfile.supportRailLocalStart) - hkAnchorW);

            // ---- Kabza cipasi: handle'in ust-orta noktasi (avuc kabzanin ustune oturur) -----
            Vector3 hMin, hMax;
            FrameExtents(handle, invFrame, out hMin, out hMax);
            Vector3 anchorF = new Vector3(
                (hMin.x + hMax.x) * 0.5f,
                (hMin.y + hMax.y) * 0.5f + (hMax.y - hMin.y) * 0.18f,
                (hMin.z + hMax.z) * 0.5f);
            Vector3 anchorW = frame * anchorF;
            Quaternion anchorRotW = frame * anchorRel;

            // ---- Destek rayi: HK416 el-arasi mesafesi korunur, kundaga kelepcelenir ---------
            Vector3 muzzleW = PartTipAlong(nozzle, fwd);
            Vector3 railW = anchorW + frame * railOffset;
            {
                Vector3 railF = invFrame * railW;
                float lo = anchorF.z + 0.12f, hi = (invFrame * muzzleW).z - 0.10f;
                if (frontPat != null)
                {
                    Vector3 fMin, fMax;
                    FrameExtents(frontPat, invFrame, out fMin, out fMax);
                    lo = Mathf.Max(lo, fMin.z + 0.02f);
                    hi = Mathf.Min(hi, fMax.z - 0.02f);
                }
                if (hi > lo) railF.z = Mathf.Clamp(railF.z, lo, hi);
                railW = frame * railF;
            }

            // ---- Sahne kablolamasi (HK416 tarifi: menu 10 + menu 16 esdegeri) --------------
            int added = 0;

            if (root.GetComponentInChildren<Collider>() == null)
            {
                MeshFilter biggest = BiggestMesh(root);
                if (biggest != null)
                {
                    var mc = biggest.gameObject.AddComponent<MeshCollider>();
                    mc.convex = true;
                    added++;
                }
                else { root.gameObject.AddComponent<BoxCollider>(); added++; }
            }

            var rb = root.GetComponent<Rigidbody>();
            if (rb == null) { rb = root.gameObject.AddComponent<Rigidbody>(); added++; }
            rb.mass = 2f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            if (root.GetComponent<NetworkObject>() == null) { root.gameObject.AddComponent<NetworkObject>(); added++; }

            var cnt = root.GetComponent<ClientNetworkTransform>();
            if (cnt == null) { cnt = root.gameObject.AddComponent<ClientNetworkTransform>(); added++; }
            cnt.SyncPositionX = cnt.SyncPositionY = cnt.SyncPositionZ = true;
            cnt.SyncRotAngleX = cnt.SyncRotAngleY = cnt.SyncRotAngleZ = true;
            cnt.SyncScaleX = cnt.SyncScaleY = cnt.SyncScaleZ = false;
            cnt.Interpolate = true;

            var grab = root.GetComponent<GrabbableObject>();
            if (grab == null) { grab = root.gameObject.AddComponent<GrabbableObject>(); added++; }
            var hkGrab = hkRoot.GetComponent<GrabbableObject>();
            if (hkGrab != null)
            {
                grab.snapToHand = hkGrab.snapToHand;
                grab.gripRotationEuler = hkGrab.gripRotationEuler;
            }

            // Muzzle cocugu: iz TAM namlu ucundan, forward = namlu (createMuzzleIfMissing'in
            // kimlik-rotasyon varsayimi Dmr1'in eksenine uymayabilir, o yuzden burada acikca).
            var muzzleT = root.Find("Muzzle");
            if (muzzleT == null)
            {
                var mgo = new GameObject("Muzzle");
                muzzleT = mgo.transform;
                muzzleT.SetParent(root, false);
                added++;
            }
            muzzleT.position = muzzleW;
            muzzleT.rotation = Quaternion.LookRotation(fwd, up);

            var weapon = root.GetComponent<NetworkWeapon>();
            if (weapon == null) { weapon = root.gameObject.AddComponent<NetworkWeapon>(); added++; }
            weapon.muzzle = muzzleT;
            weapon.fireInterval = hkProfile.overrideFire ? hkProfile.fireInterval : 0.18f;
            weapon.range = hkProfile.overrideFire ? hkProfile.range : 60f;

            // ---- Profil asseti: HK416 kopyasi + Dmr1-lokal anchor degerleri -----------------
            // Otomatik yol VAR OLAN profile dokunmaz (elle ince ayarlar ezilmesin); yeniden
            // hesaplamayi yalnizca menu yapar.
            Vector3 gripLocalPos = root.InverseTransformPoint(anchorW);
            Vector3 gripLocalEuler = (Quaternion.Inverse(root.rotation) * anchorRotW).eulerAngles;
            Vector3 barrelLocal = fl.normalized;
            Vector3 railLocal = root.InverseTransformPoint(railW);

            var existing = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(ProfilePath);
            bool writeProfile = interactive || existing == null;
            if (writeProfile)
            {
                var p = Object.Instantiate(hkProfile);
                p.name = WeaponName + "_GripProfile";
                p.weaponNameEquals = WeaponName;
                p.weaponNameContains = WeaponName; // kopyalar ("Dmr1 (1)") da eslessin
                p.gripLocalPosition = gripLocalPos;
                p.gripLocalEuler = gripLocalEuler;
                p.barrelLocalDirection = barrelLocal;
                p.supportRailLocalStart = railLocal;
                p.supportRailLocalEnd = railLocal;
                p.createMuzzleIfMissing = false; // Muzzle cocugu sahnede, dogru yonuyle duruyor

                if (existing == null)
                {
                    AssetDatabase.CreateAsset(p, ProfilePath);
                }
                else
                {
                    EditorUtility.CopySerialized(p, existing); // GUID sabit kalsin
                    EditorUtility.SetDirty(existing);
                    Object.DestroyImmediate(p);
                }
                AssetDatabase.SaveAssets();
            }

            EditorUtility.SetDirty(root.gameObject);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

            // Kulaklikta yakalanmis gercek poz varsa geometrik tahminlerin USTUNE geri yazilir —
            // menu 32 hicbir zaman yakalanan tutusu kaybettirmez.
            bool capturedReapplied = false;
            if (writeProfile && EditorPrefs.GetBool(CaptureLatchKey, false))
            {
                ApplyCapturedPose(false);
                capturedReapplied = true;
            }

            // ---- Rapor ----------------------------------------------------------------------
            float trigDist = trigger != null ? Vector3.Distance(anchorW, PartCenter(trigger)) : -1f;
            string profileNote = !writeProfile ? " (korundu — elle ayarlar ezilmedi)"
                : existing == null ? " (yeni)" : " (yeniden hesaplandi)";
            if (capturedReapplied) profileNote += " + kulaklik yakalamasi tekrar uygulandi";
            string msg = "Dmr1 tutus kurulumu tamam.\n"
                + "  Eklenen bilesen/cocuk: " + added + "\n"
                + "  Profil: " + ProfilePath + profileNote + "\n"
                + "  Namlu (kok-lokal): " + barrelLocal.ToString("F3") + "\n"
                + "  Kabza cipasi (kok-lokal): " + gripLocalPos.ToString("F4") + "\n"
                + "  Ray noktasi (kok-lokal): " + railLocal.ToString("F4") + "\n"
                + "  Muzzle (dunya): " + muzzleW.ToString("F3") + "\n"
                + (trigDist >= 0f
                    ? "  Cipa-tetik mesafesi: " + (trigDist * 100f).ToString("F1") + " cm"
                      + (trigDist > 0.12f ? "  <-- BEKLENENDEN UZAK, elle kontrol et!" : " (makul)")
                    : "  Uyari: 'trigger' parcasi bulunamadi, mesafe kontrolu yapilamadi.")
                + "\nSahne kirli birakildi — kontrol edip Ctrl+S ile kaydet. Ince ayar icin: "
                + "profili sec, Scene'de kabza/ray handle'lari cikar (Play modunda da kalici).";
            Debug.Log("[Dmr1GripSetup] " + msg);
            if (interactive) EditorUtility.DisplayDialog("Dmr1 Tutus", msg, "Tamam");
        }

        // ------------------------------------------------------------------ yardimcilar

        static void Fail(bool interactive, string why)
        {
            Debug.LogWarning("[Dmr1GripSetup] " + why);
            if (interactive) EditorUtility.DisplayDialog("Dmr1 Tutus", why, "Tamam");
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

        static GameObject FindSceneWeapon()
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.gameObject.scene.IsValid() && WeaponGripBinder.CleanName(t.name) == WeaponName)
                    return t.gameObject;
            return null;
        }

        static Transform FindHkInScene(WeaponGripProfile hkProfile)
        {
            foreach (var g in Object.FindObjectsByType<GrabbableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (hkProfile.MatchScore(WeaponGripBinder.CleanName(g.name)) > 0)
                    return g.transform;
            return null;
        }

        /// <summary>Adi 'mustContain' iceren EN BUYUK hacimli mesh parcasi.</summary>
        static MeshFilter FindPart(Transform root, string mustContain, string mustNotContain)
        {
            MeshFilter best = null;
            float bestVol = -1f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                string n = mf.name.ToLowerInvariant();
                if (!n.Contains(mustContain)) continue;
                if (mustNotContain != null && n.Contains(mustNotContain)) continue;
                Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                float vol = Mathf.Abs(s.x * s.y * s.z);
                if (vol > bestVol) { bestVol = vol; best = mf; }
            }
            return best;
        }

        /// <summary>Adi eslesen parcalardan, merkezi 'axis' boyunca en ileri/en geri olani.</summary>
        static MeshFilter FindExtremePart(Transform root, string mustContain, Vector3 axis, bool furthest)
        {
            MeshFilter best = null;
            float bestD = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!mf.name.ToLowerInvariant().Contains(mustContain)) continue;
                float d = Vector3.Dot(PartCenter(mf), axis) * (furthest ? 1f : -1f);
                if (best == null || d > bestD) { bestD = d; best = mf; }
            }
            return best;
        }

        static MeshFilter BiggestMesh(Transform root)
        {
            MeshFilter best = null;
            float bestSize = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > bestSize) { bestSize = s; best = mf; }
            }
            return best;
        }

        static Vector3 PartCenter(MeshFilter mf)
        {
            if (mf == null) return Vector3.zero;
            return mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
        }

        static readonly Vector3[] CornerSigns =
        {
            new Vector3( 1,  1,  1), new Vector3( 1,  1, -1), new Vector3( 1, -1,  1), new Vector3( 1, -1, -1),
            new Vector3(-1,  1,  1), new Vector3(-1,  1, -1), new Vector3(-1, -1,  1), new Vector3(-1, -1, -1),
        };

        /// <summary>Parcanin 8 kose noktasinin verilen cercevedeki (invFrame * dunya) AABB'si.</summary>
        static void FrameExtents(MeshFilter mf, Quaternion invFrame, out Vector3 min, out Vector3 max)
        {
            Bounds b = mf.sharedMesh.bounds;
            min = Vector3.positiveInfinity;
            max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, CornerSigns[i]);
                Vector3 pf = invFrame * mf.transform.TransformPoint(corner);
                min = Vector3.Min(min, pf);
                max = Vector3.Max(max, pf);
            }
        }

        /// <summary>Parca merkezinden 'fwd' yonunde parcanin en uc noktasina uzanan nokta.</summary>
        static Vector3 PartTipAlong(MeshFilter mf, Vector3 fwd)
        {
            Bounds b = mf.sharedMesh.bounds;
            Vector3 c = PartCenter(mf);
            float maxOff = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, CornerSigns[i]);
                float off = Vector3.Dot(mf.transform.TransformPoint(corner) - c, fwd);
                if (off > maxOff) maxOff = off;
            }
            return c + fwd * maxOff;
        }

        static Vector3 DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) return new Vector3(Mathf.Sign(v.x), 0, 0);
            if (ay >= az) return new Vector3(0, Mathf.Sign(v.y), 0);
            return new Vector3(0, 0, Mathf.Sign(v.z));
        }
    }
}
