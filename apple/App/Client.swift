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

    // Standalone HTTPS is chosen explicitly; it must not loosen the embedded origin check.
    init(directHTTPSOrigin origin: String) throws {
        let prefix = "https://"
        guard origin.hasPrefix(prefix), origin.utf8.count <= 267 else { throw ClientError.origin }
        let authority = origin.dropFirst(prefix.count).split(separator: ":", omittingEmptySubsequences: false)
        guard let host = authority.first, (1...2).contains(authority.count), host.utf8.count <= 253,
              host != "localhost", !host.hasSuffix(".localhost"),
              host.split(separator: ".", omittingEmptySubsequences: false).allSatisfy(Self.isDNSLabel),
              let url = URL(string: origin), url.absoluteString == origin else { throw ClientError.origin }
        if authority.count == 2 {
            guard let port = Int(authority[1]), (1...65535).contains(port), port != 443,
                  authority[1] == String(port) else { throw ClientError.origin }
        }
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
    case pairOffer, pairExpired, enrollmentUnavailable, deviceName, tailnetState, tailnetNotReady, image
    case pairingAlreadySaved, forgetPending, nodeCloseFailed
    case attachment, processLog, notebookTooLarge, memoryTooLarge

    var errorDescription: String? {
        switch self {
        case .origin: "Use the exact https://<device>.<tailnet>.ts.net origin, without a port or path. Manual development connections must use loopback."
        case .unauthorized: "The server rejected the Marten credential."
        case .pairOffer: "This is not a supported pairing QR for the selected connection mode."
        case .pairExpired: "This pairing QR has expired. Get a new one from the server."
        case .image: "Could not read one pairing QR. Try scanning again or importing a different image."
        case .attachment: "This attachment is not a supported image or exceeds the preview limit."
        case .processLog: "The process log is not plain text or exceeds the display limit."
        case .notebookTooLarge: "The notebook response exceeds the 32 MiB limit."
        case .memoryTooLarge: "Project memory and history exceed the 8 MiB display limit."
        case .enrollmentUnavailable: "Embedded Tailscale enrollment is unavailable in this build."
        case .tailnetState: "Could not access private Tailscale device state."
        case .pairingAlreadySaved: "This server is already saved. To pair again, forget it first and get a new pairing QR."
        case .forgetPending: "Finish forgetting this server before connecting or pairing it again."
        case .nodeCloseFailed: "Could not confirm that the local Tailscale node stopped. Quit and reopen Marten, then try Forget again."
        case .tailnetNotReady: "The embedded Tailscale connection is not ready."
        case .deviceName: "Use a device name of 1–80 characters with no control characters."
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
    func boundedData(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse)
}

extension Transport {
    func boundedData(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        let (body, response) = try await data(for: request)
        guard body.count <= limit else { throw ClientError.attachment }
        return (body, response)
    }
}

struct EventStream: Sendable {
    let hints: AsyncThrowingStream<Void, any Error>
    let cancel: @Sendable () -> Void
    var activity: AsyncStream<[String: LiveActivity]>? = nil
}

protocol EventTransport: Transport {
    func events(for request: URLRequest) throws -> EventStream
}

struct URLSessionTransport: EventTransport {
    private let session: URLSession
    private let notebookSession: URLSession
    static let notebookRerunTimeout: TimeInterval = 180

    init(configuration config: URLSessionConfiguration = .ephemeral) {
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = 15
        config.timeoutIntervalForResource = 30
        self.session = URLSession(configuration: config, delegate: NoRedirects(), delegateQueue: nil)
        // Same proxy/trust/redirect policy, with room for the server's default 120-second run.
        let longer = config.copy() as! URLSessionConfiguration
        longer.timeoutIntervalForRequest = Self.notebookRerunTimeout
        longer.timeoutIntervalForResource = Self.notebookRerunTimeout
        self.notebookSession = URLSession(configuration: longer, delegate: NoRedirects(), delegateQueue: nil)
    }

    func events(for request: URLRequest) throws -> EventStream {
        let socket = session.webSocketTask(with: request)
        socket.maximumMessageSize = 1_048_576
        let (hints, continuation) = AsyncThrowingStream<Void, any Error>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let (activity, updates) = AsyncStream<[String: LiveActivity]>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let reader = Task {
            var live = LiveEvents()
            socket.resume()
            defer { updates.finish(); socket.cancel(with: .goingAway, reason: nil) }
            do {
                while !Task.isCancelled {
                    let frame = try await socket.receive()
                    let payload: Data
                    switch frame {
                    case .string(let text): payload = Data(text.utf8)
                    case .data(let data): payload = data
                    @unknown default: continue
                    }
                    let change = live.receive(payload)
                    if change.updated { updates.yield(live.agents) }
                    if change.invalidate { continuation.yield(()) }
                }
                continuation.finish()
            } catch { continuation.finish(throwing: ClientError.network) }
        }
        let cancel: @Sendable () -> Void = {
            reader.cancel()
            socket.cancel(with: .goingAway, reason: nil)
        }
        continuation.onTermination = { @Sendable _ in cancel() }
        return EventStream(hints: hints, cancel: cancel, activity: activity)
    }

    func boundedData(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        let route = request.timeoutInterval == Self.notebookRerunTimeout ? notebookSession : session
        let (bytes, response) = try await route.bytes(for: request)
        defer { bytes.task.cancel() }
        guard let http = response as? HTTPURLResponse else { throw ClientError.malformed }
        guard (200..<300).contains(http.statusCode) else { return (Data(), http) }
        guard http.expectedContentLength <= Int64(limit) else { throw ClientError.attachment }
        var data = Data()
        for try await byte in bytes {
            guard data.count < limit else { throw ClientError.attachment }
            data.append(byte)
        }
        return (data, http)
    }

    func invalidate() {
        session.invalidateAndCancel()
        notebookSession.invalidateAndCancel()
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
    struct Config: Decodable, Sendable { let model: String?; let defaultModel: String?; let effort: String? }
    let config: Config?
    let projects: [Project]
    let agents: [Agent]
    let inbox: Int?
    let openQuestions: Int?
    let paused: Bool?
}

struct Project: Decodable, Identifiable, Sendable {
    let id: String
    let name: String
    let bindings: [String]?
    let worktreesByDefault: Bool?
    let orchestratorId: String?
    let paused: Bool?
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
    let workingDirectory: String?
    let worktree: String?
    let model: String?
    let effort: String?
    let parentId: String?
    let pending: Int?
    let openTasks: Int?
    let statusDetail: String?
    let retryAt: String?
    let modelCall: ModelCall?
    let nudgesStopped: Bool?
    let abandonedRunspaces: [AbandonedRunspace]?

    struct ModelCall: Decodable, Sendable {
        let model: String
        let kind: String
        let startedAt: String
        let endedAt: String?
        let outcome: String?
    }
    struct AbandonedRunspace: Decodable, Sendable { let at: String; let command: String }

    var canStartFreshContext: Bool {
        !pausedEffective && !["paused", "running", "waiting_for_quota", "waiting_for_network", "waiting_for_slot"].contains(status)
    }
}

struct Entry: Decodable, Identifiable, Sendable {
    let seq: Int64
    let kind: String
    let text: String?
    let from: String?
    let name: String?
    let reason: String?
    var id: Int64 { seq }
    private(set) var ts: String? = nil
    private(set) var callId: String? = nil
    private(set) var arguments: String? = nil
    private(set) var isError: Bool? = nil
    private(set) var seqs: [Int64]? = nil
    private(set) var model: String? = nil
    private(set) var images: [TranscriptImage]? = nil
}

struct TranscriptImage: Decodable, Hashable, Sendable {
    let path: String
    let mimeType: String

    func resource() throws -> (name: String, mime: String) {
        let name = path.components(separatedBy: CharacterSet(charactersIn: "/\\")).last ?? ""
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.")
        guard !name.isEmpty, !name.hasPrefix("."), name.utf8.count <= 255,
              name.unicodeScalars.allSatisfy(allowed.contains) else { throw ClientError.attachment }
        let mime = ["png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "gif": "image/gif", "webp": "image/webp"][(name as NSString).pathExtension.lowercased()]
        guard let mime, mime == mimeType else { throw ClientError.attachment }
        return (name, mime)
    }
}

struct InboxMessage: Decodable, Identifiable, Sendable, Equatable {
    let id: String
    let at: String
    let from: String
    let fromName: String
    let projectId: String?
    let text: String
    let question: Bool?
    let options: [String]?
    let context: String?
    let answer: String?
    let answeredAt: String?

    let kind: String?
    let reference: String?

    var isToolDecision: Bool { kind == "approval" || kind == "tool_review" }
    var isOpen: Bool { question == true && answer == nil }

    static func date(_ stamp: String) -> Date? {
        (try? Date(stamp, strategy: Date.ISO8601FormatStyle(includingFractionalSeconds: true)))
            ?? (try? Date(stamp, strategy: .iso8601))
    }
}

struct AgentMessage: Decodable, Hashable, Sendable {
    let at: String
    let fromId: String
    let fromName: String
    let toId: String
    let toName: String
    let projectId: String?
    let mode: String
    let text: String
    let reason: String?
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

    func redeem(code: String, deviceName: String) async throws -> API {
        struct Redemption: Encodable { let code: String; let deviceName: String }
        struct Credential: Decodable { let token: String }
        // Construct an unauthenticated API even if called on an already paired client.
        let bootstrap = API(endpoint: endpoint, transport: transport)
        let body = try JSONEncoder().encode(Redemption(code: code, deviceName: deviceName))
        let credential = try await bootstrap.decode(Credential.self, bootstrap.call("POST", "/api/pair/redeem", body: body))
        guard Self.validToken(credential.token) else { throw ClientError.malformed }
        return API(endpoint: endpoint, token: credential.token, transport: transport)
    }

    func events() throws -> EventStream? {
        guard let transport = transport as? TailnetTransport, let token, Self.validToken(token),
              var url = URLComponents(url: endpoint.url, resolvingAgainstBaseURL: false), url.scheme == "https" else { return nil }
        url.scheme = "wss"
        url.path = "/api/events"
        guard let resource = url.url else { throw ClientError.origin }
        var request = URLRequest(url: resource)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return try transport.events(for: request)
    }

    func status() async throws -> ServerStatus {
        try await decode(ServerStatus.self, call("GET", "/api/status"))
    }

    func image(_ ref: TranscriptImage, agent: String) async throws -> Data {
        guard let token, Self.validToken(token) else { throw ClientError.unauthorized }
        let resource = try ref.resource()
        let payload = try await response("GET", route(agent, "images/\(resource.name)"), accept: resource.mime, limit: 12 * 1024 * 1024)
        guard payload.1.mimeType?.lowercased() == resource.mime else { throw ClientError.attachment }
        return payload.0
    }

    func tasks(_ agent: String) async throws -> [AgentTask] {
        try requireBearer()
        return try await decode([AgentTask].self, call("GET", route(agent, "tasks")))
    }

    func schedules(_ agent: String) async throws -> [AgentSchedule] {
        try requireBearer()
        return try await decode([AgentSchedule].self, call("GET", route(agent, "schedules")))
    }

    func processes(_ agent: String? = nil) async throws -> [AgentProcess] {
        try requireBearer()
        let path = try agent.map { try route($0, "processes") } ?? "/api/processes"
        return try await decode([AgentProcess].self, call("GET", path))
    }

    func removeSchedule(_ agent: String, _ id: Int) async throws {
        try requireBearer()
        guard id > 0 else { throw ClientError.malformed }
        _ = try await call("DELETE", route(agent, "schedules/\(id)"))
    }

    func stopProcess(_ key: AgentProcess.Key) async throws {
        try requireBearer()
        guard key.number > 0 else { throw ClientError.malformed }
        _ = try await call("POST", route(key.agent, "processes/\(key.number)/stop"))
    }

    func processLog(_ key: AgentProcess.Key) async throws -> String {
        try requireBearer()
        guard key.number > 0 else { throw ClientError.malformed }
        do {
            let (data, reply) = try await response("GET", route(key.agent, "processes/\(key.number)/log"),
                                                   query: [.init(name: "lines", value: "200")], accept: "text/plain", limit: 1024 * 1024)
            guard reply.mimeType == "text/plain" else { throw ClientError.processLog }
            return String(decoding: data, as: UTF8.self)
        } catch ClientError.attachment {
            throw ClientError.processLog
        }
    }

    func notebook(_ agent: String) async throws -> [NotebookCell] {
        let (data, _) = try await notebookResponse("GET", route(agent, "notebook"), query: [.init(name: "limit", value: "300")])
        return try decode([NotebookCell].self, data)
    }

    func rerunCell(_ agent: String, _ cell: Int) async throws -> NotebookReplay {
        guard cell > 0 else { throw ClientError.malformed }
        let (data, _) = try await notebookResponse("POST", route(agent, "notebook/\(cell)/rerun"), timeout: URLSessionTransport.notebookRerunTimeout)
        return try decode(NotebookReplay.self, data)
    }

    func notebookFile(_ agent: String) async throws -> Data {
        let mime = "application/x-ipynb+json"
        let (data, reply) = try await notebookResponse("GET", route(agent, "notebook.ipynb"), accept: mime)
        guard reply.mimeType == mime else { throw ClientError.malformed }
        _ = try decode(NotebookFileHeader.self, data)
        return data
    }

    private func notebookResponse(_ method: String, _ path: String, query: [URLQueryItem] = [],
                                  accept: String = "application/json", timeout: TimeInterval? = nil) async throws -> (Data, HTTPURLResponse) {
        try requireBearer()
        try Task.checkCancellation()
        do {
            let result = try await response(method, path, query: query, accept: accept, limit: 32 * 1024 * 1024, timeout: timeout)
            try Task.checkCancellation()
            return result
        } catch ClientError.attachment {
            throw ClientError.notebookTooLarge
        }
    }

    private func requireBearer() throws {
        guard let token, Self.validToken(token) else { throw ClientError.unauthorized }
    }

    func agentMessages(projectID: String?) async throws -> [AgentMessage] {
        guard let token, Self.validToken(token) else { throw ClientError.unauthorized }
        let query = projectID.flatMap { $0.isEmpty ? nil : URLQueryItem(name: "projectId", value: $0) }
        return try await decode([AgentMessage].self, call("GET", "/api/messages", query: query.map { [$0] } ?? []))
    }

    func inbox() async throws -> [InboxMessage] {
        guard let token, Self.validToken(token) else { throw ClientError.unauthorized }
        return try await decode([InboxMessage].self, call("GET", "/api/inbox"))
    }

    func answer(_ question: InboxMessage, choice: Int?, text: String) async throws -> InboxMessage {
        struct Answer: Encodable {
            let choice: Int?
            let text: String?
            enum CodingKeys: String, CodingKey { case choice, text }
            func encode(to encoder: any Encoder) throws {
                var keys = encoder.container(keyedBy: CodingKeys.self)
                try keys.encode(choice, forKey: .choice)
                try keys.encode(text, forKey: .text)
            }
        }
        guard let token, Self.validToken(token) else { throw ClientError.unauthorized }
        let words = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard question.isOpen, choice != nil || !words.isEmpty else { throw ClientError.malformed }
        if question.isToolDecision {
            guard choice == 0 || choice == 1, question.options?.count == 2,
                  question.context?.isEmpty == false else { throw ClientError.malformed }
        }
        if let choice, question.options?.indices.contains(choice) != true { throw ClientError.malformed }
        try Self.checkRouteID(question.id)
        let body = try JSONEncoder().encode(Answer(choice: choice, text: words.isEmpty ? nil : words))
        let saved = try await decode(InboxMessage.self, call("POST", "/api/questions/\(question.id)/answer", body: body))
        guard saved.id == question.id, saved.question == true, saved.answer != nil else { throw ClientError.malformed }
        return saved
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

    func scriptTools(project id: String) async throws -> [ReviewedTool] {
        try requireBearer()
        try Self.checkRouteID(id)
        return try decode([ReviewedTool].self, await call("GET", "/api/tools", query: [.init(name: "projectId", value: id)]))
    }

    func models() async throws -> [String] {
        try requireBearer()
        return try decode([String].self, await call("GET", "/api/models"))
    }

    func updateSettings(_ id: String, settings: AgentSettings) async throws {
        try requireBearer()
        _ = try await call("PUT", route(id, "settings"), body: JSONEncoder().encode(settings))
    }

    func projectMemory(_ id: String) async throws -> ProjectMemory {
        try requireBearer()
        try Self.checkRouteID(id)
        do {
            let (data, _) = try await response("GET", "/api/projects/\(id)/memory", limit: 8 * 1024 * 1024)
            return try decode(ProjectMemory.self, data)
        } catch ClientError.attachment { throw ClientError.memoryTooLarge }
    }

    func createProject(_ project: ProjectCreation) async throws -> Project {
        try requireBearer()
        return try decode(Project.self, await call("POST", "/api/projects", body: JSONEncoder().encode(project)))
    }

    func createChat(_ name: String) async throws -> Agent {
        struct Chat: Encodable { let name: String }
        try requireBearer()
        return try decode(Agent.self, await call("POST", "/api/agents", body: JSONEncoder().encode(Chat(name: name))))
    }

    func applyLifecycle(_ action: LifecycleAction) async throws {
        struct Close: Encodable { let closed: Bool }
        try requireBearer()
        switch action {
        case .pauseAll(let paused):
            _ = try await call("POST", "/api/pause", body: JSONEncoder().encode(Pause(paused: paused)))
        case .pauseProject(let id, let paused):
            try Self.checkRouteID(id)
            _ = try await call("POST", "/api/projects/\(id)/pause", body: JSONEncoder().encode(Pause(paused: paused)))
        case .pauseAgent(let id, let paused):
            try await pause(id, paused: paused)
        case .close(let id, let closed):
            _ = try await call("POST", route(id, "close"), body: JSONEncoder().encode(Close(closed: closed)))
        case .freshContext(let id):
            _ = try await call("POST", route(id, "start-fresh-context"))
        }
    }

    private func route(_ agent: String, _ action: String) throws -> String {
        try Self.checkRouteID(agent)
        return "/api/agents/\(agent)/\(action)"
    }

    private static func checkRouteID(_ id: String) throws {
        let permitted = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        guard !id.isEmpty, id.unicodeScalars.allSatisfy(permitted.contains) else { throw ClientError.malformed }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        do { return try JSONDecoder().decode(type, from: data) }
        catch { throw ClientError.malformed }
    }

    private func call(_ method: String, _ path: String, body: Data? = nil, query: [URLQueryItem] = []) async throws -> Data {
        try await response(method, path, body: body, query: query).0
    }

    private func response(_ method: String, _ path: String, body: Data? = nil, query: [URLQueryItem] = [],
                          accept: String = "application/json", limit: Int? = nil, timeout: TimeInterval? = nil) async throws -> (Data, HTTPURLResponse) {
        var url = URLComponents(url: endpoint.url, resolvingAgainstBaseURL: false)!
        url.path = path
        if !query.isEmpty { url.queryItems = query }
        var request = URLRequest(url: url.url!)
        request.httpMethod = method
        request.httpBody = body
        if let timeout { request.timeoutInterval = timeout }
        request.setValue(accept, forHTTPHeaderField: "Accept")
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let (data, response): (Data, HTTPURLResponse)
        do {
            if let limit { (data, response) = try await transport.boundedData(for: request, limit: limit) }
            else { (data, response) = try await transport.data(for: request) }
        } catch let error as ClientError {
            throw error
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
        return (data, response)
    }
}
