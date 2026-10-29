using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static OLEDable.Tools;

namespace OLEDable
{
    /// <summary>Progress of a running job: a line of text and 0..1 (negative = indeterminate).</summary>
    public struct JobStatus
    {
        public string Text;
        public double Fraction;
        public JobStatus(string text, double fraction) { Text = text; Fraction = fraction; }
    }

    /// <summary>Set by the UI to end a recording early but keep what was captured.</summary>
    public class StopSignal { public volatile bool Requested; }

    /// <summary>
    /// One video being edited: where the clean original is, and every setting that turns it into the HydroShift
    /// version. Saved next to the library (see <see cref="SourceStore"/>) so re-editing always starts from the original.
    /// </summary>
    [DataContract]
    public class EditSession
    {
        [DataMember] public string Id;
        [DataMember] public string Title;
        [DataMember] public string SourcePath;
        /// <summary>"recording" (captured from the display) or "video" (converted from a file).</summary>
        [DataMember] public string Kind;
        [DataMember] public DateTime Created;
        [DataMember] public OledParams Oled;
        [DataMember] public bool TrueBlack;
        [DataMember] public bool Loop;
        [DataMember] public bool Smooth;
        [DataMember] public double TrimStart;
        /// <summary>End of the used part in seconds; 0 = the end of the source.</summary>
        [DataMember] public double TrimEnd;
        /// <summary>File name of the exported video (null until first export).</summary>
        [DataMember] public string OutputName;
        /// <summary>AI enhance (Real-ESRGAN) before everything else; null = not chosen yet (on for small sources).</summary>
        [DataMember] public bool? Enhance;
        /// <summary>Model: "anime", "real" or "m:name" (see <see cref="EsrModel"/>); null until the editor fills in the last-used one.</summary>
        [DataMember] public string EnhanceStyle;
        /// <summary>0..1: how much of the AI result is used, blended with a plain upscale.</summary>
        [DataMember] public double EnhanceStrength;
        /// <summary>Light denoise/deblock before enhancing, so compression blocks aren't sharpened into "detail".</summary>
        [DataMember] public bool EnhanceCleanup;
        /// <summary>Real-ESRGAN's TTA mode: a little cleaner, about 8× slower.</summary>
        [DataMember] public bool EnhanceTta;
        /// <summary>A light unsharp mask after the upscale.</summary>
        [DataMember] public bool EnhanceSharpen;
        /// <summary>Output size: "hydroshift" (default), "original", "upscale" or "custom"; see <see cref="OutputSpec"/>.</summary>
        [DataMember] public string OutputMode;
        /// <summary>2 or 4, for the "upscale" output.</summary>
        [DataMember] public int UpscaleFactor;
        /// <summary>Size for the "custom" output.</summary>
        [DataMember] public int CustomW, CustomH;
        /// <summary>Also put the export into Wallpaper Engine as a wallpaper of its own (with the video's sound).</summary>
        [DataMember] public bool AddToWallpaperEngine;
        /// <summary>The Wallpaper Engine folder made for it last time, so the next export updates it.</summary>
        [DataMember] public string WeFolder;

        /// <summary>Where the exported video already is, when the session was opened from it; not saved.</summary>
        public string ExistingPath;

        // filled in from the source when the session is opened; not saved
        public double Duration, Fps;
        public int Width, Height;

        public EditSession() { Oled = new OledParams(); TrueBlack = true; Loop = true; Smooth = true; Created = DateTime.Now; }

        public double UsedStart { get { return Clamp(TrimStart, 0, Math.Max(0, Duration - 1)); } }
        public double UsedEnd { get { return TrimEnd > 0 ? Clamp(TrimEnd, UsedStart + 1, Duration) : Duration; } }
        public double UsedLength { get { return Math.Max(0, UsedEnd - UsedStart); } }

        /// <summary>Whether AI enhance is on, using the automatic choice when the user hasn't picked.</summary>
        public bool EnhanceOn { get { return Enhance ?? EnhanceSuggested; } }

        /// <summary>Suggested for sources smaller than the output (after cropping to its shape).</summary>
        public bool EnhanceSuggested { get { return Width > 0 && Height > 0 && FfArgs.EnhanceScale(Width, Height, Output) >= 1.15; } }

        /// <summary>The output size and shape for this session.</summary>
        public OutputSpec Output { get { return OutputSpec.For(this); } }

        /// <summary>A still image (PNG, JPEG…) rather than a video.</summary>
        public bool IsImage { get { return Tools.IsImage(SourcePath); } }

        /// <summary>Whether exporting this would give the same file as exporting <paramref name="o"/> (same settings and name).</summary>
        public bool SameExportAs(EditSession o)
        {
            if (o == null) return false;
            OledParams a = Oled ?? new OledParams(), b = o.Oled ?? new OledParams();
            return a.Black == b.Black && a.Shadows == b.Shadows && a.Bright == b.Bright && a.Highlights == b.Highlights &&
                   a.Color == b.Color && a.Glow == b.Glow && a.GlowSize == b.GlowSize && a.Cleanup == b.Cleanup &&
                   TrueBlack == o.TrueBlack && Loop == o.Loop && Smooth == o.Smooth &&
                   TrimStart == o.TrimStart && TrimEnd == o.TrimEnd && EnhanceOn == o.EnhanceOn &&
                   EnhanceStyle == o.EnhanceStyle && EnhanceStrength == o.EnhanceStrength && EnhanceCleanup == o.EnhanceCleanup &&
                   EnhanceTta == o.EnhanceTta && EnhanceSharpen == o.EnhanceSharpen &&
                   OutputMode == o.OutputMode && UpscaleFactor == o.UpscaleFactor && CustomW == o.CustomW && CustomH == o.CustomH &&
                   Title == o.Title && OutputName == o.OutputName && AddToWallpaperEngine == o.AddToWallpaperEngine;
        }

        public EditSession Clone()
        {
            var c = (EditSession)MemberwiseClone();
            c.Oled = Oled.Clone();
            return c;
        }
    }

    /// <summary>All ffmpeg / RIFE command lines in one place, so tags, quality and quoting stay consistent.</summary>
    public static class FfArgs
    {
        /// <summary>
        /// Fit a source to the output: cover it and crop the overflow (HydroShift, custom sizes), or just scale it
        /// when the output keeps the source's shape (original size, upscale).
        /// </summary>
        public static string Fit(OutputSpec o)
        {
            return o.Crop
                ? "scale=" + o.W + ":" + o.H + ":force_original_aspect_ratio=increase:flags=lanczos,crop=" + o.W + ":" + o.H + ",setsar=1"
                : "scale=" + o.W + ":" + o.H + ":flags=lanczos,setsar=1";
        }

        /// <summary>Crop to the output's shape at the source's own size (for AI enhance, which scales it up after).</summary>
        public static string AspectCrop(OutputSpec o)
        {
            return o.Crop ? "crop='min(iw,ih*" + o.W + "/" + o.H + ")':'min(ih,iw*" + o.H + "/" + o.W + ")',setsar=1" : "setsar=1";
        }
        public const string ToYuv = "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p";
        public const string Tags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
        const string Quiet = "-y -hide_banner -loglevel error -nostats -progress pipe:2 ";
        public const double MaxUploadMB = 30;   // L-Connect 3's upload limit for custom themes

        /// <summary>
        /// Final encode. x264 "medium" at CRF 18 measured best on a 5090 + 28-thread CPU: as fast as NVENC here
        /// (the filters are the bottleneck, not the encoder) and 1.7-2.8x smaller at the same quality.
        /// The VBV cap only matters for long, very busy clips, and keeps the file under L-Connect's limit.
        /// </summary>
        public static string Encode(double seconds, OutputSpec o)
        {
            if (!o.Panel)
                // any other size isn't uploaded to L-Connect: no size cap, a little more quality
                return "-c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p " + Tags + " -movflags +faststart";
            double kbps = MaxUploadMB * 8 * 1024 * 0.92 / Math.Max(1, seconds);
            return string.Format(Inv, "-c:v libx264 -preset medium -crf 18 -maxrate {0:0}k -bufsize {1:0}k -pix_fmt yuv420p ", kbps, kbps * 2) +
                   Tags + " -movflags +faststart";
        }

        /// <summary>One screenshot of a display (Desktop Duplication) to a PNG.</summary>
        public static string Grab(int adapter, int output, string png)
        {
            return "-y -hide_banner -loglevel error -init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                   "-filter_complex \"ddagrab=output_idx=" + output + ":framerate=1:draw_mouse=0,hwdownload,format=bgra\" -frames:v 1 " + Q(png);
        }

        public static string Record(int adapter, int output, int fps, int seconds, bool nvenc, string dest)
        {
            return Quiet + "-init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                   "-filter_complex \"ddagrab=output_idx=" + output + ":framerate=" + fps + ":draw_mouse=0,hwdownload,format=bgra," + ToYuv + "\" " +
                   "-t " + seconds + " " + RecordEncoder(nvenc) + " " + Tags + " " + Q(dest);
        }

        static string RecordEncoder(bool nvenc)
        {
            return nvenc ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 16 -b:v 0" : "-c:v libx264 -preset ultrafast -crf 14";
        }

        /// <summary>
        /// Records one window by its title with Windows' own window capture (ffmpeg 8's gfxcapture), at exactly
        /// w × h. Used for Wallpaper Engine scenes playing in a pop-out window: nothing on screen has to change.
        /// </summary>
        public static string RecordWindow(string titleRegex, int w, int h, int fps, int seconds, bool nvenc, string dest)
        {
            return Quiet + "-init_hw_device d3d11va=hs -filter_hw_device hs " +
                   "-filter_complex \"gfxcapture=window_title='" + titleRegex + "':capture_cursor=0:max_framerate=" + fps +
                   ":width=" + w + ":height=" + h + ":resize_mode=scale,hwdownload,format=bgra,fps=" + fps + "," + ToYuv + "\" " +
                   "-t " + seconds + " " + RecordEncoder(nvenc) + " " + Tags + " " + Q(dest);
        }

        /// <summary>Fallback for older ffmpeg: records a w × h area at (x, y) of a display (Desktop Duplication).</summary>
        public static string RecordArea(int adapter, int output, int x, int y, int w, int h, int fps, int seconds, bool nvenc, string dest)
        {
            return Quiet + "-init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                   "-filter_complex \"ddagrab=output_idx=" + output + ":framerate=" + fps + ":draw_mouse=0:offset_x=" + x + ":offset_y=" + y +
                   ":video_size=" + w + "x" + h + ",hwdownload,format=bgra," + ToYuv + "\" " +
                   "-t " + seconds + " " + RecordEncoder(nvenc) + " " + Tags + " " + Q(dest);
        }

        /// <summary>
        /// Library thumbnail as PNG (JPEG showed banding in dark gradients), converted to RGB with accurate
        /// rounding at a size that stays sharp on high-DPI screens.
        /// </summary>
        public static string Thumb(string src, double at, string png)
        {
            return "-y -hide_banner -loglevel error -ss " + Num(at) + " -i " + Q(src) +
                   " -frames:v 1 -an -vf \"scale=640:293:force_original_aspect_ratio=increase:flags=lanczos+accurate_rnd+full_chroma_int,crop=640:293,format=rgb24\" " + Q(png);
        }

        /// <summary>How much a source must grow to cover the output after cropping to its shape (1 = already big enough).</summary>
        public static double EnhanceScale(int w, int h, OutputSpec o)
        {
            if (w <= 0 || h <= 0) return 1;
            double cw = o.Crop ? Math.Min(w, h * (double)o.W / o.H) : w, ch = o.Crop ? Math.Min(h, w * (double)o.H / o.W) : h;
            return Math.Max(o.W / cw, o.H / ch);
        }

        /// <summary>
        /// The size frames go into Real-ESRGAN at: the source cropped to the output's shape, shrunk first when a
        /// fixed-scale model (4×) would otherwise make them far bigger than needed. Aims for twice the output,
        /// which keeps the detail but avoids e.g. 9152 × 4192 frames for a full-size source (about 4× faster).
        /// </summary>
        public static int[] EsrInputSize(int w, int h, int fixedScale, OutputSpec o)
        {
            double cw = o.Crop ? Math.Min(w, h * (double)o.W / o.H) : w, ch = o.Crop ? Math.Min(h, w * (double)o.H / o.W) : h;
            if (fixedScale > 0 && cw * fixedScale > 2 * o.W)
            {
                double k = 2.0 * o.W / (cw * fixedScale);
                cw *= k; ch *= k;
            }
            return new[] { Math.Max(2, (int)Math.Round(cw / 2) * 2), Math.Max(2, (int)Math.Round(ch / 2) * 2) };
        }

        /// <summary>The crop (and shrink, see <see cref="EsrInputSize"/>) applied before Real-ESRGAN.</summary>
        public static string EsrPrep(int w, int h, int fixedScale, OutputSpec o)
        {
            double cw = o.Crop ? Math.Min(w, h * (double)o.W / o.H) : w;
            var sz = EsrInputSize(w, h, fixedScale, o);
            return sz[0] < cw - 1 ? AspectCrop(o) + ",scale=" + sz[0] + ":" + sz[1] + ":flags=area" : AspectCrop(o);
        }

        /// <summary>Real-ESRGAN's factor (2, 3 or 4) for a source: the smallest that covers the output, 2 at least.</summary>
        public static int EsrganFactor(int w, int h, OutputSpec o)
        {
            double need = EnhanceScale(w, h, o);
            return need <= 2 ? 2 : need <= 3 ? 3 : 4;
        }

        /// <summary>Light denoise used before AI enhance (on the cropped source, so it's cheap).</summary>
        public const string Clean = "hqdn3d=2:1.5:3:2.25";

        /// <summary>Real-ESRGAN with a model (see <see cref="EsrModel"/>) and scale; TTA = slower, slightly cleaner.</summary>
        public static string Esrgan(string inPath, string outPath, string model, int scale, string modelDir, bool tta)
        {
            return "-i " + Q(inPath) + " -o " + Q(outPath) + " -n " + model + " -s " + scale + " -m " + Q(modelDir) + " -f png -j 4:4:4" + (tta ? " -x" : "");
        }

        public static string Cadence(string src, double t0, double dur, string md5)
        {
            return Quiet + "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(src) + " -an -vf \"" + DupFilter + "\" -fps_mode passthrough -f framemd5 " + Q(md5);
        }

        /// <summary>
        /// Frames for RIFE / Real-ESRGAN: fitted to the panel, or only cropped to its shape when they'll be
        /// AI-enhanced first. Light PNG compression: 40% faster to write.
        /// </summary>
        public static string PrepPng(string src, double t0, double dur, double fps, string dir, string shape, bool clean = false, int frames = 0)
        {
            return Quiet + "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(src) + " -vf \"" + shape + (clean ? "," + Clean : "") +
                   ",fps=" + Num(fps) + "\" -an " + (frames > 0 ? "-frames:v " + frames + " " : "") + "-compression_level 1 " + Q(Path.Combine(dir, "%08d.png"));
        }

        /// <summary>
        /// Fits enhanced frames to the panel (so RIFE and the finish work at panel size, not 4K). With
        /// <paramref name="plainDir"/> and a strength below 1, blends them with a plain upscale of the
        /// original frames; <paramref name="sharpen"/> adds a light unsharp mask.
        /// </summary>
        public static string FitPng(string enhDir, string plainDir, double strength, bool sharpen, double fps, string outDir, OutputSpec o)
        {
            string inE = "-framerate " + Num(fps) + " -i " + Q(Path.Combine(enhDir, "%08d.png"));
            if (plainDir != null && strength < 0.995)
            {
                string inP = " -framerate " + Num(fps) + " -i " + Q(Path.Combine(plainDir, "%08d.png"));
                return Quiet + inE + inP + " -filter_complex \"" + EnhancedGraph(o, strength, sharpen, true) + "[v]\" -map \"[v]\" -compression_level 1 " +
                       Q(Path.Combine(outDir, "%08d.png"));
            }
            return Quiet + inE + " -filter_complex \"" + EnhancedGraph(o, 1, sharpen, false) + "[v]\" -map \"[v]\" -compression_level 1 " +
                   Q(Path.Combine(outDir, "%08d.png"));
        }

        /// <summary>
        /// Graph from AI-enhanced input [0:v] (and the plain source [1:v] when <paramref name="blend"/>) to one
        /// output fitted to <paramref name="o"/>: strength blend and optional sharpening. Ends unlabelled (append "[v]" or more filters).
        /// </summary>
        public static string EnhancedGraph(OutputSpec o, double strength, bool sharpen, bool blend)
        {
            string tail = sharpen ? ",unsharp=5:5:0.6:5:5:0" : "";
            if (!blend) return "[0:v]" + Fit(o) + tail;
            double a = Clamp(strength, 0, 1);
            return "[0:v]" + Fit(o) + ",format=gbrp[e];[1:v]" + Fit(o) + ",format=gbrp[o];" +
                   "[e][o]blend=all_expr='A*" + Num(a) + "+B*" + Num(1 - a) + "'" + tail;
        }

        /// <summary>RIFE 4.6 with 8 load / 8 GPU / 8 save threads: 2.9x faster than 2:2:2 on an RTX 5090.</summary>
        public static string Rife(string inDir, string outDir, int frames, string model)
        {
            return "-i " + Q(inDir) + " -o " + Q(outDir) + " -n " + frames + " -m " + Q(model) + " -j 8:8:8";
        }

        /// <summary>
        /// The finishing filter graph: <paramref name="pre"/> (fit, fps, OLED look …) then YUV, and if
        /// <paramref name="loop"/>, the end crossfaded into the start so the clip repeats seamlessly.
        /// </summary>
        public static string FinishGraph(string pre, double seconds, bool loop, double crossfade, out double outSeconds)
        {
            double c = crossfade;
            if (loop && seconds > 2 * c + 1)
            {
                string cs = Num(c), off = Num(seconds - 2 * c);
                outSeconds = seconds - c;
                return "[0:v]" + pre + ToYuv + ",split[a][b];[a]trim=start=" + cs + ",setpts=PTS-STARTPTS[m];[b]trim=end=" + cs + ",setpts=PTS-STARTPTS[h];" +
                       "[m][h]xfade=transition=fade:duration=" + cs + ":offset=" + off + ",format=yuv420p[v]";
            }
            outSeconds = seconds;
            return "[0:v]" + pre + ToYuv + "[v]";
        }
    }

    /// <summary>
    /// Everything that turns a source into a finished HydroShift video, as awaitable steps that report progress.
    /// No UI in here: the pages start jobs and show the progress.
    /// </summary>
    public class Pipeline
    {
        const string RifeUrl = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip";

        const string EsrganUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";

        public readonly string Ffmpeg, Ffprobe, RifeDir, RifeExe, EsrganDir, EsrganExe;
        /// <summary>Measured step speeds on this PC, for time estimates (kept in the settings).</summary>
        public SpeedBook Speeds = new SpeedBook(null);

        public Pipeline(string ffmpeg, string ffprobe, string rifeDir, string esrganDir)
        {
            Ffmpeg = ffmpeg; Ffprobe = ffprobe;
            RifeDir = rifeDir;
            RifeExe = Path.Combine(RifeDir, "rife-ncnn-vulkan.exe");
            EsrganDir = esrganDir;
            EsrganExe = Path.Combine(EsrganDir, "realesrgan-ncnn-vulkan.exe");
        }

        /// <summary>
        /// Runs a frame tool (RIFE, Real-ESRGAN), reporting how many of <paramref name="target"/> frames are done,
        /// the percentage and the time left. <paramref name="later"/> = estimated seconds of the steps after this one.
        /// </summary>
        static async Task RunFrames(string exe, string args, string outDir, int target, string label, double later, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var job = new FfJob(exe, args);
            var wait = job.WaitAsync(ct);
            var eta = new Eta();
            while (!wait.IsCompleted)
            {
                int n = Directory.EnumerateFiles(outDir, "*.png").Count();
                double f = Clamp((double)n / target, 0, 1);
                pr.Report(new JobStatus(eta.Line(label, f, later), f));
                await Task.WhenAny(wait, Task.Delay(500));
            }
            await wait;
            ct.ThrowIfCancellationRequested();
            int made = Directory.GetFiles(outDir, "*.png").Length;
            if (made < Math.Floor(target * 0.9)) throw new Exception(label + " failed: " + job.LastLine);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>
        /// Runs ffmpeg, reporting progress against <paramref name="expectedSeconds"/> of output, with the time left.
        /// <paramref name="later"/> = estimated seconds of the steps after this one (0 = none). Throws on failure.
        /// </summary>
        async Task RunFf(string args, double expectedSeconds, string label, IProgress<JobStatus> pr, CancellationToken ct, double later = 0)
        {
            var job = new FfJob(Ffmpeg, args);
            var wait = job.WaitAsync(ct);
            var eta = new Eta();
            while (!wait.IsCompleted)
            {
                double f = expectedSeconds > 0 ? Clamp(job.Seconds / expectedSeconds, 0, 1) : -1;
                pr.Report(new JobStatus(f >= 0 ? eta.Line(label, f, later) : label + "…", f));
                await Task.WhenAny(wait, Task.Delay(150));
            }
            int code = await wait;
            ct.ThrowIfCancellationRequested();
            if (code != 0) throw new Exception(label + " failed: " + job.LastLine);
        }

        // ------------------------------------------------------------ recording

        /// <summary>
        /// Records the HydroShift to <paramref name="dest"/>. <paramref name="stop"/> ends early and keeps the clip;
        /// cancelling discards it. Returns the encoder that worked ("nvenc" or "x264").
        /// </summary>
        public Task<string> RecordAsync(int adapter, int output, int fps, int seconds, string encoder, string dest,
                                        StopSignal stop, IProgress<JobStatus> pr, CancellationToken ct)
        {
            return CaptureAsync(nvenc => FfArgs.Record(adapter, output, fps, seconds, nvenc, dest), seconds, encoder, dest, stop, pr, ct);
        }

        /// <summary>
        /// Runs a capture made by <paramref name="argsFor"/> (given "use NVENC?"), falling back to the CPU encoder
        /// when NVENC fails. <paramref name="stop"/> ends early and keeps the clip; cancelling discards it.
        /// Returns the encoder that worked ("nvenc" or "x264").
        /// </summary>
        public async Task<string> CaptureAsync(Func<bool, string> argsFor, int seconds, string encoder, string dest,
                                               StopSignal stop, IProgress<JobStatus> pr, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            foreach (var enc in encoder == "nvenc" ? new[] { "nvenc", "x264" } : new[] { "x264" })
            {
                TryDelete(dest);
                var job = new FfJob(Ffmpeg, argsFor(enc == "nvenc"));
                var wait = job.WaitAsync(ct);
                bool quitSent = false;
                while (!wait.IsCompleted)
                {
                    if (stop.Requested && !quitSent) { job.Quit(); quitSent = true; }
                    double t = job.Seconds;
                    pr.Report(new JobStatus(string.Format(Inv, "Recording  ·  {0} of {1}", Fmt(t), Fmt(seconds)), Clamp(t / seconds, 0, 1)));
                    await Task.WhenAny(wait, Task.Delay(100));
                }
                await wait;
                if (ct.IsCancellationRequested) { TryDelete(dest); ct.ThrowIfCancellationRequested(); }
                if (File.Exists(dest) && new FileInfo(dest).Length > 50 * 1024) return enc;
                if (enc == "x264") throw new Exception("Recording failed: " + job.LastLine);
                pr.Report(new JobStatus("No NVIDIA encoder, trying the CPU encoder…", -1));
            }
            throw new Exception("Recording failed");
        }

        // ------------------------------------------------------------ smoothing

        public bool HasRife { get { return File.Exists(RifeExe); } }

        public async Task EnsureRifeAsync(IProgress<JobStatus> pr, CancellationToken ct)
        {
            if (HasRife) return;
            var zip = Path.Combine(Path.GetTempPath(), "rife-ncnn-vulkan.zip");
            TryDelete(zip);
            try
            {
                await Downloader.Download(RifeUrl, zip, new Progress<double>(f =>
                    pr.Report(new JobStatus(string.Format(Inv, "Downloading RIFE (one time only)…  {0:0}%", f * 100), f))), ct);
                pr.Report(new JobStatus("Unpacking RIFE…", -1));
                await Task.Run(() =>
                {
                    using (var z = ZipFile.OpenRead(zip))
                        foreach (var e in z.Entries)
                        {
                            int slash = e.FullName.IndexOf('/');
                            if (slash < 0) continue;
                            var rel = e.FullName.Substring(slash + 1);
                            if (rel.Length == 0 || rel.EndsWith("/") || rel.Contains("..")) continue;
                            if (!Regex.IsMatch(rel, "^(rife-ncnn-vulkan\\.exe|vcomp140\\.dll|LICENSE|README\\.md|rife-v4\\.6/.+)$")) continue;
                            var dest = Path.Combine(RifeDir, rel.Replace('/', '\\'));
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                        }
                }, ct);
            }
            finally { TryDelete(zip); }
        }

        // ------------------------------------------------------------ AI enhance

        public bool HasEsrgan { get { return File.Exists(EsrganExe); } }

        /// <summary>Is Real-ESRGAN there with this model?</summary>
        public bool HasModel(string key) { return HasEsrgan && EsrModel.Find(EsrganDir, key).Installed(EsrganDir); }

        /// <summary>
        /// Downloads Real-ESRGAN (ncnn/Vulkan build, ~50 MB) when it or the chosen model is missing: the exe and
        /// the built-in models (video "animevideov3", "x4plus" for realistic, "x4plus-anime").
        /// </summary>
        public async Task EnsureEsrganAsync(IProgress<JobStatus> pr, CancellationToken ct, string modelKey = EsrModel.Anime)
        {
            if (HasModel(modelKey)) return;
            var zip = Path.Combine(Path.GetTempPath(), "realesrgan-ncnn-vulkan.zip");
            TryDelete(zip);
            try
            {
                await Downloader.Download(EsrganUrl, zip, new Progress<double>(f =>
                    pr.Report(new JobStatus(string.Format(Inv, "Downloading AI enhance (one time only)…  {0:0}%", f * 100), f))), ct);
                pr.Report(new JobStatus("Unpacking AI enhance…", -1));
                await Task.Run(() =>
                {
                    using (var z = ZipFile.OpenRead(zip))
                        foreach (var e in z.Entries)
                        {
                            var rel = e.FullName;
                            if (rel.Length == 0 || rel.EndsWith("/")) continue;
                            if (!Regex.IsMatch(rel, "^(realesrgan-ncnn-vulkan\\.exe|vcomp140\\.dll|models/(realesr-animevideov3-x\\d|realesrgan-x4plus|realesrgan-x4plus-anime)\\.(bin|param))$")) continue;
                            var dest = Path.Combine(EsrganDir, rel.Replace('/', '\\'));
                            // only add what's missing: files already there may be in use by a preview right now
                            if (File.Exists(dest)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            try { e.ExtractToFile(dest, false); }
                            catch (IOException) { if (!File.Exists(dest)) throw; }
                        }
                }, ct);
            }
            finally { TryDelete(zip); }
        }

        /// <summary>
        /// One AI-enhanced frame for the editor preview, at <paramref name="w"/> × <paramref name="h"/> as RGB24
        /// (null on failure), with the session's model, cleanup and quality mode. Strength is blended by the
        /// editor. Takes a second or two: the model loads for each call.
        /// </summary>
        public byte[] EnhanceFrame(string src, double seconds, int srcW, int srcH, int w, int h, string modelKey, bool cleanup, bool tta, OutputSpec o)
        {
            var dir = Path.Combine(Path.GetTempPath(), "oledable_enh_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                var a = Path.Combine(dir, "a.png");
                var b = Path.Combine(dir, "b.png");
                var j = new FfJob(Ffmpeg, "-y -hide_banner -loglevel error -ss " + Num(Math.Max(0, seconds)) + " -i " + Q(src) +
                                          " -frames:v 1 -an -vf \"" + FfArgs.EsrPrep(srcW, srcH, EsrModel.Find(EsrganDir, modelKey).FixedScale, o) +
                                          (cleanup ? "," + FfArgs.Clean : "") + "\" " + Q(a));
                if (!j.Wait(20000)) j.Kill();
                j.Drain();
                if (!File.Exists(a)) return null;
                var m = EsrModel.Find(EsrganDir, modelKey);
                var e = new FfJob(EsrganExe, FfArgs.Esrgan(a, b, m.Name, m.ScaleFor(srcW, srcH, o), Path.Combine(EsrganDir, "models"), tta));
                if (!e.Wait(120000)) e.Kill();
                e.Drain();
                return File.Exists(b) ? Oled.GrabFrame(Ffmpeg, b, 0, w, h) : null;
            }
            catch { return null; }
            finally { TryDeleteDir(dir); }
        }

        /// <summary>
        /// For a 60 fps recording: how many capture frames each real wallpaper frame lasted (2 = it drew 30 fps),
        /// or 0 when it's genuinely smooth or changes irregularly. See <see cref="Tools.CadenceGap"/>.
        /// </summary>
        public async Task<int> CadenceAsync(string src, double t0, double dur, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var md5 = Path.Combine(Path.GetTempPath(), "oledable_frames_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try
            {
                await RunFf(FfArgs.Cadence(src, t0, dur, md5), dur, "Checking how smoothly it moved", pr, ct);
                double unique;
                return File.Exists(md5) ? CadenceGap(File.ReadAllText(md5), dur, out unique) : 0;
            }
            finally { TryDelete(md5); }
        }

        // ------------------------------------------------------------ export

        /// <summary>One step of an export, for time estimates: its speed key and how many units it processes.</summary>
        class Step
        {
            public string Key; public double Units;
            public Step(string key, double units) { Key = key; Units = units; }
        }

        /// <summary>Fills AI enhance settings a session saved before they existed.</summary>
        static void Normalize(EditSession s)
        {
            if (s.EnhanceStyle == null) { s.EnhanceStyle = EsrModel.Anime; s.EnhanceStrength = 0.75; }
        }

        /// <summary>The steps an export will run, given whether it smooths from <paramref name="from"/> fps.</summary>
        List<Step> Plan(EditSession s, double from)
        {
            Normalize(s);
            double dur = s.UsedLength;
            bool enhance = s.EnhanceOn;
            double rate = from > 0 ? from : Math.Min(60, s.Fps > 0 ? s.Fps : 60);
            double frames = dur * rate;
            var steps = new List<Step>();
            if (enhance || from > 0) steps.Add(new Step("prep", frames));
            if (enhance)
            {
                var m = EsrModel.Find(EsrganDir, s.EnhanceStyle ?? EsrModel.Anime);
                var inSize = FfArgs.EsrInputSize(s.Width, s.Height, m.FixedScale, s.Output);
                steps.Add(new Step(EsrKey(m, s), frames * inSize[0] * inSize[1] / 1e6));   // megapixels in
                if (NeedsFit(s, from)) steps.Add(new Step("fit", frames));
            }
            if (from > 0) steps.Add(new Step("rife", dur * 60));
            steps.Add(new Step("finish", (s.Loop ? Math.Max(1, dur - 1) : dur) * 60));
            return steps;
        }

        string EsrKey(EsrModel m, EditSession s)
        {
            return "esrmp:" + (m.Key.StartsWith("m:") ? "custom" : m.Key) + ":" + m.ScaleFor(s.Width, s.Height, s.Output) + (s.EnhanceTta ? ":tta" : "");
        }

        static bool NeedsFit(EditSession s, double from) { return from > 0 || s.EnhanceStrength < 0.995 || s.EnhanceSharpen; }

        /// <summary>
        /// Estimated export time in seconds from this PC's measured speeds (assumes no smoothing is needed for a
        /// 60 fps recording; that's only known once it's checked).
        /// </summary>
        public double EstimateSeconds(EditSession s)
        {
            if (s.IsImage)
            {
                if (!s.EnhanceOn) return 2;
                Normalize(s);
                var m = EsrModel.Find(EsrganDir, s.EnhanceStyle);
                var sz = FfArgs.EsrInputSize(s.Width, s.Height, m.FixedScale, s.Output);
                return 3 + Speeds.Seconds(EsrKey(m, s), sz[0] * sz[1] / 1e6);
            }
            double from = s.Smooth && s.Fps > 0 && s.Fps < 58 ? s.Fps : 0;
            return Plan(s, from).Sum(st => Speeds.Seconds(st.Key, st.Units));
        }

        /// <summary>
        /// Renders the session to <paramref name="outPath"/>: trim, optional AI enhance, optional smoothing to 60 fps,
        /// the OLED look, the loop crossfade, and the size-capped encode. Writes to a temporary file first and only
        /// then replaces the target, so a failed or cancelled export never destroys the previous version.
        /// </summary>
        public async Task ExportAsync(EditSession s, string outPath, IProgress<JobStatus> pr, CancellationToken ct)
        {
            if (s.IsImage) { await ExportImageAsync(s, outPath, pr, ct); return; }
            double t0 = s.UsedStart, dur = s.UsedLength;
            if (dur < 1) throw new Exception("The selected part is shorter than a second.");
            string folder = Path.GetDirectoryName(outPath);
            Directory.CreateDirectory(folder);
            Normalize(s);
            var o = s.Output;

            // 1. does it need smoothing, and from what frame rate?
            double from = 0;
            if (s.Smooth)
            {
                if (s.Fps > 0 && s.Fps < 58) from = s.Fps;
                else if (s.Kind == "recording")
                {
                    int gap = await CadenceAsync(s.SourcePath, t0, dur, pr, ct);
                    if (gap >= 2) from = 60.0 / gap;
                }
            }

            bool enhance = s.EnhanceOn;
            var model = EsrModel.Find(EsrganDir, s.EnhanceStyle ?? EsrModel.Anime);
            var plan = Plan(s, from);
            int stepNo = 0;
            Func<double> later = () => plan.Skip(stepNo + 1).Sum(st => Speeds.Seconds(st.Key, st.Units));
            var clock = new System.Diagnostics.Stopwatch();
            Action<double> done = units => { Speeds.Learn(plan[stepNo].Key, units, clock.Elapsed.TotalSeconds); stepNo++; clock.Restart(); };

            string work = null, input, pre;
            try
            {
                if (from > 0 || enhance)
                {
                    // 2a. frame by frame (AI enhance and/or RIFE smoothing), in pieces of about 10 seconds: each piece's
                    // frames are made, encoded to a near-lossless clip and deleted before the next, so the temporary
                    // space stays at a few GB however long the video is. The pieces are joined for the finish.
                    if (enhance) await EnsureEsrganAsync(pr, ct, model.Key);
                    if (from > 0) await EnsureRifeAsync(pr, ct);
                    work = Path.Combine(folder, SourceStore.FolderName, "temp");   // same drive as the library
                    Directory.CreateDirectory(work);
                    try { var di = new DirectoryInfo(Path.Combine(folder, SourceStore.FolderName)); di.Attributes |= FileAttributes.Hidden; } catch { }
                    TryDeleteDir(work);
                    Directory.CreateDirectory(work);
                    double rate = from > 0 ? from : Math.Min(60, s.Fps > 0 ? s.Fps : 60);
                    double outRate = from > 0 ? 60 : rate;
                    int factor = model.ScaleFor(s.Width, s.Height, o);
                    var inSz = FfArgs.EsrInputSize(s.Width, s.Height, model.FixedScale, o);
                    bool fitStep = enhance && NeedsFit(s, from);

                    int total = Math.Max(2, (int)Math.Round(dur * rate));
                    int per = Math.Max(2, (int)Math.Round(rate * PieceSeconds));
                    int pieces = (total + per - 1) / per;
                    double perFrameMB = 6 + (enhance ? 6.0 * factor * factor / 4 + 3 : 0);
                    double pieceGB = ((per + 1) * perFrameMB + (from > 0 ? (per + 1) * 60 / from * 6 : 0)) / 1024;
                    double clipsGB = dur * outRate * 0.5 / 1024;   // the encoded pieces (generous)
                    CheckSpace(folder, pieceGB + clipsGB + 1);

                    // progress across all pieces: each step's share of a piece, from this PC's measured speeds
                    var steps = plan.Where(st => st.Key != "finish").ToList();
                    double est = Math.Max(1e-6, steps.Sum(st => Speeds.Seconds(st.Key, st.Units)));
                    var share = steps.ToDictionary(st => st.Key, st => Speeds.Seconds(st.Key, st.Units) / est);
                    double finishEst = plan.Where(st => st.Key == "finish").Sum(st => Speeds.Seconds(st.Key, st.Units));
                    var eta = new Eta();
                    var spent = new Dictionary<string, double>();
                    var units = new Dictionary<string, double>();
                    Action<string, double, double> learn = (key, u, sec) =>
                    {
                        double v; spent.TryGetValue(key, out v); spent[key] = v + sec;
                        units.TryGetValue(key, out v); units[key] = v + u;
                    };

                    var clips = new List<string>();
                    int outFrames = 0;
                    for (int k = 0; k < pieces; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int first = k * per, n = Math.Min(per, total - first);
                        bool last = k == pieces - 1;
                        int take = last ? n : n + 1;   // one frame of the next piece, so smoothing can fill the gap to it
                        string tag = pieces > 1 ? string.Format(Inv, "Piece {0} of {1}: ", k + 1, pieces) : "";
                        double done0 = 0;
                        Func<string, string, IProgress<JobStatus>> stage = (key, label) =>
                        {
                            double c0 = done0, w; share.TryGetValue(key, out w);
                            int kk = k;
                            return new Relay(st =>
                            {
                                double f = Clamp(st.Fraction < 0 ? 0 : st.Fraction, 0, 1);
                                double all = Clamp((kk + c0 + w * f) / pieces, 0, 1);
                                pr.Report(new JobStatus(eta.Line(tag + label, all, finishEst), all));
                            });
                        };
                        Action<string> next = key => { double w; share.TryGetValue(key, out w); done0 += w; };

                        var dir = Path.Combine(work, "piece");
                        TryDeleteDir(dir);
                        string plain = Path.Combine(dir, "in"), frames = plain;
                        Directory.CreateDirectory(plain);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string shape = enhance ? FfArgs.EsrPrep(s.Width, s.Height, model.FixedScale, o) : FfArgs.Fit(o);
                        double pt0 = t0 + first / rate;
                        await RunFf(FfArgs.PrepPng(s.SourcePath, pt0, (take + 1) / rate, rate, plain, shape, enhance && s.EnhanceCleanup, take),
                                    take / rate, "Preparing frames", stage("prep", "Preparing frames"), ct);
                        int count = Directory.GetFiles(plain, "*.png").Length;
                        if (count < 1) { if (k > 0) break; throw new Exception("Preparing frames failed: no frames were written."); }
                        if (!last && count <= n) last = true;   // the source ended early: nothing to overlap with
                        learn("prep", count, sw.Elapsed.TotalSeconds); next("prep");

                        if (enhance)
                        {
                            string key = EsrKey(model, s);
                            sw.Restart();
                            var esr = Path.Combine(dir, "enhanced");
                            Directory.CreateDirectory(esr);
                            await RunFrames(EsrganExe, FfArgs.Esrgan(plain, esr, model.Name, factor, Path.Combine(EsrganDir, "models"), s.EnhanceTta), esr, count,
                                            "AI enhancing " + factor + "× (" + model.Label + ")", 0, stage(key, "AI enhancing " + factor + "× (" + model.Label + ")"), ct);
                            learn(key, count * inSz[0] * inSz[1] / 1e6, sw.Elapsed.TotalSeconds); next(key);
                            frames = esr;
                            if (fitStep)
                            {
                                sw.Restart();
                                var fit = Path.Combine(dir, "fit");
                                Directory.CreateDirectory(fit);
                                await RunFf(FfArgs.FitPng(esr, plain, s.EnhanceStrength, s.EnhanceSharpen, rate, fit, o), count / rate,
                                            "Blending and fitting frames", stage("fit", "Blending and fitting frames"), ct);
                                learn("fit", count, sw.Elapsed.TotalSeconds); next("fit");
                                TryDeleteDir(esr);
                                frames = fit;
                            }
                            TryDeleteDir(plain);
                        }

                        // the frames this piece contributes: its own, without the overlap frame
                        int keep;
                        if (from > 0)
                        {
                            sw.Restart();
                            var outDir = Path.Combine(dir, "out");
                            Directory.CreateDirectory(outDir);
                            int target = (int)Math.Round(count * 60 / from);
                            string label = string.Format(Inv, "Smoothing {0:0} → 60 fps", from);
                            await RunFrames(RifeExe, FfArgs.Rife(frames, outDir, target, Path.Combine(RifeDir, "rife-v4.6")), outDir, target,
                                            label, 0, stage("rife", label), ct);
                            learn("rife", target, sw.Elapsed.TotalSeconds); next("rife");
                            frames = outDir;
                            keep = last ? target : (int)Math.Round(n * 60 / from);
                        }
                        else keep = last ? count : n;
                        keep = Math.Min(keep, Directory.GetFiles(frames, "*.png").Length);

                        // near-lossless RGB clip (the finish re-encodes it anyway)
                        var clip = Path.Combine(work, string.Format(Inv, "piece{0:00000}.mkv", k));
                        string vf = enhance && !fitStep ? " -vf \"" + FfArgs.Fit(o) + "\"" : "";
                        await RunFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 -framerate " + Num(outRate) + " -i " + Q(Path.Combine(frames, "%08d.png")) +
                                    " -frames:v " + keep + vf + " -an -c:v libx264rgb -preset veryfast -crf 6 " + Q(clip),
                                    keep / outRate, "Saving the piece", stage("", "Saving the piece"), ct);
                        if (!File.Exists(clip)) throw new Exception("Saving a piece failed: no file was written.");
                        clips.Add(clip);
                        outFrames += keep;
                        TryDeleteDir(dir);
                        if (last) break;
                    }

                    // what each step cost on this PC, for better estimates next time
                    foreach (var key in spent.Keys) Speeds.Learn(key, units[key], spent[key]);
                    stepNo = plan.FindIndex(st => st.Key == "finish");
                    clock.Restart();

                    var list = Path.Combine(work, "pieces.txt");
                    File.WriteAllLines(list, clips.Select(c => "file '" + c.Replace("'", "'\\''") + "'"));
                    input = "-f concat -safe 0 -i " + Q(list);
                    pre = "";
                    dur = outFrames / outRate;
                }
                else
                {
                    // 2b. straight from the source
                    clock.Start();
                    bool over = s.Fps > 61;
                    input = "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(s.SourcePath);
                    pre = FfArgs.Fit(o) + "," + (over ? "fps=60," : "");
                }

                // 3. the look, the loop, the encode
                if (s.TrueBlack) pre += Oled.Filter(s.Oled, o.W, o.H);
                double outSec;
                var graph = FfArgs.FinishGraph(pre, dur, s.Loop, 1, out outSec);
                // the sound comes along for Wallpaper Engine (the HydroShift plays none): straight from the source,
                // crossfaded at the loop point the same way as the picture
                string audioIn = "", audioOut = " -an";
                if (s.AddToWallpaperEngine && HasAudio(s.SourcePath))
                {
                    audioIn = " -ss " + Num(t0) + " -t " + Num(s.UsedLength) + " -i " + Q(s.SourcePath);
                    graph += outSec < dur - 0.01
                        ? ";[1:a]aresample=48000,asplit[a1][a2];[a1]atrim=start=1,asetpts=PTS-STARTPTS[am];[a2]atrim=end=1,asetpts=PTS-STARTPTS[ah];[am][ah]acrossfade=d=1[a]"
                        : ";[1:a]aresample=48000[a]";
                    audioOut = " -map \"[a]\" -c:a aac -b:a 192k -shortest";
                }
                var tmp = outPath + ".partial.mp4";
                TryDelete(tmp);
                var meta = "-metadata comment=" + Q("oled|" + s.Id + "|" + s.Kind + (s.TrueBlack ? "|tb" : "")) + " -metadata title=" + Q(s.Title ?? "");
                await RunFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 " + input + audioIn + " -filter_complex \"" + graph + "\" -map \"[v]\"" + audioOut + " " +
                            FfArgs.Encode(outSec, o) + " " + meta + " " + Q(tmp),
                            outSec, s.Loop ? "Finishing and looping" : "Finishing", pr, ct);
                if (!File.Exists(tmp)) throw new Exception("Finishing failed: no file was written.");
                if (stepNo < plan.Count) done(outSec * 60);
                await PutInPlace(tmp, outPath, ct);
            }
            finally
            {
                TryDelete(outPath + ".partial.mp4");
                if (work != null) TryDeleteDir(work);
            }
        }

        /// <summary>
        /// A still image: optional AI enhance (with strength and sharpening), fitted to the output, the OLED look,
        /// saved as PNG. Noise cleanup is skipped: it works across video frames.
        /// </summary>
        async Task ExportImageAsync(EditSession s, string outPath, IProgress<JobStatus> pr, CancellationToken ct)
        {
            Normalize(s);
            var o = s.Output;
            string folder = Path.GetDirectoryName(outPath);
            Directory.CreateDirectory(folder);
            var look = s.Oled.Clone();
            look.Cleanup = false;
            string oled = s.TrueBlack ? Oled.Filter(look, o.W, o.H) : "";
            var tmp = Path.Combine(folder, Path.GetFileNameWithoutExtension(outPath) + ".partial.png");
            string work = null;
            try
            {
                string input, graph;
                if (s.EnhanceOn)
                {
                    var model = EsrModel.Find(EsrganDir, s.EnhanceStyle);
                    await EnsureEsrganAsync(pr, ct, model.Key);
                    work = Path.Combine(Path.GetTempPath(), "oledable_img_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(work);
                    var plain = Path.Combine(work, "plain.png");
                    var esr = Path.Combine(work, "enhanced.png");
                    pr.Report(new JobStatus("Preparing the image…", -1));
                    await RunFf("-y -hide_banner -loglevel error -i " + Q(s.SourcePath) + " -frames:v 1 -vf \"" +
                                FfArgs.EsrPrep(s.Width, s.Height, model.FixedScale, o) + (s.EnhanceCleanup ? "," + FfArgs.Clean : "") + "\" " + Q(plain),
                                0, "Preparing the image", pr, ct);
                    int factor = model.ScaleFor(s.Width, s.Height, o);
                    pr.Report(new JobStatus("AI enhancing " + factor + "× (" + model.Label + ")…", -1));
                    var job = new FfJob(EsrganExe, FfArgs.Esrgan(plain, esr, model.Name, factor, Path.Combine(EsrganDir, "models"), s.EnhanceTta));
                    await job.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(esr)) throw new Exception("AI enhance failed: " + job.LastLine);
                    bool blend = s.EnhanceStrength < 0.995;
                    input = "-i " + Q(esr) + (blend ? " -i " + Q(plain) : "");
                    graph = FfArgs.EnhancedGraph(o, s.EnhanceStrength, s.EnhanceSharpen, blend) + ",";
                }
                else
                {
                    input = "-i " + Q(s.SourcePath);
                    graph = "[0:v]" + FfArgs.Fit(o) + ",";
                }
                pr.Report(new JobStatus("Applying true black…", -1));
                TryDelete(tmp);
                await RunFf("-y -hide_banner -loglevel error " + input + " -filter_complex \"" + graph + oled + "format=rgb24[v]\" -map \"[v]\" -frames:v 1 -update 1 " + Q(tmp),
                            0, "Saving the image", pr, ct);
                if (!File.Exists(tmp)) throw new Exception("Saving the image failed: no file was written.");
                await PutInPlace(tmp, outPath, ct);
            }
            finally
            {
                TryDelete(tmp);
                if (work != null) TryDeleteDir(work);
            }
        }

        /// <summary>
        /// Swaps the finished file in for the old one. A preview playing the old file (or a scan reading it) can
        /// hold it for a moment, so it tries for a few seconds before saying what's wrong; until then the old
        /// version stays untouched.
        /// </summary>
        static async Task PutInPlace(string tmp, string outPath, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(outPath)) File.Replace(tmp, outPath, null, true);
                    else File.Move(tmp, outPath);
                    return;
                }
                catch (IOException) when (attempt < 15) { await Task.Delay(400, ct); }
                catch (UnauthorizedAccessException) when (attempt < 15) { await Task.Delay(400, ct); }
                catch (IOException)
                {
                    throw new Exception("“" + Path.GetFileName(outPath) + "” is open in another program, so it couldn't be replaced. Close it and export again.");
                }
            }
        }

        /// <summary>Whether the file has a sound track.</summary>
        bool HasAudio(string file)
        {
            if (Ffprobe == null) return false;
            try
            {
                var j = new FfJob(Ffprobe, "-v error -select_streams a:0 -show_entries stream=index -of csv=p=0 " + Q(file));
                if (!j.Wait(15000)) j.Kill();
                j.Drain();
                return j.Output.Trim().Length > 0;
            }
            catch { return false; }
        }

        /// <summary>Length of one piece of a frame-by-frame export (see ExportAsync).</summary>
        const double PieceSeconds = 10;

        sealed class Relay : IProgress<JobStatus>
        {
            readonly Action<JobStatus> a;
            public Relay(Action<JobStatus> a) { this.a = a; }
            public void Report(JobStatus v) { a(v); }
        }

        static void CheckSpace(string folder, double needGB)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                double freeGB = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                if (freeGB < needGB)
                    throw new Exception(string.Format(Inv, "This export needs about {0:0} GB free on {1} for its temporary files (only {2:0} GB free). Free up some space, or choose a library folder on another drive.", needGB, root, freeGB));
            }
            catch (ArgumentException) { }
        }
    }
}
