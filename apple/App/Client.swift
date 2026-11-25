import Foundation

struct Endpoint: Hashable, Sendable {
    let url: URL
    var origin: String { url.absoluteString }

    // Validate the original wire spelling, before Foundation can normalize it.
    init(_ origin: String) throws {
        let prefix = "https://"
        let suffix = ".ts.net"
        guard origin.hasPrefix(prefix), origin.hasSuffix(suffix),
              origin.utf8.count <= prefix.count + 63 + 1 + 63 + suffix.count else { throw ClientError.origin }
        let labels = origin.dropFirst(prefix.count).dropLast(suffix.count)
            .split(separator: ".", omittingEmptySubsequences: false)
        guard labels.count == 2, labels.allSatisfy(Self.isDNSLabel),
              let url = URL(string: origin), url.absoluteString == origin else { throw ClientError.origin }
        self.url = url
    }

    private static func isDNSLabel(_ label: Substring) -> Bool {
        let bytes = label.utf8
        guard bytes.count <= 63, let first = bytes.first, let last = bytes.last,
              Self.isLabelAlphanumeric(first), Self.isLabelAlphanumeric(last) else { return false }
        return bytes.allSatisfy { Self.isLabelAlphanumeric($0) || $0 == 45 }
    }

    private static func isLabelAlphanumeric(_ byte: UInt8) -> Bool {
        (97...122).contains(byte) || (48...57).contains(byte)
    }

    #if DEBUG
    init(developmentOrigin input: String) throws {
        guard input.utf8.count <= 1024,
              let parts = URLComponents(string: input),
              let scheme = parts.scheme?.lowercased(), let host = parts.host?.lowercased(),
              let rawHost = parts.percentEncodedHost, rawHost.lowercased() == host,
              !host.isEmpty, host.utf8.allSatisfy({ Self.hostBytes.contains($0) }),
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil,
              parts.percentEncodedPath.isEmpty || parts.percentEncodedPath == "/",
              parts.port.map({ (1...65535).contains($0) }) ?? true else { throw ClientError.origin }
        guard Self.loopback.contains(host), scheme == "http" else { throw ClientError.origin }
        var normalized = URLComponents()
        normalized.scheme = scheme
        normalized.host = "127.0.0.1"
        normalized.port = parts.port == 80 ? nil : parts.port
        guard let url = normalized.url else { throw ClientError.origin }
        self.url = url
    }

    private static let hostBytes = Set("abcdefghijklmnopqrstuvwxyz0123456789.-".utf8)
    private static let loopback = ["localhost", "127.0.0.1"]
    #endif

    static func saved(_ origin: String) -> Endpoint? {
        if let production = try? Endpoint(origin) { return production }
        #if DEBUG
        if let development = try? Endpoint(developmentOrigin: origin), development.origin == origin { return development }
        #endif
        return nil
    }
}

enum ClientError: LocalizedError, Equatable {
    case origin, unauthorized, response(Int), malformed, network, tls, keychain

    var errorDescription: String? {
        switch self {
        case .origin: "Use the exact https://<device>.<tailnet>.ts.net origin, without a port or path. Manual development connections must use loopback."
        case .unauthorized: "The server rejected the Marten credential."
        case .response(let status): "The server returned HTTP \(status). Redirects are not followed."
        case .malformed: "The server returned an unexpected response."
        case .network: "Could not reach the server. Check the network and Tailscale connection."
        case .tls: "Could not verify the server's HTTPS connection. Check the address and certificate."
        case .keychain: "Could not access Marten credentials in Keychain."
        }
    }
}

protocol Transport: Sendable {
    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

struct URLSessionTransport: Transport {
    private let session: URLSession

    init() {
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = 15
        config.timeoutIntervalForResource = 30
        self.session = URLSession(configuration: config, delegate: NoRedirects(), delegateQueue: nil)
    }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        // Default URLSession trust evaluation (ATS, chain, expiry, hostname); no trust override.
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw ClientError.malformed }
        return (data, http)
    }
}

private final class NoRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

struct ServerStatus: Decodable, Sendable {
    let projects: [Project]
    let agents: [Agent]
}

struct Project: Decodable, Identifiable, Sendable {
    let id: String
    let name: String
}

struct Agent: Decodable, Identifiable, Sendable {
    let id: String
    let name: String
    let status: String
    let kind: String
    let projectId: String?
    let closed: Bool?
    let paused: Bool
    let pausedEffective: Bool
}

struct Entry: Decodable, Identifiable, Sendable {
    let seq: Int64
    let kind: String
    let text: String?
    let from: String?
    let name: String?
    let reason: String?
    var id: Int64 { seq }
}

struct TranscriptPage: Decodable, Sendable {
    let entries: [Entry]
    let hasOlder: Bool
}

enum SendMode: String, CaseIterable, Identifiable, Encodable {
    case queued, immediate
    var id: Self { self }
}

struct API: Sendable {
    let endpoint: Endpoint
    var token: String? = nil
    let transport: any Transport

    private struct Message: Encodable { let text: String; let mode: SendMode }
    private struct Pause: Encodable { let paused: Bool }

    static func validToken(_ token: String) -> Bool {
        !token.isEmpty && token.utf8.count <= 8192 && token.utf8.allSatisfy { (33...126).contains($0) }
    }

    func status() async throws -> ServerStatus {
        try await decode(ServerStatus.self, call("GET", "/api/status"))
    }

    func transcript(_ agent: String, before: Int64? = nil) async throws -> TranscriptPage {
        var query = [URLQueryItem(name: "limit", value: "80")]
        if let before { query.append(.init(name: "before", value: String(before))) }
        return try await decode(TranscriptPage.self, call("GET", route(agent, "transcript"), query: query))
    }

    func send(_ agent: String, text: String, mode: SendMode) async throws {
        _ = try await call("POST", route(agent, "messages"), body: JSONEncoder().encode(Message(text: text, mode: mode)))
    }

    func pause(_ agent: String, paused: Bool) async throws {
        _ = try await call("POST", route(agent, "pause"), body: JSONEncoder().encode(Pause(paused: paused)))
    }

    func interrupt(_ agent: String) async throws {
        _ = try await call("POST", route(agent, "interrupt"))
    }

    private func route(_ agent: String, _ action: String) throws -> String {
        let permitted = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        guard !agent.isEmpty, agent.unicodeScalars.allSatisfy(permitted.contains) else { throw ClientError.malformed }
        return "/api/agents/\(agent)/\(action)"
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        do { return try JSONDecoder().decode(type, from: data) }
        catch { throw ClientError.malformed }
    }

    private func call(_ method: String, _ path: String, body: Data? = nil, query: [URLQueryItem] = []) async throws -> Data {
        var url = URLComponents(url: endpoint.url, resolvingAgainstBaseURL: false)!
        url.path = path
        if !query.isEmpty { url.queryItems = query }
        var request = URLRequest(url: url.url!)
        request.httpMethod = method
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let (data, response): (Data, HTTPURLResponse)
        do {
            (data, response) = try await transport.data(for: request)
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError {
            switch error.code {
            case .cancelled: throw CancellationError()
            case .secureConnectionFailed, .serverCertificateHasBadDate, .serverCertificateUntrusted,
                 .serverCertificateHasUnknownRoot, .serverCertificateNotYetValid, .appTransportSecurityRequiresSecureConnection:
                throw ClientError.tls
            default: throw ClientError.network
            }
        } catch {
            throw ClientError.network
        }
        guard (200..<300).contains(response.statusCode) else {
            if [401, 403].contains(response.statusCode) { throw ClientError.unauthorized }
            // Never surface response bodies, request URLs or underlying transport errors.
            throw ClientError.response(response.statusCode)
        }
        return data
    }
}
