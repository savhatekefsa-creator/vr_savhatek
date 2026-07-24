using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.UI;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// COMELME POZU YAKALAMA — sahnedeki avatarin BACAK kemiklerini elle cevirdikten sonra
    /// o pozu bir asset'e yazar. Silah tutuslarindaki WeaponGripCaptureTool ile ayni is akisi.
    ///
    /// SADECE BACAK KEMIKLERI yakalanir (kalca/omurga/gogus HARIC). Sebep onemli:
    /// AvatarIKController kafa kemigini oyuncunun gozune kilitliyor ve kok yuksekligini kafa
    /// kemiginin koke gore yuksekliginden hesapliyor. Omurgayi cevirmek o yuksekligi degistirir,
    /// o da koku kaydirir, sonuc GERI BESLEME DONGUSU olur (bunu bir kez yasadik, duruş cizgisi
    /// bozuldu). Bacaklar bu zincirin disinda oldugu icin tamamen guvenli.
    /// </summary>
    public static class CrouchPoseCaptureTool
    {
        const string Dir = "Assets/_VRMultiplayer/Resources/CrouchPoses";

        // Kalca/omurga BILINCLI olarak listede yok — yukaridaki nota bak.
        static readonly HumanBodyBones[] Captured =
        {
            HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
        };

        [MenuItem("Tools/VR Multiplayer/44. Comelme Pozunu Yakala (once sahnede avatari sec)")]
        public static void Capture()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Say("Once sahnedeki avatari (ya da bir kemigini) sec, sonra tekrar dene.");
                return;
            }

            var anim = go.GetComponentInParent<Animator>();
            if (anim == null) anim = go.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
            {
                Say("Secilen nesnede humanoid Animator bulunamadi.\n\n" +
                    "Avatar modelini sahneye surukleyip onu (ya da altindaki bir kemigi) sec.");
                return;
            }

            var list = new List<CrouchPoseAsset.BonePose>();
            var missing = new List<string>();
            foreach (var b in Captured)
            {
                var t = anim.GetBoneTransform(b);
                if (t == null) { missing.Add(b.ToString()); continue; }
                list.Add(new CrouchPoseAsset.BonePose { bone = b, rotation = t.localRotation });
            }

            if (list.Count == 0)
            {
                Say("Bacak kemikleri bulunamadi — model humanoid olarak eslenmemis olabilir.");
                return;
            }

            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, "ComelmePozu.asset").Replace('\\', '/');

            var asset = AssetDatabase.LoadAssetAtPath<CrouchPoseAsset>(path);
            bool isNew = asset == null;
            if (isNew) asset = ScriptableObject.CreateInstance<CrouchPoseAsset>();

            asset.bones = list.ToArray();

            if (isNew) AssetDatabase.CreateAsset(asset, path);
            else EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            // Teshis: pozun ne kadar derin oldugunu bildirelim ki oran ayari korlemesine olmasin.
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            var foot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            string depth = (hips != null && foot != null)
                ? $"\n\nOlculen kalca-bilek dikey mesafesi: {(hips.position.y - foot.position.y):F3} m " +
                  "(ayaktaki degeri ~0.85 m'dir — ne kadar kucukse poz o kadar derin)."
                : "";

            Debug.Log($"[Comelme Pozu] {list.Count} bacak kemigi yakalandi -> {path}" +
                      (missing.Count > 0 ? $" (bulunamayan: {string.Join(", ", missing)})" : ""));

            Say($"Poz kaydedildi: {path}\n\n" +
                $"{list.Count} bacak kemigi yakalandi. Kalca ve omurgaya BILEREK dokunulmadi " +
                "(oyuncunun gercek durusunu ezmesin).\n\n" +
                "Asset'in Inspector'inda iki ayar var:\n" +
                "  startsAtHeightRatio (0.94) — poz devreye girmeye baslar\n" +
                "  appliesAtHeightRatio (0.65) — poz tam devrede" + depth, asset);

            Selection.activeObject = asset;
        }

        static void Say(string msg, Object select = null)
        {
            EditorUtility.DisplayDialog("Comelme Pozu", msg, "Tamam");
            if (select != null) Selection.activeObject = select;
        }
    }
}
