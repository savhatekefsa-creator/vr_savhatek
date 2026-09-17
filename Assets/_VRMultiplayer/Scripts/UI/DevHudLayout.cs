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
    /// disarida kaldigi icin istek de gelmez. Imlecler her karede bir kez sifirlanir.
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

        // Sutun imlecleri: Y = sutun icindeki bir sonraki bos yer, X = sutunun kaydirmasi,
        // W = o sutundaki en genis panel (bir sonraki sutunun nereden baslayacagini belirler).
        static float _leftY, _leftX, _leftW;
        static float _rightY, _rightX, _rightW;

        // Alt bant: ekranin dibinden YUKARI dogru yigilir.
        static float _bottomY;
        // Alt bandin bir onceki gecisteki toplam yuksekligi. Left() bunu rezerve eder.
        static float _bottomUsed, _bottomReserved;

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

            // Alt bandin BU gecisteki yuksekligini daha bilmiyoruz (OnGUI sirasi tanimsiz),
            // bu yuzden bir onceki gecisin olcusunu rezerve ediyoruz. Paneller kareler
            // arasinda ayni boyda kaldigi icin bu pratikte tam dogru; degisirse tek karelik
            // bir cakisma olur ve ertesi karede kendini duzeltir.
            _bottomReserved = _bottomUsed;
            _bottomUsed = 0f;

            _leftY = Margin; _leftX = Margin; _leftW = 0f;
            _rightY = Margin; _rightX = 0f; _rightW = 0f;
            _bottomY = 0f;
        }

        /// <summary>Sol sutunda bir sonraki bos yer. Sutun ekranin dibine (veya alt bandin
        /// ustune) dayandiginda YENI SUTUNA gecer — tasip ekran disinda kaybolmaz.</summary>
        public static Rect Left(float width, float height)
        {
            Sync();

            // Alt bant yalnizca ILK sutunla ayni x'te durur; sonraki sutunlar onun sagindadir
            // ve tam yuksekligi kullanabilir.
            float limit = Screen.height - Margin - (_leftX <= Margin ? _bottomReserved : 0f);

            // Sutun basindaki tek panel limitten uzunsa yine de buraya konur: sonsuz sutun
            // acmanin alemi yok, kirpilmasi ekran disina tasmasindan iyi.
            if (_leftY > Margin && _leftY + height > limit)
            {
                _leftX += _leftW + Gap;
                _leftY = Margin;
                _leftW = 0f;
            }

            var r = new Rect(_leftX, _leftY, width, height);
            _leftY += height + Gap;
            if (width > _leftW) _leftW = width;
            return r;
        }

        /// <summary>Sag sutunda bir sonraki bos yer. Dolunca SOLA dogru yeni sutun acar.</summary>
        public static Rect Right(float width, float height)
        {
            Sync();

            if (_rightY > Margin && _rightY + height > Screen.height - Margin)
            {
                _rightX += _rightW + Gap;
                _rightY = Margin;
                _rightW = 0f;
            }

            var r = new Rect(Screen.width - width - Margin - _rightX, _rightY, width, height);
            _rightY += height + Gap;
            if (width > _rightW) _rightW = width;
            return r;
        }

        /// <summary>Ekranin dibine sabitlenen paneller; yukari dogru yigilir.
        ///
        /// ONCEDEN HATALIYDI: sabit bir dikdortgen donduruyor, ne kendi imlecini ilerletiyor
        /// ne de Left()'in nereye kadar indigini biliyordu. Sinifin butun amaci cakismayi
        /// onlemekken bu fonksiyon mekanizmanin DISINDA kaliyordu: ServerView (320 yuksek)
        /// tek basina sol sutunu alt banda kadar indiriyor, uzerine MatchGui gelince
        /// WeaponGripCaptureTool'un 360'lik paneliyle ic ice giriyordu — ikisi de okunmuyordu
        /// (videoda goruldu). Artik hem kendi arasinda yigiliyor hem de Left()'e yer birakiyor.</summary>
        public static Rect BottomLeft(float width, float height)
        {
            Sync();

            float y = Screen.height - Margin - _bottomY - height;
            _bottomY += height + Gap;
            _bottomUsed = _bottomY;
            return new Rect(Margin, y, width, height);
        }
    }
}
