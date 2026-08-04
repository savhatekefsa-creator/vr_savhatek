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

        /// <summary><see cref="sentBy"/> of a plan that <see cref="FreeSpace"/> made up.</summary>
        public const string FreeSpaceTag = "serbest alan";

        /// <summary>True when this is an invented floor rather than a real scan.</summary>
        public bool IsFreeSpace => sentBy == FreeSpaceTag;

        /// <summary>
        /// A room-shaped stand-in for "there is no scan": a square of open floor centred on the
        /// shared frame's origin, no walls, no furniture.
        ///
        /// The grid needs a polygon; it does NOT need the polygon to be true. Cells outside it
        /// come out <c>FreeOutside</c>, which is buildable, so the only thing the outline really
        /// decides is where the grid ends. That is what makes building with no headset, no scan
        /// and no PC possible — the ground is the only thing required.
        ///
        /// THE SIDE IS NOT ARBITRARY. The grid spans this square plus
        /// <c>RoomGrid.DefaultOutsideMargin</c> on every side, and <c>MaxCellsPerAxis</c> caps
        /// the result at 1024 x 6.25 cm = 64 m — of which 2 x 25 m of margin is already spent.
        /// 12 m keeps the whole thing under the cap (62 m -> 992 cells); a wider square would be
        /// clipped, and clipping is NOT symmetric (the origin is the minimum corner, so only
        /// +X/+Z lose area).
        /// </summary>
        public static RoomPlan FreeSpace(float side = 12f, float floorY = 0f, float ceilingY = 3f)
        {
            float h = Mathf.Max(1f, side) * 0.5f;
            return new RoomPlan
            {
                sentBy = FreeSpaceTag,
                floorY = floorY,
                ceilingY = ceilingY,
                floorPolygon = new[]
                {
                    new Vector2(-h, -h), new Vector2(h, -h), new Vector2(h, h), new Vector2(-h, h),
                },
                walls = new RoomWall[0],
                furniture = new RoomBox[0],
            };
        }

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
