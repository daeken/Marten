import Foundation

struct MacFilesSession {
    struct Key: Hashable, Sendable {
        let endpoint: Endpoint
        let pairedSession: UUID
        let route: UUID
    }

    let key: Key
    let foregroundEpoch: UUID?
    let isCurrent: @MainActor () -> Bool
    let canTransfer: @MainActor () -> Bool
}

struct MacFileRoot: Decodable, Identifiable, Hashable, Sendable {
    let id: String
    let label: String
}

struct MacFileEntry: Decodable, Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let directory: Bool
    let bytes: Int64?
    let modified: String
}

struct MacFilePage: Decodable, Sendable {
    let entries: [MacFileEntry]
    let next: String?
}

enum MacFilesDestination: Hashable, Sendable {
    case roots
    case folder(String, String)
    case file(MacFileEntry)
}

struct MacFilesWork: Hashable, Sendable {
    let scope: MacFilesSession.Key
    let destination: MacFilesDestination
    let generation: UUID
    var foregroundEpoch: UUID? = nil

    struct Job<ID: Hashable>: Hashable {
        let work: MacFilesWork
        let id: ID
    }

    func job<ID: Hashable>(_ id: ID) -> Job<ID> { Job(work: self, id: id) }

    func permits(_ current: Self, destinationIsCurrent: Bool) -> Bool {
        self == current && destinationIsCurrent && !Task.isCancelled
    }

    @MainActor func share(_ copy: MacFilesStagedCopy, current: Self, destinationIsCurrent: Bool) -> MacFilesShare? {
        guard permits(current, destinationIsCurrent: destinationIsCurrent) else {
            copy.discardUnpublished()
            return nil
        }
        return MacFilesShare(copy, work: self)
    }
}

/// Both bounds are independent of server-provided IDs (including empty pages).
struct MacFilesListing {
    static let rowLimit = 2_048
    static let pageLimit = 64
    private(set) var rows: [MacFileEntry] = []
    private(set) var next: String?
    private(set) var reachedLimit = false
    private var seen: Set<String> = []
    private var entryIDs: Set<String> = []

    mutating func append(_ page: MacFilePage, requested: String) throws {
        guard !reachedLimit, seen.isEmpty || requested == next, !seen.contains(requested),
              page.next.map({ $0 != requested && !seen.contains($0) }) ?? true else { throw ClientError.malformed }
        let incoming = Set(page.entries.map(\.id))
        guard incoming.count == page.entries.count, entryIDs.isDisjoint(with: incoming) else { throw ClientError.malformed }
        guard page.entries.count <= Self.rowLimit - rows.count, seen.count < Self.pageLimit else {
            next = nil
            reachedLimit = true
            return
        }
        seen.insert(requested)
        rows.append(contentsOf: page.entries)
        entryIDs.formUnion(incoming)
        reachedLimit = page.next != nil && (rows.count == Self.rowLimit || seen.count == Self.pageLimit)
        next = reachedLimit ? nil : page.next
    }
}

/// App-side presentation ownership is separate from the strong UIKit lease.
@MainActor final class MacFilesShare: Identifiable {
    let id = UUID()
    private var copy: MacFilesStagedCopy?
    private let work: MacFilesWork
    private(set) var acquiredByActivity = false

    init(_ copy: MacFilesStagedCopy, work: MacFilesWork) { self.copy = copy; self.work = work }

    func leaseForActivity(current: MacFilesWork, destinationIsCurrent: Bool) -> Lease? {
        guard !acquiredByActivity, let copy, work.permits(current, destinationIsCurrent: destinationIsCurrent) else { return nil }
        acquiredByActivity = true
        return Lease(copy)
    }

    func preservesPresentation(inBackground: Bool) -> Bool { acquiredByActivity && !inBackground }

    func finish(current: inout MacFilesShare?) {
        requestCleanup()
        if current === self { current = nil }
    }

    /// Marks cleanup pending: the copy survives only if the activity already owns it.
    func requestCleanup() { copy = nil }

    final class Lease: Sendable {
        fileprivate let copy: MacFilesStagedCopy
        var url: URL { copy.url }
        fileprivate init(_ copy: MacFilesStagedCopy) { self.copy = copy }
    }
}

enum MacFilePurpose: String, Sendable {
    case text, raster, share

    var cap: Int {
        switch self {
        case .text: 256 * 1024
        case .raster: 8 * 1024 * 1024
        case .share: 32 * 1024 * 1024
        }
    }

    func count(for row: MacFileEntry) throws -> Int {
        guard !row.directory, let size = row.bytes, size >= 0, self == .text || size <= Int64(cap) else {
            throw ClientError.attachment
        }
        return Int(min(size, Int64(cap)))
    }
}

/// No default implementation: a generic data transport cannot safely buffer file exports.
protocol MacFilesTransport: Transport {
    func readMacFiles(_ request: URLRequest, policy: MacFilesRead, staging: URL?) async throws -> Data
}

struct MacFilesRead: Sendable {
    let limit: Int
    var exact: Int? = nil

    var mime: String { exact == nil ? "application/json" : "application/octet-stream" }

    func check(_ response: HTTPURLResponse, for request: URLRequest) throws {
        guard response.url == request.url else { throw ClientError.origin }
        switch response.statusCode {
        case 200: break
        case 401: throw ClientError.unauthorized
        default: throw ClientError.response(response.statusCode)
        }
        guard response.mimeType == mime,
              response.value(forHTTPHeaderField: "Content-Encoding").map({ $0.lowercased() == "identity" }) ?? true,
              response.expectedContentLength <= Int64(limit) else { throw ClientError.malformed }
        if let exact {
            let disposition = response.value(forHTTPHeaderField: "Content-Disposition")?
                .split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespaces).lowercased()
            guard disposition == "attachment", response.expectedContentLength == Int64(exact) else {
                throw ClientError.malformed
            }
        }
    }

    /// Consume only a validated 200 response. Keep at most one chunk in memory for a share.
    func consume<Body: AsyncSequence>(_ body: Body, staging: URL?) async throws -> Data where Body.Element == UInt8 {
        let file = try staging.map { try FileHandle(forWritingTo: $0) }
        defer { try? file?.close() }
        let chunkSize = 64 * 1024
        var buffer = Data()
        var count = 0
        for try await byte in body {
            guard count < limit else { throw ClientError.attachment }
            buffer.append(byte)
            count += 1
            if let file, buffer.count == chunkSize {
                try Task.checkCancellation()
                try file.write(contentsOf: buffer)
                buffer.removeAll(keepingCapacity: true)
            }
        }
        try Task.checkCancellation()
        guard exact == nil || count == exact else { throw ClientError.malformed }
        if let file {
            try file.write(contentsOf: buffer)
            try file.close()
            return Data()
        }
        return buffer
    }
}

struct MacFilesClient: Sendable {
    private let endpoint: Endpoint
    private let token: String
    private let transport: TailnetTransport

    init?(api: API) {
        guard let token = api.token, API.validToken(token),
              let transport = api.transport as? TailnetTransport,
              transport.supportsMacFiles(at: api.endpoint) else { return nil }
        self.endpoint = api.endpoint
        self.token = token
        self.transport = transport
    }

    func roots() async throws -> [MacFileRoot] {
        let roots = try JSONDecoder().decode([MacFileRoot].self, from: await get("roots", policy: .init(limit: 32 * 1024)))
        try Self.validateIDs(roots.map(\.id))
        return roots
    }

    func page(_ handle: String) async throws -> MacFilePage {
        let page = try JSONDecoder().decode(MacFilePage.self, from: await get("entries/\(Self.checked(handle))",
                                                                           policy: .init(limit: 256 * 1024)))
        try Self.validateIDs(page.entries.map(\.id))
        if let next = page.next {
            guard try Self.checked(next) != handle else { throw ClientError.malformed }
        }
        return page
    }

    func preview(_ row: MacFileEntry, _ purpose: MacFilePurpose) async throws -> Data {
        guard purpose != .share else { throw ClientError.malformed }
        return try await transfer(row, purpose)
    }

    func share(_ row: MacFileEntry) async throws -> MacFilesStagedCopy {
        _ = try MacFilePurpose.share.count(for: row)
        let file = try MacFilesStagedCopy.create(leaf: row.name)
        do {
            _ = try await transfer(row, .share, staging: file.url)
            try Task.checkCancellation()
            return file
        } catch {
            file.discardUnpublished()
            throw error
        }
    }

    private func transfer(_ row: MacFileEntry, _ purpose: MacFilePurpose, staging: URL? = nil) async throws -> Data {
        let count = try purpose.count(for: row)
        return try await get("bytes/\(Self.checked(row.id))/\(purpose.rawValue)", policy: .init(limit: count, exact: count), staging: staging)
    }

    private static func validateIDs(_ ids: [String]) throws {
        guard Set(ids).count == ids.count else { throw ClientError.malformed }
        for id in ids { _ = try checked(id) }
    }

    private static func checked(_ value: String) throws -> String {
        guard !value.isEmpty, value.utf8.count <= 64, value.utf8.allSatisfy({
            (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95
        }) else { throw ClientError.malformed }
        return value
    }

    private func get(_ route: String, policy: MacFilesRead, staging: URL? = nil) async throws -> Data {
        try Task.checkCancellation()
        var parts = URLComponents(url: endpoint.url, resolvingAgainstBaseURL: false)!
        parts.path = "/api/mac-files/\(route)"
        var request = URLRequest(url: parts.url!, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(policy.mime, forHTTPHeaderField: "Accept")
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        let result = try await transport.readMacFiles(request, policy: policy, staging: staging)
        try Task.checkCancellation()
        return result
    }
}

final class MacFilesStagedCopy: Sendable {
    private let folder: URL
    let url: URL
    private static var parent: URL {
        FileManager.default.temporaryDirectory.appending(path: "MacFilesExports", directoryHint: .isDirectory)
    }

    private init(folder: URL, url: URL) { self.folder = folder; self.url = url }
    deinit { remove() }

    /// Call at process launch only, before there can be any active exports.
    static func sweep() { try? FileManager.default.removeItem(at: parent) }

    static func create(leaf: String) throws -> MacFilesStagedCopy {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: " ._-"))
        let clean = String(String.UnicodeScalarView(leaf.unicodeScalars.prefix(60).map { allowed.contains($0) ? $0 : "_" }))
            .trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        var folder = parent.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        let file = folder.appending(path: clean.isEmpty ? "File" : clean, directoryHint: .notDirectory)
        var attributes: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
        var options: Data.WritingOptions = [.withoutOverwriting]
        #if os(iOS)
        attributes[.protectionKey] = FileProtectionType.complete
        options.insert(.completeFileProtection)
        #endif
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true, attributes: attributes)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false, attributes: attributes)
        do {
            var values = URLResourceValues(); values.isExcludedFromBackup = true
            try folder.setResourceValues(values)
            try Data().write(to: file, options: options)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            return MacFilesStagedCopy(folder: folder, url: file)
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    /// Call only before handing the copy to an activity controller.
    func discardUnpublished() { remove() }

    private func remove() { try? FileManager.default.removeItem(at: folder) }
}
