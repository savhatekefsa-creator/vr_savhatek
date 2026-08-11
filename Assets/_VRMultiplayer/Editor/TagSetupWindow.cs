using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   49. Tag Kurulum Merkezi — origin (tag 0) tanimi, plakalardan yerlesim (menu 47) ve
    /// tag'leri acma (menu 48) TEK pencerede, sirali ve kapili.
    ///
    /// NEDEN PENCERE: kurulum uc ayri menu + Inspector'da elle tag 0 duzenlemeye dagilmisti
    /// ve iki SESSIZ tuzagi vardi:
    ///   - layoutVersion artirilmadan yapilan yerlesim degisikligini cihazdaki TagLayout.json
    ///     ezer; belirtisi "degistirdim ama degismedi" ve sebebi hicbir yerde yazmaz
    ///     (iki kez yasandi — bkz. TagLayoutStore.Load).
    ///   - kagit yapistirilmadan acilan tag'i kalibrasyon olmadigi yerde arar ve dogru
    ///     tag'lerin kurdugu cerceveyi de bozar.
    /// Pencere ilkini OTOMATIKLESTIRIR (origin her yazildiginda surum artar), ikincisini
    /// KAPIYLA tutar (onay kutusu isaretlenmeden acma dugmesi calismaz).
    ///
    /// Menu 47/48'in kodu burada TEKRARLANMAZ — ayni statik metodlar cagrilir. Pencere
    /// yalnizca durumu gosterir, sirayi dayatir ve on kosullari denetler. Menuler tek tek
    /// calistirilmaya devam edilebilir; iki yol ayni koddan gectigi icin ayrisamaz.
    /// </summary>
    public class TagSetupWindow : EditorWindow
    {
        const string PrefabPath = "Assets/_VRMultiplayer/Prefabs/AprilTagRig.prefab";

        [MenuItem("Tools/VR Multiplayer/49. Tag Kurulum Merkezi (origin + plakalar + acma)")]
        public static void Open()
        {
            var w = GetWindow<TagSetupWindow>("Tag Kurulum");
            w.minSize = new Vector2(520f, 560f);
            w._fieldsFromData = false;
            w.Reload();
        }

        // ---- durum onbellegi: her repaint'te JSON okumamak icin Reload'da doldurulur ----
        AprilTagCalibration _prefabCal;
        AprilTagCalibration _sceneCal;
        MapLayout _map;                    // null = harita dosyasi yok
        string _mapName = ConstructorSession.DefaultMapName;
        string[] _mapNames = new string[0];
        int _plateCount;
        int _tagCount, _openCount;
        readonly List<string> _errors = new List<string>();
        readonly List<string> _warnings = new List<string>();

        // ---- form alanlari ----
        float _originHeight = 1.5f;
        float _tagSize = 0.14f;
        bool _papersGlued;                 // oturumluk kapi; kalici degil, cunku fiziksel
                                           // durumun kaydi degil BEYANI — her turda yeniden
                                           // isaretlenmeli
        bool _fieldsFromData;
        Vector2 _scroll;
        string _report = "";

        void OnFocus() => Reload();

        void Reload()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            _prefabCal = go != null ? go.GetComponentInChildren<AprilTagCalibration>(true) : null;
            _sceneCal = FindFirstObjectByType<AprilTagCalibration>(FindObjectsInactive.Include);

            // Harita listesi diskten: pencere acikken creator modda kayit yapilmis olabilir.
            _mapNames = MapLayout.List();
            System.Array.Sort(_mapNames);

            // Varsayilan harita adi ("Current") cogu kurulumda diskte YOK. Secimi burada
            // bilerek yapiyoruz; popup'a birakilsaydi ilk cizimde kendiliginden kayardi ve
            // pencere bir an yanlis haritanin durumunu gosterirdi.
            if (System.Array.IndexOf(_mapNames, _mapName) < 0 && _mapNames.Length > 0)
                _mapName = _mapNames[0];

            // File.Exists kapisi: MapLayout.Load yoksa Console'a uyari yaziyor ve Reload her
            // OnFocus'ta calisiyor — kapisiz birakilirsa pencereye her donuste log birikirdi.
            _map = System.Array.IndexOf(_mapNames, _mapName) >= 0 ? MapLayout.Load(_mapName) : null;

            _plateCount = 0;
            if (_map != null && _map.props != null)
                foreach (var p in _map.props)
                    if (p != null && p.propId == MapTagCapture.PlateId) _plateCount++;

            _tagCount = 0; _openCount = 0;
            var tags = ActiveTags();
            if (tags != null)
                foreach (var t in tags)
                    if (t != null) { _tagCount++; if (t.useForCalibration) _openCount++; }

            // Form alanlari yalnizca ILK yuklemede veriden dolar. Her Reload'da dolsaydi
            // kullanicinin yazdigi deger pencere odak degistirince sessizce geri alinirdi.
            var cal = _prefabCal != null ? _prefabCal : _sceneCal;
            if (!_fieldsFromData && cal != null)
            {
                var zero = FindTag(cal.tagLayout, 0);
                if (zero != null) _originHeight = zero.position.y;
                _tagSize = cal.tagSizeMeters;
                _fieldsFromData = true;
            }

            BuildChecks();
        }

        /// <summary>
        /// Gecerli yerlesim: harita varsa harita (runtime onceligi de bu), yoksa prefab.
        /// Sahne kopyasi kasten degil — prefabla ayni olmasi beklenir; ayrisirsa uyari cikar.
        /// </summary>
        AprilTagCalibration.TagEntry[] ActiveTags()
        {
            if (_map != null && _map.tags != null && _map.tags.Length > 0) return _map.tags;
            return _prefabCal != null ? _prefabCal.tagLayout : null;
        }

        /// <summary>Haritadaki plakadan turemis tag sayisi (tag 0 origin oldugu icin sayilmaz).</summary>
        int PlateDerivedTagCount()
        {
            if (_map == null || _map.tags == null) return 0;
            int n = 0;
            foreach (var t in _map.tags) if (t != null && t.id != 0) n++;
            return n;
        }

        void BuildChecks()
        {
            _errors.Clear();
            _warnings.Clear();

            if (_prefabCal == null)
                _errors.Add("AprilTagRig prefabinda AprilTagCalibration yok:\n" + PrefabPath);
            if (_map == null)
                _warnings.Add($"'{_mapName}' haritasi yok ({MapLayout.PathFor(_mapName)}).\n" +
                              "Plaka akisi (Adim 2-4) harita ister; origin (Adim 1) haritasiz da yazilir.");

            var tags = ActiveTags();
            var zero = FindTag(tags, 0);
            if (zero == null)
                _errors.Add("Tag 0 (origin) tanimsiz — once Adim 1.");
            else if (Mathf.Abs(zero.position.x) > 0.001f || Mathf.Abs(zero.position.z) > 0.001f)
                _errors.Add($"Tag 0 konumu ({zero.position.x:0.###}, {zero.position.y:0.###}, " +
                            $"{zero.position.z:0.###}) — origin TANIM geregi (0, y, 0) olmali. Adim 1 duzeltir.");

            if (tags != null)
            {
                var seen = new HashSet<int>();
                foreach (var t in tags)
                    if (t != null && !seen.Add(t.id))
                        _errors.Add($"Tag ID {t.id} yerlesimde BIRDEN FAZLA — kalibrasyon hangisine " +
                                    "inanacagini bilemez.");
            }

            // Plaka / tag sayisi karsilastirmasi YONLUDUR, cunku iki yon ayni sey degil:
            //
            //   plaka > tag : cevrilmemis plaka var — Adim 2 gercekten gerekli.
            //   plaka < tag : normal. Kagit yapistirildiktan sonra plaka haritadan
            //                 silinebilir; tag yerinde durmaya devam eder. Uyarmak, her
            //                 saglikli haritada yanip duran bir lamba olurdu.
            //
            // Asil tehlike ikinci durumda ve SESSIZ: Capture listeyi sifirdan kuruyor, yani
            // 3 tag'li / 1 plakali bir haritada Adim 2 tag 2'yi SILER ve tag 1'i kalan
            // plakanin yerine tasir. Kapisi Adim 2'nin dugmesinde (bkz. WouldDropTags).
            if (PlateDerivedTagCount() < _plateCount)
                _warnings.Add($"Haritada {_plateCount} plaka ama {PlateDerivedTagCount()} plaka-kaynakli " +
                              "tag var — cevrilmemis plaka var, Adim 2 calistirilmali.");

            if (_prefabCal != null && _sceneCal != null &&
                !LayoutsEqual(_prefabCal.tagLayout, _sceneCal.tagLayout))
                _warnings.Add("Sahnedeki AprilTagCalibration yerlesimi PREFABDAN FARKLI (override). " +
                              "Adim 1 ikisini birden yazar; baska alanlar icin Inspector'dan Revert dusunun.");

            if (_prefabCal != null &&
                (_prefabCal.tagSizeMeters < 0.05f || _prefabCal.tagSizeMeters > 0.3f))
                _warnings.Add($"tagSizeMeters = {_prefabCal.tagSizeMeters:0.###} m supheli — basili karenin " +
                              "dis kenari cetvelle olculmeli (yazicilar olcek kaydirir).");
        }

        // ------------------------------------------------------------------ cizim

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawMapPicker();
            DrawStatus();

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Play modunda yazma kapali: sahneye yazilan degisiklik " +
                                        "cikista kaybolur.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawStep1Origin();
                DrawStep2Capture();
                DrawStep3Physical();
                DrawStep4Enable();
            }

            DrawTagTable();
            DrawReport();
            EditorGUILayout.EndScrollView();
        }

        void DrawMapPicker()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Harita:", GUILayout.Width(50f));
                if (_mapNames.Length == 0)
                    GUILayout.Label(_mapName + "  (diskte harita yok)", EditorStyles.miniLabel);
                else
                {
                    int cur = Mathf.Max(0, System.Array.IndexOf(_mapNames, _mapName));
                    int next = EditorGUILayout.Popup(cur, _mapNames, EditorStyles.toolbarPopup);
                    if (next != cur)
                    {
                        _mapName = _mapNames[next];
                        Reload();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Yenile", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                    Reload();
            }
        }

        void DrawStatus()
        {
            foreach (var e in _errors) EditorGUILayout.HelpBox(e, MessageType.Error);
            foreach (var w in _warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            if (_errors.Count == 0 && _warnings.Count == 0)
                EditorGUILayout.HelpBox("Sorun gorunmuyor.", MessageType.Info);

            var sb = new StringBuilder();
            sb.Append($"Yerlesim: {_tagCount} tag ({_openCount} acik)");
            if (_map != null) sb.Append($"   Plaka: {_plateCount}");
            if (_prefabCal != null) sb.Append($"   layoutVersion: {_prefabCal.layoutVersion}");
            EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
            EditorGUILayout.Space(4f);
        }

        void DrawStep1Origin()
        {
            EditorGUILayout.LabelField("1. ORIGIN (tag 0)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Tag 0 sifir noktasinin TANIMIDIR: konumu (0, y, 0), y kagidin " +
                                    "MERKEZININ zeminden yuksekligi. Yazmak layoutVersion'i otomatik " +
                                    "artirir — cihazlardaki eski TagLayout.json boylece devre disi kalir.",
                                    MessageType.None);

            _originHeight = EditorGUILayout.FloatField(
                new GUIContent("Kagit merkezi yuksekligi (m)",
                    "Tag 0 kagidinin MERKEZININ zeminden yuksekligi. Metreyle olcun."),
                _originHeight);
            _tagSize = EditorGUILayout.FloatField(
                new GUIContent("Tag kenari (m)",
                    "Basili tag'in SIYAH karesinin dis kenari. Cetvelle olcun — yazicilar olcek kaydirir."),
                _tagSize);

            // Yaw KASTEN salt-okunur: tag 0'in yaw'i cercevenin kendisini dondurur, yani
            // dogrulanmis TUM tag konumlarini birden yanlislar. Degistirmek pencere isi degil,
            // bilincli bir cerceve karari — Inspector'da yapilir.
            var zero = FindTag(ActiveTags(), 0);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.FloatField(
                    new GUIContent("Yaw (salt okunur)",
                        "Cerceve konvansiyonu. Degistirmek tum tag konumlarini yanlislar; " +
                        "gercekten gerekiyorsa Inspector'dan, bilerek."),
                    zero != null ? zero.yawDegrees : 0f);

            bool heightOk = _originHeight >= 0.3f && _originHeight <= 3f;
            if (!heightOk)
                EditorGUILayout.HelpBox("Yukseklik 0.3–3 m araliginin disinda — olcumu kontrol edin.",
                                        MessageType.Warning);

            using (new EditorGUI.DisabledScope(!heightOk || (_prefabCal == null && _sceneCal == null)))
                if (GUILayout.Button("Origin'i Yaz (prefab + sahne + harita)"))
                {
                    SetReport(WriteOrigin());
                    Reload();
                }
            EditorGUILayout.Space(8f);
        }

        void DrawStep2Capture()
        {
            EditorGUILayout.LabelField("2. PLAKALARDAN YERLESIM URET (menu 47)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Yaratici modda konan TagIsaret plakalarini haritanin tag yerlesimine " +
                                    "cevirir. Yeni tag'ler KAPALI dogar — kagit henuz yerinde degildir.",
                                    MessageType.None);

            int drop = PlateDerivedTagCount() - _plateCount;
            if (drop > 0)
                EditorGUILayout.HelpBox(
                    $"DIKKAT: yerlesim sifirdan kurulur. Su an {PlateDerivedTagCount()} plaka-kaynakli tag " +
                    $"var ama {_plateCount} plaka — calistirmak {drop} tag'i SILER ve kalanlarin ID'lerini " +
                    "koyma sirasina gore YENIDEN dagitir. Duvardaki kagitlar yerinde duruyorsa bu, calisan " +
                    "bir yerlesimi bozar. Onay istenecek.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(_map == null))
                if (GUILayout.Button($"Plakalardan Uret ({_plateCount} plaka)"))
                {
                    bool go = drop <= 0 || EditorUtility.DisplayDialog("Tag Kurulum",
                        $"'{_mapName}' haritasinda {PlateDerivedTagCount()} plaka-kaynakli tag var ama " +
                        $"yalnizca {_plateCount} plaka.\n\n" +
                        $"Devam edilirse {drop} tag SILINIR ve kalan tag'lerin ID'leri yeniden dagitilir. " +
                        "Duvarda o ID'lere ait kagitlar asiliysa kalibrasyon bozulur.\n\n" +
                        "Devam edilsin mi?", "Devam et", "Vazgec");
                    if (go)
                    {
                        SetReport(MapTagCapture.Capture(_mapName));
                        Reload();
                    }
                }
            EditorGUILayout.Space(8f);
        }

        void DrawStep3Physical()
        {
            EditorGUILayout.LabelField("3. FIZIKSEL IS — kagitlari yapistir", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Kagit tag'leri plakalarin TAM USTUNE yapistirin (plaka nereye " +
                                    "dusuyorsa dogru yer orasi). Bu adim pencereden yapilamaz; asagidaki " +
                                    "kutu fiziksel isin bittiginin beyanidir ve Adim 4'un kapisidir.",
                                    MessageType.None);
            _papersGlued = EditorGUILayout.ToggleLeft(
                "Kagitlar plakalarin uzerine yapistirildi", _papersGlued);
            EditorGUILayout.Space(8f);
        }

        void DrawStep4Enable()
        {
            EditorGUILayout.LabelField("4. TAG'LERI KALIBRASYONA AC (menu 48)", EditorStyles.boldLabel);
            int kapali = _tagCount - _openCount;
            using (new EditorGUI.DisabledScope(_map == null || !_papersGlued || kapali == 0))
                if (GUILayout.Button(kapali > 0 ? $"Hepsini Ac ({kapali} kapali)" : "Hepsi zaten acik"))
                {
                    SetReport(MapTagCapture.Enable(_mapName));
                    Reload();
                }
            if (!_papersGlued && kapali > 0)
                EditorGUILayout.LabelField("Adim 3'teki kutu isaretlenmeden acilmaz.",
                                           EditorStyles.miniLabel);
            EditorGUILayout.Space(8f);
        }

        void DrawTagTable()
        {
            var tags = ActiveTags();
            if (tags == null || tags.Length == 0) return;

            bool fromMap = _map != null && _map.tags != null && _map.tags.Length > 0;
            EditorGUILayout.LabelField(fromMap ? "TAG'LER (haritadan)" : "TAG'LER (prefabdan)",
                                       EditorStyles.boldLabel);

            // ID'ye gore SIRALI gosterilir. Dizideki sira koyma sirasidir (HARITA2'de 0, 2, 1)
            // ve okuyan "tag 1 nerede" diye bakiyor, "ucuncu eleman ne" diye degil. Kopya
            // uzerinde siralanir — dizinin kendi sirasi ID dagitimini belirliyor.
            var sirali = new List<AprilTagCalibration.TagEntry>(tags);
            sirali.Sort((a, b) => (a?.id ?? int.MaxValue).CompareTo(b?.id ?? int.MaxValue));

            foreach (var t in sirali)
            {
                if (t == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        $"tag {t.id}{(t.id == 0 ? " (origin)" : "")}   " +
                        $"{t.position.x:0.000} {t.position.y:0.000} {t.position.z:0.000}   " +
                        $"yaw {t.yawDegrees:0.0}",
                        GUILayout.MinWidth(280f));

                    // Tek tek acma yalnizca HARITA tag'lerinde: buyuk mekanda kagitlar tek
                    // turda bitmeyebilir, menu 48'in hepsini-birden'i o durumda fazla kaba.
                    if (fromMap && !Application.isPlaying)
                    {
                        bool now = EditorGUILayout.ToggleLeft(t.useForCalibration ? "ACIK" : "KAPALI",
                            t.useForCalibration, GUILayout.Width(70f));
                        if (now != t.useForCalibration)
                        {
                            t.useForCalibration = now;
                            if (_map.Save(_mapName)) AssetDatabase.Refresh();
                            Reload();
                        }
                    }
                    else
                        EditorGUILayout.LabelField(t.useForCalibration ? "ACIK" : "KAPALI",
                                                   GUILayout.Width(70f));
                }
            }
            EditorGUILayout.Space(8f);
        }

        void DrawReport()
        {
            if (string.IsNullOrEmpty(_report)) return;
            EditorGUILayout.LabelField("SON ISLEM", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true), GUILayout.MinHeight(90f));
        }

        void SetReport(string r)
        {
            _report = r ?? "";
            Debug.Log("[TagSetupWindow] " + _report);
        }

        // ------------------------------------------------------------------ yazma

        /// <summary>
        /// Tag 0'i (0, h, 0) olarak prefaba, sahnedeki kopyaya ve (tag'leri varsa) haritaya
        /// yazar. Yerlesim degistiyse layoutVersion IKI HEDEFTE BIRDEN ayni degere cikar —
        /// surumlerin ayrismasi, "degistirdim ama degismedi" tuzaginin ikinci yoludur.
        /// </summary>
        string WriteOrigin()
        {
            var sb = new StringBuilder();
            var targets = new List<AprilTagCalibration>();
            if (_prefabCal != null) targets.Add(_prefabCal);
            if (_sceneCal != null && _sceneCal != _prefabCal) targets.Add(_sceneCal);

            int nextVersion = 0;
            foreach (var cal in targets) nextVersion = Mathf.Max(nextVersion, cal.layoutVersion);
            nextVersion++;

            bool layoutChanged = false;
            foreach (var cal in targets)
            {
                Undo.RecordObject(cal, "Origin (tag 0)");
                var zero = FindOrAddZero(cal);
                bool changed = (zero.position - new Vector3(0f, _originHeight, 0f)).sqrMagnitude > 1e-8f ||
                               !zero.useForCalibration;
                zero.position = new Vector3(0f, _originHeight, 0f);
                zero.useForCalibration = true;
                cal.tagSizeMeters = _tagSize;
                if (changed) layoutChanged = true;
                EditorUtility.SetDirty(cal);
            }

            if (layoutChanged)
                foreach (var cal in targets) { cal.layoutVersion = nextVersion; EditorUtility.SetDirty(cal); }

            if (_sceneCal != null)
                EditorSceneManager.MarkSceneDirty(_sceneCal.gameObject.scene);
            AssetDatabase.SaveAssets();

            sb.AppendLine($"Tag 0 = (0, {_originHeight:0.000}, 0), kenar {_tagSize:0.000} m.");
            sb.AppendLine($"  prefab: {(_prefabCal != null ? "yazildi" : "YOK")}   " +
                          $"sahne: {(_sceneCal != null ? "yazildi" : "yok")}");
            sb.AppendLine(layoutChanged
                ? $"  layoutVersion -> {nextVersion} (cihazlardaki eski TagLayout.json devre disi kalacak)"
                : "  yerlesim degismedi — layoutVersion oldugu gibi kaldi");

            // Harita tag'leri kendi katmani: runtime'da prefabi EZER (harita > cihaz > prefab).
            // Origin degisip harita eski kalirsa degisiklik oyunda hic gorunmez — o yuzden
            // tag 0 tasiyan haritaya da ayni deger yazilir.
            if (_map != null && _map.tags != null && _map.tags.Length > 0)
            {
                var mapZero = FindTag(_map.tags, 0);
                if (mapZero == null)
                    sb.AppendLine($"  harita '{_mapName}': tag 0 yok — Adim 2 calisinca sahnedekinden alinir");
                else if ((mapZero.position - new Vector3(0f, _originHeight, 0f)).sqrMagnitude <= 1e-8f)
                    sb.AppendLine($"  harita '{_mapName}': tag 0 zaten ayni — dokunulmadi");
                else
                {
                    // Degismediyse KAYDETME. Save her cagrildiginda savedAt damgasini
                    // tazeliyor, yani no-op bir yazma haritayi versiyon kontrolunde
                    // "degismis" gosterir ve gercek degisiklikleri gurultuye gomer.
                    mapZero.position = new Vector3(0f, _originHeight, 0f);
                    if (_map.Save(_mapName))
                    {
                        AssetDatabase.Refresh();
                        sb.AppendLine($"  harita '{_mapName}': tag 0 guncellendi");
                    }
                    else sb.AppendLine($"  harita '{_mapName}': KAYDEDILEMEDI — Console'a bak");
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ yardimcilar

        static AprilTagCalibration.TagEntry FindTag(AprilTagCalibration.TagEntry[] list, int id)
        {
            if (list == null) return null;
            foreach (var t in list)
                if (t != null && t.id == id) return t;
            return null;
        }

        static AprilTagCalibration.TagEntry FindOrAddZero(AprilTagCalibration cal)
        {
            var zero = FindTag(cal.tagLayout, 0);
            if (zero != null) return zero;
            var list = new List<AprilTagCalibration.TagEntry>(
                cal.tagLayout ?? new AprilTagCalibration.TagEntry[0]);
            zero = new AprilTagCalibration.TagEntry { id = 0 };
            list.Insert(0, zero);
            cal.tagLayout = list.ToArray();
            return zero;
        }

        static bool LayoutsEqual(AprilTagCalibration.TagEntry[] a, AprilTagCalibration.TagEntry[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                var x = a[i]; var y = b[i];
                if (x == null || y == null) { if (x != y) return false; continue; }
                if (x.id != y.id || x.yawDegrees != y.yawDegrees ||
                    x.useForCalibration != y.useForCalibration ||
                    (x.position - y.position).sqrMagnitude > 1e-8f) return false;
            }
            return true;
        }
    }
}
