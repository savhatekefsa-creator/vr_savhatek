using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRMultiplayer
{
    /// <summary>
    /// Puts a <see cref="ThemeDef"/> on the scene, and takes it back off.
    ///
    /// GLOBAL SETTINGS ONLY — <see cref="RenderSettings"/> and the one directional light. It
    /// creates nothing, parents nothing and runs no per-frame code, which is what lets a theme
    /// change be a handful of assignments on map load instead of a scene rebuild.
    ///
    /// IT ALWAYS REMEMBERS WHAT IT OVERWROTE. A player opens a themed map, then opens a
    /// themeless one; without a captured baseline the second map would keep the first one's
    /// amber sun forever, and the only way back would be restarting the app. The capture happens
    /// ONCE, at the first change, so it holds the scene's authored look and not the previous
    /// theme's. Same shape as <c>ConstructorPassthrough</c>'s <c>_prevClear</c>/<c>_prevBackground</c>.
    ///
    /// THE CAMERA IS NOT TOUCHED, deliberately. Passthrough owns the camera's clear flags and
    /// background (see <c>ConstructorPassthrough</c>), and that ownership was hard won — the
    /// alpha-composite bug where the real room bled through smoke, explosions and death screens
    /// came from exactly this settings block being written by two things at once. A theme sets
    /// <see cref="RenderSettings.skybox"/>, which is what gets drawn when the camera decides to
    /// clear to a sky; whether it decides that stays passthrough's call.
    /// </summary>
    public static class WorldTheme
    {
        /// <summary>The theme currently on the scene. Empty means the scene's authored look.</summary>
        public static string ActiveId { get; private set; } = "";

        /// <summary>
        /// Raised whenever the theme changes — the new <see cref="ThemeDef"/>, or null when the
        /// scene's own look is restored.
        ///
        /// THE SEAM THAT KEEPS THIS TYPE SMALL. Ambience is a looping AudioSource and a particle
        /// emitter: things with a lifetime, an owner and a place in the hierarchy, which is
        /// exactly what this class promises not to have. Announcing the change instead of acting
        /// on it lets <see cref="ThemeAmbience"/> own that side without either of them knowing
        /// how the other works — and anything added later (border decor, weather) hooks the same
        /// seam rather than growing this file.
        /// </summary>
        public static event System.Action<ThemeDef> Changed;

        // ---- yakalanmis ozgun sahne ayarlari (yalnizca ilk degisiklikte doldurulur)
        static bool _captured;
        static Material _skybox0;
        static Light _sun0Ref;
        static AmbientMode _ambientMode0;
        static Color _ambientSky0, _ambientEquator0, _ambientGround0, _ambientLight0;
        static float _ambientIntensity0;
        static bool _fog0;
        static Color _fogColor0;
        static FogMode _fogMode0;
        static float _fogDensity0;

        // ---- yakalanmis gunes (isik nesnesi basina ayri, bkz. CaptureSun)
        static Light _sun;
        static Color _sunColor0;
        static float _sunIntensity0;
        static Quaternion _sunRot0;
        static LightShadows _sunShadows0;

        /// <summary>
        /// Other directional lights the theme switched off, so <see cref="Restore"/> can switch
        /// them back on. See <see cref="ApplySun"/> for why they are switched off at all.
        /// </summary>
        static readonly List<Light> _dimmed = new List<Light>();

        /// <summary>A floor renderer and the materials it had before the theme painted over it.</summary>
        struct FloorPaint
        {
            public Renderer renderer;
            public Material[] materials;
        }

        static readonly List<FloorPaint> _painted = new List<FloorPaint>();

        /// <summary>
        /// Wipes the statics when play starts.
        ///
        /// NOT OPTIONAL with domain reload disabled: without it a second Play would inherit the
        /// first session's "baseline", which by then is whatever theme was last applied — and
        /// restoring to it would bake that theme into the scene permanently.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ActiveId = "";
            _captured = false;
            _skybox0 = null;
            _sun0Ref = null;
            _sun = null;
            _dimmed.Clear();
            _painted.Clear();
        }

        /// <summary>
        /// Applies the theme named <paramref name="themeId"/>. Empty restores the scene's own
        /// look; an unknown id does the same AND says so, because a map pointing at a deleted
        /// theme is a real problem and silently playing under the last theme would hide it.
        /// </summary>
        public static void Apply(string themeId)
        {
            themeId = themeId ?? "";
            if (ActiveId == themeId) return;

            if (string.IsNullOrEmpty(themeId)) { Restore(); return; }

            var def = ThemeLibrary.Instance.ById(themeId);
            if (def == null)
            {
                Debug.LogWarning($"[WorldTheme] '{themeId}' temasi kutuphanede yok — " +
                                 "sahnenin kendi gorunumune donuluyor (menu 49).");
                Restore();
                return;
            }

            // TEMASIZDAN TEMALIYA her gecisde yeniden yakala. Yakalama eskiden bir kez
            // yapiliyordu ve bu, yedegi BAYATLATIYORDU: sahne arada baska bir yoldan
            // degistiginde (editorde elle, ya da bir onceki geri-alma yarim kaldiginda)
            // "orijinal" artik sahnenin o anki hali degildi, ve Restore sahneyi eski bir
            // duruma geri yaziyordu. ActiveId bos oldugu her an sahne tanim geregi temasiz,
            // yani o an yakalanan sey gercekten sahnenin kendi gorunumudur.
            if (string.IsNullOrEmpty(ActiveId)) Capture();

            ApplySun(def);
            ApplySky(def);
            ApplyFog(def);
            ApplyFloor(def);

            // Ortam kuresini TAZELE. Gokyuzu ve ortam renkleri bu cagri olmadan yalnizca
            // yeni cizilen nesnelerde gecerli olur; sahnede duran proplar eski ortam
            // isigiyla kalir ve tema yarim uygulanmis gorunur.
            DynamicGI.UpdateEnvironment();

            ActiveId = themeId;
            Changed?.Invoke(def);
        }

        /// <summary>Puts the scene back the way it was authored. No-op if nothing was ever applied.</summary>
        public static void Restore()
        {
            // Ambiyans HER DURUMDA susturulur, RenderSettings yakalanmamis olsa bile:
            // yakalama yalnizca gorsel ayarlar icin var, ses onun disinda yasiyor ve
            // "geri al" dedikten sonra calmaya devam eden bir ruzgar sesi hata olurdu.
            ActiveId = "";
            Changed?.Invoke(null);

            if (!_captured) return;

            RenderSettings.skybox = _skybox0;
            RenderSettings.sun = _sun0Ref;

            RenderSettings.ambientMode = _ambientMode0;
            RenderSettings.ambientSkyColor = _ambientSky0;
            RenderSettings.ambientEquatorColor = _ambientEquator0;
            RenderSettings.ambientGroundColor = _ambientGround0;
            RenderSettings.ambientLight = _ambientLight0;
            RenderSettings.ambientIntensity = _ambientIntensity0;

            RenderSettings.fog = _fog0;
            RenderSettings.fogColor = _fogColor0;
            RenderSettings.fogMode = _fogMode0;
            RenderSettings.fogDensity = _fogDensity0;

            if (_sun != null)
            {
                _sun.color = _sunColor0;
                _sun.intensity = _sunIntensity0;
                _sun.transform.rotation = _sunRot0;
                _sun.shadows = _sunShadows0;
            }

            foreach (var l in _dimmed)
                if (l != null) l.enabled = true;
            _dimmed.Clear();

            UnpaintFloor();

            DynamicGI.UpdateEnvironment();
        }

        // ------------------------------------------------------------- uygulama

        static void ApplySun(ThemeDef def)
        {
            var sun = ResolveSun();
            if (sun == null) return;

            CaptureSun(sun);
            DimOtherSuns(sun);

            sun.color = def.sunColor;
            sun.intensity = def.sunIntensity;
            sun.transform.rotation = def.SunRotation;
            sun.shadows = def.sunShadows ? LightShadows.Soft : LightShadows.None;

            // Prosedurel gokyuzu gunes diskini BURADAN okuyor. Atanmazsa disk sahnedeki
            // rastgele bir isigi izler ya da hic cikmaz — gokyuzu ile golgeler farkli
            // yonleri gosterir, ki bakan herkesin fark ettigi ama sebebini bulamadigi
            // turden bir yanlislik.
            RenderSettings.sun = sun;
        }

        /// <summary>
        /// Switches off every OTHER directional light while a theme is on.
        ///
        /// A THEME OWNS THE KEY LIGHT, and it can only own it if there is one. SampleScene ships
        /// with two enabled directional lights — a white one at intensity 2 and a warm one at 1,
        /// pointing different ways — so retinting only the first leaves the second washing the
        /// amber straight back out, and the theme reads as "barely changed anything" for a
        /// reason nobody would think to look for. Two suns is also a bug in its own right: both
        /// cast soft shadows, and Quest's budget is ONE shadow-casting light.
        ///
        /// SWITCHED OFF, NOT DELETED, and remembered for <see cref="Restore"/> — the second
        /// light is somebody's authored scene lighting, and a themeless map is supposed to get
        /// the scene back exactly as it was.
        /// </summary>
        static void DimOtherSuns(Light keep)
        {
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l == keep || l.type != LightType.Directional || !l.isActiveAndEnabled) continue;
                l.enabled = false;
                _dimmed.Add(l);
            }
        }

        static void ApplySky(ThemeDef def)
        {
            // BOS YOL = "gokyuzune dokunma". Yalnizca isigi yeniden renklendiren bir tema
            // gecerli bir tema; burada null atamak onu siyah bir bosluga cevirirdi.
            var sky = def.ResolveSkybox();
            if (sky != null) RenderSettings.skybox = sky;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = def.ambientSky;
            RenderSettings.ambientEquatorColor = def.ambientEquator;
            RenderSettings.ambientGroundColor = def.ambientGround;
        }

        static void ApplyFog(ThemeDef def)
        {
            RenderSettings.fog = def.fogEnabled;
            if (!def.fogEnabled) return;

            RenderSettings.fogColor = def.fogColor;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = def.fogDensity;
        }

        // ------------------------------------------------------------- zemin

        /// <summary>
        /// Paints the theme's floor material over every renderer the library calls a floor.
        ///
        /// UNPAINTS FIRST. Going from one theme straight to another would otherwise record the
        /// PREVIOUS theme's material as "the original", and the scene's own floor would be lost
        /// for the rest of the session — the same trap <see cref="EnsureCaptured"/> avoids for
        /// the render settings, and it is worth solving the same way rather than cleverly.
        /// </summary>
        static void ApplyFloor(ThemeDef def)
        {
            UnpaintFloor();

            var mat = def.ResolveFloor();
            if (mat == null) return;   // zemine dokunmayan tema gecerli bir tema

            var names = ThemeLibrary.Instance.floorObjectNames;
            if (names == null || names.Length == 0) return;

            // PASIF NESNELER DE TARANIR — bu, zeminin gozlukte hic boyanmamasinin sebebiydi.
            // Gozluk istemciyken sira soyle isliyor: once insa moduna giriliyor, orada
            // ConstructorPassthrough gercek odayi gostermek icin sanal KOK nesneleri
            // SetActive(false) yapiyor (Ground dahil), harita ve temasi ise ag uzerinden
            // BUNDAN SONRA geliyor. Varsayilan arama pasifleri atladigi icin boyanacak
            // zemin bulunamiyor, sessizce gecip gidiyordu — gokyuzu/sis/isik global ayar
            // oldugu icin degisiyor, zemin degismiyordu. Simdi pasifken boyaniyor ve
            // passthrough nesneyi geri actiginda zaten temali geliyor.
            foreach (var r in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsFloorName(names, r.gameObject.name)) continue;

                _painted.Add(new FloorPaint { renderer = r, materials = r.sharedMaterials });

                // Cok malzemeli bir zemin mesh'inde HER yuvaya ayni malzeme gider: alt
                // bolumleri ayri ayri boyayacak veri yok, ve yarisi boyanmis bir zemin
                // hic boyanmamis olandan daha kotu gorunur.
                var slots = new Material[r.sharedMaterials.Length == 0 ? 1 : r.sharedMaterials.Length];
                for (int i = 0; i < slots.Length; i++) slots[i] = mat;
                r.sharedMaterials = slots;
            }
        }

        static void UnpaintFloor()
        {
            foreach (var p in _painted)
                if (p.renderer != null) p.renderer.sharedMaterials = p.materials;
            _painted.Clear();
        }

        /// <summary>
        /// Ad eslesmesi: birebir, ya da listelenen adin ardindan SAYI gelmesi.
        ///
        /// Sayi kuyrugu oda taramasinin kendi adlandirmasi: ikinci oda kurulunca zemin
        /// "Zemin2" oluyor (Editor/RoomScanSetup, <c>"Zemin" + suffix</c>). Yalnizca birebir
        /// eslesme aransa iki odali bir kurulumda ikinci odanin zemini temasiz kalirdi — ve
        /// bunun belirtisi "odanin yarisi boyandi" gibi, sebebi hic akla gelmeyecek bir sey
        /// olurdu.
        /// </summary>
        static bool IsFloorName(string[] names, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;

            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (n == candidate) return true;
                if (candidate.Length <= n.Length || !candidate.StartsWith(n)) continue;

                bool allDigits = true;
                for (int i = n.Length; i < candidate.Length; i++)
                    if (!char.IsDigit(candidate[i])) { allDigits = false; break; }
                if (allDigits) return true;
            }
            return false;
        }

        // ------------------------------------------------------------- gunes bulma

        /// <summary>
        /// The scene's main directional light: whatever <see cref="RenderSettings.sun"/> names,
        /// otherwise the first active directional light found.
        ///
        /// The scan runs only when the cached light has gone (scene change, light disabled), not
        /// every apply — a map load already costs a rebuild, but this would also fire on the
        /// server view and on every peer, and FindObjectsByType walks the whole scene.
        /// </summary>
        static Light ResolveSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;

            var named = RenderSettings.sun;
            if (named != null && named.isActiveAndEnabled) return named;

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.isActiveAndEnabled) return l;

            return null;
        }

        // ------------------------------------------------------------- yakalama

        /// <summary>
        /// Yedegi ALIR — cagrildigi anda sahnede ne varsa "orijinal" odur.
        ///
        /// KOSULSUZ, bilerek. Bir zamanlar "yalnizca ilk seferde" idi ve tek bir bayat yedek,
        /// sonraki her geri-almanin sahneyi yanlis bir duruma yazmasina yetiyordu. Cagiran
        /// taraf bunu yalnizca <see cref="ActiveId"/> bosken cagiriyor; orada sahne tanim
        /// geregi temasiz.
        ///
        /// KALAN TUZAK: editorde 49b ile 49c arasinda kod derlenirse statikler silinir,
        /// sahne temali kalir ve bir sonraki yakalama o temali hali "orijinal" sayar. Calisma
        /// zamaninda bu olamaz (oyun ortasinda domain reload yok, ResetStatics de basta
        /// temizler). Editorde onlemi 49c'yi derlemeden ONCE calistirmak.
        /// </summary>
        static void Capture()
        {
            _captured = true;

            // Gunesin yedegi de tazelensin: CaptureSun ayni isik icin bir kez yakaliyor,
            // ve o "bir kez" burada sifirlanmazsa render ayarlari tazelenirken gunes
            // eski yedekte kalirdi — yariyi duzeltip yariyi bozan bir geri-alma.
            _sun = null;

            _skybox0 = RenderSettings.skybox;
            _sun0Ref = RenderSettings.sun;

            _ambientMode0 = RenderSettings.ambientMode;
            _ambientSky0 = RenderSettings.ambientSkyColor;
            _ambientEquator0 = RenderSettings.ambientEquatorColor;
            _ambientGround0 = RenderSettings.ambientGroundColor;
            _ambientLight0 = RenderSettings.ambientLight;
            _ambientIntensity0 = RenderSettings.ambientIntensity;

            _fog0 = RenderSettings.fog;
            _fogColor0 = RenderSettings.fogColor;
            _fogMode0 = RenderSettings.fogMode;
            _fogDensity0 = RenderSettings.fogDensity;
        }

        /// <summary>
        /// Captures a light's original state the first time we write to it.
        ///
        /// PER LIGHT, not once: the sun is resolved lazily and the scene may not have had one
        /// when the first theme was applied. Capturing at the moment of the first write is what
        /// guarantees the stored values are the light's own and not a previous theme's.
        /// </summary>
        static void CaptureSun(Light sun)
        {
            if (_sun == sun) return;
            _sun = sun;
            _sunColor0 = sun.color;
            _sunIntensity0 = sun.intensity;
            _sunRot0 = sun.transform.rotation;
            _sunShadows0 = sun.shadows;
        }
    }
}
