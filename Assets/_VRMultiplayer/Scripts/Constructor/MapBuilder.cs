using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Turns a <see cref="MapLayout"/> into actual scene objects. The runtime twin of the
    /// editor-time room builder in <c>RoomScanSetup</c> (menus 13/14) — same geometry, minus
    /// the AssetDatabase/PrefabUtility/Undo calls that only exist in the editor.
    ///
    /// DETERMINISM IS A HARD REQUIREMENT. Every peer builds its own copy from the same layout,
    /// and hit detection is server-authoritative — so a wall the PC thinks is at X must be at X
    /// on every headset too. That means: no Random without a seed, nothing derived from Time,
    /// nothing that depends on scene load order. Keep it that way.
    /// </summary>
    public static class MapBuilder
    {
        public const string DefaultRootName = "ConstructorMap";

        /// <summary>Props instantiated per frame by <see cref="BuildAsync"/>.</summary>
        public const int DefaultPropsPerFrame = 20;

        // ------------------------------------------------------------- root

        /// <summary>Finds (or creates) the container every built prop is parented to.</summary>
        public static Transform EnsureRoot(string rootName = DefaultRootName)
        {
            var go = GameObject.Find(rootName);
            if (go == null) go = new GameObject(rootName);
            return go.transform;
        }

        /// <summary>Removes everything previously built under <paramref name="root"/>.</summary>
        public static void Clear(Transform root)
        {
            if (root == null) return;
            for (int i = root.childCount - 1; i >= 0; i--)
                DestroySafe(root.GetChild(i).gameObject);
        }

        // ------------------------------------------------------------- build

        /// <summary>
        /// Builds the whole map in one go. Fine for the editor and for small maps; use
        /// <see cref="BuildAsync"/> in VR, where instantiating a few hundred props in a single
        /// frame is a visible hitch (and a hitch in VR is nausea, not just a dropped frame).
        ///
        /// Returns instanceId -> instance, which the placement layer needs to move/delete a
        /// prop the player points at.
        /// </summary>
        public static Dictionary<uint, GameObject> Build(MapLayout layout,
            PropLibrary library = null, Transform root = null, RoomGrid grid = null)
        {
            var built = new Dictionary<uint, GameObject>();
            var ctx = Prepare(layout, library, root, grid);
            if (ctx == null) return built;

            Clear(ctx.root);
            foreach (var p in ctx.layout.props)
            {
                var go = SpawnOne(p, ctx);
                if (go != null) built[p.instanceId] = go;
            }
            foreach (var f in ctx.layout.freeProps)
            {
                var go = SpawnOneFree(f, ctx);
                if (go != null) built[f.instanceId] = go;
            }

            Debug.Log($"[MapBuilder] '{ctx.layout.name}' kuruldu: " +
                      $"{built.Count}/{ctx.layout.Count + ctx.layout.FreeCount} prop.");
            return built;
        }

        /// <summary>
        /// Same as <see cref="Build"/> but spread across frames. Drive it with StartCoroutine;
        /// the finished dictionary arrives through <paramref name="onDone"/>.
        /// </summary>
        public static IEnumerator BuildAsync(MapLayout layout,
            PropLibrary library = null, Transform root = null, RoomGrid grid = null,
            int propsPerFrame = DefaultPropsPerFrame,
            Action<Dictionary<uint, GameObject>> onDone = null)
        {
            var built = new Dictionary<uint, GameObject>();
            var ctx = Prepare(layout, library, root, grid);
            if (ctx == null)
            {
                onDone?.Invoke(built);
                yield break;
            }

            Clear(ctx.root);
            if (propsPerFrame < 1) propsPerFrame = 1;

            int sinceYield = 0;
            foreach (var p in ctx.layout.props)
            {
                var go = SpawnOne(p, ctx);
                if (go != null) built[p.instanceId] = go;

                if (++sinceYield >= propsPerFrame)
                {
                    sinceYield = 0;
                    yield return null;
                }
            }
            foreach (var f in ctx.layout.freeProps)
            {
                var go = SpawnOneFree(f, ctx);
                if (go != null) built[f.instanceId] = go;

                if (++sinceYield >= propsPerFrame)
                {
                    sinceYield = 0;
                    yield return null;
                }
            }

            Debug.Log($"[MapBuilder] '{ctx.layout.name}' kuruldu (kareye yayilmis): " +
                      $"{built.Count}/{ctx.layout.Count + ctx.layout.FreeCount} prop.");
            onDone?.Invoke(built);
        }

        // ------------------------------------------------------------- internals

        /// <summary>Everything the per-prop step needs, resolved once.</summary>
        class Ctx
        {
            public MapLayout layout;
            public PropLibrary library;
            public Transform root;
            public RoomGrid grid;
        }

        /// <summary>
        /// Resolves the optional arguments; null when the layout is unusable.
        ///
        /// Returns a context object rather than filling <c>ref</c> parameters because
        /// <see cref="BuildAsync"/> is an iterator, and iterator plumbing plus by-ref argument
        /// passing is a combination best not relied on.
        /// </summary>
        static Ctx Prepare(MapLayout layout, PropLibrary library, Transform root, RoomGrid grid)
        {
            if (layout == null)
            {
                Debug.LogWarning("[MapBuilder] Harita null — kurulacak bir sey yok.");
                return null;
            }
            if (layout.props == null) layout.props = new PlacedProp[0];
            if (layout.freeProps == null) layout.freeProps = new FreePlacedProp[0];
            if (library == null) library = PropLibrary.Instance;
            if (root == null) root = EnsureRoot();

            if (grid == null)
            {
                grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                    RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
                if (grid == null)
                {
                    Debug.LogWarning($"[MapBuilder] '{layout.name}' icin izgara kurulamadi " +
                                     "(haritanin icindeki oda plani gecersiz).");
                    return null;
                }
            }

            if (library != null && library.contentVersion != layout.libraryVersion)
                Debug.LogWarning($"[MapBuilder] Kutuphane surumu uyusmuyor " +
                                 $"(harita: {layout.libraryVersion}, kutuphane: {library.contentVersion}). " +
                                 "Eksik/kaymis proplar olabilir.");

            return new Ctx { layout = layout, library = library, root = root, grid = grid };
        }

        static GameObject SpawnOne(PlacedProp p, Ctx ctx)
        {
            if (p == null) return null;

            var def = ctx.library != null ? ctx.library.ById(p.propId) : null;
            if (def == null)
            {
                Debug.LogWarning($"[MapBuilder] Kutuphanede yok, atlandi: '{p.propId}'.");
                return null;
            }
            return Spawn(p, def, ctx.grid, ctx.layout.levelHeight, ctx.root);
        }

        /// <summary>
        /// Instantiates ONE placement. The single place a prop's transform is decided — bulk
        /// building and interactive placement both come through here, so a prop dropped by hand
        /// lands exactly where a rebuild from the saved layout will put it. Two code paths doing
        /// this arithmetic separately is how maps start drifting between peers.
        /// </summary>
        public static GameObject Spawn(PlacedProp p, PropDef def, RoomGrid grid,
            float levelHeight, Transform parent)
        {
            if (p == null || def == null || grid == null) return null;

            var prefab = def.Resolve();
            if (prefab == null) return null;   // Resolve zaten uyardi

            var rect = grid.FootprintRect(def, p.cellX, p.cellZ, p.rot, p.scalePct);
            Vector3 pos = grid.RectCenter(rect, p.level, levelHeight);

            // LOCAL konum, dunya konumu DEGIL: kok objesi ODA UZAYIDIR. Bugun birim donusumde
            // duruyor (local == dunya), ama kok bir gun tasinir ya da olceklenirse dunya konumu
            // yazan her prop haritanin disinda kalirdi.
            var scale = LocalScaleFor(def, prefab, grid.CellSize, p.scalePct, p.heightPct);

            var go = UnityEngine.Object.Instantiate(prefab, parent);
            go.transform.localPosition = pos - PivotOffset(def, scale, p.Yaw);
            go.transform.localRotation = Quaternion.Euler(0f, p.Yaw, 0f);
            go.transform.localScale = scale;
            // Kimlik isimde: sahnede gozle bulmak ve hata ayiklamak icin. Adres olarak
            // kullanma — sozlukteki instanceId tek dogru kaynak.
            go.name = $"{def.id}#{p.instanceId}";
            return go;
        }

        static GameObject SpawnOneFree(FreePlacedProp f, Ctx ctx)
        {
            if (f == null) return null;

            var def = ctx.library != null ? ctx.library.ById(f.propId) : null;
            if (def == null)
            {
                Debug.LogWarning($"[MapBuilder] Kutuphanede yok, atlandi (serbest): '{f.propId}'.");
                return null;
            }
            return SpawnFree(f, def, ctx.root);
        }

        /// <summary>
        /// Instantiates ONE free placement. NO grid, NO pivot correction, NO fit — the stored
        /// transform IS the truth and is applied verbatim. The fine-edit flow copies a built
        /// object's local transform into the data and back again; any arithmetic added here
        /// would break that round trip (the exact drift <see cref="Spawn"/>'s doc warns about,
        /// avoided by having none).
        ///
        /// Local space on purpose, like <see cref="Spawn"/>: the root is room space, so free
        /// props follow it wherever it goes.
        /// </summary>
        public static GameObject SpawnFree(FreePlacedProp f, PropDef def, Transform parent)
        {
            if (f == null || def == null) return null;

            var prefab = def.Resolve();
            if (prefab == null) return null;   // Resolve zaten uyardi

            var go = UnityEngine.Object.Instantiate(prefab, parent);
            go.transform.localPosition = f.position;
            go.transform.localRotation = f.Rotation;
            go.transform.localScale = f.scale;
            go.name = $"{def.id}#{f.instanceId}~serbest";
            return go;
        }

        /// <summary>
        /// How far the prefab's ORIGIN has to move so the MODEL ends up centred on its cells.
        ///
        /// <see cref="RoomGrid.RectCenter"/> answers "where is the middle of the reserved
        /// rectangle", and the only thing a Transform can be given is where the origin goes.
        /// Those agree only when the model is centred on its own origin, which bought art
        /// almost never is: exporters leave the pivot where the artist left it, and in this
        /// library 38 of 46 prefabs are off — one modular wall by 2.6 cm, a floor panel by
        /// 15 cm. Uncorrected, that offset is a straight lie about where the prop stands, and
        /// it is worst exactly where it is most visible: turn a piece 90 degrees and its offset
        /// swings to a different world axis, so a joint that lined up along one wall opens up
        /// around the corner. It is a second, independent cause of the same symptom the
        /// thickness fit addressed, and no amount of grid arithmetic can see it.
        ///
        /// HEIGHT IS LEFT ALONE. Vertical placement is <see cref="RoomGrid.RectCenter"/>'s floor
        /// plus the level step, and props are authored standing on that floor; the ground and
        /// hill pieces in this library carry a ten-metre skirt below their origin, so centring Y
        /// on the mesh would fling them into the sky. Only the two axes the grid reasons about
        /// are corrected.
        /// </summary>
        public static Vector3 PivotOffset(PropDef def, Vector3 localScale, float yaw)
        {
            if (def == null) return Vector3.zero;

            Vector3 c = def.MeshLocalCenter;
            var flat = new Vector3(c.x * localScale.x, 0f, c.z * localScale.z);
            return Quaternion.Euler(0f, yaw, 0f) * flat;
        }

        /// <summary>
        /// How big a placement is. THE single answer, asked by both the built map and the build
        /// ghost — what the player previews has to be what lands, or the ghost is a lie.
        ///
        /// With <see cref="PropDef.fitToFootprint"/> the prop is scaled to fill its reserved
        /// cells EXACTLY, and that is what lets two props sit flush. The grid can only reserve
        /// whole cells, so a prop measured at 0.54 m holds 0.50 m of lattice; left at its own
        /// size it leaves a 4 cm seam against its neighbour — every time, however carefully the
        /// player aims, because the gap is in the arithmetic and not in the aiming.
        ///
        /// On a fitted axis the prefab's own scale is REPLACED rather than multiplied in, which
        /// is the opposite of the unfitted path: the fit is an absolute target in metres, and
        /// the authored scale is already accounted for because <see cref="PropDef.MeshLocalSize"/>
        /// measures without it. When <see cref="PropDef.sizeMeters"/> is honest the two agree —
        /// a correctly authored prefab comes back out at very nearly its own scale, which is the
        /// whole idea: <see cref="FitAxis"/> only ever nudges, and hands the axis back untouched
        /// when the correction would be large enough to count as resizing the model.
        ///
        /// HEIGHT IS NEVER FITTED. Vertical size has no cell to answer to, so Y stays on the
        /// prefab's scale times the height percentage; only the two footprint axes are pinned
        /// to the grid.
        /// </summary>
        public static Vector3 LocalScaleFor(PropDef def, GameObject prefab, float cellSize,
            byte scalePct, byte heightPct)
        {
            Vector3 baseScale = prefab != null ? prefab.transform.localScale : Vector3.one;
            Vector3 k = PlacedProp.ScaleVectorOf(scalePct, heightPct);

            // Olcusuz kalan yol: prefabin KENDI olcegini carpiyoruz, ezmiyoruz — bazi modeller
            // birim olcekte degil.
            var unfitted = new Vector3(baseScale.x * k.x, baseScale.y * k.y, baseScale.z * k.z);
            if (def == null || !def.fitToFootprint) return unfitted;

            Vector3 mesh = def.MeshLocalSize;
            if (mesh.x < 0.0001f || mesh.z < 0.0001f) return unfitted;   // olculecek mesh yok

            // Hedef DONDURULMEMIS ayak izi (rot = 0). Serbest donuste doluluk kare sinir
            // kutusuna buyuyor (RoomGrid.FootprintCells); ona gore olceklemek propu 90 derece
            // olmayan her acida sisirirdi. Prop donuyor, ayak izi onunla birlikte donuyor —
            // yerel eksenlerdeki olcu ise acidan bagimsiz.
            Vector2Int cells = RoomGrid.FootprintCells(def, 0, cellSize, scalePct);
            return new Vector3(
                FitAxis(cells.x * cellSize, mesh.x, unfitted.x, cellSize),
                unfitted.y,
                FitAxis(cells.y * cellSize, mesh.z, unfitted.z, cellSize));
        }

        /// <summary>
        /// One axis of the fit. Two ways in: a small correction, or an axis thinner than the
        /// grid can express.
        ///
        /// THE SMALL CORRECTION absorbs the grid's rounding, at most half a cell. It is not a
        /// licence to resize models: a spawn ring whose disc is deliberately wider than the
        /// cells it holds (<c>ConstructorSetup.SpawnFootprintMetres</c>) would collapse to less
        /// than half its size, and a tree that reserves only its trunk would shrink to the
        /// trunk. Those are cases where <see cref="PropDef.sizeMeters"/> means something other
        /// than "how big the mesh is", so past <see cref="PropLibrary.FitMismatchTolerance"/>
        /// the axis is handed back untouched.
        ///
        /// THE SUB-CELL AXIS is the one that made perpendicular joints fail. A footprint cannot
        /// be thinner than one cell, so a 3.4 cm plank reserves the full 12.5 cm and used to
        /// stand as a thin strip in the middle of it, with 4.5 cm of reserved-but-empty floor on
        /// either side. Along its length that never shows — the length axis is fitted. Turn a
        /// second copy 90 degrees against it and the joint lands exactly on that padding: the
        /// grid rectangles are flush and the models are 4.5 cm apart. Growing the axis to the
        /// one cell the grid already gave it is the same rule as everywhere else in this method
        /// — what you see is what is reserved — and it is the only form of the fix that holds
        /// for every approach direction, because it removes the padding instead of choosing a
        /// side to hide it on.
        ///
        /// Deliberately one-way: only GROWTH, and only up to a single cell. A tree whose canopy
        /// dwarfs its one-cell trunk is a shrink, so it still falls to the tolerance rule and is
        /// left alone. A prop this visibly thickens is one <see cref="PropDef.fitToFootprint"/>
        /// tick away from opting out, and <see cref="PropLibrary.Validate"/> names it.
        /// </summary>
        public static float FitAxis(float targetMeters, float meshLocal, float unfittedScale, float cellSize)
        {
            // Mevcut dunya boyu. Isaret ayri tutuluyor: aynalanmis prefablarda olcek negatif
            // olabilir ve orani ters cevirirdi.
            float current = meshLocal * Mathf.Abs(unfittedScale);
            if (current < 0.0001f) return unfittedScale;

            float correction = targetMeters / current;

            // Hucrenin altinda kalan eksen, buyume yonunde.
            bool subCell = correction > 1f && targetMeters <= cellSize * 1.0001f;

            // YARIM HUCREYI GECMEYEN duzeltme — mutlak olcuyle.
            //
            // Yalnizca oransal bakmak kucuk olculerde yaniltiyor: 6.25 cm'lik bir hucrede 8.4
            // cm'lik bir bariyer %25.6 kuculmek zorunda ve toleransin kil payi disinda kaliyor,
            // oysa mutlak fark 2.2 cm — yuvarlamanin kendi butcesinin ucte biri. Fit zaten
            // yuvarlamayi emmek icin var ve yuvarlama yarim hucreyle sinirli, yani dogru olcut
            // bu. Agacin govdesi ya da dogus halkasi gibi gercek uyusmazliklar metrelerce
            // uzakta oldugu icin bu kapidan gecemez.
            bool withinRounding = Mathf.Abs(targetMeters - current) <= cellSize * 0.5f;

            bool withinTolerance = Mathf.Abs(correction - 1f) <= PropLibrary.FitMismatchTolerance;

            if (!subCell && !withinRounding && !withinTolerance) return unfittedScale;

            return Mathf.Sign(unfittedScale) * targetMeters / meshLocal;
        }

        /// <summary>
        /// Destroy that also works in edit mode. The editor preview tool (menu 28) rebuilds maps
        /// outside play mode, where <c>Object.Destroy</c> is deferred forever and the old props
        /// would pile up on every rebuild.
        /// </summary>
        static void DestroySafe(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
