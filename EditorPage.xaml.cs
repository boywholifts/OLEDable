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
        const int N = 8;
        // preview frame size: half the HydroShift's 2288 × 1048, or the output's shape at about the same pixel count
        int W = 1144, H = 524;
        const double DefShadows = 1.2, DefColor = 1.08, DefGlowSize = 20;

        readonly MainWindow shell;
        WriteableBitmap oledBmp;
        byte[] buf;
        readonly OledScratch scratch = new OledScratch();
        readonly DispatcherTimer renderTimer;
        readonly DispatcherTimer autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        readonly Border[] thumbs = new Border[N];

        EditSession s;
        string titleAtOpen;
        byte[][] frames = new byte[N][];
        byte[][] enhanced = new byte[N][];   // AI-enhanced preview frames (before strength), made on demand
        bool[] enhFailed = new bool[N];
        bool enhancing;
        int enhancingIdx = -1, enhGen;       // enhGen changes when the model/cleanup/quality change: old frames are stale
        byte[] blendBuf;
        double[] origOff = new double[N];
        int sel = -1, openGen;
        double autoBlack = -1, split = 0.5;
        bool sync, userMovedBlack, dirty, rendering, unsaved;
        Border dragging;

        public EditorPage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();
            SetPreviewSize(1144, 524);

            for (int i = 0; i < N; i++)
            {
                var b = new Border { BorderThickness = new Thickness(0), Background = Brushes.Black, Cursor = Cursors.Hand };
                thumbs[i] = b;
                Thumbs.Children.Add(b);
            }

            renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            renderTimer.Tick += (o, e) => StartRender();
            autosaveTimer.Tick += (o, e) => FlushAutosave();
            SetupEnhanceControls();
            SetupOutputControls();
            SetupValueEditing();

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

            Left.SizeChanged += (o, e) => LayoutPreview();
            PreviewHost.SizeChanged += (o, e) => UpdateSplit();
            PreviewHost.MouseLeftButtonDown += (o, e) => { PreviewHost.CaptureMouse(); MoveSplit(e); };
            PreviewHost.MouseMove += (o, e) => { if (PreviewHost.IsMouseCaptured) MoveSplit(e); };
            PreviewHost.MouseLeftButtonUp += (o, e) => PreviewHost.ReleaseMouseCapture();

            Strip.SizeChanged += (o, e) => UpdateTrim();
            // a click anywhere on the filmstrip previews the frame under it. Handled as a "preview" (tunnelling)
            // event on the strip itself, so no layer drawn on top (trim shading, marker) can swallow it; a press on
            // a trim handle, or a drag, doesn't count as a click
            Point? stripDown = null;
            Strip.PreviewMouseLeftButtonDown += (o, e) =>
            {
                bool onHandle = e.OriginalSource is DependencyObject hit && (IsInside(hit, HandleL) || IsInside(hit, HandleR));
                stripDown = onHandle ? (Point?)null : e.GetPosition(Strip);
            };
            Strip.PreviewMouseLeftButtonUp += (o, e) =>
            {
                if (stripDown == null || dragging != null) { stripDown = null; return; }
                var at = e.GetPosition(Strip);
                bool moved = Math.Abs(at.X - stripDown.Value.X) > 6;
                stripDown = null;
                if (moved || Strip.ActualWidth <= 0) return;
                int frameNo = (int)Clamp(Math.Floor(at.X / Strip.ActualWidth * N), 0, N - 1);
                Select(frameNo);
            };
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
            UpdateSaveAs();
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

        void SetupEnhanceControls()
        {
            StrengthSlider.ValueChanged += (o, e) =>
            {
                StrengthVal.Text = Pct(StrengthSlider.Value);
                if (sync || s == null) return;
                s.EnhanceStrength = Math.Round(StrengthSlider.Value, 2);
                dirty = true;            // blended in the preview, no new AI run needed
                Touched();
            };
            CleanFirstSw.Click += (o, e) => { if (s != null) { s.EnhanceCleanup = CleanFirstSw.IsChecked == true; EnhanceOptionsChanged(true); } };
            TtaSw.Click += (o, e) => { if (s != null) { s.EnhanceTta = TtaSw.IsChecked == true; EnhanceOptionsChanged(true); } };
            SharpenSw.Click += (o, e) => { if (s != null) { s.EnhanceSharpen = SharpenSw.IsChecked == true; EnhanceOptionsChanged(false); } };
            MoreBtn.Click += (o, e) =>
            {
                bool open = MorePanel.Visibility != Visibility.Visible;
                MorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                MoreBtn.Content = open ? "Fewer options" : "More options";
                if (open) BuildStyleChips();   // picks up models added to the folder meanwhile
            };
            ModelsBtn.Click += (o, e) =>
            {
                var dir = Path.Combine(shell.P.EsrganDir, "models");
                try { Directory.CreateDirectory(dir); } catch { }
                MainWindow.OpenInShell(dir);
            };
        }

        /// <summary>One chip per model: the two built-in styles plus any community models in the models folder.</summary>
        void BuildStyleChips()
        {
            if (s == null) return;
            StyleChips.Children.Clear();
            foreach (var m in EsrModel.All(shell.P.EsrganDir))
            {
                var model = m;
                var chip = new RadioButton
                {
                    Content = model.Label, ToolTip = model.Tip, GroupName = "esrstyle", Tag = model.Key,
                    Margin = new Thickness(0, 0, 6, 6), IsChecked = model.Key == (s.EnhanceStyle ?? EsrModel.Anime),
                };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                chip.Checked += async (o, e) =>
                {
                    if (sync || s == null || s.EnhanceStyle == model.Key) return;
                    var before = s.EnhanceStyle;
                    s.EnhanceStyle = model.Key;
                    if (!await EnsureModel()) { s.EnhanceStyle = before; BuildStyleChips(); return; }
                    EnhanceOptionsChanged(true);
                };
                StyleChips.Children.Add(chip);
            }
        }

        /// <summary>Downloads Real-ESRGAN or the chosen built-in model if it isn't there yet. False if it couldn't.</summary>
        async Task<bool> EnsureModel()
        {
            var key = s.EnhanceStyle ?? EsrModel.Anime;
            if (shell.P.HasModel(key)) return true;
            bool ok = await shell.RunJob("Getting AI enhance", (pr, ct) => shell.P.EnsureEsrganAsync(pr, ct, key));
            return ok && shell.P.HasModel(key);
        }

        /// <summary>An enhance option changed; <paramref name="rerun"/> = the AI preview frames must be made again.</summary>
        void EnhanceOptionsChanged(bool rerun)
        {
            if (rerun)
            {
                enhanced = new byte[N][];
                enhFailed = new bool[N];
                enhGen++;
            }
            Touched();
            UpdateEnhanceNote();
            dirty = true;
            EnsureEnhanced();
        }

        async void OnEnhanceToggled()
        {
            if (s == null) return;
            bool on = EnhanceSw.IsChecked == true;
            s.Enhance = on;
            Touched();
            if (on && !await EnsureModel())
            {
                sync = true; EnhanceSw.IsChecked = false; sync = false;
                s.Enhance = false;
            }
            EnhanceOpts.Visibility = EnhanceSw.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BuildStyleChips();
            UpdateEnhanceNote();
            UpdateSaveAs();
            dirty = true;
            EnsureEnhanced();
        }

        void UpdateEnhanceNote()
        {
            if (s == null) return;
            if (s.Width <= 0) { EnhanceNote.Text = "Sharpens with AI before the rest (runs on your GPU)"; return; }
            var m = EsrModel.Find(shell.P.EsrganDir, s.EnhanceStyle ?? EsrModel.Anime);
            // overall growth: a fixed 4× model gets a shrunk input first (see FfArgs.EsrInputSize)
            var o = s.Output;
            var inSz = FfArgs.EsrInputSize(s.Width, s.Height, m.FixedScale, o);
            double cropW = o.Crop ? Math.Min(s.Width, s.Height * (double)o.W / o.H) : s.Width;
            int f = Math.Max(1, (int)Math.Round(inSz[0] * m.ScaleFor(s.Width, s.Height, o) / cropW));
            string size = s.Width + " × " + s.Height;
            string what = s.EnhanceSuggested
                ? "Source is " + size + ", smaller than the output: AI redraws it " + f + "× larger instead of stretching it"
                : "Source is already " + size + ": AI redraws it " + f + "× and scales back down, for crisper detail";
            if (!shell.P.HasEsrgan) what += ". Downloads Real-ESRGAN (about 50 MB) the first time.";
            EnhanceNote.Text = what;
        }

        /// <summary>
        /// Makes the AI-enhanced preview frames in the background: the selected one first (with a progress badge
        /// on the preview), then the rest of the filmstrip, so clicking around is instant.
        /// </summary>
        async void EnsureEnhanced()
        {
            EnhPill.Visibility = enhancing && enhancingIdx == sel ? Visibility.Visible : Visibility.Collapsed;
            if (s == null || enhancing || EnhanceSw.IsChecked != true || !shell.P.HasModel(s.EnhanceStyle ?? EsrModel.Anime)) return;
            int idx = -1;
            if (sel >= 0 && frames[sel] != null && enhanced[sel] == null && !enhFailed[sel]) idx = sel;
            else if (!shell.Busy)   // don't compete with an export for the GPU
                for (int k = 1; k < N && idx < 0; k++)
                    foreach (int c in new[] { sel + k, sel - k })   // nearest frames first
                        if (c >= 0 && c < N && frames[c] != null && enhanced[c] == null && !enhFailed[c]) { idx = c; break; }
            if (idx < 0) return;

            enhancing = true; enhancingIdx = idx;
            EnhPill.Visibility = idx == sel ? Visibility.Visible : Visibility.Collapsed;
            int gen = openGen, eg = enhGen;
            var src = s.SourcePath; double t = s.Duration * (idx + 0.5) / N; int sw = s.Width, sh = s.Height;
            string key = s.EnhanceStyle ?? EsrModel.Anime; bool clean = s.EnhanceCleanup, tta = s.EnhanceTta;
            int pw = W, ph = H; var outSpec = s.Output;
            var f = await Task.Run(() => shell.P.EnhanceFrame(src, t, sw, sh, pw, ph, key, clean, tta, outSpec));
            enhancing = false; enhancingIdx = -1;
            if (gen != openGen) { EnhPill.Visibility = Visibility.Collapsed; return; }
            if (eg == enhGen && pw == W && ph == H)
            {
                if (f == null) { enhFailed[idx] = true; if (idx == sel) OffText.Text = "AI enhance couldn't run on this frame"; }
                else { enhanced[idx] = f; if (idx == sel) dirty = true; }
            }
            EnsureEnhanced();   // next frame, or the new selection / new options
        }

        /// <summary>Whether <paramref name="d"/> is <paramref name="parent"/> or inside it.</summary>
        static bool IsInside(DependencyObject d, DependencyObject parent)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == parent) return true;
            return false;
        }

        /// <summary>Mixes the AI frame with the plain one: strength 1 = all AI.</summary>
        static byte[] Blend(byte[] ai, byte[] plain, double strength, byte[] into)
        {
            int w = (int)Math.Round(Clamp(strength, 0, 1) * 256), v = 256 - w;
            for (int i = 0; i < into.Length; i++) into[i] = (byte)((ai[i] * w + plain[i] * v) >> 8);
            return into;
        }

        // ---------------------------------------------------------------- opening a session

        public async void Open(EditSession session)
        {
            int gen = ++openGen;
            s = session;
            titleAtOpen = s.Title;
            frames = new byte[N][];
            enhanced = new byte[N][];
            enhFailed = new bool[N];
            enhGen++;
            shell.S.FillEnhance(s);
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
            sel = -1;

            sync = true;
            NameBox.Text = s.Title ?? "";
            TitleText.Text = s.Title ?? "";
            MetaText.Text = (s.IsImage ? "Image" : s.Kind == "recording" ? "Recording" : "Video") + "  ·  reading…";
            ShowImageMode(s.IsImage);
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
            StrengthSlider.Value = Clamp(s.EnhanceStrength, 0, 1);
            StrengthVal.Text = Pct(StrengthSlider.Value);
            CleanFirstSw.IsChecked = s.EnhanceCleanup;
            TtaSw.IsChecked = s.EnhanceTta;
            SharpenSw.IsChecked = s.EnhanceSharpen;
            EnhanceOpts.Visibility = EnhanceSw.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BuildStyleChips();
            sync = false;
            UpdateLabels();
            UpdateSaveAs();

            var info = await ProbeAsync(shell.Ffprobe, s.SourcePath);
            if (gen != openGen) return;
            if (info == null) { LoadingText.Text = s.IsImage ? "Couldn't read this image." : "Couldn't read this video."; return; }
            s.Duration = info.Duration; s.Fps = info.Fps; s.Width = info.Width; s.Height = info.Height;
            MetaText.Text = s.IsImage
                ? string.Format(Inv, "Image  ·  {0} × {1}", s.Width, s.Height)
                : string.Format(Inv, "{0}  ·  {1}  ·  {2:0} fps  ·  {3} × {4}",
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
            EnhanceOpts.Visibility = EnhanceSw.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            sync = false;
            UpdateOutputUi();
            UpdateEnhanceNote();
            UpdateSaveAs();
            UpdateTrim();
            LoadFrames();
        }

        /// <summary>
        /// Fetches the preview frames, straight at the preview size for the current output shape (8 across a video,
        /// one for an image). Called on open and whenever the output's shape changes.
        /// </summary>
        async void LoadFrames()
        {
            if (s == null || s.Width <= 0) return;
            int gen = ++openGen;
            var size = s.Output.PreviewSize();
            if (size[0] != W || size[1] != H) SetPreviewSize(size[0], size[1]);
            frames = new byte[N][];
            enhanced = new byte[N][];
            enhFailed = new bool[N];
            enhGen++;
            origOff = new double[N];
            int keep = sel;
            sel = -1;
            foreach (var t in thumbs) t.Background = Brushes.Black;
            OrigImg.Source = null;
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = s.IsImage ? "Reading the image…" : "Reading frames…";
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Hidden;

            int w = W, h = H;
            var src = s.SourcePath;
            if (s.IsImage)
            {
                var one = await Task.Run(() => Oled.GrabFrame(shell.Ffmpeg, src, 0, w, h));
                if (gen != openGen) return;
                for (int i = 0; i < N; i++) if (one != null) OnFrame(i, one);
            }
            else
            {
                var tasks = new Task[N];
                for (int i = 0; i < N; i++)
                {
                    int idx = i;
                    double t = s.Duration * (i + 0.5) / N;
                    tasks[i] = Task.Run(() => Oled.GrabFrame(shell.Ffmpeg, src, t, w, h))
                        .ContinueWith(task => { if (gen == openGen) OnFrame(idx, task.IsFaulted ? null : task.Result); },
                                      TaskScheduler.FromCurrentSynchronizationContext());
                }
                await Task.WhenAll(tasks);
            }
            if (gen != openGen) return;
            var got = new List<byte[]>();
            foreach (var f in frames) if (f != null) got.Add(f);
            if (got.Count == 0) { LoadingText.Text = s.IsImage ? "Couldn't read this image." : "Couldn't read frames from this video."; return; }
            if (keep >= 0 && frames[keep] != null) Select(keep);
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
                s.OutputName = shell.UniqueOutputName(s.Title, s.IsImage ? ".png" : ".mp4");
        }

        void UpdateSaveAs()
        {
            if (s == null) return;
            bool exists = s.OutputName != null && File.Exists(shell.OutputPathFor(s));
            bool renamed = !string.IsNullOrWhiteSpace(NameBox.Text) && NameBox.Text.Trim() != titleAtOpen;
            string folder = exists ? Path.GetFileName(Path.GetDirectoryName(shell.OutputPathFor(s))) : Path.GetFileName(shell.S.ConvertedPath.TrimEnd('\\'));
            SaveAsText.Text = exists ? "Export updates " + s.OutputName + " in " + folder + "."
                                     : "Export saves " + (renamed || s.OutputName == null ? SafeName(NameBox.Text) + ".mp4" : s.OutputName) + " to " + folder + ".";
            if (s.Duration > 0)
            {
                // from how fast each step ran in earlier exports on this PC
                double est = shell.P.EstimateSeconds(Snapshot());
                SaveAsText.Text += "  Takes about " + Eta.Fmt(est) + (s.Kind == "recording" && s.Smooth ? " (longer if it needs smoothing)." : ".");
            }
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
            if (EnhanceSw.IsChecked == true && !useEnh && enhancing && enhancingIdx == sel) return;   // wait for it rather than flash the plain frame
            dirty = false;
            rendering = true;
            var plainFrame = frames[sel];
            var aiFrame = useEnh ? enhanced[sel] : null;
            double strength = s != null ? s.EnhanceStrength : 1;
            var into = blendBuf;
            var was = origOff[sel];
            var p = Current();
            int w = W, h = H;
            var outSpec = s != null ? s.Output : OutputSpec.Panel2288;
            double glowScale = Oled.GlowScale(outSpec.W, outSpec.H) * (w / (double)outSpec.W) * 2;
            var target = buf;
            Task.Run(() => Oled.Apply(aiFrame != null ? Blend(aiFrame, plainFrame, strength, into) : plainFrame, w, h, target, p, scratch, glowScale)).ContinueWith(t =>
            {
                rendering = false;
                if (t.IsFaulted) return;
                if (w != W || h != H) { dirty = true; return; }   // the preview size changed meanwhile
                oledBmp.WritePixels(new Int32Rect(0, 0, w, h), target, w * 4, 0);
                OffText.Text = string.Format(Inv, "{0:0}% of pixels off   ·   was {1:0}%", t.Result * 100, was * 100) + (useEnh ? string.Format(Inv, "   ·   AI {0:0}%", strength * 100) : "");
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
