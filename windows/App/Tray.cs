using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Uselim
{
    // What the panel shows while figures are missing
    internal enum Shown { Waiting, Limits, SignedOut, Expired, Missing, Failed, Paused }

    // The readout on the taskbar, the tray icon, the panel and the minute's refresh, the Mac's Controller on Windows.
    // Figures arrive on a background task and land on the UI thread. The chats waiting on an answer sit at the top of the
    // panel, with a dot on the readout and the icon.
    internal sealed class Tray : ApplicationContext
    {
        private readonly NotifyIcon icon = new NotifyIcon();
        private readonly Control ui = new Control();
        private readonly Flyout panel;
        private readonly TaskbarReadout readout;
        // the phone side: the relay on a loop of its own, what it asks of Windows, the Tailscale steps, Pair a phone
        private readonly Loop relayLoop = new Loop("Uselim relay");
        private readonly RelayHost relayHost = new RelayHost();
        private readonly Relay relay;
        private readonly PhoneSetup phoneSetup;
        private PairWindow pairWindow;
        private readonly int[] demo;

        public List<Limit> Limits = new List<Limit>();
        public DateTime? Fetched;
        public Shown State = Shown.Waiting;
        private string failedWhy;
        private Login login;
        private bool reading;

        // the reads' pacing: the usage server answers about twenty-five reads an hour, then refuses with 429 for a while,
        // so a read waits five minutes after the last one (a minute while zero figures show), and a 429 pauses the
        // reading fifteen minutes, doubling to an hour while refusals go on; all of it carries over a restart
        private const double ReadGap = 300;
        private DateTime lastReadAt = DateTime.MinValue;
        private DateTime holdUntil = DateTime.MinValue;
        private double pause;
        private DateTime? lastPush;

        public List<Wait> Waits = new List<Wait>();
        private Dictionary<string, bool> seen = new Dictionary<string, bool>();
        private readonly Alerts alerts = new Alerts();
        public readonly Updater Updater = new Updater();
        public string UpdateLine;

        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 60_000 };
        private readonly System.Windows.Forms.Timer updates = new System.Windows.Forms.Timer { Interval = 6 * 3600_000 };
        private readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private EventWaitHandle quit;
        private RegisteredWaitHandle quitWait;
        private IntPtr iconHandle = IntPtr.Zero;
        private bool waitingChanged, pushChanged;

        public Tray(int[] demo)
        {
            this.demo = demo ?? new int[0];
            var forced = ui.Handle;
            panel = new Flyout(this);

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
            else if (holdUntil > DateTime.UtcNow)
            {
                State = Shown.Paused;
            }

            icon.Text = "Uselim";
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
            icon.BalloonTipClicked += (s, e) => ShowPanel();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Uselim", null, (s, e) => ShowPanel());
            menu.Items.Add("Read again", null, (s, e) => Retry());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Restart", null, (s, e) => Restart());
            menu.Items.Add("Quit", null, (s, e) => ExitThread());
            icon.ContextMenuStrip = menu;
            readout = new TaskbarReadout(this);
            phoneSetup = new PhoneSetup();
            relay = new Relay(new RelaySettings
            {
                Dir = Paths.RelayDir, Port = PhoneSetup.RelayPort,
                TailnetName = Prefs.Str("tailnetName") ?? "", TailnetLogin = Prefs.Str("tailnetLogin") ?? "",
            }, relayHost, relayLoop);
            phoneSetup.Changed = () => panel.Redraw();
            phoneSetup.NameFound = name => relay.TailnetName = name;
            phoneSetup.LoginFound = login => relay.TailnetLogin = login;
            Draw();
            icon.Visible = true;
            if (OnTaskbar) readout.Start();
            // after an update, and for copies set up before the Apps entry existed: the hooks and the entry's version current
            if (this.demo.Length == 0 && Install.IsInstalledCopy) Task.Run(() => { Install.RefreshHooks(); Install.WriteAppsEntry(); });
            if (this.demo.Length == 0 && IsPhoneOn()) StartPhone();

            Watch(Paths.WaitingDir, name => true, () => waitingChanged = true);
            Watch(Paths.SupportDir, name => string.Equals(name, "limits.json", StringComparison.OrdinalIgnoreCase), () => pushChanged = true);
            settle.Tick += (s, e) =>
            {
                settle.Stop();
                if (waitingChanged) { waitingChanged = false; ReloadWaiting(true); }
                if (pushChanged) { pushChanged = false; TakePushed(); }
            };
            ReloadWaiting(false);
            TakePushed();
            Refresh();
            // the readout's whole minutes move on each minute
            poll.Tick += (s, e) => { Draw(); Refresh(); };
            poll.Start();

            SystemEvents.UserPreferenceChanged += ThemeChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionSwitched;

            // "Uselim.exe --quit" and a setup's update ask this copy to leave through a named event
            quit = new EventWaitHandle(false, EventResetMode.AutoReset, Install.QuitName);
            quitWait = ThreadPool.RegisterWaitForSingleObject(quit, (o, t) => ui.BeginInvoke((Action)ExitThread), null, -1, true);

            if (this.demo.Length == 0)
            {
                Updater.Changed = () => ui.BeginInvoke((Action)(() => panel.Redraw()));
                Updater.Check();
                updates.Tick += (s, e) => Updater.Check();
                updates.Start();
            }
            Welcome();
            Log.Note("started " + Usage.Version);
        }

        // the first start of the installed copy: a notice says where Uselim sits. With the readout on the taskbar, that
        // is all; with the readout hidden, the taskbar settings open too, since Windows 11 tucks a new tray icon behind the
        // arrow and one switch there pins it beside the clock
        private void Welcome()
        {
            if (Prefs.Flag("welcomed") || !Install.IsInstalledCopy) return;
            Prefs.Set("welcomed", true);
            if (readout.IsShown)
            {
                Notify("Uselim is on the taskbar", "Its two lines sit beside the clock; click them for each limit, its reset and the waiting chats.");
                return;
            }
            Notify("Uselim is in the tray", "Pin it beside the clock: Settings › Taskbar › Other system tray icons › Uselim.");
            try { Process.Start("ms-settings:taskbar"); } catch (Exception) { }
        }

        private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Draw();
            panel.Redraw();
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Refresh();
        }

        // the screen locked counts as away for the phone, as on the Mac
        private void SessionSwitched(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect) relayHost.IsLocked = true;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect) relayHost.IsLocked = false;
        }

        // The phone

        // off until turned on, and on by itself where a phone already subscribed
        public static bool IsPhoneOn() => Prefs.FlagOrNull("phone") ?? File.Exists(Path.Combine(Paths.RelayDir, "push.json"));

        public bool PhoneOn => IsPhoneOn();
        public string PhoneLine => PhoneOn ? phoneSetup.Line : null;
        public bool CanPair => PhoneOn && phoneSetup.Step == PhoneStep.Ready && phoneSetup.DnsName.Length > 0;
        public void PhoneAct() => phoneSetup.Act();

        public void SetPhone(bool on)
        {
            Prefs.Set("phone", on);
            if (on) StartPhone();
            else StopPhone();
            panel.Redraw();
        }

        private void StartPhone()
        {
            relayLoop.Post(relay.Start);
            phoneSetup.Start();
            if (Install.IsInstalledCopy) Task.Run(() => Install.EnsurePlugin());
            Log.Note("phone: on");
        }

        private void StopPhone()
        {
            relayLoop.Post(relay.Stop);
            phoneSetup.Stop();
            pairWindow?.Close();
            Log.Note("phone: off");
        }

        public void OpenPair()
        {
            panel.Hide();
            if (pairWindow != null && !pairWindow.IsDisposed)
            {
                pairWindow.Activate();
                return;
            }
            pairWindow = new PairWindow(relay, relayLoop, "https://" + phoneSetup.DnsName);
            pairWindow.FormClosed += (o, e) => pairWindow = null;
            pairWindow.Show();
            pairWindow.Activate();
        }

        // the limit notices, on the PC and, while the phone side is on, in the phone's log and its notifications
        private void Tell(List<string[]> notices)
        {
            foreach (var n in notices)
            {
                Notify(n[0], n[1]);
                if (PhoneOn) relay.LimitNotice(n[0], n[1]);
            }
        }

        // Reading

        // a read, when the pacing allows it: past the gap since the last one, outside a 429's pause, and while the status
        // line's figures are older than five minutes (they cover the session and the week; the per-model lines wait a
        // quarter of an hour at most). The reload by hand goes at once.
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
                ui.BeginInvoke((Action)(() => Took(found, result)));
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
                    // an expired token waits for Claude Code to renew it; a refused one means signed out
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
            Prefs.Set("readAt", Spans.ToEpoch(lastReadAt < new DateTime(1971, 1, 1) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) : lastReadAt));
            Prefs.Set("pausedUntil", Spans.ToEpoch(holdUntil < new DateTime(1971, 1, 1) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) : holdUntil));
            Prefs.Set("pause", pause);
        }

        // demo figures: the session resets in two hours, the week in nine
        private List<Limit> DemoLimits()
        {
            var now = DateTime.UtcNow;
            var kinds = new[] { new object[] { "session", "Session (5 hr)", 2.0 }, new object[] { "weekly_all", "Weekly (7 days)", 9.0 }, new object[] { "weekly_scoped", "Fable (weekly)", 9.0 } };
            return kinds.Take(Math.Max(1, Math.Min(3, demo.Length))).Select((k, i) => new Limit
            {
                Kind = (string)k[0], Name = (string)k[1], Percent = demo[i], Resets = now.AddHours((double)k[2]),
            }).ToList();
        }

        private void Show(Shown next)
        {
            if (Words(next) != Words(State)) Log.Note("state: " + (Words(next) ?? "limits"));
            State = next;
            Draw();
            panel.Redraw();
        }

        public void Retry() => Refresh(true);

        // the panel's line while figures are missing or a read failed
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

        // figures older than ten minutes, or a login that is out, show in grey; one failed read keeps fresh figures as they are
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

        // the note line shows while figures are missing, or once a failed read leaves them stale
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

        // a folder's entries coming and going, settled over a quarter second, since one write raises several events; the
        // support folder's own log and settings stay out through the name test
        private void Watch(string dir, Func<string, bool> wanted, Action changed)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, SynchronizingObject = ui };
                void Hit(string name)
                {
                    if (!wanted(name ?? "")) return;
                    changed();
                    settle.Stop();
                    settle.Start();
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

        // the status line's figures, when newer than the last ones taken: the session and the week replace their lines,
        // the per-model lines stay from the last read
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

        // The chats waiting on an answer

        private void ReloadWaiting(bool notify)
        {
            Waits = Waiting.Read();
            if (notify)
                foreach (var w in Waits.Where(w => !seen.TryGetValue(w.Session, out var q) || q != w.IsQuestion))
                    Notify(w.Project + (w.IsQuestion ? " asks" : " needs a permission"), w.Detail);
            seen = Waits.GroupBy(w => w.Session).ToDictionary(g => g.Key, g => g.First().IsQuestion);
            Draw();
            panel.Redraw();
        }

        // The icon

        public void Draw()
        {
            relayHost.Snapshot = Tuple.Create(Limits, Fetched);
            var light = Palette.TaskbarLight;
            var dot = Waits.Count > 0;
            var stale = Limits.Count == 0 ? State != Shown.Waiting : Stale;
            using (var bmp = Glyph.TrayIcon(SystemInformation.SmallIconSize.Width, stale, dot, light))
            {
                var handle = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(handle);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = handle;
            }
            icon.Text = Tooltip();

            List<Piece> top, bottom;
            if (Limits.Count == 0)
            {
                // "…" while a read can still bring figures, "off" while the login stands in the way, as on the Mac
                var waiting = State == Shown.Waiting || State == Shown.Failed || State == Shown.Paused;
                top = new List<Piece> { new Piece { Text = waiting ? "…" : "off", Tone = Tone.Normal } };
                bottom = new List<Piece>();
            }
            else Readout.Lines(Limits, DateTime.UtcNow, out top, out bottom);
            readout.Update(top, bottom, Limits.Count > 0 && Stale, dot);
        }

        // the readout on the taskbar, on unless its switch in the panel turned it off
        public bool OnTaskbar => !Prefs.Flag("taskbarOff");

        public void SetOnTaskbar(bool on)
        {
            Prefs.Set("taskbarOff", !on);
            if (on) readout.Start();
            else readout.Stop();
            panel.Redraw();
        }

        public void OpenPanel()
        {
            if (panel.Visible || panel.JustHidden) panel.Hide();
            else ShowPanel();
        }

        public void OpenMenu() => icon.ContextMenuStrip?.Show(Cursor.Position);

        // "Session 17% · 2h", "Weekly 38% · Fri" and the waiting count; Windows keeps 63 characters of it
        private string Tooltip()
        {
            var now = DateTime.UtcNow;
            var lines = new List<string>();
            if (Limits.Count == 0) lines.Add("Uselim: " + (Words(State) ?? "Claude usage limits"));
            foreach (var l in Limits.Where(l => l.Kind == "session" || l.Kind == "weekly_all"))
                lines.Add((l.Kind == "session" ? "Session " : "Weekly ") + l.Percent + "%" + (l.Resets.HasValue ? " · " + Spans.Whole(l.Resets.Value, now) : ""));
            foreach (var l in Limits.Where(l => l.Kind == "weekly_scoped" && l.Percent > 50))
                lines.Add(l.Name.Replace(" (weekly)", "") + " " + l.Percent + "%");
            if (Waits.Count > 0) lines.Add(Waits.Count + " waiting on you");
            var text = string.Join("\n", lines);
            return text.Length > 63 ? text.Substring(0, 62) + "…" : text;
        }

        public void Notify(string title, string body)
        {
            Log.Note("notice: " + title + (string.IsNullOrEmpty(body) ? "" : ", " + body));
            icon.ShowBalloonTip(6000, title, string.IsNullOrEmpty(body) ? title : body, ToolTipIcon.None);
        }

        // The panel

        private void TogglePanel()
        {
            if (panel.Visible || panel.JustHidden) panel.Hide();
            else ShowPanel();
        }

        private void ShowPanel()
        {
            ReloadWaiting(false);
            Refresh();
            Updater.Check();
            panel.ShowNear(Cursor.Position);
        }

        public void SetOpenAtLogin(bool on)
        {
            if (!Install.IsInstalledCopy) return;
            Install.OpenAtLogin = on;
            panel.Redraw();
        }

        public void RunUpdate()
        {
            Updater.Update(line => ui.BeginInvoke((Action)(() =>
            {
                UpdateLine = line;
                panel.Redraw();
            })), () => ui.BeginInvoke((Action)(() =>
            {
                Program.ReleaseSingle();
                ExitThread();
            })));
        }

        public void ConfirmRemove()
        {
            panel.Hide();
            var answer = MessageBox.Show(
                "Uselim's hooks and status line leave Claude Code's settings, with a backup kept; Open at login goes, and Uselim.exe is deleted once Uselim quits.",
                "Remove Uselim?", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            Install.RemoveFromApp();
            ExitThread();
        }

        // a fresh copy opens once this one lets go of its single-copy hold
        public void Restart()
        {
            Log.Note("restart");
            Program.ReleaseSingle();
            try { Process.Start(new ProcessStartInfo(Install.ThisExe, "--wait" + (demo.Length > 0 ? " --demo " + string.Join(",", demo) : "")) { UseShellExecute = false }); }
            catch (Exception e) { Log.Note("restart: " + e.Message); }
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionSwitch -= SessionSwitched;
            pairWindow?.Close();
            relayLoop.Post(relay.Stop);
            phoneSetup.Stop();
            quitWait?.Unregister(null);
            quit?.Dispose();
            foreach (var w in watchers) w.Dispose();
            poll.Stop();
            updates.Stop();
            icon.Visible = false;
            icon.Dispose();
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            readout.Dispose();
            panel.Dispose();
            Log.Note("quit");
            base.ExitThreadCore();
        }
    }
}
