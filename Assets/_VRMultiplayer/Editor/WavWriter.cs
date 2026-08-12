using System.IO;
using System.Text;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Uretilen seslerin diske yazilmasi: 16-bit PCM mono WAV.
    ///
    /// Shared for the same reason as <see cref="ProceduralNoise"/>: the header is a fixed
    /// sequence of sizes and offsets where a single wrong field produces a file Unity imports
    /// as silence or as noise, and debugging that twice is once too many.
    /// </summary>
    public static class WavWriter
    {
        public static byte[] ToWav(float[] samples, int sampleRate)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLen);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);                       // fmt blok boyu
                w.Write((short)1);                 // PCM
                w.Write((short)1);                 // mono
                w.Write(sampleRate);
                w.Write(sampleRate * 2);           // byte/sn
                w.Write((short)2);                 // blok hizalama
                w.Write((short)16);                // bit/ornek
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLen);
                foreach (float f in samples)
                    w.Write((short)(Mathf.Clamp(f, -1f, 1f) * 32767f));
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Tepe degerini <paramref name="peak"/>'e getirir. Sessiz diziye dokunmaz.</summary>
        public static void Normalize(float[] s, float peak)
        {
            float max = 0f;
            for (int i = 0; i < s.Length; i++) max = Mathf.Max(max, Mathf.Abs(s[i]));
            if (max <= 1e-6f) return;
            float k = peak / max;
            for (int i = 0; i < s.Length; i++) s[i] *= k;
        }
    }
}
