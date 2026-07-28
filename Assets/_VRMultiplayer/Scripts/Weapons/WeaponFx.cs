using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Silahin ISTEMCI-TARAFI gorsel efektleri: ucan izler (tracer), namlu alevi, namlu dumani,
    /// mermi izi (decal) havuzu ve oyuncu isabetinde kan. NetworkWeapon'dan ayrildi — ag durumu
    /// OKUMAZ, RPC BILMEZ; tek girisi <see cref="ShowVolley"/>. NetworkWeapon calisma aninda
    /// ekler (prefab degisikligi yok) ve profili <see cref="Setup"/> ile verir.
    ///
    /// Havuz kurallari (performans gerekceleriyle birlikte NetworkWeapon'dan tasindi):
    /// - Mermi izleri TUM silahlarin paylastigi TEK global havuz (dunya kokunde); ilk ize
    ///   kadar hic kurulmaz. Eskiden her silah kendi 48 silindirini kuruyordu ve her silah
    ///   takasi ~50 obje yikip yaratiyordu.
    /// - FX malzemeleri RENK basina paylasimli cache'ten gelir; instance yaratmaz, sizdirmaz.
    /// - Iz cizgileri (LineRenderer) silah basina tembel havuz; MaxTracers ile sinirli.
    /// - Duman + kan da paylasimli DUNYA-uzayi ParticleSystem'lardir (decal havuzu gibi):
    ///   silah birakilsa/cantaya girse de havadaki duman ve ucan damla tamamlanir. Emisyon
    ///   yalnizca Emit() ile — sistem kendi basina particle uretmez.
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

        // Paylasimli partikul sistemleri: namlu dumani, kan damlaciklari, kan sisi.
        // Play cikisinda sahne objeleri ve runtime asset'ler (doku/malzeme) Unity tarafindan
        // yok edilir; ResetFxStatics referanslari temizler, ilk kullanim yeniden kurar.
        static ParticleSystem _smokePs;
        static ParticleSystem _bloodDropPs;
        static ParticleSystem _bloodMistPs;
        static Material _puffMat, _dropMat;
        static Texture2D _puffTex, _dropTex;

        // Domain reload kapali projede play'e her giriste statikler elle sifirlanir.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetFxStatics()
        {
            _sharedDecalRoot = null;
            _sharedDecals = null;
            _sharedDecalRenderers = null;
            _sharedDecalNext = 0;
            _fxMatCache.Clear();
            _smokePs = null;
            _bloodDropPs = null;
            _bloodMistPs = null;
            _puffMat = null;
            _dropMat = null;
            _puffTex = null;
            _dropTex = null;
        }

        // Ucan ates izleri: her atis/pellet dunya uzayinda saklanir, her kare ilerletilir.
        // Havuz MaxTracers ile sinirli — asilirsa en eski iz devrilir (pompali seri atis).
        struct ShotFx
        {
            public Vector3 origin, dir, end, normal;
            public float dist, firedAt;
            public bool impactShown;
            public bool flesh; // sunucu "hasar uygulandi" dedi: varista decal degil KAN cizilir
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
            EnsureSharedParticleFx(); // doku uretimi + sistem kurulumu ilk atis karesine kalmasin

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

        /// <summary>Bir volley'in tum gorsellerini baslatir (izler + alev + namlu dumani +
        /// varista kivilcim/kan). fleshMask: bit i = i. pellet bir OYUNCUYA hasar verdi
        /// (sunucu yazdi) — varista decal yerine kan cizilir.
        /// Ses BURADA CALMAZ — kim duyacagina ag katmani (NetworkWeapon) karar verir.</summary>
        public void ShowVolley(Vector3 origin, Vector3[] ends, Vector3[] normals, int fleshMask = 0)
        {
            if (ends == null || ends.Length == 0) return;

            Vector3 smokeDir = transform.forward;
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
                    flesh = ((fleshMask >> i) & 1) != 0,
                };
                if (i == 0) smokeDir = f.dir; // ilk pellet = nisan tabani; duman namlu yonune
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

            // Namlu dumani: pellet sayisindan bagimsiz atis basina TEK salvo (alev gibi).
            EmitMuzzleSmoke(origin, smokeDir);

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
                    if (!f.impactShown) { ShowArrival(f); f.impactShown = true; }
                    done = Time.time - f.firedAt > 0.07f;
                }
                else
                {
                    float travelled = (Time.time - f.firedAt) * speed;
                    // Kivilcim/kan izin ucu hedefe VARDIGINDA parlar, atisla ayni anda degil.
                    if (travelled >= f.dist && !f.impactShown)
                    {
                        ShowArrival(f);
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

        /// <summary>Izin ucu hedefe vardi: oyuncuysa kan, sabit geometriyse mermi izi.</summary>
        void ShowArrival(in ShotFx f)
        {
            if (f.flesh) EmitBlood(f.end, f.dir);
            else ShowImpact(f.end, f.normal);
        }

        // ------------------------------------------------- paylasimli partikul FX (duman + kan)

        /// <summary>Namlu dumani: atisla birlikte namludan hizla cikan kisa gaz jeti + arkasindan
        /// yavasca buyuyup dagilan gri puf'lar. Dunya-uzayi paylasimli sistem — silah hareket
        /// etse/el degistirse de birakilan duman oldugu yerde asili kalip dagilir.</summary>
        void EmitMuzzleSmoke(Vector3 origin, Vector3 dir)
        {
            EnsureSharedParticleFx();
            if (_smokePs == null) return;

            var ep = new ParticleSystem.EmitParams();

            // Gaz jeti: namlu hattinda hizli cikar, drag ile aniden yavaslar (ilk "fis" hissi).
            for (int i = 0; i < 3; i++)
            {
                ep.position = origin + dir * Random.Range(0f, 0.05f);
                ep.velocity = dir * Random.Range(1.2f, 2.4f) + Random.insideUnitSphere * 0.15f;
                ep.startSize = Random.Range(0.10f, 0.16f);
                ep.startLifetime = Random.Range(0.35f, 0.6f);
                ep.startColor = new Color(0.78f, 0.78f, 0.80f, 0.30f);
                ep.rotation = Random.Range(0f, 360f);
                ep.angularVelocity = Random.Range(-40f, 40f);
                _smokePs.Emit(ep, 1);
            }

            // Oyalanan puf: yavas surur, hafif yukselir, buyuyerek seffaflasir (barut dumani).
            for (int i = 0; i < 4; i++)
            {
                ep.position = origin + dir * Random.Range(0.02f, 0.10f);
                ep.velocity = dir * Random.Range(0.15f, 0.5f)
                            + Vector3.up * Random.Range(0.08f, 0.22f)
                            + Random.insideUnitSphere * 0.12f;
                ep.startSize = Random.Range(0.18f, 0.30f);
                ep.startLifetime = Random.Range(1.1f, 1.9f);
                ep.startColor = new Color(0.72f, 0.72f, 0.74f, 0.22f);
                ep.rotation = Random.Range(0f, 360f);
                ep.angularVelocity = Random.Range(-25f, 25f);
                _smokePs.Emit(ep, 1);
            }
        }

        /// <summary>Oyuncu isabet noktasinda kan: cogunlukla GIRIS tarafina (atana dogru) geri
        /// sicrayan, yercekimiyle dusen damlaciklar + birkac cikis-yonu damlasi + isabetin
        /// uzaktan da okunmasini saglayan kisa kirmizi sis. dir = merminin gidis yonu.</summary>
        static void EmitBlood(Vector3 pos, Vector3 dir)
        {
            EnsureSharedParticleFx();
            if (_bloodDropPs == null) return;

            var ep = new ParticleSystem.EmitParams();

            for (int i = 0; i < 14; i++)
            {
                bool exitSide = i >= 10; // 4 damla cikis yonune: delip gecme hissi
                Vector3 baseV = exitSide
                    ? dir * Random.Range(0.8f, 2.2f)
                    : -dir * Random.Range(1.0f, 3.0f);
                ep.position = pos;
                ep.velocity = baseV + Random.insideUnitSphere * 1.1f + Vector3.up * 0.4f;
                ep.startSize = Random.Range(0.012f, 0.030f);
                ep.startLifetime = Random.Range(0.45f, 0.80f);
                // Koyu, hafif degisken kirmizi; parlak neon degil (alpha-blend, additive degil).
                ep.startColor = new Color(0.42f + Random.value * 0.12f, 0.015f, 0.02f, 1f);
                _bloodDropPs.Emit(ep, 1);
            }

            for (int i = 0; i < 3; i++)
            {
                ep.position = pos + Random.insideUnitSphere * 0.03f;
                ep.velocity = -dir * Random.Range(0.15f, 0.45f) + Random.insideUnitSphere * 0.25f;
                ep.startSize = Random.Range(0.16f, 0.26f);
                ep.startLifetime = Random.Range(0.35f, 0.55f);
                ep.startColor = new Color(0.45f, 0.02f, 0.03f, 0.60f);
                ep.rotation = Random.Range(0f, 360f);
                ep.angularVelocity = Random.Range(-30f, 30f);
                _bloodMistPs.Emit(ep, 1);
            }
        }

        /// <summary>Paylasimli duman/kan sistemlerini (tembel) kurar. Sahne gecisi objeleri
        /// oldurduyse yeniden kurulur (decal havuzuyla ayni fake-null sozlesmesi).</summary>
        static void EnsureSharedParticleFx()
        {
            if (_smokePs != null && _bloodDropPs != null && _bloodMistPs != null) return;

            if (_puffTex == null) _puffTex = MakePuffTexture(64);
            if (_dropTex == null) _dropTex = MakeDropTexture(32);
            if (_puffMat == null) _puffMat = MakeParticleMaterial(_puffTex);
            if (_dropMat == null) _dropMat = MakeParticleMaterial(_dropTex);

            // --- Namlu dumani ---
            if (_smokePs == null)
            {
                _smokePs = NewWorldParticles("Weapon Smoke (paylasimli)", _puffMat, 256, false);
                var main = _smokePs.main;
                main.gravityModifier = -0.02f; // sicak gaz: belli belirsiz yukselme
                var lv = _smokePs.limitVelocityOverLifetime;
                lv.enabled = true;
                lv.limit = 0.12f;  // jet bu hiza kadar sonumlenir, sonra sadece surumlenir
                lv.dampen = 0.35f;
                var col = _smokePs.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(FadeInOutGradient(0.12f));
                var sz = _smokePs.sizeOverLifetime;
                sz.enabled = true;
                // startSize = SON boyut; puf %35'ten buyuyerek acilir (dagilan barut dumani).
                sz.size = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.3f, 0.7f), new Keyframe(1f, 1f)));
                var noise = _smokePs.noise;
                noise.enabled = true; // 256 particle tavaninda maliyeti onemsiz, dumani "canli" tutar
                noise.strength = 0.05f;
                noise.frequency = 0.6f;
                noise.scrollSpeed = 0.15f;
                noise.quality = ParticleSystemNoiseQuality.Low;
            }

            // --- Kan damlaciklari ---
            if (_bloodDropPs == null)
            {
                _bloodDropPs = NewWorldParticles("Blood Drops (paylasimli)", _dropMat, 512, true);
                var main = _bloodDropPs.main;
                main.gravityModifier = 1.4f; // damla balistigi: fiskirir, kavis cizip duser
                var col = _bloodDropPs.colorOverLifetime;
                col.enabled = true;
                // Omrunun cogunda opak kalir, sonda hizla kaybolur (havada eriyen damla degil).
                var g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(g);
            }

            // --- Kan sisi ---
            if (_bloodMistPs == null)
            {
                _bloodMistPs = NewWorldParticles("Blood Mist (paylasimli)", _puffMat, 128, false);
                var main = _bloodMistPs.main;
                main.gravityModifier = 0.12f; // sis hafifce coker
                var lv = _bloodMistPs.limitVelocityOverLifetime;
                lv.enabled = true;
                lv.limit = 0.08f;
                lv.dampen = 0.5f;
                var col = _bloodMistPs.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(FadeInOutGradient(0.10f));
                var sz = _bloodMistPs.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 0.45f), new Keyframe(1f, 1f)));
            }
        }

        /// <summary>Yalnizca Emit() ile beslenen dunya-uzayi partikul sistemi iskeleti.</summary>
        static ParticleSystem NewWorldParticles(string name, Material mat, int maxParticles, bool stretched)
        {
            var go = new GameObject(name);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = true;
            main.maxParticles = maxParticles;
            // Otomatik culling bakis disinda simulasyonu durdurur; VR'da kafa cevirip donunce
            // havada DONMUS duman gorunurdu. Sistemler kucuk, hep simule et.
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var em = ps.emission; em.enabled = false;   // particle yalniz Emit()'ten
            var sh = ps.shape; sh.enabled = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (stretched)
            {
                // Damla hiza gore uzar: hizli fiskiran kan cizgi, dusen damla nokta gorunur.
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.035f;
                r.lengthScale = 1f;
            }
            return ps;
        }

        /// <summary>Alpha: 0'dan tepeye hizli girer (sert dogum pop'u olmasin), sonra uzun soner.</summary>
        static Gradient FadeInOutGradient(float peakAt)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, peakAt), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        /// <summary>Radyal solme x Perlin gurultusu: kenari seffaf, ici lekeli duman pufu dokusu.
        /// Sabit tohum — her istemcide ayni gorunum.</summary>
        static Texture2D MakePuffTexture(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size - 0.5f, ny = (y + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny) * 2f; // 0 merkez, 1 kenar
                    float fall = Mathf.Clamp01(1f - r);
                    fall = fall * fall * (3f - 2f * fall); // smoothstep
                    float n = Mathf.PerlinNoise(3.7f + x * 5f / size, 8.1f + y * 5f / size) * 0.55f
                            + Mathf.PerlinNoise(1.3f + x * 11f / size, 5.9f + y * 11f / size) * 0.45f;
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, fall * (0.3f + 0.7f * n)));
                }
            }
            t.Apply(false, true);
            return t;
        }

        /// <summary>Sert kenarli yuvarlak damla dokusu (ince yumusak halo ile).</summary>
        static Texture2D MakeDropTexture(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size - 0.5f, ny = (y + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny) * 2f;
                    float a = 1f - Mathf.SmoothStep(0.55f, 0.95f, r);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            t.Apply(false, true);
            return t;
        }

        /// <summary>Partikul malzemesi: Resources'taki WeaponParticle.mat (WarFX alpha-blend,
        /// vertex-color carpimli — ayni malzeme gri duman ve kirmizi kani particle rengiyle
        /// boyar) kopyalanir; o yoksa build'de her zaman gemide olan Sprites/Default'a duser.
        /// Resources kopyasi ayrica WFX shader'inin build'den strip edilmemesini garantiler.</summary>
        static Material MakeParticleMaterial(Texture2D tex)
        {
            var baseMat = Resources.Load<Material>("WeaponFx/WeaponParticle");
            Material m;
            if (baseMat != null)
            {
                m = new Material(baseMat); // asset'i kirletme: kopya uzerinde doku degistir
            }
            else
            {
                var sh = Shader.Find("WFX/Alpha Blended (No Soft Particles)");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                m = new Material(sh);
            }
            m.mainTexture = tex;
            return m;
        }
    }
}
