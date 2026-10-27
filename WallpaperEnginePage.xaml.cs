using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HydroShiftRecorder
{
    /// <summary>
    /// Every Wallpaper Engine wallpaper on this PC as a grid. A video wallpaper is copied into the library and
    /// opened in the editor; a scene can be unpacked (RePKG) for its background image or a video inside it.
    /// </summary>
    public partial class WallpaperEnginePage : UserControl, IPage
    {
        public static readonly DependencyProperty TileWProperty = DependencyProperty.Register("TileW", typeof(double), typeof(WallpaperEnginePage), new PropertyMetadata(260.0));
        public static readonly DependencyProperty TileHProperty = DependencyProperty.Register("TileH", typeof(double), typeof(WallpaperEnginePage), new PropertyMetadata(146.0));
        public double TileW { get { return (double)GetValue(TileWProperty); } set { SetValue(TileWProperty, value); } }
        public double TileH { get { return (double)GetValue(TileHProperty); } set { SetValue(TileHProperty, value); } }

        const double MinTile = 240, Gap = 22;

        readonly MainWindow shell;
        readonly ObservableCollection<WeItem> view = new ObservableCollection<WeItem>();
        List<WeItem> all = new List<WeItem>();
        bool loaded;
        int generation;

        public WallpaperEnginePage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();
            Tiles.ItemsSource = view;
            foreach (var r in new[] { FAll, FVideo, FScene, FOther }) r.Checked += (s, e) => ApplyFilter();
            SearchBox.TextChanged += (s, e) =>
            {
                SearchPh.Visibility = SearchBox.Text.Length > 0 ? Visibility.Hidden : Visibility.Visible;
                ApplyFilter();
            };
            RefreshBtn.Click += (s, e) => Refresh();
            EditBtn.Click += (s, e) => { if (selected != null) ImportVideo(selected); };
            RecordBtn.Click += (s, e) => { if (selected != null) AskAndRecord(selected); };
            StillBtn.Click += (s, e) => { if (selected != null) ExtractScene(selected, false); };
            UseVideoBtn.Click += (s, e) => { if (selected != null) ExtractScene(selected, true); };
            FolderBtn.Click += (s, e) => { if (selected != null) MainWindow.OpenInShell(selected.Folder); };
            Scroller.SizeChanged += (s, e) => Layout();
        }

        public void OnShown() { if (!loaded) Refresh(); }
        public void OnHidden() { }

        async void Refresh()
        {
            loaded = true;
            int gen = ++generation;
            CountText.Text = "Looking…";
            // the icon pulses while it looks
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.5)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            RefreshIcon.BeginAnimation(OpacityProperty, pulse);
            List<WeItem> items;
            try { items = await Task.Run(() => Workshop.Scan()); }
            finally { RefreshIcon.BeginAnimation(OpacityProperty, null); }
            if (gen != generation) return;
            all = items;
            SelectItem(null);
            foreach (var it in all) it.Meta = Meta(it);
            ApplyFilter();
            if (all.Count == 0)
            {
                EmptyText.Text = Workshop.Roots().Count == 0
                    ? "Wallpaper Engine's Steam folders weren't found on this PC."
                    : "Subscribe to wallpapers in Wallpaper Engine (Steam Workshop) and they'll show up here.";
            }
            using (var gate = new SemaphoreSlim(6))
            {
                await Task.WhenAll(all.Select(async it =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var img = await Task.Run(() => Workshop.LoadPreview(it.PreviewPath));
                        if (gen == generation) it.Thumb = img;
                    }
                    finally { gate.Release(); }
                }));
            }
        }

        string Meta(WeItem it)
        {
            var bits = new List<string> { it.Source };
            if (Workshop.AlreadyCopied(shell.S, it)) bits.Add("in your library");
            return string.Join("  ·  ", bits);
        }

        void ApplyFilter()
        {
            IEnumerable<WeItem> items = all;
            if (FVideo.IsChecked == true) items = items.Where(i => i.IsVideo);
            else if (FScene.IsChecked == true) items = items.Where(i => i.IsScene);
            else if (FOther.IsChecked == true) items = items.Where(i => !i.IsVideo && !i.IsScene);
            var q = SearchBox.Text.Trim();
            if (q.Length > 0) items = items.Where(i => i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = items.ToList();
            if (selected != null && !list.Contains(selected)) SelectItem(null);   // filtered out
            view.Clear();
            foreach (var i in list) view.Add(i);
            CountText.Text = list.Count == all.Count ? all.Count + " wallpapers" : list.Count + " of " + all.Count;
            Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Scroller.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void Layout()
        {
            double avail = (Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth) - 4;   // the room for the ring
            if (avail <= 0) return;
            int cols = Math.Max(1, (int)(avail / (MinTile + Gap)));
            TileW = Math.Floor(avail / cols - Gap);
            TileH = Math.Round(TileW * 9 / 16);
        }

        // ---------------------------------------------------------------- actions

        WeItem selected;

        /// <summary>Selects a wallpaper: accent ring on its tile, its actions at the top right.</summary>
        void SelectItem(WeItem it)
        {
            if (selected != null) selected.IsSelected = false;
            selected = it;
            if (it != null) it.IsSelected = true;
            UpdateActions();
        }

        void UpdateActions()
        {
            var it = selected;
            SelHint.Visibility = it == null ? Visibility.Visible : Visibility.Collapsed;
            SelActions.Visibility = it == null ? Visibility.Collapsed : Visibility.Visible;
            FolderBtn.Visibility = SelActions.Visibility;
            if (it == null) return;
            EditBtn.Visibility = it.IsVideo ? Visibility.Visible : Visibility.Collapsed;
            RecordBtn.Visibility = it.CanRecord ? Visibility.Visible : Visibility.Collapsed;
            StillBtn.Visibility = it.IsScene ? Visibility.Visible : Visibility.Collapsed;
            UseVideoBtn.Visibility = it.HasVideo ? Visibility.Visible : Visibility.Collapsed;
            SelNote.Visibility = it.Unsupported ? Visibility.Visible : Visibility.Collapsed;
            SelNote.Text = it.KindLabel + " wallpapers can't be imported";
        }

        void Tile_Click(object sender, RoutedEventArgs e)
        {
            var it = (sender as FrameworkElement)?.Tag as WeItem;
            if (it != null) SelectItem(it);
        }

        /// <summary>Double-click: the main action (edit a video, record a scene or web wallpaper).</summary>
        void Tile_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var it = (sender as FrameworkElement)?.Tag as WeItem;
            if (it == null) return;
            SelectItem(it);
            if (it.IsVideo) ImportVideo(it);
            else if (it.CanRecord) AskAndRecord(it);
            e.Handled = true;
        }

        /// <summary>Opens the small window to pick the size, length and frame rate, then records.</summary>
        void AskAndRecord(WeItem it)
        {
            var c = RecordSceneDialog.Show(shell, it.Name, shell.S.Seconds, shell.S.Fps);
            if (c == null) return;
            shell.S.Seconds = c.Seconds; shell.S.Fps = c.Fps;   // shared with Record screen
            shell.SaveSettings();
            RecordScene(it, c.W, c.H, c.Seconds, c.Fps);
        }

        async void RecordScene(WeItem it, int w, int h, int secs, int fps)
        {
            var S = shell.S;
            var id = SourceStore.NewId();
            var dest = shell.Store.RecordingPath(id);
            var stop = new StopSignal();
            bool ok = await shell.RunJob("Recording " + it.Name, async (pr, ct) =>
            {
                var enc = await SceneRecorder.RecordAsync(shell.P, S, it, w, h, secs, fps, dest, stop, pr, ct);
                if (enc != S.Encoder) { S.Encoder = enc; shell.SaveSettings(); }
            });
            if (!ok) { Tools.TryDelete(dest); return; }
            var session = new EditSession
            {
                Id = id, Title = it.Name, SourcePath = dest, Kind = "recording",
                Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                OutputName = shell.UniqueOutputName(it.Name),
                // recorded at the size it's meant for: the HydroShift, or a custom size of exactly that
                OutputMode = w == OutputSpec.PanelW && h == OutputSpec.PanelH ? null : OutputSpec.Custom,
                CustomW = w, CustomH = h,
            };
            session.Oled.Black = -1;   // detect
            shell.Store.Save(session);
            it.Meta = Meta(it);
            shell.OpenEditor(session);
        }

        async void ImportVideo(WeItem it)
        {
            string copy = null;
            bool ok = await shell.RunJob("Copying " + it.Name, (pr, ct) => Task.Run(() =>
            {
                pr.Report(new JobStatus("Copying the video into your library…", -1));
                copy = Workshop.CopyVideo(shell.S, it);
            }, ct));
            if (!ok || copy == null) return;
            it.Meta = Meta(it);
            shell.Import(copy);
        }

        async void ExtractScene(WeItem it, bool video)
        {
            string result = null;
            bool ok = await shell.RunJob((video ? "Taking the video from: " : "Extracting still: ") + it.Name, async (pr, ct) =>
            {
                var dir = await Workshop.UnpackSceneAsync(shell.DataDir, it, pr, ct);
                try { result = video ? Workshop.PickVideo(shell.S, it, dir) : Workshop.PickStill(shell.S, it, dir); }
                finally { Tools.TryDeleteDir(dir); }
            });
            if (!ok) return;
            if (result == null)
            {
                shell.ShowToast(video ? "No video was found inside this scene. Try Record or Still image." : "No image was found inside this scene.");
                return;
            }
            it.Meta = Meta(it);
            shell.Import(result);
        }
    }
}
