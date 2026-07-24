using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Silahin ISTEMCI-TARAFI gorsel efektleri: ucan izler (tracer), namlu alevi ve mermi izi
    /// (decal) havuzu. NetworkWeapon'dan ayrildi — ag durumu OKUMAZ, RPC BILMEZ; tek girisi
    /// <see cref="ShowVolley"/>. NetworkWeapon calisma aninda ekler (prefab degisikligi yok)
    /// ve profili <see cref="Setup"/> ile verir.
    ///
    /// Havuz kurallari (performans gerekceleriyle birlikte NetworkWeapon'dan tasindi):
    /// - Mermi izleri TUM silahlarin paylastigi TEK global havuz (dunya kokunde); ilk ize
    ///   kadar hic kurulmaz. Eskiden her silah kendi 48 silindirini kuruyordu ve her silah
    ///   takasi ~50 obje yikip yaratiyordu.
    /// - FX malzemeleri RENK basina paylasimli cache'ten gelir; instance yaratmaz, sizdirmaz.
    /// - Iz cizgileri (LineRenderer) silah basina tembel havuz; MaxTracers ile sinirli.
    /// </summary>
    public class WeaponFx : MonoBehaviour
    {
        WeaponGripProfile _profile;

        // Effects: iz cizgileri havuzdan (pellet = ayni anda birden cok ucan iz), alev tek.
        readonly List<LineRenderer> _tracers = new List<LineRenderer>();
        Material _tracerMat;
        Light _flash;
        Material _impactMat; // bu silahin iz malzemesi (paylasimli cache'ten)

        const int DecalCount = 96;
        static Transform _sharedDecalRoot;
        static Transform[] _sharedDecals;
        static MeshRenderer[] _sharedDecalRenderers;
        static int _sharedDecalNext;

        static readonly Dictionary<Color32, Material> _fxMatCache = new Dictionary<Color32, Material>();

        // Domain reload kapali projede play'e her giriste statikler elle sifirlanir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetFxStatics()
        {
            _sharedDecalRoot = null;
            _sharedDecals = null;
            _sharedDecalRenderers = null;
            _sharedDecalNext = 0;
            _fxMatCache.Clear();
        }

        // Ucan ates izleri: her atis/pellet dunya uzayinda saklanir, her kare ilerletilir.
        // Havuz MaxTracers ile sinirli — asilirsa en eski iz devrilir (pompali seri atis).
        struct ShotFx
        {
            public Vector3 origin, dir, end, normal;
            public float dist, firedAt;
            public bool impactShown;
        }
        // NetworkWeapon.MaxPellets (16) x 2: ardisik volley'ler ust uste binebilir (otomatik).
        const int MaxTracers = 32;
        readonly List<ShotFx> _flights = new List<ShotFx>();
        bool _tracersOn;
        float _flashOffAt = -1f;

        Color TracerColor => _profile != null ? _profile.tracerColor : new Color(1f, 0.45f, 0.12f);
        float TracerSpeed => _profile != null ? _profile.tracerSpeed : 260f;
        float TracerLength => _profile != null ? _profile.tracerLength : 2.5f;
        float TracerWidth => _profile != null ? _profile.tracerWidth : 0.03f;
        float FlashDuration => _profile != null ? _profile.flashDuration : 0.035f;
        Color ImpactColor => _profile != null ? _profile.impactColor : new Color(0.03f, 0.03f, 0.04f, 1f);
        float ImpactSize => _profile != null ? _profile.impactSize : 0.022f;

        /// <summary>NetworkWeapon.Awake cagirir: profil (null olabilir) + FX on-kurulumu.</summary>
        public void Setup(WeaponGripProfile profile)
        {
            _profile = profile;
            _tracerMat = GetFxMaterial(TracerColor);
            EnsureTracers(1); // ilk iz hazir; pellet gelirse havuz lazily buyur

            var flashGo = new GameObject("Muzzle Flash");
            flashGo.transform.SetParent(transform, false);
            _flash = flashGo.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.8f, 0.4f);
            _flash.intensity = 3f;
            _flash.range = 4f;
            _flash.enabled = false;

            // Iz malzemesi paylasimli cache'ten; global iz havuzunu ilk ShowImpact tembel kurar.
            if (ImpactSize > 0f) _impactMat = GetFxMaterial(ImpactColor);
        }

        /// <summary>Renk basina cache'lenmis, calisma aninda uretilmis unlit malzeme. Silah
        /// instance'lari malzemeyi PAYLASIR — kimse yok etmemeli.</summary>
        static Material GetFxMaterial(Color c)
        {
            Color32 key = c;
            if (!_fxMatCache.TryGetValue(key, out var m) || m == null)
            {
                m = new Material(VRMultiplayer.UI.UITheme.SafeUnlitShader);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                else m.color = c;
                _fxMatCache[key] = m;
            }
            return m;
        }

        /// <summary>Global mermi izi havuzunu (tembel) kurar. Sahne gecisinde kok yok olduysa
        /// yeniden kurulur (Unity fake-null kontrolu).</summary>
        static void EnsureSharedDecalPool()
        {
            if (_sharedDecalRoot != null && _sharedDecals != null) return;
            _sharedDecalRoot = new GameObject("Bullet Holes (paylasimli)").transform;
            _sharedDecals = new Transform[DecalCount];
            _sharedDecalRenderers = new MeshRenderer[DecalCount];
            _sharedDecalNext = 0;
            for (int i = 0; i < DecalCount; i++)
            {
                // Yassilastirilmis SILINDIR = yuvarlak disk (kursun deligi kare degil yuvarlak).
                // Silindir de kup gibi simetrik, yani normalin isareti yanlis olsa bile gorunmez
                // yuze donmez (Quad'in tek yuzu var, ters donerse hic cizilmez).
                var d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                d.name = "Bullet Hole";
                Destroy(d.GetComponent<Collider>());
                d.transform.SetParent(_sharedDecalRoot, false);
                d.SetActive(false);
                _sharedDecals[i] = d.transform;
                _sharedDecalRenderers[i] = d.GetComponent<MeshRenderer>();
            }
        }

        LineRenderer NewTracer()
        {
            var go = new GameObject("Tracer");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.widthMultiplier = TracerWidth;
            lr.material = _tracerMat;
            lr.enabled = false;
            return lr;
        }

        void EnsureTracers(int n)
        {
            n = Mathf.Min(n, MaxTracers);
            while (_tracers.Count < n) _tracers.Add(NewTracer());
        }

        /// <summary>Bir volley'in tum gorsellerini baslatir (izler + alev + varista kivilcim).
        /// Ses BURADA CALMAZ — kim duyacagina ag katmani (NetworkWeapon) karar verir.</summary>
        public void ShowVolley(Vector3 origin, Vector3[] ends, Vector3[] normals)
        {
            if (ends == null || ends.Length == 0) return;

            for (int i = 0; i < ends.Length; i++)
            {
                Vector3 d = ends[i] - origin;
                float dist = d.magnitude;
                var f = new ShotFx
                {
                    origin = origin,
                    end = ends[i],
                    normal = normals != null && i < normals.Length ? normals[i] : Vector3.zero,
                    dir = dist > 1e-4f ? d / dist : transform.forward,
                    dist = dist,
                    firedAt = Time.time,
                };
                if (_flights.Count >= MaxTracers) _flights.RemoveAt(0);
                _flights.Add(f);
            }

            // Namlu alevi: pellet sayisi kac olursa olsun TEK parlama. Alev silahin cocugu:
            // dunya noktasi bir kez yazilir, sonra silahla birlikte hareket eder.
            if (_flash != null)
            {
                _flash.transform.position = origin;
                _flash.enabled = true;
                _flashOffAt = Time.time + FlashDuration;
            }

            UpdateFx(); // ilk kareyi hemen ciz: bir kare gecikmeyle baslamasin
        }

        // Herkeste, silah elde olmasa da calisir: ucan iz, sahibi silahi biraksa da tamamlanir.
        void Update() => UpdateFx();

        // Izleri namludan hedefe dogru UCURUR (pellet basina bir iz). Eskiden tam boy cizgi
        // aninda cizilip 70 ms duruyordu: silahi cevirirken donuk cizgi namludan kopuk kaliyor
        // ve atis sapmis gibi gorunuyordu.
        void UpdateFx()
        {
            if (_flashOffAt > 0f && Time.time > _flashOffAt)
            {
                _flashOffAt = -1f;
                if (_flash != null) _flash.enabled = false;
            }

            if (_flights.Count == 0)
            {
                if (_tracersOn)
                {
                    for (int i = 0; i < _tracers.Count; i++) _tracers[i].enabled = false;
                    _tracersOn = false;
                }
                return;
            }
            _tracersOn = true;

            float speed = TracerSpeed;
            float len = Mathf.Max(0.1f, TracerLength);

            // GECIS 1 — ilerlet/temizle: biten ucuslar listeden cikar, varista kivilcim.
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                bool done;
                if (speed <= 0f)
                {
                    // Hiz 0 = eski davranis: aninda tam boy cizgi, ~70 ms sonra soner.
                    if (!f.impactShown) { ShowImpact(f.end, f.normal); f.impactShown = true; }
                    done = Time.time - f.firedAt > 0.07f;
                }
                else
                {
                    float travelled = (Time.time - f.firedAt) * speed;
                    // Kivilcim izin ucu hedefe VARDIGINDA parlar, atisla ayni anda degil.
                    if (travelled >= f.dist && !f.impactShown)
                    {
                        ShowImpact(f.end, f.normal);
                        f.impactShown = true;
                    }
                    done = travelled - len >= f.dist;
                }
                if (done) _flights.RemoveAt(i);
                else _flights[i] = f;
            }

            // GECIS 2 — ciz: kalan ucuslar temiz index eslesmesiyle havuza yazilir (silme
            // sonrasi ayni karede cizim yapildigi icin 1-karelik iz kaymasi olmaz).
            EnsureTracers(_flights.Count);
            int drawn = Mathf.Min(_flights.Count, _tracers.Count);
            for (int i = 0; i < drawn; i++)
            {
                var f = _flights[i];
                var t = _tracers[i];
                if (speed <= 0f)
                {
                    t.SetPosition(0, f.origin);
                    t.SetPosition(1, f.end);
                }
                else
                {
                    float travelled = (Time.time - f.firedAt) * speed;
                    float head = Mathf.Min(travelled, f.dist);
                    float tail = Mathf.Max(0f, travelled - len);
                    t.SetPosition(0, f.origin + f.dir * tail);
                    t.SetPosition(1, f.origin + f.dir * head);
                }
                t.enabled = true;
            }
            for (int i = drawn; i < _tracers.Count; i++)
                _tracers[i].enabled = false;
        }

        void ShowImpact(Vector3 end, Vector3 normal)
        {
            // Normal sifir = hicbir seye carpmadi ya da bir OYUNCUYA carpti: iz birakma.
            // Yuruyen bir oyuncuya cakilan dunya-uzayi izi havada asili kalirdi.
            if (_impactMat == null || normal.sqrMagnitude < 0.5f) return;

            EnsureSharedDecalPool();
            int idx = _sharedDecalNext;
            _sharedDecalNext = (_sharedDecalNext + 1) % _sharedDecals.Length;
            var d = _sharedDecals[idx];
            if (d == null) return; // sahne gecisi havuzu oldurmus — bir sonraki cagri yeniden kurar
            _sharedDecalRenderers[idx].sharedMaterial = _impactMat; // renk silah-basina

            // Silindirin ekseni LOKAL Y; onu yuzey normaline hizala. Olcek: mesh yaricapi 0.5
            // (yani cap = scale.x) ve yuksekligi 2 (yani kalinlik = 2 * scale.y).
            float s = ImpactSize;
            d.SetPositionAndRotation(end + normal * 0.001f,
                Quaternion.FromToRotation(Vector3.up, normal));
            d.localScale = new Vector3(s, 0.0015f, s); // kalinlik 3 mm: yuzeye gomulu dursun
            d.gameObject.SetActive(true);
        }
    }
}
