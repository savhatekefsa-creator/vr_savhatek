using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
// Not: UnityEditor.ProBuilder'i "using" yapMIYORUZ — icinde de bir EditorUtility var ve
// UnityEditor.EditorUtility ile cakisiyor. Tam adiyla yaziyoruz (XRButtons'daki ayni ders).
using UnityEngine.ProBuilder.MeshOperations;
using VRMultiplayer.Constructor;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   30. Yapi Kiti Uret — insa modunun temel parcalarini ProBuilder ile uretir.
    ///
    /// WHY GENERATED AND NOT BOUGHT. The constructor places on a 0.125 m lattice
    /// (<see cref="RoomGrid.DefaultCellSize"/>), and a piece only tiles cleanly when its
    /// footprint is a whole number of cells. Bought kits are authored on the artist's grid, not
    /// this one, so every piece lands a few centimetres off and the fit has to stretch it back.
    /// Declaring the dimensions here means they are cell multiples BY CONSTRUCTION — and the
    /// numbers stay legible instead of being buried in an FBX.
    ///
    /// Everything is sized for the scanned room this project targets: roughly 4.9 x 3.7 m with a
    /// 2.68 m ceiling. That is small. A full-height wall is a serious commitment of floor in a
    /// space like that, which is why the kit is a wall, a way THROUGH a wall, a waist-high
    /// piece that doubles as cover, and a railing that bounds the space without closing the view.
    ///
    /// Re-running is safe: prefabs are overwritten in place, so the library keeps its ids and
    /// every saved map that references them survives. Change a constant, run the menu again.
    /// </summary>
    public static class ConstructorKitBuilder
    {
        const string PropFolder = "Assets/_VRMultiplayer/Resources/ConstructorProps";
        const string MaterialFolder = "Assets/_VRMultiplayer/Materials";

        // Mesh'ler Resources'in DISINDA: prefab onlara referans verdigi icin builde zaten
        // giriyorlar, ama Resources altindaki her varlik build'e KOSULSUZ girer — orayi
        // yalnizca kutuphanenin yoldan yukledigi prefablara birakiyoruz.
        const string MeshFolder = "Assets/_VRMultiplayer/Meshes";

        // --- izgara ---
        // Ayak izi olculeri BUNUN katı olmali, yoksa RoomGrid en yakin hucreye yuvarlar ve
        // MapBuilder.LocalScaleFor propu o kadar gerer (birkac cm, gorunmez ama gereksiz).
        const float Cell = RoomGrid.DefaultCellSize;   // 0.125 m

        // --- duvar ---
        const float WallLength = 1.0f;    // 8 hucre
        const float WallThick = 0.25f;    // 2 hucre — bir hucrenin altina inersen fit onu
                                          // 0.125'e sisirir (bkz. MapBuilder.FitAxis)
        const float WallHeight = 2.2f;    // tavan 2.68: ustunden atlanamaz, basi da sikismaz

        // --- kapili duvar ---
        // Kanat genisligi (1.5 - 0.875) / 2 = 0.3125 m. Ayak izi 12 x 2 hucre; ic olculerin
        // hucreye oturmasi gerekmiyor, izgarayi ilgilendiren yalnizca dis dikdortgen.
        const float DoorWallLength = 1.5f;   // 12 hucre
        const float DoorWidth = 0.875f;      // VR'da rahat gecis; 0.75 dar geliyor
        const float DoorHeight = 2.0f;       // ustunde 0.2 m lento kalir

        // --- boru korkuluk ---
        // SINIR, DUVAR DEGIL. Bu parcanin isi oyuncuyu bir hattin gerisinde tutmak ama arkasini
        // GOSTERMEYE devam etmek — cati kenarinda sehri, gercek odada da acikligi. Iki yatay
        // boru ve iki dikmeden ibaret olmasinin sebebi bu: dolu bir levha ayni durdurmayi yapar
        // ve manzarayi da goturur.
        //
        // 1.10 m GOGUS HIZASI, kasten. Diz hizasi bir korkuluk govdeyi durdurmaz — oyuncu
        // ustunden egilir, ve VR'da sanal engelin gerisinde duran sey gercek duvar oldugu icin
        // bu elini duvara vurmasi demek. Gogus hizasinda govde daha once durur; goz hizasinin
        // altinda kaldigi icin de gorusu kapatmaz.
        const float RailLength = 1.0f;    // 8 hucre — Wall_Solid ile ayni modul, yan yana dizilir
        const float RailHeight = 1.10f;
        const float RailPipe = 0.05f;     // boru kesiti
        const float RailPostW = 0.09f;    // dikme genisligi

        // Dikme DERINLIGI tam bir hucre — sabit bir sayi DEGIL, Cell'in kendisi. Ince eksen
        // bunun altina indiginde MapBuilder.FitAxis onu zaten bir hucreye buyutur (izgara bir
        // hucreden inceyi rezerve edemez), yani elle 4 cm yazsaydik borular oyunda yassilmis
        // cikardi. Hucreye baglamak o buyutmeyi gereksiz kilar: ne rezerve ediliyorsa gorunen o
        // — ve hucre boyu bir daha degistiginde (0.125 -> 0.0625 bir kez oldu) korkuluk
        // kendiliginde dogru kalir.
        const float RailDepth = Cell;

        // --- masa ---
        const float TableLength = 1.0f;   // 8 hucre
        const float TableDepth = 0.5f;    // 4 hucre
        const float TableHeight = 0.75f;
        const float TopThick = 0.06f;
        const float LegThick = 0.08f;
        const float LegInset = 0.05f;     // ayak, tabla kenarindan iceri

        // --- silah rafi ---
        // Tek parca duvar rafi: arkasinda tel izgara levha, uzerinde 16 silahin tamami.
        //
        // Levha ProBuilder'da CUBUKLARDAN oruluyor, hazir mesh'ten degil. Sahnedeki Meshy
        // panelinin gorunumu dogru ama 348.739 ucgen — Quest'te tek bir tanesi bile agir, ve
        // insa modu demek oyuncunun BIRKAC tane koyabilmesi demek. Cerceve + dikey/yatay
        // cubuklar aynisi gibi okunuyor ve ~300 ucgen tutuyor.
        // Genislik silahlarin yayilimindan geliyor, yuvarlak bir sayidan degil: 16 silah
        // 3.34 m tutuyor, 3.75'lik levha iki yanda 41 cm bos izgara birakiyordu. 3.50'de
        // (28 hucre) en distaki tufek cerceveye 6 cm kala bitiyor — levha silahlari
        // cerceveliyor, etraflarinda dolasmiyor.
        const float RackWidth = 3.5f;        // 28 hucre
        const float RackHeight = 1.625f;
        const float RackDepth = 0.125f;      // 1 hucre — daha incesi fit tarafindan sisirilir
        const float RackFrame = 0.04f;       // dis cerceve kalinligi
        const float RackBarThick = 0.022f;   // tel cubuk kalinligi
        const int RackBarsX = 14;            // dikey cubuk
        const int RackBarsY = 5;             // yatay ray

        // Silah levhanin ONUNDE ne kadar duracak. 9 cm'de havada asili duruyordu; 4 cm
        // yuzeye yaslanmis gibi okunuyor ve namlu/sarjor kalinligi hala cubuklara girmiyor.
        const float RackSlotOut = 0.04f;

        /// <summary>One weapon on the rack: name, where it hangs, which way it lies.</summary>
        struct Slot
        {
            public string weapon;
            public float x, y, yaw;   // x/y levhanin MERKEZINE gore (m), yaw yerel derece
            public Slot(string weapon, float x, float y, float yaw)
            { this.weapon = weapon; this.x = x; this.y = y; this.yaw = yaw; }
        }

        /// <summary>
        /// The rack's layout, lifted STRAIGHT off the arrangement in the scene rather than
        /// invented here — the spacing, the pairing of long guns with the pistols stacked down
        /// one side, the grenades along the bottom. Read out of the live scene once and frozen,
        /// so the prop you place is the wall you already built by hand.
        /// </summary>
        static readonly Slot[] RackSlots =
        {
            new Slot("Weapon_Smg 2",        -0.776f,  0.524f,  90f),
            new Slot("Weapon_Sniper 1",      1.142f,  0.304f,  90f),
            new Slot("Weapon_Smg 1",         0.130f,  0.313f, 270f),
            new Slot("Weapon_Pistol 2",     -1.469f,  0.313f,  90f),
            new Slot("Weapon_Smg 3",        -0.822f,  0.175f,  90f),
            new Slot("Weapon_Pistol 3",     -1.443f,  0.007f,  90f),
            new Slot("Weapon_Shotgun 2",    -0.812f, -0.098f,  90f),
            new Slot("Weapon_Rifle 3",       0.152f, -0.161f,  90f),
            new Slot("Weapon_Rifle 1",       1.255f, -0.205f,  90f),
            new Slot("Weapon_Pistol 4",     -1.443f, -0.452f,  90f),
            new Slot("Weapon_Rifle_HK416",   1.363f, -0.482f,   0f),
            new Slot("Weapon_Dmr1",          0.083f, -0.495f,  90f),
            new Slot("Weapon_Rifle 2",      -0.851f, -0.548f,  90f),
            new Slot("Weapon_Grenade 3",    -1.246f, -0.701f, 180f),
            new Slot("Weapon_Grenade 2",    -0.251f, -0.718f, 180f),
            new Slot("Weapon_Grenade 1",     0.675f, -0.723f, 180f),
        };

        /// <summary>One box of a prop, in the prop's own local space.</summary>
        struct Box
        {
            public Vector3 size;
            public Vector3 center;
            public Box(Vector3 size, Vector3 center) { this.size = size; this.center = center; }
        }

        [MenuItem("Tools/VR Multiplayer/30. Yapi Kiti Uret (ProBuilder)")]
        public static void BuildKitMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", BuildKit(), "Tamam");

        public static string BuildKit()
        {
            EnsureFolder(PropFolder);
            EnsureFolder(MaterialFolder);
            EnsureFolder(MeshFolder);

            var stone = EnsureMaterial(MaterialFolder + "/Kit_Stone.mat", new Color(0.62f, 0.61f, 0.58f));
            var wood = EnsureMaterial(MaterialFolder + "/Kit_Wood.mat", new Color(0.45f, 0.32f, 0.20f));
            // Galvaniz boru grisi — korkulugu tas duvardan ayirir, ama dikkat cekmeye
            // calismaz; hangi rengin "buraya girme" diye bagiracagi haritayi kuranin karari.
            var metal = EnsureMaterial(MaterialFolder + "/Kit_Metal.mat", new Color(0.55f, 0.57f, 0.60f));
            // Raf, sahnedeki panel gibi BEYAZ — silahlar onunde kontrastla okunuyor.
            var rackMat = EnsureMaterial(MaterialFolder + "/Kit_Rack.mat", new Color(0.90f, 0.90f, 0.88f));

            var made = new List<string>
            {
                Build("Wall_Solid", stone, SolidWall()),
                Build("Wall_Door", stone, DoorWall()),
                Build("Railing_Pipe", metal, PipeRailing()),
                Build("Table_Low", wood, Table()),

                // Silah rafi: tek parca, 16 silahin tamami uzerinde.
                BuildRack("Rack_Wall", rackMat),
            };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return "Yapi kiti uretildi:\n\n - " + string.Join("\n - ", made) +
                   "\n\nSirada: menu 25 (kutuphaneyi tara), sonra menu 29 (oz-denetim).";
        }

        // ------------------------------------------------------------- parcalar

        /// <summary>Plain wall: one slab, based at y = 0 so it stands on the floor.</summary>
        static Box[] SolidWall() => new[]
        {
            new Box(new Vector3(WallLength, WallHeight, WallThick),
                    new Vector3(0f, WallHeight * 0.5f, 0f)),
        };

        /// <summary>
        /// Wall with a way through it: two jambs and the lintel over them.
        ///
        /// Three boxes rather than a cube with a hole cut in it — the opening is rectangular and
        /// axis-aligned, so boolean geometry would buy nothing and cost a messier mesh. It also
        /// keeps the colliders honest: each box becomes one BoxCollider and the doorway is
        /// genuinely empty, instead of a mesh collider the player can get caught on.
        /// </summary>
        static Box[] DoorWall()
        {
            float jamb = (DoorWallLength - DoorWidth) * 0.5f;
            float jambX = (DoorWidth + jamb) * 0.5f;
            float lintel = WallHeight - DoorHeight;

            return new[]
            {
                new Box(new Vector3(jamb, WallHeight, WallThick),
                        new Vector3(-jambX, WallHeight * 0.5f, 0f)),
                new Box(new Vector3(jamb, WallHeight, WallThick),
                        new Vector3(jambX, WallHeight * 0.5f, 0f)),
                new Box(new Vector3(DoorWidth, lintel, WallThick),
                        new Vector3(0f, DoorHeight + lintel * 0.5f, 0f)),
            };
        }

        /// <summary>
        /// Pipe railing: two horizontal rails on two posts. A boundary you can see through.
        ///
        /// THE POSTS SIT FULLY INSIDE THE MODULE, which is what makes a run of these read as one
        /// railing instead of a row of separate panels. Each post is inset by its own half-width,
        /// so two modules placed side by side bring their end posts edge to edge at the seam —
        /// they touch and form a single double-width stanchion, the way a real railing joins.
        /// Centring the posts on the module ends would instead have put two posts in exactly the
        /// same place, z-fighting against each other at every joint.
        ///
        /// NO <c>BulletPassThrough</c> HERE, unlike the weapon rack. The rack needs it because
        /// its grid is drawn as one dense panel whose box colliders cover the gaps; this railing
        /// has a collider per pipe and nothing in between, so shots already pass through the open
        /// air and stop on the metal. That is the honest behaviour, and it comes for free.
        /// </summary>
        static Box[] PipeRailing()
        {
            float postX = RailLength * 0.5f - RailPostW * 0.5f;

            return new[]
            {
                // Ust boru — el yuksekligi, korkulugun okunan hatti.
                new Box(new Vector3(RailLength, RailPipe, RailPipe),
                        new Vector3(0f, RailHeight - RailPipe * 0.5f, 0f)),
                // Orta boru: tek boruda alttan gecilebilir goruntusu olusuyor ve bosluk
                // "buradan atlanir" diye okunuyor. Ikincisi hatti kapatir, gorusu kapatmadan.
                new Box(new Vector3(RailLength, RailPipe, RailPipe),
                        new Vector3(0f, RailHeight * 0.5f, 0f)),
                // Dikmeler.
                new Box(new Vector3(RailPostW, RailHeight, RailDepth),
                        new Vector3(-postX, RailHeight * 0.5f, 0f)),
                new Box(new Vector3(RailPostW, RailHeight, RailDepth),
                        new Vector3(postX, RailHeight * 0.5f, 0f)),
            };
        }

        /// <summary>Waist-high table: a top and four legs. Cover you can also shoot under.</summary>
        static Box[] Table()
        {
            float legY = TableHeight - TopThick;
            float legX = TableLength * 0.5f - LegInset - LegThick * 0.5f;
            float legZ = TableDepth * 0.5f - LegInset - LegThick * 0.5f;

            var boxes = new List<Box>
            {
                new Box(new Vector3(TableLength, TopThick, TableDepth),
                        new Vector3(0f, TableHeight - TopThick * 0.5f, 0f)),
            };

            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    boxes.Add(new Box(new Vector3(LegThick, legY, LegThick),
                                      new Vector3(sx * legX, legY * 0.5f, sz * legZ)));

            return boxes.ToArray();
        }

        /// <summary>
        /// The weapon wall: a wire-grid panel with every weapon hanging on it.
        ///
        /// The panel is ordinary scenery — built locally on every peer like any other prop, and
        /// costing nothing on the wire. The markers carry no geometry at all; they are positions
        /// with a name attached, and the SERVER turns each into a real networked weapon once the
        /// rack is placed. That split is the whole design: what you SEE is scenery, what you
        /// PICK UP is not.
        ///
        /// The grid is WOVEN from bars rather than imported. The scanned panel in the scene
        /// looks right but costs 348,739 triangles — one is already too much for a Quest, and
        /// build mode means the player can put down several. A frame with fifteen uprights and
        /// five rails reads the same from arm's length and costs about three hundred.
        /// </summary>
        static string BuildRack(string name, Material material)
        {
            var boxes = new List<Box>();
            float halfH = RackHeight * 0.5f;

            // Dis cerceve: ust, alt, sol, sag.
            boxes.Add(new Box(new Vector3(RackWidth, RackFrame, RackDepth), new Vector3(0f, RackHeight - RackFrame * 0.5f, 0f)));
            boxes.Add(new Box(new Vector3(RackWidth, RackFrame, RackDepth), new Vector3(0f, RackFrame * 0.5f, 0f)));
            boxes.Add(new Box(new Vector3(RackFrame, RackHeight, RackDepth), new Vector3(-(RackWidth - RackFrame) * 0.5f, halfH, 0f)));
            boxes.Add(new Box(new Vector3(RackFrame, RackHeight, RackDepth), new Vector3((RackWidth - RackFrame) * 0.5f, halfH, 0f)));

            // Dikey cubuklar — levhanin ARKA yarisinda kalsinlar ki silahlar onlerinde dursun.
            float barZ = RackDepth * 0.25f;
            float barD = RackDepth * 0.5f;
            for (int i = 1; i <= RackBarsX; i++)
            {
                float t = i / (float)(RackBarsX + 1);
                boxes.Add(new Box(new Vector3(RackBarThick, RackHeight - RackFrame * 2f, barD),
                                  new Vector3(Mathf.Lerp(-RackWidth * 0.5f, RackWidth * 0.5f, t), halfH, barZ)));
            }
            // Yatay raylar.
            for (int i = 1; i <= RackBarsY; i++)
            {
                float t = i / (float)(RackBarsY + 1);
                boxes.Add(new Box(new Vector3(RackWidth - RackFrame * 2f, RackBarThick, barD),
                                  new Vector3(0f, Mathf.Lerp(0f, RackHeight, t), barZ)));
            }

            string line = Build(name, material, boxes.ToArray(), root =>
            {
                // Levha TEL IZGARA: aradan gorunuyor, o halde mermi de gecmeli. Kutu
                // carpisicilar duruyor (oyuncu rafin icinde duramaz), yalnizca silah isinlari
                // onlari gormezden geliyor — bkz. BulletPassThrough. Menu 30 yeniden
                // calistiginda kaybolmasin diye URETIMDE ekleniyor.
                root.AddComponent<VRMultiplayer.Weapons.BulletPassThrough>();

                foreach (var s in RackSlots)
                {
                    var marker = new GameObject("Slot_" + s.weapon);
                    marker.transform.SetParent(root.transform, false);
                    // Yuva yukseklikleri levhanin MERKEZINE gore olculmustu; prefabin kokeni
                    // tabanda oldugu icin yariyi ekliyoruz.
                    //
                    // YEREL +Z, -Z DEGIL: ConstructorPlacer.PlaceOnWall propu yerel +Z'yi
                    // duvarin normaline (odanin icine) cevirir. Isaret ters olsaydi silahlar
                    // duvarin icinde dogardi.
                    marker.transform.localPosition =
                        new Vector3(s.x, halfH + s.y, RackDepth * 0.5f + RackSlotOut);
                    marker.transform.localRotation = Quaternion.Euler(0f, s.yaw, 0f);
                    marker.AddComponent<VRMultiplayer.Weapons.WeaponRackSlot>().weaponPrefabName = s.weapon;
                }
            });

            return line + "  +" + RackSlots.Length + " yuva";
        }

        // ------------------------------------------------------------- uretim

        /// <summary>
        /// Turns a box list into a prefab.
        ///
        /// PIVOT IS THE WHOLE POINT. <c>MapBuilder.Spawn</c> puts the prefab's origin on the
        /// floor at the centre of the reserved cells, so a piece is only where the grid thinks
        /// it is when its origin sits at the middle of its footprint with the geometry standing
        /// on y = 0. Authoring the boxes directly in that space — rather than centring a shape
        /// and nudging the transform afterwards — is what makes it exact instead of nearly
        /// right. (<c>MapBuilder.PivotOffset</c> forgives art that gets this wrong; there is no
        /// reason to lean on it for pieces we generate ourselves.)
        /// </summary>
        static string Build(string name, Material material, Box[] boxes,
            System.Action<GameObject> decorate = null)
        {
            var parts = new List<ProBuilderMesh>();
            foreach (var b in boxes) parts.Add(MakeBox(b));

            ProBuilderMesh mesh = parts[0];
            if (parts.Count > 1)
            {
                var result = CombineMeshes.Combine(parts, parts[0]);
                mesh = result[0];
                foreach (var m in parts) if (m != null && m != mesh) Object.DestroyImmediate(m.gameObject);
                foreach (var m in result) if (m != null && m != mesh) Object.DestroyImmediate(m.gameObject);
            }

            mesh.ToMesh();
            mesh.Refresh();
            UnityEditor.ProBuilder.EditorMeshUtility.Optimize(mesh, true);

            var go = mesh.gameObject;
            go.name = name;
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = material;

            // MeshCollider yerine kutu carpisicilar: sekil zaten kutulardan olusuyor ve Quest'te
            // mermi isinlarinin kesistigi sey kac ucgen oldugunu umursuyor.
            foreach (var mc in go.GetComponents<MeshCollider>()) Object.DestroyImmediate(mc);
            foreach (var b in boxes)
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.size = b.size;
                bc.center = b.center;
            }

            // Ek parcalar (raf yuvalari gibi) KAYDETMEDEN ONCE ekleniyor. Prefabi kaydedip
            // sonra LoadPrefabContents ile geri acmak da ise yarardi — ama ProBuilder acilista
            // mesh'i yeniden uretiyor ve varlik referansini kopariyor, yani prefab gorunmez
            // kaydediliyordu. Tek kayit, tek dogru sonuc.
            decorate?.Invoke(go);

            // MESH'I VARLIGA CEVIR, SONRA KAYDET. ProBuilder gorunur mesh'i ToMesh() ile
            // ANLIK uretir; o Mesh bir varlik olmadigi icin prefaba yazilamaz ve prefab
            // MeshFilter'i bos, prop da gorunmez cikar. Once diske yaziyoruz ki prefabin
            // tuttugu referans gercek bir sey olsun.
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) mf.sharedMesh = PersistMesh(mf.sharedMesh, name);

            string path = PropFolder + "/" + name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);

            var size = Bound(boxes);
            return $"{name}  {size.x:0.000} x {size.z:0.000} m taban, {size.y:0.00} m boy " +
                   $"({Mathf.RoundToInt(size.x / Cell)}x{Mathf.RoundToInt(size.z / Cell)} hucre)";
        }

        /// <summary>
        /// Writes the generated mesh to disk, reusing the existing asset when there is one.
        ///
        /// Overwritten IN PLACE rather than deleted and recreated: the asset keeps its GUID, so
        /// re-running this menu after changing a dimension does not break the prefab pointing at
        /// it — or anything else that has since referenced it.
        /// </summary>
        static Mesh PersistMesh(Mesh generated, string name)
        {
            string path = MeshFolder + "/" + name + "_Mesh.asset";
            var copy = Object.Instantiate(generated);
            copy.name = name + "_Mesh";

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, path);
                return copy;
            }

            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        static ProBuilderMesh MakeBox(Box b)
        {
            var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, b.size);
            if (b.center != Vector3.zero)
            {
                var positions = new List<Vector3>(pb.positions);
                for (int i = 0; i < positions.Count; i++) positions[i] += b.center;
                pb.positions = positions;
            }
            pb.ToMesh();
            pb.Refresh();
            return pb;
        }

        static Vector3 Bound(Box[] boxes)
        {
            var b = new Bounds(boxes[0].center, boxes[0].size);
            foreach (var box in boxes) b.Encapsulate(new Bounds(box.center, box.size));
            return b.size;
        }

        // ------------------------------------------------------------- varliklar

        static Material EnsureMaterial(string path, Color color)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                // Simple Lit, Lit degil: mobil VR'da parca basina isik hesabi bedava degil ve
                // bu parcalar duz renkli. Bulunamazsa Lit'e dusuyoruz.
                var shader = Shader.Find("Universal Render Pipeline/Simple Lit")
                             ?? Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.08f);
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }
    }
}
