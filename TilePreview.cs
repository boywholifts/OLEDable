using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace HydroShiftRecorder
{
    /// <summary>
    /// The live preview in a selected gallery tile: the video itself (muted, looping), or an animated GIF preview
    /// (Wallpaper Engine scenes). One at a time: playing another stops the first. The player is handed to the item
    /// (its Player property, shown by the tile on top of its thumbnail), so it moves from tile to tile without
    /// rebuilding anything. If a file can't be played (an unsupported codec) the tile keeps its thumbnail.
    /// </summary>
    public sealed class TilePreview
    {
        const double Radius = 12;   // the tiles' corner radius

        readonly MediaElement media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            IsMuted = true, Volume = 0, Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false,
        };
        readonly Image gif = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        Action<object> attach;   // gives the player to the current tile (null takes it back)
        int gen;

        public TilePreview()
        {
            // rounded corners like the thumbnail underneath
            foreach (var el in new FrameworkElement[] { media, gif })
            {
                var e0 = el;
                e0.SizeChanged += (s, e) => e0.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);
            }
            media.MediaOpened += (s, e) => media.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            media.MediaEnded += (s, e) => { media.Position = TimeSpan.Zero; media.Play(); };
            media.MediaFailed += (s, e) => Stop();   // the thumbnail stays
        }

        /// <summary>Plays <paramref name="path"/> in the tile <paramref name="setPlayer"/> belongs to.</summary>
        public void PlayVideo(Action<object> setPlayer, string path)
        {
            Stop();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            attach = setPlayer;
            media.BeginAnimation(UIElement.OpacityProperty, null);
            media.Opacity = 0;   // fades in once the first frame is ready
            attach(media);
            media.Source = new Uri(path);
            media.Play();
        }

        /// <summary>Animates the GIF at <paramref name="path"/> in the tile; nothing happens for a still image.</summary>
        public async void PlayGif(Action<object> setPlayer, string path)
        {
            Stop();
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)) return;
            int g = gen;
            var anim = await Task.Run(() => GifAnimation.Load(path));
            if (g != gen || anim == null) return;
            attach = setPlayer;
            gif.Source = anim.Frames[0];
            gif.BeginAnimation(Image.SourceProperty, anim.Animation);
            attach(gif);
        }

        /// <summary>Stops whatever is playing and gives the tile its thumbnail back.</summary>
        public void Stop()
        {
            gen++;
            media.Stop();
            media.Source = null;   // releases the file
            media.BeginAnimation(UIElement.OpacityProperty, null);
            media.Opacity = 0;
            gif.BeginAnimation(Image.SourceProperty, null);
            gif.Source = null;
            if (attach != null) { attach(null); attach = null; }
        }
    }

    /// <summary>An animated GIF decoded into full frames (GIF frames are often only the part that changed).</summary>
    sealed class GifAnimation
    {
        public List<BitmapSource> Frames;
        public ObjectAnimationUsingKeyFrames Animation;

        const int MaxFrames = 400;
        const long MaxBytes = 400L * 1024 * 1024;

        public static GifAnimation Load(string path)
        {
            try
            {
                var dec = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (dec.Frames.Count < 2) return null;
                int w = 0, h = 0;
                try
                {
                    var md = dec.Metadata;
                    if (md != null)
                    {
                        w = Convert.ToInt32(md.GetQuery("/logscrdesc/Width"));
                        h = Convert.ToInt32(md.GetQuery("/logscrdesc/Height"));
                    }
                }
                catch { }
                if (w <= 0 || h <= 0) { w = dec.Frames[0].PixelWidth; h = dec.Frames[0].PixelHeight; }
                int stride = w * 4;
                if ((long)stride * h * Math.Min(dec.Frames.Count, MaxFrames) > MaxBytes) return null;

                var canvas = new byte[stride * h];
                var frames = new List<BitmapSource>();
                var anim = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                var t = TimeSpan.Zero;
                for (int i = 0; i < dec.Frames.Count && i < MaxFrames; i++)
                {
                    var f = dec.Frames[i];
                    int left = 0, top = 0, delay = 10, disposal = 0;
                    var fm = f.Metadata as BitmapMetadata;
                    if (fm != null)
                    {
                        try { left = Convert.ToInt32(fm.GetQuery("/imgdesc/Left")); } catch { }
                        try { top = Convert.ToInt32(fm.GetQuery("/imgdesc/Top")); } catch { }
                        try { delay = Convert.ToInt32(fm.GetQuery("/grctlext/Delay")); } catch { }
                        try { disposal = Convert.ToInt32(fm.GetQuery("/grctlext/Disposal")); } catch { }
                    }
                    var before = disposal == 3 ? (byte[])canvas.Clone() : null;

                    // draw this frame's pixels (transparent ones leave what's underneath)
                    var bgra = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                    int fw = bgra.PixelWidth, fh = bgra.PixelHeight, fs = fw * 4;
                    var px = new byte[fs * fh];
                    bgra.CopyPixels(px, fs, 0);
                    for (int y = 0; y < fh; y++)
                    {
                        int cy = top + y;
                        if (cy < 0 || cy >= h) continue;
                        for (int x = 0; x < fw; x++)
                        {
                            int cx = left + x;
                            if (cx < 0 || cx >= w) continue;
                            int si = y * fs + x * 4, di = cy * stride + cx * 4;
                            if (px[si + 3] == 0) continue;
                            canvas[di] = px[si]; canvas[di + 1] = px[si + 1]; canvas[di + 2] = px[si + 2]; canvas[di + 3] = 255;
                        }
                    }
                    var frame = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                    frame.Freeze();
                    frames.Add(frame);
                    anim.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(t)));
                    t += TimeSpan.FromMilliseconds((delay <= 1 ? 10 : delay) * 10);   // like browsers: 0 or 1 means 100 ms

                    // what happens to this frame's area before the next one
                    if (disposal == 2)
                        for (int y = Math.Max(0, top); y < Math.Min(h, top + fh); y++)
                            Array.Clear(canvas, y * stride + Math.Max(0, left) * 4, Math.Max(0, Math.Min(w, left + fw) - Math.Max(0, left)) * 4);
                    else if (disposal == 3) canvas = before;
                }
                anim.Duration = new Duration(t);
                anim.Freeze();
                return new GifAnimation { Frames = frames, Animation = anim };
            }
            catch { return null; }
        }
    }
}
