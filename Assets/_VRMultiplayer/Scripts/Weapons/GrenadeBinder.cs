using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Ismi Resources/GrenadeConfigs'teki bir <see cref="GrenadeConfig"/> ile eslesen her
    /// <see cref="GrabbableObject"/>'e spawn aninda <see cref="GrenadeController"/> takar —
    /// her makinede, runtime'da, sahne/prefab duzenlemesi SIFIR (WeaponGripBinder ile ayni
    /// kalip; boylece bombalari dinamik spawn eden sistemlerle de catismaz). Eslesmeyen
    /// objeye dokunulmaz.
    /// </summary>
    public static class GrenadeBinder
    {
        static GrenadeConfig[] _configs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            // Statikler domain reload'da sifirlanir; her play oturumunda yeniden abone olmak guvenli.
            GrabbableObject.AnySpawned -= OnGrabbableSpawned;
            GrabbableObject.AnySpawned += OnGrabbableSpawned;
            _configs = null;
        }

        static void OnGrabbableSpawned(GrabbableObject g)
        {
            var cfg = FindConfig(g.name);
            if (cfg == null) return;

            var c = g.GetComponent<GrenadeController>();
            if (c == null) c = g.gameObject.AddComponent<GrenadeController>();
            c.Bind(cfg);
        }

        /// <summary>Obje adina uyan config: birebir eslesme oncelikli; yoksa obje adi config
        /// adini ICEREN kayit ("Weapon_Grenade 1" gibi onekli spawner prefablari icin —
        /// silah secici cantasi bombalari bu adla dogurur). Uymayan icin null.</summary>
        public static GrenadeConfig FindConfig(string objectName)
        {
            objectName = WeaponGripBinder.CleanName(objectName);
            if (_configs == null)
                _configs = Resources.LoadAll<GrenadeConfig>("GrenadeConfigs");

            GrenadeConfig partial = null;
            foreach (var c in _configs)
            {
                if (c == null) continue;
                if (c.name == objectName) return c;
                if (objectName.Contains(c.name)) partial = c;
            }
            return partial;
        }
    }
}
