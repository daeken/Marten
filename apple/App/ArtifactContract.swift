import Foundation

struct ArtifactReference: Decodable, Hashable, Sendable {
    let id: String
    let name: String
    let bytes: Int
    let previewKind: String

    var valid: Bool {
        id.utf8.count == 32 && id.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
            && (0...ArtifactPurpose.download.limit).contains(bytes)
    }
    var preview: ArtifactPurpose? { [ArtifactPurpose.text, .png].first { $0.rawValue == previewKind } }

    var exportName: String {
        let banned = CharacterSet.controlCharacters.union(CharacterSet(charactersIn: "/\\:"))
        let cleaned = String(String.UnicodeScalarView(name.unicodeScalars.filter { !banned.contains($0) }))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty, !cleaned.hasPrefix(".") else { return "Artifact" }
        // The server allows a 120-byte stem plus a short suffix; do not truncate the type.
        func prefix(_ text: String, budget: Int) -> String {
            var result = ""
            var remaining = budget
            for character in text {
                let size = String(character).utf8.count
                guard size <= remaining else { break }
                result.append(character)
                remaining -= size
            }
            return result
        }
        if let dot = cleaned.lastIndex(of: "."), cleaned[dot...].utf8.count <= 16 {
            let suffix = String(cleaned[dot...])
            return prefix(String(cleaned[..<dot]), budget: 125 - suffix.utf8.count) + suffix
        }
        return prefix(cleaned, budget: 125)
    }
}

// Optional metadata must never prevent the surrounding transcript from opening.
@propertyWrapper struct ArtifactReferences: Decodable, Sendable {
    var wrappedValue: [ArtifactReference]
    init(wrappedValue: [ArtifactReference]) { self.wrappedValue = wrappedValue }
    init(from decoder: Decoder) throws {
        var list = try decoder.unkeyedContainer()
        var result: [ArtifactReference] = []
        var ids: Set<String> = []
        while !list.isAtEnd {
            let item = try list.superDecoder()
            if let ref = try? ArtifactReference(from: item), ref.valid, ids.insert(ref.id).inserted { result.append(ref) }
        }
        wrappedValue = result
    }
}

extension KeyedDecodingContainer {
    func decode(_ type: ArtifactReferences.Type, forKey key: Key) throws -> ArtifactReferences {
        (try? decodeIfPresent(type, forKey: key)) ?? ArtifactReferences(wrappedValue: [])
    }
}

enum ArtifactPurpose: String, Sendable {
    case download, text, png
    var limit: Int {
        switch self { case .download: 8 * 1024 * 1024; case .text: 64 * 1024; case .png: 2 * 1024 * 1024 }
    }
    var mime: String {
        switch self { case .download: "application/octet-stream"; case .text: "text/plain"; case .png: "image/png" }
    }
}

struct ArtifactUnavailable: LocalizedError {
    var errorDescription: String? { "Artifact unavailable" }
}

enum ArtifactPayload: Sendable {
    case download(Data), text(String, truncated: Bool), png(Data)

    init(data: Data, reply: HTTPURLResponse, reference: ArtifactReference, purpose: ArtifactPurpose) throws {
        guard reply.statusCode == 200, reply.mimeType?.lowercased() == purpose.mime, data.count <= min(reference.bytes, purpose.limit),
              reply.value(forHTTPHeaderField: "Content-Disposition")?.split(separator: ";").first?
                .trimmingCharacters(in: .whitespaces).lowercased() == "attachment" else { throw ClientError.attachment }
        if let length = reply.value(forHTTPHeaderField: "Content-Length") {
            let raw = length.trimmingCharacters(in: .whitespaces)
            guard !raw.isEmpty, raw.utf8.allSatisfy({ (48...57).contains($0) }), Int(raw) == data.count else { throw ClientError.attachment }
        }
        switch purpose {
        case .download:
            guard data.count == reference.bytes else { throw ClientError.attachment }
            self = .download(data)
        case .text:
            guard reply.textEncodingName?.lowercased() == "utf-8", let text = String(data: data, encoding: .utf8) else { throw ClientError.attachment }
            let marker = reply.value(forHTTPHeaderField: "X-Marten-Preview-Truncated")
            let clipped = reference.bytes > purpose.limit
            guard clipped ? (marker == "1" && data.count >= purpose.limit - 3)
                          : (marker == nil && data.count == reference.bytes) else { throw ClientError.attachment }
            self = .text(text, truncated: clipped)
        case .png:
            guard data.count == reference.bytes, Self.exactStaticPNG(data) else { throw ClientError.attachment }
            self = .png(data)
        }
    }

    // PNG has an in-band end marker even without a Content-Length. Reject
    // incomplete/trailing data and APNG animation before passing bytes to ImageIO.
    private static func exactStaticPNG(_ data: Data) -> Bool {
        guard data.count >= 45, data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) else { return false }
        return data.withUnsafeBytes { bytes in
            func word(_ offset: Int) -> Int { (0..<4).reduce(0) { ($0 << 8) | Int(bytes[offset + $1]) } }
            var offset = 8
            var header = false
            var pixels = false
            while offset <= bytes.count - 12 {
                let length = word(offset)
                guard length <= bytes.count - offset - 12 else { return false }
                switch word(offset + 4) {
                case 0x49484452: // IHDR
                    guard !header, length == 13 else { return false }
                    let width = word(offset + 8), height = word(offset + 12)
                    guard (1...8192).contains(width), (1...8192).contains(height), width * height <= 8_000_000 else { return false }
                    header = true
                case _ where !header: return false
                case 0x49444154: pixels = pixels || length > 0 // IDAT
                case 0x49454e44: // IEND
                    return pixels && length == 0 && offset + 12 == bytes.count
                        && word(offset + 8) == 0xae426082
                case 0x504c5445, 0x74524e53: break // PLTE, tRNS
                default: return false
                }
                offset += 12 + length
            }
            return false
        }
    }
}
