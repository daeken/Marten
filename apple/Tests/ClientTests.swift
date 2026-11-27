import CoreImage
import CoreImage.CIFilterBuiltins
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
    private static func fixture(_ origin: String = fixtureOrigin, key: Bool = true, code: String = offerCode) -> String {
        let optional = key ? ",\"tsAuthKey\":\"\(fixtureKey)\"" : ""
        return #"{"v":2,"server":"\#(origin)","code":"\#(code)","expiresAt":"2026-11-25T06:35:00.123Z"\#(optional)}"#
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

    @MainActor func testSavedProductionOriginDoesNotUseSystemTransportWhilePairingIsGated() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        let vault = CredentialStore(service: domain)
        let endpoint = try Endpoint(Self.fixtureOrigin)
        defer {
            defaults.removePersistentDomain(forName: domain)
            try? vault.remove(endpoint)
        }
        defaults.set([endpoint.origin], forKey: "endpoints")
        defaults.set(endpoint.origin, forKey: "activeEndpoint")
        try vault.save("fixture-token", for: endpoint)
        let ordinary = StubTransport([])
        let connections = Connections(transport: ordinary, keychain: vault, defaults: defaults)
        XCTAssertNil(connections.connected)
        XCTAssertFalse(connections.pairingEnabled)
        connections.scan(Self.fixture())
        XCTAssertNil(connections.pendingOffer)
        await connections.confirmOffer(deviceName: "Phone")
        await connections.foreground()
        await connections.select(endpoint)
        XCTAssertNil(connections.connected)
        await connections.forget(endpoint)
        XCTAssertEqual(connections.endpoints, [endpoint])
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-token")
        XCTAssertTrue(connections.pendingForget.isEmpty)
        let requests = await ordinary.requests
        XCTAssertTrue(requests.isEmpty)
    }

    @MainActor func testFixtureConfirmationAndReconnectKeepSecretsOnThePinnedRoute() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        let vault = CredentialStore(service: domain)
        let endpoint = try Endpoint(Self.fixtureOrigin)
        defer {
            defaults.removePersistentDomain(forName: domain)
            try? vault.remove(endpoint)
        }
        let direct = StubTransport([])
        let first = FixtureEventTransport([.init(200, #"{"token":"fixture-operator"}"#)])
        let second = FixtureEventTransport([.init(200, #"{"projects":[],"agents":[]}"#)])
        let manager = ManagedFixture(first: first, replacement: second)
        let connections = Connections(transport: direct, keychain: vault, defaults: defaults, enrollment: manager,
                                      clock: { .distantPast })
        XCTAssertTrue(connections.pairingEnabled)
        connections.scan(Self.fixture())
        XCTAssertEqual(connections.pendingOffer?.endpoint, endpoint)
        XCTAssertNil(connections.connected)
        connections.cancelOffer()
        await connections.confirmOffer(deviceName: "Phone")
        let afterCancel = await manager.calls
        XCTAssertEqual(afterCancel, [])

        connections.scan(Self.fixture())
        await connections.confirmOffer(deviceName: "  Phone  ")
        await connections.confirmOffer(deviceName: "Phone")
        XCTAssertNil(connections.pendingOffer)
        XCTAssertEqual(connections.endpoints, [endpoint])
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-operator")
        let saved = try XCTUnwrap(defaults.persistentDomain(forName: domain))
        XCTAssertEqual(saved.count, 2)
        XCTAssertEqual(saved["endpoints"] as? [String], [endpoint.origin])
        XCTAssertEqual(saved["activeEndpoint"] as? String, endpoint.origin)
        let original = try XCTUnwrap(connections.connected)
        let stream = try XCTUnwrap(original.events())
        stream.cancel()
        let directAPI = API(endpoint: endpoint, token: "fixture-operator", transport: first)
        XCTAssertNil(try directAPI.events())

        let workspace = connections.workspaceID
        let generation = connections.transportGeneration
        await connections.foreground()
        XCTAssertEqual(connections.workspaceID, workspace)
        XCTAssertEqual(connections.transportGeneration, generation)
        await manager.replaceOnNextProbe()
        await connections.foreground()
        XCTAssertEqual(connections.workspaceID, workspace)
        XCTAssertNotEqual(connections.transportGeneration, generation)
        let replacement = try XCTUnwrap(connections.connected)
        try replacement.events()?.cancel()
        _ = try await replacement.status()

        let relaunched = Connections(transport: direct, keychain: vault, defaults: defaults, enrollment: manager)
        XCTAssertNil(relaunched.connected)
        await relaunched.foreground()
        XCTAssertEqual(relaunched.connected?.token, "fixture-operator")
        let calls = await manager.calls
        XCTAssertEqual(calls, ["enroll", "commit", "probe", "probe", "reopen"])
        let firstRequests = await first.http.requests
        XCTAssertEqual(firstRequests.map(\.httpMethod), ["POST"])
        let redeem = try XCTUnwrap(firstRequests.first)
        XCTAssertEqual(redeem.url?.absoluteString, endpoint.origin + "/api/pair/redeem")
        XCTAssertNil(redeem.value(forHTTPHeaderField: "Authorization"))
        XCTAssertEqual(try Self.body(redeem), ["code": Self.offerCode, "deviceName": "Phone"])
        let secondRequests = await second.http.requests
        XCTAssertEqual(secondRequests.map(\.httpMethod), ["GET"])
        XCTAssertEqual(secondRequests.first?.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-operator")
        for capture in [first.eventRequests, second.eventRequests] {
            XCTAssertEqual(capture.count, 1)
            XCTAssertEqual(capture.first?.url?.absoluteString, "wss://test-node.tail-test1.ts.net/api/events")
            XCTAssertEqual(capture.first?.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-operator")
        }
        let directRequests = await direct.requests
        XCTAssertTrue(directRequests.isEmpty)
    }

    @MainActor func testFailedConfirmationConsumesOfferWithoutFallback() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let direct = StubTransport([])
        let proxy = StubTransport([.init(400)])
        let manager = ManagedFixture(first: proxy, replacement: proxy)
        let clock = FixtureClock()
        let connections = Connections(transport: direct, keychain: CredentialStore(service: domain), defaults: defaults,
                                      enrollment: manager, clock: clock.read)
        for name in ["Phone\u{200B}Name", "Phone\u{2028}Name", String(repeating: "😀", count: 41)] {
            connections.scan(Self.fixture())
            await connections.confirmOffer(deviceName: name)
            XCTAssertNil(connections.pendingOffer)
            XCTAssertEqual(connections.error, ClientError.deviceName.errorDescription)
        }
        connections.scan(Self.fixture())
        clock.expire(true)
        await connections.confirmOffer(deviceName: "Phone")
        XCTAssertEqual(connections.error, ClientError.pairExpired.errorDescription)
        XCTAssertNil(connections.pendingOffer)
        let earlyCalls = await manager.calls
        XCTAssertEqual(earlyCalls, [])
        clock.expire(false)
        connections.scan(Self.fixture())
        await connections.confirmOffer(deviceName: "Phone")
        await connections.confirmOffer(deviceName: "Phone")
        XCTAssertNil(connections.pendingOffer)
        XCTAssertNil(connections.connected)
        XCTAssertTrue(connections.endpoints.isEmpty)
        let calls = await manager.calls
        XCTAssertEqual(calls, ["enroll", "discard"])
        let sent = await proxy.requests
        XCTAssertEqual(sent.count, 1)
        let directRequests = await direct.requests
        XCTAssertTrue(directRequests.isEmpty)
    }

    func testWSSRejectsOtherAuthoritiesBrowserTokensAndUnpinnedRoute() throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let stub = FixtureEventTransport([])
        let transport = try TailnetTransport(endpoint: endpoint, transport: stub)
        let url = "wss://test-node.tail-test1.ts.net/api/events"
        for rejected in [url + "?token=fixture-operator", url + "?", url + "#fragment", url + "/",
                         url.replacingOccurrences(of: ".ts.net", with: ".ts.net:443"),
                         url.replacingOccurrences(of: "test-node", with: "other"),
                         url.replacingOccurrences(of: "wss:", with: "ws:")] {
            var request = URLRequest(url: try XCTUnwrap(URL(string: rejected)))
            request.setValue("Bearer fixture-operator", forHTTPHeaderField: "Authorization")
            XCTAssertThrowsError(try transport.events(for: request)) { XCTAssertEqual($0 as? ClientError, .origin) }
        }
        var request = URLRequest(url: try XCTUnwrap(URL(string: url)))
        XCTAssertThrowsError(try transport.events(for: request)) { XCTAssertEqual($0 as? ClientError, .unauthorized) }
        request.setValue("other.tail-test1.ts.net", forHTTPHeaderField: "Host")
        XCTAssertThrowsError(try transport.events(for: request)) { XCTAssertEqual($0 as? ClientError, .origin) }
        XCTAssertTrue(stub.eventRequests.isEmpty)
    }

    @MainActor func testForgetIsRetryableAndRePairRequiresFreshOffer() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        let vault = CredentialStore(service: domain)
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let other = try Endpoint("https://another.tail-test1.ts.net")
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer {
            defaults.removePersistentDomain(forName: domain)
            try? vault.remove(endpoint)
            try? vault.remove(other)
            try? FileManager.default.removeItem(at: root)
        }
        let disk = TailnetState(root: root)
        let node = try disk.directory(for: endpoint)
        let otherNode = try disk.directory(for: other)
        let oldIdentity = node.appending(path: "old-identity")
        try disk.prepareNew(otherNode)
        try disk.markReady(otherNode)
        try vault.save("fixture-other", for: other)
        defaults.set([other.origin], forKey: "endpoints")

        let direct = StubTransport([])
        let proxy = StubTransport([.init(200, #"{"token":"fixture-old"}"#), .init(400),
                                   .init(200, #"{"token":"fixture-new"}"#)])
        let manager = ManagedFixture(first: proxy, replacement: proxy, disk: disk)
        let connections = Connections(transport: direct, keychain: vault, defaults: defaults, enrollment: manager,
                                      clock: { .distantPast })
        connections.scan(Self.fixture())
        await connections.confirmOffer(deviceName: "Phone")
        XCTAssertTrue(disk.isReady(node))
        try Data([1]).write(to: oldIdentity)
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-old")
        connections.disconnect()
        XCTAssertTrue(disk.isReady(node))
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-old")
        await connections.select(endpoint)
        XCTAssertEqual(connections.connected?.endpoint, endpoint)

        let freshCode = String(repeating: "B", count: 43)
        connections.scan(Self.fixture(code: freshCode))
        await connections.confirmOffer(deviceName: "Phone")
        XCTAssertEqual(connections.error, ClientError.pairingAlreadySaved.errorDescription)
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-old")

        await manager.failNextForget()
        connections.scan(Self.fixture(code: freshCode))
        await connections.forget(endpoint)
        XCTAssertNil(connections.pendingOffer)
        XCTAssertNil(connections.connected)
        XCTAssertNil(defaults.string(forKey: "activeEndpoint"))
        XCTAssertEqual(defaults.stringArray(forKey: "pendingForget"), [endpoint.origin])
        XCTAssertEqual(connections.error, ClientError.tailnetState.errorDescription)
        XCTAssertThrowsError(try vault.token(for: endpoint))
        XCTAssertTrue(disk.isReady(node))
        XCTAssertEqual(connections.endpoints, [other, endpoint])

        let relaunched = Connections(transport: direct, keychain: vault, defaults: defaults, enrollment: manager,
                                     clock: { .distantPast })
        XCTAssertEqual(relaunched.pendingForget, [endpoint])
        await relaunched.foreground()
        await relaunched.select(endpoint)
        XCTAssertNil(relaunched.connected)
        XCTAssertEqual(relaunched.error, ClientError.forgetPending.errorDescription)
        relaunched.scan(Self.fixture(code: freshCode))
        await relaunched.confirmOffer(deviceName: "Phone")
        XCTAssertEqual(relaunched.error, ClientError.forgetPending.errorDescription)
        await relaunched.forget(endpoint)
        XCTAssertEqual(relaunched.endpoints, [other])
        XCTAssertNil(defaults.object(forKey: "pendingForget"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: node.path))
        XCTAssertTrue(disk.isReady(otherNode))
        XCTAssertEqual(try vault.token(for: other), "fixture-other")

        // Like the #55 contract, the server fixture rejects the consumed code.
        relaunched.scan(Self.fixture())
        await relaunched.confirmOffer(deviceName: "Phone")
        XCTAssertEqual(relaunched.error, ClientError.response(400).errorDescription)
        XCTAssertFalse(FileManager.default.fileExists(atPath: node.path))
        relaunched.scan(Self.fixture(code: freshCode))
        await relaunched.confirmOffer(deviceName: "Phone")
        XCTAssertEqual(relaunched.connected?.token, "fixture-new")
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-new")
        XCTAssertTrue(disk.isReady(node))
        XCTAssertFalse(FileManager.default.fileExists(atPath: oldIdentity.path))
        let calls = await manager.calls
        XCTAssertEqual(calls, ["enroll", "commit", "reopen", "forget", "forget", "enroll", "discard", "enroll", "commit"])
        let proxied = await proxy.requests
        XCTAssertEqual(proxied.map { $0.url?.path }, Array(repeating: "/api/pair/redeem", count: 3))
        XCTAssertEqual(try proxied.map { try Self.body($0)["code"] }, [Self.offerCode, Self.offerCode, freshCode])
        XCTAssertTrue(proxied.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == nil })
        let directRequests = await direct.requests
        XCTAssertTrue(directRequests.isEmpty)
    }

    #if MARTEN_EMBEDDED_TAILNET
    func testEmbeddedAdapterForgetsClosedCommittedStateWithoutEnrolling() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: root) }
        let state = TailnetState(root: root)
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let node = try state.directory(for: endpoint)
        try state.prepareNew(node)
        try state.markReady(node)
        let adapter = TailscaleKitEnrollment(state: state)
        try await adapter.forgetEnrollment(for: endpoint)
        try await adapter.forgetEnrollment(for: endpoint)
        XCTAssertFalse(FileManager.default.fileExists(atPath: node.path))
        do {
            _ = try await adapter.connectedTransport(for: endpoint)
            XCTFail("Adapter reopened a forgotten node")
        } catch { XCTAssertEqual(error as? ClientError, .tailnetState) }
        try state.prepareNew(node)
        XCTAssertFalse(state.isReady(node))
    }
    #endif

    private actor ManagedFixture: ManagedEnrollment {
        let first: any Transport
        let replacement: any Transport
        var calls: [String] = []
        private var stale = false
        private var replaced = false
        private var forgetFails = false
        let disk: TailnetState?

        init(first: any Transport, replacement: any Transport, disk: TailnetState? = nil) {
            self.first = first; self.replacement = replacement; self.disk = disk
        }
        func enroll(authKey: String, endpoint: Endpoint, deadline: Date) async throws -> any Transport {
            guard authKey == ClientTests.fixtureKey else { throw ClientError.pairOffer }
            calls.append("enroll")
            if let disk { try disk.prepareNew(disk.directory(for: endpoint)) }
            return try TailnetTransport(endpoint: endpoint, transport: first)
        }
        func discardEnrollment(for endpoint: Endpoint) async {
            calls.append("discard")
            try? disk?.removeNode(for: endpoint)
        }
        func commitEnrollment(for endpoint: Endpoint) async throws {
            calls.append("commit")
            if let disk { try disk.markReady(disk.directory(for: endpoint)) }
        }
        func failNextForget() { forgetFails = true }
        func forgetEnrollment(for endpoint: Endpoint) async throws {
            calls.append("forget")
            if forgetFails { forgetFails = false; throw ClientError.tailnetState }
            try disk?.removeNode(for: endpoint)
        }
        func connectedTransport(for endpoint: Endpoint) async throws -> any Transport {
            calls.append("reopen")
            return try TailnetTransport(endpoint: endpoint, transport: replaced ? replacement : first)
        }
        func replaceOnNextProbe() { stale = true }
        func recoverOnForeground(for endpoint: Endpoint) async throws -> (any Transport)? {
            calls.append("probe")
            guard stale else { return nil }
            stale = false
            replaced = true
            return try TailnetTransport(endpoint: endpoint, transport: replacement)
        }
    }

    private final class FixtureEventTransport: EventTransport, @unchecked Sendable {
        let http: StubTransport
        private let lock = NSLock()
        private var captured: [URLRequest] = []
        var eventRequests: [URLRequest] { lock.withLock { captured } }
        init(_ replies: [StubTransport.Reply]) { http = StubTransport(replies) }
        func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) { try await http.data(for: request) }
        func events(for request: URLRequest) throws -> EventStream {
            lock.withLock { captured.append(request) }
            return EventStream(hints: AsyncThrowingStream { $0.finish() }, cancel: {})
        }
    }

    private final class FixtureClock: @unchecked Sendable {
        private let lock = NSLock()
        private var expired = false
        func expire(_ value: Bool) { lock.withLock { expired = value } }
        func read() -> Date { lock.withLock { expired ? .distantFuture : .distantPast } }
    }

    func testImportsSyntheticV2QRImageWithoutExternalURL() throws {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(Self.fixture().utf8)
        let code = try XCTUnwrap(filter.outputImage).transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        let image = code.composited(over: CIImage(color: .white))
            .cropped(to: code.extent.insetBy(dx: -32, dy: -32))
        let bytes = try XCTUnwrap(CIContext().pngRepresentation(of: image, format: .RGBA8,
                                                                 colorSpace: CGColorSpaceCreateDeviceRGB()))
        let url = FileManager.default.temporaryDirectory.appending(path: "\(UUID()).png")
        defer { try? FileManager.default.removeItem(at: url) }
        try bytes.write(to: url)
        let imported = try QRImage.read(url)
        XCTAssertTrue(imported == Self.fixture(), "Unexpected pairing QR payload")
        let offer = try PairOffer(imported, mode: .embedded, now: .distantPast)
        XCTAssertEqual(offer.endpoint.origin, Self.fixtureOrigin)
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
        await relaunched.forget(endpoint)
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

    private static func inboxItem(_ id: String, from: String = "task-42", extra: String = "") -> String {
        #"{"id":"\#(id)","at":"2026-11-25T06:35:00.1234567+00:00","from":"\#(from)","fromName":"Fixture Agent","projectId":"p","text":"Which label?"\#(extra)}"#
    }

    @MainActor func testInboxAndQuestionAnswersUseOperatorContract() async throws {
        let option = Self.inboxItem("opt", extra: #", "question":true,"options":["One","Two"],"context":"Pick a label""#)
        let words = Self.inboxItem("words", extra: #", "question":true"#)
        let noNote = Self.inboxItem("no-note", extra: #", "question":true,"options":["One"]"#)
        let saved = Self.inboxItem("saved", from: "other", extra: #", "question":true,"answer":"Previously saved","answeredAt":"2026-11-25T07:00:00Z""#)
        let notice = Self.inboxItem("notice")
        let fixture = "[\(option),\(words),\(noNote),\(saved),\(notice)]"
        let transport = StubTransport([.init(200, #"{"projects":[],"agents":[],"inbox":5,"openQuestions":3}"#), .init(200, fixture),
                                       .init(200, Self.inboxItem("opt", extra: #", "question":true,"answer":"Two: note""#)),
                                       .init(200, Self.inboxItem("words", extra: #", "question":true,"answer":"custom""#)),
                                       .init(200, Self.inboxItem("no-note", extra: #", "question":true,"answer":"One""#))])
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let api = API(endpoint: endpoint, token: "fixture-token", transport: try TailnetTransport(endpoint: endpoint, transport: transport))
        let status = try await api.status()
        XCTAssertEqual(status.inbox, 5)
        XCTAssertEqual(status.openQuestions, 3)
        let state = InboxState()
        await state.refresh(api)
        XCTAssertEqual(state.messages?.map(\.id), ["opt", "words", "no-note", "saved", "notice"])
        let first = try XCTUnwrap(state.messages?.first)
        XCTAssertEqual(first.context, "Pick a label")
        XCTAssertNotNil(InboxMessage.date(first.at))
        XCTAssertEqual(state.questions(for: "other").first?.answer, "Previously saved")
        XCTAssertEqual(state.questions(for: "task-42").map(\.id), ["opt", "words", "no-note"])
        state.drafts["opt"] = " note \n"
        state.drafts["words"] = " custom "
        await state.answer("opt", choice: 1, using: api)
        await state.answer("words", choice: nil, using: api)
        await state.answer("no-note", choice: 0, using: api)
        await state.answer("saved", choice: nil, using: api)
        await state.answer("opt", choice: 1, using: api)
        XCTAssertEqual(state.messages?.compactMap(\.answer), ["Two: note", "custom", "One", "Previously saved"])
        XCTAssertTrue(state.drafts.isEmpty)
        let requests = await transport.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "GET", "POST", "POST", "POST"])
        XCTAssertEqual(requests.compactMap { $0.url?.path }, ["/api/status", "/api/inbox", "/api/questions/opt/answer", "/api/questions/words/answer", "/api/questions/no-note/answer"])
        XCTAssertTrue(requests.allSatisfy { $0.url?.host == endpoint.url.host && $0.url?.query == nil && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token" })
        func payload(_ request: URLRequest) throws -> NSDictionary {
            try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? NSDictionary)
        }
        XCTAssertEqual(try payload(requests[2]), ["choice": 1, "text": "note"] as NSDictionary)
        XCTAssertEqual(try payload(requests[3]), ["choice": NSNull(), "text": "custom"] as NSDictionary)
        XCTAssertEqual(try payload(requests[4]), ["choice": 0, "text": NSNull()] as NSDictionary)
    }

    @MainActor func testFailedAnswerReconcilesByReadingOnReplacementWithoutReplay() async throws {
        let raw = Self.inboxItem("question", extra: #", "question":true"#)
        let saved = Self.inboxItem("question", extra: #", "question":true,"answer":"Other operator’s answer""#)
        let transport = StubTransport([.init(200, "[\(raw)]"), .init(409, "hidden detail")])
        let replacement = StubTransport([.init(502), .init(200, "[\(saved)]")])
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let api = API(endpoint: endpoint, token: "fixture-token", transport: transport)
        let reopened = API(endpoint: endpoint, token: "fixture-token", transport: replacement)
        let state = InboxState()
        await state.refresh(api)
        state.drafts["question"] = "Keep this draft"
        await state.answer("question", choice: nil, using: api)
        XCTAssertEqual(state.drafts["question"], "Keep this draft")
        XCTAssertEqual(state.needsRefresh, ["question"])
        XCTAssertFalse(state.answerErrors["question", default: ""].contains("hidden detail"))
        await state.answer("question", choice: nil, using: reopened)
        await state.refresh(reopened)
        XCTAssertNotNil(state.loadError)
        XCTAssertEqual(state.needsRefresh, ["question"])
        XCTAssertEqual(state.messages?.first?.id, "question")
        await state.refresh(reopened)
        XCTAssertNil(state.loadError)
        XCTAssertEqual(state.messages?.first?.answer, "Other operator’s answer")
        XCTAssertTrue(state.drafts.isEmpty)
        await state.answer("question", choice: nil, using: reopened)
        let firstRequests = await transport.requests
        let replacementRequests = await replacement.requests
        XCTAssertEqual(firstRequests.map(\.httpMethod), ["GET", "POST"])
        XCTAssertEqual(replacementRequests.map(\.httpMethod), ["GET", "GET"])
    }

    func testAnswerRejectsUnsafeQuestionIDAndUncredentialedInbox() async throws {
        let transport = StubTransport([])
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let api = API(endpoint: endpoint, token: "fixture-token", transport: transport)
        let bad = try JSONDecoder().decode(InboxMessage.self, from: Data(Self.inboxItem("../status", extra: #", "question":true"#).utf8))
        do { _ = try await api.answer(bad, choice: nil, text: "answer"); XCTFail("Accepted unsafe question ID") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { _ = try await API(endpoint: endpoint, transport: transport).inbox(); XCTFail("Accepted uncredentialed inbox") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await transport.requests
        XCTAssertTrue(requests.isEmpty)
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

    @MainActor func testTimelineKeepsArchiveStableDuringRefreshAndPlacesSavedAnswersOnce() async throws {
        let open = Self.inboxItem("pending", extra: #", "question":true"#)
        let stale = Self.inboxItem("saved", extra: #", "question":true"#)
        let saved = Self.inboxItem("saved", extra: #", "question":true,"answer":"Yes","answeredAt":"2026-11-25T06:35:03Z""#)
        let fixture = StubTransport([.init(200, "[\(open),\(saved)]"), .init(502), .init(200, "[\(open),\(stale)]")])
        let api = API(endpoint: try Endpoint(Self.fixtureOrigin), token: "fixture-token", transport: fixture)
        let inbox = InboxState()
        await inbox.refresh(api)
        func page(_ rows: String, older: Bool = true) throws -> TranscriptPage {
            try JSONDecoder().decode(TranscriptPage.self, from: Data("{\"entries\":[\(rows)],\"hasOlder\":\(older)}".utf8))
        }
        let latest = try page(#"{"seq":3,"ts":"2026-11-25T06:35:02Z","kind":"tool_call","callId":"c","arguments":"{}"},{"seq":4,"ts":"2026-11-25T06:35:03Z","kind":"tool_result","callId":"c","text":"Output","isError":true},{"seq":5,"ts":"2026-11-25T06:35:04Z","kind":"assistant","text":"Done"}"#)
        var timeline = TimelinePages()
        timeline.updateLatest(latest)
        func rows(_ state: TimelinePages) throws -> [TimelineRow] {
            let questions = inbox.questions(for: "task-42")
            _ = try XCTUnwrap(state.page)
            return state.rows(questions: questions)
        }
        let initial = try rows(timeline)
        XCTAssertEqual(initial.map(\.id), [.entry(3), .question("saved"), .entry(5), .question("pending")])
        guard case .entry(_, let result) = initial[0].content else { return XCTFail("No associated tool result") }
        XCTAssertEqual(result?.text, "Output")
        XCTAssertEqual(result?.isError, true)
        await inbox.refresh(api) // failed invalidation read keeps last good state
        XCTAssertEqual(try rows(timeline).map(\.id), initial.map(\.id))
        await inbox.refresh(api) // a stale open snapshot cannot undo the acknowledged answer
        XCTAssertEqual(try rows(timeline).map(\.id), initial.map(\.id))
        let older = try XCTUnwrap(timeline.target(.older))
        XCTAssertEqual(older, [3])
        try timeline.show(page(#"{"seq":1,"ts":"2026-11-25T06:35:00Z","kind":"assistant","text":"Earlier"},{"seq":2,"ts":"2026-11-25T06:35:01Z","kind":"tool_result","text":"Unmatched","callId":"previous"}"#), at: older)
        XCTAssertEqual(timeline.target(.older), [3, 1])
        XCTAssertEqual(try rows(timeline).map(\.id), [.entry(1), .entry(2)])
        let firstArchive = try XCTUnwrap(timeline.archive)
        try timeline.show(page(#"{"seq":1,"kind":"message"}"#), at: [3, 1]) // nonadvancing response
        XCTAssertNil(timeline.target(.older))
        XCTAssertEqual(timeline.target(.newer), [3])
        timeline.show(firstArchive, at: [3])
        let delivery = try page(#"{"seq":5,"ts":"2026-11-25T06:35:04Z","kind":"assistant","text":"Done"},{"seq":6,"ts":"2026-11-25T06:35:05Z","kind":"message","from":"Sera","text":"Sera answered your question \"Which label?\":\n\nYes"}"#)
        timeline.updateLatest(delivery) // WSS invalidations may update latest while archive is shown
        XCTAssertEqual(try rows(timeline).map(\.id), [.entry(1), .entry(2)])
        XCTAssertEqual(timeline.target(.newer), [])
        timeline.returnToLatest()
        XCTAssertEqual(try rows(timeline).map(\.id), [.entry(5), .entry(6), .question("pending")])
        timeline.updateLatest(try page(#"{"seq":7,"ts":"2026-11-25T06:35:06Z","kind":"assistant","text":"A later window"}"#, older: false))
        XCTAssertEqual(try rows(timeline).map(\.id), [.entry(7), .question("pending")])
        let requests = await fixture.requests
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "GET" })
    }

    @MainActor func testMessagesProjectFilterKeepsOrderingAndDuplicatesAcrossRefresh() async throws {
        let one = #"{"at":"2026-11-25T06:35:04Z","fromId":"a","fromName":"Alpha","toId":"b","toName":"Beta","projectId":"p&x=1","mode":"immediate","text":"Latest","reason":"handoff"}"#
        let two = #"{"at":"2026-11-25T06:35:03Z","fromId":"c","fromName":"Chat","toId":"a","toName":"Alpha","mode":"queued","text":"Older"}"#
        let transport = StubTransport([.init(200, "[\(one),\(one),\(two)]"), .init(502), .init(200, "[\(one),\(one)]")])
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let api = API(endpoint: endpoint, token: "fixture-token", transport: try TailnetTransport(endpoint: endpoint, transport: transport))
        let messages = MessagesState()
        await messages.refresh(api)
        XCTAssertEqual(messages.rows?.map { $0.message.text }, ["Latest", "Latest", "Older"])
        let first = try XCTUnwrap(messages.rows)
        XCTAssertEqual(Set(first.map(\.id)).count, 3)
        XCTAssertEqual(first.first?.message.reason, "handoff")
        XCTAssertEqual(first.first?.message.toId, "b")
        await messages.refresh(api)
        XCTAssertNotNil(messages.error)
        XCTAssertEqual(messages.rows?.map(\.id), first.map(\.id))
        messages.select("p&x=1")
        XCTAssertNil(messages.rows)
        await messages.refresh(api)
        XCTAssertEqual(messages.rows?.map(\.id), Array(first.prefix(2).map(\.id)))
        let requests = await transport.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "GET", "GET"])
        XCTAssertTrue(requests.allSatisfy { $0.url?.path == "/api/messages" && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token" })
        XCTAssertNil(requests[0].url?.query)
        let url = try XCTUnwrap(requests[2].url)
        XCTAssertEqual(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems, [URLQueryItem(name: "projectId", value: "p&x=1")])
    }

    func testAttachmentStaysOnPinnedBearerRouteAndRejectsUnexpectedResponses() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport([.init(200, "placeholder", mime: "image/png"), .init(307), .init(200, "unexpected", mime: "text/html"), .init(200, String(repeating: "x", count: 12 * 1024 * 1024 + 1), mime: "image/png")])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let image = TranscriptImage(path: #"C:\private\images\20260101-image.png"#, mimeType: "image/png")
        let data = try await api.image(image, agent: "agent-1")
        XCTAssertEqual(data, Data("placeholder".utf8))
        for expected in [ClientError.response(307), .attachment, .attachment] {
            do { _ = try await api.image(image, agent: "agent-1"); XCTFail("Accepted rejected image") }
            catch { XCTAssertEqual(error as? ClientError, expected) }
        }
        for path in ["/tmp/.png", "/tmp/injected%2Fname.png", "/tmp/name.png?token=x", "/tmp/name.svg"] {
            do { _ = try await api.image(.init(path: path, mimeType: "image/png"), agent: "agent-1"); XCTFail("Accepted bad filename") }
            catch { XCTAssertEqual(error as? ClientError, .attachment) }
        }
        do { _ = try await API(endpoint: endpoint, transport: transport).image(image, agent: "agent-1"); XCTFail("Accepted missing Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.count, 4)
        XCTAssertTrue(requests.allSatisfy { $0.url?.absoluteString == endpoint.origin + "/api/agents/agent-1/images/20260101-image.png" && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" && $0.value(forHTTPHeaderField: "Accept") == "image/png" })
    }

    func testNativeRichTextAndBoundedImageDecoding() throws {
        let source = "# Heading\n\n**bold** and [label](https://example.invalid/x)\n\n```swift\nlet n = 1\n```"
        let blocks = TranscriptBlock.parse(source)
        XCTAssertEqual(blocks.map(\.style), [.heading, .paragraph, .code])
        XCTAssertEqual(blocks.last?.text, "let n = 1")
        let inline = TranscriptBlock.inline(blocks[1].text)
        XCTAssertEqual(String(inline.characters), "bold and label")
        XCTAssertTrue(inline.runs.allSatisfy { $0.link == nil })
        let bytes = try XCTUnwrap(Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR1sAAAAASUVORK5CYII="))
        let image = try AttachmentPreview.thumbnail(bytes)
        XCTAssertEqual(image.width, 1)
        XCTAssertEqual(image.height, 1)
        XCTAssertThrowsError(try AttachmentPreview.thumbnail(Data("<svg/>".utf8)))
    }

    @MainActor func testPendingComposerWireSteeringDraftAndStaleRecall() async throws {
        let rows = #"[{"id":"q","text":"Waiting","mode":"queued"},{"id":"s","text":"Steer","mode":"immediate"},{"id":"internal","text":"Hidden","mode":"nudge"}]"#
        let transport = StubTransport([.init(200, rows), .init(404), .init(404)])
        let api = API(endpoint: try Endpoint(Self.fixtureOrigin), token: "fixture-token", transport: transport)
        let composer = PendingComposer(agent: "a")
        let route = UUID()
        await composer.refresh(using: api, route: route)
        XCTAssertEqual(composer.rows(on: route).map(\.id), ["q", "s"])
        let queued = try XCTUnwrap(composer.rows(on: route).first)
        let steering = try XCTUnwrap(composer.rows(on: route).last)
        XCTAssertNil(composer.recall(steering, using: api, route: route))
        composer.setDraft("Keep this draft")
        XCTAssertNil(composer.recall(queued, using: api, route: route))
        XCTAssertEqual(composer.draft, "Keep this draft")
        composer.setDraft("")
        XCTAssertNil(composer.recall(queued, using: api, route: UUID()))
        let deletion = try XCTUnwrap(composer.recall(queued, using: api, route: route))
        XCTAssertNil(composer.recall(queued, using: api, route: route))
        await deletion.value
        XCTAssertTrue(composer.draft.isEmpty)
        XCTAssertNotNil(composer.recallNotice)
        XCTAssertEqual(composer.rows(on: route).map(\.id), ["s"])
        await composer.refresh(using: api, route: route)
        XCTAssertTrue(composer.rows(on: route).isEmpty)
        let requests = await transport.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "DELETE", "GET"])
        XCTAssertEqual(requests.map { $0.url?.path }, ["/api/agents/a/pending-messages", "/api/agents/a/pending-messages/q", "/api/agents/a/pending-messages"])
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token" })
    }

    @MainActor func testPendingRecallRetainsReturnedTextAcrossNavigationAndRejectsLateRead() async throws {
        let lateStarted = expectation(description: "Old GET suspended")
        let deleteStarted = expectation(description: "Original route DELETE suspended")
        let oldTransport = GatedPendingTransport(lateStarted: lateStarted, deleteStarted: deleteStarted)
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let oldAPI = API(endpoint: endpoint, token: "fixture-token", transport: oldTransport)
        let newTransport = StubTransport([.init(200, "[]"), .init(200, "{}")])
        let newAPI = API(endpoint: endpoint, token: oldAPI.token, transport: newTransport)
        let states = ConversationComposers()
        let composer = states.state("a", using: oldAPI)
        let old = UUID(), new = UUID()
        await composer.refresh(using: oldAPI, route: old)
        let row = try XCTUnwrap(composer.rows(on: old).first)
        let late = Task { await composer.refresh(using: oldAPI, route: old) }
        await fulfillment(of: [lateStarted], timeout: 3)
        let recall = try XCTUnwrap(composer.recall(row, using: oldAPI, route: old))
        let another = states.state("b", using: newAPI)
        another.setDraft("Other agent draft")
        XCTAssertTrue(states.state("a", using: newAPI) === composer)
        XCTAssertTrue(states.state("a", using: API(endpoint: endpoint, token: "other-token", transport: newTransport)).draft.isEmpty)
        XCTAssertTrue(states.state("a", using: API(endpoint: try Endpoint("https://other.tail-test1.ts.net"), token: oldAPI.token, transport: newTransport)).draft.isEmpty)
        await fulfillment(of: [deleteStarted], timeout: 3)
        composer.setDraft("Blocked while reserved")
        let sentWhileReserved = try await composer.send(using: newAPI)
        XCTAssertFalse(sentWhileReserved)
        XCTAssertTrue(composer.draft.isEmpty)
        await oldTransport.releaseDelete()
        await recall.value
        XCTAssertEqual(composer.draft, "Returned\ntext")
        XCTAssertEqual(another.draft, "Other agent draft")
        await composer.refresh(using: newAPI, route: new)
        await oldTransport.releaseLate()
        await late.value
        XCTAssertTrue(composer.rows(on: old).isEmpty)
        XCTAssertTrue(composer.rows(on: new).isEmpty)
        let oldRequests = await oldTransport.requests
        XCTAssertEqual(oldRequests.map { $0.url?.path }, ["/api/agents/a/pending-messages", "/api/agents/a/pending-messages", "/api/agents/a/pending-messages/q"])
        composer.mode = .immediate
        let sent = try await composer.send(using: newAPI)
        XCTAssertTrue(sent)
        let newRequests = await newTransport.requests
        XCTAssertEqual(newRequests.map(\.httpMethod), ["GET", "POST"])
        XCTAssertEqual(try Self.body(XCTUnwrap(newRequests.last)), ["mode": "immediate", "text": "Returned\ntext"])
        XCTAssertTrue(composer.draft.isEmpty)
    }

    func testPendingSocketHintsContainOnlyTheAffectedAgent() {
        var events = LiveEvents()
        _ = events.receive(Data(#"{"type":"inbox","agentId":"a","pending":1}"#.utf8))
        XCTAssertEqual(events.pendingAgent, "a")
        _ = events.receive(Data(#"{"type":"inbox","agentId":"b","pending":0}"#.utf8))
        XCTAssertEqual(events.pendingAgent, "b")
        _ = events.receive(Data(#"{"type":"status","agentId":"a"}"#.utf8))
        XCTAssertNil(events.pendingAgent)
    }

    func testLiveEventsKeepAccumulatedDeltasAndFollowOnlyAtLiveTail() {
        var events = LiveEvents()
        func receive(_ payload: String) -> (updated: Bool, invalidate: Bool) { events.receive(Data(payload.utf8)) }
        XCTAssertTrue(receive(#"{"type":"model_call_start","agentId":"a","modelCall":{"kind":"compaction"}}"#).updated)
        XCTAssertEqual(events.agents["a"]?.thinking, "Compacting context")
        XCTAssertFalse(receive(#"{"type":"delta","agentId":"a","text":"Hello "}"#).invalidate)
        _ = receive(#"{"type":"delta","agentId":"b","text":"Other agent"}"#)
        _ = receive(#"{"type":"delta","agentId":"a","text":"again"}"#)
        XCTAssertEqual(events.agents["a"]?.text, "Hello again")
        XCTAssertTrue(receive(#"{"type":"entry","agentId":"a","entry":{"seq":9,"kind":"assistant"}}"#).invalidate)
        XCTAssertTrue(events.agents["a"]?.text.isEmpty == true)
        _ = receive(#"{"type":"tool_start","agentId":"a","name":"fixture"}"#)
        XCTAssertEqual(events.agents["a"]?.tool, "fixture")
        _ = receive(#"{"type":"entry","agentId":"a","entry":{"seq":10,"kind":"tool_result"}}"#)
        XCTAssertNil(events.agents["a"]?.tool)
        _ = receive(#"{"type":"model_call_end","agentId":"a","modelCall":{"outcome":"failed"}}"#)
        XCTAssertNil(events.agents["a"])
        XCTAssertEqual(events.agents["b"]?.text, "Other agent")
        XCTAssertTrue(LiveEvents().agents.isEmpty)
        var follow = LiveFollow()
        XCTAssertTrue(follow.shouldFollow(archived: false))
        XCTAssertFalse(follow.shouldFollow(archived: true))
        follow.measure(distance: 200)
        XCTAssertFalse(follow.shouldFollow(archived: false))
        follow.resume()
        XCTAssertTrue(follow.shouldFollow(archived: false))
    }

    func testOperationsDecodeOperatorContractAndUsePinnedRoutes() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let tasks = #"[{"id":5,"title":"Ship fixture","state":"waiting","waitingOn":{"agentId":"agent-2","taskId":9},"reason":"waiting for fixture","assignedBy":"orch","updated":"2026-01-01T10:00:00+00:00","check":"fixture test","lastCheck":{"at":"2026-01-01T09:00:00+00:00","passed":false,"exitCode":1,"output":"fixture output\nsecond line"}}]"#
        let schedules = #"[{"id":1,"created":"2026-01-01T10:00:00Z","command":"fixture once","nextRun":"2026-01-01T11:00:00Z"},{"id":2,"created":"2026-01-01T10:00:00Z","command":"fixture repeat","description":"Refresh fixture","everySeconds":900,"nextRun":"2026-01-01T11:15:00Z","lastRun":"2026-01-01T11:00:00Z","lastResult":"output delivered","inProgress":true}]"#
        let running = #"{"agentId":"agent-1","agentName":"Fixture One","id":3,"name":"fixture","command":"fixture server","workingDirectory":"/fixture","pid":100,"started":"2026-01-01T10:00:00Z","logPath":"/fixture/private.log","state":"running"}"#
        let stopped = #"{"agentId":"agent-2","agentName":"Fixture Two","id":3,"name":"old","command":"fixture server","workingDirectory":"/fixture","pid":101,"started":"2026-01-01T09:00:00Z","stopped":"2026-01-01T10:00:00Z","state":"stopped"}"#
        let fixture = StubTransport([.init(200, tasks), .init(200, schedules), .init(200, "[\(running),\(stopped)]"),
                                     .init(200, "[\(running)]"), .init(200, "[\(running),\(stopped)]"),
                                     .init(200, "[\(running),\(stopped)]"), .init(200), .init(200, stopped)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: try TailnetTransport(endpoint: endpoint, transport: fixture))
        let taskList = try await api.tasks("agent-1")
        let task = try XCTUnwrap(taskList.first)
        XCTAssertEqual(task.state, "waiting")
        XCTAssertEqual(task.waitingOn?.agentId, "agent-2")
        XCTAssertEqual(task.waitingOn?.taskId, 9)
        XCTAssertEqual(task.reason, "waiting for fixture")
        XCTAssertEqual(task.check, "fixture test")
        XCTAssertEqual(task.lastCheck?.passed, false)
        XCTAssertEqual(task.lastCheck?.exitCode, 1)
        XCTAssertEqual(task.lastCheck?.output, "fixture output\nsecond line")
        let scheduleList = try await api.schedules("agent-1")
        XCTAssertEqual(scheduleList.map(\.timing), ["Once", "Every 15m"])
        XCTAssertEqual(scheduleList.last?.created, "2026-01-01T10:00:00Z")
        XCTAssertEqual(scheduleList.last?.nextRun, "2026-01-01T11:15:00Z")
        XCTAssertEqual(scheduleList.last?.lastRun, "2026-01-01T11:00:00Z")
        XCTAssertEqual(scheduleList.last?.lastResult, "output delivered")
        XCTAssertEqual(scheduleList.last?.inProgress, true)
        let global = try await api.processes()
        let scoped = try await api.processes("agent-1")
        let scopedHistory = try await api.processes("agent-2", includeStopped: true)
        let globalHistory = try await api.processes(includeStopped: true)
        XCTAssertEqual(ProcessGroups(scopedHistory).history.map(\.state), ["stopped"])
        XCTAssertEqual(globalHistory.map(\.id), global.map(\.id))
        XCTAssertEqual(scoped.map(\.id), [.init(agent: "agent-1", number: 3)])
        XCTAssertEqual(Set(global.map(\.id)).count, 2)
        XCTAssertEqual(global.map(\.canStop), [true, false])
        XCTAssertEqual(global.first?.workingDirectory, "/fixture")
        try await api.removeSchedule("agent-1", 1)
        try await api.stopProcess(try XCTUnwrap(scoped.first).id)
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "GET", "GET", "GET", "GET", "GET", "DELETE", "POST"])
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, [
            "/api/agents/agent-1/tasks", "/api/agents/agent-1/schedules", "/api/processes", "/api/agents/agent-1/processes",
            "/api/agents/agent-2/processes?includeStopped=true", "/api/processes",
            "/api/agents/agent-1/schedules/1", "/api/agents/agent-1/processes/3/stop"
        ].map { endpoint.origin + $0 })
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" && $0.httpBody == nil })
    }

    func testTaskGroupsKeepAllActiveStatesSeparateFromDone() throws {
        let items = ["open", "waiting", "blocked", "done", "unknown"].enumerated().map { id, state in
            AgentTask(id: id, title: "Task", state: state, waitingOn: nil, reason: nil, assignedBy: "fixture",
                      updated: "2026-01-01T10:00:00Z", check: nil, lastCheck: nil)
        }
        let groups = TaskGroups(items)
        XCTAssertEqual(groups.active.map(\.state), ["open", "waiting", "blocked"])
        XCTAssertEqual(groups.done.map(\.id), [3])
        XCTAssertTrue(TaskGroups([]).active.isEmpty)
        XCTAssertTrue(TaskGroups([]).done.isEmpty)
    }

    @MainActor func testProcessRefreshMovesTerminalRowsIntoHistory() async throws {
        func process(_ id: Int, _ state: String) throws -> AgentProcess {
            let json = #"{"agentId":"agent-1","agentName":"Fixture","id":\#(id),"name":"job","command":"job","workingDirectory":"/fixture","pid":100,"started":"2026-01-01T10:00:00Z","state":"\#(state)"}"#
            return try JSONDecoder().decode(AgentProcess.self, from: Data(json.utf8))
        }
        let list = OperationList<AgentProcess>()
        let running = try process(1, "running")
        let stopped = try process(2, "stopped")
        let unknown = try process(3, "starting")
        let wrongCase = try process(4, "Running")
        await list.refresh { [running, stopped, unknown, wrongCase] }
        let before = ProcessGroups(try XCTUnwrap(list.items))
        XCTAssertEqual(before.running.map(\.id), [running.id])
        XCTAssertEqual(before.history.map(\.id), [stopped.id])
        let exited = try process(1, "exited")
        await list.refresh { [exited, stopped, unknown, wrongCase] }
        let after = ProcessGroups(try XCTUnwrap(list.items))
        XCTAssertTrue(after.running.isEmpty)
        XCTAssertEqual(after.history.map(\.id), [running.id, stopped.id])
        XCTAssertEqual(after.history.map(\.canStop), [true, false])
    }

    func testProcessLogUsesBoundedPlainTextAndRejectsUnsafeRequests() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport([.init(200, "fixture log\n", mime: "text/plain; charset=utf-8"), .init(307), .init(401),
                                     .init(200, "unexpected", mime: "text/html"),
                                     .init(200, String(repeating: "x", count: 1024 * 1024 + 1), mime: "text/plain")])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let key = AgentProcess.Key(agent: "agent-1", number: 3)
        let log = try await api.processLog(key)
        XCTAssertEqual(log, "fixture log\n")
        for expected in [ClientError.response(307), .unauthorized, .processLog, .processLog] {
            do { _ = try await api.processLog(key); XCTFail("Accepted rejected log") }
            catch { XCTAssertEqual(error as? ClientError, expected) }
        }
        do { try await api.stopProcess(.init(agent: "other/log?token=x", number: 3)); XCTFail("Accepted unsafe agent") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { try await api.removeSchedule("agent-1", 0); XCTFail("Accepted unsafe number") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { _ = try await API(endpoint: endpoint, transport: transport).processLog(key); XCTFail("Accepted missing Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.count, 5)
        XCTAssertTrue(requests.allSatisfy {
            $0.httpMethod == "GET" && $0.url?.absoluteString == endpoint.origin + "/api/agents/agent-1/processes/3/log?lines=200" &&
            $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" && $0.value(forHTTPHeaderField: "Accept") == "text/plain"
        })
    }

    @MainActor func testOperationFailureKeepsRowsAndReplacementOnlyReads() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let schedule = #"[{"id":1,"created":"2026-01-01T10:00:00Z","command":"fixture once","nextRun":"2026-01-01T11:00:00Z"}]"#
        let fixture = StubTransport([.init(200, schedule), .init(502, "private error"), .init(503)])
        let replacement = StubTransport([.init(200, schedule), .init(200), .init(200, "[]")])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let reconnected = API(endpoint: endpoint, token: "fixture-bearer", transport: replacement)
        let state = OperationList<AgentSchedule>()
        await state.refresh { try await api.schedules("agent-1") }
        await state.change({ try await api.removeSchedule("agent-1", 1) }, read: { try await api.schedules("agent-1") })
        XCTAssertTrue(state.needsRead)
        XCTAssertEqual(state.items?.map(\.id), [1])
        XCTAssertNotNil(state.error)
        XCTAssertFalse(try XCTUnwrap(state.actionError).contains("private error"))
        await state.change({ XCTFail("Must first reconcile by reading") }, read: { XCTFail("Blocked action should not issue a read"); return [] })
        await state.refresh { try await reconnected.schedules("agent-1") }
        XCTAssertFalse(state.needsRead)
        XCTAssertNil(state.error)
        let reconnectRequests = await replacement.requests
        XCTAssertEqual(reconnectRequests.map(\.httpMethod), ["GET"])
        await state.change({ try await reconnected.removeSchedule("agent-1", 1) }, read: { try await reconnected.schedules("agent-1") })
        XCTAssertEqual(state.items?.map(\.id), [])
        XCTAssertNil(state.actionError)
        let initialRequests = await fixture.requests
        let finalRequests = await replacement.requests
        XCTAssertEqual(initialRequests.map(\.httpMethod), ["GET", "DELETE", "GET"])
        XCTAssertEqual(finalRequests.map(\.httpMethod), ["GET", "DELETE", "GET"])
    }

    private static let notebookCell = #"{"id":8,"at":"2026-01-01T10:00:00Z","command":"Write-Output 'fixture'","workingDirectory":"/fixture","stateIn":{"count":9007199254740993,"flag":true,"nested":[null,"value"]},"stateOut":[1,"changed"],"output":"recorded","isError":false,"durationMs":20}"#
    private static let rerunCell = #"{"id":0,"at":"2026-01-01T10:01:00Z","command":"Write-Output 'fixture'","workingDirectory":"/fixture","stateOut":false,"output":"different","isError":true,"durationMs":32}"#
    private static let notebookFile = #"{"nbformat":4,"nbformat_minor":5,"cells":[{"cell_type":"code","source":["fixture"]}],"metadata":{}}"#

    @MainActor func testNotebookConfirmationConsumesOnceAndDecodesRecordedAndReplayState() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let listing = "[\(Self.notebookCell)]"
        let reply = #"{"run":\#(Self.rerunCell),"sameOutput":false,"sameState":false}"#
        let fixture = StubTransport([.init(200, listing), .init(200, reply), .init(200, listing)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: try TailnetTransport(endpoint: endpoint, transport: fixture))
        let state = NotebookState()
        let route = UUID()
        await state.refresh(using: api, agent: "agent-1", route: route)
        let cell = try XCTUnwrap(state.cells?.first)
        XCTAssertEqual(cell.id, 8)
        XCTAssertEqual(cell.command, "Write-Output 'fixture'")
        XCTAssertEqual(cell.workingDirectory, "/fixture")
        XCTAssertEqual(cell.at, "2026-01-01T10:00:00Z")
        XCTAssertEqual(cell.output, "recorded")
        XCTAssertEqual(cell.durationMs, 20)
        XCTAssertFalse(cell.isError)
        let input = try XCTUnwrap(cell.stateIn?.display)
        XCTAssertTrue(input.contains("9007199254740993"))
        XCTAssertTrue(input.contains("true"))
        XCTAssertTrue(input.contains("null"))
        XCTAssertTrue(try XCTUnwrap(cell.stateOut?.display).contains("changed"))
        state.prepare(8, route: route)
        XCTAssertEqual(state.pending?.id, 8)
        state.cancelConfirmation()
        XCTAssertNil(state.confirm(8, using: api, agent: "agent-1", route: route))
        let cancelled = await fixture.requests
        XCTAssertEqual(cancelled.map(\.httpMethod), ["GET"])
        state.prepare(8, route: route)
        XCTAssertNil(state.confirm(8, using: api, agent: "agent-1", route: UUID()))
        state.prepare(8, route: route)
        let run = try XCTUnwrap(state.confirm(8, using: api, agent: "agent-1", route: route))
        XCTAssertNil(state.confirm(8, using: api, agent: "agent-1", route: route))
        await run.value
        await state.refresh(using: api, agent: "agent-1", route: route)
        guard case .result(let replay) = state.outcomes[8] else { return XCTFail("Missing rerun result") }
        XCTAssertFalse(replay.sameOutput)
        XCTAssertFalse(replay.sameState)
        XCTAssertEqual(replay.run.output, "different")
        XCTAssertNil(replay.run.stateIn)
        XCTAssertEqual(replay.run.stateOut?.display, "false")
        XCTAssertTrue(replay.run.isError)
        XCTAssertEqual(replay.run.durationMs, 32)
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "POST", "GET"])
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, [
            "/api/agents/agent-1/notebook?limit=300", "/api/agents/agent-1/notebook/8/rerun", "/api/agents/agent-1/notebook?limit=300"
        ].map { endpoint.origin + $0 })
        XCTAssertGreaterThan(requests[1].timeoutInterval, 120)
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" && $0.httpBody == nil })
    }

    @MainActor func testNotebookCancelledAndFailedRerunsRequireReadAndNeverReplayOnReconnect() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let listing = "[\(Self.notebookCell)]"
        let fixture = StubTransport([.init(200, listing), .init(failure: .cancelled)])
        let replacement = StubTransport([.init(503), .init(200, listing), .init(502, "private detail"), .init(200, listing)])
        let original = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let reconnected = API(endpoint: endpoint, token: "fixture-bearer", transport: replacement)
        let state = NotebookState()
        let firstRoute = UUID()
        await state.refresh(using: original, agent: "agent-1", route: firstRoute)
        state.prepare(8, route: firstRoute)
        await state.confirm(8, using: original, agent: "agent-1", route: firstRoute)?.value
        XCTAssertFalse(state.canPrepare(route: firstRoute))
        guard case .unconfirmed(let cancellation) = state.outcomes[8] else { return XCTFail("Missing cancellation warning") }
        XCTAssertTrue(cancellation.contains("cancelled"))
        XCTAssertTrue(cancellation.contains("side effects"))
        let route = UUID()
        await state.refresh(using: reconnected, agent: "agent-1", route: route)
        XCTAssertEqual(state.cells?.map(\.id), [8])
        XCTAssertNotNil(state.readError)
        state.prepare(8, route: route)
        XCTAssertNil(state.pending)
        await state.refresh(using: reconnected, agent: "agent-1", route: route)
        let reads = await replacement.requests
        XCTAssertEqual(reads.map(\.httpMethod), ["GET", "GET"])
        state.prepare(8, route: route)
        await state.confirm(8, using: reconnected, agent: "agent-1", route: route)?.value
        guard case .unconfirmed(let failure) = state.outcomes[8] else { return XCTFail("Missing error warning") }
        XCTAssertTrue(failure.contains("502"))
        XCTAssertFalse(failure.contains("private detail"))
        await state.refresh(using: reconnected, agent: "agent-1", route: route)
        let initial = await fixture.requests
        let final = await replacement.requests
        XCTAssertEqual(initial.map(\.httpMethod), ["GET", "POST"])
        XCTAssertEqual(final.map(\.httpMethod), ["GET", "GET", "POST", "GET"])
    }

    func testNotebookExportPinnedAuthorizationValidationAndCancellation() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let mime = "application/x-ipynb+json"
        let fixture = StubTransport([.init(200, Self.notebookFile, mime: mime), .init(307), .init(401), .init(403),
                                     .init(200, Self.notebookFile, mime: "text/html"), .init(200, "{}", mime: mime), .init(failure: .cancelled)])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let bytes = try await api.notebookFile("agent-1")
        XCTAssertEqual(bytes, Data(Self.notebookFile.utf8))
        for expected in [ClientError.response(307), .unauthorized, .unauthorized, .malformed, .malformed] {
            do { _ = try await api.notebookFile("agent-1"); XCTFail("Accepted rejected export") }
            catch { XCTAssertEqual(error as? ClientError, expected) }
        }
        do { _ = try await api.notebookFile("agent-1"); XCTFail("Accepted cancelled download") }
        catch { XCTAssertTrue(error is CancellationError) }
        let cancelled = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await api.notebookFile("agent-1")
        }
        do { _ = try await cancelled.value; XCTFail("Cancelled download should not start") }
        catch { XCTAssertTrue(error is CancellationError) }
        let unauthenticated = API(endpoint: endpoint, transport: transport)
        do { _ = try await unauthenticated.notebook("agent-1"); XCTFail("Listed without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { _ = try await unauthenticated.rerunCell("agent-1", 8); XCTFail("Reran without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { _ = try await unauthenticated.notebookFile("agent-1"); XCTFail("Exported without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { _ = try await api.notebookFile("../other"); XCTFail("Accepted unsafe agent") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { _ = try await api.rerunCell("agent-1", 0); XCTFail("Accepted unsafe cell number") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.count, 7)
        XCTAssertTrue(requests.allSatisfy {
            $0.httpMethod == "GET" && $0.url?.absoluteString == endpoint.origin + "/api/agents/agent-1/notebook.ipynb" &&
            $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" && $0.value(forHTTPHeaderField: "Accept") == mime
        })
    }

    private static let lifecycleAgent = #"{"id":"agent-1","name":"Fixture Chat","kind":"chat","status":"idle","closed":false,"paused":false,"pausedEffective":false,"workingDirectory":"/fixture"}"#
    private static let lifecycleProject = #"{"id":"project-1","name":"Fixture Project","bindings":["/fixture/a","/fixture/b"],"worktreesByDefault":true,"orchestratorId":"agent-2","paused":false}"#
    private static let lifecycleStatus = #"{"agents":[\#(lifecycleAgent)],"projects":[\#(lifecycleProject)],"paused":false}"#

    func testProjectMemoryAndCreationOperatorContract() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let memory = ##"{"path":"/fixture/MEMORY.md","text":"# Notes","prompt":"Fixture prompt","log":[{"at":"2026-01-01T10:00:00Z","agentId":"agent-1","reason":"Initial","added":["one"],"removed":[]},{"at":"2026-01-02T10:00:00Z","agentId":"unknown","path":"memory/topic.md","added":["two"],"removed":["one"]}]}"##
        let fixture = StubTransport([.init(200, memory), .init(200, Self.lifecycleProject), .init(200, Self.lifecycleAgent), .init(403)])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let doc = try await api.projectMemory("project-1")
        XCTAssertEqual(doc.path, "/fixture/MEMORY.md")
        XCTAssertEqual(doc.text, "# Notes")
        XCTAssertEqual(doc.prompt, "Fixture prompt")
        XCTAssertEqual(doc.log.first?.agentId, "agent-1")
        XCTAssertEqual(doc.log.first?.reason, "Initial")
        XCTAssertNil(doc.log.first?.path)
        XCTAssertEqual(doc.log.last?.path, "memory/topic.md")
        XCTAssertEqual(doc.log.last?.removed, ["one"])
        XCTAssertEqual(doc.log.last?.added, ["two"])
        let draft = ProjectCreation(name: " Fixture Project ", folders: " /fixture/a \r\n\n /fixture/b ", prompt: "   ", worktrees: true, brief: " Start with fixture.\n")
        XCTAssertTrue(draft.isValid)
        XCTAssertFalse(ProjectCreation(name: "Fixture", folders: " \n", prompt: "", worktrees: false, brief: "").isValid)
        let project = try await api.createProject(draft)
        XCTAssertEqual(project.orchestratorId, "agent-2")
        XCTAssertEqual(project.bindings, ["/fixture/a", "/fixture/b"])
        let chat = try await api.createChat("Fixture Chat")
        XCTAssertEqual(chat.id, "agent-1")
        do { _ = try await api.projectMemory("project-1"); XCTFail("Accepted 403") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { _ = try await api.projectMemory("../other"); XCTFail("Accepted unsafe project ID") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        let guest = API(endpoint: endpoint, transport: transport)
        do { _ = try await guest.createProject(draft); XCTFail("Created without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { _ = try await guest.createChat("Fixture Chat"); XCTFail("Created without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "POST", "POST", "GET"])
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, ["/api/projects/project-1/memory", "/api/projects", "/api/agents", "/api/projects/project-1/memory"].map { endpoint.origin + $0 })
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(requests[1].httpBody)) as? [String: Any])
        XCTAssertEqual(payload["name"] as? String, "Fixture Project")
        XCTAssertEqual(payload["bindings"] as? [String], ["/fixture/a", "/fixture/b"])
        XCTAssertTrue(payload["prompt"] is NSNull)
        XCTAssertEqual(payload["worktreesByDefault"] as? Bool, true)
        XCTAssertEqual(payload["brief"] as? String, "Start with fixture.")
        XCTAssertEqual(try Self.body(requests[2]), ["name": "Fixture Chat"])
    }

    func testLifecycleWireRoutesAndAuthorization() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport(Array(repeating: .init(200), count: 5) + [.init(204)])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let actions: [LifecycleAction] = [.pauseAll(true), .pauseProject("project-1", false), .pauseAgent("agent-1", true),
                                          .close("agent-1", true), .close("agent-1", false), .freshContext("agent-1")]
        for action in actions { try await api.applyLifecycle(action) }
        do { try await API(endpoint: endpoint, transport: transport).applyLifecycle(.freshContext("agent-1")); XCTFail("Accepted missing auth") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { try await api.applyLifecycle(.pauseProject("?id=none", true)); XCTFail("Accepted unsafe project ID") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, [
            "/api/pause", "/api/projects/project-1/pause", "/api/agents/agent-1/pause", "/api/agents/agent-1/close",
            "/api/agents/agent-1/close", "/api/agents/agent-1/start-fresh-context"
        ].map { endpoint.origin + $0 })
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "POST" && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
        for (request, expected) in zip(requests, [["paused": true], ["paused": false], ["paused": true], ["closed": true], ["closed": false]]) {
            XCTAssertEqual(try JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Bool], expected)
        }
        XCTAssertNil(requests.last?.httpBody)
    }

    @MainActor func testLifecycleConfirmationConflictAndReadOnlyReconnect() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let first = StubTransport([.init(200, Self.lifecycleStatus), .init(409, "internal detail"), .init(503)])
        let next = StubTransport([.init(200, Self.lifecycleStatus), .init(200), .init(200, Self.lifecycleStatus)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: first)
        let replacement = API(endpoint: endpoint, token: "fixture-bearer", transport: next)
        let route = UUID()
        let target = WorkspaceControls.Target(route: route, page: .agent("agent-1"))
        let controls = WorkspaceControls()
        await controls.refresh(using: api, route: route)
        let fresh = LifecycleAction.freshContext("agent-1")
        let close = LifecycleAction.close("agent-1", true)
        XCTAssertNil(controls.apply(fresh, target: target, using: api))
        controls.stage(close, name: "Fixture Chat", target: target)
        let cancelled = try XCTUnwrap(controls.pending?.id)
        controls.cancel()
        XCTAssertNil(controls.apply(close, confirmation: cancelled, target: target, using: api))
        controls.stage(fresh, name: "Fixture Chat", target: target)
        let moved = WorkspaceControls.Target(route: route, page: nil)
        XCTAssertNil(controls.apply(fresh, confirmation: controls.pending?.id, target: moved, using: api))
        controls.stage(fresh, name: "Fixture Chat", target: target)
        let confirmation = try XCTUnwrap(controls.pending?.id)
        let run = try XCTUnwrap(controls.apply(fresh, confirmation: confirmation, target: target, using: api))
        XCTAssertNil(controls.apply(fresh, confirmation: confirmation, target: target, using: api))
        await run.value
        XCTAssertTrue(controls.notice?.contains("active turn") == true)
        XCTAssertFalse(controls.notice?.contains("internal detail") == true)
        XCTAssertFalse(controls.ready(route))
        XCTAssertEqual(controls.status?.agents.first?.id, "agent-1")
        XCTAssertNil(controls.apply(.pauseAll(true), target: target, using: api))
        let newRoute = UUID()
        await controls.refresh(using: replacement, route: newRoute)
        XCTAssertTrue(controls.ready(newRoute))
        XCTAssertFalse(controls.ready(route))
        let reconnectReads = await next.requests
        XCTAssertEqual(reconnectReads.map(\.httpMethod), ["GET"])
        let newTarget = WorkspaceControls.Target(route: newRoute, page: .agent("agent-1"))
        controls.stage(close, name: "Fixture Chat", target: newTarget)
        await controls.apply(close, confirmation: controls.pending?.id, target: newTarget, using: replacement)?.value
        let initialRequests = await first.requests
        let finalRequests = await next.requests
        XCTAssertEqual(initialRequests.map(\.httpMethod), ["GET", "POST", "GET"])
        XCTAssertEqual(finalRequests.map(\.httpMethod), ["GET", "POST", "GET"])
    }

    @MainActor func testGuardDecisionsRequireReviewedCardAndNeverReplay() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let permit = #"{"id":"guard-1","at":"2026-01-01T10:00:00Z","from":"agent-1","fromName":"Fixture","projectId":"project-1","text":"Approve fixture call?","question":true,"kind":"approval","reference":"fixture-fingerprint","options":["Allow once","Deny"],"context":"Rule: fixture\npreview"}"#
        let deny = permit.replacingOccurrences(of: "guard-1", with: "guard-2")
        let listing = "[\(permit),\(deny)]"
        let saved = String(permit.dropLast()) + #", "answer":"Allow once: fixture note"}"#
        let fixture = StubTransport([.init(200, listing), .init(200, saved), .init(409, "private detail")])
        let next = StubTransport([.init(200, listing), .init(200, String(deny.dropLast()) + #", "answer":"Deny"}"#)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let replacement = API(endpoint: endpoint, token: "fixture-bearer", transport: next)
        let inbox = InboxState()
        await inbox.refresh(api)
        let first = try XCTUnwrap(inbox.messages?.first)
        let second = try XCTUnwrap(inbox.messages?.last)
        XCTAssertTrue(first.isToolDecision)
        XCTAssertEqual(first.projectId, "project-1")
        XCTAssertEqual(first.context, "Rule: fixture\npreview")
        do { _ = try await api.answer(first, choice: nil, text: "fixture"); XCTFail("Submitted without explicit choice") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        await inbox.answer(first.id, choice: 0, using: api)
        await inbox.answer(first.id, choice: 0, using: api, confirmed: second)
        let unconfirmed = await fixture.requests
        XCTAssertEqual(unconfirmed.map(\.httpMethod), ["GET"])
        inbox.drafts[first.id] = " fixture note "
        await inbox.answer(first.id, choice: 0, using: api, confirmed: first)
        XCTAssertEqual(inbox.messages?.first?.answer, "Allow once: fixture note")
        await inbox.answer(second.id, choice: 1, using: api, confirmed: second)
        XCTAssertTrue(inbox.needsRefresh.contains(second.id))
        XCTAssertFalse(inbox.answerErrors[second.id]?.contains("private detail") == true)
        await inbox.answer(second.id, choice: 1, using: api, confirmed: second)
        await inbox.refresh(replacement)
        let reconnect = await next.requests
        XCTAssertEqual(reconnect.map(\.httpMethod), ["GET"])
        await inbox.answer(second.id, choice: 1, using: replacement, confirmed: second)
        XCTAssertEqual(inbox.messages?.last?.answer, "Deny")
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "POST", "POST"])
        XCTAssertEqual(requests[1].url?.absoluteString, endpoint.origin + "/api/questions/guard-1/answer")
        XCTAssertEqual(requests[2].url?.absoluteString, endpoint.origin + "/api/questions/guard-2/answer")
        let bodies = try requests.dropFirst().map { try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap($0.httpBody)) as? [String: Any]) }
        XCTAssertEqual(bodies[0]["choice"] as? Int, 0)
        XCTAssertEqual(bodies[0]["text"] as? String, "fixture note")
        XCTAssertEqual(bodies[1]["choice"] as? Int, 1)
        XCTAssertTrue(bodies[1]["text"] is NSNull)
    }

    func testProjectToolReviewMatchesOnlyTheExactVersion() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let row = #"{"name":"fixture","scope":"global","path":"/fixture/tool.ps1","script":"fixture script","pendingReview":true,"error":"fixture parse error"}"#
        let card = #"{"id":"review-1","at":"2026-01-01T10:00:00Z","from":"agent-1","fromName":"Fixture","text":"Global tool","question":true,"kind":"tool_review","reference":"/fixture/tool.ps1\n92F67C348EA1BC6C0EBD28BC2D5B3B8C859BD97826C7AB9FE36AABE3A63EE87F","options":["Approve","Reject"],"context":"fixture script"}"#
        let changed = row.replacingOccurrences(of: "fixture script", with: "fixture script ")
        let fixture = StubTransport([.init(200, "[\(row),\(changed)]"), .init(200, "[\(card)]"), .init(403)])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let tools = try await api.scriptTools(project: "project-1")
        let inbox = try await api.inbox()
        XCTAssertEqual(tools.first?.scope, "global")
        XCTAssertEqual(tools.first?.script, "fixture script")
        XCTAssertEqual(tools.first?.error, "fixture parse error")
        XCTAssertEqual(tools.first?.reviewCard(in: inbox)?.id, "review-1")
        XCTAssertNil(tools.last?.reviewCard(in: inbox))
        do { _ = try await api.scriptTools(project: "project-1"); XCTFail("Accepted HTTP 403") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.first?.url?.absoluteString, endpoint.origin + "/api/tools?projectId=project-1")
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "GET" && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
    }

    @MainActor func testSettingsDefaultsDiagnosticsAndUnconfirmedSave() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let agent = String(Self.lifecycleAgent.dropLast()) + #", "model":"old-model","effort":"high","statusDetail":"Waiting for a slot","retryAt":"2026-01-01T10:00:00Z","modelCall":{"id":2,"model":"old-model","kind":"compaction","startedAt":"2026-01-01T09:00:00Z","endedAt":"2026-01-01T09:01:00Z","outcome":"succeeded"},"parentId":"agent-2","pending":3,"openTasks":2,"nudgesStopped":true,"abandonedRunspaces":[{"at":"2026-01-01T09:00:00Z","command":"fixture"}]}"#
        let status = #"{"projects":[],"agents":[\#(agent)],"paused":false,"config":{"defaultModel":"default-model","effort":"low"}}"#
        let fixture = StubTransport([.init(200, status), .init(200, #"["default-model"]"#), .init(502), .init(503)])
        let next = StubTransport([.init(200, status)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let replacement = API(endpoint: endpoint, token: "fixture-bearer", transport: next)
        let controls = WorkspaceControls()
        let route = UUID()
        await controls.refresh(using: api, route: route)
        let agentState = try XCTUnwrap(controls.status?.agents.first)
        XCTAssertEqual(agentState.parentId, "agent-2")
        XCTAssertEqual(agentState.pending, 3)
        XCTAssertEqual(agentState.openTasks, 2)
        XCTAssertEqual(agentState.modelCall?.outcome, "succeeded")
        XCTAssertEqual(agentState.retryAt, "2026-01-01T10:00:00Z")
        XCTAssertEqual(agentState.nudgesStopped, true)
        XCTAssertEqual(agentState.abandonedRunspaces?.first?.command, "fixture")
        XCTAssertEqual(controls.status?.config?.defaultModel, "default-model")
        let catalog = try await api.models()
        XCTAssertEqual(catalog, ["default-model"])
        let draft = AgentSettings(model: nil, effort: "high")
        XCTAssertNil(controls.saveSettings(draft, original: .init(), agent: "agent-1", using: api, route: route))
        await controls.saveSettings(draft, original: agentState.settings, agent: "agent-1", using: api, route: route)?.value
        XCTAssertFalse(controls.ready(route))
        XCTAssertNotNil(controls.notice)
        await controls.refresh(using: replacement, route: UUID())
        let initial = await fixture.requests
        let reconnect = await next.requests
        XCTAssertEqual(initial.map(\.httpMethod), ["GET", "GET", "PUT", "GET"])
        XCTAssertEqual(initial[1].url?.path, "/api/models")
        XCTAssertEqual(initial[2].url?.path, "/api/agents/agent-1/settings")
        let body = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(initial[2].httpBody)) as? [String: Any])
        XCTAssertTrue(body["model"] is NSNull)
        XCTAssertEqual(body["effort"] as? String, "high")
        XCTAssertEqual(reconnect.map(\.httpMethod), ["GET"])
    }

    private static let providerStatus = #"{"projects":[],"agents":[],"auth":{"connected":true,"email":"fixture@example.test","signingIn":false,"lastError":"synthetic-private-upstream-detail"},"backends":[{"provider":"fixture","label":"Fixture API","connected":true,"keySource":"saved","spentToday":1.50,"budget":1.50},{"provider":"environment","label":"Fixture Environment","connected":true,"keySource":"FIXTURE_API_KEY","envVar":"FIXTURE_API_KEY","spentToday":0}]}"#

    func testProviderMetadataAndPinnedCredentialRoutes() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport([.init(200, Self.providerStatus), .init(200), .init(200), .init(200)])
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let status = try await api.status()
        XCTAssertEqual(status.auth?.email, "fixture@example.test")
        XCTAssertEqual(status.auth?.connected, true)
        XCTAssertEqual(status.auth?.signInFailed, true)
        XCTAssertFalse(String(reflecting: status.auth).contains("synthetic-private-upstream-detail"))
        XCTAssertEqual(status.backends?.first?.hasSavedKey, true)
        XCTAssertEqual(status.backends?.first?.exhaustedBudget, true)
        XCTAssertEqual(status.backends?.last?.hasSavedKey, false)
        XCTAssertEqual(status.backends?.last?.connectionLabel, "Server environment (FIXTURE_API_KEY)")
        try await api.setProviderKey("fixture", key: "  synthetic-key  ")
        try await api.setProviderKey("fixture", key: nil)
        try await api.signOutProviderAccount()
        do { try await api.setProviderKey("fixture", key: " \n"); XCTFail("Accepted an empty replacement") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { try await api.setProviderKey("fixture/other", key: nil); XCTFail("Accepted an unsafe provider ID") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        let guest = API(endpoint: endpoint, transport: transport)
        do { try await guest.setProviderKey("fixture", key: nil); XCTFail("Accepted a key mutation without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { try await guest.signOutProviderAccount(); XCTFail("Accepted sign-out without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "PUT", "PUT", "POST"])
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, [
            "/api/status", "/api/backends/fixture/key", "/api/backends/fixture/key", "/api/auth/logout"
        ].map { endpoint.origin + $0 })
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
        let saved = try Self.body(requests[1])
        XCTAssertTrue(saved == ["key": "synthetic-key"], "Expected only the trimmed synthetic value in the request body")
        let removed = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(requests[2].httpBody)) as? [String: Any])
        XCTAssertTrue(removed["key"] is NSNull)
        XCTAssertNil(requests[3].httpBody)
    }

    @MainActor func testProviderMutationFailureIsRedactedAndReconnectOnlyReads() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport([.init(200, Self.providerStatus), .init(502, "synthetic-key echoed by upstream"), .init(503)])
        let replacement = StubTransport([.init(200, Self.providerStatus), .init(200), .init(200, Self.providerStatus)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let next = API(endpoint: endpoint, token: "fixture-bearer", transport: replacement)
        let controls = WorkspaceControls()
        let route = UUID()
        await controls.refresh(using: api, route: route)
        XCTAssertNil(controls.updateProvider("environment", key: nil, using: api, route: route))
        await controls.updateProvider("fixture", key: "synthetic-key", using: api, route: route)?.value
        XCTAssertFalse(controls.ready(route))
        let notice = try XCTUnwrap(controls.notice)
        XCTAssertTrue(notice.contains("502"))
        XCTAssertFalse(notice.contains("synthetic-key"))
        XCTAssertNil(controls.updateProvider("fixture", key: "synthetic-key", using: api, route: route))
        XCTAssertNil(controls.signOutProvider(using: api, route: route))
        let newRoute = UUID()
        await controls.refresh(using: next, route: newRoute)
        XCTAssertTrue(controls.ready(newRoute))
        let reconnect = await replacement.requests
        XCTAssertEqual(reconnect.map(\.httpMethod), ["GET"])
        await controls.signOutProvider(using: next, route: newRoute)?.value
        let initial = await fixture.requests
        let final = await replacement.requests
        XCTAssertEqual(initial.map(\.httpMethod), ["GET", "PUT", "GET"])
        XCTAssertEqual(final.map(\.httpMethod), ["GET", "POST", "GET"])
    }

    private static let backupStatusFixture = #"{"directory":"/srv/fixture/backups","encrypting":true,"intervalMinutes":15,"snapshots":[{"name":"fixture.enc","taken":"2026-01-01T10:00:00Z","bytes":4294967296,"encrypted":true}]}"#

    func testAdministrationStatusAndAuthenticatedWebRoutes() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let mcp = #"[{"name":"fixture","command":"synthetic-credential-command","args":["synthetic-credential-arg"],"disabled":false,"tools":["fixturetool",null],"readAt":"2026-01-01T10:00:00Z","error":"synthetic-credential-error","running":[{"agentId":"agent-1","agentName":"Fixture Agent","pid":123}]}]"#
        let clients = #"[{"name":"fixture-client","created":"2026-01-01T10:00:00Z","tokenPath":"/srv/fixture/client.token","unread":3,"token":"synthetic-credential-token"}]"#
        let fixture = StubTransport([.init(200, mcp), .init(200, clients), .init(200, Self.backupStatusFixture)] + Array(repeating: .init(200), count: 6))
        let transport = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: transport)
        let servers = try await api.mcpStatus()
        XCTAssertEqual(servers.first?.running.first?.agentId, "agent-1")
        XCTAssertEqual(servers.first?.running.first?.pid, 123)
        XCTAssertEqual(servers.first?.failed, true)
        XCTAssertEqual(servers.first?.tools.compactMap { $0 }, ["fixturetool"])
        let outside = try await api.outsideClients()
        XCTAssertEqual(outside.first?.unread, 3)
        XCTAssertEqual(outside.first?.tokenPath, "/srv/fixture/client.token")
        XCTAssertFalse(String(reflecting: servers).contains("synthetic-credential"))
        XCTAssertFalse(String(reflecting: outside).contains("synthetic-credential"))
        let info = try await api.backupStatus()
        XCTAssertEqual(info.snapshots.first?.bytes, 4294967296)
        XCTAssertEqual(info.snapshots.first?.encrypted, true)
        XCTAssertEqual(info.intervalMinutes, 15)
        try await api.reloadMCP()
        try await api.addOutsideClient(" Fixture-Client ")
        try await api.removeOutsideClient("fixture-client")
        try await api.backupNow()
        try await api.setBackupPassphrase(" synthetic-passphrase ")
        try await api.setBackupPassphrase(nil)
        do { try await api.removeOutsideClient("../other"); XCTFail("Accepted an unsafe client name") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        do { try await api.setBackupPassphrase(" \n"); XCTFail("Accepted a blank passphrase as a mutation") }
        catch { XCTAssertEqual(error as? ClientError, .malformed) }
        let guest = API(endpoint: endpoint, transport: transport)
        do { _ = try await guest.outsideClients(); XCTFail("Listed without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { try await guest.reloadMCP(); XCTFail("Reloaded without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        do { try await guest.setBackupPassphrase(nil); XCTFail("Changed encryption without Bearer") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "GET", "GET", "POST", "POST", "DELETE", "POST", "PUT", "PUT"])
        XCTAssertEqual(requests.compactMap { $0.url?.absoluteString }, [
            "/api/mcp", "/api/clients", "/api/backups", "/api/mcp/refresh", "/api/clients", "/api/clients/fixture-client",
            "/api/backups", "/api/backups/passphrase", "/api/backups/passphrase"
        ].map { endpoint.origin + $0 })
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
        XCTAssertEqual(try Self.body(requests[4]), ["name": "fixture-client"])
        XCTAssertTrue(try Self.body(requests[7]) == ["key": "synthetic-passphrase"])
        let removal = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(requests[8].httpBody)) as? [String: Any])
        XCTAssertTrue(removal["key"] is NSNull)
    }

    @MainActor func testBackupMutationCancellationAndFailureAreReadOnlyOnReconnect() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let fixture = StubTransport([.init(200, Self.backupStatusFixture), .init(502, "synthetic-passphrase echoed by upstream"), .init(503)])
        let replacement = StubTransport([.init(200, Self.backupStatusFixture)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: fixture)
        let next = API(endpoint: endpoint, token: "fixture-bearer", transport: replacement)
        let state = OperationList<BackupInfo>()
        await state.refresh { [try await api.backupStatus()] }
        let cancelled = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await api.backupNow()
        }
        do { try await cancelled.value; XCTFail("A cancelled backup request should not start") }
        catch { XCTAssertTrue(error is CancellationError) }
        await state.change({ try await api.setBackupPassphrase("synthetic-passphrase") }, read: { [try await api.backupStatus()] })
        XCTAssertTrue(state.needsRead)
        XCTAssertEqual(state.items?.first?.encrypting, true)
        XCTAssertTrue(state.actionError?.contains("502") == true)
        XCTAssertFalse(state.actionError?.contains("synthetic-passphrase") == true)
        await state.change({ XCTFail("Mutation must be blocked until a fresh read") }, read: { [] })
        await state.refresh { [try await next.backupStatus()] }
        XCTAssertFalse(state.needsRead)
        let initial = await fixture.requests
        let reconnect = await replacement.requests
        XCTAssertEqual(initial.map(\.httpMethod), ["GET", "PUT", "GET"])
        XCTAssertEqual(reconnect.map(\.httpMethod), ["GET"])
    }

    func testInviteExpiresAndIsPinnedToExpectedAuthorityAndKey() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let expiry = "2026-11-25T06:35:00.123Z"
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let expiryDate = try XCTUnwrap(formatter.date(from: expiry))
        func reply(_ payload: String) throws -> StubTransport.Reply {
            let data = try JSONEncoder().encode(["payload": payload, "expiresAt": expiry])
            return .init(200, String(decoding: data, as: UTF8.self))
        }
        let fixture = StubTransport(try [reply(Self.fixture()), reply(Self.fixture()),
                                         reply(Self.fixture("https://wrong.tail-test1.ts.net")), reply(Self.fixture(key: false))])
        let route = try TailnetTransport(endpoint: endpoint, transport: fixture)
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: route)
        let before = expiryDate.addingTimeInterval(-5)
        let invite = try await api.createDeviceInvite(key: Self.fixtureKey, mode: .embedded) { before }
        XCTAssertEqual(invite.expiresAt, expiryDate)
        XCTAssertNotNil(invite.image(at: before))
        XCTAssertNil(invite.image(at: expiryDate))
        XCTAssertFalse(String(reflecting: invite).contains(Self.fixtureKey))
        do { _ = try await api.createDeviceInvite(key: Self.fixtureKey, mode: .embedded) { expiryDate }; XCTFail("Accepted expired QR") }
        catch { XCTAssertEqual(error as? ClientError, .pairExpired) }
        do { _ = try await api.createDeviceInvite(key: Self.fixtureKey, mode: .embedded) { before }; XCTFail("Accepted different authority") }
        catch { XCTAssertEqual(error as? ClientError, .pairOffer) }
        _ = try await api.createDeviceInvite(key: nil, mode: .direct) { before }
        do { _ = try await api.createDeviceInvite(key: "invalid", mode: .embedded); XCTFail("Sent invalid key") }
        catch { XCTAssertEqual(error as? ClientError, .pairOffer) }
        do { _ = try await API(endpoint: endpoint, transport: route).pairedDevices(); XCTFail("Listed without credential") }
        catch { XCTAssertEqual(error as? ClientError, .unauthorized) }
        let requests = await fixture.requests
        XCTAssertEqual(requests.count, 4)
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "POST" && $0.url?.absoluteString == endpoint.origin + "/api/pair/create" && $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-bearer" })
        XCTAssertTrue(try Self.body(requests[0]) == ["tsAuthKey": Self.fixtureKey])
        XCTAssertNil(requests.last?.httpBody)
    }

    @MainActor func testUncertainDeviceRevokeRequiresReadAndDetectsRejectedCurrentSession() async throws {
        let endpoint = try Endpoint(Self.fixtureOrigin)
        let id = try XCTUnwrap(UUID(uuidString: "f12af00d-03c2-4c7a-85f0-107568e70000"))
        let listing = #"{"enabled":true,"requiresTailscaleEnrollment":true,"devices":[{"id":"f12af00d-03c2-4c7a-85f0-107568e70000","name":"Fixture","createdAt":"2026-11-25T06:30:00Z"}]}"#
        let original = StubTransport([.init(200, listing), .init(502), .init(503)])
        let reconnect = StubTransport([.init(200, listing), .init(204), .init(403), .init(403)])
        let api = API(endpoint: endpoint, token: "fixture-bearer", transport: original)
        let next = API(endpoint: endpoint, token: "fixture-bearer", transport: reconnect)
        let state = DeviceManagementState()
        await state.refresh(api)
        XCTAssertEqual(state.listing?.devices.first?.id, id)
        XCTAssertEqual(state.listing?.requiresTailscaleEnrollment, true)
        await state.revoke(id, using: api)
        XCTAssertFalse(state.ready)
        XCTAssertFalse(state.rejectedSession)
        XCTAssertNotNil(state.actionError)
        await state.revoke(id, using: api)
        await state.refresh(next)
        let reads = await reconnect.requests
        XCTAssertEqual(reads.map(\.httpMethod), ["GET"])
        XCTAssertTrue(state.ready)
        await state.revoke(id, using: next)
        XCTAssertTrue(state.rejectedSession)
        XCTAssertNil(state.listing)
        let initial = await original.requests
        let final = await reconnect.requests
        XCTAssertEqual(initial.map(\.httpMethod), ["GET", "DELETE", "GET"])
        XCTAssertEqual(final.map(\.httpMethod), ["GET", "DELETE", "GET", "GET"])
        XCTAssertEqual(final.map { $0.url?.path }, ["/api/pair/devices", "/api/pair/devices/f12af00d-03c2-4c7a-85f0-107568e70000", "/api/pair/devices", "/api/status"])
    }

    private static func body(_ request: URLRequest) throws -> [String: String] {
        let body = try XCTUnwrap(request.httpBody)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: String])
    }
}

private actor GatedPendingTransport: Transport {
    let lateStarted: XCTestExpectation
    let deleteStarted: XCTestExpectation
    private var late: CheckedContinuation<Void, Never>?
    private var deletion: CheckedContinuation<Void, Never>?
    private(set) var requests: [URLRequest] = []

    init(lateStarted: XCTestExpectation, deleteStarted: XCTestExpectation) {
        self.lateStarted = lateStarted
        self.deleteStarted = deleteStarted
    }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        let body: String
        if request.httpMethod == "DELETE" {
            await withCheckedContinuation { deletion = $0; deleteStarted.fulfill() }
            body = #"{"id":"q","text":"Returned\ntext","mode":"queued"}"#
        } else {
            if requests.count > 1 {
                await withCheckedContinuation { late = $0; lateStarted.fulfill() }
            }
            body = #"[{"id":"q","text":"Original preview","mode":"queued"}]"#
        }
        return (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
    }

    func releaseLate() { late?.resume(); late = nil }
    func releaseDelete() { deletion?.resume(); deletion = nil }
}

private actor StubTransport: Transport {
    struct Reply {
        let code: Int
        let body: String
        let mime: String?
        let failure: URLError.Code?
        init(_ code: Int, _ body: String = "", mime: String? = nil) {
            self.code = code; self.body = body; self.mime = mime; self.failure = nil
        }
        init(failure: URLError.Code) {
            self.code = 0; self.body = ""; self.mime = nil; self.failure = failure
        }
    }
    var requests: [URLRequest] = []
    var replies: [Reply]

    init(_ replies: [Reply]) { self.replies = replies }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        guard !replies.isEmpty else { throw URLError(.badServerResponse) }
        let reply = replies.removeFirst()
        if let failure = reply.failure { throw URLError(failure) }
        return (Data(reply.body.utf8), HTTPURLResponse(url: request.url!, statusCode: reply.code,
                                                       httpVersion: "HTTP/1.1", headerFields: reply.mime.map { ["Content-Type": $0] })!)
    }
}
