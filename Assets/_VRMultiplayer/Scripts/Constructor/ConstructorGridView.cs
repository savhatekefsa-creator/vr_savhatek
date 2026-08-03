using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRMultiplayer.UI;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Paints the buildable cells on the floor while build mode is on: pale cyan = free,
    /// red = real furniture, amber = already taken.
    ///
    /// ONE MESH PER STATE, not one object per cell. A 19x23 room is 437 cells; as LineRenderers
    /// or quads-with-their-own-GameObject that is 437+ draw calls and a dead Quest. Three
    /// meshes cost three.
    ///
    /// Cells are drawn slightly INSET, so the thin gaps between them read as grid lines without
    /// drawing any lines — half the geometry for the same result, and the only thing that makes
    /// the snap resolution visible at all. Set <see cref="insetRatio"/> to 0 to close the gaps
    /// and get one continuous sheet instead.
    /// </summary>
    public class ConstructorGridView : MonoBehaviour
    {
        [Tooltip("Zeminden yukseklik (m) — z-fighting'i onler.")]
        public float lift = 0.012f;

        /// <summary>
        /// How far around the cursor the lattice is drawn, in metres. 0 = the whole grid.
        ///
        /// The grid used to be painted in full, and in this room 87% of it was the five metres
        /// of build margin outside the walls — geometry nobody was looking at, rebuilt from
        /// scratch on every single placement. That was affordable at a 0.25 m cell and stops
        /// being affordable the moment the cell gets finer, because the cost goes up with the
        /// SQUARE: halving the cell quadruples the mesh. Drawing a few metres around the cursor
        /// costs the same at any resolution, which is what makes a fine grid possible at all.
        /// </summary>
        [Tooltip("Imlec cevresinde cizilecek yaricap (m). 0 = izgaranin tamami.")]
        [Range(0f, 12f)] public float viewRadius = 3f;

        [Tooltip("Hucre kenarindan iceri pay (hucre boyunun orani). Aradaki bosluk = " +
                 "2 x hucre x bu oran; 0.125 m hucrede 0.04 -> 10 mm. 0 = hucreler tamamen " +
                 "birlesir (duz levha), buyudukce bosluklar izgara cizgisi gibi okunur.")]
        [Range(0f, 0.3f)] public float insetRatio = 0.04f;

        [Tooltip("Oda ICI, yurunebilir hucreler.")]
        public Color freeColor = new Color(0.35f, 0.85f, 1f, 0.16f);

        // Oda ici ile AYNI mavi, yalnizca daha soluk: zemin tek bir yuzey gibi okunmali ama
        // oyuncu gercek odanin nerede bittigini — yani nereye yuruyebilecegini — gorebilmeli.
        [Tooltip("Oda DISI ama insa edilebilir hucreler. Oda ici ile ayni mavi, daha soluk.")]
        public Color freeOutsideColor = new Color(0.35f, 0.85f, 1f, 0.07f);

        public Color blockedColor = new Color(1f, 0.25f, 0.2f, 0.34f);
        public Color occupiedColor = new Color(1f, 0.7f, 0.15f, 0.26f);

        [Header("Imlec ayak izi")]
        [Tooltip("Hayaletin basacagi BOS hucreler.")]
        public Color cursorFitColor = new Color(0.3f, 1f, 0.45f, 0.5f);

        [Tooltip("Hayaletin basacagi ama DOLU/engelli hucreler — 'neden kirmizi'nin cevabi.")]
        public Color cursorBlockColor = new Color(1f, 0.25f, 0.2f, 0.65f);

        /// <summary>
        /// The single cell the ray is on, drawn over the footprint.
        ///
        /// The footprint alone says what will be covered but not what the aim is anchored to,
        /// and those are not the same square: a prop wider than one cell spreads around the
        /// point, so "it does not go where I point" is impossible to diagnose — or to correct by
        /// eye — without seeing the point itself.
        /// </summary>
        [Tooltip("Isinin dustugu TEK hucre — ayak izinin uzerine cizilir.")]
        public Color cursorAimColor = new Color(1f, 1f, 1f, 0.85f);

        /// <summary>
        /// Parent of every cell layer, and the grid's ODA UZAYI: mesh vertices are written in
        /// room coordinates, so shrinking this one transform (dollhouse mode) scales the whole
        /// grid with the map and nothing has to be rebuilt.
        /// </summary>
        public Transform Container
        {
            get
            {
                if (_container == null)
                {
                    _container = new GameObject("GridCells").transform;
                    _container.SetParent(transform, false);
                }
                return _container;
            }
        }

        Transform _container;

        readonly Dictionary<CellState, MeshFilter> _layers = new Dictionary<CellState, MeshFilter>();
        ConstructorSession _session;
        bool _dirty;
        bool _visible;

        void Awake() => SetVisible(false);

        void OnEnable()
        {
            _session = ConstructorSession.Instance;
            if (_session != null) _session.Changed += MarkDirty;
        }

        void OnDisable()
        {
            if (_session != null) _session.Changed -= MarkDirty;
        }

        void MarkDirty() => _dirty = true;

        // Mesh yalnizca oturum "degisti" dediginde yenilenir; bu olmadan inspector'da insetRatio
        // surukleyince bir sonraki yerlestirmeye kadar hicbir sey olmazdi.
        void OnValidate() => _dirty = true;

        void LateUpdate()
        {
            // Kare basina EN FAZLA bir kez yeniden kur: tek karede birden fazla yerlestirme
            // olursa (geri al, harita yukleme) mesh'i her seferinde bastan ormeyelim.
            if (!_dirty || !_visible) return;
            _dirty = false;
            Rebuild();
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            foreach (var mf in _layers.Values)
                if (mf != null) mf.gameObject.SetActive(visible);
            if (_cursorFit != null) _cursorFit.gameObject.SetActive(visible);
            if (_cursorBlock != null) _cursorBlock.gameObject.SetActive(visible);
            if (_cursorAim != null) _cursorAim.gameObject.SetActive(visible);

            if (visible) _dirty = true;
            else HideCursor();
        }

        // ------------------------------------------------------------- imlec ayak izi

        MeshFilter _cursorFit, _cursorBlock, _cursorAim;
        readonly List<Vector3> _cursorFitVerts = new List<Vector3>();
        readonly List<Vector3> _cursorBlockVerts = new List<Vector3>();
        readonly List<Vector3> _cursorAimVerts = new List<Vector3>();

        /// <summary>
        /// Paints the cells the build cursor is standing on — green where the prop fits, red on
        /// the ones refusing it.
        ///
        /// The ghost only ever says yes or no. On a long piece that leaves the player sweeping
        /// the ray around hunting for whichever cell is in the way, and on an angled one it is
        /// worse than unhelpful: the occupied cells are a diagonal band, not the box you can
        /// see, so the shape being tested is invisible. This draws the answer where the problem
        /// is. It also makes the snap legible — you watch the footprint jump onto the neighbour
        /// instead of wondering why the ghost moved.
        ///
        /// Rebuilt every frame while the cursor sweeps, so it deliberately does NOT go through
        /// the dirty flag the room layers use. It costs one small mesh, not a pass over the
        /// lattice.
        /// </summary>
        public void ShowCursor(in RoomGrid.Shape shape) => ShowCursor(shape, null);

        public void ShowCursor(in RoomGrid.Shape shape, Vector2Int? aimCell)
        {
            var grid = ConstructorSession.Instance != null ? ConstructorSession.Instance.Grid : null;
            if (grid == null || !_visible) { HideCursor(); return; }

            _cursorFitVerts.Clear();
            _cursorBlockVerts.Clear();
            _cursorAimVerts.Clear();

            float half = grid.CellSize * (0.5f - insetRatio);
            // Oda hucrelerinin USTUNDE: ayni yukseklikte ikisi z-fighting yapardi.
            float y = grid.FloorY + lift * 2f;

            for (int cz = shape.rect.yMin; cz < shape.rect.yMax; cz++)
            {
                for (int cx = shape.rect.xMin; cx < shape.rect.xMax; cx++)
                {
                    // Sinir kutusunun tamami DEGIL, propun gercekten bastigi hucreler.
                    if (!grid.Covers(shape, cx, cz)) continue;

                    // Izgara disi hucre de kirmiziya dusuyor: "kenardan tastin" da bir cevap.
                    var target = RoomGrid.IsBuildable(grid.State(cx, cz))
                        ? _cursorFitVerts : _cursorBlockVerts;

                    Vector3 c = grid.CellCenter(cx, cz);
                    target.Add(new Vector3(c.x - half, y, c.z - half));
                    target.Add(new Vector3(c.x - half, y, c.z + half));
                    target.Add(new Vector3(c.x + half, y, c.z + half));
                    target.Add(new Vector3(c.x + half, y, c.z - half));
                }
            }

            // Nisan hucresi ayak izinin USTUNDE, daha yuksekte: altindaki yesil/kirmizi
            // karenin icinden okunsun diye.
            if (aimCell.HasValue)
            {
                Vector3 a = grid.CellCenter(aimCell.Value.x, aimCell.Value.y);
                a.y = grid.FloorY + lift * 3f;
                float ah = grid.CellSize * 0.30f;
                _cursorAimVerts.Add(new Vector3(a.x - ah, a.y, a.z - ah));
                _cursorAimVerts.Add(new Vector3(a.x - ah, a.y, a.z + ah));
                _cursorAimVerts.Add(new Vector3(a.x + ah, a.y, a.z + ah));
                _cursorAimVerts.Add(new Vector3(a.x + ah, a.y, a.z - ah));
            }

            if (_cursorFit == null) _cursorFit = CreateLayer("Cursor_Fit", cursorFitColor);
            if (_cursorBlock == null) _cursorBlock = CreateLayer("Cursor_Block", cursorBlockColor);
            if (_cursorAim == null) _cursorAim = CreateLayer("Cursor_Aim", cursorAimColor);
            WriteQuads(_cursorFit.sharedMesh, _cursorFitVerts);
            WriteQuads(_cursorBlock.sharedMesh, _cursorBlockVerts);
            WriteQuads(_cursorAim.sharedMesh, _cursorAimVerts);
        }

        /// <summary>Clears the cursor footprint — no ray on the floor, or build mode is off.</summary>
        public void HideCursor()
        {
            if (_cursorFit != null) _cursorFit.sharedMesh.Clear();
            if (_cursorBlock != null) _cursorBlock.sharedMesh.Clear();
            if (_cursorAim != null) _cursorAim.sharedMesh.Clear();
        }

        // ------------------------------------------------------------- mesh

        /// <summary>States that get a mesh. Anything else (Outside) is simply not drawn.</summary>
        static readonly CellState[] DrawnStates =
            { CellState.Free, CellState.FreeOutside, CellState.Blocked, CellState.Occupied };

        Color ColorOf(CellState s) =>
            s == CellState.Free ? freeColor :
            s == CellState.FreeOutside ? freeOutsideColor :
            s == CellState.Blocked ? blockedColor : occupiedColor;

        // Yeniden kurulumlar arasinda YASAYAN tamponlar, CellState ile indekslenir. Her
        // yerlestirmede on binlerce Vector3'luk liste ayirmak Quest'te bosuna GC tetikler;
        // kapasiteleri ilk kurulumdan sonra oturur. Cizilmeyen durumlarin girdisi null kalir,
        // bu da dongudeki tek null kontrolunu durum ayikligi yerine gecirir.
        readonly List<Vector3>[] _verts = CreateBuffers();
        readonly List<int> _tris = new List<int>();

        // Alan baslatiticisi, Awake DEGIL: Rebuild'in tamponlari her zaman hazir bulmasi
        // gerekiyor ve Awake, edit mode'da eklenen bir bilesende hic calismaz.
        static List<Vector3>[] CreateBuffers()
        {
            var buffers = new List<Vector3>[(int)CellState.FreeOutside + 1];
            foreach (var s in DrawnStates) buffers[(int)s] = new List<Vector3>();
            return buffers;
        }

        /// <summary>
        /// Rebuilds every state mesh in a SINGLE pass over the grid.
        ///
        /// One pass per state would walk the whole lattice four times. That was affordable at a
        /// 0.25 m cell; at 0.125 m the cell count quadruples, so the old shape would have cost
        /// sixteen times as much work per placement — the grid got denser, not the traversal.
        /// </summary>
        // Son orulen pencerenin merkezi. Imlec bundan yeterince uzaklasinca yeniden orulur —
        // her karede degil: kafesi kare basi yeniden kurmak, tam da kacindigimiz maliyet.
        Vector2Int _viewCenter;
        bool _hasViewCenter;

        /// <summary>
        /// Tells the view where the player is pointing. Rebuilds only when the cursor has left
        /// the middle of the window, so sweeping the room costs a rebuild every few metres
        /// rather than every frame.
        /// </summary>
        public void SetCursorCell(Vector2Int cell)
        {
            if (viewRadius <= 0f) return;
            var grid = ConstructorSession.Instance != null ? ConstructorSession.Instance.Grid : null;
            if (grid == null) return;

            int slack = Mathf.Max(2, Mathf.CeilToInt(viewRadius / grid.CellSize) / 3);
            if (_hasViewCenter &&
                Mathf.Abs(cell.x - _viewCenter.x) < slack && Mathf.Abs(cell.y - _viewCenter.y) < slack)
                return;

            _viewCenter = cell;
            _hasViewCenter = true;
            _dirty = true;
        }

        void Rebuild()
        {
            var grid = ConstructorSession.Instance != null ? ConstructorSession.Instance.Grid : null;
            if (grid == null) return;

            foreach (var s in DrawnStates) _verts[(int)s].Clear();

            float half = grid.CellSize * (0.5f - insetRatio);
            float y = grid.FloorY + lift;

            // Cizim penceresi: imlecin cevresi, izgaranin sinirlarina kirpilmis.
            int x0 = 0, x1 = grid.Cols, z0 = 0, z1 = grid.Rows;
            if (viewRadius > 0f && _hasViewCenter)
            {
                int r = Mathf.CeilToInt(viewRadius / grid.CellSize);
                x0 = Mathf.Max(0, _viewCenter.x - r);
                x1 = Mathf.Min(grid.Cols, _viewCenter.x + r + 1);
                z0 = Mathf.Max(0, _viewCenter.y - r);
                z1 = Mathf.Min(grid.Rows, _viewCenter.y + r + 1);
            }

            for (int cz = z0; cz < z1; cz++)
            {
                for (int cx = x0; cx < x1; cx++)
                {
                    var target = _verts[(int)grid.State(cx, cz)];
                    if (target == null) continue;   // cizilmeyen durum

                    Vector3 c = grid.CellCenter(cx, cz);
                    target.Add(new Vector3(c.x - half, y, c.z - half));
                    target.Add(new Vector3(c.x - half, y, c.z + half));
                    target.Add(new Vector3(c.x + half, y, c.z + half));
                    target.Add(new Vector3(c.x + half, y, c.z - half));
                }
            }

            foreach (var s in DrawnStates) UploadLayer(s, _verts[(int)s]);
        }

        void UploadLayer(CellState state, List<Vector3> verts) =>
            WriteQuads(EnsureLayer(state).sharedMesh, verts);

        /// <summary>
        /// Writes a quad soup into a mesh. Indices are regenerated from the vertex count rather
        /// than tracked while filling: every quad winds identically, so there is nothing
        /// per-cell to remember.
        /// </summary>
        void WriteQuads(Mesh mesh, List<Vector3> verts)
        {
            mesh.Clear();
            if (verts.Count == 0) return;

            _tris.Clear();
            for (int b = 0; b < verts.Count; b += 4)
            {
                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
            }

            // 16-bit indeks 65535 kose ile sinirli; buyuk odalarda 32-bit'e gec.
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();
        }

        MeshFilter EnsureLayer(CellState state)
        {
            if (_layers.TryGetValue(state, out var mf) && mf != null) return mf;
            mf = CreateLayer("Cells_" + state, ColorOf(state));
            _layers[state] = mf;
            return mf;
        }

        MeshFilter CreateLayer(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Container, false);
            go.SetActive(_visible);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = name };

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = UITheme.CreateTransparentMaterial(color);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mf;
        }
    }
}
