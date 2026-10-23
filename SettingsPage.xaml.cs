using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HydroShiftRecorder
{
    /// <summary>Appearance (theme, follow Windows, accent), library folder, and about.</summary>
    public partial class SettingsPage : UserControl, IPage
    {
        readonly MainWindow shell;

        public SettingsPage(MainWindow shell)
        {
            this.shell = shell;
            InitializeComponent();

            foreach (var card in new[] { CardLight, CardDark, CardBlack })
            {
                var c = card;
                c.Click += (o, e) =>
                {
                    shell.S.Theme = (string)c.Tag;
                    shell.S.FollowWindows = false;
                    shell.ApplyTheme();
                    Sync();
                };
            }
            FollowSw.Click += (o, e) => { shell.S.FollowWindows = FollowSw.IsChecked == true; shell.ApplyTheme(); Sync(); };
            foreach (var hex in Theme.AccentChoices)
            {
                var h = hex;
                var b = new Button { Style = (Style)FindResource("Swatch"), Background = Theme.Brush(h), ToolTip = h, Tag = h };
                b.Click += (o, e) => { shell.S.Accent = h; shell.ApplyTheme(); Sync(); };
                Swatches.Children.Add(b);
            }
            FolderBtn.Click += (o, e) =>
            {
                var picked = FolderPicker.Pick(shell, "Choose the folder for your HydroShift videos", shell.S.OutDir);
                if (picked == null) return;
                shell.S.OutDir = picked;
                shell.SaveSettings();
                Sync();
            };
            ConvBtn.Click += (o, e) =>
            {
                var start = Directory.Exists(shell.S.ConvertedPath) ? shell.S.ConvertedPath : shell.S.OutDir;
                var picked = FolderPicker.Pick(shell, "Choose where converted videos are saved", start);
                if (picked == null) return;
                // the default (inside the library) is kept as "default", so it follows the library if that moves
                shell.S.ConvertedDir = string.Equals(picked.TrimEnd('\\'), Path.Combine(shell.S.OutDir, "Converted"), StringComparison.OrdinalIgnoreCase) ? null : picked;
                shell.SaveSettings();
                Sync();
            };
            ChStable.Checked += (o, e) => SetChannel(Updater.Stable);
            ChDev.Checked += (o, e) => SetChannel(Updater.Dev);
            AutoUpdSw.Click += (o, e) => { shell.S.AutoUpdateCheck = AutoUpdSw.IsChecked == true; shell.SaveSettings(); };
            CheckUpdBtn.Click += async (o, e) =>
            {
                CheckUpdBtn.IsEnabled = false;
                UpdStatus.Text = "Checking…";
                UpdStatus.Text = await shell.CheckForUpdate(true);
                CheckUpdBtn.IsEnabled = true;
            };
            KeepSw.Click += (o, e) => { shell.S.KeepSources = KeepSw.IsChecked == true; shell.SaveSettings(); };
            DataBtn.Click += (o, e) => MainWindow.OpenInShell(shell.DataDir);
            Theme.Changed += Sync;
        }

        bool syncing;

        void SetChannel(string ch)
        {
            if (syncing || shell.S.UpdateChannel == ch) return;
            shell.S.UpdateChannel = ch;
            shell.S.SkippedVersion = null;
            shell.SaveSettings();
            UpdStatus.Text = ch == Updater.Dev ? "Dev channel: you'll get every new build. Click Check now to look." : "Stable channel: tested releases only.";
        }

        public void OnShown() { Sync(); }
        public void OnHidden() { }

        void Sync()
        {
            var S = shell.S;
            string cur = Theme.Effective(S);
            var accent = (Brush)FindResource("Accent");
            var line = (Brush)FindResource("Line");
            var sub = (Brush)FindResource("Sub");
            foreach (var pair in new[] { Tuple.Create(CardLight, DotLight), Tuple.Create(CardDark, DotDark), Tuple.Create(CardBlack, DotBlack) })
            {
                bool on = (string)pair.Item1.Tag == cur;
                pair.Item1.BorderBrush = on ? accent : line;
                pair.Item2.Stroke = on ? accent : sub;
                pair.Item2.Fill = on ? accent : Brushes.Transparent;
            }
            FollowSw.IsChecked = S.FollowWindows;
            foreach (Button b in Swatches.Children)
                b.BorderBrush = string.Equals((string)b.Tag, S.Accent, StringComparison.OrdinalIgnoreCase) ? (Brush)FindResource("Text") : Brushes.Transparent;
            FolderText.Text = S.OutDir;
            ConvText.Text = S.ConvertedPath;
            syncing = true;
            ChStable.IsChecked = S.UpdateChannel != Updater.Dev;
            ChDev.IsChecked = S.UpdateChannel == Updater.Dev;
            syncing = false;
            AutoUpdSw.IsChecked = S.AutoUpdateCheck;
            VersionText.Text = "Version " + Updater.CurrentText + (Updater.IsLocalBuild ? " (local build)" : "");
            if (string.IsNullOrEmpty(UpdStatus.Text))
                UpdStatus.Text = S.LastUpdateCheck > DateTime.MinValue ? "Last checked " + S.LastUpdateCheck.ToString("MMM d, h:mm tt") : "Not checked yet";
            KeepSw.IsChecked = S.KeepSources;

            AboutText.Text = "HydroShift Recorder " + Updater.CurrentText +
                             "\nffmpeg: " + (shell.Ffmpeg ?? "not found") +
                             "\nRIFE smoothing: " + (shell.P.HasRife ? "installed" : "downloads on first use") +
                             "\nAI enhance (Real-ESRGAN): " + (shell.P.HasEsrgan ? "installed" : "downloads on first use") +
                             "\nSettings: " + shell.SettingsPath;
        }
    }
}
