import CoreImage
import Foundation

enum QRImage {
    static func read(_ url: URL) throws -> String {
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        let metadata = try url.resourceValues(forKeys: [.isRegularFileKey])
        guard metadata.isRegularFile == true else { throw ClientError.image }
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        let limit = 12 * 1024 * 1024
        guard let data = try input.read(upToCount: limit + 1), data.count <= limit,
              let image = CIImage(data: data, options: [.applyOrientationProperty: true]),
              image.extent.width > 0, image.extent.height > 0,
              image.extent.width * image.extent.height <= 20_000_000,
              let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil,
                                        options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]) else { throw ClientError.image }
        let codes = detector.features(in: image)
        guard codes.count == 1, let code = (codes.first as? CIQRCodeFeature)?.messageString else { throw ClientError.image }
        return code
    }
}

#if os(iOS)
import AVFoundation
import SwiftUI
import VisionKit
import Vision

struct ScannerScreen: View {
    @Environment(\.dismiss) private var dismiss
    @State private var authorized = false
    let finish: (Result<String, any Error>) -> Void

    var body: some View {
        NavigationStack {
            Group {
                if authorized && QRScanner.available {
                    QRScanner(finish: finish).ignoresSafeArea(edges: .bottom)
                } else {
                    ContentUnavailableView("Camera Unavailable", systemImage: "camera",
                                           description: Text("Allow camera access in Settings, or import a QR image instead."))
                }
            }
            .navigationTitle("Scan Pairing QR")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } } }
            .task {
                authorized = await AVCaptureDevice.requestAccess(for: .video)
            }
        }
    }
}

struct QRScanner: UIViewControllerRepresentable {
    static var available: Bool { DataScannerViewController.isSupported && DataScannerViewController.isAvailable }
    let finish: (Result<String, any Error>) -> Void

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let controller = DataScannerViewController(recognizedDataTypes: [.barcode(symbologies: [.qr])],
                                                  isGuidanceEnabled: true, isHighlightingEnabled: true)
        controller.delegate = context.coordinator
        do { try controller.startScanning() }
        catch {
            let coordinator = context.coordinator
            Task { @MainActor in coordinator.complete(.failure(ClientError.image)) }
        }
        return controller
    }

    func updateUIViewController(_ controller: DataScannerViewController, context: Context) {}
    static func dismantleUIViewController(_ controller: DataScannerViewController, coordinator: Coordinator) {
        coordinator.finish = nil
        controller.stopScanning()
    }
    func makeCoordinator() -> Coordinator { Coordinator(finish: finish) }

    @MainActor final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        var finish: ((Result<String, any Error>) -> Void)?

        init(finish: @escaping (Result<String, any Error>) -> Void) { self.finish = finish }

        func complete(_ result: Result<String, any Error>) {
            let callback = finish
            finish = nil
            callback?(result)
        }

        func dataScanner(_ dataScanner: DataScannerViewController, didAdd addedItems: [RecognizedItem], allItems: [RecognizedItem]) {
            for item in addedItems {
                if case .barcode(let barcode) = item, let value = barcode.payloadStringValue {
                    dataScanner.stopScanning()
                    complete(.success(value))
                    return
                }
            }
        }

        func dataScanner(_ dataScanner: DataScannerViewController, becameUnavailableWithError error: DataScannerViewController.ScanningUnavailable) {
            complete(.failure(ClientError.image))
        }
    }
}
#endif
