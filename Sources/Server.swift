import Foundation
import Network

// A small HTTP/1.1 server on the loopback address for the phone relay, one request per connection. The head comes
// first (16 KiB at most, whole within 10 s of the connection's start) and goes to `admit`, the relay's checks of
// Host, header, Origin and key: an answer there ends the request before any of its body is read. An admitted body
// follows by its Content-Length or in chunks, decoded as they arrive: a file from the phone streams to a file in the
// spool folder (200 MiB at most), any other body stays in memory (1 MiB at most). A connection silent for 30 s while
// its request arrives closes, as does a body still arriving past its deadline (an upload's ten minutes, 30 s for the
// rest), and 24 connections stay open at most, each one past them answered 503. The route then
// answers, at once or later, which is how /relay/wait holds a request open; the connection closes after the answer.

private let headLimit = 16_384
private let bodyLimit = 1_048_576
#if RELAY_TEST
private let idleSeconds = Double(ProcessInfo.processInfo.environment["USELIM_TEST_IDLE"] ?? "") ?? 30
private let connectionLimit = Int(ProcessInfo.processInfo.environment["USELIM_TEST_CONNECTIONS"] ?? "") ?? 24
private let bodySeconds = Double(ProcessInfo.processInfo.environment["USELIM_TEST_BODY"] ?? "") ?? 30
#else
private let idleSeconds = 30.0
private let connectionLimit = 24
private let bodySeconds = 30.0
#endif
private let headSeconds = min(10, idleSeconds)
// an upload's whole body within ten minutes, any other body within its own deadline
private let uploadSeconds = 600.0

struct Request {
    let method: String
    let path: String
    let query: [String: String]
    let headers: [String: String]
    var body = Data()
    // an upload's body, streamed to a file in the spool folder: the route moves it away or leaves it to be removed
    var bodyFile: String?
    // what let a request from the phone in at its head (a phone's id, or "lasting" for the lasting code), so its route
    // answers 401 once that key has ended meanwhile
    var key: String?
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
    private let spool: String
    private let admit: (inout Request) -> Response?
    private let route: (Request, @escaping (Response) -> Void) -> Void
    private var listener: NWListener?
    private var open: [ObjectIdentifier: Exchange] = [:]

    init(port: UInt16, spool: String, admit: @escaping (inout Request) -> Response?,
         route: @escaping (Request, @escaping (Response) -> Void) -> Void) {
        self.port = port
        self.spool = spool
        self.admit = admit
        self.route = route
    }

    func start() {
        // an upload cut short by a quit leaves its part behind; the spool starts empty
        try? FileManager.default.removeItem(atPath: spool)
        try? FileManager.default.createDirectory(atPath: spool, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
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
        guard open.count < connectionLimit else {
            connection.start(queue: .main)
            return send(.text("Busy", status: 503), on: connection)
        }
        let exchange = Exchange(connection)
        let key = ObjectIdentifier(exchange)
        open[key] = exchange
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .cancelled, .failed:
                exchange.finish()
                self?.open[key] = nil
                connection.stateUpdateHandler = nil
            default: break
            }
        }
        connection.start(queue: .main)
        exchange.headTimer = after(headSeconds) { connection.cancel() }
        exchange.idleTimer = after(idleSeconds) { connection.cancel() }
        receive(exchange)
    }

    private func receive(_ exchange: Exchange) {
        exchange.connection.receive(minimumIncompleteLength: 1, maximumLength: 1_048_576) {
            [weak self] data, _, isComplete, error in
            guard let self, !exchange.isRouted else { return }
            if let data, !data.isEmpty {
                exchange.idleTimer?.cancel()
                exchange.idleTimer = after(idleSeconds) { exchange.connection.cancel() }
                if let answer = self.take(data, into: exchange) {
                    exchange.isRouted = true
                    exchange.stopTimers()
                    return self.send(answer, on: exchange.connection)
                }
                if exchange.isRouted { return }
            }
            if isComplete || error != nil {
                exchange.connection.cancel()
            } else {
                self.receive(exchange)
            }
        }
    }

    // the bytes as they arrive: the head until it is whole, then the body; an answer ends the request
    private func take(_ data: Data, into exchange: Exchange) -> Response? {
        var data = data
        if exchange.head == nil {
            exchange.buffer.append(data)
            guard let end = exchange.buffer.range(of: Data("\r\n\r\n".utf8),
                                                  in: exchange.buffer.startIndex..<min(exchange.buffer.endIndex,
                                                                                       exchange.buffer.startIndex + headLimit))
            else { return exchange.buffer.count >= headLimit ? .text("Malformed request", status: 400) : nil }
            guard var head = parseHead(exchange.buffer[exchange.buffer.startIndex..<end.lowerBound]) else {
                return .text("Malformed request", status: 400)
            }
            exchange.headTimer?.cancel()
            exchange.headTimer = nil
            data = exchange.buffer[end.upperBound...]
            exchange.buffer = Data()
            if let refusal = admit(&head) { return refusal }
            exchange.head = head
            if let refusal = exchange.frame(head, spool: spool) { return refusal }
            // the whole body within its deadline, so a trickle under the idle time holds a connection a while at most
            exchange.bodyTimer = after(head.path == "/api/upload" ? uploadSeconds : bodySeconds) {
                exchange.connection.cancel()
            }
            if head.headers["expect"]?.lowercased() == "100-continue" {
                exchange.connection.send(content: Data("HTTP/1.1 100 Continue\r\n\r\n".utf8), completion: .idempotent)
            }
        }
        switch exchange.feed(data) {
        case .more:
            return nil
        case .malformed:
            return .text("Malformed request", status: 400)
        case .tooLarge:
            return .text("Too large", status: 413)
        case .done:
            guard var request = exchange.head else { return .text("Malformed request", status: 400) }
            request.body = exchange.memory
            request.bodyFile = exchange.closeFile()
            exchange.isRouted = true
            exchange.stopTimers()
            route(request) { [weak self] response in self?.send(response, on: exchange.connection) }
            return nil
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

private func after(_ seconds: Double, _ work: @escaping () -> Void) -> DispatchWorkItem {
    let item = DispatchWorkItem(block: work)
    DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: item)
    return item
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
    case 409: return "Conflict"
    case 413: return "Content Too Large"
    case 500: return "Internal Server Error"
    case 503: return "Service Unavailable"
    default: return "Status"
    }
}

// the request line and headers; the query read from the target
private func parseHead(_ bytes: Data) -> Request? {
    let head = String(decoding: bytes, as: UTF8.self)
    var lines = head.components(separatedBy: "\r\n")
    let first = lines.removeFirst().split(separator: " ")
    guard first.count == 3 else { return nil }
    var headers: [String: String] = [:]
    for line in lines {
        guard let colon = line.firstIndex(of: ":") else { continue }
        let name = line[..<colon].lowercased()
        headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
    }
    let target = String(first[1])
    let parts = URLComponents(string: target)
    var query: [String: String] = [:]
    for item in parts?.queryItems ?? [] { query[item.name] = item.value ?? "" }
    return Request(method: String(first[0]), path: parts?.path ?? target, query: query, headers: headers)
}

private enum Fed {
    case more, done, malformed, tooLarge
}

// one connection's request as it arrives: the head's bytes until it is whole, then its body, by Content-Length or
// in chunks (what Tailscale's proxy sends for a body of unstated length), into memory or into a spool file
private final class Exchange {
    let connection: NWConnection
    var buffer = Data()
    var head: Request?
    var memory = Data()
    var isRouted = false
    var headTimer: DispatchWorkItem?
    var idleTimer: DispatchWorkItem?
    var bodyTimer: DispatchWorkItem?
    private var file: FileHandle?
    private var filePath: String?
    private var limit = bodyLimit
    private var taken = 0
    // Content-Length's bytes still to come, or the chunk reader's place
    private var remaining = 0
    private var isChunked = false
    private var chunkLeft = 0
    private var line = Data()
    private var stage = Stage.size

    private enum Stage { case size, data, dataEnd, trailer }

    init(_ connection: NWConnection) {
        self.connection = connection
    }

    // the body's framing and its home: an answer refuses the request as it stands
    func frame(_ head: Request, spool: String) -> Response? {
        limit = head.path == "/api/upload" ? uploadLimit : bodyLimit
        isChunked = head.headers["transfer-encoding"]?.lowercased().contains("chunked") == true
        if !isChunked {
            guard let length = Int(head.headers["content-length"] ?? "0"), length >= 0 else {
                return .text("Malformed request", status: 400)
            }
            guard length <= limit else { return .text("Too large", status: 413) }
            remaining = length
        }
        if head.path == "/api/upload" {
            let path = spool + "/" + UUID().uuidString
            guard FileManager.default.createFile(atPath: path, contents: nil, attributes: [.posixPermissions: 0o600]),
                  let handle = FileHandle(forWritingAtPath: path) else { return .text("Saving failed", status: 500) }
            file = handle
            filePath = path
        }
        return nil
    }

    func feed(_ data: Data) -> Fed {
        guard isChunked else {
            let part = data.prefix(remaining)
            write(part)
            remaining -= part.count
            return remaining == 0 ? .done : .more
        }
        var index = data.startIndex
        while index < data.endIndex || stage == .dataEnd || stage == .size || stage == .trailer {
            switch stage {
            case .data:
                let end = data.index(index, offsetBy: min(chunkLeft, data.distance(from: index, to: data.endIndex)))
                write(data[index..<end])
                chunkLeft -= data.distance(from: index, to: end)
                index = end
                if chunkLeft == 0 { stage = .dataEnd }
                if index == data.endIndex && stage == .data { return .more }
            case .size, .dataEnd, .trailer:
                // a line: a chunk's size, the line break after its data, or a trailer, the empty one ending the body
                guard let found = nextLine(data, &index) else { return line.count > 1_024 ? .malformed : .more }
                guard found.count <= 1_024 else { return .malformed }
                switch stage {
                case .size:
                    let digits = String(decoding: found, as: UTF8.self).split(separator: ";").first
                        .map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
                    guard let size = Int(digits, radix: 16), size >= 0 else { return .malformed }
                    if size == 0 {
                        stage = .trailer
                    } else {
                        // the room left, never the sum: a size near Int.max leaves the sum past what an Int holds
                        guard size <= limit - taken else { return .tooLarge }
                        chunkLeft = size
                        stage = .data
                    }
                case .dataEnd:
                    guard found.isEmpty else { return .malformed }
                    stage = .size
                default:
                    if found.isEmpty { return .done }
                }
            }
        }
        return .more
    }

    // the next whole line from what has arrived, its break left off; a part waits in `line` for the rest
    private func nextLine(_ data: Data, _ index: inout Data.Index) -> Data? {
        guard let newline = data[index...].firstIndex(of: 0x0A) else {
            line.append(data[index...])
            index = data.endIndex
            return nil
        }
        line.append(data[index..<newline])
        index = data.index(after: newline)
        var found = line
        line = Data()
        if found.last == 0x0D { found.removeLast() }
        return found
    }

    private func write(_ part: Data) {
        taken += part.count
        if let file { try? file.write(contentsOf: part) } else { memory.append(part) }
    }

    // the spool file's path once its body is whole, for the route
    func closeFile() -> String? {
        try? file?.close()
        file = nil
        return filePath
    }

    func stopTimers() {
        headTimer?.cancel()
        idleTimer?.cancel()
        bodyTimer?.cancel()
        headTimer = nil
        idleTimer = nil
        bodyTimer = nil
    }

    // the connection's end: its timers stop, and a spool file the route left behind goes
    func finish() {
        stopTimers()
        try? file?.close()
        file = nil
        if let filePath { try? FileManager.default.removeItem(atPath: filePath) }
    }
}
