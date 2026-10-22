using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>One video in the library folder, with details filled in in the background.</summary>
    public class LibraryItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public DateTime Modified { get; set; }
        public long Size { get; set; }
        public VideoInfo Info { get; set; }
        /// <summary>Session id, when the app made this video (read from the file's comment tag).</summary>
        public string Id { get; set; }
        /// <summary>"recording", "video", or null for videos the app didn't make.</summary>
        public string Kind { get; set; }
        public bool Tuned { get; set; }

        ImageSource thumb; string meta;
        public ImageSource Thumb { get { return thumb; } set { thumb = value; Raise("Thumb"); } }
        public string Meta { get { return meta; } set { meta = value; Raise("Meta"); } }
        /// <summary>True when the file is in the converted-videos folder.</summary>
        public bool InConverted { get; set; }
        /// <summary>The dashed "record or drop a video" tile at the end of the grid.</summary>
        public bool IsAddTile { get; set; }
        public bool IsNormal { get { return !IsAddTile; } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }

        public string KindLabel
        {
            get { return Kind == "recording" ? "Recording" : Kind == "video" ? "Converted" : "Video"; }
        }
    }

    /// <summary>
    /// The library's hidden ".hydroshift" folder: one JSON per edit session (the settings), plus the original
    /// recordings, so a video can be re-edited from its clean source instead of being processed twice.
    /// </summary>
    public class SourceStore
    {
        public readonly string Dir;

        public SourceStore(string libraryFolder)
        {
            Dir = System.IO.Path.Combine(libraryFolder, ".hydroshift");
        }

        void EnsureDir()
        {
            if (Directory.Exists(Dir)) return;
            Directory.CreateDirectory(Dir);
            try { var di = new DirectoryInfo(Dir); di.Attributes |= FileAttributes.Hidden; } catch { }
        }

        public static string NewId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>Where a new recording's original is kept.</summary>
        public string RecordingPath(string id)
        {
            EnsureDir();
            var d = System.IO.Path.Combine(Dir, "sources");
            Directory.CreateDirectory(d);
            return System.IO.Path.Combine(d, id + ".mp4");
        }

        public void Save(EditSession s)
        {
            try { EnsureDir(); File.WriteAllText(System.IO.Path.Combine(Dir, s.Id + ".json"), Json.Write(s)); } catch { }
        }

        public EditSession Load(string id)
        {
            try
            {
                var f = System.IO.Path.Combine(Dir, id + ".json");
                return File.Exists(f) ? Json.Read<EditSession>(File.ReadAllText(f)) : null;
            }
            catch { return null; }
        }

        /// <summary>Removes a session's settings and its kept recording (used when its video is deleted).</summary>
        public void Forget(string id)
        {
            var s = Load(id);
            if (s != null && s.Kind == "recording" && s.SourcePath != null && s.SourcePath.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
                TryDelete(s.SourcePath);
            TryDelete(System.IO.Path.Combine(Dir, id + ".json"));
        }
    }

    public static class LibraryScanner
    {
        static readonly string[] Ext = { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v", ".wmv" };

        public static bool IsVideo(string path)
        {
            var e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return Ext.Contains(e) && !path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Quick listing (names, sizes, dates) of the library folder and the converted-videos folder;
        /// details come later from <see cref="LoadDetails"/>.
        /// </summary>
        public static List<LibraryItem> List(string folder, string converted)
        {
            var list = new List<LibraryItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new[] { converted, folder };   // converted first, so a shared folder counts as converted
            for (int d = 0; d < dirs.Length; d++)
            {
                var dir = dirs[d];
                bool conv = d == 0;
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        if (!IsVideo(f) || !seen.Add(f)) continue;
                        var fi = new FileInfo(f);
                        list.Add(new LibraryItem
                        {
                            Path = f, Name = System.IO.Path.GetFileNameWithoutExtension(f),
                            Modified = fi.LastWriteTime, Size = fi.Length, Meta = "", InConverted = conv,
                        });
                    }
                }
                catch { }
            }
            return list.OrderByDescending(i => i.Modified).ToList();
        }

        /// <summary>Probes the video and makes (or reuses) its thumbnail. Runs off the UI thread.</summary>
        public static void LoadDetails(LibraryItem it, string ffmpeg, string ffprobe, string cacheDir)
        {
            var info = Probe(ffprobe, it.Path);
            it.Info = info;
            if (info != null && info.Comment != null && info.Comment.StartsWith("hsr|"))
            {
                var parts = info.Comment.Split('|');
                if (parts.Length > 1) it.Id = parts[1];
                if (parts.Length > 2) it.Kind = parts[2];
                it.Tuned = parts.Contains("tb");
            }

            var bits = new List<string> { it.KindLabel };
            if (info != null)
            {
                if (info.Fps > 0) bits.Add(string.Format(Inv, "{0:0} fps", info.Fps));
                if (info.Duration > 0) bits.Add(Fmt(info.Duration));
            }
            bits.Add(string.Format(Inv, "{0:0.#} MB", it.Size / 1048576.0));
            it.Meta = string.Join("  ·  ", bits);

            try
            {
                Directory.CreateDirectory(cacheDir);
                var jpg = System.IO.Path.Combine(cacheDir, Key(it) + ".png");
                if (!File.Exists(jpg) && ffmpeg != null)
                {
                    double at = info != null && info.Duration > 0 ? Math.Min(1.5, info.Duration * 0.3) : 0;
                    var j = new FfJob(ffmpeg, FfArgs.Thumb(it.Path, at, jpg));
                    if (!j.Wait(20000)) j.Kill();
                    j.Drain();
                }
                if (File.Exists(jpg))
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.UriSource = new Uri(jpg);
                    b.EndInit();
                    b.Freeze();
                    it.Thumb = b;
                }
            }
            catch { }
        }

        /// <summary>Cache key from path, size and date, so an updated video gets a new thumbnail.</summary>
        static string Key(LibraryItem it)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(it.Path.ToLowerInvariant() + "|" + it.Size + "|" + it.Modified.Ticks));
                return BitConverter.ToString(bytes, 0, 10).Replace("-", "");
            }
        }
    }
}
