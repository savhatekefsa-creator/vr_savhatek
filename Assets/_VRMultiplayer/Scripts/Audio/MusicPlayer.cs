using UnityEngine;

namespace VRMultiplayer.Audio
{
    /// <summary>
    /// Fon muzigi — her oyuncu kendi kulakliginda duyar. Kaynak 2D'dir: sahnede bir
    /// konumu YOKTUR, mesafe/yon hesabina girmez; nerede durursan dur ayni seviyede
    /// calar (bu yuzden bu objenin sahnedeki yeri onemsizdir).
    ///
    /// Klip KODA YA DA SAHNEYE BAGLANMAZ: Resources/Music klasorundeki ilk klip calinir —
    /// muzigi degistirmek = o klasordeki dosyayi yenisiyle degistirmek, baska adim yok.
    /// Klasor bosken SESSIZ kalir, hata degil (silah sesleriyle ayni felsefe).
    /// Klip bitince basa sarar (loop).
    ///
    /// Tamamen YEREL calisir: her istemci muzigi kendi tarafinda baslatir, ag trafigi yok.
    /// </summary>
    [DisallowMultipleComponent]
    public class MusicPlayer : MonoBehaviour
    {
        [Tooltip("Muzik seviyesi (0-1). Fon muzigi silah/adim/konusma seslerini ortmesin diye dusuk tutulur.")]
        [Range(0f, 1f)] public float volume = 0.35f;

        void Start()
        {
            var clips = Resources.LoadAll<AudioClip>("Music");
            if (clips == null || clips.Length == 0)
            {
                Debug.Log("[MusicPlayer] Resources/Music klasorunde klip yok — muzik sessiz. " +
                          "Eklemek icin ses dosyasini o klasore atmak yeterli.");
                return;
            }
            if (clips.Length > 1)
                Debug.LogWarning("[MusicPlayer] Resources/Music'te birden fazla klip var; " +
                                 "alfabetik ilki caliniyor: " + clips[0].name +
                                 " — tek dosya birakmak kafa karisikligini onler.");

            var src = gameObject.AddComponent<AudioSource>();
            src.clip = clips[0];
            src.loop = true;          // bittikce basa sarar
            src.volume = volume;
            src.spatialBlend = 0f;    // 2D: dogrudan kulaklikta, sahne konumundan bagimsiz
            src.priority = 200;       // ses kanali daralirsa ilk kesilecek sey muzik olsun
            src.playOnAwake = false;
            src.Play();
        }
    }
}
