using UnityEngine;
using UnityEngine.AI;

namespace VRMultiplayer
{
    /// <summary>
    /// Odanin yurunebilir alanini (NavMesh) calisma aninda devreye alan bilesen. Editorde
    /// bake edilen <see cref="NavMeshData"/> varligini tutar; oyun basladiginda kaydeder.
    ///
    /// NEDEN NavMeshSurface DEGIL: NavMeshSurface, yaricap/yukseklik degerlerini projenin
    /// AJAN TIPI ayarindan alir. Bu projede tek ajan tipi var ve yaricapi 0.5 — odalar arasi
    /// kapilar ise 1.10 m. 0.5 yaricapla kapidan gecilebilir koridor 10 cm'ye duser ve
    /// voksellemede tamamen kapanir, yani iki oda birbirine BAGLANMAZ. Bu yuzden NavMesh
    /// dusuk seviyeli API ile, kendi olculerimizle kuruluyor (bkz. Editor/RoomNavMeshSetup)
    /// ve sonucu burada tasiniyor.
    ///
    /// Bilesen "RoomMap" kokune bake sirasinda otomatik eklenir.
    /// </summary>
    public class RoomNavMesh : MonoBehaviour
    {
        [Tooltip("Editorde bake edilen yurunebilir alan verisi (Tools > VR Multiplayer > 23).")]
        public NavMeshData data;

        NavMeshDataInstance _instance;

        /// <summary>Yurunebilir alan yuklu mu? Rota gostergesi bunu sorar; yuklu degilse
        /// hicbir sey cizmez (bkz. <see cref="UI.SpawnRouteGuide"/>) — duvarin icinden gecen
        /// bir cizgi cizmektense hic cizmemek dogru.</summary>
        public static bool Loaded { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Loaded = false;

        void OnEnable()
        {
            if (data == null)
            {
                Debug.LogWarning("[RoomNavMesh] NavMesh verisi atanmamis — dogum bolgesi rotasi " +
                                 "cizilmeyecek. Tools > VR Multiplayer > 23 ile bake al.");
                return;
            }

            _instance = NavMesh.AddNavMeshData(data);
            Loaded = _instance.valid;
            if (!Loaded)
                Debug.LogWarning("[RoomNavMesh] NavMesh verisi kaydedilemedi.");
        }

        void OnDisable()
        {
            if (_instance.valid) _instance.Remove();
            Loaded = false;
        }
    }
}
