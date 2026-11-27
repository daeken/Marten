#if os(iOS)
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct MacFilesView: View {
    enum Page: Hashable {
        case roots
        case folder(String, String)
        case file(MacFileEntry)
    }
    let service: MacFilesClient
    let page: Page
    let route: UUID
    let active: Bool
    let endSession: () -> Void
    @State private var roots: [MacFileRoot]?
    @State private var rows: [MacFileEntry]?
    @State private var next: String?
    @State private var preview: Preview?
    @State private var issue: String?
    @State private var refresh = UUID()
    @State private var more: String?
    @State private var previewRequest: UUID?
    @State private var export: UUID?
    @State private var staged: MacFilesStagedCopy?
    @State private var showingShare = false
    @State private var valid = UUID()
    @State private var loadedPages: Set<String> = []

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
                    if roots.isEmpty { Text("No Mac locations available.") }
                    ForEach(roots) { root in
                        destination(.folder(root.id, root.label)) { Label(root.label, systemImage: "folder") }
                    }
                } else if issue == nil { ProgressView("Loading Mac locations…") }
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
                Text("Modified: \(row.modified)").font(.footnote).foregroundStyle(.secondary)
                if let purpose = previewPurpose(row) {
                    Button(purpose == .text ? "Preview Text" : "Preview Image", systemImage: "eye") {
                        issue = nil
                        previewRequest = UUID()
                    }.disabled(!active || previewRequest != nil || export != nil || showingShare)
                }
                switch preview {
                case .text(let text, let truncated):
                    if truncated { Text("Showing the first 256 KiB").foregroundStyle(.secondary) }
                    Text(text).font(.body.monospaced()).textSelection(.enabled)
                        .accessibilityIdentifier("mac-file-preview")
                case .image(let image):
                    Image(uiImage: image).resizable().scaledToFit().accessibilityLabel(row.name)
                case .unavailable: Text("Preview unavailable. You can share a copy if the file is within the download limit.")
                case nil: if previewRequest != nil { ProgressView("Loading preview…") }
                }
                Button("Share a copy…", systemImage: "square.and.arrow.up") { issue = nil; export = UUID() }
                    .disabled(!active || previewRequest != nil || export != nil || showingShare || (try? MacFilePurpose.share.count(for: row)) == nil)
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
                guard let rows, Set(rows.map(\.id)).isDisjoint(with: result.entries.map(\.id)),
                      result.next.map({ $0 != more && !loadedPages.contains($0) }) ?? true else { throw ClientError.malformed }
                self.rows = rows + result.entries
                loadedPages.insert(more)
                next = result.next
                issue = nil
            } catch { if !Task.isCancelled && version == valid { fail(error) } }
            if version == valid { self.more = nil }
        }
        .task(id: previewRequest) {
            guard active, let previewRequest, case .file(let row) = page, let purpose = previewPurpose(row) else { return }
            let version = valid
            defer { if self.previewRequest == previewRequest { self.previewRequest = nil } }
            do {
                let result = try await filePreview(row, purpose: purpose)
                guard !Task.isCancelled, version == valid else { return }
                preview = result
            } catch { if !Task.isCancelled && version == valid { fail(error) } }
        }
        .task(id: export) {
            guard active, let export, case .file(let row) = page else { return }
            let version = valid
            defer { if self.export == export { self.export = nil } }
            do {
                let file = try await service.share(row)
                guard !Task.isCancelled, version == valid else { file.cleanup(); return }
                staged = file
                showingShare = true
            } catch { if !Task.isCancelled && version == valid { fail(error) } }
        }
        .onDisappear { reset() }
        .task(id: staged?.url) {
            guard let staged else { return }
            do { try await Task.sleep(for: .seconds(300)) } catch { return }
            guard self.staged === staged else { return }
            showingShare = false
            cleanupExport()
        }
        .sheet(isPresented: $showingShare, onDismiss: cleanupExport) {
            if let staged {
                ActivitySheet(url: staged.url) { showingShare = false }
            }
        }
    }

    private struct ReadKey: Hashable { let route: UUID; let active: Bool; let refresh: UUID }

    private var title: String { switch page {
    case .roots: "Mac Files"
    case .folder(_, let name): name
    case .file(let row): row.name
    } }

    private func destination<V: View>(_ page: Page, @ViewBuilder label: () -> V) -> some View {
        NavigationLink(value: WorkspaceDestination.macFiles(page), label: label)
    }

    private func reset() {
        valid = UUID()
        roots = nil; rows = nil; next = nil; preview = nil; issue = nil; more = nil
        previewRequest = nil; export = nil; loadedPages = []
        showingShare = false
        cleanupExport()
    }

    private func cleanupExport() { staged?.cleanup(); staged = nil }

    private func fail(_ error: Error) {
        switch error as? ClientError {
        case .unauthorized: reset(); endSession()
        case .response(403): reset(); issue = "Mac Files is no longer available on this connection."
        case .response(404), .response(409), .response(410):
            reset(); issue = "Files changed or expired. Go back to Mac Files and refresh."
        case .response(413), .attachment: issue = "This folder or file exceeds the limit."
        default: issue = "Could not load files. Try again."
        }
    }

    private func previewPurpose(_ row: MacFileEntry) -> MacFilePurpose? {
        let ext = (row.name as NSString).pathExtension.lowercased()
        let purpose: MacFilePurpose
        if ["txt", "md", "swift", "cs", "json", "yaml", "yml", "csv", "log"].contains(ext) ||
            ["README", "LICENSE"].contains(row.name) {
            purpose = .text
        } else if ["png", "jpg", "jpeg"].contains(ext) {
            purpose = .raster
        } else { return nil }
        return (try? purpose.count(for: row)) == nil ? nil : purpose
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
                if !Task.isCancelled, valid == version { rows = value.entries; next = value.next; loadedPages = [id] }
            case .file:
                break // No bytes before a distinct Preview or Share action.
            }
        } catch { if !Task.isCancelled, valid == version { fail(error) } }
    }

    private func filePreview(_ row: MacFileEntry, purpose: MacFilePurpose) async throws -> Preview {
        let bytes = try await service.preview(row, purpose)
        if purpose == .text {
            let truncated = (row.bytes ?? 0) > Int64(bytes.count)
            for drop in 0...(truncated ? min(3, bytes.count) : 0) {
                let prefix = bytes.dropLast(drop)
                if !prefix.contains(0), let text = String(data: Data(prefix), encoding: .utf8) {
                    return .text(text, truncated: truncated)
                }
            }
        } else if purpose == .raster {
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
