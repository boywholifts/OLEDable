using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OLEDable
{
    /// <summary>Sends a video to the HydroShift II screen through RGBable, which streams it to the cooler.</summary>
    public static class HydroShift
    {
        public const string Download = "https://github.com/boywholifts/rgbable/releases/latest";

        public static string FindRgbable()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, "RGBable", "rgbable.exe"),
                Path.Combine(local, "Programs", "RGBable", "rgbable.exe"),
                FromUninstallKey(),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            return null;
        }

        static string FromUninstallKey()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\RGBable"))
                {
                    if (k == null) return null;
                    var dir = k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir.Trim('"'), "rgbable.exe");
                    var icon = k.GetValue("DisplayIcon") as string;
                    return string.IsNullOrEmpty(icon) ? null : icon.Split(',')[0].Trim('"');
                }
            }
            catch { return null; }
        }

        public static void Play(MainWindow shell, string file)
        {
            var exe = FindRgbable();
            if (exe == null)
            {
                if (Dialog.Ask(shell, "Play on HydroShift",
                               "Playing on the HydroShift II screen needs RGBable, the free app that runs the cooler's screen and lighting. Download it?",
                               "Download"))
                    MainWindow.OpenInShell(Download);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--oled \"" + file + "\"") { UseShellExecute = false });
                shell.ShowToast("Sent to the HydroShift screen: " + Path.GetFileName(file));
            }
            catch (Exception ex) { shell.ShowToast("Couldn't start RGBable: " + ex.Message); }
        }
    }
}
