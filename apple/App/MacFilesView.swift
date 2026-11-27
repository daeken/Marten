#if os(iOS)
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct MacFilesView: View {
    struct Identity: Hashable {
        let scope: MacFilesSession.Key
        let page: MacFilesDestination
    }
    let service: MacFilesClient
    let page: MacFilesDestination
    let scope: MacFilesSession.Key
    let foregroundEpoch: UUID?
    let active: Bool
    let isCurrent: @MainActor () -> Bool
    let endSession: () -> Void
    @Environment(\.scenePhase) private var phase
    @State private var roots: [MacFileRoot]?
    @State private var listing: MacFilesListing?
    @State private var preview: Preview?
    @State private var issue: String?
    @State private var refresh = UUID()
    @State private var more: String?
    @State private var previewRequest: UUID?
    @State private var export: UUID?
    @State private var staged: MacFilesShare?
    @State private var valid = UUID()

    private var work: MacFilesWork { .init(scope: scope, destination: page, generation: valid, foregroundEpoch: foregroundEpoch) }
    private var identity: Identity { .init(scope: scope, page: page) }
    private func permits(_ started: MacFilesWork) -> Bool {
        started.permits(work, destinationIsCurrent: isCurrent())
    }

    private enum Preview: Sendable {
        case text(String, truncated: Bool)
        case image(CGImage)
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
                if let listing {
                    if listing.rows.isEmpty { Text("No visible files.") }
                    ForEach(listing.rows) { row in
                        destination(row.directory ? .folder(row.id, row.name) : .file(row)) {
                            Label(row.name, systemImage: row.directory ? "folder" : "doc")
                        }
                    }
                    if let next = listing.next { Button("Load more") { more = next }.disabled(more != nil || !active) }
                    if listing.reachedLimit { Text("Listing limit reached. Open a subfolder to continue.").foregroundStyle(.secondary) }
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
                    }.disabled(!active || previewRequest != nil || export != nil || staged != nil)
                }
                switch preview {
                case .text(let text, let truncated):
                    if truncated { Text("Showing the first 256 KiB").foregroundStyle(.secondary) }
                    Text(text).font(.body.monospaced()).textSelection(.enabled)
                        .accessibilityIdentifier("mac-file-preview")
                case .image(let image):
                    Image(decorative: image, scale: 1, orientation: .up).resizable().scaledToFit().accessibilityLabel(row.name)
                case .unavailable: Text("Preview unavailable. You can share a copy if the file is within the download limit.")
                case nil: if previewRequest != nil { ProgressView("Loading preview…") }
                }
                Button("Share a copy…", systemImage: "square.and.arrow.up") { issue = nil; export = UUID() }
                    .disabled(!active || previewRequest != nil || export != nil || staged != nil || (try? MacFilePurpose.share.count(for: row)) == nil)
                if export != nil { ProgressView("Preparing copy…") }
            }
        }
        .privacySensitive()
        .overlay {
            if phase != .active {
                Rectangle().fill(.background).ignoresSafeArea().accessibilityHidden(true)
            }
        }
        .navigationTitle(phase == .active ? title : "Mac Files")
        .toolbar { Button("Refresh files", systemImage: "arrow.clockwise") { refresh = UUID() }.disabled(!active) }
        .task(id: ReadKey(identity: identity, foregroundEpoch: foregroundEpoch, active: active, refresh: refresh)) {
            // UIKit can make the scene temporarily inactive as the share activity
            // takes focus. Cancel network work and clear previews, not the handoff.
            reset(keepShare: phase != .background && staged != nil)
            if active && isCurrent(), staged == nil { await load() }
        }
        .task(id: work.job(more)) {
            guard let more, active, isCurrent() else { return }
            let started = work
            do {
                let result = try await service.page(more)
                guard permits(started) else { return }
                try listing?.append(result, requested: more)
                issue = nil
            } catch { if permits(started) { fail(error) } }
            if self.more == more && started == work { self.more = nil }
        }
        .task(id: work.job(previewRequest)) {
            guard active, isCurrent(), let previewRequest, case .file(let row) = page, let purpose = previewPurpose(row) else { return }
            let started = work
            defer { if self.previewRequest == previewRequest { self.previewRequest = nil } }
            do {
                let result = try await filePreview(row, purpose: purpose)
                guard permits(started) else { return }
                preview = result
            } catch { if permits(started) { fail(error) } }
        }
        .task(id: work.job(export)) {
            guard active, isCurrent(), let export, case .file(let row) = page else { return }
            let started = work
            defer { if self.export == export { self.export = nil } }
            do {
                let file = try await service.share(row)
                guard let share = started.share(file, current: work, destinationIsCurrent: isCurrent()) else { return }
                staged = share
            } catch { if permits(started) { fail(error) } }
        }
        .onDisappear { reset() }
        .onChange(of: phase) { _, phase in
            if phase == .background { reset() }
        }
        .task(id: identityWithShare) {
            guard let staged else { return }
            do { try await Task.sleep(for: .seconds(300)) } catch { return }
            guard self.staged === staged else { return }
            dismissShare() // Drop app ownership, but let UIKit finish with its lease.
        }
        .sheet(item: $staged) { share in
            ActivitySheet(acquire: {
                share.leaseForActivity(current: work, destinationIsCurrent: staged === share && isCurrent())
            }, completed: {
                share.finish(current: &staged)
            })
            .onDisappear { share.requestCleanup() }
        }
    }

    private var identityWithShare: MacFilesWork.Job<UUID?> {
        MacFilesWork(scope: scope, destination: page, generation: refresh).job(staged?.id)
    }

    private struct ReadKey: Hashable { let identity: Identity; let foregroundEpoch: UUID?; let active: Bool; let refresh: UUID }

    private var title: String { switch page {
    case .roots: "Mac Files"
    case .folder(_, let name): name
    case .file(let row): row.name
    } }

    private func destination<V: View>(_ page: MacFilesDestination, @ViewBuilder label: () -> V) -> some View {
        NavigationLink(value: WorkspaceDestination.macFiles(page), label: label)
    }

    private func reset(keepShare: Bool = false) {
        valid = UUID()
        roots = nil; listing = nil; preview = nil; issue = nil; more = nil
        previewRequest = nil; export = nil
        if !keepShare { dismissShare() }
    }

    private func dismissShare() {
        staged?.requestCleanup()
        staged = nil
    }

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
        let started = work
        do {
            switch page {
            case .roots:
                let value = try await service.roots()
                if permits(started) { roots = value }
            case .folder(let id, _):
                let value = try await service.page(id)
                if permits(started) {
                    var first = MacFilesListing()
                    try first.append(value, requested: id)
                    listing = first
                }
            case .file:
                break // No bytes before a distinct Preview or Share action.
            }
        } catch { if permits(started) { fail(error) } }
    }

    private func filePreview(_ row: MacFileEntry, purpose: MacFilePurpose) async throws -> Preview {
        let bytes = try await service.preview(row, purpose)
        try Task.checkCancellation()
        let decode = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let decoded = Self.decodePreview(bytes, sourceSize: row.bytes ?? 0, purpose: purpose)
            try Task.checkCancellation()
            return decoded
        }
        return try await withTaskCancellationHandler { try await decode.value } onCancel: { decode.cancel() }
    }

    nonisolated private static func decodePreview(_ bytes: Data, sourceSize: Int64, purpose: MacFilePurpose) -> Preview {
        if purpose == .text {
            let truncated = sourceSize > Int64(bytes.count)
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
                return .image(thumbnail)
            }
        }
        return .unavailable
    }
}

private struct ActivitySheet: UIViewControllerRepresentable {
    let acquire: @MainActor () -> MacFilesShare.Lease?
    let completed: @MainActor () -> Void

    func makeUIViewController(context: Context) -> UIViewController {
        // Check live foreground readiness, route and destination immediately before
        // giving UIKit a URL. The presentation request may have been queued earlier.
        guard let lease = acquire() else {
            Task { @MainActor in completed() }
            return UIViewController()
        }
        let item = ActivityFile(lease: lease)
        let controller = UIActivityViewController(activityItems: [item], applicationActivities: nil)
        // The actual controller (not just the SwiftUI sheet) also owns the lease.
        controller.completionWithItemsHandler = { [lease] _, _, _, _ in
            withExtendedLifetime(lease) { completed() }
        }
        return controller
    }
    func updateUIViewController(_ controller: UIViewController, context: Context) {}

    private final class ActivityFile: NSObject, UIActivityItemSource {
        let lease: MacFilesShare.Lease
        init(lease: MacFilesShare.Lease) { self.lease = lease }

        func activityViewControllerPlaceholderItem(_ activityViewController: UIActivityViewController) -> Any { lease.url }
        func activityViewController(_ activityViewController: UIActivityViewController,
                                    itemForActivityType activityType: UIActivity.ActivityType?) -> Any? { lease.url }
    }
}
#endif
