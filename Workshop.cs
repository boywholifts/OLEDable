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

        /// <summary>Where a scene's video is: a whole file, or a byte range of the scene's package.</summary>
        public sealed class VideoRef { public string File; public long Offset, Length; public string Ext = ".mp4"; }

        /// <summary>True if the scene has a video worth using (see <see cref="FindSceneVideo"/>).</summary>
        public static bool SceneHasVideo(string folder) { return FindSceneVideo(folder) != null; }

        /// <summary>
        /// The biggest video in a scene: an .mp4/.webm… file, or a texture that is really an MP4 (Wallpaper
        /// Engine's video textures, in old and new texture layouts). Reads only the package's file list and the
        /// first bytes of each big texture, so it's quick enough to run for every scene.
        /// </summary>
        public static VideoRef FindSceneVideo(string folder)
        {
            try
            {
                var pkg = Directory.GetFiles(folder, "*.pkg").FirstOrDefault();
                if (pkg != null) return PkgVideo(pkg);
                // an already-unpacked scene
                VideoRef best = null;
                foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    long len = new FileInfo(f).Length;
                    if (len < MinVideoBytes || (best != null && len <= best.Length)) continue;
                    if (VideoExt.Contains(ext)) best = new VideoRef { File = f, Offset = 0, Length = len, Ext = ext };
                    else if (ext == ".tex")
                        using (var fs = System.IO.File.OpenRead(f))
                        {
                            long at = Mp4Start(fs, 0);
                            if (at >= 0) best = new VideoRef { File = f, Offset = at, Length = len - at };
                        }
                }
                return best;
            }
            catch { return null; }
        }

        static string ReadSized(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) throw new InvalidDataException();
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        /// <summary>Wallpaper Engine's .pkg: "PKGV…", a file count, then (name, offset, length) for each file, then the data.</summary>
        static VideoRef PkgVideo(string pkg)
        {
            using (var fs = System.IO.File.OpenRead(pkg))
            using (var r = new BinaryReader(fs))
            {
                if (!ReadSized(r).StartsWith("PKGV")) return null;
                int count = r.ReadInt32();
                if (count < 0 || count > 100000) return null;
                var entries = new List<Tuple<string, long, long>>();
                for (int i = 0; i < count; i++)
                {
                    var name = ReadSized(r);
                    long off = r.ReadInt32(), len = r.ReadInt32();
                    if (len >= MinVideoBytes) entries.Add(Tuple.Create(name, off, len));
                }
                long dataStart = fs.Position;
                VideoRef best = null;
                foreach (var e in entries.OrderByDescending(e => e.Item3))
                {
                    if (best != null && e.Item3 <= best.Length) break;
                    var ext = Path.GetExtension(e.Item1).ToLowerInvariant();
                    long start = dataStart + e.Item2;
                    if (VideoExt.Contains(ext)) best = new VideoRef { File = pkg, Offset = start, Length = e.Item3, Ext = ext };
                    else if (ext == ".tex")
                    {
                        long at = Mp4Start(fs, start);
                        if (at >= 0) best = new VideoRef { File = pkg, Offset = at, Length = e.Item3 - (at - start) };
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// A video texture keeps a plain MP4 after its small header ("TEXV0005", "TEXI0001", sizes, "TEXB000x",
        /// mipmap sizes). Returns where the MP4 starts (its first box, just before "ftyp"), or -1 for a picture.
        /// </summary>
        static long Mp4Start(Stream fs, long texStart)
        {
            var head = new byte[256];
            fs.Position = texStart;
            int n = fs.Read(head, 0, head.Length);
            if (n < 16 || System.Text.Encoding.ASCII.GetString(head, 0, 4) != "TEXV") return -1;
            for (int i = 4; i + 4 <= n; i++)
                if (head[i] == (byte)'f' && head[i + 1] == (byte)'t' && head[i + 2] == (byte)'y' && head[i + 3] == (byte)'p')
                    return texStart + i - 4;
            return -1;
        }

        /// <summary>Copies a scene's video out to <paramref name="dest"/>.</summary>
        public static void WriteVideo(VideoRef v, string dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var tmp = dest + ".partial";
            using (var src = System.IO.File.OpenRead(v.File))
            using (var dst = System.IO.File.Create(tmp))
            {
                src.Position = v.Offset;
                var buf = new byte[1 << 20];
                long left = v.Length;
                while (left > 0)
                {
                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                    left -= n;
                }
            }
            if (System.IO.File.Exists(dest)) System.IO.File.Delete(dest);
            System.IO.File.Move(tmp, dest);
        }

        /// <summary>"Use its video": the scene's video, copied into the library. Null if it has none.</summary>
        public static string ExtractSceneVideo(Settings s, WeItem it)
        {
            var v = FindSceneVideo(it.Folder);
            if (v == null) return null;
            var tmp = Path.Combine(Path.GetTempPath(), "OLEDable", "scene_" + Path.GetFileName(it.Folder) + v.Ext);
            try { WriteVideo(v, tmp); return CopyInto(s, it, tmp, ""); }
            finally { TryDelete(tmp); }
        }

        /// <summary>A scene's video for the live preview, kept in the temp folder so it's only written once.</summary>
        public static string PreviewVideo(WeItem it)
        {
            var v = FindSceneVideo(it.Folder);
            if (v == null) return null;
            var path = Path.Combine(Path.GetTempPath(), "OLEDable", "previews", Path.GetFileName(it.Folder) + v.Ext);
            if (!System.IO.File.Exists(path) || new FileInfo(path).Length != v.Length) WriteVideo(v, path);
            return path;
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
