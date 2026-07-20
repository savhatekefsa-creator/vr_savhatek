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
    /// Rolloff LINEAR: maxDistance'ta ses 0'a iner (Logarithmic kesmez — uzak kaynaklar voice
    /// tuketip havuzu bogar). Dolum gibi uzun sesler icin ayri ONCELIKLI kaynak kullanilir ki
    /// atis selinde devrilip kesilmesinler.
    /// </summary>
    public static class WeaponAudioPlayer
    {
        const int PoolSize = 16;

        static Transform _root;
        static AudioSource[] _pool;
        static AudioSource _priority;
        static int _next;
        static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>3B tek-seferlik ses. clipPath bos ya da klip yoksa SESSIZCE doner —
        /// hicbir kosulda exception/hata uretmez.</summary>
        public static void PlayAt(string clipPath, Vector3 pos, float volume,
            float pitchMin = 1f, float pitchMax = 1f, float maxDistance = 120f, bool priority = false)
        {
            var clip = Load(clipPath);
            if (clip == null || volume <= 0f) return;
            EnsurePool();

            AudioSource s = priority ? _priority : Pick();
            s.transform.position = pos;
            s.maxDistance = Mathf.Max(1f, maxDistance);
            s.minDistance = 1f;
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
            s.rolloffMode = AudioRolloffMode.Linear;
            s.dopplerLevel = 0f; // VR'da hizli el hareketi pitch'i bukmesin
            s.priority = priority;
            return s;
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
            _clips.Clear(); _warned.Clear();
        }
    }
}
