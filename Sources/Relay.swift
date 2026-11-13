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
    // the Mac's own name on the tailnet, the one name Host and Origin carry from the phone: Tailscale's status gives it
    // while the Phone switch is on, and the last one stands in across a restart
    #if RELAY_TEST
    var tailnetName = ProcessInfo.processInfo.environment["USELIM_TEST_NAME"] ?? "mac.example.ts.net"
    #else
    var tailnetName = UserDefaults.standard.string(forKey: "tailnetName") ?? "" {
        didSet { if tailnetName != oldValue { UserDefaults.standard.set(tailnetName, forKey: "tailnetName") } }
    }
    #endif
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
        readPhones()
        readGuard()
        // a subscription from before the tags joins the lasting code; one that no phone stands behind leaves
        push.keep(tags: pushTags(), adopting: legacy.first.map(pairingTag) ?? "")
        // what is on disk at the start counts as seen, so a restart sends only what arrives after it
        scan(sending: false)
        server = Server(port: relayPort, spool: relayDir + "/spool",
                        admit: { [weak self] (request: inout Request) -> Response? in
                            guard let self else { return .text("Relay closed", status: 503) }
                            return self.admit(&request)
                        },
                        route: { [weak self] request, reply in self?.route(request, reply) })
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

    // A phone pairs once with six digits from Pair a phone and gets its own key: 32 random bytes, kept on the phone and
    // sent with every call, and kept on the Mac as their SHA-256 beside the phone's name in relay/phones.json. The
    // digits pair one phone, within ten minutes, while Pair a phone stays open; Remove there ends a phone's key and
    // its notifications. The lasting code from before the phones' own keys (relay/pairing.json) keeps working for
    // fourteen days after the update, and the first pairing call carrying it trades it for that phone's key.
    struct Phone {
        let id: String
        var name: String
        let hash: String
        let paired: Date
        var seen: Date
    }

    private(set) var phones: [Phone] = []
    private var seenSaved = Date.distantPast
    // the digits on show in Pair a phone, and until when they pair
    private(set) var pairCode: String?
    private var pairCodeUntil = Date.distantPast
    // the codes used or run out within the hour, as their tags, so the phone tells a spent code from a mistyped one
    private var spent: [String: Date] = [:]
    // the lasting code from before, and the end of its days
    private var legacy: [String] = []
    private var legacyUntil = Date.distantPast
    // Pair a phone's redraw: a phone paired, or one left the list
    var onPhones: (() -> Void)?
    #if RELAY_TEST
    private let pairSeconds = Double(ProcessInfo.processInfo.environment["USELIM_TEST_PAIR_SECONDS"] ?? "") ?? 600
    var tailnetLogin = ProcessInfo.processInfo.environment["USELIM_TEST_LOGIN"] ?? ""
    #else
    private let pairSeconds = 600.0
    // the Mac's own login on the tailnet, which Tailscale Serve names on each request from the phone
    var tailnetLogin = UserDefaults.standard.string(forKey: "tailnetLogin") ?? "" {
        didSet { if tailnetLogin != oldValue { UserDefaults.standard.set(tailnetLogin, forKey: "tailnetLogin") } }
    }
    #endif
    private var isLoginNoted = false
    // wrong codes per device (the phone's tailnet address, as Tailscale Serve forwards it): the distinct codes sent, so a
    // phone repeating its one stale code counts once; from the fifth, the device pauses a minute, doubling to an hour,
    // and every code from it waits out the pause, the right one included. Tailscale stays the outer gate. A right code
    // leaves the count as it stands, so the phone's own polls hand a guesser under its address zero fresh tries; the
    // count lapses a day after its last miss. A paired device's count lives in relay/guard.json too, so a restart
    // keeps its pause; each code is kept as its tag.
    private var misses: [String: (codes: Set<String>, until: Date, last: Date)] = [:]

    // the phones, and the lasting code from before with its fourteen days, counted from the first start that found it
    private func readPhones() {
        phones = (readJSON(relayDir + "/phones.json")?["phones"] as? [[String: Any]] ?? []).compactMap { row in
            guard let id = row["id"] as? String, let hash = row["hash"] as? String else { return nil }
            return Phone(id: id, name: row["name"] as? String ?? "Phone", hash: hash,
                         paired: Date(timeIntervalSince1970: (row["paired"] as? Double ?? 0) / 1000),
                         seen: Date(timeIntervalSince1970: (row["seen"] as? Double ?? 0) / 1000))
        }
        let path = relayDir + "/pairing.json"
        guard var record = readJSON(path) else { return }
        legacy = [record["code"] as? String, record["previous"] as? String].compactMap { $0 }.filter { !$0.isEmpty }
        if let until = record["until"] as? Double {
            legacyUntil = Date(timeIntervalSince1970: until / 1000)
        } else {
            legacyUntil = Date().addingTimeInterval(14 * 86_400)
            record["until"] = millis(legacyUntil)
            writeJSON(record, to: path)
            chmod(path, 0o600)
        }
        if legacy.isEmpty || legacyUntil < Date() { retireLegacy() }
    }

    private func savePhones() {
        let path = relayDir + "/phones.json"
        let rows: [[String: Any]] = phones.map {
            ["id": $0.id, "name": $0.name, "hash": $0.hash, "paired": millis($0.paired), "seen": millis($0.seen)]
        }
        writeJSON(["phones": rows], to: path)
        chmod(path, 0o600)
    }

    private func retireLegacy() {
        legacy = []
        try? FileManager.default.removeItem(atPath: relayDir + "/pairing.json")
    }

    // Pair a phone opening, or its minutes running out: fresh digits from the system's secure random source
    @discardableResult
    func openPairing() -> String {
        if let pairCode { spend(pairCode) }
        let code = String(format: "%06d", Int.random(in: 0..<1_000_000))
        pairCode = code
        pairCodeUntil = Date().addingTimeInterval(pairSeconds)
        return code
    }

    // the seconds the digits on show still pair
    var pairSecondsLeft: Int { max(0, Int(pairCodeUntil.timeIntervalSinceNow.rounded(.up))) }

    // Pair a phone closing: its digits pair nothing more
    func closePairing() {
        if let pairCode { spend(pairCode) }
        pairCode = nil
    }

    private func spend(_ code: String) {
        let now = Date()
        spent = spent.filter { now.timeIntervalSince($0.value) < 3600 }
        spent[pairingTag(code)] = now
    }

    // Remove in Pair a phone: the phone's key ends, and its notifications with it
    func removePhone(_ id: String) {
        guard let phone = phones.first(where: { $0.id == id }) else { return }
        phones.removeAll { $0.id == id }
        savePhones()
        push.keep(tags: pushTags(), adopting: "")
        note("relay: \(phone.name) removed")
        onPhones?()
    }

    // the tags the notification subscriptions may carry: each phone's own, and the lasting code's through its days
    private func pushTags() -> Set<String> {
        Set(phones.map(\.id)).union(legacy.map(pairingTag))
    }

    private enum Key: Equatable { case phone(String), pairCode, legacy(String), spent, wrong, blank }

    // what a request's key names: a phone's own key; on the pairing call, the digits on show; the lasting code within
    // its days, in any case, its dash and spaces left out; digits spent within the hour; or nothing
    private func keyOf(_ given: String?, path: String) -> Key {
        guard let given, !given.isEmpty else { return .blank }
        let hash = sha256Hex(given)
        if let phone = phones.first(where: { $0.hash == hash }) { return .phone(phone.id) }
        let digits = given.filter(\.isNumber)
        let isDigits = given.allSatisfy { $0.isNumber || $0 == " " }
        if path == "/api/pair", isDigits, let pairCode, Date() < pairCodeUntil, digits == pairCode { return .pairCode }
        let clean = { (text: String) in text.uppercased().filter { $0.isLetter || $0.isNumber } }
        if Date() < legacyUntil, let code = legacy.first(where: { clean($0) == clean(given) }) { return .legacy(code) }
        if isDigits, digits.count == 6, spent[pairingTag(digits)] != nil || digits == pairCode { return .spent }
        return .wrong
    }

    // the phone's name from its browser: the kind of device its User-Agent names, a number added past the first
    private func phoneName(_ agent: String?) -> String {
        let agent = agent ?? ""
        let kind = agent.contains("iPhone") ? "iPhone" : agent.contains("iPad") ? "iPad"
            : agent.contains("Android") ? "Android phone" : agent.contains("Macintosh") ? "Mac" : "Phone"
        let taken = Set(phones.map(\.name))
        guard taken.contains(kind) else { return kind }
        var number = 2
        while taken.contains("\(kind) \(number)") { number += 1 }
        return "\(kind) \(number)"
    }

    // a phone's request: its last sight, kept to disk once a minute at most
    private func see(_ id: String) {
        guard let at = phones.firstIndex(where: { $0.id == id }) else { return }
        phones[at].seen = Date()
        guard Date().timeIntervalSince(seenSaved) > 60 else { return }
        seenSaved = Date()
        savePhones()
    }

    // the phone's pairing call: the digits on show in Pair a phone, or the lasting code from before, traded for the
    // phone's own key; the digits pair this one phone, and the lasting code retires, its notifications moving to it
    private func pair(_ request: Request) -> Response {
        let key = keyOf(request.headers["x-uselim-key"], path: "/api/pair")
        let isTraded: Bool
        switch key {
        case .pairCode: isTraded = false
        case .legacy: isTraded = true
        default: return .text("Pairing refused", status: 400)
        }
        guard isRemote(request) else { return .text("Phone only", status: 403) }
        let token = base64URL(SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) })
        let id = "p" + SymmetricKey(size: .bits128).withUnsafeBytes { Data($0) }.prefix(4)
            .map { String(format: "%02x", $0) }.joined()
        let phone = Phone(id: id, name: phoneName(request.headers["user-agent"]), hash: sha256Hex(token),
                          paired: Date(), seen: Date())
        phones.append(phone)
        savePhones()
        if isTraded {
            for code in legacy { push.retag(from: pairingTag(code), to: phone.id) }
            retireLegacy()
            note("relay: \(phone.name) paired, trading the lasting code from before for its own key")
        } else {
            if let pairCode { spend(pairCode) }
            pairCode = nil
            note("relay: \(phone.name) paired")
        }
        onPhones?()
        return .json(["token": token, "name": phone.name])
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

    // a phone's key as the Mac keeps it
    private func sha256Hex(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
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
        // the lasting code's days over while Uselim runs: it retires at once, its notifications with it
        if !legacy.isEmpty, Date() >= legacyUntil {
            retireLegacy()
            push.keep(tags: pushTags(), adopting: "")
            note("relay: the lasting code from before retired, its fourteen days over")
        }
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

    // the checks a request passes at its head, before any of its body is read: its Host, the header and Origin on /api
    // and /relay, and its key; an answer here ends the request
    private func admit(_ request: inout Request) -> Response? {
        var answer: Response?
        check(request) { answer = $0 }
        // the key that let a request from the phone in, for the route to find it still standing
        if answer == nil, isRemote(request) {
            switch keyOf(request.headers["x-uselim-key"], path: request.path) {
            case .phone(let id): request.key = id
            case .legacy: request.key = "lasting"
            default: break
            }
        }
        return answer
    }

    // a key that ended while its request's body arrived: Remove took its phone, or the lasting code retired
    private func isEnded(_ key: String?) -> Bool {
        guard let key else { return false }
        return key == "lasting" ? legacy.isEmpty : !phones.contains { $0.id == key }
    }

    private func check(_ request: Request, _ reply: (Response) -> Void) {
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
                // Tailscale Serve names the tailnet login behind each request: one naming another login stops here
                if let login = request.headers["tailscale-user-login"]?.lowercased() {
                    if !isLoginNoted {
                        isLoginNoted = true
                        note("relay: Tailscale Serve names the login behind the phone's requests")
                    }
                    if !tailnetLogin.isEmpty, login != tailnetLogin.lowercased() {
                        return reply(.text("Login refused", status: 403))
                    }
                }
                let from = device(request)
                let given = request.headers["x-uselim-key"]
                let key = keyOf(given, path: request.path)
                // a blank key answers 401 at once, free and alike for every address
                if key == .blank { return reply(.text("Pairing needed", status: 401)) }
                if let wait = pausedFor(from) {
                    return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                }
                // spent digits count as wrong ones, and the phone hears they are spent
                let refusal: Response = key == .spent
                    ? .json(["error": "Pairing needed", "reason": "spent"], status: 401)
                    : .text("Pairing needed", status: 401)
                let isWrong = key == .wrong || key == .spent
                // a paired address's wrong key always goes into its own saved count; while the pause across unpaired
                // devices lasts it answers 429 like theirs, so every address answers alike and every try costs
                if isWrong, known[from] != nil {
                    missed(from, given ?? "")
                    if let wait = pausedFor(from) ?? strangersWait() {
                        return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                    }
                    return reply(refusal)
                }
                if let wait = strangerPause(from) {
                    return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                }
                switch key {
                case .phone(let id):
                    know(from)
                    see(id)
                case .pairCode, .legacy:
                    know(from)
                case .blank:
                    return reply(.text("Pairing needed", status: 401))
                case .wrong, .spent:
                    // a device yet to pair: its own count, and the count across them all
                    missed(from, given ?? "")
                    strangerMissed(given ?? "")
                    if let wait = pausedFor(from) ?? strangerPause(from) {
                        return reply(.json(["error": "Too many tries", "wait": wait], status: 429))
                    }
                    return reply(refusal)
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
    }

    // the handlers, for a request `admit` let through, its body whole, its key still standing
    private func route(_ request: Request, _ reply: @escaping (Response) -> Void) {
        if isEnded(request.key) { return reply(.text("Pairing needed", status: 401)) }
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
        case ("POST", "/api/pair"):
            reply(pair(request))
        case ("POST", "/api/push"):
            reply(subscribe(request))
        #if RELAY_TEST
        // the test build's stand-ins for Pair a phone: its digits, and its Remove
        case ("POST", "/relay/test-pair"):
            reply(.json(["code": openPairing()]))
        case ("POST", "/relay/test-remove"):
            removePhone(request.query["id"] ?? "")
            reply(.json(["ok": true]))
        #endif
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
        return host == "127.0.0.1:\(relayPort)" || host == "localhost:\(relayPort)" || isOwnName(name)
    }

    private func originKnown(_ origin: String) -> Bool {
        guard let url = URL(string: origin.lowercased()), let host = url.host else { return false }
        if isOwnName(host) { return true }
        return (host == "127.0.0.1" || host == "localhost") && url.port == Int(relayPort)
    }

    private func isOwnName(_ name: String) -> Bool {
        !tailnetName.isEmpty && name == tailnetName.lowercased()
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
    // a file from the phone, which the server streamed to the spool: a picture fitted, ImageIO reading it from the
    // file, and anything else moved into place as it came
    private func upload(_ request: Request, _ reply: @escaping (Response) -> Void) {
        let size = request.bodyFile.flatMap { try? FileManager.default.attributesOfItem(atPath: $0)[.size] as? Int } ?? 0
        guard let file = request.bodyFile, size > 0, let session = request.query["session"], isSessionID(session),
              isKnown(session) else { return reply(.text("Upload refused", status: 400)) }
        phoneActive = Date()
        let name = request.query["name"] ?? ""
        let folder = relayDir + "/files/\(session)"
        DispatchQueue.global(qos: .userInitiated).async {
            let fitted = fitImage(at: URL(fileURLWithPath: file))
            DispatchQueue.main.async {
                do {
                    let saved = try fitted?.data.map { try saveUpload($0, named: name, ext: fitted?.ext, in: folder) }
                        ?? moveUpload(file, named: name, ext: fitted?.ext, in: folder)
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
        // the ask the chat holds this moment alone takes an answer; one that moved on (answered at the Mac, its turn
        // over, a newer ask in its place) answers 409, and the phone closes its card
        guard readJSON(relayDir + "/asks/\(session).json")?["id"] as? String == ask else {
            return .text("Ask moved on", status: 409)
        }
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

    // a phone's subscription alone, under the phone's own tag (the lasting code's, from a phone yet to trade it), which
    // stands behind it from then on: Remove ends both
    private func subscribe(_ request: Request) -> Response {
        guard isRemote(request) else { return .text("Phone only", status: 403) }
        guard let body = jsonObject(request.body), let subscription = body["subscription"] as? [String: Any] else {
            return .text("Subscription refused", status: 400)
        }
        let tag: String
        switch keyOf(request.headers["x-uselim-key"], path: request.path) {
        case .phone(let id): tag = id
        case .legacy(let code): tag = pairingTag(code)
        default: return .text("Subscription refused", status: 400)
        }
        let origin = request.headers["origin"] ?? "https://\(request.headers["host"] ?? "localhost")"
        guard push.add(subscription, origin: origin, tag: tag) else {
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
    // a permission's call in full rides on the card; the notice names its first line, a command's or a file's path
    let first = (ask["detail"] as? String)?.split(separator: "\n", maxSplits: 1).first.map(String.init)
    return [ask["tool"] as? String, first].compactMap { $0 }.joined(separator: ": ")
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
