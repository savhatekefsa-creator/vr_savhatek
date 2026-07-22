using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Iki takimin dogum bolgesini sahneye kurar (<see cref="TeamSpawnZone"/>).
    ///
    /// Bolgeler kalibrasyon cercevesine gore yerlestirilir: <see cref="CalibrationManager"/>
    /// sahnede varsa onun sharedOrigin/sharedForward degerleri kullanilir, yoksa dunya kokü ve
    /// +Z varsayilir. A takimi ILERI, B takimi GERI tarafa konur.
    ///
    /// ONEMLI: bunlar FIZIKSEL yerlerdir. Oyun kolokasyonlu oldugu icin oyuncu bu cembere
    /// gercek odada YURUYEREK gelecek — kurulumdan sonra iki bolgeyi de gercek odanin uygun
    /// noktalarina (iki ucuna) SURUKLEYIP birak. Varsayilan +-<see cref="DefaultSeparation"/>/2
    /// metre yalnizca bir baslangictir; odaniz kucukse iki cember ust uste biner ve
    /// "olunce geri yurume" cezasi anlamsizlasir.
    /// </summary>
    public static class TeamSpawnZoneSetup
    {
        /// <summary>Iki bolge merkezi arasindaki varsayilan mesafe (metre).</summary>
        const float DefaultSeparation = 6f;
        const float DefaultRadius = 1.2f;

        [MenuItem("Tools/VR Multiplayer/22. Takim Dogum Bolgelerini Kur")]
        public static void SetupZones()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Dogum Bolgeleri",
                    "Bu menu Play modunda calistirilamaz. Once Play'i durdur.", "Tamam");
                return;
            }

            Vector3 origin = Vector3.zero;
            Vector3 forward = Vector3.forward;
            var cal = Object.FindFirstObjectByType<CalibrationManager>();
            if (cal != null)
            {
                origin = cal.sharedOrigin;
                Vector3 f = new Vector3(cal.sharedForward.x, 0f, cal.sharedForward.z);
                if (f.sqrMagnitude > 1e-4f) forward = f.normalized;
            }

            var a = EnsureZone("Team A Spawn Zone", 1, origin + forward * (DefaultSeparation * 0.5f));
            var b = EnsureZone("Team B Spawn Zone", 2, origin - forward * (DefaultSeparation * 0.5f));

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.objects = new Object[] { a.gameObject, b.gameObject };

            string msg =
                "Iki dogum bolgesi hazir (A ileri, B geri; arasi " + DefaultSeparation + " m).\n\n" +
                "SIMDI ONEMLI ADIM: bunlar FIZIKSEL yerler. Iki cemberi de gercek odanin " +
                "uygun noktalarina surukleyip birak — oyuncu olunce oraya GERCEKTEN yuruyecek.\n\n" +
                "Yaricap her bolgenin Inspector'undan ayarlanir (varsayilan " + DefaultRadius + " m).";
            Debug.Log("[TeamSpawnZoneSetup] " + msg);
            EditorUtility.DisplayDialog("Dogum Bolgeleri", msg, "Tamam");
        }

        static TeamSpawnZone EnsureZone(string name, byte team, Vector3 position)
        {
            // Ayni takima ait bir bolge zaten varsa TASINMAZ: elle ayarlanmis konum, menuyu
            // ikinci kez calistirinca varsayilana geri donmemeli.
            foreach (var z in Object.FindObjectsByType<TeamSpawnZone>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (z.team == team)
                {
                    Debug.Log($"[TeamSpawnZoneSetup] '{z.name}' zaten var — konumu korundu.");
                    return z;
                }
            }

            var go = new GameObject(name);
            go.transform.position = position;
            var zone = go.AddComponent<TeamSpawnZone>();
            zone.team = team;
            zone.radius = DefaultRadius;
            Undo.RegisterCreatedObjectUndo(go, "Dogum bolgesi olustur");
            return zone;
        }
    }
}
