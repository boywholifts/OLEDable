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

namespace HydroShiftRecorder
{
    /// <summary>Runs ffmpeg / ffprobe / RIFE hidden in the background, collecting output and ffmpeg progress.</summary>
    public class FfJob
    {
        static readonly Regex ProgressKey = new Regex(
            "^(frame|fps|stream_\\d+_\\d+_q|bitrate|total_size|out_time|dup_frames|drop_frames|speed|progress)=",
            RegexOptions.Compiled);

        readonly Process p;
        readonly StringBuilder output = new StringBuilder();
        long outUs;

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
            p = new Process { StartInfo = si };
            p.ErrorDataReceived += OnLine;
            p.OutputDataReceived += OnLine;
            p.Start();
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

        public bool Done { get { return p.HasExited; } }
        public int Code { get { return p.ExitCode; } }
        public double Seconds { get { return Interlocked.Read(ref outUs) / 1e6; } }
        public string Output { get { lock (output) return output.ToString(); } }

        /// <summary>Asks ffmpeg to stop gracefully (keeps what was written so far).</summary>
        public void Quit() { try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { } }
        public void Kill() { try { if (!p.HasExited) p.Kill(); } catch { } }
        public bool Wait(int ms) { return p.WaitForExit(ms); }
        /// <summary>Waits for exit and for all output to be read.</summary>
        public void Drain() { try { p.WaitForExit(); } catch { } }

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

    /// <summary>Downloads a file on a background thread with progress and cancel.</summary>
    public class Downloader
    {
        long got, total;
        public volatile bool Done, Cancel;
        public volatile string Error;
        public long Got { get { return Interlocked.Read(ref got); } }
        public long Total { get { return Interlocked.Read(ref total); } }

        public Downloader(string url, string path)
        {
            var t = new Thread(() => Run(url, path)) { IsBackground = true };
            t.Start();
        }

        void Run(string url, string path)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "HydroShift-Recorder";
                using (var resp = req.GetResponse())
                {
                    Interlocked.Exchange(ref total, resp.ContentLength);
                    using (var s = resp.GetResponseStream())
                    using (var f = File.Create(path))
                    {
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (Cancel) throw new OperationCanceledException("cancelled");
                            f.Write(buf, 0, n);
                            Interlocked.Add(ref got, n);
                        }
                    }
                }
            }
            catch (Exception e) { Error = e.Message; }
            Done = true;
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
        [DataMember] public int Output;

        public Settings() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext c) { SetDefaults(); }

        void SetDefaults()
        {
            Seconds = 45; Fps = 60; Loop = true; TrueBlack = true; Countdown = true; Smooth = true; Crossfade = 1;
            OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HydroShift");
            Encoder = "nvenc"; Adapter = -1; Output = -1;
        }

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path)); // ReadAllText strips any BOM
                    using (var ms = new MemoryStream(bytes))
                    {
                        var s = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(ms);
                        if (string.IsNullOrWhiteSpace(s.OutDir)) s.OutDir = new Settings().OutDir;
                        if (s.Crossfade <= 0) s.Crossfade = 1;
                        return s;
                    }
                }
            }
            catch { }
            return new Settings();
        }

        public void Save(string path)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(Settings)).WriteObject(ms, this);
                    File.WriteAllBytes(path, ms.ToArray());
                }
            }
            catch { }
        }
    }

    /// <summary>The bits of a Wallpaper Engine project.json we care about.</summary>
    [DataContract]
    public class WeProject
    {
        [DataMember(Name = "type")] public string Type;
        [DataMember(Name = "file")] public string File;
        [DataMember(Name = "title")] public string Title;

        public static WeProject Load(string path)
        {
            var bytes = Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(path));
            using (var ms = new MemoryStream(bytes))
                return (WeProject)new DataContractJsonSerializer(typeof(WeProject)).ReadObject(ms);
        }
    }

    public class VideoInfo
    {
        public double Fps;
        public double Duration;
    }

    public static class Tools
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
                var j = new FfJob(ffprobe, "-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate:format=duration -of default=noprint_wrappers=1 " + Q(file));
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
                double fps = kv.TryGetValue("avg_frame_rate", out v) ? ParseRate(v) : 0;
                if (fps <= 0 || fps > 240) fps = kv.TryGetValue("r_frame_rate", out v) ? ParseRate(v) : 0;
                double dur = 0;
                if (kv.TryGetValue("duration", out v)) double.TryParse(v, NumberStyles.Float, Inv, out dur);
                return new VideoInfo { Fps = fps, Duration = dur };
            }
            catch { return null; }
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

        /// <summary>The app's folder if it's writable (portable), otherwise %LOCALAPPDATA%\HydroShift Recorder.</summary>
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
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HydroShift Recorder");
                Directory.CreateDirectory(d);
                return d;
            }
        }
    }
}
