using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>A Real-ESRGAN model the app can use: the two built-in styles, or a community model in the models folder.</summary>
    public class EsrModel
    {
        /// <summary>Stored in the session: "anime", "real", or "m:" + file name for a community model.</summary>
        public string Key;
        public string Label;
        /// <summary>The name passed to realesrgan-ncnn-vulkan's -n.</summary>
        public string Name;
        /// <summary>The model's fixed scale, or 0 when it comes in 2×, 3× and 4× versions (animevideov3).</summary>
        public int FixedScale;
        public string Tip;

        public const string Anime = "anime", Real = "real";

        public int ScaleFor(int w, int h) { return FixedScale > 0 ? FixedScale : FfArgs.EsrganFactor(w, h); }

        /// <summary>Is the model's file there (the anime model is checked at the 2× file)?</summary>
        public bool Installed(string esrganDir)
        {
            var models = Path.Combine(esrganDir, "models");
            return File.Exists(Path.Combine(models, (FixedScale > 0 ? Name : Name + "-x2") + ".param"));
        }

        /// <summary>The built-in styles, then any community models (e.g. the ncnn models Upscayl uses) in the models folder.</summary>
        public static List<EsrModel> All(string esrganDir)
        {
            var list = new List<EsrModel>
            {
                new EsrModel { Key = Anime, Label = "Animated", Name = "realesr-animevideov3", FixedScale = 0,
                               Tip = "Made for video: fast and steady from frame to frame. Best for anime, games and stylized wallpapers." },
                new EsrModel { Key = Real, Label = "Realistic", Name = "realesrgan-x4plus", FixedScale = 4,
                               Tip = "For real-world footage and photos: sharper natural detail. Much slower, always 4×." },
            };
            try
            {
                var dir = Path.Combine(esrganDir, "models");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.param").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        if (name.StartsWith("realesr-animevideov3", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("realesrgan-x4plus", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(Path.ChangeExtension(f, ".bin"))) continue;
                        list.Add(new EsrModel { Key = "m:" + name, Label = Pretty(name), Name = name, FixedScale = ScaleFromName(name),
                                                Tip = "Community model from the models folder (" + name + ")." });
                    }
            }
            catch { }
            return list;
        }

        public static EsrModel Find(string esrganDir, string key)
        {
            var all = All(esrganDir);
            return all.FirstOrDefault(m => m.Key == key) ?? all[0];
        }

        static int ScaleFromName(string name)
        {
            var m = Regex.Match(name, "(?:^|[^a-z0-9])(\\d)x|x(\\d)(?:[^0-9]|$)", RegexOptions.IgnoreCase);
            int s;
            if (m.Success && int.TryParse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value, out s) && s >= 1 && s <= 8) return s;
            return 4;
        }

        static string Pretty(string name)
        {
            if (name.Equals("realesrgan-x4plus-anime", StringComparison.OrdinalIgnoreCase)) return "Anime (detailed)";
            var n = Regex.Replace(name, "(?i)(^|[-_ ])(\\d)x([-_ ]|$)|(^|[-_ ])x(\\d)([-_ ]|$)", " ").Replace('_', ' ').Replace('-', ' ').Trim();
            n = Regex.Replace(n, "\\s+", " ");
            return n.Length == 0 ? name : char.ToUpperInvariant(n[0]) + n.Substring(1);
        }
    }

    /// <summary>
    /// Time remaining for one step: a moving average over the last ~10 seconds blended with the step's average
    /// so far, so it settles quickly without jumping around. Nothing for the first couple of seconds.
    /// </summary>
    public sealed class Eta
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        readonly List<double[]> pts = new List<double[]>();

        public double Elapsed { get { return sw.Elapsed.TotalSeconds; } }

        /// <summary>Seconds left at <paramref name="fraction"/> done, or -1 while still estimating.</summary>
        public double Remaining(double fraction)
        {
            double t = sw.Elapsed.TotalSeconds;
            pts.Add(new[] { t, fraction });
            while (pts.Count > 2 && t - pts[0][0] > 10) pts.RemoveAt(0);
            if (t < 2.5 || fraction < 0.01) return -1;
            double overall = fraction / t;
            double df = fraction - pts[0][1], dt = t - pts[0][0];
            double recent = dt > 1 && df > 0 ? df / dt : overall;
            double rate = 0.6 * recent + 0.4 * overall;
            return rate > 0 ? (1 - fraction) / rate : -1;
        }

        /// <summary>"45 s", "1 min 10 s", "12 min", "1 h 5 min".</summary>
        public static string Fmt(double sec)
        {
            if (sec < 0) return "";
            if (sec < 60) return string.Format(Inv, "{0:0} s", Math.Max(1, Math.Ceiling(sec / 5) * 5));
            if (sec < 600) { int m = (int)(sec / 60); int s = (int)(Math.Round((sec - m * 60) / 10) * 10); if (s == 60) { m++; s = 0; } return s == 0 ? m + " min" : m + " min " + s + " s"; }
            if (sec < 3600) return Math.Round(sec / 60) + " min";
            int h = (int)(sec / 3600);
            return h + " h " + Math.Round((sec - h * 3600) / 60) + " min";
        }

        /// <summary>The status line: "AI enhancing 2× · 42% · about 1 min 10 s left · 3 min in total".</summary>
        public string Line(string label, double fraction, double laterSeconds)
        {
            var text = string.Format(Inv, "{0}  ·  {1:0}%", label, Clamp(fraction, 0, 1) * 100);
            double left = Remaining(fraction);
            if (left < 0) return text + "  ·  estimating time…";
            text += "  ·  about " + Fmt(left) + " left";
            if (laterSeconds > 1) text += " (" + Fmt(left + laterSeconds) + " for the whole export)";
            return text;
        }
    }

    /// <summary>
    /// How fast each export step runs on this PC (units per second: frames, or seconds of video for the
    /// finish), learned from past exports and kept in the settings, so the whole export can be estimated
    /// up front and while it runs.
    /// </summary>
    public class SpeedBook
    {
        readonly Dictionary<string, double> rates;

        public SpeedBook(Dictionary<string, double> stored) { rates = stored ?? new Dictionary<string, double>(); }

        /// <summary>First-guess speeds for a fast GPU; replaced by measured ones after the first export.</summary>
        static double Default(string key)
        {
            if (key == "prep") return 160;
            if (key == "fit") return 110;
            if (key == "rife") return 70;
            if (key == "finish") return 80;
            if (key.StartsWith("esrmp:"))
            {
                // esrmp:<model>:<scale>[:tta], in megapixels of input per second; the cost follows the output size
                var p = key.Split(':');
                int sc; int.TryParse(p.Length > 2 ? p[2] : "2", out sc); sc = Math.Max(1, sc);
                double r = p[1] == EsrModel.Anime ? 240.0 / (sc * sc) : 1.2;
                return key.EndsWith(":tta") ? r / 8 : r;
            }
            return 100;
        }

        public double Rate(string key) { double r; return rates.TryGetValue(key, out r) && r > 0 ? r : Default(key); }
        public double Seconds(string key, double units) { return units / Rate(key); }

        public void Learn(string key, double units, double seconds)
        {
            if (units <= 0 || seconds < 0.5) return;
            double r = units / seconds, old;
            rates[key] = rates.TryGetValue(key, out old) && old > 0 ? 0.5 * old + 0.5 * r : r;
        }
    }
}
