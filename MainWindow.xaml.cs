using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static OLEDable.Tools;

namespace OLEDable
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
        /// <summary>Exports waiting their turn (see <see cref="RunQueue"/>).</summary>
        public readonly ExportQueue Queue;
        /// <summary>The queued export running now, or null.</summary>
        public QueuedExport CurrentExport { get; private set; }
        bool queueRunning, closing, cancelCurrent, quietCancel;
        public bool DisplayFound { get { return S.Adapter >= 0; } }
        public event Action DisplayChanged;

        LibraryPage library;
        EditorPage editor;
        RecordPage record;
        SettingsPage settings;
        IPage current;
        CancellationTokenSource jobCts;
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };

        public MainWindow()
        {
            DataDir = Tools.DataDir();
            Queue = new ExportQueue(DataDir);
            SettingsPath = Path.Combine(DataDir, "OLEDable.json");
            // renamed from HydroShift Recorder: carry the old settings over the first time
            var oldSettings = Path.Combine(DataDir, "HydroShift-Recorder.json");
            if (!File.Exists(SettingsPath) && File.Exists(oldSettings)) { try { File.Copy(oldSettings, SettingsPath); } catch { } }
            CacheDir = Path.Combine(Path.GetTempPath(), "OLEDable", "thumbs");
            S = Settings.Load(SettingsPath);
            // tools chosen in the installer win; otherwise look on PATH
            Ffmpeg = !string.IsNullOrWhiteSpace(S.FfmpegPath) && File.Exists(S.FfmpegPath) ? S.FfmpegPath : Find("ffmpeg");
            var probeNext = Ffmpeg == null ? null : Path.Combine(Path.GetDirectoryName(Ffmpeg), "ffprobe.exe");
            Ffprobe = probeNext != null && File.Exists(probeNext) ? probeNext : Find("ffprobe");
            P = new Pipeline(Ffmpeg, Ffprobe,
                             string.IsNullOrWhiteSpace(S.RifeDir) ? Path.Combine(DataDir, "rife") : S.RifeDir,
                             string.IsNullOrWhiteSpace(S.EsrganDir) ? Path.Combine(DataDir, "realesrgan") : S.EsrganDir);
            P.Speeds = new SpeedBook(S.Rates);   // learns as exports run; saved with the settings

            Theme.Apply(Theme.Effective(S), S.Accent);
            InitializeComponent();
            TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();   // job progress on the taskbar button

            NavLibrary.Checked += (s, e) => ShowPage(Library);
            NavSettings.Checked += (s, e) => ShowPage(SettingsPg);
            NavEditing.Checked += (s, e) => ShowPage(Editor);
            NavWe.Checked += (s, e) => ShowPage(WePage);
            EditCloseBtn.Click += (s, e) => { e.Handled = true; CloseEditor(); };
            BtnMin.Click += (s, e) => WindowState = WindowState.Minimized;
            BtnMax.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            BtnClose.Click += (s, e) => Close();
            StateChanged += (s, e) => FitMaximized();
            JobCancel.Click += (s, e) => { if (CurrentExport != null) cancelCurrent = true; if (jobCts != null) jobCts.Cancel(); };
            QueueStart.Click += (s, e) => { Queue.Requeue(); RunQueue(); };
            QueueClear.Click += (s, e) =>
            {
                if (Dialog.Ask(this, "Clear the queue", "Remove the " + QueuedCount() + " waiting export" + (QueuedCount() == 1 ? "" : "s") + "? Their settings stay saved, so you can export them again from the editor.", "Clear"))
                {
                    foreach (var q in Queue.Items.Where(i => i != CurrentExport).ToList()) Queue.Remove(q);
                }
            };
            Queue.Changed += UpdateQueueCard;
            UpdateNow.Click += (s, e) => InstallUpdate();
            UpdateNotes.Click += (s, e) => { if (update != null) OpenInShell(update.Page); };
            DoneCard.Click += (s, e) =>
            {
                if (donePath == null) return;
                if (File.Exists(donePath)) OpenInShell(donePath, true);
                else ShowToast("That file has been moved or deleted.");
            };
            DoneDismiss.Click += (s, e) => { e.Handled = true; DoneCard.Visibility = Visibility.Collapsed; };
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
                // closing in the middle of an export, recording or download stops it: ask first
                if (Busy)
                {
                    string msg;
                    if (CurrentExport != null)
                    {
                        int more = QueuedCount();
                        msg = "“" + CurrentExport.Title + "” is still exporting" + (more > 0 ? ", with " + more + " more in the queue" : "") +
                              ". If you close OLEDable it stops, and the queue " +
                              (S.AutoResumeQueue ? "starts again the next time you open it." : "waits until you press Start.");
                    }
                    else msg = JobTitle.Text + " isn't finished. Stop it and close OLEDable?";
                    if (!Dialog.Ask(this, "Something is still running", msg, CurrentExport != null ? "Close" : "Stop and close", "Keep running"))
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                closing = true;
                if (jobCts != null) jobCts.Cancel();
                if (record != null) record.OnHidden();
                if (editor != null) editor.FlushAutosave();
                SaveSettings();
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
            };

            // Ctrl+Shift+F12: a PNG of the window, as drawn (for the README and bug reports)
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F12 &&
                    System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
                {
                    e.Handled = true;
                    SaveWindowPicture();
                }
                // Ctrl+Shift+F11: a standard 1600 × 1000 window at the top-left of the main screen, so README
                // pictures all come out the same size
                else if (e.Key == System.Windows.Input.Key.F11 &&
                         System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
                {
                    e.Handled = true;
                    WindowState = WindowState.Normal;
                    Width = 1600; Height = 1000;
                    Left = SystemParameters.WorkArea.Left + 16; Top = SystemParameters.WorkArea.Top + 16;
                }
            };

            Loaded += (s, e) =>
            {
                NavLibrary.IsChecked = true;
                // updates: in the background, at most every 3 hours; local builds (dist folder) never prompt on their own
                if (S.AutoUpdateCheck && !Updater.IsLocalBuild && (DateTime.Now - S.LastUpdateCheck).TotalHours >= 3)
                    _ = CheckForUpdate(false);
                if (Ffmpeg == null)
                {
                    SetDisplay(false, "ffmpeg is not installed", "Run  winget install Gyan.FFmpeg  then reopen the app.");
                    DisplayCard.Visibility = Visibility.Visible;
                    return;
                }
                // exports left from last time: start them again, or keep them until "Start"
                if (Queue.Count > 0)
                {
                    if (S.AutoResumeQueue) { Queue.Requeue(); RunQueue(); }
                    else
                    {
                        foreach (var q in Queue.Items.Where(i => i.State == QueuedExport.Waiting).ToList()) Queue.Set(q, QueuedExport.Interrupted);
                        UpdateQueueCard();
                    }
                }
            };
        }

        // ---------------------------------------------------------------- pages

        LibraryPage Library { get { return library ?? (library = new LibraryPage(this)); } }
        RecordPage Record { get { return record ?? (record = new RecordPage(this)); } }
        WallpaperEnginePage wePage;
        WallpaperEnginePage WePage { get { return wePage ?? (wePage = new WallpaperEnginePage(this)); } }
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
        /// <summary>The screen recorder: no sidebar item of its own (it's opened from the Library).</summary>
        public void ShowRecord()
        {
            NavLibrary.IsChecked = NavEditing.IsChecked = NavWe.IsChecked = NavSettings.IsChecked = false;
            ShowPage(Record);
        }

        public void ShowWallpaperEngine() { if (NavWe.IsChecked == true) ShowPage(WePage); else NavWe.IsChecked = true; }

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
            if (s == null && it.Id == null)
            {
                // exported images can't carry the app's tag: find their session by file name
                s = Store.FindByOutput(Path.GetFileName(it.Path));
                if (s != null && (s.SourcePath == null || !File.Exists(s.SourcePath))) s = null;
                if (s != null) s.ExistingPath = it.Path;
            }
            if (s == null)
            {
                bool image = Tools.IsImage(it.Path);
                s = new EditSession
                {
                    Id = SourceStore.NewId(), Title = it.Name, SourcePath = it.Path, Kind = image ? "image" : "video",
                    Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                    OutputName = UniqueOutputName(it.Name + " (OLED)", image ? ".png" : ".mp4"),
                    OutputMode = OutputSpec.Original,   // new videos and images keep their size; HydroShift is one click away
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
                    ShowToast("“" + title + "” is a " + (type.Length > 0 ? type : "non-video") + " wallpaper: record it from the Wallpaper Engine page.");
                    ShowWallpaperEngine();
                    return;
                }
                path = Path.Combine(Path.GetDirectoryName(pj), proj.File ?? "");
            }
            bool image = Tools.IsImage(path);
            if (!File.Exists(path) || !LibraryScanner.IsVideo(path) && !image && Path.GetExtension(path).ToLowerInvariant() != ".gif")
            {
                ShowToast("That isn't a video or image the app can open.");
                return;
            }
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
            var s = new EditSession
            {
                Id = SourceStore.NewId(), Title = title, SourcePath = path, Kind = image ? "image" : "video",
                Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                OutputName = UniqueOutputName(title, image ? ".png" : ".mp4"),
                OutputMode = OutputSpec.Original,
            };
            s.Oled.Black = -1;
            OpenEditor(s);
        }

        public void PickAndImport()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a video or image",
                Filter = "Videos and images|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|" +
                         "Videos|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.gif|Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|" +
                         "Wallpaper Engine project|project.json|All files|*.*",
            };
            var we = FindWorkshopDir();
            if (we != null) d.InitialDirectory = we;
            if (d.ShowDialog(this) == true) Import(d.FileName);
        }

        public string UniqueOutputName(string title, string ext = ".mp4")
        {
            var n = SafeName(title);
            if (n.Length == 0) n = "OLEDable " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");
            var name = n + ext;
            for (int i = 2; File.Exists(Path.Combine(S.ConvertedPath, name)) || File.Exists(Path.Combine(S.OutDir, name)) ||
                            Queue.Claims(Path.Combine(S.ConvertedPath, name)); i++)
                name = n + " (" + i + ")" + ext;
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

        // ---------------------------------------------------------------- live resizing

        /// <summary>True while the window edge is being dragged (Windows' size/move loop).</summary>
        public bool LiveResizing { get; private set; }
        /// <summary>The drag ended: pages lay themselves out properly again.</summary>
        public event Action LiveResizeEnded;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (src != null) src.AddHook(SizeMoveHook);
        }

        IntPtr SizeMoveHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
            if (msg == WM_ENTERSIZEMOVE) LiveResizing = true;
            else if (msg == WM_EXITSIZEMOVE)
            {
                LiveResizing = false;
                LiveResizeEnded?.Invoke();
            }
            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------- jobs

        /// <summary>
        /// Runs one background job at a time with progress in the sidebar. Returns true on success;
        /// failures are shown to the user, cancelling is silent.
        /// </summary>
        public async Task<bool> RunJob(string title, Func<IProgress<JobStatus>, CancellationToken, Task> work, IProgress<JobStatus> also = null,
                                       Action<string> onError = null)
        {
            if (Busy) { ShowToast("Something is already running. Wait for it to finish or cancel it."); return false; }
            Busy = true; BusyChanged?.Invoke();
            jobCts = new CancellationTokenSource();
            DoneCard.Visibility = Visibility.Collapsed;   // a new job replaces the last "finished" note
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
            catch (OperationCanceledException) { if (!quietCancel) ShowToast("Cancelled"); quietCancel = false; return false; }
            catch (Exception ex)
            {
                if (jobCts.IsCancellationRequested) { if (!quietCancel) ShowToast("Cancelled"); quietCancel = false; return false; }
                if (onError != null) onError(ex.Message);   // the queue keeps going and shows it on the item instead
                else Dialog.Tell(this, "Something went wrong", ex.Message);
                return false;
            }
            finally
            {
                jobCts.Dispose(); jobCts = null;
                JobCard.Visibility = Visibility.Collapsed;
                TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                Busy = false; BusyChanged?.Invoke();
                // exports queued while something else ran (a recording, a download) start now
                if (!queueRunning && !closing && Queue.Next != null) Dispatcher.BeginInvoke(new Action(RunQueue));
            }
        }

        /// <summary>
        /// A short job that's light on the PC (copying a wallpaper into the library, pulling an image or video out
        /// of a scene, downloading a model). When nothing is running it shows in the sidebar like any job; during an
        /// export it runs alongside it instead of being turned away, so the next video can be opened and edited
        /// while the export works.
        /// </summary>
        public async Task<bool> RunQuick(string title, Func<IProgress<JobStatus>, CancellationToken, Task> work)
        {
            if (!Busy) return await RunJob(title, work);
            ShowToast(title + "…");
            try
            {
                using (var cts = new CancellationTokenSource())
                    await work(new Progress<JobStatus>(_ => { }), cts.Token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Dialog.Tell(this, "Something went wrong", ex.Message);
                return false;
            }
        }

        string donePath;

        /// <summary>The sidebar note that an export finished; it stays until dismissed or the next job starts.</summary>
        void ShowDone(string path)
        {
            donePath = path;
            DoneName.Text = Path.GetFileName(path);
            DoneName.ToolTip = path;
            DoneText.Text = "In " + Path.GetFileName(Path.GetDirectoryName(path)) + "  ·  " + DateTime.Now.ToString("h:mm tt");
            DoneCard.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Exports the session into the library folder, keeping its settings for later re-editing. It goes into
        /// the queue: straight away when nothing else is running, otherwise when its turn comes. Returns true once
        /// it's queued.
        /// </summary>
        public Task<bool> Export(EditSession s, bool restart = false)
        {
            if (string.IsNullOrEmpty(s.OutputName)) s.OutputName = UniqueOutputName(s.Title, s.IsImage ? ".png" : ".mp4");
            var outPath = OutputPathFor(s);
            var snap = s.Clone();
            Store.Save(snap);
            S.Loop = snap.Loop;   // (the look isn't remembered: every video starts untouched, and Auto adjust is per video) S.Smooth = snap.Smooth;
            S.RememberEnhance(snap);
            SaveSettings();
            bool wait = Busy || queueRunning;
            Queue.Add(QueuedExport.For(snap, outPath), CurrentExport, restart);
            if (restart && CurrentExport != null && CurrentExport.Session.Id == snap.Id)
            {
                // stop the export with the old settings; the new one is next in line, so it starts straight away
                quietCancel = true; cancelCurrent = true;
                if (jobCts != null) jobCts.Cancel();
                ShowToast("Starting again with your changes");
            }
            else if (wait) ShowToast("Added to the queue: " + (snap.Title ?? snap.OutputName));
            RunQueue();
            return Task.FromResult(true);
        }

        int QueuedCount() { return Queue.Items.Count(i => i != CurrentExport); }

        /// <summary>Turns the Wallpaper Engine step on or off for this video's running or queued exports. True if there were any.</summary>
        public bool SetWallpaperEngineStep(string sessionId, bool on)
        {
            bool any = false;
            foreach (var q in Queue.Items.Where(i => i.Session.Id == sessionId))
            {
                q.Session.AddToWallpaperEngine = on;   // the running export reads it once its video is done
                any = true;
            }
            if (any) Queue.Save();
            return any;
        }

        /// <summary>
        /// Puts a video that's already in the library into Wallpaper Engine (with its original's sound, for an
        /// OLEDable export). Seconds, not an export: the picture is copied as it is.
        /// </summary>
        public async void AddToWallpaperEngine(LibraryItem it)
        {
            if (it == null || it.IsImage) return;
            var session = it.Id != null ? Store.Load(it.Id) : null;
            if (session == null)
            {
                // any other video: it goes in as it is (keeping its own sound); the id keeps re-adds in one folder
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    session = new EditSession
                    {
                        Id = "file-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(it.Path.ToLowerInvariant())), 0, 8).Replace("-", ""),
                        Title = it.Name,
                    };
            }
            else if (session.Title == null) session.Title = it.Name;
            string folder = null;
            // the trim is measured on the original, so its length is the one that counts
            var info = session.SourcePath != null && File.Exists(session.SourcePath) ? await ProbeAsync(Ffprobe, session.SourcePath) : it.Info;
            if (info != null) { session.Duration = info.Duration; session.Fps = info.Fps; session.Width = info.Width; session.Height = info.Height; }
            bool ok = await RunQuick("Adding " + it.Name + " to Wallpaper Engine", (pr, ct) => Task.Run(() =>
            {
                pr.Report(new JobStatus("Adding it to Wallpaper Engine…", -1));
                folder = Workshop.AddToWallpaperEngine(session, it.Path, P);
            }, ct));
            if (!ok) return;
            if (it.Id != null && session.SourcePath != null) { session.WeFolder = folder; session.AddToWallpaperEngine = true; Store.Save(session); }
            ShowToast("Added to Wallpaper Engine under My projects (press its refresh button if it isn't listed yet)");
        }

        /// <summary>
        /// Runs the queued exports one after another. One at a time on purpose: an export already keeps the CPU
        /// and GPU busy, so two together would each take twice as long and need twice the temporary space.
        /// A failed export is marked and skipped; cancelling one removes it; closing the app leaves the rest.
        /// </summary>
        async void RunQueue()
        {
            if (queueRunning || Busy) return;
            queueRunning = true;
            try
            {
                QueuedExport q;
                while (!closing && (q = Queue.Next) != null)
                {
                    CurrentExport = q;
                    cancelCurrent = false;
                    UpdateQueueCard();
                    var snap = q.Ready();
                    string error = null, weFolder = null;
                    bool ok = await RunJob("Exporting " + q.Title, async (pr, ct) =>
                    {
                        FfJob.LowPriority.Value = true;
                        await P.ExportAsync(snap, q.OutPath, pr, ct);
                        if (snap.AddToWallpaperEngine && !snap.IsImage)
                        {
                            pr.Report(new JobStatus("Adding it to Wallpaper Engine…", -1));
                            weFolder = await Task.Run(() => Workshop.AddToWallpaperEngine(snap, q.OutPath, P), ct);
                        }
                    }, null, msg => error = msg);
                    SaveSettings();   // keeps the step speeds it measured, for better time estimates next time
                    if (ok)
                    {
                        Queue.Remove(q);
                        if (!S.KeepSources && snap.Kind == "recording") Store.Forget(snap.Id);   // the export is what gets edited next time
                        if (weFolder != null)
                        {
                            snap.WeFolder = weFolder;
                            Store.Save(snap);
                            ShowToast("Added to Wallpaper Engine under My projects (press its refresh button if it isn't listed yet)");
                        }
                        if (editor != null) editor.Exported(snap.Id, q.OutPath, weFolder);
                        ShowDone(q.OutPath);
                        if (library != null) library.Refresh();
                    }
                    else if (closing) Queue.Set(q, QueuedExport.Interrupted);
                    else if (error != null)
                    {
                        Queue.Set(q, QueuedExport.Failed, error);
                        ShowToast("Couldn't export " + q.Title + ". It's marked in the queue.");
                    }
                    else Queue.Remove(q);   // cancelled
                }
            }
            finally
            {
                CurrentExport = null;
                queueRunning = false;
                UpdateQueueCard();
                BusyChanged?.Invoke();
            }
        }

        /// <summary>The sidebar list of exports waiting (with Start and Clear when the queue isn't running).</summary>
        void UpdateQueueCard()
        {
            var waiting = Queue.Items.Where(i => i != CurrentExport).ToList();
            QueueCard.Visibility = waiting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueList.Children.Clear();
            if (waiting.Count == 0) return;
            bool running = CurrentExport != null;
            int stopped = waiting.Count(i => i.State == QueuedExport.Interrupted), failed = waiting.Count(i => i.State == QueuedExport.Failed);
            QueueTitle.Text = running ? "Up next" : "Export queue";
            QueueText.Text = running ? waiting.Count + " waiting"
                           : stopped > 0 ? waiting.Count + " waiting  ·  stopped when OLEDable closed"
                           : failed > 0 ? failed + " couldn't be exported"
                           : waiting.Count + " waiting";
            QueueStart.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            QueueStart.Content = failed > 0 && stopped == 0 ? "Try again" : "Start";
            foreach (var q in waiting.Take(6))
            {
                var item = q;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var x = new Button { Width = 20, Height = 20, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false, ToolTip = "Remove from the queue",
                                     Template = (ControlTemplate)QueueCard.FindResource("QueueRemoveBtn") };
                x.SetResourceReference(ForegroundProperty, "Sub");
                x.Click += (o, e) => Queue.Remove(item);
                DockPanel.SetDock(x, Dock.Right);
                row.Children.Add(x);
                var name = new TextBlock { Text = item.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                                           ToolTip = item.State == QueuedExport.Failed ? "Couldn't export: " + item.Error : item.OutPath };
                if (item.State == QueuedExport.Failed) name.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
                row.Children.Add(name);
                QueueList.Children.Add(row);
            }
            if (waiting.Count > 6)
            {
                var more = new TextBlock { Text = "and " + (waiting.Count - 6) + " more", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) };
                more.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
                QueueList.Children.Add(more);
            }
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

        void SetDisplay(bool ok, string text, string sub)
        {
            DisplayDot.Fill = Theme.Brush(ok ? "#FF22B07D" : "#FF8E8E93");
            DisplayText.Text = text;
            DisplaySub.Text = sub;
        }

        /// <summary>A screen the recorder can capture (Desktop Duplication adapter/output and its size).</summary>
        public class ScreenInfo
        {
            public int Adapter, Output, W, H;
            public bool IsHydroShift { get { return (W == 2288 && H == 1048) || (W == 1048 && H == 2288); } }
            public string Label { get { return (IsHydroShift ? "HydroShift  ·  " : "Screen " + (Output + 1) + (Adapter > 0 ? " (GPU " + (Adapter + 1) + ")" : "") + "  ·  ") + W + " × " + H; } }
        }

        /// <summary>Screens found by the last <see cref="ScanScreens"/>; null until then.</summary>
        public List<ScreenInfo> Screens { get; private set; }
        bool scanning;

        /// <summary>
        /// Finds every screen that can be recorded, by grabbing one frame from each graphics adapter's outputs
        /// (in the background; takes a few seconds). Keeps the chosen one if it's still there.
        /// </summary>
        public async Task ScanScreens()
        {
            if (scanning || Ffmpeg == null) return;
            scanning = true;
            var ff = Ffmpeg;
            var found = await Task.Run(() =>
            {
                var list = new List<ScreenInfo>();
                var png = Path.Combine(Path.GetTempPath(), "oledable_probe.png");
                for (int a = 0; a <= 3; a++)
                {
                    int misses = 0;
                    for (int o = 0; o <= 5; o++)
                    {
                        TryDelete(png);
                        var j = new FfJob(ff, FfArgs.Grab(a, o, png));
                        if (!j.Wait(10000)) j.Kill();
                        j.Drain();
                        if (!File.Exists(png))
                        {
                            // one output that can't be captured (e.g. HDR) mustn't hide the next ones; two in a row = no more
                            if (++misses >= 2) break;
                            continue;
                        }
                        misses = 0;
                        try
                        {
                            var f = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                            list.Add(new ScreenInfo { Adapter = a, Output = o, W = f.PixelWidth, H = f.PixelHeight });
                        }
                        catch { }
                    }
                }
                TryDelete(png);
                return list;
            });
            scanning = false;
            Screens = found;
            if (!found.Any(sc => sc.Adapter == S.Adapter && sc.Output == S.Output))
            {
                // default: the HydroShift if it's on, otherwise the first screen
                var pick = found.FirstOrDefault(sc => sc.IsHydroShift) ?? found.FirstOrDefault();
                S.Adapter = pick != null ? pick.Adapter : -1;
                S.Output = pick != null ? pick.Output : -1;
                SaveSettings();
            }
            DisplayChanged?.Invoke();
        }

        /// <summary>The screen the recorder captures.</summary>
        public ScreenInfo CurrentScreen
        {
            get { return Screens == null ? null : Screens.FirstOrDefault(sc => sc.Adapter == S.Adapter && sc.Output == S.Output); }
        }

        public void ChooseScreen(ScreenInfo sc)
        {
            S.Adapter = sc.Adapter; S.Output = sc.Output;
            SaveSettings();
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

        /// <summary>Saves the window as a PNG in the data folder's "screenshots" folder, at the screen's real resolution.</summary>
        void SaveWindowPicture()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var el = (FrameworkElement)Content;
                int w = (int)Math.Round(el.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(el.ActualHeight * dpi.DpiScaleY);
                var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rtb.Render(el);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                var dir = Path.Combine(DataDir, "screenshots");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "OLEDable " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", Inv) + ".png");
                using (var f = File.Create(file)) enc.Save(f);
                ShowToast("Saved a picture of the window to " + dir);
            }
            catch (Exception ex) { ShowToast("Couldn't save the picture: " + ex.Message); }
        }

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
    }
}
