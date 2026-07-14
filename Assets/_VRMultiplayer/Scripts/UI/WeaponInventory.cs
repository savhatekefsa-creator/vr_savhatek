using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Yerel silah envanteri: yerel oyuncunun bu oturumda TOPLADIGI her farkli silahi, alis
    /// sirasiyla hatirlar. Silah secici (carousel) bu listeyi gosterir.
    ///
    /// Tamamen YEREL (kendi goruntun) — ag gerekmez; secilen silahi ele koyma (equip) ileride
    /// normal tutma yolundan gecer. Her kare (kismacil) elde tutulan grabbable'lari tarar, bu
    /// yuzden HandGrabber / GrabbableObject'e HIC dokunmaz; sahneye/prefaba da elle bir sey
    /// eklemek gerekmez (kendini otomatik olusturur).
    /// </summary>
    public class WeaponInventory : MonoBehaviour
    {
        public static WeaponInventory Instance { get; private set; }

        // Toplanan farkli silah "tur anahtarlari", alis sirasiyla.
        readonly List<string> _collected = new List<string>();
        readonly HashSet<string> _seen = new HashSet<string>();
        float _nextScan;

        /// <summary>Simdiye kadar toplanan silahlar (tur anahtari), alis sirasiyla.</summary>
        public IReadOnlyList<string> Collected => _collected;

        /// <summary>Envanter degisince (yeni silah eklenince) tetiklenir — UI bunu dinleyebilir.</summary>
        public event System.Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponInventory");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponInventory>();
        }

        void Awake() => Instance = this;

        void Update()
        {
            // Sik sik FindObjects cagirmamak icin saniyede ~3 kez tara (silah kapma bundan yavas).
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 0.3f;

            var nm = NetworkManager.Singleton;
            if (nm == null || !(nm.IsServer || nm.IsConnectedClient)) return;
            ulong me = nm.LocalClientId;

            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                if (g.HolderClientId != me) continue; // sadece SU AN benim tuttugum silahlar
                string key = TypeKey(g);
                if (_seen.Add(key))
                {
                    _collected.Add(key);
                    Debug.Log($"[WeaponInventory] Yeni silah toplandi: {key}  (toplam {_collected.Count})");
                    Changed?.Invoke();
                }
            }
        }

        // Silahin "tur"u: varsa tutuş profili adi (her silah turunun tek profili var), yoksa
        // obje adinin klon/kopya gurultusu temizlenmis hali. Ayni tur iki kez alinirsa tek kayit.
        static string TypeKey(GrabbableObject g)
        {
            var grip = g.GetComponent<WeaponGrip>();
            if (grip != null && grip.Profile != null) return grip.Profile.name;

            string n = g.name.Replace("(Clone)", "").Trim();
            int paren = n.IndexOf(" (");
            return paren > 0 ? n.Substring(0, paren) : n;
        }
    }
}
