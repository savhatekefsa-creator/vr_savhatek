using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Dunya-uzayinda duran kucuk uyari ucgeni (⚠): kehribar govde + koyu cerceve ve unlem.
    /// Sahne geometrisinin USTUNE cizer, yani isaret bir duvarin icinde kalsa bile gorunur.
    ///
    /// NEDEN IKI KATMAN: "her zaman ustte ciz" davranisi UITheme'in overlay malzemesinden
    /// geliyor ve o malzeme "GUI/Text Shader" kullaniyor — bu shader dokunun yalnizca ALFA
    /// kanalini okur, RGB'yi <c>_Color</c>'dan alir (UITheme.CreateOverlayMaterial'daki nota
    /// bakiniz). Yani tek dokuya iki renk sigdirmak mumkun degil. Cozum: sekiller ALFA
    /// maskesi olarak uretilir, renk katman basina verilir.
    ///   Katman 1 (arkada) : dolu ucgen, kehribar
    ///   Katman 2 (onde)   : cerceve + unlem, koyu
    /// Ikinci katmani ayri tutmak bedava bir kazanc da veriyor: cerceve ile unlem AYNI
    /// dokuda oldugu icin ek bir cizim maliyeti cikarmiyor.
    ///
    /// Dokular STATIK ve paylasimli: kac silah ayni anda uyari gosterirse gostersin bellekte
    /// tek kopya durur.
    /// </summary>
    public class WarningIcon : MonoBehaviour
    {
        /// <summary>Ikonun kenar uzunlugu (m). Degistirmek icin <see cref="SetSize"/>.</summary>
        public float size = 0.04f;

        static readonly Color BodyColor  = new Color(1f, 0.72f, 0.05f, 1f);   // kehribar
        static readonly Color GlyphColor = new Color(0.10f, 0.07f, 0.01f, 1f); // neredeyse siyah

        /// <summary>Doku cozunurlugu. 128 bu olcekte (3-5 cm, ~0.5-1 m mesafe) fazlasiyla
        /// yeterli; kenar yumusatma dokuya islendigi icin MSAA'ya bagimli degiliz.</summary>
        const int TexSize = 128;

        /// <summary>Cizim sirasi: govde altta, cerceve+unlem ustte.</summary>
        const int BodyQueue = 4000;
        const int GlyphQueue = 4001;

        static Texture2D _bodyTex, _glyphTex;

        Material _bodyMat, _glyphMat;

        void Awake()
        {
            _bodyMat = BuildLayer("Govde", BodyTexture, BodyColor, BodyQueue);
            _glyphMat = BuildLayer("Unlem", GlyphTexture, GlyphColor, GlyphQueue);
        }

        Material BuildLayer(string name, Texture2D tex, Color color, int queue)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);   // sahne mermi raycast'lerine gorunmesin
            q.transform.SetParent(transform, false);
            q.transform.localScale = Vector3.one * size;

            var m = UITheme.CreateOverlayMaterial(color);
            m.renderQueue = queue;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);

            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return m;
        }

        /// <summary>Ikonun tamaminin opakligi (yanip sonme buradan surulur).</summary>
        public void SetAlpha(float a)
        {
            a = Mathf.Clamp01(a);
            if (_bodyMat != null)
                UITheme.SetMaterialColor(_bodyMat, new Color(BodyColor.r, BodyColor.g, BodyColor.b, a));
            if (_glyphMat != null)
                UITheme.SetMaterialColor(_glyphMat, new Color(GlyphColor.r, GlyphColor.g, GlyphColor.b, a));
        }

        /// <summary>Kenar uzunlugunu (m) degistirir.</summary>
        public void SetSize(float s)
        {
            size = s;
            foreach (Transform t in transform) t.localScale = Vector3.one * s;
        }

        // ------------------------------------------------------------- dokular

        static Texture2D BodyTexture => _bodyTex != null ? _bodyTex : (_bodyTex = BuildBody());
        static Texture2D GlyphTexture => _glyphTex != null ? _glyphTex : (_glyphTex = BuildGlyph());

        // Ucgenin koseleri (normalize doku koordinati, y yukari). CCW sirali.
        static readonly Vector2 TriB = new Vector2(0.045f, 0.13f);   // sol alt
        static readonly Vector2 TriC = new Vector2(0.955f, 0.13f);   // sag alt
        static readonly Vector2 TriA = new Vector2(0.500f, 0.95f);   // tepe

        /// <summary>Kose yuvarlatma yaricapi (normalize).</summary>
        const float CornerRadius = 0.075f;

        /// <summary>Koyu cercevenin kalinligi (normalize).</summary>
        const float RimWidth = 0.055f;

        /// <summary>Kenar yumusatma yari-genisligi (normalize). 1.5 piksel.</summary>
        const float AA = 1.5f / TexSize;

        static Texture2D NewTex()
        {
            return new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        static Texture2D BuildBody()
        {
            var t = NewTex();
            for (int y = 0; y < TexSize; y++)
            for (int x = 0; x < TexSize; x++)
            {
                float d = RoundedTriangleSd(Uv(x, y));
                // d < 0 iceride. Kenarda AA bandi.
                float a = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(-AA, AA, d));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            t.Apply();
            return t;
        }

        static Texture2D BuildGlyph()
        {
            // Unlem olculeri: cubuk ile nokta, ucgenin ic alanina yerlesecek sekilde. Cubugun
            // ustu 0.68'de bitiyor — orada ucgenin genisligi ~0.30, cubuk yarim kalinligi 0.05,
            // yani iki yanda pay kaliyor ve tepeye yapismiyor.
            Vector2 barTop = new Vector2(0.5f, 0.66f);
            Vector2 barBottom = new Vector2(0.5f, 0.42f);
            const float barRadius = 0.050f;
            Vector2 dotCentre = new Vector2(0.5f, 0.305f);
            const float dotRadius = 0.056f;

            var t = NewTex();
            for (int y = 0; y < TexSize; y++)
            for (int x = 0; x < TexSize; x++)
            {
                Vector2 p = Uv(x, y);

                float glyph = Mathf.Min(
                    SdSegment(p, barBottom, barTop) - barRadius,
                    (p - dotCentre).magnitude - dotRadius);
                float glyphA = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(-AA, AA, glyph));

                // Cerceve: ucgenin ic kenarina yaslanan serit. Iki kosul birden — ucgenin
                // ICINDE ol (body < 0) ve kenara RimWidth'ten yakin ol.
                float body = RoundedTriangleSd(p);
                float rimA = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(-AA, AA, body)) *
                             Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-RimWidth - AA, -RimWidth + AA, body));

                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Max(glyphA, rimA)));
            }
            t.Apply();
            return t;
        }

        /// <summary>Piksel merkezinin normalize koordinati (y yukari).</summary>
        static Vector2 Uv(int x, int y) => new Vector2((x + 0.5f) / TexSize, (y + 0.5f) / TexSize);

        /// <summary>Yuvarlatilmis kose ucgenin isaretli mesafesi; ICERIDE NEGATIF.
        /// Yontem: ucgeni CornerRadius kadar ICERI cek, sonra o kucuk ucgene olan gercek
        /// Oklid mesafesinden yaricapi dus. Kucuk sekli yaricap kadar SISIRMEK, orijinal
        /// ucgenin koselerini yuvarlamakla ayni seydir.</summary>
        static float RoundedTriangleSd(Vector2 p)
        {
            Vector2 g = (TriA + TriB + TriC) / 3f;

            // Ic tegetin (inradius) yaklasigi: agirlik merkezinin kenarlara en kisa uzakligi.
            float ri = Mathf.Min(SdSegment(g, TriB, TriC),
                       Mathf.Min(SdSegment(g, TriC, TriA), SdSegment(g, TriA, TriB)));
            float s = Mathf.Max(0.01f, (ri - CornerRadius) / ri);

            Vector2 a = g + (TriA - g) * s;
            Vector2 b = g + (TriB - g) * s;
            Vector2 c = g + (TriC - g) * s;

            float d = Mathf.Min(SdSegment(p, a, b), Mathf.Min(SdSegment(p, b, c), SdSegment(p, c, a)));
            return (InTriangle(p, a, b, c) ? -d : d) - CornerRadius;
        }

        /// <summary>Noktanin [a,b] PARCASINA (dogruya degil) uzakligi.</summary>
        static float SdSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float len = Vector2.Dot(ba, ba);
            float h = len < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(pa, ba) / len);
            return (pa - ba * h).magnitude;
        }

        static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a);
            float d2 = Cross(c - b, p - b);
            float d3 = Cross(a - c, p - c);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);   // hepsi ayni isaretteyse iceride
        }
    }
}
