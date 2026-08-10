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
        bool _onlyPalette;
        readonly List<string> _problems = new List<string>();

        // Palet kurallarinin yerel kopyasi DEGIL: oturum acik olmadan da dogru cevap
        // verebilmek icin ConstructorSession'daki suzgecin ayni sartlarini burada tekrar
        // ediyoruz. Ikisi ayrilirsa pencere yalan soyler, o yuzden tek yerde toplandi.
        const float MaxPlaceableMetres = 4f;

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
            DrawDropArea();
            DrawList();
            DrawFooter();
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
                "Elenenler: Ground kategorisi, zemine oturmayanlar, " + MaxPlaceableMetres + " m'den buyukler, " +
                "prefabi cozulemeyenler.",
                palette > 0 ? MessageType.Info : MessageType.Warning);

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

            PropCategory? lastCat = null;
            for (int i = 0; i < _lib.props.Length; i++)
            {
                var p = _lib.props[i];
                if (p == null || !Matches(p)) continue;

                if (lastCat != p.category)
                {
                    lastCat = p.category;
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.LabelField(ConstructorPaletteUI.CategoryName(p.category).ToUpperInvariant(),
                        EditorStyles.boldLabel);
                }
                DrawRow(p, i);
            }

            EditorGUILayout.EndScrollView();
        }

        bool Matches(PropDef p)
        {
            if (_categoryFilter >= 0 && (int)p.category != _categoryFilter) return false;
            if (_onlyPalette && (p.hiddenInPalette || !InPalette(p))) return false;
            if (string.IsNullOrEmpty(_search)) return true;
            return p.id.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (p.displayName ?? "").IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void DrawRow(PropDef p, int index)
        {
            bool inPalette = !p.hiddenInPalette && InPalette(p);

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
        static bool InPalette(PropDef p) =>
            p.snap == PropSnap.Floor &&
            p.category != PropCategory.Ground &&
            p.sizeMeters.x <= MaxPlaceableMetres && p.sizeMeters.y <= MaxPlaceableMetres &&
            p.prefab != null;

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
            if (p.category == PropCategory.Ground)
                return "Ground kategorisi paletten elenir (zemin parcasi, prop degil)";
            if (p.snap != PropSnap.Floor)
                return "snap Floor degil — palet yalnizca zemine oturanlari gosterir";
            if (p.sizeMeters.x > MaxPlaceableMetres || p.sizeMeters.y > MaxPlaceableMetres)
                return $"{p.sizeMeters.x:0.00} x {p.sizeMeters.y:0.00} m, sinir {MaxPlaceableMetres} m — paletten elenir";

            if (!inPalette) return "";

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
