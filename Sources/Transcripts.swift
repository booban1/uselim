import Foundation

// A chat's whole history from Claude Code's own transcript (~/.claude/projects/<folder>/<session>.jsonl), for the
// phone's earlier pages and the chat's title. The mod's history holds the recent window of the conversation as the
// engine holds it, which a compaction starts over; the transcript keeps every line from the first words. Indexed in
// the background once and extended as it grows: the byte offset of each line of the main conversation (a line that
// opens with its parent's id, off any subagent's chain, and names its role in its first few hundred bytes), and the
// titles VS Code shows, the name given to the chat first, else Claude's.
//
// Every call runs on `transcriptQueue`.

let transcriptQueue = DispatchQueue(label: "uselim.transcripts", qos: .userInitiated)

private let headLength = 600
private let pageTalk = 40
private let pageTools = 160

// Claude Code's folder for a working directory: every character outside letters and digits becomes a dash
func transcriptPath(cwd: String, session: String) -> String {
    let folder = String(cwd.unicodeScalars.map { scalar -> Character in
        CharacterSet.alphanumerics.contains(scalar) && scalar.isASCII ? Character(scalar) : "-"
    })
    return NSHomeDirectory() + "/.claude/projects/\(folder)/\(session).jsonl"
}

final class Transcript {
    let path: String
    private(set) var entries: [Int] = []
    private var customTitle = ""
    private var aiTitle = ""
    private var indexed = 0

    init(path: String) { self.path = path }

    var title: String { customTitle.isEmpty ? aiTitle : customTitle }

    // reads the lines added since the last call
    func refresh() {
        guard let handle = FileHandle(forReadingAtPath: path) else { return }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd(), Int(size) > indexed else { return }
        try? handle.seek(toOffset: UInt64(indexed))
        var pending = Data()
        var start = indexed
        while true {
            let chunk = handle.readData(ofLength: 8 << 20)
            if chunk.isEmpty { break }
            pending.append(chunk)
            var used = 0
            pending.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                guard let base = raw.baseAddress else { return }
                var position = 0
                while position < raw.count, let hit = memchr(base + position, 0x0A, raw.count - position) {
                    let end = base.distance(to: hit)
                    classify(UnsafeRawBufferPointer(rebasing: raw[position..<end]), at: start + position)
                    position = end + 1
                }
                used = position
            }
            start += used
            pending = used == pending.count ? Data() : Data(pending.suffix(from: pending.startIndex + used))
        }
        indexed = start
    }

    private func classify(_ line: UnsafeRawBufferPointer, at offset: Int) {
        let head = String(decoding: UnsafeRawBufferPointer(rebasing: line.prefix(headLength)), as: UTF8.self)
        if head.hasPrefix("{\"parentUuid\""), head.contains("\"isSidechain\":false"),
           head.contains("\"role\":\"user\"") || head.contains("\"role\":\"assistant\"") {
            entries.append(offset)
        } else if head.hasPrefix("{\"type\":\"custom-title\"") || head.hasPrefix("{\"type\":\"ai-title\""),
                  let json = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any] {
            if let title = json["customTitle"] as? String { customTitle = title }
            if let title = json["aiTitle"] as? String { aiTitle = title }
        }
    }

    // MARK: reading lines

    private func readLine(_ handle: FileHandle, at offset: Int) -> [String: Any]? {
        try? handle.seek(toOffset: UInt64(offset))
        var data = Data()
        while true {
            let chunk = handle.readData(ofLength: 256 << 10)
            if chunk.isEmpty { break }
            if let newline = chunk.firstIndex(of: 0x0A) {
                data.append(chunk[chunk.startIndex..<newline])
                break
            }
            data.append(chunk)
        }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    private func blocks(_ line: [String: Any]) -> [[String: Any]] {
        let message = line["message"] as? [String: Any]
        if let text = message?["content"] as? String { return [["type": "text", "text": text]] }
        return message?["content"] as? [[String: Any]] ?? []
    }

    // MARK: anchors: where the mod's window begins

    func index(ofTool id: String) -> Int? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        for index in stride(from: entries.count - 1, through: 0, by: -1) {
            guard let line = readLine(handle, at: entries[index]), (line["message"] as? [String: Any])?["role"] as? String == "assistant"
            else { continue }
            if blocks(line).contains(where: { $0["type"] as? String == "tool_use" && $0["id"] as? String == id }) { return index }
        }
        return nil
    }

    func index(ofText anchor: String) -> Int? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        let wanted = anchorWords(anchor)
        guard !wanted.isEmpty else { return nil }
        for index in stride(from: entries.count - 1, through: 0, by: -1) {
            guard let line = readLine(handle, at: entries[index]) else { continue }
            let text = blocks(line).compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }
                .joined(separator: "\n")
            let words = anchorWords(cleanPrompt(text))
            if !words.isEmpty, words.hasPrefix(wanted) || wanted.hasPrefix(words) { return index }
        }
        return nil
    }

    // MARK: pages

    // the items before entry `end`, oldest first, up to a page of messages; the cursor for the page before, nil at the
    // chat's start. A tool's result comes from the user line after its call, so the lines just past the page are read
    // for results first.
    func page(before end: Int) -> (items: [[String: Any]], cursor: Int?) {
        guard let handle = FileHandle(forReadingAtPath: path), end > 0 else { return ([], nil) }
        defer { try? handle.close() }
        var results: [String: (text: String, isError: Bool, images: [String])] = [:]
        func takeResults(_ line: [String: Any], at offset: Int) {
            for (n, block) in blocks(line).enumerated() where block["type"] as? String == "tool_result" {
                guard let id = block["tool_use_id"] as? String else { continue }
                var text = ""
                var images: [String] = []
                if let plain = block["content"] as? String { text = plain }
                if let parts = block["content"] as? [[String: Any]] {
                    text = parts.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }
                        .joined(separator: "\n")
                    for (m, part) in parts.enumerated() where part["type"] as? String == "image" {
                        images.append("@\(offset)-\(n)-\(m)")
                    }
                }
                results[id] = (text, block["is_error"] as? Bool ?? false, images)
            }
        }
        for index in end..<min(entries.count, end + 30) {
            if let line = readLine(handle, at: entries[index]) { takeResults(line, at: entries[index]) }
        }
        var items: [[String: Any]] = []
        var talk = 0
        var tools = 0
        var index = end - 1
        while index >= 0, talk < pageTalk, tools < pageTools {
            defer { index -= 1 }
            let offset = entries[index]
            guard let line = readLine(handle, at: offset) else { continue }
            let role = (line["message"] as? [String: Any])?["role"] as? String
            let parts = blocks(line)
            if role == "assistant" {
                var found: [[String: Any]] = []
                for block in parts {
                    switch block["type"] as? String {
                    case "text":
                        let text = (block["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                        if !text.isEmpty { found.append(["role": "assistant", "text": String(text.prefix(12_000))]); talk += 1 }
                    case "tool_use":
                        let id = block["id"] as? String ?? ""
                        let name = block["name"] as? String ?? "Tool"
                        let described = describeTool(name, block["input"] as? [String: Any] ?? [:])
                        var row: [String: Any] = ["role": "tool", "id": id, "tool": name, "target": described.target,
                                                  "detail": described.detail]
                        if let result = results[id] {
                            row["state"] = result.isError ? "error" : "done"
                            let output = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
                            if !output.isEmpty { row["output"] = excerpt(output) }
                            if !result.images.isEmpty { row["images"] = result.images }
                        } else {
                            row["state"] = "done"
                        }
                        found.append(row)
                        tools += 1
                    default:
                        break
                    }
                }
                items.insert(contentsOf: found, at: 0)
                continue
            }
            takeResults(line, at: offset)
            if line["isMeta"] as? Bool == true { continue }
            let text = parts.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }.joined(separator: "\n")
            if line["isCompactSummary"] as? Bool == true {
                items.insert(["role": "summary", "text": String(text.prefix(12_000))], at: 0)
                continue
            }
            let images = parts.enumerated().filter { $0.element["type"] as? String == "image" }.map { "@\(offset)-\($0.offset)" }
            let words = cleanPrompt(text)
            guard !words.isEmpty || !images.isEmpty else { continue }
            var row: [String: Any] = ["role": "user", "text": String(words.prefix(12_000))]
            if !images.isEmpty { row["images"] = images }
            items.insert(row, at: 0)
            talk += 1
        }
        return (items, index >= 0 ? index + 1 : nil)
    }

    // MARK: one tool call whole, for the phone's code views

    // a call by its id: its tool, its input (each text field up to 60,000 characters) and, once in, its result's text
    // (up to 200,000) and whether it failed; the result sits on a user line shortly after the call
    func call(_ id: String) -> [String: Any]? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        for index in stride(from: entries.count - 1, through: 0, by: -1) {
            guard let line = readLine(handle, at: entries[index]),
                  (line["message"] as? [String: Any])?["role"] as? String == "assistant",
                  let use = blocks(line).first(where: { $0["type"] as? String == "tool_use" && $0["id"] as? String == id })
            else { continue }
            let input = (use["input"] as? [String: Any] ?? [:]).mapValues { value -> Any in
                (value as? String).map { String($0.prefix(60_000)) } ?? value
            }
            var found: [String: Any] = ["tool": use["name"] as? String ?? "Tool", "input": input]
            for later in (index + 1)..<min(entries.count, index + 40) {
                guard let next = readLine(handle, at: entries[later]),
                      let result = blocks(next).first(where: {
                          $0["type"] as? String == "tool_result" && $0["tool_use_id"] as? String == id
                      }) else { continue }
                var text = result["content"] as? String ?? ""
                if let parts = result["content"] as? [[String: Any]] {
                    text = parts.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }
                        .joined(separator: "\n")
                }
                found["output"] = String(text.prefix(200_000))
                found["isError"] = result["is_error"] as? Bool ?? false
                break
            }
            return found
        }
        return nil
    }

    // a picture named "@offset-block" or "@offset-block-part" (one inside a tool's result), from an indexed line alone
    func picture(_ name: String) -> Data? {
        let numbers = name.dropFirst().split(separator: "-").compactMap { Int($0) }
        guard name.hasPrefix("@"), numbers.count >= 2, let offset = numbers.first,
              entries.contains(offset), let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        guard let line = readLine(handle, at: offset) else { return nil }
        let parts = blocks(line)
        guard numbers[1] < parts.count else { return nil }
        var block = parts[numbers[1]]
        if numbers.count == 3 {
            guard let inner = block["content"] as? [[String: Any]], numbers[2] < inner.count else { return nil }
            block = inner[numbers[2]]
        }
        guard block["type"] as? String == "image", let source = block["source"] as? [String: Any],
              let data = source["data"] as? String else { return nil }
        return Data(base64Encoded: data)
    }
}

// MARK: chats with a transcript, open or closed, for the phone's Recent list

struct PastChat {
    let session: String
    let cwd: String
    let title: String
    let at: Double
}

// the chats changed in the last two weeks, newest first: the folder from the transcript's first lines, the title from
// its latest title lines (the given name, else Claude's), else its opening words
func pastChats(days: Double = 14, limit: Int = 25) -> [PastChat] {
    let root = NSHomeDirectory() + "/.claude/projects"
    let since = Date().addingTimeInterval(-days * 86_400)
    var found: [(path: String, session: String, at: Date)] = []
    for folder in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? [] {
        let dir = root + "/" + folder
        for name in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] where name.hasSuffix(".jsonl") {
            let path = dir + "/" + name
            guard let at = (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date,
                  at > since else { continue }
            found.append((path, String(name.dropLast(6)), at))
        }
    }
    return found.sorted { $0.at > $1.at }.prefix(limit).compactMap { file in
        guard let handle = FileHandle(forReadingAtPath: file.path) else { return nil }
        defer { try? handle.close() }
        let head = String(decoding: handle.readData(ofLength: 256 << 10), as: UTF8.self)
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > 2 << 20 ? size - (2 << 20) : 0)
        let tail = String(decoding: handle.readData(ofLength: 2 << 20), as: UTF8.self)
        guard let cwd = firstField("cwd", in: head) else { return nil }
        let title = latestTitle(in: tail) ?? latestTitle(in: head) ?? openingWords(file.path)
        guard !title.isEmpty else { return nil }
        return PastChat(session: file.session, cwd: cwd, title: title, at: (file.at.timeIntervalSince1970 * 1000).rounded())
    }
}

private func firstField(_ key: String, in text: String) -> String? {
    guard let range = text.range(of: "\"\(key)\":\""), let end = text[range.upperBound...].firstIndex(of: "\"") else {
        return nil
    }
    return String(text[range.upperBound..<end]).replacingOccurrences(of: "\\/", with: "/")
}

private func latestTitle(in text: String) -> String? {
    var custom: String?
    var ai: String?
    for line in text.split(separator: "\n") where line.hasPrefix("{\"type\":\"custom-title\"") || line.hasPrefix("{\"type\":\"ai-title\"") {
        guard let json = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else { continue }
        if let title = json["customTitle"] as? String { custom = title }
        if let title = json["aiTitle"] as? String { ai = title }
    }
    return custom ?? ai
}

// MARK: the mod's words, in Swift: the same cleaning, targets and excerpts as mod/hooks/register.ts

func cleanPrompt(_ text: String) -> String {
    text.replacingOccurrences(of: "<([a-z_-]+)[^>]*>[\\s\\S]*?</\\1>", with: " ", options: .regularExpression)
        .replacingOccurrences(of: "<[^>]+>", with: " ", options: .regularExpression)
        .replacingOccurrences(of: "[^\\S\\n]+", with: " ", options: .regularExpression)
        .replacingOccurrences(of: " *\\n *", with: "\n", options: .regularExpression)
        .replacingOccurrences(of: "\\n{3,}", with: "\n\n", options: .regularExpression)
        .trimmingCharacters(in: .whitespacesAndNewlines)
}

// the opening of a text on one line, the way the phone names where the mod's window begins
func anchorWords(_ text: String) -> String {
    String(text.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        .trimmingCharacters(in: .whitespaces).prefix(100))
}

private func excerpt(_ text: String) -> String {
    text.count > 500 ? String(text.prefix(300)) + "\n…\n" + String(text.suffix(200)) : text
}

private func describeTool(_ tool: String, _ input: [String: Any]) -> (target: String, detail: String) {
    func field(_ key: String) -> String { input[key] as? String ?? "" }
    func cut(_ text: String, _ length: Int) -> String { text.count > length ? String(text.prefix(length)) + " …" : text }
    let file = field("file_path").isEmpty ? field("notebook_path") : field("file_path")
    let name = file.split(separator: "/").last.map(String.init) ?? file
    func marked(_ text: String, _ mark: String) -> String {
        text.split(separator: "\n", omittingEmptySubsequences: false).map { "\(mark) \($0)" }.joined(separator: "\n")
    }
    var target = ""
    var detail = ""
    switch tool {
    case "Bash":
        target = field("command").split(separator: "\n").first.map(String.init) ?? ""
        detail = field("command")
    case "Read", "NotebookEdit":
        target = name
        detail = file
    case "Write":
        target = name
        detail = "\(file)\n\n\(field("content"))"
    case "Edit":
        target = name
        detail = "\(file)\n\n\(marked(field("old_string"), "-"))\n\(marked(field("new_string"), "+"))"
    case "Grep", "Glob":
        target = field("pattern")
        detail = [field("pattern"), field("path")].filter { !$0.isEmpty }.joined(separator: "\nin ")
    case "WebFetch":
        target = field("url")
        detail = field("url")
    case "WebSearch":
        target = field("query")
        detail = field("query")
    case "Agent", "Task":
        target = field("description")
        detail = field("prompt")
    default:
        target = input.values.compactMap { $0 as? String }.first?.split(separator: "\n").first.map(String.init) ?? ""
        let json = (try? JSONSerialization.data(withJSONObject: input, options: [.prettyPrinted, .sortedKeys])) ?? Data()
        detail = String(decoding: json, as: UTF8.self)
    }
    return (cut(target, 160), cut(detail, 600))
}
