import Foundation
import XCTest
@testable import Marten

final class ArtifactTests: XCTestCase, @unchecked Sendable {
    private let id = String(repeating: "b", count: 32)
    private func ref(kind: String = "text", bytes: Int = 3) -> ArtifactReference {
        ArtifactReference(id: id, name: "fixture.txt", bytes: bytes, previewKind: kind)
    }

    func testOptionalAndMalformedReferencesDoNotBreakTranscript() throws {
        let decoder = JSONDecoder()
        let plain = try decoder.decode(Entry.self, from: Data(#"{"seq":1,"kind":"tool_result"}"#.utf8))
        XCTAssertTrue(plain.artifacts.isEmpty)
        let refs = Data(#"{"seq":2,"kind":"tool_result","artifacts":[null,{"id":"../bad"},{"id":"\#(id)","name":"test","bytes":3,"previewKind":"future"},{"id":"\#(id)","name":"duplicate","bytes":3,"previewKind":"text"}]}"#.utf8)
        let entry = try decoder.decode(Entry.self, from: refs)
        XCTAssertEqual(entry.artifacts.count, 1)
        XCTAssertNil(entry.artifacts.first?.preview)
    }

    func testOnlyExplicitPairedDownloadsAndPreviewAreAgentScoped() async throws {
        let endpoint = try Endpoint("https://node.tail-test.ts.net")
        let raw = ArtifactFixture(data: Data("abc".utf8), mime: "application/octet-stream")
        let direct = API(endpoint: endpoint, token: "test", transport: raw)
        XCTAssertFalse(direct.supportsArtifacts)
        do { _ = try await direct.artifact(ref(), agent: "agent-A", purpose: .download); XCTFail() }
        catch ClientError.unauthorized {}
        let paired = API(endpoint: endpoint, token: "test", transport: try TailnetTransport(endpoint: endpoint, transport: raw))
        let result = try await paired.artifact(ref(kind: "future"), agent: "agent-A", purpose: .download)
        guard case .download(let body) = result else { return XCTFail() }
        XCTAssertEqual(body, Data("abc".utf8))
        let requests = await raw.requests
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(requests.first?.httpMethod, "GET")
        XCTAssertEqual(requests.first?.url?.absoluteString, "https://node.tail-test.ts.net/api/agents/agent-A/artifacts/\(id)")
        XCTAssertEqual(requests.first?.value(forHTTPHeaderField: "Authorization"), "Bearer test")
        do { _ = try await paired.artifact(ref(), agent: "../bad", purpose: .download); XCTFail() }
        catch ClientError.malformed {}
        do { _ = try await paired.artifact(ref(kind: "future"), agent: "agent-A", purpose: .text); XCTFail() }
        catch ClientError.attachment {}
        let after = await raw.requests.count
        XCTAssertEqual(after, 1)

        let text = ArtifactFixture(data: Data("abc".utf8), mime: "text/plain; charset=utf-8", extra: ["X-Marten-Preview-Truncated": "1"])
        let preview = API(endpoint: endpoint, token: "test", transport: try TailnetTransport(endpoint: endpoint, transport: text))
        let payload = try await preview.artifact(ref(), agent: "agent-A", purpose: .text)
        guard case .text(let content, let truncated) = payload else { return XCTFail() }
        XCTAssertEqual(content, "abc")
        XCTAssertTrue(truncated)
        let request = await text.requests.first
        XCTAssertEqual(request?.url?.path, "/api/agents/agent-A/artifacts/\(id)/preview")
    }

    func testPNGRejectsTruncationAndTrailingBytesWithoutLengthHeader() throws {
        let png = try XCTUnwrap(Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jP1sAAAAASUVORK5CYII="))
        let reply = try XCTUnwrap(HTTPURLResponse(url: URL(string: "https://node.tail-test.ts.net")!, statusCode: 200,
                                                httpVersion: "HTTP/1.1", headerFields: ["Content-Type": "image/png", "Content-Disposition": "attachment"]))
        XCTAssertNoThrow(try ArtifactPayload(data: png, reply: reply, reference: ref(kind: "png"), purpose: .png))
        XCTAssertThrowsError(try ArtifactPayload(data: Data(png.dropLast()), reply: reply, reference: ref(kind: "png"), purpose: .png))
        XCTAssertThrowsError(try ArtifactPayload(data: png + Data([0]), reply: reply, reference: ref(kind: "png"), purpose: .png))
    }

    func testOldPickerCallbacksDoNotMutateReplacementExport() {
        var slot = ArtifactExportSlot()
        slot.begin(Data([1]))
        let retired = slot.document?.id
        slot.reset()
        slot.begin(Data([2]))
        let current = slot.document?.id
        slot.setPresented(false, for: retired)
        XCTAssertFalse(slot.finish(retired))
        XCTAssertFalse(slot.finish(nil))
        XCTAssertTrue(slot.presented)
        XCTAssertEqual(slot.document?.id, current)
        XCTAssertEqual(slot.document?.data, Data([2]))
        slot.setPresented(false, for: current)
        XCTAssertFalse(slot.presented)
        XCTAssertEqual(slot.document?.id, current, "Wait for completion or cancellation to release bytes")
        XCTAssertTrue(slot.finish(current))
        XCTAssertNil(slot.document)
    }

    func testErrorAndFramingChecks() async throws {
        let endpoint = try Endpoint("https://node.tail-test.ts.net")
        func api(_ fixture: ArtifactFixture) throws -> API {
            API(endpoint: endpoint, token: "test", transport: try TailnetTransport(endpoint: endpoint, transport: fixture))
        }
        let missing = try api(.init(data: Data(), mime: "text/plain", status: 404))
        do { _ = try await missing.artifact(ref(), agent: "a", purpose: .download); XCTFail() }
        catch is ArtifactUnavailable {}
        let redirect = try api(.init(data: Data(), mime: "text/plain", status: 307, extra: ["Location": "https://elsewhere.invalid/"]))
        do { _ = try await redirect.artifact(ref(), agent: "a", purpose: .download); XCTFail() }
        catch ClientError.response(307) {}
        for foreign in ["https://elsewhere.invalid/api/agents/a/artifacts/\(id)", "https://node.tail-test.ts.net/other"] {
            let wrongSource = try api(.init(data: Data("abc".utf8), mime: "application/octet-stream", responseURL: URL(string: foreign)))
            do { _ = try await wrongSource.artifact(ref(), agent: "a", purpose: .download); XCTFail() }
            catch ClientError.origin {}
        }
        for fixture in [
            ArtifactFixture(data: Data("abc".utf8), mime: "application/octet-stream", extra: ["Content-Length": "4"]),
            ArtifactFixture(data: Data("ab".utf8), mime: "application/octet-stream"),
            ArtifactFixture(data: Data("abc".utf8), mime: "text/html"),
            ArtifactFixture(data: Data("abc".utf8), mime: "application/octet-stream", status: 206),
        ] {
            do { _ = try await api(fixture).artifact(ref(), agent: "a", purpose: .download); XCTFail() }
            catch ClientError.attachment {}
        }
        let tooLarge = try api(.init(data: Data(count: 65_537), mime: "text/plain; charset=utf-8"))
        do { _ = try await tooLarge.artifact(ref(), agent: "a", purpose: .text); XCTFail() }
        catch ClientError.attachment {}
    }
}

private actor ArtifactFixture: ArtifactByteTransport {
    let body: Data
    let mime: String
    let status: Int
    let extra: [String: String]
    let responseURL: URL?
    private(set) var requests: [URLRequest] = []

    init(data: Data, mime: String, status: Int = 200, extra: [String: String] = [:], responseURL: URL? = nil) {
        body = data; self.mime = mime; self.status = status; self.extra = extra; self.responseURL = responseURL
    }

    func artifactBytes(for request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        try await data(for: request)
    }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        let headers = ["Content-Type": mime, "Content-Disposition": "attachment"].merging(extra) { _, newest in newest }
        return (body, HTTPURLResponse(url: responseURL ?? request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!)
    }
}
