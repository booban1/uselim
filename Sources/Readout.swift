import AppKit

// The menu bar readout: two lines beside the asterisk. Above, the session's share and its time left; below, the weekly
// share, each model's share and the time to the weekly reset, in whole units. A template image while every limit is in
// its normal range, so it follows the bar's own colour; a coloured image once a limit warns (orange) or nears its end
// (red), and a grey one while the figures are stale.

enum Tone { case normal, warning, critical }

func tone(of limit: Limit) -> Tone {
    if limit.percent >= 80 || ["critical", "exceeded", "locked"].contains(limit.severity) { return .critical }
    if limit.percent >= 60 || limit.severity != "normal" { return .warning }
    return .normal
}

struct Piece {
    let text: String
    let tone: Tone
}

func readoutLines(_ limits: [Limit], now: Date = Date()) -> (top: [Piece], bottom: [Piece]) {
    let session = limits.first { $0.kind == "session" }
    let weekly = limits.first { $0.kind == "weekly_all" }
    let models = limits.filter { $0.kind == "weekly_scoped" }
    var top: [Piece] = []
    var bottom: [Piece] = []
    func add(_ text: String, _ tone: Tone, to line: inout [Piece]) {
        if !line.isEmpty { line.append(Piece(text: " · ", tone: .normal)) }
        line.append(Piece(text: text, tone: tone))
    }
    if let session {
        add("\(session.percent)%", tone(of: session), to: &top)
        if let r = session.resets { add(whole(until: r, now: now), .normal, to: &top) }
    }
    if let weekly { add("\(weekly.percent)%", tone(of: weekly), to: &bottom) }
    // a model's own share joins the bar past half; the menu keeps its row
    for m in models where m.percent > 50 { add("\(m.percent)%", tone(of: m), to: &bottom) }
    if let r = weekly?.resets { add(whole(until: r, now: now), .normal, to: &bottom) }
    return (top, bottom)
}

// The dot after the figures, orange, says a chat waits on an answer
func barImage(top: [Piece], bottom: [Piece], stale: Bool, dark: Bool, dot: Bool = false) -> NSImage {
    let coloured = stale || dot || (top + bottom).contains { $0.tone != .normal }
    let base: NSColor = {
        guard coloured else { return .black }
        let ink: NSColor = dark ? .white : .black
        return stale ? ink.withAlphaComponent(0.5) : ink
    }()
    func colour(_ tone: Tone) -> NSColor {
        if stale { return base }
        switch tone {
        case .normal: return base
        case .warning: return .systemOrange
        case .critical: return .systemRed
        }
    }
    func line(_ pieces: [Piece], size: CGFloat) -> NSAttributedString {
        let s = NSMutableAttributedString()
        let font = NSFont.monospacedDigitSystemFont(ofSize: size, weight: .medium)
        for p in pieces {
            s.append(NSAttributedString(string: p.text, attributes: [.font: font, .foregroundColor: colour(p.tone)]))
        }
        return s
    }
    let t = line(top, size: 10)
    let b = line(bottom, size: 8)
    let icon = NSImage(systemSymbolName: "asterisk", accessibilityDescription: "Claude usage")?
        .withSymbolConfiguration(.init(pointSize: 13, weight: .regular))
    let iconW = icon.map { ceil($0.size.width) } ?? 0
    let textW = ceil(max(t.size().width, b.size().width))
    let h: CGFloat = 22
    let dotW: CGFloat = dot ? 9 : 0
    let image = NSImage(size: NSSize(width: iconW + 4 + textW + dotW, height: h), flipped: false) { _ in
        if dot {
            NSColor.systemOrange.setFill()
            NSBezierPath(ovalIn: NSRect(x: iconW + 4 + textW + 4, y: (h - 5) / 2, width: 5, height: 5)).fill()
        }
        if let icon {
            let box = NSRect(x: 0, y: (h - icon.size.height) / 2, width: icon.size.width, height: icon.size.height)
            icon.draw(in: box)
            if coloured {
                base.set()
                box.fill(using: .sourceAtop)
            }
        }
        let x = iconW + 4
        if b.length > 0 {
            t.draw(at: NSPoint(x: x, y: h - t.size().height + 1))
            b.draw(at: NSPoint(x: x, y: 0))
        } else {
            t.draw(at: NSPoint(x: x, y: (h - t.size().height) / 2))
        }
        return true
    }
    image.isTemplate = !coloured
    return image
}
