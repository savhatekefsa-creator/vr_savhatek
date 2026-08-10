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
        /// <summary>
        /// Hicbir isim verilmemisken kullanilan harita adi. Menuler gelene kadar (Serit 1) tek
        /// harita buydu; artik yalnizca BASLANGIC degeri.
        /// </summary>
        public const string DefaultMapName = "Current";

        /// <summary>
        /// Uzerinde calisilan haritanin adi — ayni zamanda dosya adi.
        ///
        /// SABIT DEGIL, DEGISKEN: eskiden tek bir "Current" haritasi vardi ve kaydetme de yukleme
        /// de onu kastediyordu. Serit 1/2 akisi (yeni harita, mevcut harita, yeniden adlandir)
        /// ayni anda TEK haritayi acik tutar ama hangisinin acik oldugu degisir.
        ///
        /// BOS OLABILIR: "yeni harita" isimsiz baslar, ismi ilk kayitta sorulur. Isimsizken
        /// <see cref="Save"/> yazmayi reddeder — otomatik kayit adsiz bir dosya uretmesin.
        /// </summary>
        public string CurrentMapName { get; private set; } = DefaultMapName;

        /// <summary>
        /// Son kayittan bu yana degisiklik var mi? "Kaydet?" karari bunu sorar.
        ///
        /// OTORITEDEN BAGIMSIZ, <see cref="_dirty"/>'den ayri: soru GOZLUKTE soruluyor ama
        /// dosyayi PC yaziyor. Otomatik kaydi baglayan bayrak istemcide hic set edilmedigi icin
        /// (bkz. <see cref="MarkDirty"/>) ona bakilsaydi gozlukte "kaydedilecek bir sey yok"
        /// denip butun oturum sessizce atilirdi.
        /// </summary>
        public bool HasUnsavedChanges => _touched;

        bool _touched;

        /// <summary>
        /// Otomatik kayit askida mi?
        ///
        /// Yaratici akista kaydetme KARARI oyuncunun (Serit 1: "Kaydet?"), ve "degisiklikleri
        /// at" secenegi ancak hicbir sey yazilmamissa bir anlam tasir — otomatik kayit acik
        /// kalsaydi "at" demek daima gec kalmis olurdu. Bedeli acik: editordeyken cokme olursa
        /// o oturumun emegi gider. Karsiliginda "at" gercekten atiyor.
        /// </summary>
        public static bool AutoSaveSuspended { get; set; }

        /// <summary>Kaydedildi say — istemcide sunucunun onayi gelince cagriliyor.</summary>
        public void ClearUnsaved() => _touched = false;

        public static ConstructorSession Instance { get; private set; }

        public MapLayout Layout { get; private set; }
        public RoomGrid Grid { get; private set; }
        public PropLibrary Library => PropLibrary.Instance;
        public Transform Root { get; private set; }

        public bool IsActive => Layout != null && Grid != null;
        public int PlacedCount => Layout != null ? Layout.Count : 0;
        public int FreePlacedCount => Layout != null ? Layout.FreeCount : 0;

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
        ///
        /// GOZLUK BU KURALIN DISINDA: haritalar PC'de yasiyor, gozlukte hicbir zaman degil.
        /// "Baglanti yoksa sahip benim" kurali gozlukte SESSIZ VERI KAYBI uretiyordu: ekranda
        /// sunucudan gelmis liste duruyor, ama baglanti dustugu anda silme/kaydetme gozlugun
        /// kendi bos klasorune uygulaniyor. Silinen harita PC'de oldugu gibi kaliyor (sonraki
        /// acilista "geri gelmis" gorunuyor), yeni yapilan harita ise hicbir yere ulasmiyor.
        /// Sahiplik yerine islem BASARISIZ olmali — cagiran taraf sebebi soyleyebilsin.
        ///
        /// Editor bu kuralin disinda tutuldu: PC'de sunucu baslatmadan harita duzenlemek
        /// gelistirmenin normal yolu.
        /// </summary>
        public static bool IsMapAuthority
        {
            get
            {
                var net = Unity.Netcode.NetworkManager.Singleton;
                if (net != null && net.IsServer) return true;
#if UNITY_ANDROID && !UNITY_EDITOR
                return false;
#else
                return net == null || !net.IsClient;
#endif
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
            // Degisiklik HER peer'da isaretlenir (bkz. HasUnsavedChanges); otorite kontrolu
            // yalnizca OTOMATIK kaydi baglar.
            _touched = true;

            if (!IsMapAuthority) return;
            _dirty = true;
            _saveAt = Time.unscaledTime + AutoSaveDelay;
        }

        // MAC HARITASI BURADA SECILMIYOR. Kurayi sunucu, ilk oyuncu maca girerken cekiyor
        // (ConstructorSync.ServerPickMatchMap). Bir zamanlar acilista secilirdi ve sunucu
        // saatlerce bos beklerken secilen harita, tasarimcinin havuzda yaptigi degisikliklere
        // kor kalirdi.

        void Update()
        {
            HookServerStopped();
            if (!_dirty || AutoSaveSuspended || Time.unscaledTime < _saveAt) return;
            FlushPendingSave();
        }

        /// <summary>
        /// Haritayi OYNANIS icin kurar — insa modu acilmadan, oyuncudan bagimsiz.
        ///
        /// HARITA DEKOR, INSA MODU EDITOR. Bunlar ayrilana kadar kayitli harita yalnizca biri
        /// insa moduna girince sahneye geliyordu; OYUNCU modunda duvarlar da raflar da hic
        /// dogmuyordu, cunku oturumu acan tek yol editordu. Silahlar bunun en gorunur yuzuydu:
        /// raf yuvalarini <see cref="Weapons.WeaponRackRespawner.RegisterConstructorRack"/>
        /// yalnizca <see cref="Adopt"/> sirasinda kaydeder.
        ///
        /// KAYIT YOKSA HICBIR SEY YAPMAZ, ve bu bilincli: <see cref="Clear"/> harita kokunun
        /// altini siliyor, yani bos bir oturum acmak sahneye KAYDEDILMIS bir harita kopyasini
        /// yok ederdi. Kaydi olmayan makinede (Maps/ surumlenmiyor) duvarlar boylece kaybolurdu.
        /// Serbest alan yedegi insa modu icindir; oynanista kurulacak bir sey yoksa dogru
        /// davranis sahneye dokunmamaktir.
        ///
        /// ISTEMCI KENDI DISKINDEN KURMAZ: sunucu baglanti aninda haritayi zaten yolluyor
        /// (<see cref="ConstructorSync"/>). <see cref="IsMapAuthority"/> baglanmadan ONCE de
        /// true oldugu icin agsiz oynayan kendi haritasini kurar; sonradan bir sunucuya
        /// baglanirsa gelen harita <see cref="AdoptJson"/> ile bunun yerine gecer.
        ///
        /// SECIM DEGIL, KURULUM. Hangi haritanin oynanacagina bu metot karar VERMEZ; adini
        /// disaridan alir. Serit 3'te secimi havuz yapacak (rastgele, ve YALNIZCA sunucuda —
        /// her gozluk kendi rastgelesini cekerse herkes baska haritaya duser). Ikisi burada
        /// birlesseydi o adimda sokulmesi gerekirdi.
        /// </summary>
        public bool BuildForPlay(string mapName)
        {
            if (IsActive) return true;
            if (!IsMapAuthority) return false;
            if (string.IsNullOrEmpty(mapName)) return false;

            CurrentMapName = mapName;
            return EnsureStarted(requireSavedMap: true);
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
        /// <param name="requireSavedMap">
        /// True ise KAYITLI harita yoksa oturum acilmaz (tarama ya da serbest alan yedegine
        /// dusulmez). Oynanis yolu bunu kullanir — bkz. <see cref="BuildForPlay"/>.
        /// </param>
        public bool EnsureStarted(bool requireSavedMap = false)
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
            var saved = MapLayout.Load(CurrentMapName);

            // BURAYA HAVUZ YEDEGI KOYULMAMALI. Bu dalda bir sure "adi tutmayan harita varsa
            // havuzdan sec" yedegi vardi; mac haritasini ConstructorSync.ServerPickMatchMap
            // cekiyor ve kurayi MatchMapChosen ile MAC BASINA BIR KEZ kilitliyor. Buraya
            // ikinci bir secici koymak o kilidi delerdi: sonradan acilan bir oturum kendi
            // kurasini ceker, ayni macin oyunculari baska haritalara duserdi.

            // Kurulacak kayitli harita yoksa SESSIZCE cik: cagiran oynanis yoluysa sahnedeki
            // her sey oldugu gibi kalmali (bkz. BuildForPlay). NotStartedReason yazilmiyor
            // — bu bir hata degil, "yapilacak is yok".
            if (requireSavedMap && saved == null) return false;

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

            // KAYITLI HARITANIN ODASI KENDI ICINDE. Tarama VARSA bile onu ezmemeli: izgaranin
            // kokeni oda poligonundan turuyor, baska bir poligon koymak kokeni kaydirir ve
            // haritadaki BUTUN yerlestirmeler yanlis yere duser.
            //
            // Bu kural yukaridaki "tarama yok" dalinda zaten yaziliydi ve OpenExisting de ayni
            // sekilde davraniyordu; eksik olan tek yol buydu. Tam da mekan degistirirken
            // isirirdi: yeni odada eski odanin taramasi diskte durur, kayitli bir harita
            // acilir ve proplar sessizce kayardi.
            bool ok = saved != null
                ? Adopt(saved, saved.HasRoom ? null : plan)
                : StartNew(plan);
            if (!ok) NotStartedReason = "Oda plani gecersiz: izgara kurulamadi.";
            return ok;
        }

        /// <summary>Why <see cref="EnsureStarted"/> last returned false (shown to the player).</summary>
        public string NotStartedReason { get; private set; } = "";

        // --------------------------------------------- harita acma / kaydetme (Serit 1 akisi)

        /// <summary>
        /// Bos bir harita acar — "yeni harita" akisinin giris noktasi.
        ///
        /// ZEMIN HEP SERBEST ALAN, oda taramasi degil. Sifirdan tasarim fiziksel odadan bagimsiz
        /// demek; taramaya baglamak haritayi TARAYANIN odasina baglardi ve harita baska bir
        /// gozlukte acilinca izgara kokeni kayardi. Serbest alanin poligonu her yerde ayni.
        ///
        /// ISIM ZORUNLU DEGIL: isimsiz acilan harita ilk kayitta isimlendirilir.
        /// </summary>
        public bool OpenNew(string mapName = null)
        {
            // Once acik haritanin bekleyen kaydini yaz: harita degistirmek onceki emegi
            // silmenin yolu olmamali.
            FlushPendingSave();

            CurrentMapName = mapName;
            bool ok = StartNew(RoomPlan.FreeSpace());
            _dirty = false;   // yeni ve bos — yazilacak bir sey yok
            _touched = false;
            return ok;
        }

        /// <summary>
        /// Kayitli bir haritayi acar — "mevcut harita" akisi.
        ///
        /// HARITANIN KENDI ODASI kullanilir, guncel tarama DEGIL: harita hangi poligon uzerine
        /// oruldiyse hucre koordinatlari ona gore anlamli. Baska bir odayla yeniden izgaralamak
        /// kokeni kaydirir ve butun yerlestirmeler yanlis yere duser.
        /// </summary>
        public bool OpenExisting(string mapName)
        {
            var saved = MapLayout.Load(mapName);
            if (saved == null)
            {
                NotStartedReason = $"'{mapName}' bulunamadi.";
                return false;
            }

            FlushPendingSave();
            CurrentMapName = mapName;

            // Odasi olmayan kayit (elle bozulmus ya da cok eski) bos zemine oturur —
            // yerlestirmeleri toptan reddetmektense zeminini tamamlamak daha az kayipli.
            bool ok = Adopt(saved, saved.HasRoom ? null : RoomPlan.FreeSpace());
            if (ok) { _dirty = false; _touched = false; }
            else NotStartedReason = $"'{mapName}' acilamadi: izgara kurulamadi.";
            return ok;
        }

        /// <summary>
        /// Haritayi VERILEN ISIMLE kaydeder ve bundan sonra o isim uzerinde calisir. Isimsiz
        /// haritanin ilk kaydi da "farkli kaydet" de ayni kapidan gecer.
        /// </summary>
        public bool SaveAs(string mapName, bool allowEmpty = false)
        {
            if (string.IsNullOrEmpty(mapName)) return false;
            if (!Save(mapName, allowEmpty)) return false;

            CurrentMapName = mapName;
            _dirty = false;
            return true;
        }

        /// <summary>
        /// Acik haritanin ADI degisti (katalogdan yeniden adlandirma). Icerik ayni kalir;
        /// degisen tek sey bundan sonraki kayitlarin hangi dosyaya gidecegi.
        /// </summary>
        public void NoteRenamed(string newMapName)
        {
            if (string.IsNullOrEmpty(newMapName)) return;
            CurrentMapName = newMapName;
            if (Layout != null) Layout.name = newMapName;
        }

        /// <summary>
        /// Acik harita SILINDI. Adi dusuruluyor: yoksa bir sonraki otomatik kayit dosyayi geri
        /// diriltir ve "sildim ama duruyor" olur. Sahnedeki yapi duruyor — silinen dosya, yasayan
        /// oturum degil.
        /// </summary>
        public void NoteDeleted() => CurrentMapName = null;

        /// <summary>Begins an empty map on the given room.</summary>
        public bool StartNew(RoomPlan plan)
        {
            var grid = RoomGrid.FromPlan(plan);
            if (grid == null) return false;

            var layout = new MapLayout
            {
                name = CurrentMapName,
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

            // Serbest katman: izgara dolulugu YOK (bilincli, bkz. FreePlacedProp) — yalnizca
            // sahne kurulumu + kimlik kaydi. Raf kaydi izgara yoluyla ayni: serbest konmus
            // bir raf da silah dogurmali.
            foreach (var fp in Layout.freeProps)
            {
                var def = Library.ById(fp.propId);
                if (def == null) continue;
                var go = MapBuilder.SpawnFree(fp, def, Root);
                if (go != null)
                {
                    _instances[fp.instanceId] = go;
                    Weapons.WeaponRackRespawner.RegisterConstructorRack(fp.instanceId, go);
                }
            }

            // HARITANIN KENDI TAG YERLESIMI. Tek gecis noktasi burasi: OpenExisting (yaratici
            // modda harita acma) da AdoptJson (sunucudan gelen harita) da Adopt'a dusuyor,
            // yani "harita degisti -> tag'ler de degisti" tek yerde bagli.
            //
            // Bos birakan haritalar bozulmuyor: ApplyMapLayout bos gelince onyukleme
            // yerlesimine donuyor.
            if (AprilTagCalibration.Instance != null)
                AprilTagCalibration.Instance.ApplyMapLayout(Layout.tags);

            Debug.Log($"[Constructor] Oturum acildi: '{Layout.name}' — {applied} yerlestirme + " +
                      $"{Layout.FreeCount} serbest, " +
                      $"{(Layout.tags != null ? Layout.tags.Length : 0)} tag, " +
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
            byte heightPct, bool fromUndo, uint preferredId = 0)
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
                ApplyPlace(def.id, minCell, level, rot, ResolveInstanceId(preferredId), scalePct, heightPct);
                return true;
            }

            if (net.IsServer)
            {
                // Sunucu kendi karari: kimligi uret ve HERKESE yayinla. Uygulama tek yoldan,
                // RPC alicisindan gecer (sunucu da alicilardan biri) — iki ayri uygulama yolu
                // olsaydi sunucu ile istemciler zamanla ayrisirdi.
                return ConstructorSync.ServerBroadcastPlace(def, minCell, level, rot,
                    ResolveInstanceId(preferredId), scalePct, heightPct);
            }

            return ConstructorSync.ClientRequestPlace(def, minCell, level, rot, scalePct, heightPct, preferredId);
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

        /// <summary>
        /// Uses a REQUESTED id when it is still free, otherwise mints a new one.
        ///
        /// This is what lets undo put a prop back with the SAME identity it had. Without it the
        /// resurrected prop got a fresh id, and every older undo entry naming the old one
        /// (especially free-layer moves) was silently skipped as "someone else deleted it" — so
        /// undoing a convert-then-nudge sequence stopped part-way through and the chain could
        /// never reach the beginning.
        ///
        /// Still checked rather than trusted: ids arrive from clients, and a stale request must
        /// never be able to collide with a live prop. Both layers share the id space, so both
        /// are consulted.
        /// </summary>
        public uint ResolveInstanceId(uint preferred)
        {
            if (!IsActive) return 0;
            if (preferred != 0 && Layout.Find(preferred) == null && Layout.FindFree(preferred) == null)
                return preferred;
            return MintInstanceId();
        }

        /// <summary>
        /// Sahnedeki bir objeden yerlesim kimligine geri gider — collider cocuk objede
        /// olabilecegi icin koke dogru tirmanir. PC ince-ayar editorunun tiklama secimi icin;
        /// tiklama sikliginda calisir, sozluk taramasinin maliyeti dert degil.
        /// </summary>
        public uint InstanceIdOf(Transform t)
        {
            if (!IsActive || t == null) return 0;
            while (t != null && t != Root)
            {
                foreach (var kv in _instances)
                    if (kv.Value != null && kv.Value.transform == t) return kv.Key;
                t = t.parent;
            }
            return 0;
        }


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
            int i = _pending.FindIndex(x => x.place && x.free == null && x.prop.propId == propId &&
                                            x.prop.cellX == minCell.x && x.prop.cellZ == minCell.y &&
                                            x.prop.rot == rot && x.prop.scalePct == scalePct && x.prop.heightPct == heightPct);
            if (i >= 0)
            {
                bool fromUndo = _pending[i].fromUndo;
                _pending.RemoveAt(i);
                if (!fromUndo) PushUndo(new UndoEntry { kind = UndoKind.Place, instanceId = instanceId });
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

            int i = _pending.FindIndex(x => !x.place && x.free == null && x.prop.instanceId == instanceId);
            if (i >= 0)
            {
                var p = _pending[i];
                _pending.RemoveAt(i);
                if (!p.fromUndo)
                    PushUndo(new UndoEntry
                    {
                        kind = UndoKind.Remove,
                        // Kimlik de saklanir: geri alinca prop AYNI kimlikle geri gelmeli,
                        // yoksa ondan onceki kayitlar sahipsiz kalip atlanir.
                        instanceId = instanceId,
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

        // ------------------------------------------------- serbest katman (tam transform)

        // Surukleme yasam dongusu. Fotograf ilk dokunusta cekilir, commit'te geri-al yigitina
        // yazilir. Yankilar (kendi RPC'mizin bize donusu) surukleme bitene kadar yoksayilir —
        // yoksa agin 15 Hz'lik eski konumlari taze yerel konumun ustune biner, prop elde
        // titrerdi. Ayni propu iki kisinin ayni anda suruklemesi catisir; son commit kazanir.
        FreePlacedProp _dragSnapshot;
        bool _echoMuted;
        uint _echoMutedId;
        float _nextFreeMoveNetAt;

        /// <summary>Suruklerken agin gordugu gonderim tavani (adet/sn). Yerel gorunum her kare.</summary>
        public const float FreeMoveSendHz = 15f;

        /// <summary>
        /// Serbest yerlestirme istegi — <see cref="TryPlace"/>'in ikizi, ayni yol ayrimi:
        /// offline hemen, sunucuda yayin, istemcide istek. CanPlace YOK: serbest katman hucre
        /// dolulugunun disinda (bkz. <see cref="FreePlacedProp"/>).
        /// </summary>
        public bool TryPlaceFree(PropDef def, Vector3 position, Vector3 rotationEuler, Vector3 scale) =>
            TryPlaceFree(def, position, rotationEuler, scale, false);

        bool TryPlaceFree(PropDef def, Vector3 position, Vector3 rotationEuler, Vector3 scale,
            bool fromUndo, uint preferredId = 0)
        {
            if (!IsActive || def == null || def.Resolve() == null) return false;

            // Harita hacminin disina kacan prop bulunamaz ve dosyada oyle kalir (bkz.
            // RoomGrid.ClampToBounds). Sinirlama ISTEK yolunda: apply'a hep kirpilmis deger
            // gelir, yani her este ayni sayi uygulanir.
            position = Grid.ClampToBounds(position);

            AddPending(new Pending
            {
                place = true,
                fromUndo = fromUndo,
                free = new FreePlacedProp
                {
                    propId = def.id, position = position, rotationEuler = rotationEuler, scale = scale,
                },
            });

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);

            if (!online)
                return ApplyPlaceFree(def.id, position, rotationEuler, scale,
                    ResolveInstanceId(preferredId)) != null;
            if (net.IsServer)
                return ConstructorSync.ServerBroadcastPlaceFree(def, position, rotationEuler, scale,
                    ResolveInstanceId(preferredId));
            return ConstructorSync.ClientRequestPlaceFree(def, position, rotationEuler, scale, preferredId);
        }

        public bool TryRemoveFree(uint instanceId) => TryRemoveFree(instanceId, false);

        bool TryRemoveFree(uint instanceId, bool fromUndo)
        {
            if (!IsActive || instanceId == 0) return false;

            var doomed = Layout.FindFree(instanceId);
            if (doomed == null) return false;

            // Silinmeden ONCE fotografla — geri almak transformun tamamini ister.
            AddPending(new Pending
            {
                place = false,
                fromUndo = fromUndo,
                free = new FreePlacedProp
                {
                    propId = doomed.propId,
                    position = doomed.position,
                    rotationEuler = doomed.rotationEuler,
                    scale = doomed.scale,
                    instanceId = instanceId,
                },
            });

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);

            if (!online) return ApplyRemoveFree(instanceId);
            if (net.IsServer) return ConstructorSync.ServerBroadcastRemoveFree(instanceId);
            return ConstructorSync.ClientRequestRemoveFree(instanceId);
        }

        /// <summary>
        /// Serbest propu tasir/dondurur/olcekler. <paramref name="commit"/> false iken SURUKLEME
        /// karesi: yerel gorunum aninda guncellenir, ag <see cref="FreeMoveSendHz"/>'e kirpilir,
        /// geri-al yigitina yazilmaz. true iken jest biter: kesin deger HER ZAMAN gider ve ilk
        /// dokunustaki fotograf geri-al yigitina yazilir.
        ///
        /// YEREL TAHMIN BILEREK VAR (yerlestirmedeki "tahmin yok" kuralinin tersi): surukleyen
        /// el kendi karesini ag gecikmesiyle izleseydi ince ayar imkansizlasirdi. Sunucunun
        /// reddedebilecegi tek sey "boyle bir prop yok" — o da yerelde zaten denetleniyor.
        /// </summary>
        public bool TryMoveFree(uint instanceId, Vector3 position, Vector3 rotationEuler,
            Vector3 scale, bool commit) =>
            TryMoveFree(instanceId, position, rotationEuler, scale, commit, false);

        bool TryMoveFree(uint instanceId, Vector3 position, Vector3 rotationEuler, Vector3 scale,
            bool commit, bool fromUndo)
        {
            if (!IsActive || instanceId == 0) return false;

            var f = Layout.FindFree(instanceId);
            if (f == null) return false;

            position = Grid.ClampToBounds(position);

            if (!fromUndo)
            {
                // Baska propa gecis onceki jesti kendiliginden bitirir — yigit yarim kalmasin.
                if (_dragSnapshot != null && _dragSnapshot.instanceId != instanceId)
                    CommitDragSnapshot();
                if (_dragSnapshot == null)
                    _dragSnapshot = new FreePlacedProp
                    {
                        propId = f.propId, position = f.position,
                        rotationEuler = f.rotationEuler, scale = f.scale, instanceId = instanceId,
                    };
                _echoMuted = true;
                _echoMutedId = instanceId;
            }

            ApplyFreeTransform(f, position, rotationEuler, scale);

            if (commit)
            {
                if (!fromUndo) CommitDragSnapshot();
                MarkDirty();
                Changed?.Invoke();
            }

            var net = Unity.Netcode.NetworkManager.Singleton;
            bool online = net != null && (net.IsServer || net.IsConnectedClient);
            if (!online)
            {
                _echoMuted = false;   // yanki gelmeyecek; bayrak asili kalmasin
                return true;
            }

            // Ag kirpma: surukleme kareleri tavana tabi, commit her zaman gider.
            if (!commit && Time.time < _nextFreeMoveNetAt) return true;
            _nextFreeMoveNetAt = Time.time + 1f / FreeMoveSendHz;

            if (net.IsServer)
                return ConstructorSync.ServerBroadcastMoveFree(instanceId, position, rotationEuler,
                    f.scale, commit);
            return ConstructorSync.ClientRequestMoveFree(instanceId, position, rotationEuler,
                f.scale, commit);
        }

        void CommitDragSnapshot()
        {
            if (_dragSnapshot == null) return;
            PushUndo(new UndoEntry
            {
                kind = UndoKind.FreeMove,
                instanceId = _dragSnapshot.instanceId,
                free = _dragSnapshot,
            });
            _dragSnapshot = null;
        }

        /// <summary>Veri + sahne tek elden — ikisi ayri guncellenseydi zamanla ayrisirdi.</summary>
        void ApplyFreeTransform(FreePlacedProp f, Vector3 position, Vector3 rotationEuler, Vector3 scale)
        {
            f.position = position;
            f.rotationEuler = rotationEuler;
            f.scale = scale == Vector3.zero ? Vector3.one : scale;

            if (_instances.TryGetValue(f.instanceId, out var go) && go != null)
            {
                go.transform.localPosition = f.position;
                go.transform.localRotation = f.Rotation;
                go.transform.localScale = f.scale;
            }
        }

        // ---- serbest apply: bu esin durumu TEK yoldan degisir (izgara ikizleriyle ayni kural) ----

        public FreePlacedProp ApplyPlaceFree(string propId, Vector3 position, Vector3 rotationEuler,
            Vector3 scale, uint instanceId)
        {
            if (!IsActive) return null;

            var def = Library.ById(propId);
            if (def == null)
            {
                Debug.LogWarning($"[Constructor] Kutuphanede olmayan serbest prop uygulanamadi: '{propId}'.");
                return null;
            }
            if (Layout.FindFree(instanceId) != null) return null;   // yinelenen yayin

            var placed = Layout.AddFreeWithId(def.id, position, rotationEuler, scale, instanceId);

            var go = MapBuilder.SpawnFree(placed, def, Root);
            if (go != null)
            {
                _instances[placed.instanceId] = go;
                Weapons.WeaponRackRespawner.RegisterConstructorRack(placed.instanceId, go);
            }

            // Bizim istegimiz miydi? Kimligi sunucu verdigi icin eslesme icerikle yapilir;
            // degerler agdan bit-bit ayni doner, epsilon guvenlik payi.
            int i = _pending.FindIndex(x => x.place && x.free != null && x.free.propId == propId &&
                                            (x.free.position - position).sqrMagnitude < 1e-6f);
            if (i >= 0)
            {
                bool fromUndo = _pending[i].fromUndo;
                _pending.RemoveAt(i);
                if (!fromUndo)
                    PushUndo(new UndoEntry { kind = UndoKind.FreePlace, instanceId = instanceId });
            }

            MarkDirty();
            Changed?.Invoke();
            return placed;
        }

        public bool ApplyRemoveFree(uint instanceId)
        {
            if (!IsActive || instanceId == 0) return false;

            var doomed = Layout.FindFree(instanceId);
            if (doomed == null) return false;

            Weapons.WeaponRackRespawner.UnregisterConstructorRack(instanceId);

            if (_instances.TryGetValue(instanceId, out var go))
            {
                if (go != null) Destroy(go);
                _instances.Remove(instanceId);
            }

            Layout.RemoveFree(instanceId);

            int i = _pending.FindIndex(x => !x.place && x.free != null && x.free.instanceId == instanceId);
            if (i >= 0)
            {
                var p = _pending[i];
                _pending.RemoveAt(i);
                if (!p.fromUndo)
                    PushUndo(new UndoEntry
                    {
                        kind = UndoKind.FreeRemove,
                        instanceId = instanceId,
                        free = p.free,
                    });
            }

            MarkDirty();
            Changed?.Invoke();
            return true;
        }

        public bool ApplyMoveFree(uint instanceId, Vector3 position, Vector3 rotationEuler,
            Vector3 scale, bool commit)
        {
            if (!IsActive || instanceId == 0) return false;

            // Kendi suruklememizin yankisi: eski surukleme kareleri yoksayilir (yerel daha
            // guncel), commit yankisi susturmayi kapatir — deger zaten esit, uygulamak zararsiz.
            if (_echoMuted && _echoMutedId == instanceId)
            {
                if (!commit) return true;
                _echoMuted = false;
            }

            var f = Layout.FindFree(instanceId);
            if (f == null) return false;

            ApplyFreeTransform(f, position, rotationEuler, scale);

            if (commit)
            {
                MarkDirty();
                Changed?.Invoke();
            }
            return true;
        }

        // ------------------------------------------------------------- undo

        enum UndoKind : byte
        {
            Place,       // izgara: biz koyduk -> geri al = sil
            Remove,      // izgara: biz sildik -> geri al = tekrar koy
            FreePlace,   // serbest: koyduk -> geri al = sil
            FreeRemove,  // serbest: sildik -> geri al = tekrar koy (fotograftan)
            FreeMove,    // serbest: tasidik -> geri al = eski transforma don (fotograftan)
        }

        struct UndoEntry
        {
            public UndoKind kind;
            public uint instanceId;       // Place / FreePlace / FreeMove icin
            public string propId;         // Remove icin -> geri al = tekrar koy
            public int cellX, cellZ;
            public byte level;
            public byte rot;
            public byte scalePct;
            public byte heightPct;
            public FreePlacedProp free;   // FreeRemove/FreeMove: geri donulecek anin fotografi
        }

        struct Pending
        {
            public bool place;
            public bool fromUndo;
            public PlacedProp prop;       // izgara istekleri
            public FreePlacedProp free;   // null degilse SERBEST istek (place alani koy/sil ayrimi)
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

                switch (e.kind)
                {
                    case UndoKind.Place:
                        if (Layout.Find(e.instanceId) == null) continue;   // baskasi zaten silmis
                        return TryRemove(e.instanceId, true);

                    case UndoKind.FreePlace:
                        if (Layout.FindFree(e.instanceId) == null) continue;
                        return TryRemoveFree(e.instanceId, true);

                    case UndoKind.FreeRemove:
                    {
                        var fdef = e.free != null ? Library.ById(e.free.propId) : null;
                        if (fdef == null) continue;
                        return TryPlaceFree(fdef, e.free.position, e.free.rotationEuler,
                            e.free.scale, true, e.free.instanceId);
                    }

                    case UndoKind.FreeMove:
                        if (e.free == null || Layout.FindFree(e.instanceId) == null) continue;
                        return TryMoveFree(e.instanceId, e.free.position, e.free.rotationEuler,
                            e.free.scale, true, true);

                    default:   // UndoKind.Remove: geri al = tekrar koy
                    {
                        var def = Library.ById(e.propId);
                        if (def == null) continue;
                        var cell = new Vector2Int(e.cellX, e.cellZ);
                        byte sc = e.scalePct == 0 ? (byte)100 : e.scalePct;
                        byte hp = e.heightPct == 0 ? (byte)100 : e.heightPct;
                        if (!CanPlace(def, cell, e.rot, sc, e.level, hp)) continue;   // yeri kapilmis
                        return TryPlace(def, cell, e.level, e.rot, sc, hp, true, e.instanceId);
                    }
                }
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
        public bool Save(string mapName = null, bool allowEmpty = false)
        {
            if (!IsActive) return false;

            if (string.IsNullOrEmpty(mapName)) mapName = CurrentMapName;

            // ISIMSIZ HARITA YAZILMAZ. "Yeni harita" isimsiz baslar ve ismi ilk kayitta sorulur
            // (Serit 1). Buraya isimsiz dusmek, otomatik kaydin arkada "Map.json" gibi kimsenin
            // istemedigi bir dosya uretmesi demek olurdu.
            if (string.IsNullOrEmpty(mapName))
            {
                Debug.LogWarning("[Constructor] Harita adsiz — kaydedilmedi. Once isim verilmeli.");
                return false;
            }

            if (!allowEmpty && Layout.Count == 0 && Layout.FreeCount == 0)
            {
                var onDisk = MapLayout.Load(mapName);
                if (onDisk != null && (onDisk.Count > 0 || onDisk.FreeCount > 0))
                {
                    Debug.LogWarning($"[Constructor] BOS harita kaydedilmedi: '{mapName}' diskte " +
                                     $"{onDisk.Count} prop tutuyor. Oturum bos basladiysa dosya " +
                                     "korunur; gercekten temizlemek istiyorsan Save(allowEmpty: true).");
                    return false;
                }
            }

            Layout.libraryVersion = Library.contentVersion;
            bool yazildi = Layout.Save(mapName);
            if (yazildi) _touched = false;
            return yazildi;
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

            // HANGI HARITANIN ACIK OLDUGU DA SUNUCUDAN GELIR. Istemci dosyalari gormuyor;
            // "uzerine mi yazayim, farkli mi kaydedeyim" sorusu bu ada dayaniyor ve eski ad
            // kalirsa gozluk yanlis dosyayi hedefler. Isimsiz harita bos ad tasir — dogru.
            CurrentMapName = layout.name;

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
