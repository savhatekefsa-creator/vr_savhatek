using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Owns the live editing state: the layout being built, the grid it is built on, and the
    /// scene instances that mirror it. Every mutation goes through <see cref="TryPlace"/> /
    /// <see cref="TryRemove"/> so those three never disagree.
    ///
    /// This is also the seam for networking (step 8): the placer never touches the grid or the
    /// layout directly, it asks the session. Making the session send an RPC and apply on the
    /// reply — instead of applying locally — is the whole change.
    /// </summary>
    public class ConstructorSession : MonoBehaviour
    {
        /// <summary>Uzerinde calisilan haritanin dosya adi (menu 27'nin urettigi "Test" degil).</summary>
        public const string WorkingMapName = "Current";

        public static ConstructorSession Instance { get; private set; }

        public MapLayout Layout { get; private set; }
        public RoomGrid Grid { get; private set; }
        public PropLibrary Library => PropLibrary.Instance;
        public Transform Root { get; private set; }

        public bool IsActive => Layout != null && Grid != null;
        public int PlacedCount => Layout != null ? Layout.Count : 0;

        /// <summary>
        /// True when the grid stands on an invented floor (<see cref="RoomPlan.FreeSpace"/>)
        /// instead of a scan. Read from the LAYOUT, not from a flag set at startup, so it stays
        /// right for a peer that received the map over the network instead of building it.
        /// </summary>
        public bool IsFreeSpace => Layout != null && Layout.builtForRoom != null &&
                                   Layout.builtForRoom.IsFreeSpace;

        /// <summary>Fired after any placement/removal — the grid view and HUD redraw on this.</summary>
        public event Action Changed;

        readonly Dictionary<uint, GameObject> _instances = new Dictionary<uint, GameObject>();

        // Hucre -> ustunde duran yerlestirmenin kimligi. Imlecin altindakini O(1) bulmak icin;
        // her karede prop listesini taramak VR'da bedava degil.
        readonly Dictionary<int, uint> _cellOwner = new Dictionary<int, uint>();

        List<PropDef> _placeable;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            // Oyun bitiyor ya da sahne kapaniyor: bekleyen kayit varsa SIMDI yaz. Editor'de
            // Play'i durdurmak da buraya dusuyor, yani "durdurdum, emegim gitti" olmuyor.
            FlushPendingSave();
            UnhookServerStopped();
            if (Instance == this) Instance = null;
        }

        void OnApplicationQuit() => FlushPendingSave();

        // ------------------------------------------------------------- otomatik kayit

        /// <summary>
        /// How long the autosave waits after the last change. Long enough that laying down a run
        /// of props writes the file once instead of once per prop, short enough that nobody
        /// loses a session's work to a closed window.
        /// </summary>
        public const float AutoSaveDelay = 3f;

        bool _dirty;
        float _saveAt;
        bool _hookedServerStopped;

        /// <summary>
        /// Whether THIS peer owns the map file.
        ///
        /// The map belongs to the server: a client writing its own copy leaves a file nobody
        /// reads and that the next sync invalidates. Offline — no NetworkManager, or one that
        /// has not started — the local peer is the only peer, so it owns it.
        /// </summary>
        public static bool IsMapAuthority
        {
            get
            {
                var net = Unity.Netcode.NetworkManager.Singleton;
                return net == null || !net.IsClient || net.IsServer;
            }
        }

        /// <summary>
        /// A placement changed the map — schedule a write.
        ///
        /// THIS IS WHAT MAKES BUILDING IN THE HEADSET STICK. The player building is a client, so
        /// nothing they do can write the file; the server holds every placement in memory and
        /// used to drop the lot when it closed, because the only calls to <see cref="Save"/>
        /// came from a keyboard shortcut and from leaving build mode — neither of which the
        /// server's own player ever does when the building happens on a headset.
        /// </summary>
        void MarkDirty()
        {
            if (!IsMapAuthority) return;
            _dirty = true;
            _saveAt = Time.unscaledTime + AutoSaveDelay;
        }

        void Update()
        {
            HookServerStopped();
            if (!_dirty || Time.unscaledTime < _saveAt) return;
            FlushPendingSave();
        }

        /// <summary>Writes now if a save is pending. Safe to call at any time.</summary>
        public void FlushPendingSave()
        {
            if (!_dirty) return;
            _dirty = false;
            if (IsActive && IsMapAuthority) Save();
        }

        // Sunucu DURDURULUP uygulama acik kalabilir (menuye donmek gibi) — o da bir kapanistir.
        // NetworkManager, ConstructorBootstrap bilesenleri kurarken henuz var olmayabildigi
        // icin abonelik tembel: ilk goruldugunde baglaniyor.
        void HookServerStopped()
        {
            if (_hookedServerStopped) return;
            var net = Unity.Netcode.NetworkManager.Singleton;
            if (net == null) return;
            net.OnServerStopped += OnServerStopped;
            _hookedServerStopped = true;
        }

        void UnhookServerStopped()
        {
            if (!_hookedServerStopped) return;
            var net = Unity.Netcode.NetworkManager.Singleton;
            if (net != null) net.OnServerStopped -= OnServerStopped;
            _hookedServerStopped = false;
        }

        void OnServerStopped(bool _) => FlushPendingSave();

        // ------------------------------------------------------------- session start

        /// <summary>
        /// Starts editing if it has not started yet. Returns false when the session could not
        /// start; <see cref="NotStartedReason"/> says why.
        ///
        /// A CLIENT NEVER READS THE ROOM FROM ITS OWN DISK. The scan pipeline stores the plan on
        /// the SERVER only (<see cref="RoomScanSync"/> ships it there and the PC writes the
        /// file) — a headset has no local copy no matter how many times it rescans. And it must
        /// not use one even if it had it: two scans of the same room produce slightly different
        /// polygons, the grid origin snaps differently, and cell (4,7) stops meaning the same
        /// spot on every headset. So the client asks the server and waits.
        ///
        /// NO SCAN IS NOT A DEAD END. Offline (or on the server) a missing scan opens a
        /// <see cref="RoomPlan.FreeSpace"/> floor instead of refusing: the grid needs a polygon,
        /// not a TRUE one. A saved map beats both — it carries the room it was authored in.
        /// </summary>
        public bool EnsureStarted()
        {
            if (IsActive) return true;

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);

            if (online && !net.IsServer)
            {
                NotStartedReason = ConstructorSync.ClientRequestLayout()
                    ? "Sunucudan harita bekleniyor..."
                    : "Sunucuya baglanti yok.";
                return false;
            }

            var plan = RoomPlanIO.Load();
            var saved = MapLayout.Load(WorkingMapName);

            if (plan == null)
            {
                // Bir kayit varsa ODASI ONUN ICINDE: uydurma bir zeminle yeniden izgaralamak
                // kokeni kaydirir ve butun yerlestirmeler yanlis yere duser. Tarama dosyasi
                // gitmis olabilir, harita hala kendi odasini biliyor.
                if (saved != null && saved.HasRoom)
                {
                    if (!Adopt(saved)) NotStartedReason = "Kayitli haritanin odasi gecersiz.";
                    return IsActive;
                }

                // Tarama da kayit da yok: VAZGECME, bos bir zemin uret. Poligonun disi zaten
                // insa edilebilir (FreeOutside), yani kaybedilen tek sey "buraya yurunebilir"
                // bilgisi — taranmamis bir odada zaten bilinmeyen bir sey.
                plan = RoomPlan.FreeSpace();
                Debug.LogWarning("[Constructor] Oda taramasi yok — SERBEST ALAN ile aciliyor: " +
                                 $"kokende {plan.Area():0} m2 zemin + {RoomGrid.DefaultOutsideMargin:0} m pay. " +
                                 "Hicbir hucre mobilyaya kapali degil; gercek odanin duvarlari bilinmiyor.");
            }

            bool ok = saved != null ? Adopt(saved, plan) : StartNew(plan);
            if (!ok) NotStartedReason = "Oda plani gecersiz: izgara kurulamadi.";
            return ok;
        }

        /// <summary>Why <see cref="EnsureStarted"/> last returned false (shown to the player).</summary>
        public string NotStartedReason { get; private set; } = "";

        /// <summary>Begins an empty map on the given room.</summary>
        public bool StartNew(RoomPlan plan)
        {
            var grid = RoomGrid.FromPlan(plan);
            if (grid == null) return false;

            var layout = new MapLayout
            {
                name = WorkingMapName,
                createdBy = SystemInfo.deviceName,
                libraryVersion = Library.contentVersion,
                cellSize = grid.CellSize,
                levelHeight = grid.LevelHeight,
                buildMargin = grid.OutsideMargin,
                builtForRoom = plan,
            };
            return Adopt(layout, plan);
        }

        /// <summary>
        /// Adopts a layout and rebuilds everything from it. <paramref name="roomOverride"/>
        /// lets a map authored in this room be re-grided against the CURRENT scan; pass null to
        /// use the room embedded in the map.
        /// </summary>
        public bool Adopt(MapLayout layout, RoomPlan roomOverride = null)
        {
            if (layout == null) return false;
            if (roomOverride != null) layout.builtForRoom = roomOverride;

            // levelHeight de izgaraya gidiyor: dikey doluluk propun kac kat tuttugunu ondan
            // hesapliyor, ve hucre boyu gibi o da haritanin kendi olcusu.
            var grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
            if (grid == null) return false;

            Clear();
            Layout = layout;
            Grid = grid;
            Root = MapBuilder.EnsureRoot();

            // Once dolulugu isle, SONRA sahneyi kur: boylece kayitli haritadaki cakisan bir
            // yerlestirme (elle duzenlenmis JSON, eski kutuphane) sessizce ust uste binmez.
            int applied = 0;
            foreach (var p in Layout.props)
            {
                var def = Library.ById(p.propId);
                if (def == null) continue;
                var cell = new Vector2Int(p.cellX, p.cellZ);
                Grid.Occupy(def, cell, p.rot, p.scalePct, p.level, p.heightPct);
                RememberOwner(Grid.ShapeOf(def, cell, p.rot, p.scalePct), p.level, p.instanceId);
                applied++;
            }

            MapBuilder.Clear(Root);
            foreach (var p in Layout.props)
            {
                var def = Library.ById(p.propId);
                if (def == null) continue;
                var go = MapBuilder.Spawn(p, def, Grid, Layout.levelHeight, Root);
                if (go != null)
                {
                    _instances[p.instanceId] = go;
                    Weapons.WeaponRackRespawner.RegisterConstructorRack(p.instanceId, go);
                }
            }

            Debug.Log($"[Constructor] Oturum acildi: '{Layout.name}' — {applied} yerlestirme, " +
                      $"izgara {Grid.Cols}x{Grid.Rows}, {Grid.Report().free} oda-ici bos hucre " +
                      $"(+{Grid.OutsideMargin:0.0} m oda disi pay).");

            // Pay haritaya YAZILI. Varsayilan buyudugunde eski harita yine kendi dar sinirinda
            // aciliyor — dogru davranis (hucre koordinatlari kaymasin diye), ama disaridan
            // "hala koyamiyorum"dan ayirt edilemiyor. Sebebi ve cikis yolu bir kez soylensin.
            if (Layout.buildMargin < RoomGrid.DefaultOutsideMargin - 0.01f)
                Debug.LogWarning($"[Constructor] '{Layout.name}' {Layout.buildMargin:0.0} m oda disi payla " +
                                 $"kaydedilmis, guncel varsayilan {RoomGrid.DefaultOutsideMargin:0.0} m. " +
                                 "Insa sinirini genisletmek icin: Tools > VR Multiplayer > " +
                                 "32. Haritayi Guncel Izgaraya Tasi");
            Changed?.Invoke();
            return true;
        }

        public void Clear()
        {
            // Raf kayitlarini ONCE dus: harita yeniden kurulunca prop objeleri yok edilip
            // yeniden yaratiliyor, ve eski yuvalar durursa her yeniden kurulumda silah sayisi
            // katlanirdi.
            Weapons.WeaponRackRespawner.ClearConstructorRacks();

            if (Root != null) MapBuilder.Clear(Root);
            _instances.Clear();
            _cellOwner.Clear();
            // Yeni harita = yeni gecmis. Eski kimlikler bu haritada baska proplara denk gelir.
            _pending.Clear();
            _undo.Clear();
            Layout = null;
            Grid = null;
        }

        // ------------------------------------------------------------- mutations

        /// <summary>True when the placement would fit: prop is real and every cell under it is free.</summary>
        public bool CanPlace(PropDef def, Vector2Int minCell, byte rot, byte scalePct = 100,
            byte level = 0, byte heightPct = 100)
        {
            if (!IsActive || def == null || def.Resolve() == null) return false;
            return Grid.CanPlace(def, minCell, rot, scalePct, level, heightPct);
        }

        /// <summary>
        /// Asks for a placement. OFFLINE this applies immediately; ONLINE it goes to the server
        /// and the prop appears when the server's broadcast comes back.
        ///
        /// No client-side prediction on purpose: a predicted prop that the server then rejects
        /// has to be un-drawn, and a player watching cover blink out of existence trusts the
        /// tool less than one who waited 15 ms on a LAN.
        ///
        /// Returns false only for a LOCAL rejection (does not fit / no prop); true means
        /// "applied, or the request is on its way".
        /// </summary>
        public bool TryPlace(PropDef def, Vector2Int minCell, byte level, byte rot,
            byte scalePct = 100, byte heightPct = 100) =>
            TryPlace(def, minCell, level, rot, scalePct, heightPct, false);

        /// <summary>
        /// <paramref name="level"/> lifts the placement off the floor in
        /// <see cref="MapLayout.levelHeight"/> steps.
        ///
        /// It was hardcoded to 0 in all three paths below, which is why nothing could be built
        /// in the air: the field existed on <see cref="PlacedProp"/>, the RPC carried it and
        /// <see cref="MapBuilder.Spawn"/> already positioned by it — the only place the number
        /// never came from was the player.
        /// </summary>
        bool TryPlace(PropDef def, Vector2Int minCell, byte level, byte rot, byte scalePct,
            byte heightPct, bool fromUndo)
        {
            if (!CanPlace(def, minCell, rot, scalePct, level, heightPct)) return false;

            // Istek kaydi: online'da kimligi SUNUCU verir, yani "az once ne koydum" bilgisi
            // ancak yayin geri geldiginde tamamlanabilir. Burada niyeti yaziyoruz, ApplyPlace
            // eslesince geri-al yigitina isliyor.
            AddPending(new Pending
            {
                place = true,
                fromUndo = fromUndo,
                prop = new PlacedProp
                {
                    propId = def.id, cellX = minCell.x, cellZ = minCell.y, level = level,
                    rot = rot, scalePct = scalePct, heightPct = heightPct,
                },
            });

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);

            if (!online)
            {
                ApplyPlace(def.id, minCell, level, rot, MintInstanceId(), scalePct, heightPct);
                return true;
            }

            if (net.IsServer)
            {
                // Sunucu kendi karari: kimligi uret ve HERKESE yayinla. Uygulama tek yoldan,
                // RPC alicisindan gecer (sunucu da alicilardan biri) — iki ayri uygulama yolu
                // olsaydi sunucu ile istemciler zamanla ayrisirdi.
                return ConstructorSync.ServerBroadcastPlace(def, minCell, level, rot, MintInstanceId(), scalePct, heightPct);
            }

            return ConstructorSync.ClientRequestPlace(def, minCell, level, rot, scalePct, heightPct);
        }

        /// <summary>
        /// Highest level that still fits under the scanned ceiling. A placement above the real
        /// ceiling is one the player can neither see nor reach, so the control stops there
        /// rather than letting the byte run to 255.
        /// </summary>
        public byte MaxLevel
        {
            get
            {
                if (!IsActive) return 0;
                float step = Layout.levelHeight;
                if (step <= 0.01f) return 0;
                float headroom = Grid.CeilingY - Grid.FloorY;
                // Kat maskesi bir byte: RoomGrid.MaxLevels'in otesi saklanamaz.
                return (byte)Mathf.Clamp(Mathf.FloorToInt(headroom / step), 0, RoomGrid.MaxLevels - 1);
            }
        }

        public bool TryRemove(uint instanceId) => TryRemove(instanceId, false);

        bool TryRemove(uint instanceId, bool fromUndo)
        {
            if (!IsActive || instanceId == 0) return false;

            var doomed = Layout.Find(instanceId);
            if (doomed == null) return false;

            // Silinmeden ONCE kopyala: geri almak icin propun nerede/nasil durdugunu bilmemiz
            // gerekiyor ve silindikten sonra o bilgi kalmiyor.
            AddPending(new Pending
            {
                place = false,
                fromUndo = fromUndo,
                prop = new PlacedProp
                {
                    propId = doomed.propId,
                    cellX = doomed.cellX,
                    cellZ = doomed.cellZ,
                    level = doomed.level,
                    rot = doomed.rot,
                    scalePct = doomed.scalePct,
                    heightPct = doomed.heightPct,
                    instanceId = instanceId,
                },
            });

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);

            if (!online) return ApplyRemove(instanceId);
            if (net.IsServer) return ConstructorSync.ServerBroadcastRemove(instanceId);
            return ConstructorSync.ClientRequestRemove(instanceId);
        }

        /// <summary>
        /// Takes the next id. Server/offline only — clients receive ids, never mint them.
        /// CONSUMES it (post-increment) rather than peeking: two placements in the same frame
        /// would otherwise both read the same value before either had been applied.
        /// </summary>
        public uint MintInstanceId() => IsActive ? Layout.nextInstanceId++ : 0u;

        // ------------------------------------------------------------- authoritative apply

        /// <summary>
        /// Actually puts a prop in the world. THE only place a placement enters this peer's
        /// state — offline path, server broadcast and late-join rebuild all land here, so the
        /// layout, the grid occupancy and the scene instances can never drift apart.
        /// </summary>
        public PlacedProp ApplyPlace(string propId, Vector2Int minCell, byte level, byte rot,
            uint instanceId, byte scalePct = 100, byte heightPct = 100)
        {
            if (!IsActive) return null;

            var def = Library.ById(propId);
            if (def == null)
            {
                Debug.LogWarning($"[Constructor] Kutuphanede olmayan prop uygulanamadi: '{propId}'.");
                return null;
            }
            if (Layout.Find(instanceId) != null) return null;   // yinelenen yayin

            var placed = Layout.AddWithId(def.id, minCell.x, minCell.y, level, rot, instanceId, scalePct, heightPct);
            Grid.Occupy(def, minCell, rot, scalePct, level, heightPct);
            RememberOwner(Grid.ShapeOf(def, minCell, rot, scalePct), level, placed.instanceId);

            var go = MapBuilder.Spawn(placed, def, Grid, Layout.levelHeight, Root);
            if (go != null)
            {
                _instances[placed.instanceId] = go;
                // Raf propuysa yuvalarini sunucuya bildir: mesh her este yerel kuruldu, ama
                // uzerindeki silahlari yalnizca sunucu spawn edebilir.
                Weapons.WeaponRackRespawner.RegisterConstructorRack(placed.instanceId, go);
            }

            // Bu yerlestirme BIZIM istegimiz miydi? Oyleyse geri-al yigitina yaz. Baska bir
            // oyuncunun koydugu propu bizim "geri al" tusumuz kaldirmamali.
            int i = _pending.FindIndex(x => x.place && x.prop.propId == propId &&
                                            x.prop.cellX == minCell.x && x.prop.cellZ == minCell.y &&
                                            x.prop.rot == rot && x.prop.scalePct == scalePct && x.prop.heightPct == heightPct);
            if (i >= 0)
            {
                bool fromUndo = _pending[i].fromUndo;
                _pending.RemoveAt(i);
                if (!fromUndo) PushUndo(new UndoEntry { wasPlace = true, instanceId = instanceId });
            }

            MarkDirty();
            Changed?.Invoke();
            return placed;
        }

        public bool ApplyRemove(uint instanceId)
        {
            if (!IsActive || instanceId == 0) return false;

            var placed = Layout.Find(instanceId);
            if (placed == null) return false;

            // Raf silindiyse yuvalarindaki silahlar da gitsin — ama oyuncunun ELINDEKI kalsin.
            Weapons.WeaponRackRespawner.UnregisterConstructorRack(instanceId);

            var def = Library.ById(placed.propId);
            if (def != null)
            {
                var cell = new Vector2Int(placed.cellX, placed.cellZ);
                Grid.Release(def, cell, placed.rot, placed.scalePct, placed.level, placed.heightPct);
                ForgetOwner(Grid.ShapeOf(def, cell, placed.rot, placed.scalePct), placed.level, instanceId);
            }

            if (_instances.TryGetValue(instanceId, out var go))
            {
                if (go != null) Destroy(go);
                _instances.Remove(instanceId);
            }

            Layout.Remove(instanceId);

            int i = _pending.FindIndex(x => !x.place && x.prop.instanceId == instanceId);
            if (i >= 0)
            {
                var p = _pending[i];
                _pending.RemoveAt(i);
                if (!p.fromUndo)
                    PushUndo(new UndoEntry
                    {
                        wasPlace = false,
                        propId = p.prop.propId,
                        cellX = p.prop.cellX,
                        cellZ = p.prop.cellZ,
                        level = p.prop.level,
                        rot = p.prop.rot,
                        scalePct = p.prop.scalePct,
                        heightPct = p.prop.heightPct,
                    });
            }

            MarkDirty();
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------- undo

        struct UndoEntry
        {
            public bool wasPlace;     // true: biz koyduk -> geri al = sil
            public uint instanceId;   // wasPlace icin
            public string propId;     // !wasPlace icin -> geri al = tekrar koy
            public int cellX, cellZ;
            public byte level;
            public byte rot;
            public byte scalePct;
            public byte heightPct;
        }

        struct Pending
        {
            public bool place;
            public bool fromUndo;
            public PlacedProp prop;
        }

        readonly List<Pending> _pending = new List<Pending>();
        readonly List<UndoEntry> _undo = new List<UndoEntry>();

        /// <summary>Kac adim geri gidilebilir. Elle harita kurarken bu kadari fazlasiyla yeter.</summary>
        public const int MaxUndo = 50;

        public int UndoCount => _undo.Count;

        void PushUndo(UndoEntry e)
        {
            _undo.Add(e);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }

        /// <summary>
        /// Records an outgoing request. Capped because a request the server REFUSES never comes
        /// back to clear its entry — without a ceiling those would accumulate for the whole
        /// session.
        /// </summary>
        void AddPending(Pending p)
        {
            _pending.Add(p);
            if (_pending.Count > 64) _pending.RemoveAt(0);
        }

        /// <summary>
        /// Reverses this player's last placement or deletion.
        ///
        /// Only OUR operations are on the stack — undoing something a teammate placed would be
        /// baffling for both of you. Entries whose prop has since disappeared (someone else
        /// deleted it) are skipped rather than failing, so the button keeps working.
        ///
        /// The reversal goes back through the normal request path, so online it is validated
        /// and broadcast like any other edit; it is flagged so it does not push itself back
        /// onto the stack as a fresh operation.
        /// </summary>
        public bool Undo()
        {
            if (!IsActive) return false;

            while (_undo.Count > 0)
            {
                var e = _undo[_undo.Count - 1];
                _undo.RemoveAt(_undo.Count - 1);

                if (e.wasPlace)
                {
                    if (Layout.Find(e.instanceId) == null) continue;   // baskasi zaten silmis
                    return TryRemove(e.instanceId, true);
                }

                var def = Library.ById(e.propId);
                if (def == null) continue;
                var cell = new Vector2Int(e.cellX, e.cellZ);
                byte sc = e.scalePct == 0 ? (byte)100 : e.scalePct;
                byte hp = e.heightPct == 0 ? (byte)100 : e.heightPct;
                if (!CanPlace(def, cell, e.rot, sc, e.level, hp)) continue;   // yeri baskasi kapmis
                return TryPlace(def, cell, e.level, e.rot, sc, hp, true);
            }
            return false;
        }

        /// <summary>
        /// Instance standing on a cell at a given build height, or 0 when nothing is there.
        ///
        /// Keyed by cell AND level now that a column can hold more than one prop. Deleting
        /// follows the height you are building at, which is the only reading that stays
        /// predictable — pointing at a shelf's cell from the floor should not take the shelf
        /// away. If that exact level is empty the topmost prop in the column answers, so
        /// deleting still works without hunting for the right height.
        /// </summary>
        public uint InstanceIdAt(Vector2Int cell, byte level = 0)
        {
            if (!IsActive || !Grid.InBounds(cell.x, cell.y)) return 0;

            int key = (cell.y * Grid.Cols + cell.x) * RoomGrid.MaxLevels;
            if (_cellOwner.TryGetValue(key + level, out uint exact)) return exact;

            for (int l = RoomGrid.MaxLevels - 1; l >= 0; l--)
                if (_cellOwner.TryGetValue(key + l, out uint any)) return any;
            return 0;
        }

        public GameObject InstanceOf(uint instanceId) =>
            _instances.TryGetValue(instanceId, out var go) ? go : null;

        // Sahiplik, izgara dolulugunun AYNI hucrelerini kullanmali — sinir kutusunun degil.
        // Ara acida duran bir duvarin kutu kosesindeki bos hucreyi sahiplenmesi, oyuncunun
        // yanindaki bosluga nisan alip o duvari silmesi demek olurdu.
        void RememberOwner(in RoomGrid.Shape s, byte level, uint id)
        {
            for (int cz = s.rect.yMin; cz < s.rect.yMax; cz++)
                for (int cx = s.rect.xMin; cx < s.rect.xMax; cx++)
                    if (Grid.Covers(s, cx, cz) && Grid.InBounds(cx, cz))
                        _cellOwner[(cz * Grid.Cols + cx) * RoomGrid.MaxLevels + level] = id;
        }

        void ForgetOwner(in RoomGrid.Shape s, byte level, uint id)
        {
            for (int cz = s.rect.yMin; cz < s.rect.yMax; cz++)
                for (int cx = s.rect.xMin; cx < s.rect.xMax; cx++)
                {
                    if (!Grid.Covers(s, cx, cz)) continue;
                    int k = (cz * Grid.Cols + cx) * RoomGrid.MaxLevels + level;
                    // Yalnizca GERCEKTEN bu yerlestirmenin sahibi oldugu hucreyi birak: ust uste
                    // binmis bozuk veride baskasinin hucresini silmeyelim.
                    if (_cellOwner.TryGetValue(k, out uint cur) && cur == id) _cellOwner.Remove(k);
                }
        }

        // ------------------------------------------------------------- persistence

        /// <summary>
        /// Writes the map to disk.
        ///
        /// AN EMPTY MAP NEVER OVERWRITES A FULL ONE unless asked to. Autosave fires on every
        /// change and cannot tell "the player cleared the arena" from "this session started
        /// blank because the file failed to load a moment ago" — and the second one silently
        /// destroys everything the file held. A refusal costs one warning; the alternative cost
        /// a room's worth of walls. Pass <paramref name="allowEmpty"/> when clearing is what the
        /// player actually asked for.
        /// </summary>
        public bool Save(string mapName = WorkingMapName, bool allowEmpty = false)
        {
            if (!IsActive) return false;

            if (!allowEmpty && Layout.Count == 0)
            {
                var onDisk = MapLayout.Load(mapName);
                if (onDisk != null && onDisk.Count > 0)
                {
                    Debug.LogWarning($"[Constructor] BOS harita kaydedilmedi: '{mapName}' diskte " +
                                     $"{onDisk.Count} prop tutuyor. Oturum bos basladiysa dosya " +
                                     "korunur; gercekten temizlemek istiyorsan Save(allowEmpty: true).");
                    return false;
                }
            }

            Layout.libraryVersion = Library.contentVersion;
            return Layout.Save(mapName);
        }

        /// <summary>The current layout as JSON — what the server ships to a late joiner.</summary>
        public string CurrentJson() => IsActive ? Layout.ToJson(false) : null;

        /// <summary>
        /// Replaces everything with a layout received from the server, INCLUDING the room it was
        /// built for. A client's own scan is not used here on purpose: peers must raster the same
        /// grid from the same polygon, or cell (4,7) means a different spot on every headset.
        /// </summary>
        public bool AdoptJson(string json)
        {
            var layout = MapLayout.FromJson(json);
            if (layout == null)
            {
                Debug.LogWarning("[Constructor] Sunucudan gelen harita okunamadi.");
                return false;
            }
            Debug.Log($"[Constructor] Sunucudan harita alindi: {layout.Count} prop.");
            return Adopt(layout);
        }

        // ------------------------------------------------------------- palette source

        /// <summary>
        /// Biggest prop the palette will offer, per axis (m). In metres, not cells: a cell count
        /// would silently re-tighten every time the grid resolution changed.
        ///
        /// Raised from 1.5 m, which was cutting out the four bunkers (1.85 m and 2.53 m) even
        /// though the buildable area runs five metres past the walls in every direction and has
        /// room for them. The line now sits just above those and still keeps genuinely
        /// scenery-sized pieces out — the paintball marker line is 9.26 m, which is not
        /// something a player places, it is something a map is made of.
        ///
        /// Raised again for the weapon wall, which is 3.75 m: it is one deliberate object, not
        /// terrain, and the buildable area is roughly 15 x 14 m.
        /// </summary>
        const float MaxPlaceableMetres = 4f;

        /// <summary>
        /// Props the player may drop on the floor. Terrain-sized pieces are filtered out — a
        /// hillside prefab in a 3x4 m living room is never the right answer, and letting one
        /// into the cycle just wastes the player's time. Step 7's wheel groups these by category.
        ///
        /// THE ONE GATE. The wheel, the category list and the placer's cycle all read this, so a
        /// prop retired with <see cref="PropDef.hiddenInPalette"/> disappears from every one of
        /// them at once — and stays resolvable everywhere else, which is what keeps maps built
        /// out of it intact.
        /// </summary>
        public IReadOnlyList<PropDef> Placeable
        {
            get
            {
                if (_placeable != null) return _placeable;
                _placeable = new List<PropDef>();
                foreach (var p in Library.props)
                    if (p != null && !p.hiddenInPalette &&
                        p.snap == PropSnap.Floor && p.category != PropCategory.Ground &&
                        p.sizeMeters.x <= MaxPlaceableMetres && p.sizeMeters.y <= MaxPlaceableMetres &&
                        p.Resolve() != null)
                        _placeable.Add(p);
                return _placeable;
            }
        }

        /// <summary>
        /// Categories that actually contain something placeable, in enum order. The palette
        /// wheel draws one slice per entry — an empty slice is a slice the player can waste a
        /// selection on, so empty categories never get one.
        /// </summary>
        public IReadOnlyList<PropCategory> Categories
        {
            get
            {
                if (_categories != null) return _categories;
                _categories = new List<PropCategory>();
                foreach (PropCategory c in System.Enum.GetValues(typeof(PropCategory)))
                    if (PlaceableIn(c).Count > 0) _categories.Add(c);
                return _categories;
            }
        }

        /// <summary>Placeable props in one category (cached; the list is rebuilt with the library).</summary>
        public IReadOnlyList<PropDef> PlaceableIn(PropCategory category)
        {
            if (_byCategory == null) _byCategory = new Dictionary<PropCategory, List<PropDef>>();
            if (_byCategory.TryGetValue(category, out var list)) return list;

            list = new List<PropDef>();
            foreach (var p in Placeable)
                if (p.category == category) list.Add(p);
            _byCategory[category] = list;
            return list;
        }

        List<PropCategory> _categories;
        Dictionary<PropCategory, List<PropDef>> _byCategory;

        /// <summary>Kutuphane editorde degistiyse (menu 25) filtreleri yeniden kur.</summary>
        public void InvalidatePlaceable()
        {
            _placeable = null;
            _categories = null;
            _byCategory = null;
        }
    }
}
