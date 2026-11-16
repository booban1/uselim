using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace Uselim
{
    // Setup and removal. The app lives in %LOCALAPPDATA%\Programs\Uselim\Uselim.exe, a folder of the person's own, so
    // every step runs with the person's own rights. "--setup" copies this exe there, adds the hooks and the status line to Claude
    // Code's settings (a backup first), turns Open at login on and starts the app; "--remove" takes each back, quits the
    // app and deletes the exe. A double-click on a downloaded Uselim.exe asks first, then runs the setup. Each step prints one JSON line,
    // {"step", "ok", "note"}, and the last one {"done", "ok"}; the exit code is 0 when every step went through, 2 when
    // Claude Code's folder is missing, 1 for any other failure.

    internal static class Install
    {
        public const string MutexName = @"Local\Uselim.App";
        public const string QuitName = @"Local\Uselim.Quit";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static readonly Lazy<string> thisExe = new Lazy<string>(() => Process.GetCurrentProcess().MainModule.FileName);
        public static string ThisExe => thisExe.Value;
        public static bool IsInstalledCopy => Paths.Same(ThisExe, Paths.InstalledExe);
        public static string Command => Settings.CommandFor(Paths.InstalledExe, Paths.Home);

        private static bool allOK = true;
        private static bool printing;

        private static void Step(string name, bool ok, string note = "")
        {
            if (!ok) allOK = false;
            var line = new JObject { ["step"] = name, ["ok"] = ok };
            if (note.Length > 0) line["note"] = note;
            Log.Note("setup " + name + ": " + (ok ? "ok" : "failed") + (note.Length > 0 ? ", " + note : ""));
            if (printing) Stdio.Line(Json.Write(line));
        }

        private static int Finish(bool claudeMissing)
        {
            if (printing) Stdio.Line(Json.Write(new JObject { ["done"] = true, ["ok"] = allOK && !claudeMissing }));
            return claudeMissing ? 2 : allOK ? 0 : 1;
        }

        // Open at login: the Run entry under the person's own key

        public static bool OpenAtLogin
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue("Uselim") is string v && v.Trim('"').Length > 0 && File.Exists(v.Trim('"'));
            }
            set
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) key.SetValue("Uselim", "\"" + Paths.InstalledExe + "\"");
                        else if (key.GetValue("Uselim") != null) key.DeleteValue("Uselim");
                    }
                }
                catch (Exception e)
                {
                    Log.Note("open at login: " + e.Message);
                }
            }
        }

        // The running copy

        public static bool IsRunning
        {
            get
            {
                try
                {
                    using (Mutex.OpenExisting(MutexName)) return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // asks the running app to quit, then ends any copy left after five seconds; hooks' short runs finish alone
        public static bool QuitRunning()
        {
            if (!IsRunning) return true;
            try
            {
                using (var quit = EventWaitHandle.OpenExisting(QuitName)) quit.Set();
            }
            catch (Exception) { }
            for (var i = 0; i < 50 && IsRunning; i++) Thread.Sleep(100);
            if (!IsRunning) return true;
            var self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("Uselim").Where(p => p.Id != self))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch (Exception) { }
            }
            Thread.Sleep(200);
            return !IsRunning;
        }

        // The steps

        // this exe into the app's folder, written fresh so the copy starts clean
        private static void CopySelf()
        {
            if (IsInstalledCopy)
            {
                Step("install", true, "already in place");
                return;
            }
            try
            {
                if (File.Exists(Paths.InstalledExe) && !QuitRunning()) throw new IOException("the running Uselim kept its place");
                Directory.CreateDirectory(Paths.InstallDir);
                var bytes = File.ReadAllBytes(ThisExe);
                var temp = Paths.InstalledExe + ".new";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(Paths.InstalledExe)) File.Delete(Paths.InstalledExe);
                File.Move(temp, Paths.InstalledExe);
                Step("install", true, Paths.InstalledExe);
            }
            catch (Exception e)
            {
                Step("install", false, e.Message);
            }
        }

        private static void SettingsStep(bool adding)
        {
            var before = Settings.ReadSettings();
            if (before == null)
            {
                Step("hooks", false, Paths.SettingsPath + " holds something other than JSON; left as it is");
                return;
            }
            var after = Settings.Reconciled(before, adding, Command);
            var ok = Settings.Write(after, before, out var note);
            Step("hooks", ok, note);
            if (adding) Step("statusline", ok, Settings.StatusLineOwner(after) == "uselim" ? "Uselim's" : "another status line kept");
        }

        private static int RunSetup()
        {
            var claudeMissing = !Directory.Exists(Paths.ClaudeFolder);
            CopySelf();
            SettingsStep(true);
            OpenAtLogin = true;
            Step("login", OpenAtLogin, OpenAtLogin ? "on" : "the Run entry stayed off");
            if (IsRunning)
            {
                Step("launch", true, "already running");
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Paths.InstalledExe) { UseShellExecute = false, WorkingDirectory = Paths.InstallDir });
                    Step("launch", true, "started");
                }
                catch (Exception e)
                {
                    Step("launch", false, e.Message);
                }
            }
            if (claudeMissing) Step("claude", false, "Claude Code's folder " + Paths.ClaudeFolder + " was missing; the hooks wait there for it");
            return Finish(claudeMissing);
        }

        public static int Setup()
        {
            Stdio.Attach();
            printing = true;
            return RunSetup();
        }

        // a double-click on a downloaded copy: one dialog names every change first, then the same setup runs quietly; a
        // failure shows its steps in one more dialog
        public static int FirstRun()
        {
            var ask = "Uselim shows Claude's usage limits on the taskbar. Setting it up:\n\n"
                + "• puts Uselim.exe in " + Paths.InstallDir + "\n"
                + "• adds five hooks to Claude Code's settings, plus a status line when that slot is free, so Uselim "
                + "hears the limits and the chats waiting on you (a backup of the settings is kept)\n"
                + "• opens Uselim at login\n\n"
                + "Remove Uselim… in its panel takes each change back.";
            var answer = System.Windows.Forms.MessageBox.Show(ask, "Set up Uselim?", System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.None, System.Windows.Forms.MessageBoxDefaultButton.Button1);
            if (answer != System.Windows.Forms.DialogResult.OK) return 0;
            var code = RunSetup();
            if (code == 1)
            {
                var log = Paths.Read(Paths.LogPath) ?? "";
                var tail = string.Join("\n", log.Split('\n').Where(l => l.Contains(" setup ")).Reverse().Take(6).Reverse());
                System.Windows.Forms.MessageBox.Show("Uselim's setup stopped at a step:\n\n" + tail, "Uselim",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
            return code;
        }

        // the exe and its folder go once this copy has quit; the folder goes only while empty
        private static void DeleteFilesAfterExit()
        {
            var exe = Paths.InstalledExe;
            var old = Path.Combine(Paths.InstallDir, "Uselim.old.exe");
            var script = "ping -n 3 127.0.0.1 >nul & del /f /q \"" + exe + "\" \"" + old + "\" & rmdir \"" + Paths.InstallDir + "\"";
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
                {
                    CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                });
                Step("files", true, "deleted after Uselim quits");
            }
            catch (Exception e)
            {
                Step("files", false, e.Message);
            }
        }

        private static void RemoveSteps(bool quittingOthers)
        {
            SettingsStep(false);
            OpenAtLogin = false;
            Step("login", !OpenAtLogin, OpenAtLogin ? "the Run entry stayed on" : "off");
            if (quittingOthers) Step("quit", QuitRunning());
            if (File.Exists(Paths.InstalledExe)) DeleteFilesAfterExit();
        }

        public static int Remove()
        {
            Stdio.Attach();
            printing = true;
            RemoveSteps(true);
            return Finish(false);
        }

        // the panel's Remove Uselim: every step of a removal; the app quits after
        public static void RemoveFromApp() => RemoveSteps(false);

        public static void PrintState()
        {
            Stdio.Attach();
            var settings = Settings.ReadSettings() ?? new JObject();
            Stdio.Line(Json.Write(new JObject
            {
                ["version"] = Usage.Version,
                ["hooks"] = Settings.HasHooks(settings),
                ["statusline"] = Settings.StatusLineOwner(settings),
                ["login"] = OpenAtLogin,
                ["running"] = IsRunning,
                ["installed"] = File.Exists(Paths.InstalledExe),
            }));
        }
    }
}
