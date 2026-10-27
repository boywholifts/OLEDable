using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OLEDable
{
    /// <summary>
    /// A live, smooth view of the HydroShift for the Record page: one long-running ffmpeg captures the display
    /// (Desktop Duplication) at 30 fps, scales it down and streams raw frames over a pipe into a reused bitmap.
    /// Much cheaper than a new ffmpeg per screenshot, which managed one frame every couple of seconds.
    /// </summary>
    public sealed class LiveFeed : IDisposable
    {
        public const int Fps = 30;
        /// <summary>Preview frame size (the screen's shape, scaled down).</summary>
        public readonly int W, H;

        public readonly WriteableBitmap Bitmap;
        /// <summary>Raised on the UI thread when the feed stops on its own (display gone, ffmpeg failed).</summary>
        public event Action Failed;
        /// <summary>Raised on the UI thread for the first frame.</summary>
        public event Action Started;

        readonly string ffmpeg;
        readonly int adapter, output;
        readonly Dispatcher ui;
        readonly byte[] front;
        volatile bool stopped;
        int pending;   // 1 while a frame is waiting for the UI thread; newer frames are dropped meanwhile
        Process proc;
        Thread reader;

        public LiveFeed(string ffmpeg, int adapter, int output, int w, int h, Dispatcher ui)
        {
            this.ffmpeg = ffmpeg; this.adapter = adapter; this.output = output; this.ui = ui;
            W = Math.Max(2, w / 2 * 2); H = Math.Max(2, h / 2 * 2);
            Bitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
            front = new byte[W * H * 4];
        }

        public void Start()
        {
            reader = new Thread(Run) { IsBackground = true, Name = "HydroShift live view" };
            reader.Start();
        }

        /// <summary>
        /// Scaling on the GPU first (only a small image is copied back) when this ffmpeg has scale_d3d11,
        /// otherwise the plain way.
        /// </summary>
        string[] Graphs()
        {
            string grab = "ddagrab=output_idx=" + output + ":framerate=" + Fps + ":draw_mouse=0";
            return new[]
            {
                grab + ",scale_d3d11=width=" + W + ":height=" + H + ",hwdownload,format=bgra",
                grab + ",hwdownload,format=bgra,scale=" + W + ":" + H + ":flags=bilinear",
            };
        }

        void Run()
        {
            foreach (var graph in Graphs())
            {
                if (stopped) return;
                int got = RunOne(graph);
                if (got > 0) break;   // it worked, then ended: the display went away
            }
            if (!stopped) ui.BeginInvoke(new Action(() => { if (!stopped) Failed?.Invoke(); }));
        }

        /// <summary>Runs one ffmpeg until it ends; returns how many frames came through.</summary>
        int RunOne(string graph)
        {
            var args = "-hide_banner -loglevel error -init_hw_device d3d11va=hs:" + adapter + " -filter_hw_device hs " +
                       "-filter_complex \"" + graph + "\" -f rawvideo -pix_fmt bgra pipe:1";
            var si = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            int frames = 0;
            try
            {
                var p = Process.Start(si);
                lock (this) proc = p;
                if (stopped) { Kill(p); return 0; }
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                var stream = p.StandardOutput.BaseStream;
                var back = new byte[W * H * 4];
                while (!stopped)
                {
                    if (!ReadFull(stream, back)) break;
                    frames++;
                    if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) continue;   // UI still busy: drop this one
                    lock (front) Buffer.BlockCopy(back, 0, front, 0, back.Length);
                    bool first = frames == 1;
                    ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        if (!stopped)
                        {
                            lock (front) Bitmap.WritePixels(new Int32Rect(0, 0, W, H), front, W * 4, 0);
                            if (first) Started?.Invoke();
                        }
                        Interlocked.Exchange(ref pending, 0);
                    }));
                }
                Kill(p);
                try { p.WaitForExit(2000); p.Dispose(); } catch { }
            }
            catch { }
            finally { lock (this) proc = null; }
            return frames;
        }

        static bool ReadFull(Stream s, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = s.Read(buf, off, buf.Length - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        static void Kill(Process p)
        {
            try { if (!p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); } } catch { }
            try { if (!p.WaitForExit(300)) p.Kill(); } catch { }
        }

        public void Dispose()
        {
            stopped = true;
            Process p;
            lock (this) p = proc;
            if (p != null) { try { if (!p.HasExited) p.Kill(); } catch { } }
        }
    }
}
