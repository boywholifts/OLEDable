using System.Windows;

namespace HydroShiftRecorder
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Show unexpected errors instead of silently closing the app
            DispatcherUnhandledException += (s, a) =>
            {
                MessageBox.Show(a.Exception.Message, "HydroShift Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
                a.Handled = true;
            };
            base.OnStartup(e);
        }
    }
}
