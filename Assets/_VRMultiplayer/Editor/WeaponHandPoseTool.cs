using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Parmak pozu yazma araci — projenin ISDK HandGrabPose editoru karsiligi.
    ///
    /// Neden var: prosedurel curl bir kabzayi saramaz. Tek skaler (0..1) + sabit acili menteşe
    /// ile parmak yayilmasi, her parmagin kabzada farkli derinlikte durmasi ve basparmagin avuc
    /// ustunden capraz gecmesi TEMSIL EDILEMEZ; ustelik kod kabzanin nerede bittigini bilmez, o
    /// yuzden parmaklar icinden geçer. Cozum: pozu bir kere elle ver, 15 eklemin lokal
    /// rotasyonunu profile kaydet, runtime aynen yazsin. Eksen tahmini tamamen ortadan kalkar —
    /// rig'in kemik eksenleri ne kadar tuhaf olursa olsun gordugun poz aynen geri gelir.
    ///
    /// Akis (EDIT MODE'da, Play'de degil — Play'de poser her kare uzerine yazar):
    ///   1. Sahneye avatari ve silahi surukle.
    ///   2. Bu pencerede profil + avatar + silah + rol/el sec.
    ///   3. "Silahi ele hizala" — silah, weld'in runtime'da koyacagi yere birebir oturur.
    ///   4. "Kaba kivrim uygula" — baslangic pozu. Sonra parmak kemiklerini Hierarchy'den secip
    ///      normal Rotate tool (E) ile ince ayar yap.
    ///   5. "Profile KAYDET".
    ///
    /// Edit mode'da idle animasyonu calismadigi icin olcumler temiz: WYSIWYG.
    /// </summary>
    public class WeaponHandPoseTool : EditorWindow
    {
        WeaponGripProfile _profile;
        Animator _avatar;
        Transform _weapon;
        bool _support;          // false = ana el (kabza), true = destek eli (kundak)
        bool _left;             // pozun yazildigi FIZIKSEL el
        float _seed = 1f;
        Vector2 _scroll;
        string _status = "";

        // Rest (T-pose) anlik goruntusu: seed her seferinde BURADAN baslar, yoksa butona iki kez
        // basinca kivrim uzerine biner.
        readonly Dictionary<Transform, Quaternion> _rest = new Dictionary<Transform, Quaternion>();
        Animator _restOf;

        // Prosedurel curl'un kaba karsiligi — sadece BASLANGIC pozu uretmek icin. Runtime'daki
        // ProceduralFingerPoser degerleriyle birebir ayni olmasi gerekmiyor: uzerine zaten elle
        // ince ayar yapilacak, kaydedilen sey de curl degil son rotasyonlar.
        const float ProximalCurl = 55f, IntermediateCurl = 80f, DistalCurl = 55f, ThumbCurl = 40f;

        [MenuItem("Tools/VR Multiplayer/31. Weapon Finger Pose Tool")]
        static void Open() => GetWindow<WeaponHandPoseTool>("Parmak Pozu");

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "EDIT MODE'da kullan (Play'de poser her kare uzerine yazar).\n" +
                "Parmaklari Hierarchy'den secip Rotate tool (E) ile bukersin, sonra kaydedersin.",
                MessageType.Info);

            _profile = (WeaponGripProfile)EditorGUILayout.ObjectField("Profil", _profile, typeof(WeaponGripProfile), false);
            _avatar = (Animator)EditorGUILayout.ObjectField("Avatar (sahnede)", _avatar, typeof(Animator), true);
            _weapon = (Transform)EditorGUILayout.ObjectField("Silah (sahnede)", _weapon, typeof(Transform), true);

            EditorGUILayout.Space();
            _support = EditorGUILayout.Popup("Rol", _support ? 1 : 0, new[] { "Ana el (kabza)", "Destek eli (kundak)" }) == 1;
            _left = EditorGUILayout.Popup("El", _left ? 1 : 0, new[] { "Sag el", "Sol el" }) == 1;

            if (_profile != null)
            {
                var fp = (_support ? _profile.supportHand : _profile.mainHand).Fingers(_left);
                EditorGUILayout.LabelField("Bu slotta poz",
                    fp.HasPose ? (fp.HasIndexPulled ? "VAR (+ tetik cekili)" : "VAR") : "yok (curl kullanilir)");
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Ready()))
            {
                if (GUILayout.Button("1) Silahi ele hizala")) AlignWeapon();

                EditorGUILayout.BeginHorizontal();
                _seed = EditorGUILayout.Slider("Kivrim", _seed, 0f, 1.5f);
                if (GUILayout.Button("2) Kaba kivrim uygula", GUILayout.Width(160))) SeedCurl();
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("T-pose'a don")) ResetToRest();

                EditorGUILayout.Space();
                if (GUILayout.Button("3) Profile KAYDET", GUILayout.Height(30))) Save(false);
                if (GUILayout.Button("Tetik CEKILI isaret parmagini kaydet")) Save(true);

                EditorGUILayout.Space();
                if (GUILayout.Button("Profilden yukle (duzenlemeye devam et)")) Load();
            }

            if (!Ready())
                EditorGUILayout.HelpBox("Profil + avatar sec. Avatar humanoid olmali.", MessageType.Warning);
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        bool Ready() => _profile != null && _avatar != null && _avatar.isHuman;

        Transform Bone(int joint) => _avatar.GetBoneTransform(HandPoseBones.Bone(joint, _left));

        // Yazim kurali ana el=SAG / destek=SOL. Roller takas edilince profilin lokal degerleri
        // aynalanir — weld ile BIREBIR ayni kural, yoksa arac silahi runtime'dakinden baska
        // yere koyar ve yanlis yere poz verirsin.
        bool Mirrored => _support != _left;

        void CacheRest()
        {
            if (_restOf == _avatar && _rest.Count > 0) return;
            _rest.Clear();
            _restOf = _avatar;
            for (int side = 0; side < 2; side++)
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                {
                    var t = _avatar.GetBoneTransform(HandPoseBones.Bone(j, side == 0));
                    if (t != null) _rest[t] = t.localRotation;
                }
        }

        void ResetToRest()
        {
            CacheRest();
            foreach (var kv in _rest)
            {
                if (kv.Key == null) continue;
                Undo.RecordObject(kv.Key, "T-pose'a don");
                kv.Key.localRotation = kv.Value;
            }
            _status = "T-pose'a donuldu.";
        }

        // Weld'in TERSI: bilek kemigi nerede duruyorsa, silahi cipasi tam oraya gelecek sekilde
        // yerlestir. WeaponHandWeld.WeldSide ile ayni formul, ters cozulmus hali —
        // boylece editorde gordugun tutus runtime'dakiyle ayni.
        void AlignWeapon()
        {
            if (_weapon == null) { _status = "Silah alani bos."; return; }

            var hp = _support ? _profile.supportHand : _profile.mainHand;
            Transform bone = _avatar.GetBoneTransform(_left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (bone == null) { _status = "Bilek kemigi bulunamadi (rig humanoid mi?)."; return; }

            // Destek eli weld'de ray uzerinde kayar; poz verirken rayin BASI referans alinir.
            Vector3 anchorLocal = _support ? _profile.supportRailLocalStart : _profile.gripLocalPosition;
            Quaternion anchorLocalRot = _profile.GripLocalRotation;
            Vector3 wristLocalPos = hp.wristLocalPosition;
            Quaternion wristLocalRot = Quaternion.Euler(hp.wristLocalEuler);
            if (Mirrored)
            {
                anchorLocal = WeaponGripMath.MirrorX(anchorLocal);
                anchorLocalRot = WeaponGripMath.MirrorX(anchorLocalRot);
                wristLocalPos = WeaponGripMath.MirrorX(wristLocalPos);
                wristLocalRot = WeaponGripMath.MirrorX(wristLocalRot);
            }

            Undo.RecordObject(_weapon, "Silahi ele hizala");
            // bone.rotation = anchorRot * wristLocalRot  =>  anchorRot = bone.rotation * inv(wristLocalRot)
            Quaternion anchorRot = bone.rotation * Quaternion.Inverse(wristLocalRot);
            Quaternion weaponRot = anchorRot * Quaternion.Inverse(anchorLocalRot);
            // bone.position = anchorPos + anchorRot * wristLocalPos
            Vector3 anchorPos = bone.position - anchorRot * wristLocalPos;
            // anchorPos = weapon.TransformPoint(anchorLocal)
            Vector3 weaponPos = anchorPos - weaponRot * Vector3.Scale(_weapon.lossyScale, anchorLocal);
            _weapon.SetPositionAndRotation(weaponPos, weaponRot);
            _status = "Silah hizalandi. Simdi parmaklari bük.";
        }

        // Baslangic pozu: her bogumu kendi menteşe ekseni etrafinda kabaca kivirir. Edit mode'da
        // idle animasyonu olmadigi icin eksenler TEMIZ olcusuluyor (runtime'daki eski bug'in
        // sebebi tam da idle'in bu olcumu kirletmesiydi).
        void SeedCurl()
        {
            CacheRest();
            ResetToRest();

            Transform wrist = _avatar.GetBoneTransform(_left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform idxP = Bone(3), midP = Bone(6), litP = Bone(12);
            if (wrist == null || idxP == null || midP == null || litP == null)
            {
                _status = "Parmak kemikleri eslesmemis — Avatar Configure ekranindan parmaklari map'le.";
                return;
            }

            Vector3 fingersDir = (midP.position - wrist.position).normalized;
            Vector3 sideDir = (idxP.position - litP.position).normalized;
            Vector3 palmNormal = Vector3.Cross(fingersDir, sideDir).normalized;
            Vector3 curlPlane = _left ? -palmNormal : palmNormal;
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

                    // Dort parmak kendi duzleminde katlanir; basparmak avuc ustunden hedefe dogru.
                    Vector3 hinge = f == 0
                        ? Vector3.Cross(ext.normalized, (thumbTarget - b.position).normalized)
                        : Vector3.Cross(ext.normalized, curlPlane);
                    if (hinge.sqrMagnitude < 1e-8f) continue;

                    float deg = f == 0
                        ? (j == 2 ? ThumbCurl * 0.8f : ThumbCurl)
                        : (j == 0 ? ProximalCurl : j == 1 ? IntermediateCurl : DistalCurl);

                    Vector3 axisParent = b.parent.InverseTransformDirection(hinge.normalized).normalized;
                    Undo.RecordObject(b, "Kaba kivrim");
                    b.localRotation = Quaternion.AngleAxis(deg * _seed, axisParent) * b.localRotation;
                }
            }
            _status = $"Kaba kivrim uygulandi ({_seed:0.00}). Simdi ince ayar yap.";
        }

        void Load()
        {
            var fp = (_support ? _profile.supportHand : _profile.mainHand).Fingers(_left);
            if (!fp.HasPose) { _status = "Bu slotta kayitli poz yok."; return; }
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var t = Bone(j);
                if (t == null) continue;
                Undo.RecordObject(t, "Pozu yukle");
                t.localRotation = fp.joints[j];
            }
            _status = "Poz profilden yuklendi.";
        }

        void Save(bool indexPulledOnly)
        {
            // HandPose ve FingerPose STRUCT: uzerinde oynayip geri ATAMAZSAN degisiklik kaybolur.
            var hp = _support ? _profile.supportHand : _profile.mainHand;
            var fp = _left ? hp.leftFingers : hp.rightFingers;

            Undo.RecordObject(_profile, "Parmak pozu kaydet");

            if (indexPulledOnly)
            {
                if (!fp.HasPose) { _status = "Once tam pozu kaydet (tetik BIRAKILI hali)."; return; }
                var pulled = new Quaternion[HandPoseBones.IndexJointCount];
                for (int i = 0; i < pulled.Length; i++)
                {
                    var t = Bone(HandPoseBones.IndexFirst + i);
                    pulled[i] = t != null ? t.localRotation : Quaternion.identity;
                }
                fp.indexPulled = pulled;
                _status = "Tetik cekili isaret parmagi kaydedildi.";
            }
            else
            {
                var joints = new Quaternion[HandPoseBones.JointCount];
                int missing = 0;
                for (int j = 0; j < joints.Length; j++)
                {
                    var t = Bone(j);
                    if (t == null) { joints[j] = Quaternion.identity; missing++; }
                    else joints[j] = t.localRotation;
                }
                fp.joints = joints;
                _status = missing == 0
                    ? $"Poz kaydedildi ({(_support ? "destek" : "ana")} / {(_left ? "sol" : "sag")})."
                    : $"Poz kaydedildi ama {missing} eklem rig'de YOK — Avatar Configure'dan parmaklari map'le.";
            }

            if (_left) hp.leftFingers = fp; else hp.rightFingers = fp;
            if (_support) _profile.supportHand = hp; else _profile.mainHand = hp;

            EditorUtility.SetDirty(_profile);
            AssetDatabase.SaveAssets();
        }
    }
}
