using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SkiaSharp;

namespace Uselim
{
    // The phone side on Linux: the core's relay on a loop of its own, what it asks of Linux (RelayHost below), and the
    // Tailscale steps. Tailscale on Linux installs and grants its operator right through sudo, which only the person can
    // type, so those two steps open a terminal with the command in it; every other step runs from the panel's line.

    internal enum PhoneStep { Off, Missing, Operator, SignIn, Connect, Serving, Https, Ready, Failed }

    internal sealed class Phone
    {
        public const int RelayPort = 47472;
        private readonly Controller owner;
        private readonly Loop loop = new Loop("Uselim relay");
        private readonly RelayHost host = new RelayHost();
        public readonly Relay Relay;
        private DispatcherTimer timer;
        private bool isChecking;
        private Process serving;
        private string why, link;
        public string DnsName = "";
        public PhoneStep Step = PhoneStep.Off;
        public Action Changed;

        public Phone(Controller owner)
        {
            this.owner = owner;
            Relay = new Relay(new RelaySettings
            {
                Dir = Paths.RelayDir, Port = RelayPort,
                TailnetName = Prefs.Str("tailnetName") ?? "", TailnetLogin = Prefs.Str("tailnetLogin") ?? "",
            }, host, loop);
        }

        // off until turned on, and on by itself where a phone already subscribed
        public static bool IsOnSaved() => Prefs.FlagOrNull("phone") ?? File.Exists(Path.Combine(Paths.RelayDir, "push.json"));
        public bool IsOn => IsOnSaved();
        public bool CanPair => IsOn && Step == PhoneStep.Ready && DnsName.Length > 0;

        public void Snapshot(List<Limit> limits, DateTime? at) => host.Snapshot = Tuple.Create(limits, at);
        public void LimitNotice(string title, string body) => Relay.LimitNotice(title, body);
        public string ChatName(string session) => IsOn ? loop.Invoke(() => Relay.TitleOf(session)) : null;

        public void Set(bool on)
        {
            Prefs.Set("phone", on);
            if (on) Start();
            else Stop();
        }

        public void Start()
        {
            if (timer != null) return;
            loop.Post(Relay.Start);
            if (Install.IsInstalledCopy) Task.Run(Install.EnsurePlugin);
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) => Check();
            timer.Start();
            Check();
            _ = WatchIdle();
            Log.Note("phone: on");
        }

        public void Stop()
        {
            if (timer == null) return;
            timer.Stop();
            timer = null;
            loop.Post(Relay.Stop);
            try { if (serving != null && !serving.HasExited) serving.Kill(); } catch (Exception) { }
            serving = null;
            Step = PhoneStep.Off;
            Log.Note("phone: off");
        }

        // the screen's lock and the idle time, read every ten seconds for the relay's away reading
        private async Task WatchIdle()
        {
            while (timer != null)
            {
                host.Idle = await Desktop.IdleSeconds() ?? 0;
                host.IsLocked = await Desktop.IsLocked();
                await Task.Delay(10_000);
            }
        }

        public string Line
        {
            get
            {
                switch (Step)
                {
                    case PhoneStep.Missing: return "Install Tailscale (opens a terminal)";
                    case PhoneStep.Operator: return "Let Uselim run Tailscale Serve (opens a terminal)";
                    case PhoneStep.SignIn: return "Sign in to Tailscale";
                    case PhoneStep.Connect: return "Turn Tailscale on";
                    case PhoneStep.Serving: return "Starting the relay on Tailscale…";
                    case PhoneStep.Https: return "Turn on HTTPS in Tailscale";
                    case PhoneStep.Failed: return why;
                    case PhoneStep.Ready: return "Ready at " + DnsName;
                    default: return null;
                }
            }
        }

        public static string Tool()
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Concat(new[] { "/usr/bin", "/usr/local/bin" }))
            {
                var path = Path.Combine(folder, "tailscale");
                if (folder.Length > 0 && File.Exists(path)) return path;
            }
            return null;
        }

        // a click on the line: the step's own way forward
        public void Act()
        {
            var tool = Tool();
            switch (Step)
            {
                case PhoneStep.Missing:
                    Terminal("curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale set --operator=$USER && sudo tailscale up --operator=$USER");
                    break;
                case PhoneStep.Operator:
                    Terminal("sudo tailscale set --operator=$USER");
                    break;
                case PhoneStep.SignIn:
                case PhoneStep.Connect:
                    if (tool != null) SignIn(tool);
                    break;
                case PhoneStep.Https:
                    if (link != null) Open(link);
                    break;
                case PhoneStep.Failed:
                    why = null;
                    Check();
                    break;
            }
        }

        // a terminal with the command typed out and run, its window left open to read
        private static void Terminal(string command)
        {
            var script = command + "; echo; echo 'Done. This window can close.'; read _";
            foreach (var t in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xfce4-terminal", "-x"), ("xterm", "-e") })
            {
                try
                {
                    Process.Start(new ProcessStartInfo(t.Item1) { UseShellExecute = false, ArgumentList = { t.Item2, "sh", "-c", script } });
                    return;
                }
                catch (Exception) { }
            }
            Log.Note("phone: no terminal found for: " + command);
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
            catch (Exception e) { Log.Note("open: " + e.Message); }
        }

        // tailscale up prints the sign-in link, opened in the browser
        private void SignIn(string tool)
        {
            Task.Run(() =>
            {
                try
                {
                    var process = Process.Start(new ProcessStartInfo(tool, "up --timeout=300s")
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                    var said = new StringBuilder();
                    var opened = false;
                    void Read(string line)
                    {
                        if (line == null || opened) return;
                        lock (said) said.AppendLine(line);
                        var found = Regex.Match(line, "https://login\\.tailscale\\.com/[^\\s]+");
                        if (!found.Success) return;
                        opened = true;
                        Open(found.Value);
                    }
                    process.OutputDataReceived += (s, e) => Read(e.Data);
                    process.ErrorDataReceived += (s, e) => Read(e.Data);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch (Exception e)
                {
                    Log.Note("phone: tailscale up failed to start, " + e.Message);
                }
            });
        }

        private void Check()
        {
            if (timer == null || isChecking) return;
            var tool = Tool();
            if (tool == null)
            {
                Move(PhoneStep.Missing);
                return;
            }
            isChecking = true;
            Task.Run(() =>
            {
                var status = Read(tool);
                Dispatcher.UIThread.Post(() =>
                {
                    isChecking = false;
                    if (timer == null) return;
                    DnsName = status.Name;
                    if (status.Name.Length > 0)
                    {
                        Prefs.Set("tailnetName", status.Name);
                        loop.Post(() => Relay.TailnetName = status.Name);
                    }
                    if (status.Login.Length > 0)
                    {
                        Prefs.Set("tailnetLogin", status.Login);
                        loop.Post(() => Relay.TailnetLogin = status.Login);
                    }
                    Advance(tool, status.State, status.Https, status.Serving);
                });
            });
        }

        private static (string State, string Name, string Login, bool Https, bool Serving) Read(string tool)
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
            return (status?.Str("BackendState") ?? "", name, login, https, isServing);
        }

        private void Advance(string tool, string state, bool hasHttps, bool isServing)
        {
            if (Step == PhoneStep.Failed || Step == PhoneStep.Operator) { if (state != "Running" || !isServing) return; }
            switch (state)
            {
                case "Running":
                    if (isServing && hasHttps) Move(PhoneStep.Ready);
                    else if (serving == null && Step != PhoneStep.Https) Serve(tool);
                    break;
                case "NeedsLogin":
                case "NeedsMachineAuth":
                    Move(PhoneStep.SignIn);
                    break;
                case "Stopped":
                    Move(PhoneStep.Connect);
                    break;
                default:
                    why = "Tailscale's service is off: sudo systemctl start tailscaled";
                    Move(PhoneStep.Failed);
                    break;
            }
        }

        private void Move(PhoneStep next)
        {
            if (Step == next) return;
            Step = next;
            Changed?.Invoke();
        }

        // Serve for the relay; a tailnet with HTTPS off answers with Tailscale's link to turn it on and waits; a person
        // without the operator right is refused, and the line offers the terminal step that grants it
        private void Serve(string tool)
        {
            Move(PhoneStep.Serving);
            var said = new StringBuilder();
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(tool, "serve --bg " + RelayPort)
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    },
                    EnableRaisingEvents = true,
                };
                void ReadLine(string line)
                {
                    if (line == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        said.AppendLine(line);
                        var found = Regex.Match(said.ToString(), "https://login\\.tailscale\\.com/[^\\s]+");
                        if (!found.Success) return;
                        link = found.Value;
                        Move(PhoneStep.Https);
                    });
                }
                process.OutputDataReceived += (s, e) => ReadLine(e.Data);
                process.ErrorDataReceived += (s, e) => ReadLine(e.Data);
                process.Exited += (s, e) => Dispatcher.UIThread.Post(() =>
                {
                    var code = process.ExitCode;
                    serving = null;
                    if (code != 0 && timer != null)
                    {
                        var text = said.ToString();
                        if (text.Contains("Access denied") || text.Contains("operator"))
                        {
                            Move(PhoneStep.Operator);
                            return;
                        }
                        var lines = text.Trim().Split('\n');
                        why = "Tailscale Serve: " + lines[lines.Length - 1].Trim();
                        Move(PhoneStep.Failed);
                    }
                });
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                serving = process;
            }
            catch (Exception)
            {
                why = "Tailscale Serve would not start";
                Move(PhoneStep.Failed);
            }
        }

        // Pair a phone: the six digits the relay opens, its time left, the paired phones
        public string OpenPairing() => loop.Invoke(Relay.OpenPairing);
        public void ClosePairing() => loop.Post(Relay.ClosePairing);
        public int PairSecondsLeft => loop.Invoke(() => Relay.PairCode == null ? 0 : Relay.PairSecondsLeft);
        public List<Relay.Phone> Phones => loop.Invoke(() => Relay.Phones);
        public void RemovePhone(string id) => loop.Post(() => Relay.RemovePhone(id));
    }

    // What the relay asks of Linux: the limits the controller holds, the idle time and lock, links into VS Code, the
    // phone app's files from inside the program, and pictures fitted through Skia, which Avalonia carries.
    internal sealed class RelayHost : IRelayHost
    {
        public volatile Tuple<List<Limit>, DateTime?> Snapshot = Tuple.Create(new List<Limit>(), (DateTime?)null);
        public volatile bool IsLocked;
        private double idle;
        public double Idle { set => Volatile.Write(ref idle, value); }

        public List<Limit> Limits(out DateTime? at)
        {
            var held = Snapshot;
            at = held.Item2;
            return held.Item1;
        }

        public double IdleSeconds(out bool locked)
        {
            locked = IsLocked;
            return Volatile.Read(ref idle);
        }

        // the extension's own link: VS Code opens the chat, or a fresh one
        public void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
            catch (Exception e) { Log.Note("relay: the link failed to open, " + e.Message); }
        }

        public byte[] WebFile(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/" + name))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        public void Saved(string name, string value) => Prefs.Set(name, value);

        // A picture is fitted the way the Mac and Windows fit one: the long side at most the size given and the area two
        // thirds of that square; one the Claude Code panel takes as it is (JPEG, PNG, GIF) that fits already goes byte for
        // byte, every other one becomes a JPEG, or a PNG where it carries transparency. Data null: the file goes as it is.
        public Fitted FitFile(string path, int longSide)
        {
            try { return Fit(File.ReadAllBytes(path), longSide); }
            catch (Exception) { return null; }
        }

        public Fitted FitBytes(byte[] data, int longSide)
        {
            try
            {
                var fitted = Fit(data, longSide);
                if (fitted != null && fitted.Data == null) fitted.Data = data;
                return fitted;
            }
            catch (Exception) { return null; }
        }

        private static Fitted Fit(byte[] data, int longSide)
        {
            using (var codec = SKCodec.Create(new MemoryStream(data)))
            {
                if (codec == null) return null;
                var format = codec.EncodedFormat;
                string ext = format == SKEncodedImageFormat.Jpeg ? "jpg" : format == SKEncodedImageFormat.Png ? "png" : format == SKEncodedImageFormat.Gif ? "gif" : null;
                var origin = codec.EncodedOrigin;
                double w = codec.Info.Width, h = codec.Info.Height;
                if (w <= 0 || h <= 0) return null;
                var scale = Math.Min(1, Math.Min(longSide / Math.Max(w, h), Math.Sqrt(0.667 * longSide * longSide / (w * h))));
                if (scale >= 1 && origin == SKEncodedOrigin.TopLeft && ext != null) return new Fitted { Data = null, Ext = ext };
                using (var bitmap = SKBitmap.Decode(codec))
                {
                    if (bitmap == null) return null;
                    var isClear = format == SKEncodedImageFormat.Png || format == SKEncodedImageFormat.Gif || bitmap.AlphaType != SKAlphaType.Opaque;
                    using (var upright = Upright(bitmap, origin))
                    {
                        var tw = Math.Max(1, (int)Math.Round(upright.Width * scale));
                        var th = Math.Max(1, (int)Math.Round(upright.Height * scale));
                        using (var fitted = upright.Resize(new SKImageInfo(tw, th, SKColorType.Rgba8888, isClear ? SKAlphaType.Premul : SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell)))
                        using (var image = SKImage.FromBitmap(fitted))
                        using (var encoded = image.Encode(isClear ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 85))
                            return new Fitted { Data = encoded.ToArray(), Ext = isClear ? "png" : "jpg" };
                    }
                }
            }
        }

        // the picture turned as its camera's orientation says
        private static SKBitmap Upright(SKBitmap source, SKEncodedOrigin origin)
        {
            if (origin == SKEncodedOrigin.TopLeft) return source.Copy();
            var swap = origin == SKEncodedOrigin.LeftTop || origin == SKEncodedOrigin.RightTop || origin == SKEncodedOrigin.RightBottom || origin == SKEncodedOrigin.LeftBottom;
            var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
            using (var canvas = new SKCanvas(result))
            {
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, result.Width / 2f, 0); break;
                    case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, result.Width / 2f, result.Height / 2f); break;
                    case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, result.Height / 2f); break;
                    case SKEncodedOrigin.RightTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); break;
                    case SKEncodedOrigin.LeftBottom: canvas.Translate(0, result.Height); canvas.RotateDegrees(270); break;
                    // the two mirrored turns: across the main diagonal, and across the other one
                    case SKEncodedOrigin.LeftTop: canvas.SetMatrix(new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1)); break;
                    case SKEncodedOrigin.RightBottom: canvas.SetMatrix(new SKMatrix(0, -1, source.Height, -1, 0, source.Width, 0, 0, 1)); break;
                }
                canvas.DrawBitmap(source, 0, 0);
            }
            return result;
        }
    }
}
