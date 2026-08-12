using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Radyasyon varilinin tikirtisi — YAKLASTIKCA HIZLANIR.
    ///
    /// DIEGETIC, AND THAT IS THE WHOLE POINT. A radiation drum with a hazard symbol says
    /// "dangerous" once, in a language the player reads and then ignores. A counter that speeds
    /// up as they close in says it continuously, without a HUD, without a tutorial, and in a way
    /// that keeps working when the drum is behind them.
    ///
    /// PURELY LOCAL, LIKE <see cref="FootSurface"/>. Each client measures the distance from its
    /// OWN head, so what a player hears is about where THEY are standing — which is the only
    /// thing this sound could sensibly mean. There is nothing to replicate and no RPC: the drum
    /// is built locally from the map layout on every peer, and the head is already there.
    ///
    /// NOT A COST/DAMAGE MECHANIC. Nothing here touches health. Radiation that actually hurt
    /// would need the player to be able to see how much they had taken, and a room-scale player
    /// who cannot see their own feet should not be losing health to a thing they walked past.
    /// </summary>
    [DisallowMultipleComponent]
    public class GeigerTicker : MonoBehaviour
    {
        [Tooltip("Bu mesafenin otesinde hic tikirdamaz (m).")]
        [Min(0.5f)] public float maxDistance = 7f;

        [Tooltip("Varile YAPISIKKEN iki tikirti arasi (sn).")]
        [Min(0.02f)] public float minInterval = 0.09f;

        [Tooltip("Menzilin KENARINDA iki tikirti arasi (sn).")]
        [Min(0.05f)] public float maxInterval = 1.5f;

        [Range(0f, 1f)] public float volume = 0.5f;

        [Tooltip("Tikirti klibinin Resources yolu.")]
        public string clipPath = "WeaponSounds/geiger_tick";

        AudioSource _src;
        AudioClip _clip;
        float _next;

        void Awake()
        {
            _clip = Resources.Load<AudioClip>(clipPath);
            if (_clip == null)
            {
                Debug.LogWarning($"[GeigerTicker] Resources/{clipPath} yok — sayac sessiz.");
                enabled = false;
                return;
            }

            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;                       // varilden geliyor, kafadan degil
            _src.minDistance = 0.6f;
            _src.maxDistance = maxDistance;
            _src.rolloffMode = AudioRolloffMode.Linear;
            // Silahin/adimin altinda kalsin: bir odada dort varil olabilir ve hicbiri
            // catismanin sesini bastirmamali.
            _src.priority = 220;

            // Ilk tikirti hemen gelmesin: harita kurulurken ayni anda dogan varillerin
            // hepsi ayni karede tikirdarsa tek bir "cat" sesi duyulur, sayac degil.
            _next = Random.Range(0f, maxInterval);
        }

        void Update()
        {
            var head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            float d = Vector3.Distance(head.position, transform.position);
            if (d > maxDistance) return;                  // menzil disi: zamanlayici da ilerlemez

            _next -= Time.deltaTime;
            if (_next > 0f) return;

            // Mesafeye gore aralik. Kare alma, yakinlasmanin son metresini belirginlestiriyor:
            // dogrusal bir egride 7 m'den 3 m'ye yaklasmak 3 m'den 1 m'ye yaklasmakla ayni
            // hizlanmayi verir, oysa oyuncunun hissetmesi gereken sey ikincisi.
            float t = Mathf.Clamp01(d / maxDistance);
            float interval = Mathf.Lerp(minInterval, maxInterval, t * t);

            // Geiger sayaci DUZENLI TIKIRDAMAZ — bozunma rastgeledir, ve duzenli bir tik
            // metronom gibi duyulup canliligini kaybeder.
            _next = interval * Random.Range(0.55f, 1.75f);

            _src.pitch = Random.Range(0.92f, 1.10f);
            _src.PlayOneShot(_clip, volume);
        }
    }
}
