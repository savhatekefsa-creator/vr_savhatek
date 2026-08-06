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

        // Effects: iz cizgileri havuzdan (pellet = ayni anda birden cok iz), alev tek.
        readonly List<LineRenderer> _tracers = new List<LineRenderer>();
        Material _tracerMat;
        Light _flash;
        Material _impactMat; // bu silahin iz malzemesi (paylasimli cache'ten)

        // Namlu alevi GORSELI: isik tek basina gunduz sahnede gorunmuyordu (cihazda olculdu) —
        // alev, WarFX dokulu uc quad'lik bir rig: iki capraz yan alev (namlu ekseni boyunca)
        // + bir on yildiz. Malzemeler PAYLASIMLI (Resources'tan, additive A8 — renk malzemeden);
        // bu yuzden solma alfa ile degil RIG OLCEGIYLE yapilir, kimsenin malzemesi kirlenmez.
        Transform _flashRig;
        Vector3 _flashRigBaseScale = Vector3.one; // ebeveyn olceginin telafisi * atis jitter'i
        float _flashShownAt = -1f;
        static Material _flashSideMat, _flashFrontMat;

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
            _flashSideMat = null;
            _flashFrontMat = null;
            _shellRoot = null;
            _shellSlots = null;
            _shellHolders = null;
            _shellFilters = null;
            _shellRenderers = null;
            _shellNext = 0;
            _shellFlights.Clear();
            _shellTickFrame = -1;
            _donatedShell = default;
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

        /// <summary>NetworkWeapon.Awake cagirir: profil (null olabilir) + FX on-kurulumu.
        ///
        /// <paramref name="barrelLocal"/> NetworkWeapon'un ComputeBarrel'inden gelir (Setup'tan
        /// ONCE kosar): profili olmayan ya da barrelLocalDirection'i bos silahta da namlu
        /// ekseni dogru bilinsin. Profil doluysa PROFIL KAZANIR — WeaponRecoil'deki oncelikle
        /// ayni. Sürgü yonu ve kovan atma yonu bu eksenden turuyor.</summary>
        public void Setup(WeaponGripProfile profile, Vector3 barrelLocal = default)
        {
            _profile = profile;

            Vector3 barrel = profile != null && profile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? profile.barrelLocalDirection
                : barrelLocal;
            _barrelLocal = barrel.sqrMagnitude > 1e-6f ? barrel.normalized : Vector3.forward;

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

            BuildFlashRig();

            // Iz malzemesi paylasimli cache'ten; global iz havuzunu ilk ShowImpact tembel kurar.
            if (ImpactSize > 0f) _impactMat = GetFxMaterial(ImpactColor);

            ResolveMechanism();   // surgu parcasi + kovan gorseli (bkz. bolum sonu)
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
            ShowMuzzleFlash(origin, smokeDir);

            // Namlu dumani: pellet sayisindan bagimsiz atis basina TEK salvo (alev gibi).
            EmitMuzzleSmoke(origin, smokeDir);

            // Mekanizma: surgu tepmesi + TEK kovan. Pellet basina DEGIL volley basina —
            // pompaliyla tek atista 16 kovan firlamasin.
            KickSlide();
            EjectShell();

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
            TickMuzzleFlash();
            TickSlide();
            TickShells();

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

        // ------------------------------------------------- namlu alevi (gorsel rig)

        /// <summary>Uc quad'lik alev rig'i: iki capraz YAN alev (uzunluk namlu ekseninde) + bir
        /// ON yildiz. WFX shader'lari cift yuzlu, yani her bakis acisindan okunur — VR'da goz
        /// basina billboard derdine girmeden calisir. Rig silahin cocugu: iki-uc karelik omrunde
        /// namluyla birlikte hareket eder (hizli bilek cevirmede alev namludan kopmaz).</summary>
        void BuildFlashRig()
        {
            EnsureFlashMaterials();

            _flashRig = new GameObject("Muzzle Flash Rig").transform;
            _flashRig.SetParent(transform, false);

            // Ebeveyn olcegi silahtan silaha degisiyor; alevin DUNYA boyu sabit kalsin.
            Vector3 ls = transform.lossyScale;
            _flashRigBaseScale = new Vector3(
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));

            // Yan alevler: quad'in +Y'si rig'in +Z'sine (namlu yonu) cevrilir; taban namluda.
            const float len = 0.30f, wid = 0.16f, front = 0.17f;
            for (int i = 0; i < 2; i++)
            {
                var q = FlashQuad("Yan " + i, _flashSideMat);
                q.localRotation = Quaternion.AngleAxis(i * 90f, Vector3.forward)
                                * Quaternion.Euler(90f, 0f, 0f);
                q.localPosition = Vector3.forward * (len * 0.42f);
                q.localScale = new Vector3(wid, len, 1f);
            }
            var f = FlashQuad("On", _flashFrontMat);
            f.localPosition = Vector3.forward * 0.03f;
            f.localScale = new Vector3(front, front, 1f);

            _flashRig.gameObject.SetActive(false);
        }

        Transform FlashQuad(string name, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_flashRig, false);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Atis basina: rig namluya tasinir, namlu ekseni etrafinda rastgele yuvarlanir
        /// ve boyu hafif degisir — iki atis ust uste ayni kareyi gostermez (film karesi hissi
        /// yerine canli alev). Solma TickMuzzleFlash'ta olcekle yapilir.</summary>
        void ShowMuzzleFlash(Vector3 origin, Vector3 dir)
        {
            if (_flashRig == null) return;
            if (dir.sqrMagnitude < 1e-6f) dir = transform.forward;

            _flashRig.SetPositionAndRotation(origin,
                Quaternion.LookRotation(dir) * Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward));
            float jitter = Random.Range(0.85f, 1.25f);
            _flashRig.localScale = _flashRigBaseScale * jitter;
            _flashRig.gameObject.SetActive(true);
            _flashShownAt = Time.time;
        }

        void TickMuzzleFlash()
        {
            if (_flashShownAt < 0f || _flashRig == null) return;

            // Isikla ayni omur ailesi: FlashDuration'in 1.6 kati (~3-4 kare @72 Hz). Additive
            // malzeme paylasimli oldugu icin solma alfayla degil kuculmeyle: parlak dogar,
            // hizla buzulup soner — gercek namlu gazinin okunusu da bu.
            float life = Mathf.Max(0.03f, FlashDuration * 1.6f);
            float t = (Time.time - _flashShownAt) / life;
            if (t >= 1f)
            {
                _flashShownAt = -1f;
                _flashRig.gameObject.SetActive(false);
                return;
            }
            float shrink = 1f - t * t * 0.85f;
            _flashRig.localScale = _flashRigBaseScale * shrink;
        }

        /// <summary>Alev malzemeleri: Resources'taki hazir asset'ler (WarFX Additive A8 + resmi
        /// WarFX namlu dokusu, turuncu tint). Asset yoksa (henuz uretilmedi) iz rengindeki
        /// unlit'e duser — alev yine gorunur, sadece dokusuz. Resources'ta durmalari WFX
        /// shader'inin build'den strip edilmemesini de garantiler (WeaponParticle.mat kurali).</summary>
        static void EnsureFlashMaterials()
        {
            if (_flashSideMat == null)
                _flashSideMat = Resources.Load<Material>("WeaponFx/WeaponFlashSide");
            if (_flashFrontMat == null)
                _flashFrontMat = Resources.Load<Material>("WeaponFx/WeaponFlashFront");

            if (_flashSideMat == null) _flashSideMat = GetFxMaterial(new Color(1f, 0.55f, 0.15f));
            if (_flashFrontMat == null) _flashFrontMat = _flashSideMat;
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

        // ================================================================= mekanizma
        //
        // SURGU TEPMESI + BOS KOVAN. Ikisi de tamamen KOZMETIK ve tamamen YEREL: ag uzerinde
        // fazladan tek bayt tasinmaz. Sebep, ShowVolley'in sozlesmesi — her makinede atis
        // basina TAM BIR KEZ cagriliyor (sahipte tahminle, uzaktakilerde FireFxClientRpc ile),
        // yani buraya takilan her sey herkeste kendiliginden calisiyor.
        //
        // WEAPONRECOIL ILE KARISTIRMA: o silahin KOKUNU oynatir ve yalnizca SAHIPTE calisir,
        // cunku kok pozu ClientNetworkTransform ile zaten yayiliyor. Surgu ise bir ALT PARCA;
        // CNT alt parcalari tasimaz. Bu yuzden burada "mine" filtresi YOK — uzaktaki oyuncunun
        // silahinda surguyu gormek istiyorsak onu o istemcinin kendisi oynatmali.

        /// <summary>
        /// Silah -> mekanizma parcalari. Paket tek bir adlandirma standardi kullanmiyor (her
        /// silah baska bir sanatcidan gelmis), o yuzden isim tahmini yerine ACIK TABLO.
        /// Ayni kalip: <see cref="UI.WeaponLaserBinder"/> / <see cref="UI.WeaponScopeBinder"/>.
        /// </summary>
        struct MechSpec
        {
            public string key;          // GameObject adinda aranan parca (sirali: ilk eslesen kazanir)

            /// <summary>Surgu parcalarinin ad ONEKLERI. Eslesen HER parca birlikte hareket eder
            /// — adaylar arasindan biri secilmez. Sebep Pistol 4: oluklar ("top_slider_*_grooves")
            /// surgunun COCUGU degil KARDESI; yalniz "top_sliderHG25"i oynatmak silahi ikiye
            /// ayirirdi. "top_slider" oneki ucunu birden yakalar.</summary>
            public string[] slideNames;

            public float travel;        // surgunun geri gidecegi mesafe (METRE, dunya)
            public float backT, fwdT;   // geri ve ileri faz sureleri (saniye)
            public string shellHint;    // kovan mesh'inin adinda gecen parca (null = kovan yok)

            /// <summary>Kendi kovan mesh'i OLMAYAN silah bagistan odunc alir; odunc kovan bu
            /// boya (metre, en uzun eksen) getirilir. Sifir = dokunma.
            ///
            /// SART, cunku bagisi hangi silahin yaptigi Setup SIRASINA bagli: HK416 bir
            /// tabanca kovani (2.1 cm) da alabilir, bir keskin nisanci kovani (6.2 cm) de.
            /// Ikincisi elinde devasa durur. Boyu sabitlemek sonucu deterministik yapar.</summary>
            public float shellFallbackSize;
        }

        // SIRA KRITIK: "Weapon_Pistol 2" jenerik "Weapon_Pistol" anahtarini da icerir, o yuzden
        // ozel satirlar jenerigin ONUNDE. Tabloda olmayan silah (Paintball, bombalar, Rifle 1'in
        // surgusu) sessizce mekanizmasiz kalir — laser/dürbün baglayicilarindaki ayni davranis.
        static readonly MechSpec[] MechSpecs =
        {
            // --- tabancalar: surgu TAM YOL gider, cevrim hizli
            new MechSpec { key = "Pistol 2", slideNames = new[] { "Slide1" },
                           travel = 0.035f, backT = 0.030f, fwdT = 0.070f, shellHint = "Bullet_Back_Shell" },
            new MechSpec { key = "Pistol 3", slideNames = new[] { "Main_SliderH2020" },
                           travel = 0.035f, backT = 0.030f, fwdT = 0.070f, shellHint = "shell_base" },
            // "top_slider" oneki surgu + iki oluk parcasini BIRLIKTE tasir (bkz. slideNames).
            // Kovan adi TAM verilmeli: "bullet_shell" oneki once 3.5 cm'lik
            // "bullet_shell_outsideHG25"i yakaliyor; gercek kovan 1.9 cm'lik "bullet_shellHG25".
            new MechSpec { key = "Pistol 4", slideNames = new[] { "top_slider" },
                           travel = 0.035f, backT = 0.030f, fwdT = 0.070f, shellHint = "bullet_shellHG25" },

            // --- otomatikler: gorunen parca kisa yol yapar
            new MechSpec { key = "Rifle 2", slideNames = new[] { "sliderX7_XRQ" },
                           travel = 0.020f, backT = 0.025f, fwdT = 0.060f, shellHint = "bullet_shell" },
            new MechSpec { key = "Smg 1", slideNames = new[] { "mechanism_01Triss_Sector" },
                           travel = 0.020f, backT = 0.025f, fwdT = 0.060f, shellHint = "shell_" },
            new MechSpec { key = "Smg 3", slideNames = new[] { "BoltMP5" },
                           travel = 0.020f, backT = 0.025f, fwdT = 0.060f, shellHint = "Bullet_Shell" },

            // --- yavas mekanizmalar: "blowback" degil "kurma" hissi
            new MechSpec { key = "Sniper 1", slideNames = new[] { "BoltE7" },
                           travel = 0.015f, backT = 0.050f, fwdT = 0.120f, shellHint = "Bullet_Shell" },
            // Kovan oneki "Bullet" DEGIL: 13 cm'lik "Bullet_Holder_beltXY1510" (fisek kayisi)
            // once eslesiyordu. "BulletXY" yalnizca gercek av kovanini (3.6 cm) yakalar.
            new MechSpec { key = "Shotgun 2", slideNames = new[] { "BoltXY1510" },
                           travel = 0.025f, backT = 0.050f, fwdT = 0.120f, shellHint = "BulletXY" },
            // Prefab adi "Weapon_Dmr1" — BOSLUKSUZ. "Dmr 1" anahtari hicbir zaman eslesmezdi.
            new MechSpec { key = "Dmr1", slideNames = new[] { "main_reloding_part12" },
                           travel = 0.030f, backT = 0.030f, fwdT = 0.080f, shellHint = "bullet_shell" },

            // G36C'nin kovan atma penceresi kapagi: gercekte atisla birlikte hareket eder.
            // Kucuk bir parca, o yuzden yol da kisa.
            new MechSpec { key = "Rifle 3", slideNames = new[] { "shell_remover" },
                           travel = 0.012f, backT = 0.025f, fwdT = 0.060f, shellHint = "bullet_shell" },

            // --- surgusuz ama KOVANLI. Rifle 1'in tek hareketli aday parcasi "Receiver_Cover"
            //     ama AK'de ust kapak SABITTIR; oynatmak yanlis gorunur, o yuzden yalniz kovan.
            new MechSpec { key = "Rifle 1", shellHint = "Bullet_Shell" },

            // --- MODELDE MEKANIZMA PARCASI YOK, bolunmeden animasyon imkansiz:
            //     HK416 tek mesh (toplam 2 dugum), Smg 2 (P90) yalnizca 55 fisek kapagi
            //     tasiyor. Kovan da yok -> bagistan, sabit boyla.
            new MechSpec { key = "HK416", shellFallbackSize = 0.045f },   // 5.56 NATO
            new MechSpec { key = "Smg 2", shellFallbackSize = 0.028f },   // 5.7x28

            // Jenerik tabanca EN SONDA (Weapon_Pistol = Pistol 2 modeli).
            new MechSpec { key = "Weapon_Pistol", slideNames = new[] { "Slide1", "Slide" },
                           travel = 0.035f, backT = 0.030f, fwdT = 0.070f, shellHint = "Bullet_Back_Shell" },
        };

        MechSpec _mech;
        bool _hasMech;
        Vector3 _barrelLocal = Vector3.forward;

        // --- surgu durumu. Parca BASINA taban + yon: parcalarin ebeveynleri farkli olabilir,
        // tek bir donusum herkese uymaz.
        Transform[] _slideParts;
        Vector3[] _slideBaseLocal;   // parcanin dokunulmamis localPosition'i
        Vector3[] _slideBackLocal;   // 1 metre GERI = o parcanin ebeveyn uzayinda bu vektor
        float _slideFiredAt = -1f;
        float _slideStartOffset;   // bu cevrime hangi offsetten baslandi (retrigger icin)
        float _slideCurOffset;

        // --- kovan gorseli (silah basina, Setup'ta bir kez)
        struct ShellVisual
        {
            public Mesh mesh;
            public Material mat;
            public Vector3 scale;        // dunya boyunu birebir veren olcek
            public Vector3 centerOffset; // mesh pivot -> geometrik merkez (pivotlar model orijininde!)
            public float radius;         // yere oturma payi
            public bool valid;
        }
        ShellVisual _shell;
        Vector3 _ejectOriginLocal;  // kok-lokal firlatma noktasi
        Vector3 _ejectDirLocal = Vector3.right;

        // Modelinde kovan olmayan silahlar (HK416, Smg 2) icin: ilk bulan BAGISLAR.
        static ShellVisual _donatedShell;

        // --- kovan havuzu (GLOBAL, decal havuzuyla ayni sozlesme)
        const int ShellCount = 32;
        static Transform _shellRoot;
        static Transform[] _shellSlots;     // konum/donus buraya yazilir
        static Transform[] _shellHolders;   // mesh child'i: pivot duzeltmesi burada
        static MeshFilter[] _shellFilters;
        static MeshRenderer[] _shellRenderers;
        static int _shellNext;

        struct ShellFlight
        {
            public int slot;
            public Vector3 pos, vel, angVel;
            public Quaternion rot;
            public float spawnedAt, floorY;
            public bool landed;
        }
        // STATIK: kovan silahin cocugu degil. Silah birakilsa, cantaya girse, hatta despawn olsa
        // bile havadaki kovan ucusunu tamamlar (duman/decal ile ayni sozlesme).
        static readonly List<ShellFlight> _shellFlights = new List<ShellFlight>();
        // Her WeaponFx.Update tick'i cagiriyor; listeyi karede BIR kez ilerlet.
        static int _shellTickFrame = -1;
        static readonly RaycastHit[] _shellRayHits = new RaycastHit[8];

        const float ShellLife = 2.5f;
        const float ShellGravity = 9.81f;

        /// <summary>Setup'in mekanizma yarisi: spec'i bul, surgu parcasini ve kovan gorselini
        /// coz. Hepsi BIR KEZ — atis aninda arama yapilmaz.</summary>
        void ResolveMechanism()
        {
            string clean = WeaponGripBinder.CleanName(gameObject.name);
            if (string.IsNullOrEmpty(clean)) return;

            for (int i = 0; i < MechSpecs.Length; i++)
            {
                if (!clean.Contains(MechSpecs[i].key)) continue;
                _mech = MechSpecs[i];
                _hasMech = true;
                break;
            }
            if (!_hasMech) return;

            ResolveSlide();
            ResolveShell();
        }

        /// <summary>Onekle eslesen TUM surgu parcalarini toplar ve her biri icin taban pozu +
        /// geri yonunu onceden hesaplar.</summary>
        void ResolveSlide()
        {
            if (_mech.slideNames == null || _mech.travel <= 0f) return;

            var found = new List<Transform>();
            var all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == transform || t.parent == null) continue;
                // Cizilen bir sey tasimayan grup dugumunu oynatmanin gorsel karsiligi yok.
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;

                for (int n = 0; n < _mech.slideNames.Length; n++)
                {
                    if (!t.name.StartsWith(_mech.slideNames[n], System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    found.Add(t);
                    break;
                }
            }
            if (found.Count == 0) return;

            _slideParts = found.ToArray();
            _slideBaseLocal = new Vector3[_slideParts.Length];
            _slideBackLocal = new Vector3[_slideParts.Length];

            // METRE -> PARCA-EBEVEYN BIRIMI. InverseTransformVector hem donusu hem olcegi tek
            // adimda cozer; alev rig'indeki elle 1/lossyScale telafisinden daha dogru. Kok ->
            // parca zinciri rijit oldugu icin Setup anindaki dunya yoneliminden bagimsiz gecerli.
            Vector3 worldBack = -(transform.rotation * _barrelLocal);
            for (int i = 0; i < _slideParts.Length; i++)
            {
                _slideBaseLocal[i] = _slideParts[i].localPosition;
                _slideBackLocal[i] = _slideParts[i].parent.InverseTransformVector(worldBack);
            }
        }

        /// <summary>Kovan mesh'ini silahin KENDI hiyerarsisinden alir (pakette ayri kovan
        /// asset'i yok). Bulursa bagis havuzuna da yazar; bulamayan silah (HK416, Smg 2)
        /// oradan odunc alir.</summary>
        void ResolveShell()
        {
            // Firlatma noktasi ve yonu, kovan mesh'i olmasa bile (bagis gelebilir) hesaplanir.
            _ejectOriginLocal = ResolveEjectPortLocal();

            Vector3 side = Vector3.Cross(Vector3.up, _barrelLocal);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right;   // namlu tam yukari (olmamali)
            // Kok-lokal hesaplanir: silah yan yatikken de kovan KENDI atma penceresinden cikar.
            // WeaponRecoil'in dunya-up'li Cross'unu kopyalamak bunu bozardi.
            _ejectDirLocal = (side.normalized + Vector3.up * 0.8f).normalized;

            if (!string.IsNullOrEmpty(_mech.shellHint))
            {
                var mf = FindShellMesh(_mech.shellHint);
                if (mf != null) _shell = BuildShellVisual(mf);
            }

            if (_shell.valid)
            {
                if (!_donatedShell.valid) _donatedShell = _shell;   // ilk bulan bagislar
            }
            else if (_donatedShell.valid)
            {
                _shell = _donatedShell;
                if (_mech.shellFallbackSize > 0f) RescaleShell(ref _shell, _mech.shellFallbackSize);
            }
        }

        /// <summary>Odunc alinan kovani istenen boya getirir (bkz. MechSpec.shellFallbackSize).
        /// centerOffset mesh uzayinda oldugu icin DEGISMEZ — yalnizca olcek dokunur.</summary>
        static void RescaleShell(ref ShellVisual v, float wantedSize)
        {
            Vector3 ws = Vector3.Scale(v.mesh.bounds.size, v.scale);
            float now = Mathf.Max(ws.x, Mathf.Max(ws.y, ws.z));
            if (now < 1e-5f) return;

            float k = wantedSize / now;
            v.scale *= k;
            v.radius = Mathf.Max(0.004f, wantedSize * 0.5f);
        }

        /// <summary>
        /// Kovanin cikacagi nokta (KOK-LOKAL).
        ///
        /// SURGU VARSA oradan: mekanizmanin gorunen agzi odur ve tabancalarda birebir dogru
        /// sonuc veriyor.
        ///
        /// SURGUSUZ SILAHTA eski davranis TANIMSIZDI — "hiyerarsideki ilk Renderer'in merkezi"
        /// aliniyordu ve bu silahtan silaha bambaska yerlere dusuyordu: HK416'da butun silahin
        /// merkezi (yani SARJOR), Rifle 3'te namlu, Smg 2'de kabza. Kovanin nereden ciktigi
        /// belli olmuyordu.
        ///
        /// Yeni nokta KABZAYA capalanir: kovan atma penceresi mekanizmanin yeridir, mekanizma
        /// da kabzanin hemen onunde ve ustundedir. gripLocalPosition her profilde var ve
        /// WeaponPackSetup onu kabza mesh'inden cikariyor — elimizdeki en guvenilir capa.
        /// Katsayilar surgulu silahlardan olculdu: surgu merkezi kabzanin 5-29 cm onunde,
        /// 2-9 cm ustunde duruyor.
        /// </summary>
        Vector3 ResolveEjectPortLocal()
        {
            if (_slideParts != null && _slideParts.Length > 0)
            {
                var r = _slideParts[0].GetComponentInChildren<Renderer>(true);
                if (r != null) return transform.InverseTransformPoint(r.bounds.center);
            }

            Vector3 grip = _profile != null ? _profile.gripLocalPosition : Vector3.zero;
            float span = BarrelSpanLocal();
            return grip
                 + _barrelLocal * Mathf.Clamp(span * 0.15f, 0.05f, 0.18f)
                 + Vector3.up * 0.04f;
        }

        /// <summary>Silahin namlu ekseni boyunca uzunlugu (kok-lokal metre). Renderer.bounds
        /// DUNYA-hizali oldugu icin donmus bir silahta sisiyor; mesh koseleri kok uzayina
        /// tasinarak olculuyor. Setup'ta BIR KEZ.</summary>
        float BarrelSpanLocal()
        {
            var filters = GetComponentsInChildren<MeshFilter>(true);
            float min = float.MaxValue, max = float.MinValue;

            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;

                Bounds mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        mb.center.x + ((c & 1) == 0 ? -mb.extents.x : mb.extents.x),
                        mb.center.y + ((c & 2) == 0 ? -mb.extents.y : mb.extents.y),
                        mb.center.z + ((c & 4) == 0 ? -mb.extents.z : mb.extents.z));
                    float a = Vector3.Dot(
                        transform.InverseTransformPoint(mf.transform.TransformPoint(corner)),
                        _barrelLocal);
                    if (a < min) min = a;
                    if (a > max) max = a;
                }
            }
            return max > min ? max - min : 0.5f;
        }

        MeshFilter FindShellMesh(string hint)
        {
            var filters = GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.name.IndexOf(hint, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                return mf;
            }
            return null;
        }

        ShellVisual BuildShellVisual(MeshFilter mf)
        {
            var mesh = mf.sharedMesh;
            var mr = mf.GetComponent<MeshRenderer>();
            Vector3 ls = mf.transform.lossyScale;
            Bounds b = mesh.bounds;

            // Havuz objesi dunya kokunde duracak: kaynak parcanin lossyScale'ini birebir
            // kopyalamak dunya boyunu da birebir kopyalar (bounds normalizasyonundan basit ve
            // kesin). Emniyet kemeri: sacma bir import olcegi gelirse makul boya cek.
            Vector3 worldSize = Vector3.Scale(b.size, ls);
            float biggest = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z));
            if (biggest < 0.005f || biggest > 0.12f)
            {
                float k = biggest > 1e-5f ? 0.025f / biggest : 1f;
                ls *= k;
                worldSize *= k;
                biggest = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z));
            }

            return new ShellVisual
            {
                mesh = mesh,
                mat = mr != null ? mr.sharedMaterial : null,
                scale = ls,
                // PIVOTLAR MODEL ORIJININDE, parcanin merkezinde DEGIL. Duzeltilmezse kovan
                // firlatma noktasindan metrelerce yanda cizilir ve uzak bir pivot etrafinda
                // savrulur.
                centerOffset = b.center,
                radius = Mathf.Max(0.004f, biggest * 0.5f),
                valid = true,
            };
        }

        // ------------------------------------------------- surgu

        /// <summary>
        /// Atisla surguyu geri savurur.
        ///
        /// RETRIGGER (otomatik silah, 900 rpm): cevrim bitmeden yeni atis gelirse zamanlayici
        /// sifirlanir ama baslangic offseti MEVCUT konum olur — surgu bulundugu yerden tekrar
        /// geriye gider. Sifirdan baslatmak her ateste one "pop" ettirirdi; beklemek ise
        /// yuksek kadansta surguyu tamamen olduruyordu. Boylece yuksek kadansta surgu geride
        /// asili titrer — gercek silahta da boyle gorunur.
        /// </summary>
        void KickSlide()
        {
            if (_slideParts == null) return;
            _slideStartOffset = _slideCurOffset;
            _slideFiredAt = Time.time;
        }

        void TickSlide()
        {
            if (_slideFiredAt < 0f) return;
            if (_slideParts == null) { _slideFiredAt = -1f; return; }

            float t = Time.time - _slideFiredAt;
            float back = Mathf.Max(0.005f, _mech.backT);
            float fwd = Mathf.Max(0.005f, _mech.fwdT);

            float offset;
            bool done = false;
            if (t <= back)
            {
                offset = Mathf.Lerp(_slideStartOffset, _mech.travel, t / back);
            }
            else if (t <= back + fwd)
            {
                // Ileri donus yavas ve yumusak: SmoothStep, yaya oturan surgunun okunusu.
                offset = Mathf.Lerp(_mech.travel, 0f, Mathf.SmoothStep(0f, 1f, (t - back) / fwd));
            }
            else
            {
                offset = 0f;   // cevrim bitti: taban pozu TAM yaz, sonra bir daha dokunma
                done = true;
            }

            for (int i = 0; i < _slideParts.Length; i++)
            {
                var p = _slideParts[i];
                if (p == null) continue;
                p.localPosition = done ? _slideBaseLocal[i]
                                       : _slideBaseLocal[i] + _slideBackLocal[i] * offset;
            }

            _slideCurOffset = offset;
            if (done) _slideFiredAt = -1f;
        }

        // ------------------------------------------------- bos kovan

        /// <summary>Global kovan havuzunu (tembel) kurar. Decal havuzuyla ayni fake-null
        /// sozlesmesi: sahne gecisi objeleri oldurduyse yeniden kurulur.
        ///
        /// COLLIDER YOK — ve bu SUS DEGIL: WeaponHitscanServer Physics.AllLayers tariyor ve
        /// yalnizca silah kokunun cocuklarini atliyor. Yerde duran ya da ucan bir kovanin
        /// collider'i MERMIYI DURDURURDU.</summary>
        static void EnsureShellPool()
        {
            if (_shellRoot != null && _shellSlots != null) return;

            _shellRoot = new GameObject("Shells (paylasimli)").transform;
            _shellSlots = new Transform[ShellCount];
            _shellHolders = new Transform[ShellCount];
            _shellFilters = new MeshFilter[ShellCount];
            _shellRenderers = new MeshRenderer[ShellCount];
            _shellNext = 0;

            for (int i = 0; i < ShellCount; i++)
            {
                // Iki katman: DIS obje konum/donusu tasir, IC obje pivot duzeltmesini. Tek
                // katman olsaydi kovan kendi geometrik merkezi yerine model orijini etrafinda
                // takla atardi (bkz. ShellVisual.centerOffset).
                var slot = new GameObject("Shell").transform;
                slot.SetParent(_shellRoot, false);

                var holder = new GameObject("Mesh").transform;
                holder.SetParent(slot, false);

                var mf = holder.gameObject.AddComponent<MeshFilter>();
                var mr = holder.gameObject.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;

                slot.gameObject.SetActive(false);
                _shellSlots[i] = slot;
                _shellHolders[i] = holder;
                _shellFilters[i] = mf;
                _shellRenderers[i] = mr;
            }
        }

        /// <summary>Atis basina tek kovan: firlatma penceresinden saga-yukari savrulur, takla
        /// atarak duser, zemine oturur ve kisa sure sonra havuza doner.</summary>
        void EjectShell()
        {
            if (!_hasMech || !_shell.valid) return;

            EnsureShellPool();
            int slot = _shellNext;
            _shellNext = (_shellNext + 1) % ShellCount;
            var st = _shellSlots[slot];
            if (st == null) { _shellRoot = null; return; }   // sahne gecisi: sonraki cagri kurar

            // Ayni kovani art arda ayni silah atiyorsa render state'i bosuna kirletme.
            if (_shellFilters[slot].sharedMesh != _shell.mesh)
                _shellFilters[slot].sharedMesh = _shell.mesh;
            if (_shellRenderers[slot].sharedMaterial != _shell.mat)
                _shellRenderers[slot].sharedMaterial = _shell.mat;

            st.localScale = _shell.scale;
            _shellHolders[slot].localPosition = -_shell.centerOffset;

            Vector3 dir = (transform.rotation * _ejectDirLocal).normalized;
            // Silahin govdesinden disari tasi: firlatma noktasi surgunun ICINDE, ilk karede
            // metalin icinden dogmus gorunmesin.
            Vector3 pos = transform.TransformPoint(_ejectOriginLocal) + dir * 0.03f;

            var flight = new ShellFlight
            {
                slot = slot,
                pos = pos,
                vel = dir * Random.Range(2.0f, 3.0f) + Random.insideUnitSphere * 0.35f,
                angVel = Random.insideUnitSphere.normalized * Random.Range(400f, 900f),
                rot = Random.rotation,
                spawnedAt = Time.time,
                floorY = FindFloor(pos),
                landed = false,
            };

            st.SetPositionAndRotation(flight.pos, flight.rot);
            st.gameObject.SetActive(true);
            if (_shellFlights.Count >= ShellCount) _shellFlights.RemoveAt(0);
            _shellFlights.Add(flight);
        }

        /// <summary>Kovanin oturacagi zemin yuksekligi. KENDI SILAHINI ATLAMAK SART: firlatma
        /// noktasi silahin kokteki statik collider kutularinin ICINDE, ilk isin kendi kutusuna
        /// carpip zemini silah hizasi saniyor ve kovan havada duruyordu.</summary>
        float FindFloor(Vector3 from)
        {
            int n = Physics.RaycastNonAlloc(from + Vector3.up * 0.05f, Vector3.down,
                _shellRayHits, 4f, Physics.AllLayers, QueryTriggerInteraction.Ignore);

            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var col = _shellRayHits[i].collider;
                if (col == null) continue;
                if (col.transform.IsChildOf(transform)) continue;   // silahin kendi kutulari
                float y = _shellRayHits[i].point.y;
                if (y > best) best = y;
            }
            return best > float.NegativeInfinity ? best : from.y - 1.2f;
        }

        /// <summary>Kovan ucuslari. STATIK liste + kare kilidi: her silahin WeaponFx.Update'i
        /// cagiriyor, listenin karede BIR kez ilerlemesi gerekiyor.</summary>
        static void TickShells()
        {
            if (_shellFlights.Count == 0) return;
            if (_shellTickFrame == Time.frameCount) return;
            _shellTickFrame = Time.frameCount;

            float dt = Time.deltaTime;
            float now = Time.time;

            for (int i = _shellFlights.Count - 1; i >= 0; i--)
            {
                var f = _shellFlights[i];
                var st = _shellSlots != null && f.slot < _shellSlots.Length ? _shellSlots[f.slot] : null;
                if (st == null) { _shellFlights.RemoveAt(i); continue; }

                if (now - f.spawnedAt > ShellLife)
                {
                    st.gameObject.SetActive(false);
                    _shellFlights.RemoveAt(i);
                    continue;
                }

                if (!f.landed)
                {
                    f.vel.y -= ShellGravity * dt;
                    f.pos += f.vel * dt;
                    f.rot = Quaternion.Euler(f.angVel * dt) * f.rot;

                    // Zemine degdi. Sert durdurmak yerine bir-iki SEKME: pirinc kovanin
                    // zemindeki sesi/gorunusu bu, ve duran kovan da dogru acida oturur.
                    if (f.pos.y <= f.floorY)
                    {
                        f.pos.y = f.floorY;
                        if (-f.vel.y > 0.6f)
                        {
                            f.vel.y = -f.vel.y * 0.35f;
                            f.vel.x *= 0.6f;
                            f.vel.z *= 0.6f;
                            f.angVel *= 0.5f;
                        }
                        else
                        {
                            f.vel = Vector3.zero;
                            f.angVel = Vector3.zero;
                            f.landed = true;
                        }
                    }

                    st.SetPositionAndRotation(f.pos, f.rot);
                }

                _shellFlights[i] = f;
            }
        }

        /// <summary>Editor testi: kulaklik ve sunucu olmadan mekanizmayi gozle dogrulamak icin.
        /// Play modunda Inspector'da bilesenin sag-tik menusunden calistirilir.</summary>
        [ContextMenu("Test Volley")]
        void TestVolley()
        {
            Vector3 dir = (transform.rotation * _barrelLocal).normalized;
            Vector3 origin = transform.position + dir * 0.2f;
            ShowVolley(origin, new[] { origin + dir * 5f }, new[] { Vector3.up });
        }
    }
}
