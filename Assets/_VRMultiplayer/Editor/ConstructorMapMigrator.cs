using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   32. Haritayi Guncel Izgaraya Tasi — re-grids a saved map onto the current defaults:
    /// cell size (<see cref="RoomGrid.DefaultCellSize"/>) and how far outside the room you may
    /// build (<see cref="RoomGrid.DefaultOutsideMargin"/>).
    ///
    /// A map records BOTH of those (<see cref="MapLayout.cellSize"/>,
    /// <see cref="MapLayout.buildMargin"/>) and always reopens on that grid. That is what keeps
    /// old maps safe when a default changes — and it is also why changing a default does nothing
    /// for a map you have already built. This closes that gap: it rewrites the cell coordinates
    /// so every prop keeps its WORLD position while the lattice underneath it changes.
    ///
    /// Not a multiply-by-four, and not an offset either: the grid's origin is the room's minimum
    /// corner MINUS the outside margin, snapped to a multiple of the cell size. Change either
    /// number and the lattice starts somewhere else. Scaling or shifting the coordinates by hand
    /// would slide the whole map by that difference. Each prop is converted through world space
    /// instead, which is the only quantity both grids agree on.
    /// </summary>
    public static class ConstructorMapMigrator
    {
        [MenuItem("Tools/VR Multiplayer/32. Haritayi Guncel Izgaraya Tasi")]
        public static void MigrateMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Migrate(ConstructorSession.WorkingMapName), "Tamam");

        public static string Migrate(string mapName, float newCellSize = RoomGrid.DefaultCellSize,
            float newMargin = RoomGrid.DefaultOutsideMargin)
        {
            var layout = MapLayout.Load(mapName);
            if (layout == null) return $"'{mapName}' haritasi okunamadi.\n\n{MapLayout.PathFor(mapName)}";
            if (layout.props == null) layout.props = new PlacedProp[0];

            bool sameCell = Mathf.Abs(layout.cellSize - newCellSize) < 0.0001f;
            bool sameMargin = Mathf.Abs(layout.buildMargin - newMargin) < 0.0001f;
            if (sameCell && sameMargin)
                return $"'{mapName}' zaten {newCellSize} m hucre / {newMargin:0.0} m oda disi pay " +
                       "ile kayitli — yapilacak bir sey yok.";

            var oldGrid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
            var newGrid = RoomGrid.FromPlan(layout.builtForRoom, newCellSize,
                RoomGrid.DefaultWallMargin, newMargin, layout.levelHeight);
            if (oldGrid == null || newGrid == null)
                return "Haritanin icindeki oda planindan izgara kurulamadi.";

            // Yedek ONCE: bu dosyanin uzerine yaziyoruz ve geri donusu yalnizca bu saglar. Ad hem
            // hucreyi hem payi tasiyor: ikisi ayri ayri tasinirsa ikinci gecis birincinin yedegini
            // ezmesin.
            string stamp = layout.cellSize.ToString("0.0000") + "_" + layout.buildMargin.ToString("0.0");
            string backup = MapLayout.PathFor(mapName + "_" + stamp.Replace(".", "_"));
            try { System.IO.File.WriteAllText(backup, layout.ToJson()); }
            catch (System.Exception e) { return "Yedek yazilamadi, ise baslanmadi:\n" + e.Message; }

            var lib = PropLibrary.Instance;
            int moved = 0, skipped = 0;
            float worstDrift = 0f;
            var lost = new List<string>();

            foreach (var p in layout.props)
            {
                var def = lib.ById(p.propId);
                if (def == null) { skipped++; lost.Add(p.propId); continue; }

                // Propun DUNYA merkezi, eski kafeste.
                var oldRect = oldGrid.FootprintRect(def, p.cellX, p.cellZ, p.rot, p.scalePct);
                Vector3 world = oldGrid.RectCenter(oldRect, p.level, layout.levelHeight);

                // Yeni kafeste ayni merkeze en yakin min kose.
                Vector2Int size = RoomGrid.FootprintCells(def, p.rot, newCellSize, p.scalePct);
                int cx = Mathf.RoundToInt((world.x - newGrid.Origin.x) / newCellSize - size.x * 0.5f);
                int cz = Mathf.RoundToInt((world.z - newGrid.Origin.y) / newCellSize - size.y * 0.5f);

                p.cellX = cx;
                p.cellZ = cz;
                moved++;

                var back = newGrid.RectCenter(new RectInt(cx, cz, size.x, size.y), p.level, layout.levelHeight);
                worstDrift = Mathf.Max(worstDrift, Vector3.Distance(world, back));
            }

            layout.cellSize = newCellSize;
            layout.buildMargin = newMargin;
            if (!layout.Save(mapName)) return "Harita KAYDEDILEMEDI — Console'a bak. Yedek: " + backup;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"'{mapName}' guncel izgaraya tasindi.");
            sb.AppendLine();
            if (!sameCell) sb.AppendLine($"Hucre boyu       : {oldGrid.CellSize} m -> {newCellSize} m");
            if (!sameMargin) sb.AppendLine($"Oda disi pay     : {oldGrid.OutsideMargin:0.0} m -> {newMargin:0.0} m");
            sb.AppendLine($"Tasinan prop     : {moved}");
            if (skipped > 0)
                sb.AppendLine($"Atlanan (kutuphanede yok): {skipped}  ({string.Join(", ", lost)})");
            sb.AppendLine($"En buyuk kayma   : {worstDrift * 100f:0.0} cm");
            sb.AppendLine($"Izgara           : {oldGrid.Cols}x{oldGrid.Rows} -> {newGrid.Cols}x{newGrid.Rows} hucre");
            sb.AppendLine();
            sb.AppendLine("Yedek: " + backup);
            sb.AppendLine();
            sb.AppendLine("Sunucu ACIKSA harita bellekte duruyor; degisikligi gormek icin " +
                          "oturumu yeniden baslat (play modundan cik-gir).");
            return sb.ToString();
        }
    }
}
