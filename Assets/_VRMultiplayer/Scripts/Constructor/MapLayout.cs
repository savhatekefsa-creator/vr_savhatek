using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// One placed item. Deliberately tiny: a network placement message is ~12 bytes, and a
    /// 250-prop map serializes to a few KB — which is why the constructor syncs LAYOUT DATA and
    /// lets every peer build locally, instead of spawning 250 NetworkObjects.
    /// </summary>
    [Serializable]
    public class PlacedProp
    {
        [Tooltip("PropLibrary kimligi (dizin degil — kutuphane sirasi degisince bozulmasin).")]
        public string propId;

        [Tooltip("Ayak izinin MIN KOSE hucresi (merkez degil). Merkez, cift sayili ayak " +
                 "izlerinde bir hucreye denk gelmez; min kose her zaman kesindir.")]
        public int cellX;
        public int cellZ;

        [Tooltip("Kat/yukseklik adimi — zeminden yukari MapLayout.levelHeight katlari.")]
        public byte level;

        [Tooltip("Donus adimi: 0..71, her adim 5 derece. v1 kayitlarda adim 15 dereceydi; " +
                 "FromJson yuklerken x3 tasir (bkz. MapLayout.CurrentVersion).")]
        public byte rot;

        [Tooltip("GENISLIK yuzdesi (yalnizca yerel X). 100 = prefabin kendi boyu. KALINLIK " +
                 "(yerel Z) bundan etkilenmez: bir bariyeri uzatmak istedigin gibi " +
                 "sismanlatmak istemezsin. Izgara ayak izinin GENISLIK eksenini de bu belirler.\n\n" +
                 "Serbest bir sayi DEGIL: yerlestirici bunu tam hucre adimlarindan uretir " +
                 "(RoomGrid.WidthPctForCells).")]
        public byte scalePct = 100;

        [Tooltip("BOY yuzdesi (Y). Genislikten AYRI: bir bariyeri sadece yukseltmek ya da " +
                 "sadece genisletmek isteyebilirsin, ikisini birden buyutmek cogu zaman istenen " +
                 "sey degil.")]
        public byte heightPct = 100;

        [Tooltip("Sunucunun verdigi benzersiz kimlik; silme/tasima bunu adresler.")]
        public uint instanceId;

        public float Yaw => rot * MapLayout.RotationStepDegrees;

        /// <summary>Genislik carpani (yalnizca yerel X). Alan 0 ise 1.0 sayilir.</summary>
        public float WidthScale => scalePct <= 0 ? 1f : scalePct * 0.01f;

        /// <summary>Boy carpani (Y).</summary>
        public float HeightScale => heightPct <= 0 ? 1f : heightPct * 0.01f;

        /// <summary>
        /// Per-axis multiplier applied on top of the prefab's own scale.
        ///
        /// THICKNESS (local Z) IS DELIBERATELY PINNED AT 1, and this is where that rule lives.
        /// Cover is authored thin on one axis; scaling it with the width turned a longer barrier
        /// into a fatter slab, which is never what "make it bigger" means for a wall.
        /// <see cref="RoomGrid.FootprintCells"/> and the placer's ghost preview both follow it.
        /// </summary>
        public Vector3 ScaleVector => ScaleVectorOf(scalePct, heightPct);

        /// <summary>
        /// Same rule, for callers holding loose percentages instead of a placement — the build
        /// ghost previews a prop that has not been placed yet, and must not reimplement this.
        /// </summary>
        public static Vector3 ScaleVectorOf(byte scalePct, byte heightPct) => new Vector3(
            scalePct <= 0 ? 1f : scalePct * 0.01f,
            heightPct <= 0 ? 1f : heightPct * 0.01f,
            1f);
    }

    /// <summary>
    /// One FREELY transformed item — the fine-adjust layer on top of the grid.
    ///
    /// <see cref="PlacedProp"/> compresses to ~12 bytes by quantizing to cells and 5-degree
    /// yaw steps; this one deliberately does the opposite and stores the FULL transform,
    /// because its reason to exist is exactly what the grid cannot express: a wall tilted
    /// 1.8 degrees, a crate sunk 6 mm into the ground, an angle typed as 91.53. It costs a
    /// few dozen bytes and there are expected to be few of them per map.
    ///
    /// DELIBERATELY OUTSIDE THE GRID: free props occupy no cells — they neither block
    /// building nor claim walkable space. Structure belongs on the grid; this layer is for
    /// dressing and millimetre corrections, edited from the PC (gizmo/numeric input) while
    /// the headset keeps building on cells.
    /// </summary>
    [Serializable]
    public class FreePlacedProp
    {
        [Tooltip("PropLibrary kimligi (dizin degil — kutuphane sirasi degisince bozulmasin).")]
        public string propId;

        [Tooltip("ODA UZAYINDA konum (kalibre cerceve; kok objenin yerel uzayi). Y de " +
                 "serbesttir: yari gomme ya da havada durus mumkun.")]
        public Vector3 position;

        [Tooltip("Euler aci (derece), UC EKSEN de serbest — yatirma/devirme dahil.")]
        public Vector3 rotationEuler;

        [Tooltip("MUTLAK localScale (prefabin kendi olcegi dahil) — izgaradaki gibi carpan " +
                 "DEGIL. Boylece sahnede duran bir kopyanin transformu birebir kopyalanabilir.")]
        public Vector3 scale = Vector3.one;

        [Tooltip("Sunucunun verdigi benzersiz kimlik. Izgara proplariyla AYNI sayactan " +
                 "(MapLayout.nextInstanceId): silme/tasima tek adres uzayinda konusur.")]
        public uint instanceId;

        public Quaternion Rotation => Quaternion.Euler(rotationEuler);
    }

    /// <summary>
    /// A player-built arena: the prop placements plus the scanned room they were built for.
    /// Serialized with JsonUtility exactly like <see cref="RoomPlan"/>, saved by the PC server
    /// (the room's authority) and shipped to late joiners in chunks.
    ///
    /// The room plan is EMBEDDED on purpose: a map is only meaningful against the footprint it
    /// was authored in, so loading one into a different room can be validated (or refused)
    /// instead of silently placing cover inside a real wall.
    /// </summary>
    [Serializable]
    public class MapLayout
    {
        /// <summary>
        /// Dosya surumu (<see cref="version"/>).
        /// v2: donus adimi 5 derece (eskiden 15) — <see cref="FromJson"/> v1 kayitlardaki
        /// rot'u x3 carparak ayni fiziksel aciya tasir.
        /// v3: serbest prop katmani (<see cref="freeProps"/>) eklendi — eski kayitlarda alan
        /// yok, bos listeyle acilir; tasinacak veri olmadigindan damga yeterli.
        /// </summary>
        public const int CurrentVersion = 3;

        public const float RotationStepDegrees = 5f;
        public const int RotationSteps = 72;          // 360 / 5

        /// <summary>
        /// Ceyrek tur, adim cinsinden. "90 derece" demek isteyen her yer BUNU kullanmali:
        /// adim inceldiginde elle yazilmis bir 6, 90 yerine 30 derece anlamina gelirdi.
        /// </summary>
        public const int QuarterTurnSteps = RotationSteps / 4;

        /// <summary>Default for <see cref="levelHeight"/>; the grid needs it before a layout exists.</summary>
        public const float DefaultLevelHeight = 0.5f;

        public int version = CurrentVersion;
        public string name = "";
        public string createdBy = "";
        public string savedAt = "";

        [Tooltip("Kutuphane surumu — farkli surumle kaydedilmis harita uyari uretir.")]
        public int libraryVersion = 1;

        [Tooltip("Izgara hucre boyu (m). Haritaya YAZILIR: sonradan varsayilani degistirsen " +
                 "bile eski haritalar kendi izgarasiyla acilir.")]
        public float cellSize = RoomGrid.DefaultCellSize;

        [Tooltip("Bir 'kat' adiminin yuksekligi (m).")]
        public float levelHeight = DefaultLevelHeight;

        [Tooltip("Odanin DISINA ne kadar insa edilebilecegi (m). cellSize gibi haritaya yazilir: " +
                 "izgaranin boyutunu belirledigi icin her istemci ayni degeri kullanmali, yoksa " +
                 "hucre koordinatlari uyusmaz.")]
        public float buildMargin = RoomGrid.DefaultOutsideMargin;

        [Tooltip("Harita oyuncu rotasyonunda mi? Havuz, kayitli haritalarin alt kumesidir " +
                 "(bkz. MapCatalog): oyuncu modunda yalnizca bu isaretli olanlar cikar.")]
        public bool inPool;

        public RoomPlan builtForRoom = new RoomPlan();

        /// <summary>
        /// Bu haritanin KENDI tag yerlesimi. Bos ise kalibrasyon prefabdaki/diskteki genel
        /// yerlesimi kullanmaya devam eder (eski haritalar bozulmaz).
        ///
        /// NEDEN HARITANIN ICINDE: tag'ler fiziksel mekana cakili. Baska bir odaya gecince
        /// hepsinin yeri degisiyor, ve tek bir genel yerlesim varken bu "her mekan
        /// degisiminde yerlesimi bastan ayarla, eskisini kaybet" demekti. Harita zaten o
        /// mekanin kaydi — tag'lerin oraya ait olmasi, odanin planinin oraya ait olmasiyla
        /// ayni sey.
        ///
        /// BEDAVA GELEN FAYDA: harita her istemciye zaten parca parca gonderiliyor
        /// (<see cref="ConstructorSync"/>), yani tag yerlesimi de kendiliginden gidiyor.
        /// Bu, "cihazdaki TagLayout.json her gozlukte ayri" derdini yapisal olarak
        /// bitiriyor — ikinci gozlukte tag'lerin "yerlesimde YOK" cikmasi bu yuzdendi.
        ///
        /// TIP AprilTagCalibration'DAN ODUNC: ayni kaydin ikinci bir tanimini yapmak, ikisini
        /// ayri tutma isini surekli bir bakim borcuna cevirirdi. Proje tek derleme, yani
        /// bagimlilik yalnizca kavramsal.
        /// </summary>
        public AprilTagCalibration.TagEntry[] tags = new AprilTagCalibration.TagEntry[0];

        /// <summary>
        /// True when the map carries a room a grid can be built from. A saved map is the ONLY
        /// copy of the room it was authored in — the scan file it came from may be long gone.
        /// </summary>
        public bool HasRoom => builtForRoom != null && builtForRoom.floorPolygon != null &&
                               builtForRoom.floorPolygon.Length >= 3;

        public PlacedProp[] props = new PlacedProp[0];

        [Tooltip("Serbest katman: tam transformla duran proplar (bkz. FreePlacedProp). " +
                 "Izgara doluluguna girmezler; PC'den ince ayar icindir.")]
        public FreePlacedProp[] freeProps = new FreePlacedProp[0];

        [Tooltip("Bir sonraki yerlestirmeye verilecek kimlik. Sunucu artirir; kayitta saklanir " +
                 "ki harita yeniden yuklendiginde kimlikler cakismasin. Izgara ve serbest " +
                 "katman AYNI sayaci paylasir.")]
        public uint nextInstanceId = 1;

        // ------------------------------------------------------------- editing

        public PlacedProp Find(uint instanceId)
        {
            if (props == null) return null;
            for (int i = 0; i < props.Length; i++)
                if (props[i] != null && props[i].instanceId == instanceId) return props[i];
            return null;
        }

        /// <summary>Appends a placement and assigns it the next instance id.</summary>
        public PlacedProp Add(string propId, int cellX, int cellZ, byte level, byte rot,
            byte scalePct = 100, byte heightPct = 100) =>
            AddWithId(propId, cellX, cellZ, level, rot, nextInstanceId, scalePct, heightPct);

        /// <summary>
        /// Appends a placement carrying an id decided ELSEWHERE. Online, ids are the server's to
        /// hand out — if every client minted its own, two headsets placing in the same frame
        /// would produce the same id and then delete each other's prop.
        ///
        /// The local counter is pushed past the incoming id so a peer that later goes offline
        /// (or becomes the server) never reissues one.
        /// </summary>
        public PlacedProp AddWithId(string propId, int cellX, int cellZ, byte level, byte rot,
            uint instanceId, byte scalePct = 100, byte heightPct = 100)
        {
            var p = new PlacedProp
            {
                propId = propId,
                cellX = cellX,
                cellZ = cellZ,
                level = level,
                rot = (byte)(rot % RotationSteps),
                instanceId = instanceId,
                scalePct = scalePct == 0 ? (byte)100 : scalePct,
                heightPct = heightPct == 0 ? (byte)100 : heightPct,
            };

            if (instanceId >= nextInstanceId) nextInstanceId = instanceId + 1;

            var list = new List<PlacedProp>(props ?? new PlacedProp[0]) { p };
            props = list.ToArray();
            return p;
        }

        public bool Remove(uint instanceId)
        {
            if (props == null || props.Length == 0) return false;
            var list = new List<PlacedProp>(props);
            int removed = list.RemoveAll(p => p != null && p.instanceId == instanceId);
            if (removed == 0) return false;
            props = list.ToArray();
            return true;
        }

        public int Count => props != null ? props.Length : 0;

        // ---- serbest katman ----
        // Izgara metotlariyla BILEREK ayri: iki liste iki farkli sozlesme tasiyor (hucre vs
        // tam transform). Kimlik uzayi ise ORTAK (ayni nextInstanceId sayaci) — silme/tasima
        // tek adresle konusur ve iki katman ayni kimligi asla paylasamaz.

        public FreePlacedProp FindFree(uint instanceId)
        {
            if (freeProps == null) return null;
            for (int i = 0; i < freeProps.Length; i++)
                if (freeProps[i] != null && freeProps[i].instanceId == instanceId) return freeProps[i];
            return null;
        }

        /// <summary>Appends a free placement and assigns it the next instance id.</summary>
        public FreePlacedProp AddFree(string propId, Vector3 position, Vector3 rotationEuler,
            Vector3 scale) =>
            AddFreeWithId(propId, position, rotationEuler, scale, nextInstanceId);

        /// <summary>Free-layer twin of <see cref="AddWithId"/> — same id rules, same counter.</summary>
        public FreePlacedProp AddFreeWithId(string propId, Vector3 position, Vector3 rotationEuler,
            Vector3 scale, uint instanceId)
        {
            var f = new FreePlacedProp
            {
                propId = propId,
                position = position,
                rotationEuler = rotationEuler,
                // Sifir olcek gorunmez prop demek — kotu veri sinirda duzeltilir.
                scale = scale == Vector3.zero ? Vector3.one : scale,
                instanceId = instanceId,
            };

            if (instanceId >= nextInstanceId) nextInstanceId = instanceId + 1;

            var list = new List<FreePlacedProp>(freeProps ?? new FreePlacedProp[0]) { f };
            freeProps = list.ToArray();
            return f;
        }

        public bool RemoveFree(uint instanceId)
        {
            if (freeProps == null || freeProps.Length == 0) return false;
            var list = new List<FreePlacedProp>(freeProps);
            int removed = list.RemoveAll(f => f != null && f.instanceId == instanceId);
            if (removed == 0) return false;
            freeProps = list.ToArray();
            return true;
        }

        public int FreeCount => freeProps != null ? freeProps.Length : 0;

        // ------------------------------------------------------------- json

        public string ToJson(bool pretty = true) => JsonUtility.ToJson(this, pretty);

        /// <summary>Parses a map, returning null (and logging) on anything unusable.</summary>
        public static MapLayout FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var m = JsonUtility.FromJson<MapLayout>(json);
                if (m == null) return null;
                // JsonUtility eksik dizileri null birakir; tuketiciler her yerde null kontrolu
                // yapmasin diye burada bir kez normalize ediyoruz.
                if (m.props == null) m.props = new PlacedProp[0];
                if (m.tags == null) m.tags = new AprilTagCalibration.TagEntry[0];
                if (m.freeProps == null) m.freeProps = new FreePlacedProp[0];
                foreach (var f in m.freeProps)
                    if (f != null && f.scale == Vector3.zero) f.scale = Vector3.one;
                if (m.builtForRoom == null) m.builtForRoom = new RoomPlan();
                if (m.builtForRoom.floorPolygon == null) m.builtForRoom.floorPolygon = new Vector2[0];
                if (m.builtForRoom.walls == null) m.builtForRoom.walls = new RoomWall[0];
                if (m.builtForRoom.furniture == null) m.builtForRoom.furniture = new RoomBox[0];
                if (m.cellSize <= 0f) m.cellSize = RoomGrid.DefaultCellSize;
                if (m.buildMargin < 0f) m.buildMargin = 0f;

                // v1 -> v2: donus adimi 15 dereceden 5'e indi (24 -> 72 adim). Eski rot 15'lik
                // adim sayiyordu; x3 ayni fiziksel aciya tasir. Surum damgasi ikinci carpmayi
                // onler: sunucu tasinmis haritayi yayinlayinca istemci ayni JSON'u bir kez
                // daha yukluyor.
                if (m.version < 2)
                {
                    foreach (var p in m.props)
                        if (p != null) p.rot = (byte)(p.rot * 3 % RotationSteps);
                    m.version = 2;
                }

                // v2 -> v3: serbest prop katmani eklendi. Tasinacak veri yok — eski kayitta
                // alan bulunmadigindan liste bos acilir; damga "bu format taninip yazilacak"
                // kaydidir (Save hep guncel surumu yazar).
                if (m.version < 3) m.version = 3;
                return m;
            }
            catch (Exception e)
            {
                Debug.LogError("[MapLayout] JSON okunamadi: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------- files

        /// <summary>
        /// Where maps live. Same editor/device split as
        /// <see cref="RoomScanSync"/>'s room-plan save: inside Assets while working in the
        /// editor (so they are visible and version-controlled), in persistent storage on a
        /// standalone build.
        /// </summary>
        public static string Directory
        {
            get
            {
#if UNITY_EDITOR
                return Application.dataPath + "/_VRMultiplayer/Maps";
#else
                return Application.persistentDataPath + "/Maps";
#endif
            }
        }

        public static string PathFor(string mapName) => Directory + "/" + Sanitize(mapName) + ".json";

        /// <summary>Map names become file names — keep them to characters every OS accepts.</summary>
        public static string Sanitize(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return "Map";
            var sb = new System.Text.StringBuilder(mapName.Length);
            foreach (char c in mapName)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        public bool Save(string mapName = null)
        {
            if (!string.IsNullOrEmpty(mapName)) name = mapName;
            if (string.IsNullOrEmpty(name)) name = "Map";
            savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(PathFor(name), ToJson());
                Debug.Log($"[MapLayout] Kaydedildi: {PathFor(name)}  " +
                          $"({Count} izgara + {FreeCount} serbest prop)");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MapLayout] Kaydetme hatasi: " + e);
                return false;
            }
        }

        public static MapLayout Load(string mapName)
        {
            string path = PathFor(mapName);
            if (!File.Exists(path))
            {
                Debug.LogWarning("[MapLayout] Harita bulunamadi: " + path);
                return null;
            }
            return FromJson(File.ReadAllText(path));
        }

        /// <summary>Map names found on disk (no extension). Empty when the folder is missing.</summary>
        public static string[] List()
        {
            if (!System.IO.Directory.Exists(Directory)) return new string[0];
            var files = System.IO.Directory.GetFiles(Directory, "*.json");
            var names = new string[files.Length];
            for (int i = 0; i < files.Length; i++) names[i] = Path.GetFileNameWithoutExtension(files[i]);
            return names;
        }
    }
}
