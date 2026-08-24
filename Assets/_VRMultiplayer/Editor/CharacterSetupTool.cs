using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// KARAKTER SISTEMI KURULUMU (Tools &gt; Karakter &gt; Kur). Tek tikla ve TEKRAR
    /// CALISTIRILABILIR sekilde:
    ///
    ///  1. NetworkPlayer prefabindaki 10 vucut parcasini ESKI FBX'e (US-Soldier-Colored)
    ///     BAGLI TUTAR — mesh + kemik dizisi + Animator avatari eski FBX'ten yeniden
    ///     baglanir. GOVDE YENI FBX'E TASINMAZ, BILEREK: yeni paket (vol2) SAG KOLU
    ///     yeniden riglemis (Right_LowerArm 43.9,0,0 vs eski 0.7,2.8,15.3; parmak eksenleri
    ///     ~90 derece kaymis). Yeni avatar Animator'a takilinca sag el kemiklerine yeni
    ///     eksenlere gore rotasyon yaziliyor ve ESKI bind pozuyla uretilen FP_Hands ile
    ///     eski eksenlere ayarli ProceduralFingerPoser/WeaponGrip sag eli TERS bukuyordu.
    ///     Sol tarafta iki rig birebir ayni — sorun yalnizca sag elde gorunuyordu.
    ///  2. Kafa aksesuar mesh'lerini (kask, sapka, boonie, kar maskesi, maske) YENI FBX'ten
    ///     alip ayni iskelete AD YOLUYLA baglar — guvenli, cunku aksesuarlarin agirlik
    ///     verdigi kemiklerin (Head/Neck/UpperChest/omuzlar) duruslari iki rigde AYNI
    ///     (dogrulandi). Iskeletteki eksik kemikler FBX'ten kopyalanir (sifir agirlikli
    ///     dolgu — gorunume etkisi yok). Varsayilan KAPALI, secimi CharacterCustomizer acar.
    ///  3. KADIN KAFAYI FemaleHeadKach.fbx'ten aşilar — IKI VARYANT: normal (KachFull) ve
    ///     topuzu yassilastirilmis (KachNoBun, tepeyi kapatan aksesuarlar icin). Mixamo
    ///     modelinden alinip Blender'da bizim iskelete baglandi; yuz + sac + goz + boyun
    ///     tek mesh. Kadin malzemelerini YOKSA URETIR (URP AutodeskInteractive).
    ///  4. CharacterCustomizer + PlayerAppearance bilesenlerini kurar, secenek listelerini
    ///     doldurur. 0. indeks HER LISTEDE prefabin MEVCUT malzemesi (M_Soldier_*): kimse
    ///     secim yapmazsa oyuncular bugunku gorunumleriyle spawn olur.
    ///  5. Karakter ekrani icin MANKEN prefabi uretir (Resources/CharacterMannequin).
    ///
    /// Runtime'da AssetDatabase yok — malzeme listeleri bu aracla prefaba SERILESTIRILIR.
    /// Yeni secenek eklemek: asagidaki tablolara satir ekle, araci yeniden calistir.
    /// </summary>
    public static class CharacterSetupTool
    {
        const string PrefabPath = "Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab";
        const string FbxPath = "Assets/Soldiers-Pack/Mesh/US-Soldier.fbx";
        const string OldFbxPath = "Assets/Soldiers-Pack/Mesh/US-Soldier-Colored.fbx";
        const string FemaleFbxPath = "Assets/_VRMultiplayer/Models/FemaleHeadKach.fbx";
        const string MatDir = "Assets/Soldiers-Pack/Matierials/";
        const string TexDir = "Assets/Soldiers-Pack/Textures/";
        const string ModelDir = "Assets/_VRMultiplayer/Models/";
        const string MannequinDir = "Assets/Resources";
        const string MannequinPath = MannequinDir + "/CharacterMannequin.prefab";

        /// <summary>Eski FBX'ten baglanan parcalar — adlar iki FBX'te de birebir ayni.</summary>
        static readonly string[] BodyParts =
            { "Belt", "Bodyarmour", "boots.001", "Eye", "Glove", "Heads", "Jaket", "Necck", "Pants", "watch" };

        // Kafa secenekleri: (kafa malzemesi, boyun malzemesi, kadin mi). Bos kafa adi =
        // prefabin mevcut malzemesi (varsayilan). Kadin malzemeleri EnsureFemaleMaterials
        // uretir; boyun ten uyumu icin en yakin erkek boyun malzemesi kullanilir (kadin
        // cilt texture'u erkekten turedigi icin ton farki gozle gorulmez).
        static readonly (string headMat, string neckMat, bool female)[] HeadOptions =
        {
            ("",                     "",         false),   // varsayilan (M_Soldier_*)
            ("M_Head",               "M_Neck",   false),
            ("M_Head 1",             "M_Neck 1", false),
            ("M_Head 2",             "M_Neck 2", false),
            ("M_Head 3",             "M_Neck 3", false),
            ("M_Female_Kach",        "",         true),    // kadin — kahverengi sac
            ("M_Female_Kach_Blonde", "",         true),    // kadin — sari sac
        };

        static readonly string[] JacketMats =
            { "Jaket", "Jaket 1", "Jaket 2", "Jaket 3", "Jaket 4", "Jaket 5", "Jaket 6", "Jaket 7", "Jaket 8", "Jaket 9" };
        static readonly string[] PantsMats =
            { "M_Pants Skin1", "M_Pants Skin1 1", "M_Pants Skin1 2", "M_Pants Skin1 3",
              "M_Pants Skin1 4", "M_Pants Skin1 5", "M_Pants Skin1 6", "M_Pants Skin1 7" };

        /// <summary>
        /// Aksesuarlar: (ekran adi, FBX mesh adi, malzeme, kadin kaydirmasi, kadin olcegi,
        /// topuzu gizler mi).
        ///
        /// KADIN KAYDIRMA/OLCEK: kadin kafasinin saci toplu; sapka ve boonie sacin icine
        /// gomuluyor, birkac cm yukari + biraz genis gerekiyor. Kask ve maskeler yeterince
        /// buyuk — 0/1 birakilir, yoksa havada dururlardi.
        ///
        /// TOPUZU GIZLER: yalnizca TEPEYI KAPATAN parcalar (kask, sapka, boonie, kafayi
        /// saran maske). Kar maskeleri agzi/boynu kapatir, tepeye dokunmaz — onlarda topuz
        /// gizlenirse kafa KESIK gorunur (topuzsuz varyant tepeyi yassilastiriyor).
        /// </summary>
        static readonly (string label, string mesh, string mat,
                         float femaleLift, float femaleScale, bool hidesBun)[] Accessories =
        {
            ("KASK A",        "helmet.001", "M_Helmet 1",     0f,     1f,    true),
            ("KASK B",        "helmet.001", "M_Helmet 2",     0f,     1f,    true),
            ("ŞAPKA A",       "Cap",        "M_Cap",          0.030f, 1.12f, true),
            ("ŞAPKA B",       "Cap",        "M_Cap 1",        0.030f, 1.12f, true),
            ("BOONIE",        "boonie",     "M_Boonie_Skin1", 0.022f, 1.06f, true),
            ("KAR MASKESİ A", "Balaclava",  "M_Balaclava",    0f,     1f,    false),
            ("KAR MASKESİ B", "Balaclava",  "M_Balaclava 1",  0f,     1f,    false),
            ("MASKE",         "Mask",       "M_Mask",         0f,     1f,    true),
        };

        [MenuItem("Tools/Karakter/Kur (FBX gecisi + secenekler)")]
        public static void Run()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var oldFbx = AssetDatabase.LoadAssetAtPath<GameObject>(OldFbxPath);
            var femaleFbx = AssetDatabase.LoadAssetAtPath<GameObject>(FemaleFbxPath);
            if (fbx == null) { Debug.LogError("[CharacterSetup] FBX yok: " + FbxPath); return; }
            if (oldFbx == null) { Debug.LogError("[CharacterSetup] FBX yok: " + OldFbxPath); return; }
            if (femaleFbx == null)
                Debug.LogWarning("[CharacterSetup] Kadin FBX yok (" + FemaleFbxPath +
                                 ") — kadin secenekleri atlanacak.");

            DisableCamerasAndLights(FbxPath);
            DisableCamerasAndLights(FemaleFbxPath);
            EnsureFemaleMaterials();

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                if (!Migrate(root.transform, fbx.transform, oldFbx.transform,
                             femaleFbx != null ? femaleFbx.transform : null))
                    return;   // hata loglandi, KAYDETME

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            BuildMannequin();
            AssetDatabase.SaveAssets();
            Debug.Log("[CharacterSetup] Tamam: govde eski FBX'te, aksesuarlar + kadin kafa " +
                      "(2 varyant) aşılandı, secenek listeleri ve manken guncellendi.");
        }

        /// <summary>Karakter FBX'lerinde kamera/isik importunu kapat (harita FBX'lerindeki
        /// "FBX kamerasi VR goruntuyu ele geciriyor" tuzagi bu dosyalarda tekrarlamasin).</summary>
        static void DisableCamerasAndLights(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) return;

            // isReadable ZORUNLU: kapaliyken mesh verisi yalnizca GPU'da yasar ve
            // Object.Instantiate(mesh) BOS bir kopya uretir. O bos kopya asset olarak
            // yazilinca Unity "does not match the expected mesh data size and vertex
            // stride" deyip parcayi hic cizmez (kadin kafasi gorunmez olmustu).
            bool dirty = imp.importCameras || imp.importLights || !imp.isReadable;
            if (!dirty) return;

            imp.importCameras = false;
            imp.importLights = false;
            imp.isReadable = true;
            imp.SaveAndReimport();
        }

        // ------------------------------------------------------------------ kadin malzemeleri

        /// <summary>
        /// Kadin kafa malzemelerini YOKSA uretir. Kadin kafasi Mixamo modelinden geldigi icin
        /// KENDI texture setini kullanir (yuz + sac + goz tek atlas'ta); erkek kafa
        /// haritalariyla ilgisi yok. Shader paket malzemeleriyle ayni tutulur
        /// (URP AutodeskInteractive) — SRP batching ve isik tepkisi tutarli kalsin.
        /// </summary>
        static void EnsureFemaleMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Autodesk Interactive/AutodeskInteractive");
            if (shader == null) { Debug.LogWarning("[CharacterSetup] URP AutodeskInteractive yok."); return; }

            MakeFemaleMat("M_Female_Kach", "T_Female_Diffuse.png", shader);
            MakeFemaleMat("M_Female_Kach_Blonde", "T_Female_Diffuse_Blonde.png", shader);
        }

        static void MakeFemaleMat(string name, string diffuse, Shader shader)
        {
            string path = ModelDir + name + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;

            var m = new Material(shader);
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(ModelDir + diffuse));
            m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(ModelDir + "T_Female_Normal.png"));
            m.SetFloat("_UseColorMap", 1f);
            m.SetFloat("_UseNormalMap", m.GetTexture("_BumpMap") != null ? 1f : 0f);
            m.SetFloat("_UseMetallicMap", 0f);
            m.SetFloat("_UseRoughnessMap", 0f);
            m.SetFloat("_UseAoMap", 0f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", 0.35f);
            AssetDatabase.CreateAsset(m, path);
        }

        // ------------------------------------------------------------------ prefab gecisi

        static bool Migrate(Transform root, Transform fbxRoot, Transform oldFbxRoot,
                            Transform femaleFbxRoot)
        {
            var avatar = root.Find("Avatar");
            if (avatar == null) { Debug.LogError("[CharacterSetup] Prefabda 'Avatar' yok."); return false; }

            var prefabSkel = avatar.Find("Skeleton");
            var fbxSkel = fbxRoot.Find("Skeleton");
            if (prefabSkel == null || fbxSkel == null)
            { Debug.LogError("[CharacterSetup] 'Skeleton' bulunamadi (prefab ya da FBX)."); return false; }

            // Once iskelet tamamlanir: aksesuar mesh'leri yeni rigin 100 kemikli dizisini
            // istiyor, prefabda 68 var. Eksikler FBX'ten AYNI YEREL TRS ile kopyalanir —
            // mevcutlara DOKUNULMAZ (IK hedefleri, collider'lar ve SAG ELIN ESKI DURUSU
            // onlara bagli; sag el sorunu icin bkz. sinif notu).
            SyncSkeleton(fbxSkel, prefabSkel);

            // Vucut parcalari ESKI FBX'e baglanir (mesh + kemikler + rootBone, ad yoluyla).
            // Idempotent geri donus: onceki surum govdeyi yanlislikla yeni FBX'e tasidiysa
            // bu calisma onu eski FBX'e dondurur; zaten eskideyse ayni degerleri yazar.
            foreach (var part in BodyParts)
            {
                var dst = avatar.Find(part);
                var src = FindDeep(oldFbxRoot, part);
                var dstR = dst != null ? dst.GetComponent<SkinnedMeshRenderer>() : null;
                var srcR = src != null ? src.GetComponent<SkinnedMeshRenderer>() : null;
                if (dstR == null || srcR == null)
                { Debug.LogError("[CharacterSetup] Parca eksik: " + part); return false; }

                if (!Rebind(dstR, srcR, oldFbxRoot, avatar)) return false;
            }

            // Aksesuar mesh'leri (tekillestirilmis — ayni mesh iki secenekte olabilir).
            var accParts = new Dictionary<string, GameObject>();
            foreach (var acc in Accessories)
            {
                if (accParts.ContainsKey(acc.mesh)) continue;
                var go = Graft(avatar, fbxRoot, acc.mesh, LoadMat(acc.mat));
                if (go == null) return false;
                accParts[acc.mesh] = go;
            }

            // Kadin kafa + sac (varsa). BIND POZLAR YENIDEN HESAPLANIR: paket FBX'leri
            // 1/100 olcek + dondurulmus eksen konvansiyonunda, Blender cikisi metre
            // konvansiyonunda — mesh'in kendi bind pozlari prefab iskeletiyle carpilinca
            // 100 kat buyuk gorunuyordu. Konvansiyon donusturmek yerine bind pozlar
            // dogrudan prefabin REST pozundan turetilir (iskeletin rest'i Blender'daki
            // sahneyle birebir ayni — ayni FBX'ten geliyor).
            if (femaleFbxRoot != null)
            {
                var femMat = AssetDatabase.LoadAssetAtPath<Material>(ModelDir + "M_Female_Kach.mat");
                // BIND POZ YENIDEN KURULUR: FBX'in kendi bind pozu prefab iskeletiyle
                // UYUMSUZ (FBX bake_space_transform ile metre uzayinda cikiyor, prefab
                // iskeleti ise paketin cm konvansiyonunda ~100x olcekli). FBX mesh'i
                // dogrudan baglanirsa parca ekranda hic gorunmez. Bind pozlar prefabin
                // REST pozundan yeniden hesaplaniyor (bkz. Graft).
                var fh = Graft(avatar, femaleFbxRoot, "KachFull", femMat,
                    rebindPoseAsset: ModelDir + "FemaleHead_Bound.asset", dstName: "FemaleHead");
                if (fh == null) return false;

                // Topuzsuz varyant: kask/sapka takiliyken bunun yerine bu acilir.
                var fnb = Graft(avatar, femaleFbxRoot, "KachNoBun", femMat,
                    rebindPoseAsset: ModelDir + "FemaleHeadNoBun_Bound.asset", dstName: "FemaleHeadNoBun");
                if (fnb == null) return false;

                // Onceki surumun ayri sac mesh'i artik gereksiz (sac kadin kafasina dahil).
                var oldHair = avatar.Find("FemaleHair");
                if (oldHair != null) Object.DestroyImmediate(oldHair.gameObject);
            }

            // Animator ESKI FBX'in avatarinda kalir (sag el sorunu — bkz. sinif notu).
            var animator = avatar.GetComponent<Animator>();
            var oldAvatar = LoadAvatar(OldFbxPath);
            if (animator == null || oldAvatar == null)
            { Debug.LogError("[CharacterSetup] Animator ya da eski FBX avatari yok."); return false; }
            animator.avatar = oldAvatar;

            var cust = root.GetComponent<CharacterCustomizer>();
            if (cust == null) cust = root.gameObject.AddComponent<CharacterCustomizer>();
            FillCustomizer(cust, avatar, accParts);

            if (root.GetComponent<PlayerAppearance>() == null)
                root.gameObject.AddComponent<PlayerAppearance>();

            return true;
        }

        /// <summary>Kaynak FBX'teki skinned mesh'i avatarin altina ayni adla aşilar
        /// (yoksa olusturur), kemikleri prefab iskeletine baglar, varsayilan KAPALI birakir.
        /// <paramref name="rebindPoseAsset"/> doluysa mesh kopyalanip bind pozlari prefab
        /// iskeletinin REST pozundan yeniden hesaplanir ve o yola asset olarak yazilir
        /// (farkli olcek/eksen konvansiyonundaki FBX'ler icin — bkz. Migrate'teki not).</summary>
        static GameObject Graft(Transform avatar, Transform srcRoot, string meshName,
                                Material mat, string rebindPoseAsset = null, string dstName = null)
        {
            // Kaynaktaki mesh adi ile PREFABDAKI obje adi ayri olabilir (kadin kafasi:
            // kaynakta "KachFull", prefabda "FemaleHead"). Ayrilmazsa arac ikinci
            // calismada hedefi bulamayip her seferinde yeni bir obje yaratirdi.
            string dst = dstName ?? meshName;
            var t = avatar.Find(dst);
            var src = FindDeep(srcRoot, meshName);
            var srcR = src != null ? src.GetComponent<SkinnedMeshRenderer>() : null;
            if (srcR == null)
            { Debug.LogError("[CharacterSetup] Mesh kaynakta yok: " + meshName); return null; }

            SkinnedMeshRenderer r;
            if (t == null)
            {
                var go = new GameObject(dst);
                t = go.transform;
                t.SetParent(avatar, false);
                r = go.AddComponent<SkinnedMeshRenderer>();
                go.SetActive(false);
            }
            else r = t.GetComponent<SkinnedMeshRenderer>();

            if (!Rebind(r, srcR, srcRoot, avatar)) return null;

            if (rebindPoseAsset != null)
            {
                // bindpose_i = kemik.worldToLocal * meshSahibi.localToWorld (rest pozunda)
                // => deforme sonuc, mesh'in yazarken durdugu yerin birebir aynisi olur.
                var bones = r.bones;
                var bp = new Matrix4x4[bones.Length];
                var ownerL2W = t.localToWorldMatrix;
                for (int i = 0; i < bones.Length; i++)
                    bp[i] = bones[i].worldToLocalMatrix * ownerL2W;

                // Asset ICERIGI guncellenir, asset SILINMEZ: DeleteAsset acik prefab
                // duzenlemesindeki referansi de kopariyor ve parca mesh'siz kaliyordu.
                // (Onceki "vertex stride" hatasinin sebebi silme eksikligi degil, FBX'te
                // Read/Write kapali olmasiydi — bkz. DisableCamerasAndLights.)
                //
                // BILINEN KUSUR: kaynak mesh vertex sayisi/duzeni degistiginde
                // CopySerialized ile uzerine yazilan asset SESSIZCE CIZILMEZ hale
                // gelebiliyor (veri saglikli okunuyor: normaller, agirliklar, bindpose,
                // BakeMesh hepsi dogru — ama hicbir sey render edilmiyor). Blender'da
                // mesh degistirildikten sonra kafa gorunmuyorsa bu asset'leri SILIP
                // araci yeniden calistirmak duzeltir.
                var fresh = Object.Instantiate(srcR.sharedMesh);
                if (fresh.vertexCount == 0)
                {
                    Debug.LogError("[CharacterSetup] Mesh kopyasi bos: " + meshName +
                                   " — FBX'te Read/Write kapali olabilir.");
                    Object.DestroyImmediate(fresh);
                    return null;
                }
                fresh.name = dst + "_Bound";
                fresh.bindposes = bp;

                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(rebindPoseAsset);
                if (mesh == null)
                {
                    mesh = fresh;
                    AssetDatabase.CreateAsset(mesh, rebindPoseAsset);
                }
                else
                {
                    EditorUtility.CopySerialized(fresh, mesh);
                    Object.DestroyImmediate(fresh);
                }
                EditorUtility.SetDirty(mesh);
                // Referans atanmadan ONCE asset diske insin: prefab kaydi sirasinda
                // henuz yazilmamis bir asset'e verilen referans null olarak serilesiyor.
                AssetDatabase.SaveAssets();

                r.sharedMesh = mesh;
                if (r.sharedMesh == null)
                    Debug.LogError("[CharacterSetup] mesh atanamadi: " + dst);
                // Sinirlar rootBone uzayinda ve konvansiyonlar karisik — her kare yeniden
                // hesaplatmak (2 mesh icin) en guvenlisi; FP_Hands ayni yontemi kullaniyor.
                r.updateWhenOffscreen = true;
            }

            if (mat != null) r.sharedMaterial = mat;
            return t.gameObject;
        }

        /// <summary>FBX iskeletinde olup hedefte olmayan kemikleri (ayni ebeveyn altina, ayni
        /// yerel TRS ile) ekler. Ad bazli ozyineleme: kemik adlari iskelet icinde essiz.</summary>
        static void SyncSkeleton(Transform src, Transform dst)
        {
            foreach (Transform sc in src)
            {
                var dc = dst.Find(sc.name);
                if (dc == null)
                {
                    var go = new GameObject(sc.name);
                    dc = go.transform;
                    dc.SetParent(dst, false);
                    dc.localPosition = sc.localPosition;
                    dc.localRotation = sc.localRotation;
                    dc.localScale = sc.localScale;
                }
                SyncSkeleton(sc, dc);
            }
        }

        /// <summary>Mesh'i ve kemik dizisini kaynaktan hedefe, KEMIK ADLARI uzerinden tasir.
        /// Yol kaynak kokune gore hesaplanir ve prefabin Avatar'i altinda aynen aranir — iki
        /// hiyerarsi de "Skeleton/Hips/..." duzeninde.</summary>
        static bool Rebind(SkinnedMeshRenderer dst, SkinnedMeshRenderer src,
                           Transform srcRoot, Transform avatar)
        {
            var srcBones = src.bones;
            var bones = new Transform[srcBones.Length];
            for (int i = 0; i < srcBones.Length; i++)
            {
                string path = PathUnder(srcRoot, srcBones[i]);
                var t = path != null ? avatar.Find(path) : null;
                if (t == null)
                {
                    Debug.LogError("[CharacterSetup] Kemik eslesmedi: " +
                                   (path ?? srcBones[i].name) + " (" + src.name + ")");
                    return false;
                }
                bones[i] = t;
            }

            string rootPath = src.rootBone != null ? PathUnder(srcRoot, src.rootBone) : null;
            var rootBone = rootPath != null ? avatar.Find(rootPath) : null;

            dst.sharedMesh = src.sharedMesh;
            dst.bones = bones;
            if (rootBone != null) dst.rootBone = rootBone;
            dst.localBounds = src.localBounds;
            return true;
        }

        static string PathUnder(Transform root, Transform t)
        {
            if (t == null) return null;
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            if (t == null) return null;   // kok altinda degil
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindDeep(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // ------------------------------------------------------------------ secenekler

        /// <summary>
        /// Customizer'in serilestirilmis listelerini doldurur. Alanlar private [SerializeField]
        /// oldugu icin SerializedObject kullanilir — runtime API'sini kirletmemek icin alanlar
        /// bilerek disari acilmadi.
        /// </summary>
        static void FillCustomizer(CharacterCustomizer cust, Transform avatar,
                                   Dictionary<string, GameObject> accParts)
        {
            var so = new SerializedObject(cust);

            var heads  = avatar.Find("Heads");
            var neck   = avatar.Find("Necck");
            var jacket = avatar.Find("Jaket");
            var pants  = avatar.Find("Pants");
            var femaleHead = avatar.Find("FemaleHead");
            var femaleNoBun = avatar.Find("FemaleHeadNoBun");
            var eye = avatar.Find("Eye");

            SetRef(so, "headRenderer", heads);
            SetRef(so, "neckRenderer", neck);
            SetRef(so, "jacketRenderer", jacket);
            SetRef(so, "pantsRenderer", pants);
            SetRef(so, "eyeRenderer", eye);
            so.FindProperty("femaleHeadPart").objectReferenceValue =
                femaleHead != null ? femaleHead.gameObject : null;
            so.FindProperty("femaleHeadNoBunPart").objectReferenceValue =
                femaleNoBun != null ? femaleNoBun.gameObject : null;

            // Kafa secenekleri: bos ad = mevcut (varsayilan) malzeme. Kadin secenegi kadin
            // FBX'i yoksa atlanir — sayaclar gercek listeye gore olusur, bos secenek kalmaz.
            var defaults = (head: CurrentMat(heads), neck: CurrentMat(neck));
            var headList = new List<(Material mat, Material neckMat, bool female)>();
            foreach (var o in HeadOptions)
            {
                Material m = o.headMat == ""
                    ? defaults.head
                    : (o.female
                        ? AssetDatabase.LoadAssetAtPath<Material>(ModelDir + o.headMat + ".mat")
                        : LoadMat(o.headMat));
                Material nm = o.neckMat == "" ? defaults.neck : LoadMat(o.neckMat);
                if (m == null) { Debug.LogWarning("[CharacterSetup] Kafa malzemesi yok: " + o.headMat); continue; }
                if (o.female && femaleHead == null) continue;
                headList.Add((m, nm, o.female));
            }

            var hp = so.FindProperty("headOptions");
            hp.arraySize = headList.Count;
            for (int i = 0; i < headList.Count; i++)
            {
                var el = hp.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("material").objectReferenceValue = headList[i].mat;
                el.FindPropertyRelative("neckMaterial").objectReferenceValue = headList[i].neckMat;
                el.FindPropertyRelative("female").boolValue = headList[i].female;
            }

            SetMats(so, "jacketMaterials", CurrentMat(jacket), JacketMats);
            SetMats(so, "pantsMaterials", CurrentMat(pants), PantsMats);

            var acc = so.FindProperty("accessories");
            acc.arraySize = Accessories.Length;
            for (int i = 0; i < Accessories.Length; i++)
            {
                var el = acc.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("label").stringValue = Accessories[i].label;
                el.FindPropertyRelative("part").objectReferenceValue =
                    accParts.TryGetValue(Accessories[i].mesh, out var go) ? go : null;
                el.FindPropertyRelative("material").objectReferenceValue = LoadMat(Accessories[i].mat);
                el.FindPropertyRelative("hidesHair").boolValue = Accessories[i].hidesBun;

                // Kadin varyanti: mesh KOPYALANIR ve vertexleri yukari kaydirilir.
                // Parca kemige bagli oldugu icin transform kaydirmak ise yaramaz.
                Mesh maleMesh = null, femaleMesh = null;
                if (accParts.TryGetValue(Accessories[i].mesh, out var accGo))
                {
                    var ar = accGo.GetComponent<SkinnedMeshRenderer>();
                    maleMesh = ar != null ? ar.sharedMesh : null;
                    bool needsVariant = ar != null && maleMesh != null &&
                                        (Accessories[i].femaleLift > 0f ||
                                         !Mathf.Approximately(Accessories[i].femaleScale, 1f));
                    if (needsVariant)
                        femaleMesh = MakeLifted(ar, Accessories[i].mesh,
                                                Accessories[i].femaleLift, Accessories[i].femaleScale);
                }
                el.FindPropertyRelative("maleMesh").objectReferenceValue = maleMesh;
                el.FindPropertyRelative("femaleMesh").objectReferenceValue = femaleMesh;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetRef(SerializedObject so, string field, Transform t) =>
            so.FindProperty(field).objectReferenceValue =
                t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;

        static Material CurrentMat(Transform t)
        {
            var r = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
            return r != null ? r.sharedMaterial : null;
        }

        static void SetMats(SerializedObject so, string field, Material current, string[] names)
        {
            var list = new List<Material>();
            if (current != null) list.Add(current);
            foreach (var n in names)
            {
                var m = LoadMat(n);
                if (m != null) list.Add(m);
                else Debug.LogWarning("[CharacterSetup] Malzeme yok, atlandi: " + n);
            }

            var prop = so.FindProperty(field);
            prop.arraySize = list.Count;
            for (int i = 0; i < list.Count; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = list[i];
        }

        /// <summary>
        /// Mesh'in KOPYASINI dunya-Y ekseninde <paramref name="lift"/> metre yukari kaydirip
        /// asset olarak kaydeder (kadin aksesuar varyanti).
        ///
        /// Kaydirma BIND POZA islenir, vertexlere degil: vertexler kemik uzayinda duruyor ve
        /// her kemigin ekseni farkli — hepsini dunya Y'sinde kaydirmak mesh'i carpitirdi.
        /// Bind poz matrisine dunya oteleme eklemek parcayi butun halinde tasir.
        /// </summary>
        static Mesh MakeLifted(SkinnedMeshRenderer r, string meshName, float lift, float scale)
        {
            var src = r.sharedMesh;
            string path = ModelDir + meshName + "_FemaleLift.asset";
            var copy = Object.Instantiate(src);
            copy.name = meshName + "_FemaleLift";

            // Deforme: world = kemik.localToWorld * bindpose * v
            // Istenen : world + (0,lift,0) = T * kemik.localToWorld * bindpose * v
            // Dolayisiyla: bindpose' = kemik.worldToLocal * T * kemik.localToWorld * bindpose
            //
            // ORIJINAL BIND POZ KORUNUR, uzerine dunya otelemesi eklenir. Bind pozu
            // sifirdan kurmak (kadin kafasinda yapildigi gibi) burada YANLIS olurdu:
            // aksesuar mesh'lerinin vertexleri kendi FBX uzayinda duruyor, prefabin
            // obje uzayinda degil — parca origin'e ucuyordu.
            var bones = r.bones;
            var bp = src.bindposes;
            var lifted = new Matrix4x4[bp.Length];
            // Olcek parcanin KENDI merkezi etrafinda uygulanir (dunya orijininde degil),
            // yoksa buyuyen sapka ayni anda yukari da firlardi.
            var pivot = r.bounds.center;
            var T = Matrix4x4.Translate(new Vector3(0f, lift, 0f)) *
                    Matrix4x4.Translate(pivot) *
                    Matrix4x4.Scale(new Vector3(scale, scale, scale)) *
                    Matrix4x4.Translate(-pivot);
            for (int i = 0; i < bp.Length; i++)
            {
                var b = bones[i];
                lifted[i] = b != null
                    ? b.worldToLocalMatrix * T * b.localToWorldMatrix * bp[i]
                    : bp[i];
            }
            copy.bindposes = lifted;

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, path);
                AssetDatabase.SaveAssets();
                return copy;
            }
            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            return existing;
        }

        static Material LoadMat(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>(MatDir + name + ".mat");

        static Avatar LoadAvatar(string fbxPath)
        {
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                if (a is Avatar av) return av;
            return null;
        }

        // ------------------------------------------------------------------ manken

        /// <summary>
        /// Secim ekrani mankeni: NetworkPlayer'daki Avatar'in ag/IK scriptlerinden arindirilmis
        /// kopyasi. Resources'a kaydedilir cunku secim ekrani KODLA kurulan bir UI — sahnede
        /// ya da baska prefabda referans tutacak bir yer yok (bkz. CharacterSelectUI).
        /// </summary>
        static void BuildMannequin()
        {
            // ZORLA YENIDEN IMPORT: SaveAsPrefabAsset'ten hemen sonra LoadAssetAtPath
            // ONBELLEKTEKI ESKI surumu dondurebiliyor. Manken o eski kopyadan uretilince
            // kadin kafasi mesh'siz (gorunmez) kaliyordu — prefab dogruyken manken bozuk.
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);

            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var temp = Object.Instantiate(playerPrefab);
            try
            {
                var avatar = temp.transform.Find("Avatar");
                if (avatar == null) { Debug.LogError("[CharacterSetup] Manken: Avatar yok."); return; }

                var clone = Object.Instantiate(avatar.gameObject);
                clone.name = "CharacterMannequin";
                clone.SetActive(true);   // prefabda uzak-avatar kapali baslar; manken hep acik

                // Ise yaramayan alt agaclar: isim etiketi ve IK kurulumu. IK, gercek el/kafa
                // takibi olmadan mankeni T-poz kavgasina sokar; Animator'in IdleController'i
                // tek basina yeter.
                foreach (var childName in new[] { "NameTag", "UpperBodyRig", "IKTargets" })
                {
                    var c = clone.transform.Find(childName);
                    if (c != null) Object.DestroyImmediate(c.gameObject);
                }

                // Kalan TUM scriptler gider (RigBuilder, AvatarIKController...). Animator
                // MonoBehaviour degildir, kalir. TEK GECIS YETMEZ: RequireComponent zinciri
                // (or. AvatarFitDebug -> AvatarIKController) bagimli silinene kadar temel
                // bileseni kilitler — kalan sayisi dusmeyi birakana dek tekrarlanir.
                var left = clone.GetComponentsInChildren<MonoBehaviour>(true);
                while (left.Length > 0)
                {
                    int before = left.Length;
                    foreach (var mb in left)
                        if (mb != null) Object.DestroyImmediate(mb);
                    left = clone.GetComponentsInChildren<MonoBehaviour>(true);
                    if (left.Length >= before) break;   // dusmuyor: dongusel bagimlilik
                }
                foreach (var mb in left)
                    Debug.LogWarning("[CharacterSetup] Manken: script kaldirilamadi: " +
                                     mb.GetType().Name);

                var cust = clone.AddComponent<CharacterCustomizer>();
                var accParts = new Dictionary<string, GameObject>();
                foreach (var acc in Accessories)
                {
                    if (accParts.ContainsKey(acc.mesh)) continue;
                    var t = clone.transform.Find(acc.mesh);
                    if (t != null) accParts[acc.mesh] = t.gameObject;
                }
                FillCustomizer(cust, clone.transform, accParts);

                if (!AssetDatabase.IsValidFolder(MannequinDir))
                    AssetDatabase.CreateFolder("Assets", "Resources");
                PrefabUtility.SaveAsPrefabAsset(clone, MannequinPath);
                Object.DestroyImmediate(clone);
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }
        }
    }
}
