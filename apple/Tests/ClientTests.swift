import Foundation
import XCTest
@testable import Marten

final class ClientTests: XCTestCase, @unchecked Sendable {
    func testOnlyCanonicalTailnetOriginIsAccepted() throws {
        let origin = "https://marten.tail-test1.ts.net"
        XCTAssertEqual(try Endpoint(origin).origin, origin)
        XCTAssertEqual(Endpoint.saved(origin)?.origin, origin)
        let enrolledOrigin = "https://marten-mac-2.tail-test1.ts.net"
        XCTAssertEqual(try Endpoint(enrolledOrigin).origin, enrolledOrigin)
        XCTAssertEqual(Endpoint.saved(enrolledOrigin)?.origin, enrolledOrigin)
        let maxLabel = String(repeating: "x", count: 63)
        XCTAssertNoThrow(try Endpoint("https://\(maxLabel).\(maxLabel).ts.net"))
        for invalid in [
            "http://marten.tail-test1.ts.net", "HTTPS://marten.tail-test1.ts.net", "https://Marten.tail-test1.ts.net",
            "https://marten.Tail-test1.ts.net", "https://marten.tail-test1.ts.net:443", "https://marten.tail-test1.ts.net:8443",
            "\(origin)/", "\(origin)/api", "\(origin)?", "\(origin)#", "\(origin).", " \(origin)",
            "https://user@marten.tail-test1.ts.net", "https://%6darten.tail-test1.ts.net", "https://marten.t%61il.ts.net",
            "https://marten.-bad.ts.net", "https://marten.bad-.ts.net", "https://marten.bad_.ts.net",
            "https://marten..ts.net", "https://marten.extra.label.ts.net", "https://marten.täil.ts.net",
            "https://.tail-test1.ts.net", "https://tail-test1.ts.net", "https://-node.tail-test1.ts.net",
            "https://node-.tail-test1.ts.net", "https://node_.tail-test1.ts.net", "https://\(maxLabel)x.tail-test1.ts.net",
            "https://marten.\(String(repeating: "a", count: 64)).ts.net", "https://marten.example", "https://localhost",
            "https://127.0.0.1", "https://marten.tail-test1.ts.net.attacker.invalid", "http://127.0.0.1:17450",
            "marten://pair?v=1&server=https%3A%2F%2Fmarten.tail-test1.ts.net&code=synthetic-fixture",
            #"{"v":2,"server":"https://marten.tail-test1.ts.net"}"#,
        ] {
            XCTAssertThrowsError(try Endpoint(invalid))
        }
    }

    private static let offerCode = String(repeating: "A", count: 43)
    private static let fixtureKey = "tskey-auth-fixture-not-a-key"
    private static let fixtureOrigin = "https://test-node.tail-test1.ts.net"
    private static func fixture(_ origin: String = fixtureOrigin, key: Bool = true) -> String {
        let optional = key ? ",\"tsAuthKey\":\"\(fixtureKey)\"" : ""
        return #"{"v":2,"server":"\#(origin)","code":"\#(offerCode)","expiresAt":"2026-11-25T06:35:00.123Z"\#(optional)}"#
    }

    func testV2PairOfferIsStrictAndModeExplicit() throws {
        let good = Self.fixture()
        let offer = try PairOffer(good, mode: .embedded, now: .distantPast)
        XCTAssertEqual(offer.endpoint.origin, Self.fixtureOrigin)
        XCTAssertFalse(String(reflecting: offer).contains(Self.fixtureKey))
        for bad in [good.replacingOccurrences(of: "\"v\":2", with: "\"v\":2.0"),
                    good.replacingOccurrences(of: "\"v\":2", with: "\"v\":\"2\""),
                    good.replacingOccurrences(of: "\"v\":2", with: "\"v\":2,\"\\u0076\":2"),
                    good.replacingOccurrences(of: "\"v\":2", with: "\"v\":2,\"other\":2"),
                    good.replacingOccurrences(of: Self.offerCode, with: String(Self.offerCode.dropLast()) + "+"),
                    good.replacingOccurrences(of: Self.fixtureKey, with: "tskey-auth-"),
                    good.replacingOccurrences(of: "00.123Z", with: "00Z"),
                    good + "{}", "marten://pair?v=1&code=fixture", Self.fixture(key: false),
                    Self.fixture(Self.fixtureOrigin + ":443"), Self.fixture("https://localhost"),
                    String(repeating: " ", count: 4097) + good] {
            XCTAssertThrowsError(try PairOffer(bad, mode: .embedded, now: .distantPast))
        }
        XCTAssertThrowsError(try PairOffer(good, mode: .embedded, now: .distantFuture)) {
            XCTAssertEqual($0 as? ClientError, .pairExpired)
        }
        XCTAssertThrowsError(try PairOffer(good, mode: .direct, now: .distantPast))
        let direct = "https://private.example:8443"
        XCTAssertEqual(try PairOffer(Self.fixture(direct, key: false), mode: .direct, now: .distantPast).endpoint.origin, direct)
    }

    func testEmbeddedRedemptionNeverUsesDirectTransportOrForwardsEnrollmentKey() async throws {
        let offer = try PairOffer(Self.fixture(), mode: .embedded, now: .distantPast)
        let direct = StubTransport([])
        let proxied = StubTransport([.init(200, #"{"token":"fixture-operator"}"#), .init(200, #"{"projects":[],"agents":[]}"#)])
        do {
            _ = try await PairingHandshake(directTransport: direct, embedded: nil)
                .confirm(offer, deviceName: "Phone", clock: { .distantPast })
            XCTFail("Embedded pairing without enrollment was accepted")
        } catch { XCTAssertEqual(error as? ClientError, .enrollmentUnavailable) }
        let enroller = FixtureEnrollment(transport: proxied)
        let flow = PairingHandshake(directTransport: direct, embedded: enroller)
        let paired = try await flow.confirm(offer, deviceName: "  Phone  ", clock: { .distantPast })
        _ = try await paired.status()
        let directRequests = await direct.requests
        XCTAssertTrue(directRequests.isEmpty)
        let origins = await enroller.enrolledOrigins
        XCTAssertEqual(origins, [Self.fixtureOrigin])
        let requests = await proxied.requests
        XCTAssertEqual(requests.count, 2)
        let redeem = try XCTUnwrap(requests.first)
        XCTAssertEqual(redeem.url?.absoluteString, Self.fixtureOrigin + "/api/pair/redeem")
        XCTAssertNil(redeem.value(forHTTPHeaderField: "Authorization"))
        XCTAssertEqual(try Self.body(redeem), ["code": Self.offerCode, "deviceName": "Phone"])
        XCTAssertEqual(requests.last?.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-operator")
    }

    func testFailedRedemptionDiscardsEmbeddedEnrollment() async throws {
        let offer = try PairOffer(Self.fixture(), mode: .embedded, now: .distantPast)
        let direct = StubTransport([])
        let enroller = FixtureEnrollment(transport: StubTransport([.init(400)]))
        do {
            _ = try await PairingHandshake(directTransport: direct, embedded: enroller)
                .confirm(offer, deviceName: "Phone", clock: { .distantPast })
            XCTFail("Invalid redemption was accepted")
        } catch { XCTAssertEqual(error as? ClientError, .response(400)) }
        let discarded = await enroller.discardedOrigins
        XCTAssertEqual(discarded, [Self.fixtureOrigin])
        let requests = await direct.requests
        XCTAssertTrue(requests.isEmpty)
    }

    private actor FixtureEnrollment: EmbeddedEnrollment {
        let transport: any Transport
        var enrolledOrigins: [String] = []
        var discardedOrigins: [String] = []
        init(transport: any Transport) { self.transport = transport }
        func enroll(authKey: String, endpoint: Endpoint, deadline: Date) async throws -> any Transport {
            guard authKey == ClientTests.fixtureKey else { throw ClientError.pairOffer }
            enrolledOrigins.append(endpoint.origin)
            return transport
        }
        func discardEnrollment(for endpoint: Endpoint) async { discardedOrigins.append(endpoint.origin) }
    }

    func testEmbeddedProxyGuardNeverHandsOffOtherAuthorities() async throws {
        let stub = StubTransport([.init(200)])
        let transport = try TailnetTransport(endpoint: Endpoint(Self.fixtureOrigin), transport: stub)
        for bad in ["http://localhost/status", "https://127.0.0.1/status", "https://localhost/status",
                    "https://other.tail-test1.ts.net/api/status", Self.fixtureOrigin + ":443/api/status",
                    Self.fixtureOrigin + "/api/status#fragment"] {
            do {
                _ = try await transport.data(for: URLRequest(url: XCTUnwrap(URL(string: bad))))
                XCTFail("An unpinned request reached the embedded transport")
            } catch { XCTAssertEqual(error as? ClientError, .origin) }
        }
        let good = try XCTUnwrap(URL(string: Self.fixtureOrigin + "/api/status?q=ok"))
        var forged = URLRequest(url: good)
        forged.setValue("other.tail-test1.ts.net", forHTTPHeaderField: "Host")
        do {
            _ = try await transport.data(for: forged)
            XCTFail("Client accepted a caller-supplied Host header")
        } catch { XCTAssertEqual(error as? ClientError, .origin) }
        _ = try await transport.data(for: URLRequest(url: good))
        let requests = await stub.requests
        XCTAssertEqual(requests.map(\.url), [good])
    }

    func testTailnetStateIsPrivateAndScopedToExactEndpoint() throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: root) }
        let first = try Endpoint(Self.fixtureOrigin)
        let second = try Endpoint("https://other.tail-test1.ts.net")
        let store = TailnetState(root: root)
        let directory = try store.directory(for: first)
        XCTAssertNotEqual(directory, try store.directory(for: second))
        try store.prepareNew(directory)
        XCTAssertEqual((try FileManager.default.attributesOfItem(atPath: directory.path)[.posixPermissions] as? NSNumber)?.intValue, 0o700)
        XCTAssertFalse(store.isReady(directory))
        try store.markReady(directory)
        XCTAssertTrue(store.isReady(try TailnetState(root: root).directory(for: first)))
        XCTAssertThrowsError(try store.prepareNew(directory))
        XCTAssertTrue(store.isReady(directory))
    }

    func testAppHasNoExternalURLSchemeRegistration() {
        XCTAssertEqual(Bundle.main.bundleIdentifier, "dev.marten.native")
        XCTAssertNil(Bundle.main.object(forInfoDictionaryKey: "CFBundleURLTypes"))
    }

    #if DEBUG
    @MainActor func testLoopbackIsolatedFromProductionAndCredentialsStayInKeychain() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        let vault = CredentialStore(service: domain)
        let endpoint = try Endpoint(developmentOrigin: "http://localhost:17450/")
        XCTAssertEqual(endpoint.origin, "http://127.0.0.1:17450")
        XCTAssertThrowsError(try Endpoint(endpoint.origin))
        defer {
            defaults.removePersistentDomain(forName: domain)
            try? vault.remove(endpoint)
        }
        let transport = StubTransport([.init(200, #"{"projects":[],"agents":[]}"#)])
        let connections = Connections(transport: transport, keychain: vault, defaults: defaults)
        for rejected in ["http://127.0.0.1.evil.example", "http://127.1", "https://marten.tail-test1.ts.net",
                         "http://localhost@other.example", "http://127.0.0.1/path", "http://localhost?x=1"] {
            await connections.connectLoopback(rejected, token: "fixture-token")
            XCTAssertNil(connections.connected)
        }
        let rejectedRequests = await transport.requests
        XCTAssertTrue(rejectedRequests.isEmpty)
        await connections.connectLoopback("http://localhost:17450", token: "fixture-token")
        let requests = await transport.requests
        XCTAssertEqual(requests.map { $0.url?.absoluteString }, ["http://127.0.0.1:17450/api/status"])
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-token")
        XCTAssertThrowsError(try vault.token(for: Endpoint("https://marten.tail-test1.ts.net")))
        let saved = defaults.persistentDomain(forName: domain)
        XCTAssertEqual(saved?["activeEndpoint"] as? String, endpoint.origin)
        XCTAssertEqual(saved?["endpoints"] as? [String], [endpoint.origin])
        XCTAssertEqual(saved?.count, 2)
        let relaunched = Connections(transport: transport, keychain: vault, defaults: defaults)
        XCTAssertEqual(relaunched.connected?.token, "fixture-token")
        relaunched.forget(endpoint)
        XCTAssertNil(relaunched.connected)
        XCTAssertThrowsError(try vault.token(for: endpoint))
    }
    #endif

    func testOperatorRoutesAndPagingCursor() async throws {
        let transport = StubTransport([.init(200, """
            {"projects":[{"id":"p","name":"Project"}],"agents":[{"id":"task-42","name":"Test","status":"idle","kind":"task","projectId":"p","paused":false,"pausedEffective":false}]}
            """), .init(200, #"{"entries":[{"seq":44,"kind":"assistant","text":"Hi"}],"hasOlder":true}"#),
            .init(202), .init(200), .init(200)])
        let api = API(endpoint: try Endpoint("https://test-node.tail-test1.ts.net"), token: "fixture-token", transport: transport)
        let status = try await api.status()
        let page = try await api.transcript("task-42", before: 80)
        try await api.send("task-42", text: "Test", mode: .immediate)
        try await api.pause("task-42", paused: true)
        try await api.interrupt("task-42")
        XCTAssertEqual(status.projects.first?.name, "Project")
        XCTAssertEqual(status.agents.first?.projectId, "p")
        XCTAssertEqual(page.entries.first?.seq, 44)
        XCTAssertTrue(page.hasOlder)
        let requests = await transport.requests
        XCTAssertEqual(requests.compactMap { $0.url?.path }, ["/api/status", "/api/agents/task-42/transcript",
                                                              "/api/agents/task-42/messages", "/api/agents/task-42/pause",
                                                              "/api/agents/task-42/interrupt"])
        XCTAssertEqual(requests[1].url?.query, "limit=80&before=80")
        XCTAssertTrue(requests.allSatisfy {
            $0.url?.scheme == "https" && $0.url?.host == "test-node.tail-test1.ts.net" && $0.url?.port == nil
        })
        XCTAssertEqual(try Self.body(requests[2]), ["text": "Test", "mode": "immediate"])
        XCTAssertEqual(requests[3].httpBody, Data(#"{"paused":true}"#.utf8))
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token" })
    }

    func testResponseErrorsDoNotSurfaceBodyOrRetry() async throws {
        let transport = StubTransport([.init(307, "never display this"), .init(401)])
        let api = API(endpoint: try Endpoint("https://marten.tail-test1.ts.net"), token: "fixture-token", transport: transport)
        do {
            _ = try await api.status()
            XCTFail("Accepted redirect")
        } catch let error as ClientError {
            XCTAssertEqual(error, .response(307))
            XCTAssertFalse(safeMessage(error).contains("never display this"))
        }
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 1)
        do {
            _ = try await api.status()
            XCTFail("Accepted unauthorized response")
        } catch let error as ClientError { XCTAssertEqual(error, .unauthorized) }
    }

    func testTranscriptOverlapAndDisjointWindows() {
        func page(_ ids: [Int64], older: Bool) -> TranscriptPage {
            TranscriptPage(entries: ids.map { Entry(seq: $0, kind: "message", text: nil, from: nil, name: nil, reason: nil) }, hasOlder: older)
        }
        var buffer = TranscriptBuffer()
        buffer.applyLatest(page([11, 12, 13], older: true))
        buffer.prepend(page([8, 9, 10, 11], older: false), before: 11)
        XCTAssertFalse(buffer.applyLatest(page([12, 13, 14], older: true)))
        XCTAssertEqual(buffer.entries.map(\.seq), Array(8...14))
        XCTAssertFalse(buffer.hasOlder)
        XCTAssertTrue(buffer.applyLatest(page([20, 21], older: true)))
        XCTAssertEqual(buffer.entries.map(\.seq), [20, 21])
        XCTAssertTrue(buffer.hasOlder)
        buffer.prepend(page([20], older: true), before: 20)
        XCTAssertFalse(buffer.hasOlder)
    }

    private static func body(_ request: URLRequest) throws -> [String: String] {
        let body = try XCTUnwrap(request.httpBody)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: String])
    }
}

private actor StubTransport: Transport {
    struct Reply {
        let code: Int
        let body: String
        init(_ code: Int, _ body: String = "") { self.code = code; self.body = body }
    }
    var requests: [URLRequest] = []
    var replies: [Reply]

    init(_ replies: [Reply]) { self.replies = replies }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        guard !replies.isEmpty else { throw URLError(.badServerResponse) }
        let reply = replies.removeFirst()
        return (Data(reply.body.utf8), HTTPURLResponse(url: request.url!, statusCode: reply.code,
                                                       httpVersion: "HTTP/1.1", headerFields: nil)!)
    }
}
