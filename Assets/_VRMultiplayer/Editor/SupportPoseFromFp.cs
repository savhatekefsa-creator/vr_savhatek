using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Avatarin DESTEK eli pozunu, atolyede ayarlanan BIRINCI SAHIS (fp*) pozundan turetir.
    ///
    /// Sorun: atolye yalniz fpWristLocal* alanlarini ayarliyor (oyuncunun gordugu el). Avatarin
    /// bilek pozu (supportHand.wristLocal*) ayri veri ve tabancalarda hic ayarlanmamisti:
    /// FP'de destek el ana elin 5 cm yaninda (cup-and-saucer), avatarda 16-24 cm onde -
    /// disaridan bakan oyuncu tabancayi tufek gibi tutuyor goruyordu. Olculdu (2026-09-08):
    /// Pistol 2/3/4/Pistol destek eli avatar-FP farki 14-22 cm, Rifle 1'de 1 cm.
    ///
    /// Yontem: iki rig'in bilek cerceveleri farkli ama aralarindaki iliski SABIT (ayni elin
    /// iki farkli dugumu). ANA el her silahta iki tarafta da ayarli oldugu icin donusum oradan
    /// cikar: avatarRot = fpRot * D, avatarPos = fpPos + fpRot * d. D ve d tum silahlarin ana
    /// el ciftlerinden ortalanir (olcumde silahlar arasi sapma 0-22 derece, ortalama ~10:
    /// elle ayar gurultusu). Sonra destek eline uygulanir; avatar-FP farki her silahta ~2 cm'e
    /// duser.
    ///
    /// Yalniz avatar-FP farki esigi asan silahlara yazilir (varsayilan 8 cm): elle iyi
    /// ayarlanmis destek pozlari (Rifle 1: 1 cm) dokunulmadan kalir. Parmak kivrimlarina
    /// dokunmaz (bkz. FpGripPoseBake - o yon avatar->FP). Atolyede fp* yeniden ayarlaninca
    /// bu arac tekrar kosulmali.
    /// </summary>
    public static class SupportPoseFromFp
    {
        const float DefaultThresholdMeters = 0.08f;

        [MenuItem("Tools/VR Multiplayer/58 Destek pozunu FP'den turet (avatar, >8 cm sapanlar)")]
        public static void RunMenu() => Debug.Log(Run(DefaultThresholdMeters, true));

        [MenuItem("Tools/VR Multiplayer/58b Destek pozu FP'den - YALNIZ RAPOR")]
        public static void ReportMenu() => Debug.Log(Run(DefaultThresholdMeters, false));

        static bool HasFp(WeaponGripProfile.HandPose h) =>
            h.fpWristLocalPosition.sqrMagnitude > 1e-8f || h.fpWristLocalEuler.sqrMagnitude > 1e-6f;

        public static string Run(float thresholdMeters, bool apply)
        {
            var profiles = Resources.LoadAll<WeaponGripProfile>("WeaponGripProfiles");
            var sb = new StringBuilder();
            sb.AppendLine($"[DestekPozu] {profiles.Length} profil, esik {thresholdMeters * 100f:0} cm, {(apply ? "YAZ" : "rapor")}");

            // 1) Rig donusumu: ana el ciftleri
            var Ds = new List<Quaternion>();
            var ds = new List<Vector3>();
            foreach (var p in profiles)
            {
                var m = p.mainHand;
                if (!HasFp(m)) continue;
                Quaternion fpR = Quaternion.Euler(m.fpWristLocalEuler);
                Quaternion avR = Quaternion.Euler(m.wristLocalEuler);
                Ds.Add(Quaternion.Inverse(fpR) * avR);
                ds.Add(Quaternion.Inverse(fpR) * (m.wristLocalPosition - m.fpWristLocalPosition));
            }
            if (Ds.Count == 0) return sb.AppendLine("  ana el FP pozu olan profil yok - donusum cikarilamadi").ToString();

            Quaternion D = Ds[0];
            Vector3 d = Vector3.zero;
            for (int i = 1; i < Ds.Count; i++) D = Quaternion.Slerp(D, Ds[i], 1f / (i + 1));
            foreach (var v in ds) d += v;
            d /= ds.Count;
            float spread = 0f;
            foreach (var q in Ds) spread = Mathf.Max(spread, Quaternion.Angle(D, q));
            sb.AppendLine($"  donusum: {Ds.Count} ciftten, D={D.eulerAngles:0} d={d:0.000}, en buyuk sapma {spread:0} deg");

            // 2) Destek eli
            int written = 0;
            foreach (var p in profiles)
            {
                var s = p.supportHand;
                if (!HasFp(s)) { sb.AppendLine($"  {p.name,-24} destek FP pozu yok - atlandi"); continue; }
                Quaternion fpR = Quaternion.Euler(s.fpWristLocalEuler);
                Vector3 newP = s.fpWristLocalPosition + fpR * d;
                Quaternion newR = fpR * D;
                float before = Vector3.Distance(s.wristLocalPosition, s.fpWristLocalPosition);
                float after = Vector3.Distance(newP, s.fpWristLocalPosition);
                float rot = Quaternion.Angle(Quaternion.Euler(s.wristLocalEuler), newR);
                bool doIt = before >= thresholdMeters;
                sb.AppendLine($"  {p.name,-24} avatar-FP {before * 100f,3:0} cm -> {after * 100f,3:0} cm, donus {rot,3:0} deg  {(doIt ? (apply ? "YAZILDI" : "yazilacak") : "korundu")}");
                if (!doIt || !apply) continue;

                s.wristLocalPosition = newP;
                s.wristLocalEuler = newR.eulerAngles;
                p.supportHand = s;
                EditorUtility.SetDirty(p);
                written++;
            }
            if (apply && written > 0) AssetDatabase.SaveAssets();
            sb.AppendLine($"  {written} profil yazildi");
            return sb.ToString();
        }
    }
}
