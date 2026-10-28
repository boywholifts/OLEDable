using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OLEDable
{
    /// <summary>The library: every video in the chosen folder as a thumbnail grid. Click one to edit it.</summary>
    public partial class LibraryPage : UserControl, IPage
    {
        public static readonly DependencyProperty TileWProperty = DependencyProperty.Register("TileW", typeof(double), typeof(LibraryPage), new PropertyMetadata(280.0));
        public static readonly DependencyProperty TileHProperty = DependencyProperty.Register("TileH", typeof(double), typeof(LibraryPage), new PropertyMetadata(128.0));
        public double TileW { get { return (double)GetValue(TileWProperty); } set { SetValue(TileWProperty, value); } }
        public double TileH { get { return (double)GetValue(TileHProperty); } set { SetValue(TileHProperty, value); } }

        const double MinTile = 280, Gap = 22;

        readonly MainWindow shell;
        readonly ObservableCollection<LibraryItem> view = new ObservableCollection<LibraryItem>();
        readonly Dictionary<string, LibraryItem> known = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        List<LibraryItem> all = new List<LibraryItem>();
        List<LibraryItem> built;   // the list the tiles were last made from
        FileSystemWatcher watcher;
        int generation;

        public LibraryItem FirstItem { get { return all.FirstOrDefault(); } }

        public LibraryPage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();
            Tiles.ItemsSource = view;

            FolderBtn.Click += (s, e) =>
            {
                var picked = FolderPicker.Pick(shell, "Choose your library folder", shell.S.OutDir);
                if (picked == null) return;
                shell.S.OutDir = picked;
                shell.SaveSettings();
                Watch();
                Refresh();
            };
            ImportBtn.Click += (s, e) => shell.PickAndImport();
            EditBtn.Click += (s, e) => { if (selected != null) shell.OpenItem(selected); };
            SelFolderBtn.Click += (s, e) => { if (selected != null) MainWindow.OpenInShell(selected.Path, true); };
            RefreshBtn.Click += (s, e) => Refresh();
            // the import strip lights up while a file is dragged over it (the window takes the drop)
            ImportStrip.DragEnter += (s, e) => ImportStrip.SetResourceReference(Border.BorderBrushProperty, "Accent");
            ImportStrip.DragLeave += (s, e) => ImportStrip.SetResourceReference(Border.BorderBrushProperty, "Dashed");
            ImportStrip.Drop += (s, e) => ImportStrip.SetResourceReference(Border.BorderBrushProperty, "Dashed");
            EmptyImport.Click += (s, e) => shell.PickAndImport();
            EmptyRecord.Click += (s, e) => shell.ShowWallpaperEngine();
            RecScreenBtn.Click += (s, e) => shell.ShowRecord();
            foreach (var r in new[] { FAll, FRec, FConv, FTodo }) r.Checked += (s, e) => ApplyFilter();
            shell.LiveResizeEnded += Layout;
            // at most one re-layout per frame while the window is being resized
            Scroller.SizeChanged += (s, e) =>
            {
                if (layoutQueued) return;
                layoutQueued = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { layoutQueued = false; Layout(); }));
            };
            debounce.Tick += (s, e) => { debounce.Stop(); Refresh(); };
        }

        public void OnShown()
        {
            Watch();
            Refresh();
            StartPreview();
        }

        public void OnHidden() { preview.Stop(); }

        // ---------------------------------------------------------------- selection and live preview

        readonly TilePreview preview = new TilePreview();
        LibraryItem selected;

        /// <summary>Selects a video: accent ring, it plays in its tile, Edit and its folder appear at the top right.</summary>
        void SelectItem(LibraryItem it)
        {
            if (it != null && it == selected) return;   // already playing
            if (selected != null) selected.IsSelected = false;
            selected = it;
            if (it != null) it.IsSelected = true;
            SelHint.Visibility = it == null ? Visibility.Visible : Visibility.Collapsed;
            EditBtn.Visibility = SelFolderBtn.Visibility = it == null ? Visibility.Collapsed : Visibility.Visible;
            StartPreview();
        }

        void StartPreview()
        {
            var it = selected;
            if (it == null || it.IsImage) { preview.Stop(); return; }   // pictures are already shown as they are
            preview.PlayVideo(o => it.Player = o, it.Path);
        }

        // ---------------------------------------------------------------- loading

        public async void Refresh()
        {
            int gen = ++generation;
            FolderText.Text = shell.S.OutDir;
            var folder = shell.S.OutDir;
            var converted = shell.S.ConvertedPath;
            var weCopies = Workshop.ImportDir(shell.S);
            await Task.Run(() => SourceStore.Tidy(folder, converted));   // libraries from before the rename
            var listed = await Task.Run(() => LibraryScanner.List(folder, converted, weCopies));
            if (gen != generation) return;

            // keep details for files that haven't changed; load the rest in the background
            var todo = new List<LibraryItem>();
            all = listed.Select(it =>
            {
                LibraryItem old;
                if (known.TryGetValue(it.Path, out old) && old.Size == it.Size && old.Modified == it.Modified) return old;
                known[it.Path] = it;
                todo.Add(it);
                return it;
            }).ToList();
            ApplyFilter();

            var ff = shell.Ffmpeg; var fp = shell.Ffprobe; var cache = shell.CacheDir;
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(todo.Select(async it =>
                {
                    await gate.WaitAsync();
                    try { await Task.Run(() => LibraryScanner.LoadDetails(it, ff, fp, cache)); }
                    finally { gate.Release(); }
                }));
            }
            if (gen == generation)
            {
                ApplyFilter(); OfferMoveConverted();   // kinds are known now
                var snapshot = all.ToList();
                _ = Task.Run(() => LibraryScanner.PruneCache(cache, snapshot));
            }
        }

        /// <summary>
        /// Once: exports made before the converted folder existed sit in the library root. Offer to move them
        /// (videos the app made, and older "(OLED)" files) into the converted folder.
        /// </summary>
        void OfferMoveConverted()
        {
            var S = shell.S;
            if (S.ConvertedOffered) return;
            var old = all.Where(i => !i.InConverted && (i.Id != null || i.Name.EndsWith("(OLED)", StringComparison.OrdinalIgnoreCase))).ToList();
            S.ConvertedOffered = true;
            shell.SaveSettings();
            if (old.Count == 0) return;
            if (!Dialog.Ask(shell, "Converted videos folder", "Exports now go to their own folder:\n" + S.ConvertedPath +
                            "\n\nMove the " + old.Count + " converted video" + (old.Count == 1 ? "" : "s") + " already in your library there too?",
                            "Move them", "Not now")) return;
            int moved = 0;
            try { Directory.CreateDirectory(S.ConvertedPath); } catch { }
            foreach (var it in old)
            {
                try
                {
                    var dest = Path.Combine(S.ConvertedPath, Path.GetFileName(it.Path));
                    if (File.Exists(dest)) continue;
                    File.Move(it.Path, dest);
                    moved++;
                }
                catch { }
            }
            shell.ShowToast("Moved " + moved + " video" + (moved == 1 ? "" : "s") + " to " + Path.GetFileName(S.ConvertedPath.TrimEnd('\\')));
            Refresh();
        }

        void ApplyFilter()
        {
            IEnumerable<LibraryItem> items = all;
            // Converted = everything the app exported (recordings too); Recordings = exports that started as a
            // recording; Originals = source videos the app didn't make
            if (FRec.IsChecked == true) items = items.Where(i => i.Kind == "recording");
            else if (FConv.IsChecked == true) items = items.Where(i => i.Id != null || i.InConverted);
            else if (FTodo.IsChecked == true) items = items.Where(i => i.Id == null && !i.InConverted);
            // tiles are made once per scan of the folder (and only when something changed); the filters only show
            // or hide them (rebuilding every tile on each switch gets slow as the library grows)
            if (built == null || !built.SequenceEqual(all))
            {
                built = all;
                view.Clear();
                foreach (var i in all) view.Add(i);
            }
            var shown = new HashSet<LibraryItem>(items);
            foreach (var i in all) i.IsShown = shown.Contains(i);
            // keep the selection through a re-scan (same file), drop it when it's gone or filtered out
            if (selected != null)
            {
                var same = all.FirstOrDefault(i => string.Equals(i.Path, selected.Path, StringComparison.OrdinalIgnoreCase));
                if (same == null || !shown.Contains(same)) SelectItem(null);
                else if (same != selected) { selected.IsSelected = false; preview.Stop(); selected = null; SelectItem(same); }
            }
            ImportStrip.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Scroller.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CountText.Text = all.Count == 1 ? "1 video" : all.Count + " videos";
        }

        bool layoutQueued;

        /// <summary>
        /// Tile size for the width. The scrollbar's width is always set aside, so it appearing or disappearing
        /// can't flip the column count back and forth (that was the flicker while resizing slowly).
        /// </summary>
        void Layout()
        {
            double avail = Scroller.ActualWidth - ScrollBarW - 4;   // and the room for the highlight ring
            if (avail <= 0) return;
            if (shell.LiveResizing && TileW > 0) return;   // tiles keep their size during a window drag and re-flow; sized to fit when it ends
            int cols = Math.Max(1, (int)(avail / (MinTile + Gap)));
            double w = Math.Floor(avail / cols - Gap);
            if (Math.Abs(w - TileW) < 0.5) return;   // nothing to re-lay out
            TileW = w;
            TileH = Math.Round(w * 1048 / 2288);
        }

        const double ScrollBarW = 10;

        void Watch()
        {
            if (watcher != null && string.Equals(watcher.Path, shell.S.OutDir, StringComparison.OrdinalIgnoreCase)) return;
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (!Directory.Exists(shell.S.OutDir)) return;
            try
            {
                // subfolders too (the converted folder), but not the app's own working folder, where smoothing
                // writes thousands of frames
                watcher = new FileSystemWatcher(shell.S.OutDir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                FileSystemEventHandler h = (s, e) =>
                {
                    if (e.FullPath.IndexOf("\\" + SourceStore.FolderName, StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (e.FullPath.IndexOf("\\" + SourceStore.OldFolderName, StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!LibraryScanner.IsMedia(e.FullPath) && Path.HasExtension(e.FullPath)) return;
                    // an export in progress writes "name.partial.mp4" constantly; the rename at the end is what counts
                    if (e.ChangeType != WatcherChangeTypes.Renamed && e.FullPath.IndexOf(".partial.", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
                };
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += (s, e) => h(s, e);
                watcher.InternalBufferSize = 64 * 1024;
            }
            catch { }
        }

        // ---------------------------------------------------------------- tiles

        void Tile_Click(object sender, RoutedEventArgs e)
        {
            var it = (sender as FrameworkElement)?.Tag as LibraryItem;
            if (it != null) SelectItem(it);
        }

        void Tile_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var it = (sender as FrameworkElement)?.Tag as LibraryItem;
            if (it == null) return;
            SelectItem(it);
            shell.OpenItem(it);
            e.Handled = true;
        }

        void Tile_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var it = el?.Tag as LibraryItem;
            if (it == null) return;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Edit", () => shell.OpenItem(it)));
            menu.Items.Add(Item("Play", () => MainWindow.OpenInShell(it.Path)));
            menu.Items.Add(Item("Show in folder", () => MainWindow.OpenInShell(it.Path, true)));
            menu.Items.Add(Item("Delete", () => Delete(it)));
            menu.PlacementTarget = el;
            menu.IsOpen = true;
            e.Handled = true;
        }

        static MenuItem Item(string header, Action a)
        {
            var m = new MenuItem { Header = header };
            m.Click += (s, e) => a();
            return m;
        }

        void Delete(LibraryItem it)
        {
            if (!Dialog.Ask(shell, "Delete video", "Move “" + it.Name + "” to the Recycle Bin?" +
                            (it.Id != null ? "\n\nIts kept original and settings are removed too." : ""), "Delete")) return;
            if (it == selected) SelectItem(null);   // stops the preview, which has the file open
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                   Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (it.Id != null) shell.Store.Forget(it.Id);
            }
            catch (Exception ex) { shell.ShowToast("Couldn't delete: " + ex.Message); }
            Refresh();
        }
    }
}
