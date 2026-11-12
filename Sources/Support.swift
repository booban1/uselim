import Foundation

let appVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"

// Lines for ~/Library/Logs/Uselim.log: the start, state changes and failures
private let logPath = NSHomeDirectory() + "/Library/Logs/Uselim.log"
private let stamp: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    return f
}()

func note(_ text: String) {
    let line = "\(stamp.string(from: Date())) \(text)\n"
    guard let data = line.data(using: .utf8) else { return }
    if let handle = FileHandle(forWritingAtPath: logPath) {
        handle.seekToEndOfFile()
        handle.write(data)
        handle.closeFile()
    } else {
        FileManager.default.createFile(atPath: logPath, contents: data)
    }
}

// "15:32", or "3:32 PM", as the Mac shows times
func clock(_ date: Date) -> String {
    DateFormatter.localizedString(from: date, dateStyle: .none, timeStyle: .short)
}

// "12 s ago", "3 min ago", "2 h ago"
func ago(_ date: Date, now: Date = Date()) -> String {
    let s = Int(now.timeIntervalSince(date))
    if s < 60 { return "\(s) s ago" }
    if s < 3600 { return "\(s / 60) min ago" }
    return "\(s / 3600) h ago"
}
