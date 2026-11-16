using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Uselim
{
    // Uselim's own updates from booban1/uselim's releases, asked for at launch and every six hours. The Windows build
    // has releases of its own, tagged windows-v<version> and kept off "latest" so the Mac's updater and Culpmit read the
    // Mac's; its zip is Uselim-<version>-windows.zip beside its .sha256, the version read from that name. The newest
    // such zip across the recent releases counts. An update downloads both, checks the digest, takes Uselim.exe out of the zip, renames the
    // running exe to Uselim.old.exe (Windows lets a running exe be renamed), puts the new one in its place, starts it and
    // quits. The next start deletes the old one.

    internal sealed class Updater
    {
        private const string ReleasesUrl = "https://api.github.com/repos/booban1/uselim/releases?per_page=30";
        private static readonly Regex assetName = new Regex(@"^Uselim-(\d+(?:\.\d+)*)-windows\.zip$", RegexOptions.IgnoreCase);
        private static readonly HttpClient client = MakeClient();

        public string Newest { get; private set; }
        public bool IsBusy { get; private set; }
        public Action Changed;
        private DateTime checkedAt = DateTime.MinValue;

        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Uselim-Windows");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return c;
        }

        public void Check()
        {
            if (IsBusy || (DateTime.UtcNow - checkedAt).TotalHours < 6) return;
            checkedAt = DateTime.UtcNow;
            Task.Run(() =>
            {
                var found = Latest();
                Newest = found != null && Spans.IsNewer(found.Item1, Usage.Version) ? found.Item1 : null;
                Changed?.Invoke();
            });
        }

        // the newest Windows version among the recent releases, its zip and the zip's checksum
        private static Tuple<string, string, string> Latest()
        {
            Tuple<string, string, string> best = null;
            try
            {
                var releases = Json.Parse(client.GetStringAsync(ReleasesUrl).GetAwaiter().GetResult()) as System.Collections.Generic.List<object>;
                foreach (var release in releases?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (release.Bool("draft") == true || release.Bool("prerelease") == true) continue;
                    var assets = release.Arr("assets")?.OfType<JObject>().ToList();
                    if (assets == null) continue;
                    foreach (var a in assets)
                    {
                        var m = assetName.Match(a.Str("name") ?? "");
                        if (!m.Success) continue;
                        var sum = assets.FirstOrDefault(x => string.Equals(x.Str("name"), a.Str("name") + ".sha256", StringComparison.OrdinalIgnoreCase));
                        if (sum == null) continue;
                        if (best == null || Spans.IsNewer(m.Groups[1].Value, best.Item1))
                            best = Tuple.Create(m.Groups[1].Value, a.Str("browser_download_url"), sum.Str("browser_download_url"));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Note("update check: " + e.Message);
            }
            return best;
        }

        // the update; each live step and any stop comes back as one line for the panel, and done() once the new copy runs
        public void Update(Action<string> report, Action done)
        {
            if (IsBusy || !Install.IsInstalledCopy) return;
            IsBusy = true;
            Task.Run(() =>
            {
                try
                {
                    var found = Latest();
                    if (found == null || !Spans.IsNewer(found.Item1, Usage.Version))
                    {
                        IsBusy = false;
                        report("Uselim is up to date");
                        return;
                    }
                    report("Downloading Uselim " + found.Item1 + "…");
                    Log.Note("update: installing " + found.Item1 + " over " + Usage.Version);
                    var expected = client.GetStringAsync(found.Item3).GetAwaiter().GetResult().Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    var zip = client.GetByteArrayAsync(found.Item2).GetAwaiter().GetResult();
                    string digest;
                    using (var sha = SHA256.Create()) digest = string.Concat(sha.ComputeHash(zip).Select(b => b.ToString("x2")));
                    if (expected == null || digest != expected) throw new InvalidDataException("the download's digest differs from its checksum");
                    var fresh = Path.Combine(Paths.InstallDir, "Uselim.new.exe");
                    using (var archive = new ZipArchive(new MemoryStream(zip)))
                    {
                        var entry = archive.Entries.FirstOrDefault(x => string.Equals(x.Name, "Uselim.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new InvalidDataException("the zip carries zero Uselim.exe");
                        using (var from = entry.Open())
                        using (var to = File.Create(fresh))
                            from.CopyTo(to);
                    }
                    var old = Path.Combine(Paths.InstallDir, "Uselim.old.exe");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Paths.InstalledExe, old);
                    File.Move(fresh, Paths.InstalledExe);
                    done();
                    Process.Start(new ProcessStartInfo(Paths.InstalledExe, "--wait") { UseShellExecute = false, WorkingDirectory = Paths.InstallDir });
                }
                catch (Exception e)
                {
                    Log.Note("update stopped: " + e.Message);
                    IsBusy = false;
                    report("The update stopped; choose Update to try again");
                }
            });
        }

        // the copy an update set aside, once the new one runs
        public static void Tidy()
        {
            try
            {
                var old = Path.Combine(Paths.InstallDir, "Uselim.old.exe");
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception) { }
        }
    }
}
