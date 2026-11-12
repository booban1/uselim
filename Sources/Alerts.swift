import AppKit
import UserNotifications

// Notices: one as a limit crosses 60, 80 and 100% within a window, and one when a limit that had warned resets.
// Each limit's window is its reset time; a new reset time starts the steps over.

final class Alerts {
    // the limit notices, handed on to the phone relay
    var onLimit: ((String, String?) -> Void)?
    private var told: [String: Int] = [:]
    private var windows: [String: Date] = [:]

    // figures carried over from the last copy: their steps count as told, so a restart stays quiet
    func seed(_ limits: [Limit]) {
        for l in limits {
            windows[l.kind] = l.resets ?? .distantPast
            told[l.kind] = l.percent >= 100 ? 100 : l.percent >= 80 ? 80 : l.percent >= 60 ? 60 : 0
        }
    }

    func check(_ limits: [Limit]) {
        for l in limits {
            let window = l.resets ?? .distantPast
            // the read and the status line push write the same reset time with and without its fraction of a second
            if let known = windows[l.kind], abs(known.timeIntervalSince(window)) > 60 {
                if (told[l.kind] ?? 0) >= 60 {
                    send("\(l.name) reset", "Back to \(l.percent)%")
                    onLimit?("\(l.name) reset", "Back to \(l.percent)%")
                }
                // (a window change with zero earlier warning stays quiet)
                told[l.kind] = 0
            }
            windows[l.kind] = window
            let step = l.percent >= 100 ? 100 : l.percent >= 80 ? 80 : l.percent >= 60 ? 60 : 0
            guard step > (told[l.kind] ?? 0) else { continue }
            told[l.kind] = step
            let when = l.resets.map { "\(resetText($0)), in \(exact(until: $0))" }
            send(step == 100 ? "\(l.name) spent" : "\(l.name) at \(l.percent)%", when)
            onLimit?(step == 100 ? "\(l.name) spent" : "\(l.name) at \(l.percent)%", when)
        }
    }

    func send(_ title: String, _ body: String?) {
        let content = UNMutableNotificationContent()
        content.title = title
        if let body { content.body = body }
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { allowed, error in
            guard allowed else {
                // macOS refuses Uselim's notices, an earlier Don't Allow or the switch in System Settings: the log names
                // why, and the menu offers the way back
                let why = error.map { error in
                    let found = error as NSError
                    return "\(found.domain) \(found.code), \(found.localizedDescription)"
                } ?? "refused"
                note("notice held back, macOS refuses notifications: \(why)")
                return
            }
            center.add(request)
        }
        note("notice: \(title)\(body.map { ", \($0)" } ?? "")")
    }
}

// the Notifications pane of System Settings with Uselim picked, where a refused permission is turned back on
func openNotificationSettings() {
    let id = Bundle.main.bundleIdentifier ?? "com.simonbooban.uselim"
    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.notifications?id=\(id)") {
        NSWorkspace.shared.open(url)
    }
}

// --status: where the notifications permission stands
func noticeState() -> String {
    let wait = DispatchSemaphore(value: 0)
    var word = "unknown"
    UNUserNotificationCenter.current().getNotificationSettings { settings in
        switch settings.authorizationStatus {
        case .authorized: word = "allowed"
        case .denied: word = "denied"
        case .notDetermined: word = "unasked"
        case .provisional: word = "provisional"
        default: word = "other"
        }
        wait.signal()
    }
    wait.wait()
    return word
}
