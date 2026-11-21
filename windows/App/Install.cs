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

        // Settings › Apps: Uselim's entry under the person's own key, so Windows' installed-apps list and winget read its
        // version and remove it through Uselim's own removal

        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Uselim";

        public static bool WriteAppsEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    var exe = Paths.InstalledExe;
                    var remove = "\"" + exe + "\" --remove";
                    key.SetValue("DisplayName", "Uselim");
                    key.SetValue("DisplayVersion", Usage.Version);
                    key.SetValue("Publisher", "Simon Booban");
                    key.SetValue("DisplayIcon", exe);
                    key.SetValue("InstallLocation", Paths.InstallDir);
                    key.SetValue("UninstallString", remove);
                    key.SetValue("QuietUninstallString", remove);
                    key.SetValue("URLInfoAbout", "https://github.com/booban1/uselim");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    if (File.Exists(exe)) key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                    if (key.GetValue("InstallDate") == null) key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Note("apps entry: " + e.Message);
                return false;
            }
        }

        private static bool DeleteAppsEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                return true;
            }
            catch (Exception e)
            {
                Log.Note("apps entry: " + e.Message);
                return false;
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
            CopySelf();
            Step("apps", WriteAppsEntry(), "Settings › Apps");
            SettingsStep(true);
            var claude = ClaudeBinary();
            if (claude != null) PluginStep(claude);
            else Step("plugin", false, "Claude Code was not found: the claude command, or the VS Code extension");
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
                    // started through the shell, so the app inherits zero handles: a caller reading setup's output (a
                    // script's pipe, Culpmit's Claude limits switch) meets its end once setup finishes
                    Process.Start(new ProcessStartInfo(Paths.InstalledExe) { UseShellExecute = true, WorkingDirectory = Paths.InstallDir });
                    Step("launch", true, "started");
                }
                catch (Exception e)
                {
                    Step("launch", false, e.Message);
                }
            }
            return Finish(claude == null);
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
                + "• adds six hooks to Claude Code's settings, plus a status line when that slot is free, so Uselim "
                + "hears the limits and the chats waiting on you (a backup of the settings is kept)\n"
                + "• installs Uselim's Claude Code plugin, which carries your phone's messages into each chat\n"
                + "• opens Uselim at login\n"
                + "• lists Uselim in Settings › Apps\n\n"
                + "Remove Uselim… in its panel, or Uninstall in Settings › Apps, takes each change back.";
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

        // the exe, the plugin's copy and their folder go once this copy has quit; the folder goes only while empty
        private static void DeleteFilesAfterExit()
        {
            var exe = Paths.InstalledExe;
            var old = Path.Combine(Paths.InstallDir, "Uselim.old.exe");
            var script = "ping -n 3 127.0.0.1 >nul & del /f /q \"" + exe + "\" \"" + old + "\" & rmdir /s /q \"" + PluginDir + "\" & rmdir \"" + Paths.InstallDir + "\"";
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
            var claude = ClaudeBinary();
            if (claude != null) PluginRemoval(claude);
            else Step("plugin", true, "Claude Code was not found; nothing to remove");
            OpenAtLogin = false;
            Step("login", !OpenAtLogin, OpenAtLogin ? "the Run entry stayed on" : "off");
            if (quittingOthers) Step("quit", QuitRunning());
            Step("apps", DeleteAppsEntry(), "out of Settings › Apps");
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

        // Claude Code's plugin: the copy inside the exe written beside it, its one-entry marketplace added where none
        // named uselim exists (one of that name already in place stays, whatever its source), then the plugin installed
        // for the user. Claude Code's own command does each step.

        private const string MarketplaceName = "uselim";
        private const string PluginId = "uselim@uselim";
        public static string PluginDir => Path.Combine(Paths.InstallDir, "Plugin");

        // on the PATH or in a usual install folder, else the newest inside a VS Code family editor's Claude Code extension
        public static string ClaudeBinary()
        {
            var home = Paths.Home;
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(f => f.Length > 0).ToList();
            folders.Add(Path.Combine(home, ".local", "bin"));
            folders.Add(Path.Combine(home, ".claude", "local"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (var folder in folders)
                foreach (var name in new[] { "claude.exe", "claude.cmd" })
                {
                    try
                    {
                        var path = Path.Combine(folder.Trim('"'), name);
                        if (File.Exists(path)) return path;
                    }
                    catch (Exception) { }
                }
            string best = null;
            var bestVersion = "0";
            foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                var root = Path.Combine(home, editor, "extensions");
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "anthropic.claude-code-*"))
                {
                    var path = Path.Combine(dir, "resources", "native-binary", "claude.exe");
                    if (!File.Exists(path)) continue;
                    var version = Path.GetFileName(dir).Substring("anthropic.claude-code-".Length).Split('-')[0];
                    if (best != null && !Spans.IsNewer(version, bestVersion)) continue;
                    best = path;
                    bestVersion = version;
                }
            }
            return best;
        }

        // a tool's exit code and output, ended after its time; npm's claude.cmd runs through cmd.exe
        public static int RunTool(string path, string arguments, out string output, int seconds = 120)
        {
            output = "";
            try
            {
                var isCmd = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
                var info = new ProcessStartInfo(isCmd ? "cmd.exe" : path, isCmd ? "/c \"\"" + path + "\" " + arguments + "\"" : arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                using (var process = Process.Start(info))
                {
                    var text = new System.Text.StringBuilder();
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(seconds * 1000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        output = "timed out";
                        return -1;
                    }
                    process.WaitForExit();
                    lock (text) output = text.ToString();
                    return process.ExitCode;
                }
            }
            catch (Exception e)
            {
                output = e.Message;
                return -1;
            }
        }

        // the JSON array a list command prints, its entries by one key
        private static System.Collections.Generic.Dictionary<string, JObject> Listed(string claude, string arguments, string key)
        {
            RunTool(claude, arguments + " --json", out var output);
            var start = output.IndexOf('[');
            var found = new System.Collections.Generic.Dictionary<string, JObject>();
            if (start < 0) return found;
            var list = Json.Parse(output.Substring(start).Trim()) as System.Collections.Generic.List<object>;
            foreach (var entry in list?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                if (entry.Str(key) is string name) found[name] = entry;
            return found;
        }

        private static bool IsPluginOn(string claude) =>
            Listed(claude, "plugin list", "id").TryGetValue(PluginId, out var entry) && entry.Bool("enabled") == true;

        // the plugin's files, from inside the exe to the folder beside it
        private static bool WritePlugin()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Plugin/", StringComparison.Ordinal)))
                {
                    var target = Path.Combine(PluginDir, name.Substring("Plugin/".Length).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var from = assembly.GetManifestResourceStream(name))
                    using (var to = File.Create(target))
                        from.CopyTo(to);
                }
                return File.Exists(Path.Combine(PluginDir, ".claude-plugin", "marketplace.json"));
            }
            catch (Exception e)
            {
                Log.Note("plugin copy: " + e.Message);
                return false;
            }
        }

        private static void PluginStep(string claude)
        {
            if (!WritePlugin())
            {
                Step("plugin", false, "the plugin's copy failed to land in " + PluginDir);
                return;
            }
            if (IsPluginOn(claude))
            {
                Step("plugin", true, "already installed");
                return;
            }
            if (!Listed(claude, "plugin marketplace list", "name").ContainsKey(MarketplaceName))
            {
                if (RunTool(claude, "plugin marketplace add \"" + PluginDir + "\"", out var added) != 0)
                {
                    Step("plugin", false, "marketplace add: " + added.Trim());
                    return;
                }
            }
            var code = RunTool(claude, "plugin install " + PluginId + " --scope user", out var installed);
            var on = code == 0 && IsPluginOn(claude);
            Step("plugin", on, on ? "installed" : installed.Trim());
        }

        private static void PluginRemoval(string claude)
        {
            if (Listed(claude, "plugin list", "id").ContainsKey(PluginId)
                && RunTool(claude, "plugin uninstall " + PluginId, out var removed) != 0)
            {
                Step("plugin", false, removed.Trim());
                return;
            }
            // the marketplace goes only where it is the copy beside this exe; a marketplace from elsewhere stays
            if (Listed(claude, "plugin marketplace list", "name").TryGetValue(MarketplaceName, out var market)
                && market.Str("path") is string source && source.StartsWith(Paths.InstallDir, StringComparison.OrdinalIgnoreCase))
                RunTool(claude, "plugin marketplace remove " + MarketplaceName, out _);
            Step("plugin", true, "removed");
        }

        // after an update: hooks a newer version adds join the ones in place; settings with Uselim's hooks taken out
        // stay as they are
        public static void RefreshHooks()
        {
            var before = Settings.ReadSettings();
            if (before == null || !Settings.HasAnyHook(before) || Settings.HasHooks(before)) return;
            var ok = Settings.Write(Settings.Reconciled(before, true, Command), before, out var note);
            Log.Note("hooks after an update: " + (ok ? "" : "failed, ") + note);
        }

        // the phone's plugin, once Phone is on: written from the exe and installed where Claude Code lacks it
        public static void EnsurePlugin()
        {
            var claude = ClaudeBinary();
            if (claude == null) Log.Note("plugin: Claude Code was not found");
            else PluginStep(claude);
        }

        public static void PrintState()
        {
            Stdio.Attach();
            var settings = Settings.ReadSettings() ?? new JObject();
            Stdio.Line(Json.Write(new JObject
            {
                ["version"] = Usage.Version,
                ["hooks"] = Settings.HasHooks(settings),
                ["statusline"] = Settings.StatusLineOwner(settings),
                ["plugin"] = ClaudeBinary() is string claude && IsPluginOn(claude),
                ["login"] = OpenAtLogin,
                ["running"] = IsRunning,
                ["installed"] = File.Exists(Paths.InstalledExe),
            }));
        }
    }
}
