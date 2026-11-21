using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Uselim
{
    // Uselim's own updates from booban1/uselim's releases, asked for at launch and every six hours. The Linux build has
    // releases of its own, tagged linux-v<version> and kept off "latest" as the Windows ones are; each holds
    // Uselim-<version>-linux-<x64|arm64>.tar.gz beside its .sha256, the version read from that name. An update downloads
    // both, checks the digest, takes `uselim` out of the archive, moves it over ~/.local/bin/uselim (Linux lets a running
    // program's file be replaced; the running copy keeps its own), starts it and quits.

    internal sealed class Updater
    {
        private const string ReleasesUrl = "https://api.github.com/repos/booban1/uselim/releases?per_page=30";
        private static readonly Regex assetName = new Regex(@"^Uselim-(\d+(?:\.\d+)*)-linux-" + Arch + @"\.tar\.gz$", RegexOptions.IgnoreCase);
        private static readonly HttpClient client = MakeClient();

        public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        public string Newest { get; private set; }
        public bool IsBusy { get; private set; }
        public Action Changed;
        private DateTime checkedAt = DateTime.MinValue;

        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Uselim-Linux");
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

        // the newest Linux version among the recent releases, its archive and the archive's checksum
        private static Tuple<string, string, string> Latest()
        {
            Tuple<string, string, string> best = null;
            try
            {
                var releases = Json.Parse(client.GetStringAsync(ReleasesUrl).GetAwaiter().GetResult()) as List<object>;
                foreach (var release in releases?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (release.Bool("draft") == true || release.Bool("prerelease") == true) continue;
                    var assets = release.Arr("assets")?.OfType<JObject>().ToList();
                    if (assets == null) continue;
                    foreach (var a in assets)
                    {
                        var m = assetName.Match(a.Str("name") ?? "");
                        if (!m.Success) continue;
                        var sum = assets.FirstOrDefault(x => x.Str("name") == a.Str("name") + ".sha256");
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
                    var expected = client.GetStringAsync(found.Item3).GetAwaiter().GetResult()
                        .Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    var archive = client.GetByteArrayAsync(found.Item2).GetAwaiter().GetResult();
                    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
                    if (expected == null || digest != expected) throw new InvalidDataException("the download's digest differs from its checksum");
                    var fresh = Install.InstalledExe + ".new";
                    using (var gz = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress))
                    using (var tar = new TarReader(gz))
                    {
                        TarEntry entry;
                        var wrote = false;
                        while ((entry = tar.GetNextEntry()) != null)
                        {
                            if (System.IO.Path.GetFileName(entry.Name) != "uselim" || entry.DataStream == null) continue;
                            using (var to = File.Create(fresh)) entry.DataStream.CopyTo(to);
                            wrote = true;
                            break;
                        }
                        if (!wrote) throw new InvalidDataException("the archive carries zero uselim");
                    }
                    File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    File.Move(fresh, Install.InstalledExe, true);
                    Process.Start(new ProcessStartInfo(Install.InstalledExe, "--wait") { UseShellExecute = false, WorkingDirectory = Paths.Home });
                    done();
                }
                catch (Exception e)
                {
                    Log.Note("update stopped: " + e.Message);
                    IsBusy = false;
                    report("The update stopped; choose Update to try again");
                }
            });
        }
    }
}
