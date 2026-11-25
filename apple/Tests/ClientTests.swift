import CoreImage
import CoreImage.CIFilterBuiltins
import Foundation
import XCTest
@testable import Marten

final class ClientTests: XCTestCase, @unchecked Sendable {
    func testImportQRImage() throws {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(Self.offer.utf8)
        let qr = try XCTUnwrap(filter.outputImage).transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        let canvas = qr.extent.insetBy(dx: -32, dy: -32)
        let image = qr.composited(over: CIImage(color: .white).cropped(to: canvas))
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).png")
        defer { try? FileManager.default.removeItem(at: url) }
        try CIContext().writePNGRepresentation(of: image, to: url, format: .RGBA8,
                                              colorSpace: CGColorSpaceCreateDeviceRGB())
        let offer = try PairOffer(QRImage.read(url))
        XCTAssertEqual(offer.endpoint.origin, "https://marten.example:8443")
    }

    func testOriginNormalizesAndRejectsNonOrigins() throws {
        XCTAssertEqual(try Endpoint("HTTPS://MARTEN.example:443/").origin, "https://marten.example")
        XCTAssertEqual(try Endpoint("https://marten.example:8443").origin, "https://marten.example:8443")
        for invalid in ["http://marten.example", "https://user@server.example", "https://server.example/api",
                        "https://server.example?", "https://server.example#", "https://server.example:65536",
                        "https://server.example/%2f", "marten.example", "https://%6darten.example"] {
            XCTAssertThrowsError(try Endpoint(invalid))
        }
        #if DEBUG
        XCTAssertEqual(try Endpoint("http://localhost:8080/", allowLoopback: true).origin, "http://127.0.0.1:8080")
        XCTAssertThrowsError(try Endpoint("http://127.0.0.1.evil.example", allowLoopback: true))
        XCTAssertThrowsError(try Endpoint("http://127.1", allowLoopback: true))
        #endif
    }

    func testStrictPairLink() throws {
        XCTAssertEqual(try PairOffer(Self.offer).endpoint.origin, "https://marten.example:8443")
        for invalid in [Self.offer + "&code=second", Self.offer + "#fragment", Self.offer.replacingOccurrences(of: "v=1", with: "v=2"),
                        "marten://pair?v=1&server=http%3A%2F%2F127.0.0.1&code=x", "marten://pair?v=1&server=https%3A%2F%2Fx&code="] {
            XCTAssertThrowsError(try PairOffer(invalid))
        }
    }

    @MainActor func testConfirmationGatesRedemptionAndStoresOnlyToken() async throws {
        let domain = "NativeMartenTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        let vault = CredentialStore(service: domain)
        let endpoint = try PairOffer(Self.offer).endpoint
        defer {
            defaults.removePersistentDomain(forName: domain)
            try? vault.remove(endpoint)
        }
        let transport = StubTransport([.init(200, #"{"token":"fixture-token"}"#)])
        let connections = Connections(transport: transport, keychain: vault, defaults: defaults)
        connections.receive(Self.offer)
        let before = await transport.requests
        XCTAssertTrue(before.isEmpty)
        XCTAssertNil(connections.connected)
        connections.offer = nil
        await connections.redeem(deviceName: "Fixture Device")
        let afterCancel = await transport.requests
        XCTAssertTrue(afterCancel.isEmpty)
        connections.receive(Self.offer)
        await connections.redeem(deviceName: "Fixture Device")

        let requests = await transport.requests
        let request = try XCTUnwrap(requests.first)
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.url?.absoluteString, "https://marten.example:8443/api/pair/redeem")
        XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
        XCTAssertEqual(try Self.body(request), ["code": "fixture+code=", "deviceName": "Fixture Device"])
        XCTAssertNil(connections.error)
        XCTAssertNil(connections.offer)
        XCTAssertEqual(try vault.token(for: endpoint), "fixture-token")
        let unrelated = try Endpoint("https://marten.example:8444")
        XCTAssertThrowsError(try vault.token(for: unrelated))
        let otherDomain = defaults.persistentDomain(forName: domain)
        XCTAssertEqual(otherDomain?["activeEndpoint"] as? String, endpoint.origin)
        XCTAssertEqual(otherDomain?["endpoints"] as? [String], [endpoint.origin])
        XCTAssertEqual(otherDomain?.count, 2)
        let relaunched = Connections(transport: transport, keychain: vault, defaults: defaults)
        XCTAssertEqual(relaunched.connected?.token, "fixture-token")
        relaunched.forget(endpoint)
        XCTAssertNil(relaunched.connected)
        XCTAssertThrowsError(try vault.token(for: endpoint))
    }

    func testOperatorRoutesAndPagingCursor() async throws {
        let transport = StubTransport([.init(200, """
            {"projects":[{"id":"p","name":"Project"}],"agents":[{"id":"task-42","name":"Test","status":"idle","kind":"task","projectId":"p","paused":false,"pausedEffective":false}]}
            """), .init(200, #"{"entries":[{"seq":44,"kind":"assistant","text":"Hi"}],"hasOlder":true}"#),
            .init(202), .init(200), .init(200)])
        let api = API(endpoint: try Endpoint("https://marten.example"), token: "fixture-token", transport: transport)
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
        XCTAssertEqual(try Self.body(requests[2]), ["text": "Test", "mode": "immediate"])
        XCTAssertEqual(requests[3].httpBody, Data(#"{"paused":true}"#.utf8))
        XCTAssertTrue(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token" })
    }

    func testPairErrorsDoNotSurfaceBodyOrRetry() async throws {
        let transport = StubTransport([.init(307, "never display this"), .init(401)])
        let offer = try PairOffer(Self.offer)
        do {
            _ = try await API.redeem(offer, deviceName: "Device", transport: transport)
            XCTFail("Accepted redirect")
        } catch let error as ClientError {
            XCTAssertEqual(error, .response(307))
            XCTAssertFalse(safeMessage(error).contains("never display this"))
        }
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 1)
        do {
            _ = try await API.redeem(offer, deviceName: "Device", transport: transport)
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

    private static let offer = "marten://pair?v=1&server=https%3A%2F%2FMarten.example%3A8443%2F&code=fixture%2Bcode%3D"

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
