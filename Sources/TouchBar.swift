import AppKit

// Uselim on the Touch Bar: a Control Strip item with the session and the week beside the asterisk, the dot when a chat
// waits, and a tap that opens a strip with every limit's reset and time left and a chip per chat waiting. The Control
// Strip calls are private to macOS, so each is checked before use. Colours are fixed white with alpha: a dynamic
// colour resolves with the app's appearance, which is wrong on the black bar.

private let trayID = NSTouchBarItem.Identifier("com.simonbooban.uselim.tray")
private let mainID = NSTouchBarItem.Identifier("com.simonbooban.uselim.main")
private let ink = NSColor.white
private let dim = NSColor.white.withAlphaComponent(0.6)
private let faded = NSColor.white.withAlphaComponent(0.5)

final class Strip: NSObject, NSTouchBarDelegate {
    private let dfr = dlopen("/System/Library/PrivateFrameworks/DFRFoundation.framework/DFRFoundation", RTLD_LAZY)
    private let addTray = Selector(("addSystemTrayItem:"))
    private let removeTray = Selector(("removeSystemTrayItem:"))
    private let present = Selector(("presentSystemModalTouchBar:systemTrayItemIdentifier:"))
    private let dismiss = Selector(("dismissSystemModalTouchBar:"))

    private var trayItem: NSCustomTouchBarItem?
    private let button = NSButton(title: "", target: nil, action: nil)
    // one bar, presented again on each open; the strip's content is rebuilt from the figures
    private lazy var bar: NSTouchBar = {
        let tb = NSTouchBar()
        tb.delegate = self
        tb.defaultItemIdentifiers = [mainID]
        return tb
    }()
    private var visible: NSKeyValueObservation?
    private var opened = false
    private let content: NSStackView = {
        let s = NSStackView()
        s.orientation = .horizontal
        s.alignment = .centerY
        s.spacing = 34
        s.edgeInsets = NSEdgeInsets(top: 0, left: 8, bottom: 0, right: 8)
        return s
    }()
    private var limits: [Limit] = []
    private var waiting: [Waiting] = []
    private var stale = false

    var available: Bool {
        dfr != nil && dlsym(dfr, "DFRElementSetControlStripPresenceForIdentifier") != nil
            && (NSTouchBarItem.self as AnyObject).responds(to: addTray)
            && (NSTouchBar.self as AnyObject).responds(to: present)
    }

    var isShown: Bool { trayItem != nil }

    private func setPresence(_ on: Bool) {
        guard let dfr, let f = dlsym(dfr, "DFRElementSetControlStripPresenceForIdentifier") else { return }
        typealias Presence = @convention(c) (CFString, Bool) -> Void
        unsafeBitCast(f, to: Presence.self)(trayID.rawValue as CFString, on)
    }

    private func closeBox() {
        guard let dfr, let f = dlsym(dfr, "DFRSystemModalShowsCloseBoxWhenFrontMost") else { return }
        typealias Flag = @convention(c) (Bool) -> Void
        unsafeBitCast(f, to: Flag.self)(true)
    }

    private func symbol(_ name: String, _ size: CGFloat) -> NSImage? {
        NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(.init(pointSize: size, weight: .regular))
    }

    // MARK: the Control Strip item

    func show() {
        guard available, trayItem == nil else { return }
        button.target = self
        button.action = #selector(toggle)
        button.isBordered = false
        button.imagePosition = .imageLeading
        button.image = symbol("asterisk", 13)
        button.contentTintColor = ink
        let item = NSCustomTouchBarItem(identifier: trayID)
        item.view = button
        trayItem = item
        closeBox()
        _ = (NSTouchBarItem.self as AnyObject).perform(addTray, with: item)
        setPresence(true)
        visible = bar.observe(\.isVisible) { [weak self] bar, _ in self?.opened = bar.isVisible }
        redraw()
    }

    // takes the Control Strip's slot back, from Now Playing for one
    func reclaim() {
        guard trayItem != nil else { return }
        setPresence(true)
    }

    func hide() {
        guard let item = trayItem else { return }
        if opened, (NSTouchBar.self as AnyObject).responds(to: dismiss) {
            _ = (NSTouchBar.self as AnyObject).perform(dismiss, with: bar)
        }
        opened = false
        setPresence(false)
        if (NSTouchBarItem.self as AnyObject).responds(to: removeTray) {
            _ = (NSTouchBarItem.self as AnyObject).perform(removeTray, with: item)
        }
        trayItem = nil
    }

    @objc private func toggle() {
        if opened {
            if (NSTouchBar.self as AnyObject).responds(to: dismiss) { _ = (NSTouchBar.self as AnyObject).perform(dismiss, with: bar) }
            opened = false
            return
        }
        fill()
        _ = (NSTouchBar.self as AnyObject).perform(present, with: bar, with: trayID.rawValue)
        opened = true
    }

    func touchBar(_ touchBar: NSTouchBar, makeItemForIdentifier identifier: NSTouchBarItem.Identifier) -> NSTouchBarItem? {
        guard identifier == mainID else { return nil }
        let item = NSCustomTouchBarItem(identifier: mainID)
        item.view = content
        return item
    }

    // MARK: the figures

    func update(limits: [Limit], waiting: [Waiting], stale: Bool) {
        self.limits = limits
        self.waiting = waiting
        self.stale = stale
        redraw()
    }

    private func colour(_ limit: Limit) -> NSColor {
        if stale { return faded }
        switch tone(of: limit) {
        case .normal: return ink
        case .warning: return .systemOrange
        case .critical: return .systemRed
        }
    }

    private func redraw() {
        guard trayItem != nil else { return }
        let title = NSMutableAttributedString()
        let font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        func run(_ text: String, _ colour: NSColor, _ font: NSFont = font, lift: CGFloat = 0) {
            title.append(NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: colour, .baselineOffset: lift]))
        }
        let session = limits.first { $0.kind == "session" }
        let weekly = limits.first { $0.kind == "weekly_all" }
        if let session { run("\(session.percent)%", colour(session)) }
        if let weekly {
            if title.length > 0 { run(" · ", stale ? faded : ink) }
            run("\(weekly.percent)%", colour(weekly))
        }
        if title.length == 0 { run("…", ink) }
        if !waiting.isEmpty { run("  ●", .systemOrange, .systemFont(ofSize: 7), lift: 2) }
        button.attributedTitle = title
        if opened { fill() }
    }

    // the strip: a group per limit, a chip per chat waiting
    private func fill() {
        for v in content.arrangedSubviews {
            content.removeArrangedSubview(v)
            v.removeFromSuperview()
        }
        let now = Date()
        for l in limits {
            let icon: String
            switch l.kind {
            case "session": icon = "clock"
            case "weekly_all": icon = "calendar"
            default: icon = "sparkle"
            }
            let image = NSImageView(image: symbol(icon, 14) ?? NSImage())
            image.contentTintColor = stale ? faded : ink
            let figure = NSTextField(labelWithString: "\(l.percent)%")
            figure.font = .monospacedDigitSystemFont(ofSize: 13, weight: .regular)
            figure.textColor = colour(l)
            let words: String
            if l.kind == "weekly_scoped" {
                words = l.name.replacingOccurrences(of: " (weekly)", with: "")
            } else if let r = l.resets {
                words = "\(resetText(r, now: now).replacingOccurrences(of: "Resets at ", with: "").replacingOccurrences(of: "Resets ", with: "")) · \(exact(until: r, now: now))"
            } else {
                words = ""
            }
            let sub = NSTextField(labelWithString: words)
            sub.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
            sub.textColor = dim
            let group = NSStackView(views: [image, figure, sub])
            group.orientation = .horizontal
            group.alignment = .centerY
            group.spacing = 7
            content.addArrangedSubview(group)
        }
        for w in waiting {
            let line = NSMutableAttributedString(string: w.project, attributes: [.font: NSFont.boldSystemFont(ofSize: 12), .foregroundColor: ink])
            line.append(NSAttributedString(string: " · ", attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: ink]))
            line.append(NSAttributedString(string: w.kind.word, attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.systemOrange]))
            let label = NSTextField(labelWithAttributedString: line)
            let chip = NSStackView(views: [label])
            chip.edgeInsets = NSEdgeInsets(top: 0, left: 10, bottom: 0, right: 10)
            chip.wantsLayer = true
            chip.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.2).cgColor
            chip.layer?.cornerRadius = 6
            chip.heightAnchor.constraint(equalToConstant: 24).isActive = true
            content.addArrangedSubview(chip)
        }
    }
}
