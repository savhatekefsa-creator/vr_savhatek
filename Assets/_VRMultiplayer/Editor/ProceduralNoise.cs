using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Uretilen dokularin ortak gurultu matematigi.
    ///
    /// PULLED OUT OF ThemeSetup when a second tool needed it. Copying these five methods would
    /// have been the easy move and the wrong one: they are not five independent helpers, they
    /// are one recipe — tileable base, octave stack, range stretch, ridge fold, threshold — and
    /// each step only behaves as documented because the step before it did. A second copy drifts
    /// on the first tweak, and the symptom (a texture that tiles with a seam, or a threshold
    /// that never fires) shows up nowhere near the edit.
    /// </summary>
    public static class ProceduralNoise
    {
        /// <summary>
        /// Smooth 0..1 threshold — 0 below <paramref name="edge0"/>, 1 above
        /// <paramref name="edge1"/>, eased in between.
        ///
        /// NOT <see cref="Mathf.SmoothStep"/>, and the difference has already cost one flat
        /// texture. Unity's SmoothStep(from, to, t) is an INTERPOLATION: it returns a value
        /// between from and to, so SmoothStep(0.86, 0.99, x) never returns less than 0.86 and a
        /// "threshold" written with it blends its layer over the ENTIRE image at near-full
        /// strength. What every caller here wants is the GLSL meaning: how far past this edge
        /// is x.
        /// </summary>
        public static float Step01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(1e-5f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Seamless Perlin: the blend of four copies shifted by exactly the domain width.
        ///
        /// <see cref="Mathf.PerlinNoise"/> does not repeat on its own. At u=0 this blend is
        /// entirely the unshifted copy and at u=1 entirely the shifted one, and those two sample
        /// the same point — so the left edge and the right edge agree by construction. A texture
        /// that does not tile shows a seam at every repeat, which on a floor is every couple of
        /// metres, forever.
        /// </summary>
        public static float TileNoise(float u, float v, float freq, float seed)
        {
            float x = u * freq, y = v * freq;
            float a = Mathf.PerlinNoise(x + seed, y + seed);
            float b = Mathf.PerlinNoise(x - freq + seed, y + seed);
            float c = Mathf.PerlinNoise(x + seed, y - freq + seed);
            float d = Mathf.PerlinNoise(x - freq + seed, y - freq + seed);
            return a * (1f - u) * (1f - v) + b * u * (1f - v)
                 + c * (1f - u) * v + d * u * v;
        }

        /// <summary>Kesintisizligi koruyan cok katmanli gurultu.</summary>
        public static float Fbm(float u, float v, float freq, int octaves, float seed)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += TileNoise(u, v, freq, seed + i * 37f) * amp;
                norm += amp;
                freq *= 2f;
                amp *= 0.5f;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>
        /// An fBm field stretched to fill 0..1.
        ///
        /// THE STRETCH IS THE POINT. Raw fBm has a narrow, parameter-dependent spread — both the
        /// octave average and the four-copy tiling blend pull values toward the middle — so a
        /// threshold written as a constant means something different for every frequency and
        /// octave count, and usually means "never". Normalising first makes the thresholds at
        /// the call site read as what they look like: fractions of the range that occurs.
        /// </summary>
        public static float[] NoiseField(int size, float freq, int octaves, float seed)
        {
            var f = new float[size * size];
            float min = float.MaxValue, max = float.MinValue;

            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float n = Fbm(x / (float)size, v, freq, octaves, seed);
                    f[y * size + x] = n;
                    if (n < min) min = n;
                    if (n > max) max = n;
                }
            }

            return Normalize(f, min, max);
        }

        /// <summary>
        /// Ridged noise, normalised: peaks where the underlying field crosses its own middle,
        /// which is what turns blobs into the connected thin lines a crack pattern needs.
        /// </summary>
        public static float[] RidgeField(int size, float freq, int octaves, float seed)
        {
            var f = NoiseField(size, freq, octaves, seed);
            float min = float.MaxValue, max = float.MinValue;

            for (int i = 0; i < f.Length; i++)
            {
                float r = 1f - Mathf.Abs(f[i] * 2f - 1f);
                f[i] = r;
                if (r < min) min = r;
                if (r > max) max = r;
            }

            return Normalize(f, min, max);
        }

        static float[] Normalize(float[] f, float min, float max)
        {
            float span = Mathf.Max(1e-5f, max - min);
            for (int i = 0; i < f.Length; i++) f[i] = (f[i] - min) / span;
            return f;
        }
    }
}
