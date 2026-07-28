using UnityEngine;

namespace VRMultiplayer.Audio
{
    /// <summary>
    /// Oyuncuya bagli sesler. PlayerHealth spawn'da calisma aninda ekler — prefab
    /// degisikligi YOK (sihirbaz prefab'i sifirdan kursa da calismaya devam eder).
    ///
    /// 1) AYAK SESI: oyuncu kokunun yatay yer degistirmesi adim sayacinda birikir; her
    ///    adim boyunda (~0.72 m) bir adim klibi KOKUN konumunda 3D calinir. Kok konumu
    ///    herkese replike oldugu icin ayni kod yerel ve uzak kopyalarda ayni calisir:
    ///    uzaktaki oyuncunun adimini kendi yonunden/mesafesinden duyarsin. Kendi adimlarin
    ///    bilerek kisik — VR'da yuksek kendi-adim sesi rahatsiz eder; baskasinin adimi ise
    ///    taktik bilgidir, daha gur. Isinlanma/spawn sicramasi (karede >1.5 m) ve yavas
    ///    kafa sallantisi (0.6 m/s alti) adim SAYILMAZ — durup dururken hayalet adim olmaz.
    ///
    /// 2) VUCUDA MERMI SESI ("tik/puf"): can dususu her istemcide replike olsa da ses
    ///    YALNIZ hasari alanin kendi kulakliginda (2D) calar — kisiye ozel geri bildirim.
    ///    Siddet hasar miktariyla olceklenir: siyrik hafif "tik", agir isabet dolgun "puf".
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerAudio : MonoBehaviour
    {
        const float StrideMeters = 0.72f;      // iki adim sesi arasi yatay yol
        const float TeleportThreshold = 1.5f;  // karede bundan uzun sicrama = isinlanma, adim degil
        const float MinWalkSpeed = 0.6f;       // m/s; alti kafa sallantisi/egilme sayilir, birikmez
        const float StepMaxDistance = 22f;

        PlayerHealth _health;
        Vector3 _lastPos;
        float _accum;
        int _stepIdx;

        void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            _lastPos = transform.position;
        }

        void OnEnable()
        {
            if (_health != null) _health.Health.OnValueChanged += OnHealthChanged;
        }

        void OnDisable()
        {
            if (_health != null) _health.Health.OnValueChanged -= OnHealthChanged;
        }

        void OnHealthChanged(int prev, int now)
        {
            if (now >= prev) return;                       // yenilenme/ilk senkron, hasar degil
            if (_health == null || !_health.IsOwner) return; // KISIYE OZEL: yalniz hasari alan
            float vol = Mathf.Lerp(0.45f, 0.95f, Mathf.InverseLerp(5f, 40f, prev - now));
            WeaponAudioPlayer.Play2D("WeaponSounds/hit_body_" + Random.Range(1, 4), vol, 0.92f, 1.08f);
        }

        void Update()
        {
            Vector3 p = transform.position;
            Vector3 d = p - _lastPos;
            _lastPos = p;
            d.y = 0f; // comelme/egilme dikey oynamasi adim degildir
            float dist = d.magnitude;

            if (dist > TeleportThreshold) { _accum = 0f; return; }
            if (_health != null && _health.IsDead) { _accum = 0f; return; } // olu/bekleyen sessiz
            if (dist < MinWalkSpeed * Time.deltaTime)
            {
                // Duruyor ya da sallaniyor: birikimi yavasca bosalt ki sallantiyla
                // damla damla dolan sayac dakikalar sonra hayalet adim uretmesin.
                _accum = Mathf.Max(0f, _accum - StrideMeters * Time.deltaTime);
                return;
            }

            _accum += dist;
            if (_accum < StrideMeters) return;
            _accum -= StrideMeters;

            // 4 varyant sirayla degil karisik ama ardisik tekrarsiz: ayni klibin arka
            // arkaya calmasi "makine" hissi verir.
            int next = Random.Range(1, 4);
            _stepIdx = (_stepIdx + next) % 4;
            bool own = _health != null && _health.IsOwner;
            if (own)
                // Kendi adimin 3D CALINMAZ: ayak kulaga ~1.7 m uzakta ve algisal rolloff
                // orada bile sert kisiyor — 3D'de fiilen duyulmuyordu. Kendi bedeninin sesi
                // zaten "mesafesiz" hissedilir; dogrudan kulakliga, kisik ver.
                WeaponAudioPlayer.Play2D("WeaponSounds/footstep_" + (_stepIdx + 1),
                    0.3f, 0.93f, 1.07f);
            else
                WeaponAudioPlayer.PlayAt("WeaponSounds/footstep_" + (_stepIdx + 1),
                    p, 0.75f, 0.93f, 1.07f, StepMaxDistance);
        }
    }
}
