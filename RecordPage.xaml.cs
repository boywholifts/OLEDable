using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static OLEDable.Tools;

namespace OLEDable
{
    /// <summary>Records one of the screens (the HydroShift or any monitor), then opens the clip in the editor.</summary>
    public partial class RecordPage : UserControl, IPage
    {
        readonly MainWindow shell;
        LiveFeed feed;
        bool shown, recording, counting, syncLen;
        StopSignal stop;
        CancellationTokenSource countCts;

        public RecordPage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();

            var S = shell.S;
            if (S.Seconds > LenSlider.Maximum) LenSlider.Maximum = S.Seconds;
            syncLen = true; LenSlider.Value = S.Seconds; syncLen = false;
            UpdateLen();
            if (S.Fps == 30) Fps30.IsChecked = true; else Fps60.IsChecked = true;
            CountSw.IsChecked = S.Countdown;

            LenSlider.ValueChanged += (o, e) =>
            {
                if (syncLen) return;
                S.Seconds = (int)Math.Max(5, Math.Round(LenSlider.Value / 5) * 5);
                UpdateLen();
            };
            LenSlider.PreviewMouseLeftButtonUp += (o, e) => shell.SaveSettings();
            Fps30.Checked += (o, e) => { S.Fps = 30; shell.SaveSettings(); };
            Fps60.Checked += (o, e) => { S.Fps = 60; shell.SaveSettings(); };
            CountSw.Click += (o, e) => { S.Countdown = CountSw.IsChecked == true; shell.SaveSettings(); };
            NameBox.TextChanged += (o, e) => NamePh.Visibility = NameBox.Text.Length > 0 ? Visibility.Hidden : Visibility.Visible;
            FindBtn.Click += async (o, e) => { LiveMsg.Text = "Looking for screens…"; await shell.ScanScreens(); };
            RecBtn.MouseLeftButtonUp += (o, e) => OnRecClick();
            PreviewFrame.SizeChanged += (o, e) => SizePreview();

            shell.DisplayChanged += () => { BuildScreenChips(); SizePreview(); UpdateDisplayState(); StopFeed(); StartFeed(); };
            shell.BusyChanged += UpdateDisplayState;
        }

        public async void OnShown()
        {
            shown = true;
            BuildScreenChips();
            UpdateDisplayState();
            if (shell.Screens == null) await shell.ScanScreens();   // the first time: find the screens
            else StartFeed();
        }

        /// <summary>One chip per screen found; the chosen one is the one recorded.</summary>
        void BuildScreenChips()
        {
            ScreenChips.Children.Clear();
            if (shell.Screens == null) return;
            foreach (var sc in shell.Screens)
            {
                var screen = sc;
                var chip = new RadioButton
                {
                    Content = sc.Label, GroupName = "screen", Margin = new Thickness(0, 0, 6, 6),
                    IsChecked = sc.Adapter == shell.S.Adapter && sc.Output == shell.S.Output,
                };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                chip.Checked += (o, e) => { if (!recording && !(screen.Adapter == shell.S.Adapter && screen.Output == shell.S.Output)) shell.ChooseScreen(screen); };
                ScreenChips.Children.Add(chip);
            }
        }

        /// <summary>The live view takes the chosen screen's shape.</summary>
        void SizePreview()
        {
            var sc = shell.CurrentScreen;
            double aspect = sc != null ? (double)sc.H / sc.W : 1048.0 / 2288;
            PreviewHost.Height = Math.Min(PreviewFrame.ActualWidth * aspect, 640);
            PreviewHost.Width = PreviewHost.Height / aspect;
        }

        public void OnHidden()
        {
            shown = false;
            StopFeed();
            if (countCts != null) countCts.Cancel();
        }

        void UpdateLen() { LenText.Text = shell.S.Seconds < 60 ? shell.S.Seconds + " s" : Fmt(shell.S.Seconds); }

        void UpdateDisplayState()
        {
            if (recording) return;
            bool ok = shell.DisplayFound;
            RecBtn.Opacity = ok && !shell.Busy ? 1 : 0.4;
            if (!ok)
            {
                LiveImg.Source = null;
                LiveMsg.Visibility = Visibility.Visible;
                LiveMsg.Text = shell.Ffmpeg == null ? "ffmpeg is not installed. Run  winget install Gyan.FFmpeg  and reopen the app."
                             : shell.Screens == null ? "Looking for screens…"
                             : "No screen could be captured. Click “Look for screens again”. (A Lian Li HydroShift shows up here when it's on as a secondary screen in L-Connect 3.)";
                LivePill.Visibility = Visibility.Collapsed;
                StatusText.Text = "";
            }
            else
            {
                LivePill.Visibility = Visibility.Visible;
                StatusText.Text = shell.Busy ? "Another job is running" : "Ready";
            }
        }

        /// <summary>Starts the smooth live view (kept running while recording, so you see what's captured).</summary>
        void StartFeed()
        {
            if (!shown || feed != null || !shell.DisplayFound || shell.Ffmpeg == null) return;
            var sc = shell.CurrentScreen;
            if (sc == null) return;
            double k = Math.Min(1144.0 / sc.W, 1144.0 / sc.H);
            var f = new LiveFeed(shell.Ffmpeg, sc.Adapter, sc.Output, (int)(sc.W * k), (int)(sc.H * k), Dispatcher);
            f.Started += () => { if (feed == f) { LiveImg.Source = f.Bitmap; LiveMsg.Visibility = Visibility.Collapsed; } };
            f.Failed += () =>
            {
                if (feed != f) return;
                feed = null;
                LiveImg.Source = null;
                LiveMsg.Visibility = Visibility.Visible;
                LiveMsg.Text = "Can't see this screen right now. If you changed displays, click “Look for screens again”.";
            };
            feed = f;
            f.Start();
        }

        void StopFeed()
        {
            if (feed == null) return;
            feed.Dispose();
            feed = null;
        }

        void SetRecLook(bool square)
        {
            RecInner.Width = RecInner.Height = square ? 28 : 60;
            RecInner.CornerRadius = new CornerRadius(square ? 7 : 30);
            RecBtn.ToolTip = square ? "Stop and keep what was recorded" : "Record";
        }

        async void OnRecClick()
        {
            if (counting) { if (countCts != null) countCts.Cancel(); return; }
            if (recording) { if (stop != null) stop.Requested = true; StatusText.Text = "Finishing up…"; return; }
            if (!shell.DisplayFound || shell.CurrentScreen == null) { var _ = shell.ScanScreens(); return; }
            if (shell.Busy) { shell.ShowToast("Wait for the running job to finish first."); return; }

            var S = shell.S;
            shell.SaveSettings();

            if (S.Countdown)
            {
                counting = true;
                countCts = new CancellationTokenSource();
                StatusText.Text = "Get ready…   click to cancel";
                try
                {
                    for (int i = 3; i > 0; i--) { CountText.Text = i.ToString(); await Task.Delay(1000, countCts.Token); }
                }
                catch (OperationCanceledException) { }
                CountText.Text = "";
                counting = false;
                if (countCts.IsCancellationRequested) { StatusText.Text = "Ready"; return; }
            }

            var screenUsed = shell.CurrentScreen;
            var id = SourceStore.NewId();
            var dest = shell.Store.RecordingPath(id);
            var title = string.IsNullOrWhiteSpace(NameBox.Text) ? "Recording " + DateTime.Now.ToString("MMM d, h.mm tt") : NameBox.Text.Trim();
            stop = new StopSignal();
            recording = true;
            SetRecLook(true);
            Prog.Visibility = Visibility.Visible;
            LiveDot.Fill = Theme.Brush("#FFFF453A");
            LiveText.Text = "0:00";
            int secs = S.Seconds, fps = S.Fps;
            var ui = new Progress<JobStatus>(st =>
            {
                StatusText.Text = st.Text + "   ·   click ■ to stop early";
                if (st.Fraction >= 0) { Prog.Value = st.Fraction; LiveText.Text = Fmt(st.Fraction * secs); }
            });
            bool ok = await shell.RunJob("Recording " + title, async (pr, ct) =>
            {
                var enc = await shell.P.RecordAsync(S.Adapter, S.Output, fps, secs, S.Encoder, dest, stop, pr, ct);
                if (enc != S.Encoder) { S.Encoder = enc; shell.SaveSettings(); }
            }, ui);
            recording = false;
            SetRecLook(false);
            Prog.Visibility = Visibility.Hidden;
            LiveDot.Fill = Theme.Brush("#FF22B07D");
            LiveText.Text = "Live";
            StatusText.Text = "Ready";
            UpdateDisplayState();
            if (!ok) { TryDelete(dest); return; }

            NameBox.Text = "";
            var session = new EditSession
            {
                Id = id, Title = title, SourcePath = dest, Kind = "recording",
                Oled = S.DefaultOled(), Loop = S.Loop, Smooth = S.Smooth,
                OutputName = shell.UniqueOutputName(title),
                // the HydroShift's own size stays the HydroShift output; any other screen keeps its size
                OutputMode = screenUsed != null && !screenUsed.IsHydroShift ? OutputSpec.Original : null,
            };
            session.Oled.Black = -1;   // detect
            shell.Store.Save(session);
            shell.OpenEditor(session);
        }
    }
}
