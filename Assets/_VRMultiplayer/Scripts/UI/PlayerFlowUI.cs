using UnityEngine;
using Unity.Netcode;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// OYUNCU MODUNUN KAPISI: once "oynanabilir harita var mi?", sonra isim + takim ekrani.
    ///
    /// KONTROL EN BASTA, giristen de once: havuz bossa oyuncunun isim yazip takim secmesinin
    /// bir karsiligi yok, ve bunu ancak maca girdikten sonra ogrenmek en can sikici sirasi.
    ///
    /// HARITA SECIMI YOK. Oyuncuya "hangi harita?" diye sorulmuyor; havuz doluysa mac
    /// havuzdan RASTGELE cekilen bir haritada geciyor ve kurayi sunucu cekiyor
    /// (bkz. ConstructorSync.ServerPickMatchMap). Bu bilesenin isi havuzun bos olmadigini
    /// dogrulayip giris ekranina devretmek.
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
    ///
    /// GIRIS EKRANINA DEVREDINCE ISI BITMEZ — 2. EVREDE BAGLANTIYI GOZLER. Sebep: OYUNA
    /// BASLA'ya basildiktan sonra sunucu hic gelmezse LanBootstrap sonsuza kadar yeniden
    /// dener ve oyuncunun elinde TEK BIR CIKIS KAPISI BILE KALMAZ. Gozcu o hatayi gorup
    /// ana menu kapisini aciyor.
    /// </summary>
    public class PlayerFlowUI : MonoBehaviour
    {
        /// <summary>Sunucu aranirken beklenecek sure (sn). LAN'da kesif genelde bir saniyenin
        /// altinda sonuclanir; bu sure "yayin dusmus" demeye yetecek kadar uzun.</summary>
        const float DiscoverTimeout = 5f;

        /// <summary>"BEKLEMEYE DEVAM" diyen oyuncuya cikis kapisini yeniden onerme araligi (sn).
        /// Her basarisiz denemede (~12 sn) sormak dayanilmaz olurdu; hic sormamak da onu sonsuz
        /// bir bekleyise kilitler.</summary>
        const float WarnAgainDelay = 30f;

        const float Distance = 1.4f, HeightDrop = 0.12f, TiltDegrees = 8f;
        const float RecenterAngle = 35f, RecenterSpeed = 6f;

        ConfirmPanel _warn;
        TextMesh _searchNote;
        VRPointer _pointer;
        NetworkDiscovery _discovery;

        float _searchUntil;
        float _warnAgainAt;
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

            // BAGLANDIYSA IS BITTI — uyari paneli ACIK OLSA BILE. "Baglanilamadi" panelini
            // okurken arka plandaki deneme tutabilir; ekranda kalan uyari o an yalan olur.
            // Bu yuzden kontrol panel dalindan ONCE.
            if (_handedOff && Connected) { Destroy(gameObject); return; }

            if (_warn != null) { Place(_warn.transform); _warn.Tick(_pointer); return; }

            // IMLEC PANELE BAGLI. Lazer kendini her karede panelin Tick'inde ciziyor; panel
            // kapaninca cizen de silen de kalmiyor ve SON CIZILEN ISIN HAVADA ASILI KALIYOR.
            // Acik panel yoksa imleci de kaldiriyoruz; bir panel acilinca yeniden dogar.
            ReleasePointer();

            // 2. EVRE — BAGLANTI GOZCUSU (bkz. sinif notu).
            if (_handedOff) { WatchConnection(); return; }

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
                ShowSearching();

                _discovery = FindFirstObjectByType<NetworkDiscovery>();
                // Sunucuda ASLA cagirilmaz: StartClientDiscovery yayini durdurur ve sunucu
                // gorunmez olurdu. Yukaridaki IsServer dali tam da bunun icin once geliyor.
                if (_discovery != null) _discovery.StartClientDiscovery();
            }

            if (_discovery == null) { HandOff(); return; }   // kesif yok: engelleme, sunucu karar versin

            if (_discovery.TryGetFoundHost(out string ip, out ushort hostPort))
            {
                // Kullanimdaki isimler: giris ekrani acilmadan ONCE elimizde olmali, cunku
                // oyuncu ismini orada seciyor ve o an henuz bagli degil (bkz. PlayerProfile).
                PlayerProfile.SetTakenNames(_discovery.FoundHostNames);

                switch (_discovery.FoundHostPool)
                {
                    case NetworkDiscovery.PoolHint.Empty: ShowEmptyPool(); break;

                    default:                                 // HasMaps ve Unknown: gecir
                        // KESFI IKINCI KEZ YAPTIRMA: adres elimizde, LanBootstrap'e ver.
                        // Verilmezse oyuncu OYUNA BASLA'ya bastiktan sonra ayni aramayi bir
                        // daha, bu kez 10 saniyeye kadar bekliyor.
                        var boot = FindFirstObjectByType<LanBootstrap>();
                        if (boot != null) boot.UseKnownHost(ip, hostPort);
                        HandOff();
                        break;
                }
                return;
            }

            if (Time.unscaledTime >= _searchUntil) ShowNoServer();
        }

        static bool Connected =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;

        void OnDestroy()
        {
            CloseWarning();
            CloseSearching();
            ReleasePointer();
        }

        // ------------------------------------------------------------- 2. evre: baglanti

        /// <summary>
        /// Baglanti kurulamadiysa oyuncuya CIKIS KAPISI ac. Yeniden deneme arka planda zaten
        /// suruyor; buradaki panel onu durdurmuyor, yalnizca "istersen menuye don" diyor.
        /// </summary>
        void WatchConnection()
        {
            // OYUNA BASLA'ya basilana kadar gozculuk edilecek bir sey yok: giris ekrani hala
            // acik ve oyuncu isim yaziyor. Kapisiz birakilirsa buradaki uyari giris panelinin
            // uzerine acilabilir (ve ikinci bir lazer dogar).
            if (!PlayerProfile.Confirmed) return;

            string problem = LanBootstrap.JoinFailure;
            if (string.IsNullOrEmpty(problem)) return;
            if (Time.unscaledTime < _warnAgainAt) return;

            OpenWarning("BAĞLANILAMADI",
                problem + "\nArka planda denemeye devam ediliyor.",
                "BEKLEMEYE DEVAM", "ANA MENÜYE DÖN", UITheme.AccentCyan,
                yes =>
                {
                    if (yes)
                    {
                        CloseWarning();
                        _warnAgainAt = Time.unscaledTime + WarnAgainDelay;
                    }
                    else CancelJoin();
                });
        }

        /// <summary>
        /// Maca girmekten vazgec: denemeyi durdur, giris onayini geri al, ana menuye don.
        ///
        /// ONAYI GERI ALMAK SART (bkz. <see cref="PlayerProfile.Unconfirm"/>): birakilirsa
        /// oyuncu tekrar OYUNCU modunu sectiginde giris ekrani "zaten onaylandi" deyip kendini
        /// yok eder ve oyuncu hicbir sey yapamadan bos ekranda kalir.
        /// </summary>
        void CancelJoin()
        {
            var boot = FindFirstObjectByType<LanBootstrap>();
            if (boot != null) boot.CancelJoin();

            // Isim listesi de bayat: bir dahaki girise kadar sunucuya hic bakmamis sayilalim,
            // yoksa eski listeye dayanip yasayan bir ismi reddedebiliriz.
            PlayerProfile.ForgetTakenNames();
            PlayerProfile.Unconfirm();
            AppMode.ReturnToModeSelect();   // bir sonraki karede bu bilesen de yok olur
        }

        // ------------------------------------------------------------- arama bildirimi

        /// <summary>
        /// Arama sirasinda EKRANI BOS BIRAKMA. Sahne bos tuval (grid zemin) oldugu icin mod
        /// secildikten sonra oyuncu 5 saniye boyunca hicbir sey gormuyor ve uygulamanin
        /// dondugunu saniyor. Tembel takipte: bilgi verirken bakisi hapsetmesin.
        /// </summary>
        void ShowSearching()
        {
            if (_searchNote != null) return;
            _searchNote = HeadFollowPanel.Create("Search Notice",
                "SUNUCU ARANIYOR...", UITheme.AccentCyan, lazy: true);
            _searchNote.transform.SetParent(transform, false);
        }

        void CloseSearching()
        {
            if (_searchNote == null) return;
            Destroy(_searchNote.gameObject);
            _searchNote = null;
        }

        // ------------------------------------------------------------- ekranlar

        /// <summary>
        /// Giris ekranini ac. Macin haritasi artik sorulmuyor (sunucu cekiyor), ama bu bilesen
        /// CEKILMEZ: baglanti kurulana kadar gozculuk ediyor (bkz. <see cref="WatchConnection"/>).
        /// </summary>
        void HandOff()
        {
            _handedOff = true;
            CloseWarning();
            CloseSearching();

            // Kesif isi bitti: adres LanBootstrap'e verildi, yayin dinlemeyi surdurmenin
            // anlami yok. Baglanti tutmazsa LanBootstrap kendi aramasini bastan baslatir.
            if (_discovery != null) _discovery.StopDiscovery();

            PlayerEntryUI.Create();
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

            // Uyari ile arama bildirimi YAN YANA DURMAZ: ikisi de kafanin 1.4 m onunde,
            // birlikte acilirlarsa ust uste iki yazi olurlar.
            CloseSearching();

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
