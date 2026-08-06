using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Kayitli haritalarin ve HAVUZUN tek kapisi.
    ///
    /// KAYITLI OLMAK != HAVUZDA OLMAK. Havuz, kayitli haritalarin bir ALT KUMESI: oyuncu
    /// modunda yalnizca havuzdakiler cikar. Havuzdan cikarmak SILMEK DEGILDIR — cikan harita
    /// kayitli kalir, istenince geri eklenir; silme ayri ve geri donussuzdur.
    ///
    /// NEDEN TEK KAPI: harita secimi (Serit 3) havuzdan RASTGELE yapiliyor ve bu secim yalnizca
    /// SUNUCUDA anlamli — her gozluk kendi rastgelesini cekerse herkes baska haritaya duser.
    /// Yani bu katman er gec agin arkasina gececek. Arayuz diski dogrudan okumadigi surece
    /// (MapLayout.List/Load cagirmadigi surece) o gecis, cagiran hicbir kodu degistirmeden
    /// yapilabilir. Diske dokunan TEK yer <see cref="Refresh"/>; agli surumde orayi sunucudan
    /// gelen liste dolduracak.
    ///
    /// ANLIK GORUNTU + OLAY: <see cref="All"/> her cagrida diski taramaz, elindeki listeyi verir;
    /// degisiklikte <see cref="Changed"/> atar. Arayuz senkron okur, olayla tazelenir — agli
    /// surumde de ayni sekilde calisir, cunku orada da liste "gelmis olan"dir.
    /// </summary>
    public static class MapCatalog
    {
        /// <summary>Bir haritanin listede gorunen hali — dosyayi acmadan karar vermek icin.</summary>
        [Serializable]
        public class Entry
        {
            /// <summary>KIMLIK: dosya adi (<see cref="MapLayout.Sanitize"/>'dan gecmis hali).</summary>
            public string name;

            /// <summary>Haritanin kendi icindeki ad — bosluk gibi dosya adina giremeyen
            /// karakterleri tasiyabilir, o yuzden kimlik DEGIL.</summary>
            public string displayName;

            public bool inPool;
            public int propCount;
            public string savedAt;

            /// <summary>Havuza girebilir mi? Giremiyorsa sebebi <see cref="poolBlockReason"/>.</summary>
            public bool poolEligible;
            public string poolBlockReason;
        }

        /// <summary>Liste degisti — arayuz bunu dinleyip kendini yeniden cizer.</summary>
        public static event Action Changed;

        /// <summary>
        /// Son islemin oyuncuya gosterilecek hatasi, ya da null.
        ///
        /// ISTEMCIDE ISLEM ANINDA SONUCLANMAZ: gozluk istegi yollar, PC uygular, sonuc geri
        /// doner. O yuzden yazma metotlarinin donus degeri istemcide "istek gitti" demektir;
        /// gercek sonuc <see cref="Changed"/> ile ya da buraya yazilan hatayla gelir.
        /// </summary>
        public static string LastError { get; set; }

        /// <summary>
        /// Katalogun SAHIBI bu makine mi? Kural haritanin sahipligiyle AYNI olmali, yoksa liste
        /// bir yerde, dosyalar baska yerde olur.
        /// </summary>
        public static bool IsAuthority => ConstructorSession.IsMapAuthority;

        static List<Entry> _entries;

        /// <summary>Butun kayitli haritalar. Ilk cagrida diski tarar, sonra elindekini verir.</summary>
        public static IReadOnlyList<Entry> All
        {
            get
            {
                if (_entries == null) Refresh();
                return _entries;
            }
        }

        // ------------------------------------------------------------- okuma

        /// <summary>
        /// Listeyi tazeler.
        ///
        /// ISTEMCI KENDI DISKINE BAKMAZ. Haritalar PC'de yasiyor; gozlukte tasarlarken gorulen
        /// liste sunucunun listesi olmali, yoksa gozlukte var gorunen bir harita mac aninda
        /// bulunamaz. Istemcide bu metot yalnizca ISTEK yollar; liste cevap gelince dolar ve
        /// <see cref="Changed"/> o zaman atar.
        /// </summary>
        public static void Refresh()
        {
            if (IsAuthority) { RefreshFromDisk(); return; }

            if (_entries == null) _entries = new List<Entry>();   // cevap gelene kadar bos liste
            ConstructorSync.ClientRequestCatalog();
        }

        /// <summary>DISKE DOKUNAN TEK YER — yalnizca otoritede calisir.</summary>
        static void RefreshFromDisk()
        {
            _entries = new List<Entry>();

            foreach (string fileName in MapLayout.List())
            {
                var m = MapLayout.Load(fileName);
                if (m == null) continue;   // bozuk/okunamayan dosya listeyi dusurmesin
                _entries.Add(EntryFor(fileName, m));
            }

            _entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            Changed?.Invoke();
        }

        // ------------------------------------------------------------- ag: liste tasima

        [Serializable]
        class Snapshot { public Entry[] entries; }

        /// <summary>Sunucunun listesini tele verilecek hale getirir.</summary>
        internal static string SnapshotJson()
        {
            if (_entries == null) RefreshFromDisk();
            return JsonUtility.ToJson(new Snapshot { entries = _entries.ToArray() });
        }

        /// <summary>Sunucudan gelen listeyi kabul eder (istemci tarafi).</summary>
        internal static void ApplySnapshot(string json)
        {
            Snapshot snap;
            try
            {
                snap = JsonUtility.FromJson<Snapshot>(json);
            }
            catch (Exception e)
            {
                // JsonUtility BOZUK metinde null DONMEZ, ATAR. Bu metot bir RPC isleyicisinin
                // icinden cagriliyor: yakalanmazsa yarim kalmis tek bir paket butun mesaj
                // dongusunu dusurur ve istemci baglantiyi kaybeder.
                Debug.LogWarning("[MapCatalog] Sunucudan gelen liste cozulemedi: " + e.Message);
                return;
            }

            if (snap == null)
            {
                Debug.LogWarning("[MapCatalog] Sunucudan gelen liste bos.");
                return;
            }

            _entries = new List<Entry>(snap.entries ?? new Entry[0]);
            Changed?.Invoke();
        }

        /// <summary>Sunucu: liste degisti, herkese yolla. Otorite degilse hicbir sey yapmaz.</summary>
        static void Broadcast()
        {
            if (IsAuthority) ConstructorSync.ServerBroadcastCatalog();
        }

        /// <summary>
        /// Otoritede her yazma isleminden sonra: kendi listeni tazele VE baglilara yolla.
        /// Ikisini ayirmak, PC'de dogru gozlukte yanlis bir liste birakirdi.
        /// </summary>
        static void RefreshAndBroadcast()
        {
            RefreshFromDisk();
            Broadcast();
        }

        /// <summary>
        /// Bir harita diske YAZILDI (kaydetme yolu katalogdan gecmiyor, oturumdan geciyor).
        /// Yeni ya da adi degismis harita listeye girsin ve bagli gozlukler de gorsun.
        /// </summary>
        public static void NoteSaved()
        {
            if (IsAuthority) RefreshAndBroadcast();
        }

        public static Entry Find(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            string key = MapLayout.Sanitize(mapName);
            foreach (var e in All)
                if (string.Equals(e.name, key, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Havuzdaki haritalar — OYUNCU modunun tek kaynagi.</summary>
        public static List<Entry> Pool()
        {
            var list = new List<Entry>();
            foreach (var e in All)
                if (e.inPool) list.Add(e);
            return list;
        }

        /// <summary>
        /// Havuz bos mu? Serit 3 bunu harita seciminden ONCE sorar: son harita cikarildiginda
        /// ya da silindiginde havuz bosalabilir ve o durumda oyuncu modu hic acilmamali.
        /// </summary>
        public static bool PoolIsEmpty => Pool().Count == 0;

        /// <summary>
        /// Havuzdaki harita adlari RASTGELE SIRADA — mac haritasi buradan cekiliyor.
        ///
        /// ESIT PAY: Fisher-Yates her haritayi esit olasilikla basa koyar, yani N haritalik
        /// havuzda her birinin sansi 1/N (5 harita -> %20). Havuza harita ekleyip cikarmak
        /// paylari kendiliginden yeniden boler; elle ayarlanacak bir agirlik yok.
        ///
        /// NEDEN LISTE, TEK AD DEGIL: bastaki ad SECILEN harita, gerisi YEDEK. Secilen harita
        /// acilamazsa (dosya bozulmus ya da disaridan silinmis) sirayla digerleri denenir —
        /// tek bozuk dosya butun maci durdurmamali. Tek ad donseydi cagiran taraf ya pes
        /// edecek ya da kendi kurasini yeniden cekecekti; ikincisi ayni bozuk haritayi
        /// tekrar tekrar secebilirdi.
        ///
        /// YALNIZCA SUNUCUDA CAGRILMALI: her gozluk kendi rastgelesini cekerse ayni macin
        /// oyunculari baska haritalara duser. Secilen harita istemcilere yollanir, oradan kurulur.
        /// </summary>
        public static List<string> PoolInRandomOrder()
        {
            var pool = Pool();
            var names = new List<string>(pool.Count);
            foreach (var e in pool) names.Add(e.name);

            for (int i = names.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                string tmp = names[i];
                names[i] = names[j];
                names[j] = tmp;
            }
            return names;
        }

        /// <summary>
        /// Bu ad kullanilabilir mi? Bos adi ve MEVCUT bir haritayla cakismayi reddeder.
        ///
        /// CAKISMA GORUNDUGUNDEN GENIS: ad ayni zamanda dosya adi ve <see cref="MapLayout.Sanitize"/>
        /// dosya adina giremeyen karakterleri '_' yapiyor — yani "A B" ile "A_B" AYNI dosyaya
        /// duser. Kontrol sanitize edilmis hal uzerinden yapiliyor, yoksa ikinci harita
        /// birincisini sessizce ezerdi.
        /// </summary>
        public static bool NameAvailable(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return false;
            return Find(mapName) == null;
        }

        // ------------------------------------------------------------- havuza uygunluk

        /// <summary>
        /// Harita havuza girebilir mi? Giremiyorsa <paramref name="hata"/> oyuncuya
        /// gosterilecek sebebi tasir.
        /// </summary>
        public static bool CanEnterPool(string mapName, out string hata)
        {
            var m = MapLayout.Load(mapName);
            if (m == null) { hata = $"'{mapName}' bulunamadi."; return false; }
            return CanEnterPool(m, out hata);
        }

        /// <summary>
        /// HER TAKIMIN DOGUM BOLGESI SART.
        ///
        /// Serit 3'un son adimi "takim alanina git": oyuncu kendi takiminin baslangic alanina
        /// FIZIKSEL olarak yuruyor ve o alan haritadaki dogum propundan geliyor
        /// (<see cref="TeamSpawnZone"/> constructor bolgesini sahnedekinin onune aliyor).
        /// Dogum bolgesi olmayan bir harita havuza girerse mac baslar ama gidilecek yer olmaz —
        /// ve hata haritayi yapana degil, o haritaya denk gelen oyuncuya cikar. Kapi bu yuzden
        /// burada, havuza girerken.
        ///
        /// TAKIMLAR KUTUPHANEDEN TURETILIYOR, id yazilmiyor: <see cref="PropCategory.Spawn"/>
        /// kategorisindeki proplarin prefabindaki <see cref="TeamSpawnZone.team"/> hangi takim
        /// oldugunu soyluyor. Ileride ucuncu bir takim eklenirse kural kendiliginden genisler.
        /// </summary>
        public static bool CanEnterPool(MapLayout layout, out string hata)
        {
            hata = null;
            if (layout == null) { hata = "Harita okunamadi."; return false; }

            var spawnTeams = SpawnPropTeams();

            // Kutuphane takim ayrimi yapmiyorsa (dogum propu yok ya da hepsi ayni takim)
            // kural uygulanamaz — var olmayan bir sarti dayatip her haritayi reddetmektense
            // gecmek dogru: sart, oyunun takimli olmasindan doguyor.
            var required = new HashSet<byte>(spawnTeams.Values);
            if (required.Count < 2) return true;

            var have = new HashSet<byte>();
            if (layout.props != null)
                foreach (var p in layout.props)
                    if (p != null && spawnTeams.TryGetValue(p.propId, out byte t)) have.Add(t);

            var eksik = new List<string>();
            foreach (byte t in required)
                if (!have.Contains(t)) eksik.Add(SpawnPropName(t));

            if (eksik.Count == 0) return true;

            hata = "Havuza eklenemez: haritada " + string.Join(" ve ", eksik) +
                   " yok. Oyuncular takim alanina yurumek zorunda, o alan bu proplardan geliyor.";
            return false;
        }

        // propId -> takim. Kutuphane calisma aninda degismiyor, bir kez kurulup saklaniyor.
        static Dictionary<string, byte> _spawnTeams;

        static Dictionary<string, byte> SpawnPropTeams()
        {
            if (_spawnTeams != null) return _spawnTeams;

            _spawnTeams = new Dictionary<string, byte>();
            var lib = PropLibrary.Instance;
            if (lib == null || lib.props == null) return _spawnTeams;

            foreach (var def in lib.props)
            {
                if (def == null || def.category != PropCategory.Spawn || string.IsNullOrEmpty(def.id))
                    continue;

                var prefab = def.Resolve();
                var zone = prefab != null ? prefab.GetComponent<TeamSpawnZone>() : null;
                if (zone != null) _spawnTeams[def.id] = zone.team;
            }
            return _spawnTeams;
        }

        /// <summary>Takimin dogum propunun palette gorunen adi — hata mesajinda oyuncuya
        /// "hangi propu koymam lazim"i soyleyebilmek icin.</summary>
        static string SpawnPropName(byte team)
        {
            var lib = PropLibrary.Instance;
            foreach (var pair in SpawnPropTeams())
            {
                if (pair.Value != team) continue;
                var def = lib != null ? lib.ById(pair.Key) : null;
                return def != null && !string.IsNullOrEmpty(def.displayName) ? def.displayName : pair.Key;
            }
            return "takim " + team + " dogum bolgesi";
        }

        // ------------------------------------------------------------- yazma

        /// <summary>
        /// Haritayi oyuncu rotasyonuna alir.
        ///
        /// ISTEMCIDE donus "istek gitti" demektir; sonuc <see cref="Changed"/> ile gelir. Ama
        /// UYGUNSUZLUK yerel anlik goruntude zaten yaziyor (<c>poolEligible</c>), o yuzden
        /// sebebi soylemek icin sunucuya sormaya gerek yok.
        /// </summary>
        public static bool AddToPool(string mapName, out string hata)
        {
            hata = null;

            if (!IsAuthority)
            {
                var yerel = Find(mapName);
                if (yerel != null && !yerel.poolEligible) { hata = yerel.poolBlockReason; return false; }
                return ConstructorSync.ClientRequestPoolChange(mapName, true);
            }

            var m = MapLayout.Load(mapName);
            if (m == null) { hata = $"'{mapName}' bulunamadi."; return false; }
            if (!CanEnterPool(m, out hata)) return false;

            if (!m.inPool)
            {
                m.inPool = true;
                if (!m.Save(mapName)) { hata = $"'{mapName}' kaydedilemedi."; return false; }
                SyncOpenSession(mapName, true);
            }

            RefreshAndBroadcast();
            return true;
        }

        /// <summary>
        /// Haritayi rotasyondan alir. SILMEZ: dosya yerinde kalir, istenince geri eklenir.
        /// </summary>
        public static bool RemoveFromPool(string mapName)
        {
            if (!IsAuthority) return ConstructorSync.ClientRequestPoolChange(mapName, false);

            var m = MapLayout.Load(mapName);
            if (m == null) return false;

            if (m.inPool)
            {
                m.inPool = false;
                if (!m.Save(mapName)) return false;
                SyncOpenSession(mapName, false);
            }

            RefreshAndBroadcast();
            return true;
        }

        /// <summary>
        /// Haritanin adini degistirir — ad ayni zamanda dosya adi oldugu icin dosya tasinir.
        ///
        /// HAVUZ UYELIGI KENDILIGINDEN TASINIR, cunku uyelik haritanin ICINDE (<c>inPool</c>)
        /// duruyor. Ayri bir havuz dosyasi olsaydi her yeniden adlandirmada iki yeri birden
        /// guncellemek gerekirdi ve biri sasarsa havuz sessizce bozulurdu.
        /// </summary>
        public static bool Rename(string oldName, string newName, out string hata)
        {
            if (string.IsNullOrWhiteSpace(newName)) { hata = "Ad bos olamaz."; return false; }

            if (!IsAuthority)
            {
                // Cakisma kontrolu yerel listede de yapilabilir: cevabi beklemeden soyle.
                if (!NameAvailable(newName) &&
                    !string.Equals(MapLayout.Sanitize(oldName), MapLayout.Sanitize(newName),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    hata = $"'{newName}' zaten var.";
                    return false;
                }
                hata = null;
                return ConstructorSync.ClientRequestRename(oldName, newName);
            }

            string oldKey = MapLayout.Sanitize(oldName);
            string newKey = MapLayout.Sanitize(newName);

            if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
            {
                // Yalnizca gorunen ad degisiyor (ornegin "A B" -> "A_B"): dosya ayni kalir.
                hata = null;
                return WriteDisplayName(oldName, newName);
            }

            if (!NameAvailable(newName)) { hata = $"'{newName}' zaten var."; return false; }

            var m = MapLayout.Load(oldName);
            if (m == null) { hata = $"'{oldName}' bulunamadi."; return false; }

            m.name = newName;
            if (!m.Save(newName)) { hata = "Yeni ad kaydedilemedi."; return false; }

            DeleteFiles(oldKey);

            var s = ConstructorSession.Instance;
            if (s != null && SameMap(s.CurrentMapName, oldName)) s.NoteRenamed(newKey);

            RefreshAndBroadcast();
            hata = null;
            return true;
        }

        /// <summary>Haritayi TAMAMEN kaldirir — geri donusu yok.</summary>
        public static bool Delete(string mapName)
        {
            if (!IsAuthority) return ConstructorSync.ClientRequestDelete(mapName);

            string key = MapLayout.Sanitize(mapName);
            if (!File.Exists(MapLayout.PathFor(key))) return false;

            DeleteFiles(key);

            // Acik harita silindiyse adini dusur: yoksa bir sonraki otomatik kayit dosyayi
            // geri diriltir ve "sildim ama duruyor" olur.
            var s = ConstructorSession.Instance;
            if (s != null && SameMap(s.CurrentMapName, mapName)) s.NoteDeleted();

            RefreshAndBroadcast();
            return true;
        }

        // ------------------------------------------------------------- ic isler

        static Entry EntryFor(string fileName, MapLayout m)
        {
            bool ok = CanEnterPool(m, out string sebep);
            return new Entry
            {
                name = fileName,
                displayName = string.IsNullOrEmpty(m.name) ? fileName : m.name,
                inPool = m.inPool,
                propCount = m.Count,
                savedAt = m.savedAt,
                poolEligible = ok,
                poolBlockReason = sebep,
            };
        }

        static bool WriteDisplayName(string mapName, string newDisplayName)
        {
            var m = MapLayout.Load(mapName);
            if (m == null) return false;
            m.name = newDisplayName;
            if (!m.Save(mapName)) return false;
            RefreshAndBroadcast();
            return true;
        }

        /// <summary>
        /// Diskteki bayrak degisti; harita SU AN ACIKSA bellekteki kopyayi da tasi. Yoksa
        /// oturumun bir sonraki kaydi eski bayragi geri yazar ve havuz kendiliginden geri alinir.
        /// </summary>
        static void SyncOpenSession(string mapName, bool inPool)
        {
            var s = ConstructorSession.Instance;
            if (s == null || s.Layout == null) return;
            if (!SameMap(s.CurrentMapName, mapName)) return;
            s.Layout.inPool = inPool;
        }

        static bool SameMap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(MapLayout.Sanitize(a), MapLayout.Sanitize(b),
                                 StringComparison.OrdinalIgnoreCase);
        }

        static void DeleteFiles(string key)
        {
            string path = MapLayout.PathFor(key);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                // Editorde her dosyanin bir .meta'si var; birakilirsa Unity "eksik dosya"
                // uyarisi verir. Build'de zaten yok, File.Exists kontrolu yetiyor.
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MapCatalog] '{key}' silinemedi: {e.Message}");
            }
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir: eski oturumun listesi
        // ikinci Play'de silinmis bir haritayi gostermeye devam ederdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _entries = null;
            _spawnTeams = null;
            Changed = null;
        }
    }
}
