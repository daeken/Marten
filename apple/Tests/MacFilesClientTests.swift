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

    @MainActor func testLatePublicationRejectsDestinationSessionRouteAndGeneration() throws {
        let session = UUID(), route = UUID(), generation = UUID()
        let scope = MacFilesSession.Key(endpoint: endpoint, pairedSession: session, route: route)
        let destination = MacFilesDestination.file(row(bytes: 4))
        let started = MacFilesWork(scope: scope, destination: destination, generation: generation)
        let changed: [MacFilesWork] = [
            .init(scope: scope, destination: .folder("home", "Home"), generation: generation),
            .init(scope: scope, destination: .file(row(bytes: 5)), generation: generation),
            .init(scope: scope, destination: destination, generation: UUID()),
            .init(scope: .init(endpoint: endpoint, pairedSession: session, route: UUID()), destination: destination, generation: generation),
            .init(scope: .init(endpoint: endpoint, pairedSession: UUID(), route: route), destination: destination, generation: generation),
            .init(scope: .init(endpoint: try Endpoint("https://other.example.ts.net"), pairedSession: session, route: route),
                  destination: destination, generation: generation)
        ]
        XCTAssertTrue(started.permits(started, destinationIsCurrent: true))
        XCTAssertFalse(started.permits(started, destinationIsCurrent: false))
        for current in changed {
            XCTAssertFalse(started.permits(current, destinationIsCurrent: true)) // preview
            let unpublished = try MacFilesStagedCopy.create(leaf: "test")
            XCTAssertNil(started.share(unpublished, current: current, destinationIsCurrent: true))
            XCTAssertFalse(FileManager.default.fileExists(atPath: unpublished.url.path))
        }
    }

    @MainActor func testCancelledLatePreviewCannotPublishAndShareLeaseSurvivesDismissal() async throws {
        let key = MacFilesWork(scope: .init(endpoint: endpoint, pairedSession: UUID(), route: UUID()),
                               destination: .file(row(bytes: 4)), generation: UUID())
        let latch = Latch()
        let preview = Task {
            await latch.park()
            return key.permits(key, destinationIsCurrent: true)
        }
        await latch.waitUntilParked()
        preview.cancel()
        await latch.release()
        let canPublish = await preview.value
        XCTAssertFalse(canPublish)

        let activity = try XCTUnwrap(key.share(MacFilesStagedCopy.create(leaf: "test"),
                                               current: key, destinationIsCurrent: true))
        var lease = activity.leaseForActivity(current: key, destinationIsCurrent: true)
        let path = try XCTUnwrap(lease).url.path
        activity.requestCleanup() // expiry, navigation, or revocation only dismisses
        XCTAssertNil(activity.leaseForActivity(current: key, destinationIsCurrent: true))
        withExtendedLifetime(lease) {
            XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        }
        lease = nil // UIKit completes and releases ownership
        XCTAssertFalse(FileManager.default.fileExists(atPath: path))
    }

    @MainActor func testAcquisitionRevalidatesEpochAndDelayedCompletionKeepsNewShare() throws {
        let scope = MacFilesSession.Key(endpoint: endpoint, pairedSession: UUID(), route: UUID())
        let original = MacFilesWork(scope: scope, destination: .file(row(bytes: 4)), generation: UUID(), foregroundEpoch: UUID())
        var changed = original
        changed.foregroundEpoch = UUID()
        let old = try XCTUnwrap(original.share(MacFilesStagedCopy.create(leaf: "old"), current: original, destinationIsCurrent: true))
        XCTAssertNil(old.leaseForActivity(current: changed, destinationIsCurrent: true))
        XCTAssertNil(old.leaseForActivity(current: original, destinationIsCurrent: false))
        var oldLease = old.leaseForActivity(current: original, destinationIsCurrent: true)
        let oldPath = try XCTUnwrap(oldLease).url.path
        var new = try changed.share(MacFilesStagedCopy.create(leaf: "new"), current: changed, destinationIsCurrent: true)
        XCTAssertNotNil(new)
        let newID = new?.id
        old.finish(current: &new) // late system dismissal from the previous share
        XCTAssertEqual(new?.id, newID)
        withExtendedLifetime(oldLease) { XCTAssertTrue(FileManager.default.fileExists(atPath: oldPath)) }
        oldLease = nil
        XCTAssertFalse(FileManager.default.fileExists(atPath: oldPath))
    }

    @MainActor func testForegroundRoundTripNeverPreservesUnacquiredCopyButKeepsAcquiredActivity() throws {
        let scope = MacFilesSession.Key(endpoint: endpoint, pairedSession: UUID(), route: UUID())
        let initial = MacFilesWork(scope: scope, destination: .file(row(bytes: 4)), generation: UUID(), foregroundEpoch: UUID())
        var resumed = initial
        resumed.foregroundEpoch = UUID()
        var first: MacFilesShare? = try initial.share(MacFilesStagedCopy.create(leaf: "unacquired"),
                                                      current: initial, destinationIsCurrent: true)
        let before = try XCTUnwrap(first)
        XCTAssertFalse(before.preservesPresentation(inBackground: false))
        XCTAssertNil(before.leaseForActivity(current: initial, destinationIsCurrent: false)) // inactive
        before.finish(current: &first)
        XCTAssertNil(first)
        XCTAssertNil(before.leaseForActivity(current: resumed, destinationIsCurrent: true)) // active again

        let after = try XCTUnwrap(resumed.share(MacFilesStagedCopy.create(leaf: "acquired"),
                                              current: resumed, destinationIsCurrent: true))
        let lease = try XCTUnwrap(after.leaseForActivity(current: resumed, destinationIsCurrent: true))
        withExtendedLifetime(lease) {
            XCTAssertTrue(after.preservesPresentation(inBackground: false)) // inactive
            XCTAssertTrue(after.preservesPresentation(inBackground: false)) // active again
            XCTAssertFalse(after.preservesPresentation(inBackground: true))
        }
    }

    func testCancelledAfterWritingAChunkDeletesPartialShare() async throws {
        let latch = Latch()
        let fixture = Reader(mode: .midBody(latch))
        let client = try client(fixture)
        let entry = row(bytes: 128 * 1024 + 1)
        let transfer = Task { try await client.share(entry) }
        await latch.waitUntilParked()
        let staging = await fixture.staging
        let partial = try XCTUnwrap(staging)
        let observed = try Data(contentsOf: partial).count
        XCTAssertEqual(observed, 128 * 1024)
        transfer.cancel()
        await latch.release()
        do { _ = try await transfer.value; XCTFail("cancelled staged body published") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: partial.path))
    }

    @MainActor func testSupersededRouteDiscardsCompletedShareAfterStagingMidBody() async throws {
        let latch = Latch()
        let fixture = Reader(mode: .midBody(latch))
        let client = try client(fixture)
        let entry = row(bytes: 128 * 1024 + 1)
        let started = MacFilesWork(scope: .init(endpoint: endpoint, pairedSession: UUID(), route: UUID()),
                                   destination: .file(entry), generation: UUID(), foregroundEpoch: UUID())
        let transfer = Task { try await client.share(entry) }
        await latch.waitUntilParked()
        let staging = await fixture.staging
        let partial = try XCTUnwrap(staging)
        XCTAssertEqual(try Data(contentsOf: partial).count, 128 * 1024)
        let newRoute = MacFilesWork(scope: .init(endpoint: endpoint, pairedSession: started.scope.pairedSession, route: UUID()),
                                    destination: started.destination, generation: started.generation,
                                    foregroundEpoch: started.foregroundEpoch)
        await latch.release()
        let finished = try await transfer.value
        XCTAssertNil(started.share(finished, current: newRoute, destinationIsCurrent: true))
        XCTAssertFalse(FileManager.default.fileExists(atPath: partial.path))
    }

    /// The real URLSession AsyncBytes reader, with a private in-process URLProtocol
    /// instead of a socket. No DNS, live service, Tailscale node, or real credential.
    func testURLSessionMidBodyCancellation() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ParkedURLProtocol.self]
        let transport = URLSessionTransport(configuration: config)
        defer { transport.invalidate() }
        let route = try TailnetTransport(endpoint: endpoint, transport: transport)
        let copy = try MacFilesStagedCopy.create(leaf: "url-protocol.txt")
        defer { copy.discardUnpublished() }
        var request = URLRequest(url: endpoint.url.appending(path: "api/mac-files/bytes/fixture/share"))
        request.setValue("Bearer fake-paired-fixture", forHTTPHeaderField: "Authorization")
        let finished = expectation(description: "URLSession byte reader completed")
        let outcome = CancellationOutcome()
        let transfer = Task { [request] in
            do {
                _ = try await route.readMacFiles(request, policy: .init(limit: ParkedURLProtocol.exact, exact: ParkedURLProtocol.exact), staging: copy.url)
                outcome.save(.success(()))
            } catch {
                outcome.save(.failure(error))
            }
            finished.fulfill()
        }
        defer { transfer.cancel() }
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        var stagedSize = 0
        while clock.now < deadline {
            stagedSize = (try? copy.url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
            if stagedSize > 64 * 1024 { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        transfer.cancel()
        let watchdog = Task.detached {
            do { try await Task.sleep(for: .seconds(2)) } catch { return }
            if outcome.markWatchdogIfUnfinished() { transport.invalidate() }
        }
        defer { watchdog.cancel() }
        XCTAssertGreaterThan(stagedSize, 64 * 1024, "URLSession never staged two chunks")
        await fulfillment(of: [finished], timeout: 4)
        let (result, watchdogFired) = outcome.snapshot()
        XCTAssertFalse(watchdogFired, "task cancellation did not finish before the isolated URLSession watchdog")
        guard let result else { XCTFail("URLSession reader never returned"); return }
        switch result {
        case .success: XCTFail("cancelled URLSession body returned successfully")
        case .failure(let error as URLError): XCTAssertEqual(error.code, .cancelled)
        case .failure(let error): XCTAssertTrue(error is CancellationError, "unexpected error \(error)")
        }
    }

    private final class CancellationOutcome: @unchecked Sendable {
        private let lock = NSLock()
        private var result: Result<Void, any Error>?
        private var watchdogFired = false

        func save(_ result: Result<Void, any Error>) { lock.withLock { self.result = result } }
        func markWatchdogIfUnfinished() -> Bool {
            lock.withLock {
                guard result == nil else { return false }
                watchdogFired = true
                return true
            }
        }
        func snapshot() -> (Result<Void, any Error>?, Bool) { lock.withLock { (result, watchdogFired) } }
    }

    private final class ParkedURLProtocol: URLProtocol, @unchecked Sendable {
        static let exact = 256 * 1024
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

        override func startLoading() {
            guard let url = request.url, let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: [
                "Content-Type": "application/octet-stream", "Content-Length": String(Self.exact), "Content-Disposition": "attachment"
            ]) else { client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse)); return }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(repeating: 1, count: 160 * 1024))
            // Deliberately omit finish: waiter cancels during body transfer.
        }
        override func stopLoading() {}
    }

    func testListingCapsRowsAndEmptyFreshCursorsIndependently() throws {
        var empty = MacFilesListing()
        for i in 0..<MacFilesListing.pageLimit {
            try empty.append(.init(entries: [], next: "page-\(i+1)"), requested: "page-\(i)")
        }
        XCTAssertTrue(empty.reachedLimit)
        XCTAssertNil(empty.next)
        XCTAssertThrowsError(try empty.append(.init(entries: [], next: "never"), requested: "never"))

        let rows = (0..<MacFilesListing.rowLimit).map {
            MacFileEntry(id: "row-\($0)", name: "file", directory: false, bytes: 4, modified: "time")
        }
        var listing = MacFilesListing()
        try listing.append(.init(entries: rows, next: "fresh"), requested: "home")
        XCTAssertTrue(listing.reachedLimit)
        XCTAssertNil(listing.next)
        XCTAssertEqual(listing.rows.count, MacFilesListing.rowLimit)
        var loop = MacFilesListing()
        try loop.append(.init(entries: [], next: "two"), requested: "one")
        XCTAssertThrowsError(try loop.append(.init(entries: [], next: "one"), requested: "two"))
    }

    private actor Latch {
        private var parked: CheckedContinuation<Void, Never>?
        private var observer: CheckedContinuation<Void, Never>?
        private var emitted = 0

        func park() async {
            await withCheckedContinuation {
                parked = $0
                observer?.resume()
                observer = nil
            }
        }
        func waitUntilParked() async {
            if parked != nil { return }
            await withCheckedContinuation { observer = $0 }
        }
        func release() { parked?.resume(); parked = nil }
        func next() async -> UInt8? {
            emitted += 1
            if emitted == 128 * 1024 + 1 { await park() }
            return emitted <= 128 * 1024 + 1 ? 1 : nil
        }
    }

    private func client(_ fixture: Reader) throws -> MacFilesClient {
        try XCTUnwrap(MacFilesClient(api: API(endpoint: endpoint, token: token,
                                              transport: TailnetTransport(endpoint: endpoint, transport: fixture))))
    }

    private func row(bytes: Int64?) -> MacFileEntry {
        MacFileEntry(id: "file", name: "sample.txt", directory: false, bytes: bytes, modified: "2026-01-01T00:00:00Z")
    }

    private actor Reader: MacFilesTransport {
        enum Mode { case valid, short, long, fail, suspend, midBody(Latch) }
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
                case .midBody(let latch):
                    return try await policy.consume(AsyncStream(unfolding: { await latch.next() }), staging: staging)
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
