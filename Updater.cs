using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OLEDable
{
    /// <summary>
    /// Updates from GitHub Releases. Two channels: "stable" sees only normal releases (made by pushing a tag
    /// like v2.3.0); "dev" also sees the pre-releases the workflow publishes for every push to main
    /// (versions like 2.3.0-dev.14). An update downloads the new setup and runs it silently over the
    /// current install, which keeps the settings, library and tools.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "boywholifts/oledable";   // renamed from hydroshift-recorder (GitHub redirects the old name)
        public const string Stable = "stable", Dev = "dev";

        [DataContract]
        public class Release
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "prerelease")] public bool Prerelease;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "body")] public string Notes;
            [DataMember(Name = "html_url")] public string Page;
            [DataMember(Name = "assets")] public List<Asset> Assets;

            public Version3 Version { get { return Version3.Parse(Tag); } }

            /// <summary>The installer attached to the release.</summary>
            public Asset Setup
            {
                get { return Assets == null ? null : Assets.FirstOrDefault(a => a.Name != null && a.Name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0 && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); }
            }
        }

        [DataContract]
        public class Asset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "browser_download_url")] public string Url;
            [DataMember(Name = "size")] public long Size;
        }

        /// <summary>This build's version, e.g. "2.3.0" or "2.3.0-dev.14" (from the build's informational version).</summary>
        public static string CurrentText
        {
            get
            {
                var a = Assembly.GetExecutingAssembly();
                var info = a.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
                var v = info != null ? info.InformationalVersion : a.GetName().Version.ToString(3);
                int plus = v.IndexOf('+');   // the SDK appends "+<commit>"
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        public static Version3 Current { get { return Version3.Parse(CurrentText); } }

        /// <summary>A build made locally with build.cmd (running from the project's dist folder): no automatic prompts.</summary>
        public static bool IsLocalBuild
        {
            get { return string.Equals(Path.GetFileName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')), "dist", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>The newest release on <paramref name="channel"/> that's newer than this build and has a setup, or null.</summary>
        public static Task<Release> CheckAsync(string channel, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases?per_page=30");
                req.UserAgent = "OLEDable";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                List<Release> list;
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s))
                    list = Json.Read<List<Release>>(r.ReadToEnd());
                ct.ThrowIfCancellationRequested();
                var cur = Current;
                return list.Where(x => !x.Draft && (channel == Dev || !x.Prerelease) && x.Setup != null && x.Version != null)
                           .OrderByDescending(x => x.Version)
                           .FirstOrDefault(x => x.Version.CompareTo(cur) > 0);
            }, ct);
        }

        /// <summary>Downloads the release's setup to a temp folder and returns its path.</summary>
        public static async Task<string> DownloadAsync(Release r, IProgress<JobStatus> pr, CancellationToken ct)
        {
            var dir = Path.Combine(Path.GetTempPath(), "OLEDable", "update");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, r.Setup.Name);
            Tools.TryDelete(file);
            await Downloader.Download(r.Setup.Url, file, new Progress<double>(f =>
                pr.Report(new JobStatus(string.Format(Tools.Inv, "Downloading {0}…  {1:0}%", r.Version, f * 100), f))), ct);
            if (!File.Exists(file) || new FileInfo(file).Length < 100 * 1024) throw new Exception("The update didn't download completely. Try again later.");
            return file;
        }

        /// <summary>Starts the setup silently; it closes this app, updates it in place and opens it again.</summary>
        public static void Install(string setup)
        {
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
    }

    /// <summary>"2.3.0", "v2.3.0", "2.3.0-dev.14": a release is newer than any dev build of the same number.</summary>
    public class Version3 : IComparable<Version3>
    {
        public int Major, Minor, Patch;
        /// <summary>The dev build number, or -1 for a release.</summary>
        public int Dev = -1;

        public static Version3 Parse(string s)
        {
            var m = Regex.Match((s ?? "").Trim(), "^v?(\\d+)\\.(\\d+)(?:\\.(\\d+))?(?:-([0-9A-Za-z.-]+))?");
            if (!m.Success) return null;
            var v = new Version3 { Major = int.Parse(m.Groups[1].Value), Minor = int.Parse(m.Groups[2].Value), Patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0 };
            if (m.Groups[4].Success)
            {
                var d = Regex.Match(m.Groups[4].Value, "(\\d+)$");
                v.Dev = d.Success ? int.Parse(d.Groups[1].Value) : 0;
            }
            return v;
        }

        public int CompareTo(Version3 o)
        {
            if (o == null) return 1;
            int c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            int a = Dev < 0 ? int.MaxValue : Dev, b = o.Dev < 0 ? int.MaxValue : o.Dev;
            return a.CompareTo(b);
        }

        public override string ToString() { return Major + "." + Minor + "." + Patch + (Dev >= 0 ? "-dev." + Dev : ""); }
    }
}
