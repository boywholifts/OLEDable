using System;
using System.IO;
using System.Windows;

namespace HydroShiftRecorder
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // hidden QA mode: render every page in every theme to PNGs, then exit.
            //   HydroShiftRecorder.exe --snapshot <folder>
            string snapshotDir = null;
            if (e.Args.Length >= 2 && e.Args[0] == "--snapshot") snapshotDir = e.Args[1];

            DispatcherUnhandledException += (s, a) =>
            {
                if (snapshotDir != null)
                {
                    try { File.AppendAllText(Path.Combine(snapshotDir, "errors.txt"), a.Exception + Environment.NewLine); } catch { }
                }
                else MessageBox.Show(a.Exception.Message, "HydroShift Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
                a.Handled = true;
            };

            var w = new MainWindow(snapshotDir);
            MainWindow = w;
            w.Show();
        }
    }
}
