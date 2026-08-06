using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Network transport for the constructor. Sits on the player prefab next to
    /// <see cref="RoomScanSync"/> and follows the same shape: clients ask, the PC decides.
    ///
    /// WHAT TRAVELS: placement OPERATIONS (~14 bytes each), never GameObjects. Spawning a
    /// NetworkObject per prop would put 250 networked transforms in a room where nothing moves;
    /// instead every peer builds the same scenery locally from the same layout, and the wire
    /// only carries "prop 7 went to cell (4,9)".
    ///
    /// ONE APPLY PATH: the server validates and then broadcasts to EVERYONE INCLUDING ITSELF,
    /// so the server reaches its own state through the exact code clients do. A server that
    /// applied directly and only told the others would be a second implementation, and the two
    /// would drift.
    /// </summary>
    public class ConstructorSync : NetworkBehaviour
    {
        // RoomScanSync ile ayni: guvenilir kanalin tasima yuku sinirli, buyuk harita parcalanir.
        const int ChunkSize = 3000;
        const int MaxChunks = 512;

        static readonly List<ConstructorSync> Spawned = new List<ConstructorSync>();

        /// <summary>
        /// Senkronunu BITIRMIS istemciler (sunucu tarafi). <c>NetworkClient.IsConnected</c>
        /// Netcode'un icinde internal, yani disaridan okunamiyor; <c>OnClientConnectedCallback</c>
        /// ise public ve tam senkron tamamlaninca atiyor — "artik RPC gonderilebilir"in kesin
        /// isareti bu. Bkz. <see cref="SendLayoutToOwner"/>.
        /// </summary>
        static readonly HashSet<ulong> ReadyClients = new HashSet<ulong>();
        static bool _hookedReady;

        static void HookClientReady(NetworkManager net)
        {
            if (_hookedReady || net == null) return;
            _hookedReady = true;
            net.OnClientConnectedCallback += id => ReadyClients.Add(id);
            net.OnClientDisconnectCallback += id => ReadyClients.Remove(id);
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir: eski oturumdan kalan
        // "hazir" kimlikler ikinci Play'de haritanin ONAY ORTASINDA gonderilmesine geri
        // donerdi — yani duzeltilen hatanin ta kendisine.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ReadyClients.Clear();
            _hookedReady = false;
            Spawned.Clear();
            MatchMapChosen = false;
        }

        static ConstructorSession Session => ConstructorSession.Instance;

        byte[][] _rxChunks;
        int _rxReceived;

        public override void OnNetworkSpawn()
        {
            Spawned.Add(this);

            // Sunucu, YENI katilan oyuncuyu karsilar: gerekiyorsa harita sectirir, degilse
            // devam eden macin haritasini yollar. Bu obje o oyuncunun objesi oldugu icin
            // "katilim" tam da burasi — ama her sey istemci senkronunu bitirene kadar bekler
            // (bkz. JoinFlow).
            if (IsServer)
            {
                HookClientReady(NetworkManager);
                StartCoroutine(JoinFlow());
            }
        }

        /// <summary>
        /// Client side of "I want to build": asks the server for the map (and the room inside
        /// it). Needed because the spawn-time push happens once and can find the server's
        /// session not yet started — the PC only opens its session on demand, and in the
        /// headset-builds-it flow the PC never enters build mode at all.
        /// </summary>
        public static bool ClientRequestLayout()
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            PendingMessage = null;   // eski oturumdan kalan mesaji tasima
            sync.RequestLayoutServerRpc();
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestLayoutServerRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;

            // Sunucunun oturumu henuz acilmamis olabilir; oda plani PC'de oldugu icin acabilir.
            if (Session != null && !Session.IsActive && !Session.EnsureStarted())
            {
                NoLayoutOwnerRpc(Session.NotStartedReason);
                return;
            }
            StartCoroutine(SendLayoutToOwner());
        }

        [Rpc(SendTo.Owner)]
        void NoLayoutOwnerRpc(string reason)
        {
            Debug.LogWarning("[ConstructorSync] Sunucuda harita yok: " + reason);
            PendingMessage = "SUNUCUDA HARITA YOK\n\n" + reason;

            // PendingMessage'i yalnizca INSA MODU gosteriyor (placer, moda girmeye
            // calisirken okuyor). Oyuncu modunda kimse okumaz ve oyuncu bos bir dunyada
            // sebepsiz kalirdi — mesaji orada kendimiz gosteriyoruz.
            if (!AppMode.IsPlayer) return;
            StartCoroutine(ShowNotice("HARİTA YOK\n\n" + reason, 8f));
        }

        IEnumerator ShowNotice(string text, float seconds)
        {
            var panel = UI.HeadFollowPanel.Create("Map Notice", text, Color.white);
            yield return new WaitForSeconds(seconds);
            if (panel != null) Destroy(panel.gameObject);
        }

        /// <summary>Last thing the server said about why building is not possible yet.</summary>
        public static string PendingMessage { get; set; }

        public override void OnNetworkDespawn()
        {
            Spawned.Remove(this);

            // SON OYUNCU CIKTI -> MAC BITTI. Karar dusuyor ki sunucuya sonra gelen oyuncuya
            // harita yeniden sorulsun; yoksa bombos bir sunucuda onceki macin haritasi
            // dayatilirdi.
            //
            // Sayim Spawned uzerinden: bu liste yalnizca GERCEK oyuncularin borularini tutuyor
            // (adanmis sunucunun kendi oyuncu objesi yok) ve kendimizi yukarida cikardik. Netcode'un
            // baglanti listesine bakmak, kopma anindaki sirasina bagimli olurdu.
            if (IsServer && Spawned.Count == 0) ClearMatchMap();
        }

        /// <summary>
        /// Macin harita karari sifirlanir. HARITA SAHNEDEN KALDIRILMAZ: bir sonraki oyuncu
        /// zaten secim yapacak ve secimi haritayi degistirecek. Kaldirmak, tasarimcinin
        /// duzenledigi ve HENUZ KAYDEDILMEMIS oturumu da goturme riski tasiyordu.
        /// </summary>
        static void ClearMatchMap()
        {
            if (!MatchMapChosen) return;
            MatchMapChosen = false;
            Debug.Log("[ConstructorSync] Sunucuda oyuncu kalmadi — mac haritasi karari sifirlandi; " +
                      "sonraki oyuncu yeniden secer.");
        }

        // ------------------------------------------------------------- session entry points

        /// <summary>The local player's transport, or null when this peer has no player object.</summary>
        static ConstructorSync LocalOwned()
        {
            for (int i = 0; i < Spawned.Count; i++)
                if (Spawned[i] != null && Spawned[i].IsOwner) return Spawned[i];
            return null;
        }

        /// <summary>
        /// Any live transport. The dedicated server has NO player object of its own, so when the
        /// PC itself places something it borrows a connected player's object purely as a pipe —
        /// which object carries an Rpc does not affect who receives it.
        /// </summary>
        static ConstructorSync AnySpawned()
        {
            for (int i = 0; i < Spawned.Count; i++)
                if (Spawned[i] != null && Spawned[i].IsSpawned) return Spawned[i];
            return null;
        }

        public static bool ClientRequestPlace(PropDef def, Vector2Int cell, byte level, byte rot, byte scalePct, byte heightPct)
        {
            var sync = LocalOwned();
            int index = PropLibrary.Instance.IndexOf(def != null ? def.id : null);
            if (sync == null || index < 0) return false;
            sync.PlaceServerRpc((ushort)index, cell.x, cell.y, level, rot, scalePct, heightPct);
            return true;
        }

        public static bool ClientRequestRemove(uint instanceId)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.RemoveServerRpc(instanceId);
            return true;
        }

        /// <summary>
        /// Asks the SERVER to write the map to disk, and to say whether it worked.
        ///
        /// A client cannot save the map itself — the file is the server's, and a local copy
        /// would be one nobody reads and the next sync overwrites. Before this existed there was
        /// simply no way to save from inside a headset at all: the save on leaving build mode
        /// checked the same rule and quietly returned, so a whole session of building lived only
        /// in the server's memory until the server closed.
        /// </summary>
        /// <param name="mapName">
        /// Bos birakilirsa sunucu ACIK haritanin uzerine yazar. Dolu gelirse "farkli kaydet":
        /// isimlendirme ekrani (Serit 1) gozlukte doldurulur ama dosyayi yazan PC'dir, yani ad
        /// da tele binmek zorunda.
        /// </param>
        public static bool ClientRequestSave(string mapName = null)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.SaveServerRpc(mapName ?? "");
            return true;
        }

        public static bool ServerBroadcastPlace(PropDef def, Vector2Int cell, byte level, byte rot,
            uint instanceId, byte scalePct, byte heightPct)
        {
            int index = PropLibrary.Instance.IndexOf(def != null ? def.id : null);
            if (index < 0) return false;

            var sync = AnySpawned();
            if (sync == null)
            {
                // Bagli istemci yok — haber verecek kimse olmadigi icin dogrudan uygula.
                return Session != null &&
                       Session.ApplyPlace(def.id, cell, level, rot, instanceId, scalePct, heightPct) != null;
            }
            sync.ApplyPlaceRpc(index, cell.x, cell.y, level, rot, instanceId, scalePct, heightPct);
            return true;
        }

        public static bool ServerBroadcastRemove(uint instanceId)
        {
            var sync = AnySpawned();
            if (sync == null) return Session != null && Session.ApplyRemove(instanceId);
            sync.ApplyRemoveRpc(instanceId);
            return true;
        }

        // ------------------------------------------------------------- requests (client -> server)

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void PlaceServerRpc(ushort propIndex, int cellX, int cellZ, byte level, byte rot,
            byte scalePct, byte heightPct, RpcParams p = default)
        {
            // Yalnizca bu oyuncu objesinin SAHIBI kendi objesinden istek yollayabilir —
            // RoomScanSync'teki ayni koruma. Yoksa bir istemci baskasinin objesi uzerinden
            // harita degistirip izini ona birakabilirdi.
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null || !Session.IsActive) return;

            var def = PropLibrary.Instance.ByIndex(propIndex);
            if (def == null) return;

            var cell = new Vector2Int(cellX, cellZ);
            // Sunucu dogrulamasi istegin KATINI da bilmeli: yoksa istemci havaya koydugunu
            // sanirken sunucu zemine bakip ya reddeder ya da yanlis yeri isgal ederdi.
            if (!Session.CanPlace(def, cell, rot, scalePct, level, heightPct)) return;

            ApplyPlaceRpc(propIndex, cellX, cellZ, level, rot, Session.MintInstanceId(), scalePct, heightPct);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RemoveServerRpc(uint instanceId, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null || !Session.IsActive) return;
            if (Session.Layout.Find(instanceId) == null) return;

            ApplyRemoveRpc(instanceId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SaveServerRpc(string mapName, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null || !Session.IsActive)
            {
                SaveResultOwnerRpc(false, 0);
                return;
            }

            // Ad geldiyse "farkli kaydet": oturum bundan sonra o ad uzerinde calisir. Ad yoksa
            // acik haritanin uzerine yazilir.
            bool ok = string.IsNullOrEmpty(mapName) ? Session.Save() : Session.SaveAs(mapName);
            if (ok) MapCatalog.NoteSaved();   // yeni/adi degismis harita listeye girsin
            // Bekleyen otomatik kaydi da dusur: elle kaydettikten 3 saniye sonra ayni dosyayi
            // bir kez daha yazmanin anlami yok.
            Session.FlushPendingSave();
            SaveResultOwnerRpc(ok, Session.PlacedCount);
        }

        /// <summary>
        /// Tells the player who asked how it went.
        ///
        /// Without this the headset has no way to tell a save from a no-op — the file lands on a
        /// machine the player is not looking at, and "did that work?" is exactly the question
        /// that made building feel unsafe.
        /// </summary>
        [Rpc(SendTo.Owner)]
        void SaveResultOwnerRpc(bool ok, int propCount)
        {
            SaveMessage = ok
                ? $"KAYDEDILDI\n\n{propCount} prop sunucuya yazildi."
                : "KAYDEDILEMEDI\n\nSunucu diske yazamadi (Console'a bak).";

            // Istemcinin "kaydedilmemis degisiklik" isareti ancak sunucu ONAYLAYINCA dusuyor:
            // istegi yollar yollamaz dusseydi, reddedilen bir kayitta gozluk isini kaydedilmis
            // sanardi.
            if (ok && Session != null) Session.ClearUnsaved();
        }

        // ------------------------------------------------------- harita ACMA (Serit 1)
        //
        // HARITA ACMAK DA SUNUCUNUN ISI. Dosyalar PC'de: gozlukte MapLayout.Load kendi bos
        // klasorune bakip "bulunamadi" der. Liste sunucudan geldigi icin harita EKRANDA
        // gorunuyordu ama acilmiyordu — kaydetme ve havuz islemleri gibi acma da tele binmeliydi.
        //
        // Ayni sey YENI HARITA icin de gecerli, ve orada sessizce daha kotusu oluyordu: gozluk
        // kendi bos oturumunu acar, sunucu ESKI haritada kalirdi; sonraki her yerlestirme eski
        // haritaya islenirdi.

        public static bool ClientRequestOpen(string mapName)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.OpenMapServerRpc(mapName);
            return true;
        }

        public static bool ClientRequestNewMap()
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.NewMapServerRpc();
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void OpenMapServerRpc(string mapName, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null) return;

            if (!Session.OpenExisting(mapName))
            {
                SessionErrorOwnerRpc(Session.NotStartedReason);
                return;
            }
            StartCoroutine(SendLayout(true));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void NewMapServerRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null) return;

            if (!Session.OpenNew())
            {
                SessionErrorOwnerRpc("Yeni harita acilamadi.");
                return;
            }
            StartCoroutine(SendLayout(true));
        }

        [Rpc(SendTo.Owner)]
        void SessionErrorOwnerRpc(string reason)
        {
            PendingMessage = "HARITA ACILAMADI\n\n" + reason;
            Debug.LogWarning("[ConstructorSync] Sunucu haritayi acamadi: " + reason);
        }

        /// <summary>Istemci: "acik haritayi diskteki haline geri dondur" (degisiklikleri at).</summary>
        public static bool ClientRequestDiscard()
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.DiscardServerRpc();
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void DiscardServerRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (Session == null) return;

            // Degisiklikler SUNUCUNUN belleginde de duruyor (her yerlestirme oraya uygulaniyor),
            // yani atmak istemcide tek basina yapilamaz. Sunucu diskteki haline donuyor —
            // otomatik kayit yaratici akista askida oldugu icin disk hala eski hali — ve
            // sonucu isteyene geri yolluyor.
            string ad = Session.CurrentMapName;
            bool ok = string.IsNullOrEmpty(ad) ? Session.OpenNew() : Session.OpenExisting(ad);
            if (ok) StartCoroutine(SendLayoutToOwner());
        }

        /// <summary>Last save result from the server; the placer shows it once and clears it.</summary>
        public static string SaveMessage { get; set; }

        // ------------------------------------------------------- mac haritasi secimi (Serit 3)

        /// <summary>
        /// SUNUCU: bu macin haritasi belirlendi mi?
        ///
        /// "Oturumda harita yuklu mu" ile AYNI SEY DEGIL, ve ikisini bir sanmak iki hataya yol
        /// acmisti:
        ///  - Tasarimci yaratici modda harita duzenleyip oyuncu moduna gecince oturum zaten
        ///    ACIK oluyordu; kimse secim yapmadigi halde "harita hazir" sayilip editordeki
        ///    harita maca giriyordu.
        ///  - Bir oyuncu harita secip cikinca oturum acik kaliyordu; sunucu bombosken gelen
        ///    yeni oyuncuya secim sorulmuyor, onceki macin haritasi dayatiliyordu.
        ///
        /// Oturum "sahnede su an ne kurulu"yu soyler; bu bayrak "bu mac icin karar verildi mi"yi.
        /// Karar SON OYUNCU CIKINCA dusuyor (bkz. OnNetworkDespawn) — sunucuda kimse kalmadiysa
        /// devam eden bir mac da yoktur.
        /// </summary>
        public static bool MatchMapChosen { get; private set; }

        /// <summary>
        /// SUNUCU: bu macin haritasini havuzdan RASTGELE cekip kurar.
        ///
        /// OYUNCUYA SORULMUYOR. Once havuz listesi acilip "hangi harita?" deniyordu; artik
        /// OYUNCU modunu secen kisi dogrudan bir haritaya dusuyor. Havuzdaki her harita esit
        /// paya sahip (bkz. <see cref="MapCatalog.PoolInRandomOrder"/>).
        ///
        /// KURA SUNUCUDA CEKILIYOR, cunku mac TEK harita uzerinde gecmeli: her gozluk kendi
        /// rastgelesini cekseydi ayni macin oyunculari farkli haritalara duserdi. Secilen
        /// harita <see cref="SendLayout"/> ile herkese gidiyor.
        ///
        /// ILK OYUNCU CEKER, SONRAKILER DEVAM EDEN MACA GIRER — bkz. <see cref="MatchMapChosen"/>.
        /// Bayrak son oyuncu cikinca dustugu icin her mac kendi kurasini cekiyor; ayni sunucuda
        /// arka arkaya oynanan maclar ayni haritaya cakilip kalmiyor.
        /// </summary>
        static bool ServerPickMatchMap(out string sebep)
        {
            sebep = null;

            var kura = MapCatalog.PoolInRandomOrder();
            if (kura.Count == 0)
            {
                sebep = "Sunucunun havuzunda oynanabilir harita kalmamis.";
                return false;
            }

            // Bastaki kurayi kazandi. ACILAMAYANI ATLA: bir haritanin dosyasi bozulduysa
            // dogru davranis maci hic baslatmamak degil, havuzdaki bir sonrakine gecmek.
            for (int i = 0; i < kura.Count; i++)
            {
                if (!Session.OpenExisting(kura[i]))
                {
                    Debug.LogWarning($"[ConstructorSync] Kurada '{kura[i]}' cikti ama acilamadi " +
                                     $"({Session.NotStartedReason}) — havuzdaki bir sonraki deneniyor.");
                    continue;
                }

                MatchMapChosen = true;
                Debug.Log($"[ConstructorSync] Mac haritasi havuzdan rastgele secildi: " +
                          $"'{kura[i]}' ({kura.Count} harita arasindan, her biri %{100f / kura.Count:0.#}).");
                return true;
            }

            sebep = $"Havuzdaki {kura.Count} haritanin hicbiri acilamadi.";
            return false;
        }

        // ------------------------------------------------------------- katalog (Serit 2)
        //
        // HARITALAR PC'DE YASAR, TASARIM GOZLUKTE YAPILIR. Gozluk kendi diskindeki listeyi
        // gosterseydi orada var gorunen bir harita mac aninda bulunamazdi — liste de dosyalar
        // da tek yerde olmali. Bu bolum o tek yeri gozlugun eline veriyor: istek gozlukten,
        // is PC'de, sonuc herkese.

        /// <summary>Istemci: "listeyi yolla". Cevap <see cref="CatalogChunkClientRpc"/> ile gelir.</summary>
        public static bool ClientRequestCatalog()
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.RequestCatalogServerRpc();
            return true;
        }

        public static bool ClientRequestPoolChange(string mapName, bool add)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.PoolChangeServerRpc(mapName, add);
            return true;
        }

        public static bool ClientRequestRename(string oldName, string newName)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.RenameServerRpc(oldName, newName);
            return true;
        }

        public static bool ClientRequestDelete(string mapName)
        {
            var sync = LocalOwned();
            if (sync == null) return false;
            sync.DeleteServerRpc(mapName);
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestCatalogServerRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;
            StartCoroutine(SendCatalogToOwner());
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void PoolChangeServerRpc(string mapName, bool add, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;

            string hata = null;
            bool ok = add ? MapCatalog.AddToPool(mapName, out hata) : MapCatalog.RemoveFromPool(mapName);
            if (!ok) CatalogErrorOwnerRpc(hata ?? $"'{mapName}' islenemedi.");
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RenameServerRpc(string oldName, string newName, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;

            if (!MapCatalog.Rename(oldName, newName, out string hata))
                CatalogErrorOwnerRpc(hata ?? "Yeniden adlandirilamadi.");
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void DeleteServerRpc(string mapName, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != OwnerClientId) return;

            if (!MapCatalog.Delete(mapName))
                CatalogErrorOwnerRpc($"'{mapName}' silinemedi.");
        }

        [Rpc(SendTo.Owner)]
        void CatalogErrorOwnerRpc(string hata)
        {
            // Isi yapan makine oyuncunun BAKMADIGI makine; sebep geri donmezse islem sessizce
            // yutulmus gorunur.
            MapCatalog.LastError = hata;
            Debug.LogWarning("[MapCatalog] Sunucu reddetti: " + hata);
        }

        /// <summary>Sunucu: guncel listeyi BUTUN istemcilere yollar.</summary>
        public static void ServerBroadcastCatalog()
        {
            var any = AnySpawned();
            if (any == null) return;   // bagli istemci yok — yollanacak kimse de yok
            any.StartCoroutine(any.SendCatalogToAll());
        }

        IEnumerator SendCatalogToOwner() => SendCatalog(false);
        IEnumerator SendCatalogToAll() => SendCatalog(true);

        /// <summary>
        /// Listeyi parcalayarak yollar — haritayla ayni gerekce: guvenilir kanalin kuyrugu
        /// sinirli, tek karede sikistirilan buyuk yuk dusuyor ve dusen parcanin tekrar yolu yok.
        /// </summary>
        IEnumerator SendCatalog(bool toAll)
        {
            string json = MapCatalog.SnapshotJson();
            if (string.IsNullOrEmpty(json)) yield break;

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int total = (bytes.Length + ChunkSize - 1) / ChunkSize;
            if (total > MaxChunks)
            {
                Debug.LogError($"[MapCatalog] Liste cok buyuk ({bytes.Length} bayt) — gonderilemedi.");
                yield break;
            }

            for (int i = 0; i < total; i++)
            {
                if (!IsSpawned) yield break;
                int len = Mathf.Min(ChunkSize, bytes.Length - i * ChunkSize);
                var chunk = new byte[len];
                Buffer.BlockCopy(bytes, i * ChunkSize, chunk, 0, len);

                if (toAll) CatalogChunkClientRpc(i, total, chunk);
                else CatalogChunkOwnerRpc(i, total, chunk);

                if ((i & 7) == 7) yield return null;
            }
        }

        [Rpc(SendTo.Owner)]
        void CatalogChunkOwnerRpc(int index, int total, byte[] data) => ReceiveCatalogChunk(index, total, data);

        [Rpc(SendTo.NotServer)]
        void CatalogChunkClientRpc(int index, int total, byte[] data) => ReceiveCatalogChunk(index, total, data);

        byte[][] _catalogChunks;
        int _catalogReceived;

        void ReceiveCatalogChunk(int index, int total, byte[] data)
        {
            if (total <= 0 || total > MaxChunks || index < 0 || index >= total) return;

            if (_catalogChunks == null || _catalogChunks.Length != total)
            {
                _catalogChunks = new byte[total][];
                _catalogReceived = 0;
            }
            if (_catalogChunks[index] == null) _catalogReceived++;
            _catalogChunks[index] = data;
            if (_catalogReceived < total) return;

            int size = 0;
            foreach (var c in _catalogChunks) size += c.Length;
            var all = new byte[size];
            int at = 0;
            foreach (var c in _catalogChunks) { Buffer.BlockCopy(c, 0, all, at, c.Length); at += c.Length; }

            _catalogChunks = null;
            _catalogReceived = 0;
            MapCatalog.ApplySnapshot(Encoding.UTF8.GetString(all));
        }

        // ------------------------------------------------------------- apply (server -> everyone)

        [Rpc(SendTo.Everyone)]
        void ApplyPlaceRpc(int propIndex, int cellX, int cellZ, byte level, byte rot,
            uint instanceId, byte scalePct, byte heightPct)
        {
            if (Session == null || !Session.IsActive) return;
            var def = PropLibrary.Instance.ByIndex(propIndex);
            if (def == null)
            {
                Debug.LogWarning($"[ConstructorSync] Bilinmeyen prop indeksi {propIndex} — " +
                                 "kutuphane surumleri farkli olabilir (menu 25).");
                return;
            }
            Session.ApplyPlace(def.id, new Vector2Int(cellX, cellZ), level, rot, instanceId, scalePct, heightPct);
        }

        [Rpc(SendTo.Everyone)]
        void ApplyRemoveRpc(uint instanceId)
        {
            if (Session == null || !Session.IsActive) return;
            Session.ApplyRemove(instanceId);
        }

        // ------------------------------------------------------------- late join

        /// <summary>
        /// Ships the whole map to a player that just connected.
        ///
        /// The pacing here is not decoration: the reliable channel's send queue is finite, and
        /// a large map queued in one frame overflows it. A dropped chunk has no resend path, so
        /// the transfer would simply never complete. Breathing every 8 chunks is the same fix
        /// <see cref="RoomScanSync"/> arrived at going the other direction.
        /// </summary>
        IEnumerator SendLayoutToOwner() => SendLayout(false);

        /// <summary>
        /// Istemcinin sahne senkronunu bitirmesini bekler. BAGLANTI ONAYININ ORTASINA RPC
        /// SOKMAMAK icin: OnNetworkSpawn, Netcode'un HandleConnectionApproval'inin TAM
        /// ORTASINDA cagriliyor — oyuncu objesi dogdu ama sahne senkron paketi henuz yazilmadi.
        /// Araya RPC sikistirmak o paketi bozuyor ve istemci HIC baglanamiyor.
        /// </summary>
        IEnumerator WaitUntilClientReady()
        {
            float deadline = Time.time + 20f;
            while (Time.time < deadline && !ReadyClients.Contains(OwnerClientId))
                yield return null;

            if (!ReadyClients.Contains(OwnerClientId))
                Debug.LogWarning($"[ConstructorSync] Istemci {OwnerClientId} 20 sn icinde senkron " +
                                 "olmadi — harita gonderilmedi. Insa moduna girince yeniden istenir.");
        }

        /// <summary>
        /// KATILIM AKISI — yalnizca oyuncu objesi dogarken. Harita secimi BURAYA ait, genel
        /// gonderme yoluna degil.
        ///
        /// Once secim mantigi <see cref="SendLayout"/> icindeydi ve YARATICI AKISI KIRIYORDU:
        /// tasarimci "yeni harita" deyince sunucu haritayi kuruyor, ama gonderme yolu ayni
        /// daldan gecip "haritayi sen sec" deyip cikiyordu. Gozluk haritayi hic almiyor ve
        /// "Sunucudan harita bekleniyor..." ekraninda kaliyordu.
        ///
        /// Ayrim basit: SendLayout "elindekini yolla" demek; secim ise yalnizca yeni bir oyuncu
        /// maca girerken sorulacak bir sey.
        /// </summary>
        IEnumerator JoinFlow()
        {
            if (OwnerClientId == NetworkManager.ServerClientId) yield break;

            yield return WaitUntilClientReady();
            if (!ReadyClients.Contains(OwnerClientId)) yield break;   // uyari iceride yazildi

            // Bu macin haritasi secilmediyse SUNUCU KURAYI CEKER. Secildiyse hicbir sey
            // yapilmaz: oyuncu devam eden maca girer (bkz. MatchMapChosen).
            if (Session != null && !MatchMapChosen)
            {
                if (!ServerPickMatchMap(out string sebep))
                {
                    NoLayoutOwnerRpc(sebep);
                    yield break;
                }

                // HERKESE: kura yeni bir harita kurdu, baglilarin elindeki artik yanlis.
                // (Uygulamada mac haritasi ilk oyuncu girerken seciliyor, yani genelde
                // tek kisi var; "genelde" bir gonderme kurali icin yeterli degil.)
                yield return SendLayout(true);
                yield break;
            }

            yield return SendLayout(false);
        }

        /// <param name="toAll">
        /// true ise harita BUTUN istemcilere gider. Gec katilimda yalnizca yeni gelene
        /// yollamak yeter; ama harita DEGISTIGINDE (yeni harita, baska harita acildi,
        /// degisiklikler atildi) herkesin elindeki artik yanlis — o durumda hepsine gider.
        /// </param>
        IEnumerator SendLayout(bool toAll)
        {
            // Sunucunun kendi oyuncu objesi yok; bu yalnizca istemci objeleri icin anlamli.
            if (OwnerClientId == NetworkManager.ServerClientId) yield break;

            // BAGLANTI ONAYININ ORTASINA RPC SOKMA.
            //
            // OnNetworkSpawn, Netcode'un HandleConnectionApproval'inin TAM ORTASINDA cagriliyor:
            // oyuncu objesi dogdu ama sahne senkron paketi (SynchronizeNetworkObjects) HENUZ
            // yazilmadi. Araya RPC sikistirmak o paketi bozuyor — sunucu
            // NetworkObject.Serialize icinde NullReferenceException atiyor ve ISTEMCI HIC
            // BAGLANAMIYOR. Objelerin kendisi saglam; kirilan sey SIRA.
            //
            // Eskiden bu kaza eseri dogruydu: sunucunun oturumu kapali oldugu icin asagidaki
            // bekleme dongusu zaten kareyi devrediyor, gonderme onaydan cok sonraya kaliyordu.
            // Harita artik acilista kuruldugundan (ConstructorSession.BuildForPlay) oturum
            // ilk kareden itibaren ACIK ve donguye hic girilmiyordu — yani "gonderme sonra olur"
            // bir kural degil, tesadufmus. Simdi kural.
            yield return WaitUntilClientReady();
            if (!ReadyClients.Contains(OwnerClientId)) yield break;

            // Oturum henuz acilmamis olabilir (oda taramasi gelmemis) — bir sure bekle.
            float deadline = Time.time + 10f;
            while (Time.time < deadline && (Session == null || !Session.IsActive))
                yield return null;

            if (Session == null || !Session.IsActive) yield break;
            if (!IsSpawned) yield break;

            string json = Session.CurrentJson();
            if (string.IsNullOrEmpty(json)) yield break;

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int total = (bytes.Length + ChunkSize - 1) / ChunkSize;
            if (total > MaxChunks)
            {
                Debug.LogError($"[ConstructorSync] Harita cok buyuk ({bytes.Length} bayt) — gonderilemedi.");
                yield break;
            }

            for (int i = 0; i < total; i++)
            {
                if (!IsSpawned) yield break;   // oyuncu transferin ortasinda cikti
                int len = Mathf.Min(ChunkSize, bytes.Length - i * ChunkSize);
                var chunk = new byte[len];
                Buffer.BlockCopy(bytes, i * ChunkSize, chunk, 0, len);
                if (toAll) LayoutChunkClientRpc(i, total, chunk);
                else LayoutChunkOwnerRpc(i, total, chunk);
                if ((i & 7) == 7) yield return null;
            }

            Debug.Log($"[ConstructorSync] Harita gonderildi: {bytes.Length} bayt, {total} parca " +
                      $"-> istemci {OwnerClientId}");
        }

        // IKI RPC, TEK GOVDE. Bir [Rpc] metodunu kod icinden cagirmak govdesini CALISTIRMAZ,
        // yeni bir RPC YOLLAR — o yuzden ortak is duz bir metotta (katalogda da ayni kalip).
        [Rpc(SendTo.NotServer)]
        void LayoutChunkClientRpc(int index, int total, byte[] data) =>
            ReceiveLayoutChunk(index, total, data);

        [Rpc(SendTo.Owner)]
        void LayoutChunkOwnerRpc(int index, int total, byte[] data) =>
            ReceiveLayoutChunk(index, total, data);

        void ReceiveLayoutChunk(int index, int total, byte[] data)
        {
            if (total <= 0 || total > MaxChunks || index < 0 || index >= total) return;

            if (_rxChunks == null || _rxChunks.Length != total)
            {
                _rxChunks = new byte[total][];
                _rxReceived = 0;
            }
            if (_rxChunks[index] == null) _rxReceived++;
            _rxChunks[index] = data;
            if (_rxReceived < total) return;

            int size = 0;
            foreach (var c in _rxChunks) size += c.Length;
            var all = new byte[size];
            int at = 0;
            foreach (var c in _rxChunks)
            {
                Buffer.BlockCopy(c, 0, all, at, c.Length);
                at += c.Length;
            }
            _rxChunks = null;
            _rxReceived = 0;

            if (Session != null) Session.AdoptJson(Encoding.UTF8.GetString(all));
        }
    }
}
