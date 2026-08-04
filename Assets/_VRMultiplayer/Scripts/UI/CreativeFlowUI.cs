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
        VRPointer _pointer;
        CalibrationManager _calibration;
        ConstructorPlacer _placer;

        bool _placed, _recentering;
        bool _calibrationAsked;

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
            if (Placer != null && Placer.BuildMode) { CloseAll(); return; }

            // 3) Menu ya da liste.
            if (_list != null) { Place(_list.transform); _list.Tick(_pointer); return; }

            if (_menu == null) OpenMenu();
            Place(_menu.transform);
            _menu.Tick(_pointer);
        }

        void OnDestroy() => CloseAll();

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
                    // Harita Yoneticisi ayri bir ekran; su an yalnizca sebebi soyluyoruz ki
                    // dugme "bozuk" gibi durmasin.
                    StartCoroutine(Note("HARİTA YÖNETİCİSİ\n\nHenüz hazır değil.", 3f));
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
            _list.Picked += OnMapPicked;
            _list.Back += CloseList;

            _placed = false;
            _recentering = false;
        }

        void OnMapPicked(string mapName)
        {
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
            Placer.SetBuildMode(true);
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
