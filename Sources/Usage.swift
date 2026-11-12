import Foundation

// One call to the usage endpoint the Claude panel reads, and the limits it answers with: the session, the week and
// a weekly line per model, each with its share, a severity word and its reset time.

struct Limit {
    let kind: String
    let name: String
    let percent: Int
    let severity: String
    let resets: Date?
}

enum Fetch {
    case limits([Limit])
    case signedOut
    // 429: the server asks for a pause, with its length in seconds when its Retry-After names one
    case busy(TimeInterval?)
    case failed(String)
}

// the lighter form Claude's own panel asks for; the full one, spend section included, refuses reads with 429 for an
// hour after about twenty-five of them, while this form answers
private let endpoint = URL(string: "https://api.anthropic.com/api/oauth/usage?skip_spend=1")!
private let iso: ISO8601DateFormatter = {
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime]
    return f
}()

func fetchUsage(_ login: Login, done: @escaping (Fetch) -> Void) {
    var request = URLRequest(url: endpoint, timeoutInterval: 15)
    request.setValue("Bearer \(login.token)", forHTTPHeaderField: "Authorization")
    request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
    request.setValue("application/json", forHTTPHeaderField: "Accept")
    request.setValue("Uselim/\(appVersion)", forHTTPHeaderField: "User-Agent")
    URLSession.shared.dataTask(with: request) { data, response, error in
        if let error {
            done(.failed(error.localizedDescription))
            return
        }
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        if code == 401 || code == 403 {
            done(.signedOut)
            return
        }
        if code == 429 {
            let asked = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init)
            done(.busy(asked))
            return
        }
        guard code == 200, let data,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = json["limits"] as? [[String: Any]] else {
            done(.failed("HTTP \(code)"))
            return
        }
        done(.limits(rows.compactMap(parse)))
    }.resume()
}

private func parse(_ row: [String: Any]) -> Limit? {
    guard let kind = row["kind"] as? String, let percent = (row["percent"] as? NSNumber)?.intValue else { return nil }
    let name: String
    switch kind {
    case "session": name = "Session (5 hr)"
    case "weekly_all": name = "Weekly (7 days)"
    default:
        let model = ((row["scope"] as? [String: Any])?["model"] as? [String: Any])?["display_name"] as? String
        name = "\(model ?? kind) (weekly)"
    }
    // the stamp carries six decimals, which the parser leaves out
    let stamp = (row["resets_at"] as? String)?.replacingOccurrences(of: "\\.\\d+", with: "", options: .regularExpression)
    return Limit(kind: kind, name: name, percent: percent, severity: row["severity"] as? String ?? "normal",
                 resets: stamp.flatMap { iso.date(from: $0) })
}

// The last figures, kept on disk so a fresh copy shows them the instant it starts and knows when they were read
private let statePath = NSHomeDirectory() + "/Library/Application Support/Uselim/state.json"

func saveState(_ limits: [Limit], at: Date) {
    let rows: [[String: Any]] = limits.map { l in
        var row: [String: Any] = ["kind": l.kind, "name": l.name, "percent": l.percent, "severity": l.severity]
        if let r = l.resets { row["resets"] = r.timeIntervalSince1970 }
        return row
    }
    try? FileManager.default.createDirectory(atPath: (statePath as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
    guard let data = try? JSONSerialization.data(withJSONObject: ["at": at.timeIntervalSince1970, "limits": rows]) else { return }
    try? data.write(to: URL(fileURLWithPath: statePath), options: .atomic)
}

func loadState() -> (at: Date, limits: [Limit])? {
    guard let data = FileManager.default.contents(atPath: statePath),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let at = (json["at"] as? NSNumber)?.doubleValue, let rows = json["limits"] as? [[String: Any]] else { return nil }
    let limits = rows.compactMap { row -> Limit? in
        guard let kind = row["kind"] as? String, let name = row["name"] as? String,
              let percent = (row["percent"] as? NSNumber)?.intValue else { return nil }
        return Limit(kind: kind, name: name, percent: percent, severity: row["severity"] as? String ?? "normal",
                     resets: (row["resets"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) })
    }
    return limits.isEmpty ? nil : (Date(timeIntervalSince1970: at), limits)
}

// --status: one read, printed
func printStatus() {
    guard case .found(let login) = readLogin() else {
        print("login: unavailable")
        return
    }
    let wait = DispatchSemaphore(value: 0)
    fetchUsage(login) { result in
        switch result {
        case .limits(let limits):
            for l in limits {
                let when = l.resets.map { "\(resetText($0)) (in \(exact(until: $0)))" } ?? "reset unknown"
                print("\(l.name): \(l.percent)% \(l.severity), \(when)")
            }
        case .signedOut: print("signed out")
        case .busy(let asked): print("failed: HTTP 429" + (asked.map { ", retry after \(Int($0)) s" } ?? ""))
        case .failed(let why): print("failed: \(why)")
        }
        wait.signal()
    }
    wait.wait()
    print("notices: \(noticeState())")
}
