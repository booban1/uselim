using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Uselim
{
    // Uselim's entries in Claude Code's settings.json: five hooks, so Uselim hears questions, permission asks and their
    // answers, and a status line that hands Uselim the limits after each reply, set only where the settings hold zero status line.
    // The file as it stood is copied to the backups before each change, the newest ten kept. The phone's permission hook
    // and plugin wait for the phone version.
    //
    // On Windows Claude Code runs these commands through Git Bash, or PowerShell where Git Bash is missing. Both read
    // "~/AppData/Local/Programs/Uselim/Uselim.exe --hook" alike: forward slashes, and the profile's path (spaces
    // included) behind the tilde.

    public static class Settings
    {
        private static readonly string[][] events =
        {
            new[] { "PreToolUse", "AskUserQuestion" },
            new[] { "PostToolUse", null },
            new[] { "Notification", "permission_prompt" },
            new[] { "UserPromptSubmit", null },
            new[] { "SessionEnd", null },
        };

        // the command that runs an exe: "~/..." inside the profile, else the whole path in quotes with forward slashes
        public static string CommandFor(string exe, string home)
        {
            var full = exe.Replace('\\', '/');
            var root = home.Replace('\\', '/').TrimEnd('/') + "/";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rest = full.Substring(root.Length);
                if (!rest.Contains(" ")) return "~/" + rest;
            }
            return full.Contains(" ") ? "\"" + full + "\"" : full;
        }

        // a command of Uselim's, wherever its exe sits, the Mac's form included
        public static bool IsOurs(object command, string flag)
        {
            if (!(command is string s)) return false;
            var c = s.Replace("\"", "").Replace('\\', '/').Trim().ToLowerInvariant();
            flag = " " + flag.ToLowerInvariant();
            return c.EndsWith("/uselim.exe" + flag) || c == "uselim.exe" + flag || c.EndsWith("/uselim" + flag) || c == "uselim" + flag;
        }

        private static bool IsOurGroup(object group) =>
            (group as JObject)?.Arr("hooks")?.OfType<JObject>().Any(h => IsOurs(h["command"], "--hook") || IsOurs(h["command"], "--permission")) == true;

        // the settings with Uselim's hook groups taken out, and when adding, put back in one group per event; the status
        // line set where the settings hold zero status line, or taken off where it is Uselim's. Every other entry stays as it is, in its place.
        public static JObject Reconciled(JObject settings, bool adding, string command)
        {
            var copy = Json.ParseObject(Json.Write(settings)) ?? new JObject();
            var hooks = copy.Obj("hooks") ?? new JObject();
            foreach (var key in hooks.Keys.ToList())
            {
                if (!(hooks[key] is List<object> groups)) continue;
                var kept = groups.Where(g => !IsOurGroup(g)).ToList();
                if (kept.Count == 0) hooks.Remove(key);
                else hooks[key] = kept;
            }
            if (adding)
            {
                foreach (var e in events)
                {
                    var handler = new JObject { ["type"] = "command", ["command"] = command + " --hook", ["timeout"] = 5L };
                    var group = new JObject();
                    if (e[1] != null) group["matcher"] = e[1];
                    group["hooks"] = new List<object> { handler };
                    var list = hooks.Arr(e[0]) ?? new List<object>();
                    list.Add(group);
                    hooks[e[0]] = list;
                }
                if (!copy.Has("statusLine") || copy["statusLine"] == null)
                    copy["statusLine"] = new JObject { ["type"] = "command", ["command"] = command + " --statusline" };
            }
            else if (IsOurs(copy.Obj("statusLine")?["command"], "--statusline"))
            {
                copy.Remove("statusLine");
            }
            if (hooks.Count == 0) copy.Remove("hooks");
            else copy["hooks"] = hooks;
            return copy;
        }

        // the settings as they stand: an absent file reads as empty, one that fails to parse as null, left untouched
        public static JObject ReadSettings()
        {
            var text = Paths.Read(Paths.SettingsPath);
            if (text == null) return File.Exists(Paths.SettingsPath) ? null : new JObject();
            return text.Trim().Length == 0 ? new JObject() : Json.ParseObject(text);
        }

        // writes the settings when they changed, the file as it stood copied to the backups first (the newest ten kept)
        public static bool Write(JObject settings, JObject before, out string note)
        {
            var next = Json.Write(settings, true);
            if (next == Json.Write(before, true))
            {
                note = "already in place";
                return true;
            }
            note = "written";
            var target = Paths.SettingsPath;
            if (File.Exists(target))
            {
                try
                {
                    Directory.CreateDirectory(Paths.BackupDir);
                    var backup = Path.Combine(Paths.BackupDir, "settings-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".json");
                    File.Copy(target, backup, true);
                    foreach (var old in Directory.GetFiles(Paths.BackupDir, "settings-*.json").OrderByDescending(f => f).Skip(10))
                        File.Delete(old);
                    note = "backup in " + backup;
                }
                catch (Exception e)
                {
                    note = "the backup failed: " + e.Message;
                    return false;
                }
            }
            if (!Paths.WriteAtomic(target, next + "\n"))
            {
                note = "writing the settings failed at " + target;
                return false;
            }
            return true;
        }

        // every hook in place
        public static bool HasHooks(JObject settings)
        {
            var hooks = settings?.Obj("hooks");
            if (hooks == null) return false;
            return events.All(e => hooks.Arr(e[0])?.Any(IsOurGroup) == true);
        }

        // "uselim", "other" or "none"
        public static string StatusLineOwner(JObject settings)
        {
            var line = settings?.Obj("statusLine");
            if (line == null) return "none";
            return IsOurs(line["command"], "--statusline") ? "uselim" : "other";
        }
    }

    // Notices: one as a limit crosses 60, 80 and 100% within a window, and one when a limit that had warned resets.
    // Each limit's window is its reset time; a new reset time starts the steps over.
    public sealed class Alerts
    {
        private readonly Dictionary<string, int> told = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> windows = new Dictionary<string, DateTime>();

        private static int Step(int percent) => percent >= 100 ? 100 : percent >= 80 ? 80 : percent >= 60 ? 60 : 0;

        // figures carried over from the last copy: their steps count as told, so a restart stays quiet
        public void Seed(IEnumerable<Limit> limits)
        {
            foreach (var l in limits)
            {
                windows[l.Kind] = l.Resets ?? DateTime.MinValue;
                told[l.Kind] = Step(l.Percent);
            }
        }

        // the notices these figures bring, title and body
        public List<string[]> Check(IEnumerable<Limit> limits, DateTime now)
        {
            var notices = new List<string[]>();
            foreach (var l in limits)
            {
                var window = l.Resets ?? DateTime.MinValue;
                // the read and the status line push write the same reset time, one with its fraction of a second
                if (windows.TryGetValue(l.Kind, out var known) && Math.Abs((known - window).TotalSeconds) > 60)
                {
                    if ((told.TryGetValue(l.Kind, out var was) ? was : 0) >= 60)
                        notices.Add(new[] { l.Name + " reset", "Back to " + l.Percent + "%" });
                    told[l.Kind] = 0;
                }
                windows[l.Kind] = window;
                var step = Step(l.Percent);
                if (step <= (told.TryGetValue(l.Kind, out var t) ? t : 0)) continue;
                told[l.Kind] = step;
                var when = l.Resets.HasValue ? Spans.ResetText(l.Resets.Value, now) + ", in " + Spans.Exact(l.Resets.Value, now) : null;
                notices.Add(new[] { step == 100 ? l.Name + " spent" : l.Name + " at " + l.Percent + "%", when });
            }
            return notices;
        }
    }
}
