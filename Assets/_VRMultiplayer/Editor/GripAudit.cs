using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Silah tutuslarinin SAYISAL denetimi. Amac "hangi silah bozuk" tartismasini
    /// pikselden sayiya tasimak ve degisiklik sonrasi regresyon kapisi olmak.
    ///
    /// Her silah icin olculenler:
    ///   - authored poz var mi (ana el / destek el), FP eline cevrilmis mi
    ///   - CEVRIM SADAKATI: ayni poz avatarda ve FP elinde uygulaninca parmak uclari
    ///     kendi el cercevelerinde ne kadar ayrisiyor (mm). Cevrim aci uzerinden
    ///     yapildigi icin sifir beklenmez; buyuk sapma "o parmakta mentese eksenleri
    ///     ayristi" demektir.
    ///   - TUTUS GEOMETRISI: poz uygulanmis avatar eli silahin ustune, profilin kendi
    ///     bilek offset'iyle oturtulur; her parmak ucunun silah yuzeyine uzakligi
    ///     olculur. Negatif = mesh icinde (parmak silahin icinden geciyor).
    ///
    /// Silah ELE gore konumlandirilir (weld'in tersi): anchorRot = bilek * inv(wristLocalRot),
    /// anchorPos = bilekKonumu - anchorRot * wristLocalPos.
    /// </summary>
    public static class GripAudit
    {
        const string AvatarPrefab = "Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab";
        const string WeaponDir = "Assets/_VRMultiplayer/Resources/WeaponPrefabs";

        static readonly string[] FingerName = { "bas", "isaret", "orta", "yuzuk", "serce" };
        /// <summary>Her parmagin UC eklemi (HandPoseBones sirasinda 2, 5, 8, 11, 14).</summary>
        static readonly int[] TipJoint = { 2, 5, 8, 11, 14 };

        [MenuItem("Tools/VR Multiplayer/51. Silah Tutuslarini Denetle (sayisal tablo)")]
        public static void Run()
        {
            var avatarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefab);
            if (avatarPrefab == null) { Debug.LogError("[GripAudit] Avatar prefabi yok"); return; }

            var avatar = Object.Instantiate(avatarPrefab);
            avatar.transform.position = new Vector3(0f, -200f, 0f);   // sahnenin uzagi
            var animator = avatar.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            { Object.DestroyImmediate(avatar); Debug.LogError("[GripAudit] Humanoid yok"); return; }

            var sb = new StringBuilder();
            sb.AppendLine("silah                | poz  | FP  | cevrim sapmasi (yon acisi)   | parmak ucu <-> silah mesh'i (mm)");
            sb.AppendLine("---------------------|------|-----|------------------------------|---------------------------------");

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { WeaponDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var profile = WeaponGripBinder.FindProfile(prefab.name);
                if (profile == null) continue;

                var fp = profile.mainHand.rightFingers;
                string posed = fp.HasPose ? "var" : "YOK";
                string conv = fp.HasFpJoints ? "var" : "YOK";

                string drift = fp.HasPose && fp.HasFpJoints ? ConversionDrift(animator, fp) : "-";
                string contact = fp.HasPose ? Contact(animator, prefab, profile) : "-";

                sb.AppendLine(string.Format("{0,-20} | {1,-4} | {2,-3} | {3,-28} | {4}",
                    prefab.name.Replace("Weapon_", ""), posed, conv, drift, contact));
            }

            Object.DestroyImmediate(avatar);
            Debug.Log("[GripAudit]\n" + sb);
        }

        /// <summary>Ayni pozun avatardaki ve FP elindeki parmak uclari, iki el de kendi
        /// bilek cercevesine tasinip el boyuna gore olceklenerek karsilastirilir.</summary>
        static string ConversionDrift(Animator animator, WeaponGripProfile.FingerPose fp)
        {
            var saved = new Quaternion[HandPoseBones.JointCount];
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var b = animator.GetBoneTransform(HandPoseBones.Bone(j, false));
                if (b == null) continue;
                saved[j] = b.localRotation;
                b.localRotation = fp.joints[j];
            }

            var avatarTips = HandFramePoints(
                animator.GetBoneTransform(HumanBodyBones.RightHand),
                animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal),
                animator.GetBoneTransform(HumanBodyBones.RightIndexProximal),
                animator.GetBoneTransform(HumanBodyBones.RightLittleProximal),
                j => animator.GetBoneTransform(HandPoseBones.Bone(j, false)));

            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var b = animator.GetBoneTransform(HandPoseBones.Bone(j, false));
                if (b != null) b.localRotation = saved[j];
            }

            var hand = FpHand.Spawn(false);
            if (hand == null || avatarTips == null) return "olculemedi";
            try
            {
                hand.ApplyAngles(fp);
                var fpTips = hand.TipsInHandFrame();
                // Uc KONUMU degil YONU karsilastirilir: iki rig'in parmak boylari farkli
                // (avatarinkiler belirgin uzun), konum farki pozdan bagimsiz sabit bir
                // kayma uretiyordu ve cevrim hatasini gizliyordu.
                float worst = 0f; int worstFinger = 0;
                for (int f = 0; f < 5; f++)
                {
                    float d = Vector3.Angle(avatarTips[f].normalized, fpTips[f].normalized);
                    if (d > worst) { worst = d; worstFinger = f; }
                }
                return string.Format("en fazla {0:F1} derece ({1})", worst, FingerName[worstFinger]);
            }
            finally { hand.Dispose(); }
        }

        /// <summary>Parmak uclarinin silah yuzeyine uzakligi. Silah, profilin bilek
        /// offset'iyle avatarin eline oturtulur (weld'in ters cozumu).</summary>
        static string Contact(Animator animator, GameObject weaponPrefab, WeaponGripProfile profile)
        {
            var saved = new Quaternion[HandPoseBones.JointCount];
            var fp = profile.mainHand.rightFingers;
            for (int j = 0; j < HandPoseBones.JointCount; j++)
            {
                var b = animator.GetBoneTransform(HandPoseBones.Bone(j, false));
                if (b == null) continue;
                saved[j] = b.localRotation;
                b.localRotation = fp.joints[j];
            }

            var weapon = Object.Instantiate(weaponPrefab);
            try
            {
                var wrist = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (wrist == null) return "bilek yok";

                Quaternion wristLocalRot = Quaternion.Euler(profile.mainHand.wristLocalEuler);
                Quaternion anchorRot = wrist.rotation * Quaternion.Inverse(wristLocalRot);
                Vector3 anchorPos = wrist.position - anchorRot * profile.mainHand.wristLocalPosition;
                weapon.transform.rotation = anchorRot * Quaternion.Inverse(profile.GripLocalRotation);
                weapon.transform.position = anchorPos - weapon.transform.rotation * profile.gripLocalPosition;

                // GERCEK mesh'e olculuyor, collider'a degil: silahlarin collider'lari
                // bolge-bazli kaba kutular (GunPhysicsSetup), kabzayi saran parmak o
                // kutunun "icinde" cikiyor ve olcum yaniltiyor.
                var verts = MeshPoints(weapon);
                if (verts == null || verts.Length == 0) return "mesh yok";

                var sb = new StringBuilder();
                for (int f = 0; f < 5; f++)
                {
                    var tip = animator.GetBoneTransform(HandPoseBones.Bone(TipJoint[f], false));
                    if (tip == null) { sb.Append(FingerName[f]).Append("=?  "); continue; }
                    float d = NearestPoint(tip.position, verts) * 1000f;
                    sb.AppendFormat("{0}={1,4:F0}  ", FingerName[f], d);
                }
                return sb.ToString();
            }
            finally
            {
                Object.DestroyImmediate(weapon);
                for (int j = 0; j < HandPoseBones.JointCount; j++)
                {
                    var b = animator.GetBoneTransform(HandPoseBones.Bone(j, false));
                    if (b != null) b.localRotation = saved[j];
                }
            }
        }

        /// <summary>Silahin gorunen mesh'lerinin dunya uzayindaki tepe noktalari.
        /// Silah mesh'leri yeterince yogun oldugu icin en yakin TEPE, yuzeye uzakligin
        /// iyi bir yaklasimi (ucgen ici mesafe hesabina gerek yok).</summary>
        static Vector3[] MeshPoints(GameObject weapon)
        {
            var list = new System.Collections.Generic.List<Vector3>();
            foreach (var mf in weapon.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var v = mf.sharedMesh.vertices;
                var l2w = mf.transform.localToWorldMatrix;
                for (int i = 0; i < v.Length; i += 3)     // seyreltilmis ornekleme yeter
                    list.Add(l2w.MultiplyPoint3x4(v[i]));
            }
            foreach (var smr in weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var v = smr.sharedMesh.vertices;
                var l2w = smr.transform.localToWorldMatrix;
                for (int i = 0; i < v.Length; i += 3)
                    list.Add(l2w.MultiplyPoint3x4(v[i]));
            }
            return list.ToArray();
        }

        static float NearestPoint(Vector3 p, Vector3[] pts)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Length; i++)
            {
                float d = (pts[i] - p).sqrMagnitude;
                if (d < best) best = d;
            }
            return best == float.MaxValue ? float.NaN : Mathf.Sqrt(best);
        }

        static Vector3[] HandFramePoints(Transform wrist, Transform mid, Transform idx, Transform pky,
                                         System.Func<int, Transform> bone)
        {
            if (wrist == null || mid == null || idx == null || pky == null) return null;
            Vector3 fwd = (mid.position - wrist.position).normalized;
            Vector3 palm = Vector3.Cross(fwd, (idx.position - pky.position).normalized).normalized;
            Vector3 side = Vector3.Cross(fwd, palm).normalized;
            float scale = Vector3.Distance(wrist.position, mid.position);   // el boyu ile normalize
            if (scale < 1e-6f) return null;

            var outp = new Vector3[5];
            for (int f = 0; f < 5; f++)
            {
                var t = bone(TipJoint[f]);
                if (t == null) continue;
                Vector3 d = t.position - wrist.position;
                outp[f] = new Vector3(Vector3.Dot(d, side), Vector3.Dot(d, fwd), Vector3.Dot(d, palm)) / scale;
            }
            return outp;
        }

        /// <summary>Olcum icin gecici FP eli: prefabi kurar, aci uygular, uclari okur.</summary>
        class FpHand : System.IDisposable
        {
            GameObject _go;
            System.Collections.Generic.Dictionary<string, Transform> _map;
            string _p, _m;
            Quaternion[] _rest;
            Vector3[] _axis;
            Transform[] _bone;

            public static FpHand Spawn(bool left)
            {
                var prefab = Resources.Load<GameObject>("FPHands/Meta/OculusHand_" + (left ? "L" : "R"));
                if (prefab == null) return null;
                var h = new FpHand { _go = Object.Instantiate(prefab), _p = left ? "b_l_" : "b_r_", _m = left ? "l_" : "r_" };
                h._map = new System.Collections.Generic.Dictionary<string, Transform>();
                foreach (var t in h._go.GetComponentsInChildren<Transform>(true))
                    if (!h._map.ContainsKey(t.name)) h._map[t.name] = t;
                h.BuildAxes();
                return h;
            }

            void BuildAxes()
            {
                Transform wrist = _map[_p + "wrist"], mid = _map[_p + "middle1"], idx = _map[_p + "index1"], pky = _map[_p + "pinky1"];
                Vector3 fwd = (mid.position - wrist.position).normalized;
                Vector3 palm = Vector3.Cross(fwd, (idx.position - pky.position).normalized).normalized;

                string[] bones = { "thumb0", "thumb1", "thumb2", "index1", "index2", "index3",
                                   "middle1", "middle2", "middle3", "ring1", "ring2", "ring3",
                                   "pinky1", "pinky2", "pinky3" };
                string[] markers = { "thumb_cmc_fe_axis_marker", "thumb_mcp_fe_axis_marker", "thumb_ip_fe_axis_marker" };
                string[] next = { "thumb1", "thumb2", "thumb3", "index2", "index3", "index_null",
                                  "middle2", "middle3", "middle_null", "ring2", "ring3", "ring_null",
                                  "pinky2", "pinky3", "pinky_null" };

                _bone = new Transform[15]; _rest = new Quaternion[15]; _axis = new Vector3[15];
                for (int j = 0; j < 15; j++)
                {
                    Transform b;
                    if (!_map.TryGetValue(_p + bones[j], out b)) continue;
                    _bone[j] = b; _rest[j] = b.localRotation;
                    Transform mk;
                    if (j < 3 && _map.TryGetValue(_m + markers[j], out mk) && mk != null)
                    { _axis[j] = b.InverseTransformDirection(mk.right); continue; }
                    Transform nx;
                    Vector3 ext = _map.TryGetValue(_p + next[j], out nx) && nx != null
                        ? (nx.position - b.position) : b.forward;
                    Vector3 hinge = Vector3.Cross(ext.normalized, palm);
                    _axis[j] = hinge.sqrMagnitude < 1e-8f ? Vector3.right : b.InverseTransformDirection(hinge.normalized);
                }
            }

            public void ApplyAngles(WeaponGripProfile.FingerPose fp)
            {
                for (int j = 0; j < 15; j++)
                    if (_bone[j] != null) _bone[j].localRotation = _rest[j] * fp.fpJoints[j];
            }

            public Vector3[] TipsInHandFrame()
            {
                Transform wrist = _map[_p + "wrist"], mid = _map[_p + "middle1"], idx = _map[_p + "index1"], pky = _map[_p + "pinky1"];
                Vector3 fwd = (mid.position - wrist.position).normalized;
                Vector3 palm = Vector3.Cross(fwd, (idx.position - pky.position).normalized).normalized;
                Vector3 side = Vector3.Cross(fwd, palm).normalized;
                float scale = Vector3.Distance(wrist.position, mid.position);
                var outp = new Vector3[5];
                for (int f = 0; f < 5; f++)
                {
                    var t = _bone[TipJoint[f]];
                    if (t == null) continue;
                    Vector3 d = t.position - wrist.position;
                    outp[f] = new Vector3(Vector3.Dot(d, side), Vector3.Dot(d, fwd), Vector3.Dot(d, palm)) / scale;
                }
                return outp;
            }

            public void Dispose() { if (_go != null) Object.DestroyImmediate(_go); }
        }
    }
}
