#if os(iOS) && MARTEN_FILES_PROTOTYPE
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct FileRoot: Decodable, Identifiable, Hashable, Sendable {
    let id: String
    let label: String
}

struct ProjectFileRow: Decodable, Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let directory: Bool
    let bytes: Int64?
    let modified: String
}

struct ProjectFilePage: Decodable, Sendable {
    let entries: [ProjectFileRow]
    let next: String?
}

struct PrototypeFilesAPI: Sendable {
    let api: API
    let project: String

    enum Purpose: String {
        case text, raster, share
        var cap: Int { switch self {
        case .text: 256 * 1024
        case .raster: 8 * 1024 * 1024
        case .share: 32 * 1024 * 1024
        } }
    }

    func roots() async throws -> [FileRoot] {
        let data = try await get("roots", limit: 32 * 1024, mime: "application/json")
        return try JSONDecoder().decode([FileRoot].self, from: data)
    }

    func page(_ id: String) async throws -> ProjectFilePage {
        let data = try await get("entries/\(Self.checked(id))", limit: 256 * 1024, mime: "application/json")
        return try JSONDecoder().decode(ProjectFilePage.self, from: data)
    }

    func bytes(_ row: ProjectFileRow, _ purpose: Purpose) async throws -> Data {
        guard let size = row.bytes, size >= 0, purpose == .text || size <= purpose.cap else { throw ClientError.attachment }
        let count = Int(min(size, Int64(purpose.cap)))
        let data = try await get("bytes/\(Self.checked(row.id))/\(purpose.rawValue)", limit: count,
                                 mime: "application/octet-stream")
        guard data.count == count else { throw ClientError.malformed }
        return data
    }

    private static func checked(_ value: String) throws -> String {
        guard !value.isEmpty, value.utf8.count <= 64, value.utf8.allSatisfy({
            (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95
        }) else { throw ClientError.malformed }
        return value
    }

    // Fixture only. The shipping connection needs the existing exact-authority proxy and a
    // true bounded stream-to-file transport for export (rather than this fixture Data response).
    private func get(_ route: String, limit: Int, mime: String) async throws -> Data {
        guard let token = api.token, API.validToken(token) else { throw ClientError.unauthorized }
        var parts = URLComponents(url: api.endpoint.url, resolvingAgainstBaseURL: false)!
        parts.path = "/api/projects/\(try Self.checked(project))/files/\(route)"
        var request = URLRequest(url: parts.url!, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(mime, forHTTPHeaderField: "Accept")
        let (data, reply) = try await api.transport.boundedData(for: request, limit: limit)
        try Task.checkCancellation()
        guard reply.url == request.url else { throw ClientError.origin }
        guard reply.statusCode == 200 else { throw ClientError.response(reply.statusCode) }
        guard reply.mimeType == mime, data.count <= limit else { throw ClientError.malformed }
        return data
    }
}

struct ProjectFilesPrototype: View {
    enum Page: Hashable {
        case roots
        case folder(String, String)
        case file(ProjectFileRow)
    }
    let service: PrototypeFilesAPI
    let page: Page
    let route: UUID
    let active: Bool
    @State private var roots: [FileRoot]?
    @State private var rows: [ProjectFileRow]?
    @State private var next: String?
    @State private var preview: Preview?
    @State private var issue: String?
    @State private var refresh = UUID()
    @State private var more: String?
    @State private var export: UUID?
    @State private var staged: StagedFile?
    @State private var showingShare = false
    @State private var valid = UUID()

    private enum Preview {
        case text(String, truncated: Bool)
        case image(UIImage)
        case unavailable
    }

    var body: some View {
        List {
            if let issue { Label(issue, systemImage: "exclamationmark.circle") }
            switch page {
            case .roots:
                if let roots {
                    if roots.isEmpty { Text("No approved folders.") }
                    ForEach(roots) { root in
                        destination(.folder(root.id, root.label)) { Label(root.label, systemImage: "folder") }
                    }
                } else if issue == nil { ProgressView("Loading folders…") }
            case .folder:
                if let rows {
                    if rows.isEmpty { Text("No visible files.") }
                    ForEach(rows) { row in
                        destination(row.directory ? .folder(row.id, row.name) : .file(row)) {
                            Label(row.name, systemImage: row.directory ? "folder" : "doc")
                        }
                    }
                    if let next { Button("Load more") { more = next }.disabled(more != nil || !active) }
                } else if issue == nil { ProgressView("Loading files…") }
            case .file(let row):
                if let bytes = row.bytes {
                    Text(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)).foregroundStyle(.secondary)
                }
                switch preview {
                case .text(let text, let truncated):
                    if truncated { Text("Showing the first 256 KiB").foregroundStyle(.secondary) }
                    Text(text).font(.body.monospaced()).textSelection(.enabled)
                        .accessibilityIdentifier("project-file-preview")
                case .image(let image):
                    Image(uiImage: image).resizable().scaledToFit().accessibilityLabel(row.name)
                case .unavailable: Text("Preview unavailable. You can share a copy if the file is within the download limit.")
                case nil: if issue == nil { ProgressView("Loading preview…") }
                }
                Button("Share a copy…", systemImage: "square.and.arrow.up") { issue = nil; export = UUID() }
                    .disabled(!active || export != nil || showingShare || (row.bytes ?? Int64.max) > Int64(PrototypeFilesAPI.Purpose.share.cap))
                if export != nil { ProgressView("Preparing copy…") }
            }
        }
        .navigationTitle(title)
        .toolbar { Button("Refresh files", systemImage: "arrow.clockwise") { refresh = UUID() }.disabled(!active) }
        .task(id: ReadKey(route: route, active: active, refresh: refresh)) {
            reset()
            if active { await load() }
        }
        .task(id: more) {
            guard let more, active else { return }
            let version = valid
            do {
                let result = try await service.page(more)
                guard !Task.isCancelled, version == valid else { return }
                rows?.append(contentsOf: result.entries)
                next = result.next
            } catch { if !Task.isCancelled && version == valid { issue = message(error) } }
            if version == valid { self.more = nil }
        }
        .task(id: export) {
            guard active, let export, case .file(let row) = page else { return }
            let version = valid
            defer { if self.export == export { self.export = nil } }
            do {
                let bytes = try await service.bytes(row, .share)
                try Task.checkCancellation()
                guard version == valid else { return }
                let file = try StagedFile.create(bytes, leaf: row.name)
                guard !Task.isCancelled, version == valid else { file.cleanup(); return }
                staged = file
                showingShare = true
            } catch { if !Task.isCancelled && version == valid { issue = message(error) } }
        }
        .onDisappear { reset() }
        .sheet(isPresented: $showingShare, onDismiss: cleanupExport) {
            if let staged {
                ActivitySheet(url: staged.url) { showingShare = false }
            }
        }
    }

    private struct ReadKey: Hashable { let route: UUID; let active: Bool; let refresh: UUID }

    private var title: String { switch page {
    case .roots: "Files"
    case .folder(_, let name): name
    case .file(let row): row.name
    } }

    private func destination<V: View>(_ page: Page, @ViewBuilder label: () -> V) -> some View {
        NavigationLink(value: WorkspaceDestination.prototypeFiles(service.project, page), label: label)
    }

    private func reset() {
        valid = UUID()
        roots = nil; rows = nil; next = nil; preview = nil; issue = nil; more = nil; export = nil
        if showingShare { showingShare = false } else { cleanupExport() }
    }

    private func cleanupExport() { staged?.cleanup(); staged = nil }

    private func message(_ error: Error) -> String {
        switch error as? ClientError {
        case .response(404), .response(409): "Files changed or expired. Go back to Files and refresh."
        case .response(413), .attachment: "This folder or file exceeds the limit."
        default: "Could not load files. Try again."
        }
    }

    private func load() async {
        let version = valid
        do {
            switch page {
            case .roots:
                let value = try await service.roots()
                if !Task.isCancelled, valid == version { roots = value }
            case .folder(let id, _):
                let value = try await service.page(id)
                if !Task.isCancelled, valid == version { rows = value.entries; next = value.next }
            case .file(let row):
                let value = try await filePreview(row)
                if !Task.isCancelled, valid == version { preview = value }
            }
        } catch { if !Task.isCancelled, valid == version { issue = message(error) } }
    }

    private func filePreview(_ row: ProjectFileRow) async throws -> Preview {
        let ext = (row.name as NSString).pathExtension.lowercased()
        if ["txt", "md", "swift", "cs", "json", "yaml", "yml", "csv", "log"].contains(ext) ||
            ["README", "LICENSE"].contains(row.name) {
            let bytes = try await service.bytes(row, .text)
            let truncated = (row.bytes ?? 0) > Int64(bytes.count)
            for drop in 0...(truncated ? min(3, bytes.count) : 0) {
                let prefix = bytes.dropLast(drop)
                if !prefix.contains(0), let text = String(data: Data(prefix), encoding: .utf8) {
                    return .text(text, truncated: truncated)
                }
            }
        } else if ["png", "jpg", "jpeg"].contains(ext), (row.bytes ?? Int64.max) <= Int64(PrototypeFilesAPI.Purpose.raster.cap) {
            let bytes = try await service.bytes(row, .raster)
            let png: [UInt8] = [137, 80, 78, 71, 13, 10, 26, 10]
            guard bytes.starts(with: png) || bytes.starts(with: [0xff, 0xd8, 0xff]) else { return .unavailable }
            if let source = CGImageSourceCreateWithData(bytes as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
               CGImageSourceGetCount(source) == 1,
               let type = CGImageSourceGetType(source) as String?, [UTType.png.identifier, UTType.jpeg.identifier].contains(type),
               let metadata = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
               let width = metadata[kCGImagePropertyPixelWidth] as? Int, let height = metadata[kCGImagePropertyPixelHeight] as? Int,
               (1...8192).contains(width), (1...8192).contains(height), width * height <= 16_000_000,
               let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true, kCGImageSourceThumbnailMaxPixelSize: 1600] as CFDictionary) {
                return .image(UIImage(cgImage: thumbnail))
            }
        }
        return .unavailable
    }
}

private struct StagedFile {
    let folder: URL
    let url: URL
    private static let parent = FileManager.default.temporaryDirectory.appending(path: "ProjectFilesExports", directoryHint: .isDirectory)

    static func sweep() {
        try? FileManager.default.removeItem(at: parent) // fixture launch only, no active exports yet
    }

    static func create(_ data: Data, leaf: String) throws -> Self {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: " ._-"))
        let clean = String(String.UnicodeScalarView(leaf.unicodeScalars.prefix(80).map { allowed.contains($0) ? $0 : "_" }))
            .trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        let folder = parent.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        let file = folder.appending(path: clean.isEmpty ? "File" : clean, directoryHint: .notDirectory)
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700, .protectionKey: FileProtectionType.complete])
            var protected = folder
            var values = URLResourceValues(); values.isExcludedFromBackup = true
            try protected.setResourceValues(values)
            try data.write(to: file, options: [.withoutOverwriting, .completeFileProtection])
            return Self(folder: folder, url: file)
        } catch { try? FileManager.default.removeItem(at: folder); throw error }
    }

    func cleanup() { try? FileManager.default.removeItem(at: folder) }
}

func sweepProjectFilePrototypeExports() { StagedFile.sweep() }

private struct ActivitySheet: UIViewControllerRepresentable {
    let url: URL
    let completed: () -> Void
    func makeUIViewController(context: Context) -> UIActivityViewController {
        let controller = UIActivityViewController(activityItems: [url], applicationActivities: nil)
        controller.completionWithItemsHandler = { _, _, _, _ in completed() }
        return controller
    }
    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
#endif
