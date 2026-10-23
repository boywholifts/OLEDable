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

namespace HydroShiftRecorder
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
                var picked = FolderPicker.Pick(shell, "Choose the folder for your HydroShift videos", shell.S.OutDir);
                if (picked == null) return;
                shell.S.OutDir = picked;
                shell.SaveSettings();
                Watch();
                Refresh();
            };
            ImportBtn.Click += (s, e) => shell.PickAndImport();
            EmptyImport.Click += (s, e) => shell.PickAndImport();
            RecordBtn.Click += (s, e) => shell.ShowRecord();
            EmptyRecord.Click += (s, e) => shell.ShowRecord();
            foreach (var r in new[] { FAll, FRec, FConv, FTodo }) r.Checked += (s, e) => ApplyFilter();
            Scroller.SizeChanged += (s, e) => Layout();
            debounce.Tick += (s, e) => { debounce.Stop(); Refresh(); };
        }

        public void OnShown()
        {
            Watch();
            Refresh();
        }

        public void OnHidden() { }

        // ---------------------------------------------------------------- loading

        public async void Refresh()
        {
            int gen = ++generation;
            FolderText.Text = shell.S.OutDir;
            var folder = shell.S.OutDir;
            var converted = shell.S.ConvertedPath;
            var listed = await Task.Run(() => LibraryScanner.List(folder, converted));
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
            if (gen == generation) { ApplyFilter(); OfferMoveConverted(); }   // kinds are known now
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
            var list = items.ToList();
            view.Clear();
            foreach (var i in list) view.Add(i);
            if (all.Count > 0) view.Add(new LibraryItem { IsAddTile = true, Name = "" });
            Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Scroller.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CountText.Text = all.Count == 1 ? "1 video" : all.Count + " videos";
        }

        void Layout()
        {
            double avail = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
            if (avail <= 0) return;
            int cols = Math.Max(1, (int)(avail / (MinTile + Gap)));
            TileW = Math.Floor(avail / cols - Gap);
            TileH = Math.Round(TileW * 1048 / 2288);
        }

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
                    if (e.FullPath.IndexOf("\\.hydroshift", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!LibraryScanner.IsVideo(e.FullPath) && Path.HasExtension(e.FullPath)) return;
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
            if (it == null) return;
            if (it.IsAddTile) shell.ShowRecord();
            else shell.OpenItem(it);
        }

        void Tile_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var it = el?.Tag as LibraryItem;
            if (it == null || it.IsAddTile) return;
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
