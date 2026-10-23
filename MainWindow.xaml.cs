using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>A page shown in the main window's content area.</summary>
    public interface IPage
    {
        void OnShown();
        void OnHidden();
    }

    /// <summary>
    /// The app shell: sidebar, title bar, page switching, and the services every page uses — settings, the
    /// pipeline, the library's source store, the one running job, and finding the HydroShift display.
    /// </summary>
    public partial class MainWindow : Window
    {
        public readonly Settings S;
        public readonly string DataDir, SettingsPath, CacheDir, Ffmpeg, Ffprobe;
        public readonly Pipeline P;
        public SourceStore Store { get { return new SourceStore(S.OutDir); } }

        public bool Busy { get; private set; }
        public event Action BusyChanged;
        public bool DisplayFound { get { return S.Adapter >= 0; } }
        public event Action DisplayChanged;

        readonly string snapshotDir;
        LibraryPage library;
        EditorPage editor;
        RecordPage record;
        SettingsPage settings;
        IPage current;
        CancellationTokenSource jobCts;
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };

        public MainWindow(string snapshotDir)
        {
            this.snapshotDir = snapshotDir;
            DataDir = Tools.DataDir();
            SettingsPath = Path.Combine(DataDir, "HydroShift-Recorder.json");
            CacheDir = Path.Combine(Path.GetTempPath(), "HydroShift Recorder", "thumbs");
            S = Settings.Load(SettingsPath);
            // tools chosen in the installer win; otherwise look on PATH
            Ffmpeg = !string.IsNullOrWhiteSpace(S.FfmpegPath) && File.Exists(S.FfmpegPath) ? S.FfmpegPath : Find("ffmpeg");
            var probeNext = Ffmpeg == null ? null : Path.Combine(Path.GetDirectoryName(Ffmpeg), "ffprobe.exe");
            Ffprobe = probeNext != null && File.Exists(probeNext) ? probeNext : Find("ffprobe");
            P = new Pipeline(Ffmpeg, Ffprobe,
                             string.IsNullOrWhiteSpace(S.RifeDir) ? Path.Combine(DataDir, "rife") : S.RifeDir,
                             string.IsNullOrWhiteSpace(S.EsrganDir) ? Path.Combine(DataDir, "realesrgan") : S.EsrganDir);
            P.Speeds = new SpeedBook(S.Rates);   // learns as exports run; saved with the settings
            ImportOldDisplaySetting();

            Theme.Apply(Theme.Effective(S), S.Accent);
            InitializeComponent();
            TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();   // job progress on the taskbar button

            NavLibrary.Checked += (s, e) => ShowPage(Library);
            NavRecord.Checked += (s, e) => ShowPage(Record);
            NavSettings.Checked += (s, e) => ShowPage(SettingsPg);
            NavEditing.Checked += (s, e) => ShowPage(Editor);
            EditCloseBtn.Click += (s, e) => { e.Handled = true; CloseEditor(); };
            BtnMin.Click += (s, e) => WindowState = WindowState.Minimized;
            BtnMax.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            BtnClose.Click += (s, e) => Close();
            StateChanged += (s, e) => FitMaximized();
            JobCancel.Click += (s, e) => { if (jobCts != null) jobCts.Cancel(); };
            DisplayCard.MouseLeftButtonUp += (s, e) => DetectDisplay();
            UpdateNow.Click += (s, e) => InstallUpdate();
            UpdateNotes.Click += (s, e) => { if (update != null) OpenInShell(update.Page); };
            UpdateDismiss.Click += (s, e) =>
            {
                if (update != null) { S.SkippedVersion = update.Version.ToString(); SaveSettings(); }
                UpdateCard.Visibility = Visibility.Collapsed;
            };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

            Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Import(files[0]);
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsThemeChanged;
            Closing += (s, e) =>
            {
                if (jobCts != null) jobCts.Cancel();
                if (record != null) record.OnHidden();
                if (editor != null) editor.FlushAutosave();
                SaveSettings();
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
            };

            Loaded += async (s, e) =>
            {
                NavLibrary.IsChecked = true;
                // updates: in the background, at most every 3 hours; local builds (dist folder) never prompt on their own
                if (snapshotDir == null && S.AutoUpdateCheck && !Updater.IsLocalBuild && (DateTime.Now - S.LastUpdateCheck).TotalHours >= 3)
                    _ = CheckForUpdate(false);
                if (Ffmpeg == null)
                {
                    SetDisplay(false, "ffmpeg is not installed", "Run  winget install Gyan.FFmpeg  then reopen the app.");
                    return;
                }
                if (snapshotDir != null) { await RunSnapshots(snapshotDir); return; }
                if (S.Adapter >= 0) { SetDisplay(true, "HydroShift connected", "2288 × 1048"); DisplayChanged?.Invoke(); }
                else DetectDisplay();
            };
        }

        // ---------------------------------------------------------------- pages

        LibraryPage Library { get { return library ?? (library = new LibraryPage(this)); } }
        RecordPage Record { get { return record ?? (record = new RecordPage(this)); } }
        SettingsPage SettingsPg { get { return settings ?? (settings = new SettingsPage(this)); } }
        EditorPage Editor { get { return editor ?? (editor = new EditorPage(this)); } }

        void ShowPage(IPage page)
        {
            if (current == page) return;
            if (current != null) current.OnHidden();
            current = page;
            PageHost.Content = page;
            page.OnShown();
        }

        public void ShowLibrary() { if (NavLibrary.IsChecked == true) ShowPage(Library); else NavLibrary.IsChecked = true; }
        public void ShowRecord() { if (NavRecord.IsChecked == true) ShowPage(Record); else NavRecord.IsChecked = true; }

        /// <summary>
        /// Opens a session in the editor. The sidebar gets an "Editing" item, so you can go to other pages and
        /// come back to the edit as you left it. Asks first if another edit has changes that weren't exported.
        /// </summary>
        public void OpenEditor(EditSession s)
        {
            if (editor != null && editor.HasSession && !editor.IsSame(s) && !ConfirmLeaveEdit()) return;
            Editor.Open(s);
            ShowEditing();
        }

        void ShowEditing()
        {
            EditingText.Text = editor.SessionTitle;
            NavEditing.Visibility = Visibility.Visible;
            if (NavEditing.IsChecked == true) ShowPage(Editor); else NavEditing.IsChecked = true;
        }

        public void SetEditingTitle(string title) { EditingText.Text = title; }

        /// <summary>True when it's fine to drop the current edit (nothing unexported, or the user agreed).</summary>
        bool ConfirmLeaveEdit()
        {
            if (editor == null || !editor.Unsaved) return true;
            if (editor.AutoSaved)
                return Dialog.Ask(this, "Close edit", "“" + editor.SessionTitle + "” has changes that haven't been exported. " +
                                  "They're saved: open it from the library to carry on.", "Close");
            return Dialog.Ask(this, "Close edit", "“" + editor.SessionTitle + "” has changes that haven't been exported.\n\n" +
                              "Close anyway? Your changes will be lost.", "Close anyway");
        }

        /// <summary>Closes the current edit (the × on the sidebar item).</summary>
        public void CloseEditor()
        {
            if (editor == null || !ConfirmLeaveEdit()) return;
            bool wasShowing = current == editor;
            editor.Close();
            NavEditing.Visibility = Visibility.Collapsed;
            NavEditing.IsChecked = false;
            if (wasShowing || current == null) ShowLibrary();
        }

        /// <summary>
        /// Opens a library video: from its kept original and saved settings when the app made it. If it's the
        /// video already open in the editor, just goes back to that edit.
        /// </summary>
        public void OpenItem(LibraryItem it)
        {
            if (editor != null && editor.HasSession && editor.IsFor(it)) { ShowEditing(); return; }
            EditSession s = null;
            if (it.Id != null)
            {
                s = Store.Load(it.Id);
                if (s != null && (s.SourcePath == null || !File.Exists(s.SourcePath))) s = null;   // original gone: edit the video itself
                if (s != null) { s.OutputName = Path.GetFileName(it.Path); s.ExistingPath = it.Path; }
            }
            if (s == null)
            {
                s = new EditSession
                {
                    Id = SourceStore.NewId(), Title = it.Name, SourcePath = it.Path, Kind = "video",
                    Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                    OutputName = UniqueOutputName(it.Name + " (OLED)"),
                };
                s.Oled.Black = -1;   // detect
            }
            OpenEditor(s);
        }

        /// <summary>Imports a video file, a Wallpaper Engine wallpaper folder or its project.json into the editor.</summary>
        public void Import(string path)
        {
            string title = null, pj = null;
            if (Directory.Exists(path)) pj = Path.Combine(path, "project.json");
            else if (string.Equals(Path.GetFileName(path), "project.json", StringComparison.OrdinalIgnoreCase)) pj = path;
            if (pj != null)
            {
                if (!File.Exists(pj)) { ShowToast("That folder isn't a Wallpaper Engine wallpaper."); return; }
                WeProject proj;
                try { proj = WeProject.Load(pj); } catch { ShowToast("Couldn't read that wallpaper's project.json."); return; }
                var type = (proj.Type ?? "").ToLowerInvariant();
                title = proj.Title;
                if (type != "video")
                {
                    ShowToast("“" + title + "” is a " + (type.Length > 0 ? type : "non-video") + " wallpaper. Set it on the HydroShift and record it instead.");
                    ShowRecord();
                    return;
                }
                path = Path.Combine(Path.GetDirectoryName(pj), proj.File ?? "");
            }
            if (!File.Exists(path) || !LibraryScanner.IsVideo(path) && Path.GetExtension(path).ToLowerInvariant() != ".gif")
            {
                ShowToast("That isn't a video the app can open.");
                return;
            }
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
            var s = new EditSession
            {
                Id = SourceStore.NewId(), Title = title, SourcePath = path, Kind = "video",
                Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                OutputName = UniqueOutputName(title),
            };
            s.Oled.Black = -1;
            OpenEditor(s);
        }

        public void PickAndImport()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a video for the HydroShift",
                Filter = "Videos|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.gif|Wallpaper Engine project|project.json|All files|*.*",
            };
            var we = FindWorkshopDir();
            if (we != null) d.InitialDirectory = we;
            if (d.ShowDialog(this) == true) Import(d.FileName);
        }

        public string UniqueOutputName(string title)
        {
            var n = SafeName(title);
            if (n.Length == 0) n = "HydroShift " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");
            var name = n + ".mp4";
            for (int i = 2; File.Exists(Path.Combine(S.ConvertedPath, name)) || File.Exists(Path.Combine(S.OutDir, name)); i++)
                name = n + " (" + i + ").mp4";
            return name;
        }

        /// <summary>
        /// Where exporting this session writes: over the video it was opened from (wherever that is), otherwise
        /// into the converted-videos folder.
        /// </summary>
        public string OutputPathFor(EditSession s)
        {
            if (s.ExistingPath != null && File.Exists(s.ExistingPath) &&
                string.Equals(Path.GetFileName(s.ExistingPath), s.OutputName, StringComparison.OrdinalIgnoreCase))
                return s.ExistingPath;
            return Path.Combine(S.ConvertedPath, s.OutputName ?? "");
        }

        // ---------------------------------------------------------------- jobs

        /// <summary>
        /// Runs one background job at a time with progress in the sidebar. Returns true on success;
        /// failures are shown to the user, cancelling is silent.
        /// </summary>
        public async Task<bool> RunJob(string title, Func<IProgress<JobStatus>, CancellationToken, Task> work, IProgress<JobStatus> also = null)
        {
            if (Busy) { ShowToast("Something is already running. Wait for it to finish or cancel it."); return false; }
            Busy = true; BusyChanged?.Invoke();
            jobCts = new CancellationTokenSource();
            JobTitle.Text = title;
            JobText.Text = "Starting…";
            JobProg.Value = 0;
            JobCard.Visibility = Visibility.Visible;
            var pr = new Progress<JobStatus>(st =>
            {
                JobText.Text = st.Text;
                JobProg.IsIndeterminate = st.Fraction < 0;
                if (st.Fraction >= 0) JobProg.Value = st.Fraction;
                TaskbarItemInfo.ProgressState = st.Fraction < 0 ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                                                                : System.Windows.Shell.TaskbarItemProgressState.Normal;
                if (st.Fraction >= 0) TaskbarItemInfo.ProgressValue = st.Fraction;
                if (also != null) also.Report(st);
            });
            try
            {
                await work(pr, jobCts.Token);
                return true;
            }
            catch (OperationCanceledException) { ShowToast("Cancelled"); return false; }
            catch (Exception ex)
            {
                if (jobCts.IsCancellationRequested) { ShowToast("Cancelled"); return false; }
                Dialog.Tell(this, "Something went wrong", ex.Message);
                return false;
            }
            finally
            {
                jobCts.Dispose(); jobCts = null;
                JobCard.Visibility = Visibility.Collapsed;
                TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                Busy = false; BusyChanged?.Invoke();
            }
        }

        public void CancelJob() { if (jobCts != null) jobCts.Cancel(); }

        /// <summary>Exports the session into the library folder, keeping its settings for later re-editing.</summary>
        public async Task<bool> Export(EditSession s)
        {
            if (string.IsNullOrEmpty(s.OutputName)) s.OutputName = UniqueOutputName(s.Title);
            var outPath = OutputPathFor(s);
            var snap = s.Clone();
            Store.Save(snap);
            S.RememberOled(snap.Oled); S.Loop = snap.Loop; S.Smooth = snap.Smooth;
            S.RememberEnhance(snap);
            SaveSettings();
            bool ok = await RunJob("Exporting " + (s.Title ?? s.OutputName), (pr, ct) => P.ExportAsync(snap, outPath, pr, ct));
            SaveSettings();   // keeps the step speeds it measured, for better time estimates next time
            if (ok)
            {
                if (!S.KeepSources && snap.Kind == "recording")
                {
                    // originals not kept: the exported video becomes the thing to edit next time
                    Store.Forget(snap.Id);
                }
                s.ExistingPath = outPath;
                ShowToast("Saved " + s.OutputName + " to " + Path.GetFileName(Path.GetDirectoryName(outPath)));
                if (library != null) library.Refresh();
            }
            return ok;
        }

        // ---------------------------------------------------------------- updates

        Updater.Release update;

        /// <summary>
        /// Looks for a newer release on the chosen channel and shows the sidebar card. Returns a short status for
        /// Settings. <paramref name="manual"/> = asked for in Settings: shows skipped versions too.
        /// </summary>
        public async Task<string> CheckForUpdate(bool manual)
        {
            try
            {
                var r = await Updater.CheckAsync(S.UpdateChannel, CancellationToken.None);
                S.LastUpdateCheck = DateTime.Now;
                SaveSettings();
                if (r == null) { UpdateCard.Visibility = Visibility.Collapsed; return "You're up to date (" + Updater.CurrentText + ")."; }
                if (!manual && S.SkippedVersion == r.Version.ToString()) return "";
                update = r;
                UpdateTitle.Text = "Update available";
                UpdateText.Text = "Version " + r.Version + (r.Prerelease ? " (dev build)" : "") + " is ready. You have " + Updater.CurrentText + ".";
                UpdateCard.Visibility = Visibility.Visible;
                return "Version " + r.Version + " is available: see the card in the sidebar.";
            }
            catch (Exception ex)
            {
                return manual ? "Couldn't check for updates: " + ex.Message : "";
            }
        }

        /// <summary>Downloads the update, then closes the app while the setup updates it in place and reopens it.</summary>
        async void InstallUpdate()
        {
            if (update == null) return;
            if (editor != null && editor.Unsaved && !editor.AutoSaved &&
                !Dialog.Ask(this, "Install update", "“" + editor.SessionTitle + "” has changes that haven't been exported. Update anyway and lose them?", "Update")) return;
            string setup = null;
            var r = update;
            bool ok = await RunJob("Updating to " + r.Version, async (pr, ct) => { setup = await Updater.DownloadAsync(r, pr, ct); });
            if (!ok || setup == null) return;
            try { Updater.Install(setup); }
            catch (Exception ex) { Dialog.Tell(this, "Couldn't start the update", ex.Message); return; }
            Close();
        }

        // ---------------------------------------------------------------- the HydroShift display

        void ImportOldDisplaySetting()
        {
            // reuse the display found by the old PowerShell script, if it was in this folder
            var oldCfg = Path.Combine(DataDir, "hydroshift-capture.json");
            if (S.Adapter >= 0 || !File.Exists(oldCfg)) return;
            try
            {
                var t = File.ReadAllText(oldCfg);
                var a = System.Text.RegularExpressions.Regex.Match(t, "\"Adapter\"\\s*:\\s*(\\d+)");
                var o = System.Text.RegularExpressions.Regex.Match(t, "\"Output\"\\s*:\\s*(\\d+)");
                if (t.Contains("\"dda\"") && a.Success && o.Success) { S.Adapter = int.Parse(a.Groups[1].Value); S.Output = int.Parse(o.Groups[1].Value); }
            }
            catch { }
        }

        void SetDisplay(bool ok, string text, string sub)
        {
            DisplayDot.Fill = Theme.Brush(ok ? "#FF22B07D" : "#FF8E8E93");
            DisplayText.Text = text;
            DisplaySub.Text = sub;
        }

        bool detecting;

        /// <summary>Looks for the 2288 × 1048 display on every graphics adapter, in the background.</summary>
        public async void DetectDisplay()
        {
            if (detecting || Ffmpeg == null) return;
            detecting = true;
            SetDisplay(false, "Looking for HydroShift…", "");
            var ff = Ffmpeg;
            int lastA = S.Adapter >= 0 ? S.Adapter : S.LastAdapter, lastO = S.Adapter >= 0 ? S.Output : S.LastOutput;
            var found = await Task.Run(() =>
            {
                var png = Path.Combine(Path.GetTempPath(), "hsr_probe.png");
                // try the last known spot first, then every adapter/output; an output that
                // can't be captured (e.g. an HDR monitor) must not hide the ones after it
                var spots = new System.Collections.Generic.List<int[]>();
                if (lastA >= 0) spots.Add(new[] { lastA, lastO });
                for (int a = 0; a <= 3; a++) for (int o = 0; o <= 5; o++) spots.Add(new[] { a, o });
                int missAdapter = -1;
                foreach (var spot in spots)
                {
                        int a = spot[0], o = spot[1];
                        if (a == missAdapter) continue;
                        TryDelete(png);
                        var j = new FfJob(ff, FfArgs.Grab(a, o, png));
                        if (!j.Wait(10000)) j.Kill();
                        j.Drain();
                        if (!File.Exists(png))
                        {
                            if (o == 0 && a > 0) missAdapter = a;   // a second card with no output 0 has no screens
                            continue;
                        }
                        try
                        {
                            var f = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                            if ((f.PixelWidth == 2288 && f.PixelHeight == 1048) || (f.PixelWidth == 1048 && f.PixelHeight == 2288))
                                return new[] { a, o };
                        }
                        catch { }
                }
                return null;
            });
            detecting = false;
            if (found != null)
            {
                S.Adapter = S.LastAdapter = found[0]; S.Output = S.LastOutput = found[1]; SaveSettings();
                SetDisplay(true, "HydroShift connected", "2288 × 1048");
            }
            else
            {
                S.Adapter = -1; SaveSettings();
                SetDisplay(false, "HydroShift not found", "Turn it on as a secondary screen in L-Connect 3, then click here.");
            }
            DisplayChanged?.Invoke();
        }

        // ---------------------------------------------------------------- appearance

        public void ApplyTheme()
        {
            Theme.Apply(Theme.Effective(S), S.Accent);
            SaveSettings();
        }

        void OnWindowsThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (!S.FollowWindows) return;
            Dispatcher.BeginInvoke(new Action(() => Theme.Apply(Theme.Effective(S), S.Accent)));
        }

        /// <summary>A maximized custom-chrome window overhangs the screen by the resize border; pad it back in.</summary>
        void FitMaximized()
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
                : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
        }

        // ---------------------------------------------------------------- misc

        public void SaveSettings() { S.Save(SettingsPath); }

        public void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            toastTimer.Stop();
            toastTimer.Start();
        }

        public static void OpenInShell(string path, bool select = false)
        {
            try
            {
                if (select) Process.Start("explorer.exe", "/select," + Q(path));
                else Process.Start(path);
            }
            catch { }
        }

        // ---------------------------------------------------------------- QA snapshots

        async Task RunSnapshots(string dir)
        {
            Directory.CreateDirectory(dir);
            Width = 1280; Height = 800;
            WindowState = WindowState.Normal;
            await Task.Delay(800);
            var items = LibraryScanner.List(S.OutDir, S.ConvertedPath);
            bool first = true;
            foreach (var theme in new[] { "dark", "light", "black" })
            {
                Theme.Apply(theme, S.Accent);
                ShowLibrary();
                await Task.Delay(first ? 6000 : 1200);
                SaveSnapshot(Path.Combine(dir, "library-" + theme + ".png"));
                if (items.Count > 0 && Library.FirstItem != null)
                {
                    OpenItem(Library.FirstItem);
                    await Task.Delay(first ? 7000 : 2500);
                    SaveSnapshot(Path.Combine(dir, "editor-" + theme + ".png"));
                }
                ShowRecord();
                await Task.Delay(1500);
                SaveSnapshot(Path.Combine(dir, "record-" + theme + ".png"));
                NavSettings.IsChecked = true;
                await Task.Delay(800);
                SaveSnapshot(Path.Combine(dir, "settings-" + theme + ".png"));
                first = false;
            }
            File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.Now.ToString("s"));
            Close();
        }

        void SaveSnapshot(string path)
        {
            UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth), (int)Math.Ceiling(Root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Background, null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(path)) enc.Save(f);
        }
    }
}
