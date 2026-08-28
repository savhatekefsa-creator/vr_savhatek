using TMPro;
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

        static TMP_FontAsset _defaultFont;
        static Shader _textShader;
        static readonly System.Collections.Generic.Dictionary<long, Material> _fontMats =
            new System.Collections.Generic.Dictionary<long, Material>();

        /// <summary>Calisma aninda uretilen HER yazinin kullanmasi gereken TMP fontu.
        ///
        /// SIRA: once Resources/Fonts/UIFont SDF (ekibin kendi fontu — SDF asset'ini oraya
        /// birak, kod degismeden devreye girer; atlasi TURKCE karakter tablosuyla uretmeyi
        /// unutma), yoksa TMP ayarlarindaki varsayilan (LiberationSans SDF).
        ///
        /// TextMesh doneminin dersi gecerli: font atanmayan yazi editorde gorunse bile
        /// cihazda cizilmez. Tek kaynak burasi — yeni panel yazan herkes ApplyFont cagirmali.
        ///
        /// NOT: LiberationSans SDF atlasinda Turkce ozel harfler (g-breve, s-cedilla...) YOK.
        /// Calisma zamani yazilarin ASCII yazilmasi (KALIBRE DEGIL, GORUNMUYOR...) bilincli
        /// tercih; ekip fontu gelene kadar boyle kalmali.</summary>
        public static TMP_FontAsset DefaultFont
        {
            get
            {
                if (_defaultFont == null)
                {
                    _defaultFont = Resources.Load<TMP_FontAsset>("Fonts/UIFont SDF");
                    if (_defaultFont == null) _defaultFont = TMP_Settings.defaultFontAsset;
                    if (_defaultFont == null)
                        _defaultFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
                }
                return _defaultFont;
            }
        }

        /// <summary>Tum arayuz yazilarinin shader'i: SDF + ZTest Always. Eski sistemde her
        /// yazi GUI/Text Shader ile sahnenin USTUNE cizilirdi ve paneller buna yaslanir
        /// (kolokasyonda gercek duvarin sanal kopyasi cogu zaman panelden yakindir; bkz.
        /// <see cref="CreateOverlayMaterial(Color)"/>). TMP'nin Overlay varyanti ayni
        /// davranisi verir. BUILD NOTU: bu shader'a runtime'da Shader.Find ile ulasiliyor,
        /// o yuzden Always Included Shaders listesinde durmali (GUI/Text Shader gibi).</summary>
        static Shader TextShader
        {
            get
            {
                if (_textShader == null)
                    _textShader = Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
                return _textShader;
            }
        }

        /// <summary>Yaziya fontu VE paylasilan materyali atar.
        ///
        /// MATERYAL HEP PAYLASILIR: tmp.fontMaterial'i OKUMAK bile yazi basina kopya uretir
        /// (eski sistemin "6 yazi = 6 materyal" tuzaginin TMP karsiligi) — o property'ye
        /// dokunma, buradan gec. (font, kuyruk) basina TEK kopya tutulur; renderQueue > 0
        /// verilirse katman sirasi o kopyaya islenir.</summary>
        public static void ApplyFont(TMP_Text tm, int renderQueue = 0, TMP_FontAsset fontOverride = null)
        {
            if (tm == null) return;
            var font = fontOverride != null ? fontOverride : DefaultFont;
            if (font == null) return;   // TMP Essentials yoksa mevcut davranista birak

            tm.font = font;
            tm.fontSharedMaterial = SharedFontMaterial(font, renderQueue);
        }

        /// <summary>(font, renderQueue) basina tek paylasilan yazi materyali (ZTest Always).</summary>
        public static Material SharedFontMaterial(TMP_FontAsset font, int renderQueue = 0)
        {
            long key = ((long)font.GetInstanceID() << 20) ^ (uint)renderQueue;
            if (_fontMats.TryGetValue(key, out var mat) && mat != null) return mat;

            mat = new Material(font.material);
            var sh = TextShader;
            if (sh != null) mat.shader = sh;
            if (renderQueue > 0) mat.renderQueue = renderQueue;
            _fontMats[key] = mat;
            return mat;
        }

        // Domain reload kapaliyken statikler oyunlar arasi tasinir; yok edilmis materyale
        // tutunmus sozluk ikinci Play'de "MissingReference" verirdi.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _defaultFont = null;
            _textShader = null;
            _overlayShader = null;
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
        /// paneldeki YAZILAR ise duruyordu — cunku yazi materyali ustte cizen bir shader
        /// kullaniyor (bugun TMP'nin Overlay varyanti, bkz. <see cref="TextShader"/>).
        /// Sonuc: zemin yok, yazi havada. Panelin tamami ayni sinifa alindi.
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
            var sh = OverlayShader;
            if (sh == null) return CreateTransparentMaterial(color);   // olmamali; yine de duselim

            var m = new Material(sh);
            m.SetTexture("_MainTex", Texture2D.whiteTexture);
            m.SetColor("_Color", color);
            return m;
        }

        static Shader _overlayShader;

        /// <summary>Panel yuzeylerinin shader'i (GUI/Text Shader — ZTest Always).
        ///
        /// KAYNAK: Resources/UIOverlay.mat. O MATERYAL BU SHADER'I BUILD'E TASIYOR — silme,
        /// "kullanilmiyor" gorunur ama silinirse cihazda TUM panel zeminleri kaybolur.
        ///
        /// NEDEN AlwaysIncludedShaders DEGIL: shader "unity default resources" icinde yasiyor
        /// ve o listeye eklenince Android build'i unity_builtin_extra'yi yazarken cokuyordu
        /// (BufferedCacheWriter, 'm_LockCount == 0'). Resources'taki bir materyal ayni
        /// garantiyi build'i kirmadan veriyor.
        ///
        /// Shader.Find yedegi duruyor: materyal bir sekilde yuklenemezse editorde calismaya
        /// devam eder (editorde tum shader'lar yuklu, strip yok).</summary>
        static Shader OverlayShader
        {
            get
            {
                if (_overlayShader != null) return _overlayShader;
                var mat = Resources.Load<Material>("UIOverlay");
                if (mat != null) _overlayShader = mat.shader;
                if (_overlayShader == null) _overlayShader = Shader.Find("GUI/Text Shader");
                return _overlayShader;
            }
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
        public static TextMeshPro MakeText(Transform parent, string text, Color color,
            float worldLineHeight, TextAnchor anchor = TextAnchor.MiddleCenter, int renderQueue = 0)
        {
            var go = new GameObject("T_" + (string.IsNullOrEmpty(text) ? "empty" : text));
            go.transform.SetParent(parent, false);

            var tm = go.AddComponent<TextMeshPro>();
            ApplyFont(tm, renderQueue);
            ConfigureText(tm, anchor);
            tm.text = text;
            tm.color = color;
            SizeText(tm, worldLineHeight);

            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return tm;
        }

        /// <summary>Yaziyi YALNIZCA degistiyse atar.
        ///
        /// TMP'de .text atamasi ucuz DEGIL: zengin metin ayristirmasi, satir kirma, karakter
        /// basina vertex uretimi ve mesh yeniden olusturma tetikler. Eski TextMesh'te bu
        /// maliyet cok daha dusuktu, o yuzden kod her kare kosulsuz yaziyordu. TMP gocundan
        /// sonra ayni desen kare basi gereksiz mesh rebuild demek — cihazda takilma olarak
        /// hissediliyor. Ayni metinse hicbir sey yapma.</summary>
        public static void SetText(TMP_Text tm, string value)
        {
            if (tm == null) return;
            if (!string.Equals(tm.text, value, System.StringComparison.Ordinal))
                tm.text = value;
        }

        /// <summary>TMP yazisini eski TextMesh sozlesmesine oturtur: sarma YOK (satirlar
        /// eskisi gibi \n ile), tasma serbest, SIFIR boyutlu rect — boylece hiza noktalari
        /// eski anchor gibi transform'un kendisinden olculur (Center = konumda ortala,
        /// Left = konumdan saga yaz).</summary>
        public static void ConfigureText(TMP_Text tm, TextAnchor anchor)
        {
            tm.textWrappingMode = TextWrappingModes.NoWrap;
            tm.overflowMode = TextOverflowModes.Overflow;
            tm.alignment = ToAlignment(anchor);
            tm.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            tm.rectTransform.sizeDelta = Vector2.zero;
        }

        static TextAlignmentOptions ToAlignment(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.UpperLeft:   return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight:  return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft:  return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft:   return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight:  return TextAlignmentOptions.BottomRight;
                default:                     return TextAlignmentOptions.Center;
            }
        }

        /// <summary>Yaziyi ISTENEN SATIR YUKSEKLIGINE (metre) olcekler.
        ///
        /// Eski TextMesh'lerin cihazda dogrulanmis metrigi korunur: yerel satir yuksekligi
        /// 0.6 birimde sabitlenir, olcek transformdan verilir — boylece cagiran taraf
        /// "kac punto" degil "kac santim" dusunur, ki VR'da okunabilirligi belirleyen sey
        /// gorme acisi, punto degil. Punto, fontun kendi metriginden hesaplanir; ekip fontu
        /// degistiginde boyutlar kaymaz.</summary>
        public static void SizeText(TMP_Text tm, float worldLineHeight)
        {
            if (tm == null) return;
            tm.fontSize = FontSizeForLocalLineHeight(tm, 0.6f);
            tm.transform.localScale = Vector3.one * (worldLineHeight / 0.6f);
        }

        /// <summary>Yerel uzayda istenen satir yuksekligini verecek TMP punto degeri.
        /// TMP'nin 3B bileseninde 1 punto = 0.1 yerel birim; satir/punto orani fonttan
        /// fonta degistigi icin metrik fontun kendisinden okunur.</summary>
        public static float FontSizeForLocalLineHeight(TMP_Text tm, float localLineHeight)
        {
            var f = tm != null && tm.font != null ? tm.font : DefaultFont;
            float linePerPoint = 1.1f;   // metrik okunamazsa makul varsayilan
            if (f != null && f.faceInfo.pointSize > 0f)
                linePerPoint = f.faceInfo.lineHeight / f.faceInfo.pointSize;
            return localLineHeight / (0.1f * linePerPoint);
        }

        /// <summary>Genis harf araligi + soldan saga renk gecisli baslik — TEK yazi objesi.
        ///
        /// Eski surum bunu HARF BASINA AYRI OBJE ile esit adimlarla yapiyordu (TextMesh
        /// kisiti). Ilk TMP cevirisi mspace etiketiyle ayni esit-adim yerlesimi korudu ama
        /// esit HUCRE, dar harflerin (I, dotlu I) etrafinda hava birakip basligi
        /// "I HAR I TA" gibi kopuk okutuyordu. Simdi gercek harf izleme (tracking) var:
        /// her glif dogal genisliginde, aradaki EK bosluk sabit. characterSpacing degeri
        /// olculerek bulunur — ilk/son harf merkezleri eski -halfSpan..+halfSpan
        /// araligina oturur, yani basligin toplam genisligi degismez. color etiketi harf
        /// basina gecis rengini verir; yerlesim bir kez kurulur, kare basi maliyet yok.</summary>
        public static TextMeshPro MakeTitle(Transform parent, string text, Color colorA,
            Color colorB, float worldLineHeight, float worldHalfSpan, int renderQueue = 0)
        {
            var tm = MakeText(parent, "", colorA, worldLineHeight, TextAnchor.MiddleCenter, renderQueue);

            var sb = new System.Text.StringBuilder(text.Length * 16);
            int n = text.Length;
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (c == ' ') { sb.Append(' '); continue; }
                float t = n > 1 ? i / (float)(n - 1) : 0f;
                sb.Append("<color=#")
                  .Append(ColorUtility.ToHtmlStringRGB(Color.Lerp(colorA, colorB, t)))
                  .Append('>').Append(c);
            }
            tm.text = sb.ToString();
            tm.gameObject.name = "T_" + text;

            FitTitleSpan(tm, worldHalfSpan);
            return tm;
        }

        /// <summary>Izleme payini OLCEREK bulur: dogal dizilimde ilk/son harf merkezleri
        /// arasi mesafe hedeften (2*halfSpan) ne kadar eksikse, aradaki her ilerlemeye
        /// esit dagitir. Iki gecis olcum yapilir — ikincisi, characterSpacing biriminin
        /// font/olcek carpanlarina dair varsayimi dogrudan olcumle duzeltir (span,
        /// spacing'in dogrusal fonksiyonu oldugu icin tek duzeltme yeter).</summary>
        static void FitTitleSpan(TextMeshPro tm, float worldHalfSpan)
        {
            tm.ForceMeshUpdate();
            var ti = tm.textInfo;
            int ilk = -1, son = -1;
            for (int i = 0; i < ti.characterCount; i++)
                if (ti.characterInfo[i].isVisible) { if (ilk < 0) ilk = i; son = i; }
            if (son <= ilk) return;

            float dogal = MerkezAraligi(ti, ilk, son);
            float hedef = (worldHalfSpan * 2f) / Mathf.Max(tm.transform.localScale.x, 1e-6f);

            // TMP 3B icin beklenen birim: ek ilerleme (yerel) ~ spacing * fontSize * 0.001
            float tahmin = ((hedef - dogal) / (son - ilk)) / (tm.fontSize * 0.001f);
            tm.characterSpacing = tahmin;
            tm.ForceMeshUpdate();

            float olculen = MerkezAraligi(tm.textInfo, ilk, son);
            if (Mathf.Abs(olculen - dogal) > 1e-5f)
                tm.characterSpacing = tahmin * (hedef - dogal) / (olculen - dogal);
        }

        static float MerkezAraligi(TMP_TextInfo ti, int ilk, int son)
        {
            float a = (ti.characterInfo[ilk].bottomLeft.x + ti.characterInfo[ilk].bottomRight.x) * 0.5f;
            float b = (ti.characterInfo[son].bottomLeft.x + ti.characterInfo[son].bottomRight.x) * 0.5f;
            return b - a;
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
        /// Alfa ile solabilen (transparan) unlit materyal olusturur — insa izgarasi, hayalet
        /// prop, hasar flasi gibi her sey icin.
        ///
        /// ALFA KANALI AYRI HARMANLANIR — PASSTHROUGH ICIN SART. Bu oyunda gercek oda uygulamanin
        /// ALTINA kompozit ediliyor ve kare tamponunun ALFASI "burada ne kadar sanal icerik var"
        /// demek. Duz alfa harmani (SrcAlpha/OneMinusSrcAlpha) alfa kanalini DA carpar:
        ///
        ///     dstA = srcA^2 + (1 - srcA) * dstA        alfa 0.5 ile:  1.00 -> 0.75
        ///
        /// yani yari saydam bir hasar flasi, sanal dunyanin USTUNE cizilse bile oranin alfasini
        /// DUSURUR ve gercek oda efektin icinden sizar. Bombadan hasar alinca "bir sure gercek
        /// dunyayi gormek" tam olarak buydu.
        ///
        /// Dogru harman "uzerine" (over) formulu — One / OneMinusSrcAlpha:
        ///
        ///     dstA = srcA + (1 - srcA) * dstA
        ///
        /// Alfa BUNUNLA ASLA DUSMEZ (opak dunyanin ustunde 1 kalir, sizinti kapali) ama bos bir
        /// tampona cizilince ARTAR — yani icerik gercek odanin uzerinde gorunur.
        ///
        /// ONCEKI COZUM (Zero/One = "hedef alfaya hic dokunma") sizintiyi kapatiyordu ama isin
        /// ikinci yarisini kiriyordu: passthrough acikken kamera alfa 0'a temizliyor, saydam
        /// hicbir sey alfa yazmadigi icin tampon 0'da kaliyor ve kompozitor SADECE gercek odayi
        /// gosteriyordu. INSA MODUNDA GERCEK DUNYAYA GECINCE ZEMINDEKI IZGARANIN, HAYALETIN VE
        /// ISININ KAYBOLMASININ SEBEBI BUYDU.
        ///
        /// URP/Unlit bu ozellikleri (_SrcBlendAlpha / _DstBlendAlpha) tanimliyor; tanimlamayan
        /// bir shader'a duserse kod sessizce eski davranista kalir.
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
            CompositeAlphaOver(m);
            SetMaterialColor(m, color);
            return m;
        }

        /// <summary>Alfa kanalini "uzerine" (over) harmanina alir: dstA = srcA + (1-srcA)*dstA
        /// (bkz. <see cref="CreateTransparentMaterial"/>). Ozellikler yoksa hicbir sey yapmaz.</summary>
        public static void CompositeAlphaOver(Material m)
        {
            if (m == null) return;
            if (m.HasProperty("_SrcBlendAlpha"))
                m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha"))
                m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        // --- Halka / yay parcalari ---
        // Eskiden silah carki bu uc yardimcinin kendi PRIVATE kopyasini tasiyordu; cark
        // kaldirilinca (yerine WeaponBeltUI geldi) kopyalar da gitti, tek kaynak burasi.

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
        /// Dunya-uzayi yazi etiketi. Boyut eski TextMesh sozlesmesiyle uyumlu: characterSize,
        /// eski (characterSize x punto 64 / 10) yerel satir yuksekligine cevrilir — kol saati
        /// gibi cagiranlarin yerlesimi degismesin. Materyal artik etiket basina KOPYALANMAZ,
        /// (font, kuyruk) basina paylasilir (bkz. <see cref="SharedFontMaterial"/>).
        /// </summary>
        public static TextMeshPro CreateLabel(Transform parent, string text, Vector3 localPosition,
            float characterSize, int renderQueue)
        {
            var go = new GameObject("Label_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var tm = go.AddComponent<TextMeshPro>();
            ApplyFont(tm, renderQueue);
            ConfigureText(tm, TextAnchor.MiddleCenter);
            tm.text = text;
            tm.color = Text;
            tm.fontSize = FontSizeForLocalLineHeight(tm, characterSize * 6.4f);

            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
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