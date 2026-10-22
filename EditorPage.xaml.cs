using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>
    /// Edits one video: live before/after preview of the HydroShift look, trimming on a filmstrip, and the
    /// settings inspector. Export renders it into the library (see <see cref="MainWindow.Export"/>).
    /// </summary>
    public partial class EditorPage : UserControl, IPage
    {
        const int W = 1144, H = 524, N = 8;     // preview frames: half the panel's 2288 × 1048
        const double DefShadows = 1.2, DefColor = 1.08, DefGlowSize = 20;

        readonly MainWindow shell;
        readonly WriteableBitmap oledBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
        readonly byte[] buf = new byte[W * H * 4];
        readonly OledScratch scratch = new OledScratch();
        readonly DispatcherTimer renderTimer;
        readonly DispatcherTimer autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        readonly Border[] thumbs = new Border[N];

        EditSession s;
        string titleAtOpen;
        byte[][] frames = new byte[N][];
        byte[][] enhanced = new byte[N][];   // AI-enhanced preview frames, made on demand
        bool enhancing;
        double[] origOff = new double[N];
        int sel = -1, openGen;
        double autoBlack = -1, split = 0.5;
        bool sync, userMovedBlack, dirty, rendering, unsaved;
        Border dragging;

        public EditorPage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();
            OledImg.Source = oledBmp;

            for (int i = 0; i < N; i++)
            {
                int idx = i;
                var b = new Border { BorderThickness = new Thickness(0), Background = Brushes.Black, Cursor = Cursors.Hand };
                b.MouseLeftButtonUp += (o, e) => Select(idx);
                thumbs[i] = b;
                Thumbs.Children.Add(b);
            }

            renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            renderTimer.Tick += (o, e) => StartRender();
            autosaveTimer.Tick += (o, e) => FlushAutosave();

            BackBtn.Click += (o, e) => shell.ShowLibrary();
            ExportBtn.Click += async (o, e) =>
            {
                if (s == null) return;
                Commit();
                if (await shell.Export(s)) { unsaved = false; titleAtOpen = s.Title; }
                UpdateSaveAs();
            };
            ResetBtn.Click += (o, e) => ResetToAuto();
            shell.BusyChanged += () => ExportBtn.IsEnabled = !shell.Busy;

            BlackSlider.ValueChanged += (o, e) => { if (!sync) userMovedBlack = true; Changed(); };
            foreach (var sl in new[] { ShadowSlider, ColorSlider, BrightSlider, HighSlider, GlowSlider, GlowSizeSlider })
                sl.ValueChanged += (o, e) => Changed();
            NameBox.TextChanged += (o, e) =>
            {
                if (sync || s == null) return;
                TitleText.Text = NameBox.Text;
                shell.SetEditingTitle(NameBox.Text);
                UpdateSaveAs();
                Touched();
            };
            foreach (var sw in new[] { CleanSw, LoopSw, SmoothSw })
                sw.Click += (o, e) => Touched();
            EnhanceSw.Click += (o, e) => OnEnhanceToggled();

            PreviewFrame.SizeChanged += (o, e) => { PreviewHost.Height = PreviewFrame.ActualWidth * 1048 / 2288; UpdateSplit(); };
            PreviewHost.MouseLeftButtonDown += (o, e) => { PreviewHost.CaptureMouse(); MoveSplit(e); };
            PreviewHost.MouseMove += (o, e) => { if (PreviewHost.IsMouseCaptured) MoveSplit(e); };
            PreviewHost.MouseLeftButtonUp += (o, e) => PreviewHost.ReleaseMouseCapture();

            Strip.SizeChanged += (o, e) => UpdateTrim();
            foreach (var h in new[] { HandleL, HandleR })
            {
                var handle = h;
                handle.MouseLeftButtonDown += (o, e) => { dragging = handle; handle.CaptureMouse(); e.Handled = true; };
                handle.MouseMove += (o, e) => { if (dragging == handle) DragTrim(e.GetPosition(Strip).X); };
                handle.MouseLeftButtonUp += (o, e) => { if (dragging != null) Touched(); dragging = null; handle.ReleaseMouseCapture(); };
            }

            KeyDown += (o, e) =>
            {
                if (e.Key == Key.Left && sel > 0) Select(sel - 1);
                else if (e.Key == Key.Right && sel < N - 1) Select(sel + 1);
            };
        }

        public void OnShown() { renderTimer.Start(); ExportBtn.IsEnabled = !shell.Busy; }
        public void OnHidden() { renderTimer.Stop(); FlushAutosave(); }

        // ---------------------------------------------------------------- the open edit, for the sidebar

        public bool HasSession { get { return s != null; } }
        /// <summary>Changed since it was opened or last exported.</summary>
        public bool Unsaved { get { return s != null && unsaved; } }
        /// <summary>Its settings are kept in the library's store, so they survive closing the edit.</summary>
        public bool AutoSaved { get { return s != null && (s.Kind == "recording" || File.Exists(shell.OutputPathFor(s))); } }
        public string SessionTitle { get { return s == null ? "" : (string.IsNullOrWhiteSpace(NameBox.Text) ? s.Title : NameBox.Text.Trim()); } }

        public bool IsSame(EditSession other) { return s != null && other != null && (ReferenceEquals(s, other) || s.Id == other.Id); }

        /// <summary>Whether a library video is the one open here (its export, or its source).</summary>
        public bool IsFor(LibraryItem it)
        {
            if (s == null || it == null) return false;
            if (it.Id != null && it.Id == s.Id) return true;
            if (string.Equals(it.Path, s.SourcePath, StringComparison.OrdinalIgnoreCase)) return true;
            return s.OutputName != null && string.Equals(it.Path, shell.OutputPathFor(s), StringComparison.OrdinalIgnoreCase);
        }

        public void Close()
        {
            FlushAutosave();
            openGen++;
            s = null;
            unsaved = false;
            renderTimer.Stop();
        }

        /// <summary>The user changed something: remember it, and save it to the store shortly.</summary>
        void Touched()
        {
            if (sync || s == null) return;
            unsaved = true;
            autosaveTimer.Stop();
            autosaveTimer.Start();
        }

        /// <summary>
        /// Saves the current controls into the session's stored settings, so re-opening the video (even after
        /// restarting the app) carries on from here. Only for videos that already have a stored session.
        /// </summary>
        public void FlushAutosave()
        {
            autosaveTimer.Stop();
            if (s == null || !unsaved || !AutoSaved) return;
            var snap = Snapshot();
            if (!userMovedBlack) snap.Oled.Black = -1;   // keep detecting it
            shell.Store.Save(snap);
        }

        /// <summary>The session with the controls' current values (doesn't touch the open session).</summary>
        EditSession Snapshot()
        {
            var c = s.Clone();
            c.Oled = Current();
            c.Loop = LoopSw.IsChecked == true;
            if (SmoothSw.IsEnabled) c.Smooth = SmoothSw.IsChecked == true;   // greyed out: keep the stored choice
            if (EnhanceSw.IsChecked != c.EnhanceOn) c.Enhance = EnhanceSw.IsChecked == true;
            c.Title = string.IsNullOrWhiteSpace(NameBox.Text) ? s.Title : NameBox.Text.Trim();
            return c;
        }

        // ---------------------------------------------------------------- AI enhance

        async void OnEnhanceToggled()
        {
            if (s == null) return;
            bool on = EnhanceSw.IsChecked == true;
            s.Enhance = on;
            Touched();
            if (on && !shell.P.HasEsrgan)
            {
                bool ok = await shell.RunJob("Getting AI enhance", (pr, ct) => shell.P.EnsureEsrganAsync(pr, ct));
                if (!ok || !shell.P.HasEsrgan) { sync = true; EnhanceSw.IsChecked = false; sync = false; s.Enhance = false; UpdateEnhanceNote(); return; }
            }
            UpdateEnhanceNote();
            dirty = true;
            EnsureEnhanced();
        }

        void UpdateEnhanceNote()
        {
            if (s == null) return;
            if (s.Width <= 0) { EnhanceNote.Text = "Sharpens with AI before the rest (runs on your GPU)"; return; }
            int f = FfArgs.EsrganFactor(s.Width, s.Height);
            string size = s.Width + " × " + s.Height;
            string what = s.EnhanceSuggested
                ? "Source is " + size + ", smaller than the panel: AI redraws it " + f + "× larger instead of stretching it"
                : "Source is already " + size + ": AI redraws it " + f + "× and scales back down, for crisper detail";
            if (!shell.P.HasEsrgan) what += ". Downloads Real-ESRGAN (about 50 MB) the first time.";
            else if (EnhanceSw.IsChecked == true) what += ". Adds time to the export.";
            EnhanceNote.Text = what;
        }

        /// <summary>Makes the AI-enhanced version of the selected preview frame, in the background.</summary>
        async void EnsureEnhanced()
        {
            if (s == null || EnhanceSw.IsChecked != true || !shell.P.HasEsrgan || sel < 0 || enhanced[sel] != null || enhancing) return;
            enhancing = true;
            int gen = openGen, idx = sel;
            OffText.Text = "AI enhancing the preview…";
            var src = s.SourcePath; double t = s.Duration * (idx + 0.5) / N; int sw = s.Width, sh = s.Height;
            var f = await Task.Run(() => shell.P.EnhanceFrame(src, t, sw, sh, W, H));
            enhancing = false;
            if (gen != openGen) return;
            if (f == null) { OffText.Text = "AI enhance couldn't run on this frame"; return; }
            enhanced[idx] = f;
            dirty = true;
            EnsureEnhanced();   // the selection may have moved on meanwhile
        }

        // ---------------------------------------------------------------- opening a session

        public async void Open(EditSession session)
        {
            int gen = ++openGen;
            s = session;
            titleAtOpen = s.Title;
            frames = new byte[N][];
            enhanced = new byte[N][];
            enhancing = false;
            unsaved = false;
            autosaveTimer.Stop();
            origOff = new double[N];
            sel = -1; autoBlack = -1;
            userMovedBlack = s.Oled.Black >= 0;
            foreach (var t in thumbs) t.Background = Brushes.Black;
            OrigImg.Source = null;
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = "Reading frames…";
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Hidden;
            Array.Clear(buf, 0, buf.Length);
            oledBmp.WritePixels(new Int32Rect(0, 0, W, H), buf, W * 4, 0);

            sync = true;
            NameBox.Text = s.Title ?? "";
            TitleText.Text = s.Title ?? "";
            MetaText.Text = (s.Kind == "recording" ? "Recording" : "Video") + "  ·  reading…";
            BlackSlider.Value = s.Oled.Black >= 0 ? s.Oled.Black : 10;
            ShadowSlider.Value = Clamp(s.Oled.Shadows, 1, 2);
            ColorSlider.Value = Clamp(s.Oled.Color, 0.8, 1.5);
            BrightSlider.Value = Clamp(s.Oled.Bright, 0, 1);
            HighSlider.Value = Clamp(s.Oled.Highlights, 0, 1);
            GlowSlider.Value = Clamp(s.Oled.Glow, 0, 1);
            GlowSizeSlider.Value = Clamp(s.Oled.GlowSize, 6, 48);
            CleanSw.IsChecked = s.Oled.Cleanup;
            LoopSw.IsChecked = s.Loop;
            SmoothSw.IsChecked = s.Smooth;
            SmoothSw.IsEnabled = true;
            SmoothRow.Opacity = 1;
            EnhanceSw.IsChecked = s.Enhance == true;
            sync = false;
            UpdateLabels();
            UpdateSaveAs();

            var info = await ProbeAsync(shell.Ffprobe, s.SourcePath);
            if (gen != openGen) return;
            if (info == null) { LoadingText.Text = "Couldn't read this video."; return; }
            s.Duration = info.Duration; s.Fps = info.Fps; s.Width = info.Width; s.Height = info.Height;
            MetaText.Text = string.Format(Inv, "{0}  ·  {1}  ·  {2:0} fps  ·  {3} × {4}",
                s.Kind == "recording" ? "Recording" : "Video", Fmt(s.Duration), s.Fps, s.Width, s.Height);
            SmoothNote.Text = s.Fps > 0 && s.Fps < 58 ? string.Format(Inv, "Source is {0:0} fps, AI fills in the missing frames", s.Fps)
                            : s.Kind == "recording" ? "Checked on export; only used if the wallpaper drew fewer frames"
                            : "Already 60 fps, nothing to fill in";
            // a real 60 fps video has nothing to smooth: grey the switch out so it can't be mistaken for doing
            // something (recordings stay on: a 60 fps capture of a 30 fps wallpaper still needs it)
            bool nothingToFill = s.Kind != "recording" && s.Fps >= 58;
            sync = true;
            SmoothSw.IsEnabled = !nothingToFill;
            SmoothRow.Opacity = nothingToFill ? 0.45 : 1;
            if (nothingToFill) SmoothSw.IsChecked = false;
            EnhanceSw.IsChecked = s.EnhanceOn;
            sync = false;
            UpdateEnhanceNote();
            UpdateTrim();

            // fetch all frames at once, straight at preview size
            var tasks = new Task[N];
            for (int i = 0; i < N; i++)
            {
                int idx = i;
                double t = s.Duration * (i + 0.5) / N;
                tasks[i] = Task.Run(() => Oled.GrabFrame(shell.Ffmpeg, s.SourcePath, t, W, H))
                    .ContinueWith(task => { if (gen == openGen) OnFrame(idx, task.IsFaulted ? null : task.Result); },
                                  TaskScheduler.FromCurrentSynchronizationContext());
            }
            await Task.WhenAll(tasks);
            if (gen != openGen) return;
            var got = new List<byte[]>();
            foreach (var f in frames) if (f != null) got.Add(f);
            if (got.Count == 0) { LoadingText.Text = "Couldn't read frames from this video."; return; }
            SetAutoBlack(Oled.AutoBlack(got));
        }

        void OnFrame(int i, byte[] f)
        {
            if (f == null) return;
            frames[i] = f;
            origOff[i] = Oled.OffFraction(f);
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Rgb24, null, f, W * 3);
            src.Freeze();
            thumbs[i].Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
            if (sel < 0)
            {
                SetAutoBlack(Oled.AutoBlack(new List<byte[]> { f }));
                Select(i);
            }
        }

        void SetAutoBlack(double v)
        {
            autoBlack = v;
            if (!userMovedBlack) { sync = true; BlackSlider.Value = v; sync = false; }
            UpdateLabels();
            dirty = true;
        }

        void ResetToAuto()
        {
            userMovedBlack = false;
            sync = true;
            if (autoBlack >= 0) BlackSlider.Value = autoBlack;
            ShadowSlider.Value = DefShadows; ColorSlider.Value = DefColor;
            BrightSlider.Value = 0; HighSlider.Value = 0; GlowSlider.Value = 0; GlowSizeSlider.Value = DefGlowSize;
            CleanSw.IsChecked = true;
            sync = false;
            UpdateLabels();
            dirty = true;
            Touched();
        }

        /// <summary>Copies the controls into the session before exporting.</summary>
        void Commit()
        {
            s.Oled = Current();
            s.Loop = LoopSw.IsChecked == true;
            if (SmoothSw.IsEnabled) s.Smooth = SmoothSw.IsChecked == true;
            s.Enhance = EnhanceSw.IsChecked == true;
            s.Title = string.IsNullOrWhiteSpace(NameBox.Text) ? s.Title : NameBox.Text.Trim();
            // not exported yet: a renamed video takes its file name from the new title
            if (s.OutputName == null || (!File.Exists(shell.OutputPathFor(s)) && s.Title != titleAtOpen))
                s.OutputName = shell.UniqueOutputName(s.Title);
        }

        void UpdateSaveAs()
        {
            if (s == null) return;
            bool exists = s.OutputName != null && File.Exists(shell.OutputPathFor(s));
            bool renamed = !string.IsNullOrWhiteSpace(NameBox.Text) && NameBox.Text.Trim() != titleAtOpen;
            string folder = exists ? Path.GetFileName(Path.GetDirectoryName(shell.OutputPathFor(s))) : Path.GetFileName(shell.S.ConvertedPath.TrimEnd('\\'));
            SaveAsText.Text = exists ? "Export updates " + s.OutputName + " in " + folder + "."
                                     : "Export saves " + (renamed || s.OutputName == null ? SafeName(NameBox.Text) + ".mp4" : s.OutputName) + " to " + folder + ".";
        }

        OledParams Current()
        {
            return new OledParams
            {
                Black = Math.Round(BlackSlider.Value),
                Shadows = Math.Round(ShadowSlider.Value, 2),
                Color = Math.Round(ColorSlider.Value, 2),
                Bright = Math.Round(BrightSlider.Value, 2),
                Highlights = Math.Round(HighSlider.Value, 2),
                Glow = Math.Round(GlowSlider.Value, 2),
                GlowSize = Math.Round(GlowSizeSlider.Value),
                Cleanup = CleanSw.IsChecked == true,
            };
        }

        // ---------------------------------------------------------------- preview

        void Select(int i)
        {
            if (frames[i] == null) return;
            sel = i;
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Rgb24, null, frames[i], W * 3);
            src.Freeze();
            OrigImg.Source = src;
            LoadingText.Visibility = Visibility.Collapsed;
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Visible;
            UpdateTrim();
            dirty = true;
            EnsureEnhanced();
        }

        void StartRender()
        {
            if (!dirty || rendering || sel < 0 || frames[sel] == null) return;
            bool useEnh = EnhanceSw.IsChecked == true && enhanced[sel] != null;
            if (EnhanceSw.IsChecked == true && !useEnh && enhancing) return;   // wait for it rather than flash the plain frame
            dirty = false;
            rendering = true;
            var frame = useEnh ? enhanced[sel] : frames[sel];
            var was = origOff[sel];
            var p = Current();
            Task.Run(() => Oled.Apply(frame, W, H, buf, p, scratch)).ContinueWith(t =>
            {
                rendering = false;
                if (t.IsFaulted) return;
                oledBmp.WritePixels(new Int32Rect(0, 0, W, H), buf, W * 4, 0);
                OffText.Text = string.Format(Inv, "{0:0}% of pixels off   ·   was {1:0}%", t.Result * 100, was * 100) + (useEnh ? "   ·   AI enhanced" : "");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Changed()
        {
            if (sync) return;
            UpdateLabels();
            dirty = true;
            Touched();
        }

        void UpdateLabels()
        {
            double b = Math.Round(BlackSlider.Value);
            BlackVal.Text = string.Format(Inv, "{0:0}%", b / 255 * 100) + (autoBlack >= 0 && Math.Abs(b - autoBlack) < 0.5 ? "  auto" : "");
            ShadowVal.Text = string.Format(Inv, "{0:0}%", (ShadowSlider.Value - 1) * 100);
            double c = (ColorSlider.Value - 1) * 100;
            ColorVal.Text = Math.Abs(c) < 0.5 ? "0%" : string.Format(Inv, "{0}{1:0}%", c > 0 ? "+" : "−", Math.Abs(c));
            BrightVal.Text = Pct(BrightSlider.Value);
            HighVal.Text = Pct(HighSlider.Value);
            GlowVal.Text = GlowSlider.Value < 0.005 ? "Off" : Pct(GlowSlider.Value);
            GlowSizeVal.Text = string.Format(Inv, "{0:0} px", GlowSizeSlider.Value);
            GlowSizeSlider.IsEnabled = GlowSlider.Value >= 0.005;
            GlowSizeRow.Opacity = GlowSizeSlider.IsEnabled ? 1 : 0.45;
        }

        static string Pct(double v) { return string.Format(Inv, "{0:0}%", v * 100); }

        void MoveSplit(MouseEventArgs e)
        {
            split = Clamp(e.GetPosition(PreviewHost).X / Math.Max(1, PreviewHost.ActualWidth), 0, 1);
            UpdateSplit();
        }

        void UpdateSplit()
        {
            double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
            if (w <= 0) return;
            double x = split * w;
            OrigImg.Clip = new RectangleGeometry(new Rect(0, 0, x, h));
            SplitLine.Margin = new Thickness(x - 1, 0, 0, 0);
            SplitKnob.Margin = new Thickness(x - SplitKnob.Width / 2, 0, 0, 0);
        }

        // ---------------------------------------------------------------- trimming

        void DragTrim(double x)
        {
            if (s == null || s.Duration <= 0) return;
            double w = Math.Max(1, Strip.ActualWidth);
            double t = Clamp(x / w, 0, 1) * s.Duration;
            if (dragging == HandleL) s.TrimStart = Math.Round(Clamp(t, 0, s.UsedEnd - 1), 1);
            else
            {
                double end = Math.Round(Clamp(t, s.UsedStart + 1, s.Duration), 1);
                s.TrimEnd = end >= s.Duration - 0.05 ? 0 : end;
            }
            UpdateTrim();
        }

        void UpdateTrim()
        {
            double w = Strip.ActualWidth;
            if (s == null || w <= 0) return;
            double d = Math.Max(0.1, s.Duration);
            double x0 = s.Duration > 0 ? s.UsedStart / d * w : 0;
            double x1 = s.Duration > 0 ? s.UsedEnd / d * w : w;
            Canvas.SetLeft(HandleL, x0);
            Canvas.SetLeft(HandleR, Math.Max(x0 + 10, x1 - 10));
            Canvas.SetLeft(DimL, 0); DimL.Width = Math.Max(0, x0);
            Canvas.SetLeft(DimR, x1); DimR.Width = Math.Max(0, w - x1);
            SelMark.Visibility = sel >= 0 ? Visibility.Visible : Visibility.Hidden;
            if (sel >= 0) Canvas.SetLeft(SelMark, (sel + 0.5) / N * w - 1);
            TrimText.Text = s.Duration > 0
                ? string.Format(Inv, "{0} – {1}   ·   using {2} of {3}", Fmt(s.UsedStart), Fmt(s.UsedEnd), Fmt(s.UsedLength), Fmt(s.Duration))
                : "";
        }
    }
}
