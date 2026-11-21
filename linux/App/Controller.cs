using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Uselim
{
    // What the panel shows while figures are missing
    internal enum Shown { Waiting, Limits, SignedOut, Expired, Missing, Failed, Paused }

    // The Mac's Controller on Linux, the Windows tray's logic as it is: the minute's refresh paced against the usage
    // server, the status line's figures, the chats waiting on an answer, the limit notices. Figures arrive on a background
    // task and land on the UI thread; the tray item and the panel draw from here.
    internal sealed class Controller
    {
        private readonly TrayItem tray = new TrayItem();
        // Cinnamon's own tray, where its applet runs: the label shows there, the standard's bridge drops it
        private readonly XAppIcon xapp = new XAppIcon();
        private bool onXApp;
        private Panel panel;
        public readonly Phone Phone;
        private readonly int[] demo;

        public List<Limit> Limits = new List<Limit>();
        public DateTime? Fetched;
        public Shown State = Shown.Waiting;
        private string failedWhy;
        private Login login;
        private bool reading;

        // the reads' pacing: a read waits five minutes after the last one (a minute while zero figures show), and a 429
        // pauses the reading fifteen minutes, doubling to an hour while refusals go on; all of it carries over a restart
        private const double ReadGap = 300;
        private DateTime lastReadAt = DateTime.MinValue;
        private DateTime holdUntil = DateTime.MinValue;
        private double pause;
        private DateTime? lastPush;

        public List<Wait> Waits = new List<Wait>();
        private Dictionary<string, bool> seen = new Dictionary<string, bool>();
        // each waiting chat's notice, replaced as its ask changes and closed once the chat moves on
        private readonly Dictionary<string, uint> waitNotices = new Dictionary<string, uint>();
        private readonly Alerts alerts = new Alerts();
        public readonly Updater Updater = new Updater();
        public string UpdateLine;

        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        private readonly DispatcherTimer updates = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        private readonly DispatcherTimer settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private bool waitingChanged, pushChanged;

        public Controller(int[] demo)
        {
            this.demo = demo ?? new int[0];
            lastReadAt = Spans.FromEpoch(Prefs.Num("readAt"));
            holdUntil = Spans.FromEpoch(Prefs.Num("pausedUntil"));
            pause = Prefs.Num("pause");
            if (this.demo.Length == 0 && Usage.LoadState(out var at, out var saved))
            {
                Limits = Usage.Ordered(saved);
                Fetched = at;
                if (at > lastReadAt) lastReadAt = at;
                State = Shown.Limits;
                alerts.Seed(Limits);
            }
            else if (holdUntil > DateTime.UtcNow) State = Shown.Paused;
            Phone = new Phone(this);
        }

        public void Start()
        {
            panel = new Panel(this);
            tray.Clicked = (x, y) => Dispatcher.UIThread.Post(() => TogglePanel(x, y));
            xapp.Clicked = tray.Clicked;
            _ = StartTray();
            if (demo.Length == 0 && Install.IsInstalledCopy) Task.Run(Install.RefreshHooks);
            if (demo.Length == 0 && Phone.IsOn) Phone.Start();

            Watch(Paths.WaitingDir, name => true, () => waitingChanged = true);
            Watch(Paths.SupportDir, name => name == "limits.json", () => pushChanged = true);
            settle.Tick += (s, e) =>
            {
                settle.Stop();
                if (waitingChanged) { waitingChanged = false; ReloadWaiting(true); }
                if (pushChanged) { pushChanged = false; TakePushed(); }
            };
            ReloadWaiting(false);
            TakePushed();
            Refresh();
            poll.Tick += (s, e) => { Draw(); Refresh(); };
            poll.Start();

            if (demo.Length == 0)
            {
                Updater.Changed = () => Dispatcher.UIThread.Post(() => panel.Redraw());
                Updater.Check();
                updates.Tick += (s, e) => Updater.Check();
                updates.Start();
            }
            Welcome();
            Log.Note("started " + Usage.Version);
            // "--panel": the panel open from the start, for a check on a desktop whose tray is out of reach
            if (Environment.GetCommandLineArgs().Contains("--panel")) Dispatcher.UIThread.Post(() => TogglePanel(0, 0));
        }

        // the first start of the installed copy: a notice says where Uselim sits
        private void Welcome()
        {
            if (Prefs.Flag("welcomed") || !Install.IsInstalledCopy) return;
            Prefs.Set("welcomed", true);
            Notify("Uselim is in the tray", "Its figures sit beside the clock; click them for each limit, its reset and the waiting chats.");
        }

        // Reading

        // a read, when the pacing allows it: past the gap since the last one, outside a 429's pause, and while the status
        // line's figures are older than five minutes. The reload by hand goes at once.
        public void Refresh(bool byHand = false)
        {
            var now = DateTime.UtcNow;
            if (reading || (!byHand && now < holdUntil)) return;
            var sinceRead = (now - lastReadAt).TotalSeconds;
            var gap = Limits.Count == 0 ? 60 : ReadGap;
            var isPushFresh = lastPush.HasValue && (now - lastPush.Value).TotalSeconds < ReadGap;
            if (!byHand && (sinceRead < gap || (isPushFresh && sinceRead < 900))) return;
            if (demo.Length > 0)
            {
                Take(new Fetch { Kind = FetchKind.Limits, Limits = DemoLimits() }, new Login());
                return;
            }
            lastReadAt = now;
            KeepPacing();
            reading = true;
            Task.Run(() =>
            {
                var found = Usage.ReadLogin();
                var result = found == null ? null : Usage.FetchUsage(found);
                Dispatcher.UIThread.Post(() => Took(found, result));
            });
        }

        private void Took(Login found, Fetch result)
        {
            reading = false;
            if (found == null)
            {
                Show(Shown.Missing);
                return;
            }
            login = found;
            if (!string.IsNullOrEmpty(found.Plan)) Prefs.Set("plan", found.Plan);
            Take(result, found);
        }

        private void Take(Fetch result, Login with)
        {
            var now = DateTime.UtcNow;
            switch (result.Kind)
            {
                case FetchKind.Limits:
                    var found = Usage.Ordered(result.Limits);
                    for (var i = 0; i < found.Count && i < demo.Length; i++) found[i].Percent = demo[i];
                    Limits = found;
                    Fetched = now;
                    pause = 0;
                    KeepPacing();
                    if (demo.Length == 0) Usage.SaveState(Limits, now);
                    Tell(alerts.Check(Limits, now));
                    Show(Shown.Limits);
                    break;
                case FetchKind.SignedOut:
                    Show(with.Expires.HasValue && with.Expires.Value < now ? Shown.Expired : Shown.SignedOut);
                    break;
                case FetchKind.Busy:
                    pause = Math.Min(Math.Max(pause * 2, 900), 3600);
                    holdUntil = now.AddSeconds(Math.Min(Math.Max(result.RetryAfter ?? 0, pause), 3600));
                    KeepPacing();
                    Log.Note("read failed: HTTP 429, paused until " + Spans.Clock(holdUntil));
                    Show(Shown.Paused);
                    break;
                default:
                    Log.Note("read failed: " + result.Why);
                    failedWhy = result.Why;
                    Show(Shown.Failed);
                    break;
            }
        }

        private void KeepPacing()
        {
            if (demo.Length > 0) return;
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Prefs.Set("readAt", Spans.ToEpoch(lastReadAt < epoch.AddYears(1) ? epoch : lastReadAt));
            Prefs.Set("pausedUntil", Spans.ToEpoch(holdUntil < epoch.AddYears(1) ? epoch : holdUntil));
            Prefs.Set("pause", pause);
        }

        // demo figures: the session resets in two hours, the week in nine
        private List<Limit> DemoLimits()
        {
            var now = DateTime.UtcNow;
            var kinds = new[] { ("session", "Session (5 hr)", 2.0), ("weekly_all", "Weekly (7 days)", 9.0), ("weekly_scoped", "Fable (weekly)", 9.0) };
            return kinds.Take(Math.Max(1, Math.Min(3, demo.Length))).Select((k, i) => new Limit
            {
                Kind = k.Item1, Name = k.Item2, Percent = demo[i], Resets = now.AddHours(k.Item3), Severity = "normal",
            }).ToList();
        }

        private void Show(Shown next)
        {
            if (Words(next) != Words(State)) Log.Note("state: " + (Words(next) ?? "limits"));
            State = next;
            Draw();
            panel?.Redraw();
        }

        public void Retry() => Refresh(true);

        public string Words(Shown s)
        {
            switch (s)
            {
                case Shown.Waiting: return "Reading the limits…";
                case Shown.SignedOut:
                case Shown.Missing: return "Sign in to Claude Code to see the limits";
                case Shown.Expired: return "Open Claude Code to renew its login";
                case Shown.Failed: return "Last read: " + failedWhy;
                case Shown.Paused: return "Anthropic paused readings until " + Spans.Clock(holdUntil);
                default: return null;
            }
        }

        // figures older than ten minutes, or a login that is out, show in grey
        public bool Stale
        {
            get
            {
                if (State != Shown.Limits && State != Shown.Failed && State != Shown.Paused) return true;
                return !Fetched.HasValue || (DateTime.UtcNow - Fetched.Value).TotalSeconds > 600;
            }
        }

        public string PlanTitle
        {
            get
            {
                var plan = login?.Plan;
                if (string.IsNullOrEmpty(plan) && (State == Shown.Waiting || State == Shown.Limits || State == Shown.Failed || State == Shown.Paused))
                    plan = Prefs.Str("plan");
                if (string.IsNullOrEmpty(plan)) return null;
                return "Claude " + char.ToUpperInvariant(plan[0]) + plan.Substring(1);
            }
        }

        public string Note
        {
            get
            {
                var words = Words(State);
                if (words == null) return null;
                if ((State == Shown.Failed || State == Shown.Paused) && !Stale) return null;
                return words;
            }
        }

        public bool CanRetry => State != Shown.Limits && State != Shown.Waiting;

        // The files Claude Code's commands leave

        private void Watch(string dir, Func<string, bool> wanted, Action changed)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                void Hit(string name)
                {
                    if (!wanted(name ?? "")) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        changed();
                        settle.Stop();
                        settle.Start();
                    });
                }
                w.Created += (s, e) => Hit(e.Name);
                w.Changed += (s, e) => Hit(e.Name);
                w.Deleted += (s, e) => Hit(e.Name);
                w.Renamed += (s, e) => { Hit(e.Name); Hit(e.OldName); };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception e)
            {
                Log.Note("watch " + dir + ": " + e.Message);
            }
        }

        private void TakePushed()
        {
            if (demo.Length > 0 || !StatusLine.ReadPushed(out var at, out var pushed)) return;
            var now = DateTime.UtcNow;
            if (at <= (lastPush ?? DateTime.MinValue) || at <= (Fetched ?? DateTime.MinValue) || (now - at).TotalSeconds >= 600) return;
            if (!lastPush.HasValue) Log.Note("first push from the status line");
            lastPush = at;
            var merged = Limits.Select(l => l.Copy()).ToList();
            foreach (var p in pushed)
            {
                var i = merged.FindIndex(l => l.Kind == p.Kind);
                if (i >= 0)
                {
                    merged[i].Percent = p.Percent;
                    merged[i].Resets = p.Resets;
                }
                else merged.Add(p);
            }
            Limits = Usage.Ordered(merged);
            Fetched = at;
            Tell(alerts.Check(Limits, now));
            Show(Shown.Limits);
        }

        // The chats waiting on an answer: one notice each, named by the chat, out of the list once the chat moves on

        private void ReloadWaiting(bool notify)
        {
            Waits = Waiting.Read();
            if (notify)
                foreach (var w in Waits.Where(w => !seen.TryGetValue(w.Session, out var q) || q != w.IsQuestion))
                {
                    var name = Phone.ChatName(w.Session) ?? w.Project;
                    var session = w.Session;
                    waitNotices.TryGetValue(session, out var earlier);
                    Desktop.Notify(name + (w.IsQuestion ? " asks" : " needs a permission"), w.Detail, earlier)
                        .ContinueWith(t => Dispatcher.UIThread.Post(() => waitNotices[session] = t.Result));
                }
            var still = new HashSet<string>(Waits.Select(w => w.Session));
            foreach (var gone in waitNotices.Keys.Where(k => !still.Contains(k)).ToList())
            {
                _ = Desktop.CloseNotice(waitNotices[gone]);
                waitNotices.Remove(gone);
            }
            seen = Waits.GroupBy(w => w.Session).ToDictionary(g => g.Key, g => g.First().IsQuestion);
            Draw();
            panel?.Redraw();
        }

        // the limit notices, on the desktop and, while the phone side is on, in the phone's log and its notifications
        private void Tell(List<string[]> notices)
        {
            foreach (var n in notices)
            {
                Notify(n[0], n[1]);
                if (Phone.IsOn) Phone.LimitNotice(n[0], n[1]);
            }
        }

        public void Notify(string title, string body)
        {
            Log.Note("notice: " + title + (string.IsNullOrEmpty(body) ? "" : ", " + body));
            _ = Desktop.Notify(title, body ?? "");
        }

        // The tray

        // "9% · 79%": the session's share, then the week's and each model's past half, as the Mac's bar reads them
        public string Label
        {
            get
            {
                if (Limits.Count == 0) return State == Shown.Waiting || State == Shown.Failed || State == Shown.Paused ? "…" : "off";
                var session = Limits.FirstOrDefault(l => l.Kind == "session");
                var parts = new List<string>();
                if (session != null) parts.Add(session.Percent + "%");
                foreach (var l in Limits.Where(l => l.Kind == "weekly_all" || (l.Kind == "weekly_scoped" && l.Percent > 50)))
                    parts.Add(l.Percent + "%");
                return string.Join(" · ", parts);
            }
        }

        // the icon takes the strongest tone among the figures the label shows
        private Tone IconTone
        {
            get
            {
                var shown = Limits.Where(l => l.Kind == "session" || l.Kind == "weekly_all" || (l.Kind == "weekly_scoped" && l.Percent > 50));
                return shown.Select(Usage.ToneOf).DefaultIfEmpty(Tone.Normal).Max();
            }
        }

        // one place in the tray: the XApp icon where Cinnamon's applet runs, the standard item everywhere else
        private async System.Threading.Tasks.Task StartTray()
        {
            onXApp = await XAppIcon.IsHosted();
            Dispatcher.UIThread.Post(Draw);
            if (onXApp) await xapp.Start();
            else await tray.Start();
        }

        public void Draw()
        {
            Phone.Snapshot(Limits, Fetched);
            var stale = Limits.Count == 0 ? State != Shown.Waiting : Stale;
            var tone = IconTone;
            var dot = Waits.Count > 0;
            if (onXApp)
            {
                var look = tone.ToString().ToLowerInvariant() + (stale ? "-stale" : "") + (dot ? "-dot" : "");
                xapp.Update(Glyph.Draw(48, Glyph.Colour(tone, stale), dot), look, Label, TooltipBody().Length > 0 ? "Uselim\n" + TooltipBody() : "Uselim");
            }
            else tray.Update(Glyph.Icons(tone, stale, dot), Label, "Uselim", TooltipBody());
        }

        // "Session 17% · 2h", "Weekly 38% · Fri" and the waiting count
        private string TooltipBody()
        {
            var now = DateTime.UtcNow;
            var lines = new List<string>();
            if (Limits.Count == 0) lines.Add(Words(State) ?? "Claude usage limits");
            foreach (var l in Limits.Where(l => l.Kind == "session" || l.Kind == "weekly_all"))
                lines.Add((l.Kind == "session" ? "Session " : "Weekly ") + l.Percent + "%" + (l.Resets.HasValue ? " · " + Spans.Whole(l.Resets.Value, now) : ""));
            foreach (var l in Limits.Where(l => l.Kind == "weekly_scoped" && l.Percent > 50))
                lines.Add(l.Name.Replace(" (weekly)", "") + " " + l.Percent + "%");
            if (Waits.Count > 0) lines.Add(Waits.Count + " waiting on you");
            return string.Join("\n", lines);
        }

        // The panel

        private void TogglePanel(int x, int y)
        {
            if (panel.IsVisible)
            {
                panel.Hide();
                return;
            }
            ReloadWaiting(false);
            Refresh();
            Updater.Check();
            panel.ShowNear(x, y);
        }

        public void SetOpenAtLogin(bool on)
        {
            if (!Install.IsInstalledCopy) return;
            Install.OpenAtLogin = on;
            panel.Redraw();
        }

        public void RunUpdate()
        {
            Updater.Update(line => Dispatcher.UIThread.Post(() =>
            {
                UpdateLine = line;
                panel.Redraw();
            }), () => Dispatcher.UIThread.Post(Quit));
        }

        public void Remove()
        {
            panel.Hide();
            Install.RemoveFromApp();
            Quit();
        }

        // a fresh copy opens once this one lets go of app.pid
        public void Restart()
        {
            Log.Note("restart");
            try { Process.Start(new ProcessStartInfo(Install.ThisExe, "--wait" + (demo.Length > 0 ? " --demo " + string.Join(",", demo) : "")) { UseShellExecute = false }); }
            catch (Exception e) { Log.Note("restart: " + e.Message); }
            Quit();
        }

        public void Quit()
        {
            Phone.Stop();
            foreach (var w in watchers) w.Dispose();
            poll.Stop();
            updates.Stop();
            Log.Note("quit");
            Program.Leave();
        }
    }
}
