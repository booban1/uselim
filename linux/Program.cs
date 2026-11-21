using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Uselim
{
    // Uselim for Linux: Claude's usage limits in the tray, read through the Claude Code login, and the chats on the
    // phone. The command forms match the Windows exe's: "--hook" takes one Claude Code event on stdin and leaves;
    // "--statusline" takes one status line update on stdin, keeps its figures and prints the line; "--permission" puts a
    // permission on the phone and prints the phone's answer; "--setup", "--remove", "--state" and "--quit" set up, remove,
    // report and stop the app; "--status" prints the figures; "--login on" or "off" sets Open at login; "--demo 85,92,40"
    // shows given shares. Started from anywhere else than ~/.local/bin/uselim, the file sets itself up there.

    internal static class Program
    {
        private static int[] demo = new int[0];
        private static PosixSignalRegistration term;

        [STAThread]
        private static int Main(string[] args)
        {
            var a = args.ToList();
            if (a.Contains("--hook"))
            {
                Waiting.RunHook(Console.In.ReadToEnd(), DateTime.UtcNow);
                return 0;
            }
            if (a.Contains("--statusline"))
            {
                Console.Write(StatusLine.Take(Console.In.ReadToEnd(), DateTime.UtcNow) + "\n");
                return 0;
            }
            if (a.Contains("--permission"))
            {
                var decision = Permission.Run(Console.In.ReadToEnd(), Paths.RelayDir, Phone.RelayPort, Phone.IsOnSaved());
                if (decision != null) Console.Write(decision + "\n");
                return 0;
            }
            if (a.Contains("--setup")) return Install.Setup(true);
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
                if (loginAt + 1 < a.Count) Install.OpenAtLogin = a[loginAt + 1] == "on";
                Console.WriteLine("open at login: " + (Install.OpenAtLogin ? "on" : "off"));
                return 0;
            }
            var demoAt = a.IndexOf("--demo");
            if (demoAt >= 0 && demoAt + 1 < a.Count)
                demo = a[demoAt + 1].Split(',').Select(t => int.TryParse(t, out var n) ? n : -1).Where(n => n >= 0).ToArray();

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Note("fatal: " + e.ExceptionObject);

            // a downloaded copy sets Uselim up in ~/.local/bin and starts that copy
            if (demo.Length == 0 && !Install.IsInstalledCopy && !a.Contains("--here")) return Install.Setup(true);

            // one copy at a time: a restart or an update starts the next while this one is still leaving
            var waitUntil = DateTime.UtcNow.AddSeconds(a.Contains("--wait") ? 10 : 0);
            while (Install.IsRunning)
            {
                if (DateTime.UtcNow >= waitUntil) return 0;
                Thread.Sleep(200);
            }
            if (demo.Length == 0) Paths.WriteAtomic(Install.PidPath, Environment.ProcessId.ToString());

            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        public static int[] Demo => demo;

        // SIGTERM from "uselim --quit" or a logout closes the app as Quit does
        public static void ListenForTerm(Action quit)
        {
            term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(quit);
            });
        }

        public static void Leave()
        {
            try
            {
                if (Paths.Read(Install.PidPath)?.Trim() == Environment.ProcessId.ToString()) File.Delete(Install.PidPath);
            }
            catch (Exception) { }
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            else Environment.Exit(0);
        }

        private static void PrintStatus()
        {
            var login = Usage.ReadLogin();
            if (login == null)
            {
                Console.WriteLine("login: unavailable (" + Paths.CredentialsPath + ")");
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
                        Console.WriteLine(l.Name + ": " + l.Percent + "% " + l.Severity + ", " + when);
                    }
                    break;
                case FetchKind.SignedOut: Console.WriteLine("signed out"); break;
                case FetchKind.Busy: Console.WriteLine("failed: HTTP 429" + (result.RetryAfter.HasValue ? ", retry after " + (int)result.RetryAfter.Value + " s" : "")); break;
                default: Console.WriteLine("failed: " + result.Why); break;
            }
        }
    }

    internal sealed class App : Application
    {
        private Controller controller;

        public override void Initialize()
        {
            Name = "Uselim";
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            controller = new Controller(Program.Demo);
            controller.Start();
            Program.ListenForTerm(controller.Quit);
            base.OnFrameworkInitializationCompleted();
        }
    }
}
