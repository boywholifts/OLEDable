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
        public bool IsWeb { get { return Kind == "web"; } }
        /// <summary>Scenes and web wallpapers are recorded through Wallpaper Engine's pop-out window.</summary>
        public bool CanRecord { get { return IsScene || IsWeb; } }
        /// <summary>Applications and unknown types: nothing the app can bring in.</summary>
        public bool Unsupported { get { return !IsVideo && !CanRecord; } }
        public string MainTip { get { return IsVideo ? "Edit this video" : CanRecord ? "Record this wallpaper" : null; } }
        public string Name { get { return string.IsNullOrWhiteSpace(Title) ? Path.GetFileName(Folder) : Title; } }
        public string KindLabel
        {
            get { return IsVideo ? "Video" : IsScene ? "Scene" : Kind == "web" ? "Web" : Kind == "application" ? "Application" : "Other"; }
        }

        ImageSource thumb; string meta; bool hasVideo, selected;
        public bool IsSelected { get { return selected; } set { selected = value; Raise("IsSelected"); } }
        object player;
        /// <summary>The live preview while this tile is selected (see TilePreview); null otherwise.</summary>
        public object Player { get { return player; } set { player = value; Raise("Player"); } }
        bool shown = true;
        /// <summary>Passes the Wallpaper Engine page's filter and search.</summary>
        public bool IsShown { get { return shown; } set { if (shown == value) return; shown = value; Raise("IsShown"); } }
        /// <summary>A scene that plays a video inside it (checked from the package's file list, without unpacking).</summary>
        public bool HasVideo { get { return hasVideo; } set { hasVideo = value; Raise("HasVideo"); } }
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

        /// <summary>Every Steam library folder on this PC.</summary>
        static List<string> SteamLibraries()
        {
            var libs = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = k == null ? null : k.GetValue("SteamPath") as string;
                    if (steam == null) return libs;
                    libs.Add(steam);
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (System.IO.File.Exists(vdf))
                        foreach (Match m in Regex.Matches(System.IO.File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            catch { }
            return libs.Select(x => x.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Folders that contain wallpapers (each wallpaper is a subfolder with a project.json).</summary>
        public static List<KeyValuePair<string, string>> Roots()
        {
            var roots = new List<KeyValuePair<string, string>>();
            foreach (var l in SteamLibraries())
            {
                var ws = Path.Combine(l, "steamapps", "workshop", "content", "431960");
                if (Directory.Exists(ws)) roots.Add(new KeyValuePair<string, string>(ws, "Workshop"));
                var mine = Path.Combine(l, "steamapps", "common", "wallpaper_engine", "projects", "myprojects");
                if (Directory.Exists(mine)) roots.Add(new KeyValuePair<string, string>(mine, "My projects"));
            }
            return roots;
        }

        /// <summary>Wallpaper Engine's own exe (64-bit preferred), or null.</summary>
        public static string FindWallpaperEngine()
        {
            foreach (var l in SteamLibraries())
                foreach (var exe in new[] { "wallpaper64.exe", "wallpaper32.exe" })
                {
                    var p = Path.Combine(l, "steamapps", "common", "wallpaper_engine", exe);
                    if (System.IO.File.Exists(p)) return p;
                }
            return null;
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
                        var item = new WeItem { Folder = d, Title = p.Title, Type = p.Type, File = p.File, PreviewPath = prev, Source = root.Value, Meta = "" };
                        if (item.IsScene) item.HasVideo = SceneHasVideo(d);
                        list.Add(item);
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

        // ---------------------------------------------------------------- does a scene contain a video?

        /// <summary>Smaller videos are effects (sparks, a blinking sign), not the wallpaper itself.</summary>
        const long MinVideoBytes = 1024 * 1024;

        /// <summary>
        /// True if the scene has a video worth using: an .mp4/.webm… file, or a texture that is really an MP4
        /// (Wallpaper Engine's video textures). Reads only the package's file list and a few bytes per texture.
        /// </summary>
        public static bool SceneHasVideo(string folder)
        {
            try
            {
                var pkg = Directory.GetFiles(folder, "*.pkg").FirstOrDefault();
                if (pkg != null) return PkgHasVideo(pkg);
                // an already-unpacked scene
                foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    long len = new FileInfo(f).Length;
                    if (len < MinVideoBytes) continue;
                    if (VideoExt.Contains(ext)) return true;
                    if (ext == ".tex") using (var fs = System.IO.File.OpenRead(f)) if (TexIsVideo(new BinaryReader(fs))) return true;
                }
            }
            catch { }
            return false;
        }

        static string ReadSized(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) throw new InvalidDataException();
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        /// <summary>Wallpaper Engine's .pkg: "PKGV…", a file count, then (name, offset, length) for each file, then the data.</summary>
        static bool PkgHasVideo(string pkg)
        {
            using (var fs = System.IO.File.OpenRead(pkg))
            using (var r = new BinaryReader(fs))
            {
                if (!ReadSized(r).StartsWith("PKGV")) return false;
                int count = r.ReadInt32();
                if (count < 0 || count > 100000) return false;
                var texs = new List<KeyValuePair<long, long>>();
                for (int i = 0; i < count; i++)
                {
                    var name = ReadSized(r);
                    long off = r.ReadInt32(), len = r.ReadInt32();
                    if (len < MinVideoBytes) continue;
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (VideoExt.Contains(ext)) return true;
                    if (ext == ".tex") texs.Add(new KeyValuePair<long, long>(off, len));
                }
                long dataStart = fs.Position;
                foreach (var t in texs)
                {
                    fs.Position = dataStart + t.Key;
                    if (TexIsVideo(r)) return true;
                }
            }
            return false;
        }

        static string ReadMagic(BinaryReader r)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++) { byte b = r.ReadByte(); if (b == 0) break; sb.Append((char)b); }
            return sb.ToString();
        }

        /// <summary>
        /// A .tex texture: "TEXV0005", "TEXI0001", 7 header ints, then the image container. Version 4 containers
        /// ("TEXB0004") say whether the texture is an MP4 video.
        /// </summary>
        static bool TexIsVideo(BinaryReader r)
        {
            try
            {
                if (!ReadMagic(r).StartsWith("TEXV")) return false;
                if (!ReadMagic(r).StartsWith("TEXI")) return false;
                for (int i = 0; i < 7; i++) r.ReadInt32();
                var container = ReadMagic(r);
                if (container != "TEXB0004") return false;
                r.ReadInt32();                 // image count
                int format = r.ReadInt32();    // FreeImage format: -1 unknown, 35 MP4
                bool mp4 = r.ReadInt32() == 1;
                return format == 35 || (format == -1 && mp4);   // what RePKG exports as .mp4
            }
            catch { return false; }
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
