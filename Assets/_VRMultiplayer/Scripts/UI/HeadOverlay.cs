using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Kafanin onune kilitlenen tam-ekran efektlerin (korluk, hasar flasi, vinyet) MESAFESI.
    /// Hepsi ayni yerden hesaplansin diye tek noktada toplandi.
    ///
    /// SORUN NEYDI: efektler 0.48 / 0.50 / 0.52 m'ye sabitlenmisti. Durbun mercegi ise goz
    /// mercege 0.45 m'den yakinsa aciliyor (WeaponScope.eyeDistance) — yani mercek HER ZAMAN
    /// bu ucunun de ONUNDE kaliyor ve uzerlerine ciziyordu. Sonuc: durbune bakarken flash
    /// seni kor etmiyor, hasar yonu flasi gorunmuyor, dusuk can vinyeti gorunmuyordu.
    /// Oyuncu durbune yanasarak butun ekran efektlerinden muaf hale geliyordu.
    ///
    /// COZUM: efektleri merceğin ULASAMAYACAGI kadar yakina almak. Sabit sayi yazmak yerine
    /// kameranin yakin kesme duzleminden hesapliyoruz — VR'da bu deger cihaza/ayara gore
    /// degisir, sabit bir sayi bazi kurulumlarda kesilip gorunmez olurdu.
    ///
    /// YAN FAYDA: 0.48 m'de efektler silahin ARKASINDA kaliyordu, yani flash yerken silahini
    /// hala goruyordun. Artik onun da onunde.
    /// </summary>
    public static class HeadOverlay
    {
        /// <summary>Kamera yakin duzlemi okunamazsa varsayilan (VR'da tipik deger).</summary>
        const float FallbackNear = 0.05f;

        /// <summary>Katmanlar arasi mesafe — ust uste binince z-fighting olmasin diye.</summary>
        const float LayerGap = 0.01f;

        /// <summary>Katman sirasi — kucuk mesafe = goze yakin = daha ONDE:
        /// <see cref="Veil"/> 0, <see cref="Blind"/> 1, <see cref="DamageFlash"/> 2,
        /// <see cref="Vignette"/> 3.</summary>
        public const int Veil = 0;        // olum/bekleme perdesi: hepsinin onunde
        public const int Blind = 1;       // flash korlugu
        public const int DamageFlash = 2; // hasar yonu flasi
        public const int Vignette = 3;    // dusuk can vinyeti

        public static float Distance(int layer)
        {
            var cam = Camera.main;
            float near = cam != null ? cam.nearClipPlane : FallbackNear;

            // 1.6 kat pay: tam yakin duzleme koyarsak kirpilma riski var. 0.12 taban ise
            // yakin duzlemi cok kucuk olan kurulumlarda quad'in burnuna yapismasini onler.
            float d = Mathf.Max(near * 1.6f, 0.12f);
            return d + layer * LayerGap;
        }
    }
}
