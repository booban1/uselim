import AppKit

// Setup and removal with zero windows, for Culpmit's Claude limits switch and for a hand install.
// "--setup" adds Uselim's hooks to Claude Code's settings, and the status line where none is set (a backup first),
// installs Uselim's Claude Code plugin from the copy inside this app, turns Open at login on and starts Uselim.
// "--remove" takes each of those back and quits Uselim. "--state" prints where each stands as one JSON line.
// "--quit" asks a running copy to quit, for an update or a removal.
// Each step of a setup or a removal prints one JSON line, {"step", "ok", "note"}, and the last one {"done", "ok"}; the
// exit code is 0 when every step went through, 2 when Claude Code is missing, 1 for any other failure.

// Claude Code's folder as Claude Code finds it: CLAUDE_CONFIG_DIR where set, else .claude in HOME
private let claudeFolder: String = {
    let environment = ProcessInfo.processInfo.environment
    if let folder = environment["CLAUDE_CONFIG_DIR"], !folder.isEmpty { return folder }
    return (environment["HOME"].flatMap { $0.isEmpty ? nil : $0 } ?? NSHomeDirectory()) + "/.claude"
}()
private let settingsPath = claudeFolder + "/settings.json"
private let backupDir = supportDir + "/backups"
private let marketplaceName = "uselim"
private let pluginID = "uselim@uselim"
private let bundleID = Bundle.main.bundleIdentifier ?? "com.simonbooban.uselim"

private var executable: String { Bundle.main.executablePath ?? "/Applications/Uselim.app/Contents/MacOS/Uselim" }
// the plugin and its one-entry marketplace, copied into the app by build.sh
private var bundledPlugin: String { (Bundle.main.resourcePath ?? "") + "/Plugin" }

// the hook events Uselim answers, each with its matcher where it takes one; a permission waits up to an hour for the
// phone beside the panel's own dialog
private let events: [(event: String, matcher: String?, isPermission: Bool)] = [
    ("PreToolUse", "AskUserQuestion", false),
    ("PostToolUse", nil, false),
    ("Notification", "permission_prompt", false),
    ("UserPromptSubmit", nil, false),
    ("SessionEnd", nil, false),
    ("PermissionRequest", nil, true),
]

// a command of Uselim's, wherever its app sits
private func isOurs(_ command: Any?, _ flag: String) -> Bool {
    guard let command = command as? String else { return false }
    return command.hasSuffix("/Uselim \(flag)") || command == "Uselim \(flag)"
}

private func isOurHook(_ handler: [String: Any]) -> Bool {
    isOurs(handler["command"], "--hook") || isOurs(handler["command"], "--permission")
}

// MARK: the lines a setup prints

private var isAllOK = true

private func printLine(_ object: [String: Any]) {
    let options: JSONSerialization.WritingOptions = [.sortedKeys, .withoutEscapingSlashes]
    guard let data = try? JSONSerialization.data(withJSONObject: object, options: options) else { return }
    print(String(decoding: data, as: UTF8.self))
    fflush(stdout)
}

private func step(_ name: String, _ isOK: Bool, _ note: String = "") {
    if !isOK { isAllOK = false }
    var line: [String: Any] = ["step": name, "ok": isOK]
    if !note.isEmpty { line["note"] = note }
    printLine(line)
}

private func finish(claudeMissing: Bool) -> Never {
    printLine(["done": true, "ok": isAllOK && !claudeMissing])
    exit(claudeMissing ? 2 : isAllOK ? 0 : 1)
}

// MARK: Claude Code's settings

// the settings as they stand: an absent file reads as empty, one that fails to parse as nil, left untouched
private func readSettings() -> [String: Any]? {
    guard let data = FileManager.default.contents(atPath: settingsPath) else { return [:] }
    return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
}

// the settings with Uselim's hook groups taken out, and when adding, put back in one group per event; the status line
// set where none is set, or taken off where it is Uselim's. Every other entry stays as it is.
private func reconciled(_ settings: [String: Any], adding: Bool) -> [String: Any] {
    var settings = settings
    var hooks = settings["hooks"] as? [String: Any] ?? [:]
    for (event, value) in hooks {
        guard let groups = value as? [[String: Any]] else { continue }
        let kept = groups.filter { group in !((group["hooks"] as? [[String: Any]]) ?? []).contains(where: isOurHook) }
        hooks[event] = kept.isEmpty ? nil : kept
    }
    if adding {
        for entry in events {
            let flag = entry.isPermission ? "--permission" : "--hook"
            let handler: [String: Any] = ["type": "command", "command": "\(executable) \(flag)",
                                          "timeout": entry.isPermission ? 3600 : 5]
            var group: [String: Any] = ["hooks": [handler]]
            if let matcher = entry.matcher { group["matcher"] = matcher }
            hooks[entry.event] = (hooks[entry.event] as? [[String: Any]] ?? []) + [group]
        }
        if settings["statusLine"] == nil {
            settings["statusLine"] = ["type": "command", "command": "\(executable) --statusline"]
        }
    } else if isOurs((settings["statusLine"] as? [String: Any])?["command"], "--statusline") {
        settings["statusLine"] = nil
    }
    settings["hooks"] = hooks.isEmpty ? nil : hooks
    return settings
}

private let settingsOptions: JSONSerialization.WritingOptions = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]

// writes the settings when they changed, the file as it stood copied to the backups first (the newest ten kept); a
// linked settings file is written where it points
private func writeSettings(_ settings: [String: Any], over before: [String: Any]) -> (isOK: Bool, note: String) {
    guard let new = try? JSONSerialization.data(withJSONObject: settings, options: settingsOptions) else {
        return (false, "the settings could not be written as JSON")
    }
    if let old = try? JSONSerialization.data(withJSONObject: before, options: settingsOptions), old == new {
        return (true, "already in place")
    }
    let target = URL(fileURLWithPath: settingsPath).resolvingSymlinksInPath()
    var note = "written"
    if FileManager.default.fileExists(atPath: target.path) {
        try? FileManager.default.createDirectory(atPath: backupDir, withIntermediateDirectories: true)
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyy-MM-dd-HHmmss"
        let backup = "\(backupDir)/settings-\(stamp.string(from: Date())).json"
        do {
            try FileManager.default.copyItem(atPath: target.path, toPath: backup)
        } catch {
            return (false, "the backup failed: \(error.localizedDescription)")
        }
        let kept = ((try? FileManager.default.contentsOfDirectory(atPath: backupDir)) ?? [])
            .filter { $0.hasPrefix("settings-") }.sorted(by: >)
        for old in kept.dropFirst(10) { try? FileManager.default.removeItem(atPath: "\(backupDir)/\(old)") }
        note = "backup in \(backup)"
    }
    do {
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try (new + Data("\n".utf8)).write(to: target, options: .atomic)
    } catch {
        return (false, error.localizedDescription)
    }
    return (true, note)
}

private func settingsStep(adding: Bool) {
    guard let before = readSettings() else {
        step("hooks", false, "\(settingsPath) holds something other than JSON; left as it is")
        return
    }
    let after = reconciled(before, adding: adding)
    let written = writeSettings(after, over: before)
    step("hooks", written.isOK, written.note)
    let line = (after["statusLine"] as? [String: Any])?["command"]
    if adding {
        step("statusline", written.isOK, isOurs(line, "--statusline") ? "Uselim's" : "another status line kept")
    }
}

// MARK: Claude Code's own command, for the plugin

// on the PATH or in a usual install folder, else the newest inside a VS Code family editor's Claude Code extension
func claudeBinary() -> String? {
    let home = NSHomeDirectory()
    var folders = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
    folders += ["\(home)/.local/bin", "\(home)/.claude/local", "/usr/local/bin", "/opt/homebrew/bin",
                "\(home)/.npm-global/bin", "\(home)/.bun/bin"]
    for folder in folders where FileManager.default.isExecutableFile(atPath: folder + "/claude") {
        return folder + "/claude"
    }
    var found: [(version: String, path: String)] = []
    for editor in [".vscode", ".vscode-insiders", ".cursor", ".windsurf"] {
        let root = "\(home)/\(editor)/extensions"
        for name in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? []
        where name.hasPrefix("anthropic.claude-code-") {
            let path = "\(root)/\(name)/resources/native-binary/claude"
            guard FileManager.default.isExecutableFile(atPath: path) else { continue }
            let version = name.dropFirst("anthropic.claude-code-".count).split(separator: "-").first.map(String.init)
            found.append((version ?? "0", path))
        }
    }
    return found.max { isNewer($1.version, than: $0.version) }?.path
}

// a dotted version against another, part by part
func isNewer(_ a: String, than b: String) -> Bool {
    let x = a.split(separator: ".").map { Int($0) ?? 0 }
    let y = b.split(separator: ".").map { Int($0) ?? 0 }
    for index in 0..<max(x.count, y.count) {
        let p = index < x.count ? x[index] : 0
        let q = index < y.count ? y[index] : 0
        if p != q { return p > q }
    }
    return false
}

// a tool's exit code and output, ended after its time (two minutes unless given)
func runTool(_ path: String, _ arguments: [String], timeout: TimeInterval = 120) -> (status: Int32, out: String) {
    let tool = Process()
    tool.executableURL = URL(fileURLWithPath: path)
    tool.arguments = arguments
    let out = Pipe()
    tool.standardOutput = out
    tool.standardError = out
    do { try tool.run() } catch { return (-1, error.localizedDescription) }
    let deadline = DispatchWorkItem { if tool.isRunning { tool.terminate() } }
    DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: deadline)
    let data = out.fileHandleForReading.readDataToEndOfFile()
    tool.waitUntilExit()
    deadline.cancel()
    return (tool.terminationStatus, String(decoding: data, as: UTF8.self))
}

// the JSON array a list command prints, its entries by one key
private func listed(_ claude: String, _ arguments: [String], key: String) -> [String: [String: Any]] {
    let result = runTool(claude, arguments + ["--json"])
    guard let start = result.out.firstIndex(of: "["),
          let entries = (try? JSONSerialization.jsonObject(with: Data(result.out[start...].utf8))) as? [[String: Any]]
    else { return [:] }
    var byKey: [String: [String: Any]] = [:]
    for entry in entries { if let name = entry[key] as? String { byKey[name] = entry } }
    return byKey
}

private func isPluginOn(_ claude: String) -> Bool {
    listed(claude, ["plugin", "list"], key: "id")[pluginID]?["enabled"] as? Bool == true
}

// the plugin from the copy inside this app: its marketplace added where none named uselim exists (a marketplace of
// that name already in place stays, whatever its source), then the plugin installed for the user
private func pluginStep(_ claude: String) {
    if isPluginOn(claude) {
        step("plugin", true, "already installed")
        return
    }
    if listed(claude, ["plugin", "marketplace", "list"], key: "name")[marketplaceName] == nil {
        guard FileManager.default.fileExists(atPath: bundledPlugin + "/.claude-plugin/marketplace.json") else {
            step("plugin", false, "the plugin's copy is missing from \(bundledPlugin)")
            return
        }
        let added = runTool(claude, ["plugin", "marketplace", "add", bundledPlugin])
        guard added.status == 0 else {
            step("plugin", false, "marketplace add: \(added.out.trimmingCharacters(in: .whitespacesAndNewlines))")
            return
        }
    }
    let installed = runTool(claude, ["plugin", "install", pluginID, "--scope", "user"])
    let isOn = installed.status == 0 && isPluginOn(claude)
    step("plugin", isOn, isOn ? "installed" : installed.out.trimmingCharacters(in: .whitespacesAndNewlines))
}

private func pluginRemoval(_ claude: String) {
    let plugins = listed(claude, ["plugin", "list"], key: "id")
    if plugins[pluginID] != nil {
        let removed = runTool(claude, ["plugin", "uninstall", pluginID])
        guard removed.status == 0 else {
            step("plugin", false, removed.out.trimmingCharacters(in: .whitespacesAndNewlines))
            return
        }
    }
    // the marketplace goes only where it is the copy inside this app; a marketplace from elsewhere stays
    let source = listed(claude, ["plugin", "marketplace", "list"], key: "name")[marketplaceName]?["path"] as? String
    if let source, source.hasPrefix(Bundle.main.bundlePath) {
        _ = runTool(claude, ["plugin", "marketplace", "remove", marketplaceName])
    }
    step("plugin", true, "removed")
}

// MARK: the running copy

private func runningCopies() -> [NSRunningApplication] {
    NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
        .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
}

// asks every running copy to quit, then ends any left after five seconds
func quitRunning() -> Bool {
    let copies = runningCopies()
    copies.forEach { $0.terminate() }
    for _ in 0..<50 where runningCopies().contains(where: { !$0.isTerminated }) { usleep(100_000) }
    runningCopies().filter { !$0.isTerminated }.forEach { $0.forceTerminate() }
    usleep(200_000)
    return runningCopies().allSatisfy(\.isTerminated)
}

// MARK: the commands

func runSetup() -> Never {
    settingsStep(adding: true)
    let claude = claudeBinary()
    if let claude {
        pluginStep(claude)
    } else {
        step("plugin", false, "Claude Code was not found: the claude command, or the VS Code extension")
    }
    if isInApplications {
        openAtLogin = true
        step("login", openAtLogin, openAtLogin ? "on" : "System Settings holds it off")
    } else {
        step("login", false, "Open at login takes a copy in Applications")
    }
    if runningCopies().isEmpty {
        let opener = runTool("/usr/bin/open", ["-g", Bundle.main.bundlePath])
        step("launch", opener.status == 0, opener.status == 0 ? "started" : opener.out)
    } else {
        step("launch", true, "already running")
    }
    finish(claudeMissing: claude == nil)
}

// the removal's steps, shared by "--remove" and the menu's Remove Uselim
private func removeSteps(quittingOthers: Bool) {
    settingsStep(adding: false)
    if let claude = claudeBinary() {
        pluginRemoval(claude)
    } else {
        step("plugin", true, "Claude Code was not found; nothing to remove")
    }
    if openAtLogin { openAtLogin = false }
    step("login", !openAtLogin, openAtLogin ? "System Settings holds it on" : "off")
    if quittingOthers { step("quit", quitRunning()) }
}

// the app itself to the Trash; the running copy goes on until it quits
private func trashApp() {
    do {
        try FileManager.default.trashItem(at: URL(fileURLWithPath: Bundle.main.bundlePath), resultingItemURL: nil)
        step("trash", true, "moved to the Trash")
    } catch {
        step("trash", false, error.localizedDescription)
    }
}

func runRemove() -> Never {
    removeSteps(quittingOthers: true)
    trashApp()
    finish(claudeMissing: false)
}

// the menu's Remove Uselim: every step of a removal, the app to the Trash, then this copy quits
func removeFromMenu() {
    removeSteps(quittingOthers: false)
    trashApp()
    NSApp.terminate(nil)
}

func printState() {
    let settings = readSettings() ?? [:]
    let hooks = settings["hooks"] as? [String: Any] ?? [:]
    let hasHooks = events.allSatisfy { entry in
        ((hooks[entry.event] as? [[String: Any]]) ?? []).contains { group in
            ((group["hooks"] as? [[String: Any]]) ?? []).contains(where: isOurHook)
        }
    }
    let line = (settings["statusLine"] as? [String: Any])?["command"]
    let statusLine = settings["statusLine"] == nil ? "none" : isOurs(line, "--statusline") ? "uselim" : "other"
    let plugin = claudeBinary().map(isPluginOn) ?? false
    printLine(["version": appVersion, "hooks": hasHooks, "statusline": statusLine, "plugin": plugin,
               "login": openAtLogin, "running": !runningCopies().isEmpty])
}
