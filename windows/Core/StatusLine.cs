using System;
using System.Collections.Generic;

namespace Uselim
{
    // The status line push. Claude Code hands its status line command a JSON after every reply, with rate_limits in it:
    // the session and the week, each as used_percentage and resets_at in epoch seconds. "--statusline" takes it on
    // stdin, keeps the two figures in limits.json for the app and prints one line for the status line itself. The
    // figures land live while any chat works; the app's own read stays for idle hours and for the per-model line.

    public static class StatusLine
    {
        public static string Take(string input, DateTime now)
        {
            var limits = Json.ParseObject(input)?.Obj("rate_limits");
            if (limits == null) return "";
            var kept = new JObject { ["at"] = Spans.ToEpoch(now) };
            var words = new List<string>();
            foreach (var pair in new[] { new[] { "five_hour", "session" }, new[] { "seven_day", "weekly_all" } })
            {
                var entry = limits.Obj(pair[0]);
                var used = entry?.Num("used_percentage");
                if (!used.HasValue) continue;
                var percent = (long)Math.Round(used.Value, MidpointRounding.AwayFromZero);
                var figure = new JObject { ["percent"] = percent };
                var left = "";
                var resets = entry.Num("resets_at");
                if (resets.HasValue)
                {
                    figure["resets"] = resets.Value;
                    left = " · " + Spans.Whole(Spans.FromEpoch(resets.Value), now);
                }
                kept[pair[1]] = figure;
                words.Add(percent + "%" + left);
            }
            Paths.WriteAtomic(Paths.PushedPath, Json.Write(kept));
            return string.Join("   ", words);
        }

        // the pushed figures and their time
        public static bool ReadPushed(out DateTime at, out List<Limit> limits)
        {
            at = DateTime.MinValue;
            limits = new List<Limit>();
            var json = Json.ParseObject(Paths.Read(Paths.PushedPath));
            var stamp = json?.Num("at");
            if (!stamp.HasValue) return false;
            at = Spans.FromEpoch(stamp.Value);
            foreach (var pair in new[] { new[] { "session", "Session (5 hr)" }, new[] { "weekly_all", "Weekly (7 days)" } })
            {
                var entry = json.Obj(pair[0]);
                var percent = entry?.Num("percent");
                if (!percent.HasValue) continue;
                var resets = entry.Num("resets");
                limits.Add(new Limit
                {
                    Kind = pair[0], Name = pair[1], Percent = (int)percent.Value,
                    Resets = resets.HasValue ? Spans.FromEpoch(resets.Value) : (DateTime?)null,
                });
            }
            return true;
        }
    }
}
