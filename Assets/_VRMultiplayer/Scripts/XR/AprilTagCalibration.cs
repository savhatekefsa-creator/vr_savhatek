using System;
using System.Collections.Generic;
using PassthroughCameraSamples;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// FAZ 3 koprusu — duvardaki AprilTag'i okuyup ortak cerceveyi kurar.
    /// Bkz. PLAN-apriltag-uyarlama.md
    ///
    /// MEVCUT SISTEME ETKISI YOK: bu bilesen yalnizca A/B DOKUNMASININ yerine geciyor.
    /// Tag gorulunce rig hizalanir ve <see cref="CalibrationAnchor.Bind"/> cagrilir; oradan
    /// sonrasi (anchor surusu, paylasim, kalicilik) aynen mevcut sistemdir.
    ///
    /// Boru hatti:
    ///   1. WebCamTextureManager'dan kamera karesi
    ///   2. PassthroughCameraUtils'ten kamera pozu + intrinsics
    ///   3. TagDetector.ProcessImage  -> tag'in KAMERAYA gore pozu
    ///   4. Dunyaya cevir             -> tag'in DUNYA pozu
    ///   5. "Bu tag nerede olmaliydi" ile karsilastir -> rig duzeltmesi
    ///   6. CalibrationAnchor.Bind(...)
    ///
    /// Ayrica FAZ 0 SPIKE olcumleri icin panel: menzil, jitter, tespit hizi.
    /// </summary>
    public class AprilTagCalibration : MonoBehaviour
    {
        // ZEMIN MONTAJI KALDIRILDI. Tag'leri yere sermek denendi ve birakildi: yatik
        // kagida ayakta bakarken gorus acisi 70-80 dereceye cikiyor, o acida karenin
        // koseleri birbirine yaklasiyor ve poz cozumu bozuluyor. Duvarda 1,50 m'de
        // duran tag hem cepheden goruluyor hem goz hizasina yakin.

        [Serializable]
        public class TagEntry
        {
            [Tooltip("Basili tag'in ID'si (tag36h11 ailesinde).")]
            public int id;

            [Tooltip("Tag'in ORTAK CERCEVEDEKI konumu (metre). Tek tag ile baslarken bu, " +
                     "tag'in zeminden yuksekligi ve origin'e gore yeridir.")]
            public Vector3 position = new Vector3(0f, 1.0f, 0f);

            [Tooltip("Tag'in ekseninin yonu — ortak cercevenin +Z'sine gore derece.\n\n" +
                     "DIKKAT, KAGIDIN BAKTIGI YON DEGIL: bu deger tam TERSINI, yani DUVARIN " +
                     "ICINI gosteriyor. Tespit cozucusu tag'in +Z'sini kagidin ARKASINA " +
                     "veriyor ve karsilastirma (satir 789) olculen yaw ile bu alani dogrudan " +
                     "esitliyor, yani alan da ayni konvansiyonda olmak zorunda.\n\n" +
                     "Dogrulandi 2026-08-11: cihazda plakanin BEYAZ (kagit) yuzu odaya " +
                     "bakarken plakanin urettigi yaw 270,0 derece cikti; ayni noktada " +
                     "kameranin olctugu yaw 270,3 derece. Ikisi de duvarin icini gosteriyor.\n\n" +
                     "Duvara asili bir tag icin pratik kural: kagida bakarken okudugun yon " +
                     "artı 180.")]
            public float yawDegrees;

            [Tooltip("Bu tag'i ureten PLAKANIN kimligi (PlacedProp.instanceId). 0 = plakadan " +
                     "gelmedi (tag 0/origin, kamerayla olculmus ya da elle yazilmis tag).\n\n" +
                     "NEDEN VAR: tag ID'si eskiden plakalarin SIRASINDAN turetiliyordu, yani " +
                     "ortadaki bir plakayi silmek sonraki TUM tag'lerin ID'sini kaydiriyordu. " +
                     "Duvardaki kagitlarin uzerinde ise basili, degismez numaralar var — " +
                     "kayma, kalibrasyonu sessizce yanlis tag'i aramaya gonderirdi.\n\n" +
                     "Bu alan kimligi SIRAYA degil PLAKAYA baglar: plaka silinince yalnizca " +
                     "onun tag'i duser, digerleri numarasini korur.")]
            public uint sourceInstanceId;

            [Tooltip("Bu tag KALIBRASYONDA kullanilsin mi.\n\n" +
                     "KAPALIYKEN tag yine gorulur, olculur ve panelde gorunur — ama rig'i " +
                     "OYNATMAZ. Yeni asilan bir tag icin yerlesim degerleri gozle dogrulanana " +
                     "kadar KAPALI tutulur: yanlis olculmus tek bir tag, dogru olanlarin " +
                     "kurdugu cerceveyi de bozar ve hangisinin sucu oldugu anlasilmaz.")]
            public bool useForCalibration = true;
        }

        [Header("Tag")]
        [Tooltip("Basili tag'in SIYAH KARESININ dis kenar uzunlugu (metre). Cetvelle olcun — " +
                 "yazicilar olcek kaydirir ve bu deger dogrudan mesafe dogrulugunu belirler.")]
        public float tagSizeMeters = 0.14f;

        [Tooltip("Hangi tag nerede. Tek tag ile baslamak yeterli.")]
        public TagEntry[] tagLayout = { new TagEntry() };

        [Tooltip("Buradaki yerlesimin SURUMU. Cihazdaki TagLayout.json normalde bunu ezer " +
                 "(olculen deger elle yazilandan guvenilirdir); dosyanin surumu bundan KUCUKSE " +
                 "ezmez ve buradaki yerlesim gecerli olur.\n\n" +
                 "BU SAYIYI ARTIR: yerlesimi buradan degistirdigin ve degisikligin daha once " +
                 "olcum yapmis gozluklere de ulasmasi gerektigi her seferde. Artirmazsan " +
                 "degisiklik o gozluklerde HICBIR ETKI YAPMAZ ve sebebi hicbir yerde yazmaz.")]
        public int layoutVersion = 1;

        [Header("Tespit")]
        [Tooltip("Duzeltme GEREKIRKEN saniyede kac tespit (tag gorunuyor ama hiza bozuk). " +
                 "Tespit pahalidir: her turda tam cozunurluklu GetPixels32 + tag arama.")]
        public float detectionsPerSecond = 3f;

        [Tooltip("BOSTAKI hiz: tag gorunmuyorken ya da hiza zaten iyiyken. Macin buyuk kisminda " +
                 "tag'e bakilmaz — o sure boyunca tam hizda taramak bosuna CPU/pil yakar. " +
                 "Tag'e bakildiginda ~1 sn icinde fark edilir, sonra otomatik hizlanir.")]
        public float idleDetectionsPerSecond = 1f;

        [Tooltip("Goruntu kucultme carpani. Buyuk deger = hizli ama menzil/dogruluk duser.")]
        public int decimation = 2;

        [Header("Ogrenme modu — tag'in yerini SISTEM olcsun")]
        [Tooltip("ACIK ise: A/B ile kalibre olduktan sonra tag'e bakin, sistem tag'in ortak " +
                 "cercevedeki konumunu ve yonunu OLCUP loglar. Cikan sayilari Tag Layout'a " +
                 "yazip bu modu kapatirsiniz — bir daha A/B'ye gerek kalmaz.\n\n" +
                 "Elle olcmekten cok daha hassas: 1 m'de jitter 3 mm.")]
        public bool learnMode = false;

        [Tooltip("YALNIZCA bu ID'li tag ogrenilir. -1 = ilk uygun tag (tek tag'li kurulum icin).\n\n" +
                 "COKLU TAG'DE MUTLAKA DOLDURUN: ogrenme ilk uygun tag'e KILITLENIR ve bir daha " +
                 "birakmaz. Tag 0 menzildeyken acarsaniz onu ogrenir, yeni tag'i degil — ve " +
                 "duzeltmenin tek yolu uygulamayi kapatip acmaktir.")]
        public int learnTargetId = -1;

        [Tooltip("Ogrenme icin en fazla bu mesafeden olcum kabul edilir (m). Jitter mesafenin " +
                 "karesiyle buyudugu icin uzaktan ogrenmek hatayi kalici hale getirir.")]
        public float learnMaxDistance = 1.5f;

        [Tooltip("Ogrenme icin kac olcumun ortalamasi alinsin. Titremeyi bastirir.")]
        public int learnSampleCount = 30;

        [Header("Yerlesim isaretcisi")]
        [Tooltip("Her tag'in ILAN EDILEN yerine tag boyutunda bir plaka ciz.\n\n" +
                 "Gercek tag'e bakip plakanin uzerine oturup oturmadigina bakarsin — yerlesimin " +
                 "dogrulugu boylece sayilarla degil GOZLE denetlenir. Yesil plaka kalibrasyonda " +
                 "kullanilan tag, sari plaka dogrulama bekleyen tag.")]
        public bool showTagMarkers = true;

        [Tooltip("Passthrough'u AC ve sanal dunyayi gizle.\n\n" +
                 "OGRENME MODUNDAN BAGIMSIZ. Once ogrenmeye bagliydi, ama iki ayri ihtiyaci " +
                 "birbirine kilitliyordu:\n\n" +
                 "  olcum icin: plakanin gercek tag'e oturup oturmadigini gormek — ikisini " +
                 "AYNI ANDA gormek sart\n" +
                 "  oyun icin : harita ZEMIN URETMIYOR ve sahnede de zemin yok; passthrough " +
                 "kapaninca oyuncu bosluktu kalir\n\n" +
                 "Kilitli oldugu surece 'panel gorunmesin' istemek, passthrough'u da kapatip " +
                 "oyuncuyu boslukta birakiyordu. Isaretciler ve panel '~' onekli oldugu icin " +
                 "sanal dunya gizlenirken ayakta kalir.")]
        [UnityEngine.Serialization.FormerlySerializedAs("learnPassthrough")]
        public bool showPassthrough = true;

        [Tooltip("Kumanda ofsetinin olculecegi tag. Ofset havuzuna YALNIZCA bu tag'e yapilan " +
                 "dokunuslar girer, ve bu tag dokunusla YENIDEN YAZILAMAZ.\n\n" +
                 "Neden sabit bir tag: ofset, tag'in ILAN EDILEN konumu dogru kabul edilerek " +
                 "hesaplanir. Sifir noktasini TANIMLAYAN tag icin bu her zaman dogrudur; " +
                 "olculmus bir tag icin degildir. Herhangi bir kalibrasyon tag'ini kabul " +
                 "edersek, konumu daha az guvenilir bir tag'e dokunmak ofseti kirletir.")]
        public int offsetReferenceTagId = 0;

        [Header("Kalibrasyon")]
        [Tooltip("Ilk saglam tespitte otomatik kalibre et. Kapaliysa yalnizca olcum yapar " +
                 "(FAZ 0 spike modu).")]
        public bool autoCalibrate = true;

        [Tooltip("Dikey ekseni de tag'den duzelt. Tag'in yuksekligi olculmus oldugu icin bu, " +
                 "gozlugun zemin tahminindeki hatayi da duzeltir.\n\n" +
                 "TESHIS BITTI, GERI ACILDI (2026-08-14). Dikey diye bir sorun yokmus: kapali " +
                 "turda acilis duzeltmesinin dikey bileseni dy +0,013 m cikti (toplam sapma " +
                 "198,8 cm'nin tamami yatay: dx -1,093 dz +1,661) ve sonraki tum dy'ler ±3 cm " +
                 "icinde kaldi. Kumanda elde tutularak olculdugunde zemin de -0,009 m okudu. " +
                 "Yani onceki -0,995 m, kumandayi YERE YATIRMAKTAN gelen takip kopmasiydi — " +
                 "TickFloor notunun bastan uyardigi hata. Asagidaki (A)/(B) ayrimi (B) cikti.\n\n" +
                 "ESKI TESHIS NOTU (arsiv):\n" +
                 "Belirti: kalibrasyondan SONRA kumandayla olculen zemin -0,995 m'ye dusuyor, " +
                 "yani dunya ~1 m iniyor. Oysa kalibrasyondan ONCE ayni kumanda zemini 0,000'da " +
                 "ve tag 0'i ~1,6-1,8 m'de gosteriyor; kullanici da tum tag'lerin 150 cm'de " +
                 "oldugunu metreyle dogruladi. Yani kumanda ile kamera AYNI takip uzayinda " +
                 "farkli seyler soyluyor.\n\n" +
                 "Kapali tutmak iki hipotezi ayiriyor:\n" +
                 "  (A) kamera tag'i ~1 m yuksek goruyor -> dikey duzeltme dunyayi indiriyordu. " +
                 "Kapaliyken zemin ~0'a doner ve SNAP satirindaki dy ~-1,0 yazar.\n" +
                 "  (B) kameranin olcumu dogru, -0,995 kumandanin YERDEKI takip kopmasi " +
                 "(bkz. TickFloor notu: IR halkasi gizlenince poz yarim metre siciyor; " +
                 "_floorBest EN DUSUK degeri aliyor, tek bozuk kare olcumu asagi cekiyor). " +
                 "Kapaliyken zemin YINE -0,995 okur ve dy ~0 yazar.\n\n" +
                 "dy satiri duzeltme UYGULANMADAN once yazildigi icin bu alan kapaliyken de " +
                 "olculuyor — yani tek turda hem kontrol hem olcum elde ediliyor.\n\n" +
                 "Kapaliyken eski davranis gecerli: dikey, gozlugun kendi zemin tahminine " +
                 "birakilir.")]
        public bool correctVertical = true;

        [Tooltip("Kalibrasyon icin kac kare ortalanacak. TEK KARE titrek olabilir ve kalibrasyonu " +
                 "o hatayla kilitler (yasanmis: bir dogru, bir 15 cm kayma). Ortalama bunu bastirir.")]
        public int calibrateSampleCount = 15;

        [Tooltip("Kalibrasyon yalnizca tag bu mesafeden YAKINken yapilir (m). Jitter mesafeyle " +
                 "buyudugu icin yakindan kalibre etmek cok daha dogru — spike: 1 m'de 3 mm, 2 m'de 15 mm.\n\n" +
                 "2 -> 3 m (2026-08-14, Adim 5'in ikinci kabul maddesi). Bu kesme, uzak orneklerin " +
                 "kotu olmasinin bedelini ATARAK oduyordu. Adim 5'ten sonra uzak ornek " +
                 "agirliklandiriliyor (1/d^4), yani karisik bir pencerede 3 m'deki ornek 1 m'dekinin " +
                 "1/81'i kadar etkili — atmaya gerek yok.\n\n" +
                 "NEDEN 3 VE DAHA FAZLASI DEGIL:\n" +
                 "  * Sistematik hata mesafeyle DOGRUSAL buyuyor ve ortalamayla GECMIYOR. Ana " +
                 "nokta kaymasi (olculdu: 4,6 px = 0,30 derece) 2 m'de 1,1 cm, 3 m'de ~1,7 cm " +
                 "yanal hata demek. Olu bolge 1 cm; yani 3 m'de yalnizca uzak orneklerden olusan " +
                 "bir pencere ~1,7 cm'lik kalici bir duzeltme uygulayabilir. Bu kabul edilebilir " +
                 "bir tavan, cunku oyuncu yaklastigi anda yakin ornekler 81:1 agirlikla bastirip " +
                 "kendini onariyor. 4 m'de tavan ~2,3 cm'e cikar ve kazanci yok.\n" +
                 "  * Rastgele hata sorun DEGIL: 3 m'de sigma ~37 mm ama duzeltme ORTALAMAYA " +
                 "bakiyor, onun standart hatasi 15 ornekte ~9,6 mm — olu bolgenin altinda.\n\n" +
                 "AGIRLIKLANDIRMA TEK BASINA YETMEZ, bilerek not: 1/d^4 yalnizca pencere KARISIK " +
                 "mesafe iceriyorsa koruyor. Oyuncu surekli 3 m'de durursa tum agirliklar esit " +
                 "olur ve ortalama duz ortalamaya doner. Ustteki tavan hesabi tam da o en kotu " +
                 "durum icin yapildi.\n\n" +
                 "YON ZATEN KORUNUYOR: yawCorrectionMaxDistance 1,5 m: uzak ornek konumu duzeltir, " +
                 "yonu ellemez. Bu kesmeyi gevsetmek yaw'i uzaktan duzeltmeye ACMAZ.")]
        public float calibrateMaxDistance = 3f;

        [Tooltip("Tag olmasi gereken yerden bu kadar SAPINCA rig duzeltilir (m). Altinda dokunulmaz " +
                 "— jitter'dan surekli snap olmasin. Ust sinir yoksa uyku sonrasi buyuk sapmayi da toparlar.")]
        public float correctionDeadzoneMeters = 0.02f;

        [Tooltip("YAW yalnizca referans tag'den duzeltilsin; digerleri KONUMU duzeltsin.\n\n" +
                 "NEDEN: duzlemsel bir isaretcinin pozunda en guvenilmez bilesen duzlem disi " +
                 "donmedir — duvara asili bir tag icin bu tam olarak yaw'dir. Konum mm " +
                 "mertebesinde cikarken yaw 1-3 derece hata verir ve hata BAKIS ACISINA bagli " +
                 "oldugu icin ortalama almak duzeltmez. Olculdu: tag 1 ile tag 2'nin ilan " +
                 "edilen yaw'lari 2,4 derece celisiyordu ve gecislerde isaretli, tekrarlanabilir " +
                 "sicramalar uretiyordu.\n\n" +
                 "Gozluk ise tam tersi: yonu iyi tutar (IMU+SLAM), konumu kaybeder. Her tarafi " +
                 "guclu oldugu iste kullanmak icin yaw referanstan, konum her tag'den alinir.\n\n" +
                 "GUVENLIK: sapma anlik-duzeltme esigini asarsa yaw yine duzeltilir — uyku " +
                 "sonrasi ya da takip kaybinda yonun kilitli kalmasi cok daha kotu olurdu.")]
        public bool yawFromReferenceOnly = true;

        [Tooltip("Yaw yalnizca referans tag bu mesafeden YAKINKEN duzeltilir (m). 0 = sinir yok.\n\n" +
                 "Duzlem disi aci hatasi, tag'in goruntudeki buyuklugu kuculdukce hizla artar. " +
                 "Uzaktan olculen yaw, duzeltmedigi kadar hata katar: cihazda 2,4 cm'de seyreden " +
                 "gecisler tek bir uzak yaw duzeltmesinden sonra 20 cm'e firladi.")]
        public float yawCorrectionMaxDistance = 1.5f;

        [Tooltip("Bu kadar buyuk bir yaw sapmasi GERCEK KAYIP sayilir ve mesafe/referans " +
                 "kisitlari asilarak duzeltilir (derece).\n\n" +
                 "Uykudan uyanma ya da takip kaybi sonrasi yon tamamen kayabilir; o durumda " +
                 "duzeltecek baska hicbir sey yoktur. Esik YUKSEK tutulmali: yaw olcum " +
                 "gurultusu 1-3 derece, ve esik oraya yakin konuldugunda gurultuyu kurtarma " +
                 "sanip kacak bir geri besleme kuruyor (cihazda yasandi, 3 derece ile).")]
        public float yawRecoveryDegrees = 10f;

        [Tooltip("Kurtarmanin UST siniri (derece). Bunun ustundeki sapma TEK BASINA kabul " +
                 "edilmez, TEYIT ister.\n\n" +
                 "NEDEN VAR: kurtarma bir ISIN degil BANT olmali. Alt esik tek basina " +
                 "birakildiginda kapi ustten sinirsiz kaliyor ve duzlemsel poz belirsizliginden " +
                 "gelen buyuk bir flip 'gercek kayip' sayilip mesafe ve referans kisitlarini " +
                 "birden baypas ediyor.\n\n" +
                 "TABAN CIZGISINDE YAKALANDI (2026-08-12, EV): referans OLMAYAN tag 1'den gelen " +
                 "+147,30 derecelik tek bir okuma kabul edildi ve dunyayi 4,96 m kaydirdi. " +
                 "Log'da kanit: tag 1'in diger butun duzeltmelerinde '(uygulanmadi)' yaziyor, " +
                 "yalnizca o satirda yazmiyor.\n\n" +
                 "35 derece SECILDI, olculmedi: ayni turdaki mesru kurtarma +40,29 dereceydi ve " +
                 "REFERANS tag'den geliyordu, yani bandin ustunde kalmasi sorun degil — teyit " +
                 "ister, reddedilmez. Bandi daraltmak gerekirse once bu sayi denenir.")]
        public float yawRecoveryMaxDegrees = 35f;

        [Tooltip("Bandin USTUNDEKI sapmanin kabul edilmesi icin kac ARDISIK olcumde ayni " +
                 "degeri vermesi gerektigi.\n\n" +
                 "NEDEN TEYIT, NEDEN RED DEGIL: gercek takip kaybi da bandin ustune cikar. " +
                 "Ikisini AYIRAN sey tekrarlanabilirlik — duzlemsel belirsizlik flipi kareler " +
                 "arasi ziplar ve ust uste ayni degeri vermez; gercek kayip verir, cunku dunya " +
                 "gercekten o kadar donmustur.\n\n" +
                 "Bedeli: gercek kayipta duzeltme birkac tespit turu (~1 sn) gecikir. Yanlis " +
                 "kabulun bedeli 5 metrelik bir sicramaydi.")]
        [Range(2, 6)] public int yawRecoveryConfirmations = 3;

        [Tooltip("Yaw sacilmasi bu dereceyi asarsa YON duzeltmesi birakilir (konum duzeltilmeye " +
                 "devam eder). 0 = KAPALI, yalnizca log'a yazilir.\n\n" +
                 "NE OLCUYOR: penceredeki yon olcumlerinin dairesel standart sapmasi. Duzlemsel " +
                 "poz belirsizliginin flip'i konumu neredeyse hic oynatmadan yonu ziplatiyor — " +
                 "iki cozum ayni noktayi farkli acilarla goruyor. Konum kararlilik kapisi " +
                 "(calibStabilitySpread) bu yuzden flip'i hicbir zaman yakalayamadi.\n\n" +
                 "2 DERECE, IKI TURDA OLCULDU (2026-08-14, ofis):\n" +
                 "  normal kullanim, tur 1 : 0,08-0,63  (10 olcum, medyan 0,24)\n" +
                 "  normal kullanim, tur 2 : 0,13-0,69  (8 olcum,  medyan 0,29)\n" +
                 "  157,8 derecelik gercek yon kaybi ani : 2,77\n" +
                 "  flip benzetimi (editor, 0/0/+30 derece) : 14,20\n" +
                 "Esik normal gurultunun (max 0,69) uc kati uzaginda, flip'in (14,2) yedide biri. " +
                 "Ikisini ayirmak icin genis bir bant var.\n\n" +
                 "KURTARMA MUAF TUTULUYOR ve bu SART: gercek yon kaybi aninda sacilma 2,77 " +
                 "olculdu, yani muafiyet olmasaydi kapi tam ihtiyac duyulan anda yonu bloke " +
                 "eder ve oyuncu 157 derece donuk bir dunyada kalirdi.\n\n" +
                 "0 = kapali (yalnizca log).")]
        public float yawSpreadMaxDegrees = 2f;

        [Header("Poz gecerlilik kapisi (Adim 3)")]
        [Tooltip("SADECE LOG (3a) mi, yoksa gercekten ELESIN mi (3b).\n\n" +
                 "ACIK = hicbir tespit elenmez, yalnizca 'elenecekti' diye log'a yazilir.\n" +
                 "Esikler dogrulanmadan kapiyi devreye almak, DOGRU okumalari eleyip sorunu " +
                 "cozulmus GOSTERIR — en kotu hata turu bu, cunku sessizdir ve iyi gorunur.\n\n" +
                 "Kapatmadan once bakilacak sayi: log'daki 'ELENECEKTI' orani. Plan %20'nin " +
                 "altini kabul ediyor; ustundeyse esikler yanlis, kod degil.\n\n" +
                 "3b'DE KAPATILDI (2026-08-13). Olculen: normal oynanista %0 (198 tespit, " +
                 "hicbiri elenmeyecekti), kasitli egik bakis turunda %1,0 (199 tespitin 2'si). " +
                 "Elenen iki tespit de 'normal 89,4 derece egik' — yani tag'in neredeyse YATIK " +
                 "gorundugu, tartismasiz bozuk bir cozum. Kapi sıradan egik bakisi yakalamiyor " +
                 "cunku yakalayacak bir sey yok: kestirim 30-45 dereceden bile dogru cozumu " +
                 "buluyor. Kazanc hassasiyet degil, nadir ama BUYUK bir sicramanin onlenmesi.")]
        public bool poseGateLogOnly = false;

        [Header("Coklu tag fuzyonu (deneysel)")]
        [Tooltip("Ayni karede GORULEN tag'leri BIRLIKTE cozsun mu.\n\n" +
                 "NEDEN: duzlemsel bir isaretcinin en guvenilmez bileseni kendi yaw'idir " +
                 "(1-3 derece, bakis acisina bagli, ortalamayla gecmiyor) -- konumu ise mm " +
                 "mertebesinde. Iki tag ayni karede goruluyorsa yonu onlarin KONUMLARINDAN " +
                 "turetebiliriz: 3 m arayla duran iki tag icin 15 mm'lik konum gurultusu " +
                 "0,29 derecelik yon gurultusu demek. Yani fuzyon, tag'in zayif olcumunu " +
                 "kullanmak yerine BAYPAS ediyor.\n\n" +
                 "KAPALIYKEN eski yol aynen calisir (en yakin tek tag). Tek tag goruluyorsa " +
                 "acikken de eski yola duser -- fuzyon en az iki tag ister.")]
        public bool useMultiTagFusion = false;

        [Tooltip("Fuzyon cozumunun kabul edilebilir artik hatasi (m). Ustu REDDEDILIR.\n\n" +
                 "Bu sayi kendi kendini dogrulayan bir kapi: cozumden sonra her tag'in " +
                 "olculen yeri ile ilan edilen yeri arasinda kalan fark, tag'lerin BIRBIRIYLE " +
                 "ve yerlesimle ne kadar uyustugunu dogrudan olcer. Buyukse ya yerlesim " +
                 "yanlis, ya bir tespit bozuk, ya da flip var -- ucunu de tek sayi yakalar.\n\n" +
                 "GECIS ile ayni buyuklukte olmali: olculen en iyi GECIS medyani 3,3 cm.")]
        public float fusionMaxResidual = 0.05f;

        [Tooltip("Bilinmeyen tag'leri OTOMATIK haritalasin mi.\n\n" +
                 "Cerceve GUVENILIRKEN (ayni karede en az iki BILINEN tag fuzyonla cozulmus, " +
                 "kalinti esigin altinda) yerlesimde olmayan bir tag gorulurse konumu ve yonu " +
                 "olculup yerlesime yazilir. Yazilan tag artik BILINEN olur ve sonrakiler icin " +
                 "temel islevi gorur — harita disari dogru kendiliginden buyur.\n\n" +
                 "NEDEN GEREKLI: konumlari plaka koyarak tanimlamak, plakanin o andaki " +
                 "cerceveyi miras almasina dayaniyor; cerceve heNUZ dogrulanmamisken hata " +
                 "zincirleniyor. Cihazda olculdu: boyle konan tag'ler 1-2,8 m sapti. " +
                 "Olcerek eklemek o zinciri kesiyor, cunku temel her adimda DOGRULANMIS oluyor.")]
        public bool autoMapUnknownTags = false;

        [Tooltip("Otomatik haritalama icin kac olcum ortalanacak.")]
        public int autoMapSampleCount = 20;

        [Tooltip("Otomatik haritalamada orneklerin izin verilen sacilmasi (m). Ustu YAZILMAZ.\n\n" +
                 "Tek bir bozuk tespit yerlesime kalici olarak islenmesin diye: pencere kendi " +
                 "icinde tutarli degilse olcum guvenilmez demektir ve beklemek yazmaktan iyidir.")]
        public float autoMapMaxSpread = 0.03f;

        [Tooltip("Tag'in NORMALI yataydan bu kadar sapabilir (derece). Ustu elenir.\n\n" +
                 "NEDEN ISE YARAR: kagitlar DUVARA duz yapistirilmis, yani normalleri yatay " +
                 "olmak ZORUNDA. Duzlemsel poz belirsizliginin yanlis cozumu tag'i one/arkaya " +
                 "yatirir ve normali yataydan koparir. Yerçekimi yonu IMU'dan geliyor ve tag " +
                 "tespitinin hicbir hatasini paylasmiyor — ortalamayla gecmeyen sistematik " +
                 "hatayi eleyebilen elimizdeki TEK bagimsiz kapi bu.\n\n" +
                 "OLCULDU (2026-08-13, EV): egik bakista tag'ler arasi uyusmazlik 10,2 cm, " +
                 "karsidan bakista 2,4 cm. Elemek istedigimiz sey tam olarak o egik okumalar.")]

        [Range(5f, 45f)] public float maxNormalTiltDegrees = 20f;

        [Tooltip("Tag'in KENDI dikeyi (yukari ekseni) dunya dikeyinden bu kadar sapabilir " +
                 "(derece). Ustu elenir.\n\n" +
                 "Normal yataylığından AYRI bir sinama: kagit duvarda dik durur, yani yalnizca " +
                 "normali degil KENDI ekseni de dunya dikeyiyle hizalidir. Yanlis cozum ikisini " +
                 "birden bozar ama farkli miktarlarda; iki bagimsiz olcu tek olcuden daha zor " +
                 "kandirilir.")]
        [Range(5f, 45f)] public float maxTagTiltDegrees = 25f;

        [Header("Hareket kapisi")]
        [Tooltip("Kafa bu hizdan hizli hareket ederken tespitler kalibrasyona ve olcume " +
                 "KATILMAZ (m/sn). 0 = kapali.\n\n" +
                 "NEDEN: kamera karesi t0'da yakalaniyor ama biz onu SU ANKI kafa poziyla " +
                 "birlestiriyoruz (PassthroughCameraUtils satir 223'un kendi notu). Quest'te " +
                 "passthrough gecikmesi 30-60 ms; 0,3 m/sn yururken bu 1,5 cm konum hatasi " +
                 "demek. Hata NASIL HAREKET ETTIGINE bagli oldugu icin her tag'e farkli " +
                 "yaklastiginda farkli cikar ve ortalamayla GECMEZ.")]
        public float maxHeadSpeed = 0.15f;

        [Tooltip("Kafa bu acisal hizdan hizli donerken tespitler katilmaz (derece/sn). 0 = kapali.\n\n" +
                 "BU TAVAN AYRI BIR SEY ICIN: rolling shutter. Quest passthrough kameralari " +
                 "goruntuyu satir satir okuyor; donerken ustu ve alti farkli anlarda yakalanir " +
                 "ve tag'in dortgeni EGILIR. Poz cozucu bunu 'tag donmus' diye yorumlar. Bu " +
                 "carpitma mesafeye gore olceklenmez, o yuzden asagidaki butceden ayri durur.")]
        public float maxHeadAngularSpeed = 15f;

        [Tooltip("Varsayilan passthrough gecikmesi (sn). Kare yakalandigi an ile kafa pozunu " +
                 "okudugumuz an arasindaki tahmini fark. Quest'te tipik 30-60 ms.")]
        public float assumedFrameLatency = 0.05f;

        [Tooltip("Hareketten kaynaklanan TAHMINI konum hatasi bu butceyi asarsa tespit " +
                 "kullanilmaz (m). 0 = kapali.\n\n" +
                 "Sabit hiz esiginden neden ustun: acisal hizin konum bedeli MESAFEYLE buyur. " +
                 "15 derece/sn, 1 m'deki tag'de 1,3 cm ama 4 m'dekinde 5,2 cm demek. Tek bir " +
                 "hiz esigi ya yakini gereksiz kisitlar ya uzagi kacirir; butce ikisini de " +
                 "dogru olceklendirir.\n\n" +
                 "  hata ~ dogrusal_hiz * gecikme  +  mesafe * tan(acisal_hiz * gecikme)")]
        public float motionErrorBudget = 0.01f;

        [Header("Anchor destegi (deneysel)")]
        [Tooltip("Tag GORUNMEZKEN cerceveyi Meta spatial anchor'i tutsun.\n\n" +
                 "IS BOLUMU: tag sifir noktasinin NEREDE oldugunu tanimlar (oturumlar arasi ayni " +
                 "fiziksel kagit); anchor tag gorunmezken onu yerinde tutar. Su an o araligi " +
                 "yalnizca gozlugun odometrisi tasiyor ve yavasca kayiyor; anchor gozlugun kendi " +
                 "ozellik haritasina bagli oldugu icin cok daha az kayar.\n\n" +
                 "Eski catisma cozuldu: tag her duzeltmede anchor'a yeni cerceveyi OGRETIYOR " +
                 "(ReanchorToCurrentRig), anchor da aralarda onu koruyor. Eskiden anchor mutlak " +
                 "poz yazip tag'in duzeltmesini eziyordu.\n\n" +
                 "VARSAYILAN KAPALI: kazanci OLCULMELI. Acik ve kapali hali ayni turda " +
                 "karsilastirilmadan varsayilan yapilmamali.")]
        public bool useAnchorHold = false;

        [Tooltip("Kayan pencere bu kadar sure ornek almadiysa TEMIZLENIR (sn).\n\n" +
                 "Ornekler bir CERCEVEYE aittir; tag gorus alanindan cikip geri geldiginde " +
                 "cerceve degismis olabilir. Bayat ornekler ortalamaya karisip sapmayi " +
                 "seyreltir ve sistem hizasizken 'HIZALI' der.\n\n" +
                 "2 -> 5 SN (2026-08-13, olculdu). Adim 4 turunda pencere 9 kez silindi ve " +
                 "bosluklar 2,0 / 2,0 / 2,4 / 3,0 / 3,0 / 4,0 / 5,0 / 11,1 / 37,3 sn idi. " +
                 "Bosta tespit 1 Hz oldugu icin TEK gecikmis kare 2 sn'yi asiyor: 2,0-3,0 sn " +
                 "araligindaki uc silme sirasiyla 14, 13 ve 13 ornek atti — yani tag'e bakmaya " +
                 "devam ederken dolu pencereler bosaltildi. Bu, Adim 4'un kaldirdigi beklemeyi " +
                 "geri getiriyordu. 5 sn, olculen tum surekli-bakis boslukklarinin (max 3,0) " +
                 "ustunde, gercek bakis kopmalarinin (11,1 / 37,3) altinda.\n\n" +
                 "UYKU ARTIK BU ESIGE BAGLI DEGIL: pencere uyanista dogrudan temizleniyor " +
                 "(OnApplicationPause). Esik yalnizca 'tag gorus alanindan cikti' durumu icin.")]
        public float calibWindowMaxGap = 5f;

        [Tooltip("Kazanan tag'i degistirmek icin yeni tag'in bu kadar DAHA YAKIN olmasi gerekir (m).\n\n" +
                 "Tag degisimi kayan pencereyi temizliyor. Iki tag benzer mesafedeyse secim her " +
                 "turda salinir ve pencere hicbir zaman dolmaz — kalibrasyon tamamlanmaz. " +
                 "Cihazda goruldu: sayac 4'e kadar cikip sifirlaniyordu.")]
        public float calibSwitchMargin = 0.3f;

        [Tooltip("Duzeltmeden once pencerenin ne kadar SIKI olmasi gerektigi — 1 METREDE (m).\n\n" +
                 "Ornek SAYISI tek basina bir sey soylemez: ornekler birbirini tutmuyorsa " +
                 "ortalamalari da tutmaz. Bu kapi sabit bekleme suresinden hem daha hizli " +
                 "(ornekler kararliysa hemen gecer) hem daha guvenli (kararsizsa sayac dolsa " +
                 "bile bekler). Mikro hareket sayaci sifirlamaz, yalnizca sacilmayi buyutur.\n\n" +
                 "MESAFEYLE OLCEKLENIR (bkz. calibStabilityScalesWithDistance). Bu deger artik " +
                 "mutlak sinir degil, 1 metredeki sinir.")]
        public float calibStabilitySpread = 0.02f;

        [Tooltip("Kararlilik esigini mesafenin KARESIYLE olcekle.\n\n" +
                 "NEDEN: gurultu mesafeyle buyuyor (olculdu: 1 m'de 3 mm, 2 m'de 15 mm) ama esik " +
                 "SABITTI. Sonuc, dogrulukla ilgisi olmayan bir mesafe duvari: 1,7 m'de 15 " +
                 "ornegin beklenen max sapmasi ~2,6 cm, esik 2 cm — yani kapi TASARIM GEREGI " +
                 "kapaniyordu.\n\n" +
                 "CIHAZDA OLCULDU (2026-08-14, Adim 5 turu): tag 0'a 1,5-1,8 m'den bakilan ilk " +
                 "110 saniyede kapi ALTI kez tuttu (sacilma 2,8-4,6 cm), hic duzeltme yapilmadi " +
                 "ve biriken hizasizlik ilk gecislerde 8-12 cm olarak cikti. Ayni turun ikinci " +
                 "yarisinda (daha yakin bakis) GECIS medyani 2,75 cm'e dustu.\n\n" +
                 "US 2, cunku olculen sey STANDART SAPMA: sigma ~ d^2. Ornek agirligi 1/d^4 " +
                 "kullaniyor cunku o VARYANS ile calisiyor — ayni model, ayni us ailesi.\n\n" +
                 "Gozlenen sekiz KARARSIZ olayina karsi denendi: 1,5-1,8 m'dekilerin besi geciyor " +
                 "(esik 4,5-6,3 cm), 0,68 m'deki 3,2 cm'lik sacilma hala eleniyor (esik 0,9 cm) " +
                 "— o mesafede 3,2 cm gercekten anormal.\n\n" +
                 "Kapatirsan eski sabit esige donulur.")]
        public bool calibStabilityScalesWithDistance = true;

        [Tooltip("Pencere ortalamasindan bu kadar uzak bir olcum AYKIRI sayilir (m).\n\n" +
                 "Cerceve degistiginin gercek isareti, yeni olcumun penceredekilerle taban " +
                 "tabana zit cikmasidir — zaman esigi bunun yalnizca DOLAYLI gostergesiydi. " +
                 "Tek aykiri ornek atlanir (bozuk tespit olabilir); ust uste ikisi gelirse " +
                 "dunya gercekten oynamis demektir ve pencere atilir.")]
        public float calibOutlierDistance = 0.15f;

        [Tooltip("Yaw icin olu bolge (derece).\n\n" +
                 "MESAFEDE BASKIN HATA BUDUR: yaw sapmasi tag'den uzaklastikca dogrusal olarak " +
                 "yer degistirmeye donusur. 1,5 derece 4 metrede 10 cm demek. Olculdu: iki " +
                 "olcum kosusu arasinda tag 1 tam bu yuzden 23 cm oynadi ve yer degistirme " +
                 "tag 0'dan cikan yaricapa DIK cikti — yani konum degil, donme.")]
        public float correctionYawDeadzoneDegrees = 0.4f;

        [Tooltip("KUCUK sapmalarda duzeltmenin ne kadari bir seferde uygulanir (0-1).\n\n" +
                 "1 = aninda (eski davranis, dar olu bolgede dunya zipliyor). 0,25 = birkac " +
                 "tespitte yakinsar, titreme gorunmez. Dar olu bolgeyi kullanilabilir kilan sey budur.")]
        [Range(0.05f, 1f)]
        public float smallCorrectionRate = 0.25f;

        [Tooltip("Kazanc, olculen pencere sacilmasina gore AZALSIN mi (Adim 6).\n\n" +
                 "KAPALIYKEN kazanc yine hesaplanir ve her duzeltme satirina yazilir, ama " +
                 "UYGULANMAZ. Adim 3'un iki turlu duzeni: kabul edilen pencerelerin sacilmasi " +
                 "bugune kadar HIC olculmedi, cunku KARARSIZ satiri yalnizca sinir ASILDIGINDA " +
                 "yaziliyor. Oran normal kullanimda 0,1 ise bu alan hicbir sey degistirmez; " +
                 "0,8 ise kazanci neredeyse yariya indirir. Once olcup sonra acilir.\n\n" +
                 "Log'da bakilacak sutunlar: 'sac/sinir' ve 'kazanc'.")]
        public bool gainScalesWithSpread = false;

        [Tooltip("Bu sapmanin USTU 'buyuk' sayilir ve ANINDA duzeltilir (m). Uyku sonrasi ya da " +
                 "takip kaybinda dunya hemen yerine otursun; suzulerek gelmesi cok daha kotudur.")]
        public float snapThresholdMeters = 0.10f;

        [Tooltip("Buyuk sapma esiginin yaw karsiligi (derece).")]
        public float snapThresholdDegrees = 3f;

        [Header("Spike olcum paneli")]
        public bool showPanel = true;

        AprilTag.TagDetector _detector;
        WebCamTextureManager _camMgr;
        Color32[] _pixels;
        int _texW, _texH;
        float _nextDetectAt;
        bool _alignedNow;   // son olcumde hiza olu bolge icinde miydi (tespit hizini belirler)

        /// <summary>
        /// Yerlesim degisti ama rig HENUZ yeni cerceveye oturmadi. Harita degisiminden
        /// ilk basarili duzeltmeye kadar true. Bkz. <see cref="ApplyMapLayout"/>.
        /// </summary>
        bool _layoutStale;

        // ---- YAW REFERANSI GOZCUSU --------------------------------------------------------
        //
        // yawFromReferenceOnly aciksa YONU yalnizca TEK bir tag duzeltiyor
        // (offsetReferenceTagId, varsayilan 0; bkz. satir ~834'teki yawCounts). O tag'in
        // kagidi yirtilir, onune dolap cekilir, ya da hicbir zaman
        // yawCorrectionMaxDistance kadar yaklasilmazsa yaw duzeltmesi SESSIZCE durur.
        //
        // Sessiz olmasinin sebebi: KONUM duzelmeye devam ediyor (onu her tag yapiyor), yani
        // panelde her sey yolunda gorunuyor. Sapma yawRecoveryDegrees'i (10 derece) bulana
        // kadar hicbir belirti yok, ve 10 derece 4 metrede 70 cm demek.
        //
        // Gozcu "ne zamandir goremedim" sorusunu soruyor. Olculemeyen bir sey yoktu ortada —
        // sorulmayan bir sey vardi.
        const float RefStaleSeconds = 300f;   // 5 dakika

        /// <summary>Bu yerlesimin ne zamandir yururlukte oldugu — "hic gorulmedi" suresini olcer.</summary>
        float _layoutSince;

        /// <summary>Uyari log'a bir kez yazilsin; her karede degil.</summary>
        bool _refWarned;

        // ---- CERCEVE TAZELIGI -------------------------------------------------------------
        //
        // Plaka yerlestirme, konuldugu ANDAKI cerceveyi miras aliyor: kaydedilen sey bir oda
        // uzayi hucresi, kagit ise fiziksel duvarda. Ikisini birbirine baglayan tek sey o
        // andaki kalibrasyon.
        //
        // Harita koku DUNYA uzayinda duruyor (MapBuilder.EnsureRoot), duzeltme ise RIG'i
        // oynatiyor. Yani her duzeltme, konmus plakalari passthrough'taki gercek odaya gore
        // KAYDIRIR. Sahada gorulen "tag 1 yerinde durmuyor" tam olarak budur ve bir hata
        // degil: son duzeltmeden bu yana biriken suruklenmenin gorunur hale gelmesidir.
        //
        // Yazilimin yapabilecegi sey suruklenmeyi yok etmek degil -- o SLAM'in isi -- ne
        // zaman guvenilir olmadigini SOYLEMEK. Plakayi taze cercevede koymak, yontemin
        // gecerlilik sarti.
        float _lastCorrectionAt = -1f;

        /// <summary>
        /// Son uygulanan duzeltmeden bu yana gecen sure (sn). Hic duzeltilmediyse -1.
        /// Yerlestirme katmani bunu "plakayi simdi koymak guvenli mi" diye soruyor.
        /// </summary>
        public float SecondsSinceCorrection =>
            _lastCorrectionAt < 0f ? -1f : Time.time - _lastCorrectionAt;

        /// <summary>Kalibrasyon yoksa -1; bkz. <see cref="SecondsSinceCorrection"/>.</summary>
        public static float FrameAgeSeconds =>
            Instance != null ? Instance.SecondsSinceCorrection : -1f;

        /// <summary>Yaw referansinda sorun varsa aciklamasi, yoksa null.</summary>
        string YawReferenceWarning()
        {
            // Yaw'i her tag duzeltiyorsa tek nokta arizasi yok.
            if (!yawFromReferenceOnly) return null;
            if (!CalibrationManager.Calibrated) return null;

            if (Find(offsetReferenceTagId) == null)
                return $"YAW REFERANSI YOK — tag {offsetReferenceTagId} yerlesimde degil";

            bool gorulmus = _seenTime.TryGetValue(offsetReferenceTagId, out float t);
            float gecen = gorulmus ? Time.time - t : Time.time - _layoutSince;
            if (gecen < RefStaleSeconds) return null;

            return gorulmus
                ? $"YAW REFERANSI (tag {offsetReferenceTagId}) {gecen / 60f:0} dk gorulmedi"
                : $"YAW REFERANSI (tag {offsetReferenceTagId}) HIC gorulmedi — yon duzeltilmiyor";
        }

        /// <summary>Uyariyi log'a bir kez yazar; referans yeniden gorununce sifirlanir.</summary>
        // ---- UYANIS KAPISI ----------------------------------------------------------------
        //
        // OLCULDU (24 Agu, cok oyunculu tur): gozluk uykudan uyandi, dunya 175,8 derece donmus
        // geldi, tag 0 YEDI DAKIKA boyunca hic gorulmedi ve oyun bastan sona ters bir dunyada
        // oynandi. Uyku ONCESI olcum dogruydu (fark -0,8), yani donmeyi uyku yaratti.
        //
        // Donme ISABETI bozmaz (kamera da kumanda da rig ile birlikte doner) ama GERCEK oda ile
        // sanal dunyayi ayirir: olmayan yerde siper sanirsin, acikta kalirsin. Cok oyunculuda
        // iki oyuncu farkli yonlerde dunyalar gorur.
        bool _wokeNeedsRef;
        float _wokeAt;

        /// <summary>
        /// Uyandiktan sonra tag 0 gorulene kadar KALICI uyari gosterir.
        ///
        /// TickRefWatch ile ayni yerden cagriliyor cunku ikisi de panelden BAGIMSIZ kosmali:
        /// teshis paneli oyunda kapali ve olayin gorunmez kalmasinin sebebi tam olarak buydu.
        /// </summary>
        void TickWakeGate()
        {
            if (!_wokeNeedsRef) return;

            // Referans uyandiktan SONRA goruldu mu? Uyku oncesi gorulme sayilmaz.
            if (_seenTime.TryGetValue(offsetReferenceTagId, out float t) && t >= _wokeAt)
            {
                _wokeNeedsRef = false;
                WriteDiag($"UYANIS KAPISI ACILDI  tag {offsetReferenceTagId} goruldu " +
                          $"({Time.time - _wokeAt:0.0} sn sonra)");
                if (_cm != null) _cm.HideStatus();
                return;
            }

            // SANIYEDE BIR YAZ, HER KAREDE DEGIL.
            //
            // Panel metni yalnizca saniye sayaci degistiginde degisiyor, ama guncelleme
            // her karede yapiliyordu: 72 fps'te saniyede 72 dizgi ayirma, 72 TextMesh
            // yeniden kurulumu ve 72 Debug.Log (SetStatus her cagrida log yaziyor).
            // Kapinin acik kaldigi olculen sureler 2,5-12 sn; en uzunu ~860 gereksiz
            // log demekti ve Android'de Debug.Log logcat'e gittigi icin ucuz degil.
            int saniye = Mathf.FloorToInt(Time.time - _wokeAt);
            if (saniye == _wokeShownSecond) return;
            _wokeShownSecond = saniye;

            if (_cm == null) _cm = FindFirstObjectByType<CalibrationManager>();
            if (_cm != null)
                _cm.ShowPersistent($"UYKUDAN UYANILDI\n\nYON DOGRULANMADI — TAG {offsetReferenceTagId}'A BAK\n" +
                                   $"({saniye} sn)");
        }

        int _wokeShownSecond = -1;

        void TickRefWatch()
        {
            string uyari = YawReferenceWarning();
            if (uyari == null) { _refWarned = false; return; }
            if (_refWarned) return;
            _refWarned = true;
            WriteDiag("UYARI  " + uyari);
            Debug.LogWarning("[AprilTagCalib] " + uyari);
        }

        // Olcum (FAZ 0): son tespitler uzerinden menzil ve jitter
        // JITTER TAG BASINA tutulur.
        //
        // Eskiden TEK bir kuyruk vardi ve RecordMeasurement dongude HER tag icin cagriliyordu:
        // iki tag ayni anda gorundugunde metrelerce ayrik konumlar ayni kuyruga giriyor,
        // "jitter" diye gosterilen sey aslinda TAG'LER ARASI MESAFE oluyordu. Duzeltme yolunu
        // etkilemiyordu (o ayri, tag basina pencere kullaniyor) ama biz bu sayiya bakarak
        // karar veriyoruz — yalan soyleyen bir olcum, hic olmayandan kotudur.
        readonly Dictionary<int, Queue<Vector3>> _recentByTag = new Dictionary<int, Queue<Vector3>>();
        const int RecentMax = 30;
        int _lastId = -1;
        float _lastDistance;
        float _jitterMm;

        // Panelde EN YAKIN tag gosterilir. Eskiden "dongudeki SON tag" gosteriliyordu ve
        // iki tag gorunurken hangisinin yazdigi dongü sirasina kaliyordu.
        int _nearestId = -1;
        float _nearestDist = float.MaxValue;

        // DIKKAT — iki AYRI zaman: tespit turu her seferinde calisir, ama tag her turda
        // BULUNMAZ. Ilk surumde ikisi karistirilmisti ve panel, arada duvar olsa bile
        // "goruyorum" deyip son mesafeyi donduruyordu.
        float _lastPassTime = -1f;   // tespit turu (Hz hesabi icin)
        float _lastTagTime = -1f;    // YALNIZCA tag gercekten bulundugunda
        float _detectHz;

        /// <summary>Bu turdan once tag'siz gecen sure (sn); hic gorulmediyse -1. Teshis satirlarina yazilir.</summary>
        float _tagGapSeconds = -1f;

        TextMesh _panel;

        /// <summary>
        /// Sahnede tek bir kalibrasyon var; harita yuklendiginde ona kendi tag yerlesimini
        /// gecirebilmek icin erisilebilir olmali. FindFirstObjectByType yerine bu: harita her
        /// acilista degil, HER acilista degisebiliyor ve arama VR karesi icinde yapiliyor.
        /// </summary>
        public static AprilTagCalibration Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        // Harita gelmeden onceki yerlesim (prefab ya da cihaz dosyasi). Harita bosaltilinca
        // buna donulur — yoksa bir haritayi kapatmak oyuncuyu yerlesimsiz birakirdi.
        TagEntry[] _bootLayout;
        bool _fromMap;

        /// <summary>
        /// Haritanin KENDI tag yerlesimini devreye alir. Bos/null gelirse onyukleme yerlesimine
        /// donulur.
        ///
        /// ONCELIK: harita &gt; cihaz dosyasi &gt; prefab. Harita en ozgul olan — tag'ler fiziksel
        /// mekana cakili ve harita o mekanin kaydi. Cihaz dosyasi olcum defteri; prefab ise
        /// hicbir harita acik degilken cerceve kurabilmek icin duran onyukleme.
        /// </summary>
        public void ApplyMapLayout(TagEntry[] fromMap)
        {
            if (_bootLayout == null) _bootLayout = tagLayout;

            bool haritadan = fromMap != null && fromMap.Length > 0;
            if (!haritadan && !_fromMap) return;            // zaten onyuklemedeyiz

            tagLayout = haritadan ? fromMap : _bootLayout;
            _fromMap = haritadan;

            // Yerlesim degisti: gorulen tag'lerin eski cerceveye gore biriktirdigi ornekler
            // artik baska bir dunyaya ait. Temizlenmezse ilk duzeltme iki cercevenin
            // ortalamasini uygular ve nereden geldigi anlasilmaz.
            // Bu silme ADIM 4'TEN SONRA DA GEREKLI: rig-yerel saklama rig'in HAREKETINE karsi
            // koruyor, YERLESIMIN degismesine karsi degil. Tag'in ilan edilen konumu degistiyse
            // eski ornekler baska bir haritanin tag'ini olcmus demektir.
            _recentByTag.Clear();
            _calibLocal.Clear();
            _calibYawLocal.Clear();
            _calibWeight.Clear();
            _calibId = -1;
            _lastTagTime = -1f;

            // CERCEVE BAYAT: rig hala ESKI haritanin cercevesinde duruyor. Yerlesimi
            // degistirmek rig'i oynatmiyor, yani yeni harita oyuncunun etrafina yanlis
            // cercevede kuruluyor — oyuncu duvarin icinde dogabilir.
            //
            // Kendiliginden toparlaniyordu ama YAVAS: tag daha hic gorulmedigi icin
            // tagFresh false, dolayisiyla "busy" false kaliyor ve tespit BOSTA hizinda
            // (1 Hz) suruyordu. Bayrak uc yeri birden duzeltiyor: tespit tam hizda kosar,
            // ilk duzeltme yumusatilmadan ANINDA uygulanir ve panel oyuncuya tag'e bakmasini
            // soyler. Ilk duzeltme uygulanunca dusuyor (bkz. ApplyCorrection).
            _layoutStale = haritadan;

            // Yeni yerlesim, yeni sayac: "referansi hic gormedim" suresi bu andan olculur.
            // Eski yerlesimden kalan gorulme zamani yeni tag 0 icin bir sey soylemiyor.
            _layoutSince = Time.time;
            _seenTime.Remove(offsetReferenceTagId);
            _refWarned = false;

            RebuildMarkers();
            Debug.Log(haritadan
                ? $"[AprilTagCalib] Tag yerlesimi HARITADAN alindi ({fromMap.Length} tag)."
                : "[AprilTagCalib] Harita yerlesimi kalkti — onyukleme yerlesimine donuldu.");
        }

        void Start()
        {
            // DISK SAHNEYI EZER: cihazda olculmus deger, PC'de elle yazilandan guvenilirdir.
            // Dosya yoksa sahnedeki yerlesim varsayilan olarak kalir.
            var stored = TagLayoutStore.Load(layoutVersion);
            if (stored != null)
            {
                tagLayout = stored;
                Debug.Log($"[AprilTagCalib] Yerlesim DISKTEN yuklendi ({stored.Length} tag): " +
                          TagLayoutStore.FilePath);
            }

            // Kumanda ofseti de diskten. Sabit fiziksel bir ozellik oldugu icin oturumlar
            // arasi tasinir; yoksa her acilista yeniden olculmesi gerekir ve unutuldugunda
            // dokunusla yazma sessizce calismaz (cihazda yasandi).
            if (TagLayoutStore.LoadOffset(out Vector3 off, out int cnt, out int rt))
            {
                _storedOffset = off;
                _storedCount = cnt;
                Debug.Log($"[AprilTagCalib] Kumanda ofseti DISKTEN: {off.magnitude * 100f:0.0} cm, " +
                          $"{cnt} ornek (referans tag {rt}).");
            }

            _bootLayout = tagLayout;
            _layoutSince = Time.time;

            // Kalibrasyon, haritayi kuran oturumdan SONRA da uyanabilir (bilesen sirasi
            // garanti degil). Harita zaten acilmissa yerlesimini simdi al — yoksa bu oturum
            // boyunca onyukleme yerlesimiyle, yani baska bir mekanin tag'leriyle calisirdi.
            var sess = Constructor.ConstructorSession.Instance;
            if (sess != null && sess.Layout != null && sess.Layout.tags != null && sess.Layout.tags.Length > 0)
                ApplyMapLayout(sess.Layout.tags);

            RebuildMarkers();
        }

        /// <summary>
        /// Uyku sinirini LOG'A isaretler.
        ///
        /// Gozluk kafadan cikarilinca uygulama duraklatilir. O sinir log'da gorunmezse
        /// "uyandiktan sonraki ilk sapma" ile "zaten devam eden sapma" ayirt edilemez —
        /// oysa uyku sonrasi toparlanmayi olcmenin tek yolu tam olarak o ilk sayidir.
        /// </summary>
        void OnApplicationPause(bool paused)
        {
            WriteDiag(paused ? "=== UYKU (uygulama duraklatildi) ==="
                             : "=== UYANDI — bundan sonraki ilk tag tespiti belirleyici ===");

            // UYANISTA PENCERE TEMIZLENIR — DOGRUDAN isaret.
            //
            // Bayat pencerenin belgelenmis felaketi tam olarak uyku sonrasiydi: gozluk
            // uyandiginda takip uzayi bambaska bir yere oturmustu, penceredeki 15 bayat ornek
            // yeni olcumleri bastirdi, sapma olu bolgenin ALTINDA kaldi ve panel surekli
            // "HIZALI" yazdi. Duzeltme yapilmadigi icin pencere de temizlenmedi — kalici
            // kilitlenme. Aykiri eleme bunu yakalayamaz: esigi 15 cm, olay ise 1 cm'nin
            // altinda kalan bir bastirma.
            //
            // Bu, calibWindowMaxGap'in asil korudugu durumdu ve zaman esigi onun DOLAYLI
            // vekiliydi: "uzun sure ornek gelmediyse belki uyumustur". Uyku sinyalinin
            // kendisi elimizde oldugu icin vekile gerek yok — esik artik yalnizca "tag gorus
            // alanindan cikti" durumunu kaplıyor ve olculen degere gore gevsetilebiliyor.
            if (!paused)
            {
                _calibLocal.Clear();
                _calibYawLocal.Clear();
                _calibWeight.Clear();
                _calibId = -1;

                // UYANIS KAPISI: takip uzayi uyku boyunca DONMUS olabilir ve bunu yalnizca
                // tag 0 duzeltebilir. Bayrak, referans tekrar gorulene kadar oyuncuya
                // KALICI bir uyari gosterir (bkz. TickWakeGate).
                _wokeNeedsRef = CalibrationManager.Calibrated;
                _wokeAt = Time.time;
                _wokeShownSecond = -1;

                // Kapinin DEVREYE GIRDIGI de yazilir, yalnizca acildigi degil. Aksi halde
                // "uyari cikti mi" sorusu log'dan cevaplanamiyor ve testte tahmin gerekiyor.
                WriteDiag(_wokeNeedsRef
                    ? "UYANIS KAPISI DEVREDE — tag " + offsetReferenceTagId + " gorulene kadar uyari"
                    : "UYANIS KAPISI ATLANDI (henuz kalibre degil — normal akis zaten uyaracak)");
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _detector?.Dispose();
            _detector = null;
            if (_panel != null) Destroy(_panel.gameObject);
            ClearMarkers();
        }

        void Update()
        {
            // HER KAREDE okunur. Tespit turu 1-3 Hz'de calisir; buton okumasi o kapinin
            // ARDINDA kalsaydi basislarin cogu kacardi (saniyede bir ornekleme).
            TickLearnInput();
            TickTouch();   // her karede: en yakin yaklasmayi kacirmamak icin
            TickFloor();
            TickHeadMotion();
            TickFrameFreshness();

            // OGRENME MODUNDAN BAGIMSIZ: passthrough bir GORUNTULEME tercihi, olcum araci
            // degil. Ogrenmeye bagliyken "paneli kapat" demek passthrough'u da kapatiyor ve
            // oyuncuyu boslukta birakiyordu — harita zemin uretmiyor, sahnede de zemin yok.
            EnsurePassthrough();

            if (Time.time < _nextDetectAt) { TickPanel(); return; }

            // YENI KARE YOKSA ISLEME. _nextDetectAt'i ILERLETMEDEN donuyoruz ki kare gelir
            // gelmez islensin — aksi halde tazelik kontrolu tespit hizini yariya dusururdu.
            if (!_frameDirty)
            {
                if (++_staleSkips >= _nextStaleReport)
                {
                    // Seyrek atlama normaldir; SIK atlama kamera akisinin bozuldugunu soyler.
                    WriteDiag($"BAYAT KARE atlandi (toplam {_staleSkips})");
                    _nextStaleReport = _staleSkips * 4;
                }
                TickPanel();
                return;
            }
            _frameDirty = false;

            // UYARLANIR HIZ: tespit pahali (tam cozunurluklu GetPixels32 + tag arama, ana
            // is parcaciginda). Macin buyuk kisminda tag kadrajda degil ya da hiza zaten iyi —
            // o sure boyunca tam hizda taramak bosuna. Is varken hizlan, yokken yavasla.
            bool tagFresh = _lastTagTime > 0f && Time.time - _lastTagTime < 2f;
            // HENUZ KALIBRE DEGILSEK tam hizda tara. tagFresh ilk tespitte zorunlu olarak
            // false (tag daha hic gorulmedi), yani ilk tur idle hizinda geciyordu: 1 Hz =
            // oyuncunun tag'e bakip bosuna bekledigi 1 saniye. Tasarruf edilecek bir sey yok,
            // oyuncu zaten duvara bakmis bekliyor.
            // _layoutStale de "is var" sayilir: harita degistiginde tag daha hic gorulmedigi
            // icin tagFresh false ve kapi kapali kalirdi -- tam da en hizli aramamiz gereken
            // anda 1 Hz'de tarardik.
            bool busy = (tagFresh && !_alignedNow) || !CalibrationManager.Calibrated || _layoutStale;
            float rate = busy ? detectionsPerSecond : idleDetectionsPerSecond;
            _nextDetectAt = Time.time + 1f / Mathf.Max(0.2f, rate);

            var tex = GetCameraTexture();
            if (tex == null || tex.width <= 16) { TickPanel(); return; }

            EnsureDetector(tex.width, tex.height);
            tex.GetPixels32(_pixels);

            // TAM INTRINSICS. Eskiden yalnizca fy'den bir DIKEY FOV turetilip veriliyordu; poz
            // isi de o tek sayidan fx = fy uretip ana noktayi GORUNTU MERKEZI kabul ediyordu.
            //
            // Ana nokta varsayimi asil zararlisiydi: gercek ana nokta merkezden d piksel
            // kaymissa, tag'in goruntudeki YERINE bagli d/f radyanlik bir yon hatasi olusur.
            // Kamera hareket ettikce tag goruntude gezer, hata da onunla degisir — yani
            // ortalamayla bastirilamayan, bakis noktasina gore degisen sistematik bir sapma.
            // Iki tag'i farkli acilardan olcup "ayni duvarda ama 10 cm ayrik" cikmasinin
            // birinci sanigi budur. 600 px odakta 30 px kayma = 2,9 derece = 2 metrede 10 cm.
            //
            // fx != fy de ayrica onemli: tek FOV sayisi ikisini esitlemeye zorluyordu.
            var intr = PassthroughCameraUtils.GetCameraIntrinsics(_camMgr.Eye);

            // OLCEKLEME: intrinsics sabit bir REFERANS cozunurluk icin tanimli, biz ise
            // tex.width x tex.height okuyoruz. Ikisi farkliysa fx, fy, cx, cy'nin HEPSI ayni
            // oranla olceklenmeli. Eski kodda bu yapilmiyordu ve "RequestedResolution
            // kucultulurse tum mesafeler kayar" uyarisi tam bu eksikligi anlatiyordu.
            float sx = intr.Resolution.x > 0 ? (float)tex.width / intr.Resolution.x : 1f;
            float sy = intr.Resolution.y > 0 ? (float)tex.height / intr.Resolution.y : 1f;

            double fx = intr.FocalLength.x * sx;
            double fyy = intr.FocalLength.y * sy;
            double cx = intr.PrincipalPoint.x * sx;
            double cy = intr.PrincipalPoint.y * sy;

            // Bozulmus/eksik intrinsics gelirse eski yola dus — kalibrasyonsuz kalmak,
            // sacma bir odak uzakligiyla calismaktan iyidir.
            if (fx < 1.0 || fyy < 1.0)
            {
                float fbFy = intr.FocalLength.y > 1f ? intr.FocalLength.y : intr.FocalLength.x;
                float fovVertical = 2f * Mathf.Atan(tex.height / (2f * fbFy));
                _detector.ProcessImage(_pixels, fovVertical, tagSizeMeters);
            }
            else
            {
                LogIntrinsicsOnce(intr, tex.width, tex.height, fx, fyy, cx, cy);
                _detector.ProcessImage(_pixels, fx, fyy, cx, cy, tagSizeMeters);
            }

            float now = Time.time;
            if (_lastPassTime > 0f) _detectHz = 1f / Mathf.Max(0.0001f, now - _lastPassTime);
            _lastPassTime = now;

            // TAG'SIZ GECEN SURE — tur basina BIR kez, tag'ler islenmeden ONCE olculur
            // (RecordMeasurement _lastTagTime'i eziyor; sonra bakilsa hep ~0 cikardi).
            //
            // NEDEN GEREKLI: buyuk bir duzeltmenin SEBEBINI log'dan okuyabilmek icin.
            // Iki bambaska olay ayni satiri uretiyor ve ayirt edilemiyordu:
            //   duzlemsel belirsizlik flipi -> ardisik kareler arasi olur (bosluk ~0,3 sn)
            //   takip kopmasi/relokalizasyon -> uzun sessizlikten sonra olur (saniyeler)
            // Taban cizgisinde tam bu karisti: 4,96 m'lik sicramayi flip sandim, oysa
            // gozluk cikarilip tag 1'e yuruyup takilmisti ve arada 21 saniye tag yoktu.
            // Bosluk yazilsaydi soru hic sorulmayacakti.
            _tagGapSeconds = _lastTagTime > 0f ? now - _lastTagTime : -1f;

            var camPose = PassthroughCameraUtils.GetCameraPoseInWorld(_camMgr.Eye);

            // COKLU TAG: olcum HER tag icin yapilir, kalibrasyon YALNIZCA BIR tag ile.
            //
            // Neden tek tag ile duzeltiyoruz: ContinuousCorrect rig'i OYNATIR. Ayni karede
            // ikinci bir tag'le daha duzeltmek, ikincinin hesabini birincinin tasidigi yeni
            // cerceveye uygulamak demektir -- ust uste binen duzeltmeler sapma uretir.
            //
            // Secim olcutu EN YAKIN: jitter mesafeyle karesel buyuyor (olculdu: 1 m'de 3 mm,
            // 2 m'de 15 mm), yani en yakin tag her zaman en guvenilir olandir. Agirlikli
            // fuzyon (birden fazla tag'i birlestirme) gerekmiyor; gerekirse sonraki faz.
            TagEntry bestEntry = null;
            float bestScore = float.MaxValue;   // histerezisli secim puani (bkz. calibSwitchMargin)
            float bestDist = float.MaxValue;    // GERCEK mesafe — esiklerde bu kullanilir
            Vector3 bestPos = Vector3.zero;
            Quaternion bestRot = Quaternion.identity;
            _nearestId = -1;
            _nearestDist = float.MaxValue;

            // Fuzyon adaylari KARE BASINA toplanir; onceki karenin kalintisi
            // birikirse artik gorulmeyen tag'ler cozume girer.
            _fuseMeasured.Clear(); _fuseDeclared.Clear(); _fuseWeight.Clear();
            _fuseDist.Clear(); _fuseId.Clear();
            _autoMapId.Clear(); _autoMapPos.Clear(); _autoMapYaw.Clear();

            // TESHIS: tag'in GORUNTUDEKI yeri. Hem lens distorsiyonu hem ana nokta hatasi
            // KONUMA BAGLI etkiler — kadrajin ortasindaki tag ile kenarindaki tag farkli
            // yanilir. "Tag neredeydi" bilgisi olmadan bu ikisini olcmek mumkun degil.
            // Ayni tag'i merkezde ve kenarda olcup sonuc degisiyorsa suclu bunlardan biridir.
            _seenPixel.Clear();
            foreach (var d in _detector.RawDetections)
            {
                var c = d.Center;
                _seenPixel[d.ID] = new Vector2((float)c.x, (float)c.y);
            }

            foreach (var tag in _detector.DetectedTags)
            {
                // Tag'in DUNYA pozu (mevcut, muhtemelen kaymis rig'e gore).
                Vector3 worldPos = camPose.position + camPose.rotation * tag.Position;
                Quaternion worldRot = camPose.rotation * tag.Rotation;
                float dist = tag.Position.magnitude;

                // OLCUM her tag icin yapilir — hangi tag'i gordugumuzu ve ne kadar iyi
                // gordugumuzu bilmek istiyoruz, yerlesimde tanimli olup olmamasi onemsiz.
                RecordMeasurement(tag.ID, dist, worldPos);

                // OLCULEN poz saklanir — dokunus yakinlik testi bunu kullanir. Bkz. TouchAnchor:
                // ilan edilen degere baglanirsa yanlis tahmin dokunusu hic tetiklemez.
                _seenPos[tag.ID] = worldPos;
                _seenYaw[tag.ID] = YawOf(worldRot);
                _seenTime[tag.ID] = Time.time;

                // HAREKET KAPISI: kare ile kafa pozu ayni ana ait olmadigi icin, kafa
                // hareket ederken cikan poz sistematik olarak kaymistir. Panelde gorunmeye
                // devam etsin (oyuncu tag'i gordugunu bilsin) ama CERCEVEYE KARISMASIN.
                //
                // Esik MESAFEYE gore: uzaktaki tag ayni donme hizindan cok daha fazla etkilenir.
                if (!MotionOk(dist)) continue;

                // NORMAL KONVANSIYONU: hareket kapisindan SONRA oylanir — kafa hareket
                // ederken cikan poz sistematik olarak kaymis, oyu kirletir.
                ProbeNormalSign(worldRot, worldPos, camPose.position);

                // POZ GECERLILIK KAPISI (Adim 3). 3a'da yalnizca raporluyor, hicbir sey
                // elemiyor; 3b'de poseGateLogOnly kapatilinca burasi 'continue' eder.
                if (!PoseGate(tag.ID, worldRot, worldPos, camPose.position, dist)) continue;

                // YAW ENVANTERI — zemin kurulumunda kagidin hangi yone yapistirildigini
                // OLCEREK ogrenmenin tek yolu. Plakanin gorsel yonu ile sistemin yaw diye
                // okudugu eksen (zeminde tag'in kendi yukari ekseni, bkz. YawOf) ayni olmak
                // ZORUNDA DEGIL; ikisi arasindaki sabit kayma ancak burada gorunur.
                //
                // Yalnizca poz kapisindan gecmis, hareket kapisindan gecmis olcumler yazilir:
                // egik ya da hareketli bir okumadan cikan yaw zaten guvenilmez ve envanteri
                // kirletirdi. Tag basina 5 saniyede bir.
                {
                    var yerlesik = Find(tag.ID);
                    if (yerlesik != null &&
                        (!_yawEnvanterAt.TryGetValue(tag.ID, out float sonYazim) ||
                         Time.time - sonYazim >= 5f))
                    {
                        _yawEnvanterAt[tag.ID] = Time.time;
                        float olculen = YawOf(worldRot);
                        float fark = Mathf.DeltaAngle(yerlesik.yawDegrees, olculen);
                        WriteDiag($"YAW ENVANTERI  tag {tag.ID}  olculen {olculen:+0.0;-0.0}" +
                                  $"  ilan {yerlesik.yawDegrees:+0.0;-0.0}  fark {fark:+0.0;-0.0}" +
                                  $"  d {dist:0.00} m");
                    }
                }

                // OTOMATIK HARITALAMA ADAYI: yerlesimde OLMAYAN tag. Kapilardan gecmis
                // olcum; cercevenin guvenilir olup olmadigina asagida, fuzyon cozuldukten
                // SONRA bakilacak — o karar burada verilemez.
                if (autoMapUnknownTags && Find(tag.ID) == null && dist <= calibrateMaxDistance)
                {
                    _autoMapId.Add(tag.ID);
                    _autoMapPos.Add(worldPos);
                    _autoMapYaw.Add(YawOf(worldRot));
                }

                if (learnMode)
                    Learn(tag.ID, dist, worldPos, worldRot);

                // KALIBRASYON adayi: yalnizca yerlesimde TANIMLI tag'ler yarisir.
                if (autoCalibrate)
                {
                    var entry = Find(tag.ID);

                    // HISTEREZIS: kazanan tag'i degistirmek kayan pencereyi TEMIZLIYOR.
                    // Iki tag benzer mesafedeyse en-yakin secimi her turda salinir, pencere
                    // hicbir zaman dolmaz ve kalibrasyon TAMAMLANMAZ. Cihazda goruldu: sayac
                    // 4'e kadar cikip sifirlaniyordu. Mevcut tag'e avantaj taniyoruz —
                    // yenisi belirgin olcude yakin olmali.
                    float score = (entry != null && entry.id == _calibId)
                                ? dist - calibSwitchMargin : dist;

                    if (entry != null && entry.useForCalibration && score < bestScore)
                    {
                        bestScore = score;
                        bestEntry = entry;
                        bestDist = dist;
                        bestPos = worldPos;
                        bestRot = worldRot;
                    }

                    // FUZYON ADAYI. Kazanan secimiyle ayni kapilardan gecmis olan HER tag
                    // toplanir — fuzyon icin "en yakin" diye bir sey yok, hepsi birlikte
                    // cozuluyor. Mesafe kesmesi burada uygulanir: tek-tag yolunda bu kontrol
                    // ContinuousCorrect'in icinde, fuzyonun oraya ugramasi gerekmiyor.
                    if (useMultiTagFusion && entry != null && entry.useForCalibration &&
                        dist <= calibrateMaxDistance)
                    {
                        _fuseMeasured.Add(worldPos);
                        _fuseDeclared.Add(entry.position);
                        _fuseWeight.Add(SampleWeight(dist));
                        _fuseDist.Add(dist);
                        _fuseId.Add(entry.id);
                    }

                    // KAPALI tag KONTROLU KALDIRILDI. Yeni bir tag'i dogrulamak icin konulmustu:
                    // "plaka kaymis ama NE KADAR" sorusunu cevapliyordu. Yerini dokunus yontemi
                    // aldi — artik sapmayi olcup elle duzeltmiyoruz, konumu dogrudan kumandadan
                    // yaziyoruz. Panel sadelestiginde gosterimi kalkti, hesap ise her tespitte
                    // calismaya devam ediyordu: olu kod.
                }
            }

            // Tek seferlik DEGIL — tag her gorulduginde hiza kontrol edilir, gerekiyorsa
            // duzeltilir. Boylece uyku / konum degisimi / drift sonrasi kendini onarir.
            // Kazanan tag degisirse ContinuousCorrect kayan pencereyi zaten temizler
            // (_calibId kontrolu), yani tag'ler arasi gecis pozu bozmaz.
            // Panel degerleri EN YAKIN tag'den — dongudeki sonuncusundan degil.
            if (_nearestId >= 0)
            {
                _lastId = _nearestId;
                _lastDistance = _nearestDist;
                _jitterMm = JitterMmFor(_nearestId);
            }

            // FUZYON ONCE DENENIR, tek-tag yolu YEDEK. Iki yol ayni karede birden
            // calismaz: ikisi de rig'i oynatiyor ve ikincisi, birincinin tasidigi yeni
            // cerceveye kendi hesabini uygulardi -- ust uste binen duzeltmeler sapma uretir
            // (ayni gerekce tek-tag yolunda da yaziyor).
            bool fuzyonUygulandi = _fuseMeasured.Count >= 2 && FuseCorrect();

            // OTOMATIK HARITALAMA yalnizca fuzyon BASARILI olduysa. Kosul sert bilerek:
            // fuzyonun uygulanmis olmasi demek, en az iki BILINEN tag'in ayni karede
            // birbiriyle ve yerlesimle kalinti esiginin altinda uyustugu demek. Cerceve o
            // anda dogrulanmis durumda; bilinmeyen bir tag'i ancak boyle bir cercevede
            // olcmek anlamli.
            if (fuzyonUygulandi && _autoMapId.Count > 0) TickAutoMap();

            if (!fuzyonUygulandi && bestEntry != null)
                ContinuousCorrect(bestEntry, bestDist, bestPos, bestRot);

            TickPanel();
        }

        // Kayan pencere: son yakin olcumler (ortalanir). RIG-YEREL saklanir — bkz. ToLocal.
        readonly List<Vector3> _calibLocal = new List<Vector3>();
        readonly List<float> _calibYawLocal = new List<float>();
        readonly List<float> _calibWeight = new List<float>();   // Adim 5 — bkz. SampleWeight
        float _calibLastSampleAt = -999f;   // bkz. calibWindowMaxGap
        int _outlierRun;                    // ust uste kac aykiri ornek geldi
        int _calibId = -1;

        // ---- RIG-YEREL SAKLAMA (Adim 4) ---------------------------------------------------
        //
        // NEDEN: olcum aslinda "tag, gozlugun TAKIP UZAYINDA su noktada" diyor. Bunu dunya
        // uzayina ceviren sey rig transformu; yani dunya koordinati rig'in nerede oldugu
        // varsayimini ICINDE tasiyor. Rig duzeltilince o varsayim degisiyor ve penceredeki
        // eski ornekler artik var olmayan bir cerceveyi anlatiyor — bu yuzden her duzeltmeden
        // sonra pencere SILINMEK ZORUNDAYDI.
        //
        // Silinince ne oluyordu: duzeltme -> 5 ornek daha bekle -> duzeltme. Bosta tespit
        // ~1 Hz oldugu icin bu ~5 saniye. "5 saniye sabit bak" zorunlulugu kaldirilmamis,
        // yer degistirmisti.
        //
        // Rig-yerel koordinat rig'in kendi hareketinden ETKILENMEZ: rig oynayinca ornek de
        // onunla tasinir ve hala ayni fiziksel noktayi gosterir. Pencere yasamaya devam eder.
        //
        // ToWorld(ToLocal(p)) == p oldugu surece olcek onemsiz; rig olcegi 1 ama bagli
        // degiliz — gidis donus ayni transformu kullaniyor.
        Vector3 ToLocal(Vector3 world) => _rig != null ? _rig.InverseTransformPoint(world) : world;
        Vector3 ToWorld(Vector3 local) => _rig != null ? _rig.TransformPoint(local) : local;

        // Rig YALNIZCA Y ekseninde donduruluyor (ApplyCorrection: RotateAround(..., Vector3.up)),
        // o yuzden yaw icin tek bir aci cikarmak/eklemek yeterli.
        float RigYaw => _rig != null ? _rig.eulerAngles.y : 0f;

        // ---- ORNEK AGIRLIGI (Adim 5) ------------------------------------------------------
        //
        // Pencerede 0,5 m'den ve 1,9 m'den gelen ornekler AYNI agirliktaydi; kendi olcumumuz
        // bunu yalanliyor: 1 m'de 3 mm, 2 m'de 15 mm jitter.
        //
        // Duzlemsel poz kestiriminde konum hatasi mesafenin KARESIYLE buyuyor (tag goruntude
        // kucüldükçe ayni piksel hatasi daha cok metreye karsilik geliyor). sigma ~ d^2 ise
        // varyans ~ d^4, ve ters-varyans agirligi 1/d^4 olur. Uydurma bir us degil, olcumun
        // kendi modeli.
        //
        // Bunun bedeli SU AN calibrateMaxDistance ile odeniyordu: uzak ornegi agirliklandirmak
        // yerine TAMAMEN atmak. Agirlikli ortalamada kesme yumusuyor — uzak ornek hak ettigi
        // kadar katki veriyor, hiç yoksa da yok sayilmiyor.
        //
        // 1 m REFERANS ALINIR: w = (1/d)^4. Boylece 1 m'de w=1, 0,5 m'de 16, 2 m'de 0,0625.
        // Oranlar 1/d^4 ile ayni, sayilar okunabilir kaliyor ve toplam tasma riski yok.
        //
        // MESAFE ALTTAN KIRPILIR: gozluk tag'e 25 cm'den fazla yaklasamaz (kamera odak ve
        // gorus alani). Kirpmadan, hatali kucuk bir d tek basina butun pencereyi ele gecirirdi.
        const float WeightRefDistance = 1f;
        const float WeightMinDistance = 0.25f;

        static float SampleWeight(float distance)
        {
            float d = Mathf.Max(WeightMinDistance, distance);
            float r = WeightRefDistance / d;
            return r * r * r * r;
        }

        /// <summary>
        /// Dairesel standart sapma (derece). <paramref name="dir"/> agirlikli birim vektorlerin
        /// bileskesi, <paramref name="totalW"/> agirliklarin toplami.
        ///
        /// R = |bileske| / toplam agirlik, sonuc sqrt(-2 ln R). R=1 (hepsi ayni yon) -> 0 derece.
        /// R kucüldükçe sacilma hizla buyur.
        ///
        /// R alttan kirpilir: Log(0) eksi sonsuz doner ve tek bir NaN butun kestirimi sessizce
        /// zehirlerdi. Ustten de kirpilir — kayan nokta yuvarlamasi R'yi 1'in bir tik ustune
        /// cikarabiliyor ve Log negatif olunca karekok NaN veriyor.
        /// </summary>
        static float YawSpreadDegrees(Vector2 dir, float totalW)
        {
            if (totalW <= 0f) return 0f;
            float R = Mathf.Clamp(dir.magnitude / totalW, 1e-6f, 1f);
            return Mathf.Sqrt(-2f * Mathf.Log(R)) * Mathf.Rad2Deg;
        }

        /// <summary>Rig'i cozer. ORNEKLEMEDEN ONCE cagrilmali: rig bilinmeden alinan bir ornek
        /// dunya koordinatinda saklanip sonra yerel sanilirdi.</summary>
        bool EnsureRig()
        {
            if (_rig != null) return true;
            if (_cm == null) _cm = FindFirstObjectByType<CalibrationManager>();
            _rig = _cm != null ? _cm.rig : null;
            return _rig != null;
        }

        /// <summary>
        /// Duzeltme icin gereken EN AZ ornek.
        ///
        /// Kalibre degilken 2: ilk duzeltme metre mertebesindedir, 3 mm'lik ornekleme hatasi
        /// yaninda gurultu bile sayilmaz ve ortalama beklemek oyuncuyu bosuna oyalar.
        /// Kalibreyken 5: duzeltmeler cm'ye iner, orada ortalama gercekten degerlidir.
        ///
        /// SAYIYI BUYUTMEK ISE YARAMAZ. Ortalama gurultuyu kok-N ile bastirir: 5 ornek 1 m'de
        /// 3 mm'lik jitter'i 1,3 mm'ye indiriyor, zaten olu bolgenin (1 cm) coktan altinda.
        /// 50 ornege cikmak 17 saniye bekletir ve karsiliginda ~1 mm kazandirir; bizi yakan
        /// hata ise ortalamayla GECMEYEN sistematik hata. Kalite sayidan degil, asagidaki
        /// kararlilik kapisindan gelir.
        /// </summary>
        int CalibNeed => CalibrationManager.Calibrated ? Mathf.Min(5, calibrateSampleCount) : 2;

        /// <summary>Ilerleme cubugu: "[####------] 4/5".</summary>
        static string ProgressBar(int have, int need)
        {
            int w = 10;
            int f = need <= 0 ? w : Mathf.Clamp(Mathf.RoundToInt(w * have / (float)need), 0, w);
            return "[" + new string('#', f) + new string('-', w - f) + $"] {have}/{need}";
        }
        string _calibNote = "";

        // ---- TESHIS (gecici, sorun cozulunce kaldirilabilir) ------------------------------
        // Belirti: panel "duzeltildi -> 0/5 -> duzeltildi" dongusune giriyor, yani duzeltme
        // uygulaniyor ama sapma bir turlu olu bolgenin altina inmiyor. Duzeltmeyi geri alan
        // birileri var; hangi EKSENDE oldugunu bilmeden dogru yeri aramak tahmin olur.
        //
        // Bu uc deger her degerlendirmede yazilir ve panelde KALICI durur (_calibNote gibi
        // aninda ezilmez), boylece duzeltme sonrasi sapmanin kapanip kapanmadigi okunabilir.
        //   dy kapanmiyor  -> dikeyi ezen var (XROrigin floor offset supheli)
        //   dx/dz kapanmiyor -> yatayda baska bir yazici var
        //   hepsi kapaniyor ama yine duzeltiyor -> esik/gurultu sorunu
        Vector3 _diagDelta;

        // Tag gecisi olcumu: bir tag'den otekine gecerken olusan sapma = iki tag'in
        // yerlesimdeki degerlerinin BIRBIRIYLE uyusmazligi. Test C'nin sayisal karsiligi.
        Transform _rightHandDiag;   // sag kumanda — dokunus ve zemin olcumu (TickTouch/TickFloor)

        Vector3 _switchDelta;
        float _switchYawDev;
        int _switchFrom = -1, _switchTo = -1;
        bool _switchPending, _hasSwitch;


        Transform _rig;
        CalibrationManager _cm;   // rig + CompleteFromTag icin; ilk duzeltmede bir kez bulunur
        float _nextStateDiagAt;   // teshis yazimini kisitlar (bkz. ApplyCorrection)
        float _nextSpreadDiagAt;  // KARARSIZ satirini kisitlar — her karede yazilirdi
        float _diagYawSpread;     // son olculen yaw sacilmasi (Adim 5) — teshis satirinda

        // ADIM 6'NIN GIRDILERI. Alan olarak tutuluyorlar, parametre olarak degil: ayni desen
        // _diagYawSpread'de zaten var ve tek cagri yeri (ApplyCorrection) kapinin hemen
        // ardinda, yani bayatlamalari mumkun degil.
        float _diagSpreadRatio;   // kabul edilen pencerenin sacilma / sinir orani
        int _diagSampleCount;     // o penceredeki ornek sayisi

        /// <summary>
        /// SUREKLI, kendini onaran hizalama. Tag her gorulduginde:
        ///   - yakin degilse "yaklas" (jitter mesafeyle buyur, uzaktan hizalama kotu)
        ///   - birkac kare biriktir + ortala (tek titrek kareye guvenme)
        ///   - tag olmasi gereken yerden SAPMISSA (olu bolgeden fazla) rig'i duzelt
        ///   - sapma olu bolge icindeyse DOKUNMA (jitter'dan snap yapmasin)
        /// Tek seferlik kilit YOK: uyku / konum degisimi / drift sonrasi kendini toparlar.
        /// </summary>
        void ContinuousCorrect(TagEntry entry, float distance, Vector3 worldPos, Quaternion worldRot)
        {
            // Rig ORNEKLEMEDEN once cozulmeli: pencere rig-yerel saklaniyor, rig bilinmezken
            // alinan bir ornek dunya koordinatinda girip sonra yerel sanilirdi.
            if (!EnsureRig()) { _calibNote = "rig yok"; return; }

            if (distance > calibrateMaxDistance)
            {
                _calibNote = $"yaklas ({distance:0.00} > {calibrateMaxDistance:0.00} m)";
                // PENCERE SILINMEZ. Ornekler rig-yerel, yani uzaklasmak onlari gecersiz
                // kilmiyor; oyuncu geri yaklastiginda kaldigi yerden devam eder. Eskiden
                // silinmesinin sebebi dunya-uzayi saklamaydi, o sebep kalkti.
                _alignedNow = true;   // bu mesafede yapilacak is yok -> tespit hizlanmasin
                return;
            }
            // TAG GECISI: kazanan tag degisti. Pencere temizlenir (eski ornekler oteki tag'e
            // ait). Ayrica gecisten SONRAKI ilk sapmayi yakalamak istiyoruz — o sayi iki tag'in
            // BIRBIRINE ne kadar uymadigini dogrudan olcer. Duzeltme uygulandiktan sonraki
            // sapma hep ~0 cikar (hizalanmis olur) ve bu bilgiyi gizler.
            bool justSwitched = _calibId >= 0 && entry.id != _calibId;
            if (justSwitched)
            {
                _calibLocal.Clear(); _calibYawLocal.Clear(); _calibWeight.Clear();
                _switchFrom = _calibId;
                _switchPending = true;
            }
            _calibId = entry.id;

            // ZAMAN BOSLUGU: son ornekten bu yana uzun sure gectiyse pencere TEMIZLENIR.
            //
            // Ornekler bir CERCEVEYE aittir. Tag gorus alanindan cikip geri geldiginde ya da
            // gozluk uyuyup uyandiginda cerceve degismis olabilir; eski ornekler artik var
            // olmayan bir dunyayi anlatir. Ortalamaya karisinca sapmayi SEYRELTIRLER.
            //
            // CIHAZDA YASANDI: uyku sonrasi tag 2 acikca hizasizken, penceredeki 15 bayat
            // ornek yeni olcumleri bastirdi, sapma olu bolgenin altinda kaldi ve panel surekli
            // "HIZALI" yazdi. Duzeltme yapilmadigi icin pencere de temizlenmedi — kalici
            // kilitlenme. Tag'i gorus alanindan cikarip geri bakmak da ise yaramiyordu, cunku
            // bayat ornekler orada duruyordu.
            //
            // ADIM 4'TEN SONRA DA DURUYOR. Rig-yerel saklama bu kapinin BIR sebebini ortadan
            // kaldirdi (rig'in kendi hareketi), otekini KALDIRMADI: gozlugun takip uzayi
            // zamanla kayiyor ve uyku sonrasi bambaska bir yere oturuyor. Yukaridaki olay tam
            // olarak buydu. Silinme artik "her duzeltmeden sonra" degil "tag 2 saniye
            // gorunmediginde" oluyor — kaldirmak istedigimiz bekleme bu degildi.
            //
            // TESHIS SATIRI ADIM 4 ICIN SART. Adim 4'un kabul olcutu "duzeltmeden sonra sayac
            // 0/5'e dusmuyor". Sayac yine de duserse iki ihtimal var ve ayirt edilebilmeli:
            // (a) rig-yerel saklama calismiyor, (b) bosta tespit 1 Hz ve bu kapi 2 sn — yani
            // tek bir gecikmis kare pencereyi siliyor. (b) ise cozum esigi buyutmek, kodu geri
            // almak degil. Log yazmadan bu ayrim turda yapilamaz.
            if (_calibLocal.Count > 0 && Time.time - _calibLastSampleAt > calibWindowMaxGap)
            {
                WriteDiag($"PENCERE SILINDI  bosluk {Time.time - _calibLastSampleAt:0.0} sn " +
                          $"> {calibWindowMaxGap:0.0}  ({_calibLocal.Count} ornek atildi)");
                _calibLocal.Clear(); _calibYawLocal.Clear(); _calibWeight.Clear();
            }
            _calibLastSampleAt = Time.time;

            // AYKIRI ORNEK ELEMESI — zaman esiginden daha DOGRUDAN bir olcut.
            //
            // Zaman boslugu, "cerceve degismis olabilir"in DOLAYLI gostergesiydi: tag birkac
            // saniye gorunmedi diye pencereyi atiyorduk, oysa hicbir sey degismemis de olabilir.
            // Cerceve degistiginin GERCEK isareti, yeni olcumun penceredekilerle taban tabana
            // zit cikmasidir. Onu dogrudan sinayabiliyoruz.
            //
            // Tek bir aykiri ornek pencereyi bozmaz (bozuk bir tespit olabilir); UST USTE
            // gelirse dunya gercekten oynamis demektir ve pencere atilir.
            // Karsilastirma da YEREL uzayda: pencere yerel saklandigi icin dunya koordinatiyla
            // kiyaslamak, arada bir duzeltme olduysa her ornegi aykiri gosterirdi.
            Vector3 localPos = ToLocal(worldPos);

            if (_calibLocal.Count > 0)
            {
                Vector3 m = Vector3.zero;
                foreach (var q in _calibLocal) m += q;
                m /= _calibLocal.Count;

                if (Vector3.Distance(localPos, m) > calibOutlierDistance)
                {
                    if (++_outlierRun >= 2)
                    {
                        _calibLocal.Clear(); _calibYawLocal.Clear(); _calibWeight.Clear();
                        _outlierRun = 0;
                    }
                    else
                    {
                        // Tek seferlik sapma: orneği ATLA, pencereyi koru.
                        // Burada da pencere eksik kaliyor, yani is var (bkz. sayac kapisi).
                        _alignedNow = false;
                        _calibNote = $"olculuyor {ProgressBar(_calibLocal.Count, CalibNeed)}";
                        return;
                    }
                }
                else _outlierRun = 0;
            }

            // Kayan pencereye ekle, en fazla calibrateSampleCount tut.
            // AGIRLIK ORNEKLE BIRLIKTE SAKLANIR: ornegin alindigi andaki mesafe onun kalitesini
            // belirliyor ve o mesafe sonradan bilinemez (oyuncu hareket ediyor).
            _calibLocal.Add(localPos);
            _calibYawLocal.Add(YawOf(worldRot) - RigYaw);
            _calibWeight.Add(SampleWeight(distance));
            while (_calibLocal.Count > calibrateSampleCount)
            {
                _calibLocal.RemoveAt(0); _calibYawLocal.RemoveAt(0); _calibWeight.RemoveAt(0);
            }

            // ILK hizalamada AZ ornek yeter, sonrakilerde cok.
            // Ilk duzeltme metre mertebesindedir — 3 mm'lik ornekleme hatasi yaninda gurultu
            // bile sayilmaz, ortalama almak bosa beklemektir. Kalibre olduktan SONRA duzeltmeler
            // cm mertebesine iner ve ortalama gercekten degerli olur; orada 5 ornek kalir.
            // Kotu bir ilk hizalama zaten kendini onarir: bir sonraki tespit duzeltir.
            int need = CalibNeed;
            if (_calibLocal.Count < need)
            {
                // PENCERE DOLDURMAK "IS"TIR — tespit yavaslamamali.
                //
                // Bu satir olmadan _alignedNow bir onceki hizalamadan kalma true degerinde
                // kaliyordu, busy false cikiyordu ve pencere IDLE hizinda (1 Hz) doluyordu:
                // 5 ornek = 5 saniye. Cihazda olculdu (2026-08-18 drift turu): dokuz bakis
                // kopmasinin dokuzunda da duzeltme satiri PENCERE SILINDI'den tam 4-5 sn
                // sonra geldi. detectionsPerSecond zaten 3 idi, yani hiz ayari degil bu kapi
                // darboğazdi — 5 ornek artik ~1,7 sn'de doluyor.
                //
                // Gerekce, satir 750'deki "henuz kalibre degilsek tam hizda tara" notunun
                // aynisi: oyuncu duvara bakmis bekliyor, tasarruf edilecek bir sey yok.
                _alignedNow = false;
                _calibNote = $"olculuyor {ProgressBar(_calibLocal.Count, need)}";
                return;
            }

            // AGIRLIKLI ortalama (Adim 5). Ortalama YEREL alinir, sonra dunyaya cevrilir —
            // asagisi (sapma, GECIS, ApplyCorrection) dunya uzayinda calisiyor.
            float wTotal = 0f;
            Vector3 avgLocal = Vector3.zero;
            for (int i = 0; i < _calibLocal.Count; i++)
            {
                avgLocal += _calibLocal[i] * _calibWeight[i];
                wTotal += _calibWeight[i];
            }
            // wTotal sifir olamaz (SampleWeight her zaman pozitif) ama bolme once kontrol
            // edilir: pencere ile agirlik listesi bir sekilde ayrisirsa sessiz NaN uretmesin.
            if (wTotal <= 0f) { _calibNote = "agirlik yok"; return; }
            avgLocal /= wTotal;
            Vector3 avgPos = ToWorld(avgLocal);

            // KARARLILIK KAPISI — sayiyla degil, TUTARLILIKLA olculur.
            //
            // "N ornek topladim" tek basina bir sey soylemez: ornekler birbirini tutmuyorsa
            // ortalamalari da tutmaz. Onemli olan pencerenin ne kadar SIKI oldugu. Bu, sabit
            // bir bekleme suresi dayatmaktan hem daha hizli (ornekler zaten kararliysa hemen
            // gecer) hem daha guvenli (kararsizsa sayac dolsa bile beklemeye devam eder).
            //
            // Ayrica mikro hareket artik sayaci SIFIRLAMIYOR — yalnizca sacilmayi buyutuyor,
            // yani kendi kendini duzelten yumusak bir ceza.
            // Sacilma YEREL uzayda olculur. Rigid donusum mesafeleri korudugu icin sayi
            // dunyadakiyle ayni; yerel kalmasinin sebebi karsilastirmanin ayni uzayda olmasi.
            //
            // AGIRLIKSIZ ORTALAMAYA GORE OLCULUR — bilerek. Adim 5'in agirlikli ortalamasi
            // yakin ornege sonuna kadar yaslaniyor (0,5 m'deki ornek 2 m'dekinden 256 kat agir),
            // yani uzak ornekler ondan UZAK duser. Sacilmayi o merkeze gore olcseydik uzak
            // ornekler kapiyi bosuna tetiklerdi — ustelik zaten neredeyse hic katki vermeyen
            // ornekler yuzunden. Kullanicinin sikayet ettigi "KARARSIZ 15 ornek" durumu
            // SIKLASIRDI, azalmazdi.
            //
            // Kapinin isi degismedi: "pencere kendi icinde tutarli mi". Adim 5 KESTIRIMI
            // degistiriyor, kapiyi degil — boylece turda hangisinin ne yaptigi ayirt edilebilir.
            Vector3 plainMean = Vector3.zero;
            foreach (var p in _calibLocal) plainMean += p;
            plainMean /= _calibLocal.Count;

            float spread = 0f;
            foreach (var p in _calibLocal) spread = Mathf.Max(spread, Vector3.Distance(p, plainMean));

            // ESIK MESAFEYLE OLCEKLENIR — sacilma standart sapma oldugu icin us 2 (bkz. alanin
            // yorumu). Simdiki mesafe kullaniliyor: pencere son birkac saniyeye ait, oyuncu
            // isinlanmiyor. Karisik mesafeli bir pencerede en buyuk mesafe esigi gevsetiyor,
            // ki bu da dogru — o pencere gercekten daha genis sacilir.
            float spreadLimit = calibStabilitySpread;
            if (calibStabilityScalesWithDistance)
            {
                float dn = Mathf.Max(WeightMinDistance, distance) / WeightRefDistance;
                spreadLimit *= dn * dn;
            }

            if (spread > spreadLimit)
            {
                // SAYI DOLDUKTAN SONRA ILERLEME CUBUGU YAZILMAZ.
                //
                // Cihazda goruldu: panel "13/5", "14/5", "15/5" yaziyordu. Pay paydayi gecince
                // cubuk anlamsizlasiyor ve oyuncuya "sayiyor ama bitmiyor" hissi veriyor; oysa
                // bekleyen sey SAYI degil TUTARLILIK. Adim 4'ten once pencere her duzeltmede
                // silindigi icin bu durum nadiren goruluyordu, simdi pencere yasadigi icin
                // 15'e kadar dolabiliyor.
                _calibNote = _calibLocal.Count < need
                    ? $"olculuyor {ProgressBar(_calibLocal.Count, need)}  (sabitleniyor {spread * 100f:0.0} cm)"
                    : $"KARARSIZ  sacilma {spread * 100f:0.0} cm > {spreadLimit * 100f:0.0}  ({_calibLocal.Count} ornek)";

                // DISKE de yaz — ama yalnizca pencere DOLUYKEN ve seyrek. Dolu pencerede
                // kapinin tutmasi, Adim 4'un olcmedigimiz yan etkisi: 15 ornek artik daha uzun
                // bir zamana ve daha genis bir mesafe araligina yayiliyor, sistematik mesafe
                // hatasi da sacilmaya giriyor. Ne siklikta oldugunu bilmeden esige dokunmayiz.
                if (_calibLocal.Count >= calibrateSampleCount && Time.time >= _nextSpreadDiagAt)
                {
                    _nextSpreadDiagAt = Time.time + 5f;
                    WriteDiag($"KARARSIZ  tag {entry.id}  sacilma {spread * 100f:0.0} cm > " +
                              $"{spreadLimit * 100f:0.0}  ({_calibLocal.Count} ornek)  d {distance:0.00} m");
                }
                return;
            }

            // ADIM 6 OLCUMU. Buraya kadar gelen pencere KABUL EDILMIS demektir ve sacilmasi
            // simdiye kadar hicbir yere yazilmiyordu — KARARSIZ satiri yalnizca sinir asilinca
            // yaziliyor, yani elimizde REDDEDILENLERIN dagilimi vardi, kabul edilenlerin degil.
            // Kazanci bu sayiya baglamadan once sayinin kendisi olculmeli.
            _diagSpreadRatio = spreadLimit > 0f ? spread / spreadLimit : 0f;
            _diagSampleCount = _calibLocal.Count;

            // YAW da AYNI AGIRLIKLA ortalanir: yon kestirimi konumla ayni pozdan geliyor, yani
            // mesafeyle ayni sekilde bozuluyor.
            Vector2 dir = Vector2.zero;
            for (int i = 0; i < _calibYawLocal.Count; i++)
            {
                float y = _calibYawLocal[i] * Mathf.Deg2Rad;
                dir += new Vector2(Mathf.Sin(y), Mathf.Cos(y)) * _calibWeight[i];
            }
            float avgYaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg + RigYaw;

            // YAW SACILMASI — dairesel ortalamadan BEDAVA gelen olcu.
            //
            // Dairesel ortalamada birim vektorlerin bileskesinin boyu (R) aynilik olcusudur:
            // hepsi ayni yonu gosteriyorsa R=1, dagilmissa R kucülür. sqrt(-2 ln R) bunu
            // standart sapmaya cevirir.
            //
            // NEDEN ONEMLI: duzlemsel poz belirsizliginin flip'i, KONUMU neredeyse hic
            // oynatmadan yon'u ziplatir — iki cozum ayni noktayi farkli acilarla gorur. Bu
            // yuzden tek bir konum kararlilik kapisi (calibStabilitySpread) flip'i hicbir
            // zaman yakalayamadi: konum sacilmasi kucuk kaliyordu. Yon sacilmasi flip'in en
            // dogrudan imzasi.
            float yawSpread = YawSpreadDegrees(dir, wTotal);
            _diagYawSpread = yawSpread;   // teshis satirinda yazilacak (ApplyCorrection)

            // Tag olmasi gereken yerden ne kadar sapmis?
            float dev = Vector3.Distance(avgPos, entry.position);
            float yawDev = Mathf.Abs(Mathf.DeltaAngle(avgYaw, entry.yawDegrees));

            // TESHIS: eksen bazli sapma. Duzeltme yapilsin yapilmasin yazilir — "duzeltti ama
            // kapanmadi" durumunu ancak duzeltme sonrasi deger okunarak gorulur.
            _diagDelta = entry.position - avgPos;

            // Gecisten sonraki ILK olcum: iki tag'in uyusmazligi. Duzeltme uygulanmadan
            // once yakalanir ve KALICI durur — oyuncunun okumaya vakti olsun.
            if (_switchPending)
            {
                _switchPending = false;
                _switchDelta = _diagDelta;
                // GECIS = iki tag'in yerlesim degerlerinin BIRBIRIYLE uyusmazligi. Panelde
                // gorunuyordu ama diske yazilmiyordu; en cok ihtiyac duyulan sayi bu.
                // ISARETLI: duzeltmeyi uygulayabilmek icin yonu de lazim. yawDev mutlak deger
                // oldugu icin "1.8 derece" hangi yone bilinmiyordu.
                _switchYawDev = Mathf.DeltaAngle(avgYaw, entry.yawDegrees);
                _switchTo = entry.id;
                _hasSwitch = true;

                WriteDiag($"GECIS  tag {_switchFrom} -> {_switchTo}  " +
                          $"dx {_switchDelta.x:+0.000;-0.000} dy {_switchDelta.y:+0.000;-0.000} " +
                          $"dz {_switchDelta.z:+0.000;-0.000}  toplam {_switchDelta.magnitude:0.000} m  " +
                          $"yaw {_switchYawDev:+0.00;-0.00}");
            }

            // Bu tag'in yaw'i cerceveyi dondurebilir mi?
            //
            // Karar burada da verilmeli, yalnizca ApplyCorrection'da degil: yaw
            // uygulanmayacaksa yaw sapmasi duzeltmeyi TETIKLEMEMELI. Aksi halde sapma hic
            // kapanmaz ve her tespitte bosuna duzeltme calisir — sonsuz dongü.
            // KURTARMA KAPISI VAR AMA COK YUKSEKTE.
            //
            // Ilk denemede esik snapThresholdDegrees'ti (3 derece) ve bu FELAKETTI: yaw olcum
            // gurultusu zaten 1-3 derece, yani kapi gurultuyu "kurtarma" sanip suruyor aciliyordu
            // ve KACAK bir geri besleme kuruyordu -- sapma buyur, kapi acilir, dunya doner,
            // uzaktaki tag daha da sapar, kapi yine acilir. Cihazda olculdu: 2,4 cm'de seyreden
            // gecisler tek bir yaw duzeltmesinden sonra 10,7 -> 20,4 -> 9,3 -> 11,6 -> 12,8 cm
            // diye salindi, yakinsamadi.
            //
            // Ama kapiyi tamamen kapatmak da yanlis: uykudan uyanma ya da takip kaybi sonrasi
            // yon GERCEKTEN kaybolabilir ve o zaman duzeltecek baska bir sey yok. Kaybi
            // gurultuden ayiran sey buyukluk -- gercek kayip 10 derece mertebesindedir,
            // gurultu tabaninin cok uzaginda. Esik oraya konuldu.
            // Kurtarmanin karari yalnizca su iki durumda bir sey DEGISTIRIR:
            //   referans olmayan tag -> yaw'i zaten hic duzeltemezdi
            //   referans ama UZAK    -> mesafe kapisi kapatirdi
            // Digerlerinde referans yolu yaw'i nasilsa uyguluyor; log'un bunu "reddedildi"
            // diye yazmasi yaniltiyordu (bkz. YawRecoveryAccepted'in etkili parametresi).
            bool refTag = !yawFromReferenceOnly || entry.id == offsetReferenceTagId;
            bool uzak = yawCorrectionMaxDistance > 0f && distance > yawCorrectionMaxDistance;
            bool yawRecovery = YawRecoveryAccepted(yawDev, entry.id, !refTag || uzak);
            bool yawCounts = refTag || yawRecovery;

            // REFERANS TAG'DE BILE yaw yalnizca YAKINDAN duzeltilir. Duzlemsel poz kestiriminde
            // duzlem disi acinin hatasi, tag'in goruntudeki buyuklugu kucüldükce hizla artar;
            // uzaktan olculen yaw duzelttiginden fazla hata katar.
            //
            // KURTARMA bu kisittan MUAF: yon tamamen kaybolduysa oyuncuyu once tag'e 1,5 m
            // yaklasmaya zorlamak, dunyasi 20 derece donukken yurumesi demek olurdu.
            if (yawCounts && !yawRecovery &&
                yawCorrectionMaxDistance > 0f && distance > yawCorrectionMaxDistance)
                yawCounts = false;

            // YAW SACILMA KAPISI (Adim 5) — esik 0 iken KAPALI, yalnizca olculuyor.
            //
            // Konum duzeltilmeye DEVAM eder, yalnizca yon birakilir. Mimari bu ayrimi zaten
            // destekliyor (yawCounts konumdan bagimsiz), cunku ayni ayrim mesafe kapisinda da
            // var: uzaktan konum guvenilir, yon degil.
            //
            // KURTARMA MUAF: yon gercekten kaybolduysa (bkz. YawRecoveryAccepted, uc ardisik
            // teyit) onu sacilma yuzunden bloke etmek, oyuncuyu 69 derece donuk bir dunyada
            // birakmak olurdu — cihazda tam o olay yasandi.
            if (yawCounts && !yawRecovery &&
                yawSpreadMaxDegrees > 0f && yawSpread > yawSpreadMaxDegrees)
            {
                yawCounts = false;
                WriteDiag($"YAW SACILMA  tag {entry.id}  {yawSpread:0.00} > {yawSpreadMaxDegrees:0.00} derece " +
                          $"— yon birakildi, konum duzeltiliyor");
            }

            if (dev <= correctionDeadzoneMeters &&
                (!yawCounts || yawDev <= correctionYawDeadzoneDegrees))
            {
                _calibNote = $"HIZALI ({dev * 100f:0.0} cm)";
                _alignedNow = true;    // is yok -> tespit yavaslasin
                return;   // tolerans icinde — dokunma, jitter'dan snap olmasin
            }

            _alignedNow = false;       // duzeltme gerekiyor -> tespit hizlansin
            ApplyCorrection(entry, avgPos, avgYaw, dev, yawCounts, distance);

            // ADIM 4'UN ASIL SATIRI: pencere ARTIK TEMIZLENMIYOR.
            //
            // Eskiden burada Clear() vardi cunku ornekler dunya uzayindaydi ve rig oynayinca
            // gecersizlesiyorlardi. Artik rig-yerel: rig oynadi, ornekler de onunla tasindi,
            // hala ayni fiziksel noktayi gosteriyorlar. Bir sonraki tespit dolu bir pencereye
            // dusuyor ve duzeltme aninda calisabiliyor.
            //
            // Kacak duzeltme korkusu yersiz: duzeltmeden sonra ToWorld(ornekler) tam olarak
            // entry.position'a oturuyor, yani sapma ~0 ve olu bolge kapisi yukarida donuyor.
        }

        // ---- NORMAL KONVANSIYONU (Adim 1'in kalani) ---------------------------------------
        //
        // Tespit pozunun +Z'si tag'in ONUNE mi ARKASINA mi bakiyor? Uc donusum ust uste
        // biniyor (native cozucu, PoseEstimationJob'un Y flip'i, PassthroughCameraUtils'in
        // 180 derece X donusu) ve bileske kagit uzerinde cikarilamadi.
        //
        // FIZIK CEVABI ZATEN VERIYOR: opak bir tag'i ancak ON yuzunden gorebilirsiniz. Dogru
        // isaret, dot(normal, tag->kamera) > 0 verendir. Oylama ilk ~20 tespitte kesinlesir.
        //
        // YAW tarafi 2026-08-11'de ayrica olculdu ve yawDegrees'in duvarin ICINI gosterdigi
        // bulundu; bu oylama onu DOGRULAMALI (isaret negatif cikmali). Cikmazsa ikisinden biri
        // yanlis demektir ve Adim 3'un yuz yonu sinamasi guvenilmez olur.
        int _normalSign;        // 0 = bilinmiyor, +1 / -1 = karar
        int _votesPlus, _votesMinus;
        const int NormalVotesNeeded = 20;

        /// <summary>Tag pozunun normali (yuzey ekseni) — dunya uzayinda.</summary>
        static Vector3 TagNormal(Quaternion worldRot) => worldRot * Vector3.forward;

        void ProbeNormalSign(Quaternion worldRot, Vector3 worldPos, Vector3 camPos)
        {
            if (_normalSign != 0) return;

            Vector3 tagToCam = camPos - worldPos;
            if (tagToCam.sqrMagnitude < 1e-6f) return;

            if (Vector3.Dot(TagNormal(worldRot), tagToCam.normalized) > 0f) _votesPlus++;
            else _votesMinus++;

            int toplam = _votesPlus + _votesMinus;
            if (toplam < NormalVotesNeeded) return;

            _normalSign = _votesPlus > _votesMinus ? +1 : -1;
            WriteDiag($"NORMAL KONVANSIYONU: tag ekseni {(_normalSign > 0 ? "+Z kameraya" : "+Z duvara")} " +
                      $"bakiyor  ({Mathf.Max(_votesPlus, _votesMinus)}/{toplam} oy)");
            Debug.Log($"[AprilTagCalib] Normal konvansiyonu: isaret {_normalSign:+0;-0} " +
                      $"({_votesPlus} arti / {_votesMinus} eksi). " +
                      "Beklenen -1 (yaw olcumu duvarin icini gosteriyordu).");
        }

        // ---- POZ GECERLILIK KAPISI (Adim 3) -----------------------------------------------
        //
        // KONUMU DEGIL DONMEYI siniyor. Konum zaten guvenilir (1 m'de 3 mm); elenemeyen sey
        // donmeydi. Uc bagimsiz sinama, hepsi FIZIKSEL bir gercege dayaniyor:
        //   1. Yuz yonu     -> opak kagidi ancak on yuzunden gorebilirsin
        //   2. Normal yatay -> kagit DUVARDA, normali yatay olmak zorunda
        //   3. Tag dik      -> kagit duvarda DIK duruyor, kendi ekseni de dunya dikeyiyle hizali
        //
        // Ucu de yerçekimine dayaniyor ve yerçekimi IMU'dan geliyor: tag tespitinin hicbir
        // hatasini paylasmiyor. Ortalamayla gecmeyen sistematik hatayi eleyebilen tek
        // bagimsiz kapi bu.
        int _poseChecked, _poseWouldReject;

        bool PoseValid(Quaternion worldRot, Vector3 worldPos, Vector3 camPos,
                       out string neden)
        {
            neden = null;
            Vector3 normal = TagNormal(worldRot);

            // 1) YUZ YONU. Isaret henuz oylanmadiysa bu sinama ATLANIR — bilinmeyen bir
            //    isaretle elemek, dogru okumalari elemenin en kolay yolu olurdu.
            if (_normalSign != 0)
            {
                Vector3 tagToCam = camPos - worldPos;
                if (tagToCam.sqrMagnitude > 1e-6f)
                {
                    float d = Vector3.Dot(normal, tagToCam.normalized) * _normalSign;
                    if (d <= 0f) { neden = $"yuz yonu ters (dot {d:0.00})"; return false; }
                }
            }

            // 2) NORMAL YATAY. Kagit DUVARDA, normali yatay olmak zorunda.
            //    |normal.y| = sin(yataydan sapma).
            float normalTilt = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(normal.y))) * Mathf.Rad2Deg;
            if (normalTilt > maxNormalTiltDegrees)
            {
                neden = $"normal {normalTilt:0.0} derece egik (sinir {maxNormalTiltDegrees:0})";
                return false;
            }

            // 3) TAG EKSENI dunya dikeyiyle hizali olmali (kagit duvarda DIK duruyor).
            float tagTilt = Vector3.Angle(worldRot * Vector3.up, Vector3.up);
            if (tagTilt > 90f) tagTilt = 180f - tagTilt;   // bas asagi da olsa EGIKLIK olcuyoruz
            if (tagTilt > maxTagTiltDegrees)
            {
                neden = $"tag ekseni {tagTilt:0.0} derece sapmis (sinir {maxTagTiltDegrees:0})";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Kapiyi calistirir ve 3a'da yalnizca RAPORLAR. Donen deger "bu tespit kullanilsin mi";
        /// <see cref="poseGateLogOnly"/> aciksa her zaman true doner.
        /// </summary>
        bool PoseGate(int tagId, Quaternion worldRot, Vector3 worldPos, Vector3 camPos, float dist)
        {
            _poseChecked++;

            if (PoseValid(worldRot, worldPos, camPos, out string neden)) return true;

            _poseWouldReject++;
            // Her red YAZILIR: oran bu satirlardan cikacak ve 3b'ye gecip gecmeyecegimize
            // o oran karar verecek.
            WriteDiag($"POZ {(poseGateLogOnly ? "ELENECEKTI" : "ELENDI")}  tag {tagId}  {neden}" +
                      $"  d {dist:0.00} m  ({_poseWouldReject}/{_poseChecked} = " +
                      $"%{100f * _poseWouldReject / Mathf.Max(1, _poseChecked):0.0})");
            return poseGateLogOnly;
        }

        // ---- YAW KURTARMA BANDI -----------------------------------------------------------
        //
        // Kurtarma ISIN degil BANT. Eskiden tek bir alt esik vardi
        // ("yawDev > yawRecoveryDegrees") ve kapi USTTEN SINIRSIZ kaliyordu: ne kadar buyuk
        // olursa olsun her sapma "gercek kayip" sayilip yawFromReferenceOnly ve
        // yawCorrectionMaxDistance kisitlarini BIRDEN baypas ediyordu. Yani korunmak istenen
        // sey kapidan iceri aliniyordu.
        //
        // TABAN CIZGISINDE OLCULDU (2026-08-12, EV haritasi, 6,5 dakika, 2 tag):
        //   [361,6] SNAP tag 1  sapma 496,2 cm  yaw +147,30
        // Tag 1 referans DEGIL, yani yonu duzeltmemeliydi. 147 > 10 oldugu icin kurtarma
        // acildi ve tek bir okuma dunyayi 4,96 m kaydirdi. Ayni turda mesru bir kurtarma da
        // vardi (+40,29, REFERANS tag'den) -- ikisini ayirmak gerekiyordu.
        //
        // AYIRAN SEY TEKRARLANABILIRLIK: duzlemsel poz belirsizligi flipi kareler arasi
        // ziplar, ust uste ayni degeri vermez. Gercek takip kaybi verir, cunku dunya
        // gercekten o kadar donmustur. Bu yuzden bandin ustu REDDEDILMIYOR, TEYIT istiyor.
        int _bigYawRun;        // ust uste kac kez ayni buyuk sapma goruldu
        float _bigYawFirst;    // dizinin ilk degeri -- karsilastirma buna gore
        int _bigYawTag = -1;   // hangi tag'den; tag degisince dizi bastan baslar

        /// <summary>Teyit dizisinde "ayni deger" sayilma toleransi (derece).</summary>
        const float BigYawTolerance = 5f;

        /// <summary>
        /// Bu sapma icin kurtarma kapisi acilsin mi? Sayaci da bu metot yonetiyor — kapiyi
        /// cagiran yerde tutmak, normal okumalarda sifirlamayi unutturuyordu.
        /// </summary>
        /// <param name="etkili">
        /// Bu kapinin karari yaw'in uygulanip uygulanmayacagini GERCEKTEN degistiriyor mu.
        ///
        /// NEDEN GEREKLI: referans tag zaten yaw'i duzeltiyor (yawCounts'un ilk kosulu), yani
        /// onda kurtarmanin reddedilmesi hicbir seyi engellemiyor. Ilk surumde kapi yine de
        /// "YAW REDDEDILDI" yaziyordu ve log yalan soyluyordu — cihazda goruldu:
        ///   [24,0] YAW REDDEDILDI tag 0  97,9 derece
        ///   [24,0] SNAP tag 0 ... yaw -97,91          <- "(uygulanmadi)" YOK, yani UYGULANDI
        /// Sayaç yine de islenmeli (dizi surekliligi bozulmasin), yalnizca YAZILMAMALI.
        /// </param>
        bool YawRecoveryAccepted(float yawDev, int tagId, bool etkili)
        {
            // Kurtarma gerekmiyor: dizi varsa bozulur, cunku arada normal bir okuma gecti.
            if (yawDev <= yawRecoveryDegrees) { _bigYawRun = 0; return false; }

            // BANT ICI: gercek kayip bu mertebede, dogrudan kabul.
            if (yawDev <= yawRecoveryMaxDegrees) { _bigYawRun = 0; return true; }

            // BANDIN USTU: teyit iste. Farkli tag ya da farkli buyukluk diziyi bastan baslatir.
            bool devam = _bigYawRun > 0 && _bigYawTag == tagId &&
                         Mathf.Abs(yawDev - _bigYawFirst) <= BigYawTolerance;
            if (devam) _bigYawRun++;
            else { _bigYawRun = 1; _bigYawFirst = yawDev; _bigYawTag = tagId; }

            if (_bigYawRun >= yawRecoveryConfirmations)
            {
                if (etkili)
                    WriteDiag($"YAW TEYITLI  tag {tagId}  {yawDev:0.0} derece  " +
                              $"({_bigYawRun} ardisik) — gercek kayip sayildi");
                _bigYawRun = 0;
                return true;
            }

            // REDDEDILEN HER OKUMA YAZILIR — ama YALNIZCA red bir sey degistiriyorsa.
            // Bu satirlarin SAYISI, sorunun gercekten burada olup olmadiginin cevabi:
            // sifirsa flip baska yerden geliyor demektir. Etkisiz redleri de yazmak o sayiyi
            // sisirir ve "engellendi" diye okunur, oysa yaw referans yolundan uygulanmistir.
            if (etkili)
                WriteDiag($"YAW REDDEDILDI  tag {tagId}  {yawDev:0.0} derece  " +
                          $"(bant ustu, {_bigYawRun}/{yawRecoveryConfirmations} teyit)");
            return false;
        }

        /// <summary>
        /// Rig'i, olculen (ortalanmis) tag olmasi gereken yere denk gelecek sekilde hizalar.
        /// Mantik <see cref="CalibrationManager.Apply"/> ile ayni: once yaw etrafinda dondur,
        /// sonra otele — egim ASLA uygulanmaz. Anchor'a devretmez; tag'in KENDISI surekli
        /// referans (anchor tracking'i 'None' oldugunda ise yaramiyordu, ustelik LateUpdate'te
        /// tag'in duzeltmesini eziyordu).
        /// </summary>
        /// <summary>
        /// Duzeltme kazancinin carpani (0-1]. 1 = bugunku davranis, yani sabit
        /// <see cref="smallCorrectionRate"/>.
        ///
        /// ADIM 6'NIN YALNIZCA YARISI. Plan kazanci iki belirsizlige baglamayi oneriyordu:
        /// olcum belirsizligi ve son duzeltmeden bu yana biriken odometri suruklenmesi.
        /// IKINCISI OLCULDU VE DUSURULDU (2026-08-18, drift turu, ofis): tag 8-56 sn goruus
        /// disinda birakilip donuldugunde dokuz donusun ALTISINDA sapma olu bolgenin (1 cm)
        /// altinda kaldi; kalan ucu 38,3 sn -> 1,8 cm, 51,4 sn -> 1,7 cm, 56,4 sn -> 2,6 cm.
        /// Benzer bosluklar arasindaki sacilma trendin kendisi kadar buyuk (35,2 sn'de
        /// <=1 cm, 38,3 sn'de 1,8 cm), yani suruklenme gurultuden ayirt edilemiyor; en kotu
        /// durum bile ~0,05 cm/sn'lik bir UST SINIR veriyor. Ustelik zaman teriminin motive
        /// edici vakasi olan uyku sonrasi toparlanma bu hesaba hic ugramiyor: uykudan sonra
        /// sapma <see cref="snapThresholdMeters"/> esigini asar ve rate zaten 1 olur.
        /// "Olculmemis sayi koda girmez" kuralinin dogal sonucu: driftRatePerSecond YOK.
        ///
        /// Kalan yari OLCULU: pencerenin kendi sacilmasi. Ortalamanin standart hatasi
        /// sacilma/sqrt(N); referans olarak kabul sinirinin dolu penceredeki hali alinir.
        /// Boylece YENI BIR SABIT GIRMIYOR — calibStabilitySpread ve calibrateSampleCount
        /// zaten var ve ikisi de olculmus.
        ///
        /// KAZANC ASLA BUGUNKUNDEN BUYUK OLMAZ, bilerek. Kucuk sacilma "olcum dogru"
        /// demek DEGIL: bu dosyanin kendi notuna gore baskin hata bakis acisina bagli
        /// SISTEMATIK sapma, ve sistematik sapmanin sacilmasi kucuktur. Kazanci sacilma
        /// kucukken 1'e dogru buyutmek, tam da en emin gorunen anda yanlis cevaba kosmak
        /// olurdu — planin EKF'i reddetme gerekcesinin aynisi.
        /// </summary>
        float CorrectionGain()
        {
            if (_diagSampleCount <= 0) return 1f;   // olcum yok: davranis degismesin

            // Sacilma/sinir orani, ornek sayisiyla duzeltilir: yarim dolu bir pencere ayni
            // sacilmada daha az guvenilir, cunku ortalamanin standart hatasi sqrt(N) ile duser.
            float oran = _diagSpreadRatio *
                         Mathf.Sqrt(calibrateSampleCount / (float)Mathf.Max(1, _diagSampleCount));

            // Kalman kazancinin skaler hali. SIFIRA INMEZ ve inmemeli: 5 sn'den uzun her bakis
            // kopmasinda pencere siliniyor (calibWindowMaxGap) ve 5 ornekle bastan basliyor.
            // Drift turunda pencere 6,3 dakikada DOKUZ kez silindi — nadir bir durum degil,
            // ve o anlarda kazanci sifirlamak duzeltmeyi tamamen durdururdu.
            return 1f / (1f + oran * oran);
        }

        // ---- COKLU TAG FUZYONU ------------------------------------------------------------
        //
        // Kare basina toplanan adaylar. Tek-tag yolunun kayan penceresinden AYRI tutuluyor:
        // o pencere "tek tag'in zaman icindeki ortalamasi", bu liste "ayni ANDAKI tag'ler".
        readonly List<Vector3> _fuseMeasured = new List<Vector3>();
        readonly List<Vector3> _fuseDeclared = new List<Vector3>();
        readonly List<float> _fuseWeight = new List<float>();
        readonly List<float> _fuseDist = new List<float>();
        readonly List<int> _fuseId = new List<int>();

        /// <summary>YAW ENVANTERI icin tag basina son yazim ani — her karede yazmak dosyayi bogar.</summary>
        readonly Dictionary<int, float> _yawEnvanterAt = new Dictionary<int, float>();
        float _nextFuseDiagAt;

        /// <summary>Fuzyonun en son karar verdigi an. Panel bunu okuyor — bkz. PanelText.</summary>
        float _fuseAppliedAt = -999f;

        /// <summary>Son BASARILI fuzyonun RMS kalintisi (m); hic olmadiysa -1.</summary>
        float _fuseResidual = -1f;

        /// <summary>O fuzyonda kac tag vardi.</summary>
        int _fuseTagCount;

        /// <summary>Fuzyon SU AN mi suruyor. Tek bir karelik boslukta panelin eski mesaja
        /// donup yanip sonmemesi icin kisa bir kuyruk birakiliyor.</summary>
        bool FusionDriving => Time.time - _fuseAppliedAt < 1.5f;

        /// <summary>
        /// Cerceve SU AN coklu tag fuzyonuyla mi suruluyor, ve son kalintisi ne?
        ///
        /// Yerlestirme katmani bunu "plakayi simdi basmak guvenli mi" diye soruyor: plaka
        /// konuldugu andaki cerceveyi KALICI olarak miras aliyor, yani cerceve o an ne kadar
        /// sapiksa tag o kadar yanlis kaydediliyor ve hata sonraki tag'lere de tasiniyor.
        ///
        /// false donmesi "cerceve kotu" demek DEGIL: tek tag goruluyorsa fuzyon hic calismaz.
        /// O durumda tazelige bakilmali (bkz. <see cref="SecondsSinceCorrection"/>).
        /// </summary>
        public bool FusionQuality(out int tagCount, out float residualMeters)
        {
            tagCount = _fuseTagCount;
            residualMeters = _fuseResidual;
            return FusionDriving;
        }

        /// <summary>Kalibrasyon yoksa false; bkz. <see cref="FusionQuality"/>.</summary>
        public static bool FrameFusion(out int tagCount, out float residualMeters)
        {
            if (Instance != null) return Instance.FusionQuality(out tagCount, out residualMeters);
            tagCount = 0;
            residualMeters = -1f;
            return false;
        }

        /// <summary>
        /// Ayni karede gorulen tag'leri BIRLIKTE cozer: olculen konumlari ilan edilen
        /// konumlara en iyi oturtan yaw + oteleme.
        ///
        /// NEDEN TEK TAG'DEN IYI: duzlemsel bir isaretcinin en guvenilmez bileseni kendi
        /// yaw'idir (1-3 derece, bakis acisina bagli, ortalamayla GECMIYOR); konumu ise mm
        /// mertebesinde. Fuzyon yonu tag'lerin DONUSUNDEN degil KONUMLARINDAN turetiyor,
        /// yani sistemin zayif olcumunu hic kullanmiyor. 3 m arayla iki tag icin 15 mm'lik
        /// konum gurultusu 0,29 derecelik yon gurultusu demek.
        ///
        /// COZUM: agirlikli Procrustes, yalnizca yaw + oteleme (pitch/roll ASLA — dunyayi
        /// yan yatirmak mide bulandirir). Agirlik tek-tag yolundakiyle ayni: 1/d^4.
        ///
        /// KENDI KENDINI DOGRULAR: cozumden sonra kalan artik hata (RMS), tag'lerin
        /// birbiriyle ve yerlesimle ne kadar uyustugunu dogrudan olcer. Yerlesim hatasi,
        /// bozuk tespit ve flip -- ucu de tek sayida gorunur. Esigi asarsa duzeltme
        /// uygulanmaz ve tek-tag yoluna dusulur.
        ///
        /// YAN ETKI, BILINCLI: fuzyon calistigi karede tek-tag yolu calismaz, yani onun
        /// kayan penceresi DOLMAZ. Surekli iki tag goren bir turda sonra tek tag'e dusulurse
        /// pencere sifirdan dolar (~1,7 sn). Kabul edildi: fuzyon zaten daha iyi bir cozum
        /// veriyorken pencereyi bosuna beslemek, ayni kareye iki farkli olcum mantigi sokardi.
        /// </summary>
        /// <returns>
        /// true = fuzyon karari verdi (rig'e dokundu ya da "hizali" dedi);
        /// false = cozemedi, tek-tag yolu denesin.
        /// </returns>
        // ---- OTOMATIK HARITALAMA ------------------------------------------------------
        //
        // Kare basina toplanan BILINMEYEN tag'ler (yerlesimde yok).
        readonly List<int> _autoMapId = new List<int>();
        readonly List<Vector3> _autoMapPos = new List<Vector3>();
        readonly List<float> _autoMapYaw = new List<float>();

        /// <summary>Tag basina biriken ornekler. RIG-YEREL saklanir (bkz. ToLocal): rig
        /// duzeltilince ornekler onunla tasinir ve gecersizlesmez.</summary>
        class AutoMapOrnek
        {
            public readonly List<Vector3> Yerel = new List<Vector3>();
            public readonly List<float> Yaw = new List<float>();   // rig-yerel yaw
            public float SonOrnekAt;
        }
        readonly Dictionary<int, AutoMapOrnek> _autoMap = new Dictionary<int, AutoMapOrnek>();

        /// <summary>
        /// Bilinmeyen tag'leri, DOGRULANMIS bir cercevede olcup yerlesime ekler.
        ///
        /// YALNIZCA FUZYON BASARILIYKEN cagrilir: en az iki bilinen tag ayni karede
        /// birbiriyle ve yerlesimle uyusmus demektir. Tek tag'in kurdugu cerceveye
        /// guvenmek, bu projede olculmus bir hataya yol acti — plakalar oyle konmustu ve
        /// 1-2,8 m saptilar.
        ///
        /// YAW DA OLCULUR. Plakadan turetilen yaw, plakanin donus referansiyla zemin
        /// tag'inin yaw konvansiyonu arasindaki farka bagliydi ve 180 derece ters cikiyordu.
        /// Olcerek yazmak o sorunu kokunden kaldiriyor: ne olculduyse o yaziliyor.
        ///
        /// YENI TAG ACIK DOGAR — bilerek. Kapali dogsa sonraki tag'ler icin TEMEL olamaz ve
        /// harita disari dogru buyuyemez; oysa yontemin butun degeri o zincirde. Guvence
        /// yerine su ikili konuyor: (1) ornekler kendi icinde tutarli olmali
        /// (autoMapMaxSpread), (2) yazildiktan sonra tag fuzyona girer ve kotu ise
        /// 'kalinti' satiri onu ADIYLA yazar.
        /// </summary>
        void TickAutoMap()
        {
            if (!EnsureRig()) return;

            for (int i = 0; i < _autoMapId.Count; i++)
            {
                int id = _autoMapId[i];

                if (!_autoMap.TryGetValue(id, out var o))
                {
                    o = new AutoMapOrnek();
                    _autoMap[id] = o;
                    WriteDiag($"HARITALAMA BASLADI  tag {id}");
                }

                // Uzun bosluk pencereyi bosaltir: aradan gecen surede oyuncu bambaska bir
                // yere gitmis olabilir ve eski orneklerle yenileri ayni olcume ait degildir.
                if (o.Yerel.Count > 0 && Time.time - o.SonOrnekAt > calibWindowMaxGap)
                {
                    o.Yerel.Clear();
                    o.Yaw.Clear();
                }
                o.SonOrnekAt = Time.time;

                o.Yerel.Add(ToLocal(_autoMapPos[i]));
                o.Yaw.Add(_autoMapYaw[i] - RigYaw);

                if (o.Yerel.Count < Mathf.Max(3, autoMapSampleCount)) continue;

                // Sacilma kapisi: pencere kendi icinde tutarli degilse YAZMA. Bir kez
                // yazilan yanlis konum, sonraki tag'lerin de temeli olur.
                Vector3 ort = Vector3.zero;
                foreach (var v in o.Yerel) ort += v;
                ort /= o.Yerel.Count;
                float sacilma = 0f;
                foreach (var v in o.Yerel) sacilma = Mathf.Max(sacilma, Vector3.Distance(v, ort));
                if (sacilma > autoMapMaxSpread)
                {
                    o.Yerel.Clear();
                    o.Yaw.Clear();
                    WriteDiag($"HARITALAMA BEKLIYOR  tag {id}  sacilma {sacilma * 100f:0.0} cm > " +
                              $"{autoMapMaxSpread * 100f:0.0}  — pencere atildi");
                    continue;
                }

                // Yaw dairesel ortalanir; aritmetik ortalama 179 ile -179'u 0 yapardi.
                Vector2 yon = Vector2.zero;
                foreach (var y in o.Yaw)
                {
                    float r = y * Mathf.Deg2Rad;
                    yon += new Vector2(Mathf.Sin(r), Mathf.Cos(r));
                }
                float yaw = Mathf.Atan2(yon.x, yon.y) * Mathf.Rad2Deg + RigYaw;
                if (yaw > 180f) yaw -= 360f;
                if (yaw <= -180f) yaw += 360f;

                Vector3 dunya = ToWorld(ort);

                var liste = new List<TagEntry>(tagLayout ?? Array.Empty<TagEntry>());
                liste.Add(new TagEntry
                {
                    id = id,
                    position = dunya,
                    yawDegrees = yaw,
                    useForCalibration = true,
                    sourceInstanceId = Constructor.TagCapture.ExternalSource,   // plakadan gelmedi
                });
                tagLayout = liste.ToArray();

                bool yazildi = PersistLayout();
                RebuildMarkers();
                _autoMap.Remove(id);

                WriteDiag($"HARITALANDI  tag {id}  {dunya.x:0.000} {dunya.y:0.000} {dunya.z:0.000}" +
                          $"  yaw {yaw:+0.0;-0.0}  sacilma {sacilma * 100f:0.0} cm" +
                          $"  ({o.Yerel.Count} ornek)  {(yazildi ? PersistTarget : "YAZILAMADI")}");
                _calibNote = $"TAG {id} HARITALANDI";
            }
        }

        /// <summary>Fuzyona giren tag kimlikleri, teshis satiri icin.</summary>
        string FuseIdList()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _fuseId.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_fuseId[i]);
            }
            return sb.ToString();
        }

        bool FuseCorrect()
        {
            if (!EnsureRig()) return false;

            int n = _fuseMeasured.Count;
            float wTop = 0f;
            Vector3 mBar = Vector3.zero, dBar = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                float w = _fuseWeight[i];
                wTop += w;
                mBar += _fuseMeasured[i] * w;
                dBar += _fuseDeclared[i] * w;
            }
            if (wTop <= 0f) return false;
            mBar /= wTop;
            dBar /= wTop;

            // YAW: yatay duzlemde agirlikli Procrustes. Unity'nin Y donusu
            //   d.x = m.x*cos + m.z*sin ,  d.z = -m.x*sin + m.z*cos
            // oldugundan pay/payda asagidaki gibi cikiyor.
            float pay = 0f, payda = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 m = _fuseMeasured[i] - mBar;
                Vector3 d = _fuseDeclared[i] - dBar;
                float w = _fuseWeight[i];
                pay += w * (m.z * d.x - m.x * d.z);
                payda += w * (m.x * d.x + m.z * d.z);
            }

            // Tag'ler ust uste dusuyorsa yon belirsiz — cozmeye calismak gurultuyu
            // yon sanmak olur.
            if (Mathf.Abs(pay) < 1e-9f && Mathf.Abs(payda) < 1e-9f) return false;
            float theta = Mathf.Atan2(pay, payda) * Mathf.Rad2Deg;

            // ARTIK HATA. Dikey de dahil: tag'lerin yuksekligi yerlesimde yanlissa kati
            // donusum onu kapatamaz ve burada gorunur — istenen budur.
            Quaternion R = Quaternion.Euler(0f, theta, 0f);
            float kare = 0f;
            // EN KOTU TAG ADIYLA YAZILIR. Coklu kurulumda "kalinti yuksek" tek basina
            // "birinde sorun var, bul bakalim" demek; hangi tag oldugunu soylemeyen bir
            // teshis, tag sayisi arttikca degersizlesiyor.
            int enKotuId = -1;
            float enKotu = -1f;
            for (int i = 0; i < n; i++)
            {
                Vector3 kalan = (R * (_fuseMeasured[i] - mBar) + dBar) - _fuseDeclared[i];
                kare += _fuseWeight[i] * kalan.sqrMagnitude;
                float m2 = kalan.magnitude;
                if (m2 > enKotu) { enKotu = m2; enKotuId = _fuseId[i]; }
            }
            float rms = Mathf.Sqrt(kare / wTop);

            if (rms > fusionMaxResidual)
            {
                // Seyrek yazilir: her karede yazmak dosyayi bogar, ama BU SATIR degerli —
                // yerlesim hatasinin dogrudan olcusu.
                if (Time.time >= _nextFuseDiagAt)
                {
                    _nextFuseDiagAt = Time.time + 5f;
                    WriteDiag($"FUZYON RED  {n} tag [{FuseIdList()}]  kalinti {rms * 100f:0.0} cm > " +
                              $"{fusionMaxResidual * 100f:0.0}  (en kotu tag {enKotuId}: " +
                              $"{enKotu * 100f:0.0} cm)  yaw {theta:+0.00;-0.00}" +
                              $"  — tag'ler birbiriyle ya da yerlesimle celisiyor");
                }
                _calibNote = $"FUZYON RED (kalinti {rms * 100f:0.0} cm)";
                return false;   // tek-tag yolu denesin
            }

            Vector3 oteleme = dBar - mBar;
            float dev = oteleme.magnitude;

            if (dev <= correctionDeadzoneMeters && Mathf.Abs(theta) <= correctionYawDeadzoneDegrees)
            {
                _calibNote = $"HIZALI ({dev * 100f:0.0} cm, {n} tag fuzyon)";
                _alignedNow = true;
                _fuseAppliedAt = Time.time;
                _fuseResidual = rms;
                _fuseTagCount = n;
                return true;   // is yok — ama KARAR fuzyonun, tek-tag yolu ayni karede calismasin
            }
            _alignedNow = false;

            bool snap = _layoutStale || dev > snapThresholdMeters ||
                        Mathf.Abs(theta) > snapThresholdDegrees;
            float rate = snap ? 1f : Mathf.Clamp01(smallCorrectionRate);

            // SIRA: once donme (olculen agirlik merkezi etrafinda, o nokta sabit kalir),
            // sonra oteleme. Tek-tag yolundaki desenin aynisi.
            _rig.RotateAround(mBar, Vector3.up, theta * rate);
            Vector3 delta = oteleme * rate;
            if (!correctVertical) delta.y = 0f;
            _rig.position += delta;

            if (_cm != null) _cm.CompleteFromTag();
            TickAnchorHold();
            _layoutStale = false;
            _lastCorrectionAt = Time.time;

            _calibNote = $"FUZYON {n} tag ({dev * 100f:0.0} cm, yaw {theta:0.0})";
            _fuseAppliedAt = Time.time;
            _fuseResidual = rms;
            _fuseTagCount = n;

            if (snap || Time.time >= _nextFuseDiagAt)
            {
                _nextFuseDiagAt = Time.time + 5f;
                float dMin = float.MaxValue, dMax = 0f;
                for (int i = 0; i < n; i++)
                {
                    if (_fuseDist[i] < dMin) dMin = _fuseDist[i];
                    if (_fuseDist[i] > dMax) dMax = _fuseDist[i];
                }
                WriteDiag($"{(snap ? "FUZSNAP" : "FUZYON ")}  {n} tag [{FuseIdList()}]" +
                          $"  sapma {dev * 100f:0.0} cm  yaw {theta:+0.00;-0.00}" +
                          $"  kalinti {rms * 100f:0.0} cm (en kotu tag {enKotuId}: {enKotu * 100f:0.0} cm)" +
                          $"  d {dMin:0.00}-{dMax:0.00} m");
            }
            return true;
        }

        void ApplyCorrection(TagEntry entry, Vector3 measuredPos, float measuredYaw, float dev,
                             bool applyYaw, float distance)
        {
            if (_cm == null) _cm = FindFirstObjectByType<CalibrationManager>();
            if (_rig == null)
            {
                _rig = _cm != null ? _cm.rig : null;
                if (_rig == null) return;
            }

            float yawDelta = Mathf.DeltaAngle(measuredYaw, entry.yawDegrees);
            float yawRaw = yawDelta;
            if (!applyYaw) yawDelta = 0f;   // bu tag konumu duzeltir, yonu ellemez

            // KUCUK duzeltme YUMUSAK, BUYUK duzeltme ANINDA.
            //
            // Olu bolgeyi daraltmak dogrulugu artirir ama tek basina kotu bir takas: duzeltme
            // her tespitte tam uygulaninca dunya saniyede 1-3 kez zipliyor. Olcum gurultusu
            // ~0,3-0,5 derece oldugu icin dar bir esik neredeyse her turda tetiklenir ve
            // 4 metrede 0,4 derece 3 cm'lik gorunur bir kayma demektir — dunya yuzer.
            //
            // Sapmanin bir ORANINI uygulamak ayni dogruluga TITREMEDEN goturur: ust uste
            // birkac tespitte yakinsar, gurultu de ortalanmis olur. Boylece esigi gercekten
            // kucuk tutabiliyoruz.
            //
            // Buyuk sapma yumusatilmaz: uyku sonrasi ya da takip kaybinda oyuncu dunyanin
            // HEMEN yerine oturmasini ister, saniyelerce suzulmesini degil.
            // HARITA DEGISIMINDEN SONRAKI ILK DUZELTME HEP ANINDA. Baska bir mekanin
            // cercevesine yumusak gecis diye bir sey yok: aradaki fark sapma degil, tamamen
            // baska bir dunya. Sapma buyukse zaten snap olurdu; kucuk ciktiginda (iki harita
            // benzer cercevede) suzulmek oyuncuyu saniyelerce yanlis yerde tutardi.
            bool snap = _layoutStale
                     || dev > snapThresholdMeters || Mathf.Abs(yawDelta) > snapThresholdDegrees;
            // ADIM 6: kucuk duzeltmenin kazanci olculen sacilmaya gore azalir. SNAP YOLU
            // DISARIDA: snap zaten "olcume degil, olcumun buyuklugune" tepki veriyor ve uyku
            // sonrasi toparlanmanin tek yolu o; onu sacilmayla yavaslatmak, kazanci eklemekle
            // duzeltilmek istenen seyin tam tersi olurdu.
            float gain = CorrectionGain();
            float rate = snap ? 1f
                              : Mathf.Clamp01(smallCorrectionRate) *
                                (gainScalesWithSpread ? gain : 1f);

            // Duzeltmeler saniyede 3'e kadar tetiklenir; hepsini yazmak dosyayi bogar.
            // SNAP her zaman yazilir (nadir ve onemli), normal hiza 5 saniyede bir.
            // Log'da HAM yaw sapmasi yazilir (uygulanmasa bile): tag'in yaw kestiriminin ne
            // kadar tutarsiz oldugunu gormek, yaw'i devre disi biraktiktan SONRA da gerekli.
            string yawNot = applyYaw ? "" : " (uygulanmadi)";

            // PIKSEL KONUMU log'a girer: distorsiyon ve ana nokta hatasi konuma bagli
            // oldugu icin, sapmanin kadraj konumuyla ILISKILI olup olmadigi ancak boyle
            // gorulur. Iliski varsa optik kaynakli, yoksa baska yerde aramak gerekir.
            string px = _seenPixel.TryGetValue(entry.id, out Vector2 pc)
                ? $"  px {pc.x:0},{pc.y:0}" : "";

            // MESAFE de yazilir: acisal hata mesafeyle CARPILARAK konum hatasina donusur,
            // konumsal/referans hatasi ise mesafeden BAGIMSIZDIR. Ikisini ayirt etmenin tek
            // yolu ayni tag'i farkli mesafelerden olcup sapmanin olcekleneip olceklenmedigine
            // bakmak. "sapma/mesafe" orani sabitse acisal, "sapma" sabitse konumsal.
            string dm = $"  d {distance:0.00} m  sapma/d {dev / Mathf.Max(0.01f, distance) * 100f:0.0} cm/m";

            // TAG'SIZ GECEN SURE — buyuk bir duzeltmenin SEBEBINI ayirt eden tek sayi.
            // Flip ardisik kareler arasi olur (bosluk ~0,3 sn); takip kopmasi saniyeler
            // suren bir sessizlikten sonra gelir. Taban cizgisinde 4,96 m'lik sicramayi
            // flip sandim, oysa 21 saniyelik boslugun ardindan gelmisti -- gozluk cikarilip
            // odanin obur ucunda takilmisti. Bu sayi yazilsaydi soru hic sorulmayacakti.
            string bosluk = _tagGapSeconds >= 0.5f ? $"  bosluk {_tagGapSeconds:0.0} sn" : "";

            // EKSEN BAZLI SAPMA. Tek bir "sapma 291,8 cm" sayisi ne kadarinin DIKEY oldugunu
            // gizliyordu ve tam o soru acikta kaldi: kalibrasyondan sonra zemin -0,995 m'ye
            // dusuyor, yani dunya ~1 m indiriliyor, ama bunun duzeltmenin dikey bileseninden
            // gelip gelmedigi log'dan okunamiyordu. GECIS satiri dx/dy/dz yaziyor, SNAP/HIZA
            // yazmiyordu — ayni sayi, iki farkli ayrinti duzeyi.
            Vector3 d = entry.position - measuredPos;
            string eksen = $"  dx {d.x:+0.000;-0.000} dy {d.y:+0.000;-0.000} dz {d.z:+0.000;-0.000}";

            // YAW SACILMASI (Adim 5) — esik secilebilmesi icin normal kullanimda ne oldugunu
            // gormek sart. Plan 2 derece oneriyor ama o sayi olculmedi; bu sutun onu olcuyor.
            string ysac = $"  yawsac {_diagYawSpread:0.00}";

            // ADIM 6 SUTUNU. Kapali olsa da YAZILIR: acmadan once bu oranin normal kullanimda
            // ne oldugunu gormek gerekiyor (bkz. gainScalesWithSpread). "(uygulanmadi)" notu,
            // yaw sutunundaki ayni notun isini gorur — log'un yalan soylememesi icin.
            string kzn = $"  sac/sinir {_diagSpreadRatio:0.00} ({_diagSampleCount} ornek)" +
                         $"  kazanc {gain:0.00}" + (gainScalesWithSpread ? "" : " (uygulanmadi)");

            if (snap)
            {
                WriteDiag($"SNAP   tag {entry.id}  sapma {dev * 100f:0.0} cm{eksen}  yaw {yawRaw:+0.00;-0.00}{yawNot}{ysac}{px}{dm}{bosluk}{kzn}");
            }
            else if (Time.time >= _nextStateDiagAt)
            {
                _nextStateDiagAt = Time.time + 5f;
                WriteDiag($"HIZA   tag {entry.id}  sapma {dev * 100f:0.0} cm{eksen}  yaw {yawRaw:+0.00;-0.00}{yawNot}{ysac}{px}{dm}{bosluk}{kzn}");
            }

            _rig.RotateAround(measuredPos, Vector3.up, yawDelta * rate);

            Vector3 delta = (entry.position - measuredPos) * rate;
            if (!correctVertical) delta.y = 0f;
            _rig.position += delta;

            // Rig hizalandi -> kalibrasyon DURUMUNU da tamamla. Bu satir olmadan tag dogru
            // hizalasa bile oyun "kalibre degil" sanip A/B ekraninda bekletiyordu: bayragi
            // kaldiran tek yol Bind zinciriydi ve o zincir d1176d6'da (hakli olarak) koparildi.
            // CompleteFromTag rig'e DOKUNMAZ ve anchor'i uyandirmaz — yalnizca durumu isaretler,
            // zaten kalibreyse hicbir sey yapmaz. Boylece tag tek referans olmaya devam eder.
            if (_cm != null) _cm.CompleteFromTag();

            // ANCHOR'A YENI CERCEVEYI OGRET — SIRASI KRITIK. Rig bu karede Update'te oynadi;
            // anchor LateUpdate'te mutlak poz yazacak. Once ogretmezsek duzeltmemizi ezer,
            // ki iki sistemin eski kavgasi tam olarak buydu.
            TickAnchorHold();

            // Rig yeni cerceveye oturdu: bayat isareti kalkar, tespit bosta hizina donebilir.
            _layoutStale = false;
            _lastCorrectionAt = Time.time;

            // Oyuncu bunu okuyacak: "duzeltildi" tek basina neyin duzeldigini soylemiyordu.
            _calibNote = $"KALIBRE EDILDI ({dev * 100f:0.0} cm duzeltildi)";
            Debug.Log($"[AprilTagCalib] Tag {entry.id} duzeltme: sapma {dev * 100f:0.0} cm, " +
                      $"yaw {yawDelta:0.0} derece, oteleme {delta.magnitude:0.000} m.");
        }

        // ------------------------------------------------------------------ ogrenme modu

        readonly List<Vector3> _learnPos = new List<Vector3>();
        readonly List<float> _learnYaw = new List<float>();
        int _learnId = -1;
        bool _learnDone;

        /// <summary>
        /// Tag'in ORTAK CERCEVEDEKI yerini olcup raporlar. Kullanici tag'i elle tarif etmek
        /// zorunda kalmasin diye: kamera zaten mm hassasiyetinde olcuyor, elle koordinat
        /// yazmak o hassasiyeti çöpe atmak olurdu.
        ///
        /// SART: once A/B ile kalibre olunmus olmali — olculen konum, o ANDAKI cerceveye
        /// goredir. Kalibre olunmadan ogrenilen deger anlamsizdir.
        /// </summary>
        void Learn(int id, float distance, Vector3 worldPos, Quaternion worldRot)
        {
            if (_learnDone) return;

            // Hedef ID verilmisse baska tag'e BAKMA. Bu satir olmadan ogrenme, menzile giren
            // ILK tag'e kilitleniyor ve birakmiyordu: coklu tag kurulumunda tag 0 yakindayken
            // acinca onu olcuyor, yeni tag'i degil.
            if (learnTargetId >= 0 && id != learnTargetId)
            {
                _learnNote = $"tag {learnTargetId} bekleniyor (gorulen: {id})";
                return;
            }

            if (!CalibrationManager.Calibrated)
            {
                _learnNote = "once kalibre ol (tag 0'a bak)";
                return;
            }
            if (distance > learnMaxDistance)
            {
                _learnNote = $"yaklas ({distance:0.00} > {learnMaxDistance:0.00} m)";
                return;
            }
            if (_learnId >= 0 && id != _learnId)
                return;   // ogrenme sirasinda tek tag'e odaklan

            _learnId = id;
            _learnPos.Add(worldPos);
            _learnYaw.Add(YawOf(worldRot));
            _learnNote = $"olculuyor {_learnPos.Count}/{learnSampleCount}";

            if (_learnPos.Count < Mathf.Max(5, learnSampleCount)) return;

            // Ortalama: tek olcumun titremesini bastirir.
            Vector3 pos = Vector3.zero;
            foreach (var p in _learnPos) pos += p;
            pos /= _learnPos.Count;

            // Yaw ortalamasi aciyi vektore cevirerek — 359/1 derece sarmasinda duz ortalama
            // yanlis sonuc verir.
            Vector2 dir = Vector2.zero;
            foreach (var y in _learnYaw)
                dir += new Vector2(Mathf.Sin(y * Mathf.Deg2Rad), Mathf.Cos(y * Mathf.Deg2Rad));
            float yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;

            _learnedPos = pos;
            _learnedYaw = yaw;
            _learnDone = true;
            _learnNote = "TAMAM";

            Debug.Log(
                "[AprilTagCalib] === TAG YERI OGRENILDI ===\n" +
                $"  id   = {id}\n" +
                $"  pos  = ({pos.x:0.000}, {pos.y:0.000}, {pos.z:0.000})\n" +
                $"  yaw  = {yaw:0.0} derece\n" +
                $"  ({_learnPos.Count} olcumun ortalamasi)\n" +
                "  Bu degerleri Inspector'da Tag Layout'a yaz, Learn Mode'u KAPAT, " +
                "Auto Calibrate'i AC. Artik A/B'ye gerek yok.");
        }

        string _learnNote = "";
        Vector3 _learnedPos;
        float _learnedYaw;

        /// <summary>
        /// Yerlesime EN SON yazilan degerin sayilari, panelde okunacak bicimde.
        ///
        /// NEDEN PANELDE: yazilan konum simdiye kadar yalnizca teshis DOSYASINA ve Debug.Log'a
        /// gidiyordu. Log bu build'de logcat'e hic akmiyor (bkz. WriteDiag notu), yani sayiyi
        /// gormenin tek yolu gozlugu cikarip adb ile dosya cekmekti. Panelde durursa olcen kisi
        /// dogrudan okuyup aktarabiliyor — olculen deger PC'deki prefaba ELLE islenmek zorunda
        /// (TagLayoutStore cihazin diskine yaziyor, projeye degil).
        ///
        /// KALICI: bir sonraki yazmaya kadar silinmez. _learnNote gibi anlik olsaydi, kolunu
        /// indirip paneli okuyana kadar kaybolabilirdi — dokunus zaten "en yakin yaklasma"
        /// mantigiyla bunun icin kuruldu.
        ///
        /// 3 HANE = 1 mm. Tag'ler arasi tutarsizlik santimetre mertebesinde; daha az hane
        /// olcumun kendisini yuvarlardi, daha cok hane okunmasi zor bir sayi uretirdi.
        /// </summary>
        string _lastWrite = "";

        void NoteWrite(int id, Vector3 pos, float yaw) =>
            _lastWrite = $"YAZ tag {id}  {pos.x:0.000} {pos.y:0.000} {pos.z:0.000}  yaw {yaw:0.0}";

        // ---- OLC -> UYGULA -> GOZLE DOGRULA -----------------------------------------------
        //
        // Eskiden olcum sonucu panelde SAYI olarak kalirdi; yerlesime gecirmek PC'de sahneyi
        // duzenleyip yeniden build almak demekti. Tek bir tag icin saatler suren ve arada
        // dogru mu yanlis mi gorulemeyen bir dongu. Sag kumandanin B tusu bunu kapatir:
        //
        //   olc  ->  B  ->  yerlesime yazilir + diske kaydedilir  ->  plaka tag'in ustune atlar
        //
        // Olcum BITMEMISKEN B basmak olcumu sifirlar — yanlis yerden olcmeye basladiysan
        // uygulamayi kapatip acmak zorunda kalmayasin diye.
        //
        // TUS SECIMI: yalnizca learnMode ACIKKEN okunur. B oyunda baska is yapiyor; ogrenme
        // bir kurulum modu oldugu icin cakisma pratikte olusmaz.
        bool _applyPrev;

        void TickLearnInput()
        {
            if (!learnMode) { _applyPrev = false; _nudgePrev = Vector2.zero; return; }

            // INSA MODUNDA SUS. Sag A insa modunda "sil" tusu (ConstructorPlacer.DeleteHeld);
            // ogrenme girisleri acik kalirsa tek basis hem bir prop siler hem tag yerlesimine
            // yazar. Sol cubuk da paletle cakisabilir. Constructor bu bayragi zaten kaldiriyor.
            if (XRButtons.GameplayInputSuppressed)
            {
                _applyPrev = false;
                _nudgePrev = Vector2.zero;
                return;
            }

            TickNudge();

            // TUS SECIMI — sag A.
            //
            // B KULLANILAMAZ: LanBootstrap'ta "oyuna katil" tusu ve sunucuya baglanmamisken
            // CANLI (LanBootstrap.cs:90). Olcumu yerlesime yazarken ayni anda sunucu aramaya
            // baslamasi kabul edilemez.
            //
            // SAG A her iki durumda da guvenli: TeamSelector de A okur ama o AG OYUNCUSUNDA
            // yasar — sunucusuz hic var olmaz, baglandiktan sonra da takim secilince _done ile
            // susar. ConstructorPlacer'in A'si yalnizca insa modunda calisir.
            // SOL X ARTIK BOS: RoomScanSync'in kisayolu solXKisayolu bayraginin arkasina
            // alindi (varsayilan kapali). Buradaki grip/tetik akorlari sadelestirilecekse
            // hedef tus odur — ama kas hafizasini bir kurulum turunun ortasinda degistirme.
            bool a = XRButtons.Button(UnityEngine.XR.XRNode.RightHand,
                                      UnityEngine.XR.CommonUsages.primaryButton);
            bool pressed = a && !_applyPrev;
            _applyPrev = a;
            if (!pressed) return;

            // SOL GRIP+A (kalibrasyon izni) ve SOL TETIK+A (konumu kumandadan yaz)
            // KALDIRILDI — ikisi de gozlukten yapilmiyordu:
            //   izin  -> menu "49. Tag Kurulum Merkezi", tag basina ACIK/KAPALI anahtari;
            //            ayrica gozlukte harita kaydedilirken Capture+Enable birlikte kosuyor
            //            (ConstructorSync.HostTagSetup), yani tag'ler zaten ACIK doguyor.
            //   konum -> plaka yaratici modda tag'in ustune oturtularak tanimlaniyor.
            // Kumanda dokunusundan gelen OKUMA duruyor (TouchDerived, teshis satirinda):
            // dokunup yerlesimin ne kadar saptigini hala gorebiliyorsun, sadece YAZMIYOR.

            if (!_learnDone)
            {
                ResetLearn();
                _learnNote = "sifirlandi — bastan olculuyor";
                return;
            }

            ApplyLearned();
        }

        /// <summary>
        /// Yerlesimi DOGRU HEDEFE yazar: harita yerlesimi aktifse HARITAYA, degilse cihazin
        /// TagLayout.json'una.
        ///
        /// NEDEN VAR: dort ogrenme yolu da dogrudan <see cref="TagLayoutStore.Save"/>
        /// cagiriyordu, ama bir harita acikken <see cref="tagLayout"/> HARITANIN listesidir
        /// (<see cref="ApplyMapLayout"/> onu oyle yapti). Sonuc iki hata birdendi:
        ///
        ///   1. Duzenleme CIHAZIN dosyasina yaziliyordu, yani onyukleme yerlesimi o mekanin
        ///      tag'leriyle kirleniyordu. Baska bir mekanda acilista o kirlilik devreye
        ///      giriyordu -- "yeni harita eski mekanin tag'leriyle kalibre olmaya calisiyor"
        ///      vakasinin KAYNAGI buydu; onu tohumlama ile kapatmistik, burasi kok neden.
        ///   2. HARITAYA hic yazilmiyordu. Olcum, ait oldugu kayitta gorunmuyordu; harita
        ///      baska bir gozluge gidince duzenleme onunla birlikte gitmiyordu.
        ///
        /// Harita dosyasini HER ZAMAN yetki sahibi yazar (bkz.
        /// <see cref="Constructor.ConstructorSync.ClientRequestSave"/>): gozlukte yazilan
        /// kopyayi kimse okumaz ve ilk senkron ezer. Bellekteki duzenleme zaten uygulanmis
        /// durumda -- tagLayout ile Layout.tags AYNI dizi -- burada yalnizca kalicilastiriliyor.
        /// </summary>
        bool PersistLayout()
        {
            if (!_fromMap) return TagLayoutStore.Save(tagLayout, layoutVersion);

            if (Constructor.ConstructorSession.IsMapAuthority)
            {
                var s = Constructor.ConstructorSession.Instance;
                if (s == null || s.Layout == null) return false;

                // DIZIYI GERI BAGLA. Yeni tag eklenince "tagLayout = list.ToArray()" calisiyor
                // (ApplyLearned) ve haritanin dizisiyle paylasim KOPUYOR.
                // Baglamazsak Save, yeni tag'i olmayan ESKI diziyi yazar ve olcum sessizce
                // kaybolur -- duzenleme ekranda gorunur ama dosyaya hic gitmez.
                s.Layout.tags = tagLayout;
                return s.Save();
            }

            // ISTEMCI: harita SUNUCUNUN belleginde. Yalnizca "kaydet" demek, sunucunun
            // DUZENLENMEMIS kopyasini yazdirirdi ve panel "kaydedildi" derken olcum kaybolurdu.
            // O yuzden once tag'lerin kendisi gidiyor; sunucu uygulayip kaydediyor ve
            // digerlerine yayiyor.
            return Constructor.ConstructorSync.ClientRequestTagLayout(tagLayout);
        }

        /// <summary>Yazmanin nereye gittigi — panel ve log metinleri icin.</summary>
        string PersistTarget => _fromMap ? "haritaya" : "cihaza";

        // ---- ELLE INCE AYAR ---------------------------------------------------------------
        //
        // Yeniden olcmek yalnizca RASTGELE hatayi duzeltir. Sapma SISTEMATIKSE — belli bir
        // aciyla bakildiginda poz kestiriminin kaydigi durum — her tekrar ayni yanlisi verir
        // ve olcumu tekrarlamak yalnizca yanlisi daha kararli hale getirir. Plakayi elle
        // gercek tag'in ustune oturtmak bagimsiz bir olcumdur: gozun yakindan hizalamasi
        // kameranin poz kestirim hatasini PAYLASMAZ.
        //
        // SOL cubuk: sag cubuk insa paletine bagli, solun ekseni bostadir
        // (yalnizca tiklamasi insa moduna geciriyor).
        //
        //   sol cubuk saga/sola      -> tag duzleminde yatay (duvar boyunca)
        //   sol cubuk yukari/asagi   -> yukseklik
        //   sol GRIP + saga/sola     -> yaw
        //   sol TETIK + yukari/asagi -> duvara dik (ileri/geri)
        //
        // AYRIK ADIM, basili tutunca tekrar: surekli kaydirmada dogru noktada durmak zordur.
        const float NudgeStep = 0.01f;          // m
        const float NudgeYawStep = 0.5f;        // derece
        const float NudgeRepeatDelay = 0.35f;   // ilk adimdan sonra tekrara gecis
        const float NudgeRepeatRate = 12f;      // adim/sn

        Vector2 _nudgePrev;
        float _nudgeNextAt;
        bool _nudgeDirty;

        void TickNudge()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            if (!dev.isValid) return;

            if (!dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 ax))
                ax = Vector2.zero;

            // Olu bolge + TEK EKSENE kilit: capraz itiste hem yatay hem dikey oynarsa
            // duzelttigini sanip baska bir ekseni bozarsin.
            const float dz = 0.6f;
            int ix = (Mathf.Abs(ax.x) > dz && Mathf.Abs(ax.x) >= Mathf.Abs(ax.y)) ? (int)Mathf.Sign(ax.x) : 0;
            int iy = (Mathf.Abs(ax.y) > dz && Mathf.Abs(ax.y) >  Mathf.Abs(ax.x)) ? (int)Mathf.Sign(ax.y) : 0;

            if (ix == 0 && iy == 0)
            {
                // BIRAKILINCA yazilir. Her adimda diske yazmak, basili tutulan cubukta
                // saniyede 12 dosya yazmasi demek olurdu.
                if (_nudgeDirty)
                {
                    bool ok = PersistLayout();
                    _nudgeDirty = false;
                    _learnNote = ok ? $"ince ayar {PersistTarget} kaydedildi"
                                    : $"ince ayar {PersistTarget.ToUpperInvariant()} YAZILAMADI";
                }
                _nudgePrev = Vector2.zero;
                return;
            }

            bool first = _nudgePrev == Vector2.zero;
            _nudgePrev = new Vector2(ix, iy);

            if (first) _nudgeNextAt = Time.time + NudgeRepeatDelay;
            else if (Time.time < _nudgeNextAt) return;
            else _nudgeNextAt = Time.time + 1f / NudgeRepeatRate;

            ApplyNudge(dev, ix, iy);
        }

        void ApplyNudge(UnityEngine.XR.InputDevice dev, int ix, int iy)
        {
            var entry = Find(_lastId);
            if (entry == null)
            {
                _learnNote = $"tag {_lastId} yerlesimde yok — once olcup B ile ekle";
                return;
            }

            bool grip = XRButtons.HeldWithAxisFallback(dev,
                UnityEngine.XR.CommonUsages.gripButton, UnityEngine.XR.CommonUsages.grip, 0.5f);
            bool trigger = XRButtons.HeldWithAxisFallback(dev,
                UnityEngine.XR.CommonUsages.triggerButton, UnityEngine.XR.CommonUsages.trigger, 0.5f);

            // Eksenler tag'in KENDI cercevesinde: duvara yapisik bir tag'i dunya X/Z ile
            // itmek onu duvarin icine/disina kaydirir, oysa duzeltmek istedigin sey
            // neredeyse her zaman duvar BOYUNCA kaymadir.
            Quaternion rot = Quaternion.Euler(0f, entry.yawDegrees, 0f);
            string ne;

            if (grip && ix != 0)
            {
                entry.yawDegrees = Mathf.Repeat(entry.yawDegrees + ix * NudgeYawStep + 180f, 360f) - 180f;
                ne = "yaw";
            }
            else if (trigger && iy != 0)
            {
                entry.position += (rot * Vector3.forward) * (iy * NudgeStep);
                ne = "derinlik";
            }
            else if (ix != 0)
            {
                entry.position += (rot * Vector3.right) * (ix * NudgeStep);
                ne = "yatay";
            }
            else
            {
                entry.position += Vector3.up * (iy * NudgeStep);
                ne = "yukseklik";
            }

            _nudgeDirty = true;
            SyncMarkerPoses();

            _learnNote = $"tag {entry.id} {ne}: " +
                         $"{entry.position.x:0.00} {entry.position.y:0.00} {entry.position.z:0.00}" +
                         $" / yaw {entry.yawDegrees:0.0}";
        }

        // ---- KUMANDA DOKUNUSU -------------------------------------------------------------
        //
        // Yerlesimi denetleyecek BAGIMSIZ bir olcu lazim; kameradan gelen her sayi ayni
        // biaslari paylasiyor. Serit metre bunu verirdi ama tag'ler duvarda yuksekte, aralari
        // olculemiyor. Kumanda verebilir: takibi tag tespitinden AYRI bir sistem, dolayisiyla
        // poz kestirim hatasini paylasmaz.
        //
        // KUMANDANIN KENDI OFSETI SORUN DEGIL: izlenen nokta parmak ucunda degil, ama iki
        // tag'e de ayni sekilde degdigin icin ofset FARKTAN duser. Olculen ara ile ilan
        // edilen arayi karsilastirmak bu yuzden gecerli.
        //
        // EN YAKIN YAKLASMA kaydedilir, anlik deger degil: tag'e degerken paneli okuyamazsin,
        // kolunu indirdikten sonra okursun.
        readonly Dictionary<int, Vector3> _touchPos = new Dictionary<int, Vector3>();
        readonly Dictionary<int, Quaternion> _touchRot = new Dictionary<int, Quaternion>();
        int _approachId = -1;
        float _approachBest;
        Vector3 _approachPos;
        Quaternion _approachRot;

        const float TouchEnter = 0.45f;   // altina inince yaklasma baslar
        const float TouchExit = 0.70f;    // ustune cikinca biter, en yakin nokta saklanir

        // Kameranin SON OLCTUGU tag pozlari. Dokunusun yakinlik testi bunlara baglidir.
        readonly Dictionary<int, Vector2> _seenPixel = new Dictionary<int, Vector2>();
        readonly Dictionary<int, Vector3> _seenPos = new Dictionary<int, Vector3>();
        readonly Dictionary<int, float> _seenYaw = new Dictionary<int, float>();
        readonly Dictionary<int, float> _seenTime = new Dictionary<int, float>();
        readonly List<int> _touchCandidates = new List<int>();

        /// <summary>
        /// Dokunus icin tag'in NEREDE OLDUGU: taze bir kamera olcumu varsa O, yoksa ilan
        /// edilen konum.
        ///
        /// OLCUM ONCELIKLI OLMAK ZORUNDA. Ilan edilen deger yeni bir tag icin ya hic yoktur
        /// ya da tamamen yanlistir — kagit yeniden asilmis olabilir. Yakinlik testini yanlis
        /// bir tahmine baglarsak dokunus HIC tetiklenmez ve tag'i kaydetmenin yolu kalmaz.
        /// Kamera ise tag'i KIMLIGIYLE taniyor ve her tespitte nerede oldugunu soyluyor;
        /// konumu yanlis bilse de "su anda su tag'e bakiyorum" bilgisi dogrudur.
        /// </summary>
        bool TouchAnchor(int id, out Vector3 anchor)
        {
            if (_seenTime.TryGetValue(id, out float t) && Time.time - t < 3f)
            {
                anchor = _seenPos[id];
                return true;
            }
            var e = Find(id);
            if (e != null) { anchor = e.position; return true; }

            anchor = Vector3.zero;
            return false;
        }

        void TickTouch()
        {
            if (_rightHandDiag == null)
            {
                var rigRef = XRRigReference.Instance;
                _rightHandDiag = rigRef != null ? rigRef.rightHand : null;
                if (_rightHandDiag == null) return;
            }
            Vector3 p = _rightHandDiag.position;

            // Aday tag'ler: yerlesimde OLANLAR + su anda GORULENLER. Ikincisi sart —
            // yerlesimde hic bulunmayan yeni bir tag ancak boyle kaydedilebilir.
            _touchCandidates.Clear();
            if (tagLayout != null)
                foreach (var t in tagLayout)
                    if (t != null && !_touchCandidates.Contains(t.id)) _touchCandidates.Add(t.id);
            foreach (var kv in _seenTime)
                if (Time.time - kv.Value < 3f && !_touchCandidates.Contains(kv.Key))
                    _touchCandidates.Add(kv.Key);

            int near = -1;
            float nd = float.MaxValue;
            foreach (int id in _touchCandidates)
            {
                if (!TouchAnchor(id, out Vector3 a)) continue;
                float d = Vector3.Distance(p, a);
                if (d < nd) { nd = d; near = id; }
            }
            if (near < 0) return;

            if (_approachId < 0)
            {
                if (nd < TouchEnter)
                {
                    _approachId = near; _approachBest = nd;
                    _approachPos = p; _approachRot = _rightHandDiag.rotation;
                }
                return;
            }

            if (near == _approachId && nd < _approachBest)
            {
                _approachBest = nd;
                _approachPos = p; _approachRot = _rightHandDiag.rotation;
            }

            if (nd > TouchExit || near != _approachId)
            {
                _touchPos[_approachId] = _approachPos;
                _touchRot[_approachId] = _approachRot;

                // REFERANS tag'e dokunulduysa ofset havuzuna ekle.
                //
                // ISKALAYI ALMA: yaklasma mesafesi iyi bir dokunusta ofset buyuklugu
                // kadardir (~5-7 cm). 15 cm'nin ustu tag'e degmemissin demektir; boyle bir
                // ornegi havuza katmak ortalamayi bozar. (Cihazda goruldu: 16,9 cm'lik bir
                // iskalanin ardindan gelen dogru dokunus 6,7 cm'ydi.)
                // YALNIZCA belirlenmis referans tag'i ofset uretir. Eskiden useForCalibration
                // acik olan HER tag uretiyordu; tag 1/2 acilinca onlara dokunmak da havuza
                // ornek ekliyordu (cihazda goruldu: "ornek 5", "ornek 6"). Ofset, tag'in ILAN
                // EDILEN konumu dogru kabul edilerek hesaplandigi icin daha az guvenilir bir
                // tag'den ornek almak ofseti kirletir ve kirlilik SONRAKI TUM olcumlere gecer.
                var refEntry = Find(_approachId);
                if (refEntry != null && _approachId == offsetReferenceTagId)
                {
                    if (_approachBest <= 0.15f)
                    {
                        _refOffsets.Add(Quaternion.Inverse(_approachRot) *
                                        (refEntry.position - _approachPos));

                        // DISKE yaz: ofset kumandanin sabit ozelligi, her oturumda yeniden
                        // olcturmek gereksiz ve unutuldugunda sistemi sessizce durduruyor.
                        TouchOffsetLocal(out Vector3 mean, out _);
                        bool ok = TagLayoutStore.SaveOffset(mean, OffsetSampleCount, offsetReferenceTagId);
                        WriteDiag($"OFSET  ornek {OffsetSampleCount}  {mean.magnitude * 100f:0.0} cm  " +
                                  $"sacilma {RefOffsetSpread() * 100f:0.0} cm" + (ok ? "  (diske yazildi)" : "  (YAZILAMADI)"));
                    }
                    else
                    {
                        WriteDiag($"OFSET  ORNEK ATILDI (yaklasma {_approachBest * 100f:0.0} cm > 15)");
                    }
                }
                Debug.Log($"[AprilTagCalib] Tag {_approachId} dokunuldu: {_approachPos} " +
                          $"(en yakin yaklasma {_approachBest * 100f:0.0} cm).");

                // Panelde ANLIK geri bildirim: dokunus yakalandi mi, ne kadar yakindi.
                // Panel sadelestikten sonra dokunus listesi kalkti, bunun yerini bu satir aldi.
                //
                // YAKLASMA 15 CM'DEN BUYUKSE BUNU SOYLE. Cihazda yasandi: uc dokunusun ucu de
                // 29-32 cm'den yapildi, ucu de sessizce reddedildi ve oyuncu olctugunu sandi.
                // "dokunuldu (29,2 cm)" satiri teknik olarak dogruydu ama reddedildigini
                // soylemiyordu — sayiyi okuyup esikle karsilastirmak oyuncunun isi degil.
                bool yeterince = _approachBest <= 0.15f;
                _learnNote = yeterince
                    ? $"tag {_approachId} ALINDI ({_approachBest * 100f:0.0} cm)" + TagHeightNote(_approachId)
                    : $"tag {_approachId} COK UZAK ({_approachBest * 100f:0.0} cm > 15) — tekrar degdir";

                var de = Find(_approachId);
                string turetilen = TouchDerived(_approachId, out Vector3 dp)
                    ? $"  turetilen {dp.x:0.000} {dp.y:0.000} {dp.z:0.000}" : "";
                string ilan = de != null
                    ? $"  ilan {de.position.x:0.000} {de.position.y:0.000} {de.position.z:0.000}" : "  (yerlesimde YOK)";
                WriteDiag($"DOKUNUS tag {_approachId}  ham {_approachPos.x:0.000} {_approachPos.y:0.000} {_approachPos.z:0.000}" +
                          turetilen + ilan + $"  yaklasma {_approachBest * 100f:0.0} cm");
                _approachId = -1;
            }
        }

        /// <summary>
        /// Tag konumunu KUMANDADAN turetir — kamera poz kestirimi devre disi.
        ///
        /// Kumanda takibi tag tespitinden cok daha dogru; tek engeli izlenen noktanin
        /// parmak ucunda olmamasi. Yani tag'e degdiginde okunan konum = tag'in konumu +
        /// sabit bir OFSET. Ofset REFERANS tag'de olculur (onun konumu zaten bir TANIM,
        /// dogru kabul edilir) ve digerlerinden dusulur. A/B'nin verdigi seyi verir:
        /// kameraya hic guvenmeyen bir konum.
        ///
        /// OFSET KUMANDANIN KENDI CERCEVESINDE saklanir. Dunya cercevesinde saklansaydi
        /// iki tag'e farkli acilarla degdiginde ofset donmez, aradaki fark oldugu gibi
        /// hataya donusurdu.
        ///
        /// YAW VERMEZ: tek dokunus yon tasimaz. Yaw kameradan ya da sol cubuktan gelir.
        /// </summary>
        // Referans tag'e yapilan TUM dokunuslardan cikan ofsetler. Ortalanir.
        //
        // NEDEN ORTALAMA, NEDEN SONUNCUSU DEGIL: ofset kumandanin SABIT fiziksel ozelligi —
        // izlenen noktanin degdirdigin noktaya uzakligi. Ama her dokunus onu ~3 cm gurultuyle
        // olcuyor (cihazda goruldu: ayni tag'e iki dokunus y'de 1,472 ve 1,442 verdi). Tek
        // ornekle yeniden olcmek, o gurultuyu SONRAKI TUM tag'lere kalici olarak gecirir —
        // yasandi: tag 1 tam bu yuzden 2,4 cm yuksek yazildi. Ortalama gurultuyü kok-N ile
        // bastirir ve her yeni referans dokunusu tahmini IYILESTIRIR, bozmaz.
        readonly List<Vector3> _refOffsets = new List<Vector3>();

        // DISKTEN gelen ofset: onceki oturumlarin ortalamasi + kac ornekten geldigi.
        // Bu oturumun ornekleriyle AGIRLIKLI birlestirilir, boylece her dokunus tahmini
        // iyilestirir ve hicbir oturum sifirdan baslamaz.
        Vector3 _storedOffset;
        int _storedCount;

        /// <summary>Referans tag'de olculen kumanda ofseti — KUMANDANIN kendi cercevesinde.</summary>
        bool TouchOffsetLocal(out Vector3 offsetLocal, out int refId)
        {
            offsetLocal = Vector3.zero;
            refId = offsetReferenceTagId;

            int n = _refOffsets.Count + _storedCount;
            if (n == 0) return false;

            // Diskteki ortalama, kac ornekten geldiyse o agirlikla katilir.
            offsetLocal = _storedOffset * _storedCount;
            foreach (var o in _refOffsets) offsetLocal += o;
            offsetLocal /= n;
            return true;
        }

        int OffsetSampleCount => _refOffsets.Count + _storedCount;

        /// <summary>Ortalamadan ortalama sapma (m) — ofsetin ne kadar oturdugunu soyler.</summary>
        float RefOffsetSpread()
        {
            if (_refOffsets.Count < 2) return 0f;
            TouchOffsetLocal(out Vector3 mean, out _);
            float s = 0f;
            foreach (var o in _refOffsets) s += (o - mean).magnitude;
            return s / _refOffsets.Count;
        }

        /// <summary>Kumanda pozunu, DEGDIRILEN noktanin konumuna cevirir.</summary>
        Vector3 ApplyTouchOffset(Vector3 pos, Quaternion rot)
        {
            return TouchOffsetLocal(out Vector3 off, out _) ? pos + rot * off : pos;
        }

        bool TouchDerived(int id, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!_touchPos.ContainsKey(id)) return false;
            if (!TouchOffsetLocal(out Vector3 offsetLocal, out int refId)) return false;
            if (refId == id) return false;   // referansin kendisi turetilemez

            pos = _touchPos[id] + _touchRot[id] * offsetLocal;
            return true;
        }

        // ---- ZEMIN OLCUMU -----------------------------------------------------------------
        //
        // Kumandayi yere YATIRMAK ise yaramiyor: IR halkasi gozlugun kameralarindan gizlenince
        // takip kopuyor ve poz yarim metre siciyor (cihazda olculdu: 0.06 -> 0.56). Dogrusu
        // kumandayi ELDE TUTUP ucunu yere degdirmek, halkasi gorunur kalsin.
        //
        // Olcume tag'lerdeki AYNI ofset uygulanir, yani raporlanan sey "kumandanin izlenen
        // noktasi" degil GERCEKTEN DEGDIRDIGIN nokta olur.
        float _floorBest = float.MaxValue;
        Vector3 _floorPos;
        Quaternion _floorRot;
        bool _floorTracking, _hasFloor;
        float _floorY, _floorRaw;

        /// <summary>
        /// "tag N yerden X cm" — zemin ve tag dokunusu BIR ARADA varsa.
        ///
        /// NEDEN: butun dikey tartismasi tek bir sayiya dayaniyor ve o sayi su an yalnizca
        /// metreyle olculebiliyor. Oysa ikisi de KUMANDAYLA olculuyor, ayni takip uzayinda,
        /// ve farkları dogrudan tag'in yerden yuksekligi. Kameranin kestirimine hic girmiyor —
        /// yani kamera ile kumandayi karsilastirmanin bagimsiz yolu bu.
        ///
        /// Ikisinden biri yoksa bos doner: eksik bir sayidan uydurma bir yukseklik uretmek,
        /// hic gostermemekten kotu.
        /// </summary>
        string TagHeightNote(int tagId = -1)
        {
            if (!_hasFloor) return "";
            if (tagId < 0) tagId = offsetReferenceTagId;
            if (!_touchPos.TryGetValue(tagId, out Vector3 raw)) return "";

            // IKI UCU DA AYNI SEKILDE ISLE. Ilk yazimda tag'in HAM konumu, zeminin ise
            // OFSETLI konumu kullaniliyordu — ofset yalnizca bir tarafa girince kumandanin
            // izlenen noktasi ile ucu arasindaki fark (olculdu: 5,8-7,8 cm) sonuca oldugu gibi
            // sizardi. Ayni islemi ikisine de uygulayinca ortak bileseni birbirini goturuyor.
            float tagY = _touchRot.TryGetValue(tagId, out Quaternion rot)
                ? ApplyTouchOffset(raw, rot).y : raw.y;

            return $"  |  tag {tagId} yerden {(tagY - _floorY) * 100f:0.0} cm";
        }

        void TickFloor()
        {
            if (_rightHandDiag == null) return;
            Vector3 p = _rightHandDiag.position;

            if (p.y < 0.25f)
            {
                if (!_floorTracking || p.y < _floorBest)
                {
                    _floorTracking = true;
                    _floorBest = p.y;
                    _floorPos = p;
                    _floorRot = _rightHandDiag.rotation;
                }
            }
            else if (_floorTracking && p.y > 0.5f)
            {
                _floorTracking = false;
                _floorRaw = _floorPos.y;
                _floorY = ApplyTouchOffset(_floorPos, _floorRot).y;
                _hasFloor = true;
                _floorBest = float.MaxValue;
                WriteDiag($"ZEMIN  ham {_floorRaw:0.000}  ofsetli {_floorY:0.000}");

                // EKRANDA ONAY. Oyuncu olcumu KOR yapiyordu: kumandayi yere degdirip
                // kaldiriyor, olcum alindi mi alinmadi mi ancak sonradan log cekilince
                // anlasiliyordu. Olcumu tekrarlamasi gerekip gerekmedigini o anda bilmeli.
                //
                // Tag yuksekligi de burada yaziliyor: sorunun tamami "tag yerden kac cm'de"
                // sorusuna dayaniyor ve iki sayi bir araya gelmeden cevaplanamiyor. Zemin
                // olculdugunde tag'e zaten dokunulmussa cevap ANINDA ekranda cikiyor.
                _learnNote = $"ZEMIN alindi {_floorY:0.000} m" + TagHeightNote();
            }
        }

        /// <summary>
        /// Teshis satirini DISKE yazar.
        ///
        /// NEDEN LOGCAT DEGIL: bu build'de Unity'nin managed logu logcat'e hic akmiyor —
        /// uygulama 2700+ satir basiyor ama hepsi native katmandan. Panelden sayi okuyup
        /// sesli aktarmak hem yavas hem hataya acik. Dosya iki tarafta da guvenilir ve
        /// adb ile dogrudan cekilebiliyor.
        /// </summary>
        // ---- KAFA HAREKETI ----------------------------------------------------------------
        //
        // Kamera karesi ile kafa pozu AYNI ANA ait degil (bkz. maxHeadSpeed notu). Zaman
        // damgasi boru hattı kurmak yerine, uyusmazligin ZARARSIZ oldugu ani bekliyoruz:
        // kafa duruyorsa gecikmenin bir onemi kalmaz.
        //
        // OLCUM RIG'E GORE YAPILIR, dunyaya gore degil: kalibrasyon duzeltmesi rig'i oynatir
        // ve dunya cercevesinde bu, kafa hareket etmis gibi gorunurdu — kendi duzeltmemiz
        // kapiyi kapatirdi.
        // ---- KARE TAZELIGI ----------------------------------------------------------------
        //
        // Tespit turu sabit araliklarla (1-3 Hz) calisiyor ama kamera dokusu bagimsiz akiyor.
        // Tazelik kontrolu olmadan AYNI kare birden fazla kez islenebilir: kamera takilirsa,
        // termal kisitlama olursa ya da doku beklenenden yavas guncellenirse.
        //
        // ZARARI HATAYI BUYUTMEK DEGIL, GUVENI SAHTE ARTIRMAK: kayan pencere ortalamasi ve
        // jitter olcumu orneklerin BAGIMSIZ oldugunu varsayar. Ayni kare iki kez islenirse
        // ortalama iyilesmez ama jitter DUSUK gorunur — panel tek bir olcumu "5 ornekle
        // dogrulanmis" gibi gosterir. Sessiz bir istatistik yalani.
        bool _frameDirty;
        int _staleSkips;
        int _nextStaleReport = 1;

        /// <summary>Her karede calisir: doku guncellendiyse bayragi kaldirir.</summary>
        void TickFrameFreshness()
        {
            var tex = GetCameraTexture();
            if (tex != null && tex.didUpdateThisFrame) _frameDirty = true;
        }

        Transform _head;
        Vector3 _headPosPrev;
        Quaternion _headRotPrev;
        bool _headPrevValid;
        float _headSpeed, _headAngSpeed;

        void TickHeadMotion()
        {
            if (_head == null)
            {
                _head = XRRigReference.HeadOrCamera;
                if (_head == null) return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0.0001f) return;

            var rigRef = XRRigReference.Instance;
            Transform rig = rigRef != null ? rigRef.transform : null;

            Vector3 p = rig != null ? rig.InverseTransformPoint(_head.position) : _head.position;
            Quaternion r = rig != null ? Quaternion.Inverse(rig.rotation) * _head.rotation
                                       : _head.rotation;

            if (_headPrevValid)
            {
                // TAKIP SICRAMASI: kafa pozu bir karede FIZIKSEL OLARAK IMKANSIZ kadar
                // degistiyse, hareket eden kafa degil TAKIP UZAYININ KENDISIDIR — gozluk
                // yeniden konumlandi (relocalization) ve dunya kaymis/donmus olabilir.
                //
                // NEDEN UYKU SINYALI YETMIYOR: kapiyi once OnApplicationPause'a baglamistim,
                // ama cihazda ekran kapanma suresi 24 saate ayarli oldugu icin gozlugu
                // cikarmak uygulamayi DURAKLATMIYOR — sinyal hic gelmiyor. Oysa takip
                // kopmasi tam da o anda oluyor. Bu yuzden olayin kendisini olcuyoruz.
                //
                // ESIKLER: 72 fps'te bir kare 14 ms. Insan kafasi o surede en fazla birkac
                // santim ve birkac derece gider; 25 cm / 45 derece ancak bir sicrama olur.
                // Olculen 175,8 ve 153,9 derecelik donmeler bu esigin cok ustunde.
                float dPos = Vector3.Distance(p, _headPosPrev);
                float dAng = Quaternion.Angle(r, _headRotPrev);
                if (dPos > 0.25f || dAng > 45f)
                {
                    if (CalibrationManager.Calibrated && !_wokeNeedsRef)
                    {
                        _wokeNeedsRef = true;
                        _wokeAt = Time.time;
                        _wokeShownSecond = -1;
                        WriteDiag($"TAKIP SICRAMASI  {dPos * 100f:0} cm / {dAng:0} derece bir karede " +
                                  $"— UYANIS KAPISI DEVREDE (tag {offsetReferenceTagId} gorulene kadar)");
                    }
                    // Hiz olcumune KATMA: sicrama gercek hareket degil, kapiyi yanlis kapatirdi.
                    _headPosPrev = p;
                    _headRotPrev = r;
                    return;
                }

                // Yumusatma: tek karelik gurultu kapiyi rastgele acip kapatmasin.
                _headSpeed = Mathf.Lerp(_headSpeed, Vector3.Distance(p, _headPosPrev) / dt, 0.3f);
                _headAngSpeed = Mathf.Lerp(_headAngSpeed, Quaternion.Angle(r, _headRotPrev) / dt, 0.3f);
            }

            _headPosPrev = p;
            _headRotPrev = r;
            _headPrevValid = true;
        }

        /// <summary>Kafa, gecikmeyi zararsiz kilacak kadar sakin mi (mesafeden bagimsiz tavanlar).</summary>
        bool HeadSteady =>
            (maxHeadSpeed <= 0f || _headSpeed <= maxHeadSpeed) &&
            (maxHeadAngularSpeed <= 0f || _headAngSpeed <= maxHeadAngularSpeed);

        /// <summary>
        /// Poz-kafa zaman uyusmazliginin bu mesafedeki TAHMINI konum bedeli (m).
        ///
        ///   hata ~ dogrusal_hiz * gecikme  +  mesafe * tan(acisal_hiz * gecikme)
        ///
        /// Ikinci terim mesafeyle buyudugu icin sabit bir hiz esigi yeterli degil: ayni
        /// 15 derece/sn, 1 metredeki tag'de 1,3 cm, 4 metredekinde 5,2 cm eder.
        /// </summary>
        float MotionError(float distance)
            => _headSpeed * assumedFrameLatency
             + distance * Mathf.Tan(_headAngSpeed * assumedFrameLatency * Mathf.Deg2Rad);

        /// <summary>Bu tag, su anki hareket altinda guvenle olculebilir mi.</summary>
        bool MotionOk(float distance)
            => HeadSteady
            && (motionErrorBudget <= 0f || MotionError(distance) <= motionErrorBudget);

        bool _intrinsicsLogged;

        /// <summary>
        /// Intrinsics'i BIR KEZ log'a yazar — ana noktanin goruntu merkezinden ne kadar
        /// kaydigini ve bunun mesafedeki karsiligini da hesaplayarak.
        ///
        /// Bu sayi dogrudan "eski kod ne kadar yaniliyordu"nun olcusudur: eski yol ana noktayi
        /// goruntu merkezi KABUL EDIYORDU, yani asagida yazan kayma kadar hata yapiyordu. Ve o
        /// hata tag'in goruntudeki yerine bagli oldugu icin her bakis acisinda farkli cikiyor,
        /// ortalamayla bastirilamiyordu.
        /// </summary>
        void LogIntrinsicsOnce(PassthroughCameraIntrinsics intr, int texW, int texH,
                               double fx, double fy, double cx, double cy)
        {
            if (_intrinsicsLogged) return;
            _intrinsicsLogged = true;

            double dx = cx - texW / 2.0;
            double dy = cy - texH / 2.0;
            double dpx = System.Math.Sqrt(dx * dx + dy * dy);
            double angX = System.Math.Atan2(dx, fx) * Mathf.Rad2Deg;
            double angY = System.Math.Atan2(dy, fy) * Mathf.Rad2Deg;
            double ang = System.Math.Sqrt(angX * angX + angY * angY);

            WriteDiag($"INTRINSICS doku {texW}x{texH}  referans {intr.Resolution.x}x{intr.Resolution.y}");
            WriteDiag($"  fx {fx:0.0}  fy {fy:0.0}   fark {System.Math.Abs(fx - fy):0.0} px");
            WriteDiag($"  cx {cx:0.0}  cy {cy:0.0}   goruntu merkezi {texW / 2.0:0.0} {texH / 2.0:0.0}");
            WriteDiag($"  ANA NOKTA KAYMASI {dpx:0.0} px = {ang:0.00} derece" +
                      $"  ->  2 m'de {200.0 * System.Math.Tan(ang * Mathf.Deg2Rad):0.0} cm yanal hata");
            WriteDiag($"  skew {intr.Skew:0.0000}");

            // DISTORSIYON. Native cozucu bunlari KABUL ETMEZ (apriltag_detection_info_t
            // yalnizca tagsize/fx/fy/cx/cy tutar; tasarim geregi DUZELTILMIS kose bekler).
            // Yani "iletmek" diye bir secenek yok — kullanilacaksa kose piksellerini biz
            // duzeltmeliyiz. Once buyuklugunu OLCUYORUZ: kareler zaten rektifiyeyse
            // katsayilar sifira yakin cikar ve is buraya kadar.
            var dist = PassthroughCameraUtils.GetCameraDistortion(_camMgr.Eye);
            if (dist == null || dist.Length == 0)
            {
                WriteDiag("  DISTORSIYON: cihaz vermiyor (muhtemelen kareler rektifiye)");
            }
            else
            {
                var sb = new System.Text.StringBuilder("  DISTORSIYON [k1,k2,k3,p1,p2] =");
                float maxAbs = 0f;
                foreach (var k in dist)
                {
                    sb.Append($" {k:0.00000}");
                    if (Mathf.Abs(k) > maxAbs) maxAbs = Mathf.Abs(k);
                }
                WriteDiag(sb.ToString());
                WriteDiag(maxAbs < 1e-4f
                    ? "  -> hepsi ~0: kareler rektifiye, duzeltmeye GEREK YOK"
                    : $"  -> en buyuk |k| = {maxAbs:0.00000} — ONEMLI, kose duzeltmesi gerekebilir");
            }
        }

        static void WriteDiag(string line)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Application.persistentDataPath, "TagDiag.log"),
                    $"[{Time.time:0.0}] {line}\n");
            }
            catch { /* teshis yazamamak oyunu durdurmamali */ }
        }

        // ---- ANCHOR TUTUSU ----------------------------------------------------------------
        bool _anchorBound;

        void TickAnchorHold()
        {
            if (!useAnchorHold) { _anchorBound = false; return; }
            if (_rig == null) return;

            if (!_anchorBound)
            {
                // Anchor ILK saglam hizalamada kurulur. Verilen hedef poz onemsiz —
                // ReanchorToCurrentRig hemen ardindan dogru cerceveyi ogretiyor. Onemli olan
                // anchor'in odada olusmasi. Bind eski anchor'i atip yenisini ASENKRON yaratir,
                // o yuzden tek sefer cagrilir.
                CalibrationAnchor.Bind(_rig, new Pose(_rig.position, _rig.rotation));
                var created = CalibrationAnchor.Instance;
                if (created == null) return;

                created.driveRig = true;
                _anchorBound = true;
                WriteDiag("ANCHOR kuruldu (tag gorunmezken cerceveyi tutacak)");
                return;   // anchor henuz olusmadi; bu karede surmez
            }

            CalibrationAnchor.Instance?.ReanchorToCurrentRig();
        }

        Constructor.ConstructorPassthrough _pt;

        /// <summary>
        /// Ogrenme modunda GERCEK odayi goster.
        ///
        /// Bu bir konfor ayari degil, sart: isaretci plakasi tag'in IDDIA EDILEN yerini
        /// cizer, dogrulamak icin GERCEK tag'le ayni karede gorulmesi gerekir. Sanal dunya
        /// aciksa gercek tag zaten gorunmez ve plakanin dogru olup olmadigi anlasilamaz.
        ///
        /// Her karede kontrol edilir: insa moduna girip cikmak passthrough'u kapatabilir,
        /// bu da onu geri acar. SetActive ayni degerde erken donuyor, bosuna is olmuyor.
        /// </summary>
        void EnsurePassthrough()
        {
            if (!showPassthrough) return;
            if (_pt == null)
            {
                _pt = FindFirstObjectByType<Constructor.ConstructorPassthrough>();
                if (_pt == null) return;
            }
            if (!_pt.Active) _pt.SetActive(true);
        }

        void ResetLearn()
        {
            _learnPos.Clear();
            _learnYaw.Clear();
            _learnId = -1;
            _learnDone = false;
        }

        /// <summary>
        /// Olculen pozu yerlesime yazar, diske kaydeder, isaretciyi tazeler.
        /// </summary>
        void ApplyLearned()
        {
            if (_learnId < 0) return;

            var list = new List<TagEntry>(tagLayout ?? Array.Empty<TagEntry>());
            var entry = list.Find(t => t != null && t.id == _learnId);

            // DONGUSEL OLCUM ENGELI: su an kalibrasyonu SUREN tag'i olcmek, onu kendi
            // cercevesinde olcmek demektir — sonuc zorunlu olarak mevcut degerin ta kendisidir
            // (olu bolge kadar sapmayla). Uzerine yazmak bilgi katmaz, olu bolge hatasini
            // yerlesime kalici olarak isler. Baska bir tag referansken olcmek anlamlidir.
            if (entry != null && entry.useForCalibration && _calibId == entry.id)
            {
                _learnNote = $"tag {entry.id} SU AN referans — kendini olcemez";
                Debug.LogWarning($"[AprilTagCalib] Tag {entry.id} yazilmadi: kalibrasyon su an " +
                                 "bu tag'den geliyor, kendi cercevesinde olculen deger dongusel olur.");
                return;
            }

            bool isNew = entry == null;
            if (isNew)
            {
                // YENI tag KAPALI dogar. Yanlis olculmus tek bir tag, dogru olanlarin kurdugu
                // cerceveyi de bozar ve hangisinin sucu oldugu anlasilmaz — once dogrulanir.
                entry = new TagEntry { id = _learnId, useForCalibration = false };
                list.Add(entry);
            }

            Vector3 before = entry.position;
            float beforeYaw = entry.yawDegrees;

            entry.position = _learnedPos;
            entry.yawDegrees = _learnedYaw;
            tagLayout = list.ToArray();

            bool saved = PersistLayout();
            RebuildMarkers();
            NoteWrite(_learnId, entry.position, entry.yawDegrees);

            float moved = (entry.position - before).magnitude;
            float yawMoved = Mathf.Abs(Mathf.DeltaAngle(beforeYaw, entry.yawDegrees));
            string hedefBuyuk = PersistTarget.ToUpperInvariant();

            _learnNote = isNew
                ? (saved ? $"TAG {_learnId} EKLENDI (kapali)" : $"TAG {_learnId} EKLENDI — {hedefBuyuk} YAZILAMADI")
                : (saved ? $"UYGULANDI ({moved * 100f:0} cm, {yawMoved:0.0} derece oynadi)"
                         : $"UYGULANDI — {hedefBuyuk} YAZILAMADI");

            Debug.Log($"[AprilTagCalib] Tag {_learnId} yerlesimi guncellendi.\n" +
                      $"  once : {before}  yaw {beforeYaw:0.0}\n" +
                      $"  simdi: {entry.position}  yaw {entry.yawDegrees:0.0}\n" +
                      $"  fark : {moved * 100f:0.0} cm, {yawMoved:0.0} derece\n" +
                      $"  hedef: {PersistTarget} ({(saved ? "yazildi" : "YAZILAMADI")})" +
                      (_fromMap ? "" : $"  {TagLayoutStore.FilePath}"));

            ResetLearn();
        }

        // ---- yerlesim isaretcileri --------------------------------------------------------
        //
        // Yerlesimin dogrulugu sayilarla denetlenemez: "-1.12" dogru mu yanlis mi, ekranda
        // bakarak anlasilmaz. Plakayi ILAN EDILEN yere cizip gercek tag'e bakmak ise tek
        // bakista soyler — ustune oturuyorsa dogru, kaymissa ne kadar kaydigini da gosterir.

        readonly List<GameObject> _markers = new List<GameObject>();

        void ClearMarkers()
        {
            foreach (var m in _markers) if (m != null) Destroy(m);
            _markers.Clear();
        }

        /// <summary>
        /// Isaretcileri yeniden KURMADAN yalnizca yerlerini tazeler. Ince ayar saniyede 12
        /// adim atiyor; her adimda obje yikip yeniden yaratmak bosuna cop uretir.
        /// </summary>
        void SyncMarkerPoses()
        {
            if (tagLayout == null) return;
            for (int i = 0; i < _markers.Count && i < tagLayout.Length; i++)
            {
                if (_markers[i] == null || tagLayout[i] == null) continue;
                _markers[i].transform.SetPositionAndRotation(
                    tagLayout[i].position, Quaternion.Euler(0f, tagLayout[i].yawDegrees, 0f));
            }
        }

        void RebuildMarkers()
        {
            ClearMarkers();
            if (!showTagMarkers || tagLayout == null) return;

            foreach (var t in tagLayout)
            {
                if (t == null) continue;
                _markers.Add(BuildMarker(t));
            }
        }

        GameObject BuildMarker(TagEntry t)
        {
            // "~" ONEKI SART: passthrough acilinca HideVirtualWorld cizen tum kok objeleri
            // kapatiyor. Oneksiz birakinca isaretci tam da ise yarayacagi anda kaybolurdu —
            // plakayi gercek tag'le karsilastirmak icin ikisini AYNI ANDA gormek gerekiyor.
            var root = new GameObject($"~TagIsaretci_{t.id}");

            root.transform.SetPositionAndRotation(t.position,
                                                 Quaternion.Euler(0f, t.yawDegrees, 0f));

            // Yesil = kalibrasyonda kullaniliyor. Sari = dogrulama bekliyor.
            Color c = t.useForCalibration ? new Color(0.2f, 1f, 0.35f) : new Color(1f, 0.85f, 0.15f);

            // PLAKA: tag boyutunda, gercek tag'in tam ustune oturmali.
            // Quad DEGIL ince kutu — Quad'in tek yuzu var, arkadan bakilinca kaybolur ve
            // "plaka yok" ile "plaka yanlis yerde" ayirt edilemez hale gelirdi.
            MakePart(root.transform, "plaka", c,
                     new Vector3(tagSizeMeters, tagSizeMeters, 0.004f), Vector3.zero);

            // BURUN: yaw'i gorunur kilar. Duz bir plaka 10 derece donukken de duz gorunur;
            // duvara dik duran bir cubuk egriligi hemen ele verir.
            //
            // IKI YONE birden uzar (merkezde, tek tarafa degil): tespitin yaw konvansiyonu
            // duvarin ICINE bakiyorsa tek tarafli cubuk duvarda kaybolur ve hicbir sey
            // gostermez. Simetrik cubuk hangi konvansiyon olursa olsun gorunur kalir.
            MakePart(root.transform, "burun", c,
                     new Vector3(0.008f, 0.008f, tagSizeMeters * 2.5f), Vector3.zero);

            // ZEMINDE AYRI BIR YON CUBUGU SART. Yukaridaki burun tag'in NORMALI boyunca

            return root;
        }

        static void MakePart(Transform parent, string name, Color c, Vector3 scale, Vector3 localPos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            // Carpisan bir teshis objesi mermileri durdurur ve oyuncuyu takar — gorsel olmali.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Paint(go, c);
        }

        static void Paint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;

            // Isiktan bagimsiz: teshis isaretcisi karanlikta da okunabilmeli.
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) return;

            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            r.sharedMaterial = m;
        }

        TagEntry Find(int id)
        {
            if (tagLayout == null) return null;
            foreach (var t in tagLayout) if (t != null && t.id == id) return t;
            return null;
        }

        WebCamTexture GetCameraTexture()
        {
            if (_camMgr == null) _camMgr = FindFirstObjectByType<WebCamTextureManager>();
            return _camMgr != null ? _camMgr.WebCamTexture : null;
        }

        void EnsureDetector(int w, int h)
        {
            if (_detector != null && w == _texW && h == _texH) return;
            _detector?.Dispose();
            _detector = new AprilTag.TagDetector(w, h, AprilTag.Interop.TagFamily.Tag36h11, decimation);
            _pixels = new Color32[w * h];
            _texW = w; _texH = h;
            Debug.Log($"[AprilTagCalib] Dedektor kuruldu: {w}x{h}, decimation={decimation}, " +
                      $"tag={tagSizeMeters:0.000} m, aile=Tag36h11.");
        }

        // ------------------------------------------------------------------ olcum (FAZ 0)

        /// <summary>Spike olcumleri: menzil ve JITTER. Sabit dururken konumun ne kadar
        /// oynadigi, buyuk alandaki hata butcesini dogrudan belirler.</summary>
        void RecordMeasurement(int id, float distance, Vector3 worldPos)
        {
            _lastTagTime = Time.time;   // tag GERCEKTEN bulundu

            if (distance < _nearestDist) { _nearestDist = distance; _nearestId = id; }

            if (!_recentByTag.TryGetValue(id, out var q))
            {
                q = new Queue<Vector3>();
                _recentByTag[id] = q;
            }
            q.Enqueue(worldPos);
            while (q.Count > RecentMax) q.Dequeue();
        }

        /// <summary>Bir tag'in kendi olcumlerindeki titreme (mm). Baska tag karismaz.</summary>
        float JitterMmFor(int id)
        {
            if (!_recentByTag.TryGetValue(id, out var q) || q.Count < 5) return 0f;

            Vector3 mean = Vector3.zero;
            foreach (var p in q) mean += p;
            mean /= q.Count;

            float maxDev = 0f;
            foreach (var p in q) maxDev = Mathf.Max(maxDev, Vector3.Distance(p, mean));
            return maxDev * 1000f;
        }

        void TickPanel()
        {
            // Gozcu PANELDEN BAGIMSIZ kosar: showPanel kapaliyken de log'a yazsin. Uyariyi
            // yalnizca panele baglamak, paneli kapatan kurulumda sorunu tekrar gorunmez
            // yapardi — kapatilan sey teshis, olen sey teshisin kendisi olurdu.
            TickRefWatch();
            TickWakeGate();

            if (!showPanel) { if (_panel != null) _panel.gameObject.SetActive(false); return; }
            if (_panel == null)
            {
                // "~" ONEKI SART: ConstructorPassthrough.HideVirtualWorld cizen TUM kok
                // objeleri kapatir, panel de kok bir obje. Oneksiz birakinca passthrough
                // acildigi anda panel kaybolurdu — yani sayilari tam da gercek dunyayi
                // gordugun anda kaybederdin.
                _panel = UI.HeadFollowPanel.Create("~AprilTag Olcum", "", Color.white);
                var f = _panel.GetComponent<UI.HeadFollowPanel>();
                if (f != null)
                {
                    f.heightOffset = 0.35f;          // kalibrasyon panelinin USTUNDE
                    // ZEMIN TAG'I ICIN SART: tag yerde oldugunda oyuncu asagi bakiyor ve duz
                    // duran panel gorus alanindan cikiyor. Olu bolge, duvar tag'indeki
                    // davranisi bozmadan bunu cozuyor.
                    f.pitchFollowDeadzone = 10f;
                }
            }
            _panel.gameObject.SetActive(true);

            // Tag SU AN goruluyor mu — olcut: SON TESPIT TURU onu buldu mu. Sabit zaman
            // penceresi KULLANILMAZ: uyarlanir hizda (hizaliyken 1 Hz) 0.4 sn'lik pencere
            // tespitler ARASINDA doluyordu, tag gozunuzun onunde dururken panel saniyede bir
            // "GORUNMUYOR" diye yanip soner ve olmayan bir sorun varmis gibi gorunurdu.
            bool seen = _lastTagTime > 0f && _lastTagTime >= _lastPassTime;
            bool cameraRunning = _lastPassTime > 0f && Time.time - _lastPassTime < 2f;

            // Tag kaybolduysa jitter kuyrugunu bosalt: eski konumlar, tag geri gelince
            // olcumu kirletir ve olmayan bir titreme gosterir.
            // 2 sn: bos gezerken tespit araligi 1 sn oldugu icin 1 sn'lik esik her turda
            // tetikleniyordu — kuyruk surekli bosalip birkac ornekle dolunca panel gercekte
            // olandan daha kotu bir jitter gosteriyordu.
            if (!seen && _recentByTag.Count > 0 && Time.time - _lastTagTime > 2f)
            {
                _recentByTag.Clear();
                _jitterMm = 0f;
            }

            _panel.color = seen ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.75f, 0.2f);

            if (seen)
            {
                // Canli durum — tek seferlik "KALIBRE EDILDI" degil, surekli hiza:
                //   "HIZALI (1.2 cm)" / "duzeltildi (6.3 cm)" / "yaklas" / "olculuyor X/5"
                // IKISI BIRDEN gosterilir. Eskiden learnMode acikken kalibrasyon satiri
                // gizleniyordu: "HIZALI" hic gorunmuyor sanilip kalibrasyon bozuk zannedildi,
                // oysa calisiyordu. Coklu tag kurulumunda ikisi ayni anda kullaniliyor
                // (tag 0 kalibre eder, tag 1 olculur) — ikisini de gormek sart.
                // B tusunun ne yapacagi ANLIK duruma bagli (yaz / sifirla) — ekranda yazmazsa
                // kullanici hangi halde oldugunu bilemez ve olcumu yanlislikla siler.
                // Passthrough istendi ama kamera kalkmadiysa SOYLE. Yoksa oyuncu sanal
                // dunyayi gorup "isaretci yanlis yerde" sanir; oysa gordugu sey gercek oda
                // bile degildir.
                _panel.text = PanelText(true, cameraRunning);
            }
            else
            {
                _panel.text = PanelText(false, cameraRunning);
            }
        }

        /// <summary>
        /// Panel metni — SADE TUTULUR.
        ///
        /// Her sayi zaten TagDiag.log'a yaziliyor ve adb ile cekilebiliyor; ekranda yalnizca
        /// ANLIK KARAR icin gerekenler kalir. Onceki surumde panel 20 satiri asiyor ve gorus
        /// alanini kapatiyordu — bir teshis penceresi, oynanabilirligi bozacak kadar
        /// buyudugunde teshis olmaktan cikar.
        ///
        /// Kaldirilanlar (hepsi log'da duruyor): eksen bazli sapma, yaw olc/bek, kumanda
        /// konumu, tag'ler arasi ilan edilen mesafeler, dokunus listesi, kapali tag kontrolu,
        /// eski kamera-ogrenme sonucu.
        /// </summary>
        string PanelText(bool seen, bool cameraRunning)
        {
            // OYUN MODU: yalnizca oyuncunun bilmesi gereken uc sey.
            //
            // Teshis paneli oyunda kapatilinca hicbir geri bildirim kalmiyordu ve "kalibre
            // etmiyor" ile "kalibre ettigini goremiyorum" ayirt edilemez hale geliyordu.
            // Cihazda yasandi: oyuncu tag 2'ye bakti, hicbir sey olmadi sandi.
            if (!learnMode)
            {
                // Yaw referansi uyarisi OYUN MODUNDA DA gorunur: gizli kalmasi tam olarak
                // sorunun kendisiydi. Tek satir ve yalnizca gercekten bozukken cikiyor.
                string refUyari = YawReferenceWarning();
                string refSatir = refUyari != null ? refUyari + "\n" : "";

                // YON DOGRULANIYOR — teyit beklerken oyuncuya SEBEBINI soyle.
                //
                // Cihazda goruldu: 180 derecelik bir sapmada dunya 2,8 saniye donuk kaldi
                // (konum hemen duzeldi, yaw teyit bekledi) ve ekranda bunu anlatan hicbir
                // sey yoktu. Oyuncu icin bu "sistem bozuldu"dan ayirt edilemez. Sayilar
                // TagDiag.log'a yaziliyor ama oyuncu log okumuyor.
                string yawSatir = _bigYawRun > 0
                    ? $"YON DOGRULANIYOR {_bigYawRun}/{yawRecoveryConfirmations}\n" : "";
                refSatir += yawSatir;

                if (!seen)
                    // Harita yeni degistiyse SEBEBI de yaz: "tag gorunmuyor" tek basina
                    // "bekle" gibi okunuyor, oysa oyuncunun YAPMASI gereken bir sey var.
                    return refSatir
                         + (_layoutStale ? "YENI HARITA — bir TAG'E BAK\n" : "")
                         + "Tag GORUNMUYOR" + (cameraRunning ? "" : "\nKAMERA YOK (izin?)");

                var q = new System.Text.StringBuilder(
                    refSatir + $"Tag {_lastId} GORUNDU   {_lastDistance:0.00} m\n");

                // DURUM SATIRI, GORULEN TAG'E AIT OLMALI.
                //
                // _calibNote GLOBAL: kalibrasyonu SUREN tag'in son durumunu tutuyor. Panel onu
                // dogrudan yazinca, gorulen tag baska biriyse BASKA BIR TAG'IN mesajini o
                // tag'e aitmis gibi gosteriyordu.
                //
                // Cihazda yasandi: oyuncu tag 2'nin yarim metre onunde dururken panel
                // "yaklas (3.20 > 2.00 m)" yaziyordu — tag 0'dan kalma bayat mesaj. Tag 2
                // yerlesimde bulunmadigi icin onun adina hicbir sey calismiyordu ve bunu
                // soyleyen de yoktu.
                var e = Find(_lastId);
                if (e == null)
                    q.Append("bu tag YERLESIMDE YOK — kalibre etmez");
                else if (!e.useForCalibration)
                    q.Append("bu tag kalibrasyonda KAPALI");
                else if (!MotionOk(_lastDistance))
                    q.Append($"BEKLE — sabit dur ({MotionError(_lastDistance) * 100f:0.0} cm hata)");
                // FUZYON SURUYORSA "kazanan tag" DIYE BIR SEY YOK.
                //
                // Bu satir tek-tag yolunun mesaji: "baktigin tag degil, su oteki kalibre
                // ediyor". Fuzyonda hepsi BIRLIKTE cozuluyor, yani _calibId bayat bir sayi.
                // Cihazda yasandi: panel "tag 3 gorunuyor / tag 2 kalibre ediyor" yaziyor,
                // sayilar surekli degisiyordu ve kullanici bunu fuzyonun kendisi sandi —
                // oysa fuzyon dogru calisiyor, panel onu anlatamiyordu.
                else if (FusionDriving)
                    q.Append(_calibNote);   // "FUZYON 4 tag (1,3 cm, yaw -0,8)"
                else if (_lastId != _calibId)
                    q.Append($"tag {_calibId} kalibre ediyor");
                else
                    q.Append(_calibNote);   // "olculuyor 3/5" / "KALIBRE EDILDI" / "HIZALI" / "yaklas"

                return q.ToString();
            }

            var p = new System.Text.StringBuilder("APRILTAG\n");

            p.Append(seen
                ? $"Tag {_lastId}   {_lastDistance:0.00} m   {_jitterMm:0.0} mm   {_detectHz:0.0} Hz\n"
                : "Tag GORUNMUYOR   " +
                  (_lastTagTime > 0f ? $"{Time.time - _lastTagTime:0} sn once\n" : "hic gorulmedi\n"));

            string refUyariTeshis = YawReferenceWarning();
            if (refUyariTeshis != null) p.Append(refUyariTeshis + "\n");

            // Teyit bekleyen buyuk sapma: teshis panelinde SAYISIYLA birlikte.
            if (_bigYawRun > 0)
                p.Append($"YON DOGRULANIYOR {_bigYawRun}/{yawRecoveryConfirmations}  " +
                         $"({_bigYawFirst:0.0} derece, tag {_bigYawTag})\n");

            if (!cameraRunning) p.Append("KAMERA YOK (izin?)\n");
            if (showPassthrough && _pt != null && _pt.Active && !_pt.CameraOk)
                p.Append("PASSTHROUGH ACILAMADI\n");

            // Kapi kapaliysa SOYLE. Yoksa oyuncu tag'e bakip hicbir sey olmamasini
            // "sistem bozuk" diye yorumlar; oysa kasitli olarak bekliyoruz.
            if (seen && !MotionOk(_lastDistance))
                p.Append($"BEKLE — hareket hatasi {MotionError(_lastDistance) * 100f:0.0} cm" +
                         $"  ({_headSpeed:0.00} m/sn, {_headAngSpeed:0} der/sn)\n");

            if (autoCalibrate && !string.IsNullOrEmpty(_calibNote)) p.Append(_calibNote + "\n");

            // Ofsetin ne kadar oturdugu: turetilen konumlara guvenilip guvenilmeyecegini soyler.
            if (OffsetSampleCount > 0)
            {
                TouchOffsetLocal(out Vector3 off, out _);
                p.Append($"OFSET {off.magnitude * 100f:0.0} cm  {OffsetSampleCount} olcum" +
                         (_refOffsets.Count >= 2 ? $"  sacilma {RefOffsetSpread() * 100f:0.0} cm" : "") + "\n");
            }
            else
            {
                // Ofset yoksa dokunustan konum turetilemez ve teshis satiri bos kalir.
                p.Append("OFSET YOK — once tag " + offsetReferenceTagId + "'a dokun\n");
            }

            if (_hasFloor) p.Append($"ZEMIN {_floorY:+0.000;-0.000}\n");

            // Iki tag'in yerlesim degerlerinin birbirini tutup tutmadigi — izlenen ana sayi.
            if (_hasSwitch)
                p.Append($"GECIS {_switchFrom}->{_switchTo}  {_switchDelta.magnitude * 100f:0.0} cm  " +
                         $"yaw {_switchYawDev:+0.0;-0.0}\n");

            // Son eylemin sonucu: dokunuldu / yazildi / reddedildi.
            if (!string.IsNullOrEmpty(_learnNote)) p.Append("> " + _learnNote + "\n");

            // Yazilan SAYILAR — bir sonraki yazmaya kadar durur (bkz. _lastWrite).
            if (!string.IsNullOrEmpty(_lastWrite)) p.Append(_lastWrite + "\n");

            if (learnMode)
                p.Append("A=yaz  solCUBUK=ince");

            return p.ToString();
        }

        /// <summary>
        /// Poz'un YATAY yonu (derece). Duvardaki tag'de normalin (ileri ekseni) yatay
        /// izdusumu; ZEMINDEKI tag'de normal dik yukari baktigi icin o izdusum dejenere olur
        /// ve tag'in KENDI yukari ekseni kullanilir.
        ///
        /// ESIK NEDEN 0,25 (yani |yatay| &lt; 0,5): eskiden 1e-8'di ve bu, normalin dikeyden
        /// 0,006 dereceden az sapmasini sart kosuyordu — gercek bir olcumde ASLA olmaz.
        /// Sonuc: zemindeki tag geri dususe hic girmiyor, kucuk ama sifirdan buyuk bir
        /// vektorun yonunu okuyordu ve o yon tamamen olcum gurultusuyle belirleniyordu.
        /// Belirtisi cihazda goruldu: zemin tag'inin isaretcisi konumu dogru, ACISI rastgele.
        ///
        /// 0,5 iki durumu temiz ayirir ve arada bosluk birakir: poz kapisi duvar tag'inin
        /// normalini yataydan en fazla 20 derece saptirtiyor (|yatay| >= 0,94), zemin
        /// tag'ininkini dikeyden 20 derece (|yatay| &lt;= 0,34).
        /// </summary>
        static float YawOf(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 0.25f)
            {
                f = q * Vector3.up; f.y = 0f;
                if (f.sqrMagnitude < 1e-8f) return 0f;   // ikisi de dikey: cozulemez
            }
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
