import AppKit
import CryptoKit
import IOKit

// The phone relay: Uselim's side of the Uselim mod that runs in every Claude Code chat. Both share
// ~/Library/Application Support/Uselim/relay, laid out at the top of the mod's hooks/register.ts: the mod keeps
// chats/, history/, replies/ and asks/ current and reads inbox/ and away.json; Uselim writes inbox/ (messages and
// the phone's Stop), files/ (what the phone sends along) and away.json, serves the phone's web app with the chats'
// state, holds each ask open for the mod until the phone answers it or stops the turn, logs every reply, ask and limit
// notice for the phone's Notifications screen, and pushes each one the phone's switches let through.
//
// The server listens on 127.0.0.1:47472 alone; Tailscale Serve brings it to the phone over the tailnet, with HTTPS.
// Every /api and /relay request carries the X-Uselim header, which a page on another site can add only through a
// CORS preflight the server turns away, and names a Host the server knows, which turns away DNS rebinding. Every one
// carries a key as well: the phone's pairing code, or from the Mac itself relay/local.key, which this user alone can
// read; the rest of the Mac (an app in its sandbox, a browser extension) reaches the page's own files alone.

#if RELAY_TEST
// a test build (Tools/test-relay.sh) keeps to a folder and a port of its own, the installed app's left as they are
let relayDir = ProcessInfo.processInfo.environment["USELIM_TEST_RELAY"] ?? NSTemporaryDirectory() + "uselim-relay-test"
let relayPort = UInt16(ProcessInfo.processInfo.environment["USELIM_TEST_PORT"] ?? "") ?? 47_572
#else
let relayDir = NSHomeDirectory() + "/Library/Application Support/Uselim/relay"
let relayPort: UInt16 = 47472
#endif

// the key the Mac's own callers send, the mod's waits and --permission: 32 random bytes in hex, made by the relay
func localRelayKey() -> String? {
    let text = ((try? String(contentsOfFile: relayDir + "/local.key", encoding: .utf8)) ?? "")
        .trimmingCharacters(in: .whitespacesAndNewlines)
    return text.count == 64 && text.allSatisfy(\.isHexDigit) ? text : nil
}

// the Mac reads as away while locked, after two idle minutes, or while the phone acted after the Mac's last input
private let idleAway: TimeInterval = 120
// a chat whose mod has been silent past this has closed
private let aliveWindow = 90_000.0

final class Relay {
    var limits: () -> (limits: [Limit], at: Date?) = { ([], nil) }
    private var server: Server?
    private lazy var push = Push()
    private var away = false
    private var awayWritten = Date.distantPast
    private var phoneActive = Date.distantPast
    // a held ask's waits; a question's (`always`) holds at the desk too, beside its dialog
    private var waiters: [String: [(id: Int, always: Bool, reply: (Response) -> Void)]] = [:]
    private var answers: [String: (answer: [String: Any], at: Date)] = [:]
    // the phone's Stop by chat: an ask held from before it lets go
    private var stops: [String: Date] = [:]
    private var nextWaiter = 0
    private var sent = 0
    private var replySeen: [String: Double] = [:]
    private var askSeen: Set<String> = []
    private var swept = Date.distantPast
    private var timer: Timer?
    // each chat's title as VS Code shows it, read from its transcript every ten seconds
    private var titles: [String: String] = [:]
    private var titled = Date.distantPast
    // a name given from the phone, standing until the transcript carries it (an open chat renames between turns)
    private var pendingTitles: [String: (title: String, at: Date)] = [:]
    // the chats hidden from the phone's lists, a row's Delete on a swipe, as VS Code's Archive, the history kept
    // (relay/hidden.json, when each was hidden); an open chat returns with a reply or an ask after that
    private var hidden: [String: Double] = [:]
    // the transcripts by chat, touched on transcriptQueue alone
    private var transcripts: [String: Transcript] = [:]
    // the chats of the last two weeks, open or closed, read every minute; a closed one the phone writes to opens in
    // VS Code, once a minute at most
    private var past: [PastChat] = []
    private var pastRead = Date.distantPast
    private var woken: [String: Date] = [:]
    // a live chat's phone message waiting on its tab: when Uselim first saw the chat idle, and the message it opened the
    // tab for
    private var idleSince: [String: Date] = [:]
    private var revealed: [String: String] = [:]
    // the phone's New chat: when it last opened a fresh tab, the chats there before that tab, the fresh chats already
    // given their first message, and whether that tab had yet to register when the phone stopped waiting
    private var freshAsked = Date.distantPast
    private var freshBefore: Set<String> = []
    private var freshTaken: Set<String> = []
    private var isFreshLate = false
    // which notices go to the phone, the switches in the phone's Notification Center screen (relay/settings.json), at
    // any hour; `main` off stops every push
    private var notify: [String: Bool] = ["main": true, "replies": true, "questions": true, "permissions": true,
                                          "limits": false]
    // every notice, whatever the switches say, for the phone's Notifications screen (relay/notices.json): two weeks,
    // newest last, each unread until opened on the phone (a tap on it, or its chat)
    private var notices: [[String: Any]] = []
    private var noticesAt = 0.0
    private var noticeCount = 0
    // the chat open on the phone's screen, which asks for it every 3 s while the app shows: its replies and asks stay
    // off the lock screen and log as read; another screen of the app ends it at once
    private var viewing: (session: String, at: Date)?

    func start() {
        guard server == nil else { return }
        for folder in ["chats", "history", "replies", "inbox", "asks", "files", "images"] {
            try? FileManager.default.createDirectory(atPath: relayDir + "/" + folder, withIntermediateDirectories: true)
        }
        if let saved = readJSON(relayDir + "/settings.json")?["notify"] as? [String: Bool] {
            for (kind, isOn) in saved where notify[kind] != nil { notify[kind] = isOn }
        }
        hidden = readJSON(relayDir + "/hidden.json")?["hidden"] as? [String: Double] ?? [:]
        let savedNotices = readJSON(relayDir + "/notices.json")
        notices = savedNotices?["notices"] as? [[String: Any]] ?? []
        noticesAt = savedNotices?["at"] as? Double ?? 0
        localKey = makeLocalKey()
        pairingCode = readPairingCode()
        readGuard()
        // a subscription from before the codes' tags joins the current code; one another code stands behind leaves
        var tags: Set<String> = [pairingTag(pairingCode)]
        if let previousCode { tags.insert(pairingTag(previousCode)) }
        push.keep(tags: tags, adopting: pairingTag(pairingCode))
        // what is on disk at the start counts as seen, so a restart sends only what arrives after it
        scan(sending: false)
        server = Server(port: relayPort) { [weak self] request, reply in
            self?.route(request, reply)
        }
        server?.start()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.tick() }
        tick()
    }

    // the Phone switch off: the port closes, the waits held for the mod let go, and the chats' asks stay with VS Code
    func stop() {
        timer?.invalidate()
        timer = nil
        releaseAll(["state": "gone"])
        server?.stop()
        server = nil
    }

    // MARK: pairing

    // the code a phone pairs with, kept in relay/pairing.json and made once: six digits from the system's secure random
    // source. A ten-character code from before the six digits stays as "previous" and keeps its phones working until a
    // phone pairs with the new one.
    private(set) var pairingCode = ""
    private var previousCode: String?
    // wrong codes per device (the phone's tailnet address, as Tailscale Serve forwards it): the distinct codes sent, so a
    // phone repeating its one stale code counts once; from the fifth, the device pauses a minute, doubling to an hour,
    // and every code from it waits out the pause, the right one included. Tailscale stays the outer gate. A right code
    // leaves the count as it stands, so the phone's own polls hand a guesser under its address zero fresh tries; the
    // count lapses a day after its last miss. A paired device's count lives in relay/guard.json too, so a restart
    // keeps its pause; each code is kept as its tag.
    private var misses: [String: (codes: Set<String>, until: Date, last: Date)] = [:]

    private func readPairingCode() -> String {
        let path = relayDir + "/pairing.json"
        let saved = readJSON(path)
        if let code = saved?["code"] as? String, code.count == 6, code.allSatisfy(\.isNumber) {
            previousCode = saved?["previous"] as? String
            return code
        }
        let code = String(format: "%06d", Int.random(in: 0..<1_000_000))
        previousCode = saved?["code"] as? String
        savePairing(code)
        return code
    }

    private func savePairing(_ code: String) {
        let path = relayDir + "/pairing.json"
        var record: [String: Any] = ["code": code]
        if let previousCode { record["previous"] = previousCode }
        writeJSON(record, to: path)
        chmod(path, 0o600)
    }

    private enum Pairing { case current, previous, wrong, blank }

    // a code as typed: digits alone for the six-digit code; the previous code in any case, its dash and spaces left out
    private func pairing(_ given: String?) -> Pairing {
        guard let given, !given.isEmpty else { return .blank }
        if !pairingCode.isEmpty, given.filter(\.isNumber) == pairingCode, given.filter(\.isLetter).isEmpty { return .current }
        let clean = { (text: String) in text.uppercased().filter { $0.isLetter || $0.isNumber } }
        if let previousCode, clean(given) == clean(previousCode) { return .previous }
        return .wrong
    }

    // a request from the phone through Tailscale Serve, which forwards with the tailnet's own name and the phone's address
    private func isRemote(_ request: Request) -> Bool {
        request.headers["x-forwarded-for"] != nil || request.headers["host"]?.lowercased().contains(".ts.net") == true
    }

    // the device behind a request: the last address in X-Forwarded-For, the one Tailscale Serve adds itself
    private func device(_ request: Request) -> String {
        let forwarded = request.headers["x-forwarded-for"] ?? ""
        let last = forwarded.split(separator: ",").last.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
        return last.isEmpty ? "unknown" : last
    }

    // the seconds a device still waits, while it does; a day after its last miss, its count starts over
    private func pausedFor(_ device: String) -> Int? {
        guard let entry = misses[device] else { return nil }
        if Date().timeIntervalSince(entry.last) > 86_400 {
            misses[device] = nil
            return nil
        }
        let left = entry.until.timeIntervalSinceNow
        return left > 0 ? Int(left.rounded(.up)) : nil
    }

    private func missed(_ device: String, _ given: String) {
        var entry = misses[device] ?? (codes: [], until: .distantPast, last: Date())
        entry.codes.insert(pairingTag(given.uppercased().filter { $0.isLetter || $0.isNumber }))
        entry.last = Date()
        if entry.codes.count >= 5 {
            let pause = min(60 * pow(2, Double(entry.codes.count - 5)), 3600)
            entry.until = Date().addingTimeInterval(pause)
            note("relay: \(entry.codes.count) wrong codes from \(device), paused \(Int(pause)) s")
        }
        misses[device] = entry
        if known[device] != nil { saveGuard() }
    }

    // MARK: the local key and the guard across devices

    private var localKey = ""
    private var isOlderModNoted = false

    private func makeLocalKey() -> String {
        if let key = localRelayKey() { return key }
        let key = SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }.map { String(format: "%02x", $0) }.joined()
        let path = relayDir + "/local.key"
        FileManager.default.createFile(atPath: path, contents: Data(key.utf8), attributes: [.posixPermissions: 0o600])
        chmod(path, 0o600)
        note("relay: made the local key")
        return key
    }

    // the same length and every byte compared, so the time taken stays the same wherever a wrong key differs
    private func isLocalKey(_ given: String?) -> Bool {
        guard let given, !localKey.isEmpty, given.utf8.count == localKey.utf8.count else { return false }
        return zip(given.utf8, localKey.utf8).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
    }

    // a pairing code's mark on the subscriptions it brought, keyed with the local key, so push.json alone keeps the
    // code secret
    private func pairingTag(_ code: String) -> String {
        let mac = HMAC<SHA256>.authenticationCode(for: Data(code.utf8), using: SymmetricKey(data: Data(localKey.utf8)))
        return mac.prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    // X-Forwarded-For names the device, and a program on the Mac can name any address in it, so the per-device count
    // alone would let it try codes endlessly. The codes sent from devices yet to pair count together: from
    // the tenth distinct one in a day, every such device pauses a minute, doubling to an hour, while a device that
    // paired keeps its own count and goes on working. Both live in relay/guard.json, across restarts and updates.
    private var known: [String: Double] = [:]
    private var strangers: (codes: Set<String>, until: Date, last: Date) = ([], .distantPast, .distantPast)

    private func readGuard() {
        let saved = readJSON(relayDir + "/guard.json")
        known = saved?["known"] as? [String: Double] ?? [:]
        let count = { (record: [String: Any]) in
            (codes: Set(record["codes"] as? [String] ?? []),
             until: Date(timeIntervalSince1970: (record["until"] as? Double ?? 0) / 1000),
             last: Date(timeIntervalSince1970: (record["last"] as? Double ?? 0) / 1000))
        }
        strangers = count(saved?["strangers"] as? [String: Any] ?? [:])
        for (device, record) in saved?["misses"] as? [String: [String: Any]] ?? [:] where known[device] != nil {
            misses[device] = count(record)
        }
    }

    // the stranger count, and the paired devices' own counts beside it
    private func saveGuard() {
        let path = relayDir + "/guard.json"
        let record = { (entry: (codes: Set<String>, until: Date, last: Date)) -> [String: Any] in
            ["codes": Array(entry.codes), "until": millis(entry.until), "last": millis(entry.last)]
        }
        let paired = misses.filter { known[$0.key] != nil }.mapValues(record)
        writeJSON(["known": known, "strangers": record(strangers), "misses": paired], to: path)
        chmod(path, 0o600)
    }

    // a device that sent a right code; a request naming zero addresses ("unknown") stays a stranger
    private func know(_ device: String) {
        guard device != "unknown", known[device] == nil else { return }
        known[device] = millis(Date())
        saveGuard()
        note("relay: \(device) paired")
    }

    // the seconds a device yet to pair still waits, while the pause across them lasts
    private func strangerPause(_ device: String) -> Int? {
        known[device] == nil ? strangersWait() : nil
    }

    // the seconds left in the pause across unpaired devices; a day after the last of their misses, the count starts over
    private func strangersWait() -> Int? {
        if !strangers.codes.isEmpty, Date().timeIntervalSince(strangers.last) > 86_400 {
            strangers = ([], .distantPast, .distantPast)
            saveGuard()
            return nil
        }
        let left = strangers.until.timeIntervalSinceNow
        return left > 0 ? Int(left.rounded(.up)) : nil
    }

    // a wrong code from a device yet to pair, kept as its tag: a near miss stays off the disk
    private func strangerMissed(_ given: String) {
        strangers.codes.insert(pairingTag(given.uppercased().filter { $0.isLetter || $0.isNumber }))
        strangers.last = Date()
        if strangers.codes.count >= 10 {
            let pause = min(60 * pow(2, Double(strangers.codes.count - 10)), 3600)
            strangers.until = Date().addingTimeInterval(pause)
            note("relay: \(strangers.codes.count) wrong codes from unpaired devices, all paused \(Int(pause)) s")
        }
        saveGuard()
    }

    // an older mod's wait, sent before the local key: it passes for the ask held that moment, whose id only a reader of
    // relay/asks knows
    private func isLiveAsk(_ request: Request) -> Bool {
        guard let session = request.query["session"], isSessionID(session), let ask = request.query["ask"], !ask.isEmpty,
              let held = readJSON(relayDir + "/asks/\(session).json")?["id"] as? String else { return false }
        return held == ask
    }

    // MARK: the Mac, every two seconds

    private func tick() {
        let now = readAway()
        if now != away || Date().timeIntervalSince(awayWritten) > 30 {
            if now != away { note(now ? "relay: away from the Mac" : "relay: back at the Mac") }
            away = now
            writeJSON(["away": away, "at": millis(Date())], to: relayDir + "/away.json")
            awayWritten = Date()
            if !away { releaseAll(["state": "desk"]) }
        }
        answers = answers.filter { Date().timeIntervalSince($0.value.at) < 600 }
        stops = stops.filter { Date().timeIntervalSince($0.value) < 60 }
        scan(sending: true)
        settleCards()
        revealWaiting()
        if Date().timeIntervalSince(swept) > 600 { sweep() }
        if Date().timeIntervalSince(titled) > 10 { readTitles() }
        if Date().timeIntervalSince(pastRead) > 60 { readPast() }
    }

    private func readPast() {
        pastRead = Date()
        transcriptQueue.async { [weak self] in
            let found = pastChats()
            DispatchQueue.main.async { self?.past = found }
        }
    }

    private func pastChat(_ session: String) -> PastChat? { past.first { $0.session == session } }

    // a chat the phone may write to: open in VS Code now, or one of the last two weeks'
    private func isKnown(_ session: String) -> Bool {
        FileManager.default.fileExists(atPath: relayDir + "/chats/\(session).json") || pastChat(session) != nil
    }

    private func cwdOf(_ session: String) -> String? {
        readJSON(relayDir + "/chats/\(session).json")?["cwd"] as? String ?? pastChat(session)?.cwd
    }

    private func isLive(_ session: String) -> Bool {
        liveChats().contains { $0["session"] as? String == session }
    }

    // a closed chat opens in VS Code through the extension's own link, and its mod then takes the message waiting for it
    private func wake(_ session: String) {
        guard !isLive(session), Date().timeIntervalSince(woken[session] ?? .distantPast) > 60,
              let url = URL(string: "vscode://anthropic.claude-code/open?session=\(session)") else { return }
        woken[session] = Date()
        NSWorkspace.shared.open(url)
        note("relay: a phone message opened \(pastChat(session)?.title ?? session) in VS Code")
    }

    // the phone's New chat: a fresh tab through the extension's own link, in VS Code's first folder (the message names
    // the project), VS Code left where it sits. The first chat to register
    // after the link with zero turns is the new one, and the phone sends its words there the usual way. After 30 s
    // with zero new chats the phone keeps its message; a tab that registers later takes the next New chat within ten
    // minutes, in place of a second tab.
    private func openFresh(_ reply: @escaping (Response) -> Void) {
        phoneActive = Date()
        if isFreshLate, Date().timeIntervalSince(freshAsked) < 600, let session = freshChat() {
            isFreshLate = false
            freshTaken.insert(session)
            note("relay: the phone's new chat took the tab that registered late")
            return reply(.json(["ok": true, "session": session]))
        }
        guard let url = URL(string: "vscode://anthropic.claude-code/open") else {
            return reply(.text("Link refused", status: 500))
        }
        freshAsked = Date()
        freshBefore = Set(sessions(in: "chats"))
        isFreshLate = false
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = false
        NSWorkspace.shared.open(url, configuration: configuration)
        note("relay: the phone opened a new chat in VS Code")
        let started = Date()
        func look() {
            if let session = freshChat() {
                freshTaken.insert(session)
                note("relay: the new chat registered after \(Int(Date().timeIntervalSince(started).rounded())) s")
                return reply(.json(["ok": true, "session": session]))
            }
            guard Date().timeIntervalSince(started) < 30 else {
                isFreshLate = true
                note("relay: the new chat had yet to register after 30 s; the phone keeps its message")
                return reply(.json(["ok": false, "state": "opening"]))
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { look() }
        }
        look()
    }

    // a live chat that registered after the last fresh tab, with zero turns and still without a phone message
    private func freshChat() -> String? {
        let chat = liveChats().first { chat in
            guard let session = chat["session"] as? String else { return false }
            return !freshBefore.contains(session) && !freshTaken.contains(session)
                && (chat["began"] as? String ?? "").isEmpty
        }
        return chat?["session"] as? String
    }

    // a chat can take a phone message (its ack) and start zero turns: Claude Code holds the prompt until the chat's tab
    // shows in VS Code (a message queued at one moment ran two minutes later, as its tab opened). After six idle seconds
    // Uselim opens that tab through the extension's own link, VS Code left where it sits, once per message and only
    // within ten minutes of the message's sending or of the turn it queued behind
    private func revealWaiting() {
        let now = Date()
        for chat in liveChats() {
            guard let session = chat["session"] as? String else { continue }
            guard chat["status"] as? String == "idle" else {
                idleSince[session] = nil
                continue
            }
            let since = idleSince[session] ?? now
            idleSince[session] = since
            let ack = readText(relayDir + "/inbox/\(session).ack")
            guard now.timeIntervalSince(since) >= 6, !ack.isEmpty else { continue }
            let messages = inbox(session)
            let historyAt = readJSON(relayDir + "/history/\(session).json")?["at"] as? Double ?? 0
            // a message queued behind a running turn waits from that turn's end, the history's last write (one can enter
            // twenty minutes after its sending, minutes after the turn ahead of it ended)
            let taken = messages.filter { message in
                let age = millis(now) - max(message["at"] as? Double ?? 0, historyAt)
                return (message["id"] as? String ?? "") <= ack && age >= 6_000 && age < 600_000
            }
            guard let newest = taken.last?["id"] as? String, revealed[session] != newest else { continue }
            let settled = settledID(session, messages) { historyAt }
            guard newest > settled,
                  let url = URL(string: "vscode://anthropic.claude-code/open?session=\(session)") else { continue }
            revealed[session] = newest
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = false
            NSWorkspace.shared.open(url, configuration: configuration)
            note("relay: opened \(name(session, chat)) in VS Code for the phone message waiting on it")
        }
    }

    // permission cards and their waits: a card on a chat gone idle outlived its turn (denied at the Mac), and a wait
    // whose card went (the Mac answered, the tool ran) lets go, so the --permission behind it leaves
    private func settleCards() {
        let now = millis(Date())
        for chat in liveChats() where chat["status"] as? String == "idle" {
            guard let session = chat["session"] as? String else { continue }
            let path = relayDir + "/asks/\(session).json"
            guard let card = readJSON(path), card["kind"] as? String == "permission",
                  now - (card["at"] as? Double ?? now) > 5_000 else { continue }
            FileManager.default.createFile(atPath: path, contents: Data())
            note("relay: the permission card on \(name(session, chat)) came down with its turn")
        }
        for key in Array(waiters.keys) {
            let parts = key.split(separator: "/", maxSplits: 1).map(String.init)
            guard parts.count == 2,
                  readJSON(relayDir + "/asks/\(parts[0]).json")?["id"] as? String != parts[1] else { continue }
            for waiter in waiters.removeValue(forKey: key) ?? [] { waiter.reply(.json(["state": "gone"])) }
        }
    }

    // the last phone message the chat's history holds: the mod's mark, else (a mod from before the mark) the newest one
    // sent before the history's last write
    private func settledID(_ session: String, _ messages: [[String: Any]], historyAt: () -> Double) -> String {
        let entered = readText(relayDir + "/inbox/\(session).entered")
        if !entered.isEmpty { return entered }
        let at = historyAt()
        return messages.filter { ($0["at"] as? Double ?? 0) <= at }.compactMap { $0["id"] as? String }.max() ?? ""
    }

    // a closed chat as the phone reads it: its name and folder, its history from the transcript page by page
    private func closedChatJSON(_ session: String) -> [String: Any]? {
        guard let found = pastChat(session) else { return nil }
        let project = found.cwd.split(separator: "/").last.map(String.init) ?? found.cwd
        let messages = inbox(session)
        let ack = readText(relayDir + "/inbox/\(session).ack")
        return ["session": session, "cwd": found.cwd, "project": project, "title": found.title, "began": "",
                "status": "closed", "tools": [], "at": found.at, "history": [], "historyAt": 0, "settled": "",
                "outbox": messages.map { message -> [String: Any] in
                    var row = message
                    row["delivered"] = (message["id"] as? String ?? "") <= ack
                    return row
                },
                "now": millis(Date())]
    }

    // the transcript of a chat, indexed up to now; on transcriptQueue
    private func transcript(_ session: String, cwd: String) -> Transcript {
        if let held = transcripts[session] {
            held.refresh()
            return held
        }
        var path = transcriptPath(cwd: cwd, session: session)
        // a chat registered under a folder a shell's cd took it to keeps its transcript under the one it started in:
        // found by its id, which names one file across the projects
        if !FileManager.default.fileExists(atPath: path) {
            let projects = NSHomeDirectory() + "/.claude/projects"
            for folder in (try? FileManager.default.contentsOfDirectory(atPath: projects)) ?? [] {
                let candidate = "\(projects)/\(folder)/\(session).jsonl"
                if FileManager.default.fileExists(atPath: candidate) {
                    path = candidate
                    break
                }
            }
        }
        let found = Transcript(path: path)
        transcripts[session] = found
        found.refresh()
        return found
    }

    private func readTitles() {
        titled = Date()
        let live = liveChats().compactMap { chat -> (String, String)? in
            guard let session = chat["session"] as? String, let cwd = chat["cwd"] as? String else { return nil }
            return (session, cwd)
        }
        transcriptQueue.async { [weak self] in
            guard let self else { return }
            let found = live.map { ($0.0, self.transcript($0.0, cwd: $0.1).title) }
            DispatchQueue.main.async {
                for (session, title) in found where !title.isEmpty {
                    // a name from the phone holds for ten minutes until the transcript carries it
                    if let wanted = self.pendingTitles[session] {
                        if wanted.title != title, Date().timeIntervalSince(wanted.at) < 600 { continue }
                        self.pendingTitles[session] = nil
                    }
                    self.titles[session] = title
                }
            }
        }
    }

    private func readAway() -> Bool {
        let session = CGSessionCopyCurrentDictionary() as? [String: Any]
        let locked = session?["CGSSessionScreenIsLocked"] as? Bool ?? false
        let idle = idleSeconds()
        let lastInput = Date().addingTimeInterval(-idle)
        return locked || idle >= idleAway || phoneActive > lastInput
    }

    // every reply and every ask, each once: logged for the Notifications screen, pushed as the phone's switches say
    private func scan(sending: Bool) {
        let chats = Dictionary(readChats().map { ($0["session"] as? String ?? "", $0) }, uniquingKeysWith: { a, _ in a })
        for session in sessions(in: "replies") {
            guard let reply = readJSON(relayDir + "/replies/\(session).json"), let at = reply["at"] as? Double else { continue }
            if let seen = replySeen[session], seen >= at { continue }
            replySeen[session] = at
            guard sending, reply["reason"] as? String == "answer",
                  let text = reply["text"] as? String, !text.isEmpty else { continue }
            tell("replies", title: name(session, chats[session]), body: preview(text, 300), session: session, tag: session)
        }
        for session in sessions(in: "asks") {
            guard let ask = readJSON(relayDir + "/asks/\(session).json"), let id = ask["id"] as? String,
                  askSeen.insert("\(session)/\(id)").inserted, sending else { continue }
            let isQuestion = ask["kind"] as? String == "question"
            tell(isQuestion ? "questions" : "permissions",
                 title: "\(name(session, chats[session] ?? ask)) · \(isQuestion ? "Question" : "Permission")",
                 body: askText(ask), session: session, tag: "\(session)-ask")
        }
    }

    // a limit's notice from the Mac's Alerts, named the short way ("Weekly at 61%")
    func limitNotice(_ title: String, _ body: String?) {
        let short = title.replacingOccurrences(of: " \\([^)]*\\)", with: "", options: .regularExpression)
        tell("limits", title: short, body: body ?? "", session: nil, tag: "limits")
    }

    // one notice: into the log whatever the switches say, to the phone while the main switch and its kind's are on and
    // its chat is off the phone's screen
    private func tell(_ kind: String, title: String, body: String, session: String?, tag: String) {
        let now = millis(Date())
        let isOnScreen = session != nil && viewing?.session == session
            && Date().timeIntervalSince(viewing?.at ?? .distantPast) < 8
        noticeCount += 1
        var row: [String: Any] = ["id": String(format: "%013.0f-%04d", now, noticeCount % 10_000), "kind": kind,
                                  "title": title, "body": body, "at": now, "read": isOnScreen]
        if let session { row["session"] = session }
        let twoWeeks = now - 14 * 86_400_000
        notices = Array((notices.filter { $0["at"] as? Double ?? 0 > twoWeeks } + [row]).suffix(300))
        saveNotices()
        guard !isOnScreen, notify["main"] != false, notify[kind] == true else { return }
        push.send(title: title, body: body, url: session.map { "/#\($0)" } ?? "/", tag: tag)
    }

    private func saveNotices() {
        noticesAt = max(millis(Date()), noticesAt + 1)
        writeJSON(["notices": notices, "at": noticesAt], to: relayDir + "/notices.json")
    }

    private var unread: Int { notices.filter { $0["read"] as? Bool != true }.count }

    // the log for the phone, newest first; one the phone holds already stays off the wire
    private func noticesJSON(since: Double?) -> [String: Any] {
        let now = millis(Date())
        if let since, since >= noticesAt { return ["same": true, "at": noticesAt, "unread": unread, "now": now] }
        return ["notices": Array(notices.reversed()), "at": noticesAt, "unread": unread, "now": now]
    }

    // read: a notice opened on the phone, or a chat's once the phone opens it; clear: a day's, by their ids
    private func changeNotices(_ request: Request) -> Response {
        guard let body = jsonObject(request.body) else { return .text("Change refused", status: 400) }
        let read = Set(body["read"] as? [String] ?? [])
        let clear = Set(body["clear"] as? [String] ?? [])
        let session = body["session"] as? String
        var changed = false
        if !clear.isEmpty {
            let before = notices.count
            notices.removeAll { clear.contains($0["id"] as? String ?? "") }
            changed = notices.count != before
        }
        for index in notices.indices where notices[index]["read"] as? Bool != true {
            let id = notices[index]["id"] as? String ?? ""
            let chat = notices[index]["session"] as? String
            guard read.contains(id) || (session != nil && chat == session) else { continue }
            notices[index]["read"] = true
            changed = true
        }
        if changed { saveNotices() }
        return .json(["ok": true, "at": noticesAt, "unread": unread])
    }

    private func saveSettings(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let given = body["notify"] as? [String: Bool] else {
            return .text("Settings refused", status: 400)
        }
        for (kind, isOn) in given where notify[kind] != nil { notify[kind] = isOn }
        writeJSON(["notify": notify], to: relayDir + "/settings.json")
        note("relay: notifications set, \(notify.filter { $0.value }.keys.sorted().joined(separator: ", "))")
        return .json(["ok": true, "notify": notify])
    }

    // a chat by the name VS Code shows for it, else its folder
    private func name(_ session: String, _ chat: [String: Any]?) -> String {
        titles[session] ?? chat?["project"] as? String ?? "Claude"
    }

    // the files of chats closed for a day leave the folder, the copies of their pictures too (the transcript keeps
    // the pictures); what the phone sent along stays in files/
    private func sweep() {
        swept = Date()
        let day = millis(Date()) - 86_400_000
        // a hidden mark outlives the two weeks Recent reaches back by two more, then goes
        let kept = hidden.filter { $0.value > millis(Date()) - 28 * 86_400_000 }
        if kept.count != hidden.count {
            hidden = kept
            writeJSON(["hidden": hidden], to: relayDir + "/hidden.json")
        }
        for chat in readChats() where chat["at"] as? Double ?? 0 < day {
            guard let session = chat["session"] as? String, isSessionID(session) else { continue }
            for path in ["chats/\(session).json", "history/\(session).json", "replies/\(session).json",
                         "inbox/\(session).json", "inbox/\(session).ack", "inbox/\(session).entered",
                         "inbox/\(session).stop", "inbox/\(session).switch", "inbox/\(session).rename",
                         "asks/\(session).json"] {
                try? FileManager.default.removeItem(atPath: relayDir + "/" + path)
            }
            let pictures = relayDir + "/images/\(session)"
            for name in (try? FileManager.default.contentsOfDirectory(atPath: pictures)) ?? [] where name.hasSuffix(".json") {
                try? FileManager.default.removeItem(atPath: pictures + "/" + name)
            }
            try? FileManager.default.removeItem(atPath: pictures)
        }
    }

    // MARK: requests

    private func route(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard hostKnown(request.headers["host"]) else { return reply(.text("Host refused", status: 403)) }
        if request.path.hasPrefix("/api/") || request.path.hasPrefix("/relay/") {
            guard request.headers["x-uselim"] == "1" else { return reply(.text("Header missing", status: 403)) }
            if let origin = request.headers["origin"], !originKnown(origin) {
                return reply(.text("Origin refused", status: 403))
            }
        }
        // from the tailnet: the waits stay with the Mac's own processes, and every call carries the pairing code, checked
        // per device with its pause (missed) and across unpaired devices (strangerMissed); the first call with the
        // six-digit code retires the previous one, its phones' notifications moving to the new code
        if isRemote(request) {
            if request.path.hasPrefix("/relay/") { return reply(.text("Local only", status: 403)) }
            if request.path.hasPrefix("/api/") {
                let from = device(request)
                let given = request.headers["x-uselim-key"]
                let match = pairing(given)
                // a blank code answers 401 at once, free and alike for every address
                if match == .blank { return reply(.text("Pairing needed", status: 401)) }
                if let wait = pausedFor(from) {
                    return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                }
                // a paired address's wrong code always goes into its own saved count; while the pause across unpaired
                // devices lasts it answers 429 like theirs, so every address answers alike and every try costs
                if match == .wrong, known[from] != nil {
                    missed(from, given ?? "")
                    if let wait = pausedFor(from) ?? strangersWait() {
                        return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                    }
                    return reply(.text("Pairing needed", status: 401))
                }
                if let wait = strangerPause(from) {
                    return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                }
                switch match {
                case .current:
                    know(from)
                    if let previousCode {
                        push.retag(from: pairingTag(previousCode), to: pairingTag(pairingCode))
                        self.previousCode = nil
                        savePairing(pairingCode)
                        note("relay: a phone paired with the six-digit code; the previous code retired")
                    }
                case .previous:
                    know(from)
                case .blank:
                    return reply(.text("Pairing needed", status: 401))
                case .wrong:
                    // a device yet to pair: its own count, and the count across them all
                    missed(from, given ?? "")
                    strangerMissed(given ?? "")
                    if let wait = pausedFor(from) ?? strangerPause(from) {
                        return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                    }
                    return reply(.text("Pairing needed", status: 401))
                }
            }
        } else if request.path.hasPrefix("/api/") || request.path.hasPrefix("/relay/") {
            // from the Mac itself: the local key, or an older mod's wait naming the ask held that moment
            let isKeyed = isLocalKey(request.headers["x-uselim-key"])
            guard isKeyed || (request.path == "/relay/wait" && isLiveAsk(request)) else {
                return reply(.text("Key needed", status: 403))
            }
            if !isKeyed && !isOlderModNoted {
                isOlderModNoted = true
                note("relay: a chat's older mod waits on the header alone; its next start brings the key")
            }
        }
        switch (request.method, request.path) {
        case ("GET", "/api/state"):
            viewing = nil
            reply(.json(stateJSON()))
        case ("GET", "/api/chat"):
            guard let session = request.query["session"], isSessionID(session),
                  var chat = chatJSON(session) ?? closedChatJSON(session) else {
                return reply(.text("Unknown chat", status: 404))
            }
            viewing = (session, Date())
            // a history the phone holds already stays off the wire
            if let since = Double(request.query["since"] ?? ""), since > 0, chat["historyAt"] as? Double ?? 0 <= since {
                chat["history"] = nil
                chat["historySame"] = true
            }
            reply(.json(chat))
        case ("GET", "/api/image"):
            picture(request, reply)
        case ("GET", "/api/earlier"):
            earlier(request, reply)
        case ("GET", "/api/tool"):
            toolCall(request, reply)
        case ("POST", "/api/send"):
            reply(queue(request))
        case ("POST", "/api/new"):
            openFresh(reply)
        case ("POST", "/api/upload"):
            upload(request, reply)
        case ("POST", "/api/stop"):
            reply(stop(request))
        case ("POST", "/api/switch"):
            reply(switchChat(request))
        case ("POST", "/api/rename"):
            reply(rename(request))
        case ("POST", "/api/hide"):
            reply(hide(request))
        case ("POST", "/api/answer"):
            reply(answer(request))
        case ("POST", "/api/settings"):
            reply(saveSettings(request))
        case ("GET", "/api/notices"):
            viewing = nil
            reply(.json(noticesJSON(since: Double(request.query["since"] ?? ""))))
        case ("POST", "/api/notices"):
            reply(changeNotices(request))
        case ("POST", "/api/push"):
            reply(subscribe(request))
        case ("POST", "/api/test"):
            push.send(title: "Notifications on", body: "Replies and asks from your chats arrive here", url: "/", tag: "test")
            reply(.json(["ok": true, "count": push.count]))
        case ("GET", "/relay/wait"):
            wait(request, reply)
        case ("GET", _):
            reply(file(request.path))
        default:
            reply(.text("Unknown path", status: 404))
        }
    }

    private func hostKnown(_ host: String?) -> Bool {
        guard let host = host?.lowercased() else { return false }
        let name = host.split(separator: ":").first.map(String.init) ?? host
        return host == "127.0.0.1:\(relayPort)" || host == "localhost:\(relayPort)" || name.hasSuffix(".ts.net")
    }

    private func originKnown(_ origin: String) -> Bool {
        guard let url = URL(string: origin.lowercased()), let host = url.host else { return false }
        if host.hasSuffix(".ts.net") { return true }
        return (host == "127.0.0.1" || host == "localhost") && url.port == Int(relayPort)
    }

    private func stateJSON() -> [String: Any] {
        let (list, at) = limits()
        let rows: [[String: Any]] = list.map { l in
            ["kind": l.kind, "name": l.name, "percent": l.percent, "severity": l.severity,
             "resets": l.resets.map { millis($0) as Any } ?? NSNull()]
        }
        let live = liveChats()
        let chats: [[String: Any]] = live.compactMap { chat in
            var row = chat
            let session = chat["session"] as? String ?? ""
            var latest = 0.0
            if let reply = readJSON(relayDir + "/replies/\(session).json") {
                row["reply"] = ["at": reply["at"] ?? 0, "text": preview(reply["text"] as? String ?? "", 160)]
                latest = reply["at"] as? Double ?? 0
            }
            if let ask = readJSON(relayDir + "/asks/\(session).json"), ask["id"] != nil {
                row["ask"] = ask
                latest = max(latest, ask["at"] as? Double ?? 0)
            }
            if let title = titles[session] { row["title"] = title }
            // a hidden chat stays off the list until a reply or an ask comes after it was hidden
            if let at = hidden[session] {
                guard latest > at else { return nil }
                hidden[session] = nil
                writeJSON(["hidden": hidden], to: relayDir + "/hidden.json")
            }
            return row
        }
        // the closed ones of the last two weeks, for a message that opens one again
        let open = Set(live.compactMap { $0["session"] as? String })
        let recent: [[String: Any]] = past.filter { !open.contains($0.session) && hidden[$0.session] == nil }.map { chat in
            ["session": chat.session, "title": titles[chat.session] ?? chat.title, "at": chat.at,
             "project": chat.cwd.split(separator: "/").last.map(String.init) ?? chat.cwd]
        }
        return ["now": millis(Date()), "away": away, "limits": rows, "limitsAt": at.map { millis($0) as Any } ?? NSNull(),
                "chats": chats, "recent": recent, "push": ["key": push.publicKey, "count": push.count], "notify": notify,
                "unread": unread]
    }

    private func chatJSON(_ session: String) -> [String: Any]? {
        guard var chat = readJSON(relayDir + "/chats/\(session).json") else { return nil }
        let history = readJSON(relayDir + "/history/\(session).json") ?? [:]
        let historyAt = history["at"] as? Double ?? 0
        chat["history"] = history["messages"] ?? []
        chat["historyAt"] = historyAt
        if let title = titles[session] ?? pastChat(session)?.title { chat["title"] = title }
        if let ask = readJSON(relayDir + "/asks/\(session).json"), ask["id"] != nil { chat["ask"] = ask }
        // phone messages the history has yet to show: queued, or entered with their turn still running. The mod marks
        // the last one the history holds as that turn ends; a chat on an older mod goes by the history's time.
        let ack = readText(relayDir + "/inbox/\(session).ack")
        let messages = inbox(session)
        let settled = settledID(session, messages) { historyAt }
        chat["settled"] = settled
        chat["outbox"] = messages.filter { ($0["id"] as? String ?? "") > settled }.map { message -> [String: Any] in
            var row = message
            row["delivered"] = (message["id"] as? String ?? "") <= ack
            return row
        }
        chat["now"] = millis(Date())
        return chat
    }

    // a message from the phone, the files sent ahead of it named by their paths beneath the words
    private func queue(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              isKnown(session) else {
            return .text("Message refused", status: 400)
        }
        let text = (body["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let folder = relayDir + "/files/\(session)"
        let files = (body["files"] as? [String] ?? []).filter { name in
            safeFileName(name) == name && FileManager.default.fileExists(atPath: folder + "/" + name)
        }
        guard !text.isEmpty || !files.isEmpty, text.count <= 20_000 else { return .text("Message refused", status: 400) }
        var words = text
        if !files.isEmpty {
            let paths = files.map { folder + "/" + $0 }.joined(separator: "\n")
            words += (words.isEmpty ? "" : "\n\n") + "Attached from the phone:\n" + paths
        }
        phoneActive = Date()
        sent += 1
        let at = millis(Date())
        let id = String(format: "%013.0f-%04d", at, sent % 10_000)
        let ack = readText(relayDir + "/inbox/\(session).ack")
        let hourAgo = at - 3_600_000
        // a delivered message stays an hour, so the phone shows it until the chat's history holds it
        var messages = inbox(session).filter { ($0["id"] as? String ?? "") > ack || ($0["at"] as? Double ?? 0) > hourAgo }
        messages.append(["id": id, "text": words, "at": at])
        writeJSON(["messages": messages], to: relayDir + "/inbox/\(session).json")
        note("relay: a phone message queued for \(projectName(session))\(files.isEmpty ? "" : ", \(files.count) attached")")
        wake(session)
        return .json(["ok": true, "id": id, "at": at])
    }

    // a file from the phone, sent ahead of its message: a picture is fitted off the main thread, then saved
    private func upload(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard let session = request.query["session"], isSessionID(session), isKnown(session),
              !request.body.isEmpty else { return reply(.text("Upload refused", status: 400)) }
        phoneActive = Date()
        let name = request.query["name"] ?? ""
        let folder = relayDir + "/files/\(session)"
        DispatchQueue.global(qos: .userInitiated).async {
            let fitted = fitImage(request.body)
            DispatchQueue.main.async {
                do {
                    let saved = try saveUpload(fitted?.data ?? request.body, named: name, ext: fitted?.ext, in: folder)
                    note("relay: a file from the phone saved for \(self.projectName(session)), \(saved)")
                    reply(.json(["ok": true, "name": saved]))
                } catch {
                    note("relay: a file from the phone failed to save, \(error)")
                    reply(.text("Saving failed", status: 500))
                }
            }
        }
    }

    // a picture of a chat for the phone, 1,000 px at most: one the mod saved (images/, by key), one the phone sent
    // (files/, by name), or one on an earlier page (in the transcript, by its line and block); fitted off the main thread
    private func picture(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard let session = request.query["session"], isSessionID(session) else {
            return reply(.text("Unknown picture", status: 404))
        }
        let send = { (data: Data?) in
            guard let data, let fitted = fitImage(data, longSide: 1000) else {
                return DispatchQueue.main.async { reply(.text("Unknown picture", status: 404)) }
            }
            var response = Response(status: 200, type: mimeType(fitted.ext), body: fitted.data)
            response.isLasting = true
            DispatchQueue.main.async { reply(response) }
        }
        if let at = request.query["at"], at.range(of: "^[0-9]{1,12}(-[0-9]{1,4}){1,2}$", options: .regularExpression) != nil,
           let cwd = cwdOf(session) {
            return transcriptQueue.async { [weak self] in send(self?.transcript(session, cwd: cwd).picture("@" + at)) }
        }
        var source: Data?
        if let key = request.query["key"], key.range(of: "^[A-Za-z0-9_-]{1,100}$", options: .regularExpression) != nil,
           let stored = readJSON(relayDir + "/images/\(session)/\(key).json"), let base64 = stored["data"] as? String {
            source = Data(base64Encoded: base64)
        } else if let name = request.query["file"], safeFileName(name) == name {
            let path = relayDir + "/files/\(session)/\(name)"
            let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? Int) ?? 0
            if size > 0, size <= 40 * 1_048_576 { source = FileManager.default.contents(atPath: path) }
        }
        guard let data = source else { return reply(.text("Unknown picture", status: 404)) }
        DispatchQueue.global(qos: .userInitiated).async { send(data) }
    }

    // a page of the chat before what the phone holds, from the transcript: before a cursor from the last page, or before
    // where the mod's window begins (its oldest tool call, else its oldest words)
    private func earlier(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard let session = request.query["session"], isSessionID(session),
              let cwd = cwdOf(session) else {
            return reply(.text("Unknown chat", status: 404))
        }
        let before = Int(request.query["before"] ?? "")
        let tool = request.query["tool"]
        let text = request.query["text"]
        transcriptQueue.async { [weak self] in
            guard let self else { return }
            let found = self.transcript(session, cwd: cwd)
            var end = found.entries.count
            if let before {
                end = min(max(0, before), end)
            } else if let tool, let index = found.index(ofTool: tool) {
                end = index
            } else if let text, let index = found.index(ofText: text) {
                end = index
            }
            let page = found.page(before: end)
            let cursor: Any = page.cursor.map { $0 as Any } ?? NSNull()
            DispatchQueue.main.async { reply(.json(["items": page.items, "cursor": cursor])) }
        }
    }

    // one tool call whole from the transcript, for the phone's code views, drawn as VS Code draws code: its input and
    // result in full, and for an edit the line it starts on in the file as it stands, so the diff carries
    // the file's own line numbers (found where the new text sits; an edit since then leaves the numbers out)
    private func toolCall(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard let session = request.query["session"], isSessionID(session), let id = request.query["id"],
              id.range(of: "^[A-Za-z0-9_-]{1,100}$", options: .regularExpression) != nil, let cwd = cwdOf(session) else {
            return reply(.text("Unknown call", status: 404))
        }
        transcriptQueue.async { [weak self] in
            guard let self, var found = self.transcript(session, cwd: cwd).call(id) else {
                return DispatchQueue.main.async { reply(.text("Unknown call", status: 404)) }
            }
            let input = found["input"] as? [String: Any] ?? [:]
            if found["tool"] as? String == "Edit", let path = input["file_path"] as? String, path.hasPrefix("/"),
               let added = input["new_string"] as? String, !added.isEmpty,
               (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? Int) ?? .max <= 8 << 20,
               let text = try? String(contentsOfFile: path, encoding: .utf8), let range = text.range(of: added) {
                found["line"] = text[..<range.lowerBound].reduce(1) { $1 == "\n" ? $0 + 1 : $0 }
            }
            DispatchQueue.main.async { reply(.json(found)) }
        }
    }

    // the phone's Stop: the mod reads it within two seconds and ends the running turn; an ask held for the phone lets
    // go at once
    private func stop(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              FileManager.default.fileExists(atPath: relayDir + "/chats/\(session).json") else {
            return .text("Stop refused", status: 400)
        }
        phoneActive = Date()
        stops[session] = Date()
        writeJSON(["at": millis(Date())], to: relayDir + "/inbox/\(session).stop")
        for key in Array(waiters.keys) where key.hasPrefix(session + "/") {
            for waiter in waiters.removeValue(forKey: key) ?? [] { waiter.reply(.json(["state": "stopped"])) }
        }
        note("relay: the phone stopped the turn in \(projectName(session))")
        return .json(["ok": true])
    }

    // the phone's switch of a chat's model or effort: the mod reads it within two seconds and runs the chat's
    // own /model and /effort, which keep the change to that chat. A pick made before the mod read the last joins it;
    // `at` is the phone's clock, which the chat hands back once it switched.
    private static let models: Set<String> = ["opus", "sonnet", "haiku"]
    private static let efforts: Set<String> = ["low", "medium", "high", "xhigh", "max"]

    private func switchChat(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              FileManager.default.fileExists(atPath: relayDir + "/chats/\(session).json") else {
            return .text("Switch refused", status: 400)
        }
        let model = body["model"] as? String
        let effort = body["effort"] as? String
        guard model != nil || effort != nil, model.map(Relay.models.contains) ?? true,
              effort.map(Relay.efforts.contains) ?? true else {
            return .text("Switch refused", status: 400)
        }
        let path = relayDir + "/inbox/\(session).switch"
        var asked = readJSON(path) ?? [:]
        if let model { asked["model"] = model }
        if let effort { asked["effort"] = effort }
        asked["at"] = body["at"] as? Double ?? millis(Date())
        phoneActive = Date()
        writeJSON(asked, to: path)
        note("relay: the phone switched \(projectName(session)) to \([model, effort].compactMap { $0 }.joined(separator: ", "))")
        return .json(["ok": true])
    }

    // the phone's rename, as VS Code's panel renames: an open chat through its own /rename (the mod reads
    // inbox/<session>.rename within two seconds; the chat writes the title, as it keeps its title in memory and writes it
    // again), a closed one by a title line at the end of its transcript, the line VS Code writes; the phone shows the
    // name at once
    private func rename(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              isKnown(session), let cwd = cwdOf(session) else {
            return .text("Rename refused", status: 400)
        }
        let title = (body["title"] as? String ?? "").components(separatedBy: .newlines).joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        guard (1...100).contains(title.count) else { return .text("Rename refused", status: 400) }
        phoneActive = Date()
        titles[session] = title
        pendingTitles[session] = (title, Date())
        if isLive(session) {
            writeJSON(["title": title, "at": millis(Date())], to: relayDir + "/inbox/\(session).rename")
        } else {
            transcriptQueue.async { [weak self] in
                guard let self, let handle = FileHandle(forWritingAtPath: self.transcript(session, cwd: cwd).path) else {
                    return
                }
                defer { try? handle.close() }
                let encoder = JSONEncoder()
                encoder.outputFormatting = .withoutEscapingSlashes
                let name = (try? encoder.encode(title)).map { String(decoding: $0, as: UTF8.self) } ?? "\"\""
                let line = "{\"type\":\"custom-title\",\"customTitle\":\(name),\"sessionId\":\"\(session)\"}\n"
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: Data(line.utf8))
            }
        }
        note("relay: the phone renamed \(projectName(session))")
        return .json(["ok": true])
    }

    // the phone's swipe to delete: the chat leaves the phone's lists, VS Code's Archive, its history kept
    private func hide(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              isKnown(session) else {
            return .text("Hide refused", status: 400)
        }
        hidden[session] = millis(Date())
        writeJSON(["hidden": hidden], to: relayDir + "/hidden.json")
        note("relay: the phone hid \(projectName(session))")
        return .json(["ok": true])
    }

    private func answer(_ request: Request) -> Response {
        guard let body = jsonObject(request.body), let session = body["session"] as? String, isSessionID(session),
              let ask = body["ask"] as? String, !ask.isEmpty else { return .text("Answer refused", status: 400) }
        var answer: [String: Any] = [:]
        if let given = body["answers"] as? [String: Any] { answer["answers"] = given.compactMapValues { $0 as? String } }
        if let allow = body["allow"] as? Bool { answer["allow"] = allow }
        guard !answer.isEmpty else { return .text("Answer refused", status: 400) }
        phoneActive = Date()
        let key = "\(session)/\(ask)"
        if let list = waiters.removeValue(forKey: key), !list.isEmpty {
            for waiter in list { waiter.reply(.json(["state": "answered", "answer": answer])) }
        } else {
            answers[key] = (answer, Date())
        }
        note("relay: the phone answered \(answer["allow"] == nil ? "a question" : "a permission") for \(projectName(session))")
        return .json(["ok": true])
    }

    // a phone's subscription alone, under the code it paired with, which stands behind it from then on
    private func subscribe(_ request: Request) -> Response {
        guard isRemote(request) else { return .text("Phone only", status: 403) }
        guard let body = jsonObject(request.body), let subscription = body["subscription"] as? [String: Any] else {
            return .text("Subscription refused", status: 400)
        }
        let code = pairing(request.headers["x-uselim-key"]) == .previous ? previousCode ?? pairingCode : pairingCode
        let origin = request.headers["origin"] ?? "https://\(request.headers["host"] ?? "localhost")"
        guard push.add(subscription, origin: origin, tag: pairingTag(code)) else {
            return .text("Subscription refused", status: 400)
        }
        return .json(["ok": true, "count": push.count])
    }

    // the mod's long poll: the phone's answer, "desk" once the Mac is in use, or "waiting" after 20 s
    private func wait(_ request: Request, _ reply: @escaping (Response) -> Void) {
        guard let session = request.query["session"], isSessionID(session),
              let ask = request.query["ask"], !ask.isEmpty else { return reply(.text("Wait refused", status: 400)) }
        let key = "\(session)/\(ask)"
        if let stored = answers.removeValue(forKey: key) {
            return reply(.json(["state": "answered", "answer": stored.answer]))
        }
        if let stopped = stops[session], let held = readJSON(relayDir + "/asks/\(session).json"),
           held["at"] as? Double ?? .infinity <= millis(stopped) {
            return reply(.json(["state": "stopped"]))
        }
        let always = request.query["always"] == "1"
        guard away || always else { return reply(.json(["state": "desk"])) }
        nextWaiter += 1
        let id = nextWaiter
        waiters[key, default: []].append((id, always, reply))
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in
            guard let self, var list = self.waiters[key], let at = list.firstIndex(where: { $0.id == id }) else { return }
            let waiter = list.remove(at: at)
            self.waiters[key] = list.isEmpty ? nil : list
            waiter.reply(.json(["state": "waiting"]))
        }
    }

    // back at the Mac: the waits held for the away hours let go; a question's stays beside its dialog
    private func releaseAll(_ body: [String: Any]) {
        let all = waiters.values.flatMap { $0 }.filter { !$0.always }
        waiters = waiters.compactMapValues { list in
            let kept = list.filter(\.always)
            return kept.isEmpty ? nil : kept
        }
        for waiter in all { waiter.reply(.json(body)) }
    }

    private func file(_ path: String) -> Response {
        let files: [String: (name: String, type: String)] = [
            "/": ("index.html", "text/html; charset=utf-8"),
            "/app.js": ("app.js", "text/javascript; charset=utf-8"),
            "/app.css": ("app.css", "text/css; charset=utf-8"),
            "/sw.js": ("sw.js", "text/javascript; charset=utf-8"),
            "/manifest.webmanifest": ("manifest.webmanifest", "application/manifest+json"),
            "/icon.png": ("icon.png", "image/png"),
            "/back.png": ("back.png", "image/png"),
            "/send.png": ("send.png", "image/png"),
            "/attach.png": ("attach.png", "image/png"),
            "/remove.png": ("remove.png", "image/png"),
            "/file.png": ("file.png", "image/png"),
            "/bell.png": ("bell.png", "image/png"),
            "/gear.png": ("gear.png", "image/png"),
            "/reply.png": ("reply.png", "image/png"),
            "/question.png": ("question.png", "image/png"),
            "/permission.png": ("permission.png", "image/png"),
            "/limit.png": ("limit.png", "image/png"),
            "/compose.png": ("compose.png", "image/png"),
            "/stop.png": ("stop.png", "image/png"),
            "/copy.png": ("copy.png", "image/png"),
            "/copied.png": ("copied.png", "image/png"),
            "/updown.png": ("updown.png", "image/png"),
            "/fold.png": ("fold.png", "image/png"),
            "/scan.png": ("scan.png", "image/png"),
            // highlight.js, which colours the phone's code, and jsQR, which reads the Pair window's QR code through
            // the phone's camera; each with its version in the name, so the phone keeps it a week and a new one
            // arrives under a new name
            "/highlight-11.12.0.min.js": ("highlight-11.12.0.min.js", "text/javascript; charset=utf-8"),
            "/jsQR-1.4.0.js": ("jsQR-1.4.0.js", "text/javascript; charset=utf-8"),
        ]
        guard let entry = files[path], let base = Bundle.main.resourceURL,
              let data = try? Data(contentsOf: base.appendingPathComponent("Web/" + entry.name)) else {
            return .text("Unknown path", status: 404)
        }
        var response = Response(status: 200, type: entry.type, body: data)
        response.isLasting = entry.name.hasPrefix("highlight-") || entry.name.hasPrefix("jsQR-")
        return response
    }

    // MARK: the folder

    private func readChats() -> [[String: Any]] {
        sessions(in: "chats").compactMap { readJSON(relayDir + "/chats/\($0).json") }
    }

    private func liveChats() -> [[String: Any]] {
        let now = millis(Date())
        return readChats().filter { chat in
            chat["status"] as? String != "ended" && now - (chat["at"] as? Double ?? 0) < aliveWindow
        }
    }

    private func sessions(in folder: String) -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: relayDir + "/" + folder)) ?? []
        return names.filter { $0.hasSuffix(".json") }.map { String($0.dropLast(5)) }.filter(isSessionID)
    }

    private func inbox(_ session: String) -> [[String: Any]] {
        (readJSON(relayDir + "/inbox/\(session).json")?["messages"] as? [[String: Any]]) ?? []
    }

    private func projectName(_ session: String) -> String {
        readJSON(relayDir + "/chats/\(session).json")?["project"] as? String ?? session
    }
}

// "Header: question" for a question, "Tool: target" for a permission
private func askText(_ ask: [String: Any]) -> String {
    if ask["kind"] as? String == "question" {
        let questions = ask["questions"] as? [[String: Any]] ?? []
        return questions.map { q in
            [q["header"] as? String, q["question"] as? String].compactMap { $0 }.joined(separator: ": ")
        }.joined(separator: " · ")
    }
    return [ask["tool"] as? String, ask["detail"] as? String].compactMap { $0 }.joined(separator: ": ")
}

private func preview(_ text: String, _ length: Int) -> String {
    let plain = text
        .replacingOccurrences(of: "```[\\s\\S]*?```", with: " ", options: .regularExpression)
        .replacingOccurrences(of: "[*_`#>|]", with: "", options: .regularExpression)
        .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        .trimmingCharacters(in: .whitespaces)
    return plain.count > length ? String(plain.prefix(length)) + "…" : plain
}

// seconds since the last keyboard, pointer or trackpad input, from the HID system
private func idleSeconds() -> TimeInterval {
    let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOHIDSystem"))
    guard service != 0 else { return 0 }
    defer { IOObjectRelease(service) }
    let value = IORegistryEntryCreateCFProperty(service, "HIDIdleTime" as CFString, kCFAllocatorDefault, 0)?
        .takeRetainedValue() as? NSNumber
    return (value?.doubleValue ?? 0) / 1_000_000_000
}

private func isSessionID(_ text: String) -> Bool {
    text.range(of: "^[A-Za-z0-9_-]{8,80}$", options: .regularExpression) != nil
}

private func millis(_ date: Date) -> Double { (date.timeIntervalSince1970 * 1000).rounded() }

private func readText(_ path: String) -> String {
    ((try? String(contentsOfFile: path, encoding: .utf8)) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
}

private func jsonObject(_ data: Data) -> [String: Any]? {
    (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
}

func readJSON(_ path: String) -> [String: Any]? {
    guard let data = FileManager.default.contents(atPath: path), !data.isEmpty else { return nil }
    return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
}

func readJSONArray(_ path: String) -> [Any]? {
    guard let data = FileManager.default.contents(atPath: path), !data.isEmpty else { return nil }
    return (try? JSONSerialization.jsonObject(with: data)) as? [Any]
}

func writeJSON(_ object: Any, to path: String) {
    guard let data = try? JSONSerialization.data(withJSONObject: object) else { return }
    try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                             withIntermediateDirectories: true)
    try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
}
