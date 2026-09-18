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

        // Ust bant: ekranin UST-ORTASINDA, sutunlarin ERISEMEYECEGI ayri bir serit.
        // Alt bantla ayni "onceki gecisi rezerve et" numarasi (bkz. Sync).
        static float _topUsed, _topReserved;

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
            _topReserved = _topUsed;
            _topUsed = 0f;

            float top = Margin + _topReserved;
            _leftY = top; _leftX = Margin; _leftW = 0f;
            _rightY = top; _rightX = 0f; _rightW = 0f;
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
            if (_leftY > Margin + _topReserved && _leftY + height > limit)
            {
                _leftX += _leftW + Gap;
                _leftY = Margin + _topReserved;
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

            if (_rightY > Margin + _topReserved && _rightY + height > Screen.height - Margin)
            {
                _rightX += _rightW + Gap;
                _rightY = Margin + _topReserved;
                _rightW = 0f;
            }

            var r = new Rect(Screen.width - width - Margin - _rightX, _rightY, width, height);
            _rightY += height + Gap;
            if (width > _rightW) _rightW = width;
            return r;
        }

        /// <summary>UST BANT: ekranin ust-ortasinda, HICBIR sutunun giremedigi serit.
        ///
        /// NEDEN VAR: mod secme kutusu (ModeSelectUI) ekrani elle ortalayan sabit bir
        /// dikdortgen kullaniyordu. Gerekcesi "gelistirici kutulari SOL ve SAG kenara
        /// yapisik, orta serbest" idi — ama sutunlar dolunca ortaya dogru yeni sutun acmaya
        /// baslayinca o varsayim coktu: sunucu mac paneli kutunun tam ustune bindi ve
        /// "Bir mod sec", "AKTIF", "ISINMA ..." satirlari ic ice gecti (videoda goruldu,
        /// 637 saniyenin tamaminda).
        ///
        /// Cozum kutuyu baska bir bos koseye tasimak DEGIL — o da bir sonraki panelde yine
        /// bozulurdu. Serit ARTIK REZERVE: sutunlar bu bandin altindan basliyor, yani
        /// ust-orta kutunun uzerine yapisal olarak binemiyorlar.</summary>
        public static Rect TopBanner(float width, float height)
        {
            Sync();
            var r = new Rect((Screen.width - width) * 0.5f, Margin + _topUsed, width, height);
            _topUsed += height + Gap;

            // AYNI GECISTE DE YER AC. Alt bant gibi yalnizca "onceki gecisi rezerve et"
            // deseydik, bant ilk belirdigi karede sutunlar hala yukaridan baslar ve o tek
            // karede uzerine binerdi. OnGUI cagri sirasi tanimsiz oldugu icin bunu tamamen
            // bitiremeyiz, ama TopBanner sutunlardan ONCE kostugunda (vakalarin yarisi)
            // cakisma ILK kareden itibaren olmaz: henuz panel almamis sutunlari simdi itiyoruz.
            // Sonra kostugunda bir sonraki gecis _topReserved ile zaten dogruyu bulur.
            if (_topUsed > _topReserved)
            {
                float top = Margin + _topUsed;
                if (_leftY <= Margin + _topReserved) _leftY = top;
                if (_rightY <= Margin + _topReserved) _rightY = top;
                _topReserved = _topUsed;
            }
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
