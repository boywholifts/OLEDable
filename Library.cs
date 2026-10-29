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
using static OLEDable.Tools;

namespace OLEDable
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
        public bool IsImage { get { return Tools.IsImage(Path); } }
        /// <summary>True when the file is in the converted-videos folder.</summary>
        public bool InConverted { get; set; }
        object player;
        /// <summary>The live preview while this tile is selected (see TilePreview); null otherwise.</summary>
        public object Player { get { return player; } set { player = value; Raise("Player"); } }
        bool selected;
        public bool IsSelected { get { return selected; } set { selected = value; Raise("IsSelected"); } }
        bool shown = true;
        /// <summary>Passes the Library's filter.</summary>
        public bool IsShown { get { return shown; } set { if (shown == value) return; shown = value; Raise("IsShown"); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }

        public string KindLabel
        {
            get { return Kind == "recording" ? "Recording" : Kind == "video" ? "Converted" : IsImage ? (InConverted ? "Image · converted" : "Image") : "Video"; }
        }
    }

    /// <summary>
    /// The library's hidden ".oledable" folder (".hydroshift" before the rename): one JSON per edit session (the settings), plus the original
    /// recordings, so a video can be re-edited from its clean source instead of being processed twice.
    /// </summary>
    public class SourceStore
    {
        public readonly string Dir;

        public const string FolderName = ".oledable", OldFolderName = ".hydroshift";

        public SourceStore(string libraryFolder)
        {
            Dir = System.IO.Path.Combine(libraryFolder, FolderName);
            Migrate(libraryFolder, Dir);
        }

        /// <summary>
        /// Libraries made before the rename keep their sessions and kept recordings in ".hydroshift": move that
        /// folder to ".oledable" once, and point the saved sessions at the recordings' new place.
        /// </summary>
        static void Migrate(string libraryFolder, string dir)
        {
            try
            {
                var old = System.IO.Path.Combine(libraryFolder, OldFolderName);
                if (!Directory.Exists(old) || Directory.Exists(dir)) return;
                Directory.Move(old, dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(f);
                        // paths are stored JSON-escaped ("\\" for each backslash); plain ones just in case
                        var fixedText = text.Replace("\\\\" + OldFolderName + "\\\\", "\\\\" + FolderName + "\\\\")
                                            .Replace("\\" + OldFolderName + "\\", "\\" + FolderName + "\\");
                        if (fixedText != text) File.WriteAllText(f, fixedText);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Run when the library loads: moves an old ".hydroshift" folder right away (not only when a session is
        /// first opened), and removes the old export working folder in the converted folder once it's empty.
        /// </summary>
        public static void Tidy(string libraryFolder, string convertedFolder)
        {
            try { Migrate(libraryFolder, System.IO.Path.Combine(libraryFolder, FolderName)); } catch { }
            foreach (var root in new[] { libraryFolder, convertedFolder })
            {
                try
                {
                    var oldWork = System.IO.Path.Combine(root, OldFolderName);
                    if (Directory.Exists(oldWork) && !Directory.EnumerateFiles(oldWork, "*", SearchOption.AllDirectories).Any())
                        Directory.Delete(oldWork, true);
                }
                catch { }
            }
        }

        /// <summary>A session whose kept recording still points into the old folder name.</summary>
        static EditSession Fix(EditSession s)
        {
            if (s != null && !string.IsNullOrEmpty(s.SourcePath) && !File.Exists(s.SourcePath))
            {
                var moved = s.SourcePath.Replace("\\" + OldFolderName + "\\", "\\" + FolderName + "\\");
                if (moved != s.SourcePath && File.Exists(moved)) s.SourcePath = moved;
            }
            return s;
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
            try { EnsureDir(); Tools.WriteAtomic(System.IO.Path.Combine(Dir, s.Id + ".json"), Json.Write(s)); } catch { }
        }

        public EditSession Load(string id)
        {
            try
            {
                var f = System.IO.Path.Combine(Dir, id + ".json");
                return File.Exists(f) ? Fix(Json.Read<EditSession>(File.ReadAllText(f))) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The session that exported a file of this name, for exports that can't carry the app's tag
        /// (PNG images). Null when there's none.
        /// </summary>
        public EditSession FindByOutput(string fileName)
        {
            try
            {
                if (!Directory.Exists(Dir)) return null;
                foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
                {
                    try
                    {
                        var s = Json.Read<EditSession>(File.ReadAllText(f));
                        if (s != null && string.Equals(s.OutputName, fileName, StringComparison.OrdinalIgnoreCase)) return Fix(s);
                    }
                    catch { }
                }
            }
            catch { }
            return null;
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

        /// <summary>A video or still image the library shows.</summary>
        public static bool IsMedia(string path) { return IsVideo(path) || Tools.IsImage(path); }

        public static bool IsVideo(string path)
        {
            var e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return Ext.Contains(e) && !path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Quick listing (names, sizes, dates) of the library folder and the converted-videos folder;
        /// details come later from <see cref="LoadDetails"/>.
        /// </summary>
        public static List<LibraryItem> List(string folder, string converted, string extra = null)
        {
            var list = new List<LibraryItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // converted first, so a shared folder counts as converted; "extra" = copies of Wallpaper Engine wallpapers
            var dirs = new[] { converted, folder, extra };
            for (int d = 0; d < dirs.Length; d++)
            {
                var dir = dirs[d];
                bool conv = d == 0;
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        if (!IsMedia(f) || !seen.Add(f)) continue;
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
            // the probe is remembered next to the thumbnail (same key: path, size and date), so opening the app
            // doesn't start an ffprobe for every video in the library each time
            var key = Key(it);
            var infoFile = System.IO.Path.Combine(cacheDir, key + ".info");
            var info = ReadInfo(infoFile);
            if (info == null)
            {
                info = Probe(ffprobe, it.Path);
                if (info != null) WriteInfo(infoFile, info);
            }
            it.Info = info;
            // the app's tag: "oled|id|kind…" (exports made before the rename say "hsr|…")
            if (info != null && info.Comment != null && (info.Comment.StartsWith("oled|") || info.Comment.StartsWith("hsr|")))
            {
                var parts = info.Comment.Split('|');
                if (parts.Length > 1) it.Id = parts[1];
                if (parts.Length > 2) it.Kind = parts[2];
                it.Tuned = parts.Contains("tb");
            }

            var bits = new List<string> { it.KindLabel };
            if (info != null && info.Width > 0) bits.Add(SizeLabel(info.Width, info.Height));
            if (info != null && !it.IsImage)
            {
                if (info.Fps > 0) bits.Add(string.Format(Inv, "{0:0} fps", info.Fps));
                if (info.Duration > 0) bits.Add(Fmt(info.Duration));
            }
            bits.Add(string.Format(Inv, "{0:0.#} MB", it.Size / 1048576.0));
            it.Meta = string.Join("  ·  ", bits);

            try
            {
                Directory.CreateDirectory(cacheDir);
                var jpg = System.IO.Path.Combine(cacheDir, key + ".png");
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

        static VideoInfo ReadInfo(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var l = File.ReadAllLines(file);
                if (l.Length < 4) return null;
                return new VideoInfo
                {
                    Fps = double.Parse(l[0], Inv), Duration = double.Parse(l[1], Inv),
                    Width = int.Parse(l[2], Inv), Height = int.Parse(l[3], Inv),
                    Comment = l.Length > 4 && l[4].Length > 0 ? l[4] : null,
                };
            }
            catch { return null; }
        }

        static void WriteInfo(string file, VideoInfo i)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[]
                {
                    i.Fps.ToString("R", Inv), i.Duration.ToString("R", Inv), i.Width.ToString(Inv), i.Height.ToString(Inv),
                    (i.Comment ?? "").Replace("\r", " ").Replace("\n", " "),
                });
            }
            catch { }
        }

        /// <summary>
        /// Deletes cached thumbnails and probes for videos that are no longer in the library (or have changed),
        /// once they're a week old, so the cache doesn't grow forever.
        /// </summary>
        public static void PruneCache(string cacheDir, IEnumerable<LibraryItem> items)
        {
            try
            {
                if (!Directory.Exists(cacheDir)) return;
                var keep = new HashSet<string>(items.Select(Key), StringComparer.OrdinalIgnoreCase);
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var f in Directory.EnumerateFiles(cacheDir))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (keep.Contains(name)) continue;
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>The size as people know it: HydroShift, 4K, 1440p, 1080p, or the numbers.</summary>
        public static string SizeLabel(int w, int h)
        {
            if (w == OutputSpec.PanelW && h == OutputSpec.PanelH) return "HydroShift";
            if (w == 3840 && h == 2160) return "4K";
            if (w == 2560 && h == 1440) return "1440p";
            if (w == 1920 && h == 1080) return "1080p";
            return w + " × " + h;
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
