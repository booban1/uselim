import Foundation

// The status line push. Claude Code hands its status line command a JSON after every reply, with rate_limits in
// it: the session and the week, each as used_percentage and resets_at in epoch seconds. "--statusline" takes it on
// stdin, keeps the two figures in ~/Library/Application Support/Uselim/limits.json for the app and prints one line
// for the status line itself. The figures land live while any chat works; the two-minute read stays for idle hours
// and for the per-model line, which the push leaves out.

let supportDir = NSHomeDirectory() + "/Library/Application Support/Uselim"
let pushedPath = supportDir + "/limits.json"

func runStatusLine() {
    let data = FileHandle.standardInput.readDataToEndOfFile()
    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let limits = json["rate_limits"] as? [String: Any] else {
        print("")
        return
    }
    var kept: [String: Any] = ["at": Date().timeIntervalSince1970]
    var words: [String] = []
    for (key, kind) in [("five_hour", "session"), ("seven_day", "weekly_all")] {
        guard let entry = limits[key] as? [String: Any],
              let used = (entry["used_percentage"] as? NSNumber)?.doubleValue else { continue }
        let percent = Int(used.rounded())
        var figure: [String: Any] = ["percent": percent]
        var left = ""
        if let resets = (entry["resets_at"] as? NSNumber)?.doubleValue {
            figure["resets"] = resets
            left = " · " + whole(until: Date(timeIntervalSince1970: resets))
        }
        kept[kind] = figure
        words.append("\(percent)%\(left)")
    }
    try? FileManager.default.createDirectory(atPath: supportDir, withIntermediateDirectories: true)
    if let out = try? JSONSerialization.data(withJSONObject: kept) {
        try? out.write(to: URL(fileURLWithPath: pushedPath), options: .atomic)
    }
    print(words.joined(separator: "   "))
}

// the pushed figures and their time
func readPushed() -> (at: Date, limits: [Limit])? {
    guard let data = FileManager.default.contents(atPath: pushedPath),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let at = (json["at"] as? NSNumber)?.doubleValue else { return nil }
    var limits: [Limit] = []
    for (kind, name) in [("session", "Session (5 hr)"), ("weekly_all", "Weekly (7 days)")] {
        guard let entry = json[kind] as? [String: Any], let percent = (entry["percent"] as? NSNumber)?.intValue else { continue }
        let resets = (entry["resets"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) }
        limits.append(Limit(kind: kind, name: name, percent: percent, severity: "normal", resets: resets))
    }
    return (Date(timeIntervalSince1970: at), limits)
}
