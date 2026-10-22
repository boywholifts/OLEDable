using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    public partial class MainWindow : Window
    {
        enum State { Idle, Countdown, Recording, Tuning, Download, Prep, Rife, Processing }

        const string RifeUrl = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip";
        const string Fit = "scale=2288:1048:force_original_aspect_ratio=increase:flags=lanczos,crop=2288:1048,setsar=1";
        const string ToYuv = "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p";
        const string Tags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
        const string EncFinal = "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p " + Tags + " -movflags +faststart";

        readonly string dataDir, settingsPath, rifeDir, rifeExe, ff, ffprobe;
        readonly string raw = Path.Combine(Path.GetTempPath(), "hsr_raw.mp4");
        readonly Settings S;
        readonly DispatcherTimer timer;
        readonly Brush red, grey, purple;

        State state = State.Idle;
        FfJob job, previewJob;
        Downloader dl;
        OledParams oled;   // true-black settings chosen in the tuner for the current job
        bool cancelled, syncLen;
        string previewPng, workDir, inDir, outDir, outFile, zipPath, src, srcName, procLabel;
        double srcFps, convT, expected, outFps;
        int nOut, tickN;
        DateTime countEnd;

        public MainWindow()
        {
            InitializeComponent();

            red = Hex("#FFFF453A"); grey = Hex("#FF8E8E93"); purple = Hex("#FFBF5AF2");
            dataDir = DataDir();
            settingsPath = Path.Combine(dataDir, "HydroShift-Recorder.json");
            rifeDir = Path.Combine(dataDir, "rife");
            rifeExe = Path.Combine(rifeDir, "rife-ncnn-vulkan.exe");
            ff = Find("ffmpeg");
            ffprobe = Find("ffprobe");
            S = Settings.Load(settingsPath);

            // reuse the display found by the old PowerShell script, if it was in this folder
            var oldCfg = Path.Combine(dataDir, "hydroshift-capture.json");
            if (S.Adapter < 0 && File.Exists(oldCfg))
            {
                try
                {
                    var t = File.ReadAllText(oldCfg);
                    var a = Regex.Match(t, "\"Adapter\"\\s*:\\s*(\\d+)");
                    var o = Regex.Match(t, "\"Output\"\\s*:\\s*(\\d+)");
                    if (t.Contains("\"dda\"") && a.Success && o.Success) { S.Adapter = int.Parse(a.Groups[1].Value); S.Output = int.Parse(o.Groups[1].Value); }
                }
                catch { }
            }

            // controls from settings
            if (S.Seconds > LenSlider.Maximum) LenSlider.Maximum = S.Seconds;
            SetLength(S.Seconds);
            if (S.Fps == 30) Fps30.IsChecked = true; else Fps60.IsChecked = true;
            LoopSw.IsChecked = S.Loop;
            BlackSw.IsChecked = S.TrueBlack;
            SmoothSw.IsChecked = S.Smooth;
            CountSw.IsChecked = S.Countdown;
            SaveText.Text = S.OutDir;

            WireEvents();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) => OnTick();
            timer.Start();
        }

        // ---------------------------------------------------------------- helpers

        static Brush Hex(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        void SaveSettings() { S.Save(settingsPath); }
        void SetStatus(string t) { Status.Text = t; }
        void FlushUI() { Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background); }

        void UpdateLenText() { LenText.Text = S.Seconds < 60 ? S.Seconds + " s" : Fmt(S.Seconds); }

        void SetLength(int v)
        {
            v = (int)Clamp(v, 5, 600);
            S.Seconds = v;
            syncLen = true;
            if (v > LenSlider.Maximum) LenSlider.Maximum = v;
            LenSlider.Value = v;
            syncLen = false;
            UpdateLenText();
        }

        void CommitLenBox()
        {
            if (LenBox.Visibility != Visibility.Visible) return;
            var v = ParseLength(LenBox.Text);
            if (v.HasValue) { SetLength(v.Value); SaveSettings(); }
            LenBox.Visibility = Visibility.Collapsed;
            LenText.Visibility = Visibility.Visible;
        }

        void ReadControls()
        {
            S.Fps = Fps30.IsChecked == true ? 30 : 60;
            S.Loop = LoopSw.IsChecked == true;
            S.TrueBlack = BlackSw.IsChecked == true;
            S.Smooth = SmoothSw.IsChecked == true;
            S.Countdown = CountSw.IsChecked == true;
        }

        void SetBusy(bool busy)
        {
            foreach (FrameworkElement c in new FrameworkElement[] { SettingsCard, NameCard })
            {
                c.IsEnabled = !busy;
                c.Opacity = busy ? 0.5 : 1;
            }
            RedetectBtn.IsEnabled = !busy;
            ConvertBtn.IsEnabled = !busy && ff != null;
        }

        void SetRecLook(string look)
        {
            if (look == "circle")
            {
                RecInner.Width = 60; RecInner.Height = 60; RecInner.CornerRadius = new CornerRadius(30);
                RecInner.Background = red; RecBtn.ToolTip = "Record";
            }
            else
            {
                RecInner.Width = 28; RecInner.Height = 28; RecInner.CornerRadius = new CornerRadius(7);
                RecInner.Background = look == "grey" ? grey : red;
                RecBtn.ToolTip = look == "grey" ? "Cancel" : "Stop and keep what was recorded";
            }
        }

        void ShowPill(string text, Brush dot)
        {
            RecPill.Visibility = Visibility.Visible;
            RecTime.Text = text;
            RecDot.Fill = dot;
            RecDot.Opacity = 1;
        }

        void RemoveWork()
        {
            try { if (File.Exists(raw)) File.Delete(raw); } catch { }
            try { if (workDir != null && Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }

        void ResetIdle(string msg)
        {
            state = State.Idle; job = null; dl = null;
            RemoveWork();
            SetBusy(false);
            SetRecLook("circle");
            RecCount.Text = "";
            RecPill.Visibility = Visibility.Collapsed;
            Prog.Visibility = Visibility.Hidden;
            Prog.Value = 0;
            SetStatus(msg);
            RefreshPreview();
        }

        string GetOutPath(string fallback)
        {
            Directory.CreateDirectory(S.OutDir);
            var n = NameBox.Text;
            if (string.IsNullOrWhiteSpace(n)) n = fallback ?? "";
            n = Regex.Replace(n, "[\\\\/:*?\"<>|]", "").Trim();
            if (n.Length == 0) n = "HydroShift " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");
            var p = Path.Combine(S.OutDir, n + ".mp4");
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(S.OutDir, n + " (" + i + ").mp4");
            return p;
        }

        FfJob StartFf(string args) { return new FfJob(ff, args); }

        // ---------------------------------------------------------------- preview / finding the HydroShift

        static string GrabArgs(int adapter, int output, string png)
        {
            return "-y -hide_banner -loglevel error -init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                   "-filter_complex \"ddagrab=output_idx=" + output + ":framerate=1:draw_mouse=0,hwdownload,format=bgra\" -frames:v 1 " + Q(png);
        }

        static bool IsHsSize(int w, int h) { return (w == 2288 && h == 1048) || (w == 1048 && h == 2288); }

        void ShowImage(string png, string label)
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.UriSource = new Uri(png);
            b.EndInit();
            b.Freeze();
            Preview.Background = new ImageBrush(b) { Stretch = Stretch.UniformToFill };
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewInfo.Text = label + "  ·  " + b.PixelWidth + " × " + b.PixelHeight;
        }

        void ShowPreviewMessage(string text)
        {
            Preview.Background = Brushes.Black;
            PreviewHint.Visibility = Visibility.Visible;
            PreviewHint.Text = text;
            PreviewInfo.Text = "";
        }

        void RefreshPreview()
        {
            if (ff == null || S.Adapter < 0 || previewJob != null || state != State.Idle) return;
            previewPng = Path.Combine(Path.GetTempPath(), "hsr_prev_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            previewJob = StartFf(GrabArgs(S.Adapter, S.Output, previewPng));
        }

        string FindHydroShift()
        {
            var png = Path.Combine(Path.GetTempPath(), "hsr_probe.png");
            for (int a = 0; a <= 3; a++)
            {
                for (int o = 0; o <= 5; o++)
                {
                    try { File.Delete(png); } catch { }
                    var j = StartFf(GrabArgs(a, o, png));
                    if (!j.Wait(10000)) j.Kill();
                    j.Drain();
                    if (!File.Exists(png)) break; // no more outputs on this adapter
                    try
                    {
                        var f = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                        if (IsHsSize(f.PixelWidth, f.PixelHeight)) { S.Adapter = a; S.Output = o; SaveSettings(); return png; }
                    }
                    catch { }
                }
            }
            return null;
        }

        void Detect()
        {
            if (ff == null) return;
            ShowPreviewMessage("Looking for your HydroShift…");
            SetStatus("Looking for your HydroShift…");
            FlushUI();
            var png = FindHydroShift();
            if (png != null) { ShowImage(png, "HydroShift"); SetStatus("Ready"); }
            else
            {
                S.Adapter = -1; SaveSettings();
                ShowPreviewMessage("Couldn't find a 2288 × 1048 display");
                SetStatus("Turn on the HydroShift as a secondary screen in L-Connect 3, then click \"Find display again\". You can still convert videos.");
            }
        }

        // ---------------------------------------------------------------- final step: loop + true black + encode

        struct Finish { public string Fc; public double Expected; public string Label; }

        Finish GetFinish(string prefix, double d)
        {
            var pre = prefix;
            if (S.TrueBlack) pre += Oled.Filter(oled ?? new OledParams { Black = 10, Shadows = 1, Color = 1, Cleanup = false });
            double c = S.Crossfade;
            if (S.Loop && d > 2 * c + 1)
            {
                string cs = Num(c), off = Num(d - 2 * c);
                var fc = "[0:v]" + pre + ToYuv + ",split[a][b];[a]trim=start=" + cs + ",setpts=PTS-STARTPTS[m];[b]trim=end=" + cs + ",setpts=PTS-STARTPTS[h];" +
                         "[m][h]xfade=transition=fade:duration=" + cs + ":offset=" + off + ",format=yuv420p[v]";
                return new Finish { Fc = fc, Expected = d - c, Label = "Making it loop" };
            }
            return new Finish { Fc = "[0:v]" + pre + ToYuv + "[v]", Expected = d, Label = "Finishing" };
        }

        void StartFinal(string inputArgs, Finish f, string fallbackName, double fps)
        {
            outFile = GetOutPath(fallbackName);
            expected = f.Expected; procLabel = f.Label; outFps = fps;
            job = StartFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 " + inputArgs +
                          " -filter_complex \"" + f.Fc + "\" -map \"[v]\" -an " + EncFinal + " " + Q(outFile));
            state = State.Processing;
            SetRecLook("grey");
            Prog.Value = 0;
        }

        void FinishProcessing()
        {
            var j = job; j.Drain();
            if (cancelled) { TryDelete(outFile); ResetIdle("Cancelled"); return; }
            if (j.Code != 0 || !File.Exists(outFile)) { ResetIdle("Processing failed: " + j.LastLine); return; }
            var fi = new FileInfo(outFile);
            ResultName.Text = fi.Name;
            ResultInfo.Text = string.Format(Inv, "{0:0.0} MB  ·  {1}  ·  {2:0} fps", fi.Length / 1048576.0, Fmt(expected), outFps);
            ResultCard.Visibility = Visibility.Visible;
            NameBox.Text = "";
            ResetIdle("Saved. Upload it in L-Connect 3.");
        }

        static void TryDelete(string f) { try { if (f != null && File.Exists(f)) File.Delete(f); } catch { } }

        // ---------------------------------------------------------------- OLED tuner

        /// <summary>
        /// With True black on, opens the live tuner for this video and keeps the chosen settings for the final pass.
        /// Returns false if the user cancelled. With True black off it does nothing and returns true.
        /// </summary>
        bool Tune(string file, double duration, string name, bool fromRecording)
        {
            oled = null;
            if (!S.TrueBlack) return true;
            var start = new OledParams { Shadows = S.OledShadows, Color = S.OledColor, Cleanup = S.OledCleanup };
            var w = new TunerWindow(this, ff, file, duration, Fit, name, start, fromRecording);
            var before = state;
            state = State.Tuning;
            SetStatus("Fine-tune true black, then press Save");
            bool ok = w.ShowDialog() == true;
            state = before;
            if (!ok) return false;
            oled = w.Result;
            S.OledShadows = oled.Shadows; S.OledColor = oled.Color; S.OledCleanup = oled.Cleanup;
            SaveSettings();
            return true;
        }

        // ---------------------------------------------------------------- recording

        void StartRecording()
        {
            TryDelete(raw);
            var enc = S.Encoder == "nvenc" ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 16 -b:v 0" : "-c:v libx264 -preset ultrafast -crf 14";
            cancelled = false;
            job = StartFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 -init_hw_device d3d11va=hs:" + S.Adapter + " -filter_hw_device hs " +
                          "-filter_complex \"ddagrab=output_idx=" + S.Output + ":framerate=" + S.Fps + ":draw_mouse=0,hwdownload,format=bgra," + ToYuv + "\" " +
                          "-t " + S.Seconds + " " + enc + " " + Tags + " " + Q(raw));
            state = State.Recording;
            SetBusy(true);
            SetRecLook("square");
            RecCount.Text = "";
            ShowPill("0:00", red);
            Prog.Visibility = Visibility.Visible;
            Prog.Value = 0;
        }

        void FinishRecording()
        {
            var j = job; j.Drain();
            if (cancelled) { ResetIdle("Cancelled"); return; }
            bool ok = File.Exists(raw) && new FileInfo(raw).Length > 50 * 1024;
            if (!ok)
            {
                if (S.Encoder == "nvenc")
                {
                    // no NVIDIA encoder available: remember that and retry on the CPU
                    S.Encoder = "x264"; SaveSettings();
                    SetStatus("Switching to the CPU encoder and trying again…");
                    StartRecording();
                    return;
                }
                ResetIdle("Recording failed: " + j.LastLine);
                return;
            }
            RecPill.Visibility = Visibility.Collapsed;
            var info = Probe(ffprobe, raw);
            double d = info != null && info.Duration > 0 ? info.Duration : S.Seconds;
            // open the tuner outside the timer tick; Tuning stops the tick from finishing this recording twice
            state = State.Tuning;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Tune(raw, d, NameBox.Text.Trim(), true)) { ResetIdle("Recording discarded"); return; }
                StartFinal("-i " + Q(raw), GetFinish("", d), "", S.Fps);
            }));
        }

        // ---------------------------------------------------------------- converting a video

        void PickVideo()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a video to convert for the HydroShift",
                Filter = "Videos|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.wmv;*.gif|Wallpaper Engine project|project.json|All files|*.*",
            };
            var we = FindWorkshopDir();
            if (we != null) d.InitialDirectory = we;
            if (d.ShowDialog(this) == true) StartConvert(d.FileName);
        }

        void StartConvert(string path)
        {
            if (state != State.Idle || ff == null) return;
            CommitLenBox(); ReadControls(); SaveSettings();

            string title = null, pj = null;
            if (Directory.Exists(path)) pj = Path.Combine(path, "project.json");
            else if (string.Equals(Path.GetFileName(path), "project.json", StringComparison.OrdinalIgnoreCase)) pj = path;
            if (pj != null)
            {
                if (!File.Exists(pj)) { SetStatus("That folder isn't a Wallpaper Engine wallpaper."); return; }
                WeProject proj;
                try { proj = WeProject.Load(pj); }
                catch { SetStatus("Couldn't read that wallpaper's project.json."); return; }
                var type = (proj.Type ?? "").ToLowerInvariant();
                title = proj.Title;
                if (type != "video")
                {
                    SetStatus("“" + title + "” is a " + (type.Length > 0 ? type : "non-video") + " wallpaper, not a video. Set it on the HydroShift in Wallpaper Engine and press Record instead.");
                    return;
                }
                path = Path.Combine(Path.GetDirectoryName(pj), proj.File ?? "");
            }
            if (!File.Exists(path)) { SetStatus("Couldn't find that video file."); return; }
            var info = Probe(ffprobe, path);
            if (info == null) { SetStatus("ffmpeg couldn't read that file. Is it a video?"); return; }

            src = path;
            srcFps = info.Fps;
            convT = info.Duration > 0 ? Math.Min(info.Duration, S.Seconds) : S.Seconds;
            srcName = !string.IsNullOrWhiteSpace(title) ? title : Path.GetFileNameWithoutExtension(path);
            if (!Tune(src, convT, srcName, false)) { SetStatus("Cancelled"); return; }
            cancelled = false;
            ResultCard.Visibility = Visibility.Collapsed;
            SetBusy(true);
            SetRecLook("grey");
            Prog.Visibility = Visibility.Visible;
            Prog.Value = 0;
            ShowPill("Converting", purple);

            bool needRife = S.Smooth && srcFps > 0 && srcFps < 58;
            if (!needRife)
            {
                // already 60 fps (or smoothing is off): one pass straight to the final file
                bool over = srcFps > 61;
                StartFinal("-t " + Num(convT) + " -i " + Q(src), GetFinish(Fit + "," + (over ? "fps=60," : ""), convT), srcName, over ? 60 : srcFps);
                return;
            }

            // temporary frames live next to the output folder, so "Save to" also picks the drive used
            Directory.CreateDirectory(S.OutDir);
            workDir = Path.Combine(S.OutDir, ".hydroshift-temp");
            double needGB = convT * (srcFps + 60) * 4.0 / 1024;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(S.OutDir));
                double freeGB = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                if (freeGB < needGB)
                {
                    ResetIdle(string.Format(Inv, "Smoothing this clip needs about {0:0} GB free on {1} (only {2:0} GB free). Pick another \"Save to\" folder or shorten Length.", needGB, root, freeGB));
                    return;
                }
            }
            catch { }

            if (File.Exists(rifeExe)) StartPrep(); else StartRifeDownload();
        }

        void StartRifeDownload()
        {
            zipPath = Path.Combine(Path.GetTempPath(), "rife-ncnn-vulkan.zip");
            TryDelete(zipPath);
            dl = new Downloader(RifeUrl, zipPath);
            state = State.Download;
        }

        void ExtractRife()
        {
            using (var z = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in z.Entries)
                {
                    int slash = e.FullName.IndexOf('/');
                    if (slash < 0) continue;
                    var rel = e.FullName.Substring(slash + 1);
                    if (rel.Length == 0 || rel.EndsWith("/")) continue;
                    if (!Regex.IsMatch(rel, "^(rife-ncnn-vulkan\\.exe|vcomp140\\.dll|LICENSE|README\\.md|rife-v4\\.6/.+)$")) continue;
                    var dest = Path.Combine(rifeDir, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
        }

        void StartPrep()
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            inDir = Path.Combine(workDir, "in");
            outDir = Path.Combine(workDir, "out");
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);
            try { var di = new DirectoryInfo(workDir); di.Attributes |= FileAttributes.Hidden; } catch { }
            job = StartFf("-y -hide_banner -loglevel error -nostats -progress pipe:2 -t " + Num(convT) + " -i " + Q(src) +
                          " -vf \"" + Fit + ",fps=" + Num(srcFps) + "\" -an " + Q(Path.Combine(inDir, "%08d.png")));
            state = State.Prep;
        }

        void FinishPrep()
        {
            var j = job; j.Drain();
            if (cancelled) { ResetIdle("Cancelled"); return; }
            var frames = Directory.GetFiles(inDir, "*.png");
            if (j.Code != 0 || frames.Length < 2) { ResetIdle("Preparing frames failed: " + j.LastLine); return; }
            Array.Sort(frames, StringComparer.Ordinal);
            try { ShowImage(frames[0], "Converting"); } catch { }
            nOut = (int)Math.Round(frames.Length * 60 / srcFps);
            job = new FfJob(rifeExe, "-i " + Q(inDir) + " -o " + Q(outDir) + " -n " + nOut + " -m " + Q(Path.Combine(rifeDir, "rife-v4.6")) + " -j 2:2:2");
            state = State.Rife;
            tickN = 0;
        }

        void FinishRife()
        {
            var j = job; j.Drain();
            if (cancelled) { ResetIdle("Cancelled"); return; }
            int n = Directory.GetFiles(outDir, "*.png").Length;
            if (j.Code != 0 || n < Math.Floor(nOut * 0.9)) { ResetIdle("Smoothing failed: " + j.LastLine); return; }
            StartFinal("-framerate 60 -i " + Q(Path.Combine(outDir, "%08d.png")), GetFinish("", n / 60.0), srcName, 60);
        }

        // ---------------------------------------------------------------- buttons and the 100 ms tick

        void CancelAll()
        {
            switch (state)
            {
                case State.Countdown: ResetIdle("Cancelled"); break;
                case State.Download: cancelled = true; dl.Cancel = true; break;
                case State.Recording:
                case State.Prep:
                case State.Rife:
                case State.Processing: cancelled = true; job.Kill(); break;
            }
        }

        void OnRecClick()
        {
            switch (state)
            {
                case State.Idle:
                    if (ff == null) return;
                    if (S.Adapter < 0) { Detect(); return; }
                    CommitLenBox(); ReadControls(); SaveSettings();
                    ResultCard.Visibility = Visibility.Collapsed;
                    if (S.Countdown)
                    {
                        countEnd = DateTime.Now.AddSeconds(3);
                        state = State.Countdown;
                        SetBusy(true);
                        RecBtn.ToolTip = "Cancel";
                    }
                    else StartRecording();
                    break;
                case State.Recording:
                    job.Quit();
                    SetStatus("Finishing up…");
                    break;
                default:
                    CancelAll();
                    break;
            }
        }

        void OnTick()
        {
            if (previewJob != null && previewJob.Done)
            {
                var p = previewPng;
                previewJob = null;
                if (state == State.Idle)
                {
                    if (File.Exists(p)) { try { ShowImage(p, "HydroShift"); } catch { } }
                    else
                    {
                        ShowPreviewMessage("Can't see the HydroShift right now");
                        SetStatus("If you changed displays, click \"Find display again\".");
                    }
                }
                TryDelete(p);
            }

            switch (state)
            {
                case State.Countdown:
                {
                    int left = (int)Math.Ceiling((countEnd - DateTime.Now).TotalSeconds);
                    if (left <= 0) StartRecording();
                    else { RecCount.Text = left.ToString(); SetStatus("Get ready…"); }
                    break;
                }
                case State.Recording:
                {
                    double t = job.Seconds;
                    Prog.Value = Clamp(t / S.Seconds, 0, 1);
                    RecTime.Text = Fmt(t);
                    RecDot.Opacity = DateTime.Now.Millisecond < 500 ? 1 : 0.25;
                    SetStatus("Recording  ·  " + Fmt(t) + " of " + Fmt(S.Seconds) + "   ·   click ■ to stop early");
                    if (job.Done) FinishRecording();
                    break;
                }
                case State.Download:
                {
                    long got = dl.Got, total = dl.Total;
                    if (total > 0) Prog.Value = Clamp((double)got / total, 0, 1);
                    SetStatus(string.Format(Inv, "Downloading RIFE (one time only)…  {0:0} of {1:0} MB   ·   click ■ to cancel", got / 1048576.0, total / 1048576.0));
                    if (dl.Done)
                    {
                        if (cancelled) { TryDelete(zipPath); ResetIdle("Cancelled"); break; }
                        if (dl.Error != null) { TryDelete(zipPath); ResetIdle("Download failed: " + dl.Error); break; }
                        SetStatus("Unpacking RIFE…");
                        FlushUI();
                        try { ExtractRife(); }
                        catch (Exception ex) { TryDelete(zipPath); ResetIdle("Unpacking failed: " + ex.Message); break; }
                        TryDelete(zipPath);
                        StartPrep();
                    }
                    break;
                }
                case State.Prep:
                    if (convT > 0) Prog.Value = Clamp(job.Seconds / convT, 0, 1);
                    SetStatus(string.Format(Inv, "Preparing frames…  {0:0}%   ·   click ■ to cancel", Prog.Value * 100));
                    if (job.Done) FinishPrep();
                    break;
                case State.Rife:
                    if (++tickN % 5 == 0)
                    {
                        int n = Directory.GetFiles(outDir, "*.png").Length;
                        Prog.Value = Clamp((double)n / Math.Max(1, nOut), 0, 1);
                        SetStatus(string.Format(Inv, "Smoothing to 60 fps…  {0:N0} of {1:N0} frames   ·   click ■ to cancel", n, nOut));
                    }
                    if (job.Done) FinishRife();
                    break;
                case State.Processing:
                    if (expected > 0) Prog.Value = Clamp(job.Seconds / expected, 0, 1);
                    SetStatus(string.Format(Inv, "{0}…  {1:0}%   ·   click ■ to cancel", procLabel, Prog.Value * 100));
                    if (job.Done) FinishProcessing();
                    break;
            }
        }

        // ---------------------------------------------------------------- events

        void WireEvents()
        {
            LenSlider.ValueChanged += (s, e) =>
            {
                if (syncLen) return;
                S.Seconds = (int)Math.Max(5, Math.Round(LenSlider.Value / 5) * 5);
                UpdateLenText();
            };
            LenSlider.PreviewMouseLeftButtonUp += (s, e) => SaveSettings();
            LenText.MouseLeftButtonUp += (s, e) =>
            {
                LenBox.Text = S.Seconds.ToString();
                LenText.Visibility = Visibility.Collapsed;
                LenBox.Visibility = Visibility.Visible;
                LenBox.Focus();
                LenBox.SelectAll();
            };
            LenBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return) { CommitLenBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { LenBox.Visibility = Visibility.Collapsed; LenText.Visibility = Visibility.Visible; e.Handled = true; }
            };
            LenBox.LostKeyboardFocus += (s, e) => CommitLenBox();

            NameBox.TextChanged += (s, e) => NamePh.Visibility = NameBox.Text.Length > 0 ? Visibility.Hidden : Visibility.Visible;

            TitleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            BtnClose.MouseLeftButtonDown += (s, e) => { e.Handled = true; Close(); };
            BtnMin.MouseLeftButtonDown += (s, e) => { e.Handled = true; WindowState = WindowState.Minimized; };

            RecBtn.MouseLeftButtonUp += (s, e) => OnRecClick();
            PreviewArea.MouseLeftButtonUp += (s, e) => RefreshPreview();
            RedetectBtn.Click += (s, e) => Detect();
            ConvertBtn.Click += (s, e) => PickVideo();
            OpenBtn.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(S.OutDir); Process.Start("explorer.exe", Q(S.OutDir)); } catch { }
            };
            PlayBtn.Click += (s, e) => { if (outFile != null && File.Exists(outFile)) Process.Start(outFile); };
            ShowBtn.Click += (s, e) => { if (outFile != null && File.Exists(outFile)) Process.Start("explorer.exe", "/select," + Q(outFile)); };
            SaveRow.MouseLeftButtonUp += (s, e) =>
            {
                // modern Explorer-style picker: address bar for typing a path, follows Windows dark mode
                var picked = FolderPicker.Pick(this, "Choose where HydroShift videos are saved", S.OutDir);
                if (picked != null)
                {
                    S.OutDir = picked;
                    SaveText.Text = S.OutDir;
                    SaveSettings();
                }
            };
            foreach (var c in new System.Windows.Controls.Primitives.ToggleButton[] { LoopSw, BlackSw, SmoothSw, CountSw, Fps30, Fps60 })
                c.Click += (s, e) => { ReadControls(); SaveSettings(); };

            // drop a video, a Wallpaper Engine wallpaper folder, or its project.json onto the window
            Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) StartConvert(files[0]);
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) CancelAll();
                else if (e.Key == Key.F5) RefreshPreview();
            };
            Activated += (s, e) => RefreshPreview();
            ContentRendered += (s, e) =>
            {
                if (ff == null)
                {
                    ShowPreviewMessage("ffmpeg is not installed");
                    SetStatus("Open PowerShell and run:  winget install Gyan.FFmpeg   then reopen this app.");
                    RecBtn.Opacity = 0.35;
                    RecBtn.IsEnabled = false;
                    ConvertBtn.IsEnabled = false;
                }
                else if (S.Adapter >= 0) { RefreshPreview(); SetStatus("Ready"); }
                else Detect();
            };
            Closing += (s, e) =>
            {
                timer.Stop();
                if (job != null) { job.Kill(); job.Drain(); }
                if (previewJob != null) previewJob.Kill();
                if (dl != null) dl.Cancel = true;
                if (state == State.Processing) TryDelete(outFile);
                RemoveWork();
                ReadControls();
                SaveSettings();
            };
        }
    }
}
