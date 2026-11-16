using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Uselim
{
    // Chats waiting on an answer. Claude Code's hooks run "Uselim.exe --hook" with the event on stdin; each chat gets
    // one file in %LOCALAPPDATA%\Uselim\waiting, written as it stops for an answer (a question or a permission ask) and
    // removed as the answer comes, the tool runs, a prompt is typed in it, or it closes. The app watches the folder.

    public sealed class Wait
    {
        public string Session;
        public bool IsQuestion;
        public string Project;
        public string Began;
        public string Detail;
        public DateTime At;

        public string Word => IsQuestion ? "Question" : "Permission";
    }

    public static class Waiting
    {
        public static List<Wait> Read()
        {
            var found = new List<Wait>();
            try
            {
                if (!Directory.Exists(Paths.WaitingDir)) return found;
                foreach (var file in Directory.GetFiles(Paths.WaitingDir, "*.json"))
                {
                    var json = Json.ParseObject(Paths.Read(file));
                    var kind = json?.Str("kind");
                    if (kind != "question" && kind != "permission") continue;
                    found.Add(new Wait
                    {
                        Session = Path.GetFileNameWithoutExtension(file),
                        IsQuestion = kind == "question",
                        Project = json.Str("project") ?? "",
                        Began = json.Str("began") ?? "",
                        Detail = json.Str("detail") ?? "",
                        At = Spans.FromEpoch(json.Num("at") ?? 0),
                    });
                }
            }
            catch (Exception) { }
            return found.OrderByDescending(w => w.At).ToList();
        }

        // a session id as a file name: letters, digits, dashes and underscores
        private static bool IsSafe(string id) => !string.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}$");

        // --hook: one event from Claude Code
        public static void RunHook(string input, DateTime now)
        {
            var json = Json.ParseObject(input);
            var session = json?.Str("session_id");
            var hookEvent = json?.Str("hook_event_name");
            if (!IsSafe(session) || hookEvent == null) return;
            var file = Path.Combine(Paths.WaitingDir, session + ".json");

            void Write(string kind, string detail)
            {
                var cwd = (json.Str("cwd") ?? "").TrimEnd('\\', '/');
                var project = cwd.Length == 0 ? "" : cwd.Substring(Math.Max(cwd.LastIndexOf('\\'), cwd.LastIndexOf('/')) + 1);
                var transcript = json.Str("transcript_path");
                var record = new JObject
                {
                    ["kind"] = kind,
                    ["project"] = project,
                    ["began"] = transcript == null ? "" : OpeningWords(transcript),
                    ["detail"] = detail,
                    ["at"] = Spans.ToEpoch(now),
                };
                Paths.WriteAtomic(file, Json.Write(record));
            }

            switch (hookEvent)
            {
                case "PreToolUse":
                    if (json.Str("tool_name") != "AskUserQuestion") return;
                    var questions = json.Obj("tool_input")?.Arr("questions")?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
                    var detail = string.Join(" · ", questions.Select(q =>
                        string.Join(": ", new[] { q.Str("header"), q.Str("question") }.Where(s => !string.IsNullOrEmpty(s)))));
                    Write("question", detail.Length == 0 ? "A question" : detail);
                    break;
                case "Notification":
                    if (json.Str("notification_type") != "permission_prompt") return;
                    Write("permission", json.Str("message") ?? "A permission");
                    break;
                default:
                    // PostToolUse (the question got its answer, or the tool ran after its permission), UserPromptSubmit, SessionEnd
                    try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
                    TakeDownPermissionCard(session, json, hookEvent);
                    break;
            }
        }

        // the phone's permission card comes down once the chat moves on: the tool it asks about ran (allowed at the PC),
        // a prompt came, or the chat closed; a waiting --permission sees its card go and leaves
        private static void TakeDownPermissionCard(string session, JObject json, string hookEvent)
        {
            var path = Path.Combine(Paths.RelayDir, "asks", session + ".json");
            var card = Relay.ReadJson(path);
            if (card == null || card.Str("kind") != "permission") return;
            if (hookEvent == "PostToolUse")
            {
                var name = json.Str("tool_name") ?? "";
                Permission.DescribeCall(name, json.Obj("tool_input") ?? new JObject(), out var detail, out _);
                if (card.Str("tool") != Permission.ToolLabel(name) || card.Str("detail") != detail) return;
            }
            try { File.WriteAllText(path, ""); } catch (Exception) { }
        }

        private static string Plain(string text)
        {
            text = Regex.Replace(text, "<([a-z_-]+)[^>]*>[\\s\\S]*?</\\1>", "");
            text = Regex.Replace(text, "<[^>]+>", "");
            text = Regex.Replace(text, "\\s+", " ");
            return text.Trim();
        }

        // the chat's opening words: the first thing typed in it, from the transcript's head
        public static string OpeningWords(string path)
        {
            try
            {
                var buffer = new byte[256 * 1024];
                int read;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    read = stream.Read(buffer, 0, buffer.Length);
                var head = Encoding.UTF8.GetString(buffer, 0, read);
                foreach (var line in head.Split('\n'))
                {
                    if (!line.Contains("\"type\":\"user\"")) continue;
                    var json = Json.ParseObject(line);
                    if (json == null || json.Bool("isMeta") == true) continue;
                    var message = json.Obj("message");
                    if (message == null) continue;
                    var text = message["content"] as string;
                    if (text == null)
                        text = message.Arr("content")?.OfType<JObject>().FirstOrDefault(p => p.Str("type") == "text")?.Str("text") ?? "";
                    var clean = Plain(text);
                    if (clean.Length == 0) continue;
                    return clean.Length > 60 ? clean.Substring(0, 60) : clean;
                }
            }
            catch (Exception) { }
            return "";
        }
    }
}
