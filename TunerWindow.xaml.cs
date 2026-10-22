using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HydroShiftRecorder
{
    /// <summary>
    /// Live true-black preview for one video. Grabs a few frames from across it, picks a black level
    /// automatically, and lets you fine-tune with a before/after divider before anything is encoded.
    /// </summary>
    public partial class TunerWindow : Window
    {
        const int W = 1144, H = 524, N = 6;                 // half the HydroShift's 2288 × 1048
        const double DefShadows = 1.2, DefColor = 1.08, DefGlowSize = 20;

        /// <summary>The chosen settings, when the window closes with Save.</summary>
        public OledParams Result { get; private set; }

        readonly string ff, file;
        readonly double duration;
        readonly byte[][] frames = new byte[N][];
        readonly Border[] thumbs = new Border[N];
        readonly double[] origOff = new double[N];
        readonly WriteableBitmap oledBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
        readonly byte[] buf = new byte[W * H * 4];
        readonly OledScratch scratch = new OledScratch();
        readonly Brush accent, clear;

        int sel = -1;
        double autoBlack = -1, split = 0.5;
        bool sync, userMovedBlack, dirty, rendering;
        readonly DispatcherTimer renderTimer;
        volatile bool closed;

        public TunerWindow(Window owner, string ffmpeg, string file, double duration, string name,
                           OledParams start, bool fromRecording)
        {
            InitializeComponent();
            Owner = owner;
            ff = ffmpeg; this.file = file; this.duration = Math.Max(0.1, duration);
            accent = (Brush)FindResource("Accent");
            clear = Brushes.Transparent;

            TitleText.Text = string.IsNullOrWhiteSpace(name) ? "True black" : "True black  ·  " + name;
            if (fromRecording) { CancelBtn.Content = "Discard"; CancelBtn.ToolTip = "Throw away this recording"; }
            OledImg.Source = oledBmp;

            sync = true;
            BlackSlider.Value = 10;
            ShadowSlider.Value = Tools.Clamp(start.Shadows, 1, 2);
            ColorSlider.Value = Tools.Clamp(start.Color, 0.8, 1.5);
            BrightSlider.Value = Tools.Clamp(start.Bright, 0, 1);
            HighSlider.Value = Tools.Clamp(start.Highlights, 0, 1);
            GlowSlider.Value = Tools.Clamp(start.Glow, 0, 1);
            GlowSizeSlider.Value = Tools.Clamp(start.GlowSize, 6, 48);
            CleanSw.IsChecked = start.Cleanup;
            sync = false;
            UpdateLabels();
            UpdateSplit();

            for (int i = 0; i < N; i++)
            {
                int idx = i;
                var b = new Border
                {
                    Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(2),
                    BorderBrush = clear, Background = (Brush)FindResource("Card"), Cursor = Cursors.Hand, ClipToBounds = true,
                    ToolTip = "Preview this moment (" + Tools.Fmt(FrameTime(i)) + ")",
                };
                b.MouseLeftButtonUp += (s, e) => Select(idx);
                thumbs[i] = b;
                Thumbs.Children.Add(b);
            }

            // slider drags only mark the preview dirty; the timer starts a redraw on a background thread whenever
            // the previous one has finished, so the UI never waits on the pixel work and drags stay smooth
            renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            renderTimer.Tick += (s, e) => StartRender();
            renderTimer.Start();

            Wire();
            ContentRendered += (s, e) => StartLoading();
        }

        double FrameTime(int i) { return duration * (i + 0.5) / N; }

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

        // ---------------------------------------------------------------- loading frames

        void StartLoading()
        {
            // all frames are fetched at once (one ffmpeg each), straight at preview size
            var tasks = new Task[N];
            for (int i = 0; i < N; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    byte[] f = null;
                    if (!closed) { try { f = Oled.GrabFrame(ff, file, FrameTime(idx), W, H); } catch { } }
                    Dispatcher.BeginInvoke(new Action(() => OnFrame(idx, f)));
                });
            }
            Task.WhenAll(tasks).ContinueWith(t => Dispatcher.BeginInvoke(new Action(OnAllFrames)));
        }

        void OnFrame(int i, byte[] f)
        {
            if (closed || f == null) return;
            frames[i] = f;
            origOff[i] = Oled.OffFraction(f);
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Rgb24, null, f, W * 3);
            src.Freeze();
            thumbs[i].Child = new Image { Source = src, Stretch = Stretch.UniformToFill };

            if (sel < 0)
            {
                // first frame in: show it straight away with a level guessed from it alone
                SetAutoBlack(Oled.AutoBlack(new List<byte[]> { f }));
                Select(i);
            }
        }

        void OnAllFrames()
        {
            if (closed) return;
            var got = new List<byte[]>();
            foreach (var f in frames) if (f != null) got.Add(f);
            if (got.Count == 0)
            {
                LoadingText.Text = "Couldn't read frames from this video. Saving uses the settings below.";
                return;
            }
            // refine the automatic level using the whole video, unless it's already been adjusted by hand
            SetAutoBlack(Oled.AutoBlack(got));
        }

        void SetAutoBlack(double v)
        {
            autoBlack = v;
            if (!userMovedBlack)
            {
                sync = true; BlackSlider.Value = v; sync = false;
            }
            UpdateLabels();
            dirty = true;
        }

        // ---------------------------------------------------------------- rendering

        void Select(int i)
        {
            if (frames[i] == null) return;
            sel = i;
            for (int k = 0; k < N; k++) thumbs[k].BorderBrush = k == i ? accent : clear;
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Rgb24, null, frames[i], W * 3);
            src.Freeze();
            OrigImg.Source = src;
            LoadingText.Visibility = Visibility.Collapsed;
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Visible;
            dirty = true;
        }

        void StartRender()
        {
            if (!dirty || rendering || closed || sel < 0 || frames[sel] == null) return;
            dirty = false;
            rendering = true;
            var frame = frames[sel];
            var was = origOff[sel];
            var p = Current();
            Task.Run(() => Oled.Apply(frame, W, H, buf, p, scratch)).ContinueWith(t =>
            {
                rendering = false;
                if (closed || t.IsFaulted) return;
                oledBmp.WritePixels(new Int32Rect(0, 0, W, H), buf, W * 4, 0);
                OffText.Text = string.Format(Tools.Inv, "{0:0}% of pixels off   ·   was {1:0}%", t.Result * 100, was * 100);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateLabels()
        {
            double b = Math.Round(BlackSlider.Value);
            BlackVal.Text = string.Format(Tools.Inv, "{0:0}%", b / 255 * 100) + (autoBlack >= 0 && Math.Abs(b - autoBlack) < 0.5 ? "  auto" : "");
            ShadowVal.Text = string.Format(Tools.Inv, "{0:0}%", (ShadowSlider.Value - 1) * 100);
            double c = (ColorSlider.Value - 1) * 100;
            ColorVal.Text = Math.Abs(c) < 0.5 ? "0%" : string.Format(Tools.Inv, "{0}{1:0}%", c > 0 ? "+" : "−", Math.Abs(c));
            BrightVal.Text = Pct(BrightSlider.Value);
            HighVal.Text = Pct(HighSlider.Value);
            GlowVal.Text = GlowSlider.Value < 0.005 ? "Off" : Pct(GlowSlider.Value);
            GlowSizeVal.Text = string.Format(Tools.Inv, "{0:0} px", GlowSizeSlider.Value);
            GlowSizeSlider.IsEnabled = GlowSlider.Value >= 0.005;
            GlowSizeSlider.Opacity = GlowSizeVal.Opacity = GlowSizeSlider.IsEnabled ? 1 : 0.4;
        }

        static string Pct(double v) { return string.Format(Tools.Inv, "{0:0}%", v * 100); }

        void Changed()
        {
            if (sync) return;
            UpdateLabels();
            dirty = true;
        }

        void UpdateSplit()
        {
            double x = Tools.Clamp(split, 0, 1) * PreviewHost.Width;
            OrigImg.Clip = new RectangleGeometry(new Rect(0, 0, x, PreviewHost.Height));
            SplitLine.Margin = new Thickness(x - 1, 0, 0, 0);
            SplitKnob.Margin = new Thickness(x - SplitKnob.Width / 2, 0, 0, 0);
        }

        // ---------------------------------------------------------------- events

        void Wire()
        {
            BlackSlider.ValueChanged += (s, e) => { if (!sync) userMovedBlack = true; Changed(); };
            foreach (var sl in new[] { ShadowSlider, ColorSlider, BrightSlider, HighSlider, GlowSlider, GlowSizeSlider })
                sl.ValueChanged += (s, e) => Changed();

            PreviewHost.MouseLeftButtonDown += (s, e) => { PreviewHost.CaptureMouse(); MoveSplit(e); };
            PreviewHost.MouseMove += (s, e) => { if (PreviewHost.IsMouseCaptured) MoveSplit(e); };
            PreviewHost.MouseLeftButtonUp += (s, e) => PreviewHost.ReleaseMouseCapture();

            ResetBtn.Click += (s, e) =>
            {
                userMovedBlack = false;
                sync = true;
                if (autoBlack >= 0) BlackSlider.Value = autoBlack;
                ShadowSlider.Value = DefShadows;
                ColorSlider.Value = DefColor;
                BrightSlider.Value = 0;
                HighSlider.Value = 0;
                GlowSlider.Value = 0;
                GlowSizeSlider.Value = DefGlowSize;
                CleanSw.IsChecked = true;
                sync = false;
                UpdateLabels();
                dirty = true;
            };
            SaveBtn.Click += (s, e) => { Result = Current(); DialogResult = true; };
            CancelBtn.Click += (s, e) => DialogResult = false;
            BtnClose.MouseLeftButtonDown += (s, e) => { e.Handled = true; DialogResult = false; };
            TitleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
                else if (e.Key == Key.Left && sel > 0) Select(sel - 1);
                else if (e.Key == Key.Right && sel < N - 1) Select(sel + 1);
            };
            Closed += (s, e) => { closed = true; renderTimer.Stop(); };
        }

        void MoveSplit(MouseEventArgs e)
        {
            split = e.GetPosition(PreviewHost).X / PreviewHost.Width;
            UpdateSplit();
        }
    }
}
