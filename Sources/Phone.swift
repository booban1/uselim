import AppKit
import CoreImage

// The phone side: off until turned on, and on by itself where a phone already subscribed (relay/push.json). Turning it
// on brings Tailscale in where it is missing: Tailscale's own installer, downloaded from tailscale.com, its signature
// checked, then opened, the person's steps staying theirs (the admin password, the VPN approval, the sign-in). The relay
// then goes behind Tailscale Serve, and each live step shows as one line under the menu's Phone row.

var isPhoneOn: Bool {
    get {
        UserDefaults.standard.object(forKey: "phone") as? Bool
            ?? FileManager.default.fileExists(atPath: relayDir + "/push.json")
    }
    set { UserDefaults.standard.set(newValue, forKey: "phone") }
}

// Tailscale's command: the app's own binary answers as one, the App Store's copy and the standalone alike
func tailscaleTool() -> String? {
    ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"]
        .first { FileManager.default.isExecutableFile(atPath: $0) }
}

struct TailscaleStatus {
    // Running, NeedsLogin, Stopped, Starting, NoState; empty while the service is unreachable
    let state: String
    // the Mac's name on the tailnet, its final dot left off
    let dnsName: String
    // the tailnet login the Mac is signed in with, which Tailscale Serve names on the phone's requests
    let login: String
    // certificates on for the tailnet, which Serve's HTTPS needs
    let hasHTTPS: Bool
    // Serve forwards to the relay
    let isServing: Bool
}

// the first JSON object in a command's output, past any warning before it
private func jsonObject(in text: String) -> [String: Any]? {
    guard let start = text.firstIndex(of: "{") else { return nil }
    return (try? JSONSerialization.jsonObject(with: Data(text[start...].utf8))) as? [String: Any]
}

func readTailscale(_ tool: String) -> TailscaleStatus {
    let status = jsonObject(in: runTool(tool, ["status", "--json"], timeout: 8).out)
    let me = status?["Self"] as? [String: Any]
    let name = me?["DNSName"] as? String ?? ""
    let user = (me?["UserID"] as? NSNumber).map { "\($0)" } ?? ""
    let login = ((status?["User"] as? [String: Any])?[user] as? [String: Any])?["LoginName"] as? String ?? ""
    let serve = runTool(tool, ["serve", "status", "--json"], timeout: 8).out
    return TailscaleStatus(
        state: status?["BackendState"] as? String ?? "",
        dnsName: name.hasSuffix(".") ? String(name.dropLast()) : name,
        login: login,
        hasHTTPS: (status?["CertDomains"] as? [Any])?.isEmpty == false,
        isServing: serve.contains("127.0.0.1:\(relayPort)") || serve.contains("localhost:\(relayPort)"))
}

// Tailscale's own installer alone: macOS accepts it as a notarized Developer ID package, signed by Tailscale's team
private func isTailscaleInstaller(_ file: URL) -> Bool {
    let check = runTool("/usr/sbin/spctl", ["--assess", "--type", "install", "-vv", file.path], timeout: 60)
    return check.status == 0 && check.out.contains("accepted")
        && check.out.contains("Developer ID Installer: Tailscale Inc. (W5364U7YZB)")
}

enum PhoneStep: Equatable {
    case off, downloading, installer, opening, signIn, connect, serving, https(String), ready, failed(String)
}

final class PhoneSetup {
    var onChange: (() -> Void)?
    // the Mac's name and login on the tailnet, each time Tailscale's status names them, for the relay's checks of
    // Host, Origin and Tailscale Serve's login header
    var onName: ((String) -> Void)?
    var onLogin: ((String) -> Void)?
    private(set) var step: PhoneStep = .off {
        didSet { if step != oldValue { onChange?() } }
    }
    private(set) var dnsName = ""
    private var timer: Timer?
    private var isChecking = false
    private var download: URLSessionDownloadTask?
    private var installer: URL?
    private var serving: Process?
    private var isAppOpened = false

    // the line under the Phone row: the live step alone, explanations left to the guide
    var line: String? {
        switch step {
        case .downloading: return "Downloading Tailscale…"
        case .installer: return "Finish Tailscale's installer"
        case .opening: return "Opening Tailscale…"
        case .signIn: return "Sign in to Tailscale"
        case .connect: return "Turn Tailscale on"
        case .serving: return "Starting the relay on Tailscale…"
        case .https: return "Turn on HTTPS in Tailscale"
        case .failed(let why): return why
        case .off, .ready: return nil
        }
    }

    func start() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in self?.check() }
        check()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        download?.cancel()
        download = nil
        serving?.terminate()
        serving = nil
        step = .off
    }

    // a click on the line: the step's own way forward
    func act() {
        switch step {
        case .installer:
            if let installer { NSWorkspace.shared.open(installer) }
        case .opening, .signIn, .connect:
            openTailscale()
        case .https(let link):
            if let url = URL(string: link) { NSWorkspace.shared.open(url) }
        case .failed:
            check()
        default:
            break
        }
    }

    private func openTailscale() {
        isAppOpened = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/Applications/Tailscale.app"),
                                           configuration: NSWorkspace.OpenConfiguration())
    }

    private func check() {
        guard timer != nil, !isChecking else { return }
        guard let tool = tailscaleTool() else {
            if download == nil, installer == nil { fetchInstaller() }
            return
        }
        installer = nil
        isChecking = true
        DispatchQueue.global(qos: .utility).async {
            let status = readTailscale(tool)
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.isChecking = false
                guard self.timer != nil else { return }
                self.dnsName = status.dnsName
                if !status.dnsName.isEmpty { self.onName?(status.dnsName) }
                if !status.login.isEmpty { self.onLogin?(status.login) }
                self.advance(tool, status)
            }
        }
    }

    private func advance(_ tool: String, _ status: TailscaleStatus) {
        switch status.state {
        case "Running":
            if status.isServing, status.hasHTTPS {
                step = .ready
            } else if serving == nil {
                serve(tool)
            }
        case "NeedsLogin", "NeedsMachineAuth":
            step = .signIn
        case "Stopped":
            step = .connect
        case "":
            // the service unreachable: Tailscale's app has yet to start, or its VPN to be approved
            if !isAppOpened { openTailscale() }
            step = .opening
        default:
            step = .opening
        }
    }

    // Serve for the relay; a tailnet with HTTPS off answers with Tailscale's own link to turn it on and waits, and the
    // command finishes by itself once it is on
    private func serve(_ tool: String) {
        step = .serving
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = ["serve", "--bg", "\(relayPort)"]
        let out = Pipe()
        process.standardOutput = out
        process.standardError = out
        let said = NSMutableString()
        out.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            DispatchQueue.main.async {
                said.append(String(decoding: data, as: UTF8.self))
                let text = said as String
                if let found = text.range(of: "https://login\\.tailscale\\.com/[^\\s]+", options: .regularExpression) {
                    self?.step = .https(String(text[found]))
                }
            }
        }
        process.terminationHandler = { [weak self] done in
            out.fileHandleForReading.readabilityHandler = nil
            DispatchQueue.main.async {
                guard let self else { return }
                self.serving = nil
                if done.terminationStatus != 0, self.timer != nil {
                    let last = (said as String).split(separator: "\n").last.map(String.init) ?? "it stopped"
                    self.step = .failed("Tailscale Serve: \(last.prefix(60))")
                }
                self.check()
            }
        }
        do {
            try process.run()
            serving = process
        } catch {
            step = .failed("Tailscale Serve would not start")
        }
    }

    // Tailscale's latest installer from tailscale.com, opened once its signature checks out
    private func fetchInstaller() {
        step = .downloading
        guard let url = URL(string: "https://pkgs.tailscale.com/stable/Tailscale-latest-macos.pkg") else { return }
        download = URLSession.shared.downloadTask(with: url) { [weak self] file, _, _ in
            var saved: URL?
            if let file {
                let target = FileManager.default.temporaryDirectory.appendingPathComponent("Tailscale-macos.pkg")
                try? FileManager.default.removeItem(at: target)
                if (try? FileManager.default.moveItem(at: file, to: target)) != nil { saved = target }
            }
            let isGenuine = saved.map(isTailscaleInstaller) ?? false
            DispatchQueue.main.async {
                guard let self, self.timer != nil else { return }
                self.download = nil
                guard let saved, isGenuine else {
                    self.step = .failed("Tailscale's download stopped; click to try again")
                    return
                }
                self.installer = saved
                self.step = .installer
                NSWorkspace.shared.open(saved)
            }
        }
        download?.resume()
    }
}

// MARK: the pairing window

// a QR code drawn at whole pixels, so its squares stay sharp
func qrImage(_ text: String, side: CGFloat) -> NSImage? {
    guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
    filter.setValue(Data(text.utf8), forKey: "inputMessage")
    filter.setValue("M", forKey: "inputCorrectionLevel")
    guard let output = filter.outputImage else { return nil }
    let scale = max(1, (side * 2 / output.extent.width).rounded(.down))
    let scaled = output.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
    let rep = NSCIImageRep(ciImage: scaled)
    let image = NSImage(size: NSSize(width: side, height: side))
    image.addRepresentation(rep)
    return image
}

// Pair a phone: the relay's address with fresh pairing digits in its fragment as a QR code, the six digits in large
// type for typing (in two groups of three) with a copy button beside them and how long they still pair, the iPhone's
// steps, and the phones paired so far, each with Remove. The digits pair one phone within ten minutes while the window
// stays open; once a phone pairs, or the minutes run out, fresh digits take their place.
final class PairWindow: NSWindowController, NSWindowDelegate {
    private var relay: Relay?
    private var address = ""
    private var code = ""
    private let picture = NSImageView()
    private let codeLabel = NSTextField(labelWithString: "")
    private let copyButton = NSButton()
    private let validLabel = NSTextField(labelWithString: "")
    private let phonesBox = NSStackView()
    private let stack = NSStackView()
    private var timer: Timer?
    private var ticks = 0

    convenience init(address: String, relay: Relay) {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 520), styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "Pair a phone"
        window.isReleasedWhenClosed = false
        self.init(window: window)
        self.relay = relay
        self.address = address
        window.delegate = self

        picture.imageScaling = .scaleNone
        picture.setAccessibilityLabel("QR code")
        picture.translatesAutoresizingMaskIntoConstraints = false
        picture.widthAnchor.constraint(equalToConstant: 220).isActive = true
        picture.heightAnchor.constraint(equalToConstant: 220).isActive = true

        codeLabel.font = .monospacedSystemFont(ofSize: 26, weight: .semibold)
        codeLabel.isSelectable = true
        // the copy button: the digits alone to the clipboard, "Copied" in its place for a moment after; it hangs off the
        // code's right edge, so the code stays centred whichever it shows
        copyButton.image = NSImage(systemSymbolName: "doc.on.doc", accessibilityDescription: "Copy the code")
        copyButton.imagePosition = .imageOnly
        copyButton.isBordered = false
        copyButton.contentTintColor = .secondaryLabelColor
        copyButton.font = .systemFont(ofSize: 12)
        copyButton.target = self
        copyButton.action = #selector(copyCode)
        copyButton.translatesAutoresizingMaskIntoConstraints = false

        let steps = NSTextField(wrappingLabelWithString:
            "On the iPhone, install Tailscale from the App Store and sign in with the same account. Scan this code with "
            + "the Camera to open Uselim in Safari, then choose Share and Add to Home Screen. In the Uselim app, tap the "
            + "scan icon in the code field and point it here, or type the six digits.")
        steps.font = .systemFont(ofSize: 12)
        steps.preferredMaxLayoutWidth = 300

        let addressLabel = NSTextField(labelWithString: address)
        addressLabel.font = .systemFont(ofSize: 10)
        addressLabel.textColor = .secondaryLabelColor
        addressLabel.isSelectable = true

        validLabel.font = .systemFont(ofSize: 12)
        validLabel.textColor = .secondaryLabelColor
        phonesBox.orientation = .vertical
        phonesBox.alignment = .leading
        phonesBox.spacing = 8

        for view in [picture, codeLabel, validLabel, steps, phonesBox, addressLabel] { stack.addArrangedSubview(view) }
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 14
        stack.setCustomSpacing(6, after: codeLabel)
        stack.edgeInsets = NSEdgeInsets(top: 24, left: 30, bottom: 24, right: 30)
        stack.addSubview(copyButton)
        NSLayoutConstraint.activate([
            copyButton.leadingAnchor.constraint(equalTo: codeLabel.trailingAnchor, constant: 10),
            copyButton.centerYAnchor.constraint(equalTo: codeLabel.centerYAnchor),
        ])
        window.contentView = stack
        relay.onPhones = { [weak self] in self?.phonesChanged() }
        show(relay.openPairing())
        drawPhones()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.tick() }
        fit()
        window.center()
    }

    // fresh digits: the QR code, the digits in two groups of three, and the time they pair
    private func show(_ fresh: String) {
        code = fresh
        picture.image = qrImage("\(address)/#pair=\(fresh)", side: 220)
        codeLabel.stringValue = "\(fresh.prefix(3)) \(fresh.suffix(3))"
        tick()
    }

    // each second, the time the digits still pair; past it, fresh digits; every half minute, the list's last sights
    private func tick() {
        guard let relay else { return }
        let left = relay.pairSecondsLeft
        if left == 0 || relay.pairCode == nil { return show(relay.openPairing()) }
        validLabel.stringValue = "Pairs one phone · \(left / 60):\(String(format: "%02d", left % 60)) left"
        ticks += 1
        if ticks % 30 == 0 { drawPhones() }
    }

    // a phone paired, fresh digits for the next one; or one left the list
    private func phonesChanged() {
        if let relay, relay.pairCode == nil { show(relay.openPairing()) }
        drawPhones()
        fit()
    }

    // the phones paired so far, the section hidden while there are none
    private func drawPhones() {
        for view in phonesBox.arrangedSubviews { view.removeFromSuperview() }
        let phones = relay?.phones ?? []
        phonesBox.isHidden = phones.isEmpty
        guard !phones.isEmpty else { return }
        let line = NSBox()
        line.boxType = .separator
        line.translatesAutoresizingMaskIntoConstraints = false
        line.widthAnchor.constraint(equalToConstant: 300).isActive = true
        let head = NSTextField(labelWithString: "Paired phones")
        head.font = .systemFont(ofSize: 12, weight: .semibold)
        head.textColor = .secondaryLabelColor
        phonesBox.addArrangedSubview(line)
        phonesBox.setCustomSpacing(14, after: line)
        phonesBox.addArrangedSubview(head)
        for phone in phones { phonesBox.addArrangedSubview(row(phone)) }
    }

    // a phone's row: its name, when it paired and when it was last seen, and Remove
    private func row(_ phone: Relay.Phone) -> NSView {
        let symbol = phone.name.hasPrefix("iPad") ? "ipad" : phone.name.hasPrefix("Mac") ? "laptopcomputer" : "iphone"
        let icon = NSImageView(image: NSImage(systemSymbolName: symbol, accessibilityDescription: nil) ?? NSImage())
        icon.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 17, weight: .regular)
        let name = NSTextField(labelWithString: phone.name)
        name.font = .systemFont(ofSize: 13, weight: .semibold)
        let when = NSTextField(labelWithString: "Paired \(day(phone.paired)) · seen \(ago(phone.seen))")
        when.font = .systemFont(ofSize: 11)
        when.textColor = .secondaryLabelColor
        let words = NSStackView(views: [name, when])
        words.orientation = .vertical
        words.alignment = .leading
        words.spacing = 1
        words.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let remove = NSButton(title: "Remove", target: self, action: #selector(confirmRemove(_:)))
        remove.bezelStyle = .rounded
        remove.identifier = NSUserInterfaceItemIdentifier(phone.id)
        remove.setAccessibilityLabel("Remove \(phone.name)")
        // Remove at the row's right edge, whatever the words' width
        let line = NSStackView()
        line.addView(icon, in: .leading)
        line.addView(words, in: .leading)
        line.addView(remove, in: .trailing)
        line.orientation = .horizontal
        line.alignment = .centerY
        line.spacing = 10
        line.edgeInsets = NSEdgeInsets(top: 8, left: 10, bottom: 8, right: 10)
        line.translatesAutoresizingMaskIntoConstraints = false
        let card = Card()
        card.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(line)
        NSLayoutConstraint.activate([
            line.leadingAnchor.constraint(equalTo: card.leadingAnchor),
            line.trailingAnchor.constraint(equalTo: card.trailingAnchor),
            line.topAnchor.constraint(equalTo: card.topAnchor),
            line.bottomAnchor.constraint(equalTo: card.bottomAnchor),
            card.widthAnchor.constraint(equalToConstant: 300),
        ])
        return card
    }

    // Remove asks first, as a sheet on the window
    @objc private func confirmRemove(_ sender: NSButton) {
        guard let window, let id = sender.identifier?.rawValue,
              let phone = relay?.phones.first(where: { $0.id == id }) else { return }
        let alert = NSAlert()
        alert.messageText = "Remove \(phone.name)?"
        alert.informativeText = "It leaves Uselim and its notifications stop. Pairing it again takes a fresh code."
        alert.addButton(withTitle: "Remove").hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        alert.beginSheetModal(for: window) { [weak self] answer in
            if answer == .alertFirstButtonReturn { self?.relay?.removePhone(id) }
        }
    }

    // the window as tall as its content, its top edge where it was
    private func fit() {
        guard let window else { return }
        stack.layoutSubtreeIfNeeded()
        let frame = window.frameRect(forContentRect: NSRect(x: 0, y: 0, width: 360, height: stack.fittingSize.height))
        window.setFrame(NSRect(x: window.frame.minX, y: window.frame.maxY - frame.height, width: frame.width,
                               height: frame.height), display: true)
    }

    // closing: the digits on show pair nothing more
    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        timer = nil
        relay?.closePairing()
        relay?.onPhones = nil
    }

    // the code stays on this Mac, kept off Universal Clipboard and marked concealed for clipboard managers, and it
    // leaves the clipboard after a minute unless something else took its place
    @objc private func copyCode() {
        let board = NSPasteboard.general
        board.prepareForNewContents(with: .currentHostOnly)
        board.setString(code, forType: .string)
        board.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        let copied = board.changeCount
        DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
            if board.changeCount == copied { board.clearContents() }
        }
        copyButton.imagePosition = .noImage
        copyButton.title = "Copied"
        NSAccessibility.post(element: copyButton, notification: .announcementRequested,
                             userInfo: [.announcement: "Copied", .priority: NSAccessibilityPriorityLevel.high.rawValue])
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            self?.copyButton.title = ""
            self?.copyButton.imagePosition = .imageOnly
        }
    }
}

// a day as the list names it: today, yesterday, or the date ("13 Nov")
private func day(_ date: Date) -> String {
    let calendar = Calendar.current
    if calendar.isDateInToday(date) { return "today" }
    if calendar.isDateInYesterday(date) { return "yesterday" }
    let format = DateFormatter()
    format.dateFormat = "d MMM"
    return format.string(from: date)
}

// a last sight: just now within two minutes, then in minutes, then in hours through the day, then its day
private func ago(_ date: Date) -> String {
    let seconds = Date().timeIntervalSince(date)
    if seconds < 120 { return "just now" }
    if seconds < 3600 { return "\(Int(seconds / 60)) min ago" }
    if Calendar.current.isDateInToday(date) { return "\(Int(seconds / 3600)) h ago" }
    return day(date)
}

// a phone's row on its rounded card, in the control colour of the window's light or dark look
private final class Card: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.controlBackgroundColor.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 10, yRadius: 10).fill()
    }
}
