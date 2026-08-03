using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Spawns the constructor's runtime trio once per play session. No scene wiring, no prefab
    /// to keep in sync — the same "her sey kodda dogar" approach the weapon wheel already uses.
    ///
    /// Nothing here runs until build mode is switched on, and build mode cannot switch on
    /// without a room scan, so a session that never touches the constructor pays for one idle
    /// GameObject.
    /// </summary>
    public static class ConstructorBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var go = new GameObject("~Constructor");
            Object.DontDestroyOnLoad(go);

            // SIRA ONEMLI: AddComponent, Awake'i o anda calistirir. Session ONCE eklenmeli ki
            // GridView'in OnEnable'i abone olurken ConstructorSession.Instance dolu olsun.
            // (Palet <-> Placer cifti icin sira onemsiz: palet karsi tarafi tembel cozuyor.)
            go.AddComponent<ConstructorSession>();
            go.AddComponent<ConstructorGridView>();
            go.AddComponent<ConstructorDollhouse>();
            go.AddComponent<ConstructorPassthrough>();
            go.AddComponent<ConstructorPaletteUI>();
            go.AddComponent<ConstructorPlacer>();
        }
    }
}
