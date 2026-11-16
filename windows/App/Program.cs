using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Uselim
{
    // Uselim for Windows: Claude's usage limits in the tray, read through the Claude Code login.
    // "--hook" takes one Claude Code event on stdin and leaves; "--statusline" takes one status line update on stdin,
    // keeps its figures and prints the line; "--setup", "--remove", "--state" and "--quit" set up, remove, report and
    // stop the app; "--status" prints the figures; "--login on" or "off" sets Open at login; "--demo 85,92,40" shows
    // given shares in place of the real ones, from wherever the exe sits. Started from anywhere else than its own
    // folder, the exe sets itself up there and starts that copy.

    internal static class Program
    {
        private static Mutex single;
        private static bool holding;

        [STAThread]
        private static int Main(string[] args)
        {
            var a = args.ToList();
            if (a.Contains("--hook"))
            {
                Waiting.RunHook(Stdio.ReadIn(), DateTime.UtcNow);
                return 0;
            }
            if (a.Contains("--statusline"))
            {
                Stdio.Write(StatusLine.Take(Stdio.ReadIn(), DateTime.UtcNow) + "\n");
                return 0;
            }
            if (a.Contains("--setup")) return Install.Setup();
            if (a.Contains("--remove")) return Install.Remove();
            if (a.Contains("--state"))
            {
                Install.PrintState();
                return 0;
            }
            if (a.Contains("--quit")) return Install.QuitRunning() ? 0 : 1;
            if (a.Contains("--status"))
            {
                PrintStatus();
                return 0;
            }
            var loginAt = a.IndexOf("--login");
            if (loginAt >= 0)
            {
                Stdio.Attach();
                if (loginAt + 1 < a.Count) Install.OpenAtLogin = a[loginAt + 1] == "on";
                Stdio.Line("open at login: " + (Install.OpenAtLogin ? "on" : "off"));
                return 0;
            }
            var demoAt = a.IndexOf("--demo");
            var demo = demoAt >= 0 && demoAt + 1 < a.Count
                ? a[demoAt + 1].Split(',').Select(t => int.TryParse(t, out var n) ? n : -1).Where(n => n >= 0).ToArray()
                : new int[0];

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // an error on the UI thread goes to the log, and the app carries on
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Note("error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Note("fatal: " + e.ExceptionObject);

            // a double-click on a downloaded copy sets Uselim up in its own folder and starts that copy
            if (demo.Length == 0 && !Install.IsInstalledCopy && !a.Contains("--here")) return Install.FirstRun();

            single = new Mutex(false, Install.MutexName);
            try
            {
                // a restart or an update starts the next copy while this one is still leaving
                holding = single.WaitOne(a.Contains("--wait") ? 10_000 : 0);
            }
            catch (AbandonedMutexException)
            {
                holding = true;
            }
            if (!holding) return 0;
            if (Install.IsInstalledCopy) Updater.Tidy();
            Application.Run(new Tray(demo));
            ReleaseSingle();
            return 0;
        }

        public static void ReleaseSingle()
        {
            if (!holding) return;
            holding = false;
            try { single.ReleaseMutex(); } catch (Exception) { }
        }

        // --status: one read, printed
        private static void PrintStatus()
        {
            Stdio.Attach();
            var login = Usage.ReadLogin();
            if (login == null)
            {
                Stdio.Line("login: unavailable (" + Paths.CredentialsPath + ")");
                return;
            }
            var result = Usage.FetchUsage(login);
            var now = DateTime.UtcNow;
            switch (result.Kind)
            {
                case FetchKind.Limits:
                    foreach (var l in Usage.Ordered(result.Limits))
                    {
                        var when = l.Resets.HasValue ? Spans.ResetText(l.Resets.Value, now) + " (in " + Spans.Exact(l.Resets.Value, now) + ")" : "reset unknown";
                        Stdio.Line(l.Name + ": " + l.Percent + "% " + l.Severity + ", " + when);
                    }
                    break;
                case FetchKind.SignedOut: Stdio.Line("signed out"); break;
                case FetchKind.Busy: Stdio.Line("failed: HTTP 429" + (result.RetryAfter.HasValue ? ", retry after " + (int)result.RetryAfter.Value + " s" : "")); break;
                default: Stdio.Line("failed: " + result.Why); break;
            }
        }
    }
}
