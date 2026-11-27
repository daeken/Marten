import Foundation
import XCTest
@testable import Marten

final class MacFilesClientTests: XCTestCase, @unchecked Sendable {
    private let endpoint = try! Endpoint("https://fixture.example.ts.net")
    private let token = "fake-paired-fixture"

    func testOnlyTheMatchingOptInTailnetRouteIsAccepted() throws {
        let fixture = Reader()
        let route = try TailnetTransport(endpoint: endpoint, transport: fixture)
        XCTAssertNil(MacFilesClient(api: API(endpoint: endpoint, token: token, transport: fixture)))
        XCTAssertNil(MacFilesClient(api: API(endpoint: endpoint, token: nil, transport: route)))
        XCTAssertNil(MacFilesClient(api: API(endpoint: try Endpoint("https://other.example.ts.net"), token: token, transport: route)))
        XCTAssertNotNil(MacFilesClient(api: API(endpoint: endpoint, token: token, transport: route)))
    }

    func testReadOnlyRoutesPaginationAndExplicitExport() async throws {
        let fixture = Reader()
        let client = try client(fixture)
        let roots = try await client.roots()
        XCTAssertEqual(roots.map(\.label), ["Home", "Volumes"])
        let first = try await client.page(roots[0].id)
        let second = try await client.page(try XCTUnwrap(first.next))
        let row = try XCTUnwrap(second.entries.first)
        let before = await fixture.calls
        XCTAssertEqual(before, ["/api/mac-files/roots", "/api/mac-files/entries/home", "/api/mac-files/entries/page_2"])
        let preview = try await client.preview(row, .text)
        XCTAssertEqual(preview, Data("test".utf8))
        let file = try await client.share(row)
        XCTAssertEqual(file.url.lastPathComponent, "_sample.txt")
        XCTAssertEqual(try Data(contentsOf: file.url), preview)
        file.discardUnpublished()
        XCTAssertFalse(FileManager.default.fileExists(atPath: file.url.path))
        let after = await fixture.calls
        XCTAssertEqual(after, before + ["/api/mac-files/bytes/file/text", "/api/mac-files/bytes/file/share"])
    }

    func testHandlesAndSizeLimitsFailBeforeTransport() async throws {
        let fixture = Reader()
        let client = try client(fixture)
        for invalid in ["", "../foo", "page?x=y", "a%2Fb", "a/b", "o#f", "å", String(repeating: "a", count: 256)] {
            do { _ = try await client.page(invalid); XCTFail("invalid handle accepted") }
            catch { XCTAssertEqual(error as? ClientError, .malformed) }
        }
        for (purpose, max) in [(MacFilePurpose.raster, 8 * 1024 * 1024), (.share, 32 * 1024 * 1024)] {
            XCTAssertEqual(try purpose.count(for: row(bytes: Int64(max))), max)
            XCTAssertThrowsError(try purpose.count(for: row(bytes: Int64(max + 1))))
        }
        XCTAssertEqual(try MacFilePurpose.text.count(for: row(bytes: 5_000_000)), 256 * 1024)
        XCTAssertThrowsError(try MacFilePurpose.text.count(for: row(bytes: -1)))
        XCTAssertThrowsError(try MacFilePurpose.share.count(for: row(bytes: nil)))
        let calls = await fixture.calls
        XCTAssertTrue(calls.isEmpty)
    }

    func testResponseRequiresExactOriginStatusLengthAndAttachment() throws {
        let url = endpoint.url.appending(path: "api/mac-files/bytes/file/share")
        let request = URLRequest(url: url)
        let policy = MacFilesRead(limit: 4, exact: 4)
        func reply(status: Int = 200, target: URL? = nil, mime: String = "application/octet-stream",
                   count: String = "4", disposition: String = "attachment", encoding: String = "identity") -> HTTPURLResponse {
            HTTPURLResponse(url: target ?? url, statusCode: status, httpVersion: nil, headerFields: [
                "Content-Type": mime, "Content-Length": count, "Content-Disposition": disposition, "Content-Encoding": encoding
            ])!
        }
        try policy.check(reply(disposition: "attachment; filename=ignored"), for: request)
        for result in [reply(status: 302), reply(status: 206), reply(target: endpoint.url), reply(mime: "text/html"),
                       reply(count: "3"), reply(count: "5"), reply(disposition: "inline"), reply(encoding: "gzip")] {
            XCTAssertThrowsError(try policy.check(result, for: request))
        }
        XCTAssertThrowsError(try policy.check(reply(status: 401), for: request)) { XCTAssertEqual($0 as? ClientError, .unauthorized) }
    }

    func testTransferShortOverlongFailedAndCancelledNeverPublishes() async throws {
        for (mode, expected) in [(Reader.Mode.short, ClientError.malformed), (.long, .attachment), (.fail, .network)] {
            let fixture = Reader(mode: mode)
            let client = try client(fixture)
            do { _ = try await client.share(row(bytes: 4)); XCTFail("invalid export published") }
            catch { XCTAssertEqual(error as? ClientError, expected) }
            let partial = await fixture.staging
            XCTAssertFalse(FileManager.default.fileExists(atPath: try XCTUnwrap(partial).path))
        }
        let fixture = Reader(mode: .suspend)
        let client = try client(fixture)
        let entry = row(bytes: 4)
        let transfer = Task { try await client.share(entry) }
        let partial = await fixture.waitForStaging()
        transfer.cancel()
        do { _ = try await transfer.value; XCTFail("cancelled export published") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: partial.path))
    }

    private func client(_ fixture: Reader) throws -> MacFilesClient {
        try XCTUnwrap(MacFilesClient(api: API(endpoint: endpoint, token: token,
                                              transport: TailnetTransport(endpoint: endpoint, transport: fixture))))
    }

    private func row(bytes: Int64?) -> MacFileEntry {
        MacFileEntry(id: "file", name: "sample.txt", directory: false, bytes: bytes, modified: "2026-01-01T00:00:00Z")
    }

    private actor Reader: MacFilesTransport {
        enum Mode { case valid, short, long, fail, suspend }
        let mode: Mode
        private(set) var calls: [String] = []
        private(set) var staging: URL?
        private var staged: CheckedContinuation<URL, Never>?

        init(mode: Mode = .valid) { self.mode = mode }

        func waitForStaging() async -> URL {
            if let staging { return staging }
            return await withCheckedContinuation { staged = $0 }
        }

        func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
            XCTFail("must not fall back to an unbounded data request")
            throw ClientError.malformed
        }

        func readMacFiles(_ request: URLRequest, policy: MacFilesRead, staging: URL?) async throws -> Data {
            let url = try XCTUnwrap(request.url)
            XCTAssertEqual(url.host, "fixture.example.ts.net")
            XCTAssertNil(url.query)
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertNil(request.httpBody)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer fake-paired-fixture")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
            calls.append(url.path)
            if let staging {
                self.staging = staging
                staged?.resume(returning: staging)
                staged = nil
            }
            let data: Data
            switch url.path {
            case "/api/mac-files/roots":
                data = Data(#"[{"id":"home","label":"Home"},{"id":"volumes","label":"Volumes"}]"#.utf8)
            case "/api/mac-files/entries/home":
                data = Data(#"{"entries":[],"next":"page_2"}"#.utf8)
            case "/api/mac-files/entries/page_2":
                data = Data(#"{"entries":[{"id":"file","name":"../sample.txt","directory":false,"bytes":4,"modified":"2026-01-01T00:00:00Z"}]}"#.utf8)
            default:
                switch mode {
                case .valid: data = Data("test".utf8)
                case .short: data = Data("tes".utf8)
                case .long: data = Data("tests".utf8)
                case .fail: throw ClientError.network
                case .suspend:
                    try await Task.sleep(for: .seconds(10))
                    throw ClientError.network
                }
            }
            let reply = HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: [
                "Content-Type": policy.mime, "Content-Length": String(policy.exact ?? data.count), "Content-Disposition": "attachment"
            ])!
            try policy.check(reply, for: request)
            return try await policy.consume(AsyncStream { continuation in
                for byte in data { continuation.yield(byte) }
                continuation.finish()
            }, staging: staging)
        }
    }
}
