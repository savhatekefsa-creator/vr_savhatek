using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Dunya-uzayi arayuz icin mesh uretici: yuvarlatilmis dikdortgenler ve ikonlar.
    ///
    /// NEDEN MESH, NEDEN DOKU DEGIL: yuvarlak koseyi bir dokuyla yapmak icin ya 9-slice
    /// gerekir (Quad'da yok) ya da her boyut icin ayri doku; tek dokuyu Quad'a gerdiginde
    /// koseler boy/ene gore eziliyor. Mesh'te yaricap MUTLAK kalir — 60x50'lik tusla
    /// 914x88'lik isim alaninin kose yuvarlakligi ayni gorunur, tasarimdaki gibi.
    ///
    /// NEDEN IKONLAR MESH: font DYNAMIC, yani glifler isletim sisteminin fontundan geliyor.
    /// Windows'ta ⚡ (U+26A1) ve ⌫ (U+232B) var, Android/Quest fallback fontunda olmayabilir —
    /// o zaman cihazda bos kutu cikardi. Ucgen/poligon olarak cizince her yerde ayni.
    ///
    /// Mesh'ler BOYUTA gore onbelleklenir: 40 tusun hepsi ayni boyutta oldugu icin tek mesh
    /// paylasilir.
    /// </summary>
    public static class UIMesh
    {
        static readonly Dictionary<long, Mesh> _rounded = new Dictionary<long, Mesh>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _rounded.Clear();

        /// <summary>Merkezi orijinde, XY duzleminde yuvarlatilmis dikdortgen.</summary>
        /// <param name="w">Genislik (metre).</param>
        /// <param name="h">Yukseklik (metre).</param>
        /// <param name="radius">Kose yaricapi (metre). En kisa kenarin yarisiyla sinirlanir.</param>
        /// <param name="seg">Kose basina yay bolutu. 4 VR'da yeterince puruzsuz.</param>
        public static Mesh RoundedRect(float w, float h, float radius, int seg = 4)
        {
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(w, h) * 0.5f);
            seg = Mathf.Clamp(seg, 1, 12);

            // Onbellek anahtari: 0.1 mm cozunurluk yeterli, gereksiz kopya uretmez.
            long key = (long)Mathf.RoundToInt(w * 10000f) * 100000000L
                     + (long)Mathf.RoundToInt(h * 10000f) * 1000L
                     + Mathf.RoundToInt(radius * 10000f) * 13L + seg;
            if (_rounded.TryGetValue(key, out var cached) && cached != null) return cached;

            float hw = w * 0.5f, hh = h * 0.5f;
            var ring = new List<Vector3>((seg + 1) * 4);

            // Koseler: sag-ust, sol-ust, sol-alt, sag-alt (saat yonu tersi)
            AddCorner(ring, new Vector2(hw - radius,  hh - radius), radius,   0f,  90f, seg);
            AddCorner(ring, new Vector2(-hw + radius, hh - radius), radius,  90f, 180f, seg);
            AddCorner(ring, new Vector2(-hw + radius,-hh + radius), radius, 180f, 270f, seg);
            AddCorner(ring, new Vector2(hw - radius, -hh + radius), radius, 270f, 360f, seg);

            // Merkezden yelpaze: dikdortgen disbukey oldugu icin dogru ve en ucuz nirengi.
            var verts = new Vector3[ring.Count + 1];
            var uvs = new Vector2[verts.Length];
            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < ring.Count; i++)
            {
                verts[i + 1] = ring[i];
                uvs[i + 1] = new Vector2(ring[i].x / w + 0.5f, ring[i].y / h + 0.5f);
            }

            var tris = new int[ring.Count * 3];
            for (int i = 0; i < ring.Count; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % ring.Count + 1;
            }

            var m = new Mesh { name = $"RoundedRect {w:F3}x{h:F3}r{radius:F3}" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            _rounded[key] = m;
            return m;
        }

        static void AddCorner(List<Vector3> ring, Vector2 c, float r, float a0, float a1, int seg)
        {
            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)seg) * Mathf.Deg2Rad;
                ring.Add(new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r, 0f));
            }
        }

        // ------------------------------------------------------------------ ikonlar
        // Hepsi 1x1 kutuya sigacak sekilde tanimli; olcek transformdan verilir.

        /// <summary>Oynat ucgeni (OYUNA BASLA).</summary>
        public static Mesh Play() => FromPolygon("IconPlay", new[]
        {
            new Vector2(-0.35f,  0.5f),
            new Vector2( 0.5f,   0f),
            new Vector2(-0.35f, -0.5f),
        });

        /// <summary>Simsek (RASTGELE AD). Icbukey oldugu icin ucgenleri elle veriliyor —
        /// merkez yelpazesi icbukey poligonda yanlis yuzey uretir.</summary>
        public static Mesh Bolt()
        {
            var v = new[]
            {
                new Vector3( 0.10f,  0.50f, 0f), // 0 ust
                new Vector3(-0.34f, -0.04f, 0f), // 1 sol bel
                new Vector3(-0.02f, -0.04f, 0f), // 2 ic sol
                new Vector3(-0.10f, -0.50f, 0f), // 3 alt
                new Vector3( 0.34f,  0.06f, 0f), // 4 sag bel
                new Vector3( 0.02f,  0.06f, 0f), // 5 ic sag
            };
            var t = new[] { 0, 1, 2,  0, 2, 5,  3, 5, 4,  3, 2, 5 };
            return Build("IconBolt", v, t);
        }

        /// <summary>Yon oku: govde + baslik, +X'e bakar. Icbukey oldugu icin ucgenleri elle
        /// veriliyor (merkez yelpazesi icbukey poligonda yanlis yuzey uretir).</summary>
        public static Mesh Arrow()
        {
            var v = new[]
            {
                new Vector3(-0.50f, -0.14f, 0f), // 0 govde
                new Vector3( 0.05f, -0.14f, 0f), // 1
                new Vector3( 0.05f,  0.14f, 0f), // 2
                new Vector3(-0.50f,  0.14f, 0f), // 3
                new Vector3( 0.00f, -0.34f, 0f), // 4 baslik
                new Vector3( 0.50f,  0.00f, 0f), // 5
                new Vector3( 0.00f,  0.34f, 0f), // 6
            };
            var t = new[] { 0, 1, 2,  0, 2, 3,  4, 5, 6 };
            return Build("IconArrow", v, t);
        }

        /// <summary>Geri silme oku (SIL): sola bakan besgen.</summary>
        public static Mesh Backspace() => FromPolygon("IconBackspace", new[]
        {
            new Vector2(-0.50f,  0.00f),
            new Vector2(-0.14f,  0.42f),
            new Vector2( 0.50f,  0.42f),
            new Vector2( 0.50f, -0.42f),
            new Vector2(-0.14f, -0.42f),
        });

        /// <summary>Disbukey poligondan merkez yelpazesi.</summary>
        static Mesh FromPolygon(string name, Vector2[] pts)
        {
            var verts = new Vector3[pts.Length + 1];
            Vector2 c = Vector2.zero;
            foreach (var p in pts) c += p;
            c /= pts.Length;

            verts[0] = c;
            for (int i = 0; i < pts.Length; i++) verts[i + 1] = pts[i];

            var tris = new int[pts.Length * 3];
            for (int i = 0; i < pts.Length; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % pts.Length + 1;
            }
            return Build(name, verts, tris);
        }

        static Mesh Build(string name, Vector3[] verts, int[] tris)
        {
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
