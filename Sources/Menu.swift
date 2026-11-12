import AppKit
import UserNotifications

// The status item, its menu and the minute's refresh. Figures arrive on a background queue and land on the main one.
// While the menu is open a 1 s timer keeps the countdowns moving; a refusal in the keychain's dialog pauses the reading
// until the note line's reload. The chats waiting on an answer sit at the top of the menu, with a dot on the bar.

enum Shown {
    case waiting, limits, signedOut, expired, denied, missing
    case failed(String)
    // the usage server's pause, until the time it ends
    case paused(Date)

    // the menu's line while figures are missing
    var words: String? {
        switch self {
        case .waiting: return "Reading the limits…"
        case .limits: return nil
        case .signedOut, .missing: return "Sign in to Claude Code to see the limits"
        case .expired: return "Open Claude Code to renew its login"
        case .denied: return "Allow Uselim in the keychain dialog to read the Claude Code login"
        case .failed(let why): return "Last read: \(why)"
        case .paused(let until): return "Anthropic paused readings until \(clock(until))"
        }
    }
}

// Culpmit, whose Touch Bar chip takes the Control Strip's slot while it runs (placeTouchBar)
let culpmitID = "com.simonbooban.culpmit"

final class Controller: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let menu = NSMenu()
    var limits: [Limit] = []
    var fetched: Date?
    var state = Shown.waiting
    var login: Login?
    var reading = false
    var denied = false
    var holdUntil = Date.distantPast
    // the reads' pacing: the usage server answers about twenty-five reads an hour, then refuses with 429 for a while,
    // so a read waits five minutes after the last one, whatever asks for it (a minute while zero figures show), and a
    // 429 pauses the reading fifteen minutes, doubling to an hour while refusals go on (longer where the server's
    // Retry-After names more); all of it carries over a restart (keepPacing)
    let readGap: TimeInterval = 300
    var lastReadAt = Date.distantPast
    var pause: TimeInterval = 0
    var poll: Timer?
    var tick: Timer?
    var appearanceWatch: NSKeyValueObservation?
    let alerts = Alerts()
    let strip = Strip()
    let relay = Relay()
    // the phone side: its switch, the live step under it, and the pairing window
    let phone = PhoneSetup()
    let phoneItem = NSMenuItem(title: "Phone", action: nil, keyEquivalent: "")
    let phoneSwitch = NSSwitch()
    let phoneLineItem = NSMenuItem(title: "", action: #selector(Controller.phoneStep), keyEquivalent: "")
    let pairItem = NSMenuItem(title: "Pair a phone…", action: #selector(Controller.openPair), keyEquivalent: "")
    var pairWindow: PairWindow?
    // Uselim's own updates: the row shows while GitHub holds a newer release
    let updater = Updater()
    let updateItem = NSMenuItem(title: "", action: #selector(Controller.runUpdate), keyEquivalent: "")
    var updateLine: String?
    var updateTimer: Timer?
    // "--demo 85,92,40" shows those shares in place of the real ones, for a look at the colours and notices
    let demo: [Int] = {
        let args = CommandLine.arguments
        guard let at = args.firstIndex(of: "--demo"), at + 1 < args.count else { return [] }
        return args[at + 1].split(separator: ",").compactMap { Int($0) }
    }()

    var waiting: [Waiting] = []
    var seen: [String: Kind] = [:]
    var watchers: [DispatchSourceFileSystemObject] = []
    var lastPush: Date?
    let waitHead = NSMenuItem(title: "Waiting on you", action: nil, keyEquivalent: "")
    let waitSep = NSMenuItem.separator()
    var waitItems: [NSMenuItem] = []
    var waitRows: [WaitRow] = []

    let accountItem = NSMenuItem()
    let noteItem = NSMenuItem()
    var rowItems: [NSMenuItem] = []
    var rows: [LimitRow] = []
    // the note line's text, and its reload at the right end, shown while a read can go again by hand
    let noteLabel = NSTextField(wrappingLabelWithString: "")
    let reloadButton = NSButton()
    // the menu's on and off rows, each a name with a switch beside it (switchRow); a row that carries a view fires zero
    // action of its own, so the switches answer
    let loginItem = NSMenuItem(title: "Open at login", action: nil, keyEquivalent: "")
    let loginSwitch = NSSwitch()
    let touchBarItem = NSMenuItem(title: "Touch Bar", action: nil, keyEquivalent: "")
    let touchBarSwitch = NSSwitch()
    // Culpmit's chip holding the Control Strip's slot, as last placed (placeTouchBar), and the watch on running apps
    var isYielding: Bool?
    var appsWatch: NSKeyValueObservation?
    // shown while macOS refuses Uselim's notices; it opens the Notifications pane with Uselim picked
    let noticesItem = NSMenuItem(title: "Turn on notifications…", action: #selector(Controller.openNotices), keyEquivalent: "")

    func applicationDidFinishLaunching(_ notification: Notification) {
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        item.button?.toolTip = "Claude usage limits"
        buildMenu()
        // the last copy's pacing, then its figures, which show at once, grey past ten minutes
        let defaults = UserDefaults.standard
        lastReadAt = Date(timeIntervalSince1970: defaults.double(forKey: "readAt"))
        holdUntil = Date(timeIntervalSince1970: defaults.double(forKey: "pausedUntil"))
        pause = defaults.double(forKey: "pause")
        if let saved = loadState() {
            limits = saved.limits
            fetched = saved.at
            lastReadAt = max(lastReadAt, saved.at)
            state = .limits
            alerts.seed(limits)
        } else if holdUntil > Date() {
            state = .paused(holdUntil)
        }
        placeTouchBar()
        // Culpmit opening or quitting moves the Control Strip's slot between its chip and Uselim's item; a menu bar app
        // sends zero launch or quit notices, so the watch is on the list of running apps itself
        appsWatch = NSWorkspace.shared.observe(\.runningApplications) { [weak self] workspace, _ in
            let isRunning = workspace.runningApplications.contains { $0.bundleIdentifier == culpmitID && !$0.isTerminated }
            DispatchQueue.main.async { self?.placeTouchBar(culpmitRunning: isRunning) }
        }
        draw()
        fillMenu()
        watch(waitingDir) { [weak self] in self?.reloadWaiting(notify: true) }
        watch(supportDir) { [weak self] in self?.takePushed() }
        reloadWaiting(notify: false)
        takePushed()
        refresh()
        // the item's slot in the bar arrives a moment after launch; the bar's light or dark ink is read from it
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.draw() }
        // a check each minute; the pacing in refresh decides whether it reads
        poll = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in self?.refresh() }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.refresh()
            self?.strip.reclaim()
        }
        appearanceWatch = NSApp.observe(\.effectiveAppearance) { [weak self] _, _ in self?.draw() }
        // the phone relay: a demo copy runs beside the installed one and leaves the port to it
        if demo.isEmpty {
            relay.limits = { [weak self] in (self?.limits ?? [], self?.fetched) }
            alerts.onLimit = { [weak relay] title, body in relay?.limitNotice(title, body) }
            phone.onChange = { [weak self] in self?.fillPhone() }
            updater.onChange = { [weak self] in self?.fillUpdate() }
            updater.check()
            updateTimer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
                self?.updater.check()
            }
            if isPhoneOn {
                relay.start()
                phone.start()
            }
        }
        note("started \(appVersion)")
    }

    // the Control Strip holds one third-party item: while Culpmit runs, its chip has the slot (Claude's figures with it
    // once its Claude limits switch is on), so Uselim's Touch Bar row hides and its own item stays off; with Culpmit
    // closed, the row and the saved choice come back
    func placeTouchBar(culpmitRunning: Bool? = nil) {
        let yields = culpmitRunning
            ?? NSRunningApplication.runningApplications(withBundleIdentifier: culpmitID).contains { !$0.isTerminated }
        // every app's launch and quit lands here; only Culpmit's own change moves anything
        guard yields != isYielding else { return }
        note(yields ? "touch bar: Culpmit runs, its chip has the slot" : "touch bar: Culpmit closed, Uselim's row back")
        isYielding = yields
        touchBarItem.isHidden = !strip.available || yields
        if yields {
            strip.hide()
        } else if UserDefaults.standard.bool(forKey: "touchBar") {
            strip.show()
        }
        draw()
    }

    // the Control Strip slot goes back on the way out; a copy that left without this kept the slot as a ghost
    func applicationWillTerminate(_ notification: Notification) {
        strip.hide()
    }

    // MARK: reading

    // a read, when the pacing allows it: past the gap since the last one, outside a 429's pause, and while the status
    // line's figures are older than five minutes (they cover the session and the week; the per-model lines wait a
    // quarter of an hour at most). The reload by hand goes at once.
    func refresh(byHand: Bool = false) {
        guard !reading, !denied, byHand || Date() >= holdUntil else { return }
        let sinceRead = Date().timeIntervalSince(lastReadAt)
        let gap = limits.isEmpty ? 60 : readGap
        let isPushFresh = lastPush.map { Date().timeIntervalSince($0) < readGap } ?? false
        if !byHand, sinceRead < gap || (isPushFresh && sinceRead < 900) { return }
        // a demo copy makes its own figures and leaves the network alone
        if !demo.isEmpty {
            take(.limits(demoLimits()), login: Login(token: "", expires: nil, plan: nil))
            return
        }
        lastReadAt = Date()
        keepPacing()
        reading = true
        DispatchQueue.global(qos: .utility).async { [self] in
            let found = readLogin()
            DispatchQueue.main.async { self.took(found) }
        }
    }

    private func took(_ found: LoginState) {
        switch found {
        case .found(let l):
            login = l
            fetchUsage(l) { result in
                DispatchQueue.main.async {
                    self.reading = false
                    self.take(result, login: l)
                }
            }
            return
        case .missing: show(.missing)
        case .denied:
            denied = true
            show(.denied)
        case .unreadable(let code): show(.failed("keychain error \(code)"))
        }
        reading = false
    }

    private func take(_ result: Fetch, login: Login) {
        switch result {
        case .limits(let found):
            limits = found.enumerated().map { i, l in
                demo.indices.contains(i) ? Limit(kind: l.kind, name: l.name, percent: demo[i], severity: l.severity, resets: l.resets) : l
            }
            fetched = Date()
            pause = 0
            keepPacing()
            if demo.isEmpty { saveState(limits, at: fetched!) }
            // a demo copy keeps its notices to itself unless asked with --notices
            if demo.isEmpty || CommandLine.arguments.contains("--notices") { alerts.check(limits) }
            show(.limits)
        case .signedOut:
            // an expired token waits for Claude Code to renew it; a refused one means signed out
            let expired = login.expires.map { $0 < Date() } ?? false
            show(expired ? .expired : .signedOut)
        case .busy(let asked):
            // the server asks for a pause: fifteen minutes, doubling to an hour while refusals go on, longer where its
            // Retry-After names more (it has answered "retry-after: 0" too); the figures and the status line's pushes
            // carry on meanwhile
            pause = min(max(pause * 2, 900), 3600)
            holdUntil = Date().addingTimeInterval(min(max(asked ?? 0, pause), 3600))
            keepPacing()
            note("read failed: HTTP 429, paused until \(clock(holdUntil))")
            show(.paused(holdUntil))
        case .failed(let why):
            // every failed read is logged, a repeat included
            note("read failed: \(why)")
            show(.failed(why))
        }
    }

    // the pacing outlives the copy: a restart keeps the gap since the last read and any pause the server asked for
    func keepPacing() {
        guard demo.isEmpty else { return }
        let defaults = UserDefaults.standard
        defaults.set(lastReadAt.timeIntervalSince1970, forKey: "readAt")
        defaults.set(holdUntil.timeIntervalSince1970, forKey: "pausedUntil")
        defaults.set(pause, forKey: "pause")
    }

    // demo figures: the session resets in two hours, the week in nine
    private func demoLimits() -> [Limit] {
        let now = Date()
        let kinds = [("session", "Session (5 hr)", 2.0), ("weekly_all", "Weekly (7 days)", 9.0), ("weekly_scoped", "Fable (weekly)", 9.0)]
        return zip(kinds, demo).map { k, share in
            Limit(kind: k.0, name: k.1, percent: share, severity: "normal", resets: now.addingTimeInterval(k.2 * 3600))
        }
    }

    private func show(_ next: Shown) {
        if next.words != state.words { note("state: \(next.words ?? "limits")") }
        state = next
        draw()
        fillMenu()
    }

    // the reload: the line says so while the read runs, and the answer redraws it
    @objc func retry() {
        denied = false
        noteLabel.stringValue = Shown.waiting.words ?? ""
        reloadButton.isHidden = true
        fitNote()
        refresh(byHand: true)
    }

    // MARK: the files Claude Code's commands leave

    // a folder's entries coming and going; a file written atomically counts as an entry
    func watch(_ dir: String, _ changed: @escaping () -> Void) {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let fd = open(dir, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .delete, .rename], queue: .main)
        source.setEventHandler(handler: changed)
        source.setCancelHandler { close(fd) }
        source.resume()
        watchers.append(source)
    }

    // the status line's figures, when newer than the last ones taken: the session and the week replace their lines,
    // the per-model lines stay from the last read
    func takePushed() {
        guard let pushed = readPushed(), pushed.at > (lastPush ?? .distantPast), pushed.at > (fetched ?? .distantPast),
              Date().timeIntervalSince(pushed.at) < 600 else { return }
        if lastPush == nil { note("first push from the status line") }
        lastPush = pushed.at
        var merged = limits
        for p in pushed.limits {
            if let i = merged.firstIndex(where: { $0.kind == p.kind }) {
                merged[i] = Limit(kind: p.kind, name: p.name, percent: p.percent, severity: merged[i].severity, resets: p.resets)
            } else {
                merged.append(p)
            }
        }
        let rank = ["session": 0, "weekly_all": 1]
        limits = merged.sorted { (rank[$0.kind] ?? 2) < (rank[$1.kind] ?? 2) }
        fetched = pushed.at
        alerts.check(limits)
        show(.limits)
    }

    // MARK: the chats waiting on an answer

    func reloadWaiting(notify: Bool) {
        waiting = readWaiting()
        if notify {
            for w in waiting where seen[w.session] != w.kind {
                alerts.send("\(w.project) \(w.kind == .question ? "asks" : "needs a permission")", w.detail)
            }
        }
        seen = Dictionary(waiting.map { ($0.session, $0.kind) }, uniquingKeysWith: { a, _ in a })
        draw()
        fillWaiting()
    }

    func fillWaiting() {
        for m in [waitHead, waitSep] + waitItems where menu.index(of: m) >= 0 { menu.removeItem(m) }
        waitRows = waiting.map { _ in WaitRow(width: 340) }
        waitItems = waitRows.map { row in
            let m = NSMenuItem()
            m.view = row
            return m
        }
        guard !waiting.isEmpty else { return }
        waitHead.isEnabled = false
        var at = 0
        for m in [waitHead] + waitItems + [waitSep] {
            menu.insertItem(m, at: at)
            at += 1
        }
        let now = Date()
        for (row, w) in zip(waitRows, waiting) { row.set(w, now: now) }
    }

    // MARK: the bar

    // figures older than ten minutes, or a login that is out, show in grey; one failed read keeps fresh figures as they are
    var stale: Bool {
        switch state {
        case .limits, .failed, .paused: break
        default: return true
        }
        guard let fetched else { return true }
        return Date().timeIntervalSince(fetched) > 600
    }

    func draw() {
        let dark = item.button?.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        let dot = !waiting.isEmpty
        strip.update(limits: limits, waiting: waiting, stale: stale)
        guard !limits.isEmpty else {
            let word: String
            switch state {
            case .waiting, .limits, .failed, .paused: word = "…"
            default: word = "off"
            }
            item.button?.image = barImage(top: [Piece(text: word, tone: .normal)], bottom: [], stale: false, dark: dark, dot: dot)
            return
        }
        let lines = readoutLines(limits)
        item.button?.image = barImage(top: lines.top, bottom: lines.bottom, stale: stale, dark: dark, dot: dot)
    }

    // MARK: the menu

    func buildMenu() {
        accountItem.isEnabled = false
        noteItem.view = noteRow()
        loginItem.view = switchRow("Open at login", loginSwitch, #selector(loginSwitchChanged(_:)))
        touchBarItem.view = switchRow("Touch Bar", touchBarSwitch, #selector(touchBarSwitchChanged(_:)))
        phoneItem.view = switchRow("Phone", phoneSwitch, #selector(phoneSwitchChanged(_:)))
        menu.addItem(accountItem)
        menu.addItem(noteItem)
        menu.addItem(.separator())
        menu.addItem(loginItem)
        touchBarItem.isHidden = !strip.available
        menu.addItem(touchBarItem)
        noticesItem.target = self
        noticesItem.isHidden = true
        menu.addItem(noticesItem)
        menu.addItem(.separator())
        phoneLineItem.target = self
        phoneLineItem.indentationLevel = 1
        pairItem.target = self
        menu.addItem(phoneItem)
        menu.addItem(phoneLineItem)
        menu.addItem(pairItem)
        fillPhone()
        menu.addItem(.separator())
        updateItem.target = self
        menu.addItem(updateItem)
        fillUpdate()
        let remove = NSMenuItem(title: "Remove Uselim…", action: #selector(Controller.confirmRemove), keyEquivalent: "")
        remove.target = self
        menu.addItem(remove)
        let restart = NSMenuItem(title: "Restart", action: #selector(Controller.restart), keyEquivalent: "")
        restart.target = self
        menu.addItem(restart)
        let quit = NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quit.target = NSApp
        menu.addItem(quit)
    }

    // a fresh copy opens once this one has gone
    @objc func restart() {
        let opener = Process()
        opener.executableURL = URL(fileURLWithPath: "/bin/sh")
        opener.arguments = ["-c", "sleep 0.5; /usr/bin/open \"\(Bundle.main.bundlePath)\""]
        try? opener.run()
        note("restart")
        NSApp.terminate(nil)
    }

    func fillMenu() {
        // the account line names the plan alone ("Claude Team"), the address left out of every screenshot and screen share
        let plan = login?.plan.map { $0.prefix(1).uppercased() + $0.dropFirst() }
        accountItem.title = plan.map { "Claude \($0)" } ?? ""
        accountItem.isHidden = accountItem.title.isEmpty
        // a failed read stays quiet while the figures are fresh (the status line keeps them so); the line and its reload
        // show once they go stale, or while a login or the keychain stands in the way
        noteLabel.stringValue = state.words ?? ""
        let isQuietFailure: Bool = {
            switch state {
            case .failed, .paused: return !stale
            default: return false
            }
        }()
        noteItem.isHidden = state.words == nil || isQuietFailure
        reloadButton.isHidden = {
            switch state {
            case .limits, .waiting: return true
            default: return false
            }
        }()
        fitNote()
        if rows.count != limits.count {
            for r in rowItems { menu.removeItem(r) }
            rows = limits.map { _ in LimitRow(width: 300) }
            rowItems = rows.map { row in
                let m = NSMenuItem()
                m.view = row
                return m
            }
            let at = menu.index(of: noteItem) + 1
            for (i, m) in rowItems.enumerated() { menu.insertItem(m, at: at + i) }
        }
        tickMenu()
        loginSwitch.state = openAtLogin ? .on : .off
        touchBarSwitch.state = strip.isShown ? .on : .off
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            let isRefused = settings.authorizationStatus == .denied
            DispatchQueue.main.async { self?.noticesItem.isHidden = !isRefused }
        }
    }

    @objc func openNotices() {
        openNotificationSettings()
    }

    // MARK: updates

    // "Update to 1.0.1" while GitHub holds a newer release; during an update its live step, after a stop the line saying so
    func fillUpdate() {
        if let updateLine {
            updateItem.title = updateLine
            updateItem.isEnabled = !updater.isBusy
            updateItem.isHidden = false
        } else {
            updateItem.title = updater.newest.map { "Update to \($0)" } ?? ""
            updateItem.isEnabled = true
            updateItem.isHidden = updater.newest == nil
        }
    }

    @objc func runUpdate() {
        updater.update { [weak self] line in
            self?.updateLine = line
            self?.fillUpdate()
        }
    }

    // MARK: the phone side

    // the switch, the live step beneath it while Tailscale gets ready, and Pair a phone once it is
    func fillPhone() {
        phoneSwitch.state = isPhoneOn ? .on : .off
        let line = phone.line
        phoneLineItem.title = line ?? ""
        phoneLineItem.isHidden = !isPhoneOn || line == nil
        pairItem.isHidden = !isPhoneOn
        pairItem.isEnabled = phone.step == .ready
    }

    @objc func phoneSwitchChanged(_ sender: NSSwitch) {
        isPhoneOn = sender.state == .on
        if isPhoneOn {
            relay.start()
            phone.start()
        } else {
            phone.stop()
            relay.stop()
            pairWindow?.close()
        }
        fillPhone()
    }

    @objc func phoneStep() {
        phone.act()
    }

    @objc func openPair() {
        pairWindow?.close()
        pairWindow = PairWindow(address: "https://\(phone.dnsName)", code: relay.pairingCode)
        NSApp.activate(ignoringOtherApps: true)
        pairWindow?.showWindow(nil)
        pairWindow?.window?.makeKeyAndOrderFront(nil)
    }

    // one question, then every change Uselim made goes: its hooks and status line (a backup kept), its plugin, Open at
    // login and the app itself, to the Trash
    @objc func confirmRemove() {
        let alert = NSAlert()
        alert.messageText = "Remove Uselim?"
        alert.informativeText = "Uselim's hooks and status line leave Claude Code's settings, with a backup kept; its "
            + "Claude Code plugin and Open at login go, and the app moves to the Trash."
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn { removeFromMenu() }
    }

    @objc func touchBarSwitchChanged(_ sender: NSSwitch) {
        if sender.state == .on { strip.show() } else { strip.hide() }
        UserDefaults.standard.set(strip.isShown, forKey: "touchBar")
        sender.state = strip.isShown ? .on : .off
        draw()
    }

    // the moving parts: countdowns, the age of the figures, the age of each wait
    func tickMenu() {
        let now = Date()
        for (row, limit) in zip(rows, limits) { row.set(limit, now: now, stale: stale) }
        for (row, w) in zip(waitRows, waiting) { row.set(w, now: now) }
    }

    func menuWillOpen(_ menu: NSMenu) {
        reloadWaiting(notify: false)
        fillMenu()
        fillPhone()
        refresh()
        updater.check()
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            self?.tickMenu()
            self?.draw()
        }
        RunLoop.main.add(t, forMode: .common)
        tick = t
    }

    func menuDidClose(_ menu: NSMenu) {
        tick?.invalidate()
        tick = nil
    }

    @objc func loginSwitchChanged(_ sender: NSSwitch) {
        openAtLogin = sender.state == .on
        // Open at login takes only a copy in Applications: the switch shows what holds
        sender.state = openAtLogin ? .on : .off
    }

    // MARK: switch rows

    // the note line: what stands in the way of the figures, in the menu's grey, with a reload at the right end aligned
    // with the switches, which reads again at once
    func noteRow() -> NSView {
        let row = NSView(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        row.autoresizingMask = [.width]
        reloadButton.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: "Read again")
        reloadButton.imagePosition = .imageOnly
        reloadButton.isBordered = false
        reloadButton.contentTintColor = .secondaryLabelColor
        reloadButton.target = self
        reloadButton.action = #selector(retry)
        reloadButton.autoresizingMask = [.minXMargin]
        noteLabel.font = .menuFont(ofSize: 0)
        noteLabel.textColor = .secondaryLabelColor
        noteLabel.maximumNumberOfLines = 2
        noteLabel.cell?.truncatesLastVisibleLine = true
        noteLabel.autoresizingMask = [.width]
        row.addSubview(noteLabel)
        row.addSubview(reloadButton)
        return row
    }

    // the note's row at its text's height, one line or two, with the reload centred at the right end; its 16.5 pt margin
    // puts the glyph's edge on the switches' drawn edge (their 14 pt frames carry 2.5 pt of padding)
    func fitNote() {
        guard let row = noteItem.view else { return }
        let inset: CGFloat = 22, right: CGFloat = 16.5, icon: CGFloat = 16
        let width = row.frame.width - inset - right - icon - 8
        let fit = noteLabel.cell?.cellSize(forBounds: NSRect(x: 0, y: 0, width: width, height: 100)).height ?? 17
        let text = min(ceil(fit), 36)
        let height = max(24, text + 7)
        row.frame.size.height = height
        noteLabel.frame = NSRect(x: inset, y: ((height - text) / 2).rounded(), width: width, height: text)
        reloadButton.frame = NSRect(x: row.frame.width - right - icon, y: ((height - icon) / 2).rounded(), width: icon,
                                    height: icon)
    }

    // a row of the menu with its name on the left and a small switch on the right, Control Center's way, at the limit
    // rows' width and inset and in the menu's own type; VoiceOver reads the switch by the row's name
    func switchRow(_ title: String, _ toggle: NSSwitch, _ action: Selector) -> NSView {
        let width: CGFloat = 300, height: CGFloat = 34, inset: CGFloat = 22, right: CGFloat = 14
        let row = NSView(frame: NSRect(x: 0, y: 0, width: width, height: height))
        row.autoresizingMask = [.width]
        toggle.controlSize = .small
        toggle.target = self
        toggle.action = action
        toggle.setAccessibilityLabel(title)
        toggle.sizeToFit()
        toggle.frame.origin = NSPoint(x: width - right - toggle.frame.width,
                                      y: ((height - toggle.frame.height) / 2).rounded())
        toggle.autoresizingMask = [.minXMargin]
        let label = NSTextField(labelWithString: title)
        label.font = .menuFont(ofSize: 0)
        label.sizeToFit()
        label.frame = NSRect(x: inset, y: ((height - label.frame.height) / 2).rounded(),
                             width: toggle.frame.minX - inset - 8, height: label.frame.height)
        label.autoresizingMask = [.width]
        row.addSubview(label)
        row.addSubview(toggle)
        return row
    }
}

// One chat waiting on an answer: project and what it waits for, its age, then its opening words and the matter
final class WaitRow: NSView {
    static let height: CGFloat = 40
    private let title = NSTextField(labelWithString: "")
    private let when = NSTextField(labelWithString: "")
    private let text = NSTextField(labelWithString: "")

    init(width: CGFloat) {
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: WaitRow.height))
        autoresizingMask = [.width]
        let small = NSFont.smallSystemFontSize
        title.lineBreakMode = .byTruncatingTail
        when.font = .monospacedDigitSystemFont(ofSize: small, weight: .regular)
        when.textColor = .secondaryLabelColor
        when.alignment = .right
        when.autoresizingMask = [.minXMargin]
        text.font = .systemFont(ofSize: small)
        text.textColor = .secondaryLabelColor
        text.lineBreakMode = .byTruncatingTail
        text.autoresizingMask = [.width]
        let x: CGFloat = 22, right: CGFloat = 14
        let inner = width - x - right
        title.frame = NSRect(x: x, y: 20, width: inner - 76, height: 17)
        when.frame = NSRect(x: width - right - 70, y: 21, width: 70, height: 15)
        text.frame = NSRect(x: x, y: 4, width: inner, height: 15)
        for v in [title, when, text] { addSubview(v) }
    }

    required init?(coder: NSCoder) { return nil }

    func set(_ w: Waiting, now: Date) {
        let size = NSFont.menuFont(ofSize: 0).pointSize
        let line = NSMutableAttributedString(string: w.project + " · ", attributes: [.font: NSFont.boldSystemFont(ofSize: size), .foregroundColor: NSColor.labelColor])
        line.append(NSAttributedString(string: w.kind.word, attributes: [.font: NSFont.menuFont(ofSize: size), .foregroundColor: NSColor.systemOrange]))
        title.attributedStringValue = line
        when.stringValue = ago(w.at, now: now)
        text.stringValue = [w.began, w.detail].filter { !$0.isEmpty }.joined(separator: " · ")
        toolTip = w.detail
    }
}

// One limit in the menu: its name and share, a bar, the reset and the time left
final class LimitRow: NSView {
    static let height: CGFloat = 54
    private let name = NSTextField(labelWithString: "")
    private let percent = NSTextField(labelWithString: "")
    private let bar = Bar()
    private let when = NSTextField(labelWithString: "")
    private let left = NSTextField(labelWithString: "")

    init(width: CGFloat) {
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: LimitRow.height))
        autoresizingMask = [.width]
        let size = NSFont.menuFont(ofSize: 0).pointSize
        let small = NSFont.smallSystemFontSize
        name.font = .menuFont(ofSize: size)
        percent.font = .monospacedDigitSystemFont(ofSize: size, weight: .medium)
        percent.alignment = .right
        percent.autoresizingMask = [.minXMargin]
        when.font = .systemFont(ofSize: small)
        when.textColor = .secondaryLabelColor
        left.font = .monospacedDigitSystemFont(ofSize: small, weight: .regular)
        left.textColor = .secondaryLabelColor
        left.alignment = .right
        left.autoresizingMask = [.minXMargin]
        bar.autoresizingMask = [.width]
        let x: CGFloat = 22, right: CGFloat = 14
        let inner = width - x - right
        name.frame = NSRect(x: x, y: 33, width: inner - 60, height: 17)
        percent.frame = NSRect(x: width - right - 60, y: 33, width: 60, height: 17)
        bar.frame = NSRect(x: x, y: 24, width: inner, height: 4)
        when.frame = NSRect(x: x, y: 6, width: inner - 70, height: 15)
        left.frame = NSRect(x: width - right - 70, y: 6, width: 70, height: 15)
        for v in [name, percent, bar, when, left] { addSubview(v) }
    }

    required init?(coder: NSCoder) { return nil }

    func set(_ limit: Limit, now: Date, stale: Bool) {
        name.stringValue = limit.name
        percent.stringValue = "\(limit.percent)%"
        let colour: NSColor
        switch (stale, tone(of: limit)) {
        case (true, _): colour = .secondaryLabelColor
        case (false, .normal): colour = .labelColor
        case (false, .warning): colour = .systemOrange
        case (false, .critical): colour = .systemRed
        }
        name.textColor = stale ? .secondaryLabelColor : .labelColor
        percent.textColor = colour
        bar.fraction = min(1, CGFloat(limit.percent) / 100)
        bar.colour = colour
        when.stringValue = limit.resets.map { resetText($0, now: now) } ?? ""
        left.stringValue = limit.resets.map { "in " + exact(until: $0, now: now) } ?? ""
    }
}

// A thin track with its filled share
final class Bar: NSView {
    var fraction: CGFloat = 0 { didSet { needsDisplay = true } }
    var colour: NSColor = .labelColor { didSet { needsDisplay = true } }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds.height / 2
        NSColor.labelColor.withAlphaComponent(0.1).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: r, yRadius: r).fill()
        guard fraction > 0 else { return }
        colour.setFill()
        let filled = NSRect(x: 0, y: 0, width: max(bounds.height, bounds.width * fraction), height: bounds.height)
        NSBezierPath(roundedRect: filled, xRadius: r, yRadius: r).fill()
    }


}
