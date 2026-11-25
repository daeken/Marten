import Foundation
import Observation
import Security

struct CredentialStore {
    var service = "dev.marten.native.endpoint"

    private func query(_ endpoint: Endpoint) -> [String: Any] {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: endpoint.origin]
        #if os(iOS)
        query[kSecUseDataProtectionKeychain as String] = true
        query[kSecAttrSynchronizable as String] = false
        #endif
        // The Mac's local login Keychain supports ad-hoc developer builds without provisioning.
        return query
    }

    func token(for endpoint: Endpoint) throws -> String {
        var request = query(endpoint)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { throw ClientError.unauthorized }
        guard status == errSecSuccess, let data = result as? Data,
              let token = String(data: data, encoding: .utf8), API.validToken(token) else { throw ClientError.keychain }
        return token
    }

    func save(_ token: String, for endpoint: Endpoint) throws {
        let request = query(endpoint)
        let data = Data(token.utf8)
        var add = request
        add[kSecValueData as String] = data
        #if os(iOS)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        #endif
        let status = SecItemAdd(add as CFDictionary, nil)
        if status == errSecDuplicateItem {
            let updates = [kSecValueData as String: data]
            guard SecItemUpdate(request as CFDictionary, updates as CFDictionary) == errSecSuccess else { throw ClientError.keychain }
        } else if status != errSecSuccess {
            throw ClientError.keychain
        }
    }

    func remove(_ endpoint: Endpoint) throws {
        let status = SecItemDelete(query(endpoint) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw ClientError.keychain }
    }
}

@MainActor @Observable
final class Connections {
    private let transport: any Transport
    private let keychain: CredentialStore
    private let defaults: UserDefaults
    private let enrollment: (any ManagedEnrollment)?
    private let clock: @Sendable () -> Date
    private(set) var pendingOffer: PairOffer?
    var pairingEnabled: Bool { enrollment != nil }
    private(set) var transportGeneration = UUID()
    private(set) var routeUsable = true
    private(set) var endpoints: [Endpoint]
    private(set) var connected: API?
    // A new workspace identity clears old server state, drafts and in-flight refreshes.
    private(set) var workspaceID = UUID()
    private(set) var busy = false
    var error: String?
    var showServers = false

    init(transport: any Transport = URLSessionTransport(), keychain: CredentialStore = CredentialStore(),
         defaults: UserDefaults = .standard, enrollment: (any ManagedEnrollment)? = nil,
         clock: @escaping @Sendable () -> Date = { .now }) {
        self.enrollment = enrollment
        self.clock = clock
        self.transport = transport
        self.keychain = keychain
        self.defaults = defaults
        endpoints = (defaults.stringArray(forKey: "endpoints") ?? []).compactMap(Endpoint.saved)
        if let last = defaults.string(forKey: "activeEndpoint"), let endpoint = Endpoint.saved(last), endpoints.contains(endpoint) {
            if (try? Endpoint(endpoint.origin)) == nil { connect(endpoint) }
        }
    }

    #if DEBUG
    func connectLoopback(_ origin: String, token: String) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            let endpoint = try Endpoint(developmentOrigin: origin)
            guard API.validToken(token) else { throw ClientError.unauthorized }
            _ = try await API(endpoint: endpoint, token: token, transport: transport).status()
            try remember(token, for: endpoint)
        } catch { show(error) }
    }
    #endif

    func connect(_ endpoint: Endpoint) {
        // No production connection mode is active in this build. In particular,
        // a saved origin from an older build cannot select system networking.
        #if DEBUG
        if (try? Endpoint(developmentOrigin: endpoint.origin)) == endpoint {
            do { activate(endpoint, token: try keychain.token(for: endpoint)) }
            catch { show(error) }
            return
        }
        #endif
        show(ClientError.enrollmentUnavailable)
    }

    // A scan is local and transient; only the confirmation button starts work.
    func scan(_ payload: String) {
        guard pairingEnabled, !busy, pendingOffer == nil else { return }
        do { pendingOffer = try PairOffer(payload, mode: .embedded, now: clock()) }
        catch { show(error) }
    }

    func cancelOffer() { pendingOffer = nil }

    func confirmOffer(deviceName: String) async {
        guard !busy, let enrollment, let offer = pendingOffer else { return }
        let endpoint = offer.endpoint
        pendingOffer = nil // Never retry or retain a consumed offer.
        busy = true
        error = nil
        defer { busy = false }
        do {
            // Do not overwrite a possibly working node/bearer on a new scan.
            guard !endpoints.contains(endpoint) else { throw ClientError.tailnetState }
            let paired = try await PairingHandshake(directTransport: transport, embedded: enrollment)
                .confirm(offer, deviceName: deviceName, clock: clock)
            guard let bearer = paired.token else { throw ClientError.malformed }
            do {
                try keychain.save(bearer, for: endpoint)
                try await enrollment.commitEnrollment(for: endpoint)
            } catch {
                try? keychain.remove(endpoint)
                await enrollment.discardEnrollment(for: endpoint)
                throw error
            }
            endpoints.append(endpoint)
            persistEndpoints()
            activate(paired)
        } catch { show(error) }
    }

    func select(_ endpoint: Endpoint) async {
        guard !busy, endpoints.contains(endpoint) else { return }
        guard let enrollment, (try? Endpoint(endpoint.origin)) == endpoint else { connect(endpoint); return }
        if connected?.endpoint == endpoint {
            await foreground()
            if routeUsable { showServers = false }
            return
        }
        busy = true
        defer { busy = false }
        do {
            let token = try keychain.token(for: endpoint)
            let route = try await enrollment.connectedTransport(for: endpoint)
            try Task.checkCancellation()
            activate(API(endpoint: endpoint, token: token, transport: route))
        } catch { show(error) }
    }

    func foreground() async {
        guard !busy, let enrollment else { return }
        guard let api = connected else {
            if let origin = defaults.string(forKey: "activeEndpoint"), let endpoint = Endpoint.saved(origin), endpoints.contains(endpoint) {
                await select(endpoint)
            }
            return
        }
        guard (try? Endpoint(api.endpoint.origin)) == api.endpoint else { return }
        busy = true
        defer { busy = false }
        do {
            // If the adapter has already swapped its session, publish that
            // session even if the scene just went back to sleep. Only active
            // scenes consume it, and a later probe may see the new node alive.
            if let route = try await enrollment.recoverOnForeground(for: api.endpoint) {
                connected = API(endpoint: api.endpoint, token: api.token, transport: route)
                transportGeneration = UUID()
            }
            routeUsable = true
            if !Task.isCancelled { error = nil }
        } catch {
            routeUsable = false
            if !Task.isCancelled {
                show(error)
                showServers = true
            }
        }
    }

    func disconnect() {
        guard !busy else { return }
        cancelOffer()
        connected = nil
        workspaceID = UUID()
        defaults.removeObject(forKey: "activeEndpoint")
    }

    func forget(_ endpoint: Endpoint) {
        guard !busy else { return }
        do {
            try keychain.remove(endpoint)
            if connected?.endpoint == endpoint { disconnect() }
            endpoints.removeAll { $0 == endpoint }
            persistEndpoints()
        } catch { show(error) }
    }

    func show(_ error: Error) {
        if !(error is CancellationError) { self.error = safeMessage(error) }
    }

    #if DEBUG
    private func remember(_ token: String, for endpoint: Endpoint) throws {
        try keychain.save(token, for: endpoint)
        if !endpoints.contains(endpoint) {
            endpoints.append(endpoint)
            persistEndpoints()
        }
        activate(endpoint, token: token)
    }

    #endif

    private func activate(_ endpoint: Endpoint, token: String) {
        activate(API(endpoint: endpoint, token: token, transport: transport))
    }

    private func activate(_ api: API) {
        defaults.set(api.endpoint.origin, forKey: "activeEndpoint")
        connected = api
        routeUsable = true
        error = nil
        transportGeneration = UUID()
        workspaceID = UUID()
        showServers = false
    }

    private func persistEndpoints() { defaults.set(endpoints.map(\.origin), forKey: "endpoints") }
}

func safeMessage(_ error: Error) -> String {
    (error as? ClientError)?.errorDescription ?? "Marten could not complete the request."
}
