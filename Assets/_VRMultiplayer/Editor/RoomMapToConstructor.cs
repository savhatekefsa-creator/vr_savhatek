using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   46. RoomMap'i Kayitli Haritaya Cevir — sahneye PISMIS arenayi
    /// (<see cref="RoomMapDecorator"/>'un dokundugu <c>RoomMap</c> agaci) kayitli bir
    /// <see cref="MapLayout"/>'a cevirir ve havuza koyar.
    ///
    /// NEDEN GEREKLI: sahnedeki arena Unity mesh'i, harita havuzu ise prop YERLESIM verisi.
    /// Ikisi ayri sistemler — oda sahnede duruyor olmasi onu "kayitli harita" yapmiyor, ve
    /// havuz bos gorunuyor. Bu arac aradaki tek koprü.
    ///
    /// CEVIRININ UC KAYIPLI YERI VAR, ucu de bicimin zorlamasi:
    ///
    ///  1. ACI. <see cref="MapLayout.RotationStepDegrees"/> 15 derece. Taranmis oda elle
    ///     hizalandigi icin duvarlar eksenden 2 dereceye kadar sapiyor; en yakin adim hepsini
    ///     ceyrek tura oturtuyor. Uzun duvarda ucun kaydigi mesafe raporda yaziyor.
    ///
    ///  2. UZUNLUK. <see cref="PlacedProp.scalePct"/> bir BYTE, yani en fazla %255 —
    ///     <c>wall_solid</c> icin 2.55 m. Daha uzun duvarlar es parcalara bolunup uc uca
    ///     diziliyor. Parcalar ayni hucre kafesine oturdugu icin aralarinda dikis kalmiyor.
    ///
    ///  3. KALINLIK. <see cref="PlacedProp.ScaleVector"/> yerel Z'yi 1'e sabitliyor (bir siperi
    ///     uzatmak istersin, sismanlatmak istemezsin). Gercek duvar 12 cm, <c>wall_solid</c>
    ///     25 cm: her yuzde ~6.5 cm sisme. Duzeltilemez, sadece bilinir.
    ///
    /// KOORDINAT KORUNUR: her prop dunya merkezinden hucreye cevriliyor, hucreden yeniden
    /// dunyaya donusteki fark olculup raporlaniyor. Yani cevrilen harita, sahnedeki arenanin
    /// DURDUGU YERDE duruyor — kalibrasyon cercevesi degisirse ikisi birlikte kayar.
    /// </summary>
    public static class RoomMapToConstructor
    {
        const string MapRootName = "RoomMap";

        /// <summary>Cevirinin yazildigi harita adi. <c>Current</c> DEGIL: oyuncunun uzerinde
        /// calistigi haritayi ezmek, geri donusu olmayan tek hata olurdu.</summary>
        public const string DefaultTargetMap = "Arena";

        /// <summary>
        /// Bir parcanin en fazla kac metre olabilecegi. Tavan 2.55 m (%255 x 1.00 m); 2.40
        /// yuzde yuvarlamasi ve hucreye oturma icin pay birakiyor.
        /// </summary>
        const float MaxSegmentMeters = 2.40f;

        /// <summary>Kutuphanede ADI TUTMAYAN parcalar. Sol taraf sahnedeki ad (kucuk harf).</summary>
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "masa", "table_low" },   // gercek masa: arenada siper olarak kullaniliyor
        };

        /// <summary>
        /// Bilerek cevrilmeyenler ve gerekceleri. Atlanan her sey rapora yaziliyor — sessiz
        /// eksik, yanlis cevrilmis proptan daha kotu.
        /// </summary>
        static readonly Dictionary<string, string> Skips = new Dictionary<string, string>
        {
            // RoomMapDecorator'un kendi yorumu: bu panolar COLLIDERSIZ, cunku cikinti yapan
            // gercek siper koridoru navmesh vokselinde muhurluyor ve fiziksel gecisi
            // daraltiyordu. Constructor propunun collider'i var — cevirmek o hatayi geri
            // getirirdi.
            { "koridorpano_bati", "collidersiz gorsel pano — prop'a cevrilse koridoru kapatir" },
            { "koridorpano_dogu", "collidersiz gorsel pano — prop'a cevrilse koridoru kapatir" },
        };

        [MenuItem("Tools/VR Multiplayer/46. RoomMap'i Kayitli Haritaya Cevir")]
        public static void ConvertMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Convert(DefaultTargetMap), "Tamam");

        public static string Convert(string targetName)
        {
            var root = FindSceneObject(MapRootName);
            if (root == null)
                return "Sahnede '" + MapRootName + "' yok — cevrilecek arena bulunamadi.";

            var lib = PropLibrary.Instance;
            if (lib == null || lib.props == null || lib.props.Length == 0)
                return "PropLibrary bulunamadi.";

            var wallDef = lib.ById("wall_solid");
            if (wallDef == null) return "Kutuphanede 'wall_solid' yok — duvarlar cevrilemez.";

            var layout = new MapLayout
            {
                name = targetName,
                createdBy = "RoomMapToConstructor",
                cellSize = RoomGrid.DefaultCellSize,
                levelHeight = MapLayout.DefaultLevelHeight,
                buildMargin = RoomGrid.DefaultOutsideMargin,
                inPool = true,
                builtForRoom = SourceRoom(out string roomNote),
            };

            var grid = RoomGrid.FromPlan(layout.builtForRoom, layout.cellSize,
                RoomGrid.DefaultWallMargin, layout.buildMargin, layout.levelHeight);
            if (grid == null) return "Oda planindan izgara kurulamadi.";

            var rep = new Report();

            foreach (string wallsName in new[] { "Walls", "Walls2" })
            {
                var walls = root.transform.Find(wallsName);
                if (walls == null) continue;
                foreach (Transform w in walls) ConvertWall(w, layout, grid, wallDef, rep);
            }

            foreach (string holderName in new[] { "Props", "Furniture", "Furniture2" })
            {
                var holder = root.transform.Find(holderName);
                if (holder == null) continue;
                foreach (Transform p in holder) ConvertProp(p, layout, grid, lib, rep);
            }

            if (layout.Count == 0) return "Cevrilecek hicbir sey bulunamadi.";

            string backup = BackUpExisting(targetName);
            if (!layout.Save(targetName)) return "Harita KAYDEDILEMEDI — Console'a bak.";

            AssetDatabase.Refresh();
            return rep.Text(targetName, layout, roomNote, backup);
        }

        // ------------------------------------------------------------------ duvarlar

        /// <summary>
        /// Bir duvar kutusunu bir ya da daha cok <c>wall_solid</c> parcasina cevirir.
        ///
        /// Kutunun UZUNLUGU yerel Z'de, KALINLIGI yerel X'te (taranmis oda boyle kuruldu).
        /// <c>wall_solid</c> ise GENISLIGINI yerel X'te tasiyor — bu yuzden propun yaw'i
        /// duvarinkinden 90 derece farkli. Ceyrek tur farki oldugu icin ayak izi tam takla
        /// atiyor, kesir olusmuyor.
        /// </summary>
        static void ConvertWall(Transform w, MapLayout layout, RoomGrid grid, PropDef def, Report rep)
        {
            Vector3 s = Abs(w.lossyScale);
            float length = s.z, height = s.y;
            if (length < 0.02f || height < 0.02f) { rep.Skip(w.name, "olcusu sifira yakin"); return; }

            float wallYaw = w.rotation.eulerAngles.y;
            byte rot = RotStep(wallYaw + 90f);
            rep.NoteAngle(wallYaw + 90f, rot, length, true);

            byte heightPct = Pct(height / def.height);
            float bottom = w.position.y - height * 0.5f;
            byte level = LevelFor(bottom, grid, layout);

            int segments = Mathf.Max(1, Mathf.CeilToInt(length / MaxSegmentMeters));
            float segLength = length / segments;
            int cells = Mathf.Max(1, Mathf.RoundToInt(segLength / grid.CellSize));
            byte scalePct = RoomGrid.WidthPctForCells(def, grid.CellSize, cells);

            // Uzunluk ekseni yerel +Z. Parcalar bu eksende es araliklarla diziliyor.
            Vector3 axis = w.rotation * Vector3.forward;
            for (int i = 0; i < segments; i++)
            {
                Vector3 center = w.position + axis * (((i + 0.5f) / segments - 0.5f) * length);
                Place(layout, grid, def, center, rot, scalePct, heightPct, level, rep, true);
            }
            rep.walls++;
            rep.wallPieces += segments;
        }

        // ------------------------------------------------------------------ proplar

        static void ConvertProp(Transform t, MapLayout layout, RoomGrid grid, PropLibrary lib, Report rep)
        {
            string key = NormalizeName(t.name);

            if (Skips.TryGetValue(key, out string why)) { rep.Skip(t.name, why); return; }
            if (Aliases.TryGetValue(key, out string alias)) key = alias;

            var def = lib.ById(key);
            if (def == null) { rep.Skip(t.name, "kutuphanede '" + key + "' yok"); return; }

            var src = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) as GameObject;

            // OLCU PREFABDAN DA GELEBILIR. Sahnedeki kopyanin mesh'i eksik olabiliyor:
            // RoomMapDecorator.PlaceVisual "islevli" prefablarin butun betiklerini soküyor ve
            // silah rafinin MeshFilter'i bu islemden bos cikiyor (olculdu: raf sahnede sifir
            // kutu, prefabi 1.26 x 1.40 x 0.13). Sifir kutuyu atlamak rafi — yani arenanin TEK
            // silah kaynagini — sessizce dusuruyordu. Sekli tanimlayan zaten prefab; sahnedeki
            // kopya yalnizca nerede durdugunu soyluyor.
            var local = PropDef.MeasureLocalBounds(t.gameObject);
            if (local.size.y < 0.001f && src != null) local = PropDef.MeasureLocalBounds(src);
            if (local.size.y < 0.001f) { rep.Skip(t.name, "ne kopyada ne prefabda olculebilir mesh var"); return; }

            // OLCEK PREFABA GORE, olculen boya gore DEGIL. Kutuphanedeki sizeMeters her zaman
            // olculmus bir sayi degil (dogum halkasinda 1.00 m beyan, mesh 2.40 m); olculen boyu
            // beyana bolmek o proplari iki bucuk katina sisirirdi. Sahnedeki ornegin prefabinin
            // kendi olceginden sapmasi ise tam olarak dekoratorun uyguladigi carpan.
            Vector3 baseScale = src != null ? Abs(src.transform.localScale) : Vector3.one;
            Vector3 own = Abs(t.lossyScale);

            byte scalePct = Pct(baseScale.x > 1e-4f ? own.x / baseScale.x : 1f);
            byte heightPct = Pct(baseScale.y > 1e-4f ? own.y / baseScale.y : 1f);

            byte rot = RotStep(t.rotation.eulerAngles.y);
            rep.NoteAngle(t.rotation.eulerAngles.y, rot, Mathf.Max(local.size.x, local.size.z) * own.x, false);

            Vector3 center = t.TransformPoint(local.center);
            float bottom = center.y - local.size.y * own.y * 0.5f;
            Place(layout, grid, def, center, rot, scalePct, heightPct, LevelFor(bottom, grid, layout), rep, false);
            rep.props++;
        }

        // ------------------------------------------------------------------ yerlestirme

        /// <summary>
        /// Dunya merkezini hucre koordinatina cevirip yerlestirir, ve hucreden dunyaya GERI
        /// donusteki farki olcer. Tavan iki eksende birden yarim hucre, yani
        /// sqrt(2) x 3.125 = 4.4 cm — tek eksendeki 3.1 cm DEGIL; rapor 2B mesafeyi yaziyor.
        /// Bunun uzerine cikan bir sayi cevirinin degil, buradaki varsayimin bozuk oldugunu
        /// soyler.
        /// </summary>
        static void Place(MapLayout layout, RoomGrid grid, PropDef def, Vector3 worldCenter,
            byte rot, byte scalePct, byte heightPct, byte level, Report rep, bool isWall)
        {
            Vector2Int size = RoomGrid.FootprintCells(def, rot, grid.CellSize, scalePct);
            int cx = Mathf.RoundToInt((worldCenter.x - grid.Origin.x) / grid.CellSize - size.x * 0.5f);
            int cz = Mathf.RoundToInt((worldCenter.z - grid.Origin.y) / grid.CellSize - size.y * 0.5f);

            layout.Add(def.id, cx, cz, level, rot, scalePct, heightPct);

            Vector3 back = grid.RectCenter(new RectInt(cx, cz, size.x, size.y), level, layout.levelHeight);
            rep.NoteDrift(new Vector2(back.x - worldCenter.x, back.z - worldCenter.z).magnitude, isWall);
            rep.Extend(worldCenter);
        }

        static byte LevelFor(float bottom, RoomGrid grid, MapLayout layout) => (byte)Mathf.Clamp(
            Mathf.RoundToInt((bottom - grid.FloorY) / layout.levelHeight), 0, RoomGrid.MaxLevels - 1);

        // ------------------------------------------------------------------ oda plani

        /// <summary>
        /// Cevirinin uzerine oturacagi oda. Once <c>Current</c> haritasininki denenir: ayni
        /// plan, ayni izgara kokeni demek, yani iki harita BIT BIT ayni kafese oturur ve ayni
        /// odada karsilastirilabilir. Yoksa origine oturan 12 m'lik kare uretilir.
        ///
        /// Secim, propların DUNYA konumunu etkilemez — hucreler bu izgaradan turetiliyor ve
        /// plan haritanin icine gomuluyor, yani yuklenirken ayni koken yeniden cikiyor. Plan
        /// yalnizca hangi hucrelerin "oda ici / yurunebilir" sayildigini belirler.
        /// </summary>
        static RoomPlan SourceRoom(out string note)
        {
            var current = MapLayout.Load("Current");
            if (current != null && current.HasRoom)
            {
                note = "oda plani 'Current' haritasindan alindi (ayni izgara)";
                return current.builtForRoom;
            }

            note = "'Current' yok — origine oturan 12 m'lik kare oda uretildi";
            return new RoomPlan
            {
                floorPolygon = new[]
                {
                    new Vector2(-6f, -6f), new Vector2(6f, -6f),
                    new Vector2(6f, 6f), new Vector2(-6f, 6f),
                },
                floorY = 0f,
                ceilingY = 3f,
            };
        }

        // ------------------------------------------------------------------ yardimcilar

        /// <summary>
        /// Uzerine yazmadan once yedek — geri donusu yalnizca bu saglar.
        ///
        /// ALT KLASORE, Maps'in kendisine DEGIL: <see cref="MapLayout.List"/> klasordeki her
        /// <c>*.json</c>'u bir harita sayiyor (ozyinelemesiz), yani yan yana birakilan bir yedek
        /// katalogda ayri bir harita gibi gorunur — ve <c>inPool</c> alani da kopyalandigi icin
        /// dogrudan HAVUZA girer. Oyuncu kendini "Arena_onceki"de bulurdu.
        /// </summary>
        static string BackUpExisting(string targetName)
        {
            string path = MapLayout.PathFor(targetName);
            if (!System.IO.File.Exists(path)) return null;

            try
            {
                string dir = MapLayout.Directory + "/Yedek";
                System.IO.Directory.CreateDirectory(dir);
                string backup = dir + "/" + MapLayout.Sanitize(targetName) + "_onceki.json";
                System.IO.File.Copy(path, backup, true);
                return backup;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[RoomMap2Map] Yedek alinamadi: " + e.Message);
                return null;
            }
        }

        /// <summary>"Barrier_1 (2)" -> "barrier_1". Kutuphane kimlikleri kucuk harf ve alt
        /// cizgili; sahnedeki kopya sayaclari kimligin parcasi degil.</summary>
        static string NormalizeName(string name)
        {
            int paren = name.IndexOf(" (");
            if (paren > 0) name = name.Substring(0, paren);
            return name.Trim().ToLowerInvariant().Replace(' ', '_');
        }

        static byte RotStep(float yaw)
        {
            int step = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / MapLayout.RotationStepDegrees);
            return (byte)(step % MapLayout.RotationSteps);
        }

        static byte Pct(float ratio) => (byte)Mathf.Clamp(Mathf.RoundToInt(ratio * 100f), 1, 255);

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>GameObject.Find kapali objeleri bulamaz — arena cevrilirken kapali olabilir
        /// (bu dalda main'in dekoru kapatildi), o yuzden sahne koklerinden taranir.</summary>
        static GameObject FindSceneObject(string name)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            return null;
        }

        // ------------------------------------------------------------------ rapor

        /// <summary>
        /// DUVAR VE PROP AYRI SAYILIYOR, ve bu bir suslemé degil: ilk surumde tek bir "en kotu"
        /// vardi ve 5 derece / 11 cm yaziyordu — sayi bir CALIDAN geliyordu (40 derecede duran
        /// Bush_02, en yakin adim 45). Duvarlarin hepsi ceyrek tura 2.1 derecenin altinda
        /// oturuyor. Tek sayi, odanin sekli 11 cm bozuldu diye okunuyordu; oysa bozulan bir
        /// calinin hangi yone baktigiydi. Karisik olcunun en kotu hali budur: dogru sayi, yanlis
        /// sey hakkinda.
        /// </summary>
        class Report
        {
            public int walls, wallPieces, props;
            readonly List<string> _skipped = new List<string>();
            Vector3 _min = Vector3.positiveInfinity, _max = Vector3.negativeInfinity;

            struct Worst
            {
                public float angleDeg, angleCm, driftCm;

                public void Angle(float wanted, byte rot, float length)
                {
                    float got = rot * MapLayout.RotationStepDegrees;
                    float err = Mathf.Abs(Mathf.DeltaAngle(Mathf.Repeat(wanted, 360f), got));
                    if (err <= angleDeg) return;
                    angleDeg = err;
                    // Duvar MERKEZI etrafinda donuyor, yani bir ucun kaydigi mesafe yarim boy x
                    // sin(hata) — tam boy degil. Tam boyla yazmak sapmayi iki kat gosteriyordu
                    // (13 cm derken olculen 5.4 cm idi), ve bu haritanin kabul edilip
                    // edilmeyecegine bakilan sayi.
                    angleCm = length * 0.5f * Mathf.Sin(err * Mathf.Deg2Rad) * 100f;
                }

                public void Drift(float meters) => driftCm = Mathf.Max(driftCm, meters * 100f);
            }

            Worst _wall, _prop;

            public void Skip(string name, string why) => _skipped.Add(name + " — " + why);

            public void NoteAngle(float wanted, byte rot, float length, bool isWall)
            {
                if (isWall) _wall.Angle(wanted, rot, length); else _prop.Angle(wanted, rot, length);
            }

            public void NoteDrift(float meters, bool isWall)
            {
                if (isWall) _wall.Drift(meters); else _prop.Drift(meters);
            }

            public void Extend(Vector3 p) { _min = Vector3.Min(_min, p); _max = Vector3.Max(_max, p); }

            public string Text(string mapName, MapLayout layout, string roomNote, string backup)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("'" + mapName + "' olusturuldu ve HAVUZA kondu.");
                sb.AppendLine();
                sb.AppendLine("Duvar          : " + walls + " adet -> " + wallPieces + " parca");
                sb.AppendLine("Prop           : " + props);
                sb.AppendLine("Toplam yerlesim: " + layout.Count);
                sb.AppendLine();
                sb.AppendLine("DUVARLAR — odanin sekli:");
                sb.AppendLine("  aci yuvarlamasi: en fazla " + _wall.angleDeg.ToString("0.0") +
                              " derece  (duvar ucunda " + _wall.angleCm.ToString("0") + " cm)");
                sb.AppendLine("  hucreye oturma : en fazla " + _wall.driftCm.ToString("0.0") + " cm");
                sb.AppendLine("  kalinlik       : 12 cm -> 25 cm (her yuzde ~6.5 cm)");
                sb.AppendLine("PROPLAR — siper ve bitki:");
                sb.AppendLine("  aci yuvarlamasi: en fazla " + _prop.angleDeg.ToString("0.0") + " derece");
                sb.AppendLine("  hucreye oturma : en fazla " + _prop.driftCm.ToString("0.0") + " cm");
                sb.AppendLine();
                sb.AppendLine("Kapladigi alan : x[" + _min.x.ToString("0.0") + ".." + _max.x.ToString("0.0") +
                              "]  z[" + _min.z.ToString("0.0") + ".." + _max.z.ToString("0.0") + "]  m");
                sb.AppendLine(roomNote);

                if (_skipped.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Cevrilmeyen (" + _skipped.Count + "):");
                    foreach (string s in _skipped) sb.AppendLine("  " + s);
                }

                if (!MapCatalog.CanEnterPool(layout, out string block))
                    sb.AppendLine("\nUYARI — havuz reddedebilir: " + block);

                if (backup != null) sb.AppendLine("\nOnceki surumun yedegi: " + backup);
                sb.AppendLine("\nSunucu ACIKSA harita bellekte; degisikligi gormek icin oturumu yeniden baslat.");
                return sb.ToString();
            }
        }
    }
}
