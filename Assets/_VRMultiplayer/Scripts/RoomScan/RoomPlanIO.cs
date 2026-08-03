using System.IO;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Reads back the room plan that <see cref="RoomScanSync"/> saved. The scan path is
    /// write-only inside RoomScanSync (it is the server's job to store it); this is the
    /// read side, used by the constructor to raster a grid at runtime.
    ///
    /// COUPLING: the folder rule below MUST match <c>RoomScanSync.SaveOnServer</c> — editor
    /// builds keep plans inside Assets (visible, version controlled), standalone builds keep
    /// them in persistent storage. If one side ever moves, move the other with it.
    /// </summary>
    public static class RoomPlanIO
    {
        public const string DefaultFileName = "RoomPlan.json";

        public static string Folder
        {
            get
            {
#if UNITY_EDITOR
                return Application.dataPath + "/_VRMultiplayer/RoomPlans";
#else
                return Application.persistentDataPath + "/RoomPlans";
#endif
            }
        }

        public static string PathFor(string fileName = DefaultFileName) => Folder + "/" + fileName;

        /// <summary>The saved plan, or null when there is none / it is unusable.</summary>
        public static RoomPlan Load(string fileName = DefaultFileName)
        {
            string path = PathFor(fileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning("[RoomPlanIO] Oda plani yok: " + path);
                return null;
            }

            try
            {
                var plan = JsonUtility.FromJson<RoomPlan>(File.ReadAllText(path));
                if (plan == null || plan.floorPolygon == null || plan.floorPolygon.Length < 3)
                {
                    Debug.LogWarning("[RoomPlanIO] Oda plani bozuk (zemin poligonu yok): " + path);
                    return null;
                }
                // JsonUtility eksik dizileri null birakir — tuketiciler null kontrolu yapmasin.
                if (plan.walls == null) plan.walls = new RoomWall[0];
                if (plan.furniture == null) plan.furniture = new RoomBox[0];
                return plan;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[RoomPlanIO] Oda plani okunamadi: " + e.Message);
                return null;
            }
        }

        /// <summary>Plan file names on disk (with extension). Empty when the folder is missing.</summary>
        public static string[] List()
        {
            if (!System.IO.Directory.Exists(Folder)) return new string[0];
            var files = System.IO.Directory.GetFiles(Folder, "*.json");
            for (int i = 0; i < files.Length; i++) files[i] = Path.GetFileName(files[i]);
            return files;
        }
    }
}
