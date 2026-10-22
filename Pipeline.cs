using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
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

        /// <summary>Suggested for sources smaller than the panel (after cropping to its shape).</summary>
        public bool EnhanceSuggested { get { return Width > 0 && Height > 0 && FfArgs.EnhanceScale(Width, Height) >= 1.15; } }

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
        public const int W = 2288, H = 1048;
        /// <summary>Fit any video to the HydroShift: cover the panel, then crop the overflow.</summary>
        public const string Fit = "scale=2288:1048:force_original_aspect_ratio=increase:flags=lanczos,crop=2288:1048,setsar=1";
        /// <summary>Crop to the panel's shape at the source's own size (for AI enhance, which scales it up after).</summary>
        public const string AspectCrop = "crop='min(iw,ih*2288/1048)':'min(ih,iw*1048/2288)',setsar=1";
        public const string ToYuv = "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p";
        public const string Tags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
        const string Quiet = "-y -hide_banner -loglevel error -nostats -progress pipe:2 ";
        public const double MaxUploadMB = 30;   // L-Connect 3's upload limit for custom themes

        /// <summary>
        /// Final encode. x264 "medium" at CRF 18 measured best on a 5090 + 28-thread CPU: as fast as NVENC here
        /// (the filters are the bottleneck, not the encoder) and 1.7-2.8x smaller at the same quality.
        /// The VBV cap only matters for long, very busy clips, and keeps the file under L-Connect's limit.
        /// </summary>
        public static string Encode(double seconds)
        {
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
            var enc = nvenc ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 16 -b:v 0" : "-c:v libx264 -preset ultrafast -crf 14";
            return Quiet + "-init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                   "-filter_complex \"ddagrab=output_idx=" + output + ":framerate=" + fps + ":draw_mouse=0,hwdownload,format=bgra," + ToYuv + "\" " +
                   "-t " + seconds + " " + enc + " " + Tags + " " + Q(dest);
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

        /// <summary>How much a source must grow to cover the panel after cropping to its shape (1 = already big enough).</summary>
        public static double EnhanceScale(int w, int h)
        {
            if (w <= 0 || h <= 0) return 1;
            double cw = Math.Min(w, h * (double)W / H), ch = Math.Min(h, w * (double)H / W);
            return Math.Max(W / cw, H / ch);
        }

        /// <summary>Real-ESRGAN's factor (2, 3 or 4) for a source: the smallest that covers the panel, 2 at least.</summary>
        public static int EsrganFactor(int w, int h)
        {
            double need = EnhanceScale(w, h);
            return need <= 2 ? 2 : need <= 3 ? 3 : 4;
        }

        /// <summary>Real-ESRGAN "animevideov3": a small, fast model made for video frames (no flicker between frames).</summary>
        public static string Esrgan(string inPath, string outPath, int factor, string modelDir)
        {
            return "-i " + Q(inPath) + " -o " + Q(outPath) + " -n realesr-animevideov3 -s " + factor + " -m " + Q(modelDir) + " -f png -j 4:4:4";
        }

        public static string Cadence(string src, double t0, double dur, string md5)
        {
            return Quiet + "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(src) + " -an -vf \"" + DupFilter + "\" -fps_mode passthrough -f framemd5 " + Q(md5);
        }

        /// <summary>
        /// Frames for RIFE / Real-ESRGAN: fitted to the panel, or only cropped to its shape when they'll be
        /// AI-enhanced first. Light PNG compression: 40% faster to write.
        /// </summary>
        public static string PrepPng(string src, double t0, double dur, double fps, string dir, bool fit = true)
        {
            return Quiet + "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(src) + " -vf \"" + (fit ? Fit : AspectCrop) + ",fps=" + Num(fps) + "\" -an -compression_level 1 " +
                   Q(Path.Combine(dir, "%08d.png"));
        }

        /// <summary>Fits a folder of (enhanced) frames to the panel, so RIFE works at panel size, not 4K.</summary>
        public static string FitPng(string inDir, double fps, string outDir)
        {
            return Quiet + "-framerate " + Num(fps) + " -i " + Q(Path.Combine(inDir, "%08d.png")) + " -vf \"" + Fit + "\" -compression_level 1 " +
                   Q(Path.Combine(outDir, "%08d.png"));
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

        public Pipeline(string ffmpeg, string ffprobe, string rifeDir, string esrganDir)
        {
            Ffmpeg = ffmpeg; Ffprobe = ffprobe;
            RifeDir = rifeDir;
            RifeExe = Path.Combine(RifeDir, "rife-ncnn-vulkan.exe");
            EsrganDir = esrganDir;
            EsrganExe = Path.Combine(EsrganDir, "realesrgan-ncnn-vulkan.exe");
        }

        /// <summary>Runs a frame tool (RIFE, Real-ESRGAN), reporting how many of <paramref name="target"/> frames are done.</summary>
        static async Task RunFrames(string exe, string args, string outDir, int target, string label, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var job = new FfJob(exe, args);
            var wait = job.WaitAsync(ct);
            while (!wait.IsCompleted)
            {
                int n = Directory.EnumerateFiles(outDir, "*.png").Count();
                pr.Report(new JobStatus(string.Format(Inv, "{0}…  {1:N0} of {2:N0} frames", label, n, target), Clamp((double)n / target, 0, 1)));
                await Task.WhenAny(wait, Task.Delay(500));
            }
            await wait;
            ct.ThrowIfCancellationRequested();
            int made = Directory.GetFiles(outDir, "*.png").Length;
            if (made < Math.Floor(target * 0.9)) throw new Exception(label + " failed: " + job.LastLine);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Runs ffmpeg, reporting progress against <paramref name="expectedSeconds"/> of output. Throws on failure.</summary>
        async Task RunFf(string args, double expectedSeconds, string label, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var job = new FfJob(Ffmpeg, args);
            var wait = job.WaitAsync(ct);
            while (!wait.IsCompleted)
            {
                double f = expectedSeconds > 0 ? Clamp(job.Seconds / expectedSeconds, 0, 1) : -1;
                pr.Report(new JobStatus(f >= 0 ? string.Format(Inv, "{0}…  {1:0}%", label, f * 100) : label + "…", f));
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
        public async Task<string> RecordAsync(int adapter, int output, int fps, int seconds, string encoder, string dest,
                                              StopSignal stop, IProgress<JobStatus> pr, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            foreach (var enc in encoder == "nvenc" ? new[] { "nvenc", "x264" } : new[] { "x264" })
            {
                TryDelete(dest);
                var job = new FfJob(Ffmpeg, FfArgs.Record(adapter, output, fps, seconds, enc == "nvenc", dest));
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
                            if (rel.Length == 0 || rel.EndsWith("/")) continue;
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

        /// <summary>Downloads Real-ESRGAN (ncnn/Vulkan build, ~50 MB) once: the exe and the video model only.</summary>
        public async Task EnsureEsrganAsync(IProgress<JobStatus> pr, CancellationToken ct)
        {
            if (HasEsrgan) return;
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
                            if (!Regex.IsMatch(rel, "^(realesrgan-ncnn-vulkan\\.exe|vcomp140\\.dll|models/realesr-animevideov3-x\\d\\.(bin|param))$")) continue;
                            var dest = Path.Combine(EsrganDir, rel.Replace('/', '\\'));
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                        }
                }, ct);
            }
            finally { TryDelete(zip); }
        }

        /// <summary>
        /// One AI-enhanced frame for the editor preview, at <paramref name="w"/> × <paramref name="h"/> as RGB24
        /// (null on failure). Takes a second or two: the model loads for each call.
        /// </summary>
        public byte[] EnhanceFrame(string src, double seconds, int srcW, int srcH, int w, int h)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hsr_enh_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                var a = Path.Combine(dir, "a.png");
                var b = Path.Combine(dir, "b.png");
                var j = new FfJob(Ffmpeg, "-y -hide_banner -loglevel error -ss " + Num(Math.Max(0, seconds)) + " -i " + Q(src) +
                                          " -frames:v 1 -an -vf \"" + FfArgs.AspectCrop + "\" " + Q(a));
                if (!j.Wait(20000)) j.Kill();
                j.Drain();
                if (!File.Exists(a)) return null;
                var e = new FfJob(EsrganExe, FfArgs.Esrgan(a, b, FfArgs.EsrganFactor(srcW, srcH), Path.Combine(EsrganDir, "models")));
                if (!e.Wait(60000)) e.Kill();
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
            var md5 = Path.Combine(Path.GetTempPath(), "hsr_frames_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try
            {
                await RunFf(FfArgs.Cadence(src, t0, dur, md5), dur, "Checking how smoothly it moved", pr, ct);
                double unique;
                return File.Exists(md5) ? CadenceGap(File.ReadAllText(md5), dur, out unique) : 0;
            }
            finally { TryDelete(md5); }
        }

        // ------------------------------------------------------------ export

        /// <summary>
        /// Renders the session to <paramref name="outPath"/>: trim, optional smoothing to 60 fps, the OLED look,
        /// the loop crossfade, and the size-capped encode. Writes to a temporary file first and only then replaces
        /// the target, so a failed or cancelled export never destroys the previous version.
        /// </summary>
        public async Task ExportAsync(EditSession s, string outPath, IProgress<JobStatus> pr, CancellationToken ct)
        {
            double t0 = s.UsedStart, dur = s.UsedLength;
            if (dur < 1) throw new Exception("The selected part is shorter than a second.");
            string folder = Path.GetDirectoryName(outPath);
            Directory.CreateDirectory(folder);

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
            string work = null, input, pre;
            try
            {
                if (from > 0 || enhance)
                {
                    // 2a. frame by frame: AI enhance and/or RIFE smoothing, then back in as a video
                    if (enhance) await EnsureEsrganAsync(pr, ct);
                    if (from > 0) await EnsureRifeAsync(pr, ct);
                    work = Path.Combine(folder, ".hydroshift", "temp");   // same drive as the library
                    TryDeleteDir(work);
                    double rate = from > 0 ? from : Math.Min(60, s.Fps > 0 ? s.Fps : 60);
                    int factor = FfArgs.EsrganFactor(s.Width, s.Height);
                    double perFrameMB = 6 + (enhance ? 6.0 * factor * factor / 4 : 0);
                    CheckSpace(folder, dur * (rate * perFrameMB + (from > 0 ? 60 * 6 : 0)) / 1024);

                    string frames = Path.Combine(work, "in");
                    Directory.CreateDirectory(frames);
                    await RunFf(FfArgs.PrepPng(s.SourcePath, t0, dur, rate, frames, !enhance), dur, "Preparing frames", pr, ct);
                    int count = Directory.GetFiles(frames, "*.png").Length;
                    if (count < 2) throw new Exception("Preparing frames failed: no frames were written.");

                    if (enhance)
                    {
                        var esr = Path.Combine(work, "enhanced");
                        Directory.CreateDirectory(esr);
                        await RunFrames(EsrganExe, FfArgs.Esrgan(frames, esr, factor, Path.Combine(EsrganDir, "models")), esr, count,
                                        "AI enhancing " + factor + "×", pr, ct);
                        TryDeleteDir(frames);
                        frames = esr;
                        if (from > 0)
                        {
                            // RIFE at panel size, not at the enhanced size
                            var fit = Path.Combine(work, "fit");
                            Directory.CreateDirectory(fit);
                            await RunFf(FfArgs.FitPng(esr, rate, fit), count / rate, "Fitting frames", pr, ct);
                            TryDeleteDir(esr);
                            frames = fit;
                        }
                    }

                    if (from > 0)
                    {
                        var outDir = Path.Combine(work, "out");
                        Directory.CreateDirectory(outDir);
                        int target = (int)Math.Round(count * 60 / from);
                        await RunFrames(RifeExe, FfArgs.Rife(frames, outDir, target, Path.Combine(RifeDir, "rife-v4.6")), outDir, target,
                                        string.Format(Inv, "Smoothing {0:0} → 60 fps", from), pr, ct);
                        frames = outDir;
                        rate = 60;
                    }

                    int made = Directory.GetFiles(frames, "*.png").Length;
                    input = "-framerate " + Num(rate) + " -i " + Q(Path.Combine(frames, "%08d.png"));
                    pre = enhance && from <= 0 ? FfArgs.Fit + "," : "";
                    dur = made / rate;
                }
                else
                {
                    // 2b. straight from the source
                    bool over = s.Fps > 61;
                    input = "-ss " + Num(t0) + " -t " + Num(dur) + " -i " + Q(s.SourcePath);
                    pre = FfArgs.Fit + "," + (over ? "fps=60," : "");
                }

                // 3. the look, the loop, the encode
                if (s.TrueBlack) pre += Oled.Filter(s.Oled);
                double outSec;
                var graph = FfArgs.FinishGraph(pre, dur, s.Loop, 1, out outSec);
                var tmp = outPath + ".partial.mp4";
                TryDelete(tmp);
                var meta = "-metadata comment=" + Q("hsr|" + s.Id + "|" + s.Kind + (s.TrueBlack ? "|tb" : "")) + " -metadata title=" + Q(s.Title ?? "");
                await RunFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 " + input + " -filter_complex \"" + graph + "\" -map \"[v]\" -an " +
                            FfArgs.Encode(outSec) + " " + meta + " " + Q(tmp),
                            outSec, s.Loop ? "Finishing and looping" : "Finishing", pr, ct);
                if (!File.Exists(tmp)) throw new Exception("Finishing failed: no file was written.");
                TryDelete(outPath);
                File.Move(tmp, outPath);
            }
            finally
            {
                TryDelete(outPath + ".partial.mp4");
                if (work != null) TryDeleteDir(work);
            }
        }

        static void CheckSpace(string folder, double needGB)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                double freeGB = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                if (freeGB < needGB)
                    throw new Exception(string.Format(Inv, "Smoothing this clip needs about {0:0} GB free on {1} (only {2:0} GB free). Choose a library folder on another drive, or use a shorter part of the clip.", needGB, root, freeGB));
            }
            catch (ArgumentException) { }
        }
    }
}
