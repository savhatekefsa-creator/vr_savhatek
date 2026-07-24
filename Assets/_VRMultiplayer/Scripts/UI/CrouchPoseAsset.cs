using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// ELLE YAZILMIS COMELME POZU. Sahnede avatarin kemiklerini elinle cevirip menu 44 ile
    /// yakalanir — silah tutuslarindaki (WeaponGripCaptureTool) ayni is akisi.
    ///
    /// NEDEN IK DEGIL: analitik bacak IK'si iki denemede de kotu sonuc verdi (bacaklar 360
    /// derece dondu, ayak zeminin altina girdi). Sebebi kutup adiminin 180 derecede belirsiz
    /// kalmasi. Elle yazilmis poz ile iki gecerli poz arasinda slerp yapiyoruz — matematiksel
    /// olarak sonuc HER ZAMAN gecerli bir poz, surpriz imkansiz.
    ///
    /// Bedeli: poz tek bir derinlik icin yazildigi icin arada kalan derinliklerde ayak zeminden
    /// birkac santim kayabilir. Bilincli takas: IK'nin verdigi felaketten iyidir. Ileride birden
    /// fazla poz eklenip derinlige gore harmanlanabilir.
    /// </summary>
    [CreateAssetMenu(fileName = "ComelmePozu", menuName = "VR Multiplayer/Comelme Pozu")]
    public class CrouchPoseAsset : ScriptableObject
    {
        [Tooltip("Bu poz, oyuncu ayakta boyunun bu ORANINA indiginde TAM uygulanir. " +
                 "0.65 = boyunun %65'ine cokunce poz tamamen devrede. Kucultursen poz daha " +
                 "derin comelmede devreye girer, buyutursen daha erken.")]
        [Range(0.35f, 0.95f)] public float appliesAtHeightRatio = 0.65f;

        [Tooltip("Pozun devreye girmeye BASLADIGI oran. Bunun ustunde oyuncu ayakta sayilir.")]
        [Range(0.80f, 1.0f)] public float startsAtHeightRatio = 0.94f;

        [System.Serializable]
        public struct BonePose
        {
            public HumanBodyBones bone;
            public Quaternion rotation; // kemigin LOKAL donusu
        }

        [Tooltip("Yakalanan kemik donusleri. Menu 44 dolduruyor, elle duzenlemek gerekmez.")]
        public BonePose[] bones;
    }
}
