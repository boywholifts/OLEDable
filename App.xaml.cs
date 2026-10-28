using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OLEDable
{
    public partial class App : Application
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        static Mutex single;
        static EventWaitHandle showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // one OLEDable at a time: two copies would each save their own settings over the other's. Opening it
            // again brings the running window to the front instead.
            bool first;
            single = new Mutex(true, @"Local\OLEDable.Running", out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OLEDable.Show");
            if (!first)
            {
                // this copy is the one the user just opened, so Windows lets it hand the focus to the running one
                AllowSetForegroundWindow(-1);
                showSignal.Set();
                Shutdown();
                return;
            }

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

            new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                        w.Topmost = true; w.Topmost = false;   // over other apps, without staying on top
                    }));
                }
            }) { IsBackground = true, Name = "OLEDable second launch" }.Start();
        }
    }
}
