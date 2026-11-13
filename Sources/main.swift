import AppKit

// Uselim: Claude's usage limits in the menu bar, read through the Claude Code login kept in the keychain.
// "--status" prints the limits and leaves; "--hook" takes one Claude Code event on stdin and leaves; "--statusline"
// takes one status line update on stdin, keeps its figures and prints the line; "--permission" takes one permission
// request, puts it on the phone and prints the phone's answer, unless the Mac's dialog answers first.

#if RELAY_TEST
// a test build runs the relay alone, in the folder and on the port Tools/test-relay.sh gives it; given --permission,
// it runs that hook against the test relay
if !CommandLine.arguments.contains("--permission") {
    let testRelay = Relay()
    testRelay.start()
    RunLoop.main.run()
}
#endif

// "--setup", "--remove", "--state" and "--quit": setup and removal with zero windows, in Setup.swift
if CommandLine.arguments.contains("--setup") { runSetup() }
if CommandLine.arguments.contains("--remove") { runRemove() }
if CommandLine.arguments.contains("--state") {
    printState()
    exit(0)
}
if CommandLine.arguments.contains("--quit") { exit(quitRunning() ? 0 : 1) }

// "--login on" or "--login off" sets Open at login, the menu's switch; "--login" alone prints where it stands
if let at = CommandLine.arguments.firstIndex(of: "--login") {
    if at + 1 < CommandLine.arguments.count { openAtLogin = CommandLine.arguments[at + 1] == "on" }
    print("open at login: \(openAtLogin ? "on" : "off")")
    exit(0)
}
if CommandLine.arguments.contains("--status") {
    printStatus()
    exit(0)
}
if CommandLine.arguments.contains("--hook") {
    runHook()
    exit(0)
}
if CommandLine.arguments.contains("--statusline") {
    runStatusLine()
    exit(0)
}
if CommandLine.arguments.contains("--permission") {
    runPermission()
    exit(0)
}

// off until the Control Strip question is settled: it holds one third-party item, and Culpmit's chip has it
UserDefaults.standard.register(defaults: ["touchBar": false])
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let controller = Controller()
app.delegate = controller
app.run()
