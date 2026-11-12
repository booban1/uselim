import Foundation
import Network

// A small HTTP/1.1 server on the loopback address for the phone relay. One request per connection: the request is
// read whole (a head of 16 KiB at most, then Content-Length bytes of body: 32 MiB for a file from the phone, 1 MiB
// for the rest), handed to the route, and the connection closes after the answer. A route may answer later, which is
// how /relay/wait holds a request open.

private let headLimit = 16_384
private let bodyLimit = 1_048_576

struct Request {
    let method: String
    let path: String
    let query: [String: String]
    let headers: [String: String]
    let body: Data
}

struct Response {
    var status: Int
    var type: String
    var body: Data
    // a picture under a name that keeps its bytes: the phone's browser keeps it a week
    var isLasting = false

    static func json(_ object: Any, status: Int = 200) -> Response {
        let data = (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8)
        return Response(status: status, type: "application/json; charset=utf-8", body: data)
    }

    static func text(_ words: String, status: Int) -> Response {
        Response(status: status, type: "text/plain; charset=utf-8", body: Data(words.utf8))
    }
}

final class Server {
    private let port: UInt16
    private let route: (Request, @escaping (Response) -> Void) -> Void
    private var listener: NWListener?

    init(port: UInt16, route: @escaping (Request, @escaping (Response) -> Void) -> Void) {
        self.port = port
        self.route = route
    }

    func start() {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: port)!)
        let port = self.port
        do {
            let listener = try NWListener(using: params)
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready: note("relay: listening on 127.0.0.1:\(port)")
                case .failed(let error): note("relay: server failed, \(error)")
                default: break
                }
            }
            listener.start(queue: .main)
            self.listener = listener
        } catch {
            note("relay: server refused to start, \(error)")
        }
    }

    // the Phone switch off: the port closes until the next start
    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: .main)
        receive(connection, Data())
    }

    private func receive(_ connection: NWConnection, _ buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1_048_576) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            switch parse(buffer) {
            case .request(let request):
                self.route(request) { response in self.send(response, on: connection) }
            case .malformed:
                self.send(.text("Malformed request", status: 400), on: connection)
            case .tooLarge:
                self.send(.text("Too large", status: 413), on: connection)
            case .partial:
                if isComplete || error != nil {
                    connection.cancel()
                } else {
                    self.receive(connection, buffer)
                }
            }
        }
    }

    private func send(_ response: Response, on connection: NWConnection) {
        var head = "HTTP/1.1 \(response.status) \(reason(response.status))\r\n"
        head += "Content-Type: \(response.type)\r\n"
        head += "Content-Length: \(response.body.count)\r\n"
        head += response.isLasting ? "Cache-Control: private, max-age=604800, immutable\r\n" : "Cache-Control: no-store\r\n"
        head += "X-Content-Type-Options: nosniff\r\n"
        head += "Referrer-Policy: no-referrer\r\n"
        if response.type.hasPrefix("text/html") {
            head += "Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n"
        }
        head += "Connection: close\r\n\r\n"
        var out = Data(head.utf8)
        out.append(response.body)
        connection.send(content: out, completion: .contentProcessed { _ in connection.cancel() })
    }
}

private func reason(_ status: Int) -> String {
    switch status {
    case 200: return "OK"
    case 400: return "Bad Request"
    case 401: return "Unauthorized"
    case 403: return "Forbidden"
    case 429: return "Too Many Requests"
    case 404: return "Not Found"
    case 405: return "Method Not Allowed"
    case 413: return "Content Too Large"
    case 500: return "Internal Server Error"
    default: return "Status"
    }
}

private enum Parsed {
    case partial
    case malformed
    case tooLarge
    case request(Request)
}

// the head is looked for in the first 16 KiB alone, so a long body arriving in pieces costs one read of each piece
private func parse(_ buffer: Data) -> Parsed {
    let window = buffer.startIndex..<min(buffer.endIndex, buffer.startIndex + headLimit)
    guard let end = buffer.range(of: Data("\r\n\r\n".utf8), in: window) else {
        return buffer.count >= headLimit ? .malformed : .partial
    }
    let head = String(decoding: buffer[buffer.startIndex..<end.lowerBound], as: UTF8.self)
    var lines = head.components(separatedBy: "\r\n")
    let first = lines.removeFirst().split(separator: " ")
    guard first.count == 3 else { return .malformed }
    var headers: [String: String] = [:]
    for line in lines {
        guard let colon = line.firstIndex(of: ":") else { continue }
        let name = line[..<colon].lowercased()
        headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
    }
    let target = String(first[1])
    let parts = URLComponents(string: target)
    let path = parts?.path ?? target
    let limit = path == "/api/upload" ? uploadLimit : bodyLimit
    let bodyStart = end.upperBound
    var body = Data()
    if headers["transfer-encoding"]?.lowercased().contains("chunked") == true {
        // decoded once the closing empty line has arrived, so a long body costs one pass
        guard buffer.distance(from: bodyStart, to: buffer.endIndex) <= limit + limit / 16 + 1024 else { return .tooLarge }
        guard buffer.suffix(4) == Data("\r\n\r\n".utf8) else { return .partial }
        switch unchunk(buffer[bodyStart...], limit: limit) {
        case .request(let whole): body = whole.body
        case let other: return other
        }
    } else {
        let length = Int(headers["content-length"] ?? "0") ?? -1
        guard length >= 0 else { return .malformed }
        guard length <= limit else { return .tooLarge }
        guard buffer.distance(from: bodyStart, to: buffer.endIndex) >= length else { return .partial }
        body = Data(buffer[bodyStart..<buffer.index(bodyStart, offsetBy: length)])
    }
    var query: [String: String] = [:]
    for item in parts?.queryItems ?? [] { query[item.name] = item.value ?? "" }
    return .request(Request(method: String(first[0]), path: path, query: query, headers: headers, body: body))
}

// a chunked body, what Tailscale's proxy sends for a body of unstated length; the request it answers carries the body
// alone
private func unchunk(_ data: Data, limit: Int) -> Parsed {
    let crlf = Data("\r\n".utf8)
    var body = Data()
    var index = data.startIndex
    while true {
        guard let line = data.range(of: crlf, in: index..<data.endIndex) else { return .partial }
        let field = String(decoding: data[index..<line.lowerBound], as: UTF8.self)
        let digits = field.split(separator: ";").first.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
        guard let size = Int(digits, radix: 16), size >= 0 else { return .malformed }
        let start = line.upperBound
        if size == 0 {
            // the last chunk: an empty line ends it, after any trailers
            let isEnded = data[start...].starts(with: crlf)
                || data.range(of: Data("\r\n\r\n".utf8), in: line.lowerBound..<data.endIndex) != nil
            return isEnded ? .request(Request(method: "", path: "", query: [:], headers: [:], body: body)) : .partial
        }
        guard body.count + size <= limit else { return .tooLarge }
        guard data.distance(from: start, to: data.endIndex) >= size + 2 else { return .partial }
        body.append(data[start..<data.index(start, offsetBy: size)])
        index = data.index(start, offsetBy: size + 2)
    }
}
