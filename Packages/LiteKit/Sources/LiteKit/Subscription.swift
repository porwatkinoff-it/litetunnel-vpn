import Foundation

public struct SubscriptionMeta: Hashable, Sendable {
    public var title: String?
    public var updateIntervalHours: Int?
    public var usage: Usage?
    public var supportURL: URL?
    public var webPageURL: URL?
    public var announce: String?

    public init() {}
}

public struct SkippedEntry: Hashable, Sendable {
    public var line: String
    public var reason: String
}

public struct SubscriptionResult: Sendable {
    public var servers: [ParsedServer]
    public var skipped: [SkippedEntry]
    public var meta: SubscriptionMeta
    /// `happ://routing/...` lines found in the body; read later by the routing importer.
    public var routingLines: [String]
}

public enum SubscriptionError: Error, LocalizedError, Equatable {
    case http(Int)
    case empty
    case nothingSupported(String)
    case deviceLimit
    case deviceIDRefused

    public var errorDescription: String? {
        switch self {
        case .http(let code): return "Сервер подписки ответил \(code)"
        case .empty: return "Подписка пустая"
        case .nothingSupported(let reason): return "В подписке нет поддерживаемых серверов (\(reason))"
        case .deviceLimit: return "Достигнут лимит устройств подписки: отвяжите старое устройство у провайдера"
        case .deviceIDRefused: return "Провайдер не принял ID устройства и не выдал серверы"
        }
    }
}

public enum SubscriptionParser {
    public static func parse(body: Data, headers: [String: String]) throws -> SubscriptionResult {
        // Device-limit panels answer 200 with a placeholder server and say why
        // in these headers (docs.rw/docs/features/hwid-device-limit).
        let flag = { (name: String) in
            headers.contains { $0.key.lowercased() == name && $0.value.lowercased() == "true" }
        }
        if flag("x-hwid-max-devices-reached") { throw SubscriptionError.deviceLimit }
        if flag("x-hwid-not-supported") { throw SubscriptionError.deviceIDRefused }
        var result = parseBody(body)
        result.meta = parseHeaders(headers)
        if result.servers.isEmpty {
            if let first = result.skipped.first { throw SubscriptionError.nothingSupported(first.reason) }
            throw SubscriptionError.empty
        }
        return result
    }

    public static func parseBody(_ body: Data) -> SubscriptionResult {
        var result = SubscriptionResult(servers: [], skipped: [], meta: SubscriptionMeta(), routingLines: [])
        guard var text = String(data: body, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty else { return result }

        if text.hasPrefix("{") || text.hasPrefix("[") {
            parseJSON(text, into: &result)
            return result
        }
        if !text.contains("://"), let decoded = Base64.decodeString(text), decoded.contains("://") {
            text = decoded
        }
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix("//") { continue }
            if line.lowercased().hasPrefix("happ://routing/") {
                result.routingLines.append(line)
                continue
            }
            if ShareLink.scheme(of: line) == nil {
                // Some panels put a base64 block per line.
                if let decoded = Base64.decodeString(line), decoded.contains("://") {
                    let inner = parseBody(Data(decoded.utf8))
                    result.servers += inner.servers
                    result.skipped += inner.skipped
                    result.routingLines += inner.routingLines
                }
                continue
            }
            do {
                result.servers.append(try ShareLink.parse(line))
            } catch {
                result.skipped.append(SkippedEntry(line: line, reason: error.localizedDescription))
            }
        }
        return result
    }

    /// sing-box JSON: proxy outbounds taken as they are. Xray JSON (an array of
    /// configs, one per server, or one config): converted by XrayConfig.
    static func parseJSON(_ text: String, into result: inout SubscriptionResult) {
        let unknown = SkippedEntry(line: String(text.prefix(80)), reason: "Неизвестный формат JSON")
        guard let json = try? JSONValue.parse(Data(text.utf8)) else {
            result.skipped.append(unknown)
            return
        }
        let configs = json.arrayValue ?? [json]
        if !configs.contains(where: { $0["outbounds"]?.arrayValue != nil }) { result.skipped.append(unknown) }
        for config in configs {
            let outbounds = config["outbounds"]?.arrayValue ?? []
            if outbounds.contains(where: { $0["protocol"] != nil }) {
                XrayConfig.parse(config, into: &result)
            } else {
                parseSingBox(outbounds, into: &result)
            }
        }
    }

    static func parseSingBox(_ outbounds: [JSONValue], into result: inout SubscriptionResult) {
        for outbound in outbounds {
            guard let type = outbound["type"]?.stringValue,
                  let proto = ProxyProtocol(rawValue: type),
                  let host = outbound["server"]?.stringValue,
                  let port = outbound["server_port"]?.intValue ?? outbound["server_ports"]?.arrayValue?.first?.stringValue
                      .flatMap({ Int($0.split(separator: ":")[0]) })
            else { continue }
            var stripped = outbound
            stripped["tag"] = nil
            stripped["detour"] = nil
            let name = outbound["tag"]?.stringValue ?? "\(host):\(port)"
            result.servers.append(ParsedServer(name: name, proto: proto, host: host, port: port, outbound: stripped, shareLink: nil))
        }
    }

    public static func parseHeaders(_ headers: [String: String]) -> SubscriptionMeta {
        var lower: [String: String] = [:]
        for (key, value) in headers { lower[key.lowercased()] = value }
        var meta = SubscriptionMeta()
        meta.title = lower["profile-title"].map(decodeHeaderText)
        meta.updateIntervalHours = lower["profile-update-interval"].flatMap { Int($0.trimmingCharacters(in: .whitespaces)) }
            .map { max(1, $0) }
        meta.usage = lower["subscription-userinfo"].flatMap(parseUserInfo)
        meta.supportURL = lower["support-url"].flatMap { URL(string: $0.trimmingCharacters(in: .whitespaces)) }
        meta.webPageURL = lower["profile-web-page-url"].flatMap { URL(string: $0.trimmingCharacters(in: .whitespaces)) }
        meta.announce = lower["announce"].map(decodeHeaderText).flatMap { $0.isEmpty ? nil : $0 }
        return meta
    }

    /// `upload=1; download=2; total=3; expire=1700000000`
    static func parseUserInfo(_ value: String) -> Usage? {
        var fields: [String: Int64] = [:]
        for part in value.split(separator: ";") {
            let kv = part.split(separator: "=", maxSplits: 1)
            guard kv.count == 2 else { continue }
            let key = kv[0].trimmingCharacters(in: .whitespaces).lowercased()
            let number = kv[1].trimmingCharacters(in: .whitespaces)
            fields[key] = Int64(number) ?? Double(number).map { Int64($0) }
        }
        guard !fields.isEmpty else { return nil }
        let expire = fields["expire"].flatMap { $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0)) : nil }
        return Usage(upload: fields["upload"] ?? 0, download: fields["download"] ?? 0, total: fields["total"] ?? 0, expire: expire)
    }

    /// Header text is plain, percent-encoded, or `base64:...`.
    static func decodeHeaderText(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespaces)
        if trimmed.lowercased().hasPrefix("base64:") {
            return Base64.decodeString(String(trimmed.dropFirst(7)))?.trimmingCharacters(in: .whitespacesAndNewlines) ?? trimmed
        }
        return trimmed.removingPercentEncoding ?? trimmed
    }
}

public struct SubscriptionClient: Sendable {
    public static let defaultUserAgent = "LiteTunnel/0.1"
    public var timeout: TimeInterval = 15

    public init() {}

    public func fetch(_ subscription: Subscription) async throws -> SubscriptionResult {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = timeout
        config.timeoutIntervalForResource = timeout * 2
        config.waitsForConnectivity = false
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }

        var request = URLRequest(url: subscription.url)
        request.setValue(subscription.userAgent ?? Self.defaultUserAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("*/*", forHTTPHeaderField: "Accept")
        for (name, value) in DeviceInfo.headers { request.setValue(value, forHTTPHeaderField: name) }
        let (data, response) = try await session.data(for: request)
        var headers: [String: String] = [:]
        if let http = response as? HTTPURLResponse {
            guard (200..<300).contains(http.statusCode) else { throw SubscriptionError.http(http.statusCode) }
            for (key, value) in http.allHeaderFields {
                if let key = key as? String, let value = value as? String { headers[key] = value }
            }
        }
        return try SubscriptionParser.parse(body: data, headers: headers)
    }
}
