using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HydroShiftRecorder
{
    /// <summary>
    /// The OLED "true black" look, defined once and used two ways:
    /// <see cref="Apply"/> renders it on the live preview in the tuner window, and <see cref="Filter"/>
    /// writes the identical maths as an ffmpeg filter for the saved video, so what you see is what you get.
    ///
    /// Per colour channel:  out = 255 * ((in/255 - black) / (1 - black)) ^ shadows    (clamped to 0..1)
    /// then a luma-preserving saturation change (Rec.709 weights).
    /// </summary>
    public class OledParams
    {
        /// <summary>0..60 (of 255). Every channel at or below this becomes 0, so the pixel switches off.</summary>
        public double Black;
        /// <summary>1.0..2.0. Gamma on what's left: higher fades dark areas into black more gradually and deeper.</summary>
        public double Shadows = 1.2;
        /// <summary>0.8..1.5. Saturation multiplier.</summary>
        public double Color = 1.08;
        /// <summary>Light temporal denoise first, so compression noise doesn't flicker in the blacks.</summary>
        public bool Cleanup = true;

        public OledParams Clone() { return (OledParams)MemberwiseClone(); }
    }

    public static class Oled
    {
        public const double MaxBlack = 60;
        const double Wr = 0.2126, Wg = 0.7152, Wb = 0.0722;

        // ------------------------------------------------------------------ ffmpeg side

        /// <summary>
        /// Filter-chain fragment (ends with a comma) that applies the look. Output is rgb24; the caller converts to YUV.
        /// Single-quoted expressions, so it can sit inside a double-quoted -filter_complex.
        /// </summary>
        public static string Filter(OledParams p)
        {
            var b = Tools.Clamp(p.Black, 0, MaxBlack) / 255.0;
            var e = string.Format(Tools.Inv, "255*pow(clip((val/255-{0:0.0######})/{1:0.0######},0,1),{2:0.0####})", b, 1 - b, p.Shadows);
            var m = Matrix(p.Color);
            var cm = string.Format(Tools.Inv,
                "colorchannelmixer=rr={0:0.#####}:rg={1:0.#####}:rb={2:0.#####}:gr={3:0.#####}:gg={4:0.#####}:gb={5:0.#####}:br={6:0.#####}:bg={7:0.#####}:bb={8:0.#####}",
                m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);
            return (p.Cleanup ? "hqdn3d=1.5:1.5:4:4," : "") +
                   "format=rgb24,lutrgb=r='" + e + "':g='" + e + "':b='" + e + "'," + cm + ",";
        }

        static double[] Matrix(double s)
        {
            return new[]
            {
                Wr + s * (1 - Wr), Wg * (1 - s),       Wb * (1 - s),
                Wr * (1 - s),       Wg + s * (1 - Wg), Wb * (1 - s),
                Wr * (1 - s),       Wg * (1 - s),       Wb + s * (1 - Wb),
            };
        }

        // ------------------------------------------------------------------ preview side

        public static byte[] Lut(OledParams p)
        {
            var b = Tools.Clamp(p.Black, 0, MaxBlack) / 255.0;
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                var x = Tools.Clamp((i / 255.0 - b) / (1 - b), 0, 1);
                lut[i] = (byte)Math.Round(255 * Math.Pow(x, p.Shadows));
            }
            return lut;
        }

        /// <summary>
        /// Renders the look from an RGB24 frame into a BGRA32 buffer (for a WriteableBitmap).
        /// Returns the fraction of pixels that end up fully off.
        /// </summary>
        public static double Apply(byte[] rgb, byte[] bgra, OledParams p)
        {
            var lut = Lut(p);
            var m = Matrix(p.Color);
            float m0 = (float)m[0], m1 = (float)m[1], m2 = (float)m[2], m3 = (float)m[3], m4 = (float)m[4],
                  m5 = (float)m[5], m6 = (float)m[6], m7 = (float)m[7], m8 = (float)m[8];
            int n = rgb.Length / 3, off = 0;
            for (int i = 0, j = 0, k = 0; i < n; i++, j += 3, k += 4)
            {
                float r = lut[rgb[j]], g = lut[rgb[j + 1]], b = lut[rgb[j + 2]];
                int R = Byte(m0 * r + m1 * g + m2 * b), G = Byte(m3 * r + m4 * g + m5 * b), B = Byte(m6 * r + m7 * g + m8 * b);
                bgra[k] = (byte)B; bgra[k + 1] = (byte)G; bgra[k + 2] = (byte)R; bgra[k + 3] = 255;
                if (R <= 1 && G <= 1 && B <= 1) off++;
            }
            return n > 0 ? (double)off / n : 0;
        }

        static int Byte(float v) { int i = (int)(v + 0.5f); return i < 0 ? 0 : (i > 255 ? 255 : i); }

        /// <summary>Plain RGB24 → BGRA32, for showing the original side by side.</summary>
        public static void ToBgra(byte[] rgb, byte[] bgra)
        {
            for (int j = 0, k = 0; j + 2 < rgb.Length; j += 3, k += 4)
            {
                bgra[k] = rgb[j + 2]; bgra[k + 1] = rgb[j + 1]; bgra[k + 2] = rgb[j]; bgra[k + 3] = 255;
            }
        }

        /// <summary>Fraction of pixels already fully off in the original.</summary>
        public static double OffFraction(byte[] rgb)
        {
            int n = rgb.Length / 3, off = 0;
            for (int j = 0; j + 2 < rgb.Length; j += 3)
                if (rgb[j] <= 1 && rgb[j + 1] <= 1 && rgb[j + 2] <= 1) off++;
            return n > 0 ? (double)off / n : 0;
        }

        /// <summary>
        /// Guesses the black level for a video from a few frames. Most wallpapers made for normal screens have a
        /// dark-grey "haze" instead of black; that haze shows up as a tight cluster of dark values, so the level is
        /// set just above it. If the frames have no dark cluster (a bright scene), only the very darkest pixels
        /// are crushed, so nothing visible is lost.
        /// </summary>
        public static double AutoBlack(IList<byte[]> frames)
        {
            var hist = new long[256];
            long total = 0;
            foreach (var f in frames)
                for (int j = 0; j + 2 < f.Length; j += 3 * 7)
                {
                    int v = Math.Max(f[j], Math.Max(f[j + 1], f[j + 2]));
                    hist[v]++; total++;
                }
            if (total == 0) return 0;
            double p2 = Percentile(hist, total, 0.02), p30 = Percentile(hist, total, 0.30);
            var b = p30 <= 48 ? p30 + 3 : Math.Min(p2, 12);
            return Math.Round(Tools.Clamp(b, 0, MaxBlack));
        }

        static double Percentile(long[] hist, long total, double q)
        {
            // same "linear" definition numpy uses, so it matches the prototype
            double pos = q * (total - 1);
            long lo = (long)Math.Floor(pos);
            double frac = pos - lo;
            int a = -1, b = -1;
            long seen = 0;
            for (int v = 0; v < 256; v++)
            {
                seen += hist[v];
                if (a < 0 && seen > lo) a = v;
                if (seen > lo + 1) { b = v; break; }
            }
            if (b < 0) b = a;
            return a + (b - a) * frac;
        }

        // ------------------------------------------------------------------ frames for the preview

        /// <summary>Grabs one frame at <paramref name="seconds"/>, fitted to the HydroShift shape, as RGB24 at w×h.</summary>
        public static byte[] GrabFrame(string ffmpeg, string file, double seconds, string fit, int w, int h)
        {
            var args = "-v error -ss " + Tools.Num(Math.Max(0, seconds)) + " -i " + Tools.Q(file) +
                       " -frames:v 1 -an -vf \"" + fit + ",scale=" + w + ":" + h + ":flags=area,format=rgb24\" -f rawvideo -";
            var si = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(si))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                var ms = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                var data = ms.ToArray();
                return data.Length == w * h * 3 ? data : null;
            }
        }
    }
}
