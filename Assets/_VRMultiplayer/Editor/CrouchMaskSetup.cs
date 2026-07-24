using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Comelme (Crouch) animator katmanina ALT VUCUT maskesi takar.
    ///
    /// SORUN: Crouch katmaninin maskesi yoktu (m_Mask: {fileID: 0}) ve modu Override. Yani
    /// agirlik bindiginde klip sadece bacaklari degil GOVDEYI ve KOLLARI da ele geciriyordu.
    /// Kollar Animation Rigging IK'si ile geri duzeltiliyor ama govde klibin dedigi gibi
    /// kaliyor — bu yuzden "klip pozu kotu" gibi gorunuyordu, halbuki sorun maskenin yokluguydu.
    ///
    /// COZUM: klip yalnizca BACAKLARI (+ kok) surer; govde, kafa, kollar ve parmaklar taban
    /// katmanda ve IK'de kalir. Uzerine AvatarLegPlant'in ayak oturtmasi biner:
    ///   klip = gorunus (diz nasil bukulsun)   +   IK = temas (ayak tam zeminde)
    /// Bu, animasyonla IK'yi katmanlamanin standart yolu.
    ///
    /// KULLANIM: menuyu bir kez calistir, sonra Play'de avatarin AvatarLegPlant bileseninde
    /// "disableCrouchClip" kutusunu BOSALT. Kapali kalirsa klip susturulur ve sadece IK calisir.
    /// </summary>
    public static class CrouchMaskSetup
    {
        const string MaskPath = "Assets/_VRMultiplayer/Avatar/CrouchLowerBody.mask";
        const string LayerName = "Crouch";

        [MenuItem("Tools/VR Multiplayer/43. Comelme Katmanina Alt Vucut Maskesi Tak")]
        public static void Apply()
        {
            var ctrl = FindIdleController();
            if (ctrl == null)
            {
                EditorUtility.DisplayDialog("Comelme Maskesi",
                    "Crouch katmani olan bir AnimatorController bulunamadi.\n\n" +
                    "Once Tools > VR Multiplayer > 9 ile comelme animasyonunu ekle.", "Tamam");
                return;
            }

            var mask = BuildOrUpdateMask();

            // ctrl.layers bir KOPYA dondurur; degisiklik icin diziyi geri yazmak sart.
            var layers = ctrl.layers;
            int found = -1;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == LayerName) { found = i; break; }

            if (found < 0)
            {
                EditorUtility.DisplayDialog("Comelme Maskesi",
                    $"'{ctrl.name}' icinde '{LayerName}' adli katman yok.\n\n" +
                    "Once Tools > VR Multiplayer > 9 ile comelme animasyonunu ekle.", "Tamam");
                return;
            }

            layers[found].avatarMask = mask;
            ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Comelme Maskesi] '{ctrl.name}' -> '{LayerName}' katmanina " +
                      $"alt vucut maskesi takildi ({MaskPath}). Klip artik yalnizca bacaklari surer; " +
                      "govde/kollar taban katmanda ve IK'de kalir.");

            EditorUtility.DisplayDialog("Comelme Maskesi",
                "Maske takildi.\n\n" +
                "Klip artik SADECE bacaklari sürüyor — govde, kafa ve kollar taban katmanda kaliyor.\n\n" +
                "Simdi Play'e gec ve avatarin AvatarLegPlant bileseninde " +
                "\"disableCrouchClip\" kutusunu BOSALT. O zaman:\n" +
                "  klip = pozun gorunusu,  bacak IK = ayagin zemine temasi.\n\n" +
                "Kutuyu isaretli birakirsan klip susar, eskisi gibi yalnizca IK calisir.", "Tamam");

            Selection.activeObject = mask;
        }

        /// <summary>Alt vucut maskesi: bacaklar + kok acik, gerisi kapali.</summary>
        static AvatarMask BuildOrUpdateMask()
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            bool isNew = mask == null;
            if (isNew) mask = new AvatarMask();

            // Kok ACIK: kalcanin dikey hareketini klip verir, aksi halde comelmede kalca inmez.
            // Govde KAPALI: govdenin one egilmesini klip belirlemesin — dik durus taban katmandan
            // gelsin. (Govdeyi de klibe birakmak istersen Body'yi true yap; ama o zaman klibin
            // egilmesi senin gercek durusunu ezer.)
            Set(mask, AvatarMaskBodyPart.Root, true);
            Set(mask, AvatarMaskBodyPart.LeftLeg, true);
            Set(mask, AvatarMaskBodyPart.RightLeg, true);
            Set(mask, AvatarMaskBodyPart.LeftFootIK, true);
            Set(mask, AvatarMaskBodyPart.RightFootIK, true);

            Set(mask, AvatarMaskBodyPart.Body, false);
            Set(mask, AvatarMaskBodyPart.Head, false);
            Set(mask, AvatarMaskBodyPart.LeftArm, false);
            Set(mask, AvatarMaskBodyPart.RightArm, false);
            Set(mask, AvatarMaskBodyPart.LeftFingers, false);
            Set(mask, AvatarMaskBodyPart.RightFingers, false);
            Set(mask, AvatarMaskBodyPart.LeftHandIK, false);
            Set(mask, AvatarMaskBodyPart.RightHandIK, false);

            if (isNew) AssetDatabase.CreateAsset(mask, MaskPath);
            else EditorUtility.SetDirty(mask);
            return mask;
        }

        static void Set(AvatarMask m, AvatarMaskBodyPart part, bool on)
        {
            if ((int)part < (int)AvatarMaskBodyPart.LastBodyPart)
                m.SetHumanoidBodyPartActive(part, on);
        }

        /// <summary>Crouch katmani olan controller'i bul (isim degisirse de calissin).</summary>
        static AnimatorController FindIdleController()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AnimatorController"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(p);
                if (c == null) continue;
                foreach (var l in c.layers)
                    if (l.name == LayerName) return c;
            }
            return null;
        }
    }
}
