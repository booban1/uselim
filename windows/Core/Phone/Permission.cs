using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace Uselim
{
    // --permission: Claude Code's PermissionRequest hook, so a permission reaches the phone at any time, the Mac's
    // Permission.swift on Windows. VS Code hosts Claude Code through the SDK's canUseTool, and for those calls the hook
    // runs beside the panel's own dialog, the first decision applying. The hook puts a card with Allow and Deny on the
    // phone (relay\asks\<session>.json, which Uselim pushes and the phone's chat shows) and waits on Uselim's server for
    // the phone's answer. An answer at the PC ends the race: Claude Code ends the hook, and Uselim takes the card down once
    // its chat moves on (the tool ran, a new prompt, the turn's end) and lets the wait go. Questions stay with the mod's
    // race and plan approvals with the dialog.

    public static class Permission
    {
        private static readonly HashSet<string> atTheDialog = new HashSet<string> { "AskUserQuestion", "ExitPlanMode" };
        // under the hook's own 3,600 s timeout
        private const double Longest = 3_500;
        // the card holds the first CardLongest characters and counts the rest, which the PC's dialog shows
        public const int CardLongest = 100_000;

        // the decision to print, or null to leave the permission with VS Code's own dialog
        public static string Run(string input, string relayDir, int port, bool phoneOn)
        {
            if (!phoneOn) return null;
            var json = Json.ParseObject(input);
            var session = json?.Str("session_id");
            var tool = json?.Str("tool_name");
            if (!Relay.IsSessionId(session) || tool == null || atTheDialog.Contains(tool)) return null;
            var cwd = json.Str("cwd") ?? "";
            var now = Relay.Millis(DateTime.UtcNow);
            var id = "p" + (long)now;
            var path = Path.Combine(relayDir, "asks", session + ".json");
            DescribeCall(tool, json.Obj("tool_input") ?? new JObject(), out var detail, out var more);
            var card = new JObject
            {
                ["kind"] = "permission", ["id"] = id, ["tool"] = ToolLabel(tool), ["detail"] = detail,
                ["project"] = Words.LastName(cwd), ["began"] = "", ["at"] = now,
            };
            if (more > 0) card["more"] = (long)more;
            Relay.WriteJson(card, path);

            // the card comes down with the hook, whichever way the race ends
            void TakeDown()
            {
                if (Relay.ReadJson(path)?.Str("id") == id) File.WriteAllText(path, "");
            }

            var started = DateTime.UtcNow;
            var query = "session=" + session + "&ask=" + id + "&always=1";
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) })
            {
                while ((DateTime.UtcNow - started).TotalSeconds < Longest)
                {
                    // a card taken down: the PC answered, or the chat moved on
                    if (Relay.ReadJson(path)?.Str("id") != id) return null;
                    JObject body = null;
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/relay/wait?" + query);
                        request.Headers.TryAddWithoutValidation("X-Uselim", "1");
                        var key = Relay.LocalKey(relayDir);
                        if (key != null) request.Headers.TryAddWithoutValidation("X-Uselim-Key", key);
                        using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                            if ((int)response.StatusCode == 200)
                                body = Json.ParseObject(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    }
                    catch (Exception) { }
                    if (body == null)
                    {
                        // Uselim restarting, or its Phone switch off a moment: the card stays and the wait goes again
                        Thread.Sleep(3000);
                        continue;
                    }
                    switch (body.Str("state"))
                    {
                        case "answered":
                            TakeDown();
                            var allow = body.Obj("answer")?.Bool("allow");
                            if (!allow.HasValue) return null;
                            return Decision(allow.Value
                                ? new JObject { ["behavior"] = "allow" }
                                : new JObject { ["behavior"] = "deny", ["message"] = "Denied from the phone." });
                        case "stopped":
                            TakeDown();
                            return Decision(new JObject { ["behavior"] = "deny", ["message"] = "The turn was stopped from the phone.", ["interrupt"] = true });
                        case "gone":
                            return null;
                    }
                }
            }
            TakeDown();
            return null;
        }

        // an MCP tool by its server and name, as the phone names it
        public static string ToolLabel(string tool)
        {
            if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return tool;
            var rest = tool.Substring(5);
            var split = rest.IndexOf("__", StringComparison.Ordinal);
            return split < 0 ? rest : rest.Substring(0, split) + " " + rest.Substring(split + 2);
        }

        private static string Marked(string text, string mark) => string.Join("\n", text.Split('\n').Select(l => mark + " " + l));

        // the call in full, as the mod details a tool row, so Allow on the phone answers for what it reads
        public static void DescribeCall(string tool, JObject input, out string detail, out int more)
        {
            string Field(string key) => input.Str(key) ?? "";
            string whole;
            switch (tool)
            {
                case "Bash": whole = Field("command"); break;
                case "Read": whole = Field("file_path"); break;
                case "Write": whole = Field("file_path") + "\n\n" + Field("content"); break;
                case "Edit":
                    whole = Field("file_path") + "\n\n" + Marked(Field("old_string"), "-") + "\n" + Marked(Field("new_string"), "+");
                    break;
                case "NotebookEdit":
                    // the edit's kind first, so a cell's deletion reads as one
                    var kind = Field("edit_mode").Length == 0 ? "replace" : Field("edit_mode");
                    var cell = string.Join(" ", new[] { kind, "cell", Field("cell_id") }.Where(s => s.Length > 0));
                    var source = kind == "delete" ? "" : "\n" + Field("new_source");
                    whole = Field("notebook_path") + "\n\n" + cell + source;
                    break;
                case "Grep":
                case "Glob":
                    whole = string.Join("\nin ", new[] { Field("pattern"), Field("path") }.Where(s => s.Length > 0));
                    break;
                case "WebFetch": whole = Field("url"); break;
                case "WebSearch": whole = Field("query"); break;
                case "Agent":
                case "Task": whole = Field("prompt"); break;
                default: whole = Json.Write(Words.Sorted(input), true); break;
            }
            detail = Words.Prefix(whole, CardLongest);
            more = whole.Length - detail.Length;
        }

        private static string Decision(JObject fields) =>
            Json.Write(new JObject { ["hookSpecificOutput"] = new JObject { ["hookEventName"] = "PermissionRequest", ["decision"] = fields } });
    }
}
