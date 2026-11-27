import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct ArtifactCard: View {
    let ref: ArtifactReference
    let api: API
    let agent: String
    let active: Bool
    let route: UUID
    let isCurrent: @MainActor () -> Bool
    @Environment(\.scenePhase) private var phase
    @State private var request: Request?
    @State private var preview: ArtifactPreview?
    @State private var error: String?
    @State private var export = ArtifactExportSlot()
    private var document: ArtifactExport? { export.document }

    private struct Request: Equatable {
        let id = UUID()
        let purpose: ArtifactPurpose
        let route: UUID
    }
    private var available: Bool { active && phase == .active && isCurrent() }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(ref.exportName, systemImage: "doc").font(.subheadline.bold())
            Text(ref.bytes.formatted(.byteCount(style: .file))).font(.caption).foregroundStyle(.secondary)
            HStack {
                if let purpose = ref.preview {
                    Button(preview == nil ? "Preview" : "Hide Preview") {
                        if preview != nil { preview = nil }
                        else { begin(purpose) }
                    }
                }
                Button("Export…") { begin(.download) }
                if request != nil { ProgressView().controlSize(.small).accessibilityLabel("Loading artifact") }
            }.buttonStyle(.borderless).disabled(!available || request != nil || document != nil)
            if let error { Text(error).font(.footnote).foregroundStyle(.secondary) }
            if let preview {
                switch preview {
                case .text(let text, let truncated):
                    Text(verbatim: text).font(.callout.monospaced()).textSelection(.enabled)
                    if truncated { Text("Preview truncated").font(.caption).foregroundStyle(.secondary) }
                case .image(let image):
                    Image(decorative: image, scale: 1).resizable().scaledToFit().frame(maxHeight: 320)
                        .accessibilityLabel("Artifact image preview")
                }
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
        .task(id: request) { if let request { await load(request) } }
        .onChange(of: route) { reset() }
        .onChange(of: active) { if !active { reset() } }
        .onChange(of: phase) { if phase == .background { reset() } }
        .onDisappear { reset() }
        // FileDocument hands owned bytes, not an authenticated URL or a temporary
        // file with an ambiguous UIKit lifetime, to the system export picker.
        .modifier(exportPicker)
    }

    private var exportPicker: some ViewModifier {
        let identity = document?.id
        let binding = Binding(get: { export.presented && document?.id == identity && available }, set: { value in
            export.setPresented(value, for: identity)
        })
        return ArtifactPicker(presented: binding, document: available ? document : nil, filename: ref.exportName,
                              completion: { failed in
            guard export.finish(identity) else { return }
            if failed && available { error = "Could not export the artifact." }
        })
    }

    private func begin(_ purpose: ArtifactPurpose) {
        guard available, request == nil, document == nil else { return }
        error = nil
        request = Request(purpose: purpose, route: route)
    }

    private func reset() {
        request = nil
        preview = nil
        error = nil
        export.reset()
    }

    private func load(_ item: Request) async {
        guard request == item, available else { return }
        defer { if request == item { request = nil } }
        do {
            let payload = try await api.artifact(ref, agent: agent, purpose: item.purpose)
            let content: ArtifactPreview?
            if case .download = payload { content = nil }
            else { content = try await ArtifactPreview.decode(payload) }
            try Task.checkCancellation()
            guard request == item, item.route == route, available else { return }
            if case .download(let data) = payload {
                export.begin(data)
            } else { preview = content }
        } catch is CancellationError {
        } catch {
            guard request == item, item.route == route, available, !Task.isCancelled else { return }
            self.error = error is ArtifactUnavailable ? "Artifact unavailable" : safeMessage(error)
        }
    }
}

private enum ArtifactPreview: Sendable {
    case text(String, truncated: Bool), image(CGImage)

    nonisolated static func decode(_ payload: ArtifactPayload) async throws -> Self {
        try Task.checkCancellation()
        switch payload {
        case .download: throw ClientError.attachment
        case .text(let text, let truncated): return .text(text, truncated: truncated)
        case .png(let data):
            let options = [kCGImageSourceShouldCache: false] as CFDictionary
            guard let source = CGImageSourceCreateWithData(data as CFData, options),
                  CGImageSourceGetType(source) == UTType.png.identifier as CFString,
                  CGImageSourceGetStatus(source) == .statusComplete, CGImageSourceGetCount(source) == 1,
                  let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                  let width = properties[kCGImagePropertyPixelWidth] as? Int,
                  let height = properties[kCGImagePropertyPixelHeight] as? Int,
                  (1...8192).contains(width), (1...8192).contains(height), width * height <= 8_000_000
            else { throw ClientError.attachment }
            try Task.checkCancellation()
            let decode = [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
            guard let image = CGImageSourceCreateImageAtIndex(source, 0, decode) else { throw ClientError.attachment }
            try Task.checkCancellation()
            return .image(image)
        }
    }
}

private struct ArtifactPicker: ViewModifier {
    let presented: Binding<Bool>
    let document: ArtifactExport?
    let filename: String
    let completion: (Bool) -> Void

    func body(content: Content) -> some View {
        content.fileExporter(isPresented: presented, document: document, contentTypes: [.data], defaultFilename: filename,
                             onCompletion: { result in
            switch result { case .success: completion(false); case .failure: completion(true) }
        }, onCancellation: { completion(false) })
    }
}

struct ArtifactExportSlot {
    private(set) var document: ArtifactExport?
    private(set) var presented = false

    mutating func begin(_ data: Data) {
        document = ArtifactExport(data: data)
        presented = true
    }

    mutating func setPresented(_ value: Bool, for identity: UUID?) {
        guard let identity, document?.id == identity else { return }
        presented = value
    }

    @discardableResult mutating func finish(_ identity: UUID?) -> Bool {
        guard let identity, document?.id == identity else { return false }
        reset()
        return true
    }

    mutating func reset() {
        presented = false
        document = nil
    }
}

struct ArtifactExport: FileDocument {
    let id = UUID()
    static let readableContentTypes: [UTType] = [.data]
    let data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws { throw ClientError.attachment }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}
