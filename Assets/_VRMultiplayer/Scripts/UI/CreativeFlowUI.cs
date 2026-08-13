using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// YARATICI MODUN AKISI: once KALIBRASYON, sonra ANA MENU, oradan editor ya da havuz.
    ///
    /// SIRA NEDEN BOYLE: harita kalibre cercevede oruluyor. Kalibre olmadan konan bir duvar
    /// kalibre olan herkeste baska yere duser, ve kalibrasyon sonradan yapilirsa rig donup
    /// kaydigi icin zemin oyuncunun altindan cikar. Ayni gerekce agi da kapsiyor: haritalar
    /// PC'de yasadigi icin baglanti da kalibrasyondan SONRA aciliyor (bkz. LanBootstrap).
    ///
    /// MENU HER ISLEM SONU GERI GELIR. Sema "tum dallar bu menuye doner" diyor; burada bu bir
    /// DURUM kuralindan cikiyor, geri donus cagrilarindan degil: yaratici moddayken, kalibre
    /// olmusken ve insa modu KAPALIYKEN menu acik olur. Editorden cikan kendiliginden menuye
    /// duser, cikis yolunu ayrica kimsenin cagirmasi gerekmez.
    ///
    /// MOD DEGISINCE KENDINI TOPLAR. Serit 3'un uyari ekrani "yaratici moda gec" diyecek ve
    /// Serit 4 mac sonunda ana menuye donecek; moda ozel her ekran kendi kapanisindan sorumlu
    /// olmazsa o donuslerde paneller havada kalir.
    /// </summary>
    public class CreativeFlowUI : MonoBehaviour
    {
        const float Distance = 1.4f, HeightDrop = 0.12f, TiltDegrees = 8f;
        const float RecenterAngle = 35f, RecenterSpeed = 6f;

        CreativeMenuPanel _menu;
        MapListPanel _list;
        ConfirmPanel _confirm;
        NameEntryPanel _name;
        VRPointer _pointer;
        CalibrationManager _calibration;
        ConstructorPlacer _placer;

        MapActionsPanel _actions;

        bool _placed, _recentering;
        bool _calibrationAsked;
        bool _wasEditing;

        // Ayni liste iki isi goruyor: harita ACMA ve harita YONETME. Secilen satirin ne
        // yapacagini bu belirliyor (bkz. OnMapPicked).
        bool _managing;

        /// <summary>
        /// TAG KURULUMU, yalnizca YENI harita akisinda. Sifirdan bir mekan kuruluyor demek
        /// tag'lerin de o mekanda tanimlanmasi demek; ayri bir menu maddesi olsaydi
        /// unutulabilirdi ve tag'siz harita kalibre EDILEMEYEN haritadir.
        ///
        /// Plaka koyma adimi ayri bir yerlestirme sistemi DEGIL, insa modunun kendisi:
        /// palet zaten TagIsaret'i veriyor ve ikinci bir yerlestirici yazmak, izgara
        /// kurallarini ikinci kez uygulamak olurdu.
        /// </summary>
        enum TagStep { Yok, Plaka }
        TagStep _tagStep = TagStep.Yok;

        /// <summary>Harita listesi su an TAG KAYNAGI secmek icin acik (bkz. OnMapPicked).</summary>
        bool _pickingTagSource;

        // Aboneligi AppMode.ResetStatics temizler (domain reload kapaliyken abonelik listesi
        // oyunlar arasi tasinir ve ikinci Play'de IKI akis dogardi — PlayerEntryUI'daki ders).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() => AppMode.Chosen += OnModeChosen;

        static void OnModeChosen(AppMode.Mode m)
        {
            if (m != AppMode.Mode.Creative) return;
            var go = new GameObject("~CreativeFlowUI");
            DontDestroyOnLoad(go);
            go.AddComponent<CreativeFlowUI>();
        }

        void Update()
        {
            // Mod degisti: her seyi topla. (Kendini yok etmek, panelleri de goturur.)
            if (!AppMode.IsCreative) { Destroy(gameObject); return; }

            // 1) KALIBRASYON. Bitene kadar hicbir panel acmiyoruz: kalibrasyon paneli de
            //    kafanin onunde duruyor, ikisi ust uste biner.
            if (!EnsureCalibrated()) { CloseAll(); return; }

            // 2) INSA MODU ACIKKEN MENU YOK. Editor ekranin sahibi; menu, editorden cikilinca
            //    kendiliginden geri gelir.
            if (Placer != null && Placer.BuildMode)
            {
                _wasEditing = true;
                CloseAll();
                return;
            }

            // 3) EDITORDEN YENI CIKILDI: karar zinciri (Kaydet? -> isim -> havuz | at).
            if (_wasEditing)
            {
                _wasEditing = false;
                BeginExitFlow();
            }

            // 4) Acik olan ekrani sur — sirasi onemli: karar ekranlari menunun onunde.
            // Menu yalnizca onde baska ekran yokken acilir.
            if (_confirm == null && _name == null && _actions == null && _list == null && _menu == null)
                OpenMenu();

            ShowOnlyTop();

            if (_confirm != null) { Place(_confirm.transform); _confirm.Tick(_pointer); return; }
            if (_name != null)    { Place(_name.transform);    _name.Tick(_pointer);    return; }
            if (_actions != null) { Place(_actions.transform); _actions.Tick(_pointer); return; }
            if (_list != null)    { Place(_list.transform);    _list.Tick(_pointer);    return; }
            if (_menu != null)    { Place(_menu.transform);    _menu.Tick(_pointer); }
        }

        /// <summary>
        /// AYNI ANDA TEK EKRAN GORUNUR.
        ///
        /// Butun paneller ayni yere yerlesiyor — kafanin 1.4 m onune — cunku okunabilir olan
        /// yer orasi. Ikisi birden acik kalinca ust uste binip ikisi de okunamaz hale geliyordu:
        /// isim klavyesi harita listesinin, silme onayi da altindakinin uzerine cikiyordu.
        ///
        /// ARKADAKI YOK EDILMEZ, GIZLENIR: geri donuldugunde listenin SAYFASI ve menunun durumu
        /// yeniden kurulmasin. Liste gizliyken kacirdigi degisiklikleri gorununce topluyor
        /// (bkz. MapListPanel.OnEnable).
        /// </summary>
        void ShowOnlyTop()
        {
            Transform top = null;
            if (_confirm != null)      top = _confirm.transform;
            else if (_name != null)    top = _name.transform;
            else if (_actions != null) top = _actions.transform;
            else if (_list != null)    top = _list.transform;
            else if (_menu != null)    top = _menu.transform;

            Toggle(_confirm != null ? _confirm.transform : null, top);
            Toggle(_name    != null ? _name.transform    : null, top);
            Toggle(_actions != null ? _actions.transform : null, top);
            Toggle(_list    != null ? _list.transform    : null, top);
            Toggle(_menu    != null ? _menu.transform    : null, top);
        }

        static void Toggle(Transform t, Transform top)
        {
            if (t == null) return;
            bool on = t == top;
            if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }

        void OnDestroy()
        {
            // Askiyi BIRAKARAK cik: yaratici mod disinda otomatik kayit yine calismali.
            ConstructorSession.AutoSaveSuspended = false;
            CloseAll();
        }

        // ------------------------------------------------------------- kalibrasyon

        bool EnsureCalibrated()
        {
            if (CalibrationManager.Calibrated) return true;

            // Masaustunde (kulaklik yok) kalibrasyon yapilamaz — akisi orada kilitlemek
            // Editor'de hicbir seyi denenemez hale getirirdi.
            if (!XRSettings.isDeviceActive) return true;

            if (_calibration == null) _calibration = FindFirstObjectByType<CalibrationManager>();
            if (_calibration == null || !_calibration.enabled) return true;

            if (!_calibrationAsked)
            {
                _calibrationAsked = true;
                _calibration.Begin("Kalibrasyon bitince HARITA MENUSU acilir.");
            }
            return false;
        }

        // ------------------------------------------------------------- menu / liste

        void OpenMenu()
        {
            EnsurePointer();

            // KATALOGU SIMDIDEN ISTE. Gozlukte liste ag uzerinden geliyor ve "yeni harita"
            // secilir secilmez tag kaynagi sorusu icin gerekiyor. Menu acilirken istemek,
            // oyuncu karar verene kadar cevabin gelmis olmasini sagliyor; beklemeyi
            // BeginTagSetupRoutine yine de yapiyor ama pratikte hic beklemiyor.
            MapCatalog.Refresh();

            var go = new GameObject("Creative Menu Panel");
            go.transform.SetParent(transform, false);
            _menu = go.AddComponent<CreativeMenuPanel>();
            _menu.Selected += OnMenuChoice;

            _placed = false;
            _recentering = false;
        }

        void OnMenuChoice(CreativeMenuPanel.Choice c)
        {
            switch (c)
            {
                case CreativeMenuPanel.Choice.NewMap:
                    CloseMenu();
                    StartNewMap();
                    break;

                case CreativeMenuPanel.Choice.ExistingMap:
                    CloseMenu();
                    OpenList();
                    break;

                case CreativeMenuPanel.Choice.ManagePool:
                    CloseMenu();
                    _managing = true;
                    OpenList();
                    break;
            }
        }

        void OpenList()
        {
            EnsurePointer();

            // Listeyi ISTE: gozlukte kaynak sunucudur ve cevap bir kare sonra gelebilir.
            // Panel MapCatalog.Changed'e abone, veri gelince kendini yeniden cizer.
            MapCatalog.Refresh();

            var go = new GameObject("Map List Panel");
            go.transform.SetParent(transform, false);
            _list = go.AddComponent<MapListPanel>();
            _list.SetTitle(_pickingTagSource ? "TAG'LERİ HANGİ HARİTADAN ALAYIM?"
                         : _managing ? "HARİTA YÖNETİCİSİ" : "KAYITLI HARİTALAR");
            _list.Picked += OnMapPicked;
            _list.Back += () =>
            {
                _managing = false;
                CloseList();
                // Tag kaynagi secmekten VAZGECILDI: akis yarida kalmasin, plaka yoluna dus.
                if (_pickingTagSource) { _pickingTagSource = false; AskPlateSetup(); }
            };

            _placed = false;
            _recentering = false;
        }

        void OnMapPicked(string mapName)
        {
            // TAG KAYNAGI SECIMI: satir bir harita ACMAZ, yalnizca tag yerlesimini verir.
            // Acik olan yeni harita oldugu gibi kaliyor.
            if (_pickingTagSource)
            {
                _pickingTagSource = false;
                CloseList();
                CopyTagsFrom(mapName);
                return;
            }

            // YONETICI AKISINDA liste kapanmaz mantigi degisir: satir bir haritayi ACMAZ,
            // o haritanin islemlerini acar. "Ekleme/cikarma sinirsiz tekrarlanabilir" kurali
            // bundan cikiyor — islem bitince listeye geri donuluyor, menuye degil.
            if (_managing) { OpenActions(mapName); return; }

            CloseList();

            var s = ConstructorSession.Instance;
            if (s == null) return;

            // HARITAYI SUNUCU ACAR. Dosyalar PC'de; gozlukte diski okumak bos klasore bakip
            // "bulunamadi" demek olurdu — liste sunucudan geldigi icin harita ekranda gorunup
            // acilmiyordu. Gelen layout yerel oturumun uzerine yaziliyor.
            if (!ConstructorSession.IsMapAuthority)
            {
                if (!ConstructorSync.ClientRequestOpen(mapName))
                {
                    StartCoroutine(Note("SUNUCUYA BAĞLI DEĞİL\n\nHaritalar PC'de tutuluyor.", 5f));
                    return;
                }
                EnterEditor();
                return;
            }

            if (!s.OpenExisting(mapName))
            {
                StartCoroutine(Note("AÇILAMADI\n\n" + s.NotStartedReason, 4f));
                return;
            }
            EnterEditor();
        }

        /// <summary>
        /// "YENI" secildi: once DUNYA sorulur, sonra harita acilir.
        ///
        /// SORU HARITADAN ONCE. Tema, olusturma isteginin bir parcasi olarak gidiyor
        /// (<see cref="ConstructorSync.ClientRequestNewMap"/>); once acip sonra sormak,
        /// oyuncuyu bir kac saniyeligine yanlis dunyada birakmak ve gozluk istemciyken
        /// sunucunun yayini ile yaris etmek demekti.
        ///
        /// KUTUPHANE BOSSA HIC SORULMAZ: cevabi olmayan bir soru, akisa eklenmis bir tik
        /// sesinden baska bir sey degil.
        /// </summary>
        void StartNewMap()
        {
            var themes = ThemeLibrary.Instance;
            if (themes.Count == 0) { CreateNewMap(""); return; }

            // ONAY PANELI IKI SECENEK TASIYOR. Bugun kutuphanede tek tema var ve soru
            // "o dunya mi, duz mu" olarak tam oturuyor. Ikinci tema geldiginde bu panel
            // yetmez — sessizce ilkini secmek yerine burada yuksek sesle sikayet ediyoruz.
            if (themes.Count > 1)
                Debug.LogWarning($"[CreativeFlow] Kutuphanede {themes.Count} tema var ama yeni " +
                                 "harita sorusu iki secenek gosterebiliyor; yalnizca ilki " +
                                 "sunuluyor. Liste paneli gerekiyor.");

            var pick = themes.themes[0];
            string label = string.IsNullOrEmpty(pick.displayName) ? pick.id : pick.displayName;

            OpenConfirm("YENİ HARİTA", "Hangi dünyada kurulsun?",
                label, "NORMAL", UITheme.AccentCyan,
                yes => CreateNewMap(yes ? pick.id : ""));
        }

        void CreateNewMap(string themeId)
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            // YENI HARITAYI DA SUNUCU ACAR. Gozluk kendi bos oturumunu acsaydi sunucu ESKI
            // haritada kalirdi ve sonraki her yerlestirme oraya islenirdi — sessiz ve en kotu
            // turden bir uyusmazlik.
            if (!ConstructorSession.IsMapAuthority)
            {
                if (!ConstructorSync.ClientRequestNewMap(themeId))
                {
                    StartCoroutine(Note("SUNUCUYA BAĞLI DEĞİL\n\nHaritalar PC'de tutuluyor.", 5f));
                    return;
                }
                BeginTagSetup();
                return;
            }

            // Bos zemin: "yeni harita" sifirdan tasarim demek, oda taramasina bagli degil
            // (bkz. ConstructorSession.OpenNew). Isim ilk kayitta sorulacak.
            if (!s.OpenNew())
            {
                StartCoroutine(Note("YENİ HARİTA AÇILAMADI\n\nConsole'a bak.", 4f));
                return;
            }

            // Yetkili taraf: OpenNew duzeni temasiz kurdu, temayi hemen ustune yaziyoruz.
            // SetTheme haritayi da isaretliyor, yani secim kayitla birlikte kaliciasiyor.
            //
            // TAG KURULUMUNDAN ONCE. O akis kendi panellerini aciyor ve editore ancak
            // sonunda giriliyor; temayi oraya birakmak, haritanin dunyasinin birkac ekran
            // boyunca belirsiz kalmasi demekti.
            if (!string.IsNullOrEmpty(themeId)) s.SetTheme(themeId);

            BeginTagSetup();
        }

        // ------------------------------------------------------------- tag kurulumu (yeni harita)
        //
        // SIRA: OpenNew ONCE calisir, tag kurulumu SONRA. Plakalar haritanin icindeki proplar,
        // yani yerlestirmek icin acik bir oturum ve izgara gerekiyor. OpenNew diske hicbir sey
        // yazmadigi ve isimsiz actigi icin bu sira kullaniciya gorunmez.

        /// <summary>Tag tanimli kac harita var — secenegin sunulup sunulmayacagini belirler.</summary>
        static int TagliHaritaSayisi()
        {
            int n = 0;
            foreach (var e in MapCatalog.All)
                if (e != null && e.tagCount > 0) n++;
            return n;
        }

        /// <summary>Katalogun gozluge ulasmasi icin beklenecek en fazla sure (sn).</summary>
        const float CatalogWaitSeconds = 1.5f;

        void BeginTagSetup() => StartCoroutine(BeginTagSetupRoutine());

        IEnumerator BeginTagSetupRoutine()
        {
            // KATALOG GOZLUKTE ANINDA GELMIYOR. MapCatalog.Refresh istemcide yalnizca ISTEK
            // yolluyor, liste cevap gelince doluyor (MapCatalog.Refresh: "istemci kendi
            // diskine bakmaz"). Senkron sormak secenegin cihazda HIC cikmamasina yol
            // aciyordu -- sahada goruldu: "yeni haritadan sonra sadece BASLA/ATLA vardi".
            MapCatalog.Refresh();
            if (!ConstructorSession.IsMapAuthority)
            {
                float bitis = Time.time + CatalogWaitSeconds;
                while (Time.time < bitis && MapCatalog.All.Count == 0) yield return null;
            }

            // BILMIYORSAK SORUYORUZ. Katalog hala bossa bu "harita yok" demek degil, "cevap
            // gelmedi" demek olabilir; secenegi gizlemek onu sessizce kaybettirir. Liste bos
            // cikarsa GERI tusu plaka yoluna dusuruyor, yani cikmaz sokak degil.
            bool biliyoruz = ConstructorSession.IsMapAuthority || MapCatalog.All.Count > 0;

            // AYNI MEKANDA IKINCI HARITA. Kagitlar duvarda oldugu icin plaka koymak yanlis
            // yol: plaka kagidi kovalamak zorunda kalir ve 6,25 cm'lik izgaraya oturamaz
            // (olculdu: ayni fiziksel tag icin 6,1 cm fark). Once bunu soruyoruz, cunku
            // "yeniden kur" secilirse geri donusu olmayan fiziksel is basliyor.
            //
            // KAYNAK HARITAYI KENDIMIZ SECMIYORUZ. Ilk yazilan hali "en son kaydedilen"i
            // otomatik oneriyordu; denemede yanlis haritayi sectI, cunku havuz ayari topluca
            // yapilinca uc haritanin savedAt'i ayni saniyeye dustu. Yanlis mekanin tag'lerini
            // sessizce kullanmak, bu sistemde bulabilecegimiz en kotu hata — bir tus fazla
            // basmak buna degmez. Hangi haritanin kagitlarinin duvarda oldugunu bilen kisi
            // zaten odadaki kisi.
            if (!biliyoruz || TagliHaritaSayisi() > 0)
            {
                OpenConfirm("TAG'LER ZATEN VAR MI?",
                    "Bu mekânda daha önce tag kurduysan onları kullan —\n" +
                    "kâğıtlar duvarda durduğu sürece plaka koymana gerek yok\n" +
                    "ve değerler daha doğru olur.\n\n" +
                    "Başka bir mekândaysan YENİDEN KUR.",
                    "HARİTADAN AL", "YENİDEN KUR", UITheme.AccentCyan, al =>
                {
                    if (al) { _pickingTagSource = true; OpenList(); }
                    else AskPlateSetup();
                });
                yield break;
            }
            AskPlateSetup();
        }

        /// <summary>Tag'leri var olan bir haritadan al; plaka adimi hic calismaz.</summary>
        void CopyTagsFrom(string sourceMap)
        {
            _tagStep = TagStep.Yok;

            if (ConstructorSession.IsMapAuthority)
            {
                string rapor = ConstructorSync.HostCopyTags(sourceMap, out bool ok);
                if (!ok)
                {
                    OpenConfirm("KOPYALANAMADI", rapor, "PLAKA KOY", "VAZGEÇ",
                        UITheme.TeamRedEdge, plaka => { if (plaka) AskPlateSetup(); else EnterEditor(); });
                    return;
                }
                StartCoroutine(Note("TAG'LER KOPYALANDI\n\n" + rapor, 6f));
            }
            else if (!ConstructorSync.ClientRequestCopyTags(sourceMap))
            {
                StartCoroutine(Note("SUNUCUYA BAĞLI DEĞİL\n\nTag'ler PC'de tutuluyor.", 5f));
                return;
            }

            // Tag'ler hazir: isimlendirip insaya gec. Plaka adimi hic acilmiyor.
            OpenName("HARİTA ADI", null,
                ad => { DoSave(ad); EnterEditor(); },
                () => EnterEditor());
        }

        void AskPlateSetup()
        {
            OpenConfirm("TAG KURULUMU",
                "Yeni mekân: önce AprilTag'leri yerleştirelim.\n\n" +
                "Plakayı duvara koy, BEYAZ yüzü odaya baksın; yanındaki\n" +
                "kâğıdı aynı anda tam o noktaya yapıştırsın.\n\n" +
                "Bitince inşa modundan çık — tag'ler otomatik kaydedilir.",
                "BAŞLA", "ATLA", UITheme.AccentCyan, basla =>
            {
                // ATLA gecerli bir secim: kagitlar elde degilse tag kurulumunu simdi
                // yapmaya zorlamak, bos plakalar konmasina ve yanlis yerlesime yol acardi.
                // Harita sonradan menu 49'dan ya da yeniden acilarak tamamlanabilir.
                _tagStep = basla ? TagStep.Plaka : TagStep.Yok;
                EnterEditor();
            });
        }

        /// <summary>
        /// Tag kurulumunu KAYDET: origin'i yaz, plakalari tag'e cevir, hepsini kalibrasyona ac.
        /// Cevrimi her zaman YETKI sahibi yapar (dosya ve harita onda); gozluk isterse RPC ile.
        ///
        /// "KAGITLAR YAPISTIRILDI MI" DIYE SORMUYORUZ. Sorulmasi, kagidin plakadan BAGIMSIZ
        /// bir zamanda asildigi varsayimina dayaniyordu; sahadaki is boyle yurumuyor: gozlugu
        /// takan kisi plakayi koyarken ikinci kisi ayni anda, onun tarifiyle kagidi tam o
        /// noktaya yapistiriyor. Yerlestirme ile yapistirma es zamanli oldugu icin ayri bir
        /// onay, her turda basilan ve hicbir zaman "hayir" cevabi almayan bir ekran olurdu.
        /// </summary>
        void FinishTagSetup()
        {
            // Hic plaka konmadiysa cevrilecek bir sey yok: kullanici adimi fiilen atlamis
            // demektir. Hata ekrani gostermek gurultu olurdu, normal cikisa birakiyoruz.
            var oturum = ConstructorSession.Instance;
            if (oturum == null || oturum.Layout == null || TagCapture.PlateCount(oturum.Layout) == 0)
            {
                _tagStep = TagStep.Yok;
                BeginExitFlow();
                return;
            }

            if (ConstructorSession.IsMapAuthority)
            {
                string rapor = ConstructorSync.HostTagSetup(TagCapture.DefaultOriginHeight, out bool ok);
                if (!ok)
                {
                    // Basarisizsa insa moduna GERI DON: kullanici plakayi duzeltip
                    // tekrar deneyebilsin. Menuye dusurmek yapilan isi gorunmez kilardi.
                    OpenConfirm("TAG KURULUMU YAPILAMADI", rapor,
                        "GERİ DÖN", "VAZGEÇ", UITheme.TeamRedEdge, geri =>
                    {
                        if (geri) { _tagStep = TagStep.Plaka; EnterEditor(); }
                        // VAZGEC de bir yere cikmali: normal kaydetme zinciri. Hicbir sey
                        // yapmasaydi kullanici, konmus plakalari olan isimsiz bir haritayla
                        // menude kalirdi ve o emegi kaydetmenin yolu gorunmezdi.
                        else BeginExitFlow();
                    });
                    return;
                }
                StartCoroutine(Note("TAG KURULUMU TAMAM\n\n" + rapor, 6f));
            }
            else if (!ConstructorSync.ClientRequestTagSetup(TagCapture.DefaultOriginHeight))
            {
                StartCoroutine(Note("SUNUCUYA BAĞLI DEĞİL\n\nTag kurulumu PC'de yapılıyor.", 5f));
                return;
            }

            // ISIM SIMDI SORULUYOR: tag'ler fiziksel emek (duvara kagit yapistirildi) ve
            // isimsiz harita diske yazilamiyor. Cikisi beklemek, bir cokmede o emegin
            // tamamini goturur. Kayittan sonra insa modu basliyor.
            //
            // ADIM ANCAK KAYITLA KAPANIR (_tagStep = Yok yalnizca burada). Bastan kapatmak
            // bir acik biraktiyordu: insa modundan kazara cikan -- sol cubuk tek tikla
            // calisiyor -- yarim kalmis kurulumu bitirmis sayiliyordu, sonra konan plakalar
            // hicbir zaman tag'e cevrilmiyordu ve bunun bir belirtisi yoktu. Isim
            // verilmediyse adim ACIK kalir; sonraki cikista cevrim tekrar kosar.
            // Cevrim yeniden kosmaya elverisli: ayni plakalar ayni tag'leri uretir, var
            // olanlarin yaw'i ve acik/kapali durumu korunur.
            OpenName("HARİTA ADI", null,
                ad => { _tagStep = TagStep.Yok; DoSave(ad); EnterEditor(); },
                () => EnterEditor());
        }

        void EnterEditor()
        {
            if (Placer == null)
            {
                StartCoroutine(Note("EDİTÖR YOK\n\nConstructor bileşeni bulunamadı.", 4f));
                return;
            }

            // Editordeyken otomatik kayit YOK: kaydetme karari cikista soruluyor ve
            // "degisiklikleri at" ancak hicbir sey yazilmamissa bir anlam tasir.
            ConstructorSession.AutoSaveSuspended = true;
            Placer.SetBuildMode(true);

            // TAG KURULUMUNDA cikis tusunu SOYLE. Adimin sonu "insa modundan cik" ama o tus
            // (sol cubuk tiki) hicbir yerde yazmiyor; bilmeyen kisi plakalari koyup ekranda
            // kalirdi. Yonerge insa modu panelinden veriliyor, cunku akisin kendi ekranlari
            // insa modu acikken kapali.
            if (_tagStep == TagStep.Plaka)
                Placer.ShowHint(
                    "TAG KURULUMU\n\n" +
                    "Plakayi duvara koy — BEYAZ yuz odaya baksin,\n" +
                    "kagit ayni anda tam o noktaya yapistirilsin.\n\n" +
                    "Bitince SOL CUBUGA BAS: tag'ler kaydedilir.", 10f);
        }

        // ------------------------------------------------------------- cikis karar zinciri

        /// <summary>
        /// Editorden cikildi. Kaydedilmemis degisiklik yoksa hicbir sey sorulmaz — her cikista
        /// bir soru, en cok yapilan islemi en pahali hale getirirdi.
        /// </summary>
        void BeginExitFlow()
        {
            // TAG KURULUMU ACIKKEN insa modundan cikmak "bitirdim" demek. Normal zincire
            // dusseydi kullanici plakalari koyup cikinca harita adi sorulur, tag'ler ise hic
            // uretilmezdi -- akisin tam ortasinda sessizce kaybolurdu.
            if (_tagStep == TagStep.Plaka) { FinishTagSetup(); return; }

            var s = ConstructorSession.Instance;
            if (s == null || !s.HasUnsavedChanges) return;
            AskSave();
        }

        void AskSave()
        {
            var s = ConstructorSession.Instance;
            int prop = s != null ? s.PlacedCount : 0;
            OpenConfirm("KAYDET?", prop + " yerleştirme kaydedilmedi.",
                "KAYDET", "KAYDETME", UITheme.AccentCyan, yes =>
            {
                if (!yes) { AskDiscard(); return; }

                // Isimsiz harita once isimlendirilir; adi olanda "uzerine yaz mi, farkli mi".
                if (string.IsNullOrEmpty(s?.CurrentMapName)) OpenName("HARİTA ADI", null);
                else AskOverwrite();
            });
        }

        void AskOverwrite()
        {
            var s = ConstructorSession.Instance;
            string ad = s != null ? s.CurrentMapName : "";
            OpenConfirm("'" + ad + "'", "Üzerine mi yazılsın, yoksa yeni bir ad mı?",
                "ÜZERİNE YAZ", "FARKLI KAYDET", UITheme.AccentCyan, yes =>
            {
                if (yes) DoSave(ad);
                else OpenName("FARKLI KAYDET", null);
            });
        }

        void AskDiscard()
        {
            OpenConfirm("DEĞİŞİKLİKLERİ AT?", "Bu oturumda yaptıkların geri gelmez.",
                "AT", "VAZGEÇ", UITheme.TeamRedEdge, yes =>
            {
                if (yes) Discard();
                else AskSave();
            });
        }

        void DoSave(string mapName)
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            // Dosyayi HER ZAMAN otorite yazar. Gozlukte istek sunucuya gider; sonuc oradan
            // doner (ConstructorSync.SaveMessage) ve "kaydedilmemis" isareti orada duser.
            bool ok = ConstructorSession.IsMapAuthority
                ? s.SaveAs(mapName)
                : ConstructorSync.ClientRequestSave(mapName);

            if (!ok)
            {
                StartCoroutine(Note("KAYDEDİLEMEDİ\n\nConsole'a bak.", 4f));
                return;
            }

            if (ConstructorSession.IsMapAuthority) MapCatalog.NoteSaved();
            AskPool(mapName);
        }

        /// <summary>Serit 2'nin ilk kutusu: kaydedilen harita oyuncu rotasyonuna girsin mi?</summary>
        void AskPool(string mapName)
        {
            var e = MapCatalog.Find(mapName);

            // Zaten havuzdaysa sormaya gerek yok.
            if (e != null && e.inPool) return;

            // Havuza giremeyecek harita icin SORU DEGIL, SEBEP: "evet" dedirtip ardindan
            // reddetmek, kararin oyuncuda oldugu izlenimini bosa harcar.
            if (e != null && !e.poolEligible)
            {
                StartCoroutine(Note("HAVUZA EKLENEMEZ\n\n" + e.poolBlockReason, 6f));
                return;
            }

            OpenConfirm("HAVUZA EKLENSİN Mİ?", "Havuzdakiler oyuncu modunda çıkar.",
                "EKLE", "EKLEME", UITheme.AccentCyan, yes =>
            {
                if (!yes) return;
                if (!MapCatalog.AddToPool(mapName, out string hata) && !string.IsNullOrEmpty(hata))
                    StartCoroutine(Note("HAVUZA EKLENEMEDİ\n\n" + hata, 6f));
            });
        }

        void Discard()
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            if (!ConstructorSession.IsMapAuthority)
            {
                // Degisiklikler sunucunun belleginde de duruyor; atmayi o yapmali.
                ConstructorSync.ClientRequestDiscard();
                s.ClearUnsaved();
                return;
            }

            string ad = s.CurrentMapName;
            bool ok = string.IsNullOrEmpty(ad) ? s.OpenNew() : s.OpenExisting(ad);
            if (!ok) StartCoroutine(Note("GERİ ALINAMADI\n\n" + s.NotStartedReason, 4f));
        }

        // ------------------------------------------------------------- harita yoneticisi

        void OpenActions(string mapName)
        {
            var e = MapCatalog.Find(mapName);
            if (e == null) return;

            CloseActions();
            EnsurePointer();

            var go = new GameObject("Map Actions Panel");
            go.transform.SetParent(transform, false);
            _actions = go.AddComponent<MapActionsPanel>();
            _actions.Setup(e);
            _actions.Chosen += a => OnAction(a, mapName);

            _placed = false;
            _recentering = false;
        }

        void OnAction(MapActionsPanel.Action a, string mapName)
        {
            var e = MapCatalog.Find(mapName);
            CloseActions();

            switch (a)
            {
                case MapActionsPanel.Action.Back:
                    break;   // listeye don — panel kapandi, liste zaten acik

                case MapActionsPanel.Action.PoolToggle:
                    if (e != null && e.inPool) MapCatalog.RemoveFromPool(mapName);
                    else if (!MapCatalog.AddToPool(mapName, out string hata) &&
                             !string.IsNullOrEmpty(hata))
                        StartCoroutine(Note("HAVUZA EKLENEMEDİ\n\n" + hata, 6f));
                    break;

                case MapActionsPanel.Action.Rename:
                    OpenRename(mapName);
                    break;

                case MapActionsPanel.Action.Delete:
                    // SILME GERI DONUSSUZ: tek tikla degil, onayla. Onay kirmizi.
                    OpenConfirm("SİLİNSİN Mİ?",
                        "'" + (e != null ? e.displayName : mapName) + "' tamamen kaldırılır.",
                        "SİL", "VAZGEÇ", UITheme.TeamRedEdge, yes =>
                    {
                        if (yes && !MapCatalog.Delete(mapName))
                            StartCoroutine(Note("SİLİNEMEDİ\n\nConsole'a bak.", 4f));
                    });
                    break;
            }
        }

        void OpenRename(string mapName)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var e = MapCatalog.Find(mapName);
            var go = new GameObject("Name Entry Panel");
            go.transform.SetParent(transform, false);
            _name = go.AddComponent<NameEntryPanel>();
            _name.Setup("YENİDEN ADLANDIR", e != null ? e.displayName : mapName);
            _name.Confirmed += yeni =>
            {
                CloseName();
                if (!MapCatalog.Rename(mapName, yeni, out string hata) && !string.IsNullOrEmpty(hata))
                    StartCoroutine(Note("ADLANDIRILAMADI\n\n" + hata, 5f));
            };
            _name.Cancelled += () => { CloseName(); OpenActions(mapName); };

            _placed = false;
            _recentering = false;
        }

        void CloseActions()
        {
            if (_actions == null) return;
            Destroy(_actions.gameObject);
            _actions = null;
        }

        // ------------------------------------------------------------- karar/isim ekranlari

        void OpenConfirm(string title, string message, string yesLabel, string noLabel,
            Color yesEdge, Action<bool> onAnswer)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var go = new GameObject("Confirm Panel");
            go.transform.SetParent(transform, false);
            _confirm = go.AddComponent<ConfirmPanel>();
            _confirm.Setup(title, message, yesLabel, noLabel, yesEdge);
            _confirm.Answered += answer =>
            {
                CloseConfirm();
                onAnswer?.Invoke(answer);
            };

            _placed = false;
            _recentering = false;
        }

        /// <param name="onConfirmed">
        /// Bos birakilirsa varsayilan davranis: kaydet (cikis zinciri). Tag kurulumu kendi
        /// devamini vermek zorunda — orada isimden sonra insa moduna DONULUYOR, cikilmiyor.
        /// </param>
        void OpenName(string title, string prefill,
            Action<string> onConfirmed = null, Action onCancelled = null)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var go = new GameObject("Name Entry Panel");
            go.transform.SetParent(transform, false);
            _name = go.AddComponent<NameEntryPanel>();
            _name.Setup(title, prefill);
            _name.Confirmed += ad =>
            {
                CloseName();
                if (onConfirmed != null) onConfirmed(ad); else DoSave(ad);
            };
            _name.Cancelled += () =>
            {
                CloseName();
                if (onCancelled != null) onCancelled(); else AskSave();
            };

            _placed = false;
            _recentering = false;
        }

        void CloseConfirm()
        {
            if (_confirm == null) return;
            Destroy(_confirm.gameObject);
            _confirm = null;
        }

        void CloseName()
        {
            if (_name == null) return;
            Destroy(_name.gameObject);
            _name = null;
        }

        // ------------------------------------------------------------- yardimcilar

        ConstructorPlacer Placer
        {
            get
            {
                if (_placer == null) _placer = FindFirstObjectByType<ConstructorPlacer>();
                return _placer;
            }
        }

        void EnsurePointer()
        {
            if (_pointer != null) return;
            var go = new GameObject("UI Pointer");
            go.transform.SetParent(transform, false);
            _pointer = go.AddComponent<VRPointer>();
        }

        void CloseMenu()
        {
            if (_menu == null) return;
            _menu.Selected -= OnMenuChoice;
            Destroy(_menu.gameObject);
            _menu = null;
        }

        void CloseList()
        {
            if (_list == null) return;
            _list.Picked -= OnMapPicked;
            _list.Back -= CloseList;
            Destroy(_list.gameObject);
            _list = null;
        }

        void CloseAll()
        {
            CloseMenu();
            CloseList();
            CloseActions();
            CloseConfirm();
            CloseName();
            _managing = false;
            if (_pointer != null) { Destroy(_pointer.gameObject); _pointer = null; }
        }

        IEnumerator Note(string text, float seconds)
        {
            var p = HeadFollowPanel.Create("Creative Note", text, Color.white);
            yield return new WaitForSeconds(seconds);
            if (p != null) Destroy(p.gameObject);
        }

        /// <summary>
        /// Tembel takip: panel gorus alanindan cikinca hedefe yumusakca kayar, menzil icindeyken
        /// DUNYAYA SABIT kalir ki lazerle tusa basilabilsin. ModeSelectUI ile ayni davranis.
        /// </summary>
        void Place(Transform t)
        {
            if (t == null) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 targetPos = head.position + fwd * Distance - Vector3.up * HeightDrop;
            Quaternion targetRot = Quaternion.LookRotation(fwd) * Quaternion.Euler(TiltDegrees, 0f, 0f);

            if (!_placed)
            {
                t.SetPositionAndRotation(targetPos, targetRot);
                _placed = true;
                _recentering = false;
                return;
            }

            if (!_recentering)
            {
                Vector3 toPanel = t.position - head.position; toPanel.y = 0f;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                if (Vector3.Angle(fwd, toPanel.normalized) < RecenterAngle) return;
                _recentering = true;
            }

            float k = 1f - Mathf.Exp(-RecenterSpeed * Time.unscaledDeltaTime);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, targetPos, k),
                                     Quaternion.Slerp(t.rotation, targetRot, k));

            if ((t.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }
    }
}
