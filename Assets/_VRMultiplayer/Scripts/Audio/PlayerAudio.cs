using UnityEngine;
using UnityEngine.XR;

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
    ///    ZEMIN FARKINDALIGI: adim aninda ayagin altinda bir <see cref="FootSurface"/> varsa
    ///    (cam kirigi yamasi gibi) klip, siddet ve MENZIL oradan gelir. Bunun ag maliyeti
    ///    SIFIR: kaplama her istemcide harita duzeninden yerel kuruluyor ve kokun konumu
    ///    zaten replike, yani her istemci uzaktaki oyuncunun cama bastigini kendi basina
    ///    hesapliyor. Yavas yurumek de bedava calisiyor — 0.6 m/s alti zaten adim saymiyor,
    ///    yani camin uzerinden sessizce gecmek kendiliginden mumkun.
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
        const int DefaultVariants = 4;         // WeaponSounds/footstep_1..4
        const float OwnStepVolume = 0.3f;
        const float OtherStepVolume = 0.75f;

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

            // Ayagin altinda ozel bir kaplama var mi? Adim BASINA bir kez soruluyor, her
            // kare degil — sorgu ucuz degil ve adim zaten saniyede bir-iki kez oluyor.
            var surf = FootSurface.Under(p);

            int variants = surf != null ? Mathf.Max(1, surf.clipVariants) : DefaultVariants;

            // Varyantlar sirayla degil karisik ama ardisik tekrarsiz: ayni klibin arka
            // arkaya calmasi "makine" hissi verir.
            _stepIdx = (_stepIdx + Random.Range(1, variants)) % variants;

            string clip = surf != null
                ? surf.ClipPath(_stepIdx)
                : "WeaponSounds/footstep_" + (_stepIdx + 1);

            bool own = _health != null && _health.IsOwner;
            if (own)
            {
                // Kendi adimin 3D CALINMAZ: ayak kulaga ~1.7 m uzakta ve algisal rolloff
                // orada bile sert kisiyor — 3D'de fiilen duyulmuyordu. Kendi bedeninin sesi
                // zaten "mesafesiz" hissedilir; dogrudan kulakliga, kisik ver.
                WeaponAudioPlayer.Play2D(clip,
                    surf != null ? surf.ownVolume : OwnStepVolume, 0.93f, 1.07f);

                // Titresim YALNIZ ozel kaplamada. Her adimda titreyen kumanda, VR'da bir
                // kac dakika sonra bilek yorgunlugundan baska bir sey uretmez; nadir
                // oldugunda ise "az once bir seye bastin" bilgisini tasir.
                if (surf != null) Buzz(surf.hapticAmplitude, surf.hapticDuration);
            }
            else
            {
                WeaponAudioPlayer.PlayAt(clip, p,
                    surf != null ? surf.otherVolume : OtherStepVolume, 0.93f, 1.07f,
                    surf != null ? surf.maxDistance : StepMaxDistance);
            }
        }

        /// <summary>Iki kumandaya da kisa bir darbe. Cihaz yoksa sessizce gecer.</summary>
        static void Buzz(float amplitude, float duration)
        {
            if (amplitude <= 0f || duration <= 0f) return;

            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (left.isValid) left.SendHapticImpulse(0, amplitude, duration);

            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (right.isValid) right.SendHapticImpulse(0, amplitude, duration);
        }
    }
}
