using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// DURBUN (Pavlov usulu): silahtaki gizli bir kamera dar aciyla gordugunu, merceğin uzerine
    /// yerlestirdigimiz YUVARLAK EKRANA basar — buyutme yalnizca o dairenin icinde, kenarindan
    /// bakinca dunya normal.
    ///
    /// NEDEN KENDI EKRANIMIZ: ilk deneme goruntuyu CAMIN KENDI MESH'ine basiyordu; her modelin
    /// cam UV haritasi farkli cikti (Rifle 1'de 90 derece donuk, Sniper 1'de bas asagi) ve
    /// kamera ayariyla duzelmiyor. Kendi urettigimiz diskin UV'sini biz tanimliyoruz — goruntu
    /// her silahta ayni ve DIK. Nisan artisi da ayri bir katman diski (cizim numarasi yok,
    /// gercek geometri — URP'de kaybolmaz).
    ///
    /// Ekip karari: sadece Rifle 1 ve Sniper 1. PERFORMANS (Quest): 256px RT, golgesiz kamera,
    /// yalnizca YEREL oyuncu tutarken + goz merceğe yakinken calisir; digerlerinde hic.
    /// Kurulum SIFIR sahne/prefab dokunusu (WeaponGripBinder deseni).
    /// </summary>
    public static class WeaponScopeBinder
    {
        class Spec
        {
            public string nameContains;
            public string[] glassNames;      // cam MESH cocuklari (ISIMLE BASLAR eslesir)
            public string glassMaterialHint; // tek-mesh silahta cam MATERYAL adi ipucu
            public string anchorName;        // kamera cipasi (bos = mercekten)
            public float fov;
        }

        // Mercek MESH'inin adiyla bulunur (ISIMLE BASLAR). Materyalden tahmin yolu yedekte
        // duruyor ama guvenilmez: modeller okunamaz (isReadable 0) oldugu icin alt-mesh
        // sinirlari sasabiliyor — Rifle 1'de disk devasa cikmisti. Isimle bulunca hem yer hem
        // boy birebir dogru.
        // glassNames ONCELIK SIRALIDIR: listedeki ilk eslesen isim kullanilir (ayni isimden
        // birden fazlaysa namlu yonunde EN GERIDEKI = goz merceği). Isimler modelden birebir
        // okundu — Sniper 1: Scope_Glass_2E7, Rifle 1: Scope_Rear_Lens.
        static readonly Spec[] Specs =
        {
            new Spec { nameContains = "Sniper 1", fov = 7f, anchorName = "ScopeE7",
                       glassNames = new[] { "Scope_Glass_2", "Scope_Glass_1", "Scope_Rear_Lens" },
                       glassMaterialHint = "Glass" },
            new Spec { nameContains = "Rifle 1", fov = 8.5f, // biraz daha yakin (10 -> 8.5)
                       glassNames = new[] { "Scope_Rear_Lens", "Scope_Lens", "Scope_Glass" },
                       glassMaterialHint = "Glass" },
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            GrabbableObject.AnySpawned -= OnSpawned;
            GrabbableObject.AnySpawned += OnSpawned;
        }

        static void OnSpawned(GrabbableObject g)
        {
            string n = WeaponGripBinder.CleanName(g.name);
            foreach (var s in Specs)
            {
                if (!n.Contains(s.nameContains)) continue;
                if (g.GetComponent<ScopeView>() != null) return;
                var v = g.gameObject.AddComponent<ScopeView>();
                v.Init(g, s.glassNames, s.glassMaterialHint, s.anchorName, s.fov);
                return;
            }
        }
    }

    /// <summary>Nisan isareti bicimi — hepsi MERCEK ICINDE cizilir (red-dot mantigi: sadece
    /// nisan alan gorur, dunyada isik yoktur). Isaret, merminin gercekten gidecegi noktanin
    /// goruntudeki yerine kayar.</summary>
    public enum ScopeReticleStyle { Nokta, Arti, Ikisi }

    /// <summary>Tek silahin durbunu: mercek konumunu bulur, uzerine RT'li disk + arti katmani
    /// yerlestirir. Agirlik (kamera+RT) yalnizca aktifken yasar.</summary>
    // SIRA 80 = TEPMEDEN SONRA (WeaponRecoil 60). Sirasiz birakilinca durbun kamerasi silahin
    // TEPME ONCESI durusunu okuyordu; mermi ise tepme sonrasi namludan cikiyordu. Sonuc: dürbünde
    // gosterdigin yer ile merminin gittigi yer tepme kadar (sniper'da 12 derece!) ayrisiyordu.
    // Artik kamera ve disk son duruşu okur: dürbünde ne goruyorsan mermi oraya gider.
    [DefaultExecutionOrder(80)]
    public class ScopeView : MonoBehaviour
    {
        [Header("Canli ayar (Play'de oynanabilir)")]
        [Tooltip("Dar aci = cok buyutme. 7 ~ 9x, 10 ~ 6x his.")]
        public float scopeFov = 10f;
        [Tooltip("Gozun merceğe bu kadar yaklasinca durbun ACILIR (metre).")]
        public float eyeDistance = 0.45f;
        public int rtSize = 256;
        [Tooltip("Kamera mercekten ne kadar ONDE dursun (namlu yonunde, metre).")]
        public float camForwardOffset = 0.06f;
        [Tooltip("Ekran diskinin olcek carpani (1 = olculen mercek boyu).")]
        public float lensScale = 1f;
        [Tooltip("0'dan buyukse OLCUMU EZER: mercek yaricapini metre cinsinden elle ver. " +
                 "Modeller okunamaz oldugu icin (isReadable 0) otomatik olcum sinir kutusu " +
                 "tahminidir — sasarsa buradan sabitlenir.")]
        public float lensRadiusOverride = 0f;
        [Tooltip("Disk mercekten ne kadar GERIDE (atici tarafinda) dursun (metre). Camla ayni " +
                 "duzlemde titremesin diye 2 mm yeter — mercek nerede ise disk orada.")]
        public float lensDepthOffset = 0.002f;
        [Tooltip("Diski namlu ekseninde kaydir (metre; + ileri, - geri). Disk yanlis yerdeyse.")]
        public float lensAxisNudge = 0f;

        [Header("Nisan isareti (mercek icinde — sadece nisan alan gorur)")]
        [Tooltip("Nokta = red-dot tarzi yesil benek (ekip istegi). Arti = ince +. Ikisi = her ikisi.")]
        public ScopeReticleStyle reticleStyle = ScopeReticleStyle.Nokta;
        [Tooltip("Isaret rengi. Gercek yesil lazer/red-dot tonu (532 nm) — silah oyunlarinin standardi.")]
        public Color reticleColor = new Color(0.15f, 1f, 0.25f, 1f);

        [Tooltip("NOKTA yaricapi (doku pikseli). Kucuk tut — 4-8 arasi gercekci.")]
        public int reticleDotPx = 6;
        [Tooltip("Hale (glow) yaricap carpani: 1 = hale yok, 2.5 = yumusak parlama.")]
        public float reticleGlow = 2.5f;

        [Tooltip("ARTI kol uzunlugu (doku pikseli).")]
        public int reticleArmPx = 18;
        [Tooltip("Arti cizgi kalinligi (doku pikseli).")]
        public int reticleLinePx = 2;
        [Tooltip("Arti kollarinin merkezde birakacagi bosluk; 0 = tam kesisir.")]
        public int reticleCenterGapPx = 0;

        GrabbableObject _grab;
        NetworkWeapon _weapon;      // nisan isininin kaynagi (Fire ile ayni)
        Transform _camAnchor;
        Vector3 _barrelLocal = Vector3.forward;
        Vector3 _lensCenterLocal;   // silah-lokal mercek merkezi
        float _lensRadius;          // dunya olceginde yaricap
        string _lensName = "?";     // hangi mesh secildi (log icin)

        Camera _cam;
        RenderTexture _rt;
        Transform _lens;            // RT'yi gosteren disk
        Transform _reticle;         // isaret katmani (mercek uzerinde) — isabet noktasina KAYAR
        Material _reticleMat;       // rengi canli degistirmek icin
        ScopeReticleStyle _builtStyle = (ScopeReticleStyle)(-1); // dokusu hangi bicimle uretildi
        Material _lensMat;
        bool _active;

        public void Init(GrabbableObject g, string[] glassNames, string matHint, string anchorName, float fov)
        {
            _grab = g;
            _weapon = g.GetComponent<NetworkWeapon>();
            scopeFov = fov;

            var grip = g.GetComponent<WeaponGrip>();
            if (grip != null && grip.Profile != null && grip.Profile.barrelLocalDirection.sqrMagnitude > 0.01f)
                _barrelLocal = grip.Profile.barrelLocalDirection.normalized;

            if (!FindLens(g, glassNames, matHint))
            {
                // Son care: adinda Lens/Glass gecen herhangi bir parca (yeni silahlar icin).
                var names = new System.Collections.Generic.List<string>();
                foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                {
                    string rn = r.name.ToLowerInvariant();
                    if (rn.Contains("lens") || rn.Contains("glass")) names.Add(r.name);
                }
                if (names.Count == 0 || !FindLens(g, names.ToArray(), null))
                {
                    Debug.LogWarning($"[Durbun] {g.name}: mercek bulunamadi — durbun devre disi. " +
                                     "Mercek mesh'inin adini WeaponScopeBinder.Specs'e ekle.");
                    enabled = false;
                    return;
                }
            }

            if (!string.IsNullOrEmpty(anchorName))
            {
                foreach (var t in g.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith(anchorName)) { _camAnchor = t; break; }
            }

            Debug.Log($"[Durbun] {g.name}: mercek = '{_lensName}' — yaricap {_lensRadius:F4} m, " +
                      $"silah-lokal merkez {_lensCenterLocal}, zoom {scopeFov} derece. " +
                      "Disk yanlis boy/yerdeyse: Lens Radius Override / Lens Axis Nudge.");
        }

        /// <summary>Mercegin YERINI ve BOYUNU bul — goruntuyu basmak icin degil (UV'lere guven
        /// yok), kendi diskimizi nereye koyacagimizi bilmek icin.
        /// Ayri cam mesh'leri: namlu yonunde EN GERIDEKI cam = goz merceği.
        /// Tek-mesh silah (Rifle 1): cam materyalinin ALT-MESH sinirlarindan.</summary>
        bool FindLens(GrabbableObject g, string[] glassNames, string matHint)
        {
            // ONEMLI: her olcum LOKAL sinir kutusundan yapilir, DUNYA kutusundan degil.
            // Dunya kutusu eksen-hizalidir: silah rafta egik durunca egik bir diskin cevresine
            // cizilen kutu diskten cok daha buyuk cikiyordu (Rifle 1'de disk devasa oluyordu).
            if (glassNames != null)
            {
                Vector3 barrelWorld = g.transform.TransformDirection(_barrelLocal);
                var all = g.GetComponentsInChildren<Renderer>(true);

                // ONCELIK SIRASI: listedeki ILK eslesen isim kazanir (tahmin degil, secim).
                foreach (var gn in glassNames)
                {
                    Renderer rear = null;
                    float rearDot = float.MaxValue;
                    foreach (var r in all)
                    {
                        if (!r.name.StartsWith(gn)) continue;
                        // Ayni isimden birden fazlaysa namlu yonunde EN GERIDEKI = goz merceği
                        float d = Vector3.Dot(r.bounds.center, barrelWorld);
                        if (d < rearDot) { rearDot = d; rear = r; }
                    }
                    if (rear == null) continue;

                    var lb = rear.localBounds;
                    _lensCenterLocal = g.transform.InverseTransformPoint(rear.transform.TransformPoint(lb.center));
                    _lensRadius = LocalExtentToRadius(lb.extents, rear.transform.lossyScale);
                    _lensName = rear.name;
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(matHint))
            {
                foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null || !mats[i].name.Contains(matHint)) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null || i >= mf.sharedMesh.subMeshCount) continue;

                        // Alt-mesh sinirlari (mesh-lokal). Bos/bozuk gelirse renderer'in lokal
                        // kutusuna duseriz — okunamayan modellerde bu bilgi guvenilmez olabiliyor.
                        var sm = mf.sharedMesh.GetSubMesh(i);
                        Bounds b = sm.bounds.size.sqrMagnitude > 1e-8f ? sm.bounds : r.localBounds;
                        _lensCenterLocal = g.transform.InverseTransformPoint(r.transform.TransformPoint(b.center));
                        _lensRadius = LocalExtentToRadius(b.extents, r.transform.lossyScale);
                        _lensName = r.name + " (materyalden)";
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Lokal yari-boyutlari dunya yaricapina cevirir. Disk gibi ince bir parcada
        /// yari-boyutlar [r, r, kalinlik] gelir; EN BUYUK ikisinin ortalamasi gercek yaricapa en
        /// yakin tahmindir (tek basina max, kutu biraz sismisse abartir).
        /// Sonuc makul mercek araligina kirpilir — okunamayan modellerde olcum sasabiliyor.</summary>
        static float LocalExtentToRadius(Vector3 extents, Vector3 lossyScale)
        {
            float s = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
            float a = Mathf.Abs(extents.x), b = Mathf.Abs(extents.y), c = Mathf.Abs(extents.z);
            // en buyuk iki bileseni bul
            float max1 = Mathf.Max(a, Mathf.Max(b, c));
            float min1 = Mathf.Min(a, Mathf.Min(b, c));
            float mid = a + b + c - max1 - min1;
            return Mathf.Clamp((max1 + mid) * 0.5f * s, 0.005f, 0.08f); // 5 mm .. 8 cm
        }

        void LateUpdate()
        {
            if (_grab == null || !enabled) return;

            bool wantActive = ShouldRender();
            if (wantActive != _active)
            {
                _active = wantActive;
                if (_active) Activate(); else Deactivate();
            }
            if (!_active) return;

            Vector3 barrelWorld = _grab.transform.TransformDirection(_barrelLocal);
            // "Yukari" = dunya yukarisi (silah duz tutulunca ufuk duz); namlu dike yakinsa coksun diye yedek.
            Vector3 up = Mathf.Abs(Vector3.Dot(barrelWorld, Vector3.up)) > 0.95f ? _grab.transform.up : Vector3.up;

            Vector3 lensWorld = _grab.transform.TransformPoint(_lensCenterLocal);

            // KAMERA NISAN EKSENINE OTURUR — merceğin icine degil. Sebep: mercek namlunun
            // USTUNDE; kamerayi oraya koyunca goruntu mermiyle PARALEL ama KAYIK oluyordu ve
            // isabet noktasinin goruntudeki yeri MESAFEYE gore degisiyordu (elin en ufak
            // titremesinde nokta kayiyordu). Kamerayi merminin ekseni uzerine alinca goruntunun
            // MERKEZI = merminin gidecegi yer; isaret sabit durur, mesafe onemsiz.
            if (_cam != null)
            {
                Vector3 camPos, camDir;
                if (_weapon != null)
                {
                    _weapon.GetAimRay(out camPos, out camDir);   // Fire() ile ayni is
                    camPos += camDir * camForwardOffset;
                }
                else
                {
                    camPos = (_camAnchor != null ? _camAnchor.position : lensWorld) + barrelWorld * camForwardOffset;
                    camDir = barrelWorld;
                }
                _cam.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(camDir, up));
            }

            // Ekran diski: merceğin GOZ tarafinda, atici yuzune donuk. Kamerayla AYNI 'up'
            // kullanildigi icin goruntu her silahta dik ve aynasiz.
            if (_lens != null)
            {
                float radius = lensRadiusOverride > 0f ? lensRadiusOverride : _lensRadius;
                _lens.SetPositionAndRotation(
                    lensWorld + barrelWorld * (lensAxisNudge - lensDepthOffset),
                    Quaternion.LookRotation(-barrelWorld, up));
                _lens.localScale = Vector3.one * (radius * lensScale);
            }

            UpdateReticle();
        }

        /// <summary>Nisan halkasini merminin GERCEKTEN gidecegi noktaya kaydirir.
        ///
        /// Neden merkezde degil: durbun namluyla ayni yerde durmuyor — mermi namlu ucundan
        /// cikar, kamera durbunun icinden bakar (namlunun birkac cm ustunde/gerisinde). Iki isin
        /// paralel ama KAYIK; ustune tepme de binince "gosterdigim yere gitmiyor" oluyordu.
        /// Cozum kamerayi zorlamak degil: silahin gercek nisan isini (NetworkWeapon.GetAimRay —
        /// Fire ile ayni kaynak) alip carptigi noktayi bulmak ve halkayi o noktanin kamera
        /// goruntusundeki yerine koymak. Boylece halka NEREDEYSE mermi ORAYA gider — mesafe,
        /// tepme, montaj farki ne olursa olsun.</summary>
        /// <summary>Isareti gunceller. Kamera nisan ekseninde oldugu icin isaret SABIT
        /// MERKEZDE durur — merkez zaten merminin gidecegi yer. (Onceden isabet noktasi hesaplanip
        /// isaret oraya kaydiriliyordu; kamera mercekte oldugu icin o nokta mesafeyle oynuyor ve
        /// isaret surekli titriyordu.)</summary>
        void UpdateReticle()
        {
            if (_reticle == null) return;
            if (_builtStyle != reticleStyle) ApplyReticleTexture();   // bicim canli degistiyse
            if (_reticleMat != null) UITheme.SetMaterialColor(_reticleMat, reticleColor);
            _reticle.localPosition = new Vector3(0f, 0f, -0.003f);
        }

        /// <summary>Yalnizca: YEREL oyuncu tutuyor + goz merceğe yakin. XR yoksa (PC testi)
        /// yakinlik sarti aranmaz — tutmak yeter.</summary>
        bool ShouldRender()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !_grab.IsHeld || _grab.HolderClientId != nm.LocalClientId) return false;

            var head = Camera.main;
            if (head == null) return false;
            if (!UnityEngine.XR.XRSettings.isDeviceActive) return true;

            Vector3 eyeRef = _grab.transform.TransformPoint(_lensCenterLocal);
            return (head.transform.position - eyeRef).sqrMagnitude < eyeDistance * eyeDistance;
        }

        void Activate()
        {
            if (_rt == null)
                _rt = new RenderTexture(rtSize, rtSize, 16) { name = "ScopeRT_" + _grab.name };

            if (_cam == null)
            {
                var go = new GameObject("~ScopeCam");
                go.transform.SetParent(transform, false);
                _cam = go.AddComponent<Camera>();
                _cam.targetTexture = _rt;
                _cam.nearClipPlane = 0.3f;
                _cam.farClipPlane = 200f;
                var urp = _cam.GetUniversalAdditionalCameraData();
                if (urp != null)
                {
                    urp.renderShadows = false;
                    urp.renderPostProcessing = false;
                }
            }
            _cam.fieldOfView = scopeFov;
            _cam.enabled = true;

            if (_lens == null) BuildLens();
            _lens.gameObject.SetActive(true);
        }

        void Deactivate()
        {
            if (_cam != null) _cam.enabled = false;
            if (_lens != null) _lens.gameObject.SetActive(false);
        }

        /// <summary>RT diski + ustune arti katmani. Birim yaricapli uretilir, LateUpdate olcekler.</summary>
        void BuildLens()
        {
            _lens = new GameObject("~ScopeLens").transform;
            _lens.SetParent(transform, false);

            var disc = new GameObject("Screen");
            disc.transform.SetParent(_lens, false);
            disc.AddComponent<MeshFilter>().sharedMesh = DiscMesh(1f, 40);
            var mr = disc.AddComponent<MeshRenderer>();
            _lensMat = MakeUnlitMaterial(3000);
            if (_lensMat.HasProperty("_BaseMap")) _lensMat.SetTexture("_BaseMap", _rt);
            if (_lensMat.HasProperty("_MainTex")) _lensMat.SetTexture("_MainTex", _rt);
            UITheme.SetMaterialColor(_lensMat, Color.white);
            mr.sharedMaterial = _lensMat;

            // Arti katmani: ayni disk, hafif onde, saydam arti dokusuyla.
            var ret = new GameObject("Reticle");
            ret.transform.SetParent(_lens, false);
            ret.transform.localPosition = new Vector3(0f, 0f, -0.003f);
            _reticle = ret.transform;
            ret.AddComponent<MeshFilter>().sharedMesh = DiscMesh(1f, 40);
            var rmr = ret.AddComponent<MeshRenderer>();
            _reticleMat = MakeUnlitMaterial(3001);
            // Doku SEKLI beyaz cizer, rengi MATERYALDEN gelir -> renk Play'de canli degisir.
            rmr.sharedMaterial = _reticleMat;
            ApplyReticleTexture();

            _lens.gameObject.SetActive(false);
        }

        /// <summary>+Z'ye bakan, UV'li birim disk. UV'yi BIZ tanimladigimiz icin RT goruntusu
        /// her silahta ayni yonde durur — cam mesh'lerinin karisik UV'lerine bagimlilik yok.</summary>
        static Mesh DiscMesh(float r, int seg)
        {
            var m = new Mesh();
            var v = new Vector3[seg + 1];
            var uv = new Vector2[seg + 1];
            var t = new int[seg * 3];
            v[0] = Vector3.zero; uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                // Geometri X'i ters, UV X'i DUZ: disk aticiya donuk (+Z sana bakar) durdugu icin
                // tek eksende bir kez cevirmek gerekiyor — ikisini birden cevirmek aynayi geri
                // getirirdi. Boylece hedefin SAGindaki sey mercekte de SAGda gorunur.
                v[i + 1] = new Vector3(-Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                uv[i + 1] = new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f);
                t[i * 3] = 0;
                t[i * 3 + 1] = i + 1;
                t[i * 3 + 2] = (i + 1) % seg + 1;
            }
            m.vertices = v; m.uv = uv; m.triangles = t;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static Material MakeUnlitMaterial(int queue)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Texture")
                  ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = queue;
            return m;
        }

        /// <summary>Arti dokusu: saydam zemin + merkez bosluklu ince cizgiler + yumusak nokta.
        /// Merkez = namlu ekseni = merminin gidecegi yer (sus degil, gercek nisan).</summary>
        /// <summary>Isaret dokusunu secili bicime gore uretip materyale takar. Sekil BEYAZ
        /// cizilir (alfa ile), renk materyalden gelir — boylece rengi Play'de canli degistirebilirsin.</summary>
        void ApplyReticleTexture()
        {
            if (_reticleMat == null) return;
            var tex = MakeReticleOverlay(rtSize);
            if (_reticleMat.HasProperty("_BaseMap")) _reticleMat.SetTexture("_BaseMap", tex);
            if (_reticleMat.HasProperty("_MainTex")) _reticleMat.SetTexture("_MainTex", tex);
            _builtStyle = reticleStyle;
        }

        Texture2D MakeReticleOverlay(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ScopeReticle" };
            var clear = new Color(0, 0, 0, 0);
            float cx = (size - 1) * 0.5f, cy = (size - 1) * 0.5f;
            int lt = Mathf.Max(1, reticleLinePx);
            int arm = Mathf.Max(2, reticleArmPx);
            int gap = Mathf.Max(0, reticleCenterGapPx);
            float core = Mathf.Max(1f, reticleDotPx);
            float halo = core * Mathf.Max(1f, reticleGlow);

            bool wantDot = reticleStyle != ScopeReticleStyle.Arti;
            bool wantCross = reticleStyle != ScopeReticleStyle.Nokta;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float a = 0f;

                    if (wantCross)
                    {
                        float dx = Mathf.Abs(x - cx), dy = Mathf.Abs(y - cy);
                        bool onV = dx < lt && dy <= arm && dy >= gap;
                        bool onH = dy < lt && dx <= arm && dx >= gap;
                        if (onV || onH) a = 1f;
                    }

                    if (wantDot)
                    {
                        // Ortasi dolu, kenari sonen benek — red-dot nisangahin parlama hissi.
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                        float g = d <= core ? 1f : Mathf.Clamp01(1f - (d - core) / Mathf.Max(0.001f, halo - core));
                        a = Mathf.Max(a, g * g);
                    }

                    t.SetPixel(x, y, a > 0.002f ? new Color(1f, 1f, 1f, a) : clear);
                }
            t.Apply();
            return t;
        }

        void OnDestroy()
        {
            if (_cam != null) Destroy(_cam.gameObject);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_lens != null) Destroy(_lens.gameObject);
        }
    }
}
