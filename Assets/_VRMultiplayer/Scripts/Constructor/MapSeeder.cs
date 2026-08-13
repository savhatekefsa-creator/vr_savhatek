using System.IO;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Depoyla birlikte gelen haritalari, build'in ILK ACILISINDA yazilabilir klasore tasir.
    ///
    /// ---- NEDEN GEREKLI -----------------------------------------------------------------
    ///
    /// <c>Assets/_VRMultiplayer/Maps/*.json</c> BUILD'E GIRMEZ. Unity yalnizca bir sahnenin
    /// referans verdigi assetleri, <c>Resources/</c> ve <c>StreamingAssets/</c> icerigini
    /// paketler; duz bir klasordeki JSON bunlarin hicbiri degil. Ustelik build'de
    /// <c>Assets/</c> diye bir klasor de YOKTUR — assetler arsivlere derlenir, dosya yolu
    /// kalmaz. <see cref="MapLayout.Load"/> ise haritalari asset olarak degil
    /// <c>File.ReadAllText</c> ile okuyor, yani paketlense bile ulasamazdi.
    ///
    /// Bu yuzden <see cref="MapLayout.Directory"/> build'de <c>persistentDataPath</c>'e
    /// bakiyor: yazilabilir tek yer orasi ve haritalar yazilabilir olmak ZORUNDA (yaratici
    /// modda kaydediliyor, siliniyor, adi degistiriliyor). <c>Resources/</c> bu isi
    /// goremezdi — build'e girerdi ama SALT OKUNUR olurdu.
    ///
    /// Uc sart birden gerekiyor: build'e girsin, dosya yolu olsun, yazilabilir olsun. Tek bir
    /// klasor ucunu birden vermiyor; StreamingAssets ilk ikisini, persistentDataPath
    /// ucuncusunu veriyor. Bu sinif ikisini birlestiriyor.
    ///
    /// ---- NEDEN SENKRON, NEDEN ANDROID YOK ----------------------------------------------
    ///
    /// HARITALAR YALNIZCA OTORITEDE YASAR (bkz. <see cref="ConstructorSession.IsMapAuthority"/>):
    /// Android'de sunucu degilsek false doner ve gozluk haritayi diskten degil AGDAN alir.
    /// Yani tohumlamanin PC'de calismasi yetiyor, ve PC'de StreamingAssets duz bir klasor —
    /// <c>File.Copy</c> senkron calisiyor.
    ///
    /// Android'de StreamingAssets APK icinde sikistirilmis durur, dizin olarak gorunmez ve
    /// <c>File.Exists</c> false doner; asagidaki kontrol o durumda sessizce degil AcIKLAYARAK
    /// cikiyor. Bunu asenkron (UnityWebRequest) yapmak, tohumlamayi haritayi okuyan koddan
    /// SONRAYA birakma riski getirirdi — kazanci olmayan gercek bir tehlike. Gozluk uzerinden
    /// sunucu acmak gerekirse burasi genisletilir; o zamana kadar yazilmamis kod en iyi kod.
    ///
    /// ---- KURALLAR ----------------------------------------------------------------------
    ///
    /// UZERINE YAZMAZ. Dosya dosya bakilir; var olan harita ATLANIR. Toptan "klasor bossa
    /// kopyala" deseydik, tohumlara sonradan bir harita eklemek var olan kurulumlara hic
    /// ulasmazdi. Ustune yazsaydik oyuncunun kendi duzenlemesi her acilista geri alinirdi —
    /// ikisi de sessiz kayip.
    ///
    /// EDITORDE CALISMAZ. Editorde <see cref="MapLayout.Directory"/> zaten kaynak klasorun
    /// kendisi (<c>Assets/_VRMultiplayer/Maps</c>); kopyalamak dairesel olurdu ve silinen bir
    /// haritayi tohumdan geri diriltirdi.
    /// </summary>
    public static class MapSeeder
    {
        /// <summary>Tohumlarin build icindeki yeri: <c>Assets/StreamingAssets/Maps</c>.</summary>
        public const string SeedFolderName = "Maps";

        /// <summary>
        /// SAHNELERDEN ONCE kosar. Mevcut butun acilis kancalari AfterSceneLoad kullaniyor
        /// (ConstructorBootstrap, ConstructorPassthrough), yani haritalari okuyan hicbir kod
        /// bundan once calismiyor.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void SeedOnStartup()
        {
            if (Application.isEditor) return;   // kaynak ile hedef ayni klasor

            string kaynak = Path.Combine(Application.streamingAssetsPath, SeedFolderName);
            if (!Directory.Exists(kaynak))
            {
                // Android'de beklenen durum: StreamingAssets APK icinde, dizin degil.
                // Gozluk zaten haritayi agdan aliyor, yapilacak bir sey yok.
                Debug.Log($"[MapSeeder] Tohum klasoru dosya olarak yok ({kaynak}) — " +
                          "atlandi. Android'de bu normaldir; haritalar sunucudan gelir.");
                return;
            }

            Seed(kaynak, MapLayout.Directory);
        }

        /// <summary>
        /// Eksik haritalari kopyalar ve kac tanesinin tasindigini doner. Editor araci da
        /// bunu cagirabilsin diye ayri: tek bir kopyalama kurali olsun.
        /// </summary>
        public static int Seed(string kaynakKlasor, string hedefKlasor)
        {
            int tasinan = 0, atlanan = 0;
            try
            {
                Directory.CreateDirectory(hedefKlasor);

                foreach (string dosya in Directory.GetFiles(kaynakKlasor, "*.json"))
                {
                    string hedef = Path.Combine(hedefKlasor, Path.GetFileName(dosya));
                    if (File.Exists(hedef)) { atlanan++; continue; }   // UZERINE YAZMAZ
                    File.Copy(dosya, hedef);
                    tasinan++;
                    Debug.Log($"[MapSeeder] Harita tohumlandi: {Path.GetFileName(dosya)}");
                }
            }
            catch (System.Exception e)
            {
                // Tohumlama basarisiz olsa da oyun acilmali: harita yoksa oyuncu modu
                // "havuz bos" der, ki bu cokmekten iyidir.
                Debug.LogError($"[MapSeeder] Tohumlama basarisiz: {e.Message}");
                return tasinan;
            }

            Debug.Log($"[MapSeeder] {tasinan} harita tohumlandi, {atlanan} zaten vardi.\n" +
                      $"  kaynak: {kaynakKlasor}\n  hedef : {hedefKlasor}");
            return tasinan;
        }
    }
}
