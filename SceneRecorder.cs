using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>
    /// Records a Wallpaper Engine scene (or web wallpaper) as a video without touching the desktop or the
    /// HydroShift: Wallpaper Engine plays it in a borderless pop-out window at the chosen size (its official
    /// command line), the app captures that window, then closes it again.
    /// </summary>
    public static class SceneRecorder
    {
        public const string WindowName = "HydroShift Recorder capture";

        /// <summary>Sizes offered for a recording.</summary>
        public static readonly Tuple<string, int, int>[] Sizes =
        {
            Tuple.Create("HydroShift (2288 × 1048)", 2288, 1048),
            Tuple.Create("1080p (1920 × 1080)", 1920, 1080),
            Tuple.Create("1440p (2560 × 1440)", 2560, 1440),
            Tuple.Create("4K (3840 × 2160)", 3840, 2160),
            Tuple.Create("Fold 8 Ultra inner (2256 × 2504)", 2256, 2504),
            Tuple.Create("Phone (1080 × 2400)", 1080, 2400),
        };

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        /// <summary>The main screen's size in real pixels (the app is DPI-aware); it always starts at 0, 0.</summary>
        static int[] PrimarySize() { return new[] { GetSystemMetrics(0), GetSystemMetrics(1) }; }

        static Task<int> Control(string we, string args, CancellationToken ct)
        {
            var job = new FfJob(we, "-control " + args);
            return job.WaitAsync(ct);
        }

        /// <summary>
        /// Plays <paramref name="it"/> in a pop-out and records it to <paramref name="dest"/> (w × h, the length and
        /// frame rate from the Record page). Returns the encoder that worked.
        /// </summary>
        public static async Task<string> RecordAsync(Pipeline p, Settings S, WeItem it, int w, int h, string dest,
                                                     StopSignal stop, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var we = Workshop.FindWallpaperEngine();
            if (we == null) throw new Exception("Wallpaper Engine wasn't found in your Steam libraries.");

            // its command line only talks to a running Wallpaper Engine
            if (!Process.GetProcessesByName(Path.GetFileNameWithoutExtension(we)).Any())
            {
                pr.Report(new JobStatus("Starting Wallpaper Engine…", -1));
                Process.Start(new ProcessStartInfo(we) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(we) });
                await Task.Delay(8000, ct);
            }

            // the pop-out sits at the top-left of the main screen while recording (Windows' window capture
            // records it even if other windows cover it)
            var screen = PrimarySize();
            pr.Report(new JobStatus("Opening “" + it.Name + "” in Wallpaper Engine…", -1));
            await Control(we, "openWallpaper -file " + Q(Path.Combine(it.Folder, "project.json")) +
                              " -playInWindow " + Q(WindowName) + " -width " + w + " -height " + h +
                              " -x 0 -y 0 -borderless", ct);
            try
            {
                // give the scene time to load and settle (particles, fades)
                for (int i = 5; i > 0; i--)
                {
                    pr.Report(new JobStatus("Letting the scene start…  " + i, -1));
                    await Task.Delay(1000, ct);
                }

                int secs = S.Seconds, fps = S.Fps;
                string title = "^" + System.Text.RegularExpressions.Regex.Escape(WindowName) + "$";
                try
                {
                    return await p.CaptureAsync(nvenc => FfArgs.RecordWindow(title, w, h, fps, secs, nvenc, dest), secs, S.Encoder, dest, stop, pr, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (w <= screen[0] && h <= screen[1])
                {
                    // older ffmpeg (no window capture): record that area of the main screen instead
                    pr.Report(new JobStatus("Window capture isn't available, recording the screen area instead…", -1));
                    return await p.CaptureAsync(nvenc => FfArgs.RecordArea(0, 0, 0, 0, w, h, fps, secs, nvenc, dest), secs, S.Encoder, dest, stop, pr, ct);
                }
            }
            finally
            {
                try { await Control(we, "closeWallpaper -location " + Q(WindowName), CancellationToken.None); } catch { }
            }
        }
    }
}
