using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// 55/56 — Ayni sahnedeki iki oyun alani arasinda gidip gelen ANAHTAR: RooftopArena catisi
    /// ve eski duz "Ground" zemini. 55 catiyi acar zemini kapatir, 56 tam tersini yapar.
    ///
    /// NEDEN AYRI SAHNE DEGIL DE ACIK/KAPALI. Iki alan da y = 0'da yuruyor; XR rig,
    /// NetworkManager, kalibrasyon capalari, silahlar, muzik — hepsi ortak. Bunu ikinci bir
    /// sahneye kopyalamak, bir gun birini guncelleyip digerini unutmanin garantisidir. Tek
    /// sahnede yalnizca ayagin altindaki sey degisiyor.
    ///
    /// NEDEN TEK MENU IKISINI BIRDEN YAPIYOR. Ground duzlemi 40 x 40 m ve tam y = 0'da: catiyla
    /// AYNI hizada. Ikisi birden acik kalirsa gozle z-fighting gorunur, fizikte ise ucurumun
    /// uzerine gerilmis gorunmez bir kapak olusur — oyuncu bosluga adim atar ve dusmez. Bu,
    /// sahneye bakarak fark edilmeyecek turden sessiz bir bozulma. O yuzden acma ve kapatma
    /// ayni islemin iki yarisi; ayri dugmelere bolunmus degil.
    ///
    /// UCURUM ILANI DA ANAHTARA BAGLI. Dusme yalnizca catida anlamli bir sey: eski zeminde
    /// oyun alaninin tamami 40 x 40 m'lik duzlemin ustunde, yani orada dusme HIC tetiklenmez
    /// ama <see cref="FallHazard"/> her karede ayagin altina sonda atmaya devam eder. Bosa
    /// giden istan da onemlisi okunabilirlik: sahnede acik duran bir "Fall Hazard" objesi,
    /// bakan herkese o haritada ucurum varmis izlenimi verir. 55 ilani acar, 56 kapatir.
    ///
    /// EKSIK OBJE VARSA HICBIR SEY DEGISMEZ. Yarim anahtar en kotu sonuc olurdu: hem cati hem
    /// zemin kapali kalirsa oyuncunun altinda hicbir sey yok, oyun acilir acilmaz sonsuza kadar
    /// duser. Iki taraf da bulunamadan tek bir SetActive cagrilmiyor. (Ilanin yoklugu ayni
    /// sinifta degil: ilan olmadan sadece dusme kapali kalir, sahne saglam.)
    ///
    /// Sahneyi KAYDETMEZ, yalnizca kirli isaretler — ne degistigini gorup kendin kaydet.
    /// </summary>
    public static class MapSwitchTool
    {
        /// <summary>Cati haritasinin sahnedeki kok objesi (RooftopArena_Map.fbx ornegi).</summary>
        const string RooftopName = "RooftopArena_Map";

        /// <summary>Eski sahnedeki duz zemin duzlemi.</summary>
        const string GroundName = "Ground";

        /// <summary>Anahtarin dokundugu her objede ayni Undo etiketi. Tek menu cagrisindaki
        /// kayitlar zaten ayni Undo grubuna duser (tek Ctrl+Z hepsini geri alir); ortak etiket
        /// bunun kullaniciya "Harita anahtari" diye TEK bir adim gorunmesini saglar.</summary>
        const string UndoLabel = "Harita anahtari";

        // ------------------------------------------------------------------ menuler

        [MenuItem("Tools/VR Multiplayer/55. Rooftop Arena'ya Gec (Ground kapanir)")]
        public static void SwitchToRooftopMenu()
            => EditorUtility.DisplayDialog("Harita Anahtari", SwitchTo(true), "Tamam");

        [MenuItem("Tools/VR Multiplayer/56. Eski Zemine Don (Rooftop kapanir)")]
        public static void SwitchToGroundMenu()
            => EditorUtility.DisplayDialog("Harita Anahtari", SwitchTo(false), "Tamam");

        // ------------------------------------------------------------------ is

        /// <summary>
        /// Isin kendisi — anahtari cevirir ve raporu DONDURUR. Diyalogdan ayri durmasi bilincli:
        /// bu hali betikten (ve MCP'den) cagrilabilir, modal bir pencere editoru kilitlemez.
        /// </summary>
        /// <param name="rooftop">true: cati acilir, zemin kapanir. false: tersi.</param>
        public static string SwitchTo(bool rooftop)
        {
            var rooftopObjects = FindByName(RooftopName);
            var groundObjects = FindByName(GroundName);

            if (rooftopObjects.Count == 0 || groundObjects.Count == 0)
                return MissingReport(rooftopObjects.Count, groundObjects.Count);

            int changed = SetActive(rooftopObjects, rooftop)
                        + SetActive(groundObjects, !rooftop);

            // Ucurum ilani zeminle birlikte cevrilir: dusme yalnizca catida anlamli.
            changed += SwitchHazard(rooftop, out string hazardLine);

            if (changed > 0 && !EditorApplication.isPlaying)
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            // Secimi acik olana tasi ki Scene penceresinde F ile hemen uzerine gidebilesin.
            Selection.activeGameObject = (rooftop ? rooftopObjects : groundObjects)[0];

            string report = BuildReport(rooftop, rooftopObjects, groundObjects, hazardLine, changed);
            Debug.Log(report);
            return report;
        }

        // ------------------------------------------------------------------ arama

        /// <summary>
        /// Isimle arar, KAPALI objeler de dahil (anahtarin isi zaten kapali olani bulmak).
        ///
        /// Once KOK seviyeye bakar, yalnizca orada hic eslesme yoksa derine iner. Sira onemli:
        /// haritanin icinde "Ground" adinda bir prop parcasi olabilir ve derin arama once
        /// calissaydi anahtar zemin yerine o parcayi acip kapatirdi.
        /// </summary>
        static List<GameObject> FindByName(string name)
        {
            var roots = new List<GameObject>();
            var deep = new List<GameObject>();

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name) roots.Add(root);

                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.gameObject != root && t.gameObject.name == name)
                        deep.Add(t.gameObject);
            }

            return roots.Count > 0 ? roots : deep;
        }

        /// <summary>Durumu degisenlerin sayisini dondurur; zaten dogru olana dokunmaz
        /// (gereksiz Undo adimi ve gereksiz "sahne degisti" isareti uretmemek icin).</summary>
        static int SetActive(List<GameObject> objects, bool active)
        {
            int changed = 0;
            foreach (var go in objects) changed += SetActive(go, active);
            return changed;
        }

        /// <summary>Tek obje; degistiyse 1, zaten oyleyse 0 doner.</summary>
        static int SetActive(GameObject go, bool active)
        {
            if (go.activeSelf == active) return 0;

            Undo.RecordObject(go, UndoLabel);
            go.SetActive(active);
            EditorUtility.SetDirty(go);
            return 1;
        }

        // ------------------------------------------------------------------ ucurum ilani

        /// <summary>
        /// Ucurum ilanini haritaya gore acar/kapatir; degisen obje sayisini dondurur, olan biteni
        /// <paramref name="line"/> ile anlatir.
        ///
        /// KAPATMA YOLU OBJEYE GORE SECILIYOR. Ilan, menu 47'nin kurdugu gibi tek basina duran
        /// bir obje ise objenin kendisi kapatilir: Hierarchy'de gri gorunur ve "bu haritada
        /// ucurum yok" tek bakista okunur. Bilesen baska bir seyin uzerindeyse (harita koku,
        /// XR rig, oyun yoneticisi...) o objeyi kapatmak yanindaki her seyi de kapatirdi —
        /// orada yalnizca bilesenin enabled'i cevrilir. Iki yol da <see cref="FallHazard"/>'in
        /// OnDisable'ini calistirip Instance'i bosaltir, yani dusme sistemi ikisinde de tamamen
        /// oluyor; secim davranis degil, okunabilirlik meselesi.
        /// </summary>
        static int SwitchHazard(bool wanted, out string line)
        {
            var hazards = Object.FindObjectsByType<FallHazard>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None);

            if (hazards.Length == 0)
            {
                // Catida ilanin olmamasi sessiz bir kayip: kenardan cikarsin, hicbir sey olmaz.
                // Zeminde ise dogru durum zaten bu.
                line = wanted
                    ? "SAHNEDE YOK — cati kenarindan cikan DUSMEZ. Kurmak icin: menu 47."
                    : "sahnede yok — bu haritada zaten gerekmiyor.";
                return 0;
            }

            int changed = 0;

            foreach (var hazard in hazards)
            {
                if (IsDedicatedHazardObject(hazard.gameObject))
                {
                    changed += SetActive(hazard.gameObject, wanted);
                }
                else if (hazard.enabled != wanted)
                {
                    Undo.RecordObject(hazard, UndoLabel);
                    hazard.enabled = wanted;
                    EditorUtility.SetDirty(hazard);
                    changed++;
                }
            }

            line = wanted
                ? $"ACIK — yurunen seviye y = {hazards[0].walkableLevel:0.00} m; " +
                  "cati kenarindan cikan duser."
                : "kapali — bu haritada ucurum yok, bosuna sonda atilmiyor.";

            if (hazards.Length > 1)
                line += $"  (Sahnede {hazards.Length} ilan var — fazlasini sil, hepsi cevrildi.)";

            return changed;
        }

        /// <summary>Ilan yalnizca kendi objesinde mi duruyor — yani objeyi kapatmak baska bir sey
        /// goturur mu? Cocugu olan veya uzerinde baska bilesen tasiyan obje "kendine ait"
        /// sayilmaz.</summary>
        static bool IsDedicatedHazardObject(GameObject go)
        {
            if (go.transform.childCount > 0) return false;

            foreach (var component in go.GetComponents<Component>())
                if (!(component is Transform) && !(component is FallHazard))
                    return false;

            return true;
        }

        // ------------------------------------------------------------------ rapor

        static string MissingReport(int rooftopCount, int groundCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Anahtar cevrilmedi — sahnede bir taraf bulunamadi.");
            sb.AppendLine();

            if (rooftopCount == 0) sb.AppendLine($"BULUNAMADI: '{RooftopName}'");
            if (groundCount == 0) sb.AppendLine($"BULUNAMADI: '{GroundName}'");

            sb.AppendLine();
            sb.AppendLine("Hicbir sey kapatilmadi: yarim cevrilmis anahtar oyuncuyu zeminsiz " +
                          "birakir, oyun acilir acilmaz sonsuza kadar duser.");
            sb.AppendLine();
            sb.AppendLine("Sahnedeki kok objeler:");

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                sb.AppendLine($"  - {root.name}{(root.activeSelf ? "" : "  (kapali)")}");

            sb.AppendLine();
            sb.AppendLine("Isim degistiyse bu aracin en ustundeki sabitleri guncelle.");
            return sb.ToString();
        }

        static string BuildReport(bool rooftop, List<GameObject> rooftopObjects,
                                  List<GameObject> groundObjects, string hazardLine, int changed)
        {
            var sb = new StringBuilder();

            sb.AppendLine(rooftop
                ? "ROOFTOP ARENA acik — catida oynuyorsun."
                : "ESKI ZEMIN acik — duz Ground duzlemi.");
            sb.AppendLine();
            sb.AppendLine($"{RooftopName}: {(rooftop ? "ACIK" : "kapali")}" +
                          Suffix(rooftopObjects));
            sb.AppendLine($"{GroundName}: {(rooftop ? "kapali" : "ACIK")}" +
                          Suffix(groundObjects));

            // "Neden dusmuyorum / neden dustum" sorusunun cevabi hep bu satirda.
            sb.AppendLine("Fall Hazard: " + hazardLine);

            if (changed == 0)
                sb.AppendLine("\n(Zaten bu durumdaydi — degisen bir sey olmadi.)");

            sb.AppendLine();
            sb.AppendLine(EditorApplication.isPlaying
                ? "Play modundasin: bu degisiklik Play bitince geri alinir."
                : "Sahne KAYDEDILMEDI — degisikligi gorup kendin kaydet (Ctrl+S).");

            return sb.ToString();
        }

        /// <summary>Ayni isimden birden fazla obje varsa bunu rapora yaz: sessizce hepsini
        /// degistirip kullaniciyi tek obje sandigiyla bas basa birakmaktan iyidir.</summary>
        static string Suffix(List<GameObject> objects)
            => objects.Count > 1 ? $"  ({objects.Count} obje)" : "";
    }
}
