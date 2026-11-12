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
    let name = (status?["Self"] as? [String: Any])?["DNSName"] as? String ?? ""
    let serve = runTool(tool, ["serve", "status", "--json"], timeout: 8).out
    return TailscaleStatus(
        state: status?["BackendState"] as? String ?? "",
        dnsName: name.hasSuffix(".") ? String(name.dropLast()) : name,
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

// Pair a phone: the relay's address with the pairing code in its fragment as a QR code, the six digits in large type
// for typing (in two groups of three) with a copy button beside them, and the iPhone's steps
final class PairWindow: NSWindowController {
    private var code = ""
    private let copyButton = NSButton()

    convenience init(address: String, code: String) {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 520), styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "Pair a phone"
        window.isReleasedWhenClosed = false
        self.init(window: window)
        self.code = code

        let picture = NSImageView()
        picture.image = qrImage("\(address)/#pair=\(code)", side: 220)
        picture.imageScaling = .scaleNone
        picture.translatesAutoresizingMaskIntoConstraints = false
        picture.widthAnchor.constraint(equalToConstant: 220).isActive = true
        picture.heightAnchor.constraint(equalToConstant: 220).isActive = true

        let shown = code.count == 6 ? "\(code.prefix(3)) \(code.suffix(3))" : code
        let codeLabel = NSTextField(labelWithString: shown)
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

        let stack = NSStackView(views: [picture, codeLabel, steps, addressLabel])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(top: 24, left: 30, bottom: 24, right: 30)
        stack.addSubview(copyButton)
        NSLayoutConstraint.activate([
            copyButton.leadingAnchor.constraint(equalTo: codeLabel.trailingAnchor, constant: 10),
            copyButton.centerYAnchor.constraint(equalTo: codeLabel.centerYAnchor),
        ])
        window.contentView = stack
        // the window as tall as its content, the address the last line
        stack.layoutSubtreeIfNeeded()
        window.setContentSize(NSSize(width: 360, height: stack.fittingSize.height))
        window.center()
    }

    @objc private func copyCode() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(code, forType: .string)
        copyButton.imagePosition = .noImage
        copyButton.title = "Copied"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            self?.copyButton.title = ""
            self?.copyButton.imagePosition = .imageOnly
        }
    }
}
