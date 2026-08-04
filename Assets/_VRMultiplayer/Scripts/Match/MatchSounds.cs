using UnityEngine;

namespace VRMultiplayer.Match
{
    /// <summary>
    /// Mac bildirimi sesleri — HEPSI KODDA URETILIR, ses dosyasi yok.
    ///
    /// NEDEN PROSEDUREL: projede mac icin hicbir ses varligi yoktu ve VR'da ses en guclu
    /// bildirim kanali. Birkac sinus tonu icin asset aramak/lisanslamak yerine ortuluyorlar —
    /// doku tarafinda ayni yaklasim zaten var (<c>UITheme.VignetteTexture</c>,
    /// <c>HealthGradientTexture</c>). Uretilen klipler bir kez olusturulup paylasilir.
    ///
    /// TIKLAMA SESI OLMASIN diye her klipte zarf var: sifirdan baslamayan bir dalga hoparlorde
    /// "tak" diye patlar. Giris 5 ms, cikis toplam surenin son %35'i.
    /// </summary>
    public static class MatchSounds
    {
        const int SampleRate = 44100;

        static AudioClip _beep, _tick, _startHorn, _endHorn;

        /// <summary>Geri sayim vurusu (3-2-1). Net ve kisa.</summary>
        public static AudioClip Beep => _beep != null ? _beep
            : _beep = Tone("MatchBeep", 0.10f, 880f, 880f, 0.45f);

        /// <summary>Macin son 10 saniyesindeki tik-tak. Bilerek daha sonuk ve tiz —
        /// 10 kez ust uste calacagi icin yorucu olmamali.</summary>
        public static AudioClip Tick => _tick != null ? _tick
            : _tick = Tone("MatchTick", 0.055f, 1250f, 1250f, 0.22f);

        /// <summary>Baslangic dudugu: yukselen ton. "Basladi" hissi yukari dogru gider.</summary>
        public static AudioClip StartHorn => _startHorn != null ? _startHorn
            : _startHorn = Tone("MatchStart", 0.50f, 420f, 840f, 0.55f);

        /// <summary>Bitis dudugu: alcalan ton. Baslangicin tersi — kelimesiz anlasilir.</summary>
        public static AudioClip EndHorn => _endHorn != null ? _endHorn
            : _endHorn = Tone("MatchEnd", 0.75f, 760f, 300f, 0.55f);

        /// <summary>Frekansi f0'dan f1'e KAYAN bir sinus tonu uretir (esitse duz ton).</summary>
        static AudioClip Tone(string name, float seconds, float f0, float f1, float volume)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(SampleRate * seconds));
            var data = new float[count];

            float attack = 0.005f * SampleRate;          // 5 ms giris
            float releaseStart = count * 0.65f;          // son %35 cikis
            float phase = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                // Frekans kaymasi: fazi ADIM ADIM biriktiriyoruz. Dogrudan sin(2*pi*f(t)*t)
                // yazmak frekans degisirken faz atlamasi (duyulur catlak) uretir.
                float f = Mathf.Lerp(f0, f1, t);
                phase += 2f * Mathf.PI * f / SampleRate;

                float env = Mathf.Min(i / attack, 1f);
                if (i > releaseStart)
                    env *= 1f - Mathf.Clamp01((i - releaseStart) / (count - releaseStart));

                data[i] = Mathf.Sin(phase) * env * volume;
            }

            var clip = AudioClip.Create(name, count, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir; yok edilmis klibe tutunmus
        // referans ikinci Play'de "MissingReference" verirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _beep = null;
            _tick = null;
            _startHorn = null;
            _endHorn = null;
        }
    }
}
