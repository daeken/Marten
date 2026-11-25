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
