using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// YARATICI MODUN AKISI: once KALIBRASYON, sonra ANA MENU, oradan editor ya da havuz.
    ///
    /// SIRA NEDEN BOYLE: harita kalibre cercevede oruluyor. Kalibre olmadan konan bir duvar
    /// kalibre olan herkeste baska yere duser, ve kalibrasyon sonradan yapilirsa rig donup
    /// kaydigi icin zemin oyuncunun altindan cikar. Ayni gerekce agi da kapsiyor: haritalar
    /// PC'de yasadigi icin baglanti da kalibrasyondan SONRA aciliyor (bkz. LanBootstrap).
    ///
    /// MENU HER ISLEM SONU GERI GELIR. Sema "tum dallar bu menuye doner" diyor; burada bu bir
    /// DURUM kuralindan cikiyor, geri donus cagrilarindan degil: yaratici moddayken, kalibre
    /// olmusken ve insa modu KAPALIYKEN menu acik olur. Editorden cikan kendiliginden menuye
    /// duser, cikis yolunu ayrica kimsenin cagirmasi gerekmez.
    ///
    /// MOD DEGISINCE KENDINI TOPLAR. Serit 3'un uyari ekrani "yaratici moda gec" diyecek ve
    /// Serit 4 mac sonunda ana menuye donecek; moda ozel her ekran kendi kapanisindan sorumlu
    /// olmazsa o donuslerde paneller havada kalir.
    /// </summary>
    public class CreativeFlowUI : MonoBehaviour
    {
        const float Distance = 1.4f, HeightDrop = 0.12f, TiltDegrees = 8f;
        const float RecenterAngle = 35f, RecenterSpeed = 6f;

        CreativeMenuPanel _menu;
        MapListPanel _list;
        ConfirmPanel _confirm;
        NameEntryPanel _name;
        VRPointer _pointer;
        CalibrationManager _calibration;
        ConstructorPlacer _placer;

        MapActionsPanel _actions;

        bool _placed, _recentering;
        bool _calibrationAsked;
        bool _wasEditing;

        // Ayni liste iki isi goruyor: harita ACMA ve harita YONETME. Secilen satirin ne
        // yapacagini bu belirliyor (bkz. OnMapPicked).
        bool _managing;

        // Aboneligi AppMode.ResetStatics temizler (domain reload kapaliyken abonelik listesi
        // oyunlar arasi tasinir ve ikinci Play'de IKI akis dogardi — PlayerEntryUI'daki ders).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() => AppMode.Chosen += OnModeChosen;

        static void OnModeChosen(AppMode.Mode m)
        {
            if (m != AppMode.Mode.Creative) return;
            var go = new GameObject("~CreativeFlowUI");
            DontDestroyOnLoad(go);
            go.AddComponent<CreativeFlowUI>();
        }

        void Update()
        {
            // Mod degisti: her seyi topla. (Kendini yok etmek, panelleri de goturur.)
            if (!AppMode.IsCreative) { Destroy(gameObject); return; }

            // 1) KALIBRASYON. Bitene kadar hicbir panel acmiyoruz: kalibrasyon paneli de
            //    kafanin onunde duruyor, ikisi ust uste biner.
            if (!EnsureCalibrated()) { CloseAll(); return; }

            // 2) INSA MODU ACIKKEN MENU YOK. Editor ekranin sahibi; menu, editorden cikilinca
            //    kendiliginden geri gelir.
            if (Placer != null && Placer.BuildMode)
            {
                _wasEditing = true;
                CloseAll();
                return;
            }

            // 3) EDITORDEN YENI CIKILDI: karar zinciri (Kaydet? -> isim -> havuz | at).
            if (_wasEditing)
            {
                _wasEditing = false;
                BeginExitFlow();
            }

            // 4) Acik olan ekrani sur — sirasi onemli: karar ekranlari menunun onunde.
            if (_confirm != null) { Place(_confirm.transform); _confirm.Tick(_pointer); return; }
            if (_name != null)    { Place(_name.transform);    _name.Tick(_pointer);    return; }
            if (_actions != null) { Place(_actions.transform); _actions.Tick(_pointer); return; }
            if (_list != null)    { Place(_list.transform);    _list.Tick(_pointer);    return; }

            if (_menu == null) OpenMenu();
            Place(_menu.transform);
            _menu.Tick(_pointer);
        }

        void OnDestroy()
        {
            // Askiyi BIRAKARAK cik: yaratici mod disinda otomatik kayit yine calismali.
            ConstructorSession.AutoSaveSuspended = false;
            CloseAll();
        }

        // ------------------------------------------------------------- kalibrasyon

        bool EnsureCalibrated()
        {
            if (CalibrationManager.Calibrated) return true;

            // Masaustunde (kulaklik yok) kalibrasyon yapilamaz — akisi orada kilitlemek
            // Editor'de hicbir seyi denenemez hale getirirdi.
            if (!XRSettings.isDeviceActive) return true;

            if (_calibration == null) _calibration = FindFirstObjectByType<CalibrationManager>();
            if (_calibration == null || !_calibration.enabled) return true;

            if (!_calibrationAsked)
            {
                _calibrationAsked = true;
                _calibration.Begin("Kalibrasyon bitince HARITA MENUSU acilir.");
            }
            return false;
        }

        // ------------------------------------------------------------- menu / liste

        void OpenMenu()
        {
            EnsurePointer();

            var go = new GameObject("Creative Menu Panel");
            go.transform.SetParent(transform, false);
            _menu = go.AddComponent<CreativeMenuPanel>();
            _menu.Selected += OnMenuChoice;

            _placed = false;
            _recentering = false;
        }

        void OnMenuChoice(CreativeMenuPanel.Choice c)
        {
            switch (c)
            {
                case CreativeMenuPanel.Choice.NewMap:
                    CloseMenu();
                    StartNewMap();
                    break;

                case CreativeMenuPanel.Choice.ExistingMap:
                    CloseMenu();
                    OpenList();
                    break;

                case CreativeMenuPanel.Choice.ManagePool:
                    CloseMenu();
                    _managing = true;
                    OpenList();
                    break;
            }
        }

        void OpenList()
        {
            EnsurePointer();

            // Listeyi ISTE: gozlukte kaynak sunucudur ve cevap bir kare sonra gelebilir.
            // Panel MapCatalog.Changed'e abone, veri gelince kendini yeniden cizer.
            MapCatalog.Refresh();

            var go = new GameObject("Map List Panel");
            go.transform.SetParent(transform, false);
            _list = go.AddComponent<MapListPanel>();
            _list.SetTitle(_managing ? "HARİTA YÖNETİCİSİ" : "KAYITLI HARİTALAR");
            _list.Picked += OnMapPicked;
            _list.Back += () => { _managing = false; CloseList(); };

            _placed = false;
            _recentering = false;
        }

        void OnMapPicked(string mapName)
        {
            // YONETICI AKISINDA liste kapanmaz mantigi degisir: satir bir haritayi ACMAZ,
            // o haritanin islemlerini acar. "Ekleme/cikarma sinirsiz tekrarlanabilir" kurali
            // bundan cikiyor — islem bitince listeye geri donuluyor, menuye degil.
            if (_managing) { OpenActions(mapName); return; }

            CloseList();

            var s = ConstructorSession.Instance;
            if (s == null) return;

            if (!s.OpenExisting(mapName))
            {
                StartCoroutine(Note("AÇILAMADI\n\n" + s.NotStartedReason, 4f));
                return;
            }
            EnterEditor();
        }

        void StartNewMap()
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            // Bos zemin: "yeni harita" sifirdan tasarim demek, oda taramasina bagli degil
            // (bkz. ConstructorSession.OpenNew). Isim ilk kayitta sorulacak.
            if (!s.OpenNew())
            {
                StartCoroutine(Note("YENİ HARİTA AÇILAMADI\n\nConsole'a bak.", 4f));
                return;
            }
            EnterEditor();
        }

        void EnterEditor()
        {
            if (Placer == null)
            {
                StartCoroutine(Note("EDİTÖR YOK\n\nConstructor bileşeni bulunamadı.", 4f));
                return;
            }

            // Editordeyken otomatik kayit YOK: kaydetme karari cikista soruluyor ve
            // "degisiklikleri at" ancak hicbir sey yazilmamissa bir anlam tasir.
            ConstructorSession.AutoSaveSuspended = true;
            Placer.SetBuildMode(true);
        }

        // ------------------------------------------------------------- cikis karar zinciri

        /// <summary>
        /// Editorden cikildi. Kaydedilmemis degisiklik yoksa hicbir sey sorulmaz — her cikista
        /// bir soru, en cok yapilan islemi en pahali hale getirirdi.
        /// </summary>
        void BeginExitFlow()
        {
            var s = ConstructorSession.Instance;
            if (s == null || !s.HasUnsavedChanges) return;
            AskSave();
        }

        void AskSave()
        {
            var s = ConstructorSession.Instance;
            int prop = s != null ? s.PlacedCount : 0;
            OpenConfirm("KAYDET?", prop + " yerleştirme kaydedilmedi.",
                "KAYDET", "KAYDETME", UITheme.AccentCyan, yes =>
            {
                if (!yes) { AskDiscard(); return; }

                // Isimsiz harita once isimlendirilir; adi olanda "uzerine yaz mi, farkli mi".
                if (string.IsNullOrEmpty(s?.CurrentMapName)) OpenName("HARİTA ADI", null);
                else AskOverwrite();
            });
        }

        void AskOverwrite()
        {
            var s = ConstructorSession.Instance;
            string ad = s != null ? s.CurrentMapName : "";
            OpenConfirm("'" + ad + "'", "Üzerine mi yazılsın, yoksa yeni bir ad mı?",
                "ÜZERİNE YAZ", "FARKLI KAYDET", UITheme.AccentCyan, yes =>
            {
                if (yes) DoSave(ad);
                else OpenName("FARKLI KAYDET", null);
            });
        }

        void AskDiscard()
        {
            OpenConfirm("DEĞİŞİKLİKLERİ AT?", "Bu oturumda yaptıkların geri gelmez.",
                "AT", "VAZGEÇ", UITheme.TeamRedEdge, yes =>
            {
                if (yes) Discard();
                else AskSave();
            });
        }

        void DoSave(string mapName)
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            // Dosyayi HER ZAMAN otorite yazar. Gozlukte istek sunucuya gider; sonuc oradan
            // doner (ConstructorSync.SaveMessage) ve "kaydedilmemis" isareti orada duser.
            bool ok = ConstructorSession.IsMapAuthority
                ? s.SaveAs(mapName)
                : ConstructorSync.ClientRequestSave(mapName);

            if (!ok)
            {
                StartCoroutine(Note("KAYDEDİLEMEDİ\n\nConsole'a bak.", 4f));
                return;
            }

            if (ConstructorSession.IsMapAuthority) MapCatalog.NoteSaved();
            AskPool(mapName);
        }

        /// <summary>Serit 2'nin ilk kutusu: kaydedilen harita oyuncu rotasyonuna girsin mi?</summary>
        void AskPool(string mapName)
        {
            var e = MapCatalog.Find(mapName);

            // Zaten havuzdaysa sormaya gerek yok.
            if (e != null && e.inPool) return;

            // Havuza giremeyecek harita icin SORU DEGIL, SEBEP: "evet" dedirtip ardindan
            // reddetmek, kararin oyuncuda oldugu izlenimini bosa harcar.
            if (e != null && !e.poolEligible)
            {
                StartCoroutine(Note("HAVUZA EKLENEMEZ\n\n" + e.poolBlockReason, 6f));
                return;
            }

            OpenConfirm("HAVUZA EKLENSİN Mİ?", "Havuzdakiler oyuncu modunda çıkar.",
                "EKLE", "EKLEME", UITheme.AccentCyan, yes =>
            {
                if (!yes) return;
                if (!MapCatalog.AddToPool(mapName, out string hata) && !string.IsNullOrEmpty(hata))
                    StartCoroutine(Note("HAVUZA EKLENEMEDİ\n\n" + hata, 6f));
            });
        }

        void Discard()
        {
            var s = ConstructorSession.Instance;
            if (s == null) return;

            if (!ConstructorSession.IsMapAuthority)
            {
                // Degisiklikler sunucunun belleginde de duruyor; atmayi o yapmali.
                ConstructorSync.ClientRequestDiscard();
                s.ClearUnsaved();
                return;
            }

            string ad = s.CurrentMapName;
            bool ok = string.IsNullOrEmpty(ad) ? s.OpenNew() : s.OpenExisting(ad);
            if (!ok) StartCoroutine(Note("GERİ ALINAMADI\n\n" + s.NotStartedReason, 4f));
        }

        // ------------------------------------------------------------- harita yoneticisi

        void OpenActions(string mapName)
        {
            var e = MapCatalog.Find(mapName);
            if (e == null) return;

            CloseActions();
            EnsurePointer();

            var go = new GameObject("Map Actions Panel");
            go.transform.SetParent(transform, false);
            _actions = go.AddComponent<MapActionsPanel>();
            _actions.Setup(e);
            _actions.Chosen += a => OnAction(a, mapName);

            _placed = false;
            _recentering = false;
        }

        void OnAction(MapActionsPanel.Action a, string mapName)
        {
            var e = MapCatalog.Find(mapName);
            CloseActions();

            switch (a)
            {
                case MapActionsPanel.Action.Back:
                    break;   // listeye don — panel kapandi, liste zaten acik

                case MapActionsPanel.Action.PoolToggle:
                    if (e != null && e.inPool) MapCatalog.RemoveFromPool(mapName);
                    else if (!MapCatalog.AddToPool(mapName, out string hata) &&
                             !string.IsNullOrEmpty(hata))
                        StartCoroutine(Note("HAVUZA EKLENEMEDİ\n\n" + hata, 6f));
                    break;

                case MapActionsPanel.Action.Rename:
                    OpenRename(mapName);
                    break;

                case MapActionsPanel.Action.Delete:
                    // SILME GERI DONUSSUZ: tek tikla degil, onayla. Onay kirmizi.
                    OpenConfirm("SİLİNSİN Mİ?",
                        "'" + (e != null ? e.displayName : mapName) + "' tamamen kaldırılır.",
                        "SİL", "VAZGEÇ", UITheme.TeamRedEdge, yes =>
                    {
                        if (yes && !MapCatalog.Delete(mapName))
                            StartCoroutine(Note("SİLİNEMEDİ\n\nConsole'a bak.", 4f));
                    });
                    break;
            }
        }

        void OpenRename(string mapName)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var e = MapCatalog.Find(mapName);
            var go = new GameObject("Name Entry Panel");
            go.transform.SetParent(transform, false);
            _name = go.AddComponent<NameEntryPanel>();
            _name.Setup("YENİDEN ADLANDIR", e != null ? e.displayName : mapName);
            _name.Confirmed += yeni =>
            {
                CloseName();
                if (!MapCatalog.Rename(mapName, yeni, out string hata) && !string.IsNullOrEmpty(hata))
                    StartCoroutine(Note("ADLANDIRILAMADI\n\n" + hata, 5f));
            };
            _name.Cancelled += () => { CloseName(); OpenActions(mapName); };

            _placed = false;
            _recentering = false;
        }

        void CloseActions()
        {
            if (_actions == null) return;
            Destroy(_actions.gameObject);
            _actions = null;
        }

        // ------------------------------------------------------------- karar/isim ekranlari

        void OpenConfirm(string title, string message, string yesLabel, string noLabel,
            Color yesEdge, Action<bool> onAnswer)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var go = new GameObject("Confirm Panel");
            go.transform.SetParent(transform, false);
            _confirm = go.AddComponent<ConfirmPanel>();
            _confirm.Setup(title, message, yesLabel, noLabel, yesEdge);
            _confirm.Answered += answer =>
            {
                CloseConfirm();
                onAnswer?.Invoke(answer);
            };

            _placed = false;
            _recentering = false;
        }

        void OpenName(string title, string prefill)
        {
            CloseConfirm();
            CloseName();
            EnsurePointer();

            var go = new GameObject("Name Entry Panel");
            go.transform.SetParent(transform, false);
            _name = go.AddComponent<NameEntryPanel>();
            _name.Setup(title, prefill);
            _name.Confirmed += ad => { CloseName(); DoSave(ad); };
            _name.Cancelled += () => { CloseName(); AskSave(); };

            _placed = false;
            _recentering = false;
        }

        void CloseConfirm()
        {
            if (_confirm == null) return;
            Destroy(_confirm.gameObject);
            _confirm = null;
        }

        void CloseName()
        {
            if (_name == null) return;
            Destroy(_name.gameObject);
            _name = null;
        }

        // ------------------------------------------------------------- yardimcilar

        ConstructorPlacer Placer
        {
            get
            {
                if (_placer == null) _placer = FindFirstObjectByType<ConstructorPlacer>();
                return _placer;
            }
        }

        void EnsurePointer()
        {
            if (_pointer != null) return;
            var go = new GameObject("UI Pointer");
            go.transform.SetParent(transform, false);
            _pointer = go.AddComponent<VRPointer>();
        }

        void CloseMenu()
        {
            if (_menu == null) return;
            _menu.Selected -= OnMenuChoice;
            Destroy(_menu.gameObject);
            _menu = null;
        }

        void CloseList()
        {
            if (_list == null) return;
            _list.Picked -= OnMapPicked;
            _list.Back -= CloseList;
            Destroy(_list.gameObject);
            _list = null;
        }

        void CloseAll()
        {
            CloseMenu();
            CloseList();
            CloseActions();
            CloseConfirm();
            CloseName();
            _managing = false;
            if (_pointer != null) { Destroy(_pointer.gameObject); _pointer = null; }
        }

        IEnumerator Note(string text, float seconds)
        {
            var p = HeadFollowPanel.Create("Creative Note", text, Color.white);
            yield return new WaitForSeconds(seconds);
            if (p != null) Destroy(p.gameObject);
        }

        /// <summary>
        /// Tembel takip: panel gorus alanindan cikinca hedefe yumusakca kayar, menzil icindeyken
        /// DUNYAYA SABIT kalir ki lazerle tusa basilabilsin. ModeSelectUI ile ayni davranis.
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
    }
}
