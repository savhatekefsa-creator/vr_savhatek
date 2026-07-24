using System;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// The room layout captured from a Quest 3 Space Setup scan, expressed in the SHARED
    /// calibration frame (physical point A = world origin, A->B = world +Z). Serialized with
    /// JsonUtility; saved by the PC server as Assets/_VRMultiplayer/RoomPlans/RoomPlan.json and
    /// consumed by the editor menus "Import Room Plan" / "Build Walls From Plan".
    /// </summary>
    [Serializable]
    public class RoomPlan
    {
        public int version = 1;
        public string capturedAt;      // filled in by the PC when saving
        public string sentBy;          // player name that sent the scan

        public float floorY;
        public float ceilingY;

        [Tooltip("Room footprint outline on the floor (XZ, shared frame), closed implicitly.")]
        public Vector2[] floorPolygon = new Vector2[0];

        public RoomWall[] walls = new RoomWall[0];
        public RoomBox[] furniture = new RoomBox[0];

        /// <summary>Signed polygon area (m²); absolute value is the room size.</summary>
        public float Area()
        {
            if (floorPolygon == null || floorPolygon.Length < 3) return 0f;
            float a = 0f;
            for (int i = 0; i < floorPolygon.Length; i++)
            {
                Vector2 p = floorPolygon[i];
                Vector2 q = floorPolygon[(i + 1) % floorPolygon.Length];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5f;
        }
    }

    [Serializable]
    public class RoomWall
    {
        public Vector3 center;   // world (shared frame)
        public Vector3 normal;   // world, horizontal, points into the room
        public float width;      // meters along the wall
        public float height;     // meters floor->ceiling
    }

    [Serializable]
    public class RoomBox
    {
        public string label;     // e.g. "Couch", "Table" (from scene classifications)
        public Vector3 center;   // world (shared frame)
        public Quaternion rotation;
        public Vector3 size;     // full extents in meters
    }
}
