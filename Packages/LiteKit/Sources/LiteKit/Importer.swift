import Foundation

/// What pasted text, a scanned QR code or an opened link turns out to be.
public enum ImportItem: Equatable, Sendable {
    case subscription(URL, name: String?)
    case servers([ParsedServer], skipped: [SkippedEntry])
    case nothing(reason: String)
}

public enum Importer {
    public static let scheme = "litetunnel"

    public static func classify(_ text: String) -> ImportItem {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .nothing(reason: "Пусто") }

        if let url = URL(string: trimmed), url.scheme?.lowercased() == scheme {
            return deepLink(url)
        }
        if !trimmed.contains(where: \.isNewline), let url = URL(string: trimmed),
           let s = url.scheme?.lowercased(), s == "http" || s == "https", url.host != nil {
            return .subscription(url, name: nil)
        }
        let result = SubscriptionParser.parseBody(Data(trimmed.utf8))
        if !result.servers.isEmpty { return .servers(result.servers, skipped: result.skipped) }
        if let first = result.skipped.first { return .nothing(reason: first.reason) }
        return .nothing(reason: "Не похоже на ссылку сервера или подписки")
    }

    /// `litetunnel://add/<url or share link>` and
    /// `litetunnel://import?url=<encoded>&name=<name>`.
    public static func deepLink(_ url: URL) -> ImportItem {
        let raw = url.absoluteString
        let lower = raw.lowercased()
        let addPrefix = "\(scheme)://add/"
        if lower.hasPrefix(addPrefix) {
            let payload = String(raw.dropFirst(addPrefix.count))
            let decoded = payload.contains("://") ? payload : (payload.removingPercentEncoding ?? payload)
            return classify(decoded)
        }
        if url.host?.lowercased() == "import",
           let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
           let target = items.first(where: { $0.name == "url" })?.value {
            let name = items.first(where: { $0.name == "name" })?.value
            if case .subscription(let sub, _) = classify(target) { return .subscription(sub, name: name) }
            return classify(target)
        }
        return .nothing(reason: "Неизвестная ссылка \(scheme)://")
    }
}
