using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Uselim
{
    // Setup and removal on Linux, every step with the person's own rights. The app lives in ~/.local/bin/uselim, the
    // folder Ubuntu and Mint put on the PATH; "--setup" copies this file there, adds the hooks and the status line to
    // Claude Code's settings (a backup first), installs the plugin, writes the menu entry and the autostart entry and
    // starts the app; "--remove" takes each back. Started from anywhere else (a download), the file sets itself up the
    // same way. Each step prints one JSON line, {"step", "ok", "note"}, and the last one {"done", "ok"}; the exit code is
    // 0 when every step went through, 2 when Claude Code's folder is missing, 1 for any other failure.

    internal static class Install
    {
        private static string Local => Path.Combine(Paths.Home, ".local");
        public static string InstalledExe => Path.Combine(Local, "bin", "uselim");
        public static string PluginDir => Path.Combine(Paths.SupportDir, "plugin");
        private static string IconPath => Path.Combine(Local, "share", "icons", "hicolor", "256x256", "apps", "uselim.png");
        private static string MenuEntry => Path.Combine(Local, "share", "applications", "uselim.desktop");
        private static string ConfigHome
        {
            get
            {
                var set = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return !string.IsNullOrEmpty(set) && set.StartsWith("/") ? set : Path.Combine(Paths.Home, ".config");
            }
        }
        private static string AutostartEntry => Path.Combine(ConfigHome, "autostart", "uselim.desktop");
        public static string PidPath => Path.Combine(Paths.SupportDir, "app.pid");

        public static string ThisExe => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
        public static bool IsInstalledCopy => Paths.Same(ThisExe, InstalledExe);
        public static string Command => Settings.CommandFor(InstalledExe, Paths.Home);

        private static bool allOK = true;
        private static bool printing;

        private static void Step(string name, bool ok, string note = "")
        {
            if (!ok) allOK = false;
            var line = new JObject { ["step"] = name, ["ok"] = ok };
            if (note.Length > 0) line["note"] = note;
            Log.Note("setup " + name + ": " + (ok ? "ok" : "failed") + (note.Length > 0 ? ", " + note : ""));
            if (printing) Console.WriteLine(Json.Write(line));
        }

        private static int Finish(bool claudeMissing)
        {
            if (printing) Console.WriteLine(Json.Write(new JObject { ["done"] = true, ["ok"] = allOK && !claudeMissing }));
            return claudeMissing ? 2 : allOK ? 0 : 1;
        }

        // The desktop entries: the menu's, and autostart's for Open at login

        private static string Entry(bool autostart) =>
            "[Desktop Entry]\nType=Application\nName=Uselim\nComment=Claude's usage limits beside the clock\n"
            + "Exec=" + InstalledExe + "\nIcon=uselim\nTerminal=false\nCategories=Utility;\n"
            + (autostart ? "X-GNOME-Autostart-enabled=true\n" : "StartupNotify=false\n");

        public static bool OpenAtLogin
        {
            get => File.Exists(AutostartEntry);
            set
            {
                try
                {
                    if (value)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(AutostartEntry));
                        File.WriteAllText(AutostartEntry, Entry(true), new UTF8Encoding(false));
                    }
                    else if (File.Exists(AutostartEntry)) File.Delete(AutostartEntry);
                }
                catch (Exception e)
                {
                    Log.Note("open at login: " + e.Message);
                }
            }
        }

        // The running copy: its process id in app.pid, held while it runs

        public static int RunningPid
        {
            get
            {
                if (!int.TryParse(Paths.Read(PidPath)?.Trim(), out var pid) || pid == Environment.ProcessId) return 0;
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        return !p.HasExited && p.ProcessName.StartsWith("uselim", StringComparison.Ordinal) ? pid : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public static bool IsRunning => RunningPid != 0;

        // asks the running app to quit (SIGTERM, which it answers by closing), then ends it after five seconds
        public static bool QuitRunning()
        {
            var pid = RunningPid;
            if (pid == 0) return true;
            Process running;
            try { running = Process.GetProcessById(pid); }
            catch (Exception) { return true; }
            using (running)
            {
                // the app clears app.pid first and ends a moment later: the wait is for the process itself
                RunTool("kill", "-TERM " + pid, out _, 5);
                if (running.WaitForExit(5000)) return true;
                try { running.Kill(); running.WaitForExit(2000); } catch (Exception) { }
                return running.HasExited;
            }
        }

        // The steps

        private static void CopySelf()
        {
            if (IsInstalledCopy)
            {
                Step("install", true, "already in place");
                return;
            }
            try
            {
                if (File.Exists(InstalledExe) && !QuitRunning()) throw new IOException("the running Uselim kept its place");
                Directory.CreateDirectory(Path.GetDirectoryName(InstalledExe));
                var temp = InstalledExe + ".new";
                File.Copy(ThisExe, temp, true);
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(temp, InstalledExe, true);
                Step("install", true, InstalledExe);
            }
            catch (Exception e)
            {
                Step("install", false, e.Message);
            }
        }

        private static void EntriesStep()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IconPath));
                using (var from = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/icon.png"))
                using (var to = File.Create(IconPath))
                    from.CopyTo(to);
                Directory.CreateDirectory(Path.GetDirectoryName(MenuEntry));
                File.WriteAllText(MenuEntry, Entry(false), new UTF8Encoding(false));
                Step("menu", true, MenuEntry);
            }
            catch (Exception e)
            {
                Step("menu", false, e.Message);
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

        public static int Setup(bool print)
        {
            printing = print;
            CopySelf();
            EntriesStep();
            SettingsStep(true);
            var claude = ClaudeBinary();
            if (claude != null) PluginStep(claude);
            else Step("plugin", false, "Claude Code was not found: the claude command, or the VS Code extension");
            OpenAtLogin = true;
            Step("login", OpenAtLogin, OpenAtLogin ? "on" : "the autostart entry stayed off");
            if (IsRunning) Step("launch", true, "already running");
            else
            {
                try
                {
                    // its own fresh output pipes, so the app holds zero handles of setup's: a caller reading setup's output
                    // meets its end once setup finishes, as on Windows
                    Process.Start(new ProcessStartInfo(InstalledExe)
                    {
                        UseShellExecute = false, WorkingDirectory = Paths.Home,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                    Step("launch", true, "started");
                }
                catch (Exception e)
                {
                    Step("launch", false, e.Message);
                }
            }
            return Finish(claude == null);
        }

        private static void RemoveSteps(bool quittingOthers)
        {
            SettingsStep(false);
            var claude = ClaudeBinary();
            if (claude != null) PluginRemoval(claude);
            else Step("plugin", true, "Claude Code was not found; nothing to remove");
            OpenAtLogin = false;
            Step("login", !OpenAtLogin, OpenAtLogin ? "the autostart entry stayed" : "off");
            if (quittingOthers) Step("quit", QuitRunning());
            try
            {
                foreach (var file in new[] { MenuEntry, IconPath }) if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(PluginDir)) Directory.Delete(PluginDir, true);
                // a running program's file can go on Linux: the process keeps its copy until it ends
                if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
                Step("files", true, "deleted");
            }
            catch (Exception e)
            {
                Step("files", false, e.Message);
            }
        }

        public static int Remove()
        {
            printing = true;
            RemoveSteps(true);
            return Finish(false);
        }

        public static void RemoveFromApp() => RemoveSteps(false);

        // Claude Code's plugin: the copy inside this file written to the support folder, its one-entry marketplace added
        // where none named uselim exists, then the plugin installed for the user. Claude Code's own command does each step.

        private const string MarketplaceName = "uselim";
        private const string PluginId = "uselim@uselim";

        // on the PATH or in a usual install folder, else the newest inside a VS Code family editor's Claude Code extension
        public static string ClaudeBinary()
        {
            var home = Paths.Home;
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Where(f => f.Length > 0).ToList();
            folders.Add(Path.Combine(home, ".local", "bin"));
            folders.Add(Path.Combine(home, ".claude", "local"));
            folders.Add(Path.Combine(home, ".npm-global", "bin"));
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, "claude");
                if (File.Exists(path)) return path;
            }
            string best = null;
            var bestVersion = "0";
            foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf", ".vscode-oss" })
            {
                var root = Path.Combine(home, editor, "extensions");
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "anthropic.claude-code-*"))
                {
                    var path = Path.Combine(dir, "resources", "native-binary", "claude");
                    if (!File.Exists(path)) continue;
                    var version = Path.GetFileName(dir).Substring("anthropic.claude-code-".Length).Split('-')[0];
                    if (best != null && !Spans.IsNewer(version, bestVersion)) continue;
                    best = path;
                    bestVersion = version;
                }
            }
            return best;
        }

        // a tool's exit code and output, ended after its time
        public static int RunTool(string path, string arguments, out string output, int seconds = 120)
        {
            output = "";
            try
            {
                var info = new ProcessStartInfo(path, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using (var process = Process.Start(info))
                {
                    var text = new StringBuilder();
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

        private static Dictionary<string, JObject> Listed(string claude, string arguments, string key)
        {
            RunTool(claude, arguments + " --json", out var output);
            var start = output.IndexOf('[');
            var found = new Dictionary<string, JObject>();
            if (start < 0) return found;
            var list = Json.Parse(output.Substring(start).Trim()) as List<object>;
            foreach (var entry in list?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                if (entry.Str(key) is string name) found[name] = entry;
            return found;
        }

        private static bool IsPluginOn(string claude) =>
            Listed(claude, "plugin list", "id").TryGetValue(PluginId, out var entry) && entry.Bool("enabled") == true;

        private static bool WritePlugin()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Plugin/", StringComparison.Ordinal)))
                {
                    var target = Path.Combine(PluginDir, name.Substring("Plugin/".Length));
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
            if (!Listed(claude, "plugin marketplace list", "name").ContainsKey(MarketplaceName)
                && RunTool(claude, "plugin marketplace add \"" + PluginDir + "\"", out var added) != 0)
            {
                Step("plugin", false, "marketplace add: " + added.Trim());
                return;
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
            if (Listed(claude, "plugin marketplace list", "name").TryGetValue(MarketplaceName, out var market)
                && market.Str("path") is string source && source.StartsWith(PluginDir, StringComparison.Ordinal))
                RunTool(claude, "plugin marketplace remove " + MarketplaceName, out _);
            Step("plugin", true, "removed");
        }

        // after an update: hooks a newer version adds join the ones in place; settings with Uselim's hooks taken out stay
        public static void RefreshHooks()
        {
            var before = Settings.ReadSettings();
            if (before == null || !Settings.HasAnyHook(before) || Settings.HasHooks(before)) return;
            var ok = Settings.Write(Settings.Reconciled(before, true, Command), before, out var note);
            Log.Note("hooks after an update: " + (ok ? "" : "failed, ") + note);
        }

        public static void EnsurePlugin()
        {
            var claude = ClaudeBinary();
            if (claude == null) Log.Note("plugin: Claude Code was not found");
            else PluginStep(claude);
        }

        public static void PrintState()
        {
            var settings = Settings.ReadSettings() ?? new JObject();
            Console.WriteLine(Json.Write(new JObject
            {
                ["version"] = Usage.Version,
                ["hooks"] = Settings.HasHooks(settings),
                ["statusline"] = Settings.StatusLineOwner(settings),
                ["plugin"] = ClaudeBinary() is string claude && IsPluginOn(claude),
                ["login"] = OpenAtLogin,
                ["running"] = IsRunning,
                ["installed"] = File.Exists(InstalledExe),
            }));
        }
    }
}
