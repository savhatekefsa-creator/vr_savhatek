using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRMultiplayer.Constructor;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    ///   45. Mermi Gecirgenligini Uygula — "hangi prop mermiyi durdurur" kuralini prefablara yazar.
    ///
    /// KURAL, IKI YONLU:
    ///  - DUVAR MERMIYI DURDURUR. Bir duvarin gorunur govdesi varsa collider'i da olmak
    ///    zorunda. Paintball paketindeki dikenli uclu panel (Wood_Modular_6 / _7) HIC
    ///    collider'siz geliyordu: hem mermi hem oyuncu iginden geciyordu, ve sahada tek
    ///    belirtisi "bu duvar mermi gecirıyor" oluyordu.
    ///  - DELIGI OLAN SEY MERMIYI GECIRIR. Tel silah rafi ve merdiven aradan GORULUR;
    ///    mermiyi durdurmalari gorselin yalan soylemesi olurdu. Bunlarda collider KALIR
    ///    (oyuncu iginden gecemez), yalnizca silah isinlari <see cref="BulletPassThrough"/>
    ///    ile onlari gormezden gelir.
    ///
    /// NEDEN AYRI BIR MENU. Kuralin tasidigi yer prefab — cunku propu yalnizca insa modu
    /// degil, dekor araci (menu 18) ve elle yerlestirme de kuruyor; tek ortak nokta prefabin
    /// kendisi. Ama Wood_Modular_* satin alinmis bir paketin icinde: paket Asset Store'dan
    /// yeniden import edilirse duzeltme silinir. Bu menu tam olarak o onarimi tekrar
    /// edilebilir kiliyor — istedigin zaman calistir, zaten dogru olanlara dokunmaz.
    ///
    /// Rapor, kutuphanedeki TUM proplari tarar: kural disi kalan collider'siz proplar
    /// (kayalar, zemin panelleri) degistirilmeden LISTELENIR — onlar ayri bir karar.
    /// </summary>
    public static class PropBulletRules
    {
        /// <summary>
        /// Mermiyi GECIREN proplarin kutuphane kimlikleri. Sebep yorumda: liste kisa
        /// kalmali, "her delikli sey" degil "aradan gercekten gorulen sey".
        /// </summary>
        static readonly string[] PassThroughIds =
        {
            "wood_modular_8",  // merdiven — basamak aralari bos
            "rack_wall",       // tel izgara silah rafi (menu 30 uretimi)
            "rack_long_a",     // emekli raf parcalari: paletten gizli ama eski haritalarda var
            "rack_long_b",
            "rack_sidearm",
        };

        [MenuItem("Tools/VR Multiplayer/45. Mermi Gecirgenligini Uygula (duvar/raf/merdiven)")]
        public static void ApplyMenu() =>
            EditorUtility.DisplayDialog("VR Multiplayer", Apply(), "Tamam");

        public static string Apply()
        {
            var lib = PropLibrary.Instance;
            if (lib == null || lib.Count == 0)
                return "Kutuphane bos — once menu 25 (Prop Kutuphanesi Tara).";

            var pass = new HashSet<string>(PassThroughIds);
            var changed = new List<string>();
            var blockers = new List<string>();   // duvar ama collider'siz kalanlar
            var noCollider = new List<string>(); // kural disi, bilgi olsun diye

            foreach (var def in lib.props)
            {
                if (def == null) continue;
                var prefab = def.Resolve();
                if (prefab == null) continue;

                string path = AssetDatabase.GetAssetPath(prefab);
                if (string.IsNullOrEmpty(path)) continue;

                bool wantPass = pass.Contains(def.id);
                bool isWall = def.category == PropCategory.Wall;

                bool hasPass = prefab.GetComponent<BulletPassThrough>() != null;
                int colliders = prefab.GetComponentsInChildren<Collider>(true).Length;

                // Duvar olup collider'i olmayanlar: mermi de oyuncu da geciyor demektir.
                bool needCollider = isWall && !wantPass && colliders == 0;

                if (!isWall && !wantPass && colliders == 0)
                    noCollider.Add($"{def.id} ({def.category})");

                if (hasPass == wantPass && !needCollider) continue;

                // Prefabi ACIP degistiriyoruz: paket prefablarina Instantiate/Save yoluyla
                // dokunmak varyant/bagimlilik iliskilerini bozar.
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var mark = contents.GetComponent<BulletPassThrough>();
                    if (wantPass && mark == null)
                    {
                        contents.AddComponent<BulletPassThrough>();
                        changed.Add($"{def.id}: mermi GECIRIR isareti eklendi");
                    }
                    else if (!wantPass && mark != null)
                    {
                        Object.DestroyImmediate(mark, true);
                        changed.Add($"{def.id}: mermi geciren isareti KALDIRILDI");
                    }

                    if (needCollider)
                    {
                        int added = AddMeshColliders(contents);
                        if (added > 0)
                            changed.Add($"{def.id}: {added} MeshCollider eklendi (duvar artik mermiyi durduruyor)");
                        else
                            blockers.Add($"{def.id}: collider yok ve mesh'ten uretilemedi — elle bak");
                    }

                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            lib.InvalidateIndex();

            var sb = new StringBuilder();
            sb.Append(changed.Count == 0
                ? "Degisiklik yok — tum proplar zaten kurala uygun.\n"
                : "Uygulandi:\n - " + string.Join("\n - ", changed) + "\n");

            if (blockers.Count > 0)
                sb.Append("\nDUZELTILEMEDI:\n - " + string.Join("\n - ", blockers) + "\n");

            if (noCollider.Count > 0)
                sb.Append("\nBilgi — duvar OLMADIGI icin dokunulmadi, ama collider'i da yok " +
                          "(mermi ve oyuncu iginden gecer):\n - " +
                          string.Join("\n - ", noCollider) + "\n");

            sb.Append("\nMermi geciren proplar: " + string.Join(", ", PassThroughIds));
            return sb.ToString();
        }

        /// <summary>
        /// Collider'siz duvara mesh'inin kendisinden carpisici verir.
        ///
        /// MeshCollider, kutu yerine: bu panelin ust kenari DIKENLI — tepeler ve aralarindaki
        /// V centikler siluetin yarisi kadar. Tek bir kutu, cenlikteki bos havayi da
        /// kapatirdi: oyuncu panelin alcak tarafindan ates etmeye calisir ve mermi gorunmez
        /// bir duvara carpardi. Mesh 32 ucgen — ayni paketteki bunkerlar zaten 404 ve 560
        /// ucgenlik MeshCollider tasiyor, yani bu paketin en ucuz carpisicisi olur.
        /// (Kit'in kendi parcalarinda kutu tercihi gecerli: onlarin sekli zaten kutulardan
        /// olusuyor — bkz. ConstructorKitBuilder.)
        /// </summary>
        static int AddMeshColliders(GameObject root)
        {
            int n = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;   // sabit sahne geometrisi: ucbukey olmasi gerekmiyor
                n++;
            }
            return n;
        }
    }
}
