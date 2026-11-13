import Foundation

// Chats waiting on an answer. Claude Code's hooks run "Uselim --hook" with the event on stdin; each chat gets one file
// in ~/Library/Application Support/Uselim/waiting, written as it stops for an answer (a question or a permission ask)
// and removed as the answer comes, the tool runs, a prompt is typed in it, or it closes. The app watches the folder.

enum Kind: String {
    case question, permission

    var word: String { self == .question ? "Question" : "Permission" }
}

struct Waiting {
    let session: String
    let kind: Kind
    let project: String
    let began: String
    let detail: String
    let at: Date
}

let waitingDir = NSHomeDirectory() + "/Library/Application Support/Uselim/waiting"

func readWaiting() -> [Waiting] {
    guard let names = try? FileManager.default.contentsOfDirectory(atPath: waitingDir) else { return [] }
    return names.filter { $0.hasSuffix(".json") }.compactMap { name -> Waiting? in
        guard let data = FileManager.default.contents(atPath: waitingDir + "/" + name),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let kind = (json["kind"] as? String).flatMap(Kind.init(rawValue:)) else { return nil }
        return Waiting(session: String(name.dropLast(5)), kind: kind, project: json["project"] as? String ?? "",
                       began: json["began"] as? String ?? "", detail: json["detail"] as? String ?? "",
                       at: Date(timeIntervalSince1970: json["at"] as? Double ?? 0))
    }.sorted { $0.at > $1.at }
}

// --hook: one event from Claude Code
func runHook() {
    let data = FileHandle.standardInput.readDataToEndOfFile()
    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let session = json["session_id"] as? String, let event = json["hook_event_name"] as? String else { return }
    let file = waitingDir + "/\(session).json"

    func write(_ kind: Kind, _ detail: String) {
        try? FileManager.default.createDirectory(atPath: waitingDir, withIntermediateDirectories: true)
        let cwd = json["cwd"] as? String ?? ""
        let record: [String: Any] = [
            "kind": kind.rawValue,
            "project": (cwd as NSString).lastPathComponent,
            "began": (json["transcript_path"] as? String).map(openingWords) ?? "",
            "detail": detail,
            "at": Date().timeIntervalSince1970,
        ]
        guard let out = try? JSONSerialization.data(withJSONObject: record) else { return }
        FileManager.default.createFile(atPath: file, contents: out)
    }

    switch event {
    case "PreToolUse":
        guard json["tool_name"] as? String == "AskUserQuestion" else { return }
        let questions = (json["tool_input"] as? [String: Any])?["questions"] as? [[String: Any]] ?? []
        let detail = questions.map { q in
            [q["header"] as? String, q["question"] as? String].compactMap { $0 }.joined(separator: ": ")
        }.joined(separator: " · ")
        write(.question, detail.isEmpty ? "A question" : detail)
    case "Notification":
        guard json["notification_type"] as? String == "permission_prompt" else { return }
        write(.permission, json["message"] as? String ?? "A permission")
    default:
        // PostToolUse (the question got its answer, or the tool ran after its permission), UserPromptSubmit, SessionEnd
        try? FileManager.default.removeItem(atPath: file)
        takeDownPermissionCard(session, json, event)
    }
}

// the phone's permission card comes down once the chat moves on: the tool it asks about ran (allowed at the Mac), a
// prompt came, or the chat closed; a waiting --permission sees its card go and leaves
private func takeDownPermissionCard(_ session: String, _ json: [String: Any], _ event: String) {
    let path = relayDir + "/asks/\(session).json"
    guard let card = readJSON(path), card["kind"] as? String == "permission" else { return }
    if event == "PostToolUse" {
        let name = json["tool_name"] as? String ?? ""
        let detail = describeCall(name, json["tool_input"] as? [String: Any] ?? [:]).detail
        guard card["tool"] as? String == toolLabel(name), card["detail"] as? String == detail else { return }
    }
    FileManager.default.createFile(atPath: path, contents: Data())
}

private func plain(_ text: String) -> String {
    text.replacingOccurrences(of: "<([a-z_-]+)[^>]*>[\\s\\S]*?</\\1>", with: "", options: .regularExpression)
        .replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
        .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        .trimmingCharacters(in: .whitespaces)
}

// the chat's opening words: the first thing typed in it, from the transcript's head
func openingWords(_ path: String) -> String {
    guard let handle = FileHandle(forReadingAtPath: path) else { return "" }
    defer { handle.closeFile() }
    let head = String(decoding: handle.readData(ofLength: 256 * 1024), as: UTF8.self)
    for line in head.split(separator: "\n") {
        guard line.contains("\"type\":\"user\""), let data = line.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              json["isMeta"] as? Bool != true,
              let message = json["message"] as? [String: Any] else { continue }
        var text = ""
        if let s = message["content"] as? String {
            text = s
        } else if let parts = message["content"] as? [[String: Any]],
                  let s = parts.first(where: { $0["type"] as? String == "text" })?["text"] as? String {
            text = s
        }
        let clean = plain(text)
        if clean.isEmpty { continue }
        return String(clean.prefix(60))
    }
    return ""
}
