using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Audio
{
    /// <summary>
    /// Silah sesleri icin AudioSource HAVUZU. PlayClipAtPoint DEGIL: o her atista GameObject
    /// yaratip yok eder — otomatik silahta saniyede ~10 alloc, Quest'te GC takilmasi demek.
    ///
    /// Klipler Resources yolundan ISIMLE yuklenir ve cache'lenir. Klip YOKSA HATA YOK: isim
    /// basina tek uyari loglanir, cagri sessizce gecer — ses dosyalari sonradan ayni isimlerle
    /// (Resources/WeaponSounds/...) eklendiginde kod degisikligi olmadan calismaya baslar.
    ///
    /// Rolloff OZEL EGRI: kulak sesi 1/mesafe algilar; Linear rolloff 120 m'lik silah
    /// sesinde 10 m otedeki dolumu %92'de caldiriyordu — sunucudaki herkes her seyi
    /// "dibinden" duyuyordu. Egri yakinda hizla duser (10 m ~%19, 30 m ~%7) ve Logarithmic'in
    /// aksine maxDistance'ta GERCEKTEN 0'a iner (uzak kaynak voice tuketip havuzu bogmaz).
    /// Dolum gibi uzun sesler icin ayri ONCELIKLI kaynak kullanilir ki atis selinde
    /// devrilip kesilmesinler.
    /// </summary>
    public static class WeaponAudioPlayer
    {
        const int PoolSize = 16;

        static Transform _root;
        static AudioSource[] _pool;
        static AudioSource _priority;
        static int _next;
        static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        static readonly Dictionary<float, AnimationCurve> _curves = new Dictionary<float, AnimationCurve>();
        static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>3B tek-seferlik ses. clipPath bos ya da klip yoksa SESSIZCE doner —
        /// hicbir kosulda exception/hata uretmez.</summary>
        /// <param name="refDistance">Sesin YARIYA dustugu mesafe, METRE. 0 (varsayilan) =
        /// eski davranis, yani a=0.02 ve ref maxDistance ile birlikte olceklenir. Sifirdan
        /// buyuk verilirse mesafe modeli maxDistance'tan BAGIMSIZ olur — bkz.
        /// <see cref="Rolloff"/>'daki tuzak aciklamasi.</param>
        public static void PlayAt(string clipPath, Vector3 pos, float volume,
            float pitchMin = 1f, float pitchMax = 1f, float maxDistance = 120f, bool priority = false,
            float refDistance = 0f)
        {
            var clip = Load(clipPath);
            if (clip == null || volume <= 0f) return;
            EnsurePool();

            // Oncelikli kaynak tekse ve doluysa (iki silah ayni anda dolumda) yenisi normal
            // havuza duser — calan uzun ses ORTASINDA kesilmez.
            AudioSource s = priority && !_priority.isPlaying ? _priority : Pick();
            s.transform.position = pos;
            s.spatialBlend = 1f; // havuz Play2D ile paylasiliyor — onceki cagri 0 birakmis olabilir
            s.maxDistance = Mathf.Max(1f, maxDistance);
            s.minDistance = 1f;

            // Egri KAYNAK YARATILIRKEN degil BURADA kuruluyor: ayni havuzu silah sesi ile
            // adim sesi paylasiyor ve mesafe modelleri farkli. Egri nesneleri a'ya gore
            // onbellekte, sesi calmak yeni AnimationCurve uretmez.
            float a = refDistance > 0f
                ? Mathf.Clamp(refDistance / Mathf.Max(1f, maxDistance), 0.001f, 0.9f)
                : 0.02f;
            s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, Rolloff(a));
            s.pitch = pitchMax > pitchMin ? Random.Range(pitchMin, pitchMax) : pitchMin;
            s.volume = Mathf.Clamp01(volume);
            s.clip = clip;
            s.Play();
        }

        /// <summary>KISIYE OZEL tek-seferlik ses: mesafe/yon hesabina girmeden dogrudan
        /// kulaklikta calar (2D). Vucuda mermi girisi gibi yalniz o oyuncuyu ilgilendiren
        /// geri bildirimler icindir — cagiran taraf "yalniz bende calsin" filtresini
        /// (IsOwner vb.) kendisi uygular.</summary>
        public static void Play2D(string clipPath, float volume, float pitchMin = 1f, float pitchMax = 1f)
        {
            var clip = Load(clipPath);
            if (clip == null || volume <= 0f) return;
            EnsurePool();

            AudioSource s = Pick();
            s.spatialBlend = 0f;
            s.pitch = pitchMax > pitchMin ? Random.Range(pitchMin, pitchMax) : pitchMin;
            s.volume = Mathf.Clamp01(volume);
            s.clip = clip;
            s.Play();
        }

        static void EnsurePool()
        {
            if (_root != null) return;
            var go = new GameObject("~WeaponAudio");
            Object.DontDestroyOnLoad(go);
            _root = go.transform;
            _pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++) _pool[i] = NewSource("Src" + i, 128);
            _priority = NewSource("Priority", 32); // dusuk sayi = yuksek oncelik
        }

        static AudioSource NewSource(string name, int priority)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Custom;
            // Egrinin kendisi PlayAt'te kuruluyor — kaynaklar havuzda paylasildigi icin
            // burada kurulan tek bir egri, farkli mesafe modeli isteyen cagrilari birbirine
            // baglardi.
            s.dopplerLevel = 0f; // VR'da hizli el hareketi pitch'i bukmesin
            s.priority = priority;
            return s;
        }

        /// <summary>
        /// Algisal 1/mesafe egrisi. x = mesafe/maxDistance icin:  v = (a/(a+x)) * (1-x^2).
        /// Ilk carpan kulaktaki dogal 1/d dususunu verir, ikincisi egriyi maxDistance'ta
        /// KESIN 0'a indirir (Logarithmic'in aksine — uzak kaynak voice tuketip havuzu bogmaz).
        ///
        /// a NE DEMEK: boyutsuz bir sabit gibi durur ama degildir. a = ref/maxDistance'tir ve
        /// sadelestirince a/(a+x) = ref/(ref+d) cikar. Yani a, sesin YARIYA DUSTUGU mesafeyi
        /// (ref) maxDistance cinsinden ifade eder:
        ///     v(d) = [ref/(ref+d)] * [1-(d/max)^2]
        ///
        /// BURASI BIR TUZAKTI. a sabit tutulup maxDistance kucultulurse ref de onunla birlikte
        /// SESSIZCE kuculur. Silahta a=0.02, max=120 -> ref=2.4 m (dogru; 0.6 m %80, 10 m %19).
        /// Ama ayni a ile adim sesi 22 m'ye cagriliyordu -> ref=0.44 m: ses her 44 santimde
        /// yariliyor, 5 m'de %7'ye iniyordu. Adim sesinin duyulmamasinin sebeplerinden biri
        /// buydu. Yeni cagrilar bu yuzden refDistance'i METRE cinsinden verir.
        /// </summary>
        static AnimationCurve Rolloff(float a)
        {
            if (_curves.TryGetValue(a, out var cached)) return cached;

            var keys = new Keyframe[12];
            float[] xs = { 0f, 0.004f, 0.01f, 0.02f, 0.04f, 0.08f, 0.15f, 0.25f, 0.4f, 0.6f, 0.8f, 1f };
            for (int i = 0; i < xs.Length; i++)
            {
                float x = xs[i];
                keys[i] = new Keyframe(x, (a / (a + x)) * (1f - x * x));
            }
            var c = new AnimationCurve(keys);
            for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
            _curves[a] = c;
            return c;
        }

        static AudioSource Pick()
        {
            // Once bos kaynak; hepsi doluysa siradaki (en eski) devrilir.
            for (int i = 0; i < PoolSize; i++)
            {
                int idx = (_next + i) % PoolSize;
                if (!_pool[idx].isPlaying) { _next = (idx + 1) % PoolSize; return _pool[idx]; }
            }
            var oldest = _pool[_next];
            _next = (_next + 1) % PoolSize;
            return oldest;
        }

        static AudioClip Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_clips.TryGetValue(path, out var c)) return c;
            c = Resources.Load<AudioClip>(path);
            _clips[path] = c; // null da cache'lenir: her cagri Resources taramasin
            if (c == null && _warned.Add(path))
                Debug.LogWarning($"[SilahSes] Klip bulunamadi: Resources/{path} — dosya eklenene kadar sessiz.");
            return c;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _root = null; _pool = null; _priority = null; _next = 0;
            _clips.Clear(); _warned.Clear(); _curves.Clear();
        }
    }
}
