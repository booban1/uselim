using System;
using System.Globalization;

namespace Uselim
{
    // Time words. The tooltip shows one whole unit or, past a day, the reset's weekday; the panel the exact span, and
    // reset times in the PC's own clock style. Every DateTime here is UTC; the words turn it local.

    public static class Spans
    {
        // "2h", "20m", rounded to the nearest unit; past a day the reset's weekday, "Fri", since "1d" reads as a day for
        // a reset a day and a half away
        public static string Whole(DateTime until, DateTime now)
        {
            var s = Math.Max(0, (until - now).TotalSeconds);
            if (s >= 86400) return until.ToLocalTime().ToString("ddd", CultureInfo.CurrentCulture);
            if (s >= 3600) return Math.Round(s / 3600, MidpointRounding.AwayFromZero) + "h";
            return Math.Max(1, (int)Math.Round(s / 60, MidpointRounding.AwayFromZero)) + "m";
        }

        // "2d 9h", "1h 38m", "20m"
        public static string Exact(DateTime until, DateTime now)
        {
            var s = (long)Math.Max(0, (until - now).TotalSeconds);
            long d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60;
            if (d > 0) return d + "d " + h + "h";
            if (h > 0) return h + "h " + m + "m";
            return m + "m";
        }

        // "15:32", or "3:32 PM", as the PC shows times
        public static string Clock(DateTime at) =>
            at.ToLocalTime().ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

        // "Resets at 21:20" today, "Resets Fri 20 Nov at 05:00" on another day
        public static string ResetText(DateTime at, DateTime now)
        {
            var local = at.ToLocalTime();
            if (local.Date == now.ToLocalTime().Date) return "Resets at " + Clock(at);
            return "Resets " + local.ToString("ddd d MMM", CultureInfo.CurrentCulture) + " at " + Clock(at);
        }

        // "12 s ago", "3 min ago", "2 h ago"
        public static string Ago(DateTime at, DateTime now)
        {
            var s = (long)(now - at).TotalSeconds;
            if (s < 60) return Math.Max(0, s) + " s ago";
            if (s < 3600) return s / 60 + " min ago";
            return s / 3600 + " h ago";
        }

        public static DateTime FromEpoch(double seconds) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

        public static double ToEpoch(DateTime at) =>
            (at.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        // a dotted version against another, part by part
        public static bool IsNewer(string a, string b)
        {
            var x = (a ?? "").Split('.');
            var y = (b ?? "").Split('.');
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var p = i < x.Length && int.TryParse(x[i], out var px) ? px : 0;
                var q = i < y.Length && int.TryParse(y[i], out var qy) ? qy : 0;
                if (p != q) return p > q;
            }
            return false;
        }
    }
}
