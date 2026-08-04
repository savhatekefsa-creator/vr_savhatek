using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Holds the centralized theme for the game's UI (colors, fonts, materials).
    /// Used by PlayerHUD and the UI effects to ensure a consistent look.
    /// </summary>
    public static class UITheme
    {
        // --- Color Palette ---
        public static readonly Color Background = new Color(0.08f, 0.08f, 0.08f, 1f);
        public static readonly Color Text = Color.white;
        public static readonly Color HealthFull = Color.green;
        public static readonly Color HealthMid = Color.yellow;
        public static readonly Color HealthLow = Color.red;

        // --- Menu / HUD paneli paleti (TEK KAYNAK) ---
        //
        // Bu degerler oyuncu giris ekraninin (PlayerEntryPanel) paletidir; olum ekrani da
        // buradan besleniyor. Baslangicta renkler iki dosyaya KOPYALANMISTI ve birbirinden
        // kaydilar: olum karti kol saatinin teal'ini (0.40,0.80,0.72) ve yari saydam bir zemin
        // kullaniyordu, giris ekrani ise camgobegi ve TAM OPAK zemin. Aydinlik odanin uzerinde
        // yari saydam panel solup ucuz duruyordu. Yeni panel yazan herkes buradan alsin.
        public static readonly Color PanelBg     = new Color(0.027f, 0.047f, 0.071f, 1f);
        public static readonly Color PanelEdge   = new Color(0.090f, 0.150f, 0.210f, 0.90f);
        /// <summary>Panel icindeki cukur yuzey (isim alani, alt seritler).</summary>
        public static readonly Color SurfaceFill = new Color(0.047f, 0.078f, 0.114f, 1f);
        public static readonly Color SurfaceEdge = new Color(0.106f, 0.157f, 0.212f, 1f);

        public static readonly Color AccentCyan   = new Color(0.27f, 0.88f, 0.86f, 1f);
        public static readonly Color AccentPurple = new Color(0.65f, 0.55f, 0.98f, 1f);

        public static readonly Color TextPrimary = new Color(0.90f, 0.94f, 0.98f, 1f);
        public static readonly Color TextMuted   = new Color(0.36f, 0.41f, 0.46f, 1f);
        public static readonly Color TextDim     = new Color(0.27f, 0.33f, 0.38f, 1f);

        public static readonly Color TeamRedEdge  = new Color(0.88f, 0.34f, 0.42f, 1f);
        public static readonly Color TeamRedText  = new Color(0.94f, 0.48f, 0.53f, 1f);
        public static readonly Color TeamBlueEdge = new Color(0.29f, 0.50f, 0.91f, 1f);
        public static readonly Color TeamBlueText = new Color(0.50f, 0.65f, 0.94f, 1f);

        // --- Fonts ---
        public const float NameCharacterSize = 0.06f;
        public const int NameFontSize = 60;

        static Font _defaultFont;
        static readonly System.Collections.Generic.Dictionary<int, Material> _fontMats =
            new System.Collections.Generic.Dictionary<int, Material>();

        /// <summary>Calisma aninda uretilen HER TextMesh'in kullanmasi gereken font.
        ///
        /// Unity 6 varsayilan TextMesh fontunu KALDIRDI: font atanmayan bir TextMesh editorde
        /// (fontun bellekte oldugu ortamda) gorunur ama Quest build'inde HICBIR SEY cizmez.
        /// Katilim, takim secme, kalibrasyon, oda tarama ve olum panelleri bu yuzden cihazda
        /// bostu. Tek kaynak burasi — yeni panel yazan herkes ApplyFont cagirmali.</summary>
        public static Font DefaultFont
        {
            get
            {
                if (_defaultFont == null)
                    _defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _defaultFont;
            }
        }

        /// <summary>TextMesh'e fontu VE font materyalini atar. Ikisi de sart: tm.font yazmak
        /// MeshRenderer'in materyalini kendiliginden ayarlamaz, materyalsiz kalan yazi cizilmez.
        ///
        /// renderQueue > 0 verilirse o kuyruk icin materyal PAYLASILIR (kuyruk basina tek kopya,
        /// yazi basina degil) — boylece "her zaman ustte ciz" istegi batching'i bozmaz.</summary>
        public static void ApplyFont(TextMesh tm, int renderQueue = 0)
        {
            if (tm == null) return;
            var font = DefaultFont;
            if (font == null) return;   // built-in kaynak yoksa mevcut davranista birak

            tm.font = font;

            var mr = tm.GetComponent<MeshRenderer>();
            if (mr == null) return;

            if (renderQueue <= 0) { mr.sharedMaterial = font.material; return; }

            if (!_fontMats.TryGetValue(renderQueue, out var mat) || mat == null)
            {
                mat = new Material(font.material) { renderQueue = renderQueue };
                _fontMats[renderQueue] = mat;
            }
            mr.sharedMaterial = mat;
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir; yok edilmis materyale
        // tutunmus sozluk ikinci Play'de "MissingReference" verirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _defaultFont = null;
            _fontMats.Clear();
            _vignette = null;
            _healthGradient = null;
        }

        /// <summary>Build'de garanti bulunan unlit shader zinciri — calisma aninda malzeme
        /// ureten HER yer bunu kullanmali (4 ayri kopyasi vardi). URP/Unlit sahnede referanssiz
        /// kalirsa build'den strip edilebilir; URP/Lit oda malzemeleri sayesinde hep gemidedir.</summary>
        public static Shader SafeUnlitShader
        {
            get
            {
                var s = Shader.Find("Universal Render Pipeline/Unlit");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
                if (s == null) s = Shader.Find("Unlit/Color");
                if (s == null) s = Shader.Find("Sprites/Default");
                return s;
            }
        }

        /// <summary>
        /// Creates a material with a safe, opaque shader that won't turn magenta in builds.
        /// </summary>
        public static Material CreateLitMaterial(Color color)
        {
            // HUD elemanları için ışıklandırmadan etkilenmeyen bir shader kullanalım.
            var m = new Material(SafeUnlitShader);
            SetMaterialColor(m, color);
            return m;
        }

        /// <summary>
        /// Sets the color on a material, checking for both URP and built-in property names.
        /// </summary>
        public static void SetMaterialColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static Color GetHealthColor(float ratio)
        {
            return ratio > 0.5f
                ? Color.Lerp(HealthMid, HealthFull, (ratio - 0.5f) * 2f)
                : Color.Lerp(HealthLow, HealthMid, ratio * 2f);
        }

        // --- Kenar vinyeti ---

        static Texture2D _vignette;

        /// <summary>
        /// Merkezi seffaf, kenarlara dogru opaklasan radyal vinyet dokusu. Kafa onune
        /// kilitlenen tam-ekran efektlerin ortak dokusu (dusuk can, olum ekrani).
        ///
        /// ACI ESLEMESI (0.52 m'de 2x2 quad): uv 0.30 ≈ 30 derece, uv 0.52 ≈ 45 derece
        /// (Quest lens kenari). Esik bir ara 0.55'ti (~47 derece) ve kararma Quest'te lensin
        /// GORUNUR alaninin disina cizilip Editor'de gorunup cihazda gorunmuyordu. Bu
        /// kalibrasyon o dersin sonucu — degistirirken quad'in olcegini de ayni oranda tut,
        /// yoksa aci esleme kayar.
        /// </summary>
        public static Texture2D VignetteTexture
        {
            get
            {
                if (_vignette != null) return _vignette;

                const int S = 256;
                _vignette = new Texture2D(S, S, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                };
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    Vector2 v = new Vector2(x - (S - 1) * 0.5f, y - (S - 1) * 0.5f) / (S * 0.5f);
                    float a = Mathf.Pow(Mathf.Clamp01((v.magnitude - 0.30f) / 0.5f), 1.8f);
                    _vignette.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                _vignette.Apply();
                return _vignette;
            }
        }

        /// <summary>Kafa onune kilitlenen vinyet quad'i uretir (colliderSiz, golgesiz).</summary>
        public static Transform MakeVignetteQuad(Transform parent, string name, Color color,
            out Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            q.transform.SetParent(parent, false);

            mat = CreateTransparentMaterial(color);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", VignetteTexture);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", VignetteTexture);

            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return q.transform;
        }

        // --- Dunya-uzayi panel yapi taslari ---

        /// <summary>
        /// HUD/menu yuzeyi: sahne geometrisinin USTUNE cizen saydam malzeme.
        ///
        /// NEDEN BU SHADER: "her zaman ustte ciz" icin dogal refleks URP/Unlit'e
        /// <c>_ZTest = Always</c> yazmaktir — AMA URP/Unlit'in pass'inde ZTest hic tanimli
        /// degil ve boyle bir property YOK; <c>SetInt("_ZTest", 8)</c> sessizce hicbir sey
        /// yapmaz. Yerinde olculdu: URP/Unlit quad'lar zemine takilip kayboluyordu, ayni
        /// paneldeki YAZILAR ise duruyordu — cunku TextMesh'in font materyali bu shader'i
        /// kullaniyor. Sonuc: zemin yok, yazi havada. Panelin tamami ayni shader'a alindi.
        ///
        /// STRIP RISKI YOK: oyun zaten yazi ciziyor, yani bu shader her build'de gemide.
        /// Custom bir shader yazmak Resources'a koymayi ya da Always Included listesine
        /// eklemeyi gerektirirdi.
        ///
        /// Beyaz 1x1 doku + <c>_Color</c>: renk _Color'dan, alfa _Color.a'dan gelir.
        /// SRP batcher ile uyumlu DEGIL (quad basina cizim) — menu/HUD olceginde sorun
        /// olmadigi olculdu, ama yuzlerce quad'lik bir yuzey icin uygun degildir.
        /// </summary>
        public static Material CreateOverlayMaterial(Color color)
        {
            var sh = Shader.Find("GUI/Text Shader");
            if (sh == null) return CreateTransparentMaterial(color);   // olmamali; yine de duselim

            var m = new Material(sh);
            m.SetTexture("_MainTex", Texture2D.whiteTexture);
            m.SetColor("_Color", color);
            return m;
        }

        /// <summary>Panel zemini / tus yuzeyi icin colliderSIZ, golgesiz quad. Collider
        /// BILEREK silinir: sahne mermi raycast'leri ve bomba linecast'leriyle dolu, UI'in o
        /// sorgulara gorunmesi yanlis isabet/siper demek olurdu (imlec analitik calisir,
        /// collider'a zaten ihtiyaci yok — bkz. <see cref="VRPointer"/>).
        ///
        /// <paramref name="overlay"/> true ise yuzey sahnenin USTUNE cizilir
        /// (bkz. <see cref="CreateOverlayMaterial"/>) — kolokasyonlu oyunda gercek duvar
        /// cogu zaman panelden yakin oldugu icin HUD/menu icin dogru olan budur.</summary>
        public static Transform MakeQuad(Transform parent, string name, Color color,
            int renderQueue = 0, bool overlay = true)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            q.transform.SetParent(parent, false);

            var m = overlay ? CreateOverlayMaterial(color) : CreateTransparentMaterial(color);
            if (renderQueue > 0) m.renderQueue = renderQueue;

            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return q.transform;
        }

        /// <summary>Hazir bir mesh'i (yuvarlatilmis dikdortgen, ikon — bkz. <see cref="UIMesh"/>)
        /// arayuz yuzeyi olarak sahneye koyar. Collider yok, golge yok, sahnenin ustune cizer.</summary>
        public static Transform MakeShape(Transform parent, string name, Mesh mesh, Color color, int renderQueue = 0)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var m = CreateOverlayMaterial(color);
            if (renderQueue > 0) m.renderQueue = renderQueue;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Yuvarlatilmis dikdortgen yuzey — panel zemini, tus, kart, buton.
        /// Boyut MESH'e islenir (olcek 1 kalir) ki kose yaricapi her boyutta ayni gorunsun.</summary>
        public static Transform MakeRounded(Transform parent, string name, Vector2 center, Vector2 size,
            float radius, Color color, float z, int renderQueue = 0)
        {
            var t = MakeShape(parent, name, UIMesh.RoundedRect(size.x, size.y, radius), color, renderQueue);
            t.localPosition = new Vector3(center.x, center.y, z);
            return t;
        }

        /// <summary>Cerceveli yuzey: disarida vurgu renginde bir kart, uzerinde bir tik kucuk
        /// dolgu. Tasarimdaki 1-2 piksellik kenarlik boyle uretilir (shader'siz).</summary>
        public static void MakeOutlined(Transform parent, string name, Vector2 center, Vector2 size,
            float radius, Color border, Color fill, float thickness, float z, int queueBorder, int queueFill)
        {
            MakeRounded(parent, name + " Border", center, size, radius, border, z, queueBorder);
            MakeRounded(parent, name + " Fill", center, size - Vector2.one * (thickness * 2f),
                Mathf.Max(0f, radius - thickness), fill, z - 0.0005f, queueFill);
        }

        /// <summary>Dunya-uzayi yazi. Font otomatik atanir (bkz. <see cref="ApplyFont"/>);
        /// boyut <see cref="SizeText"/> ile METRE cinsinden verilir.</summary>
        public static TextMesh MakeText(Transform parent, string text, Color color,
            float worldLineHeight, TextAnchor anchor = TextAnchor.MiddleCenter, int renderQueue = 0)
        {
            var go = new GameObject("T_" + (string.IsNullOrEmpty(text) ? "empty" : text));
            go.transform.SetParent(parent, false);

            var tm = go.AddComponent<TextMesh>();
            ApplyFont(tm, renderQueue);
            tm.text = text;
            tm.color = color;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.UpperLeft
                ? TextAlignment.Left : TextAlignment.Center;
            SizeText(tm, worldLineHeight);

            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return tm;
        }

        /// <summary>Yaziyi ISTENEN SATIR YUKSEKLIGINE (metre) olcekler.
        ///
        /// TextMesh'te satir yuksekligi ~ characterSize * fontSize / 10 yerel birimdir. Projede
        /// cihazda dogrulanmis degerler (characterSize 0.1 / fontSize 60) sabit tutulup olcek
        /// transformdan veriliyor — boylece cagiran taraf "kac punto" degil "kac santim"
        /// dusunur, ki VR'da okunabilirligi belirleyen sey gorme acisi, punto degil.</summary>
        public static void SizeText(TextMesh tm, float worldLineHeight)
        {
            if (tm == null) return;
            tm.fontSize = 60;
            tm.characterSize = 0.1f;                       // -> 0.6 yerel birim satir yuksekligi
            tm.transform.localScale = Vector3.one * (worldLineHeight / 0.6f);
        }

        // --- Health Bar Gradient ---

        static Texture2D _healthGradient;

        /// <summary>
        /// Soldan saga kirmizi -> sari -> yesil giden yatay degrade dokusu (bir kez uretilir).
        /// </summary>
        public static Texture2D HealthGradientTexture
        {
            get
            {
                if (_healthGradient == null)
                {
                    const int W = 256;
                    _healthGradient = new Texture2D(W, 1, TextureFormat.RGBA32, false);
                    _healthGradient.wrapMode = TextureWrapMode.Clamp;
                    for (int x = 0; x < W; x++)
                        _healthGradient.SetPixel(x, 0, GetHealthColor(x / (float)(W - 1)));
                    _healthGradient.Apply();
                }
                return _healthGradient;
            }
        }

        /// <summary>
        /// Degrade dokulu can bari materyali olusturur. brightness &lt; 1 verilirse degradenin
        /// karartilmis hali cikar (bos kismi gosteren zemin icin).
        /// </summary>
        public static Material CreateHealthBarMaterial(float brightness = 1f)
        {
            float b = Mathf.Clamp01(brightness);
            var m = CreateLitMaterial(new Color(b, b, b, 1f));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", HealthGradientTexture);
            else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", HealthGradientTexture);
            return m;
        }

        /// <summary>
        /// Alfa ile solabilen (transparan) unlit materyal olusturur — hasar flasi gibi efektler icin.
        ///
        /// HEDEF ALFA KORUNUR — PASSTHROUGH ICIN SART. Bu oyunda gercek oda uygulamanin ALTINA
        /// kompozit ediliyor ve kare tamponunun ALFASI "burada sanal icerik var mi" demek.
        /// Duz alfa harmani (SrcAlpha/OneMinusSrcAlpha) alfa kanalini DA harmanlar:
        ///
        ///     dstA = srcA^2 + (1 - srcA) * dstA        alfa 0.5 ile:  1.00 -> 0.75
        ///
        /// Yani yari saydam bir hasar flasi ya da vinyet, sanal dunyanin USTUNE cizilse bile
        /// oranin alfasini dusurur ve GERCEK ODA efektin icinden sizar. Bombadan hasar alinca
        /// "bir sure gercek dunyayi gormek" tam olarak buydu.
        ///
        /// Cozum: alfa kanali icin AYRI harman — Zero/One, yani "hedef alfaya dokunma".
        /// RGB normal harmanlanmaya devam eder, gorunum degismez. URP/Unlit bu ozellikleri
        /// (_SrcBlendAlpha / _DstBlendAlpha) tanimliyor; tanimlamayan bir shader'a duserse
        /// kod sessizce eski davranista kalir (efekt yine cizilir, yalnizca sizinti surer).
        ///
        /// PANELLER BUNU KULLANAMAZ: URP/Unlit sahne geometrisine takilir (ZTest Always yok,
        /// bkz. <see cref="CreateOverlayMaterial"/>). Panellerin cozumu opak renk.
        /// </summary>
        public static Material CreateTransparentMaterial(Color color)
        {
            var m = new Material(SafeUnlitShader);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f); // Transparent
                m.SetFloat("_Blend", 0f);   // Alpha blend
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            PreserveDestinationAlpha(m);
            SetMaterialColor(m, color);
            return m;
        }

        /// <summary>Malzemeyi "hedef alfaya dokunma" moduna alir (bkz.
        /// <see cref="CreateTransparentMaterial"/>). Ozellikler yoksa hicbir sey yapmaz.</summary>
        public static void PreserveDestinationAlpha(Material m)
        {
            if (m == null) return;
            if (m.HasProperty("_SrcBlendAlpha"))
                m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
            if (m.HasProperty("_DstBlendAlpha"))
                m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        }

        // --- Radyal menu parcalari ---
        // NOT: WeaponSelectorUI ayni uc yardimciyi kendi icinde PRIVATE tutuyor (bu ortak surum
        // ondan sonra dogdu). Silah carki calisan ve ince ayarlanmis bir sistem oldugu icin
        // dokunulmadi; ileride oradaki kopyalar buraya baglanabilir.

        /// <summary>
        /// Ic/dis yaricapli yay (pasta dilimi) mesh'i, XY duzleminde. Iki yuzu de cizer —
        /// carkin arkasindan bakildiginda kaybolmasin.
        /// </summary>
        public static Mesh ArcMesh(float innerRadius, float outerRadius, float fromDeg, float toDeg, int segments)
        {
            var m = new Mesh();
            var v = new Vector3[(segments + 1) * 2];
            var t = new int[segments * 12];
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, (float)i / segments);
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v[i * 2] = dir * innerRadius;
                v[i * 2 + 1] = dir * outerRadius;
            }
            for (int i = 0; i < segments; i++)
            {
                int b = i * 2, k = i * 12;
                t[k] = b; t[k + 1] = b + 1; t[k + 2] = b + 2;
                t[k + 3] = b + 1; t[k + 4] = b + 3; t[k + 5] = b + 2;
                t[k + 6] = b + 2; t[k + 7] = b + 1; t[k + 8] = b;
                t[k + 9] = b + 2; t[k + 10] = b + 3; t[k + 11] = b + 1;
            }
            m.vertices = v;
            m.triangles = t;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Yari saydam, isiktan etkilenmeyen, DERINLIK YAZMAYAN overlay materyali.
        /// <see cref="CreateTransparentMaterial"/>'dan farki: renderQueue elle verilebilir, yani
        /// dilim / gobek / yazi katmanlari birbirinin ustune belirli sirayla cizilir.
        /// </summary>
        public static Material CreateOverlayMaterial(Color color, int renderQueue)
        {
            var m = CreateTransparentMaterial(color);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // iki yuz
            m.renderQueue = renderQueue;
            return m;
        }

        /// <summary>
        /// Dunya-uzayi yazi etiketi. Unity 6'da TextMesh VARSAYILAN FONTSUZ gelir — font
        /// atanmazsa yazi hic gorunmez (kol saatinde ogrenilen ders); burada bir kez halledildi.
        /// </summary>
        public static TextMesh CreateLabel(Transform parent, string text, Vector3 localPosition,
            float characterSize, int renderQueue)
        {
            var go = new GameObject("Label_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = characterSize;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Text;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var mr = go.GetComponent<MeshRenderer>();
            if (tm.font != null) mr.material = tm.font.material;
            mr.material.renderQueue = renderQueue;
            return tm;
        }

        /// <summary>
        /// Dokunun yalnizca sol [0..ratio] bolumunu gosterecek sekilde tiling ayarlar; boylece
        /// bar kisaldikca degrade "sıkışmaz", soldan itibaren acilir/kapanir.
        /// </summary>
        public static void SetGradientFill(Material m, float ratio)
        {
            var tiling = new Vector2(Mathf.Max(ratio, 0.0001f), 1f);
            if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", tiling);
            if (m.HasProperty("_MainTex")) m.SetTextureScale("_MainTex", tiling);
        }
    }
}