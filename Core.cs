using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OLEDable
{
    /// <summary>Runs ffmpeg / ffprobe / RIFE hidden in the background, collecting output and ffmpeg progress.</summary>
    public class FfJob
    {
        static readonly Regex ProgressKey = new Regex(
            "^(frame|fps|stream_\\d+_\\d+_q|bitrate|total_size|out_time|dup_frames|drop_frames|speed|progress)=",
            RegexOptions.Compiled);

        readonly Process p;
        readonly StringBuilder output = new StringBuilder();
        readonly TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long outUs;
        volatile bool finished;   // set by Drain(): exit code captured and the process handle released
        int exitCode;

        public FfJob(string exe, string args)
        {
            var si = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            };
            p = new Process { StartInfo = si, EnableRaisingEvents = true };
            p.ErrorDataReceived += OnLine;
            p.OutputDataReceived += OnLine;
            p.Exited += (s, e) => exited.TrySetResult(true);
            p.Start();
            ChildProcesses.Track(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
        }

        void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                long v;
                if (long.TryParse(e.Data.Substring(12), out v)) Interlocked.Exchange(ref outUs, v);
                return;
            }
            if (ProgressKey.IsMatch(e.Data)) return;
            lock (output)
            {
                output.AppendLine(e.Data);
                if (output.Length > 16000) output.Remove(0, output.Length - 16000);
            }
        }

        public double Seconds { get { return Interlocked.Read(ref outUs) / 1e6; } }
        public string Output { get { lock (output) return output.ToString(); } }

        /// <summary>Asks ffmpeg to stop gracefully (keeps what was written so far).</summary>
        public void Quit() { if (finished) return; try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { } }
        public void Kill() { if (finished) return; try { if (!p.HasExited) p.Kill(); } catch { } }
        public bool Wait(int ms) { if (finished) return true; try { return p.WaitForExit(ms); } catch { return true; } }

        /// <summary>Waits for exit and for all output to be read, then releases the process handle. Safe to call twice.</summary>
        public void Drain()
        {
            if (finished) return;
            try { p.WaitForExit(); exitCode = p.ExitCode; } catch { exitCode = -1; }
            finished = true;
            try { p.Dispose(); } catch { }
        }

        /// <summary>Waits without blocking; cancelling kills the process. Returns the exit code.</summary>
        public async Task<int> WaitAsync(CancellationToken ct)
        {
            using (ct.Register(Kill))
                await exited.Task.ConfigureAwait(false);
            await Task.Run(() => Drain()).ConfigureAwait(false);
            return exitCode;
        }

        public string LastLine
        {
            get
            {
                var lines = Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].Trim().Length > 0) return lines[i].Trim();
                return "unknown error";
            }
        }
    }

    /// <summary>Downloads a file with progress and cancel.</summary>
    public static class Downloader
    {
        public static Task Download(string url, string path, IProgress<double> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "OLEDable";
                req.ReadWriteTimeout = 60000;   // a stalled connection fails instead of hanging forever
                try
                {
                    using (var resp = req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var f = File.Create(path))
                    {
                        long total = resp.ContentLength, got = 0;
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            f.Write(buf, 0, n);
                            got += n;
                            if (total > 0 && progress != null) progress.Report((double)got / total);
                        }
                        // a dropped connection can end the stream early without an error
                        if (total > 0 && got != total) throw new IOException("The download was cut off. Try again.");
                    }
                }
                catch { Tools.TryDelete(path); throw; }
            }, ct);
        }
    }

    [DataContract]
    public class Settings
    {
        [DataMember] public int Seconds;
        [DataMember] public int Fps;
        [DataMember] public bool Loop;
        [DataMember] public bool TrueBlack;
        [DataMember] public bool Countdown;
        [DataMember] public bool Smooth;
        [DataMember] public double Crossfade;
        [DataMember] public string OutDir;
        [DataMember] public string Encoder;
        [DataMember] public int Adapter;
        [DataMember] public int LastAdapter;   // where the HydroShift was last seen
        [DataMember] public int LastOutput;
        [DataMember] public int Output;
        // last-used OLED settings (the black level itself is detected for each video)
        [DataMember] public double OledShadows;
        [DataMember] public double OledColor;
        [DataMember] public bool OledCleanup;
        [DataMember] public double OledBright;
        [DataMember] public double OledHighlights;
        [DataMember] public double OledGlow;
        [DataMember] public double OledGlowSize;
        // appearance and library
        [DataMember] public string Theme;
        [DataMember] public string Accent;
        [DataMember] public bool FollowWindows;
        [DataMember] public bool KeepSources;
        /// <summary>Where exports go; empty = a "Converted" folder inside the library.</summary>
        [DataMember] public string ConvertedDir;
        /// <summary>Set once the user was offered to move older exports into the converted folder.</summary>
        [DataMember] public bool ConvertedOffered;
        // tools: empty = find automatically (set by the installer when the user picks them)
        [DataMember] public string FfmpegPath;
        [DataMember] public string RifeDir;
        [DataMember] public string EsrganDir;
        // last-used AI enhance choices (new videos start from these)
        [DataMember] public string EnhanceStyle;
        [DataMember] public double EnhanceStrength;
        [DataMember] public bool EnhanceCleanup;
        [DataMember] public bool EnhanceTta;
        [DataMember] public bool EnhanceSharpen;
        /// <summary>Measured export step speeds on this PC, for time estimates (see <see cref="SpeedBook"/>).</summary>
        [DataMember] public Dictionary<string, double> Rates;
        // updates (see Updater)
        [DataMember] public string UpdateChannel;
        [DataMember] public bool AutoUpdateCheck;
        [DataMember] public DateTime LastUpdateCheck;
        /// <summary>A version the user dismissed; not offered again (a newer one is).</summary>
        [DataMember] public string SkippedVersion;

        public Settings() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext c) { SetDefaults(); }

        void SetDefaults()
        {
            Seconds = 45; Fps = 60; Loop = true; TrueBlack = true; Countdown = true; Smooth = true; Crossfade = 1;
            OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OLEDable");
            Encoder = "nvenc"; Adapter = -1; Output = -1; LastAdapter = 0; LastOutput = 2;
            OledShadows = 1.2; OledColor = 1.08; OledCleanup = true;
            OledBright = 0; OledHighlights = 0; OledGlow = 0; OledGlowSize = 20;
            Theme = "dark"; Accent = "#A970FF"; FollowWindows = false; KeepSources = true;
            EnhanceStyle = "anime"; EnhanceStrength = 0.75; EnhanceCleanup = false; EnhanceTta = false; EnhanceSharpen = false;
            Rates = new Dictionary<string, double>();
            UpdateChannel = "stable"; AutoUpdateCheck = true;
        }

        /// <summary>The look the editor starts from for a new video (black level is filled in automatically).</summary>
        public OledParams DefaultOled()
        {
            return new OledParams
            {
                Shadows = OledShadows, Color = OledColor, Cleanup = OledCleanup,
                Bright = OledBright, Highlights = OledHighlights, Glow = OledGlow, GlowSize = OledGlowSize,
            };
        }

        /// <summary>The folder exports go to.</summary>
        public string ConvertedPath
        {
            get { return string.IsNullOrWhiteSpace(ConvertedDir) ? Path.Combine(OutDir, "Converted") : ConvertedDir; }
        }

        /// <summary>Gives a new session the last-used AI enhance choices.</summary>
        public void FillEnhance(EditSession s)
        {
            if (s.EnhanceStyle != null) return;
            s.EnhanceStyle = EnhanceStyle ?? "anime";
            s.EnhanceStrength = EnhanceStrength > 0 ? EnhanceStrength : 0.75;
            s.EnhanceCleanup = EnhanceCleanup; s.EnhanceTta = EnhanceTta; s.EnhanceSharpen = EnhanceSharpen;
        }

        public void RememberEnhance(EditSession s)
        {
            if (s.EnhanceStyle == null) return;
            EnhanceStyle = s.EnhanceStyle; EnhanceStrength = s.EnhanceStrength;
            EnhanceCleanup = s.EnhanceCleanup; EnhanceTta = s.EnhanceTta; EnhanceSharpen = s.EnhanceSharpen;
        }

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = Json.Read<Settings>(File.ReadAllText(path));
                    if (string.IsNullOrWhiteSpace(s.OutDir)) s.OutDir = new Settings().OutDir;
                    if (s.Crossfade <= 0) s.Crossfade = 1;
                    if (s.OledShadows < 1 || s.OledShadows > 2) s.OledShadows = 1.2;
                    if (s.OledColor < 0.8 || s.OledColor > 1.5) s.OledColor = 1.08;
                    if (s.OledGlowSize < 6 || s.OledGlowSize > 48) s.OledGlowSize = 20;
                    if (s.Theme != "light" && s.Theme != "dark" && s.Theme != "black") s.Theme = "dark";
                    if (string.IsNullOrWhiteSpace(s.Accent)) s.Accent = "#A970FF";
                    if (s.Rates == null) s.Rates = new Dictionary<string, double>();
                    if (s.UpdateChannel != "dev") s.UpdateChannel = "stable";
                    if (s.EnhanceStrength <= 0 || s.EnhanceStrength > 1) s.EnhanceStrength = 0.75;
                    return s;
                }
            }
            catch { }
            return new Settings();
        }

        public void Save(string path) { try { Tools.WriteAtomic(path, Json.Write(this)); } catch { } }
    }

    public static class Json
    {
        public static T Read<T>(string text)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        public static string Write<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    /// <summary>The bits of a Wallpaper Engine project.json we care about.</summary>
    [DataContract]
    public class WeProject
    {
        [DataMember(Name = "type")] public string Type;
        [DataMember(Name = "file")] public string File;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "preview")] public string Preview;

        public static WeProject Load(string path) { return Json.Read<WeProject>(System.IO.File.ReadAllText(path)); }
    }

    public class VideoInfo
    {
        public double Fps;
        public double Duration;
        public int Width, Height;
        /// <summary>The container's "comment" tag; OLEDable (formerly HydroShift Recorder) writes "oled|id|kind" there ("hsr|…" before the rename).</summary>
        public string Comment;
    }

    public static class Tools
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Writes a file so that it's never left half-written: a crash or power cut during the save leaves the
        /// previous version, not a broken file (which would mean lost settings or edits).
        /// </summary>
        public static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Appends an unexpected error to logs\errors.txt in the app's data folder (kept small).</summary>
        public static void LogError(string where, Exception ex)
        {
            try
            {
                var dir = Path.Combine(DataDir(), "logs");
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "errors.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s", Inv) + "  " + where + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Finds an exe on PATH (including PATH changes made after this app started) or in WinGet's links folder.</summary>
        public static string Find(string name)
        {
            var dirs = new List<string>();
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                var path = Environment.GetEnvironmentVariable("PATH", target);
                if (!string.IsNullOrEmpty(path)) dirs.AddRange(path.Split(';'));
            }
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
            foreach (var d in dirs)
            {
                try
                {
                    var dir = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
                    if (dir.Length == 0) continue;
                    var f = Path.Combine(dir, name + ".exe");
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        public static string Q(string s) { return "\"" + s + "\""; }
        public static string Num(double v) { return v.ToString("0.####", Inv); }
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public static string Fmt(double sec)
        {
            int s = (int)Math.Floor(Math.Max(0, sec));
            return string.Format(Inv, "{0}:{1:00}", s / 60, s % 60);
        }

        public static double ParseRate(string r)
        {
            var m = Regex.Match(r ?? "", "^(\\d+)/(\\d+)$");
            if (m.Success)
            {
                double n = double.Parse(m.Groups[1].Value, Inv), d = double.Parse(m.Groups[2].Value, Inv);
                if (d > 0) return n / d;
            }
            return 0;
        }

        /// <summary>"90", "90s", "1:30", "2m" -> seconds, or null.</summary>
        public static int? ParseLength(string t)
        {
            t = (t ?? "").Trim().ToLowerInvariant();
            var m = Regex.Match(t, "^(\\d+):(\\d{1,2})$");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
            m = Regex.Match(t, "^(\\d+)\\s*m(in)?$");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 60;
            m = Regex.Match(t, "^(\\d+)\\s*s?(ec)?$");
            if (m.Success) return int.Parse(m.Groups[1].Value);
            return null;
        }

        public static VideoInfo Probe(string ffprobe, string file)
        {
            if (ffprobe == null) return null;
            try
            {
                var j = new FfJob(ffprobe, "-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate,width,height:format=duration:format_tags=comment -of default=noprint_wrappers=1 " + Q(file));
                if (!j.Wait(15000)) j.Kill();
                j.Drain();
                var kv = new Dictionary<string, string>();
                foreach (var line in j.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                if (!kv.ContainsKey("r_frame_rate") && !kv.ContainsKey("avg_frame_rate")) return null;
                string v;
                // the nominal rate (r_frame_rate) is the real one for normal videos; the average drifts with container
                // timing (a 60 fps clip can report 55), so it's only a fallback for odd variable-rate files
                double fps = kv.TryGetValue("r_frame_rate", out v) ? ParseRate(v) : 0;
                if (fps <= 0 || fps > 240) fps = kv.TryGetValue("avg_frame_rate", out v) ? ParseRate(v) : 0;
                double dur = 0;
                if (kv.TryGetValue("duration", out v)) double.TryParse(v, NumberStyles.Float, Inv, out dur);
                int w = 0, h = 0;
                if (kv.TryGetValue("width", out v)) int.TryParse(v, out w);
                if (kv.TryGetValue("height", out v)) int.TryParse(v, out h);
                kv.TryGetValue("TAG:comment", out v);
                return new VideoInfo { Fps = fps, Duration = dur, Width = w, Height = h, Comment = v };
            }
            catch { return null; }
        }

        public static Task<VideoInfo> ProbeAsync(string ffprobe, string file) { return Task.Run(() => Probe(ffprobe, file)); }

        /// <summary>ffmpeg filter that drops repeated frames, used to see how often a recording really changed.</summary>
        public const string DupFilter = "scale=572:262:flags=area,mpdecimate=hi=64*1.5:lo=32:frac=0.001";

        /// <summary>
        /// Reads ffmpeg framemd5 output of a 60 fps recording run through <see cref="DupFilter"/> and returns
        /// how many capture frames each real wallpaper frame lasted (2 = it rendered at 30 fps, 3 = 20 fps …),
        /// or 0 if it really was smooth 60 fps, barely moved, or changed irregularly (slow drifts, small
        /// animations), where filling in frames would make the motion uneven instead of smoother.
        /// </summary>
        public static int CadenceGap(string framemd5Text, double seconds, out double uniqueFps)
        {
            uniqueFps = 0;
            long num = 1, den = 1;
            var idx = new List<long>();
            foreach (var raw in framemd5Text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#tb"))
                {
                    var m = Regex.Match(line, ":\\s*(\\d+)/(\\d+)");
                    if (m.Success) { num = long.Parse(m.Groups[1].Value); den = long.Parse(m.Groups[2].Value); }
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                long pts;
                if (parts.Length > 2 && long.TryParse(parts[2].Trim(), out pts))
                    idx.Add((long)Math.Round(pts * (double)num / den * 60));
            }
            if (seconds > 0) uniqueFps = idx.Count / seconds;
            if (idx.Count < 20 || uniqueFps < 12 || uniqueFps > 50) return 0;

            var counts = new Dictionary<long, int>();
            for (int i = 1; i < idx.Count; i++)
            {
                long g = idx[i] - idx[i - 1];
                int c; counts.TryGetValue(g, out c); counts[g] = c + 1;
            }
            long best = 0; int bestN = 0;
            foreach (var kv in counts) if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
            double share = (double)bestN / (idx.Count - 1);
            return best >= 2 && best <= 5 && share >= 0.75 ? (int)best : 0;
        }

        /// <summary>Steam's Wallpaper Engine workshop folder, if it can be found.</summary>
        public static string FindWorkshopDir()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = k == null ? null : k.GetValue("SteamPath") as string;
                    if (steam == null) return null;
                    var libs = new List<string> { steam };
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    foreach (var l in libs)
                    {
                        var p = Path.Combine(l.Replace('/', '\\'), "steamapps", "workshop", "content", "431960");
                        if (Directory.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>The app's folder if it's writable (portable), otherwise %LOCALAPPDATA%\OLEDable (or the old HydroShift Recorder folder if that exists).</summary>
        public static string DataDir()
        {
            var app = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                var probe = Path.Combine(app, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return app;
            }
            catch
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var d = Path.Combine(local, "HydroShift Recorder");
                if (!Directory.Exists(d)) d = Path.Combine(local, "OLEDable");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void TryDelete(string f) { try { if (f != null && File.Exists(f)) File.Delete(f); } catch { } }
        public static void TryDeleteDir(string d) { try { if (d != null && Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        /// <summary>Strips characters Windows doesn't allow in file names.</summary>
        public static string SafeName(string n) { return Regex.Replace(n ?? "", "[\\\\/:*?\"<>|]", "").Trim(); }

        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

        /// <summary>A still image the app can OLED-ify (exported as PNG).</summary>
        public static bool IsImage(string path)
        {
            var e = Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(ImageExt, e) >= 0 && !(path ?? "").EndsWith(".partial.png", StringComparison.OrdinalIgnoreCase);
        }
    }
}
