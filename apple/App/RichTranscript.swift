import Foundation
import ImageIO
import SwiftUI

struct TranscriptBlock: Equatable {
    enum Style: Equatable { case paragraph, heading, code }
    let style: Style
    let text: String

    static func parse(_ source: String) -> [Self] {
        var result: [Self] = []
        var pending: [String] = []
        var fence: Character?
        var width = 0
        func flush(_ style: Style) {
            if !pending.isEmpty { result.append(.init(style: style, text: pending.joined(separator: "\n"))); pending = [] }
        }
        for raw in source.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = String(raw)
            let trim = line.trimmingCharacters(in: .whitespaces)
            if let f = fence {
                if trim.prefix(while: { $0 == f }).count >= width && trim.allSatisfy({ $0 == f }) {
                    flush(.code); fence = nil
                } else { pending.append(line) }
            } else if let first = trim.first, first == "`" || first == "~", trim.prefix(while: { $0 == first }).count >= 3 {
                flush(.paragraph); fence = first; width = trim.prefix(while: { $0 == first }).count
            } else if trim.isEmpty { flush(.paragraph) }
            else if let title = trim.firstIndex(of: " "), (1...6).contains(trim.distance(from: trim.startIndex, to: title)), trim[..<title].allSatisfy({ $0 == "#" }) {
                flush(.paragraph); result.append(.init(style: .heading, text: String(trim[trim.index(after: title)...])))
            } else { pending.append(line) }
        }
        flush(fence == nil ? .paragraph : .code)
        return result
    }

    static func inline(_ text: String) -> AttributedString {
        var result = (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
        for run in Array(result.runs) where run.link != nil { result[run.range].link = nil }
        return result
    }
}

struct RichTranscript: View {
    let text: String
    var body: some View {
        let blocks = TranscriptBlock.parse(text)
        VStack(alignment: .leading, spacing: 8) {
            ForEach(blocks.indices, id: \.self) { index in
                let block = blocks[index]
                switch block.style {
                case .heading: Text(TranscriptBlock.inline(block.text)).font(.headline).textSelection(.enabled)
                case .paragraph: Text(TranscriptBlock.inline(block.text)).textSelection(.enabled)
                case .code: TranscriptCode(text: block.text)
                }
            }
        }
    }
}

struct TranscriptCode: View {
    let text: String
    var body: some View {
        ScrollView(.horizontal) {
            Text(text).font(.callout.monospaced()).textSelection(.enabled).fixedSize(horizontal: true, vertical: false)
        }.frame(maxWidth: .infinity, alignment: .leading)
            .padding(8).background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
    }
}

struct AttachmentPreview: View {
    let ref: TranscriptImage
    let api: API
    let agent: String
    let active: Bool
    let route: UUID
    @State private var requested = false
    @State private var preview: CGImage?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let preview {
                Image(decorative: preview, scale: 1).resizable().scaledToFit().frame(maxHeight: 420)
                    .accessibilityLabel("Tool output image")
                Button("Hide Image") { reset() }
            } else if requested { ProgressView("Loading image…") }
            else { Button(error == nil ? "Load Attached Image" : "Retry Image") { error = nil; requested = true }.disabled(!active) }
            if let error { Text(error).font(.footnote).foregroundStyle(.secondary) }
        }
        .onChange(of: route) { reset() }
        .onChange(of: active) { if !active { reset() } }
        .task(id: requested) {
            guard requested, active else { return }
            do {
                let bytes = try await api.image(ref, agent: agent)
                let worker = Task.detached(priority: .utility) { try Self.thumbnail(bytes) }
                let image = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                try Task.checkCancellation()
                preview = image
                requested = false
            } catch is CancellationError {
            } catch {
                guard !Task.isCancelled else { return }
                self.error = safeMessage(error)
                requested = false
            }
        }
    }

    private func reset() { requested = false; preview = nil; error = nil }

    nonisolated static func thumbnail(_ bytes: Data) throws -> CGImage {
        try Task.checkCancellation()
        let options = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let source = CGImageSourceCreateWithData(bytes as CFData, options),
              let type = CGImageSourceGetType(source) as String?,
              ["public.png", "public.jpeg", "com.compuserve.gif", "org.webmproject.webp"].contains(type),
              let metadata = CGImageSourceCopyPropertiesAtIndex(source, 0, options) as? [CFString: Any],
              let width = metadata[kCGImagePropertyPixelWidth] as? Int, let height = metadata[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 16_384, height <= 16_384, width * height <= 40_000_000,
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: 1_600, kCGImageSourceShouldCacheImmediately: true
              ] as CFDictionary) else { throw ClientError.attachment }
        return image
    }
}
