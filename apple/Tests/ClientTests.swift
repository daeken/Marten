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
        XCTAssertEqual(state.questions(for: "other", entries: []).first?.answer, "Previously saved")
        XCTAssertEqual(state.questions(for: "task-42", entries: []).map(\.id), ["opt", "words", "no-note"])
        state.drafts["opt"] = " note \n"
        state.drafts["words"] = " custom "
        await state.answer("opt", choice: 1, using: api)
        await state.answer("words", choice: nil, using: api)
        await state.answer("no-note", choice: 0, using: api)
        await state.answer("saved", choice: nil, using: api)
        await state.answer("opt", choice: 1, using: api)
        XCTAssertEqual(state.messages?.compactMap(\.answer), ["Two: note", "custom", "One", "Previously saved"])
        XCTAssertTrue(state.drafts.isEmpty)
        let delivery = Entry(seq: 1, kind: "message", text: "Sera answered your question \"Which label?\":\n\nTwo: note", from: "Sera", name: nil, reason: nil)
        XCTAssertEqual(state.questions(for: "task-42", entries: [delivery]).map(\.id), ["words", "no-note"])
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
