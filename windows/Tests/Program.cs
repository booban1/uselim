using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Uselim.Tests
{
    // Checks for the Windows app's core, run on the Mac with "./build.sh test": JSON, the usage answer, time words, the
    // status line push, the hook's waiting files, the settings' hooks and status line, and the notices' steps. Each run
    // works in a fresh folder of its own, through USELIM_SUPPORT_DIR and USELIM_CLAUDE_DIR.

    public static class Program
    {
        private static int passed, failed;

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) passed++;
            else
            {
                failed++;
                Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
            }
        }

        public static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "uselim-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Environment.SetEnvironmentVariable("USELIM_SUPPORT_DIR", Path.Combine(root, "support"));
            Environment.SetEnvironmentVariable("USELIM_CLAUDE_DIR", Path.Combine(root, "claude"));
            Directory.CreateDirectory(Path.Combine(root, "claude"));
            try
            {
                JsonChecks();
                UsageChecks();
                SpanChecks();
                StatusLineChecks();
                HookChecks(root);
                SettingsChecks();
                AlertChecks();
                ReadoutChecks();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        private static void JsonChecks()
        {
            var text = "{\"b\":1,\"a\":[true,null,\"x\\\"y\",2.5,-3],\"c\":{\"é\":\"\\u00e9/\"}}";
            var o = Json.ParseObject(text);
            Check("json parses", o != null);
            Check("json keeps key order", string.Join(",", o.Keys) == "b,a,c");
            Check("json whole numbers are long", o["b"] is long);
            Check("json fractions are double", (o.Arr("a")[3] as double?) == 2.5);
            Check("json escapes", (string)o.Arr("a")[2] == "x\"y");
            Check("json unicode escape", o.Obj("c").Str("é") == "é/");
            Check("json round trip", Json.Write(o) == "{\"b\":1,\"a\":[true,null,\"x\\\"y\",2.5,-3],\"c\":{\"é\":\"é/\"}}", Json.Write(o));
            Check("json bad text is null", Json.Parse("{\"a\":}") == null && Json.Parse("{} x") == null);
            Check("json pretty", Json.Write(Json.ParseObject("{\"a\":[1],\"b\":{}}"), true) == "{\n  \"a\": [\n    1\n  ],\n  \"b\": {}\n}");
            Check("json bom", Json.ParseObject("\uFEFF{\"a\":1}") != null);
        }

        private static void UsageChecks()
        {
            var body = "{\"limits\":[" +
                       "{\"kind\":\"session\",\"percent\":17,\"severity\":\"normal\",\"resets_at\":\"2026-11-16T21:20:00.123456+00:00\"}," +
                       "{\"kind\":\"weekly_all\",\"percent\":64,\"severity\":\"warning\",\"resets_at\":\"2026-11-20T05:00:00+00:00\"}," +
                       "{\"kind\":\"weekly_scoped\",\"percent\":11,\"scope\":{\"model\":{\"display_name\":\"Fable\"}},\"resets_at\":null}]}";
            var limits = Usage.ParseLimits(body);
            Check("usage rows", limits != null && limits.Count == 3);
            Check("usage names", limits[0].Name == "Session (5 hr)" && limits[1].Name == "Weekly (7 days)" && limits[2].Name == "Fable (weekly)");
            Check("usage reset", limits[0].Resets == new DateTime(2026, 11, 16, 21, 20, 0, DateTimeKind.Utc).AddTicks(1234560), limits[0].Resets?.ToString("o"));
            Check("usage missing reset", limits[2].Resets == null);
            Check("usage tones", Usage.ToneOf(limits[0]) == Tone.Normal && Usage.ToneOf(limits[1]) == Tone.Warning
                                 && Usage.ToneOf(new Limit { Percent = 85 }) == Tone.Critical);
            Check("usage without limits", Usage.ParseLimits("{\"error\":1}") == null);

            var at = new DateTime(2026, 11, 16, 19, 42, 0, DateTimeKind.Utc);
            Usage.SaveState(limits, at);
            Check("state round trip", Usage.LoadState(out var back, out var saved) && saved.Count == 3 && back == at
                                      && saved[1].Percent == 64 && saved[1].Severity == "warning");

            File.WriteAllText(Paths.CredentialsPath, "{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"expiresAt\":1763320000000,\"subscriptionType\":\"team\"}}");
            var login = Usage.ReadLogin();
            Check("login read", login != null && login.Token == "tok" && login.Plan == "team" && login.Expires.HasValue);
            File.Delete(Paths.CredentialsPath);
            Check("login missing", Usage.ReadLogin() == null);
        }

        private static void SpanChecks()
        {
            var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            Check("whole hours", Spans.Whole(now.AddMinutes(150), now) == "3h", Spans.Whole(now.AddMinutes(150), now));
            Check("whole minutes", Spans.Whole(now.AddMinutes(20), now) == "20m");
            Check("whole floor minute", Spans.Whole(now.AddSeconds(10), now) == "1m");
            Check("exact days", Spans.Exact(now.AddHours(57), now) == "2d 9h");
            Check("exact hours", Spans.Exact(now.AddMinutes(98), now) == "1h 38m");
            Check("ago", Spans.Ago(now.AddSeconds(-12), now) == "12 s ago" && Spans.Ago(now.AddMinutes(-3), now) == "3 min ago");
            Check("newer", Spans.IsNewer("0.2.0", "0.1.9") && !Spans.IsNewer("0.1.0", "0.1.0") && Spans.IsNewer("1.0", "0.9.9"));
        }

        private static void StatusLineChecks()
        {
            var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var resets = Spans.ToEpoch(now.AddHours(2));
            var input = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":" + resets + "},\"seven_day\":{\"used_percentage\":41.2}}}";
            var line = StatusLine.Take(input, now);
            Check("status line words", line == "24% · 2h   41%", line);
            Check("status line kept", StatusLine.ReadPushed(out var at, out var pushed) && at == now && pushed.Count == 2
                                      && pushed[0].Percent == 24 && pushed[0].Resets == now.AddHours(2) && pushed[1].Resets == null);
            Check("status line without limits", StatusLine.Take("{\"model\":{}}", now) == "");
        }

        private static void HookChecks(string root)
        {
            var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var transcript = Path.Combine(root, "t.jsonl");
            File.WriteAllText(transcript,
                "{\"type\":\"summary\"}\n" +
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}\n" +
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"<ide_opened_file>x</ide_opened_file>Build the   Windows tray\"}]}}\n");
            var ask = "{\"session_id\":\"abc-123\",\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"AskUserQuestion\"," +
                      "\"cwd\":\"C:\\\\Users\\\\dana\\\\Documents\\\\app\\\\uselim\\\\\",\"transcript_path\":" + Json.Write(transcript) + "," +
                      "\"tool_input\":{\"questions\":[{\"header\":\"Tray\",\"question\":\"Which readout?\"}]}}";
            Waiting.RunHook(ask, now);
            var list = Waiting.Read();
            Check("hook writes a question", list.Count == 1 && list[0].IsQuestion && list[0].Session == "abc-123");
            Check("hook project from a Windows cwd", list.Count == 1 && list[0].Project == "uselim", list.FirstOrDefault()?.Project);
            Check("hook opening words", list.Count == 1 && list[0].Began == "Build the Windows tray", list.FirstOrDefault()?.Began);
            Check("hook detail", list.Count == 1 && list[0].Detail == "Tray: Which readout?");
            Waiting.RunHook("{\"session_id\":\"abc-123\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"AskUserQuestion\"}", now);
            Check("hook clears on the answer", Waiting.Read().Count == 0);
            Waiting.RunHook("{\"session_id\":\"p1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"}", now);
            Check("hook writes a permission", Waiting.Read().Any(w => !w.IsQuestion && w.Detail.Contains("Bash")));
            Waiting.RunHook("{\"session_id\":\"p1\",\"hook_event_name\":\"SessionEnd\"}", now);
            Check("hook clears on the end", Waiting.Read().Count == 0);
            Waiting.RunHook("{\"session_id\":\"../x\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\"}", now);
            Check("hook refuses an odd session id", Waiting.Read().Count == 0 && !File.Exists(Path.Combine(Paths.SupportDir, "x.json")));
        }

        private static void SettingsChecks()
        {
            Check("command in the profile", Settings.CommandFor("C:\\Users\\John Doe\\AppData\\Local\\Programs\\Uselim\\Uselim.exe", "C:\\Users\\John Doe")
                                            == "~/AppData/Local/Programs/Uselim/Uselim.exe");
            Check("command elsewhere", Settings.CommandFor("D:\\Tools\\My Apps\\Uselim.exe", "C:\\Users\\dana") == "\"D:/Tools/My Apps/Uselim.exe\"");
            Check("ours, tilde", Settings.IsOurs("~/AppData/Local/Programs/Uselim/Uselim.exe --hook", "--hook"));
            Check("ours, quoted", Settings.IsOurs("\"D:/Tools/My Apps/Uselim.exe\" --statusline", "--statusline"));
            Check("ours, the Mac's", Settings.IsOurs("/Applications/Uselim.app/Contents/MacOS/Uselim --hook", "--hook"));
            Check("others stay others", !Settings.IsOurs("~/bin/other.exe --hook", "--hook") && !Settings.IsOurs(5L, "--hook"));

            var before = Json.ParseObject("{\"model\":\"opus\",\"hooks\":{\"PostToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"~/lint.sh\"}]}]},\"env\":{\"A\":\"1\"}}");
            const string cmd = "~/AppData/Local/Programs/Uselim/Uselim.exe";
            var added = Settings.Reconciled(before, true, cmd);
            Check("setup adds every hook", Settings.HasHooks(added));
            Check("setup keeps other hooks", added.Obj("hooks").Arr("PostToolUse").Count == 2);
            Check("setup sets the status line", Settings.StatusLineOwner(added) == "uselim");
            Check("setup keeps key order", string.Join(",", added.Keys) == "model,hooks,env,statusLine", string.Join(",", added.Keys));
            Check("setup twice is the same", Json.Write(Settings.Reconciled(added, true, cmd)) == Json.Write(added));
            Check("setup leaves the input", !Settings.HasHooks(before));
            var removed = Settings.Reconciled(added, false, cmd);
            Check("remove restores", Json.Write(removed) == Json.Write(before), Json.Write(removed));

            var other = Json.ParseObject("{\"statusLine\":{\"type\":\"command\",\"command\":\"~/mine.sh\"}}");
            var withOther = Settings.Reconciled(other, true, cmd);
            Check("another status line stays", Settings.StatusLineOwner(withOther) == "other");
            Check("remove keeps another status line", Settings.StatusLineOwner(Settings.Reconciled(withOther, false, cmd)) == "other");
            var empty = Settings.Reconciled(new JObject(), false, cmd);
            Check("remove from empty stays empty", Json.Write(empty) == "{}");

            Check("absent settings read as empty", Settings.ReadSettings()?.Count == 0);
            File.WriteAllText(Paths.SettingsPath, "{\"model\":\"opus\",}");
            Check("broken settings read as null", Settings.ReadSettings() == null);
            File.WriteAllText(Paths.SettingsPath, Json.Write(before, true));
            var read = Settings.ReadSettings();
            Check("settings write", Settings.Write(Settings.Reconciled(read, true, cmd), read, out var note) && note.StartsWith("backup in"), note);
            Check("settings written", Settings.HasHooks(Settings.ReadSettings()));
            Check("backup kept", Directory.GetFiles(Paths.BackupDir, "settings-*.json").Length == 1);
            var again = Settings.ReadSettings();
            Check("settings unchanged write", Settings.Write(Settings.Reconciled(again, true, cmd), again, out var note2) && note2 == "already in place", note2);
        }

        private static void ReadoutChecks()
        {
            var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var limits = new List<Limit>
            {
                new Limit { Kind = "session", Name = "Session (5 hr)", Percent = 64, Resets = now.AddMinutes(22) },
                new Limit { Kind = "weekly_all", Name = "Weekly (7 days)", Percent = 53, Resets = now.AddDays(3).AddHours(17) },
                new Limit { Kind = "weekly_scoped", Name = "Fable (weekly)", Percent = 71, Resets = now.AddDays(3) },
                new Limit { Kind = "weekly_scoped", Name = "Other (weekly)", Percent = 20, Resets = now.AddDays(3) },
            };
            Readout.Lines(limits, now, out var top, out var bottom);
            Check("readout top", Readout.Plain(top) == "64% · 22m", Readout.Plain(top));
            var day = now.AddDays(3).AddHours(17).ToLocalTime().ToString("ddd", System.Globalization.CultureInfo.CurrentCulture);
            Check("readout bottom", Readout.Plain(bottom) == "53% · 71% · " + day, Readout.Plain(bottom));
            Check("readout tones", top[0].Tone == Tone.Warning && bottom[0].Tone == Tone.Normal && bottom[2].Tone == Tone.Warning);
            Readout.Lines(new List<Limit>(), now, out var t2, out var b2);
            Check("readout empty", t2.Count == 0 && b2.Count == 0);
        }

        private static void AlertChecks()
        {
            var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var week = now.AddDays(3);
            var alerts = new Alerts();
            alerts.Seed(new[] { new Limit { Kind = "session", Name = "Session (5 hr)", Percent = 61, Resets = now.AddHours(2) } });
            Check("seeded steps stay quiet", alerts.Check(new[] { new Limit { Kind = "session", Name = "Session (5 hr)", Percent = 65, Resets = now.AddHours(2) } }, now).Count == 0);
            var at80 = alerts.Check(new[] { new Limit { Kind = "session", Name = "Session (5 hr)", Percent = 82, Resets = now.AddHours(2) } }, now);
            Check("80% tells once", at80.Count == 1 && at80[0][0] == "Session (5 hr) at 82%", at80.FirstOrDefault()?[0]);
            var reset = alerts.Check(new[] { new Limit { Kind = "session", Name = "Session (5 hr)", Percent = 3, Resets = now.AddHours(7) } }, now);
            Check("reset after a warning tells", reset.Count == 1 && reset[0][0] == "Session (5 hr) reset" && reset[0][1] == "Back to 3%");
            Check("same window within a minute", alerts.Check(new[] { new Limit { Kind = "session", Name = "S", Percent = 3, Resets = now.AddHours(7).AddSeconds(0.5) } }, now).Count == 0);
            var spent = alerts.Check(new[] { new Limit { Kind = "weekly_all", Name = "Weekly (7 days)", Percent = 100, Resets = week } }, now);
            Check("spent", spent.Count == 1 && spent[0][0] == "Weekly (7 days) spent");
        }
    }
}
