import Foundation
import ServiceManagement

// Open at login through the system's login items list. Only a copy in Applications registers, the Mac's or the
// person's own (a standard account installs there), so a build run from elsewhere leaves the setting be.

var isInApplications: Bool {
    let path = Bundle.main.bundlePath
    return path.hasPrefix("/Applications/") || path.hasPrefix(NSHomeDirectory() + "/Applications/")
}

var openAtLogin: Bool {
    get { SMAppService.mainApp.status == .enabled }
    set {
        guard isInApplications else { return }
        do {
            if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            note("open at login: \(error.localizedDescription)")
        }
    }
}
