using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Temanin SESI ve HAVASI — donguye giren ortam sesi ve oyuncunun cevresinde ucusan
    /// toz/kul zerreleri.
    ///
    /// SEPARATE FROM <see cref="WorldTheme"/> ON PURPOSE. That class is a block of settings: it
    /// writes <see cref="RenderSettings"/>, writes them back, and owns nothing. These two things
    /// are the opposite — an AudioSource that must keep playing and a particle system that must
    /// follow the player — and folding them in would have turned a settings block into a scene
    /// manager. It listens to <see cref="WorldTheme.Changed"/> instead, which is also what any
    /// later addition (border decor, weather) should do.
    ///
    /// ONE INSTANCE, BOUND AT STARTUP, like <c>AvatarLegPlantBinder</c>: nothing in any scene
    /// has to be wired for a theme to be audible, and a scene built by the setup wizard from
    /// scratch still gets it.
    /// </summary>
    [DisallowMultipleComponent]
    public class ThemeAmbience : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bind()
        {
            var go = new GameObject("~ThemeAmbience");
            DontDestroyOnLoad(go);
            go.AddComponent<ThemeAmbience>();
        }

        AudioSource _wind;
        ParticleSystem _motes;
        ParticleSystemRenderer _motesRenderer;
        Material _moteMaterial;

        void OnEnable()
        {
            WorldTheme.Changed += Apply;
            // Bilesen temadan SONRA dogmus olabilir (tema harita yuklenirken uygulaniyor,
            // bu nesne sahne acilisinda). O yuzden mevcut temayi bir kez kendimiz okuyoruz;
            // yalnizca olaya guvenmek, ilk haritada sessiz kalmak demekti.
            Apply(string.IsNullOrEmpty(WorldTheme.ActiveId)
                ? null
                : ThemeLibrary.Instance.ById(WorldTheme.ActiveId));
        }

        void OnDisable() => WorldTheme.Changed -= Apply;

        void OnDestroy()
        {
            if (_moteMaterial != null) Destroy(_moteMaterial);
        }

        void Apply(ThemeDef def)
        {
            ApplyWind(def);
            ApplyMotes(def);
        }

        // ------------------------------------------------------------- ruzgar

        void ApplyWind(ThemeDef def)
        {
            var clip = def != null ? def.ResolveAmbience() : null;
            if (clip == null)
            {
                if (_wind != null) _wind.Stop();
                return;
            }

            if (_wind == null)
            {
                _wind = gameObject.AddComponent<AudioSource>();
                _wind.loop = true;
                _wind.playOnAwake = false;
                // 2D: ruzgar bir KAYNAKTAN gelmiyor, her yerde. 3D yapmak onu odanin
                // bir kosesine cakili bir hoparlore cevirirdi.
                _wind.spatialBlend = 0f;
                // Silah/adim seslerinin ALTINDA kalmali; oncelik sayisi buyudukce dusuyor.
                _wind.priority = 200;
            }

            _wind.clip = clip;
            _wind.volume = def.ambienceVolume;
            if (!_wind.isPlaying) _wind.Play();
        }

        // ------------------------------------------------------------- zerreler

        void ApplyMotes(ThemeDef def)
        {
            bool want = def != null && def.ambientMoteRate > 0.01f;
            if (!want)
            {
                if (_motes != null) _motes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }

            EnsureMotes();

            var emission = _motes.emission;
            emission.rateOverTime = def.ambientMoteRate;

            var main = _motes.main;
            main.startColor = def.ambientMoteColor;

            if (!_motes.isPlaying) _motes.Play();
        }

        void EnsureMotes()
        {
            if (_motes != null) return;

            var go = new GameObject("Motes");
            go.transform.SetParent(transform, false);

            _motes = go.AddComponent<ParticleSystem>();
            _motes.Stop();

            var main = _motes.main;
            main.loop = true;
            main.startLifetime = 6f;
            main.startSpeed = 0.25f;
            main.startSize = 0.012f;
            main.gravityModifier = 0.02f;      // agir agir asagi suzulsun
            main.maxParticles = 120;           // Quest'te tavan; 120 zerre gorsel olarak zaten yeter
            // DUNYA UZAYI SART. Yayici kafayi takip ediyor (bkz. LateUpdate); yerel uzayda
            // zerreler kafayla birlikte suruklenir ve oyuncu dondugunde tum toz bulutu
            // onunla beraber doner — hemen fark edilen, ne oldugu anlasilmayan bir yanlislik.
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var shape = _motes.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(9f, 5f, 9f);

            var vel = _motes.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.12f, 0.05f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);

            // Girip cikarken sonsunler: aniden beliren zerre goz alir.
            var alpha = _motes.colorOverLifetime;
            alpha.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f),
                        new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
            alpha.color = new ParticleSystem.MinMaxGradient(grad);

            _motesRenderer = go.GetComponent<ParticleSystemRenderer>();
            _moteMaterial = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"))
            {
                name = "M_ThemeMotes (runtime)"
            };
            _moteMaterial.SetFloat("_Surface", 1f);
            _moteMaterial.SetFloat("_Blend", 0f);
            _moteMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _motesRenderer.sharedMaterial = _moteMaterial;
            _motesRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            _motesRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _motesRenderer.receiveShadows = false;
        }

        void LateUpdate()
        {
            if (_motes == null) return;

            // Yayiciyi kafanin uzerine tasi. LateUpdate: kafa konumu bu karede zaten
            // guncellenmis olsun, yoksa zerreler bir kare geriden gelir.
            var head = XRRigReference.HeadOrCamera;
            if (head != null) _motes.transform.position = head.position;
        }
    }
}
