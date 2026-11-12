import Foundation

// Time words. The bar shows one whole unit or, past a day, the reset's weekday; the menu the exact span, and reset times read in the Mac's own clock style.

private let weekday: DateFormatter = {
    let f = DateFormatter()
    f.setLocalizedDateFormatFromTemplate("EEE")
    return f
}()

// "2h", "20m", rounded to the nearest unit; past a day the reset's weekday, "Fri", since "1d" reads as a day for a
// reset a day and a half away
func whole(until date: Date, now: Date = Date()) -> String {
    let s = max(0, date.timeIntervalSince(now))
    if s >= 86400 { return weekday.string(from: date) }
    if s >= 3600 { return "\(Int((s / 3600).rounded()))h" }
    return "\(max(1, Int((s / 60).rounded())))m"
}

// "2d 9h", "1h 38m", "20m"
func exact(until date: Date, now: Date = Date()) -> String {
    let s = Int(max(0, date.timeIntervalSince(now)))
    let d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60
    if d > 0 { return "\(d)d \(h)h" }
    if h > 0 { return "\(h)h \(m)m" }
    return "\(m)m"
}

private let timeOnly: DateFormatter = {
    let f = DateFormatter()
    f.timeStyle = .short
    f.dateStyle = .none
    return f
}()

private let dayOnly: DateFormatter = {
    let f = DateFormatter()
    f.setLocalizedDateFormatFromTemplate("EEEdMMM")
    return f
}()

// "Resets at 9:20 PM" today, "Resets Fri 6 Nov at 5:00 AM" on another day
func resetText(_ date: Date, now: Date = Date()) -> String {
    if Calendar.current.isDate(date, inSameDayAs: now) { return "Resets at \(timeOnly.string(from: date))" }
    return "Resets \(dayOnly.string(from: date)) at \(timeOnly.string(from: date))"
}
