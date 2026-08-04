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

        [Tooltip("Donus adimi: 0..23, her adim 15 derece.")]
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
        public const float RotationStepDegrees = 15f;
        public const int RotationSteps = 24;          // 360 / 15

        /// <summary>Default for <see cref="levelHeight"/>; the grid needs it before a layout exists.</summary>
        public const float DefaultLevelHeight = 0.5f;

        public int version = 1;
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

        public RoomPlan builtForRoom = new RoomPlan();

        /// <summary>
        /// True when the map carries a room a grid can be built from. A saved map is the ONLY
        /// copy of the room it was authored in — the scan file it came from may be long gone.
        /// </summary>
        public bool HasRoom => builtForRoom != null && builtForRoom.floorPolygon != null &&
                               builtForRoom.floorPolygon.Length >= 3;

        public PlacedProp[] props = new PlacedProp[0];

        [Tooltip("Bir sonraki yerlestirmeye verilecek kimlik. Sunucu artirir; kayitta saklanir " +
                 "ki harita yeniden yuklendiginde kimlikler cakismasin.")]
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
                if (m.builtForRoom == null) m.builtForRoom = new RoomPlan();
                if (m.builtForRoom.floorPolygon == null) m.builtForRoom.floorPolygon = new Vector2[0];
                if (m.builtForRoom.walls == null) m.builtForRoom.walls = new RoomWall[0];
                if (m.builtForRoom.furniture == null) m.builtForRoom.furniture = new RoomBox[0];
                if (m.cellSize <= 0f) m.cellSize = RoomGrid.DefaultCellSize;
                if (m.buildMargin < 0f) m.buildMargin = 0f;
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
                Debug.Log($"[MapLayout] Kaydedildi: {PathFor(name)}  ({Count} prop)");
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
