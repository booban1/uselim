using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace Uselim
{
    // The Claude Code login, the one call to the usage endpoint the Claude panel reads, and the limits it answers with:
    // the session, the week and a weekly line per model, each with its share, a severity word and its reset time.

    public sealed class Limit
    {
        public string Kind;
        public string Name;
        public int Percent;
        public string Severity = "normal";
        public DateTime? Resets;

        public Limit Copy() => (Limit)MemberwiseClone();
    }

    public enum Tone { Normal, Warning, Critical }

    public sealed class Login
    {
        public string Token;
        public DateTime? Expires;
        public string Plan;
    }

    public enum FetchKind { Limits, SignedOut, Busy, Failed }

    public sealed class Fetch
    {
        public FetchKind Kind;
        public List<Limit> Limits;
        // 429: the server's asked pause in seconds, when its Retry-After names one
        public double? RetryAfter;
        public string Why;
    }

    public static class Usage
    {
        public static string Version
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v == null ? "dev" : v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static Tone ToneOf(Limit l)
        {
            if (l.Percent >= 80 || l.Severity == "critical" || l.Severity == "exceeded" || l.Severity == "locked") return Tone.Critical;
            if (l.Percent >= 60 || l.Severity != "normal") return Tone.Warning;
            return Tone.Normal;
        }

        // Claude Code's login on Windows: .credentials.json in its folder, read fresh before every call, since Claude
        // Code renews the token and rewrites the file; Uselim reads it alone. Null while signed out.
        public static Login ReadLogin()
        {
            var json = Json.ParseObject(Paths.Read(Paths.CredentialsPath));
            var oauth = json?.Obj("claudeAiOauth");
            var token = oauth?.Str("accessToken");
            if (string.IsNullOrEmpty(token)) return null;
            var expires = oauth.Num("expiresAt");
            return new Login
            {
                Token = token,
                Expires = expires.HasValue ? Spans.FromEpoch(expires.Value / 1000) : (DateTime?)null,
                Plan = oauth.Str("subscriptionType"),
            };
        }

        // the lighter form Claude's own panel asks for; the full one refuses reads with 429 after about twenty-five
        private const string Endpoint = "https://api.anthropic.com/api/oauth/usage?skip_spend=1";

        private static readonly Lazy<HttpClient> client = new Lazy<HttpClient>(() =>
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        });

        // one read, on the calling thread
        public static Fetch FetchUsage(Login login)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + login.Token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "Uselim/" + Version + " (Windows)");
                using (var response = client.Value.SendAsync(request).GetAwaiter().GetResult())
                {
                    var code = (int)response.StatusCode;
                    if (code == 401 || code == 403) return new Fetch { Kind = FetchKind.SignedOut };
                    if (code == 429)
                    {
                        var after = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                        return new Fetch { Kind = FetchKind.Busy, RetryAfter = after };
                    }
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var limits = code == 200 ? ParseLimits(body) : null;
                    if (limits == null) return new Fetch { Kind = FetchKind.Failed, Why = "HTTP " + code };
                    return new Fetch { Kind = FetchKind.Limits, Limits = limits };
                }
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                return new Fetch { Kind = FetchKind.Failed, Why = inner.Message };
            }
        }

        // the answer's limits list, null when the answer carries zero
        public static List<Limit> ParseLimits(string body)
        {
            var rows = Json.ParseObject(body)?.Arr("limits");
            if (rows == null) return null;
            var found = new List<Limit>();
            foreach (var r in rows.OfType<JObject>())
            {
                var kind = r.Str("kind");
                var percent = r.Num("percent");
                if (kind == null || !percent.HasValue) continue;
                string name;
                if (kind == "session") name = "Session (5 hr)";
                else if (kind == "weekly_all") name = "Weekly (7 days)";
                else name = (r.Obj("scope")?.Obj("model")?.Str("display_name") ?? kind) + " (weekly)";
                DateTime? resets = null;
                if (DateTimeOffset.TryParse(r.Str("resets_at") ?? "", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                    resets = at.UtcDateTime;
                found.Add(new Limit { Kind = kind, Name = name, Percent = (int)Math.Round(percent.Value), Severity = r.Str("severity") ?? "normal", Resets = resets });
            }
            return found;
        }

        // The last figures, kept on disk so a fresh copy shows them the instant it starts and knows when they were read
        public static void SaveState(List<Limit> limits, DateTime at)
        {
            var rows = new List<object>();
            foreach (var l in limits)
            {
                var row = new JObject { ["kind"] = l.Kind, ["name"] = l.Name, ["percent"] = (long)l.Percent, ["severity"] = l.Severity };
                if (l.Resets.HasValue) row["resets"] = Spans.ToEpoch(l.Resets.Value);
                rows.Add(row);
            }
            Paths.WriteAtomic(Paths.StatePath, Json.Write(new JObject { ["at"] = Spans.ToEpoch(at), ["limits"] = rows }));
        }

        public static bool LoadState(out DateTime at, out List<Limit> limits)
        {
            at = DateTime.MinValue;
            limits = new List<Limit>();
            var json = Json.ParseObject(Paths.Read(Paths.StatePath));
            var stamp = json?.Num("at");
            var rows = json?.Arr("limits");
            if (!stamp.HasValue || rows == null) return false;
            at = Spans.FromEpoch(stamp.Value);
            foreach (var r in rows.OfType<JObject>())
            {
                var p = r.Num("percent");
                if (r.Str("kind") == null || r.Str("name") == null || !p.HasValue) continue;
                var resets = r.Num("resets");
                limits.Add(new Limit
                {
                    Kind = r.Str("kind"), Name = r.Str("name"), Percent = (int)p.Value, Severity = r.Str("severity") ?? "normal",
                    Resets = resets.HasValue ? Spans.FromEpoch(resets.Value) : (DateTime?)null,
                });
            }
            return limits.Count > 0;
        }

        // the session first, the week next, each model's line after
        public static List<Limit> Ordered(IEnumerable<Limit> limits) =>
            limits.OrderBy(l => l.Kind == "session" ? 0 : l.Kind == "weekly_all" ? 1 : 2).ToList();
    }
}
