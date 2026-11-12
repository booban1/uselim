import Foundation

// The Claude Code login: the keychain item Claude Code keeps, read fresh before every call, since Claude Code renews
// the token and rewrites the item. The read goes through /usr/bin/security, the tool Claude Code wrote the item with,
// so the keychain lets it through with zero dialogs whatever this app's own signature. Nothing here writes.

struct Login {
    let token: String
    let expires: Date?
    let plan: String?
}

enum LoginState {
    case found(Login)
    case missing
    case denied
    case unreadable(Int32)
}

func readLogin() -> LoginState {
    let tool = Process()
    tool.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    tool.arguments = ["find-generic-password", "-w", "-s", "Claude Code-credentials"]
    let out = Pipe()
    tool.standardOutput = out
    tool.standardError = Pipe()
    do { try tool.run() } catch { return .unreadable(-1) }
    let data = out.fileHandleForReading.readDataToEndOfFile()
    tool.waitUntilExit()
    switch tool.terminationStatus {
    case 0: break
    case 44: return .missing
    case 128: return .denied
    default: return .unreadable(tool.terminationStatus)
    }
    guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let oauth = json["claudeAiOauth"] as? [String: Any],
          let token = oauth["accessToken"] as? String else { return .missing }
    let expires = (oauth["expiresAt"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue / 1000) }
    return .found(Login(token: token, expires: expires, plan: oauth["subscriptionType"] as? String))
}
