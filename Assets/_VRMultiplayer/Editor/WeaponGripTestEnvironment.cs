using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah tutus TEST ortami — VR gozluk takmadan, edit mode'da.
    ///
    /// Neden var: tutuslar bugune kadar yalnizca gozlukte gorulebiliyordu; bir kemik/poz
    /// hatasini fark etmek icin build alip takmak gerekiyordu. Bu pencere ayni tutusu
    /// editorde kurar: manken + silah, WeaponHandWeld'in runtime matematiginin BIREBIR
    /// aynisiyla (ayni ayna kurali, ayni cipa formulu, ayni ray kaymasi) yerlestirilir.
    /// Gordugun poz, gozlukte gorecegin pozdur.
    ///
    /// UC DURUS: Serbest (bilek T-pozunda, silah bilege gelir — poz detayi icin),
    /// Tasima (silah gogus onunde), Nisan (namlu goz hizasinda ileriye hizali — iki elli
    /// nisanin durgun hali). Son ikisinde kollar iki-kemik IK ile silaha goturulur; boylece
    /// "silah kaldirilinca kafaya/omuza gore nerede duruyor" sorusu gozluk takmadan yanitlanir.
    ///
    /// Isbolumu: bu arac BAKMAK ve TARAMAK icin; poz DUZELTMEK icin 31. Parmak Pozu araci
    /// (WeaponHandPoseTool). "31'de ac" dugmesi profili oraya tasir; test silahi sahnede
    /// kaldigi icin aracin otomatik bulusu onu yakalar, akis kesintisiz surer.
    ///
    /// Kemik denetimi tum profilleri tarar: rig'de eksik parmak kemigi, yanlis uzunlukta
    /// eklem dizisi, bozuk (normalize olmayan / NaN) quaternion, sise bilek ofseti, sifir
    /// boyunda destek rayi, prefab eslesmeyen profil... Normalize sorunlari tek tusla
    /// duzeltilebilir; gerisi hangi asset'in hangi alaninda oldugu soylenerek raporlanir.
    ///
    /// Test objeleri DontSave isaretli: sahne kaydedilse bile dosyaya girmezler. "~" oneki
    /// diger gecici objelerle ayni sozlesme.
    /// </summary>
    public class WeaponGripTestEnvironment : EditorWindow
    {
        const string RootName = "~SilahTutusTest";
        const string MannequinPath = "Assets/Resources/CharacterMannequin.prefab";
        const string WeaponPrefabFolder = "Assets/_VRMultiplayer/Resources/WeaponPrefabs";
        static readonly Vector3 RigOrigin = new Vector3(500f, 0f, 500f);

        // [SerializeField]: domain reload'da (her derlemede) secimler ucmasin.
        [SerializeField] Animator _avatarOverride;
        [SerializeField] int _profileIndex;
        [SerializeField] bool _mainLeft;          // ana el sol mu (varsayilan sag)
        [SerializeField] bool _showSupport = true;
        [SerializeField] float _trigger;
        [SerializeField] bool _approxCurl = true; // poz yoksa kaba kivrimla goster

        /// <summary>Silahin govdeye gore nerede durdugu. Serbest: bilek T-pozunda kalir,
        /// silah bilege oturur (eski davranis; poz detayina yakindan bakmak icin).
        /// Tasima/Nisan: silah govdeye gore oyundaki gibi konumlanir, KOLLAR iki-kemik
        /// IK ile silaha goturulur — gozlukteki gorunumun editordeki karsiligi.</summary>
        enum GripTestMode { Serbest, Tasima, Nisan }
        [SerializeField] GripTestMode _mode = GripTestMode.Serbest;
        [SerializeField] float _aimDistance = 0.34f;  // goz -> kabza cipasi ileri mesafe (m)
        [SerializeField] float _aimDrop = 0.07f;      // nisan hattinin gozun altina dusmesi (m)

        List<WeaponGripProfile> _profiles = new List<WeaponGripProfile>();
        List<GameObject> _weaponPrefabs = new List<GameObject>();   // _profiles ile ayni indeks
        Vector2 _scroll;
        string _status = "";

        struct Issue
        {
            public MessageType type;
            public string msg;
            public Object target;      // ping icin
            public bool normalizable;  // tek tusla duzeltilebilir mi
        }
        readonly List<Issue> _issues = new List<Issue>();
        bool _auditRan;

        [MenuItem("Tools/VR Multiplayer/57. Silah Tutus Testi")]
        static void Open() => GetWindow<WeaponGripTestEnvironment>("Silah Tutus Testi");

        void OnEnable()
        {
            RefreshProfiles();
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

        // ---------------------------------------------------------------- veri toplama

        void RefreshProfiles()
        {
            _profiles.Clear();
            _weaponPrefabs.Clear();

            foreach (string guid in AssetDatabase.FindAssets("t:WeaponGripProfile"))
            {
                var p = AssetDatabase.LoadAssetAtPath<WeaponGripProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null) _profiles.Add(p);
            }
            _profiles.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));

            // Her profile en iyi eslesen calisma-zamani silah prefab'i. Eslesme kurali
            // runtime binder'la ayni: profil.MatchScore(prefab adi), yuksek olan kazanir.
            var prefabs = new List<GameObject>();
            if (AssetDatabase.IsValidFolder(WeaponPrefabFolder))
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { WeaponPrefabFolder }))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (go != null) prefabs.Add(go);
                }

            foreach (var p in _profiles)
            {
                GameObject best = null; int bestScore = 0;
                foreach (var w in prefabs)
                {
                    int s = p.MatchScore(w.name);
                    if (s > bestScore) { bestScore = s; best = w; }
                }
                _weaponPrefabs.Add(best);
            }
        }

        // ---------------------------------------------------------------- sahne kurulumu

        Transform Root => GameObject.Find(RootName)?.transform;

        Animator RigAvatar()
        {
            if (_avatarOverride != null && _avatarOverride.isHuman) return _avatarOverride;
            var root = Root;
            return root != null ? root.GetComponentInChildren<Animator>(true) : null;
        }

        Transform RigWeapon()
        {
            var root = Root;
            if (root == null) return null;
            // Silah = icinde HIC Animator olmayan cocuk. Mankenin Animator'u kokte degil alt
            // objede de olabilir, o yuzden GetComponent yerine GetComponentInChildren.
            foreach (Transform c in root)
                if (c.GetComponentInChildren<Animator>(true) == null) return c;
            return null;
        }

        void BuildRig()
        {
            TearDown();

            var rootGo = new GameObject(RootName);
            rootGo.transform.position = RigOrigin;

            // Manken: prefab INSTANCE'i olarak — rest pozlari prefab kaynagindan okunabilsin
            // (T-pose'a donus ve tekrarlanabilir poz icin sart; bkz. WeaponHandPoseTool).
            var mannequinAsset = AssetDatabase.LoadAssetAtPath<GameObject>(MannequinPath);
            if (mannequinAsset == null) { _status = "Manken prefab'i yok: " + MannequinPath; return; }
            var mannequin = (GameObject)PrefabUtility.InstantiatePrefab(mannequinAsset);
            mannequin.transform.SetParent(rootGo.transform, false);

            var anim = mannequin.GetComponentInChildren<Animator>(true);
            if (anim == null || !anim.isHuman)
            {
                _status = "Manken humanoid degil — 'Avatar (opsiyonel)' alanina sahneden humanoid bir Animator ver.";
            }

            SpawnWeapon();
            SetDontSaveRecursive(rootGo.transform);
            _status = "Test ortami kuruldu. Silahlar arasinda gezin, tetigi surukle.";

            // Kamerayi rige getir
            var view = SceneView.lastActiveSceneView;
            if (view != null) view.LookAt(RigOrigin + Vector3.up * 1.3f, Quaternion.Euler(15f, 200f, 0f), 2.2f);
        }

        void SpawnWeapon()
        {
            var root = Root;
            if (root == null) return;

            var old = RigWeapon();
            if (old != null) DestroyImmediate(old.gameObject);

            var prefab = CurrentWeaponPrefab();
            if (prefab == null) { _status = "Bu profile eslesen silah prefab'i yok (" + WeaponPrefabFolder + ")."; return; }

            var w = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            w.transform.SetParent(root, false);
            SetDontSaveRecursive(w.transform);
            ApplyPose();
        }

        void TearDown()
        {
            var root = Root;
            if (root != null) DestroyImmediate(root.gameObject);
        }

        static void SetDontSaveRecursive(Transform t)
        {
            t.gameObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            foreach (Transform c in t) SetDontSaveRecursive(c);
        }

        WeaponGripProfile CurrentProfile() =>
            _profiles.Count == 0 ? null : _profiles[Mathf.Clamp(_profileIndex, 0, _profiles.Count - 1)];
        GameObject CurrentWeaponPrefab() =>
            _weaponPrefabs.Count == 0 ? null : _weaponPrefabs[Mathf.Clamp(_profileIndex, 0, _weaponPrefabs.Count - 1)];

        // ---------------------------------------------------------------- poz uygulama
        //
        // WeaponHandWeld.WeldSide'in formulleri, edit mode'a cevrilmis hali:
        //  - ANA EL: bilek yerinde durur, SILAH bilege oturtulur (ayni bagil poz, ters cozum —
        //    31. aracin AlignWeapon'iyla ayni).
        //  - DESTEK ELI: silah artik sabit; bilek raya kaynaklanir (weld'in yaptigi gibi bilek
        //    dunya-uzayinda yazilir). Kol IK'siz gerilebilir — bakilan sey EL, kol degil.
        // Yazim kurali (ana=SAG / destek=SOL) disina cikan her kombinasyon MirrorX ile
        // aynalanir; kural weld ve 31. aracla BIREBIR ayni, yoksa test yalan soyler.

        void ApplyPose()
        {
            var profile = CurrentProfile();
            var anim = RigAvatar();
            var weapon = RigWeapon();
            if (profile == null || anim == null || !anim.isHuman || weapon == null) return;

            ResetHandsToRest(anim);
            ResetArmsToRest(anim);

            bool mainLeft = _mainLeft;
            var mainBone = anim.GetBoneTransform(mainLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (mainBone == null) { _status = "Bilek kemigi yok — rig humanoid mi?"; return; }

            if (_mode == GripTestMode.Serbest)
            {
                // --- SILAH bilege oturur (bilek T-pozunda kalir) — 31. aracin AlignWeapon'i
                var hp = profile.mainHand;
                bool mMir = mainLeft; // ana el icin: support(false) != left  =>  mirrored = left
                Vector3 anchorLocal = mMir ? WeaponGripMath.MirrorX(profile.gripLocalPosition) : profile.gripLocalPosition;
                Quaternion anchorLocalRot = mMir ? WeaponGripMath.MirrorX(profile.GripLocalRotation) : profile.GripLocalRotation;
                Vector3 wristPos = mMir ? WeaponGripMath.MirrorX(hp.wristLocalPosition) : hp.wristLocalPosition;
                Quaternion wristRot = mMir ? WeaponGripMath.MirrorX(Quaternion.Euler(hp.wristLocalEuler)) : Quaternion.Euler(hp.wristLocalEuler);

                Quaternion anchorRot = mainBone.rotation * Quaternion.Inverse(wristRot);
                Quaternion weaponRot = anchorRot * Quaternion.Inverse(anchorLocalRot);
                Vector3 anchorPos = mainBone.position - anchorRot * wristPos;
                Vector3 weaponPos = anchorPos - weaponRot * Vector3.Scale(weapon.lossyScale, anchorLocal);
                weapon.SetPositionAndRotation(weaponPos, weaponRot);
            }
            else
            {
                // --- SILAH govdeye gore konumlanir, ANA EL silaha goturulur (runtime weld yonu)
                PlaceWeaponForMode(anim, weapon, profile, mainLeft);
                WristWeldTarget(profile, weapon, false, mainLeft, mainBone.position,
                    out Vector3 tp, out Quaternion tr);
                ArmIK(anim, mainLeft, tp);
                mainBone.SetPositionAndRotation(tp, tr);
            }

            PoseFingers(anim, mainLeft, profile.mainHand, _trigger);

            // --- destek eli: bilek raya kaynaklanir (weld'in LateUpdate formulu)
            if (_showSupport && profile.supportRailLocalStart != profile.supportRailLocalEnd)
            {
                bool supLeft = !mainLeft;
                var supBone = anim.GetBoneTransform(supLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                if (supBone != null)
                {
                    // Ray uzerindeki nokta: Serbest'te bilegin durdugu yere en yakin nokta
                    // (weld'in davranisi). Tasima/Nisan'da bilek henuz T-pozunda oldugu icin
                    // o olcum yaniltir — omuzdan ileri bir nokta kullanilir ki el raya
                    // oyundaki gibi ONDEN tutunsun.
                    Vector3 probe = supBone.position;
                    if (_mode != GripTestMode.Serbest)
                    {
                        var shoulder = anim.GetBoneTransform(supLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                        Vector3 basis = shoulder != null ? shoulder.position : supBone.position;
                        probe = basis + anim.transform.forward * 0.45f;
                    }

                    WristWeldTarget(profile, weapon, true, supLeft, probe,
                        out Vector3 sp2, out Quaternion sr2);
                    if (_mode != GripTestMode.Serbest) ArmIK(anim, supLeft, sp2);
                    supBone.SetPositionAndRotation(sp2, sr2);

                    PoseFingers(anim, supLeft, profile.supportHand, 0f);
                }
            }

            SceneView.RepaintAll();
        }

        /// <summary>Runtime weld'in hedefi: bilegin DUNYA pozu. WeaponHandWeld.WeldSide ile
        /// birebir ayni formul — ayni ayna kurali (mirrored = isSupport != left), destek icin
        /// ayni ray-en-yakin-nokta kaymasi (probe = weld'deki carrier karsiligi).</summary>
        void WristWeldTarget(WeaponGripProfile profile, Transform weapon, bool isSupport, bool left,
            Vector3 probeWorld, out Vector3 pos, out Quaternion rot)
        {
            bool mir = isSupport != left;
            var hp = isSupport ? profile.supportHand : profile.mainHand;

            Quaternion anchorLocalRot = mir ? WeaponGripMath.MirrorX(profile.GripLocalRotation) : profile.GripLocalRotation;
            Vector3 wristPos = mir ? WeaponGripMath.MirrorX(hp.wristLocalPosition) : hp.wristLocalPosition;
            Quaternion wristRot = mir ? WeaponGripMath.MirrorX(Quaternion.Euler(hp.wristLocalEuler)) : Quaternion.Euler(hp.wristLocalEuler);

            Vector3 anchorLocal;
            if (!isSupport)
            {
                anchorLocal = mir ? WeaponGripMath.MirrorX(profile.gripLocalPosition) : profile.gripLocalPosition;
            }
            else
            {
                Vector3 rs = mir ? WeaponGripMath.MirrorX(profile.supportRailLocalStart) : profile.supportRailLocalStart;
                Vector3 re = mir ? WeaponGripMath.MirrorX(profile.supportRailLocalEnd) : profile.supportRailLocalEnd;
                float t = WeaponGripMath.RailClosestT(weapon.TransformPoint(rs), weapon.TransformPoint(re), probeWorld);
                anchorLocal = Vector3.Lerp(rs, re, t);
            }

            Vector3 anchorPos = weapon.TransformPoint(anchorLocal);
            Quaternion anchorRot = weapon.rotation * anchorLocalRot;
            pos = anchorPos + anchorRot * wristPos;
            rot = anchorRot * wristRot;
        }

        /// <summary>Silahi govdeye gore oyundaki durusa koyar.
        ///
        /// NISAN: namlu ekseni (barrelLocalDirection) avatarin ilerisine hizalanir; kabza
        /// cipasi gozun _aimDistance ilerisine, nisan hatti goze binmesin diye _aimDrop kadar
        /// altina gelir — iki elli nisanin "namluyu hedefe hizala" kuralinin durgun hali.
        ///
        /// TASIMA: ayni hizalama, gogus onunde ve 18 derece asagi egik; silah ana el
        /// tarafina hafif kayar. Degerler goz karari — amac milimetrik gercekcilik degil,
        /// tutusun govde/kafa iliskisini gozluk takmadan gorebilmek.</summary>
        void PlaceWeaponForMode(Animator anim, Transform weapon, WeaponGripProfile profile, bool mainLeft)
        {
            Vector3 up = Vector3.up;
            Vector3 fwd = anim.transform.forward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude < 1e-6f ? Vector3.forward : fwd.normalized;
            Vector3 side = Vector3.Cross(up, fwd).normalized * (mainLeft ? 1f : -1f); // ana el tarafi

            Vector3 barrelLocal = profile.barrelLocalDirection.sqrMagnitude < 1e-6f
                ? Vector3.forward : profile.barrelLocalDirection.normalized;
            Quaternion barrelFix = Quaternion.FromToRotation(barrelLocal, Vector3.forward);

            bool mMir = mainLeft;
            Vector3 gripLocal = mMir ? WeaponGripMath.MirrorX(profile.gripLocalPosition) : profile.gripLocalPosition;

            Quaternion weaponRot;
            Vector3 anchorTarget;

            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            Vector3 eye = (head != null ? head.position : anim.transform.position + up * 1.6f)
                          + up * 0.07f + fwd * 0.08f;

            if (_mode == GripTestMode.Nisan)
            {
                weaponRot = Quaternion.LookRotation(fwd, up) * barrelFix;
                anchorTarget = eye + fwd * _aimDistance - up * _aimDrop;
            }
            else // Tasima
            {
                Quaternion tilt = Quaternion.AngleAxis(18f, Vector3.Cross(up, fwd));
                weaponRot = Quaternion.LookRotation(tilt * fwd, up) * barrelFix;
                var chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = anim.GetBoneTransform(HumanBodyBones.Spine);
                Vector3 govde = chest != null ? chest.position : eye - up * 0.35f;
                anchorTarget = govde + fwd * 0.30f + side * 0.10f - up * 0.06f;
            }

            Vector3 weaponPos = anchorTarget - weaponRot * Vector3.Scale(weapon.lossyScale, gripLocal);
            weapon.SetPositionAndRotation(weaponPos, weaponRot);
        }

        /// <summary>Iki-kemik kol IK'sinin editor karsiligi — YAKLASIKTIR. Once dirsek asagi/
        /// disari bakacak sekilde omuz burulur (dirsek ipucu), sonra iki gecis CCD bilegi
        /// hedefe tasir. Runtime'daki kol IK'siyla birebir ayni degil; amac elin silah
        /// uzerindeki yerlesimini dogal bir kol durusuyla gorebilmek. Bilek pozunu cagiran
        /// yazar (IK yalnizca omuz+dirsegi ayarlar).</summary>
        static void ArmIK(Animator anim, bool left, Vector3 target)
        {
            var upper = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var lower = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var hand  = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (upper == null || lower == null || hand == null) return;

            // Dirsek ipucu: asagi + hafif geri + kendi tarafina disari.
            Vector3 hint = -Vector3.up
                         - anim.transform.forward * 0.25f
                         + anim.transform.right * (left ? -0.55f : 0.55f);

            for (int iter = 0; iter < 3; iter++)
            {
                Vector3 toWrist = hand.position - upper.position;
                Vector3 toTarget = target - upper.position;
                if (toWrist.sqrMagnitude > 1e-8f && toTarget.sqrMagnitude > 1e-8f)
                    upper.rotation = Quaternion.FromToRotation(toWrist, toTarget) * upper.rotation;

                if (iter == 0 && toTarget.sqrMagnitude > 1e-8f)
                {
                    Vector3 axis = toTarget.normalized;
                    Vector3 elbowDir = Vector3.ProjectOnPlane(lower.position - upper.position, axis);
                    Vector3 hintDir = Vector3.ProjectOnPlane(hint, axis);
                    if (elbowDir.sqrMagnitude > 1e-8f && hintDir.sqrMagnitude > 1e-8f)
                        upper.rotation = Quaternion.AngleAxis(
                            Vector3.SignedAngle(elbowDir, hintDir, axis), axis) * upper.rotation;
                }

                Vector3 toWrist2 = hand.position - lower.position;
                Vector3 toTarget2 = target - lower.position;
                if (toWrist2.sqrMagnitude > 1e-8f && toTarget2.sqrMagnitude > 1e-8f)
                    lower.rotation = Quaternion.FromToRotation(toWrist2, toTarget2) * lower.rotation;
            }
        }

        static void ResetArmsToRest(Animator anim)
        {
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 1;
                RestoreFromPrefab(anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm), false);
                RestoreFromPrefab(anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm), false);
                RestoreFromPrefab(anim.GetBoneTransform(left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder), false);
            }
        }

        /// <summary>Runtime'daki ApplyAuthored'in tek-karelik hali: authored poz varsa dogrudan
        /// yazilir, tetik ekseni isaret parmagini birakili->cekili arasinda karistirir.
        /// Poz yoksa (istege bagli) 31. aracin kaba kivrim geometrisiyle YAKLASIK gosterim.</summary>
        void PoseFingers(Animator anim, bool left, WeaponGripProfile.HandPose hp, float trigger)
        {
            var fp = hp.Fingers(left);
            if (fp.HasPose)
            {
                bool pulled = hp.indexFollowsTrigger && fp.HasIndexPulled;
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                {
                    var t = anim.GetBoneTransform(HandPoseBones.Bone(j, left));
                    if (t == null) continue;
                    Quaternion target = fp.joints[j];
                    if (pulled && HandPoseBones.IsIndex(j))
                        target = Quaternion.Slerp(target, fp.indexPulled[j - HandPoseBones.IndexFirst], trigger);
                    t.localRotation = target;
                }
            }
            else if (_approxCurl)
            {
                ApproxCurl(anim, left);
            }
        }

        void ResetHandsToRest(Animator anim)
        {
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 1;
                var wrist = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                RestoreFromPrefab(wrist, true);
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                    RestoreFromPrefab(anim.GetBoneTransform(HandPoseBones.Bone(j, left)), false);
            }
        }

        static void RestoreFromPrefab(Transform bone, bool alsoPosition)
        {
            if (bone == null) return;
            var src = PrefabUtility.GetCorrespondingObjectFromSource(bone);
            if (src == null) return;
            bone.localRotation = src.localRotation;
            if (alsoPosition) bone.localPosition = src.localPosition;
        }

        /// <summary>31. aracin SeedCurl geometrisi (seed=1, Undo'suz): menteşe eksenleri temiz
        /// T-pose'dan olculur, her bogum kendi duzleminde katlanir. YALNIZCA yaklasik gosterim —
        /// runtime'daki prosedurel curl'le birebir ayni degil, "asagi yukari boyle duruyor" der.</summary>
        static void ApproxCurl(Animator anim, bool left)
        {
            const float ProximalCurl = 55f, IntermediateCurl = 80f, DistalCurl = 55f, ThumbCurl = 40f;
            Transform Bone(int j) => anim.GetBoneTransform(HandPoseBones.Bone(j, left));

            Transform wrist = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform idxP = Bone(3), midP = Bone(6), litP = Bone(12);
            if (wrist == null || idxP == null || midP == null || litP == null) return;

            Vector3 fingersDir = (midP.position - wrist.position).normalized;
            Vector3 sideDir = (idxP.position - litP.position).normalized;
            Vector3 palmNormal = Vector3.Cross(fingersDir, sideDir).normalized;
            Vector3 curlPlane = left ? -palmNormal : palmNormal;
            Vector3 thumbTarget = (idxP.position + midP.position) * 0.5f;

            for (int f = 0; f < 5; f++)
            {
                Vector3 prevExt = Vector3.zero;
                for (int j = 0; j < 3; j++)
                {
                    int joint = f * 3 + j;
                    Transform b = Bone(joint);
                    if (b == null || b.parent == null) continue;
                    Transform next = j < 2 ? Bone(joint + 1) : null;

                    Vector3 ext = next != null ? (next.position - b.position) : prevExt;
                    if (ext.sqrMagnitude < 1e-8f) continue;
                    prevExt = ext;

                    Vector3 hinge = f == 0
                        ? Vector3.Cross(ext.normalized, (thumbTarget - b.position).normalized)
                        : Vector3.Cross(ext.normalized, curlPlane);
                    if (hinge.sqrMagnitude < 1e-8f) continue;

                    float deg = f == 0
                        ? (j == 2 ? ThumbCurl * 0.8f : ThumbCurl)
                        : (j == 0 ? ProximalCurl : j == 1 ? IntermediateCurl : DistalCurl);

                    Vector3 axisParent = b.parent.InverseTransformDirection(hinge.normalized).normalized;
                    b.localRotation = Quaternion.AngleAxis(deg, axisParent) * b.localRotation;
                }
            }
        }

        // ---------------------------------------------------------------- kemik denetimi

        void RunAudit()
        {
            _issues.Clear();
            _auditRan = true;

            // 1) RIG: manken uzerinde eksik parmak kemigi var mi
            var anim = RigAvatar();
            if (anim == null || !anim.isHuman)
                _issues.Add(new Issue { type = MessageType.Warning, msg = "Rig denetimi atlandi: sahnede humanoid manken yok (once ortami kur)." });
            else
                for (int side = 0; side < 2; side++)
                {
                    bool left = side == 1;
                    string el = left ? "SOL" : "SAG";
                    if (anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand) == null)
                        _issues.Add(new Issue { type = MessageType.Error, target = anim, msg = "RIG: " + el + " bilek kemigi map'li degil (Avatar Configure)." });
                    for (int j = 0; j < HandPoseBones.JointCount; j++)
                        if (anim.GetBoneTransform(HandPoseBones.Bone(j, left)) == null)
                            _issues.Add(new Issue { type = MessageType.Error, target = anim, msg = "RIG: " + el + " el, \"" + HandPoseBones.JointNames[j] + "\" kemigi map'li degil — poz bu ekleme yazilamaz." });
                }

            // 2) PROFILLER
            for (int i = 0; i < _profiles.Count; i++)
            {
                var p = _profiles[i];

                if (string.IsNullOrEmpty(p.weaponNameEquals) && string.IsNullOrEmpty(p.weaponNameContains))
                    _issues.Add(new Issue { type = MessageType.Error, target = p, msg = p.name + ": Equals ve Contains ikisi de bos — profil HICBIR silahla eslesmez." });
                else if (string.IsNullOrEmpty(p.weaponNameContains))
                    _issues.Add(new Issue { type = MessageType.Warning, target = p, msg = p.name + ": Contains bos — sahnedeki kopyalar (\"... (1)\") eslesmez, sessizce pivot-snap'e duser." });

                if (_weaponPrefabs[i] == null)
                    _issues.Add(new Issue { type = MessageType.Warning, target = p, msg = p.name + ": " + WeaponPrefabFolder + " altinda eslesen silah prefab'i yok — test edilemiyor." });

                AuditHand(p, p.mainHand, "ana el");
                AuditHand(p, p.supportHand, "destek eli");

                bool railDegenerate = p.supportRailLocalStart == p.supportRailLocalEnd;
                bool supportAuthored = p.supportHand.Fingers(true).HasPose || p.supportHand.Fingers(false).HasPose;
                if (railDegenerate && supportAuthored)
                    _issues.Add(new Issue { type = MessageType.Warning, target = p, msg = p.name + ": destek eli pozu VAR ama ray sifir boyda — destek eli hicbir zaman tutunamaz." });

                if (p.barrelLocalDirection.sqrMagnitude < 1e-6f)
                    _issues.Add(new Issue { type = MessageType.Error, target = p, msg = p.name + ": barrelLocalDirection sifir — iki elli nisan hizalamasi patlar." });
            }

            // 3) Ayni silaha birden fazla profil
            for (int i = 0; i < _profiles.Count; i++)
                for (int k = i + 1; k < _profiles.Count; k++)
                    if (_weaponPrefabs[i] != null && _weaponPrefabs[i] == _weaponPrefabs[k])
                        _issues.Add(new Issue { type = MessageType.Warning, target = _profiles[k], msg = _profiles[i].name + " ve " + _profiles[k].name + " AYNI prefab'la eslesiyor (" + _weaponPrefabs[i].name + ") — hangisinin kazanacagi skora bagli." });

            _status = _issues.Count == 0 ? "Denetim temiz: sorun bulunamadi." : _issues.Count + " bulgu.";
        }

        void AuditHand(WeaponGripProfile p, WeaponGripProfile.HandPose hp, string rol)
        {
            if (hp.wristLocalPosition.magnitude > 0.25f)
                _issues.Add(new Issue { type = MessageType.Warning, target = p, msg = p.name + " / " + rol + ": bilek ofseti " + hp.wristLocalPosition.magnitude.ToString("0.00") + " m — el boyu icin cok buyuk, yakalama bozuk olabilir." });

            for (int side = 0; side < 2; side++)
            {
                bool left = side == 1;
                var fp = hp.Fingers(left);
                string slot = p.name + " / " + rol + " / " + (left ? "sol" : "sag");

                if (fp.joints != null && fp.joints.Length != 0 && fp.joints.Length != HandPoseBones.JointCount)
                    _issues.Add(new Issue { type = MessageType.Error, target = p, msg = slot + ": joints uzunlugu " + fp.joints.Length + " (0 ya da " + HandPoseBones.JointCount + " olmali) — poz HIC uygulanmaz." });
                if (fp.indexPulled != null && fp.indexPulled.Length != 0 && fp.indexPulled.Length != HandPoseBones.IndexJointCount)
                    _issues.Add(new Issue { type = MessageType.Error, target = p, msg = slot + ": indexPulled uzunlugu " + fp.indexPulled.Length + " (0 ya da " + HandPoseBones.IndexJointCount + " olmali)." });

                CountBadQuats(fp.joints, p, slot + " joints");
                CountBadQuats(fp.indexPulled, p, slot + " indexPulled");
            }
        }

        void CountBadQuats(Quaternion[] qs, WeaponGripProfile p, string alan)
        {
            if (qs == null) return;
            int denorm = 0, nan = 0;
            foreach (var q in qs)
            {
                if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) { nan++; continue; }
                float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (m < 0.99f || m > 1.01f) denorm++;
            }
            if (nan > 0)
                _issues.Add(new Issue { type = MessageType.Error, target = p, msg = alan + ": " + nan + " eklem NaN — poz elle yeniden yazilmali (31. arac)." });
            if (denorm > 0)
                _issues.Add(new Issue { type = MessageType.Warning, target = p, msg = alan + ": " + denorm + " eklem normalize degil (sifir dahil) — tek tusla duzeltilebilir.", normalizable = true });
        }

        void FixNormalizableQuats()
        {
            int fixedCount = 0;
            foreach (var p in _profiles)
            {
                bool touched = false;
                touched |= NormalizeArray(p.mainHand.leftFingers.joints, ref fixedCount);
                touched |= NormalizeArray(p.mainHand.leftFingers.indexPulled, ref fixedCount);
                touched |= NormalizeArray(p.mainHand.rightFingers.joints, ref fixedCount);
                touched |= NormalizeArray(p.mainHand.rightFingers.indexPulled, ref fixedCount);
                touched |= NormalizeArray(p.supportHand.leftFingers.joints, ref fixedCount);
                touched |= NormalizeArray(p.supportHand.leftFingers.indexPulled, ref fixedCount);
                touched |= NormalizeArray(p.supportHand.rightFingers.joints, ref fixedCount);
                touched |= NormalizeArray(p.supportHand.rightFingers.indexPulled, ref fixedCount);
                if (touched) { Undo.RecordObject(p, "Quaternion normalize"); EditorUtility.SetDirty(p); }
            }
            AssetDatabase.SaveAssets();
            _status = fixedCount + " eklem normalize edildi.";
            RunAudit();
        }

        // Dizi referans tip: elemanlari yerinde duzeltmek struct kopyasina takilmaz.
        // Sifir quaternion normalize EDILEMEZ — kimlige cevrilir (acik poz kaybi yerine
        // notr poz: parmak T-pose'da kalir, en azindan NaN gibi sessizce yayilmaz).
        static bool NormalizeArray(Quaternion[] qs, ref int fixedCount)
        {
            if (qs == null) return false;
            bool touched = false;
            for (int i = 0; i < qs.Length; i++)
            {
                var q = qs[i];
                if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) continue;
                float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (m >= 0.99f && m <= 1.01f) continue;
                qs[i] = m < 1e-6f ? Quaternion.identity
                                  : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
                fixedCount++; touched = true;
            }
            return touched;
        }

        // ---------------------------------------------------------------- arayuz

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Test ortami", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Ortami kur / sifirla", GUILayout.Height(26))) BuildRig();
            using (new EditorGUI.DisabledScope(Root == null))
                if (GUILayout.Button("Temizle", GUILayout.Height(26), GUILayout.Width(80))) { TearDown(); _status = "Test objeleri silindi."; }
            EditorGUILayout.EndHorizontal();

            _avatarOverride = (Animator)EditorGUILayout.ObjectField("Avatar (opsiyonel)", _avatarOverride, typeof(Animator), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Silah", EditorStyles.boldLabel);
            if (_profiles.Count == 0)
            {
                EditorGUILayout.HelpBox("Hic WeaponGripProfile bulunamadi.", MessageType.Warning);
                if (GUILayout.Button("Yeniden tara")) RefreshProfiles();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(_profileIndex <= 0))
                    if (GUILayout.Button("<", GUILayout.Width(28))) { _profileIndex--; SpawnWeapon(); }

                var names = new string[_profiles.Count];
                for (int i = 0; i < names.Length; i++)
                    names[i] = _profiles[i].name + (_weaponPrefabs[i] == null ? "  (prefab YOK)" : "");
                int yeni = EditorGUILayout.Popup(_profileIndex, names);
                if (yeni != _profileIndex) { _profileIndex = yeni; SpawnWeapon(); }

                using (new EditorGUI.DisabledScope(_profileIndex >= _profiles.Count - 1))
                    if (GUILayout.Button(">", GUILayout.Width(28))) { _profileIndex++; SpawnWeapon(); }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tutus", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _mode = (GripTestMode)EditorGUILayout.Popup("Durus", (int)_mode,
                new[] { "Serbest (bilek T-pozunda)", "Tasima (gogus onunde)", "Nisan (goz hizasi)" });
            if (_mode == GripTestMode.Nisan)
            {
                _aimDistance = EditorGUILayout.Slider("Nisan mesafesi (m)", _aimDistance, 0.18f, 0.60f);
                _aimDrop = EditorGUILayout.Slider("Goz altina dusme (m)", _aimDrop, 0f, 0.15f);
            }
            if (_mode != GripTestMode.Serbest)
                EditorGUILayout.HelpBox("Kollar iki-kemik IK ile YAKLASIK cozulur — bakilan sey elin " +
                    "silah uzerindeki yerlesimi ve silahin govde/kafa iliskisi, dirsegin acisi degil.",
                    MessageType.None);
            _mainLeft = EditorGUILayout.Popup("Ana el", _mainLeft ? 1 : 0, new[] { "Sag", "Sol (aynali)" }) == 1;
            _showSupport = EditorGUILayout.Toggle("Destek elini goster", _showSupport);
            _trigger = EditorGUILayout.Slider("Tetik", _trigger, 0f, 1f);
            _approxCurl = EditorGUILayout.Toggle(new GUIContent("Poz yoksa kaba kivrim",
                "Authored parmak pozu olmayan slotlarda 31. aracin kaba kivrimini uygular. " +
                "YAKLASIKTIR — runtime'daki prosedurel curl'le birebir ayni degildir."), _approxCurl);
            if (EditorGUI.EndChangeCheck()) ApplyPose();

            var cp = CurrentProfile();
            if (cp != null)
            {
                var mainFp = cp.mainHand.Fingers(_mainLeft);
                var supFp = cp.supportHand.Fingers(!_mainLeft);
                EditorGUILayout.LabelField("Ana el pozu", mainFp.HasPose ? (mainFp.HasIndexPulled ? "authored (+tetik)" : "authored") : "YOK (kaba kivrim)");
                EditorGUILayout.LabelField("Destek pozu", supFp.HasPose ? "authored" : "YOK (kaba kivrim)");

                if (GUILayout.Button("Bu profili 31. Parmak Pozu aracinda ac"))
                {
                    var w = GetWindow<WeaponHandPoseTool>("Parmak Pozu");
                    var so = new SerializedObject(w);
                    var prop = so.FindProperty("_profile");
                    if (prop != null) { prop.objectReferenceValue = cp; so.ApplyModifiedPropertiesWithoutUndo(); }
                    w.Focus();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Kemik denetimi", EditorStyles.boldLabel);
            if (GUILayout.Button("Tum profilleri ve rig'i tara", GUILayout.Height(24))) RunAudit();

            if (_auditRan)
            {
                if (_issues.Count == 0)
                    EditorGUILayout.HelpBox("Temiz — sorun bulunamadi.", MessageType.Info);

                bool anyNormalizable = false;
                foreach (var issue in _issues)
                {
                    anyNormalizable |= issue.normalizable;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.HelpBox(issue.msg, issue.type);
                    if (issue.target != null && GUILayout.Button("Sec", GUILayout.Width(40), GUILayout.Height(38)))
                        EditorGUIUtility.PingObject(issue.target);
                    EditorGUILayout.EndHorizontal();
                }
                if (anyNormalizable && GUILayout.Button("Normalize edilebilir quaternionlari DUZELT"))
                    FixNormalizableQuats();
            }

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- sahne cizimleri

        /// <summary>Kabza cipasi (kure + eksenler), destek rayi (cizgi) ve namlu yonu (ok).
        /// Cipa/ray AYNASIZ cizilir — profildeki ham degerin nerede oldugunu gosterir;
        /// aynali tutusta elin baska yerde durmasi beklenen davranistir.</summary>
        void OnSceneGUI(SceneView view)
        {
            var profile = CurrentProfile();
            var weapon = RigWeapon();
            if (profile == null || weapon == null) return;

            Vector3 grip = weapon.TransformPoint(profile.gripLocalPosition);
            Quaternion gripRot = weapon.rotation * profile.GripLocalRotation;
            float size = HandleUtility.GetHandleSize(grip) * 0.06f;

            Handles.color = new Color(1f, 0.85f, 0.15f);
            Handles.SphereHandleCap(0, grip, Quaternion.identity, size, EventType.Repaint);
            Handles.color = Color.red;   Handles.DrawLine(grip, grip + gripRot * Vector3.right * size * 3f);
            Handles.color = Color.green; Handles.DrawLine(grip, grip + gripRot * Vector3.up * size * 3f);
            Handles.color = Color.blue;  Handles.DrawLine(grip, grip + gripRot * Vector3.forward * size * 3f);

            if (profile.supportRailLocalStart != profile.supportRailLocalEnd)
            {
                Vector3 s = weapon.TransformPoint(profile.supportRailLocalStart);
                Vector3 e = weapon.TransformPoint(profile.supportRailLocalEnd);
                Handles.color = new Color(0.3f, 0.9f, 1f);
                Handles.DrawLine(s, e);
                Handles.SphereHandleCap(0, s, Quaternion.identity, size * 0.8f, EventType.Repaint);
                Handles.SphereHandleCap(0, e, Quaternion.identity, size * 0.8f, EventType.Repaint);
            }

            Vector3 barrel = weapon.TransformDirection(profile.barrelLocalDirection.normalized);
            Handles.color = new Color(1f, 0.4f, 0.25f);
            Handles.DrawLine(grip, grip + barrel * size * 8f);
        }
    }
}
