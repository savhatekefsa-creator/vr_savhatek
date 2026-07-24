using Unity.Netcode;
using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// LAZER APARATI — ATES EDEBILEN HER SILAHA takilir (ekip/yonetici karari). El bombalarinda
    /// NetworkWeapon olmadigi icin kendiliginden disarida kalirlar.
    ///
    /// Gercek hayatta lazer, ray'a monte edilen bir APARATTIR (AN/PEQ-15 gibi) — silahin
    /// dogustan ozelligi degil, yani "hepsinde olsun" istegi gercekci. Ama modellerimizin
    /// yalnizca ikisinde lazer donanimi CIZILI (Pistol 4 = MK23 SOCOM, Rifle 3 = G36C);
    /// digerlerinde isin namlu ucundan cikar, gorunur bir modul yoktur. Modul mesh'i eklemek
    /// ayri bir is (secenek C) — bu adim once CALISSIN diye yapildi.
    ///
    /// Durbundeki yesil noktadan FARKLI: bu gercek bir isik. Dunyada benek dusurur ve
    /// HERKES GORUR (ekip karari) — nisan avantaji karsiliginda yerini ele verirsin,
    /// Rainbow Six / CoD dengesi. Bu yuzden ag kodu gerekmiyor: her istemci silahin
    /// IsHeld durumunu ve pozunu zaten agdan biliyor, lazeri kendi tarafinda cizer.
    ///
    /// Acma/kapama tusu yok: silah eldeyken acik, birakinca kapali. Tuslar dolu ve
    /// thumbstick tiki durbun/secici tarafindan kullaniliyor.
    ///
    /// Kurulum SIFIR sahne/prefab dokunusu (WeaponGripBinder deseni) — yeni lazerli silah
    /// eklemek = Specs'e bir satir.
    /// </summary>
    public static class WeaponLaserBinder
    {
        // (silah adi, aday parca adlari) — ONCELIK SIRALI, ISIMLE BASLAR eslesir (model son
        // ekleri HG25 vb. onemsiz). Ilk BULUNAN parca isinin cikis noktasi olur.
        // Pistol 4'te 'Lazer_main' modul GOVDESI: pivotu silahin kokunde, isin kabzadan
        // cikiyordu — gercek cikis camI 'light_glass'.
        static readonly (string weapon, string[] parts)[] Specs =
        {
            ("Pistol 4", new[] { "light_glass", "light_cover", "Lazer_main" }),
            ("Rifle 3",  new[] { "laser_barrel" }),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            GrabbableObject.AnySpawned -= OnSpawned;
            GrabbableObject.AnySpawned += OnSpawned;
        }

        static void OnSpawned(GrabbableObject g)
        {
            // Ates edebilen HER silah lazer alir. NetworkWeapon sarti sayesinde el bombalari,
            // taslar ve esyalar kendiliginden disarida kalir — isim listesi tutmaya gerek yok.
            if (g.GetComponent<NetworkWeapon>() == null) return;
            if (g.GetComponent<LaserSight>() != null) return;

            // Donanimi olan silahlarda isin O PARCADAN cikar; digerlerinde namlu ucundan.
            string n = WeaponGripBinder.CleanName(g.name);
            string[] parts = null;
            foreach (var (weapon, p) in Specs)
                if (n.Contains(weapon)) { parts = p; break; }

            g.gameObject.AddComponent<LaserSight>().Init(g, parts);
        }
    }

    /// <summary>Tek silahin lazeri: namlu ekseninde ince kirmizi cizgi + carptigi yerde benek.</summary>
    // SIRA 80 = TEPMEDEN SONRA (WeaponRecoil 60) — durbunle ayni sebep: silahin SON durusunu
    // okumazsak lazer bir kare geriden gelir ve atis aninda titrer.
    [DefaultExecutionOrder(80)]
    public class LaserSight : MonoBehaviour
    {
        [Header("Canli ayar (Play'de oynanabilir)")]
        public Color laserColor = new Color(1f, 0.06f, 0.06f, 1f); // klasik kirmizi lazer
        [Tooltip("Isin kalinligi (metre).")]
        public float beamWidth = 0.0035f;
        [Tooltip("Isinin gorunurlugu: 0 = sadece benek, 1 = tam parlak cizgi.")]
        [Range(0f, 1f)] public float beamAlpha = 0.35f;
        [Tooltip("Menzil (metre).")]
        public float maxDistance = 60f;
        [Tooltip("Benegin ACISAL boyu — mesafeyle buyur ki uzakta da gorunsun.")]
        public float dotAngularSize = 0.0022f;
        public float dotMinSize = 0.01f;
        public float dotMaxSize = 0.06f;

        GrabbableObject _grab;
        NetworkWeapon _weapon;      // nisan isininin kaynagi (Fire ile ayni)
        Transform _origin;          // lazer donanimi parcasi
        Renderer _originRenderer;   // varsa cikis noktasi BUNUN bounds merkezinden (pivot yaniltir)
        Vector3 _barrelLocal = Vector3.forward;

        LineRenderer _beam;
        Transform _dot;
        Material _mat;
        bool _on;

        public void Init(GrabbableObject g, string[] partPrefixes)
        {
            _grab = g;
            _weapon = g.GetComponent<NetworkWeapon>();

            // Aday parcalari SIRAYLA dene; ilk bulunan kazanir. partPrefixes null ise bu
            // silahta lazer donanimi cizili degil — isin namlu ucundan cikacak.
            if (partPrefixes != null)
                foreach (var prefix in partPrefixes)
                {
                    foreach (var t in g.GetComponentsInChildren<Transform>(true))
                        if (t.name.StartsWith(prefix)) { _origin = t; break; }
                    if (_origin != null) break;
                }
            // Parcanin TRANSFORM konumu mesh'in gercek yerinde OLMAYABILIR (pivot kokte
            // birakilmis modeller): isin kabzadan cikiyordu. Renderer varsa her kare
            // bounds merkezini kullaniriz — o gorunen geometrinin gercek yeri.
            _originRenderer = _origin != null ? _origin.GetComponent<Renderer>() : null;

            var grip = g.GetComponent<WeaponGrip>();
            if (grip != null && grip.Profile != null && grip.Profile.barrelLocalDirection.sqrMagnitude > 0.01f)
                _barrelLocal = grip.Profile.barrelLocalDirection.normalized;

            Debug.Log(_origin != null
                ? $"[Lazer] {g.name}: donanim = '{_origin.name}'" +
                  (_originRenderer != null ? " (cikis: mesh merkezi)" : " (cikis: transform)")
                : $"[Lazer] {g.name}: modelde lazer donanimi yok — isin namlu ucundan cikacak.");
        }

        void LateUpdate()
        {
            if (_grab == null) return;

            // Elde degilse kapali. IsHeld agda senkron oldugu icin bu kontrol HER istemcide
            // ayni sonucu verir — lazeri herkes ayni anda gorur/gormez.
            bool want = _grab.IsHeld;
            if (want != _on)
            {
                _on = want;
                if (_on && _beam == null) Build();
                if (_beam != null) _beam.enabled = _on;
                if (_dot != null) _dot.gameObject.SetActive(_on);
            }
            if (!_on || _beam == null) return;

            // Yon ve cikis: mumkunse silahin GERCEK nisan isini kullan (Fire ile ayni kaynak),
            // yoksa profil namlu ekseni. Boylece benek merminin gidecegi yeri gosterir.
            Vector3 dir, muzzle;
            if (_weapon != null) _weapon.GetAimRay(out muzzle, out dir);
            else
            {
                dir = _grab.transform.TransformDirection(_barrelLocal);
                muzzle = _grab.transform.position;
            }

            // Cikis noktasi: lazer donanimi VARSA onun mesh merkezi (gorsel dogru), YOKSA
            // namlu ucu — modelde modul olmadigi icin isinin oradan cikmasi en dogal yer.
            Vector3 emitter = _originRenderer != null ? _originRenderer.bounds.center
                            : _origin != null ? _origin.position
                            : muzzle;
            Vector3 from = emitter + dir * 0.02f; // silahin kendi govdesine carpmasin

            bool blocked = Physics.Raycast(from, dir, out var hit, maxDistance,
                                           Physics.AllLayers, QueryTriggerInteraction.Ignore);
            Vector3 end = blocked ? hit.point : from + dir * maxDistance;

            _beam.SetPosition(0, from);
            _beam.SetPosition(1, end);
            _beam.startWidth = _beam.endWidth = beamWidth;

            if (_dot != null)
            {
                _dot.gameObject.SetActive(blocked); // havada asili benek olmasin
                if (blocked)
                {
                    var cam = Camera.main;
                    float d = cam != null ? Vector3.Distance(cam.transform.position, hit.point) : 5f;
                    _dot.position = hit.point + hit.normal * 0.004f; // yuzeye gomulmesin
                    if (cam != null)
                        _dot.rotation = Quaternion.LookRotation(_dot.position - cam.transform.position, Vector3.up);
                    _dot.localScale = Vector3.one * Mathf.Clamp(d * dotAngularSize, dotMinSize, dotMaxSize);
                }
            }

            if (_mat != null) UITheme.SetMaterialColor(_mat, laserColor);
            if (_beamMat != null)
                UITheme.SetMaterialColor(_beamMat, new Color(laserColor.r, laserColor.g, laserColor.b, beamAlpha));
        }

        Material _beamMat;

        void Build()
        {
            // Isin: ince cizgi, dunyada. Ayri materyal cunku cizgi benekten daha SOLUK
            // (gercek lazerde havada gorunen sey zayif sacilim, asil parlak olan benektir).
            var bgo = new GameObject("~LaserBeam");
            bgo.transform.SetParent(null, true);
            _beam = bgo.AddComponent<LineRenderer>();
            _beam.positionCount = 2;
            _beam.useWorldSpace = true;
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;
            _beamMat = MakeGlowMaterial(null);
            _beam.material = _beamMat;

            // Benek: kameraya donuk kucuk quad, ortasi parlak kenari sonen.
            var dgo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(dgo.GetComponent<Collider>());
            dgo.name = "~LaserDot";
            dgo.transform.SetParent(null, true);
            _dot = dgo.transform;
            _mat = MakeGlowMaterial(MakeGlowDot(64));
            dgo.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            var dmr = dgo.GetComponent<MeshRenderer>();
            dmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dmr.receiveShadows = false;
        }

        /// <summary>Isiktan etkilenmeyen, EKLEMELI karisimli materyal — lazer gibi parlar.</summary>
        static Material MakeGlowMaterial(Texture tex)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One); // ekleme = parlama
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3100;
            if (tex != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            }
            return m;
        }

        /// <summary>Ortasi dolu, kenari sonen benek dokusu (hale).</summary>
        static Texture2D MakeGlowDot(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "LaserDot" };
            float c = (size - 1) * 0.5f, core = size * 0.13f, halo = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                    float a = d <= core ? 1f : Mathf.Clamp01(1f - (d - core) / (halo - core));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            t.Apply();
            return t;
        }

        void OnDestroy()
        {
            // Isin ve benek dunyada duruyor (silahin cocugu degil) — elle silinmeli.
            if (_beam != null) Destroy(_beam.gameObject);
            if (_dot != null) Destroy(_dot.gameObject);
        }
    }
}
