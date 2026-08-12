using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   31. Insa Modu Kutuphanesi — the build palette, edited by hand.
    ///
    /// The library is a ScriptableObject with a fifty-entry array, and the default Inspector
    /// shows it as fifty collapsed foldouts with no search, no preview and no idea which of them
    /// the player will actually see. Every question worth asking about it — what is in the
    /// palette, why is this one missing, what does that flag do to this prop — needed either a
    /// scroll hunt or a line of code. This window answers them in one screen.
    ///
    /// It edits the SAME asset the scan tool (menu 25) writes, so the two stay compatible: scan
    /// to pull in a folder of prefabs, come here to decide what the player gets.
    /// </summary>
    public class ConstructorLibraryWindow : EditorWindow
    {
        [MenuItem("Tools/VR Multiplayer/31. Insa Modu Kutuphanesi")]
        public static void Open()
        {
            var w = GetWindow<ConstructorLibraryWindow>("Insa Kutuphanesi");
            w.minSize = new Vector2(720f, 420f);
            w.Reload();
        }

        PropLibrary _lib;
        Vector2 _scroll;
        string _search = "";
        int _categoryFilter = -1;         // -1 = hepsi
        int _paletteFilter = -1;          // -1 = hepsi, 0 = paletsiz, 1+ = palettes[i-1]
        bool _onlyPalette;
        bool _showPalettes = true;
        string _newPaletteName = "";
        readonly List<string> _problems = new List<string>();

        // Palet kurallarinin yerel kopyasi DEGIL: oturum acik olmadan da dogru cevap
        // verebilmek icin ConstructorSession'daki suzgecin ayni sartlarini burada tekrar
        // ediyoruz. Ikisi ayrilirsa pencere yalan soyler, o yuzden tek yerde toplandi.

        void Reload()
        {
            _lib = AssetDatabase.LoadAssetAtPath<PropLibrary>(ConstructorSetup.LibraryAssetPath);
            if (_lib != null) { _lib.InvalidateIndex(); _problems.Clear(); _problems.AddRange(_lib.Validate()); }
        }

        void OnFocus() => Reload();

        void OnGUI()
        {
            if (_lib == null)
            {
                EditorGUILayout.HelpBox("PropLibrary.asset bulunamadi.\n" +
                    ConstructorSetup.LibraryAssetPath, MessageType.Error);
                if (GUILayout.Button("Menu 25 ile uret / tara")) { ConstructorSetup.ScanPropLibrary(); Reload(); }
                return;
            }

            DrawToolbar();
            DrawSummary();
            DrawPaletteManager();
            DrawDropArea();
            DrawList();
            DrawFooter();
        }

        // ------------------------------------------------------------- paletler

        /// <summary>
        /// Create, rename and fill the named prop sets the player switches between in build mode.
        ///
        /// This is the whole point of palettes being DATA instead of an enum: adding "UZAY" is
        /// something you do here in a text field, not in a source file followed by a recompile.
        ///
        /// A prop with NO palette shows under every one of them, which is why the manager keeps
        /// saying so — it is the difference between "this piece is scenery for one world" and
        /// "this piece is a spawn ring and every world needs it".
        /// </summary>
        void DrawPaletteManager()
        {
            _showPalettes = EditorGUILayout.Foldout(_showPalettes,
                $"Paletler ({_lib.PaletteCount})  —  insa modunda carkin ortasindan secilir", true,
                EditorStyles.foldoutHeader);
            if (!_showPalettes) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // --- yeni palet ---
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Yeni palet adi", GUILayout.Width(95f));
                    _newPaletteName = EditorGUILayout.TextField(_newPaletteName);

                    string wantId = PropLibrary.MakePaletteId(_newPaletteName);
                    bool valid = !string.IsNullOrEmpty(wantId) && _lib.PaletteById(wantId) == null;

                    using (new EditorGUI.DisabledScope(!valid))
                        if (GUILayout.Button("Olustur", GUILayout.Width(80f)))
                        {
                            var list = new List<PropPalette>(_lib.palettes ?? new PropPalette[0])
                            {
                                new PropPalette
                                {
                                    id = wantId,
                                    displayName = _newPaletteName.Trim().ToUpperInvariant(),
                                },
                            };
                            _lib.palettes = list.ToArray();
                            _newPaletteName = "";
                            GUI.FocusControl(null);
                            Save();
                        }
                }

                if (!string.IsNullOrEmpty(_newPaletteName) &&
                    _lib.PaletteById(PropLibrary.MakePaletteId(_newPaletteName)) != null)
                    EditorGUILayout.HelpBox("Bu kimlikte bir palet zaten var.", MessageType.Warning);

                if (_lib.PaletteCount == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Henuz palet yok — tum proplar tek listede cikiyor. Bir palet olustur, " +
                        "sonra asagidaki listede her propun palet kutusundan ona ata.",
                        MessageType.Info);
                    return;
                }

                EditorGUILayout.Space(2f);

                // --- mevcut paletler ---
                for (int i = 0; i < _lib.palettes.Length; i++)
                {
                    var pal = _lib.palettes[i];
                    if (pal == null) continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent(pal.id, "sabit kimlik — proplar bunu tutar"),
                            EditorStyles.miniLabel, GUILayout.Width(110f));

                        string shown = EditorGUILayout.TextField(pal.displayName, GUILayout.Width(160f));
                        if (shown != pal.displayName) { pal.displayName = shown; Save(); }

                        int owned = _lib.OwnedCount(pal.id);
                        EditorGUILayout.LabelField($"{owned} prop", EditorStyles.miniLabel,
                            GUILayout.Width(70f));

                        // Carkin dilim sirasi BU listenin sirasi — o yuzden sira buradan
                        // degistirilebilmeli, yoksa dilimlerin yeri yalnizca paletlerin
                        // olusturulma sirasina bagli kalirdi.
                        using (new EditorGUI.DisabledScope(i == 0))
                            if (GUILayout.Button("▲", GUILayout.Width(24f))) { SwapPalettes(i, i - 1); break; }
                        using (new EditorGUI.DisabledScope(i == _lib.palettes.Length - 1))
                            if (GUILayout.Button("▼", GUILayout.Width(24f))) { SwapPalettes(i, i + 1); break; }

                        // Filtrede gorunen her seyi tek hamlede bu palete atamak: sekiz parcayi
                        // tek tek acilir kutudan secmek, aramayi yazip bir dugmeye basmaktan
                        // hem yavas hem daha hatali.
                        if (GUILayout.Button(new GUIContent("Filtredekileri ata",
                                "Su an listede gorunen TUM proplari bu palete tasir"),
                                GUILayout.Width(130f)))
                            AssignFiltered(pal.id);

                        if (GUILayout.Button("Bosalt", GUILayout.Width(60f)))
                            ClearPalette(pal.id);

                        if (GUILayout.Button("Sil", GUILayout.Width(40f)))
                            RemovePalette(i);
                    }
                }

                EditorGUILayout.LabelField(
                    "Buradaki her palet insa modunda carkta BIR DILIM olur. Paleti olmayan " +
                    "proplar 'DIGER' diliminde toplanir — kaybolmazlar.", EditorStyles.miniLabel);
            }
        }

        /// <summary>Reorders palettes — this list's order IS the wheel's slice order.</summary>
        void SwapPalettes(int a, int b)
        {
            if (a < 0 || b < 0 || a >= _lib.palettes.Length || b >= _lib.palettes.Length) return;
            var tmp = _lib.palettes[a];
            _lib.palettes[a] = _lib.palettes[b];
            _lib.palettes[b] = tmp;
            Save();
        }

        /// <summary>Moves every prop the current filter shows into <paramref name="paletteId"/>.</summary>
        void AssignFiltered(string paletteId)
        {
            var hits = new List<PropDef>();
            foreach (var p in _lib.props)
                if (p != null && Matches(p) && p.paletteId != paletteId) hits.Add(p);

            if (hits.Count == 0)
            {
                ShowNotification(new GUIContent("Filtrede tasinacak prop yok"));
                return;
            }

            if (!EditorUtility.DisplayDialog("Palete ata",
                    $"Listede gorunen {hits.Count} prop '{_lib.PaletteName(paletteId)}' paletine " +
                    "tasinsin mi?\n\n" +
                    "Bu proplar bundan sonra YALNIZCA o palet secildiginde cikar.",
                    "Ata", "Vazgec"))
                return;

            foreach (var p in hits) p.paletteId = paletteId;
            Save();
            ShowNotification(new GUIContent(hits.Count + " prop atandi"));
        }

        /// <summary>Sends a palette's props back to the "DIGER" slice.</summary>
        void ClearPalette(string paletteId)
        {
            int n = 0;
            foreach (var p in _lib.props)
                if (p != null && p.paletteId == paletteId) { p.paletteId = ""; n++; }
            Save();
            ShowNotification(new GUIContent(n + " prop paletsize alindi"));
        }

        /// <summary>
        /// Deletes a palette AND releases its props.
        ///
        /// Releasing is not optional: a prop pointing at an id that no longer exists is not
        /// "unassigned", it is unreachable — the runtime filter would never match it and the
        /// piece would quietly vanish from every palette at once.
        /// </summary>
        void RemovePalette(int index)
        {
            var pal = _lib.palettes[index];
            int owned = _lib.OwnedCount(pal.id);

            if (!EditorUtility.DisplayDialog("Paleti sil",
                    $"'{pal.displayName}' paleti silinsin mi?\n\n" +
                    (owned > 0
                        ? $"Icindeki {owned} prop 'DIGER' dilimine alinir — silinmezler, " +
                          "kutuphanede ve carkta kalirlar."
                        : "Palet bos."),
                    "Sil", "Vazgec"))
                return;

            foreach (var p in _lib.props)
                if (p != null && p.paletteId == pal.id) p.paletteId = "";

            var list = new List<PropPalette>(_lib.palettes);
            list.RemoveAt(index);
            _lib.palettes = list.ToArray();
            if (_paletteFilter > index) _paletteFilter--;
            else if (_paletteFilter == index + 1) _paletteFilter = -1;
            Save();
        }

        // ------------------------------------------------------------- ust serit

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(220f));

                var names = new List<string> { "Tum kategoriler" };
                names.AddRange(System.Enum.GetNames(typeof(PropCategory)));
                _categoryFilter = EditorGUILayout.Popup(_categoryFilter + 1, names.ToArray(),
                    EditorStyles.toolbarPopup, GUILayout.Width(140f)) - 1;

                _paletteFilter = EditorGUILayout.Popup(_paletteFilter + 1, PaletteFilterNames(),
                    EditorStyles.toolbarPopup, GUILayout.Width(150f)) - 1;

                _onlyPalette = GUILayout.Toggle(_onlyPalette, "Sadece palettekiler", EditorStyles.toolbarButton,
                    GUILayout.Width(140f));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Klasorleri tara (25)", EditorStyles.toolbarButton, GUILayout.Width(130f)))
                { ConstructorSetup.ScanPropLibrary(); Reload(); }
                if (GUILayout.Button("Oz-denetim (29)", EditorStyles.toolbarButton, GUILayout.Width(110f)))
                    EditorUtility.DisplayDialog("Oz-Denetim", ConstructorSetup.SelfCheck(), "Tamam");
            }
        }

        void DrawSummary()
        {
            int total = 0, palette = 0, hidden = 0, filtered = 0;
            foreach (var p in _lib.props)
            {
                if (p == null) continue;
                total++;
                if (p.hiddenInPalette) hidden++;
                else if (InPalette(p)) palette++;
                else filtered++;
            }

            EditorGUILayout.HelpBox(
                $"{total} prop  ·  paletde {palette}  ·  elle gizlenen {hidden}  ·  kural geregi elenen {filtered}\n" +
                "Kural geregi elenenlerin SEBEBI kendi satirlarinda yaziyor. Toplu liste: menu 51.",
                palette > 0 ? MessageType.Info : MessageType.Warning);

            // Sessizce elenmis prop varsa BURADA soyle. Kullanicinin bu pencerede 12 prop
            // gorup gozlukte 10 bulmasinin sebebi, bu sayinin hicbir yerde yazmamasiydi.
            if (filtered > 0)
            {
                var lines = new List<string>();
                foreach (var p in _lib.props)
                {
                    if (p == null || p.hiddenInPalette) continue;
                    string why = ConstructorSession.WhyNotPlaceable(p);
                    if (why != null) lines.Add($"{p.displayName ?? p.id}: {why}");
                }
                if (lines.Count > 0)
                    EditorGUILayout.HelpBox(
                        "Bu proplar KUTUPHANEDE var ama insa modu paletinde CIKMAZ:\n - " +
                        string.Join("\n - ", lines), MessageType.Warning);
            }

            if (_problems.Count > 0)
                EditorGUILayout.HelpBox("Dogrulama:\n - " + string.Join("\n - ", _problems), MessageType.Warning);
        }

        // ------------------------------------------------------------- ekleme

        /// <summary>
        /// Drag prefabs in to add them.
        ///
        /// Deliberately separate from the folder scan: the scan is for pulling in a whole art
        /// pack, this is for "I made one thing and want it in the palette". Going through the
        /// scan for a single prefab would mean moving the file into a watched folder first.
        /// </summary>
        void DrawDropArea()
        {
            var rect = GUILayoutUtility.GetRect(0f, 46f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "Prefablari buraya SURUKLE — kutuphaneye eklenir (olculeri prefabtan alinir)",
                EditorStyles.helpBox);

            var e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                e.Use();
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                int added = 0, skipped = 0;
                foreach (var o in DragAndDrop.objectReferences)
                {
                    var go = o as GameObject;
                    if (go == null) { skipped++; continue; }
                    if (AddPrefab(go)) added++; else skipped++;
                }
                if (added > 0) Save();
                ShowNotification(new GUIContent(added + " eklendi" + (skipped > 0 ? ", " + skipped + " atlandi" : "")));
                e.Use();
            }
        }

        bool AddPrefab(GameObject prefab)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) return false;                    // sahne nesnesi
            if (prefab.GetComponentInChildren<Renderer>() == null) return false;  // gorselsiz

            string id = ConstructorSetup.IdFor(prefab.name);
            foreach (var p in _lib.props)
                if (p != null && p.id == id) return false;                   // zaten var

            var size = ConstructorSetup.MeasureFootprint(path, out float height);
            var def = new PropDef
            {
                id = id,
                displayName = prefab.name,
                prefab = prefab,
                category = ConstructorSetup.GuessCategoryFor(prefab.name),
                snap = PropSnap.Floor,
                sizeMeters = size,
                height = height,
            };

            var list = new List<PropDef>(_lib.props) { def };
            // KIMLIGE GORE SIRALI kalmali: ag mesajlari kutuphane INDEKSINI tasiyor ve tarama
            // araci da ayni sirayi uretiyor. Sira iki arac arasinda ayrilirsa iki istemci ayni
            // indeksten farkli prop anlar.
            list.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            _lib.props = list.ToArray();
            _lib.contentVersion++;
            return true;
        }

        // ------------------------------------------------------------- liste

        void DrawList()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Artik PALETE gore gruplaniyor, kategoriye gore degil: insa modunda carkin
            // dilimleri paletler, ve bu listenin oradaki duzeni yansitmasi gerekiyor.
            var order = new List<string>();
            if (_lib.palettes != null)
                foreach (var pal in _lib.palettes)
                    if (pal != null) order.Add(pal.id);
            order.Add("");   // paletsizler en sona

            foreach (var pid in order)
            {
                bool header = false;
                for (int i = 0; i < _lib.props.Length; i++)
                {
                    var p = _lib.props[i];
                    if (p == null || (p.paletteId ?? "") != pid || !Matches(p)) continue;

                    if (!header)
                    {
                        header = true;
                        EditorGUILayout.Space(4f);
                        EditorGUILayout.LabelField(
                            _lib.PaletteName(pid) + (pid == "" ? "  (carkta DIGER dilimi)" : ""),
                            EditorStyles.boldLabel);
                    }
                    DrawRow(p, i);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>"Tum paletler", "Paletsiz", sonra her palet — indeks 0 filtresizdir.</summary>
        string[] PaletteFilterNames()
        {
            var names = new List<string> { "Tum paletler", "DIGER (paletsiz)" };
            if (_lib.palettes != null)
                foreach (var pal in _lib.palettes)
                    if (pal != null)
                        names.Add(string.IsNullOrEmpty(pal.displayName) ? pal.id : pal.displayName);
            return names.ToArray();
        }

        /// <summary>Palet filtresinin karsiligi: 0 = paletsiz, 1+ = o palet.</summary>
        bool MatchesPaletteFilter(PropDef p)
        {
            if (_paletteFilter < 0) return true;
            if (_paletteFilter == 0) return string.IsNullOrEmpty(p.paletteId);
            int i = _paletteFilter - 1;
            if (_lib.palettes == null || i >= _lib.palettes.Length || _lib.palettes[i] == null) return true;
            return p.paletteId == _lib.palettes[i].id;
        }

        bool Matches(PropDef p)
        {
            if (_categoryFilter >= 0 && (int)p.category != _categoryFilter) return false;
            if (!MatchesPaletteFilter(p)) return false;
            if (_onlyPalette && !InPalette(p)) return false;
            if (string.IsNullOrEmpty(_search)) return true;
            return p.id.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (p.displayName ?? "").IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void DrawRow(PropDef p, int index)
        {
            bool inPalette = InPalette(p);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Gorunurluk: paletten cikarmanin yolu SILMEK degil gizlemek. Girdiyi
                    // silmek sonraki tum ag indekslerini kaydirir ve o propu kullanan kayitli
                    // haritalari sessizce bosaltir.
                    bool show = !p.hiddenInPalette;
                    bool newShow = GUILayout.Toggle(show, show ? "GORUNUR" : "gizli", "Button", GUILayout.Width(70f));
                    if (newShow != show) { p.hiddenInPalette = !newShow; Save(); }

                    EditorGUILayout.LabelField(new GUIContent(p.displayName ?? p.id, p.id),
                        inPalette ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(170f));

                    var cat = (PropCategory)EditorGUILayout.EnumPopup(p.category, GUILayout.Width(80f));
                    if (cat != p.category) { p.category = cat; Save(); }

                    // Palet secici: 0 = paletsiz (her sette gorunur), 1+ = o palet.
                    int cur = PaletteIndexOf(p.paletteId);
                    int pick = EditorGUILayout.Popup(cur, PaletteChoiceNames(), GUILayout.Width(110f));
                    if (pick != cur)
                    {
                        p.paletteId = pick == 0 ? "" : _lib.palettes[pick - 1].id;
                        Save();
                    }

                    EditorGUILayout.LabelField(FootprintText(p), GUILayout.Width(150f));

                    bool free = GUILayout.Toggle(p.freeRotation, "5°", "Button", GUILayout.Width(40f));
                    if (free != p.freeRotation) { p.freeRotation = free; Save(); }

                    bool fit = GUILayout.Toggle(p.fitToFootprint, "fit", "Button", GUILayout.Width(40f));
                    if (fit != p.fitToFootprint) { p.fitToFootprint = fit; Save(); }

                    if (GUILayout.Button("Sec", GUILayout.Width(38f)) && p.prefab != null)
                        EditorGUIUtility.PingObject(p.prefab);

                    if (GUILayout.Button("Sil", GUILayout.Width(38f))) Remove(p, index);
                }

                string warn = WarningFor(p, inPalette);
                if (!string.IsNullOrEmpty(warn))
                    EditorGUILayout.LabelField("    " + warn, EditorStyles.miniLabel);
            }
        }

        /// <summary>Satirdaki acilir kutunun secenekleri: paletsiz + tum paletler.</summary>
        string[] PaletteChoiceNames()
        {
            var names = new List<string> { "DIGER" };
            if (_lib.palettes != null)
                foreach (var pal in _lib.palettes)
                    if (pal != null)
                        names.Add(string.IsNullOrEmpty(pal.displayName) ? pal.id : pal.displayName);
            return names.ToArray();
        }

        /// <summary>
        /// Dropdown index for a prop's palette. An id that no longer resolves falls back to 0
        /// ("her sette") so the control stays usable — the real problem is reported by
        /// <see cref="PropLibrary.Validate"/>, which says the prop is currently unreachable.
        /// </summary>
        int PaletteIndexOf(string paletteId)
        {
            if (string.IsNullOrEmpty(paletteId) || _lib.palettes == null) return 0;
            for (int i = 0; i < _lib.palettes.Length; i++)
                if (_lib.palettes[i] != null && _lib.palettes[i].id == paletteId) return i + 1;
            return 0;
        }

        void Remove(PropDef p, int index)
        {
            bool ok = EditorUtility.DisplayDialog("Kutuphaneden sil",
                $"'{p.id}' KALICI olarak silinsin mi?\n\n" +
                "Ag mesajlari propları kutuphane INDEKSIYLE tasiyor: silmek sonraki tum " +
                "indeksleri kaydirir ve bu propu iceren kayitli haritalar onu kaybeder.\n\n" +
                "Yalnizca paletten cikarmak istiyorsan GORUNUR dugmesini kapat — girdi kalir, " +
                "eski haritalar bozulmaz.",
                "Yine de sil", "Vazgec");
            if (!ok) return;

            var list = new List<PropDef>(_lib.props);
            list.RemoveAt(index);
            _lib.props = list.ToArray();
            _lib.contentVersion++;
            Save();
        }

        // ------------------------------------------------------------- bilgi

        /// <summary>Same filter <see cref="ConstructorSession.Placeable"/> applies at runtime.</summary>
        /// <summary>
        /// Whether the prop actually reaches the build wheel — asked of the RUNTIME gate, not
        /// re-derived here.
        ///
        /// This window used to carry its own copy of the rule, with its own 4 m constant, and the
        /// two drifted exactly as far as you would expect: the library said KIYAMET had 12 props
        /// and the wheel in the headset showed 10, with nothing anywhere naming the two that fell
        /// out. A tool whose job is to explain the library cannot answer from a second copy of
        /// the library's rules.
        /// </summary>
        static bool InPalette(PropDef p) => ConstructorSession.WhyNotPlaceable(p) == null;

        static string FootprintText(PropDef p)
        {
            var cells = RoomGrid.FootprintCells(p, 0, RoomGrid.DefaultCellSize);
            return $"{p.sizeMeters.x:0.00} x {p.sizeMeters.y:0.00} m  ({cells.x}x{cells.y} hucre)";
        }

        /// <summary>
        /// Why this prop is not in the palette, or what the grid will do to it once placed.
        ///
        /// The rules are all defensible on their own but invisible together, and every one of
        /// them has cost time at some point: a prop missing because of a size limit nobody
        /// remembered, a wall silently thickened to fill its cell, a footprint rounded half a
        /// cell off the mesh.
        /// </summary>
        string WarningFor(PropDef p, bool inPalette)
        {
            if (p.prefab == null && string.IsNullOrEmpty(p.resourcePath))
                return "prefab YOK — hicbir yerde gorunmez";

            // Elenme sebebi calisma zamani kapisindan geliyor; burada YENIDEN TURETILMIYOR
            // (bkz. InPalette). Emekli olan prop zaten kendi dugmesiyle isaretli, onu tekrar
            // uyari olarak yazmak gurultu olurdu.
            if (!inPalette)
            {
                if (p.hiddenInPalette) return "";
                return ConstructorSession.WhyNotPlaceable(p) + " — PALETTE CIKMAZ";
            }

            var notes = new List<string>();

            // Fit, hucreden ince ekseni bir hucreye buyutur (MapBuilder.FitAxis).
            if (p.fitToFootprint)
            {
                var mesh = p.MeshFootprintMeters;
                float cell = RoomGrid.DefaultCellSize;
                if (mesh.y > 0.0001f && mesh.y < cell * 0.999f)
                    notes.Add($"kalinlik {mesh.y * 100f:0.0} -> {cell * 100f:0.0} cm'ye sisecek (dik eklem bosluk birakmasin diye)");
                if (mesh.x > 0.0001f)
                {
                    float drift = Mathf.Abs(mesh.x - p.sizeMeters.x) / mesh.x;
                    if (drift > PropLibrary.FitMismatchTolerance)
                        notes.Add($"sizeMeters ile mesh %{drift * 100f:0} ayri — fit uygulanmaz, yan yana bosluk kalir");
                }
            }

            return notes.Count == 0 ? "" : string.Join("   ·   ", notes);
        }

        // ------------------------------------------------------------- kayit

        void Save()
        {
            _lib.InvalidateIndex();
            EditorUtility.SetDirty(_lib);
            AssetDatabase.SaveAssets();

            // Play modunda acik bir oturum varsa suzgeclerini de tazele: palet duzenlemenin
            // gozluge yansimasi icin oyunu yeniden baslatmak gerekmesin.
            if (Application.isPlaying && ConstructorSession.Instance != null)
                ConstructorSession.Instance.InvalidatePlaceable();

            _problems.Clear();
            _problems.AddRange(_lib.Validate());
        }

        void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField($"icerik surumu {_lib.contentVersion}", EditorStyles.miniLabel,
                    GUILayout.Width(120f));
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(
                    "Paletten cikarmak icin GORUNUR'u kapat — silmek ag indekslerini kaydirir.",
                    EditorStyles.miniLabel);
            }
        }
    }
}
