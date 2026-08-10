using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>What a single grid cell can be used for.</summary>
    public enum CellState : byte
    {
        /// <summary>Beyond the grid entirely — no such cell.</summary>
        Outside = 0,
        /// <summary>Buildable AND inside the physical room, clear of the walls: safe to walk.</summary>
        Free = 1,
        /// <summary>Taken by a placed prop.</summary>
        Occupied = 2,
        /// <summary>Real furniture stands here — nothing may be placed.</summary>
        Blocked = 3,
        /// <summary>
        /// Buildable but NOT walkable play space: beyond the scanned room, or hugging a wall.
        /// The map may extend past the physical room — scenery, a treeline, cover to shoot
        /// past — the player just cannot stand there.
        /// </summary>
        FreeOutside = 4,
    }

    /// <summary>
    /// The buildable cell map derived from a Quest room scan. Pure C# (no MonoBehaviour, no
    /// scene) so it can be built and inspected in the editor from a saved RoomPlan.json without
    /// a headset.
    ///
    /// AXIS ALIGNMENT: the grid is aligned to the SHARED CALIBRATION FRAME (physical point A =
    /// origin, A->B = +Z), not to the room's walls. That is what makes it identical on every
    /// headset — which in turn is what lets peers build the same map from the same layout
    /// without syncing per-prop transforms.
    ///
    /// The origin is snapped down to a whole multiple of <see cref="CellSize"/> so two peers
    /// deriving the grid from the same plan land on the same lattice, bit for bit.
    /// </summary>
    public class RoomGrid
    {
        /// <summary>
        /// Placement resolution for NEW maps only: every map records the cell size it was
        /// authored with (<see cref="MapLayout.cellSize"/>) and reopens on that grid, and prop
        /// sizes are metric (<see cref="PropDef.sizeMeters"/>), so changing this moves nothing.
        ///
        /// Halved from 0.125 m. The rounding a footprint suffers is half a cell, and so is the
        /// thickness a sub-cell prop gets inflated to — on a 0.25 m grid a 3.4 cm modular wall
        /// was standing 25 cm thick. What made the finer grid affordable is that the lattice is
        /// no longer drawn in full (<see cref="ConstructorGridView.viewRadius"/>); cell count
        /// grows with the SQUARE, so without that the view mesh would have gone up sixteenfold.
        /// </summary>
        public const float DefaultCellSize = 0.0625f;

        /// <summary>Gercek duvardan birakilan pay (m): duvara gomulu prop cirkin durur.</summary>
        public const float DefaultWallMargin = 0.15f;

        /// <summary>
        /// Odanin DISINA ne kadar insa edilebilecegi (m). Harita fiziksel oyun alaniyla sinirli
        /// degil: duvarin otesine agac/siper koyup uzerinden ates edilebilir, oyuncu oraya
        /// yurumese de.
        ///
        /// 5 m'den bes katina cikarildi. Bes metre odayi ceviren ince bir seritti: uzaga bir
        /// siper hatti ya da silue kurmaya calisirken isin daha oraya varmadan hayalet kirmizi
        /// oluyordu, ve "buraya neden konmuyor" sorusunun cevabi gorunur hicbir yerde yazmiyordu.
        /// Bedeli hucre sayisi: izgara ALANLA, yani payin KARESIYLE buyuyor. O yuzden iki sey
        /// birlikte degisti — <see cref="MaxCellsPerAxis"/> tavani ve <see cref="FromPlan"/>'in
        /// poligon testini yalnizca odanin sinir kutusuna yapmasi.
        /// </summary>
        public const float DefaultOutsideMargin = 25f;

        /// <summary>
        /// Ceiling against a corrupt scan producing a giant grid and eating memory. Counted in
        /// CELLS, so it is raised whenever <see cref="DefaultCellSize"/> gets finer or
        /// <see cref="DefaultOutsideMargin"/> gets wider, to keep the same physical reach
        /// (1024 x 0.0625 m = 64 m per axis).
        ///
        /// At 240 the halved cell landed on 239 x 219 for this room — inside the limit by one
        /// cell, which is not a margin, it is a coincidence waiting to clip somebody's grid the
        /// first time they scan a bigger room. The 25 m pay took the same room to ~880 cells per
        /// axis, so 512 would have CLIPPED it — and clipping is not symmetric: the origin is the
        /// minimum corner, so the half that gets cut is all on the +X/+Z side and the map would
        /// quietly grow in two directions only.
        ///
        /// Three bytes per cell (state, base state, level mask) means even 1024 x 1024 is 3 MB;
        /// the guard is here to catch a corrupt scan, not to ration memory.
        /// </summary>
        public const int MaxCellsPerAxis = 1024;

        /// <summary>
        /// How many build levels a cell can hold. Eight because occupancy is one BYTE per cell —
        /// at the default 0.5 m step that is 4 m of headroom, above any scanned ceiling, and it
        /// keeps the vertical dimension free: a full third array would have quadrupled the
        /// grid's memory to store mostly-empty air.
        /// </summary>
        public const int MaxLevels = 8;

        public float CellSize { get; private set; }
        public float LevelHeight { get; private set; }
        public float WallMargin { get; private set; }
        public float OutsideMargin { get; private set; }
        public float FloorY { get; private set; }
        public float CeilingY { get; private set; }

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        /// <summary>World XZ of the (0,0) cell's minimum corner.</summary>
        public Vector2 Origin { get; private set; }

        CellState[] _cells;

        // Hucrenin prop KONULMADAN ONCEKI hali. Release bunu geri yukler; yoksa bir propu
        // silmek oda disindaki bir hucreyi "oda ici" yapardi ve mobilya kapali bir hucre
        // serbest kalirdi.
        CellState[] _base;

        // HANGI KATLAR dolu — hucre basina bir bit maskesi.
        //
        // _cells tek basina "burada bir sey var" diyebiliyordu, hangi YUKSEKLIKTE oldugunu
        // degil; o yuzden yerdeki bir masa, ustundeki butun havayi da kapatiyordu. Iki dizi
        // birlikte tek bir kurali tutuyor: _cells[i] == Occupied  <=>  _levels[i] != 0.
        byte[] _levels;

        RoomGrid() { }

        /// <summary>Prop konabilir mi? Oda ici ve oda disi serbest hucrelerin ikisi de olur.</summary>
        public static bool IsBuildable(CellState s) => s == CellState.Free || s == CellState.FreeOutside;

        // ------------------------------------------------------------- construction

        /// <summary>
        /// Rasterizes a scanned room into cells. Returns null when the plan has no usable floor
        /// polygon (fewer than 3 corners) — callers should treat that as "no room yet".
        /// </summary>
        public static RoomGrid FromPlan(RoomPlan plan,
            float cellSize = DefaultCellSize, float wallMargin = DefaultWallMargin,
            float outsideMargin = DefaultOutsideMargin,
            float levelHeight = MapLayout.DefaultLevelHeight)
        {
            if (plan == null || plan.floorPolygon == null || plan.floorPolygon.Length < 3)
            {
                Debug.LogWarning("[RoomGrid] Zemin poligonu yok/gecersiz — izgara kurulamadi.");
                return null;
            }
            if (cellSize <= 0.01f) cellSize = DefaultCellSize;

            var poly = plan.floorPolygon;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in poly)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }

            // Odanin KENDI sinir kutusu, pay eklenmeden. Asagida hangi hucrelere poligon testi
            // yapilacagini bu belirliyor.
            float roomMinX = minX, roomMaxX = maxX, roomMinZ = minZ, roomMaxZ = maxZ;

            // Izgara odanin DISINA da tasar: harita fiziksel alandan buyuk olabilir.
            float pad = Mathf.Max(0f, outsideMargin);
            minX -= pad; maxX += pad;
            minZ -= pad; maxZ += pad;

            var grid = new RoomGrid
            {
                CellSize = cellSize,
                LevelHeight = levelHeight > 0.01f ? levelHeight : MapLayout.DefaultLevelHeight,
                WallMargin = Mathf.Max(0f, wallMargin),
                OutsideMargin = pad,
                FloorY = plan.floorY,
                CeilingY = plan.ceilingY,
                // Kafese hizala: ayni plandan turetildiginde her istemcide AYNI koken cikar.
                Origin = new Vector2(Mathf.Floor(minX / cellSize) * cellSize,
                                     Mathf.Floor(minZ / cellSize) * cellSize),
            };

            int cols = Mathf.CeilToInt((maxX - grid.Origin.x) / cellSize);
            int rows = Mathf.CeilToInt((maxZ - grid.Origin.y) / cellSize);
            grid.Cols = Mathf.Clamp(cols, 1, MaxCellsPerAxis);
            grid.Rows = Mathf.Clamp(rows, 1, MaxCellsPerAxis);
            if (cols > MaxCellsPerAxis || rows > MaxCellsPerAxis)
                Debug.LogWarning($"[RoomGrid] Oda cok buyuk ({cols}x{rows} hucre) — " +
                                 $"{MaxCellsPerAxis} ile sinirlandi. Tarama hatali olabilir.");

            grid._cells = new CellState[grid.Cols * grid.Rows];
            grid._levels = new byte[grid.Cols * grid.Rows];

            // Poligon testleri yalnizca odanin sinir kutusundaki hucrelere yapilir. Kutunun
            // disindaki bir noktanin poligonun icinde OLAMAYACAGI kesin, yani sonuc birebir ayni;
            // degisen tek sey kac kez soruldugu. Pay buyudukce fark ucuyor: 25 m payla 5 m'lik bir
            // odada hucrelerin ~%1'i kutunun icinde kaliyor, kalan %99'u iki tamsayi
            // karsilastirmasiyla eleniyor. Aksi halde insa moduna girerken izgara kurulumu payin
            // karesiyle uzardi.
            int boxMinCx = Mathf.FloorToInt((roomMinX - grid.Origin.x) / cellSize) - 1;
            int boxMaxCx = Mathf.CeilToInt((roomMaxX - grid.Origin.x) / cellSize) + 1;
            int boxMinCz = Mathf.FloorToInt((roomMinZ - grid.Origin.y) / cellSize) - 1;
            int boxMaxCz = Mathf.CeilToInt((roomMaxZ - grid.Origin.y) / cellSize) + 1;

            // 1) Her hucre insa edilebilir; ayrim YURUNEBILIR mi degil mi. Oda icinde ve
            //    duvardan uzaksa Free (guvenli oyun alani), degilse FreeOutside.
            for (int cz = 0; cz < grid.Rows; cz++)
            {
                bool zInBox = cz >= boxMinCz && cz <= boxMaxCz;
                for (int cx = 0; cx < grid.Cols; cx++)
                {
                    bool inside = false;
                    if (zInBox && cx >= boxMinCx && cx <= boxMaxCx)
                    {
                        Vector2 c = grid.CellCenter2D(cx, cz);
                        inside = PointInPolygon(c, poly) &&
                                 (grid.WallMargin <= 0f ||
                                  DistanceToPolygonEdge(c, poly) >= grid.WallMargin);
                    }
                    grid._cells[cz * grid.Cols + cx] = inside ? CellState.Free : CellState.FreeOutside;
                }
            }

            // 2) Gercek mobilya: uzerine prop konamaz.
            grid.BlockFurniture(plan);

            // 3) Baslangic halini sakla — Release bunu geri yukler.
            grid._base = (CellState[])grid._cells.Clone();

            return grid;
        }

        void BlockFurniture(RoomPlan plan)
        {
            if (plan.furniture == null) return;

            foreach (var box in plan.furniture)
            {
                if (box == null) continue;

                // Yalnizca ZEMINDE duran esyalar hucre kapatir. Tavan lambasi / duvar rafi
                // gibi yukarida duran kutular yer isgal etmemeli.
                float bottom = box.center.y - box.size.y * 0.5f;
                if (bottom > FloorY + 1.0f) continue;

                Vector2 half = new Vector2(Mathf.Abs(box.size.x) * 0.5f, Mathf.Abs(box.size.z) * 0.5f);
                if (half.x < 0.01f || half.y < 0.01f) continue;

                // Donmus kutunun eksen-hizali sinir kutusunu tara, sonra her hucreyi kutunun
                // KENDI uzayinda test et — donuk bir koltuk sadece gercekten kapladigi
                // hucreleri kapatsin.
                Quaternion inv = Quaternion.Inverse(box.rotation);
                float reach = half.magnitude;
                var min = WorldToCell(new Vector3(box.center.x - reach, FloorY, box.center.z - reach));
                var max = WorldToCell(new Vector3(box.center.x + reach, FloorY, box.center.z + reach));

                for (int cz = Mathf.Max(0, min.y); cz <= Mathf.Min(Rows - 1, max.y); cz++)
                {
                    for (int cx = Mathf.Max(0, min.x); cx <= Mathf.Min(Cols - 1, max.x); cx++)
                    {
                        Vector2 c = CellCenter2D(cx, cz);
                        Vector3 local = inv * (new Vector3(c.x, box.center.y, c.y) - box.center);
                        if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.y)
                            _cells[cz * Cols + cx] = CellState.Blocked;
                    }
                }
            }
        }

        // ------------------------------------------------------------- cell access

        public bool InBounds(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Cols && cz < Rows;

        public CellState State(int cx, int cz) =>
            InBounds(cx, cz) ? _cells[cz * Cols + cx] : CellState.Outside;

        /// <summary>World-space centre of a cell, on the floor plane.</summary>
        public Vector3 CellCenter(int cx, int cz)
        {
            Vector2 c = CellCenter2D(cx, cz);
            return new Vector3(c.x, FloorY, c.y);
        }

        Vector2 CellCenter2D(int cx, int cz) =>
            new Vector2(Origin.x + (cx + 0.5f) * CellSize, Origin.y + (cz + 0.5f) * CellSize);

        /// <summary>
        /// Cell containing a world point. May be OUT OF BOUNDS — callers that use the result as
        /// an index must check <see cref="InBounds"/> first (the ghost cursor relies on getting
        /// out-of-range values so it can show "invalid" outside the room).
        /// </summary>
        public Vector2Int WorldToCell(Vector3 world) => new Vector2Int(
            Mathf.FloorToInt((world.x - Origin.x) / CellSize),
            Mathf.FloorToInt((world.z - Origin.y) / CellSize));

        // ------------------------------------------------------------- footprints

        /// <summary>
        /// The AXIS-ALIGNED BOX a rotated prop spans, in cells. Quarter turns swap the footprint
        /// exactly; any other angle gets the true bounding box of the turned rectangle.
        ///
        /// This is a BOUND, not the occupied set. At an angle the prop only covers a diagonal
        /// band inside this box, and the corners stay free — <see cref="Shape"/> and the
        /// placement overloads of <see cref="CanPlace"/> are what decide, cell by cell, which
        /// ones the prop is actually standing on. Reaching for this rectangle as though it were
        /// the footprint is how an angled wall ends up reserving the whole room.
        ///
        /// It used to BE the answer: any non-quarter turn was rounded up to a square, which for
        /// a 0.25 x 2.0 m wall meant holding 2 x 2 m — nothing could be built within two metres
        /// of anything placed at an angle. That was only tolerable while occupancy was
        /// rectangle-shaped anyway.
        ///
        /// Metres in, cells out — see <see cref="PropDef.sizeMeters"/>. Named apart from the
        /// instance overload on purpose: with both called <c>FootprintSize</c>, a plain
        /// <c>FootprintSize(def, rot, 100)</c> would bind 100 to <paramref name="cellSize"/>
        /// instead of the scale percent and silently return a one-cell footprint.
        /// </summary>
        public static Vector2Int FootprintCells(PropDef def, byte rot, float cellSize,
            byte scalePct = 100)
        {
            Vector2 m = def != null ? def.sizeMeters : Vector2.one;
            if (cellSize <= 0.001f) cellSize = DefaultCellSize;

            // EN YAKIN hucre, yukari degil. Bir propun 0.54 m'lik golgesi 0.25 m izgarada 2
            // hucre sayilir (2.16 -> 2). Ceil olsaydi 3 cikar, ayak izi bir hucre buyur ve
            // RectCenter merkezi yarim hucre kaydirirdi — yani kayitli haritalardaki proplar
            // yerinden oynardi. Olcum hucre cinsinden saklanirken de boyleydi; birimi metreye
            // tasimak yuvarlamayi degistirmemeli.
            var f = new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(m.x / cellSize)),
                Mathf.Max(1, Mathf.RoundToInt(m.y / cellSize)));

            // Olcek yalnizca genislige (yerel X) uygulanir, kalinliga degil — kurali
            // PlacedProp.ScaleVector tanimliyor, izgara ona uymak zorunda.
            //
            // EN YAKIN hucre, tabandaki yuvarlama gibi. Eskiden Ceil'di, gerekcesi "yarim
            // hucreye tasan prop komsusuyla cakisir" idi — ama taban olcu zaten Round, yani
            // %100'deki prop da yarim hucre tasabiliyordu; Ceil sadece olcekli propu bir hucre
            // sisirip yanina konani uzaklastiriyordu. Iki ucu da PropDef.fitToFootprint kapatti:
            // prop rezerve ettigi dikdortgeni tam dolduruyor, o yuzden dogru olan en yakina
            // yuvarlamak. Yuzdeler de artik tam hucre adimlarindan uretiliyor
            // (<see cref="WidthPctForCells"/>), yani Round cogu zaman kesin sonuc verir.
            if (scalePct != 100 && scalePct > 0)
                f.x = Mathf.Max(1, Mathf.RoundToInt(f.x * (scalePct * 0.01f)));

            // Ceyrek turlarda ayak izi ya aynen kalir ya takla atar — kesir yok, yani kayitli
            // haritalar bu daldan hic etkilenmez.
            if (rot % MapLayout.QuarterTurnSteps == 0)
                return (rot / MapLayout.QuarterTurnSteps) % 2 == 1 ? new Vector2Int(f.y, f.x) : f;

            // Ara acilar: donmus dikdortgenin gercek sinir kutusu.
            //
            // YUKARI yuvarlaniyor, en yakina degil: kutu OBB'yi ICERMEK zorunda. Kucuk kalirsa
            // propun gercekten bastigi bir hucre taramanin disinda kalir, isgal edilmez ve
            // uzerine ikinci bir prop konabilir. Epsilon, 4.0000001 gibi bir degerin bir hucre
            // fazla istemesini engelliyor.
            float rad = rot * MapLayout.RotationStepDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Abs(Mathf.Cos(rad));
            float sin = Mathf.Abs(Mathf.Sin(rad));
            return new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(f.x * cos + f.y * sin - 0.0001f)),
                Mathf.Max(1, Mathf.CeilToInt(f.x * sin + f.y * cos - 0.0001f)));
        }

        /// <summary>
        /// Cells the prop covers on THIS grid — the overload to reach for, since it cannot be
        /// handed a cell size that disagrees with the grid the result is used against.
        /// </summary>
        public Vector2Int FootprintSize(PropDef def, byte rot, byte scalePct = 100) =>
            FootprintCells(def, rot, CellSize, scalePct);

        /// <summary>
        /// Width of the UNROTATED footprint in cells, at a given scale percentage. The quantity
        /// the player is really adjusting when they "make it wider" — see
        /// <see cref="WidthPctForCells"/>.
        /// </summary>
        public static int WidthCells(PropDef def, float cellSize, byte scalePct = 100) =>
            FootprintCells(def, 0, cellSize, scalePct).x;

        /// <summary>
        /// The scale percentage that makes a prop exactly <paramref name="targetCells"/> cells
        /// wide — the inverse of <see cref="WidthCells"/>, and the reason the size control steps
        /// in CELLS rather than in percent.
        ///
        /// Percent steps were the wrong unit: a footprint only ever exists in whole cells, so on
        /// a wide prop several consecutive 10% presses land on the same cell count and do
        /// nothing, while on a narrow one a single press jumps two cells. Stepping the cell
        /// count and deriving the percentage from it makes one press mean one cell, always —
        /// which is exactly the granularity at which props can be butted up against each other.
        ///
        /// The percentage is still what gets stored and sent (<see cref="PlacedProp.scalePct"/>),
        /// so nothing about the save format or the wire changes.
        /// </summary>
        public static byte WidthPctForCells(PropDef def, float cellSize, int targetCells)
        {
            int baseCells = Mathf.Max(1, FootprintCells(def, 0, cellSize).x);
            int target = Mathf.Max(1, targetCells);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(target * 100f / baseCells), 1, 255);
        }

        /// <summary>
        /// Min-corner cell for a footprint the player is pointing at, so the prop appears
        /// CENTRED under the cursor instead of growing to one side.
        /// </summary>
        public static Vector2Int CenterToMin(Vector2Int pointedCell, Vector2Int size) =>
            new Vector2Int(pointedCell.x - (size.x - 1) / 2, pointedCell.y - (size.y - 1) / 2);

        public RectInt FootprintRect(PropDef def, int minCellX, int minCellZ, byte rot,
            byte scalePct = 100)
        {
            Vector2Int s = FootprintCells(def, rot, CellSize, scalePct);
            return new RectInt(minCellX, minCellZ, s.x, s.y);
        }

        // ------------------------------------------------------------- occupancy

        /// <summary>
        /// The ground a placement actually stands on: its bounding box, plus the turned
        /// rectangle inside it. Everything that asks "which cells does this prop hold" goes
        /// through here, so placement, occupancy and release can never disagree about the shape.
        ///
        /// A struct, and deliberately free of allocation: the build cursor rebuilds this every
        /// frame while the player sweeps the room, and a per-frame array would be garbage the
        /// headset has to collect mid-build.
        /// </summary>
        public struct Shape
        {
            /// <summary>Taranacak eksen-hizali hucre kutusu — kaplama bunun ICINDE aranir.</summary>
            public RectInt rect;
            /// <summary>Ceyrek turlarda donuk kutu tam olarak <see cref="rect"/>; hucre testi gereksiz.</summary>
            public bool axisAligned;

            public Vector2 center;          // dunya XZ
            public Vector2 half;            // yerel yari-olculer (m)
            public Vector2 axisX, axisZ;    // yerel eksenlerin dunyadaki yonu
        }

        /// <summary>Where a placement sits and what it covers. See <see cref="Shape"/>.</summary>
        public Shape ShapeOf(PropDef def, Vector2Int minCell, byte rot, byte scalePct = 100)
        {
            var rect = FootprintRect(def, minCell.x, minCell.y, rot, scalePct);

            Vector3 c = RectCenter(rect, 0, 0f);

            var s = new Shape
            {
                rect = rect,
                axisAligned = rot % MapLayout.QuarterTurnSteps == 0,
                center = new Vector2(c.x, c.z),
            };
            if (s.axisAligned) return s;

            // Yari-olculer DONDURULMEMIS ayak izinden, hucre cinsinden: prop rezerve ettigi
            // hucreleri dolduruyor (PropDef.fitToFootprint), yani izgaranin olcusu propun
            // olcusu. Ayni sayiyi metreden bir kez daha turetmek ikisini ayirma riski olurdu.
            Vector2Int unrotated = FootprintCells(def, 0, CellSize, scalePct);
            s.half = new Vector2(unrotated.x * CellSize * 0.5f, unrotated.y * CellSize * 0.5f);

            // Unity'de Y ekseni etrafinda donuste yerel +X (cos, -sin), yerel +Z (sin, cos)
            // olur — Quaternion.Euler(0, 90, 0) * Vector3.right == Vector3.back.
            float rad = rot * MapLayout.RotationStepDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            s.axisX = new Vector2(cos, -sin);
            s.axisZ = new Vector2(sin, cos);
            return s;
        }

        /// <summary>
        /// Does the placement stand on this cell?
        ///
        /// Separating-axis test between the cell square and the turned rectangle — NOT "is the
        /// cell centre inside". Centre testing looks equivalent and fails on exactly the props
        /// this feature is for: a 3 cm modular wall is thinner than a cell, so at an angle it
        /// slips between every centre and would be recorded as standing on nothing at all.
        /// Overlap testing is also the conservative direction — a cell the prop merely clips is
        /// held, which is what keeps two angled pieces out of each other.
        /// </summary>
        public bool Covers(in Shape s, int cx, int cz)
        {
            if (s.axisAligned) return true;

            Vector2 d = CellCenter2D(cx, cz) - s.center;
            float h = CellSize * 0.5f;

            // Dort eksen: hucrenin ikisi (dunya X/Z) ve donuk kutunun ikisi. Herhangi birinde
            // ayrilma varsa kesisme yoktur.
            if (Mathf.Abs(d.x) > h + s.half.x * Mathf.Abs(s.axisX.x) + s.half.y * Mathf.Abs(s.axisZ.x)) return false;
            if (Mathf.Abs(d.y) > h + s.half.x * Mathf.Abs(s.axisX.y) + s.half.y * Mathf.Abs(s.axisZ.y)) return false;
            if (Mathf.Abs(Vector2.Dot(d, s.axisX)) > s.half.x + h * (Mathf.Abs(s.axisX.x) + Mathf.Abs(s.axisX.y))) return false;
            if (Mathf.Abs(Vector2.Dot(d, s.axisZ)) > s.half.y + h * (Mathf.Abs(s.axisZ.x) + Mathf.Abs(s.axisZ.y))) return false;
            return true;
        }

        /// <summary>
        /// How many level steps a prop stands through, from <see cref="PropDef.height"/>.
        ///
        /// Without this a 2.2 m wall would hold only the level it sits on and a shelf could be
        /// placed at head height INSIDE it. Rounded up: a prop that pokes into the next level
        /// holds it, because half a level of a wall is still wall.
        /// </summary>
        public static int LevelSpan(PropDef def, float levelHeight, byte heightPct = 100)
        {
            if (def == null || levelHeight <= 0.01f) return 1;
            float h = def.height * (heightPct <= 0 ? 1f : heightPct * 0.01f);
            return Mathf.Clamp(Mathf.CeilToInt(h / levelHeight - 0.001f), 1, MaxLevels);
        }

        public int LevelSpan(PropDef def, byte heightPct = 100) => LevelSpan(def, LevelHeight, heightPct);

        /// <summary>Bits for the levels a placement stands through; 0 when it reaches past the top.</summary>
        static int LevelMask(int level, int span)
        {
            if (level < 0 || level >= MaxLevels) return 0;
            int mask = 0;
            for (int l = level; l < level + span && l < MaxLevels; l++) mask |= 1 << l;
            return mask;
        }

        /// <summary>
        /// True when every cell the placement stands on is in the grid, buildable, and free AT
        /// THE LEVELS IT WOULD OCCUPY.
        ///
        /// The ground is tested against <see cref="_base"/>, not the live state: a cell already
        /// carrying a prop is still perfectly good ground for a second one at a different
        /// height, and reading the live state would report it as unbuildable — which is exactly
        /// how a table used to block the whole column of air above it.
        /// </summary>
        public bool CanPlace(PropDef def, Vector2Int minCell, byte rot, byte scalePct = 100,
            byte level = 0, byte heightPct = 100)
        {
            int span = LevelSpan(def, heightPct);
            if (level + span > MaxLevels) return false;   // tavanin ustune tasiyor

            int want = LevelMask(level, span);
            var s = ShapeOf(def, minCell, rot, scalePct);

            for (int cz = s.rect.yMin; cz < s.rect.yMax; cz++)
                for (int cx = s.rect.xMin; cx < s.rect.xMax; cx++)
                {
                    if (!Covers(s, cx, cz)) continue;
                    if (!InBounds(cx, cz)) return false;

                    int i = cz * Cols + cx;
                    if (!IsBuildable(_base[i])) return false;      // oda disi / mobilya
                    if ((_levels[i] & want) != 0) return false;    // o katlar dolu
                }
            return true;
        }

        /// <summary>Marks the cells and levels a placement stands on as taken.</summary>
        public void Occupy(PropDef def, Vector2Int minCell, byte rot, byte scalePct = 100,
            byte level = 0, byte heightPct = 100)
        {
            int set = LevelMask(level, LevelSpan(def, heightPct));
            if (set == 0) return;

            var s = ShapeOf(def, minCell, rot, scalePct);
            for (int cz = s.rect.yMin; cz < s.rect.yMax; cz++)
                for (int cx = s.rect.xMin; cx < s.rect.xMax; cx++)
                {
                    if (!Covers(s, cx, cz) || !InBounds(cx, cz)) continue;
                    int i = cz * Cols + cx;
                    _levels[i] |= (byte)set;
                    _cells[i] = CellState.Occupied;
                }
        }

        /// <summary>
        /// Frees the cells and levels a placement stood on. The cell only returns to what it was
        /// BEFORE anything was placed once its LAST level is gone — and then not simply to
        /// "Free": a cell outside the room must go back to <see cref="CellState.FreeOutside"/>,
        /// and one under a couch must stay <see cref="CellState.Blocked"/>, because deleting a
        /// prop cannot move a wall or clear furniture.
        /// </summary>
        public void Release(PropDef def, Vector2Int minCell, byte rot, byte scalePct = 100,
            byte level = 0, byte heightPct = 100)
        {
            int clear = LevelMask(level, LevelSpan(def, heightPct));
            if (clear == 0) return;

            var s = ShapeOf(def, minCell, rot, scalePct);
            for (int cz = s.rect.yMin; cz < s.rect.yMax; cz++)
                for (int cx = s.rect.xMin; cx < s.rect.xMax; cx++)
                    if (Covers(s, cx, cz)) ReleaseCell(cx, cz, clear);
        }

        /// <summary>Levels taken in a cell, as a bit per level. 0 = the whole column is free.</summary>
        public byte LevelsAt(int cx, int cz) => InBounds(cx, cz) ? _levels[cz * Cols + cx] : (byte)0;

        // ------------------------------------------------------------- snapping

        /// <summary>
        /// True when anything already placed is in the one-cell ring around the rectangle — i.e.
        /// a prop put here would be standing flush against a neighbour.
        /// </summary>
        public bool TouchesOccupied(RectInt rect)
        {
            int x0 = rect.xMin - 1, x1 = rect.xMax;
            int z0 = rect.yMin - 1, z1 = rect.yMax;

            for (int cx = x0; cx <= x1; cx++)
                if (State(cx, z0) == CellState.Occupied || State(cx, z1) == CellState.Occupied) return true;
            for (int cz = z0 + 1; cz < z1; cz++)
                if (State(x0, cz) == CellState.Occupied || State(x1, cz) == CellState.Occupied) return true;
            return false;
        }

        /// <summary>
        /// Pulls a placement onto the edge of whatever is standing near it, and returns where it
        /// should actually go.
        ///
        /// THIS IS THE PART AIMING CANNOT DO. A cell is 12.5 cm; at three metres a degree of
        /// hand tremor is five, so landing on one exact cell — the one that leaves no seam and
        /// no overlap — is a coin toss the player has to win on every single piece. Snapping
        /// moves the decision from the wrist to the grid: get within a couple of cells of a wall
        /// and the piece goes flush against it.
        ///
        /// Only ever snaps to a spot that BOTH touches something and is placeable, and never
        /// moves a placement that is already flush and legal. In open ground, where there is
        /// nothing to snap to, the aim is returned untouched — the magnet has no pull of its
        /// own, it only latches onto what is already built.
        ///
        /// ONLY RESCUES A BLOCKED AIM. It used to pull whenever the aim was not already flush,
        /// which meant a perfectly good empty spot got yanked onto the nearest neighbour and the
        /// player could not put a prop on the square they were pointing at — the grid stopped
        /// being the thing that decides. Now pointing always wins where pointing is possible,
        /// and the magnet only steps in when the answer would otherwise be "no".
        ///
        /// The cost is that a gap thinner than <paramref name="radius"/> cells cannot be left on
        /// purpose next to a neighbour; that is the trade the radius dials, and 0 turns it off.
        /// </summary>
        public Vector2Int SnapToNeighbour(PropDef def, Vector2Int raw, byte rot, byte scalePct,
            int radius, byte level = 0, byte heightPct = 100)
        {
            if (radius <= 0 || def == null) return raw;

            // NISAN ALINAN YER KONULABILIYORSA ORASI. Yapisma yalnizca cakismadan kurtarir.
            if (CanPlace(def, raw, rot, scalePct, level, heightPct)) return raw;

            Vector2Int size = FootprintCells(def, rot, CellSize, scalePct);

            int best = int.MaxValue;
            Vector2Int snapped = raw;

            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Once ucuz eleme, sonra pahali testler. Esitlikte ilk bulunan kazanir:
                    // tarama sirasi sabit oldugu icin ayni nisan hep ayni hucreyi verir ve
                    // hayalet esit uzaklikta iki aday arasinda titremez.
                    int score = dx * dx + dz * dz;
                    if (score == 0 || score >= best) continue;

                    var cand = new Vector2Int(raw.x + dx, raw.y + dz);
                    if (!TouchesOccupied(new RectInt(cand.x, cand.y, size.x, size.y))) continue;
                    if (!CanPlace(def, cand, rot, scalePct, level, heightPct)) continue;

                    best = score;
                    snapped = cand;
                }

            return snapped;
        }

        // ---- ham hucre dikdortgeni: izgara mekanigi, prop DEGIL ----
        // Bir yerlesim icin bunlari cagirmak, ara acilarda propun basmadigi hucreleri de
        // kapatmak demek olurdu — prop iceren her yol yukaridaki asiri yuklemelerden gecmeli.

        /// <summary>True when every cell in the rectangle is in the grid and buildable.</summary>
        public bool CanPlaceCells(RectInt rect)
        {
            for (int cz = rect.yMin; cz < rect.yMax; cz++)
                for (int cx = rect.xMin; cx < rect.xMax; cx++)
                    if (!IsBuildable(State(cx, cz))) return false;
            return true;
        }

        /// <summary>Fills the rectangle at GROUND level — the rectangle forms are level 0 only.</summary>
        public void OccupyCells(RectInt rect)
        {
            for (int cz = rect.yMin; cz < rect.yMax; cz++)
                for (int cx = rect.xMin; cx < rect.xMax; cx++)
                {
                    if (!InBounds(cx, cz)) continue;
                    int i = cz * Cols + cx;
                    _levels[i] |= 1;
                    _cells[i] = CellState.Occupied;
                }
        }

        /// <summary>Rectangle form of <see cref="Release"/> — same restore-to-base rule.</summary>
        public void ReleaseCells(RectInt rect)
        {
            for (int cz = rect.yMin; cz < rect.yMax; cz++)
                for (int cx = rect.xMin; cx < rect.xMax; cx++)
                    ReleaseCell(cx, cz, 1);
        }

        void ReleaseCell(int cx, int cz, int clearMask)
        {
            if (!InBounds(cx, cz)) return;
            int i = cz * Cols + cx;
            _levels[i] &= (byte)~clearMask;
            // Hucre ancak SON kati da bosalinca zemine doner; yoksa ustteki rafi silmek
            // altindaki masayi izgaradan silerdi.
            if (_levels[i] == 0 && _cells[i] == CellState.Occupied) _cells[i] = _base[i];
        }

        /// <summary>Serbest katmanin zemin altina inebilecegi en fazla derinlik (m).</summary>
        public const float FreeDepthBelowFloor = 2f;

        /// <summary>Serbest katmanin zemin ustunde cikabilecegi en fazla yukseklik (m).</summary>
        public const float FreeHeightAboveFloor = 20f;

        /// <summary>
        /// Pulls a free-layer point back into the map's own volume.
        ///
        /// The grid clamps itself by construction — a cell index either exists or it does not.
        /// The free layer has no such floor under it: a typed "9999" or a slipped drag sends a
        /// prop somewhere nobody will ever find it, and it stays in the file forever. Clamping
        /// rather than refusing keeps the edit legible — the prop stops at the edge where you
        /// can see it and correct it, instead of silently ignoring what you asked for.
        ///
        /// Vertically it is deliberately generous (2 m under, 20 m over): sinking a crate a
        /// few millimetres and hanging scenery high up are both things this layer is FOR.
        /// </summary>
        public Vector3 ClampToBounds(Vector3 roomPoint, float padMeters = 0.5f)
        {
            float pad = Mathf.Max(0f, padMeters);
            return new Vector3(
                Mathf.Clamp(roomPoint.x, Origin.x - pad, Origin.x + Cols * CellSize + pad),
                Mathf.Clamp(roomPoint.y, FloorY - FreeDepthBelowFloor, FloorY + FreeHeightAboveFloor),
                Mathf.Clamp(roomPoint.z, Origin.y - pad, Origin.y + Rows * CellSize + pad));
        }

        /// <summary>
        /// World transform for a placed prop: horizontally the centre of its footprint rect,
        /// vertically the floor lifted by <paramref name="level"/> steps.
        /// </summary>
        public Vector3 RectCenter(RectInt rect, byte level, float levelHeight) => new Vector3(
            Origin.x + (rect.xMin + rect.width * 0.5f) * CellSize,
            FloorY + level * levelHeight,
            Origin.y + (rect.yMin + rect.height * 0.5f) * CellSize);

        // ------------------------------------------------------------- layout

        /// <summary>
        /// Marks every placement in a layout as occupied. Call once after loading a map so the
        /// grid agrees with what is actually standing in the room. Returns how many placements
        /// were applied; unknown prop ids are skipped and logged.
        /// </summary>
        public int ApplyLayout(MapLayout layout, PropLibrary library)
        {
            if (layout == null || layout.props == null) return 0;

            int applied = 0;
            foreach (var p in layout.props)
            {
                if (p == null) continue;
                var def = library != null ? library.ById(p.propId) : null;
                if (def == null)
                {
                    Debug.LogWarning($"[RoomGrid] Kutuphanede olmayan prop atlandi: '{p.propId}'.");
                    continue;
                }
                Occupy(def, new Vector2Int(p.cellX, p.cellZ), p.rot, p.scalePct, p.level, p.heightPct);
                applied++;
            }
            return applied;
        }

        // ------------------------------------------------------------- reporting

        public struct Stats
        {
            public int total, free, freeOutside, occupied, blocked, outside;
            /// <summary>Oda ICI, yurunebilir alan (m2).</summary>
            public float roomArea;
            /// <summary>Prop konabilen toplam alan (m2) — oda disi dahil.</summary>
            public float buildableArea;
        }

        public Stats Report()
        {
            var s = new Stats { total = _cells.Length };
            foreach (var c in _cells)
            {
                switch (c)
                {
                    case CellState.Free:        s.free++;        break;
                    case CellState.FreeOutside: s.freeOutside++; break;
                    case CellState.Occupied:    s.occupied++;    break;
                    case CellState.Blocked:     s.blocked++;     break;
                    default:                    s.outside++;     break;
                }
            }
            float cell = CellSize * CellSize;
            s.roomArea = s.free * cell;
            s.buildableArea = (s.free + s.freeOutside + s.occupied) * cell;
            return s;
        }

        // ------------------------------------------------------------- geometry

        /// <summary>
        /// Even-odd ray crossing test. Shared by the runtime grid and the editor room builder —
        /// note <c>RoomScanSetup</c> still carries its own private copy from before this class
        /// existed; they must stay in agreement if either is ever changed.
        /// </summary>
        public static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Shortest distance from a point to the polygon outline (edges, not corners only).</summary>
        public static float DistanceToPolygonEdge(Vector2 p, Vector2[] poly)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                float d = DistanceToSegment(p, poly[j], poly[i]);
                if (d < best) best = d;
            }
            return best;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-8f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
