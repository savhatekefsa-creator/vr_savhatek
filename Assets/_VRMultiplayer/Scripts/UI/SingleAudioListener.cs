using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Sahnede birden fazla AudioListener olursa Unity surekli uyari basar. Bu yardimci, Main
    /// Camera'nin listener'ini birakip DIGERLERINI kapatarak "tam olarak bir listener" kuralini
    /// calisma aninda garanti eder (ic-aktarilan modellerden vb. gelebilecek fazladan listener'lar
    /// icin). Kendini otomatik olusturur; hicbir sahneye/prefaba dokunmak gerekmez.
    /// </summary>
    public class SingleAudioListener : MonoBehaviour
    {
        float _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~SingleAudioListener");
            DontDestroyOnLoad(go);
            go.AddComponent<SingleAudioListener>();
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 1f;

            var all = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            if (all.Length <= 1) return;

            // Tutulacak: Main Camera'ninki; yoksa ilk enabled olan.
            AudioListener keep = Camera.main != null ? Camera.main.GetComponent<AudioListener>() : null;
            if (keep == null)
                foreach (var l in all) if (l.enabled) { keep = l; break; }

            foreach (var l in all)
                if (l != keep && l.enabled) l.enabled = false;
        }
    }
}
