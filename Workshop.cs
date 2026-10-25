using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>One Wallpaper Engine wallpaper found on this PC (a Workshop download or one of your own projects).</summary>
    public class WeItem : INotifyPropertyChanged
    {
        public string Folder, Title, Type, File, PreviewPath, Source;
        /// <summary>"video", "scene", "web", "application" or "" (unknown).</summary>
        public string Kind { get { return (Type ?? "").ToLowerInvariant(); } }
        public bool IsVideo { get { return Kind == "video"; } }
        public bool IsScene { get { return Kind == "scene"; } }
        public string Name { get { return string.IsNullOrWhiteSpace(Title) ? Path.GetFileName(Folder) : Title; } }
        public string KindLabel
        {
            get { return IsVideo ? "Video" : IsScene ? "Scene" : Kind == "web" ? "Web" : Kind == "application" ? "Application" : "Other"; }
        }

        ImageSource thumb; string meta;
        public ImageSource Thumb { get { return thumb; } set { thumb = value; Raise("Thumb"); } }
        public string Meta { get { return meta; } set { meta = value; Raise("Meta"); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    /// <summary>
    /// Finds Wallpaper Engine wallpapers (every Steam library's Workshop folder for app 431960, plus Wallpaper
    /// Engine's own "myprojects"), and copies or extracts what the app needs. It only ever reads Wallpaper
    /// Engine's folders: everything it uses is copied into the library's "Wallpaper Engine" folder first.
    /// </summary>
    public static class Workshop
    {
        const string RepkgUrl = "https://github.com/notscuffed/repkg/releases/download/v0.4.0-alpha/RePKG.zip";
        static readonly string[] VideoExt = { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v", ".wmv" };

        /// <summary>The library subfolder that holds copies of Wallpaper Engine wallpapers.</summary>
        public static string ImportDir(Settings s) { return Path.Combine(s.OutDir, "Wallpaper Engine"); }

        /// <summary>Folders that contain wallpapers (each wallpaper is a subfolder with a project.json).</summary>
        public static List<KeyValuePair<string, string>> Roots()
        {
            var roots = new List<KeyValuePair<string, string>>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = k == null ? null : k.GetValue("SteamPath") as string;
                    if (steam == null) return roots;
                    var libs = new List<string> { steam };
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (System.IO.File.Exists(vdf))
                        foreach (Match m in Regex.Matches(System.IO.File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    foreach (var l in libs.Select(x => x.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var ws = Path.Combine(l, "steamapps", "workshop", "content", "431960");
                        if (Directory.Exists(ws)) roots.Add(new KeyValuePair<string, string>(ws, "Workshop"));
                        var mine = Path.Combine(l, "steamapps", "common", "wallpaper_engine", "projects", "myprojects");
                        if (Directory.Exists(mine)) roots.Add(new KeyValuePair<string, string>(mine, "My projects"));
                    }
                }
            }
            catch { }
            return roots;
        }

        /// <summary>Every wallpaper in <see cref="Roots"/>, newest first. Quick: reads only each project.json.</summary>
        public static List<WeItem> Scan()
        {
            var list = new List<WeItem>();
            foreach (var root in Roots())
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root.Key).ToList(); } catch { continue; }
                foreach (var d in dirs)
                {
                    var pj = Path.Combine(d, "project.json");
                    if (!System.IO.File.Exists(pj)) continue;
                    try
                    {
                        var p = WeProject.Load(pj);
                        var prev = p.Preview != null ? Path.Combine(d, p.Preview) : null;
                        if (prev == null || !System.IO.File.Exists(prev))
                            prev = new[] { "preview.jpg", "preview.png", "preview.gif" }.Select(f => Path.Combine(d, f)).FirstOrDefault(System.IO.File.Exists);
                        list.Add(new WeItem { Folder = d, Title = p.Title, Type = p.Type, File = p.File, PreviewPath = prev, Source = root.Value, Meta = "" });
                    }
                    catch { }
                }
            }
            return list.OrderByDescending(i => SafeTime(i.Folder)).ToList();
        }

        static DateTime SafeTime(string d) { try { return Directory.GetLastWriteTime(d); } catch { return DateTime.MinValue; } }

        /// <summary>The preview image, small (off the UI thread).</summary>
        public static ImageSource LoadPreview(string path)
        {
            if (path == null) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 480;
                b.UriSource = new Uri(path);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        /// <summary>Where a copy of this wallpaper goes in the library: "&lt;library&gt;\Wallpaper Engine\&lt;title&gt;.ext".</summary>
        public static string CopyPath(Settings s, WeItem it, string ext)
        {
            var n = SafeName(it.Name);
            if (n.Length == 0) n = Path.GetFileName(it.Folder);
            return Path.Combine(ImportDir(s), n + ext.ToLowerInvariant());
        }

        /// <summary>Whether something from this wallpaper is already in the library.</summary>
        public static bool AlreadyCopied(Settings s, WeItem it)
        {
            try
            {
                var dir = ImportDir(s);
                if (!Directory.Exists(dir)) return false;
                var n = SafeName(it.Name);
                return Directory.EnumerateFiles(dir, n + ".*").Any() || Directory.EnumerateFiles(dir, n + " (still).*").Any();
            }
            catch { return false; }
        }

        /// <summary>Copies a video wallpaper's file into the library (skipped when the same copy is already there).</summary>
        public static string CopyVideo(Settings s, WeItem it)
        {
            var src = Path.Combine(it.Folder, it.File ?? "");
            if (!System.IO.File.Exists(src)) throw new Exception("The video file of “" + it.Name + "” is missing from its folder.");
            return CopyInto(s, it, src, "");
        }

        static string CopyInto(Settings s, WeItem it, string src, string suffix)
        {
            Directory.CreateDirectory(ImportDir(s));
            var n = SafeName(it.Name);
            if (n.Length == 0) n = Path.GetFileName(it.Folder);
            var ext = Path.GetExtension(src).ToLowerInvariant();
            var dest = Path.Combine(ImportDir(s), n + suffix + ext);
            long size = new FileInfo(src).Length;
            for (int i = 2; System.IO.File.Exists(dest) && new FileInfo(dest).Length != size; i++)
                dest = Path.Combine(ImportDir(s), n + suffix + " (" + i + ")" + ext);
            if (!System.IO.File.Exists(dest)) System.IO.File.Copy(src, dest);
            return dest;
        }

        // ------------------------------------------------------------ scenes: RePKG

        public static string RepkgExe(string dataDir) { return Path.Combine(dataDir, "repkg", "RePKG.exe"); }

        /// <summary>Downloads RePKG (about 1.5 MB, MIT license) the first time a scene is extracted.</summary>
        public static async Task EnsureRepkgAsync(string dataDir, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var exe = RepkgExe(dataDir);
            if (System.IO.File.Exists(exe)) return;
            var zip = Path.Combine(Path.GetTempPath(), "repkg.zip");
            TryDelete(zip);
            try
            {
                await Downloader.Download(RepkgUrl, zip, new Progress<double>(f =>
                    pr.Report(new JobStatus(string.Format(Inv, "Downloading RePKG (one time only)…  {0:0}%", f * 100), f))), ct);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(exe));
                    using (var z = ZipFile.OpenRead(zip))
                        foreach (var e in z.Entries)
                            if (e.Name.Equals("RePKG.exe", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("THIRD-PARTY", StringComparison.OrdinalIgnoreCase))
                                e.ExtractToFile(Path.Combine(Path.GetDirectoryName(exe), e.Name), true);
                }, ct);
            }
            finally { TryDelete(zip); }
        }

        /// <summary>
        /// Unpacks a scene with RePKG into a temporary folder (textures converted to PNG) and returns that folder.
        /// Scenes without a scene.pkg are already unpacked: their .tex files are converted in a copy.
        /// </summary>
        public static async Task<string> UnpackSceneAsync(string dataDir, WeItem it, IProgress<JobStatus> pr, CancellationToken ct)
        {
            await EnsureRepkgAsync(dataDir, pr, ct);
            var outDir = Path.Combine(Path.GetTempPath(), "hsr_we_" + Path.GetFileName(it.Folder));
            TryDeleteDir(outDir);
            Directory.CreateDirectory(outDir);
            pr.Report(new JobStatus("Unpacking “" + it.Name + "”…", -1));
            var pkg = Directory.GetFiles(it.Folder, "*.pkg").FirstOrDefault();
            string args = pkg != null
                ? "extract -s --overwrite -o " + Q(outDir) + " " + Q(pkg)
                : "extract -t -s --overwrite -o " + Q(outDir) + " " + Q(it.Folder);
            var job = new FfJob(RepkgExe(dataDir), args);
            await job.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (pkg == null)
                // already-unpacked scene: its ready-made images and videos are useful too
                foreach (var f in Directory.EnumerateFiles(it.Folder, "*.*", SearchOption.AllDirectories))
                    if (IsImage(f) || VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        try { System.IO.File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true); } catch { }
            return outDir;
        }

        /// <summary>The scene's main image: the largest picture in it (usually the background), copied into the library.</summary>
        public static string PickStill(Settings s, WeItem it, string unpacked)
        {
            string best = null; long bestArea = 0;
            foreach (var f in Directory.EnumerateFiles(unpacked, "*.*", SearchOption.AllDirectories).Where(f => IsImage(f) || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetFileName(f).StartsWith("preview", StringComparison.OrdinalIgnoreCase)) continue;   // the Workshop thumbnail
                try
                {
                    var frame = BitmapFrame.Create(new Uri(f), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    long area = (long)frame.PixelWidth * frame.PixelHeight;
                    if (area > bestArea) { bestArea = area; best = f; }
                }
                catch { }
            }
            return best == null ? null : CopyInto(s, it, best, " (still)");
        }

        /// <summary>A video inside the scene (some scenes are just a video layer), copied into the library; null if none.</summary>
        public static string PickVideo(Settings s, WeItem it, string unpacked)
        {
            var v = Directory.EnumerateFiles(unpacked, "*.*", SearchOption.AllDirectories)
                             .Where(f => VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            return v == null ? null : CopyInto(s, it, v, "");
        }
    }
}
