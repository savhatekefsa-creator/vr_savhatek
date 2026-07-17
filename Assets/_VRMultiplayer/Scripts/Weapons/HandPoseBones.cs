using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Authored parmak pozunun eklem SIRASI — poz yazan editor araci ile pozu uygulayan runtime
    /// arasindaki tek sozlesme. Iki taraf da BURAYI kullanmali: siralar ayrisirsa parmaklar
    /// sessizce birbirine karisir (basparmak pozu isaret parmagina yazilir vb.) ve bu hatanin
    /// derleme zamaninda yakalanmasi imkansizdir.
    ///
    /// Sira: 5 parmak x 3 bogum = 15. Parmaklar Thumb, Index, Middle, Ring, Little; her parmakta
    /// Proximal, Intermediate, Distal.
    /// </summary>
    public static class HandPoseBones
    {
        public const int JointCount = 15;

        /// <summary>Isaret parmaginin ilk ekleminin indeksi (basparmak 0,1,2 aldigi icin 3).</summary>
        public const int IndexFirst = 3;
        public const int IndexJointCount = 3;

        /// <summary>Bir eklemin humanoid kemigi. Unity'nin HumanBodyBones enum'unda parmak
        /// kemikleri BITISIK ve iki elde ayni sirada: LeftThumbProximal(24)..LeftLittleDistal(38),
        /// RightThumbProximal(39)..RightLittleDistal(53) — yani taban + offset yeterli.</summary>
        public static HumanBodyBones Bone(int joint, bool left) =>
            (HumanBodyBones)((int)(left ? HumanBodyBones.LeftThumbProximal
                                        : HumanBodyBones.RightThumbProximal) + joint);

        public static bool IsIndex(int joint) => joint >= IndexFirst && joint < IndexFirst + IndexJointCount;

        /// <summary>Editor arayuzunde gosterilen isimler (JointCount ile ayni sirada).</summary>
        public static readonly string[] JointNames =
        {
            "Basparmak / Proximal", "Basparmak / Intermediate", "Basparmak / Distal",
            "Isaret / Proximal",    "Isaret / Intermediate",    "Isaret / Distal",
            "Orta / Proximal",      "Orta / Intermediate",      "Orta / Distal",
            "Yuzuk / Proximal",     "Yuzuk / Intermediate",     "Yuzuk / Distal",
            "Serce / Proximal",     "Serce / Intermediate",     "Serce / Distal",
        };
    }
}
