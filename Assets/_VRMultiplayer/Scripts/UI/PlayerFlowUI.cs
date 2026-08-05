using UnityEngine;
using Unity.Netcode;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// OYUNCU MODUNUN KAPISI: once "oynanabilir harita var mi?", sonra isim + takim ekrani.
    ///
    /// KONTROL EN BASTA, harita seciminden de giristen de once — sema boyle istiyor ve sebebi
    /// pratik: havuz bossa oyuncunun isim yazip takim secmesinin bir karsiligi yok, ve bunu
    /// ancak maca girdikten sonra ogrenmek en can sikici sirasi.
    ///
    /// GOZLUK BAGLI DEGILKEN NASIL BILIYOR: havuz PC'de ama gozluk bu asamada henuz
    /// baglanmiyor (baglanti isim+takim onaylaninca kuruluyor). Cevap kesif yayinindan
    /// geliyor — sunucu havuz durumunu yayina yaziyor (bkz. NetworkDiscovery, Faz 6).
    ///
    /// SUNUCU YOK ile HAVUZ BOS AYRI SEYLER. Ikisi de "oynayamazsin" ama tarifleri farkli:
    /// birinde harita yapmak gerekiyor, digerinde PC'yi acmak. Tek ekranda birlestirmek
    /// oyuncuya yanlis isi yaptirirdi.
    ///
    /// EMIN OLAMAYINCA ENGELLEME: sunucu havuz bilgisi yollamiyorsa (eski surum) giris ekrani
    /// acilir; karar sunucuya kalir. Yayin zaten bir ipucu, tek dogruluk kaynagi degil.
    /// </summary>
    public class PlayerFlowUI : MonoBehaviour
    {
        /// <summary>Sunucu aranirken beklenecek sure (sn). LAN'da kesif genelde bir saniyenin
        /// altinda sonuclanir; bu sure "yayin dusmus" demeye yetecek kadar uzun.</summary>
        const float DiscoverTimeout = 5f;

        const float Distance = 1.4f, HeightDrop = 0.12f, TiltDegrees = 8f;
        const float RecenterAngle = 35f, RecenterSpeed = 6f;

        ConfirmPanel _warn;
        MapListPanel _mapList;
        VRPointer _pointer;
        NetworkDiscovery _discovery;

        float _searchUntil;
        bool _searching;
        bool _handedOff;
        bool _placed, _recentering;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() => AppMode.Chosen += OnModeChosen;

        static void OnModeChosen(AppMode.Mode m)
        {
            if (m != AppMode.Mode.Player) return;
            var go = new GameObject("~PlayerFlowUI");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerFlowUI>();
        }

        void Update()
        {
            if (!AppMode.IsPlayer) { Destroy(gameObject); return; }

            if (_warn != null) { Place(_warn.transform); _warn.Tick(_pointer); return; }

            // IMLEC PANELE BAGLI. Lazer kendini her karede panelin Tick'inde ciziyor; panel
            // kapaninca cizen de silen de kalmiyor ve SON CIZILEN ISIN HAVADA ASILI KALIYOR.
            // Acik panel yoksa imleci de kaldiriyoruz; bir panel acilinca yeniden dogar.
            if (_mapList == null) ReleasePointer();

            // 2. EVRE — MAC HARITASI. Giris ekranindan sonra sunucu "haritayi sen sec" diyebilir:
            // macin haritasi HENUZ secilmemis demektir. Secim yapilinca sunucu haritayi herkese
            // yayiyor, sonradan katilan bu evreden hic gecmiyor (bkz. ConstructorSync).
            if (_handedOff)
            {
                if (_mapList != null)
                {
                    // Harita bu arada geldiyse (baska oyuncu sectiyse) secim ekrani anlamsiz.
                    var s = ConstructorSession.Instance;
                    if (s != null && s.IsActive) { CloseMapList(); return; }

                    Place(_mapList.transform);
                    _mapList.Tick(_pointer);
                    return;
                }

                if (ConstructorSync.MapChoiceRequested) OpenMapChooser();
                return;
            }

            // SUNUCUNUN KENDISI: havuz elinin altinda, aramaya gerek yok.
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                if (MapCatalog.PoolIsEmpty) ShowEmptyPool();
                else HandOff();
                return;
            }

            // GOZLUK: sunucuyu ara, havuz durumunu yayindan ogren.
            if (!_searching)
            {
                _searching = true;
                _searchUntil = Time.unscaledTime + DiscoverTimeout;

                _discovery = FindFirstObjectByType<NetworkDiscovery>();
                // Sunucuda ASLA cagirilmaz: StartClientDiscovery yayini durdurur ve sunucu
                // gorunmez olurdu. Yukaridaki IsServer dali tam da bunun icin once geliyor.
                if (_discovery != null) _discovery.StartClientDiscovery();
            }

            if (_discovery == null) { HandOff(); return; }   // kesif yok: engelleme, sunucu karar versin

            if (_discovery.TryGetFoundHost(out string ip))
            {
                switch (_discovery.FoundHostPool)
                {
                    case NetworkDiscovery.PoolHint.Empty: ShowEmptyPool(); break;
                    default: HandOff(); break;               // HasMaps ve Unknown: gecir
                }
                return;
            }

            if (Time.unscaledTime >= _searchUntil) ShowNoServer();
        }

        void OnDestroy()
        {
            CloseWarning();
            CloseMapList();
        }

        // ------------------------------------------------------------- ekranlar

        /// <summary>
        /// Giris ekranini ac. AKIS BURADA BITMIYOR: oyuncu isim + takimi onaylayip baglandiktan
        /// sonra sunucu harita secimi isteyebilir, o da bu bilesenin isi (2. evre).
        /// </summary>
        void HandOff()
        {
            _handedOff = true;
            CloseWarning();
            PlayerEntryUI.Create();
        }

        // ------------------------------------------------------------- mac haritasi secimi

        void OpenMapChooser()
        {
            ConstructorSync.MapChoiceRequested = false;
            EnsurePointer();

            // Liste sunucudan gelir; istek simdi gidiyor, panel MapCatalog.Changed ile dolar.
            MapCatalog.Refresh();

            var go = new GameObject("Match Map List");
            go.transform.SetParent(transform, false);
            _mapList = go.AddComponent<MapListPanel>();
            _mapList.SetTitle("HARİTA SEÇ");
            _mapList.SetPoolOnly(true);
            _mapList.Picked += OnMatchMapPicked;

            // GERI = maca girmekten vazgec. Harita secmeden mac baslamaz, o yuzden tek anlamli
            // cikis ana menu.
            _mapList.Back += () => AppMode.ReturnToModeSelect();

            _placed = false;
            _recentering = false;
        }

        void OnMatchMapPicked(string mapName)
        {
            CloseMapList();

            if (ConstructorSync.ClientPickMatchMap(mapName)) return;

            // Istek gidemedi (baglanti dustu): sebebi soyle, sessizce bos ekranda birakma.
            ShowNoServer();
        }

        void CloseMapList()
        {
            if (_mapList == null) return;
            _mapList.Picked -= OnMatchMapPicked;
            Destroy(_mapList.gameObject);
            _mapList = null;
        }

        void ShowEmptyPool()
        {
            OpenWarning("OYNANABİLİR HARİTA YOK",
                "Havuzda harita yok. Yaratıcı modunda bir harita\ntasarlayıp HAVUZA EKLE.",
                "YARATICI MODA GEÇ", "ANA MENÜYE DÖN", UITheme.AccentPurple,
                yes =>
                {
                    if (yes) AppMode.Choose(AppMode.Mode.Creative);
                    else AppMode.ReturnToModeSelect();
                });
        }

        void ShowNoServer()
        {
            OpenWarning("SUNUCU BULUNAMADI",
                "PC sunucusu görünmüyor. Aynı ağda mı, ve\nSUNUCU başlatıldı mı?",
                "TEKRAR ARA", "ANA MENÜYE DÖN", UITheme.AccentCyan,
                yes =>
                {
                    if (yes) { CloseWarning(); _searching = false; }   // yeniden ara
                    else AppMode.ReturnToModeSelect();
                });
        }

        void OpenWarning(string title, string message, string yesLabel, string noLabel,
            Color yesEdge, System.Action<bool> answer)
        {
            if (_warn != null) return;

            EnsurePointer();
            var go = new GameObject("Player Warning Panel");
            go.transform.SetParent(transform, false);
            _warn = go.AddComponent<ConfirmPanel>();
            _warn.Setup(title, message, yesLabel, noLabel, yesEdge);
            _warn.Answered += answer;

            _placed = false;
            _recentering = false;
        }

        void CloseWarning()
        {
            if (_warn == null) return;
            Destroy(_warn.gameObject);
            _warn = null;
        }

        void EnsurePointer()
        {
            if (_pointer != null) return;
            var go = new GameObject("UI Pointer");
            go.transform.SetParent(transform, false);
            _pointer = go.AddComponent<VRPointer>();
        }

        void ReleasePointer()
        {
            if (_pointer == null) return;
            Destroy(_pointer.gameObject);
            _pointer = null;
        }

        /// <summary>
        /// Tembel takip — ModeSelectUI / PlayerEntryUI / CreativeFlowUI ile ayni davranis:
        /// panel gorus alanindan cikinca yumusakca kayar, menzil icindeyken DUNYAYA SABIT kalir
        /// ki lazerle tusa basilabilsin.
        /// </summary>
        void Place(Transform t)
        {
            if (t == null) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 targetPos = head.position + fwd * Distance - Vector3.up * HeightDrop;
            Quaternion targetRot = Quaternion.LookRotation(fwd) * Quaternion.Euler(TiltDegrees, 0f, 0f);

            if (!_placed)
            {
                t.SetPositionAndRotation(targetPos, targetRot);
                _placed = true;
                _recentering = false;
                return;
            }

            if (!_recentering)
            {
                Vector3 toPanel = t.position - head.position; toPanel.y = 0f;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                if (Vector3.Angle(fwd, toPanel.normalized) < RecenterAngle) return;
                _recentering = true;
            }

            float k = 1f - Mathf.Exp(-RecenterSpeed * Time.unscaledDeltaTime);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, targetPos, k),
                                     Quaternion.Slerp(t.rotation, targetRot, k));

            if ((t.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }

        // ------------------------------------------------------------- masaustu yedegi
        void OnGUI()
        {
            if (Application.isMobilePlatform || _warn == null) return;

            // Kulaklik yokken de akis test edilebilmeli (mod secim kutusuyla ayni gerekce).
            GUI.depth = -1000;
            const float w = 360f, h = 96f;
            GUILayout.BeginArea(new Rect((Screen.width - w) * 0.5f, 110f, w, h), GUI.skin.box);
            GUILayout.Label(_warn.Title);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_warn.YesLabel)) _warn.AnswerFromDesktop(true);
            if (GUILayout.Button(_warn.NoLabel)) _warn.AnswerFromDesktop(false);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
