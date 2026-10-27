using System;
using System.Threading.Tasks;
using System.Windows;

namespace OLEDable
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // anything unexpected is logged (logs\errors.txt in the app's data folder); errors on the UI thread are
            // shown and the app keeps running instead of closing
            DispatcherUnhandledException += (s, a) =>
            {
                Tools.LogError("UI", a.Exception);
                MessageBox.Show(a.Exception.Message, "OLEDable", MessageBoxButton.OK, MessageBoxImage.Warning);
                a.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => { if (a.ExceptionObject is Exception ex) Tools.LogError("background", ex); };
            TaskScheduler.UnobservedTaskException += (s, a) => { Tools.LogError("task", a.Exception); a.SetObserved(); };

            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }
}
