import CryptoKit
import Foundation

// Web push to the phone's Home Screen app (RFC 8030), each message encrypted for its subscription (RFC 8291,
// aes128gcm) and signed with Uselim's VAPID key (RFC 8292). The key and the subscriptions live in the relay folder:
// vapid.json (the signing key, readable by this user alone) and push.json. Apple's push service carries the
// message; a 404 or 410 from it retires that subscription.

final class Push {
    private let keyPath = relayDir + "/vapid.json"
    private let listPath = relayDir + "/push.json"
    private let key: P256.Signing.PrivateKey
    private var subscriptions: [[String: Any]] = []

    init() {
        if let json = readJSON(keyPath), let raw = (json["private"] as? String).flatMap(base64URLDecode),
           let stored = try? P256.Signing.PrivateKey(rawRepresentation: raw) {
            key = stored
        } else {
            key = P256.Signing.PrivateKey()
            writeJSON(["private": base64URL(key.rawRepresentation)], to: keyPath)
            chmod(keyPath, 0o600)
        }
        subscriptions = (readJSONArray(listPath) ?? []).compactMap { $0 as? [String: Any] }
    }

    // the applicationServerKey the phone subscribes with
    var publicKey: String { base64URL(key.publicKey.x963Representation) }
    var count: Int { subscriptions.count }

    func add(_ subscription: [String: Any], origin: String) {
        guard let endpoint = subscription["endpoint"] as? String, endpoint.hasPrefix("https://"),
              let keys = subscription["keys"] as? [String: Any], keys["p256dh"] is String, keys["auth"] is String else { return }
        subscriptions.removeAll { $0["endpoint"] as? String == endpoint }
        subscriptions.append(["endpoint": endpoint, "keys": keys, "origin": origin])
        save()
        note("relay: phone subscribed for notifications (\(subscriptions.count) in all)")
    }

    func send(title: String, body: String, url: String, tag: String) {
        let payload: [String: Any] = ["title": title, "body": String(body.prefix(600)), "url": url, "tag": tag]
        guard let data = try? JSONSerialization.data(withJSONObject: payload) else { return }
        for subscription in subscriptions { deliver(data, to: subscription) }
    }

    private func save() {
        guard let data = try? JSONSerialization.data(withJSONObject: subscriptions) else { return }
        try? data.write(to: URL(fileURLWithPath: listPath), options: .atomic)
        chmod(listPath, 0o600)
    }

    private func deliver(_ payload: Data, to subscription: [String: Any]) {
        guard let endpoint = subscription["endpoint"] as? String, let url = URL(string: endpoint),
              let scheme = url.scheme, let host = url.host,
              let keys = subscription["keys"] as? [String: Any],
              let p256dh = (keys["p256dh"] as? String).flatMap(base64URLDecode),
              let auth = (keys["auth"] as? String).flatMap(base64URLDecode),
              let body = try? encrypt(payload, p256dh: p256dh, auth: auth) else { return }
        let subject = subscription["origin"] as? String ?? "https://localhost"
        var request = URLRequest(url: url, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.setValue("aes128gcm", forHTTPHeaderField: "Content-Encoding")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue("86400", forHTTPHeaderField: "TTL")
        request.setValue("high", forHTTPHeaderField: "Urgency")
        request.setValue(vapid(audience: "\(scheme)://\(host)", subject: subject), forHTTPHeaderField: "Authorization")
        URLSession.shared.uploadTask(with: request, from: body) { [weak self] _, response, error in
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            DispatchQueue.main.async {
                if code == 404 || code == 410 {
                    self?.subscriptions.removeAll { $0["endpoint"] as? String == endpoint }
                    self?.save()
                    note("relay: a retired notification subscription left the list (HTTP \(code))")
                } else if !(200...299).contains(code) {
                    note("relay: notification refused, HTTP \(code) \(error?.localizedDescription ?? "")")
                }
            }
        }.resume()
    }

    // "vapid t=<JWT>, k=<public key>": the JWT names the push service as audience, lasts twelve hours and carries
    // the relay's own address as its contact
    private func vapid(audience: String, subject: String) -> String {
        let header = base64URL(Data(#"{"typ":"JWT","alg":"ES256"}"#.utf8))
        let claims: [String: Any] = ["aud": audience, "exp": Int(Date().timeIntervalSince1970) + 12 * 3600, "sub": subject]
        let body = base64URL((try? JSONSerialization.data(withJSONObject: claims, options: .withoutEscapingSlashes)) ?? Data())
        let input = "\(header).\(body)"
        let signature = (try? key.signature(for: Data(input.utf8)).rawRepresentation) ?? Data()
        return "vapid t=\(input).\(base64URL(signature)), k=\(publicKey)"
    }

    // RFC 8291: a fresh key pair per message, the shared secret mixed with the subscription's auth secret, then one
    // aes128gcm record (RFC 8188) whose header carries the salt, the record size and the fresh public key
    private func encrypt(_ plain: Data, p256dh: Data, auth: Data) throws -> Data {
        let phone = try P256.KeyAgreement.PublicKey(x963Representation: p256dh)
        let local = P256.KeyAgreement.PrivateKey()
        let localPublic = local.publicKey.x963Representation
        let shared = try local.sharedSecretFromKeyAgreement(with: phone)
        var keyInfo = Data("WebPush: info".utf8)
        keyInfo.append(0)
        keyInfo.append(p256dh)
        keyInfo.append(localPublic)
        let ikm = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: auth, sharedInfo: keyInfo, outputByteCount: 32)
        var salt = Data(count: 16)
        for i in 0..<16 { salt[i] = UInt8.random(in: 0...255) }
        var cekInfo = Data("Content-Encoding: aes128gcm".utf8)
        cekInfo.append(0)
        var nonceInfo = Data("Content-Encoding: nonce".utf8)
        nonceInfo.append(0)
        let cek = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, salt: salt, info: cekInfo, outputByteCount: 16)
        let nonce = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, salt: salt, info: nonceInfo, outputByteCount: 12)
            .withUnsafeBytes { Data($0) }
        var record = plain
        record.append(2)
        let sealed = try AES.GCM.seal(record, using: cek, nonce: AES.GCM.Nonce(data: nonce))
        var out = salt
        out.append(contentsOf: [0, 0, 0x10, 0])
        out.append(UInt8(localPublic.count))
        out.append(localPublic)
        out.append(sealed.ciphertext)
        out.append(sealed.tag)
        return out
    }
}

func base64URL(_ data: Data) -> String {
    data.base64EncodedString()
        .replacingOccurrences(of: "+", with: "-")
        .replacingOccurrences(of: "/", with: "_")
        .replacingOccurrences(of: "=", with: "")
}

func base64URLDecode(_ text: String) -> Data? {
    var plain = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
    while plain.count % 4 != 0 { plain += "=" }
    return Data(base64Encoded: plain)
}
