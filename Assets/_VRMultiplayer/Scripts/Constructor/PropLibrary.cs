using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>Palette grouping — one ring per category in the in-VR selector wheel.</summary>
    public enum PropCategory { Cover, Wall, Nature, Ground, Spawn, Target }

    /// <summary>
    /// A named SET of props — "UZAY", "ORMAN" — the second axis of the build palette, crossing
    /// <see cref="PropCategory"/> rather than replacing it.
    ///
    /// Category answers "what does this piece DO" (is it cover, is it a wall); a palette answers
    /// "which world is it FROM". Both questions are real: folding them together would give the
    /// wheel one SPACE slice holding walls, crates and consoles at once, and a player looking for
    /// a wall would have to walk past the consoles to find it.
    ///
    /// DATA, NOT AN ENUM. Adding a world is something the person building the game does in the
    /// library window (menu 31) — naming it, filling it, renaming it later — and an enum would
    /// have made every one of those a code change plus a recompile.
    /// </summary>
    [Serializable]
    public class PropPalette
    {
        [Tooltip("SABIT kimlik — proplar ve kayitli haritalar bunu tutar. Bir kez verildikten " +
                 "sonra degistirme; gorunen adi degistirmek serbest.")]
        public string id;

        [Tooltip("Insa modunda carkin ortasinda yazan ad. Serbestce degistirilebilir.")]
        public string displayName;
    }

    /// <summary>How a prop attaches to the scanned room.</summary>
    public enum PropSnap { Floor, Wall, Free }

    /// <summary>
    /// One placeable item. Authored into <see cref="PropLibrary"/> by the editor scan tool
    /// (Tools > VR Multiplayer > 25) and referenced from saved maps by <see cref="id"/>.
    /// </summary>
    [Serializable]
    public class PropDef
    {
        [Tooltip("SABIT kimlik — kayitli haritalar bunu tutar. Bir kez verildikten sonra ASLA " +
                 "degistirme, yoksa o propu iceren tum haritalar bozulur.")]
        public string id;

        [Tooltip("Palette gorunen ad (serbestce degistirilebilir, kimlik degil).")]
        public string displayName;

        public PropCategory category = PropCategory.Cover;

        /// <summary>
        /// Which <see cref="PropPalette"/> this prop belongs to. EMPTY MEANS EVERY PALETTE.
        ///
        /// The empty default is what keeps this axis free: a library that has never been split
        /// into palettes behaves exactly as it did, because every prop reads as "belongs
        /// everywhere". It is also the right answer for pieces that have no world of their own —
        /// spawn rings, the weapon rack, targets. Hiding those while UZAY is selected would leave
        /// the map missing the parts the match itself needs.
        /// </summary>
        [Tooltip("Ait oldugu palet kimligi. BOS = HER palette gorunur (dogus halkasi, silah " +
                 "rafi gibi dunyasi olmayan parcalar boyle kalmali). Menu 31'den atanir.")]
        public string paletteId = "";

        public PropSnap snap = PropSnap.Floor;

        [Tooltip("Dogrudan referans. Doluysa kutuphane yuklenirken prefab da bellege gelir.")]
        public GameObject prefab;

        [Tooltip("Doluysa prefab yerine BURADAN tembel yuklenir (Resources altinda, uzantisiz yol). " +
                 "Kutuphane buyudugunde Quest bellegini korumanin yolu bu.")]
        public string resourcePath;

        /// <summary>
        /// Ground area the prop covers, in METRES — not in cells. THE canonical statement of
        /// this rule; everything converting between the two points back here.
        ///
        /// A cell count is only meaningful next to one particular <see cref="RoomGrid.CellSize"/>:
        /// stored as "6 cells" a barrier means 1.5 m on a 0.25 m grid and 0.75 m on a 0.125 m
        /// one, so changing the grid resolution would silently resize every prop and shift every
        /// placement in already-saved maps. The physical size never changes; cells are derived
        /// from it per grid by <see cref="RoomGrid.FootprintCells"/>.
        /// </summary>
        [Tooltip("Zeminde kapladigi GERCEK alan (m, X ve Z) — dondurulmemis halde, hucre " +
                 "cinsinden DEGIL.")]
        public Vector2 sizeMeters = Vector2.one;

        /// <summary>
        /// Scale the prop to EXACTLY fill the cells it reserves, instead of leaving it at the
        /// size the model happens to be.
        ///
        /// This is what makes two props sit flush side by side. The grid rounds every footprint
        /// to whole cells (<see cref="RoomGrid.FootprintCells"/>), so a barrier measured at
        /// 0.54 m reserves 0.50 m of grid on a 0.125 m lattice — and that 4 cm difference is a
        /// visible seam between neighbours, every time, no matter how carefully the player aims.
        /// Fitting closes it by construction rather than demanding every model be authored on a
        /// cell multiple.
        ///
        /// TURN IT OFF when the footprint is deliberately SMALLER than the mesh: a tree that
        /// reserves only its trunk, a spawn ring whose disc is allowed to overhang cover (see
        /// <c>ConstructorSetup.SpawnFootprintMetres</c>). Fitting those would squash the model
        /// down to the footprint.
        /// </summary>
        [Tooltip("Acik: prop, izgarada rezerve ettigi hucreleri TAM dolduracak sekilde olceklenir " +
                 "— yan yana konan iki prop arasinda bosluk kalmaz. Kapali: prefab kendi boyunda " +
                 "kalir (ayak izi kasten mesh'ten kucuk olan agac/dogus halkasi gibi proplar icin).")]
        public bool fitToFootprint = true;

        [Tooltip("Acik: 5 derece adimlarla doner (stick basili tutulunca tekrar eder). " +
                 "Kapali: yalnizca 90 derece.\n\n" +
                 "Doluluk her iki durumda da TAM: ara acida izgara, donmus dikdortgenin " +
                 "gercekten bastigi hucreleri kapatir (sinir kutusunun kosesi bos kalir), " +
                 "yani acili parcalarin yanina da insa edilebilir.")]
        public bool freeRotation;

        /// <summary>
        /// Keeps a prop out of the build palette without taking it out of the library.
        ///
        /// Retiring a piece and DELETING it are different things. Network placement messages
        /// address props by library index, and saved maps name them by id, so dropping an entry
        /// shifts every index after it and quietly empties any map that used it. Hiding costs
        /// nothing at either end: the palette skips it, everything already built still resolves,
        /// and putting it back is one tick.
        /// </summary>
        [Tooltip("Acik: insa modunun paletinde CIKMAZ, ama kutuphanede kalir — onu kullanan " +
                 "kayitli haritalar bozulmadan kurulmaya devam eder. Bir propu emekliye ayirmanin " +
                 "yolu bu; girdiyi silmek ag indekslerini kaydirir ve eski haritalari bosaltir.")]
        public bool hiddenInPalette;

        [Tooltip("Acik: yerlestirilince NetworkObject olarak spawn edilir (kirilabilir/etkilesimli). " +
                 "Kapali: duz mesh, ag maliyeti sifir. Cogu prop kapali olmali.")]
        public bool networked;

        /// <summary>
        /// Lets this prop be placed into cells another prop already holds.
        /// </summary>
        /// <remarks>
        /// WHY THIS EXISTS: the grid's no-overlap rule assumes a MODULAR KIT — pieces authored
        /// with flat ends on cell multiples, which tile flush because their bounding box IS their
        /// geometry. The SciFi and wood sets are like that. Scanned/generated art is not: a
        /// sandbag emplacement is a curve inside its box, a ruined wall has broken ends, a dead
        /// tree is mostly air. Their footprints reserve the BOX, so two of them side by side sit
        /// as far apart as their empty corners are wide — and no amount of nudging closes it,
        /// because the grid is doing exactly what it was told.
        ///
        /// THE RULE IS THE PLACED PROP'S. Overlap is allowed when the piece BEING PLACED has this
        /// on; what is already there does not get a vote — which is what keeps the behaviour
        /// predictable when a map mixes pieces that have it with pieces that do not.
        ///
        /// ON FOR EVERYTHING IN THE LIBRARY, INCLUDING NEW PROPS. It started as an escape hatch
        /// for irregular art and became the default because the answer kept being "yes" — a
        /// person building an arena is composing a scene, and every time the grid said no to a
        /// placement that looked right, the grid was wrong. Turned on where props ENTER the
        /// library (the folder scan and the drag-drop add), not on the field itself: menu 29's
        /// self-check builds its own PropDef fixtures precisely to test the occupancy rule, and
        /// they still need the strict behaviour to be testing anything.
        ///
        /// WHAT THE MODULAR KIT GIVES UP by having it on: the accidental-duplicate guard. Those
        /// pieces already tile flush without help, so overlap buys them nothing, and the only
        /// change is that two identical walls can now be stacked in the same cell without the
        /// grid objecting. One tick in menu 31 puts the guard back per prop.
        ///
        /// WHAT IT COSTS, so it is a choice and not a surprise:
        ///  - the cell OWNER map holds one id per cell, so the newer prop wins the shared cells;
        ///    aiming at those to delete picks the newer one. The older is still reachable through
        ///    any cell it kept to itself.
        ///  - removing the older prop frees the shared cells even though the newer still stands
        ///    there. Reopening the map fixes it: <see cref="ConstructorSession.Adopt"/> rebuilds
        ///    occupancy from the layout.
        ///  - two solid meshes really do intersect. That is the point here.
        /// </remarks>
        [Tooltip("Acik: bu prop, baska bir propun tuttugu hucrelere de konabilir.\n\n" +
                 "Kutuphaneye giren HER prop bununla geliyor — tarama da surukle-birak da " +
                 "acik olarak ekliyor.\n\n" +
                 "Kapatirsan o prop dolu hucreye konamaz: yanlislikla ust uste yerlestirmeye " +
                 "karsi koruma geri gelir, ama parcayi baska bir parcaya gecirmek de mumkun " +
                 "olmaz.")]
        public bool allowOverlap;

        [Tooltip("Modelin yerel yuksekligi (m) — onizleme olceklemesi icin tarama araci doldurur.")]
        public float height = 1f;

        // Cozulmus prefab onbellegi. [NonSerialized]: Unity public alanlari seri hale getirir,
        // bu da domain reload sonrasi bayat/kopuk referans demek olurdu.
        [NonSerialized] GameObject _resolved;
        [NonSerialized] bool _resolveTried;

        // Olculmus mesh sinir kutusu — hayalet her karede soruyor, prefab hiyerarsisini her
        // karede gezmek olmaz.
        [NonSerialized] Bounds _meshBounds;
        [NonSerialized] bool _measured;

        /// <summary>
        /// The prefab to instantiate: the direct reference when set, otherwise a lazy
        /// <c>Resources.Load</c> of <see cref="resourcePath"/>. Result is cached; a missing
        /// prefab returns null exactly once per domain (it logs, then stays quiet).
        /// </summary>
        public GameObject Resolve()
        {
            if (_resolved != null) return _resolved;
            if (_resolveTried) return null;
            _resolveTried = true;

            if (prefab != null) { _resolved = prefab; return _resolved; }

            if (!string.IsNullOrEmpty(resourcePath))
            {
                _resolved = Resources.Load<GameObject>(resourcePath);
                if (_resolved == null)
                    Debug.LogWarning($"[PropLibrary] '{id}' icin Resources/{resourcePath} bulunamadi.");
                return _resolved;
            }

            Debug.LogWarning($"[PropLibrary] '{id}' icin ne prefab ne resourcePath var.");
            return null;
        }

        /// <summary>
        /// The prefab's visual size in the ROOT's local space — the root's own localScale left
        /// OUT, so a fit can be expressed as an absolute scale (see
        /// <see cref="MapBuilder.LocalScaleFor"/>) without counting the authored scale twice.
        /// Zero when the prop has no measurable renderer, which callers must treat as "cannot
        /// fit, leave the prefab alone".
        /// </summary>
        public Vector3 MeshLocalSize => MeshLocalBounds.size;

        /// <summary>
        /// Where the model actually sits relative to the prefab's origin, in root-local space.
        ///
        /// Rarely zero on bought art: exporters put the pivot wherever the artist left it, and
        /// in this library 38 of 46 prefabs are off — one by 15 cm. That offset lands straight
        /// on top of the grid, because <see cref="MapBuilder.Spawn"/> puts the prefab's ORIGIN
        /// on the cell rectangle's centre, so a model whose origin is not its centre stands that
        /// far from where the grid says it does. <see cref="MapBuilder.PivotOffset"/> is what
        /// cancels it.
        /// </summary>
        public Vector3 MeshLocalCenter => MeshLocalBounds.center;

        public Bounds MeshLocalBounds
        {
            get
            {
                if (_measured) return _meshBounds;
                _measured = true;
                _meshBounds = MeasureLocalBounds(Resolve());
                return _meshBounds;
            }
        }

        /// <summary>
        /// Measures a prefab ASSET, which is why it does not touch <c>Renderer.bounds</c>: that
        /// is a world-space box off the renderer's current transform, and on an asset that has
        /// never been instantiated it is not something to build geometry on. Walking each
        /// renderer's own local box into root space works on the asset, and folds in children
        /// that sit rotated or offset under the root.
        /// </summary>
        public static Bounds MeasureLocalBounds(GameObject prefab)
        {
            if (prefab == null) return new Bounds(Vector3.zero, Vector3.zero);

            var root = prefab.transform;
            bool any = false;
            Bounds acc = new Bounds(Vector3.zero, Vector3.zero);

            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                // Partikul/iz/cizgi ciziciler ATLANIR: sinir kutulari calisma zamaninda
                // uretiliyor, prefabta ya sifir ya sacma. Namlu dumani olan bir siperi
                // metrelerce genis gosterip ayak izini sacmalatirlardi.
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;

                Bounds lb = r.localBounds;
                if (lb.size.sqrMagnitude < 1e-8f)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    lb = mf.sharedMesh.bounds;
                }

                for (int i = 0; i < 8; i++)
                {
                    var sign = new Vector3((i & 1) == 0 ? -1f : 1f,
                                           (i & 2) == 0 ? -1f : 1f,
                                           (i & 4) == 0 ? -1f : 1f);
                    Vector3 p = root.InverseTransformPoint(
                        r.transform.TransformPoint(lb.center + Vector3.Scale(lb.extents, sign)));
                    if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                    else acc.Encapsulate(p);
                }
            }
            return any ? acc : new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Ground the prefab covers AT ITS OWN SCALE (m) — the same quantity the scan tool
        /// writes into <see cref="sizeMeters"/>, so the two are directly comparable and a drift
        /// between them is reportable rather than a silent squash.
        /// </summary>
        public Vector2 MeshFootprintMeters
        {
            get
            {
                var prefab = Resolve();
                if (prefab == null) return Vector2.zero;
                Vector3 m = MeshLocalSize;
                Vector3 s = prefab.transform.localScale;
                return new Vector2(m.x * Mathf.Abs(s.x), m.z * Mathf.Abs(s.z));
            }
        }

        /// <summary>Editor tooling changes the prefab under us; drop the cache so Resolve re-reads.</summary>
        public void InvalidateCache()
        {
            _resolved = null;
            _resolveTried = false;
            _measured = false;
        }
    }

    /// <summary>
    /// The catalogue of everything a player can place. One asset under a Resources folder so
    /// every peer reads the SAME list at runtime, matching how <see cref="CombatConfig"/> is
    /// shared — no per-scene wiring.
    ///
    /// TWO ADDRESSING SCHEMES on purpose:
    ///  - saved maps store the STRING id (readable, survives reordering the list),
    ///  - network messages carry the USHORT index (2 bytes instead of a string per placement).
    /// <see cref="IndexOf"/> / <see cref="ByIndex"/> convert between them. Because the index is
    /// positional, peers must agree on the list order — <see cref="contentVersion"/> is bumped
    /// by the scan tool whenever the order changes so a mismatch is detectable instead of
    /// silently placing the wrong prop.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Prop Library", fileName = "PropLibrary")]
    public class PropLibrary : ScriptableObject
    {
        public const string ResourceName = "PropLibrary";

        /// <summary>Current <see cref="schemaVersion"/>.</summary>
        public const int SizeInMetresSchema = 1;

        /// <summary>
        /// How far a prop's real size may sit from the footprint it reserves before fitting
        /// backs off (<see cref="MapBuilder.LocalScaleFor"/>) and <see cref="Validate"/> says so.
        ///
        /// Generous on purpose. A few centimetres of cell rounding is the normal case and
        /// exactly what fitting is for; a gap this wide means <see cref="PropDef.sizeMeters"/>
        /// was authored to mean something other than "how big the mesh is" — a tree's trunk, a
        /// spawn ring's walkable centre — and the model must not be deformed to match it.
        /// </summary>
        public const float FitMismatchTolerance = 0.25f;

        [Tooltip("Tarama araci her sira degisikliginde artirir. Farkli surumdeki iki istemci " +
                 "ayni indeksten farkli prop anlar — bu alan uyusmazligi yakalanabilir kilar.")]
        public int contentVersion = 1;

        [Tooltip("Veri semasi surumu. 0 = boyut HUCRE cinsindeydi (izgara boyuna bagli), " +
                 "1 = boyut METRE cinsinde. Tarama araci 0 gorunce tum boyutlari prefablardan " +
                 "yeniden olcup 1'e yukseltir.")]
        public int schemaVersion = 0;

        [Tooltip("Tarama aracinin (menu 25) prefab arayacagi klasorler.")]
        public string[] sourceFolders = new string[0];

        [Tooltip("Adlandirilmis prop setleri. Menu 31'den olusturulur; oyuncu insa modunda " +
                 "carkin ortasindan aralarinda gecis yapar.")]
        public PropPalette[] palettes = new PropPalette[0];

        public PropDef[] props = new PropDef[0];

        Dictionary<string, int> _index;
        int _indexedCount = -1;

        // ------------------------------------------------------------- lookup

        /// <summary>Library index of <paramref name="id"/>, or -1 when the id is unknown.</summary>
        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            EnsureIndex();
            return _index.TryGetValue(id, out int i) ? i : -1;
        }

        public PropDef ById(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? props[i] : null;
        }

        public PropDef ByIndex(int i) => (i >= 0 && i < props.Length) ? props[i] : null;

        public int Count => props != null ? props.Length : 0;

        // ------------------------------------------------------------- palettes

        public int PaletteCount => palettes != null ? palettes.Length : 0;

        public PropPalette PaletteById(string id)
        {
            if (string.IsNullOrEmpty(id) || palettes == null) return null;
            foreach (var p in palettes)
                if (p != null && p.id == id) return p;
            return null;
        }

        /// <summary>
        /// A palette's shown name, or a readable stand-in.
        ///
        /// An unknown id is NOT an error worth hiding: it means a prop points at a palette that
        /// was deleted, and saying so on the wheel is how anyone finds out.
        /// </summary>
        public string PaletteName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "DIGER";
            var p = PaletteById(id);
            return p != null && !string.IsNullOrEmpty(p.displayName)
                ? p.displayName : "? " + id;
        }

        /// <summary>Props assigned to <paramref name="id"/> — NOT counting the always-visible ones.</summary>
        public int OwnedCount(string id)
        {
            if (props == null) return 0;
            int n = 0;
            foreach (var p in props)
                if (p != null && p.paletteId == id) n++;
            return n;
        }

        /// <summary>Turns a display name into a stable id: lowercase, letters and digits only.</summary>
        public static string MakePaletteId(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return "";
            var sb = new System.Text.StringBuilder(displayName.Length);
            foreach (char c in displayName.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString().Trim('_');
        }

        void EnsureIndex()
        {
            // Sozlugun BOYUTUNA degil, indekslendigi liste uzunluguna bakiyoruz: kopya kimlik
            // varsa sozluk listeden kisa kalir ve boyut karsilastirmasi her aramada sozlugu
            // yeniden kurdururdu (oyun ici her yerlestirmede gorunmez bir maliyet).
            if (_index != null && _indexedCount == Count) return;
            _indexedCount = Count;
            _index = new Dictionary<string, int>(Count);
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                // Ilk kayit kazanir; kopya kimlikler Validate() ile raporlanir.
                if (!_index.ContainsKey(p.id)) _index.Add(p.id, i);
            }
        }

        /// <summary>Editor tooling rewrote the list — rebuild the id map on next lookup.</summary>
        public void InvalidateIndex()
        {
            _index = null;
            _indexedCount = -1;
            if (props == null) return;
            foreach (var p in props) p?.InvalidateCache();
        }

        // ------------------------------------------------------------- validation

        /// <summary>
        /// Problems that would corrupt saved maps or crash a build: missing/duplicate ids,
        /// unresolvable prefabs, nonsensical footprints. Empty list = healthy library.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (props == null || props.Length == 0)
            {
                problems.Add("Kutuphane bos — menu 25 (Prop Kutuphanesi Tara) calistir.");
                return problems;
            }

            // Paletler once: proplarin isaret ettigi kimlikler buradan dogrulaniyor.
            var paletteIds = new HashSet<string>();
            if (palettes != null)
            {
                for (int i = 0; i < palettes.Length; i++)
                {
                    var pal = palettes[i];
                    if (pal == null) { problems.Add($"palet[{i}] null girdi."); continue; }
                    if (string.IsNullOrEmpty(pal.id)) problems.Add($"palet[{i}] kimlik bos.");
                    else if (!paletteIds.Add(pal.id))
                        problems.Add($"palet[{i}] kopya kimlik: '{pal.id}'.");
                }
            }

            var seen = new HashSet<string>();
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p == null) { problems.Add($"[{i}] null girdi."); continue; }

                if (string.IsNullOrEmpty(p.id)) problems.Add($"[{i}] kimlik bos.");
                else if (!seen.Add(p.id)) problems.Add($"[{i}] kopya kimlik: '{p.id}'.");

                // Silinmis bir palete isaret eden prop HICBIR palette gorunmez — bos
                // paletteId "her yerde" demek, taninmayan bir kimlik ise "hicbir yerde".
                if (!string.IsNullOrEmpty(p.paletteId) && !paletteIds.Contains(p.paletteId))
                    problems.Add($"[{i}] '{p.id}' silinmis/bilinmeyen palete bagli: " +
                                 $"'{p.paletteId}' — hicbir palette gorunmez.");

                if (p.prefab == null && string.IsNullOrEmpty(p.resourcePath))
                    problems.Add($"[{i}] '{p.id}' icin ne prefab ne resourcePath var.");

                if (p.sizeMeters.x <= 0f || p.sizeMeters.y <= 0f)
                    problems.Add($"[{i}] '{p.id}' boyutu gecersiz: {p.sizeMeters} m.");

                // Fit acik ama sizeMeters mesh'ten kopuksa MapBuilder.FitAxis geri cekilir ve
                // prop yan yana dizilirken bosluk birakmaya devam eder — sessizce. Sebebi
                // burada soyleniyor, cunku tek belirtisi "iki propu bir turlu bitistiremiyorum"
                // olur ve o belirtiden bu alana kimse ulasamaz.
                //
                // Yalnizca GENISLIK ekseni: bir hucreden ince duvarlarda kalinlik sapmasinin
                // buyuk cikmasi normal (ayak izi bir hucrenin altina inemez) ve fit onu zaten
                // dogru sekilde atliyor. Boslugun gorundugu eksen genislik.
                if (p.fitToFootprint)
                {
                    float meshWidth = p.MeshFootprintMeters.x;
                    if (meshWidth > 0.0001f)
                    {
                        float drift = Mathf.Abs(meshWidth - p.sizeMeters.x) / meshWidth;
                        if (drift > FitMismatchTolerance)
                            problems.Add($"[{i}] '{p.id}': sizeMeters genisligi {p.sizeMeters.x:0.00} m, " +
                                         $"mesh ise {meshWidth:0.00} m (%{drift * 100f:0} sapma). Fit bu " +
                                         "kadar buyuk bir farki uygulamaz, yani prop yan yana " +
                                         "dizilirken bosluk birakir — boyutu tazele (menu 25) ya da " +
                                         "bu propta fitToFootprint'i kapat.");
                    }
                }
            }
            return problems;
        }

        // ------------------------------------------------------------- singleton

        static PropLibrary _instance;
        static PropLibrary _fallback;

        /// <summary>
        /// The shared library, loaded from any Resources folder. Returns an EMPTY in-memory
        /// library when the asset is missing so the constructor degrades to "nothing to place"
        /// instead of null-referencing mid-session.
        ///
        /// The miss is deliberately NOT cached the way <see cref="CombatConfig"/> caches its
        /// fallback: the scan tool CREATES this asset during an editor session, so a cached miss
        /// would keep serving an empty library until the next domain reload — the tool would
        /// look broken right after it succeeded.
        /// </summary>
        public static PropLibrary Instance
        {
            get
            {
                if (_instance != null) return _instance;

                _instance = Resources.Load<PropLibrary>(ResourceName);
                if (_instance != null) return _instance;

                if (_fallback == null)
                {
                    Debug.LogWarning($"[PropLibrary] Resources/{ResourceName}.asset bulunamadi — " +
                                     "bos kutuphane kullaniliyor (menu 25 ile uret).");
                    _fallback = CreateInstance<PropLibrary>();
                }
                return _fallback;
            }
        }
    }
}
