using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Sahneye gomulu harita (<c>RoomMap</c>) ile havuzdan kurulan haritalarin ayni anda
    /// gorunmesini engeller.
    ///
    /// IKI AYRI KOK VAR ve birbirlerinden habersizdiler:
    ///   RoomMap        — sahneye yazarlanmis harita (oda taramasi + Editor dekorasyonu,
    ///                    bkz. RoomMapDecorator). Statik, SampleScene'in icinde.
    ///   ConstructorMap — <see cref="MapBuilder.DefaultRootName"/>; havuz haritalari buraya
    ///                    kurulur ve her kurulumdan once <see cref="MapBuilder.Clear"/> ile
    ///                    bosaltilir.
    ///
    /// <see cref="MapBuilder.Clear"/> yalnizca KENDI kokunu temizledigi ve calisma aninda
    /// RoomMap'e dokunan baska hicbir kod olmadigi icin, havuzdan bir harita kurmak onu
    /// sahnedeki haritanin USTUNE oruyordu — iki harita ic ice.
    ///
    /// Kural: havuz haritasi kuruldugunda sahne haritasi kapanir, havuz haritasi
    /// bosaltildiginda geri acilir.
    ///
    /// NOT: sahne haritasi henuz katalogda gorunmuyor (MapCatalog yalnizca diskteki MapLayout
    /// dosyalarini listeler), yani havuza eklenemiyor. Onun yerlesik bir katalog girisi
    /// olmasi ayri bir is — bkz. GripOlcum/DEVAM-HARITA.md.
    /// </summary>
    public static class SceneMapVisibility
    {
        public const string SceneMapRootName = "RoomMap";

        static GameObject _cached;

        /// <summary>Sahne haritasini goster/gizle. Sahnede RoomMap yoksa sessizce hicbir sey
        /// yapmaz — her sahnede bulunmasi zorunlu degil.</summary>
        public static void SetVisible(bool visible)
        {
            var go = Root;
            if (go != null && go.activeSelf != visible) go.SetActive(visible);
        }

        /// <summary>Sahne haritasi su an acik mi? (yoksa false)</summary>
        public static bool IsVisible => Root != null && Root.activeSelf;

        /// <summary>
        /// KAPALI OBJEYI DE BULMALI: bir kez gizledikten sonra <see cref="GameObject.Find"/>
        /// onu bir daha bulamaz ve harita geri acilamazdi. O yuzden referans onbellege alinir,
        /// kaybolursa sahne koklerinden taranir.
        /// </summary>
        static GameObject Root
        {
            get
            {
                if (_cached != null) return _cached;

                _cached = GameObject.Find(SceneMapRootName);
                if (_cached != null) return _cached;

                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!scene.isLoaded) return null;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == SceneMapRootName) { _cached = root; break; }
                return _cached;
            }
        }

        // Domain reload kapali projede play'e her giriste statikler elle sifirlanir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _cached = null;
    }
}
