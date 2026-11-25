import Foundation

/// Which transport the user selected before scanning. A missing enrollment key
/// never implicitly downgrades a scan from embedded to direct.
enum PairingMode: Sendable {
    case embedded, direct
}

struct PairOffer: Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let endpoint: Endpoint
    let expiresAt: Date
    let expiryStamp: String
    fileprivate let code: String
    fileprivate let tsAuthKey: String?
    var description: String { "Marten pairing offer (redacted)" }
    var debugDescription: String { description }

    init(_ payload: String, mode: PairingMode, now: Date = .now) throws {
        let fields = try Self.readObject(payload)
        guard case .version? = fields["v"],
              case .string(let server)? = fields["server"],
              case .string(let code)? = fields["code"], code.utf8.count == 43,
              code.utf8.allSatisfy(Self.isBase64URL),
              case .string(let expires)? = fields["expiresAt"],
              expires.utf8.count == 24,
              expires.range(of: #"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z\z"#,
                            options: .regularExpression) != nil else { throw ClientError.pairOffer }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let deadline = formatter.date(from: expires), formatter.string(from: deadline) == expires else {
            throw ClientError.pairOffer
        }
        guard deadline > now else { throw ClientError.pairExpired }
        switch mode {
        case .embedded:
            guard case .string(let key)? = fields["tsAuthKey"], Self.validEnrollmentKey(key) else {
                throw ClientError.pairOffer
            }
            endpoint = try Endpoint(server)
            tsAuthKey = key
        case .direct:
            guard fields["tsAuthKey"] == nil else { throw ClientError.pairOffer }
            endpoint = try Endpoint(directHTTPSOrigin: server)
            tsAuthKey = nil
        }
        self.code = code
        expiresAt = deadline
        expiryStamp = expires
    }

    private enum Scalar {
        case string(String), version
    }

    // The wire schema is a bounded flat object of JSON strings and one integer.
    // Decode string escapes normally while rejecting duplicate decoded keys and
    // alternate number spellings (JSONDecoder alone discards both distinctions).
    private static func readObject(_ payload: String) throws -> [String: Scalar] {
        guard payload.utf8.count <= 4096 else { throw ClientError.pairOffer }
        let input = payload as NSString
        let ws = #"[ \t\r\n]*"#
        let string = #""(?:[^"\\\x00-\x1f]|\\(?:["\\/bfnrt]|u[0-9A-Fa-f]{4}))*""#
        let opening = try NSRegularExpression(pattern: "\\G\(ws)\\{\(ws)")
        let field = try NSRegularExpression(pattern: "\\G(\(string))\(ws):\(ws)(\(string)|2)\(ws)([,}])\(ws)")
        func next(_ regex: NSRegularExpression, at index: Int) -> NSTextCheckingResult? {
            regex.firstMatch(in: payload, options: [.anchored], range: NSRange(location: index, length: input.length - index))
        }
        func unquote(_ raw: String) throws -> String {
            guard let value = try? JSONDecoder().decode(String.self, from: Data(raw.utf8)) else { throw ClientError.pairOffer }
            return value
        }
        guard let first = next(opening, at: 0) else { throw ClientError.pairOffer }
        var index = NSMaxRange(first.range)
        var result: [String: Scalar] = [:]
        let allowed = ["v", "server", "code", "tsAuthKey", "expiresAt"]
        while let match = next(field, at: index) {
            index = NSMaxRange(match.range)
            let key = try unquote(input.substring(with: match.range(at: 1)))
            guard allowed.contains(key), result[key] == nil else { throw ClientError.pairOffer }
            let raw = input.substring(with: match.range(at: 2))
            result[key] = raw == "2" ? .version : .string(try unquote(raw))
            if input.substring(with: match.range(at: 3)) == "}" {
                guard index == input.length else { throw ClientError.pairOffer }
                return result
            }
        }
        throw ClientError.pairOffer
    }

    static func validEnrollmentKey(_ key: String) -> Bool {
        (12...256).contains(key.utf8.count) && key.hasPrefix("tskey-auth-") && key.utf8.dropFirst(11).allSatisfy(isBase64URL)
    }

    func matchesEnrollmentKey(_ key: String?) -> Bool { tsAuthKey == key }

    private static func isBase64URL(_ byte: UInt8) -> Bool {
        (65...90).contains(byte) || (97...122).contains(byte) || (48...57).contains(byte) || byte == 45 || byte == 95
    }
}

/// An implementation must return a ready, fail-closed transport for this exact
/// endpoint and roll back partial node state if enrollment fails or is cancelled.
protocol EmbeddedEnrollment: Sendable {
    func enroll(authKey: String, endpoint: Endpoint, deadline: Date) async throws -> any Transport
    func discardEnrollment(for endpoint: Endpoint) async
}

protocol ManagedEnrollment: EmbeddedEnrollment {
    func commitEnrollment(for endpoint: Endpoint) async throws
    /// Close the local session/node and remove its private state without remote revocation.
    /// Already-absent state must be treated as success so interrupted cleanup can be retried.
    func forgetEnrollment(for endpoint: Endpoint) async throws
    func connectedTransport(for endpoint: Endpoint) async throws -> any Transport
    /// Returns a replacement only when the current proxy was confirmed stale.
    func recoverOnForeground(for endpoint: Endpoint) async throws -> (any Transport)?
}

struct PairingHandshake: Sendable {
    let directTransport: any Transport
    let embedded: (any EmbeddedEnrollment)?

    /// Only invoke from the user's explicit confirmation action. On success the
    /// caller saves just the API token, keeping its already-pinned transport.
    func confirm(_ offer: PairOffer, deviceName input: String, clock: @Sendable () -> Date = { .now }) async throws -> API {
        let name = try Self.deviceName(input)
        try Task.checkCancellation()
        guard offer.expiresAt > clock() else { throw ClientError.pairExpired }
        if let key = offer.tsAuthKey {
            guard let embedded else { throw ClientError.enrollmentUnavailable }
            let transport = try await embedded.enroll(authKey: key, endpoint: offer.endpoint, deadline: offer.expiresAt)
            do { return try await Self.redeem(offer, name: name, transport: transport, clock: clock) }
            catch {
                await embedded.discardEnrollment(for: offer.endpoint)
                throw error
            }
        }
        return try await Self.redeem(offer, name: name, transport: directTransport, clock: clock)
    }

    private static func redeem(_ offer: PairOffer, name: String, transport: any Transport,
                               clock: @Sendable () -> Date) async throws -> API {
        try Task.checkCancellation()
        guard offer.expiresAt > clock() else { throw ClientError.pairExpired }
        let api = API(endpoint: offer.endpoint, transport: transport)
        let paired = try await api.redeem(code: offer.code, deviceName: name)
        try Task.checkCancellation()
        return paired
    }

    private static func deviceName(_ raw: String) throws -> String {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let forbidden: Set<Unicode.GeneralCategory> = [.control, .format, .lineSeparator, .paragraphSeparator]
        guard (1...80).contains(name.utf16.count),
              !name.unicodeScalars.contains(where: { forbidden.contains($0.properties.generalCategory) }) else {
            throw ClientError.deviceName
        }
        return name
    }
}
