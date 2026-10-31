import SwiftUI
import VisionKit

/// Live camera QR reader. The camera runs only while this screen is shown.
struct QRScannerView: UIViewControllerRepresentable {
    let onFound: (String) -> Void

    @MainActor static var isAvailable: Bool {
        DataScannerViewController.isSupported && DataScannerViewController.isAvailable
    }

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced,
            isHighlightingEnabled: true
        )
        scanner.delegate = context.coordinator
        return scanner
    }

    func updateUIViewController(_ scanner: DataScannerViewController, context: Context) {
        if !scanner.isScanning { try? scanner.startScanning() }
    }

    static func dismantleUIViewController(_ scanner: DataScannerViewController, coordinator: Coordinator) {
        scanner.stopScanning()
    }

    func makeCoordinator() -> Coordinator { Coordinator(onFound: onFound) }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        let onFound: (String) -> Void
        private var done = false

        init(onFound: @escaping (String) -> Void) { self.onFound = onFound }

        func dataScanner(_ scanner: DataScannerViewController, didAdd added: [RecognizedItem], allItems: [RecognizedItem]) {
            guard !done else { return }
            for item in added {
                if case .barcode(let code) = item, let value = code.payloadStringValue, !value.isEmpty {
                    done = true
                    scanner.stopScanning()
                    onFound(value)
                    return
                }
            }
        }
    }
}

struct QRScannerSheet: View {
    @Environment(\.dismiss) private var dismiss
    let onFound: (String) -> Void

    var body: some View {
        NavigationStack {
            QRScannerView(onFound: onFound)
                .ignoresSafeArea()
                .navigationTitle("Наведите на QR-код")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Закрыть") { dismiss() }
                    }
                }
        }
    }
}
