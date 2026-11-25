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
    private(set) var endpoints: [Endpoint]
    private(set) var connected: API?
    // A new workspace identity clears old server state, drafts and in-flight refreshes.
    private(set) var workspaceID = UUID()
    private(set) var busy = false
    var offer: PairOffer?
    var error: String?
    var showServers = false

    init(transport: any Transport = URLSessionTransport(), keychain: CredentialStore = CredentialStore(),
         defaults: UserDefaults = .standard) {
        self.transport = transport
        self.keychain = keychain
        self.defaults = defaults
        endpoints = (defaults.stringArray(forKey: "endpoints") ?? []).compactMap(Endpoint.saved)
        if let last = defaults.string(forKey: "activeEndpoint"), let endpoint = Endpoint.saved(last), endpoints.contains(endpoint) {
            connect(endpoint)
        }
    }

    func receive(_ value: String) {
        guard !busy, offer == nil else { return }
        do { offer = try PairOffer(value) }
        catch { show(error) }
    }

    func redeem(deviceName: String) async {
        guard !busy, let offer else { return }
        self.offer = nil
        busy = true
        defer { busy = false }
        do {
            let token = try await API.redeem(offer, deviceName: deviceName, transport: transport)
            try remember(token, for: offer.endpoint)
        } catch {
            guard !(error is CancellationError) else { return }
            self.error = "\(safeMessage(error)) Get a new pairing QR before retrying."
        }
    }

    #if DEBUG
    func connectLoopback(_ origin: String, token: String) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            let endpoint = try Endpoint(origin, allowLoopback: true)
            guard API.validToken(token) else { throw ClientError.unauthorized }
            _ = try await API(endpoint: endpoint, token: token, transport: transport).status()
            try remember(token, for: endpoint)
        } catch { show(error) }
    }
    #endif

    func connect(_ endpoint: Endpoint) {
        do {
            activate(endpoint, token: try keychain.token(for: endpoint))
        } catch { show(error) }
    }

    func disconnect() {
        connected = nil
        workspaceID = UUID()
        defaults.removeObject(forKey: "activeEndpoint")
    }

    func forget(_ endpoint: Endpoint) {
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

    private func remember(_ token: String, for endpoint: Endpoint) throws {
        try keychain.save(token, for: endpoint)
        if !endpoints.contains(endpoint) {
            endpoints.append(endpoint)
            persistEndpoints()
        }
        activate(endpoint, token: token)
    }

    private func activate(_ endpoint: Endpoint, token: String) {
        defaults.set(endpoint.origin, forKey: "activeEndpoint")
        connected = API(endpoint: endpoint, token: token, transport: transport)
        workspaceID = UUID()
        showServers = false
    }

    private func persistEndpoints() { defaults.set(endpoints.map(\.origin), forKey: "endpoints") }
}

func safeMessage(_ error: Error) -> String {
    (error as? ClientError)?.errorDescription ?? "Marten could not complete the request."
}
