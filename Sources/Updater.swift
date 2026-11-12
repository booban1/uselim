import AppKit

// Uselim's own updates: the latest release of booban1/uselim, asked for at launch and every six hours; the Update row
// shows while it is newer. An update downloads the zip and its checksum, checks the digest and that the app inside
// carries the one Uselim Signer certificate (the requirement Culpmit pins), then a small script waits for this copy to
// quit, moves it to the Trash, puts the new one in its place and opens it. The hooks and the plugin keep their paths.

final class Updater {
    private let latestURL = URL(string: "https://api.github.com/repos/booban1/uselim/releases/latest")!
    private let requirement = "identifier \"com.simonbooban.uselim\" and certificate leaf = "
        + "H\"140952e822dd4f3065dc6fed4ec81e013bb11ff0\""
    // a newer version on GitHub, while one is
    private(set) var newest: String?
    private(set) var isBusy = false
    private var checkedAt = Date.distantPast
    var onChange: (() -> Void)?

    func check() {
        guard !isBusy, Date().timeIntervalSince(checkedAt) >= 6 * 3600 else { return }
        checkedAt = Date()
        latest { [weak self] found in
            self?.newest = found.flatMap { isNewer($0.version, than: appVersion) ? $0.version : nil }
            self?.onChange?()
        }
    }

    // the latest release's version, its zip and the zip's checksum
    private func latest(_ done: @escaping ((version: String, zip: URL, sum: URL)?) -> Void) {
        var request = URLRequest(url: latestURL, timeoutInterval: 20)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: request) { data, _, _ in
            var found: (version: String, zip: URL, sum: URL)?
            if let data, let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
               let tag = json["tag_name"] as? String, let assets = json["assets"] as? [[String: Any]] {
                let link = { (suffix: String) in
                    (assets.first { ($0["name"] as? String)?.hasSuffix(suffix) == true }?["browser_download_url"] as? String)
                        .flatMap(URL.init(string:))
                }
                if let zip = link(".zip"), let sum = link(".zip.sha256") {
                    found = (tag.trimmingCharacters(in: CharacterSet(charactersIn: "v")), zip, sum)
                }
            }
            DispatchQueue.main.async { done(found) }
        }.resume()
    }

    // the update; each live step and any stop comes back as one line for the menu
    func update(_ report: @escaping (String) -> Void) {
        guard !isBusy else { return }
        isBusy = true
        let stop = { [weak self] (line: String) in
            self?.isBusy = false
            report(line)
        }
        latest { [weak self] found in
            guard let self else { return }
            guard let found, isNewer(found.version, than: appVersion) else { return stop("Uselim is up to date") }
            report("Downloading Uselim \(found.version)…")
            note("update: installing \(found.version) over \(appVersion)")
            URLSession.shared.dataTask(with: found.sum) { sumData, _, _ in
                let expected = sumData.map { String(decoding: $0, as: UTF8.self) }?
                    .split(whereSeparator: \.isWhitespace).first.map(String.init)?.lowercased()
                URLSession.shared.downloadTask(with: found.zip) { file, _, _ in
                    let ready = file.flatMap { self.unpack($0, expected: expected) }
                    DispatchQueue.main.async {
                        guard let ready, self.swap(to: ready) else {
                            return stop("The update stopped; choose Update to try again")
                        }
                        NSApp.terminate(nil)
                    }
                }.resume()
            }.resume()
        }
    }

    // the zip unpacked beside the other updates once its digest matches, and its app taken only with the pinned signature
    private func unpack(_ zip: URL, expected: String?) -> URL? {
        guard let expected, sha256(of: zip) == expected else { return nil }
        let folder = supportDir + "/update-\(Int(Date().timeIntervalSince1970))"
        guard runTool("/usr/bin/ditto", ["-x", "-k", zip.path, folder], timeout: 120).status == 0 else { return nil }
        let app = folder + "/Uselim.app"
        let checked = runTool("/usr/bin/codesign", ["--verify", "--strict", "-R=\(requirement)", app], timeout: 60)
        return checked.status == 0 ? URL(fileURLWithPath: app) : nil
    }

    private func sha256(of file: URL) -> String? {
        let out = runTool("/usr/bin/shasum", ["-a", "256", file.path], timeout: 60).out
        return out.split(whereSeparator: \.isWhitespace).first.map(String.init)?.lowercased()
    }

    // a small script waits for this copy to quit, moves it to the Trash, puts the new one in its place and opens it
    private func swap(to new: URL) -> Bool {
        let app = Bundle.main.bundlePath
        guard app.hasSuffix("/Uselim.app") else { return false }
        let script = """
        while /bin/kill -0 \(getpid()) 2>/dev/null; do /bin/sleep 0.2; done
        /bin/mv "\(app)" "$HOME/.Trash/Uselim \(appVersion).app"
        /usr/bin/ditto "\(new.path)" "\(app)"
        /usr/bin/touch "\(app)"
        /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "\(app)"
        /usr/bin/open "\(app)"
        """
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-c", script]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return false }
        return true
    }
}
