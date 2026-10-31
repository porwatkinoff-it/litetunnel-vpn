import CoreImage
import CoreImage.CIFilterBuiltins
import LiteKit
import SwiftUI
#if os(iOS)
import UIKit
#else
import AppKit
#endif

enum Format {
    private static let bytes: ByteCountFormatter = {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .binary
        formatter.allowedUnits = [.useKB, .useMB, .useGB, .useTB]
        return formatter
    }()

    static func size(_ value: Int64) -> String {
        bytes.string(fromByteCount: value)
    }

    static func speed(_ value: Int64) -> String {
        "\(bytes.string(fromByteCount: value))/с"
    }

    static func duration(since start: Date, now: Date = Date()) -> String {
        let total = max(0, Int(now.timeIntervalSince(start)))
        let h = total / 3600, m = total / 60 % 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }

    static func date(_ date: Date) -> String {
        date.formatted(date: .abbreviated, time: .omitted)
    }

    static func relative(_ date: Date) -> String {
        date.formatted(.relative(presentation: .named))
    }

    /// "истекает через 12 дн." or "истекла".
    static func expiry(_ date: Date, now: Date = Date()) -> String {
        guard date > now else { return "истекла \(Self.date(date))" }
        let days = Int(date.timeIntervalSince(now) / 86_400)
        return days < 1 ? "истекает сегодня" : "до \(Self.date(date)) · \(days) дн."
    }

    /// "1 сервер", "3 сервера", "5 серверов".
    static func servers(_ count: Int) -> String {
        let tens = count % 100, ones = count % 10
        let word = (11...14).contains(tens) ? "серверов" : ones == 1 ? "сервер" : (2...4).contains(ones) ? "сервера" : "серверов"
        return "\(count) \(word)"
    }
}

extension PingResult {
    var label: String {
        switch self {
        case .ms(let ms): return "\(ms) мс"
        case .failed: return "нет связи"
        }
    }

    /// How full the signal bars are.
    var level: Double {
        switch self {
        case .ms(let ms) where ms < 100: return 1
        case .ms(let ms) where ms < 200: return 0.75
        case .ms(let ms) where ms < 400: return 0.5
        case .ms: return 0.25
        case .failed: return 0
        }
    }

    var color: Color {
        switch self {
        case .ms(let ms) where ms < 150: return .green
        case .ms(let ms) where ms < 400: return .orange
        case .ms: return .red
        case .failed: return .red
        }
    }
}

extension PingMode {
    var title: String {
        switch self {
        case .real: return "Реальный"
        case .proxyGet: return "Через сервер, GET"
        case .proxyHead: return "Через сервер, HEAD"
        case .tcp: return "TCP"
        case .icmp: return "ICMP"
        }
    }

    var footer: String {
        switch self {
        case .real:
            return "Запрос к адресу проверки через каждый сервер. Показывает, что сервер пропускает трафик; время приведено к обычному пингу (делится на 3,3), чтобы его было с чем сравнить."
        case .proxyGet, .proxyHead:
            return "Запрос к адресу проверки через каждый сервер. Показано полное время запроса: оно в несколько раз больше обычного пинга."
        case .tcp:
            return "Только соединение с сервером, без запроса через него. Быстро, но не покажет сервер, который не пропускает трафик."
        case .icmp:
            return "Обычный ping. Многие серверы на него не отвечают, тогда будет «нет связи»."
        }
    }
}

extension PingDisplay {
    var title: String {
        switch self {
        case .time: return "Цифры"
        case .bars: return "Полоски"
        case .both: return "Цифры и полоски"
        case .dots: return "Точки"
        }
    }
}

/// A server's last ping, drawn the way the settings ask.
struct PingBadge: View {
    let ping: PingResult
    let display: PingDisplay

    var body: some View {
        HStack(spacing: 4) {
            switch display {
            case .time:
                time
            case .bars:
                bars
            case .both:
                time
                bars
            case .dots:
                Circle()
                    .fill(ping == .failed ? Color.red : Color.green)
                    .frame(width: 8, height: 8)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(ping.label)
    }

    private var time: some View {
        Text(ping.label)
            .font(.caption.monospacedDigit())
            .foregroundStyle(ping.color)
    }

    private var bars: some View {
        Image(systemName: "cellularbars", variableValue: ping.level)
            .font(.caption)
            .foregroundStyle(ping.color)
    }
}

extension RoutingMode {
    var title: String {
        switch self {
        case .global: return "Всё через VPN"
        case .russiaDirect: return "Россия напрямую"
        }
    }
}

extension DNSServer.Kind {
    var title: String {
        switch self {
        case .udp: return "UDP"
        case .tls: return "DoT"
        case .https: return "DoH"
        }
    }
}

extension SortMode {
    var title: String {
        switch self {
        case .none: return "Как в подписке"
        case .ping: return "По пингу"
        case .name: return "По имени"
        }
    }
}

extension TunStack {
    var title: String {
        switch self {
        case .system: return "system"
        case .gvisor: return "gVisor"
        case .mixed: return "mixed"
        }
    }
}

enum Clipboard {
    static var string: String? {
        #if os(iOS)
        UIPasteboard.general.string
        #else
        NSPasteboard.general.string(forType: .string)
        #endif
    }

    static func copy(_ text: String) {
        #if os(iOS)
        UIPasteboard.general.string = text
        #else
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        #endif
    }
}

enum QRCode {
    private static let context = CIContext(options: [.useSoftwareRenderer: false])

    /// A crisp QR code for a share link.
    static func image(for text: String) -> CGImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        return context.createCGImage(scaled, from: scaled.extent)
    }

    /// Every QR code found in a picture, e.g. a screenshot from a provider.
    static func decode(_ data: Data) -> [String] {
        guard let image = CIImage(data: data),
              let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: context,
                                        options: [CIDetectorAccuracy: CIDetectorAccuracyHigh])
        else { return [] }
        return detector.features(in: image).compactMap { ($0 as? CIQRCodeFeature)?.messageString }
    }
}
