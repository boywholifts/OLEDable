using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OLEDable
{
    /// <summary>
    /// What size an export comes out at. The app started as a HydroShift tool (2288 × 1048, cropped to fill),
    /// but the same true-black, glow and AI enhance work for any wallpaper: keep its own size, upscale it
    /// keeping its shape, or fill a chosen size (4K, a phone screen…).
    /// </summary>
    public class OutputSpec
    {
        public const string HydroShift = "hydroshift", Original = "original", Upscale = "upscale", Custom = "custom";
        /// <summary>The standard screen sizes, each a choice of its own (filled, cropping to fit).</summary>
        public const string UHD = "4k", QHD = "1440p", FHD = "1080p";

        /// <summary>Width and height of a standard-size choice, or null.</summary>
        public static int[] StandardSize(string mode)
        {
            return mode == UHD ? new[] { 3840, 2160 } : mode == QHD ? new[] { 2560, 1440 } : mode == FHD ? new[] { 1920, 1080 } : null;
        }

        /// <summary>The choice for a fill size: a standard one when it matches, otherwise Custom.</summary>
        public static string ModeFor(int w, int h)
        {
            foreach (var m in new[] { UHD, QHD, FHD }) { var z = StandardSize(m); if (z[0] == w && z[1] == h) return m; }
            return Custom;
        }
        public const int PanelW = 2288, PanelH = 1048;

        public int W, H;
        /// <summary>Cover the size and crop the overflow (HydroShift, custom sizes); otherwise just scale (same shape).</summary>
        public bool Crop;
        /// <summary>The HydroShift itself: L-Connect's upload limit applies.</summary>
        public bool Panel;

        public static OutputSpec Panel2288 { get { return new OutputSpec { W = PanelW, H = PanelH, Crop = true, Panel = true }; } }

        /// <summary>The output for a session, once its source size is known (falls back to the HydroShift size).</summary>
        public static OutputSpec For(EditSession s)
        {
            string mode = s.OutputMode ?? HydroShift;
            int sw = s.Width, sh = s.Height;
            var std = StandardSize(mode);
            if (std != null) return new OutputSpec { W = std[0], H = std[1], Crop = true };
            if (mode == Custom && s.CustomW >= 16 && s.CustomH >= 16)
                return new OutputSpec { W = Even(s.CustomW), H = Even(s.CustomH), Crop = true };
            if ((mode == Original || mode == Upscale) && sw > 0 && sh > 0)
            {
                int f = mode == Upscale ? (s.UpscaleFactor == 4 ? 4 : 2) : 1;
                return new OutputSpec { W = Even(sw * f), H = Even(sh * f), Crop = false };
            }
            return Panel2288;
        }

        static int Even(int v) { return Math.Max(2, v / 2 * 2); }

        public string SizeText { get { return W + " × " + H; } }

        /// <summary>Size presets offered for a custom output (4K, 1440p and 1080p are choices of their own).</summary>
        public static readonly KeyValuePair<string, int[]>[] Presets =
        {
            new KeyValuePair<string, int[]>("Fold 8 Ultra inner", new[] { 2256, 2504 }),
            new KeyValuePair<string, int[]>("Fold 8 Ultra cover", new[] { 1080, 2520 }),
        };

        /// <summary>A size for the editor's preview frames with this shape: about the HydroShift preview's pixel count.</summary>
        public int[] PreviewSize()
        {
            double k = Math.Min(Math.Min(1144.0 / W, 1144.0 / H), Math.Sqrt(1144.0 * 524 / ((double)W * H)));
            return new[] { Math.Max(2, (int)Math.Round(W * k / 2) * 2), Math.Max(2, (int)Math.Round(H * k / 2) * 2) };
        }
    }
}
