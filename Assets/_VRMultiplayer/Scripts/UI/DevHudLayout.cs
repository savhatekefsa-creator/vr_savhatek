using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// PC ekranindaki IMGUI panelleri icin ust uste binmeyen yerlesim.
    ///
    /// NEDEN VAR: paneller ekran dikdortgenlerini SABIT yaziyordu ve bircogu ayni noktaya
    /// dusuyordu — LanBootstrap sunucu paneli (20,20), ServerView oyuncu listesi (20,20),
    /// PlayerEntryUI (20,120), CharacterSelectUI (20,120)... Ayni anda birden fazlasi acik
    /// oldugunda yazilar ic ice geciyor ve hicbiri okunmuyordu (sahada goruldu).
    ///
    /// NASIL: panel dikdortgenini kendisi yazmak yerine buradan ISTER. Sutun basina bir imlec
    /// tutulur, her istek imleci asagi kaydirir. Cizilmeyen panel yer kaplamaz — kosul
    /// disariда kaldigi icin istek de gelmez. Imlecler her karede (Event.current sayisi degil,
    /// Time.frameCount) bir kez sifirlanir.
    ///
    /// SIRA GARANTISI YOK: OnGUI cagri sirasi bilesenler arasinda tanimsizdir, yani panellerin
    /// dikey sirasi kareler arasinda degisebilir. Amac sira degil, CAKISMAMA.
    ///
    /// Yalnizca gelistirici/PC HUD'u icindir; VR icindeki dunya-uzayi arayuz (UITheme) bundan
    /// etkilenmez.
    /// </summary>
    public static class DevHudLayout
    {
        const float Margin = 20f;
        const float Gap = 8f;

        static int _frame = -1;
        static EventType _evt = EventType.Ignore;
        static float _left, _right;

        /// <summary>Imleci her OLAY GECISINDE sifirlar.
        ///
        /// DIKKAT — ILK SURUMDEKI HATA: yalnizca Time.frameCount degisince sifirlaniyordu.
        /// Ama OnGUI kare basina BIRDEN COK kez kosar: once Layout, sonra Repaint, ayrica her
        /// girdi olayi (fare hareketi, tekerlek, tus) icin ayri bir gecis. Ilk gecis sifirliyor,
        /// sonraki gecisler imleci asagi itmeye devam ediyordu; sonuc, panellerin kare icinde
        /// asagi kaymasi ve fare hareket ettikce (olay sayisi arttikca) kaymanin artmasiydi —
        /// sahada "UI gidip geliyor" diye goruldu.
        ///
        /// Kare + olay turu birlikte anahtar: Layout ve Repaint gecisleri ayni cagri sirasini
        /// izledigi icin ikisi de AYNI dikdortgenleri uretir, yani yerlesim kararli kalir.</summary>
        static void Sync()
        {
            EventType e = Event.current != null ? Event.current.type : EventType.Ignore;
            if (_frame == Time.frameCount && _evt == e) return;
            _frame = Time.frameCount;
            _evt = e;
            _left = Margin;
            _right = Margin;
        }

        /// <summary>Sol sutunda bir sonraki bos yer.</summary>
        public static Rect Left(float width, float height)
        {
            Sync();
            var r = new Rect(Margin, _left, width, height);
            _left += height + Gap;
            return r;
        }

        /// <summary>Sag sutunda bir sonraki bos yer.</summary>
        public static Rect Right(float width, float height)
        {
            Sync();
            var r = new Rect(Screen.width - width - Margin, _right, width, height);
            _right += height + Gap;
            return r;
        }

        /// <summary>Sol sutunun ALTINDAN yukari dogru (ekranin dibine sabitlenen paneller).</summary>
        public static Rect BottomLeft(float width, float height)
        {
            Sync();
            return new Rect(Margin, Screen.height - height - Margin, width, height);
        }
    }
}
