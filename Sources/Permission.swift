import Foundation

// --permission: Claude Code's PermissionRequest hook, set in ~/.claude/settings.json, so a permission reaches the
// phone at any time. VS Code hosts Claude Code through the SDK's canUseTool, and for
// those calls the hook runs beside the panel's own dialog, the first decision applying. The hook puts a card with
// Allow and Deny on the phone (relay/asks/<session>.json, which Uselim pushes and the phone's chat shows) and waits on
// Uselim's server for the phone's answer. An answer at the Mac ends the race: Claude Code cancels the hook, which
// takes its card down as it goes; Uselim also takes a card down once its chat moves on (the tool ran, a new prompt,
// the turn's end) and lets the wait go. Questions stay with the mod's race and plan approvals with the dialog.

private let atTheDialog: Set<String> = ["AskUserQuestion", "ExitPlanMode"]
// under the hook's own 3,600 s timeout in hooks.py
private let longest: TimeInterval = 3_500
// the signal sources live as long as the process: held in a local, an optimised build lets them go at once and a
// cancel leaves the card up
private var signalSources: [DispatchSourceSignal] = []

func runPermission() {
    let data = FileHandle.standardInput.readDataToEndOfFile()
    // the phone side off: the permission stays with VS Code's own dialog
    guard isPhoneOn else { return }
    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let session = json["session_id"] as? String,
          session.range(of: "^[A-Za-z0-9_-]{8,80}$", options: .regularExpression) != nil,
          let tool = json["tool_name"] as? String, !atTheDialog.contains(tool) else { return }
    let cwd = json["cwd"] as? String ?? ""
    let id = "p\(Int((Date().timeIntervalSince1970 * 1000).rounded()))"
    let path = relayDir + "/asks/\(session).json"
    writeJSON(["kind": "permission", "id": id, "tool": toolLabel(tool),
               "detail": describeInput(json["tool_input"] as? [String: Any] ?? [:]),
               "project": (cwd as NSString).lastPathComponent, "began": "",
               "at": (Date().timeIntervalSince1970 * 1000).rounded()], to: path)

    // the card comes down with the hook, whichever way the race ends
    let takeDown = {
        if readJSON(path)?["id"] as? String == id { FileManager.default.createFile(atPath: path, contents: Data()) }
    }
    for number in [SIGTERM, SIGINT, SIGHUP] {
        signal(number, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: number, queue: .global())
        source.setEventHandler {
            takeDown()
            exit(0)
        }
        source.resume()
        signalSources.append(source)
    }

    let started = Date()
    let query = "session=\(session)&ask=\(id)&always=1"
    while Date().timeIntervalSince(started) < longest {
        // a card taken down: the Mac answered, or the chat moved on
        guard readJSON(path)?["id"] as? String == id else { return }
        guard let url = URL(string: "http://127.0.0.1:\(relayPort)/relay/wait?\(query)") else { break }
        var request = URLRequest(url: url, timeoutInterval: 40)
        request.setValue("1", forHTTPHeaderField: "X-Uselim")
        guard let body = fetchJSON(request) else {
            // Uselim restarting, or away: the card stays and the wait goes again
            Thread.sleep(forTimeInterval: 3)
            continue
        }
        switch body["state"] as? String {
        case "answered":
            takeDown()
            guard let allow = (body["answer"] as? [String: Any])?["allow"] as? Bool else { return }
            print(decision(allow ? ["behavior": "allow"] : ["behavior": "deny", "message": "Denied from the phone."]))
            return
        case "stopped":
            takeDown()
            print(decision(["behavior": "deny", "message": "The turn was stopped from the phone.", "interrupt": true]))
            return
        case "gone":
            return
        default:
            continue
        }
    }
    takeDown()
}

// an MCP tool by its server and name, as the phone names it
func toolLabel(_ tool: String) -> String {
    guard tool.hasPrefix("mcp__") else { return tool }
    let rest = String(tool.dropFirst(5))
    guard let split = rest.range(of: "__") else { return rest }
    return rest.replacingCharacters(in: split, with: " ")
}

// the tool's own target where it has one, the mod's rule: a command, a file, an address, a path, a pattern
func describeInput(_ input: [String: Any]) -> String {
    for key in ["command", "file_path", "url", "path", "pattern"] {
        if let value = input[key] as? String { return value }
    }
    guard let data = try? JSONSerialization.data(withJSONObject: input, options: [.sortedKeys]) else { return "" }
    return String(String(decoding: data, as: UTF8.self).prefix(400))
}

private func decision(_ fields: [String: Any]) -> String {
    let out: [String: Any] = ["hookSpecificOutput": ["hookEventName": "PermissionRequest", "decision": fields]]
    guard let data = try? JSONSerialization.data(withJSONObject: out) else { return "{}" }
    return String(decoding: data, as: UTF8.self)
}

private func fetchJSON(_ request: URLRequest) -> [String: Any]? {
    let done = DispatchSemaphore(value: 0)
    var found: [String: Any]?
    URLSession.shared.dataTask(with: request) { data, response, _ in
        if let data, (response as? HTTPURLResponse)?.statusCode == 200 {
            found = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        }
        done.signal()
    }.resume()
    done.wait()
    return found
}
