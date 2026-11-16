using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Uselim
{
    // A chat's whole history from Claude Code's own transcript (<Claude Code's folder>\projects\<folder>\<session>.jsonl),
    // for the phone's earlier pages and the chat's title, the Mac's Transcripts.swift on Windows. Indexed once and
    // extended as it grows: the byte offset of each line of the main conversation (a line that opens with its parent's
    // id, off any subagent's chain, and names its role in its first few hundred bytes), and the titles VS Code shows,
    // the name given to the chat first, else Claude's. Every call runs on the relay's transcript loop.

    public sealed class Transcript
    {
        private const int HeadLength = 600;
        private const int PageTalk = 40;
        private const int PageTools = 160;

        public readonly string Path;
        public readonly List<long> Entries = new List<long>();
        private string customTitle = "", aiTitle = "";
        private long indexed;

        public Transcript(string path) { Path = path; }

        public string Title => customTitle.Length > 0 ? customTitle : aiTitle;

        // Claude Code's folder for a working directory: every character outside ASCII letters and digits becomes a dash
        public static string PathFor(string cwd, string session)
        {
            var folder = new StringBuilder();
            foreach (var c in cwd) folder.Append(c < 128 && char.IsLetterOrDigit(c) ? c : '-');
            return System.IO.Path.Combine(Paths.ClaudeFolder, "projects", folder.ToString(), session + ".jsonl");
        }

        private FileStream Open() =>
            new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // reads the lines added since the last call
        public void Refresh()
        {
            try
            {
                using (var stream = Open())
                {
                    if (stream.Length <= indexed) return;
                    stream.Seek(indexed, SeekOrigin.Begin);
                    var buffer = new byte[1 << 20];
                    var pending = new MemoryStream();
                    var start = indexed;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        pending.Write(buffer, 0, read);
                        var bytes = pending.GetBuffer();
                        var length = (int)pending.Length;
                        var position = 0;
                        while (true)
                        {
                            var hit = Array.IndexOf(bytes, (byte)10, position, length - position);
                            if (hit < 0) break;
                            Classify(bytes, position, hit - position, start + position);
                            position = hit + 1;
                        }
                        start += position;
                        var rest = new MemoryStream();
                        rest.Write(bytes, position, length - position);
                        pending = rest;
                    }
                    indexed = start;
                }
            }
            catch (Exception) { }
        }

        private void Classify(byte[] bytes, int at, int length, long offset)
        {
            var head = Encoding.UTF8.GetString(bytes, at, Math.Min(length, HeadLength));
            if (head.StartsWith("{\"parentUuid\"", StringComparison.Ordinal) && head.Contains("\"isSidechain\":false")
                && (head.Contains("\"role\":\"user\"") || head.Contains("\"role\":\"assistant\"")))
            {
                Entries.Add(offset);
            }
            else if (head.StartsWith("{\"type\":\"custom-title\"", StringComparison.Ordinal) || head.StartsWith("{\"type\":\"ai-title\"", StringComparison.Ordinal))
            {
                var json = Json.ParseObject(Encoding.UTF8.GetString(bytes, at, length));
                if (json?.Str("customTitle") != null) customTitle = json.Str("customTitle");
                if (json?.Str("aiTitle") != null) aiTitle = json.Str("aiTitle");
            }
        }

        private static JObject ReadLine(FileStream stream, long offset)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var data = new MemoryStream();
            var buffer = new byte[256 << 10];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var newline = Array.IndexOf(buffer, (byte)10, 0, read);
                if (newline >= 0)
                {
                    data.Write(buffer, 0, newline);
                    break;
                }
                data.Write(buffer, 0, read);
            }
            return Json.ParseObject(Encoding.UTF8.GetString(data.ToArray()));
        }

        private static List<JObject> Blocks(JObject line)
        {
            var message = line.Obj("message");
            if (message?["content"] is string text) return new List<JObject> { new JObject { ["type"] = "text", ["text"] = text } };
            return message?.Arr("content")?.OfType<JObject>().ToList() ?? new List<JObject>();
        }

        private static string Role(JObject line) => line.Obj("message")?.Str("role");

        // anchors: where the mod's window begins

        public int? IndexOfTool(string id)
        {
            try
            {
                using (var stream = Open())
                    for (var index = Entries.Count - 1; index >= 0; index--)
                    {
                        var line = ReadLine(stream, Entries[index]);
                        if (line == null || Role(line) != "assistant") continue;
                        if (Blocks(line).Any(b => b.Str("type") == "tool_use" && b.Str("id") == id)) return index;
                    }
            }
            catch (Exception) { }
            return null;
        }

        public int? IndexOfText(string anchor)
        {
            var wanted = Words.Anchor(anchor);
            if (wanted.Length == 0) return null;
            try
            {
                using (var stream = Open())
                    for (var index = Entries.Count - 1; index >= 0; index--)
                    {
                        var line = ReadLine(stream, Entries[index]);
                        if (line == null) continue;
                        var text = string.Join("\n", Blocks(line).Where(b => b.Str("type") == "text").Select(b => b.Str("text") ?? ""));
                        var words = Words.Anchor(Words.CleanPrompt(text));
                        if (words.Length > 0 && (words.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(words, StringComparison.Ordinal))) return index;
                    }
            }
            catch (Exception) { }
            return null;
        }

        // the items before entry `end`, oldest first, up to a page of messages; the cursor for the page before, null at the
        // chat's start. A tool's result comes from the user line after its call, so the lines just past the page are read
        // for results first.
        public void Page(int end, out List<object> items, out int? cursor)
        {
            items = new List<object>();
            cursor = null;
            if (end <= 0) return;
            try
            {
                using (var stream = Open())
                {
                    var results = new Dictionary<string, Tuple<string, bool, List<object>>>();
                    void TakeResults(JObject line, long offset)
                    {
                        var parts = Blocks(line);
                        for (var n = 0; n < parts.Count; n++)
                        {
                            var block = parts[n];
                            if (block.Str("type") != "tool_result") continue;
                            var id = block.Str("tool_use_id");
                            if (id == null) continue;
                            var text = "";
                            var images = new List<object>();
                            if (block["content"] is string plain) text = plain;
                            if (block.Arr("content") is List<object> inner)
                            {
                                var innerBlocks = inner.OfType<JObject>().ToList();
                                text = string.Join("\n", innerBlocks.Where(p => p.Str("type") == "text").Select(p => p.Str("text") ?? ""));
                                for (var m = 0; m < innerBlocks.Count; m++)
                                    if (innerBlocks[m].Str("type") == "image") images.Add("@" + offset + "-" + n + "-" + m);
                            }
                            results[id] = Tuple.Create(text, block.Bool("is_error") ?? false, images);
                        }
                    }
                    for (var index = end; index < Math.Min(Entries.Count, end + 30); index++)
                    {
                        var line = ReadLine(stream, Entries[index]);
                        if (line != null) TakeResults(line, Entries[index]);
                    }
                    int talk = 0, tools = 0, at = end - 1;
                    for (; at >= 0 && talk < PageTalk && tools < PageTools; at--)
                    {
                        var offset = Entries[at];
                        var line = ReadLine(stream, offset);
                        if (line == null) continue;
                        var parts = Blocks(line);
                        if (Role(line) == "assistant")
                        {
                            var found = new List<object>();
                            foreach (var block in parts)
                            {
                                var type = block.Str("type");
                                if (type == "text")
                                {
                                    var text = (block.Str("text") ?? "").Trim();
                                    if (text.Length == 0) continue;
                                    found.Add(new JObject { ["role"] = "assistant", ["text"] = Words.Prefix(text, 12_000) });
                                    talk++;
                                }
                                else if (type == "tool_use")
                                {
                                    var id = block.Str("id") ?? "";
                                    var name = block.Str("name") ?? "Tool";
                                    Words.DescribeTool(name, block.Obj("input") ?? new JObject(), out var target, out var detail);
                                    var row = new JObject { ["role"] = "tool", ["id"] = id, ["tool"] = name, ["target"] = target, ["detail"] = detail };
                                    if (results.TryGetValue(id, out var result))
                                    {
                                        row["state"] = result.Item2 ? "error" : "done";
                                        var output = result.Item1.Trim();
                                        if (output.Length > 0) row["output"] = Words.Excerpt(output);
                                        if (result.Item3.Count > 0) row["images"] = result.Item3;
                                    }
                                    else row["state"] = "done";
                                    found.Add(row);
                                    tools++;
                                }
                            }
                            items.InsertRange(0, found);
                            continue;
                        }
                        TakeResults(line, offset);
                        if (line.Bool("isMeta") == true) continue;
                        var words = string.Join("\n", parts.Where(b => b.Str("type") == "text").Select(b => b.Str("text") ?? ""));
                        if (line.Bool("isCompactSummary") == true)
                        {
                            items.Insert(0, new JObject { ["role"] = "summary", ["text"] = Words.Prefix(words, 12_000) });
                            continue;
                        }
                        var pictures = new List<object>();
                        for (var n = 0; n < parts.Count; n++)
                            if (parts[n].Str("type") == "image") pictures.Add("@" + offset + "-" + n);
                        var cleaned = Words.CleanPrompt(words);
                        if (cleaned.Length == 0 && pictures.Count == 0) continue;
                        var userRow = new JObject { ["role"] = "user", ["text"] = Words.Prefix(cleaned, 12_000) };
                        if (pictures.Count > 0) userRow["images"] = pictures;
                        items.Insert(0, userRow);
                        talk++;
                    }
                    cursor = at >= 0 ? at + 1 : (int?)null;
                }
            }
            catch (Exception) { }
        }

        // a call by its id: its tool, its input (each text field up to 60,000 characters) and, once in, its result's text
        // (up to 200,000) and whether it failed; the result sits on a user line shortly after the call
        public JObject Call(string id)
        {
            try
            {
                using (var stream = Open())
                    for (var index = Entries.Count - 1; index >= 0; index--)
                    {
                        var line = ReadLine(stream, Entries[index]);
                        if (line == null || Role(line) != "assistant") continue;
                        var use = Blocks(line).FirstOrDefault(b => b.Str("type") == "tool_use" && b.Str("id") == id);
                        if (use == null) continue;
                        var input = new JObject();
                        foreach (var kv in use.Obj("input") ?? new JObject())
                            input[kv.Key] = kv.Value is string s ? Words.Prefix(s, 60_000) : kv.Value;
                        var found = new JObject { ["tool"] = use.Str("name") ?? "Tool", ["input"] = input };
                        for (var later = index + 1; later < Math.Min(Entries.Count, index + 40); later++)
                        {
                            var next = ReadLine(stream, Entries[later]);
                            var result = next == null ? null : Blocks(next).FirstOrDefault(b => b.Str("type") == "tool_result" && b.Str("tool_use_id") == id);
                            if (result == null) continue;
                            var text = result["content"] as string ?? "";
                            if (result.Arr("content") is List<object> parts)
                                text = string.Join("\n", parts.OfType<JObject>().Where(p => p.Str("type") == "text").Select(p => p.Str("text") ?? ""));
                            found["output"] = Words.Prefix(text, 200_000);
                            found["isError"] = result.Bool("is_error") ?? false;
                            break;
                        }
                        return found;
                    }
            }
            catch (Exception) { }
            return null;
        }

        // a picture named "@offset-block" or "@offset-block-part" (one inside a tool's result), from an indexed line alone
        public byte[] Picture(string name)
        {
            if (!name.StartsWith("@", StringComparison.Ordinal)) return null;
            var numbers = name.Substring(1).Split('-').Select(t => long.TryParse(t, out var n) ? n : -1).ToList();
            if (numbers.Count < 2 || numbers.Any(n => n < 0) || !Entries.Contains(numbers[0])) return null;
            try
            {
                using (var stream = Open())
                {
                    var line = ReadLine(stream, numbers[0]);
                    if (line == null) return null;
                    var parts = Blocks(line);
                    if (numbers[1] >= parts.Count) return null;
                    var block = parts[(int)numbers[1]];
                    if (numbers.Count == 3)
                    {
                        var inner = block.Arr("content")?.OfType<JObject>().ToList();
                        if (inner == null || numbers[2] >= inner.Count) return null;
                        block = inner[(int)numbers[2]];
                    }
                    var data = block.Str("type") == "image" ? block.Obj("source")?.Str("data") : null;
                    return data == null ? null : Convert.FromBase64String(data);
                }
            }
            catch (Exception) { return null; }
        }
    }

    // chats with a transcript, open or closed, for the phone's Recent list
    public sealed class PastChat
    {
        public string Session, Cwd, Title;
        public double At;

        // the chats changed in the last two weeks, newest first: the folder from the transcript's first lines, the title
        // from its latest title lines (the given name, else Claude's), else its opening words
        public static List<PastChat> Read(double days = 14, int limit = 25)
        {
            var root = System.IO.Path.Combine(Paths.ClaudeFolder, "projects");
            var since = DateTime.UtcNow.AddDays(-days);
            var found = new List<Tuple<string, string, DateTime>>();
            try
            {
                if (!Directory.Exists(root)) return new List<PastChat>();
                foreach (var dir in Directory.GetDirectories(root))
                    foreach (var path in Directory.GetFiles(dir, "*.jsonl"))
                    {
                        var at = File.GetLastWriteTimeUtc(path);
                        if (at > since) found.Add(Tuple.Create(path, System.IO.Path.GetFileNameWithoutExtension(path), at));
                    }
            }
            catch (Exception) { }
            var chats = new List<PastChat>();
            foreach (var file in found.OrderByDescending(f => f.Item3).Take(limit))
            {
                try
                {
                    string head, tail;
                    using (var stream = new FileStream(file.Item1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        head = ReadText(stream, 0, 256 << 10);
                        var start = stream.Length > (2 << 20) ? stream.Length - (2 << 20) : 0;
                        tail = ReadText(stream, start, 2 << 20);
                    }
                    var cwd = FirstField("cwd", head);
                    if (cwd == null) continue;
                    var title = LatestTitle(tail) ?? LatestTitle(head) ?? Waiting.OpeningWords(file.Item1);
                    if (string.IsNullOrEmpty(title)) continue;
                    chats.Add(new PastChat { Session = file.Item2, Cwd = cwd, Title = title, At = Math.Round(Spans.ToEpoch(file.Item3) * 1000) });
                }
                catch (Exception) { }
            }
            return chats;
        }

        private static string ReadText(FileStream stream, long from, int length)
        {
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[length];
            var total = 0;
            int read;
            while (total < length && (read = stream.Read(buffer, total, length - total)) > 0) total += read;
            return Encoding.UTF8.GetString(buffer, 0, total);
        }

        // a string field's value from the raw head of the file, JSON escapes read as JSON reads them
        private static string FirstField(string key, string text)
        {
            var at = text.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + key.Length + 3;
            var i = start;
            while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
            if (i >= text.Length) return null;
            return Json.Parse("\"" + text.Substring(start, i - start) + "\"") as string;
        }

        private static string LatestTitle(string text)
        {
            string custom = null, ai = null;
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("{\"type\":\"custom-title\"", StringComparison.Ordinal) && !line.StartsWith("{\"type\":\"ai-title\"", StringComparison.Ordinal)) continue;
                var json = Json.ParseObject(line);
                if (json?.Str("customTitle") != null) custom = json.Str("customTitle");
                if (json?.Str("aiTitle") != null) ai = json.Str("aiTitle");
            }
            return custom ?? ai;
        }
    }

    // The mod's words: the same cleaning, targets and excerpts as mod/hooks/register.ts
    public static class Words
    {
        public static string Prefix(string text, int length) => text.Length > length ? text.Substring(0, length) : text;

        public static string CleanPrompt(string text)
        {
            text = Regex.Replace(text, "<([a-z_-]+)[^>]*>[\\s\\S]*?</\\1>", " ");
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = Regex.Replace(text, "[^\\S\\n]+", " ");
            text = Regex.Replace(text, " *\\n *", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        // the opening of a text on one line, the way the phone names where the mod's window begins
        public static string Anchor(string text) => Prefix(Regex.Replace(text, "\\s+", " ").Trim(), 100);

        public static string Excerpt(string text) =>
            text.Length > 500 ? text.Substring(0, 300) + "\n…\n" + text.Substring(text.Length - 200) : text;

        // a folder's last name, either separator
        public static string LastName(string path)
        {
            var trimmed = (path ?? "").TrimEnd('/', '\\');
            var at = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            return at < 0 ? trimmed : trimmed.Substring(at + 1);
        }

        private static string Marked(string text, string mark) => string.Join("\n", text.Split('\n').Select(l => mark + " " + l));

        public static void DescribeTool(string tool, JObject input, out string target, out string detail)
        {
            string Field(string key) => input.Str(key) ?? "";
            string Cut(string text, int length) => text.Length > length ? text.Substring(0, length) + " …" : text;
            var file = Field("file_path").Length == 0 ? Field("notebook_path") : Field("file_path");
            var name = LastName(file);
            switch (tool)
            {
                case "Bash":
                    target = Field("command").Split('\n')[0];
                    detail = Field("command");
                    break;
                case "Read":
                case "NotebookEdit":
                    target = name;
                    detail = file;
                    break;
                case "Write":
                    target = name;
                    detail = file + "\n\n" + Field("content");
                    break;
                case "Edit":
                    target = name;
                    detail = file + "\n\n" + Marked(Field("old_string"), "-") + "\n" + Marked(Field("new_string"), "+");
                    break;
                case "Grep":
                case "Glob":
                    target = Field("pattern");
                    detail = string.Join("\nin ", new[] { Field("pattern"), Field("path") }.Where(s => s.Length > 0));
                    break;
                case "WebFetch":
                    target = detail = Field("url");
                    break;
                case "WebSearch":
                    target = detail = Field("query");
                    break;
                case "Agent":
                case "Task":
                    target = Field("description");
                    detail = Field("prompt");
                    break;
                default:
                    target = (input.Select(kv => kv.Value).OfType<string>().FirstOrDefault() ?? "").Split('\n')[0];
                    detail = Json.Write(Sorted(input), true);
                    break;
            }
            target = Cut(target, 160);
            detail = Cut(detail, 600);
        }

        // an object with its keys in order, at every depth, the way the Mac writes sorted JSON
        public static object Sorted(object value)
        {
            if (value is JObject o)
            {
                var sorted = new JObject();
                foreach (var k in o.Keys.OrderBy(k => k, StringComparer.Ordinal)) sorted[k] = Sorted(o[k]);
                return sorted;
            }
            if (value is List<object> list) return list.Select(Sorted).ToList();
            return value;
        }
    }
}
