using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Uselim
{
    // The phone side's way through Tailscale on Windows, the Mac's PhoneSetup: off until turned on. Turning it on brings
    // Tailscale in where it is missing (Tailscale's own installer from tailscale.com, its signature checked as Tailscale
    // Inc.'s, then opened, the person's steps staying theirs: the administrator's yes, the sign-in), then puts the relay
    // behind Tailscale Serve with HTTPS. Each live step shows as one line under the panel's Phone row; a click on the line
    // takes that step's own way forward.

    internal enum PhoneStep { Off, Downloading, Installer, Opening, SignIn, Connect, Serving, Https, Ready, Failed }

    internal sealed class PhoneSetup
    {
        public const int RelayPort = 47472;
        private readonly SynchronizationContext ui = SynchronizationContext.Current;
        private System.Windows.Forms.Timer timer;
        private bool isChecking, isAppOpened, isDownloading;
        private string installer, httpsLink, why;
        private Process serving;
        public Action Changed;
        public Action<string> NameFound, LoginFound;
        public string DnsName = "";

        private PhoneStep step = PhoneStep.Off;
        public PhoneStep Step
        {
            get => step;
            private set
            {
                if (value == step) return;
                step = value;
                Changed?.Invoke();
            }
        }

        // the line under the Phone row: the live step alone, explanations left to the guide
        public string Line
        {
            get
            {
                switch (step)
                {
                    case PhoneStep.Downloading: return "Downloading Tailscale…";
                    case PhoneStep.Installer: return "Finish Tailscale's installer";
                    case PhoneStep.Opening: return "Opening Tailscale…";
                    case PhoneStep.SignIn: return "Sign in to Tailscale";
                    case PhoneStep.Connect: return "Turn Tailscale on";
                    case PhoneStep.Serving: return "Starting the relay on Tailscale…";
                    case PhoneStep.Https: return "Turn on HTTPS in Tailscale";
                    case PhoneStep.Failed: return why;
                    default: return null;
                }
            }
        }

        // Tailscale's command where its installer puts it, or on the PATH
        public static string Tool()
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var path = Path.Combine(root, "Tailscale", "tailscale.exe");
                if (File.Exists(path)) return path;
            }
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var path = Path.Combine(folder.Trim('"'), "tailscale.exe");
                    if (folder.Length > 0 && File.Exists(path)) return path;
                }
                catch (Exception) { }
            }
            return null;
        }

        public void Start()
        {
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += (o, e) => Check();
            timer.Start();
            Check();
        }

        public void Stop()
        {
            timer?.Stop();
            timer?.Dispose();
            timer = null;
            try { if (serving != null && !serving.HasExited) serving.Kill(); } catch (Exception) { }
            serving = null;
            Step = PhoneStep.Off;
        }

        // a click on the line: the step's own way forward
        public void Act()
        {
            switch (step)
            {
                case PhoneStep.Installer:
                    if (installer != null) Open(installer);
                    break;
                case PhoneStep.Opening:
                    OpenTailscale();
                    break;
                case PhoneStep.SignIn:
                    SignIn();
                    break;
                case PhoneStep.Connect:
                    var tool = Tool();
                    if (tool != null) Task.Run(() => Install.RunTool(tool, "up", out _, 30));
                    break;
                case PhoneStep.Https:
                    if (httpsLink != null) Open(httpsLink);
                    break;
                case PhoneStep.Failed:
                    // a download that stopped: Tailscale's own page takes over
                    if (why != null && why.StartsWith("Tailscale's download", StringComparison.Ordinal)) Open("https://tailscale.com/download/windows");
                    else Check();
                    break;
            }
        }

        private static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { Log.Note("phone: " + target + " failed to open, " + e.Message); }
        }

        private void OpenTailscale()
        {
            isAppOpened = true;
            var tool = Tool();
            var app = tool == null ? null : Path.Combine(Path.GetDirectoryName(tool), "tailscale-ipn.exe");
            if (app != null && File.Exists(app)) Open(app);
        }

        // Tailscale's own sign-in link, opened in the browser
        private void SignIn()
        {
            var tool = Tool();
            if (tool == null) return;
            Task.Run(() =>
            {
                var info = new ProcessStartInfo(tool, "login --timeout=10m")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                try
                {
                    var process = Process.Start(info);
                    void Read(string line)
                    {
                        var found = Regex.Match(line ?? "", "https://login\\.tailscale\\.com/[^\\s]+");
                        if (found.Success) Open(found.Value);
                    }
                    process.OutputDataReceived += (s, e) => Read(e.Data);
                    process.ErrorDataReceived += (s, e) => Read(e.Data);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch (Exception e)
                {
                    Log.Note("phone: tailscale login failed to start, " + e.Message);
                }
            });
        }

        private void Post(Action action)
        {
            if (ui != null) ui.Post(_ => action(), null);
            else action();
        }

        private void Check()
        {
            if (timer == null || isChecking) return;
            var tool = Tool();
            if (tool == null)
            {
                if (!isDownloading && installer == null) FetchInstaller();
                return;
            }
            installer = null;
            isChecking = true;
            Task.Run(() =>
            {
                var status = Read(tool);
                Post(() =>
                {
                    isChecking = false;
                    if (timer == null) return;
                    DnsName = status.Item2;
                    if (status.Item2.Length > 0) NameFound?.Invoke(status.Item2);
                    if (status.Item3.Length > 0) LoginFound?.Invoke(status.Item3);
                    Advance(tool, status.Item1, status.Item4, status.Item5);
                });
            });
        }

        // the backend's state, the PC's name on the tailnet, its login, whether HTTPS certificates are on, and whether
        // Serve forwards to the relay
        private static Tuple<string, string, string, bool, bool> Read(string tool)
        {
            Install.RunTool(tool, "status --json", out var text, 8);
            var start = text.IndexOf('{');
            var status = start < 0 ? null : Json.ParseObject(text.Substring(start));
            var me = status?.Obj("Self");
            var name = (me?.Str("DNSName") ?? "").TrimEnd('.');
            var user = me?["UserID"]?.ToString() ?? "";
            var login = status?.Obj("User")?.Obj(user)?.Str("LoginName") ?? "";
            var https = (status?.Arr("CertDomains")?.Count ?? 0) > 0;
            Install.RunTool(tool, "serve status --json", out var serve, 8);
            var isServing = serve.Contains("127.0.0.1:" + RelayPort) || serve.Contains("localhost:" + RelayPort);
            return Tuple.Create(status?.Str("BackendState") ?? "", name, login, https, isServing);
        }

        private void Advance(string tool, string state, bool hasHttps, bool isServing)
        {
            switch (state)
            {
                case "Running":
                    if (isServing && hasHttps) Step = PhoneStep.Ready;
                    else if (serving == null) Serve(tool);
                    break;
                case "NeedsLogin":
                case "NeedsMachineAuth":
                    Step = PhoneStep.SignIn;
                    break;
                case "Stopped":
                    Step = PhoneStep.Connect;
                    break;
                case "":
                    // the service unreachable: Tailscale's app has yet to start
                    if (!isAppOpened) OpenTailscale();
                    Step = PhoneStep.Opening;
                    break;
                default:
                    Step = PhoneStep.Opening;
                    break;
            }
        }

        // Serve for the relay; a tailnet with HTTPS off answers with Tailscale's own link to turn it on and waits, and the
        // command finishes by itself once it is on
        private void Serve(string tool)
        {
            Step = PhoneStep.Serving;
            var said = new System.Text.StringBuilder();
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(tool, "serve --bg " + RelayPort)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    },
                    EnableRaisingEvents = true,
                };
                void Read(string line)
                {
                    if (line == null) return;
                    Post(() =>
                    {
                        said.AppendLine(line);
                        var found = Regex.Match(said.ToString(), "https://login\\.tailscale\\.com/[^\\s]+");
                        if (!found.Success) return;
                        httpsLink = found.Value;
                        Step = PhoneStep.Https;
                    });
                }
                process.OutputDataReceived += (s, e) => Read(e.Data);
                process.ErrorDataReceived += (s, e) => Read(e.Data);
                process.Exited += (s, e) => Post(() =>
                {
                    var code = process.ExitCode;
                    serving = null;
                    if (code != 0 && timer != null)
                    {
                        var lines = said.ToString().Trim().Split('\n');
                        why = "Tailscale Serve: " + Words.Prefix(lines[lines.Length - 1].Trim(), 60);
                        Step = PhoneStep.Failed;
                    }
                    Check();
                });
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                serving = process;
            }
            catch (Exception)
            {
                why = "Tailscale Serve would not start";
                Step = PhoneStep.Failed;
            }
        }

        // Tailscale's latest installer from tailscale.com, opened once Windows confirms its signature as Tailscale Inc.'s
        private void FetchInstaller()
        {
            isDownloading = true;
            Step = PhoneStep.Downloading;
            Task.Run(() =>
            {
                string saved = null;
                try
                {
                    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                    {
                        var bytes = client.GetByteArrayAsync("https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe").GetAwaiter().GetResult();
                        var target = Path.Combine(Path.GetTempPath(), "tailscale-setup-latest.exe");
                        File.WriteAllBytes(target, bytes);
                        if (IsTailscaleInstaller(target)) saved = target;
                        else Log.Note("phone: the Tailscale installer's signature failed its check");
                    }
                }
                catch (Exception e)
                {
                    Log.Note("phone: Tailscale's download stopped, " + e.Message);
                }
                Post(() =>
                {
                    isDownloading = false;
                    if (timer == null) return;
                    if (saved == null)
                    {
                        why = "Tailscale's download stopped; click for its own page";
                        Step = PhoneStep.Failed;
                        return;
                    }
                    installer = saved;
                    Step = PhoneStep.Installer;
                    Open(saved);
                });
            });
        }

        // Windows' own check of the file's Authenticode signature, then its signer: Tailscale Inc.
        private static bool IsTailscaleInstaller(string path)
        {
            if (!Trust.Verify(path)) return false;
            try
            {
                var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                return signer.GetNameInfo(X509NameType.SimpleName, false).StartsWith("Tailscale Inc", StringComparison.Ordinal)
                    || signer.Subject.Contains("O=Tailscale Inc");
            }
            catch (Exception) { return false; }
        }

        private static class Trust
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct FileInfo
            {
                public int Size;
                public string Path;
                public IntPtr File;
                public IntPtr Subject;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Data
            {
                public int Size;
                public IntPtr Policy, SipData;
                public int UiChoice, RevocationChecks, UnionChoice;
                public IntPtr File;
                public int StateAction;
                public IntPtr State;
                public IntPtr Url;
                public int ProviderFlags, UiContext;
                public IntPtr SignatureSettings;
            }

            [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
            private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref Data data);

            private static readonly Guid genericVerify = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            public static bool Verify(string path)
            {
                var file = new FileInfo { Size = Marshal.SizeOf(typeof(FileInfo)), Path = path };
                var pinned = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
                try
                {
                    Marshal.StructureToPtr(file, pinned, false);
                    var data = new Data
                    {
                        Size = Marshal.SizeOf(typeof(Data)), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1, File = pinned,
                        StateAction = 0, ProviderFlags = 0x1000,
                    };
                    var action = genericVerify;
                    return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
                }
                finally
                {
                    Marshal.DestroyStructure(pinned, typeof(FileInfo));
                    Marshal.FreeHGlobal(pinned);
                }
            }
        }
    }
}
