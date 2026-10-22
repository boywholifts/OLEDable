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
    /// 1. Per colour channel tone curve (see <see cref="Tone"/>): black level crush, shadow depth,
    ///    brightness and highlight boost. Black always stays black.
    /// 2. Luma-preserving saturation (Rec.709 weights).
    /// 3. Optional glow: the bright parts are blurred (3-pass box blur, ffmpeg's boxblur ported 1:1)
    ///    and screen-blended back on top, then the faint tail of the halo is trimmed back to true black.
    /// </summary>
    public class OledParams
    {
        /// <summary>0..60 (of 255). Every channel at or below this becomes 0, so the pixel switches off.</summary>
        public double Black;
        /// <summary>1.0..2.0. Gamma on what's left: higher fades dark areas into black more gradually and deeper.</summary>
        public double Shadows = 1.2;
        /// <summary>0..1. Lifts every lit pixel, most in the dim-to-mid range, without clipping whites.</summary>
        public double Bright;
        /// <summary>0..1. Pushes the brightest parts (lights, neon) harder; may clip them to full brightness.</summary>
        public double Highlights;
        /// <summary>0.8..1.5. Saturation multiplier.</summary>
        public double Color = 1.08;
        /// <summary>0..1. Strength of the bloom around bright areas. 0 = off.</summary>
        public double Glow;
        /// <summary>Glow blur radius in pixels at full 2288 × 1048 resolution (6..48).</summary>
        public double GlowSize = 20;
        /// <summary>Light temporal denoise first, so compression noise doesn't flicker in the blacks.</summary>
        public bool Cleanup = true;

        public bool HasGlow { get { return Glow >= 0.005; } }
        public OledParams Clone() { return (OledParams)MemberwiseClone(); }
    }

    public static class Oled
    {
        public const double MaxBlack = 60;
        const double Wr = 0.2126, Wg = 0.7152, Wb = 0.0722;
        const int GlowFrom = 64;      // only channel values above this (25%) feed the glow
        const int HaloFloor = 3;      // halo values below this go back to true black

        // ------------------------------------------------------------------ shared maths

        static double BrightGain(OledParams p) { return 1 + 0.8 * Tools.Clamp(p.Bright, 0, 1); }
        static double HighGain(OledParams p) { return 0.6 * Tools.Clamp(p.Highlights, 0, 1); }
        static double GlowGain(OledParams p) { return 1.6 * Tools.Clamp(p.Glow, 0, 1); }

        /// <summary>The tone curve on 0..1. Mirrored exactly by the ffmpeg expression in <see cref="Filter"/>.</summary>
        public static double Tone(double x, OledParams p)
        {
            double b = Tools.Clamp(p.Black, 0, MaxBlack) / 255.0;
            x = Math.Pow(Tools.Clamp((x - b) / (1 - b), 0, 1), p.Shadows);
            double g = BrightGain(p);
            x = x * g / (1 + (g - 1) * x);                          // soft lift: 0 stays 0, 1 stays 1
            double t = Tools.Clamp((x - 0.35) / 0.65, 0, 1);
            return Math.Min(1, x * (1 + HighGain(p) * t * t * (3 - 2 * t)));   // boost only the top end
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

        static int GlowRadius(OledParams p, double scale)
        {
            return Math.Max(1, (int)Math.Round(Tools.Clamp(p.GlowSize, 6, 48) * scale));
        }

        public static byte[] Lut(OledParams p)
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(255 * Tone(i / 255.0, p));
            return lut;
        }

        static byte[] GlowLut(OledParams p)
        {
            var lut = new byte[256];
            double k = GlowGain(p);
            for (int i = 0; i < 256; i++)
                lut[i] = (byte)Math.Round(Math.Min(255, k * Math.Max(0, i - GlowFrom) * 255.0 / (255 - GlowFrom)));
            return lut;
        }

        // ------------------------------------------------------------------ ffmpeg side

        /// <summary>
        /// Filter fragment (ends with a comma) to drop into a filter chain; the caller converts to YUV after it.
        /// With glow on, it branches and rejoins internally using its own [ol…] labels, which is valid
        /// inside -filter_complex. Expressions are single-quoted so it can sit inside a double-quoted argument.
        /// </summary>
        public static string Filter(OledParams p)
        {
            var I = Tools.Inv;
            double b = Tools.Clamp(p.Black, 0, MaxBlack) / 255.0, g = BrightGain(p), h = HighGain(p);
            var e = string.Format(I,
                "st(0,pow(clip((val/255-{0:0.0######})/{1:0.0######},0,1),{2:0.0####}));" +
                "st(0,ld(0)*{3:0.0####}/(1+{4:0.0####}*ld(0)));" +
                "st(1,clip((ld(0)-0.35)/0.65,0,1));" +
                "255*min(1,ld(0)*(1+{5:0.0####}*ld(1)*ld(1)*(3-2*ld(1))))",
                b, 1 - b, p.Shadows, g, g - 1, h);
            var m = Matrix(p.Color);
            var cm = string.Format(I,
                "colorchannelmixer=rr={0:0.#####}:rg={1:0.#####}:rb={2:0.#####}:gr={3:0.#####}:gg={4:0.#####}:gb={5:0.#####}:br={6:0.#####}:bg={7:0.#####}:bb={8:0.#####}",
                m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);

            var f = (p.Cleanup ? "hqdn3d=1.5:1.5:4:4," : "") +
                    "format=rgb24,lutrgb=r='" + e + "':g='" + e + "':b='" + e + "'," + cm + ",";
            if (!p.HasGlow) return f;

            var ge = string.Format(I, "min(255,{0:0.0####}*max(0,val-{1})*255/{2})", GlowGain(p), GlowFrom, 255 - GlowFrom);
            var floor = "if(lt(val," + HaloFloor + "),0,val)";
            return f + "split[ol0][ol1];" +
                   "[ol1]lutrgb=r='" + ge + "':g='" + ge + "':b='" + ge + "',format=gbrp,boxblur=" + GlowRadius(p, 1) + ":3[olg];" +
                   "[ol0]format=gbrp[olb];[olb][olg]blend=all_mode=screen," +
                   "lutrgb=r='" + floor + "':g='" + floor + "':b='" + floor + "',format=rgb24,";
        }

        // ------------------------------------------------------------------ preview side

        /// <summary>
        /// Renders the look from an RGB24 frame (w × h) into a BGRA32 buffer for a WriteableBitmap.
        /// <paramref name="scale"/> is preview pixels per full-resolution pixel (0.5 for a half-size preview),
        /// so the glow is the same size as in the saved video. Returns the fraction of pixels fully off.
        /// </summary>
        public static double Apply(byte[] rgb, int w, int h, double scale, byte[] bgra, OledParams p)
        {
            int n = w * h;
            var lut = Lut(p);
            var m = Matrix(p.Color);
            float m0 = (float)m[0], m1 = (float)m[1], m2 = (float)m[2], m3 = (float)m[3], m4 = (float)m[4],
                  m5 = (float)m[5], m6 = (float)m[6], m7 = (float)m[7], m8 = (float)m[8];
            var R = new byte[n]; var G = new byte[n]; var B = new byte[n];
            for (int i = 0, j = 0; i < n; i++, j += 3)
            {
                float r = lut[rgb[j]], g = lut[rgb[j + 1]], b = lut[rgb[j + 2]];
                R[i] = Byte(m0 * r + m1 * g + m2 * b);
                G[i] = Byte(m3 * r + m4 * g + m5 * b);
                B[i] = Byte(m6 * r + m7 * g + m8 * b);
            }

            if (p.HasGlow)
            {
                var gl = GlowLut(p);
                int rad = GlowRadius(p, scale);
                var glow = new byte[n];
                var t = new byte[Math.Max(w, h)]; var u = new byte[Math.Max(w, h)];
                foreach (var plane in new[] { R, G, B })
                {
                    for (int i = 0; i < n; i++) glow[i] = gl[plane[i]];
                    BoxBlur(glow, w, h, rad, 3, t, u);
                    for (int i = 0; i < n; i++)
                    {
                        int v = 255 - (255 - plane[i]) * (255 - glow[i]) / 255;   // screen, as ffmpeg's blend
                        plane[i] = (byte)(v < HaloFloor ? 0 : v);
                    }
                }
            }

            int off = 0;
            for (int i = 0, k = 0; i < n; i++, k += 4)
            {
                bgra[k] = B[i]; bgra[k + 1] = G[i]; bgra[k + 2] = R[i]; bgra[k + 3] = 255;
                if (R[i] <= 1 && G[i] <= 1 && B[i] <= 1) off++;
            }
            return n > 0 ? (double)off / n : 0;
        }

        static byte Byte(float v) { int i = (int)(v + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        // ffmpeg's boxblur (libavfilter/vf_boxblur.c), ported 1:1: horizontal passes then vertical passes,
        // each pass a running-sum box with mirrored edges and 16-bit fixed-point rounding.
        static void BoxBlur(byte[] img, int w, int h, int radius, int power, byte[] t, byte[] u)
        {
            radius = Math.Min(radius, Math.Min(w, h) / 2 - 1);
            if (radius < 1) return;
            var line = new byte[Math.Max(w, h)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) line[x] = img[y * w + x];
                BlurPower(line, w, radius, power, t, u);
                for (int x = 0; x < w; x++) img[y * w + x] = line[x];
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) line[y] = img[y * w + x];
                BlurPower(line, h, radius, power, t, u);
                for (int y = 0; y < h; y++) img[y * w + x] = line[y];
            }
        }

        static void BlurPower(byte[] line, int len, int radius, int power, byte[] a, byte[] b)
        {
            Blur(a, line, len, radius);
            for (; power > 2; power--) { Blur(b, a, len, radius); var c = a; a = b; b = c; }
            if (power > 1) Blur(line, a, len, radius);
            else Array.Copy(a, line, len);
        }

        static void Blur(byte[] dst, byte[] src, int len, int radius)
        {
            int length = radius * 2 + 1;
            int inv = ((1 << 16) + length / 2) / length;
            int x, sum = src[radius];
            for (x = 0; x < radius; x++) sum += src[x] << 1;
            sum = sum * inv + (1 << 15);
            for (x = 0; x <= radius; x++) { sum += (src[radius + x] - src[radius - x]) * inv; dst[x] = (byte)(sum >> 16); }
            for (; x < len - radius; x++) { sum += (src[radius + x] - src[x - radius - 1]) * inv; dst[x] = (byte)(sum >> 16); }
            for (; x < len; x++) { sum += (src[2 * len - radius - x - 1] - src[x - radius - 1]) * inv; dst[x] = (byte)(sum >> 16); }
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
