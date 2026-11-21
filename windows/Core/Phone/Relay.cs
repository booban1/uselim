using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Uselim
{
    // What the relay asks of the app around it: the limits, the PC's idle time and lock, links into VS Code, the phone
    // app's own files, and pictures fitted with Windows' codecs. The Mac-side checks hand in stand-ins.
    public interface IRelayHost
    {
        List<Limit> Limits(out DateTime? at);
        // seconds since the last keyboard or pointer input, and whether the screen is locked
        double IdleSeconds(out bool locked);
        void OpenLink(string url);
        byte[] WebFile(string name);
        Fitted FitFile(string path, int longSide);
        Fitted FitBytes(byte[] data, int longSide);
        void Saved(string name, string value);
    }

    public sealed class RelaySettings
    {
        public string Dir;
        public int Port = 47472;
        public double IdleSeconds = 30, BodySeconds = 30, PairSeconds = 600;
        public int Connections = 24;
        public string TailnetName = "", TailnetLogin = "";
        public bool IsTest;

        // the Mac-side checks' build: a folder and a port of its own, from the same variables Tools/test-relay.sh sets
        public static RelaySettings FromTestEnvironment()
        {
            string Env(string name) => Environment.GetEnvironmentVariable(name);
            double Num(string name, double fallback) => double.TryParse(Env(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            return new RelaySettings
            {
                Dir = Env("USELIM_TEST_RELAY") ?? Path.Combine(Path.GetTempPath(), "uselim-relay-test"),
                Port = (int)Num("USELIM_TEST_PORT", 47572),
                IdleSeconds = Num("USELIM_TEST_IDLE", 30),
                BodySeconds = Num("USELIM_TEST_BODY", 30),
                Connections = (int)Num("USELIM_TEST_CONNECTIONS", 24),
                PairSeconds = Num("USELIM_TEST_PAIR_SECONDS", 600),
                TailnetName = Env("USELIM_TEST_NAME") ?? "mac.example.ts.net",
                TailnetLogin = Env("USELIM_TEST_LOGIN") ?? "",
                IsTest = true,
            };
        }
    }

    // The phone relay, the Mac's Relay.swift on Windows: Uselim's side of the Uselim mod that runs in every Claude Code
    // chat. Both share the relay folder (%LOCALAPPDATA%\Uselim\relay), laid out at the top of the mod's
    // hooks/register.ts: the mod keeps chats/, history/, replies/ and asks/ current and reads inbox/ and away.json; Uselim
    // writes inbox/ (messages and the phone's Stop), files/ (what the phone sends along) and away.json, serves the phone's
    // web app with the chats' state, holds each ask open for the mod until the phone answers it or stops the turn, logs
    // every reply, ask and limit notice for the phone's Notifications screen, and pushes each one the phone's switches
    // let through.
    //
    // The server listens on 127.0.0.1 alone; Tailscale Serve brings it to the phone over the tailnet, with HTTPS. Every
    // /api and /relay request carries the X-Uselim header, which a page on another site can add only through a CORS
    // preflight the server turns away, and names a Host the server knows, which turns away DNS rebinding. Every one
    // carries a key as well: a phone's own key, or from the PC itself relay\local.key, which this user alone can read.
    public sealed class Relay
    {
        private const double IdleAway = 120;
        // a chat whose mod has been silent past this has closed
        private const double AliveWindow = 90_000;

        public readonly RelaySettings Settings;
        public readonly string Dir;
        private readonly IRelayHost host;
        private readonly Loop loop;
        private readonly Loop reader = new Loop("Uselim transcripts");
        private Server server;
        private Push push;
        private Loop.Later ticker;
        private bool running;

        private bool away;
        private DateTime awayWritten = DateTime.MinValue;
        private DateTime phoneActive = DateTime.MinValue;
        private readonly Dictionary<string, List<Waiter>> waiters = new Dictionary<string, List<Waiter>>();
        private readonly Dictionary<string, Tuple<JObject, DateTime>> answers = new Dictionary<string, Tuple<JObject, DateTime>>();
        private readonly Dictionary<string, DateTime> stops = new Dictionary<string, DateTime>();
        private int nextWaiter, sent, noticeCount;
        private readonly Dictionary<string, double> replySeen = new Dictionary<string, double>();
        private readonly HashSet<string> askSeen = new HashSet<string>();
        private DateTime swept = DateTime.MinValue, titled = DateTime.MinValue, pastRead = DateTime.MinValue;
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>();
        private readonly Dictionary<string, Tuple<string, DateTime>> pendingTitles = new Dictionary<string, Tuple<string, DateTime>>();
        private Dictionary<string, double> hidden = new Dictionary<string, double>();
        private readonly Dictionary<string, Transcript> transcripts = new Dictionary<string, Transcript>();
        private List<PastChat> past = new List<PastChat>();
        private readonly Dictionary<string, DateTime> woken = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> idleSince = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> revealed = new Dictionary<string, string>();
        private DateTime freshAsked = DateTime.MinValue;
        private HashSet<string> freshBefore = new HashSet<string>();
        private readonly HashSet<string> freshTaken = new HashSet<string>();
        private bool isFreshLate;
        private static readonly string[] notifyKinds = { "main", "replies", "questions", "permissions", "limits" };
        private readonly Dictionary<string, bool> notify = new Dictionary<string, bool>
        {
            ["main"] = true, ["replies"] = true, ["questions"] = true, ["permissions"] = true, ["limits"] = false,
        };
        private List<JObject> notices = new List<JObject>();
        private double noticesAt;
        private Tuple<string, DateTime> viewing;

        private sealed class Waiter
        {
            public int Id;
            public bool Always;
            public Action<Response> Reply;
        }

        public Relay(RelaySettings settings, IRelayHost host, Loop loop)
        {
            Settings = settings;
            Dir = settings.Dir;
            this.host = host;
            this.loop = loop;
            tailnetName = settings.TailnetName ?? "";
            tailnetLogin = settings.TailnetLogin ?? "";
        }

        private string At(params string[] parts) => Path.Combine(new[] { Dir }.Concat(parts).ToArray());

        // the key the PC's own callers send, the mod's waits and --permission: 32 random bytes in hex, made by the relay
        public static string LocalKey(string dir)
        {
            var text = (Paths.Read(Path.Combine(dir, "local.key")) ?? "").Trim();
            return text.Length == 64 && text.All(Uri.IsHexDigit) ? text : null;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            foreach (var folder in new[] { "chats", "history", "replies", "inbox", "asks", "files", "images" })
                Directory.CreateDirectory(At(folder));
            var saved = ReadJson(At("settings.json"))?.Obj("notify");
            if (saved != null)
                foreach (var kind in notifyKinds)
                    if (saved.Bool(kind) is bool on) notify[kind] = on;
            hidden = ToDoubles(ReadJson(At("hidden.json"))?.Obj("hidden"));
            var savedNotices = ReadJson(At("notices.json"));
            notices = savedNotices?.Arr("notices")?.OfType<JObject>().ToList() ?? new List<JObject>();
            noticesAt = savedNotices?.Num("at") ?? 0;
            localKey = MakeLocalKey();
            ReadPhones();
            ReadGuard();
            push = new Push(Dir, loop);
            push.Keep(PushTags());
            // what is on disk at the start counts as seen, so a restart sends only what arrives after it
            Scan(false);
            server = new Server(Settings.Port, At("spool"), loop, Admit, Route, Settings.IdleSeconds, Settings.Connections, Settings.BodySeconds);
            server.Start();
            Tick();
        }

        // the Phone switch off: the port closes, the waits held for the mod let go, and the chats' asks stay with VS Code
        public void Stop()
        {
            if (!running) return;
            running = false;
            ticker?.Cancel();
            ReleaseAll(new JObject { ["state"] = "gone" });
            server?.Stop();
            server = null;
        }

        private static Dictionary<string, double> ToDoubles(JObject o)
        {
            var d = new Dictionary<string, double>();
            if (o == null) return d;
            foreach (var kv in o)
                if (kv.Value is long l) d[kv.Key] = l;
                else if (kv.Value is double v) d[kv.Key] = v;
            return d;
        }

        private static JObject FromDoubles(Dictionary<string, double> d)
        {
            var o = new JObject();
            foreach (var kv in d) o[kv.Key] = kv.Value;
            return o;
        }

        // Pairing
        //
        // A phone pairs once with six digits from Pair a phone and gets its own key: 32 random bytes, kept on the phone
        // and sent with every call, and kept on the PC as their SHA-256 beside the phone's name in relay\phones.json. The
        // digits pair one phone, within ten minutes, while Pair a phone stays open; Remove there ends a phone's key and
        // its notifications.

        public sealed class Phone
        {
            public string Id, Name, Hash;
            public DateTime Paired, Seen;
        }

        private List<Phone> phones = new List<Phone>();
        private DateTime seenSaved = DateTime.MinValue;
        private string pairCode;
        private DateTime pairCodeUntil = DateTime.MinValue;
        private Dictionary<string, DateTime> spent = new Dictionary<string, DateTime>();
        // Pair a phone's redraw: a phone paired, or one left the list
        public Action PhonesChanged;
        private string tailnetName, tailnetLogin;
        private bool isLoginNoted;
        // wrong codes per device (the phone's tailnet address, as Tailscale Serve forwards it): the distinct codes sent,
        // so a phone repeating its one stale code counts once; from the fifth, the device pauses a minute, doubling to an
        // hour, and every code from it waits out the pause, the right one included. A right code leaves the count as it
        // stands; the count lapses a day after its last miss. A paired device's count lives in relay\guard.json too.
        private readonly Dictionary<string, Count> misses = new Dictionary<string, Count>();

        private sealed class Count
        {
            public HashSet<string> Codes = new HashSet<string>();
            public DateTime Until = DateTime.MinValue, Last = DateTime.MinValue;
        }

        // the PC's own name on the tailnet, the one name Host and Origin carry from the phone, and its login
        public string TailnetName
        {
            get => tailnetName;
            set => loop.Post(() =>
            {
                if (value == tailnetName) return;
                tailnetName = value ?? "";
                host.Saved("tailnetName", tailnetName);
            });
        }

        public string TailnetLogin
        {
            get => tailnetLogin;
            set => loop.Post(() =>
            {
                if (value == tailnetLogin) return;
                tailnetLogin = value ?? "";
                host.Saved("tailnetLogin", tailnetLogin);
            });
        }

        public List<Phone> Phones => phones.ToList();
        public string PairCode => pairCode;
        public int PairSecondsLeft => Math.Max(0, (int)Math.Ceiling((pairCodeUntil - DateTime.UtcNow).TotalSeconds));

        private void ReadPhones()
        {
            phones = (ReadJson(At("phones.json"))?.Arr("phones") ?? new List<object>()).OfType<JObject>()
                .Where(r => r.Str("id") != null && r.Str("hash") != null)
                .Select(r => new Phone
                {
                    Id = r.Str("id"), Name = r.Str("name") ?? "Phone", Hash = r.Str("hash"),
                    Paired = Spans.FromEpoch((r.Num("paired") ?? 0) / 1000), Seen = Spans.FromEpoch((r.Num("seen") ?? 0) / 1000),
                }).ToList();
        }

        private void SavePhones()
        {
            var rows = phones.Select(p => (object)new JObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["hash"] = p.Hash, ["paired"] = Millis(p.Paired), ["seen"] = Millis(p.Seen),
            }).ToList();
            WriteJson(new JObject { ["phones"] = rows }, At("phones.json"));
            Secure.File(At("phones.json"));
        }

        // Pair a phone opening, or its minutes running out: fresh digits from the system's secure random source
        public string OpenPairing()
        {
            if (pairCode != null) Spend(pairCode);
            var bytes = Push.Random(4);
            var code = ((BitConverter.ToUInt32(bytes, 0)) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            pairCode = code;
            pairCodeUntil = DateTime.UtcNow.AddSeconds(Settings.PairSeconds);
            return code;
        }

        // Pair a phone closing: its digits pair nothing more
        public void ClosePairing()
        {
            if (pairCode != null) Spend(pairCode);
            pairCode = null;
        }

        private void Spend(string code)
        {
            var now = DateTime.UtcNow;
            spent = spent.Where(kv => (now - kv.Value).TotalSeconds < 3600).ToDictionary(kv => kv.Key, kv => kv.Value);
            spent[PairingTag(code)] = now;
        }

        // Remove in Pair a phone: the phone's key ends, and its notifications with it
        public void RemovePhone(string id)
        {
            var phone = phones.FirstOrDefault(p => p.Id == id);
            if (phone == null) return;
            phones.RemoveAll(p => p.Id == id);
            SavePhones();
            push.Keep(PushTags());
            Log.Note("relay: " + phone.Name + " removed");
            PhonesChanged?.Invoke();
        }

        private HashSet<string> PushTags() => new HashSet<string>(phones.Select(p => p.Id));

        private enum Kind { Phone, PairCode, Spent, Wrong, Blank }

        // what a request's key names: a phone's own key; on the pairing call, the digits on show; digits spent within the
        // hour; or nothing
        private Kind KeyOf(string given, string path, out string phoneId)
        {
            phoneId = null;
            if (string.IsNullOrEmpty(given)) return Kind.Blank;
            var hash = Sha256Hex(given);
            var phone = phones.FirstOrDefault(p => p.Hash == hash);
            if (phone != null)
            {
                phoneId = phone.Id;
                return Kind.Phone;
            }
            var digits = new string(given.Where(char.IsDigit).ToArray());
            var isDigits = given.All(c => char.IsDigit(c) || c == ' ');
            if (path == "/api/pair" && isDigits && pairCode != null && DateTime.UtcNow < pairCodeUntil && digits == pairCode) return Kind.PairCode;
            if (isDigits && digits.Length == 6 && (spent.ContainsKey(PairingTag(digits)) || digits == pairCode)) return Kind.Spent;
            return Kind.Wrong;
        }

        // the phone's name from its browser: the kind of device its User-Agent names, a number added past the first
        private string PhoneName(string agent)
        {
            agent = agent ?? "";
            var kind = agent.Contains("iPhone") ? "iPhone" : agent.Contains("iPad") ? "iPad"
                : agent.Contains("Android") ? "Android phone" : agent.Contains("Macintosh") ? "Mac" : "Phone";
            var taken = new HashSet<string>(phones.Select(p => p.Name));
            if (!taken.Contains(kind)) return kind;
            var number = 2;
            while (taken.Contains(kind + " " + number)) number++;
            return kind + " " + number;
        }

        // a phone's request: its last sight, kept to disk once a minute at most
        private void See(string id)
        {
            var phone = phones.FirstOrDefault(p => p.Id == id);
            if (phone == null) return;
            phone.Seen = DateTime.UtcNow;
            if ((DateTime.UtcNow - seenSaved).TotalSeconds <= 60) return;
            seenSaved = DateTime.UtcNow;
            SavePhones();
        }

        // the phone's pairing call: the digits on show in Pair a phone, traded for the phone's own key; they pair this one
        // phone
        private Response Pair(Request request)
        {
            if (KeyOf(request.Header("x-uselim-key"), "/api/pair", out _) != Kind.PairCode) return Response.Text("Pairing refused", 400);
            if (!IsRemote(request)) return Response.Text("Phone only", 403);
            var token = Base64Url.Encode(Push.Random(32));
            var id = "p" + string.Concat(Push.Random(4).Select(b => b.ToString("x2")));
            var phone = new Phone { Id = id, Name = PhoneName(request.Header("user-agent")), Hash = Sha256Hex(token), Paired = DateTime.UtcNow, Seen = DateTime.UtcNow };
            phones.Add(phone);
            SavePhones();
            if (pairCode != null) Spend(pairCode);
            pairCode = null;
            Log.Note("relay: " + phone.Name + " paired");
            PhonesChanged?.Invoke();
            return Response.Json(new JObject { ["token"] = token, ["name"] = phone.Name });
        }

        // a request from the phone through Tailscale Serve, which forwards with the tailnet's own name and the phone's address
        private static bool IsRemote(Request request) =>
            request.Header("x-forwarded-for") != null || (request.Header("host") ?? "").ToLowerInvariant().Contains(".ts.net");

        // the device behind a request: the last address in X-Forwarded-For, the one Tailscale Serve adds itself
        private static string Device(Request request)
        {
            var last = (request.Header("x-forwarded-for") ?? "").Split(',').Last().Trim();
            return last.Length == 0 ? "unknown" : last;
        }

        // the seconds a device still waits, while it does; a day after its last miss, its count starts over
        private int? PausedFor(string device)
        {
            if (!misses.TryGetValue(device, out var entry)) return null;
            if ((DateTime.UtcNow - entry.Last).TotalSeconds > 86_400)
            {
                misses.Remove(device);
                return null;
            }
            var left = (entry.Until - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : (int?)null;
        }

        private static string Clean(string code) => new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        private void Missed(string device, string given)
        {
            if (!misses.TryGetValue(device, out var entry)) misses[device] = entry = new Count();
            entry.Codes.Add(PairingTag(Clean(given)));
            entry.Last = DateTime.UtcNow;
            if (entry.Codes.Count >= 5)
            {
                var pause = Math.Min(60 * Math.Pow(2, entry.Codes.Count - 5), 3600);
                entry.Until = DateTime.UtcNow.AddSeconds(pause);
                Log.Note("relay: " + entry.Codes.Count + " wrong codes from " + device + ", paused " + (int)pause + " s");
            }
            if (known.ContainsKey(device)) SaveGuard();
        }

        // The local key and the guard across devices

        private string localKey = "";

        private string MakeLocalKey()
        {
            var key = LocalKey(Dir);
            if (key != null) return key;
            key = string.Concat(Push.Random(32).Select(b => b.ToString("x2")));
            var path = At("local.key");
            File.WriteAllText(path, key, new UTF8Encoding(false));
            Secure.File(path);
            Log.Note("relay: made the local key");
            return key;
        }

        // the same length and every byte compared, so the time taken stays the same wherever a wrong key differs
        private bool IsLocalKey(string given)
        {
            if (given == null || localKey.Length == 0 || given.Length != localKey.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++) diff |= given[i] ^ localKey[i];
            return diff == 0;
        }

        // a code's mark, keyed with the local key, so the guard's file alone keeps the codes secret
        private string PairingTag(string code)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(localKey)))
                return string.Concat(hmac.ComputeHash(Encoding.UTF8.GetBytes(code)).Take(8).Select(b => b.ToString("x2")));
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        // X-Forwarded-For names the device, and a program on the PC can name any address in it, so the per-device count
        // alone would let it try codes endlessly. The codes sent from devices yet to pair count together: from the tenth
        // distinct one in a day, every such device pauses a minute, doubling to an hour, while a device that paired keeps
        // its own count and goes on working. Both live in relay\guard.json, across restarts and updates.
        private Dictionary<string, double> known = new Dictionary<string, double>();
        private Count strangers = new Count();

        private void ReadGuard()
        {
            var saved = ReadJson(At("guard.json"));
            known = ToDoubles(saved?.Obj("known"));
            Count Read(JObject r) => new Count
            {
                Codes = new HashSet<string>((r?.Arr("codes") ?? new List<object>()).OfType<string>()),
                Until = Spans.FromEpoch((r?.Num("until") ?? 0) / 1000),
                Last = Spans.FromEpoch((r?.Num("last") ?? 0) / 1000),
            };
            strangers = Read(saved?.Obj("strangers"));
            foreach (var kv in saved?.Obj("misses") ?? new JObject())
                if (known.ContainsKey(kv.Key) && kv.Value is JObject r) misses[kv.Key] = Read(r);
        }

        private void SaveGuard()
        {
            JObject Record(Count c) => new JObject { ["codes"] = c.Codes.Cast<object>().ToList(), ["until"] = Millis(c.Until), ["last"] = Millis(c.Last) };
            var paired = new JObject();
            foreach (var kv in misses.Where(kv => known.ContainsKey(kv.Key))) paired[kv.Key] = Record(kv.Value);
            WriteJson(new JObject { ["known"] = FromDoubles(known), ["strangers"] = Record(strangers), ["misses"] = paired }, At("guard.json"));
            Secure.File(At("guard.json"));
        }

        // a device that sent a right code; a request naming zero addresses ("unknown") stays a stranger
        private void Know(string device)
        {
            if (device == "unknown" || known.ContainsKey(device)) return;
            known[device] = Millis(DateTime.UtcNow);
            SaveGuard();
            Log.Note("relay: " + device + " paired");
        }

        private int? StrangerPause(string device) => known.ContainsKey(device) ? null : StrangersWait();

        // the seconds left in the pause across unpaired devices; a day after the last of their misses, the count starts over
        private int? StrangersWait()
        {
            if (strangers.Codes.Count > 0 && (DateTime.UtcNow - strangers.Last).TotalSeconds > 86_400)
            {
                strangers = new Count();
                SaveGuard();
                return null;
            }
            var left = (strangers.Until - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : (int?)null;
        }

        private void StrangerMissed(string given)
        {
            strangers.Codes.Add(PairingTag(Clean(given)));
            strangers.Last = DateTime.UtcNow;
            if (strangers.Codes.Count >= 10)
            {
                var pause = Math.Min(60 * Math.Pow(2, strangers.Codes.Count - 10), 3600);
                strangers.Until = DateTime.UtcNow.AddSeconds(pause);
                Log.Note("relay: " + strangers.Codes.Count + " wrong codes from unpaired devices, all paused " + (int)pause + " s");
            }
            SaveGuard();
        }

        // an older mod's wait, sent before the local key: it passes for the ask held that moment, whose id only a reader of
        // relay\asks knows
        private bool IsLiveAsk(Request request)
        {
            var session = request.Arg("session");
            var ask = request.Arg("ask");
            if (!IsSessionId(session) || string.IsNullOrEmpty(ask)) return false;
            return ReadJson(At("asks", session + ".json"))?.Str("id") == ask;
        }

        // The PC, every two seconds

        private void Tick()
        {
            if (!running) return;
            try
            {
                var now = ReadAway();
                if (now != away || (DateTime.UtcNow - awayWritten).TotalSeconds > 30)
                {
                    if (now != away) Log.Note(now ? "relay: away from the PC" : "relay: back at the PC");
                    away = now;
                    WriteJson(new JObject { ["away"] = away, ["at"] = Millis(DateTime.UtcNow) }, At("away.json"));
                    awayWritten = DateTime.UtcNow;
                    if (!away) ReleaseAll(new JObject { ["state"] = "desk" });
                }
                foreach (var k in answers.Where(kv => (DateTime.UtcNow - kv.Value.Item2).TotalSeconds >= 600).Select(kv => kv.Key).ToList()) answers.Remove(k);
                foreach (var k in stops.Where(kv => (DateTime.UtcNow - kv.Value).TotalSeconds >= 60).Select(kv => kv.Key).ToList()) stops.Remove(k);
                Scan(true);
                SettleCards();
                RevealWaiting();
                if ((DateTime.UtcNow - swept).TotalSeconds > 600) Sweep();
                if ((DateTime.UtcNow - titled).TotalSeconds > 10) ReadTitles();
                if ((DateTime.UtcNow - pastRead).TotalSeconds > 60) ReadPast();
            }
            finally
            {
                ticker = loop.After(2, Tick);
            }
        }

        private void ReadPast()
        {
            pastRead = DateTime.UtcNow;
            reader.Post(() =>
            {
                var found = PastChat.Read();
                loop.Post(() => past = found);
            });
        }

        private PastChat PastChatOf(string session) => past.FirstOrDefault(p => p.Session == session);

        // a chat the phone may write to: open in VS Code now, or one of the last two weeks'
        private bool IsKnown(string session) => File.Exists(At("chats", session + ".json")) || PastChatOf(session) != null;

        private string CwdOf(string session) => ReadJson(At("chats", session + ".json"))?.Str("cwd") ?? PastChatOf(session)?.Cwd;

        private bool IsLive(string session) => LiveChats().Any(c => c.Str("session") == session);

        // a closed chat opens in VS Code through the extension's own link, and its mod then takes the message waiting for it
        private void Wake(string session)
        {
            if (IsLive(session)) return;
            if (woken.TryGetValue(session, out var last) && (DateTime.UtcNow - last).TotalSeconds <= 60) return;
            woken[session] = DateTime.UtcNow;
            host.OpenLink("vscode://anthropic.claude-code/open?session=" + session);
            Log.Note("relay: a phone message opened " + (PastChatOf(session)?.Title ?? session) + " in VS Code");
        }

        // the phone's New chat: a fresh tab through the extension's own link, in VS Code's first folder. The first chat
        // to register after the link with zero turns is the new one, and the phone sends its words there the usual way.
        // After 30 s with zero new chats the phone keeps its message; a tab that registers later takes the next New chat
        // within ten minutes, in place of a second tab.
        private void OpenFresh(Action<Response> reply)
        {
            phoneActive = DateTime.UtcNow;
            if (isFreshLate && (DateTime.UtcNow - freshAsked).TotalSeconds < 600)
            {
                var late = FreshChat();
                if (late != null)
                {
                    isFreshLate = false;
                    freshTaken.Add(late);
                    Log.Note("relay: the phone's new chat took the tab that registered late");
                    reply(Response.Json(new JObject { ["ok"] = true, ["session"] = late }));
                    return;
                }
            }
            freshAsked = DateTime.UtcNow;
            freshBefore = new HashSet<string>(Sessions("chats"));
            isFreshLate = false;
            host.OpenLink("vscode://anthropic.claude-code/open");
            Log.Note("relay: the phone opened a new chat in VS Code");
            var started = DateTime.UtcNow;
            void Look()
            {
                var session = FreshChat();
                if (session != null)
                {
                    freshTaken.Add(session);
                    Log.Note("relay: the new chat registered after " + (int)Math.Round((DateTime.UtcNow - started).TotalSeconds) + " s");
                    reply(Response.Json(new JObject { ["ok"] = true, ["session"] = session }));
                    return;
                }
                if ((DateTime.UtcNow - started).TotalSeconds >= 30)
                {
                    isFreshLate = true;
                    Log.Note("relay: the new chat had yet to register after 30 s; the phone keeps its message");
                    reply(Response.Json(new JObject { ["ok"] = false, ["state"] = "opening" }));
                    return;
                }
                loop.After(0.5, Look);
            }
            Look();
        }

        // a live chat that registered after the last fresh tab, with zero turns and still without a phone message
        private string FreshChat() => LiveChats().Select(c => c.Str("session"))
            .FirstOrDefault(s => s != null && !freshBefore.Contains(s) && !freshTaken.Contains(s)
                && string.IsNullOrEmpty(ReadJson(At("chats", s + ".json"))?.Str("began")));

        // a chat can take a phone message (its ack) and start zero turns: Claude Code holds the prompt until the chat's
        // tab shows in VS Code. After six idle seconds Uselim opens that tab through the extension's own link, once per
        // message and only within ten minutes of the message's sending or of the turn it queued behind
        private void RevealWaiting()
        {
            var now = DateTime.UtcNow;
            foreach (var chat in LiveChats())
            {
                var session = chat.Str("session");
                if (session == null) continue;
                if (chat.Str("status") != "idle")
                {
                    idleSince.Remove(session);
                    continue;
                }
                if (!idleSince.TryGetValue(session, out var since)) idleSince[session] = since = now;
                var ack = ReadText(At("inbox", session + ".ack"));
                if ((now - since).TotalSeconds < 6 || ack.Length == 0) continue;
                var messages = Inbox(session);
                var historyAt = ReadJson(At("history", session + ".json"))?.Num("at") ?? 0;
                var taken = messages.Where(m =>
                {
                    var age = Millis(now) - Math.Max(m.Num("at") ?? 0, historyAt);
                    return string.CompareOrdinal(m.Str("id") ?? "", ack) <= 0 && age >= 6_000 && age < 600_000;
                }).ToList();
                var newest = taken.LastOrDefault()?.Str("id");
                if (newest == null || (revealed.TryGetValue(session, out var shown) && shown == newest)) continue;
                var settled = SettledId(session, messages, historyAt);
                if (string.CompareOrdinal(newest, settled) <= 0) continue;
                revealed[session] = newest;
                host.OpenLink("vscode://anthropic.claude-code/open?session=" + session);
                Log.Note("relay: opened " + Name(session, chat) + " in VS Code for the phone message waiting on it");
            }
        }

        // permission cards and their waits: a card on a chat gone idle outlived its turn (denied at the PC), and a wait
        // whose card went (the PC answered, the tool ran) lets go, so the --permission behind it leaves
        private void SettleCards()
        {
            var now = Millis(DateTime.UtcNow);
            foreach (var chat in LiveChats().Where(c => c.Str("status") == "idle"))
            {
                var session = chat.Str("session");
                if (session == null) continue;
                var path = At("asks", session + ".json");
                var card = ReadJson(path);
                if (card == null || card.Str("kind") != "permission" || now - (card.Num("at") ?? now) <= 5_000) continue;
                File.WriteAllText(path, "");
                Log.Note("relay: the permission card on " + Name(session, chat) + " came down with its turn");
            }
            foreach (var key in waiters.Keys.ToList())
            {
                var slash = key.IndexOf('/');
                if (slash < 0) continue;
                if (ReadJson(At("asks", key.Substring(0, slash) + ".json"))?.Str("id") == key.Substring(slash + 1)) continue;
                var list = waiters[key];
                waiters.Remove(key);
                foreach (var w in list) w.Reply(Response.Json(new JObject { ["state"] = "gone" }));
            }
        }

        // the last phone message the chat's history holds: the mod's mark, else the newest one sent before the
        // history's last write
        private string SettledId(string session, List<JObject> messages, double historyAt)
        {
            var entered = ReadText(At("inbox", session + ".entered"));
            if (entered.Length > 0) return entered;
            return messages.Where(m => (m.Num("at") ?? 0) <= historyAt).Select(m => m.Str("id")).Where(s => s != null)
                .OrderBy(s => s, StringComparer.Ordinal).LastOrDefault() ?? "";
        }

        // a closed chat as the phone reads it: its name and folder, its history from the transcript page by page
        private JObject ClosedChatJson(string session)
        {
            var found = PastChatOf(session);
            if (found == null) return null;
            var ack = ReadText(At("inbox", session + ".ack"));
            var outbox = Inbox(session).Select(m =>
            {
                var row = Copy(m);
                row["delivered"] = string.CompareOrdinal(m.Str("id") ?? "", ack) <= 0;
                return (object)row;
            }).ToList();
            return new JObject
            {
                ["session"] = session, ["cwd"] = found.Cwd, ["project"] = Words.LastName(found.Cwd), ["title"] = found.Title,
                ["began"] = "", ["status"] = "closed", ["tools"] = new List<object>(), ["at"] = found.At,
                ["history"] = new List<object>(), ["historyAt"] = 0L, ["settled"] = "", ["outbox"] = outbox,
                ["now"] = Millis(DateTime.UtcNow),
            };
        }

        // the transcript of a chat, indexed up to now; on the transcript loop
        private Transcript TranscriptOf(string session, string cwd)
        {
            if (transcripts.TryGetValue(session, out var held))
            {
                held.Refresh();
                return held;
            }
            var path = Transcript.PathFor(cwd, session);
            // a chat registered under a folder a shell's cd took it to keeps its transcript under the one it started in:
            // found by its id, which names one file across the projects
            if (!File.Exists(path))
            {
                var projects = Path.Combine(Paths.ClaudeFolder, "projects");
                try
                {
                    if (Directory.Exists(projects))
                        foreach (var folder in Directory.GetDirectories(projects))
                        {
                            var candidate = Path.Combine(folder, session + ".jsonl");
                            if (!File.Exists(candidate)) continue;
                            path = candidate;
                            break;
                        }
                }
                catch (Exception) { }
            }
            var found = new Transcript(path);
            transcripts[session] = found;
            found.Refresh();
            return found;
        }

        private void ReadTitles()
        {
            titled = DateTime.UtcNow;
            var live = LiveChats().Where(c => c.Str("session") != null && c.Str("cwd") != null)
                .Select(c => Tuple.Create(c.Str("session"), c.Str("cwd"))).ToList();
            reader.Post(() =>
            {
                var found = live.Select(c => Tuple.Create(c.Item1, TranscriptOf(c.Item1, c.Item2).Title)).ToList();
                loop.Post(() =>
                {
                    foreach (var pair in found.Where(f => f.Item2.Length > 0))
                    {
                        // a name from the phone holds for ten minutes until the transcript carries it
                        if (pendingTitles.TryGetValue(pair.Item1, out var wanted))
                        {
                            if (wanted.Item1 != pair.Item2 && (DateTime.UtcNow - wanted.Item2).TotalSeconds < 600) continue;
                            pendingTitles.Remove(pair.Item1);
                        }
                        titles[pair.Item1] = pair.Item2;
                    }
                });
            });
        }

        private bool ReadAway()
        {
            var idle = host.IdleSeconds(out var locked);
            var lastInput = DateTime.UtcNow.AddSeconds(-idle);
            return locked || idle >= IdleAway || phoneActive > lastInput;
        }

        // every reply and every ask, each once: logged for the Notifications screen, pushed as the phone's switches say
        private void Scan(bool sending)
        {
            var chats = new Dictionary<string, JObject>();
            foreach (var c in ReadChats())
                if (c.Str("session") is string s && !chats.ContainsKey(s)) chats[s] = c;
            foreach (var session in Sessions("replies"))
            {
                var reply = ReadJson(At("replies", session + ".json"));
                var at = reply?.Num("at");
                if (!at.HasValue) continue;
                if (replySeen.TryGetValue(session, out var seen) && seen >= at.Value) continue;
                replySeen[session] = at.Value;
                var text = reply.Str("text");
                if (!sending || reply.Str("reason") != "answer" || string.IsNullOrEmpty(text)) continue;
                chats.TryGetValue(session, out var chat);
                Tell("replies", Name(session, chat), Preview(text, 300), session, session);
            }
            foreach (var session in Sessions("asks"))
            {
                var ask = ReadJson(At("asks", session + ".json"));
                var id = ask?.Str("id");
                if (id == null || !askSeen.Add(session + "/" + id) || !sending) continue;
                var isQuestion = ask.Str("kind") == "question";
                chats.TryGetValue(session, out var chat);
                Tell(isQuestion ? "questions" : "permissions", Name(session, chat ?? ask) + " · " + (isQuestion ? "Question" : "Permission"),
                    AskText(ask), session, session + "-ask");
            }
        }

        // a limit's notice from the app's alerts, named the short way ("Weekly at 61%")
        public void LimitNotice(string title, string body)
        {
            var shortTitle = Regex.Replace(title, " \\([^)]*\\)", "");
            loop.Post(() => { if (running) Tell("limits", shortTitle, body ?? "", null, "limits"); });
        }

        // one notice: into the log whatever the switches say, to the phone while the main switch and its kind's are on
        // and its chat is off the phone's screen
        private void Tell(string kind, string title, string body, string session, string tag)
        {
            var now = Millis(DateTime.UtcNow);
            var isOnScreen = session != null && viewing != null && viewing.Item1 == session && (DateTime.UtcNow - viewing.Item2).TotalSeconds < 8;
            noticeCount++;
            var row = new JObject
            {
                ["id"] = now.ToString("0000000000000", CultureInfo.InvariantCulture) + "-" + (noticeCount % 10_000).ToString("D4", CultureInfo.InvariantCulture),
                ["kind"] = kind, ["title"] = title, ["body"] = body, ["at"] = now, ["read"] = isOnScreen,
            };
            if (session != null) row["session"] = session;
            var twoWeeks = now - 14 * 86_400_000.0;
            notices = notices.Where(n => (n.Num("at") ?? 0) > twoWeeks).Concat(new[] { row }).ToList();
            if (notices.Count > 300) notices = notices.Skip(notices.Count - 300).ToList();
            SaveNotices();
            if (isOnScreen || !notify["main"] || !notify[kind]) return;
            push.Send(title, body, session == null ? "/" : "/#" + session, tag);
        }

        private void SaveNotices()
        {
            noticesAt = Math.Max(Millis(DateTime.UtcNow), noticesAt + 1);
            WriteJson(new JObject { ["notices"] = notices.Cast<object>().ToList(), ["at"] = noticesAt }, At("notices.json"));
        }

        private int Unread => notices.Count(n => n.Bool("read") != true);

        // the log for the phone, newest first; one the phone holds already stays off the wire
        private JObject NoticesJson(double? since)
        {
            var now = Millis(DateTime.UtcNow);
            if (since.HasValue && since.Value >= noticesAt)
                return new JObject { ["same"] = true, ["at"] = noticesAt, ["unread"] = (long)Unread, ["now"] = now };
            return new JObject
            {
                ["notices"] = Enumerable.Reverse(notices).Cast<object>().ToList(), ["at"] = noticesAt, ["unread"] = (long)Unread, ["now"] = now,
            };
        }

        // read: a notice opened on the phone, or a chat's once the phone opens it; clear: a day's, by their ids
        private Response ChangeNotices(Request request)
        {
            var body = Json.ParseObject(Encoding.UTF8.GetString(request.Body));
            if (body == null) return Response.Text("Change refused", 400);
            var read = new HashSet<string>((body.Arr("read") ?? new List<object>()).OfType<string>());
            var clear = new HashSet<string>((body.Arr("clear") ?? new List<object>()).OfType<string>());
            var session = body.Str("session");
            var changed = false;
            if (clear.Count > 0)
            {
                var before = notices.Count;
                notices.RemoveAll(n => clear.Contains(n.Str("id") ?? ""));
                changed = notices.Count != before;
            }
            foreach (var n in notices.Where(n => n.Bool("read") != true))
            {
                if (!read.Contains(n.Str("id") ?? "") && !(session != null && n.Str("session") == session)) continue;
                n["read"] = true;
                changed = true;
            }
            if (changed) SaveNotices();
            return Response.Json(new JObject { ["ok"] = true, ["at"] = noticesAt, ["unread"] = (long)Unread });
        }

        private JObject NotifyJson()
        {
            var o = new JObject();
            foreach (var kind in notifyKinds) o[kind] = notify[kind];
            return o;
        }

        private Response SaveSettings(Request request)
        {
            var given = Json.ParseObject(Encoding.UTF8.GetString(request.Body))?.Obj("notify");
            if (given == null) return Response.Text("Settings refused", 400);
            foreach (var kind in notifyKinds)
                if (given.Bool(kind) is bool on) notify[kind] = on;
            WriteJson(new JObject { ["notify"] = NotifyJson() }, At("settings.json"));
            Log.Note("relay: notifications set, " + string.Join(", ", notifyKinds.Where(k => notify[k]).OrderBy(k => k, StringComparer.Ordinal)));
            return Response.Json(new JObject { ["ok"] = true, ["notify"] = NotifyJson() });
        }

        // a chat by the name VS Code shows for it, for the app's own notices; null while none is known (on the loop)
        public string TitleOf(string session) => titles.TryGetValue(session, out var title) ? title : null;

        // a chat by the name VS Code shows for it, else its folder
        private string Name(string session, JObject chat) =>
            titles.TryGetValue(session, out var title) ? title : chat?.Str("project") ?? "Claude";

        // the files of chats closed for a day leave the folder, the copies of their pictures too; what the phone sent
        // along stays in files\
        private void Sweep()
        {
            swept = DateTime.UtcNow;
            var day = Millis(DateTime.UtcNow) - 86_400_000;
            var cutoff = Millis(DateTime.UtcNow) - 28 * 86_400_000.0;
            var kept = hidden.Where(kv => kv.Value > cutoff).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (kept.Count != hidden.Count)
            {
                hidden = kept;
                WriteJson(new JObject { ["hidden"] = FromDoubles(hidden) }, At("hidden.json"));
            }
            foreach (var chat in ReadChats().Where(c => (c.Num("at") ?? 0) < day))
            {
                var session = chat.Str("session");
                if (!IsSessionId(session)) continue;
                foreach (var path in new[]
                {
                    "chats/" + session + ".json", "history/" + session + ".json", "replies/" + session + ".json",
                    "inbox/" + session + ".json", "inbox/" + session + ".ack", "inbox/" + session + ".entered",
                    "inbox/" + session + ".stop", "inbox/" + session + ".switch", "inbox/" + session + ".rename",
                    "asks/" + session + ".json",
                })
                    try { File.Delete(Path.Combine(Dir, path)); } catch (Exception) { }
                var pictures = At("images", session);
                try
                {
                    if (Directory.Exists(pictures))
                    {
                        foreach (var file in Directory.GetFiles(pictures, "*.json")) File.Delete(file);
                        Directory.Delete(pictures);
                    }
                }
                catch (Exception) { }
            }
        }

        // Requests

        // the checks a request passes at its head, before any of its body is read: its Host, the header and Origin on
        // /api and /relay, and its key; an answer here ends the request
        private Response Admit(Request request)
        {
            var answer = Check(request);
            // the key that let a request from the phone in, for the route to find it still standing
            if (answer == null && IsRemote(request) && KeyOf(request.Header("x-uselim-key"), request.Path, out var id) == Kind.Phone)
                request.Key = id;
            return answer;
        }

        // a key that ended while its request's body arrived: Remove took its phone
        private bool IsEnded(string key) => key != null && phones.All(p => p.Id != key);

        private static Response TooMany(int wait) => Response.Json(new JObject { ["error"] = "Too many tries", ["wait"] = (long)wait }, 429);

        private Response Check(Request request)
        {
            if (!HostKnown(request.Header("host"))) return Response.Text("Host refused", 403);
            var isApi = request.Path.StartsWith("/api/", StringComparison.Ordinal) || request.Path.StartsWith("/relay/", StringComparison.Ordinal);
            if (isApi)
            {
                if (request.Header("x-uselim") != "1") return Response.Text("Header missing", 403);
                var origin = request.Header("origin");
                if (origin != null && !OriginKnown(origin)) return Response.Text("Origin refused", 403);
            }
            // from the tailnet: the waits stay with the PC's own processes, and every call carries a key, checked per
            // device with its pause (Missed) and across unpaired devices (StrangerMissed)
            if (IsRemote(request))
            {
                if (request.Path.StartsWith("/relay/", StringComparison.Ordinal)) return Response.Text("Local only", 403);
                if (!request.Path.StartsWith("/api/", StringComparison.Ordinal)) return null;
                // Tailscale Serve names the tailnet login behind each request: one naming another login stops here
                var login = request.Header("tailscale-user-login")?.ToLowerInvariant();
                if (login != null)
                {
                    if (!isLoginNoted)
                    {
                        isLoginNoted = true;
                        Log.Note("relay: Tailscale Serve names the login behind the phone's requests");
                    }
                    if (tailnetLogin.Length > 0 && login != tailnetLogin.ToLowerInvariant()) return Response.Text("Login refused", 403);
                }
                var from = Device(request);
                var given = request.Header("x-uselim-key");
                var key = KeyOf(given, request.Path, out var phoneId);
                // a blank key answers 401 at once, free and alike for every address
                if (key == Kind.Blank) return Response.Text("Pairing needed", 401);
                var paused = PausedFor(from);
                if (paused.HasValue) return TooMany(paused.Value);
                // spent digits count as wrong ones, and the phone hears they are spent
                var refusal = key == Kind.Spent
                    ? Response.Json(new JObject { ["error"] = "Pairing needed", ["reason"] = "spent" }, 401)
                    : Response.Text("Pairing needed", 401);
                var isWrong = key == Kind.Wrong || key == Kind.Spent;
                // a paired address's wrong key always goes into its own saved count; while the pause across unpaired
                // devices lasts it answers 429 like theirs, so every address answers alike and every try costs
                if (isWrong && known.ContainsKey(from))
                {
                    Missed(from, given ?? "");
                    var wait = PausedFor(from) ?? StrangersWait();
                    return wait.HasValue ? TooMany(wait.Value) : refusal;
                }
                var strangerWait = StrangerPause(from);
                if (strangerWait.HasValue) return TooMany(strangerWait.Value);
                switch (key)
                {
                    case Kind.Phone:
                        Know(from);
                        See(phoneId);
                        return null;
                    case Kind.PairCode:
                        Know(from);
                        return null;
                    default:
                        // a device yet to pair: its own count, and the count across them all
                        Missed(from, given ?? "");
                        StrangerMissed(given ?? "");
                        var wait = PausedFor(from) ?? StrangerPause(from);
                        return wait.HasValue ? TooMany(wait.Value) : refusal;
                }
            }
            if (isApi)
            {
                // from the PC itself: the local key, or an older mod's wait naming the ask held that moment
                var isKeyed = IsLocalKey(request.Header("x-uselim-key"));
                if (!isKeyed && !(request.Path == "/relay/wait" && IsLiveAsk(request))) return Response.Text("Key needed", 403);
            }
            return null;
        }

        // the handlers, for a request Admit let through, its body whole, its key still standing
        private void Route(Request request, Action<Response> reply)
        {
            if (IsEnded(request.Key))
            {
                reply(Response.Text("Pairing needed", 401));
                return;
            }
            var route = request.Method + " " + request.Path;
            switch (route)
            {
                case "GET /api/state":
                    viewing = null;
                    reply(Response.Json(StateJson()));
                    return;
                case "GET /api/chat":
                {
                    var session = request.Arg("session");
                    var chat = IsSessionId(session) ? ChatJson(session) ?? ClosedChatJson(session) : null;
                    if (chat == null)
                    {
                        reply(Response.Text("Unknown chat", 404));
                        return;
                    }
                    viewing = Tuple.Create(session, DateTime.UtcNow);
                    // a history the phone holds already stays off the wire
                    if (double.TryParse(request.Arg("since") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var since) && since > 0
                        && (chat.Num("historyAt") ?? 0) <= since)
                    {
                        chat.Remove("history");
                        chat["historySame"] = true;
                    }
                    reply(Response.Json(chat));
                    return;
                }
                case "GET /api/image": Picture(request, reply); return;
                case "GET /api/earlier": Earlier(request, reply); return;
                case "GET /api/tool": ToolCall(request, reply); return;
                case "POST /api/send": reply(Queue(request)); return;
                case "POST /api/new": OpenFresh(reply); return;
                case "POST /api/upload": Upload(request, reply); return;
                case "POST /api/stop": reply(StopTurn(request)); return;
                case "POST /api/switch": reply(SwitchChat(request)); return;
                case "POST /api/rename": reply(Rename(request)); return;
                case "POST /api/hide": reply(Hide(request)); return;
                case "POST /api/answer": reply(Answer(request)); return;
                case "POST /api/settings": reply(SaveSettings(request)); return;
                case "GET /api/notices":
                    viewing = null;
                    reply(Response.Json(NoticesJson(double.TryParse(request.Arg("since") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : (double?)null)));
                    return;
                case "POST /api/notices": reply(ChangeNotices(request)); return;
                case "POST /api/pair": reply(Pair(request)); return;
                case "POST /api/push": reply(Subscribe(request)); return;
                case "POST /api/test":
                    push.Send("Notifications on", "Replies and asks from your chats arrive here", "/", "test");
                    reply(Response.Json(new JObject { ["ok"] = true, ["count"] = (long)push.Count }));
                    return;
                case "GET /relay/wait": Wait(request, reply); return;
            }
            // the test build's stand-ins for Pair a phone: its digits, and its Remove
            if (Settings.IsTest && route == "POST /relay/test-pair")
            {
                reply(Response.Json(new JObject { ["code"] = OpenPairing() }));
                return;
            }
            if (Settings.IsTest && route == "POST /relay/test-remove")
            {
                RemovePhone(request.Arg("id") ?? "");
                reply(Response.Json(new JObject { ["ok"] = true }));
                return;
            }
            reply(request.Method == "GET" ? FileResponse(request.Path) : Response.Text("Unknown path", 404));
        }

        private bool HostKnown(string hostHeader)
        {
            if (hostHeader == null) return false;
            var h = hostHeader.ToLowerInvariant();
            var name = h.Split(':')[0];
            return h == "127.0.0.1:" + Settings.Port || h == "localhost:" + Settings.Port || IsOwnName(name);
        }

        private bool OriginKnown(string origin)
        {
            if (!Uri.TryCreate(origin.ToLowerInvariant(), UriKind.Absolute, out var url)) return false;
            if (IsOwnName(url.Host)) return true;
            return (url.Host == "127.0.0.1" || url.Host == "localhost") && url.Port == Settings.Port;
        }

        private bool IsOwnName(string name) => tailnetName.Length > 0 && name == tailnetName.ToLowerInvariant();

        private JObject StateJson()
        {
            var list = host.Limits(out var limitsAt);
            var rows = list.Select(l => (object)new JObject
            {
                ["kind"] = l.Kind, ["name"] = l.Name, ["percent"] = (long)l.Percent, ["severity"] = l.Severity,
                ["resets"] = l.Resets.HasValue ? (object)Millis(l.Resets.Value) : null,
            }).ToList();
            var live = LiveChats();
            var chats = new List<object>();
            foreach (var chat in live)
            {
                var row = Copy(chat);
                var session = chat.Str("session") ?? "";
                double latest = 0;
                var reply = ReadJson(At("replies", session + ".json"));
                if (reply != null)
                {
                    row["reply"] = new JObject { ["at"] = reply["at"] ?? 0L, ["text"] = Preview(reply.Str("text") ?? "", 160) };
                    latest = reply.Num("at") ?? 0;
                }
                var ask = ReadJson(At("asks", session + ".json"));
                if (ask?["id"] != null)
                {
                    row["ask"] = ask;
                    latest = Math.Max(latest, ask.Num("at") ?? 0);
                }
                if (titles.TryGetValue(session, out var title)) row["title"] = title;
                // a hidden chat stays off the list until a reply or an ask comes after it was hidden
                if (hidden.TryGetValue(session, out var hiddenAt))
                {
                    if (latest <= hiddenAt) continue;
                    hidden.Remove(session);
                    WriteJson(new JObject { ["hidden"] = FromDoubles(hidden) }, At("hidden.json"));
                }
                chats.Add(row);
            }
            // the closed ones of the last two weeks, for a message that opens one again
            var open = new HashSet<string>(live.Select(c => c.Str("session")).Where(s => s != null));
            var recent = past.Where(p => !open.Contains(p.Session) && !hidden.ContainsKey(p.Session)).Select(p => (object)new JObject
            {
                ["session"] = p.Session, ["title"] = titles.TryGetValue(p.Session, out var t) ? t : p.Title, ["at"] = p.At,
                ["project"] = Words.LastName(p.Cwd),
            }).ToList();
            return new JObject
            {
                ["now"] = Millis(DateTime.UtcNow), ["away"] = away, ["limits"] = rows,
                ["limitsAt"] = limitsAt.HasValue ? (object)Millis(limitsAt.Value) : null,
                ["chats"] = chats, ["recent"] = recent,
                ["push"] = new JObject { ["key"] = push.PublicKey, ["count"] = (long)push.Count },
                ["notify"] = NotifyJson(), ["unread"] = (long)Unread,
            };
        }

        private JObject ChatJson(string session)
        {
            var chat = ReadJson(At("chats", session + ".json"));
            if (chat == null) return null;
            var history = ReadJson(At("history", session + ".json")) ?? new JObject();
            var historyAt = history.Num("at") ?? 0;
            chat["history"] = history["messages"] ?? new List<object>();
            chat["historyAt"] = historyAt;
            var title = titles.TryGetValue(session, out var t) ? t : PastChatOf(session)?.Title;
            if (title != null) chat["title"] = title;
            var ask = ReadJson(At("asks", session + ".json"));
            if (ask?["id"] != null) chat["ask"] = ask;
            // phone messages the history has yet to show: queued, or entered with their turn still running
            var ack = ReadText(At("inbox", session + ".ack"));
            var messages = Inbox(session);
            var settled = SettledId(session, messages, historyAt);
            chat["settled"] = settled;
            chat["outbox"] = messages.Where(m => string.CompareOrdinal(m.Str("id") ?? "", settled) > 0).Select(m =>
            {
                var row = Copy(m);
                row["delivered"] = string.CompareOrdinal(m.Str("id") ?? "", ack) <= 0;
                return (object)row;
            }).ToList();
            chat["now"] = Millis(DateTime.UtcNow);
            return chat;
        }

        private static JObject Body(Request request) => Json.ParseObject(Encoding.UTF8.GetString(request.Body));

        // a message from the phone, the files sent ahead of it named by their paths beneath the words
        private Response Queue(Request request)
        {
            var body = Body(request);
            var session = body?.Str("session");
            if (!IsSessionId(session) || !IsKnown(session)) return Response.Text("Message refused", 400);
            var text = (body.Str("text") ?? "").Trim();
            var folder = At("files", session);
            var files = (body.Arr("files") ?? new List<object>()).OfType<string>()
                .Where(n => Uploads.SafeName(n) == n && File.Exists(Path.Combine(folder, n))).ToList();
            if ((text.Length == 0 && files.Count == 0) || text.Length > 20_000) return Response.Text("Message refused", 400);
            var words = text;
            if (files.Count > 0)
                words += (words.Length == 0 ? "" : "\n\n") + "Attached from the phone:\n" + string.Join("\n", files.Select(n => Path.Combine(folder, n)));
            phoneActive = DateTime.UtcNow;
            sent++;
            var at = Millis(DateTime.UtcNow);
            var id = at.ToString("0000000000000", CultureInfo.InvariantCulture) + "-" + (sent % 10_000).ToString("D4", CultureInfo.InvariantCulture);
            var ack = ReadText(At("inbox", session + ".ack"));
            var hourAgo = at - 3_600_000;
            // a delivered message stays an hour, so the phone shows it until the chat's history holds it
            var messages = Inbox(session).Where(m => string.CompareOrdinal(m.Str("id") ?? "", ack) > 0 || (m.Num("at") ?? 0) > hourAgo).Cast<object>().ToList();
            messages.Add(new JObject { ["id"] = id, ["text"] = words, ["at"] = at });
            WriteJson(new JObject { ["messages"] = messages }, At("inbox", session + ".json"));
            Log.Note("relay: a phone message queued for " + ProjectName(session) + (files.Count == 0 ? "" : ", " + files.Count + " attached"));
            Wake(session);
            return Response.Json(new JObject { ["ok"] = true, ["id"] = id, ["at"] = at });
        }

        // a file from the phone, which the server streamed to the spool: a picture fitted, and anything else moved into
        // place as it came
        private void Upload(Request request, Action<Response> reply)
        {
            var file = request.BodyFile;
            var size = file != null && File.Exists(file) ? new FileInfo(file).Length : 0;
            var session = request.Arg("session");
            if (size <= 0 || !IsSessionId(session) || !IsKnown(session))
            {
                reply(Response.Text("Upload refused", 400));
                return;
            }
            phoneActive = DateTime.UtcNow;
            var name = request.Arg("name") ?? "";
            var folder = At("files", session);
            var project = ProjectName(session);
            reader.Post(() =>
            {
                Fitted fitted = null;
                try { fitted = host.FitFile(file, 1500); } catch (Exception) { }
                loop.Post(() =>
                {
                    try
                    {
                        var saved = fitted?.Data != null
                            ? Uploads.Save(fitted.Data, name, fitted.Ext, folder)
                            : Uploads.Move(file, name, fitted?.Ext, folder);
                        Log.Note("relay: a file from the phone saved for " + project + ", " + saved);
                        reply(Response.Json(new JObject { ["ok"] = true, ["name"] = saved }));
                    }
                    catch (Exception e)
                    {
                        Log.Note("relay: a file from the phone failed to save, " + e.Message);
                        reply(Response.Text("Saving failed", 500));
                    }
                });
            });
        }

        private static readonly Regex atPattern = new Regex("^[0-9]{1,12}(-[0-9]{1,4}){1,2}$");
        private static readonly Regex keyPattern = new Regex("^[A-Za-z0-9_-]{1,100}$");

        // a picture of a chat for the phone, 1,000 px at most: one the mod saved (images\, by key), one the phone sent
        // (files\, by name), or one on an earlier page (in the transcript, by its line and block); fitted off this loop
        private void Picture(Request request, Action<Response> reply)
        {
            var session = request.Arg("session");
            if (!IsSessionId(session))
            {
                reply(Response.Text("Unknown picture", 404));
                return;
            }
            void Send(byte[] data)
            {
                Fitted fitted = null;
                try { fitted = data == null ? null : host.FitBytes(data, 1000); } catch (Exception) { }
                var response = fitted?.Data == null
                    ? Response.Text("Unknown picture", 404)
                    : new Response { Status = 200, Type = Uploads.MimeType(fitted.Ext), Body = fitted.Data, IsLasting = true };
                loop.Post(() => reply(response));
            }
            var at = request.Arg("at");
            var cwd = CwdOf(session);
            if (at != null && atPattern.IsMatch(at) && cwd != null)
            {
                reader.Post(() => Send(TranscriptOf(session, cwd).Picture("@" + at)));
                return;
            }
            byte[] source = null;
            var key = request.Arg("key");
            var fileName = request.Arg("file");
            if (key != null && keyPattern.IsMatch(key))
            {
                var stored = ReadJson(At("images", session, key + ".json"))?.Str("data");
                try { source = stored == null ? null : Convert.FromBase64String(stored); } catch (FormatException) { }
            }
            else if (fileName != null && Uploads.SafeName(fileName) == fileName)
            {
                var path = At("files", session, fileName);
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size > 0 && size <= 40 * 1_048_576) source = File.ReadAllBytes(path);
            }
            if (source == null)
            {
                reply(Response.Text("Unknown picture", 404));
                return;
            }
            reader.Post(() => Send(source));
        }

        // a page of the chat before what the phone holds, from the transcript: before a cursor from the last page, or
        // before where the mod's window begins (its oldest tool call, else its oldest words)
        private void Earlier(Request request, Action<Response> reply)
        {
            var session = request.Arg("session");
            var cwd = IsSessionId(session) ? CwdOf(session) : null;
            if (cwd == null)
            {
                reply(Response.Text("Unknown chat", 404));
                return;
            }
            var hasBefore = int.TryParse(request.Arg("before") ?? "", out var before);
            var tool = request.Arg("tool");
            var text = request.Arg("text");
            reader.Post(() =>
            {
                var found = TranscriptOf(session, cwd);
                var end = found.Entries.Count;
                if (hasBefore) end = Math.Min(Math.Max(0, before), end);
                else if (tool != null && found.IndexOfTool(tool) is int t) end = t;
                else if (text != null && found.IndexOfText(text) is int x) end = x;
                found.Page(end, out var items, out var cursor);
                var answer = Response.Json(new JObject { ["items"] = items, ["cursor"] = cursor.HasValue ? (object)(long)cursor.Value : null });
                loop.Post(() => reply(answer));
            });
        }

        // one tool call whole from the transcript, for the phone's code views: its input and result in full, and for an
        // edit the line it starts on in the file as it stands
        private void ToolCall(Request request, Action<Response> reply)
        {
            var session = request.Arg("session");
            var id = request.Arg("id");
            var cwd = IsSessionId(session) && id != null && keyPattern.IsMatch(id) ? CwdOf(session) : null;
            if (cwd == null)
            {
                reply(Response.Text("Unknown call", 404));
                return;
            }
            reader.Post(() =>
            {
                var found = TranscriptOf(session, cwd).Call(id);
                if (found != null)
                {
                    var input = found.Obj("input") ?? new JObject();
                    var path = input.Str("file_path");
                    var added = input.Str("new_string");
                    if (found.Str("tool") == "Edit" && path != null && Path.IsPathRooted(path) && !string.IsNullOrEmpty(added))
                    {
                        try
                        {
                            if (File.Exists(path) && new FileInfo(path).Length <= 8 << 20)
                            {
                                var text = File.ReadAllText(path, Encoding.UTF8);
                                var hit = text.IndexOf(added, StringComparison.Ordinal);
                                if (hit >= 0) found["line"] = (long)(1 + text.Take(hit).Count(c => c == '\n'));
                            }
                        }
                        catch (Exception) { }
                    }
                }
                var answer = found == null ? Response.Text("Unknown call", 404) : Response.Json(found);
                loop.Post(() => reply(answer));
            });
        }

        // the phone's Stop: the mod reads it within two seconds and ends the running turn; an ask held for the phone
        // lets go at once
        private Response StopTurn(Request request)
        {
            var session = Body(request)?.Str("session");
            if (!IsSessionId(session) || !File.Exists(At("chats", session + ".json"))) return Response.Text("Stop refused", 400);
            phoneActive = DateTime.UtcNow;
            stops[session] = DateTime.UtcNow;
            WriteJson(new JObject { ["at"] = Millis(DateTime.UtcNow) }, At("inbox", session + ".stop"));
            foreach (var key in waiters.Keys.Where(k => k.StartsWith(session + "/", StringComparison.Ordinal)).ToList())
            {
                var list = waiters[key];
                waiters.Remove(key);
                foreach (var w in list) w.Reply(Response.Json(new JObject { ["state"] = "stopped" }));
            }
            Log.Note("relay: the phone stopped the turn in " + ProjectName(session));
            return Response.Json(new JObject { ["ok"] = true });
        }

        private static readonly HashSet<string> models = new HashSet<string> { "opus", "sonnet", "haiku" };
        private static readonly HashSet<string> efforts = new HashSet<string> { "low", "medium", "high", "xhigh", "max" };

        // the phone's switch of a chat's model or effort: the mod reads it within two seconds and runs the chat's own
        // /model and /effort; a pick made before the mod read the last joins it
        private Response SwitchChat(Request request)
        {
            var body = Body(request);
            var session = body?.Str("session");
            if (!IsSessionId(session) || !File.Exists(At("chats", session + ".json"))) return Response.Text("Switch refused", 400);
            var model = body.Str("model");
            var effort = body.Str("effort");
            if ((model == null && effort == null) || (model != null && !models.Contains(model)) || (effort != null && !efforts.Contains(effort)))
                return Response.Text("Switch refused", 400);
            var path = At("inbox", session + ".switch");
            var asked = ReadJson(path) ?? new JObject();
            if (model != null) asked["model"] = model;
            if (effort != null) asked["effort"] = effort;
            asked["at"] = body.Num("at") ?? Millis(DateTime.UtcNow);
            phoneActive = DateTime.UtcNow;
            WriteJson(asked, path);
            Log.Note("relay: the phone switched " + ProjectName(session) + " to " + string.Join(", ", new[] { model, effort }.Where(s => s != null)));
            return Response.Json(new JObject { ["ok"] = true });
        }

        // the phone's rename, as VS Code's panel renames: an open chat through its own /rename (the mod reads
        // inbox\<session>.rename within two seconds), a closed one by a title line at the end of its transcript
        private Response Rename(Request request)
        {
            var body = Body(request);
            var session = body?.Str("session");
            var cwd = IsSessionId(session) && IsKnown(session) ? CwdOf(session) : null;
            if (cwd == null) return Response.Text("Rename refused", 400);
            var title = string.Join(" ", (body.Str("title") ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)).Trim();
            if (title.Length < 1 || title.Length > 100) return Response.Text("Rename refused", 400);
            phoneActive = DateTime.UtcNow;
            titles[session] = title;
            pendingTitles[session] = Tuple.Create(title, DateTime.UtcNow);
            if (IsLive(session))
            {
                WriteJson(new JObject { ["title"] = title, ["at"] = Millis(DateTime.UtcNow) }, At("inbox", session + ".rename"));
            }
            else
            {
                reader.Post(() =>
                {
                    try
                    {
                        var line = "{\"type\":\"custom-title\",\"customTitle\":" + Json.Write(title) + ",\"sessionId\":\"" + session + "\"}\n";
                        File.AppendAllText(TranscriptOf(session, cwd).Path, line, new UTF8Encoding(false));
                    }
                    catch (Exception) { }
                });
            }
            Log.Note("relay: the phone renamed " + ProjectName(session));
            return Response.Json(new JObject { ["ok"] = true });
        }

        // the phone's swipe to delete: the chat leaves the phone's lists, VS Code's Archive, its history kept
        private Response Hide(Request request)
        {
            var session = Body(request)?.Str("session");
            if (!IsSessionId(session) || !IsKnown(session)) return Response.Text("Hide refused", 400);
            hidden[session] = Millis(DateTime.UtcNow);
            WriteJson(new JObject { ["hidden"] = FromDoubles(hidden) }, At("hidden.json"));
            Log.Note("relay: the phone hid " + ProjectName(session));
            return Response.Json(new JObject { ["ok"] = true });
        }

        private Response Answer(Request request)
        {
            var body = Body(request);
            var session = body?.Str("session");
            var ask = body?.Str("ask");
            if (!IsSessionId(session) || string.IsNullOrEmpty(ask)) return Response.Text("Answer refused", 400);
            var answer = new JObject();
            if (body.Obj("answers") is JObject given)
            {
                var kept = new JObject();
                foreach (var kv in given)
                    if (kv.Value is string v) kept[kv.Key] = v;
                answer["answers"] = kept;
            }
            if (body.Bool("allow") is bool allow) answer["allow"] = allow;
            if (answer.Count == 0) return Response.Text("Answer refused", 400);
            // the ask the chat holds this moment alone takes an answer; one that moved on answers 409
            if (ReadJson(At("asks", session + ".json"))?.Str("id") != ask) return Response.Text("Ask moved on", 409);
            phoneActive = DateTime.UtcNow;
            var key = session + "/" + ask;
            if (waiters.TryGetValue(key, out var list) && list.Count > 0)
            {
                waiters.Remove(key);
                foreach (var w in list) w.Reply(Response.Json(new JObject { ["state"] = "answered", ["answer"] = answer }));
            }
            else
            {
                answers[key] = Tuple.Create(answer, DateTime.UtcNow);
            }
            Log.Note("relay: the phone answered " + (answer.Has("allow") ? "a permission" : "a question") + " for " + ProjectName(session));
            return Response.Json(new JObject { ["ok"] = true });
        }

        // a phone's subscription alone, under the phone's own tag, which stands behind it from then on: Remove ends both
        private Response Subscribe(Request request)
        {
            if (!IsRemote(request)) return Response.Text("Phone only", 403);
            var subscription = Body(request)?.Obj("subscription");
            if (subscription == null) return Response.Text("Subscription refused", 400);
            if (KeyOf(request.Header("x-uselim-key"), request.Path, out var id) != Kind.Phone) return Response.Text("Subscription refused", 400);
            var origin = request.Header("origin") ?? "https://" + (request.Header("host") ?? "localhost");
            if (!push.Add(subscription, origin, id)) return Response.Text("Subscription refused", 400);
            return Response.Json(new JObject { ["ok"] = true, ["count"] = (long)push.Count });
        }

        // the mod's long poll: the phone's answer, "desk" once the PC is in use, or "waiting" after 20 s
        private void Wait(Request request, Action<Response> reply)
        {
            var session = request.Arg("session");
            var ask = request.Arg("ask");
            if (!IsSessionId(session) || string.IsNullOrEmpty(ask))
            {
                reply(Response.Text("Wait refused", 400));
                return;
            }
            var key = session + "/" + ask;
            if (answers.TryGetValue(key, out var stored))
            {
                answers.Remove(key);
                reply(Response.Json(new JObject { ["state"] = "answered", ["answer"] = stored.Item1 }));
                return;
            }
            if (stops.TryGetValue(session, out var stopped))
            {
                var held = ReadJson(At("asks", session + ".json"));
                if (held != null && (held.Num("at") ?? double.PositiveInfinity) <= Millis(stopped))
                {
                    reply(Response.Json(new JObject { ["state"] = "stopped" }));
                    return;
                }
            }
            var always = request.Arg("always") == "1";
            if (!away && !always)
            {
                reply(Response.Json(new JObject { ["state"] = "desk" }));
                return;
            }
            var waiter = new Waiter { Id = ++nextWaiter, Always = always, Reply = reply };
            if (!waiters.TryGetValue(key, out var list)) waiters[key] = list = new List<Waiter>();
            list.Add(waiter);
            loop.After(20, () =>
            {
                if (!waiters.TryGetValue(key, out var current) || !current.Remove(waiter)) return;
                if (current.Count == 0) waiters.Remove(key);
                waiter.Reply(Response.Json(new JObject { ["state"] = "waiting" }));
            });
        }

        // back at the PC: the waits held for the away hours let go; a question's stays beside its dialog
        private void ReleaseAll(JObject body)
        {
            var all = waiters.Values.SelectMany(l => l).Where(w => !w.Always).ToList();
            foreach (var key in waiters.Keys.ToList())
            {
                var kept = waiters[key].Where(w => w.Always).ToList();
                if (kept.Count == 0) waiters.Remove(key);
                else waiters[key] = kept;
            }
            foreach (var w in all) w.Reply(Response.Json(body));
        }

        private static readonly Dictionary<string, string[]> webFiles = new Dictionary<string, string[]>
        {
            ["/"] = new[] { "index.html", "text/html; charset=utf-8" },
            ["/app.js"] = new[] { "app.js", "text/javascript; charset=utf-8" },
            ["/app.css"] = new[] { "app.css", "text/css; charset=utf-8" },
            ["/sw.js"] = new[] { "sw.js", "text/javascript; charset=utf-8" },
            ["/manifest.webmanifest"] = new[] { "manifest.webmanifest", "application/manifest+json" },
            ["/highlight-11.12.0.min.js"] = new[] { "highlight-11.12.0.min.js", "text/javascript; charset=utf-8" },
            ["/jsQR-1.4.0.js"] = new[] { "jsQR-1.4.0.js", "text/javascript; charset=utf-8" },
        };

        private static readonly HashSet<string> pictures = new HashSet<string>
        {
            "icon", "icon-192", "back", "send", "attach", "remove", "file", "bell", "gear", "reply", "question", "permission", "limit",
            "compose", "stop", "copy", "copied", "updown", "fold", "scan",
        };

        private Response FileResponse(string path)
        {
            string name, type;
            if (webFiles.TryGetValue(path, out var entry))
            {
                name = entry[0];
                type = entry[1];
            }
            else if (path.EndsWith(".png", StringComparison.Ordinal) && pictures.Contains(path.Substring(1, path.Length - 5)))
            {
                name = path.Substring(1);
                type = "image/png";
            }
            else return Response.Text("Unknown path", 404);
            var data = host.WebFile(name);
            if (data == null) return Response.Text("Unknown path", 404);
            return new Response
            {
                Status = 200, Type = type, Body = data,
                IsLasting = name.StartsWith("highlight-", StringComparison.Ordinal) || name.StartsWith("jsQR-", StringComparison.Ordinal),
            };
        }

        // The folder

        private List<JObject> ReadChats() => Sessions("chats").Select(s => ReadJson(At("chats", s + ".json"))).Where(c => c != null).ToList();

        private List<JObject> LiveChats()
        {
            var now = Millis(DateTime.UtcNow);
            return ReadChats().Where(c => c.Str("status") != "ended" && now - (c.Num("at") ?? 0) < AliveWindow).ToList();
        }

        private List<string> Sessions(string folder)
        {
            try
            {
                var dir = At(folder);
                if (!Directory.Exists(dir)) return new List<string>();
                return Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(IsSessionId).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        private List<JObject> Inbox(string session) =>
            ReadJson(At("inbox", session + ".json"))?.Arr("messages")?.OfType<JObject>().ToList() ?? new List<JObject>();

        private string ProjectName(string session) => ReadJson(At("chats", session + ".json"))?.Str("project") ?? session;

        // "Header: question" for a question, "Tool: target" for a permission
        private static string AskText(JObject ask)
        {
            if (ask.Str("kind") == "question")
                return string.Join(" · ", (ask.Arr("questions") ?? new List<object>()).OfType<JObject>().Select(q =>
                    string.Join(": ", new[] { q.Str("header"), q.Str("question") }.Where(s => s != null))));
            // a permission's call in full rides on the card; the notice names its first line
            var first = ask.Str("detail")?.Split('\n')[0];
            return string.Join(": ", new[] { ask.Str("tool"), first }.Where(s => s != null));
        }

        private static string Preview(string text, int length)
        {
            var plain = Regex.Replace(text, "```[\\s\\S]*?```", " ");
            plain = Regex.Replace(plain, "[*_`#>|]", "");
            plain = Regex.Replace(plain, "\\s+", " ").Trim();
            return plain.Length > length ? plain.Substring(0, length) + "…" : plain;
        }

        public static bool IsSessionId(string text) => text != null && Regex.IsMatch(text, "^[A-Za-z0-9_-]{8,80}$");

        public static double Millis(DateTime at) => Math.Round(Spans.ToEpoch(at) * 1000);

        private static string ReadText(string path) => (Paths.Read(path) ?? "").Trim();

        public static JObject ReadJson(string path)
        {
            var text = Paths.Read(path);
            return string.IsNullOrEmpty(text) ? null : Json.ParseObject(text);
        }

        public static void WriteJson(object value, string path) => Paths.WriteAtomic(path, Json.Write(value));

        private static JObject Copy(JObject o) => Json.ParseObject(Json.Write(o)) ?? new JObject();
    }
}
