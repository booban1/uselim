using System;
using System.IO;
using System.Text;

namespace Uselim
{
    // Where things live on Windows. Claude Code's folder as Claude Code finds it (CLAUDE_CONFIG_DIR where set, else
    // .claude in the profile), Uselim's own files in %LOCALAPPDATA%\Uselim, the app in %LOCALAPPDATA%\Programs\Uselim.
    // USELIM_SUPPORT_DIR and USELIM_CLAUDE_DIR point a test run elsewhere.

    public static class Paths
    {
        public static string Home
        {
            get
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(profile) ? Environment.GetEnvironmentVariable("HOME") ?? "" : profile;
            }
        }

        public static string ClaudeFolder
        {
            get
            {
                var test = Environment.GetEnvironmentVariable("USELIM_CLAUDE_DIR");
                if (!string.IsNullOrEmpty(test)) return test;
                var set = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                return string.IsNullOrEmpty(set) ? Path.Combine(Home, ".claude") : set;
            }
        }

        public static string SettingsPath => Path.Combine(ClaudeFolder, "settings.json");
        public static string CredentialsPath => Path.Combine(ClaudeFolder, ".credentials.json");

        private static string LocalData
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(local) ? Path.Combine(Home, "AppData", "Local") : local;
            }
        }

        public static string SupportDir
        {
            get
            {
                var test = Environment.GetEnvironmentVariable("USELIM_SUPPORT_DIR");
                return string.IsNullOrEmpty(test) ? Path.Combine(LocalData, "Uselim") : test;
            }
        }

        public static string WaitingDir => Path.Combine(SupportDir, "waiting");
        public static string StatePath => Path.Combine(SupportDir, "state.json");
        public static string PushedPath => Path.Combine(SupportDir, "limits.json");
        public static string PrefsPath => Path.Combine(SupportDir, "prefs.json");
        public static string BackupDir => Path.Combine(SupportDir, "backups");
        public static string LogPath => Path.Combine(SupportDir, "Uselim.log");

        public static string InstallDir => Path.Combine(LocalData, "Programs", "Uselim");
        public static string InstalledExe => Path.Combine(InstallDir, "Uselim.exe");

        public static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        // a file's text as UTF-8, null when it is absent or unreadable
        public static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch (Exception) { return null; }
        }

        // written beside its place first, then moved over it, so a reader meets the old file or the new one
        public static bool WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // Lines for Uselim.log: the start, state changes and failures
    public static class Log
    {
        private static readonly object gate = new object();

        public static void Note(string text)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\n";
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Paths.SupportDir);
                    File.AppendAllText(Paths.LogPath, line, new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }
    }

    // Small settings of the app's own: the reads' pacing, the plan's name, the first run's welcome
    public static class Prefs
    {
        private static readonly object gate = new object();

        private static JObject Load() => Json.ParseObject(Paths.Read(Paths.PrefsPath)) ?? new JObject();

        public static double Num(string key, double fallback = 0)
        {
            lock (gate) return Load().Num(key) ?? fallback;
        }

        public static string Str(string key)
        {
            lock (gate) return Load().Str(key);
        }

        public static bool Flag(string key)
        {
            lock (gate) return Load().Bool(key) ?? false;
        }

        public static void Set(string key, object value)
        {
            lock (gate)
            {
                var all = Load();
                all[key] = value;
                Paths.WriteAtomic(Paths.PrefsPath, Json.Write(all, true));
            }
        }
    }
}
