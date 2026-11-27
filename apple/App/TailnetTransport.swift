import CryptoKit
import Foundation

/// Applies an exact authority check before handing a request to the proxy,
/// including rejecting loopback names URLSession may bypass the proxy for.
struct TailnetTransport: EventTransport, ArtifactByteTransport, MacFilesTransport {
    private let endpoint: Endpoint
    private let transport: any Transport

    init(endpoint: Endpoint, transport: any Transport) throws {
        self.endpoint = try Endpoint(endpoint.origin)
        self.transport = transport
    }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        try check(request, events: false)
        return try await transport.data(for: request)
    }

    func boundedData(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        try check(request, events: false)
        return try await transport.boundedData(for: request, limit: limit)
    }

    func supportsMacFiles(at endpoint: Endpoint) -> Bool {
        self.endpoint == endpoint && transport is any MacFilesTransport
    }

    func readMacFiles(_ request: URLRequest, policy: MacFilesRead, staging: URL?) async throws -> Data {
        try check(request, events: false)
        guard let url = request.url, url.path.hasPrefix("/api/mac-files/"), url.query == nil,
              request.httpMethod == "GET", request.httpBody == nil, request.httpBodyStream == nil else {
            throw ClientError.origin
        }
        guard let header = request.value(forHTTPHeaderField: "Authorization"), header.hasPrefix("Bearer "),
              API.validToken(String(header.dropFirst(7))) else { throw ClientError.unauthorized }
        guard let reader = transport as? any MacFilesTransport else { throw ClientError.tailnetNotReady }
        return try await reader.readMacFiles(request, policy: policy, staging: staging)
    }

    func artifactBytes(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        try check(request, events: false)
        guard request.httpMethod == "GET", request.url?.query == nil,
              let bearer = request.value(forHTTPHeaderField: "Authorization"), bearer.hasPrefix("Bearer "),
              API.validToken(String(bearer.dropFirst(7))) else { throw ClientError.unauthorized }
        guard let stream = transport as? any ArtifactByteTransport else { throw ClientError.tailnetNotReady }
        let (bytes, response) = try await stream.artifactBytes(for: request, limit: limit)
        guard response.url?.absoluteString == request.url?.absoluteString else { throw ClientError.origin }
        return (bytes, response)
    }

    func events(for request: URLRequest) throws -> EventStream {
        try check(request, events: true)
        guard let header = request.value(forHTTPHeaderField: "Authorization"), header.hasPrefix("Bearer "),
              API.validToken(String(header.dropFirst(7))) else { throw ClientError.unauthorized }
        guard let stream = transport as? any EventTransport else { throw ClientError.tailnetNotReady }
        return try stream.events(for: request)
    }

    private func check(_ request: URLRequest, events: Bool) throws {
        guard let url = request.url, var parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              parts.fragment == nil, request.value(forHTTPHeaderField: "Host") == nil,
              parts.scheme == (events ? "wss" : "https") else { throw ClientError.origin }
        if events && (parts.percentEncodedPath != "/api/events" || parts.query != nil) { throw ClientError.origin }
        parts.scheme = "https"
        parts.percentEncodedPath = ""
        parts.percentEncodedQuery = nil
        guard parts.string == endpoint.origin else { throw ClientError.origin }
    }
}

/// Keep node identity and its cache out of preferences and Keychain. The only
/// marker is a zero-byte file written after successful redemption/persistence.
struct TailnetState {
    let root: URL

    static func applicationSupport() throws -> Self {
        guard let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            throw ClientError.tailnetState
        }
        return Self(root: support.appending(path: "dev.marten.native/TailnetNodes", directoryHint: .isDirectory))
    }

    func directory(for endpoint: Endpoint) throws -> URL {
        let canonical = try Endpoint(endpoint.origin)
        let digest = SHA256.hash(data: Data(canonical.origin.utf8)).map { String(format: "%02x", $0) }.joined()
        return root.appending(path: digest, directoryHint: .isDirectory)
    }

    func prepareNew(_ directory: URL) throws {
        var attributes: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
        #if os(iOS)
        attributes[.protectionKey] = FileProtectionType.completeUntilFirstUserAuthentication
        #endif
        do {
            let files = FileManager.default
            try files.createDirectory(at: root, withIntermediateDirectories: true, attributes: attributes)
            var root = root
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            try root.setResourceValues(values)
            try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: attributes)
        } catch { throw ClientError.tailnetState }
    }

    func markReady(_ directory: URL) throws {
        do { try Data().write(to: marker(directory), options: [.withoutOverwriting]) }
        catch { throw ClientError.tailnetState }
    }

    func isReady(_ directory: URL) -> Bool { FileManager.default.fileExists(atPath: marker(directory).path) }

    /// Call only once no live node can write this directory.
    func removeNode(for endpoint: Endpoint) throws {
        let directory = try directory(for: endpoint)
        do { try FileManager.default.removeItem(at: directory) }
        catch let error as CocoaError where error.code == .fileNoSuchFile { /* Already absent. */ }
        catch { throw ClientError.tailnetState }
    }

    private func marker(_ directory: URL) -> URL { directory.appending(path: ".marten-ready", directoryHint: .notDirectory) }
}

#if MARTEN_EMBEDDED_TAILNET
import Network
import TailscaleKit

/// Owns one userspace node per endpoint. Callers must never wire reconnect to
/// system URLSession. A stale-proxy report invalidates the session and reopens
/// the same private identity with no auth key, without replaying the request.
actor TailscaleKitEnrollment: ManagedEnrollment {
    private struct Node {
        let id = UUID()
        let node: TailscaleNode
        let session: URLSessionTransport
        let transport: TailnetTransport
        let proxyPort: NWEndpoint.Port
        let directory: URL
    }

    private let state: TailnetState
    private var active: [Endpoint: Node] = [:]
    private var inFlight: Set<Endpoint> = []
    // At the pinned SDK, close consumes the handle even on error. An
    // ambiguous close must not be followed by deletion or reopening until
    // process exit, when there can no longer be a writer in this process.
    private var failedClose: Set<Endpoint> = []

    init(state: TailnetState) { self.state = state }

    func enroll(authKey: String, endpoint: Endpoint, deadline: Date) async throws -> any Transport {
        try begin(endpoint)
        defer { inFlight.remove(endpoint) }
        let directory = try state.directory(for: endpoint)
        guard active[endpoint] == nil else { throw ClientError.tailnetState }
        try state.prepareNew(directory)
        do {
            let node = try await open(endpoint, directory: directory, authKey: authKey, deadline: deadline)
            active[endpoint] = node
            return node.transport
        } catch {
            if !failedClose.contains(endpoint) { try? state.removeNode(for: endpoint) }
            throw error
        }
    }

    // Called once the confirmed handshake and its Keychain write both succeeded.
    func commitEnrollment(for endpoint: Endpoint) throws {
        guard !inFlight.contains(endpoint), let node = active[endpoint] else { throw ClientError.tailnetState }
        try state.markReady(node.directory)
    }

    func discardEnrollment(for endpoint: Endpoint) async {
        guard !inFlight.contains(endpoint), let node = active[endpoint], !state.isReady(node.directory) else { return }
        inFlight.insert(endpoint)
        defer { inFlight.remove(endpoint) }
        do {
            try await stopNode(for: endpoint)
            try state.removeNode(for: endpoint)
        } catch { /* Best-effort rollback; never delete under an unconfirmed close. */ }
    }

    func forgetEnrollment(for endpoint: Endpoint) async throws {
        _ = try state.directory(for: endpoint) // Reject any noncanonical origin.
        try begin(endpoint)
        defer { inFlight.remove(endpoint) }
        try await stopNode(for: endpoint)
        try state.removeNode(for: endpoint)
    }

    func connectedTransport(for endpoint: Endpoint) async throws -> any Transport {
        try await connect(endpoint, replaceStaleProxy: false)
    }

    func recoverOnForeground(for endpoint: Endpoint) async throws -> (any Transport)? {
        guard let cached = active[endpoint] else { return try await connectedTransport(for: endpoint) }
        let route = NWConnection(host: "127.0.0.1", port: cached.proxyPort, using: .tcp)
        let updates = AsyncStream<Bool?> { continuation in
            route.stateUpdateHandler = { state in
                switch state {
                case .ready: continuation.yield(true)
                case .failed(.posix(.ECONNREFUSED)), .waiting(.posix(.ECONNREFUSED)): continuation.yield(false)
                case .failed, .cancelled: continuation.yield(nil)
                default: break
                }
            }
        }
        route.start(queue: .global(qos: .utility))
        defer { route.cancel() }
        let alive = await withTaskGroup(of: Bool?.self) { group in
            group.addTask { for await result in updates { return result }; return nil }
            group.addTask { try? await Task.sleep(for: .seconds(2)); return nil }
            defer { group.cancelAll() }
            return await group.next() ?? nil
        }
        try Task.checkCancellation()
        // A concurrent recovery may have superseded the listener we probed.
        guard !inFlight.contains(endpoint), let current = active[endpoint] else { throw ClientError.tailnetNotReady }
        if current.id != cached.id { return current.transport }
        guard let alive else { throw ClientError.tailnetNotReady }
        guard !alive else { return nil }
        return try await connect(endpoint, replaceStaleProxy: true)
    }

    private func connect(_ endpoint: Endpoint, replaceStaleProxy: Bool) async throws -> any Transport {
        try begin(endpoint)
        defer { inFlight.remove(endpoint) }
        let directory = try state.directory(for: endpoint)
        guard state.isReady(directory) else { throw ClientError.tailnetState }
        if let cached = active[endpoint] {
            if !replaceStaleProxy { return cached.transport }
            try await stopNode(for: endpoint)
        }
        let node = try await open(endpoint, directory: directory, authKey: nil, deadline: .now.addingTimeInterval(25))
        active[endpoint] = node
        return node.transport
    }

    private func begin(_ endpoint: Endpoint) throws {
        guard !failedClose.contains(endpoint) else { throw ClientError.nodeCloseFailed }
        guard inFlight.insert(endpoint).inserted else { throw ClientError.tailnetNotReady }
    }

    private func stopNode(for endpoint: Endpoint) async throws {
        guard let entry = active.removeValue(forKey: endpoint) else { return }
        entry.session.invalidate() // Stop both HTTP requests and WSS reads.
        try await close(entry.node, for: endpoint)
    }

    private func close(_ node: TailscaleNode, for endpoint: Endpoint) async throws {
        do { try await node.close() }
        catch {
            failedClose.insert(endpoint)
            throw ClientError.nodeCloseFailed
        }
    }

    private func open(_ endpoint: Endpoint, directory: URL, authKey: String?, deadline: Date) async throws -> Node {
        struct Status: Decodable { let BackendState: String }
        try Task.checkCancellation()
        let seconds = min(25, deadline.timeIntervalSinceNow)
        guard seconds > 0 else { throw ClientError.tailnetNotReady }
        let clock = ContinuousClock()
        let stop = clock.now.advanced(by: .seconds(seconds))
        // Start initializes immediately. Poll the in-memory status rather than
        // blocking in up(); close on timeout. Do not call down() at this pin.
        let node: TailscaleNode
        do {
            node = try TailscaleNode(config: Configuration(hostName: "marten-client-" + String(directory.lastPathComponent.prefix(10)),
                                                          path: directory.path, authKey: authKey,
                                                          controlURL: kDefaultControlURL), logger: SilentTailnetLog())
        } catch { throw ClientError.tailnetNotReady }
        do {
            while clock.now < stop && Date.now < deadline {
                try Task.checkCancellation()
                if let data = try? await node.statusJSON(), clock.now < stop, Date.now < deadline,
                   (try? JSONDecoder().decode(Status.self, from: data).BackendState) == "Running" {
                    let proxyInfo = try await node.loopback()
                    try Task.checkCancellation()
                    guard proxyInfo.ip == "127.0.0.1", let port = proxyInfo.port,
                          let portNumber = UInt16(exactly: port), let nwPort = NWEndpoint.Port(rawValue: portNumber),
                          portNumber != 0, !proxyInfo.proxyCredential.isEmpty else { throw ClientError.tailnetNotReady }
                    var proxy = ProxyConfiguration(socksv5Proxy: .hostPort(host: "127.0.0.1", port: nwPort))
                    proxy.applyCredential(username: "tsnet", password: proxyInfo.proxyCredential)
                    proxy.allowFailover = false
                    let config = URLSessionConfiguration.ephemeral
                    config.proxyConfigurations = [proxy]
                    let session = URLSessionTransport(configuration: config)
                    return Node(node: node, session: session, transport: try TailnetTransport(endpoint: endpoint, transport: session), proxyPort: nwPort, directory: directory)
                }
                try await clock.sleep(for: .milliseconds(250))
            }
            throw ClientError.tailnetNotReady
        } catch {
            try await close(node, for: endpoint)
            if error is CancellationError { throw CancellationError() }
            throw ClientError.tailnetNotReady
        }
    }
}

private struct SilentTailnetLog: LogSink {
    var logFileHandle: Int32? { -1 } // Pinned library maps -1 to its discard logger.
    func log(_ message: String) {}
}
#endif
