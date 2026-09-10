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

        int _temiz;

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 1f;

            // BASARILI TARAMADAN SONRA DUR. Bu tarama oturum boyunca 1 Hz calisiyordu ve
            // runtime'da insa edilen haritada nesne sayisi yuksek oldugu icin duzenli, sabit
            // ritimli bir spike uretiyordu — VR'da en fark edilen hitch turu.
            var all = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            if (all.Length <= 1)
            {
                // Ust uste iki temiz tarama: is bitti, tarama kapanir. Yeni bir dinleyici
                // ancak yeni bir oyuncu/kamera dogarsa gelir; o da spawn yolundan gecer.
                if (++_temiz >= 2) enabled = false;
                return;
            }
            _temiz = 0;

            // Tutulacak: Main Camera'ninki; yoksa ilk enabled olan; o da yoksa ilk bulunan.
            AudioListener keep = Camera.main != null ? Camera.main.GetComponent<AudioListener>() : null;
            if (keep == null)
                foreach (var l in all) if (l.enabled) { keep = l; break; }
            if (keep == null) keep = all[0];

            foreach (var l in all)
                if (l != keep && l.enabled) l.enabled = false;

            // "keep" DEVRE DISI bir listener olabilir (or. baska bir arac Main Camera'ninkini
            // kapattiysa). Sondaki garanti olmadan yukaridaki dongu son etkin listener'i da
            // kapatip oyunu tamamen sessiz birakabiliyordu.
            if (!keep.enabled) keep.enabled = true;
        }
    }
}
