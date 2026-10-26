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
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>
    /// The filmstrip and the frame shown in the preview. Drag along the strip (or use ← →) to pick any frame:
    /// while dragging, the newest requested frame is fetched as soon as the previous one is in (so it keeps up
    /// without piling up work), with the OLED look applied; AI enhance runs once you let go.
    /// Frames already seen are kept, so going back is instant.
    /// </summary>
    public partial class EditorPage
    {
        /// <summary>A preview-sized frame and how much of it is already black.</summary>
        class PFrame { public byte[] Px; public double Off; }

        const int MaxPlain = 32, MaxEnh = 16;
        readonly Dictionary<long, PFrame> plainCache = new Dictionary<long, PFrame>();
        readonly Queue<long> plainOrder = new Queue<long>();
        readonly Dictionary<long, byte[]> enhCache = new Dictionary<long, byte[]>();
        readonly Queue<long> enhOrder = new Queue<long>();
        readonly HashSet<long> enhFailed = new HashSet<long>();

        PFrame cur;              // the frame in the preview
        long curKey = -1;        // its frame number
        long wantKey = -1;       // where the playhead is (the frame being fetched, if it isn't in yet)
        bool fetching, scrubbing;

        int nThumbs, thumbGen;
        readonly DispatcherTimer thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        void SetupScrub()
        {
            Strip.Focusable = true;
            Strip.FocusVisualStyle = null;
            Strip.Cursor = Cursors.Hand;
            // pressing anywhere but a trim handle starts scrubbing; handled as a "preview" (tunnelling) event on the
            // strip itself so the trim shading and the playhead drawn on top can't swallow it
            Strip.PreviewMouseLeftButtonDown += (o, e) =>
            {
                if (s == null || s.IsImage) return;
                bool onHandle = e.OriginalSource is DependencyObject hit && (IsInside(hit, HandleL) || IsInside(hit, HandleR));
                if (onHandle) return;
                scrubbing = true;
                Strip.Focus();
                Strip.CaptureMouse();
                ScrubTo(e.GetPosition(Strip).X);
                e.Handled = true;
            };
            Strip.MouseMove += (o, e) => { if (scrubbing) ScrubTo(e.GetPosition(Strip).X); };
            Strip.MouseLeftButtonUp += (o, e) => { if (scrubbing) Strip.ReleaseMouseCapture(); };
            Strip.LostMouseCapture += (o, e) =>
            {
                if (!scrubbing) return;
                scrubbing = false;
                dirty = true;
                EnsureEnhanced();   // now that it has stopped moving
            };

            // ← → one frame, Shift for one second
            KeyDown += (o, e) =>
            {
                if (s == null || s.IsImage || s.Fps <= 0 || wantKey < 0) return;
                if (e.Key != Key.Left && e.Key != Key.Right) return;
                long step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? Math.Max(1, (long)Math.Round(s.Fps)) : 1;
                RequestKey(wantKey + (e.Key == Key.Left ? -step : step));
                e.Handled = true;
            };

            // a wider strip gets more frames
            Strip.SizeChanged += (o, e) =>
            {
                if (s != null && !s.IsImage && s.Duration > 0 && Math.Abs(ThumbCount() - nThumbs) >= 2) { thumbTimer.Stop(); thumbTimer.Start(); }
            };
            thumbTimer.Tick += (o, e) => { thumbTimer.Stop(); LoadThumbs(); };
        }

        void ScrubTo(double x)
        {
            if (s == null || s.Duration <= 0) return;
            RequestKey(KeyAt(Clamp(x / Math.Max(1, Strip.ActualWidth), 0, 1) * s.Duration));
        }

        // ---------------------------------------------------------------- frame numbers

        long LastKey { get { return s == null || s.IsImage || s.Fps <= 0 ? 0 : Math.Max(0, (long)Math.Floor(s.Duration * s.Fps) - 1); } }

        long KeyAt(double seconds)
        {
            if (s == null || s.IsImage || s.Fps <= 0) return 0;
            return Math.Max(0, Math.Min(LastKey, (long)Math.Floor(seconds * s.Fps)));
        }

        /// <summary>The time to ask ffmpeg for so it returns exactly frame <paramref name="key"/> (it returns the first frame at or after it).</summary>
        double TimeOf(long key)
        {
            if (s == null || s.IsImage || s.Fps <= 0) return 0;
            return Math.Max(0, (key - 0.25) / s.Fps);
        }

        /// <summary>Where frame <paramref name="key"/> is shown on the timeline (its start).</summary>
        double StartOf(long key) { return s == null || s.Fps <= 0 ? 0 : key / s.Fps; }

        static string FmtPrecise(double sec)
        {
            sec = Math.Max(0, sec);
            int m = (int)(sec / 60);
            return string.Format(Inv, "{0}:{1:00.0}", m, Math.Floor((sec - m * 60) * 10) / 10);
        }

        // ---------------------------------------------------------------- caches

        static void Put<T>(Dictionary<long, T> cache, Queue<long> order, int max, long key, T value)
        {
            if (!cache.ContainsKey(key)) order.Enqueue(key);
            cache[key] = value;
            while (cache.Count > max && order.Count > 0) cache.Remove(order.Dequeue());
        }

        void ClearEnhanced()
        {
            enhCache.Clear(); enhOrder.Clear(); enhFailed.Clear();
            enhGen++;
        }

        /// <summary>Forgets every frame (new video, or the preview size changed). The playhead stays where it is.</summary>
        void ResetFrames()
        {
            plainCache.Clear(); plainOrder.Clear();
            ClearEnhanced();
            cur = null; curKey = -1;
            thumbGen++;
            Thumbs.Children.Clear();
            nThumbs = 0;
        }

        // ---------------------------------------------------------------- loading

        /// <summary>
        /// Fetches the preview frame and the filmstrip, straight at the preview size for the current output shape.
        /// Called on open and whenever the output's shape changes (the playhead keeps its place).
        /// </summary>
        void LoadFrames()
        {
            if (s == null || s.Width <= 0) return;
            openGen++;
            var size = s.Output.PreviewSize();
            if (size[0] != W || size[1] != H) SetPreviewSize(size[0], size[1]);
            ResetFrames();
            OrigImg.Source = null;
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = s.IsImage ? "Reading the image…" : "Reading frames…";
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Hidden;

            // start where the old filmstrip's first frame was, a little into the video
            RequestKey(wantKey >= 0 ? wantKey : KeyAt(s.Duration * 0.5 / 8));
            LoadThumbs();
        }

        int ThumbCount()
        {
            double w = Strip.ActualWidth;
            return w <= 0 ? 8 : (int)Clamp(Math.Floor(w / 110), 8, 24);
        }

        /// <summary>Small frames spread over the whole video for the filmstrip; they also set the automatic black level.</summary>
        async void LoadThumbs()
        {
            if (s == null || s.IsImage || s.Duration <= 0) return;
            int gen = ++thumbGen;
            int n = ThumbCount();
            nThumbs = n;
            Thumbs.Columns = n;
            Thumbs.Children.Clear();
            var cells = new Border[n];
            for (int i = 0; i < n; i++) { cells[i] = new Border { Background = Brushes.Black }; Thumbs.Children.Add(cells[i]); }

            const int th = 116;   // twice the strip's height, for sharpness
            int tw = Math.Max(2, Math.Min(480, (int)Math.Round(th * (double)W / H / 2) * 2));
            var src = s.SourcePath; var ff = shell.Ffmpeg; double dur = s.Duration;
            var got = new byte[n][];
            using (var gate = new SemaphoreSlim(4))
            {
                var tasks = new Task[n];
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    double t = dur * (i + 0.5) / n;
                    tasks[i] = Task.Run(async () =>
                    {
                        await gate.WaitAsync();
                        try { return gen == thumbGen ? Oled.GrabFrame(ff, src, t, tw, th) : null; }
                        finally { gate.Release(); }
                    }).ContinueWith(task =>
                    {
                        var px = task.IsFaulted ? null : task.Result;
                        if (gen != thumbGen || px == null) return;
                        got[idx] = px;
                        var bmp = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Rgb24, null, px, tw * 3);
                        bmp.Freeze();
                        cells[idx].Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                await Task.WhenAll(tasks);
            }
            if (gen != thumbGen) return;
            var list = new List<byte[]>();
            foreach (var px in got) if (px != null) list.Add(px);
            if (list.Count > 0) SetAutoBlack(Oled.AutoBlack(list));
        }

        // ---------------------------------------------------------------- the preview frame

        /// <summary>Moves the playhead to frame <paramref name="key"/> and shows it as soon as it's in.</summary>
        void RequestKey(long key)
        {
            if (s == null) return;
            wantKey = Math.Max(0, Math.Min(LastKey, key));
            UpdatePlayhead();
            PFrame f;
            if (plainCache.TryGetValue(wantKey, out f)) { Show(wantKey, f); return; }
            if (!fetching) FetchLoop();
        }

        /// <summary>
        /// One frame at a time: when a frame arrives it's shown (even if the playhead has moved on, so dragging
        /// feels live), then the newest position is fetched. Requests made in between are simply skipped.
        /// </summary>
        async void FetchLoop()
        {
            fetching = true;
            int gen = openGen;
            try
            {
                while (s != null && gen == openGen)
                {
                    long key = wantKey;
                    PFrame f;
                    if (plainCache.TryGetValue(key, out f)) { Show(key, f); return; }
                    var src = s.SourcePath; var ff = shell.Ffmpeg; int w = W, h = H; double t = TimeOf(key);
                    var px = await Task.Run(() => { try { return Oled.GrabFrame(ff, src, t, w, h); } catch { return null; } });
                    if (gen != openGen || s == null) break;
                    if (w != W || h != H) continue;
                    if (px == null)
                    {
                        if (key != wantKey) continue;
                        if (cur == null) LoadingText.Text = s.IsImage ? "Couldn't read this image." : "Couldn't read this frame.";
                        return;
                    }
                    f = new PFrame { Px = px, Off = Oled.OffFraction(px) };
                    Put(plainCache, plainOrder, MaxPlain, key, f);
                    Show(key, f);
                    if (key == wantKey) return;
                }
            }
            finally
            {
                fetching = false;
                // a newer video or preview size took over while this was waiting: carry on for it
                if (gen != openGen && s != null && wantKey >= 0 && !plainCache.ContainsKey(wantKey)) FetchLoop();
            }
        }

        void Show(long key, PFrame f)
        {
            cur = f; curKey = key;
            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Rgb24, null, f.Px, W * 3);
            bmp.Freeze();
            OrigImg.Source = bmp;
            LoadingText.Visibility = Visibility.Collapsed;
            SplitLine.Visibility = SplitKnob.Visibility = OffPill.Visibility = Visibility.Visible;
            if (autoBlack < 0) SetAutoBlack(Oled.AutoBlack(new List<byte[]> { f.Px }));   // until the filmstrip's frames are in
            dirty = true;
            UpdatePlayhead();
            if (!scrubbing) EnsureEnhanced();
        }

        void UpdatePlayhead()
        {
            double w = Strip.ActualWidth;
            bool show = s != null && !s.IsImage && wantKey >= 0 && w > 0 && s.Duration > 0;
            SelMark.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            if (show) Canvas.SetLeft(SelMark, Clamp(StartOf(wantKey) / s.Duration, 0, 1) * w - SelMark.Width / 2);
            UpdateTrimText();
        }

        void UpdateTrimText()
        {
            if (s == null || s.Duration <= 0) { TrimText.Text = ""; return; }
            string at = wantKey >= 0 && s.Fps > 0 ? "Frame at " + FmtPrecise(StartOf(wantKey)) + "   ·   " : "";
            TrimText.Text = at + string.Format(Inv, "{0} – {1}   ·   using {2} of {3}", Fmt(s.UsedStart), Fmt(s.UsedEnd), Fmt(s.UsedLength), Fmt(s.Duration));
        }

        // ---------------------------------------------------------------- AI enhance for the shown frame

        bool enhancing;
        long enhancingKey = -1;

        /// <summary>
        /// Makes the AI-enhanced version of the frame in the preview (with a progress badge), once the playhead
        /// has stopped. Enhanced frames are kept, so going back to one is instant.
        /// </summary>
        async void EnsureEnhanced()
        {
            EnhPill.Visibility = enhancing && enhancingKey == curKey ? Visibility.Visible : Visibility.Collapsed;
            if (s == null || enhancing || scrubbing || cur == null || EnhanceSw.IsChecked != true) return;
            if (!shell.P.HasModel(s.EnhanceStyle ?? EsrModel.Anime)) return;
            long key = curKey;
            if (key != wantKey || enhCache.ContainsKey(key) || enhFailed.Contains(key)) return;

            enhancing = true; enhancingKey = key;
            EnhPill.Visibility = Visibility.Visible;
            int gen = openGen, eg = enhGen;
            var src = s.SourcePath; double t = TimeOf(key); int sw = s.Width, sh = s.Height;
            string model = s.EnhanceStyle ?? EsrModel.Anime; bool clean = s.EnhanceCleanup, tta = s.EnhanceTta;
            int pw = W, ph = H; var outSpec = s.Output;
            var f = await Task.Run(() => shell.P.EnhanceFrame(src, t, sw, sh, pw, ph, model, clean, tta, outSpec));
            enhancing = false; enhancingKey = -1;
            if (gen != openGen) { EnhPill.Visibility = Visibility.Collapsed; return; }
            if (eg == enhGen && pw == W && ph == H)
            {
                if (f == null) { enhFailed.Add(key); if (key == curKey) OffText.Text = "AI enhance couldn't run on this frame"; }
                else { Put(enhCache, enhOrder, MaxEnh, key, f); if (key == curKey) dirty = true; }
            }
            EnsureEnhanced();   // the playhead moved meanwhile, or the options changed
        }
    }
}
