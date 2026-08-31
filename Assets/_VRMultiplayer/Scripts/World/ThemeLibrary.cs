using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// One named WORLD LOOK — the sky, the sun, the ambient tint and the fog a map is played
    /// under. "KIYAMET", "DENIZ".
    ///
    /// A THIRD AXIS, next to <see cref="Constructor.PropCategory"/> (what a piece DOES) and
    /// <see cref="Constructor.PropPalette"/> (which world it is FROM). Those two decide what the
    /// player can place; a theme decides what the place LOOKS like, and the two are genuinely
    /// independent: apocalypse props read completely differently under a noon sun than under a
    /// low amber one, and the same dusty sky is right for more than one prop set.
    ///
    /// DATA, NOT AN ENUM — same reasoning as <see cref="Constructor.PropPalette"/>. Adding a
    /// world is authoring work (menu 49), not a recompile.
    ///
    /// DELIBERATELY SMALL. Everything here is a global render setting that costs nothing per
    /// frame to hold: no meshes, no per-object work, no components on anything in the scene.
    /// Floor materials, border decor and ambience sit OUTSIDE this type on purpose — they are
    /// scene content with their own lifetimes, and folding them in would turn a settings block
    /// into a scene manager.
    /// </summary>
    [Serializable]
    public class ThemeDef
    {
        [Tooltip("SABIT kimlik — kayitli haritalar bunu tutar. Bir kez verildikten sonra " +
                 "degistirme; gorunen adi degistirmek serbest.")]
        public string id;

        [Tooltip("Menulerde gorunen ad. Serbestce degistirilebilir.")]
        public string displayName;

        // ------------------------------------------------------------- gokyuzu

        /// <summary>
        /// Resources-relative path of the skybox material, loaded ONLY while the theme is on
        /// screen.
        ///
        /// A PATH AND NOT A DIRECT REFERENCE, unlike most fields on a ScriptableObject. A direct
        /// reference is loaded with the asset that holds it, so six themes would mean six
        /// skyboxes — and their cubemaps — resident in Quest memory at once while five of them
        /// are not being looked at. The lazy path is the same trade
        /// <see cref="Constructor.PropDef.resourcePath"/> makes, for the same reason.
        /// </summary>
        [Tooltip("Skybox materyalinin Resources altindaki yolu (uzantisiz). BOS = sahnenin " +
                 "kendi gokyuzu degismez.\n\nDogrudan referans DEGIL: dogrudan referans, " +
                 "kutuphaneyle birlikte TUM temalarin gokyuzunu (ve cubemap'lerini) bellege " +
                 "getirirdi.")]
        public string skyboxPath = "";

        // ------------------------------------------------------------- zemin

        /// <summary>
        /// Resources-relative path of the material laid over the floor, lazily loaded like
        /// <see cref="skyboxPath"/>.
        ///
        /// THE FLOOR IS HALF THE FRAME. A standing player looks down far more than up, and in
        /// this project the floor was an UNLIT grid — a material that ignores light by
        /// definition, so the theme's amber sun landed on every prop and left the ground flat
        /// black. Whatever a theme puts here must be a LIT shader or the same hole reopens.
        ///
        /// Which objects count as "the floor" is <see cref="ThemeLibrary.floorObjectNames"/>'s
        /// business, not this field's: that is a fact about the scene, and every theme paints
        /// the same surfaces.
        /// </summary>
        [Tooltip("Zemine serilecek materyalin Resources altindaki yolu (uzantisiz). " +
                 "BOS = zemine dokunulmaz.\n\nLIT bir shader olmali: Unlit bir zemin temanin " +
                 "isigini HIC almaz ve kapkara kalir.")]
        public string floorMaterialPath = "";

        // ------------------------------------------------------------- ambiyans

        /// <summary>
        /// Resources-relative path of the looping ambience bed, lazily loaded like the rest.
        ///
        /// AMBIENCE IS NOT MUSIC and does not go through <c>MusicPlayer</c>: music is one track
        /// for the whole app and ambience belongs to the place. It is also the cheapest thing in
        /// this file by a wide margin — a room reads as "outdoors, windy, empty" from sound
        /// alone, before the player has looked at anything.
        /// </summary>
        [Tooltip("Donguye giren ortam sesinin Resources altindaki yolu (uzantisiz). " +
                 "BOS = ortam sesi yok.\n\nMuzik DEGIL: muzik tum uygulamanin, ambiyans MEKANIN.")]
        public string ambiencePath = "";

        [Tooltip("Ortam sesi siddeti. Altinda kalmasi gereken sey oyunun kendi sesleri — " +
                 "ayak sesi ve silah, ambiyansi bastirabilmeli.")]
        [Range(0f, 1f)] public float ambienceVolume = 0.35f;

        /// <summary>
        /// Ambient motes per second — ash, dust, snow. Zero turns the emitter off entirely.
        ///
        /// EMITTED AROUND THE PLAYER, not across the map (see <see cref="ThemeAmbience"/>): a
        /// world-sized emitter spends almost all of its particles where nobody is looking. A
        /// small box that follows the head gets the same effect from a few dozen particles,
        /// which is the difference between free and not on a Quest.
        /// </summary>
        [Tooltip("Saniyede uretilen toz/kul zerresi. 0 = kapali. Yayici oyuncunun ETRAFINDA " +
                 "duruyor, haritaya yayilmiyor — harita boyu bir yayici zerrelerinin cogunu " +
                 "kimsenin bakmadigi yere harcar.")]
        [Min(0f)] public float ambientMoteRate = 14f;

        [Tooltip("Zerrelerin rengi.")]
        public Color ambientMoteColor = new Color(0.55f, 0.47f, 0.38f, 0.5f);

        // ------------------------------------------------------------- gunes

        [Tooltip("Yonlu isigin rengi. Kiyamet/gun batimi icin sicak amber, steril bir ic mekan " +
                 "icin beyaza yakin.")]
        public Color sunColor = Color.white;

        [Tooltip("Yonlu isigin siddeti.")]
        [Min(0f)] public float sunIntensity = 1f;

        /// <summary>
        /// Sun elevation in degrees — 90 is straight down, small values are near the horizon.
        ///
        /// STORED AS TWO ANGLES, not a Quaternion or a direction vector: this is the one number
        /// that decides whether a scene reads as noon or as dusk, and it has to be typeable.
        /// Negative is allowed (sun below the horizon) so a night theme can keep a directional
        /// light for moonlight without fighting the field.
        /// </summary>
        [Tooltip("Gunesin YUKSEKLIGI (derece). 90 = tepede (ogle), 10-20 = ufka yakin " +
                 "(alacakaranlik, uzun golgeler). Negatif = ufkun altinda (ay isigi).")]
        [Range(-30f, 90f)] public float sunPitch = 50f;

        [Tooltip("Gunesin YONU (derece, pusula). Golgelerin hangi yone dustugunu belirler.")]
        [Range(0f, 360f)] public float sunYaw = 30f;

        /// <summary>
        /// Whether the sun casts shadows. ONE shadow-casting light is the Quest budget, and it
        /// is this one — extra lights a theme adds later (a burning barrel, a broken lamp) must
        /// leave this off.
        /// </summary>
        [Tooltip("Gunes golge dusursun mu? Quest'te GOLGE VEREN TEK ISIK bu olmali; temanin " +
                 "ekledigi varil/lamba gibi yerel isiklar golgesiz kalmali.")]
        public bool sunShadows = true;

        // ------------------------------------------------------------- ortam isigi

        /// <summary>
        /// Ambient light as a three-band gradient (sky / horizon / ground).
        ///
        /// THIS IS WHAT LIGHTS THE PROPS. Everything the player builds is spawned at runtime, so
        /// it takes no baked light at all — a placed crate is lit by the sun plus this gradient
        /// and nothing else. Getting the theme's mood into these three colours matters more than
        /// the skybox does, because the skybox is behind the player half the time and this is on
        /// every surface.
        /// </summary>
        [Tooltip("Ortam isigi — TEPE rengi. Calisma zamaninda kurulan tum proplari aydinlatan " +
                 "sey bu (yerlestirilen prop baked isik ALMAZ).")]
        public Color ambientSky = new Color(0.45f, 0.47f, 0.50f);

        [Tooltip("Ortam isigi — UFUK rengi.")]
        public Color ambientEquator = new Color(0.38f, 0.38f, 0.36f);

        [Tooltip("Ortam isigi — ZEMIN rengi (yerden yansiyan).")]
        public Color ambientGround = new Color(0.24f, 0.22f, 0.20f);

        // ------------------------------------------------------------- sis

        [Tooltip("Sis acik mi? Atmosferi kurmanin yani sira uzaktaki geometriyi eritir.")]
        public bool fogEnabled;

        [Tooltip("Sis rengi. GOKYUZUNUN UFUK RENGIYLE AYNI olmali — aralarindaki fark, " +
                 "uzaktaki geometrinin gokyuzune karismak yerine onunde yuzuyormus gibi " +
                 "gorunmesine yol acar.")]
        public Color fogColor = new Color(0.45f, 0.45f, 0.45f);

        /// <summary>
        /// Exponential-squared fog density.
        ///
        /// SIZED FOR A ROOM, not for an open world. The arena is a scanned room plus a build
        /// margin, so almost everything is inside 8 m: a density tuned on a 200 m landscape
        /// (~0.005) is invisible here. At 0.06 a prop 6 m away picks up roughly a fifth of the
        /// fog colour, which is the dusty-air look; at 0.15 the far wall of a small room starts
        /// to disappear, which is usually too much.
        /// </summary>
        [Tooltip("Sis yogunlugu (exp2). ODA OLCEGINE gore ayarlanir: 0.04-0.08 tozlu hava, " +
                 "0.15+ kucuk bir odanin karsi duvarini yutar. Acik dunya degerleri (~0.005) " +
                 "burada GORUNMEZ.")]
        [Min(0f)] public float fogDensity = 0.06f;

        // ------------------------------------------------------------- cozumleme

        [NonSerialized] Material _sky;
        [NonSerialized] bool _skyTried;
        [NonSerialized] Material _floor;
        [NonSerialized] bool _floorTried;
        [NonSerialized] AudioClip _ambience;
        [NonSerialized] bool _ambienceTried;

        /// <summary>
        /// The skybox material, lazily loaded and cached. Null both when no sky is configured
        /// and when the path does not resolve; the applier treats the two the same — it leaves
        /// the scene's own sky alone — because a theme that only retints the light is a
        /// legitimate theme.
        /// </summary>
        public Material ResolveSkybox()
        {
            if (_sky != null) return _sky;
            if (_skyTried) return null;
            _skyTried = true;

            if (string.IsNullOrEmpty(skyboxPath)) return null;

            _sky = Resources.Load<Material>(skyboxPath);
            if (_sky == null)
                Debug.LogWarning($"[ThemeLibrary] '{id}' icin Resources/{skyboxPath} bulunamadi.");
            return _sky;
        }

        /// <summary>The floor material, lazily loaded and cached. Null means "leave the floor alone".</summary>
        public Material ResolveFloor()
        {
            if (_floor != null) return _floor;
            if (_floorTried) return null;
            _floorTried = true;

            if (string.IsNullOrEmpty(floorMaterialPath)) return null;

            _floor = Resources.Load<Material>(floorMaterialPath);
            if (_floor == null)
                Debug.LogWarning($"[ThemeLibrary] '{id}' icin Resources/{floorMaterialPath} bulunamadi.");
            return _floor;
        }

        /// <summary>The ambience loop, lazily loaded. Null means "silent theme".</summary>
        public AudioClip ResolveAmbience()
        {
            if (_ambience != null) return _ambience;
            if (_ambienceTried) return null;
            _ambienceTried = true;

            if (string.IsNullOrEmpty(ambiencePath)) return null;

            _ambience = Resources.Load<AudioClip>(ambiencePath);
            if (_ambience == null)
                Debug.LogWarning($"[ThemeLibrary] '{id}' icin Resources/{ambiencePath} bulunamadi.");
            return _ambience;
        }

        /// <summary>The sun's rotation, built from the two authored angles.</summary>
        public Quaternion SunRotation => Quaternion.Euler(sunPitch, sunYaw, 0f);

        /// <summary>Editor tooling changed the asset under us; drop the cache.</summary>
        public void InvalidateCache()
        {
            _sky = null;
            _skyTried = false;
            _floor = null;
            _floorTried = false;
            _ambience = null;
            _ambienceTried = false;
        }
    }

    /// <summary>
    /// The catalogue of world looks. One asset under a Resources folder so every peer reads the
    /// SAME list at runtime — <see cref="Constructor.PropLibrary"/>'s arrangement, for the same
    /// reason: no per-scene wiring, and a map that names a theme resolves it identically on the
    /// PC server and on every headset.
    ///
    /// ONE ADDRESSING SCHEME, unlike the prop library: themes are named by STRING id everywhere.
    /// The prop library also carries a ushort index because placements are sent per prop and the
    /// two bytes matter; a theme is named once per map, inside the layout JSON that is already
    /// being shipped whole, so the index scheme would buy nothing and cost the ordering
    /// constraint that comes with it.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Theme Library", fileName = "ThemeLibrary")]
    public class ThemeLibrary : ScriptableObject
    {
        public const string ResourceName = "ThemeLibrary";

        [Tooltip("Adlandirilmis dunya gorunumleri. Menu 49 uretir/gunceller.")]
        public ThemeDef[] themes = new ThemeDef[0];

        /// <summary>
        /// Names of the objects that count as "the floor" — every theme repaints these.
        ///
        /// A FACT ABOUT THE SCENE, not about any one theme, which is why it lives on the library
        /// and not on <see cref="ThemeDef"/>. Name matching is already this project's way of
        /// pointing at scene objects from data (see <c>ConstructorPassthrough.alwaysKeep</c>);
        /// a marker component would be tidier in the abstract but would have to be added by hand
        /// to every scene before any theme worked at all.
        ///
        /// "Ground" is the dev scene's 40 m plane; "Zemin" is what the room-scan tool names the
        /// floor mesh it builds from a scan (Editor/RoomScanSetup).
        /// </summary>
        [Tooltip("Zemin sayilan nesnelerin adlari — her tema BUNLARI boyar. Isim tam eslesir.\n\n" +
                 "'Ground' = gelistirme sahnesindeki 40 m'lik duzlem, 'Zemin' = oda taramasindan " +
                 "uretilen zemin mesh'i.")]
        public string[] floorObjectNames = { "Ground", "Zemin" };

        public int Count => themes != null ? themes.Length : 0;

        public ThemeDef ById(string id)
        {
            if (string.IsNullOrEmpty(id) || themes == null) return null;
            foreach (var t in themes)
                if (t != null && t.id == id) return t;
            return null;
        }

        /// <summary>
        /// A theme's shown name, or a readable stand-in. An unknown id is NOT hidden: it means a
        /// map points at a theme that was deleted, and saying so is how anyone finds out.
        /// </summary>
        public string NameOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "TEMASIZ";
            var t = ById(id);
            return t != null && !string.IsNullOrEmpty(t.displayName) ? t.displayName : "? " + id;
        }

        /// <summary>Turns a display name into a stable id — same rule as prop palettes.</summary>
        public static string MakeId(string displayName) =>
            Constructor.PropLibrary.MakePaletteId(displayName);

        /// <summary>Problems worth reporting. Empty list = healthy library.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (themes == null || themes.Length == 0)
            {
                problems.Add("Tema kutuphanesi bos — menu 49 calistir.");
                return problems;
            }

            var seen = new HashSet<string>();
            for (int i = 0; i < themes.Length; i++)
            {
                var t = themes[i];
                if (t == null) { problems.Add($"[{i}] null girdi."); continue; }

                if (string.IsNullOrEmpty(t.id)) problems.Add($"[{i}] kimlik bos.");
                else if (!seen.Add(t.id)) problems.Add($"[{i}] kopya kimlik: '{t.id}'.");

                if (!string.IsNullOrEmpty(t.skyboxPath) && t.ResolveSkybox() == null)
                    problems.Add($"[{i}] '{t.id}': Resources/{t.skyboxPath} cozulemedi.");

                if (t.fogEnabled && t.fogDensity <= 0.0001f)
                    problems.Add($"[{i}] '{t.id}': sis acik ama yogunluk ~0 — hicbir etkisi yok.");
            }
            return problems;
        }

        /// <summary>Editor tooling rewrote the list — drop every cached skybox.</summary>
        public void InvalidateCaches()
        {
            if (themes == null) return;
            foreach (var t in themes) t?.InvalidateCache();
        }

        // ------------------------------------------------------------- singleton

        static ThemeLibrary _instance;
        static ThemeLibrary _fallback;

        /// <summary>
        /// The shared library. Returns an EMPTY in-memory one when the asset is missing, so a
        /// project that has not authored any theme yet simply plays under the scene's own
        /// lighting instead of null-referencing on map load.
        ///
        /// The miss is NOT cached, matching <see cref="Constructor.PropLibrary.Instance"/>: menu
        /// 49 creates this asset during an editor session, and a cached miss would keep serving
        /// the empty library until the next domain reload — the tool would look broken right
        /// after it succeeded.
        /// </summary>
        public static ThemeLibrary Instance
        {
            get
            {
                if (_instance != null) return _instance;

                _instance = Resources.Load<ThemeLibrary>(ResourceName);
                if (_instance != null) return _instance;

                if (_fallback == null) _fallback = CreateInstance<ThemeLibrary>();
                return _fallback;
            }
        }
    }
}
