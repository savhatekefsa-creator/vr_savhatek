using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Bir isigin siddetini alev gibi oynatir.
    ///
    /// THE LIGHT DOES MORE THAN THE FLAME. A fire read at a distance is mostly the moving
    /// orange light it throws on everything around it — the particles are the small bright part
    /// in the middle. That is also the cheap part: modulating one float per frame costs nothing,
    /// while making the particles convincing costs texture, overdraw and fill rate.
    ///
    /// PERLIN, NOT Random.value. Per-frame random is a STROBE: it jumps the full range every
    /// frame with no correlation between them, which reads as a broken fluorescent tube and, at
    /// VR framerates on a light that fills the room, is genuinely unpleasant to stand next to.
    /// Smooth noise moves continuously, so the light wavers.
    ///
    /// TWO LAYERS, because a real flame does two things at once: a fast tremble and a slow
    /// breath as the fire swells and settles. One frequency alone reads as mechanical.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public class FlickerLight : MonoBehaviour
    {
        [Tooltip("Titremenin etrafinda oynadigi taban siddet.")]
        [Min(0f)] public float baseIntensity = 2.2f;

        [Tooltip("Oynama genligi (taban siddetin orani). 0 = sabit isik.")]
        [Range(0f, 1f)] public float amplitude = 0.42f;

        [Tooltip("Hizli titremenin hizi.")]
        [Min(0.1f)] public float speed = 7f;

        Light _light;
        float _seed;

        void Awake()
        {
            _light = GetComponent<Light>();
            // Her ornege AYRI tohum: iki varil yan yana durdugunda ayni anda titrerlerse
            // ates degil, bir anahtara bagli iki ampul gibi gorunurler.
            _seed = Random.value * 137f;
        }

        void Update()
        {
            float fast = Mathf.PerlinNoise(_seed, Time.time * speed);
            float slow = Mathf.PerlinNoise(_seed + 31.7f, Time.time * speed * 0.22f);

            float f = 1f
                    + (fast - 0.5f) * 2f * amplitude * 0.70f
                    + (slow - 0.5f) * 2f * amplitude * 0.30f;

            // Taban: alev hic sonmez. Sifira inen bir carpan odayi bir karelik karanliga
            // sokar ve bu goz kirpmasi gibi degil, hata gibi okunur.
            _light.intensity = baseIntensity * Mathf.Max(0.30f, f);
        }
    }
}
