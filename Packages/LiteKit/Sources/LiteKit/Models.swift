import Foundation

public enum ProxyProtocol: String, Codable, Sendable, CaseIterable {
    case vless, vmess, trojan, shadowsocks, hysteria2, tuic, socks, http

    public var title: String {
        switch self {
        case .vless: return "VLESS"
        case .vmess: return "VMess"
        case .trojan: return "Trojan"
        case .shadowsocks: return "Shadowsocks"
        case .hysteria2: return "Hysteria2"
        case .tuic: return "TUIC"
        case .socks: return "SOCKS5"
        case .http: return "HTTP"
        }
    }
}

/// What a share link or a subscription entry turns into before it gets an ID.
public struct ParsedServer: Hashable, Sendable {
    public var name: String
    public var proto: ProxyProtocol
    public var host: String
    public var port: Int
    /// sing-box outbound without `tag`.
    public var outbound: JSONValue
    public var shareLink: String?

    public init(name: String, proto: ProxyProtocol, host: String, port: Int, outbound: JSONValue, shareLink: String?) {
        self.name = name
        self.proto = proto
        self.host = host
        self.port = port
        self.outbound = outbound
        self.shareLink = shareLink
    }

    /// Survives a subscription refresh as long as the provider keeps the server.
    var matchKey: String { "\(proto.rawValue)|\(host)|\(port)|\(name)" }
}

public struct Server: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var subscriptionID: UUID?
    public var name: String
    public var proto: ProxyProtocol
    public var host: String
    public var port: Int
    public var outbound: JSONValue
    public var shareLink: String?
    public var lastPing: PingResult?

    public init(id: UUID = UUID(), subscriptionID: UUID?, parsed: ParsedServer) {
        self.id = id
        self.subscriptionID = subscriptionID
        self.name = parsed.name
        self.proto = parsed.proto
        self.host = parsed.host
        self.port = parsed.port
        self.outbound = parsed.outbound
        self.shareLink = parsed.shareLink
    }

    var matchKey: String { "\(proto.rawValue)|\(host)|\(port)|\(name)" }

    /// Transport and security in one short label, e.g. "Reality · Vision".
    public var detail: String {
        if let members = ServerGroup.members(of: outbound) { return "Самый быстрый из \(members.count)" }
        var parts: [String] = []
        if let tls = outbound["tls"], tls["enabled"]?.boolValue == true {
            parts.append(tls["reality"]?["enabled"]?.boolValue == true ? "Reality" : "TLS")
        }
        if let transport = outbound["transport"]?["type"]?.stringValue {
            parts.append(transport.uppercased())
        }
        if outbound["flow"]?.stringValue?.hasPrefix("xtls-rprx-vision") == true {
            parts.append("Vision")
        }
        if outbound["obfs"] != nil {
            parts.append("obfs")
        }
        if outbound["server_ports"] != nil {
            parts.append("hopping")
        }
        return parts.joined(separator: " · ")
    }

    /// The line under the name in a list. A group has no protocol of its own.
    public var subtitle: String {
        if isGroup { return detail }
        return [proto.title, detail].filter { !$0.isEmpty }.joined(separator: " · ")
    }
}

public enum PingResult: Codable, Hashable, Sendable {
    case ms(Int)
    case failed

    public var sortValue: Int {
        switch self {
        case .ms(let v): return v
        case .failed: return Int.max - 1
        }
    }
}

public struct Usage: Codable, Hashable, Sendable {
    public var upload: Int64
    public var download: Int64
    /// 0 means unlimited.
    public var total: Int64
    public var expire: Date?

    public init(upload: Int64, download: Int64, total: Int64, expire: Date?) {
        self.upload = upload
        self.download = download
        self.total = total
        self.expire = expire
    }

    public var used: Int64 { upload + download }

    public var fraction: Double? {
        total > 0 ? min(1, Double(used) / Double(total)) : nil
    }
}

public struct Subscription: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var url: URL
    public var userAgent: String?
    public var updateIntervalHours: Int
    public var lastUpdated: Date?
    public var lastError: String?
    public var usage: Usage?
    public var supportURL: URL?
    public var webPageURL: URL?
    public var announce: String?
    public var skippedCount: Int
    /// Why entries were skipped, without the entries: those hold keys.
    public var skippedReasons: [String]?
    public init(id: UUID = UUID(), name: String, url: URL) {
        self.id = id
        self.name = name
        self.url = url
        self.userAgent = nil
        self.updateIntervalHours = 24
        self.skippedCount = 0
    }

    public func isDue(at now: Date) -> Bool {
        guard let lastUpdated else { return true }
        return now.timeIntervalSince(lastUpdated) >= Double(max(1, updateIntervalHours)) * 3600
    }
}

public enum RoutingMode: String, Codable, Sendable, CaseIterable {
    /// Everything through the proxy except private networks.
    case global
    /// Russian domains and IPs go direct, the rest through the proxy.
    case russiaDirect
}

public struct DNSServer: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        case udp, tls, https
    }

    public var kind: Kind
    public var address: String

    public init(kind: Kind, address: String) {
        self.kind = kind
        self.address = address
    }

    public static let cloudflare = DNSServer(kind: .https, address: "1.1.1.1")
    public static let yandex = DNSServer(kind: .udp, address: "77.88.8.8")
}

public enum TunStack: String, Codable, Sendable, CaseIterable {
    case system, gvisor, mixed
}

public enum LogLevel: String, Codable, Sendable, CaseIterable {
    case error, warn, info, debug
}

public enum SortMode: String, Codable, Sendable, CaseIterable {
    case none, ping, name
}

/// How a ping is measured. The first three send an HTTP request through the
/// server itself, so a server that answers but does not pass traffic fails.
public enum PingMode: String, Codable, Sendable, CaseIterable {
    /// A GET through the server, its time divided by 3.3 so it reads like a
    /// plain ping rather than a full proxied round trip.
    case real
    case proxyGet
    case proxyHead
    /// A TCP handshake with the server; a QUIC one for Hysteria2 and TUIC.
    case tcp
    /// An ICMP echo to the server's address.
    case icmp

    public var throughProxy: Bool { self == .real || self == .proxyGet || self == .proxyHead }
}

public enum PingDisplay: String, Codable, Sendable, CaseIterable {
    /// Milliseconds.
    case time
    /// A signal-strength scale.
    case bars
    case both
    /// A dot: answered or not.
    case dots
}

public struct AppSettings: Codable, Hashable, Sendable {
    public var routing: RoutingMode = .russiaDirect
    public var directDomains: [String] = []
    public var proxyDomains: [String] = []
    public var blockDomains: [String] = []
    public var remoteDNS: DNSServer = .cloudflare
    public var directDNS: DNSServer = .yandex
    public var killSwitch = false
    public var excludeLocalNetworks = true
    public var smartSleep = true
    public var tlsFragment = false
    public var blockAds = false
    public var sniffing = true
    public var ipv6 = false
    /// 0 lets the core pick: 4064 inside a Network Extension, where larger
    /// values make the utun noticeably slower.
    public var mtu = 0
    public var tunStack: TunStack = .mixed
    public var logLevel: LogLevel = .warn
    public var sortMode: SortMode = .none
    public var pingOnOpen = false
    public var pingMode: PingMode = .real
    /// What the proxy pings request. Plain HTTP: no TLS handshake in the time.
    public var pingURL = AppSettings.defaultPingURL
    public var pingDisplay: PingDisplay = .time
    /// Ping a subscription's servers after it updates.
    public var pingOnUpdate = false

    public static let defaultPingURL = "http://www.gstatic.com/generate_204"

    public init() {}

    // Settings files written by older builds lack newer keys; fall back to defaults.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppSettings()
        routing = try c.decodeIfPresent(RoutingMode.self, forKey: .routing) ?? d.routing
        directDomains = try c.decodeIfPresent([String].self, forKey: .directDomains) ?? d.directDomains
        proxyDomains = try c.decodeIfPresent([String].self, forKey: .proxyDomains) ?? d.proxyDomains
        blockDomains = try c.decodeIfPresent([String].self, forKey: .blockDomains) ?? d.blockDomains
        remoteDNS = try c.decodeIfPresent(DNSServer.self, forKey: .remoteDNS) ?? d.remoteDNS
        directDNS = try c.decodeIfPresent(DNSServer.self, forKey: .directDNS) ?? d.directDNS
        killSwitch = try c.decodeIfPresent(Bool.self, forKey: .killSwitch) ?? d.killSwitch
        excludeLocalNetworks = try c.decodeIfPresent(Bool.self, forKey: .excludeLocalNetworks) ?? d.excludeLocalNetworks
        smartSleep = try c.decodeIfPresent(Bool.self, forKey: .smartSleep) ?? d.smartSleep
        tlsFragment = try c.decodeIfPresent(Bool.self, forKey: .tlsFragment) ?? d.tlsFragment
        blockAds = try c.decodeIfPresent(Bool.self, forKey: .blockAds) ?? d.blockAds
        sniffing = try c.decodeIfPresent(Bool.self, forKey: .sniffing) ?? d.sniffing
        ipv6 = try c.decodeIfPresent(Bool.self, forKey: .ipv6) ?? d.ipv6
        mtu = try c.decodeIfPresent(Int.self, forKey: .mtu) ?? d.mtu
        tunStack = try c.decodeIfPresent(TunStack.self, forKey: .tunStack) ?? d.tunStack
        logLevel = try c.decodeIfPresent(LogLevel.self, forKey: .logLevel) ?? d.logLevel
        sortMode = try c.decodeIfPresent(SortMode.self, forKey: .sortMode) ?? d.sortMode
        pingOnOpen = try c.decodeIfPresent(Bool.self, forKey: .pingOnOpen) ?? d.pingOnOpen
        pingMode = try c.decodeIfPresent(PingMode.self, forKey: .pingMode) ?? d.pingMode
        pingURL = try c.decodeIfPresent(String.self, forKey: .pingURL) ?? d.pingURL
        pingDisplay = try c.decodeIfPresent(PingDisplay.self, forKey: .pingDisplay) ?? d.pingDisplay
        pingOnUpdate = try c.decodeIfPresent(Bool.self, forKey: .pingOnUpdate) ?? d.pingOnUpdate
    }
}

public struct AppState: Codable, Sendable {
    public var subscriptions: [Subscription] = []
    public var servers: [Server] = []
    public var selectedServerID: UUID?
    public var settings = AppSettings()

    public init() {}

    public var selectedServer: Server? {
        servers.first { $0.id == selectedServerID }
    }

    public func servers(in subscriptionID: UUID?) -> [Server] {
        servers.filter { $0.subscriptionID == subscriptionID }
    }

    /// Adds servers that are not already in the list. Returns the new ones.
    @discardableResult
    public mutating func addManual(_ parsed: [ParsedServer]) -> [Server] {
        let existing = Set(servers.filter { $0.subscriptionID == nil }.map(\.matchKey))
        let added = parsed.filter { !existing.contains($0.matchKey) }.map { Server(subscriptionID: nil, parsed: $0) }
        servers.append(contentsOf: added)
        if selectedServerID == nil { selectedServerID = added.first?.id }
        return added
    }

    /// Replaces a subscription's servers with a fresh list, keeping IDs (and so
    /// the selection and last ping) for servers that are still there.
    public mutating func apply(_ result: SubscriptionResult, to subscriptionID: UUID, at date: Date = Date()) {
        guard let index = subscriptions.firstIndex(where: { $0.id == subscriptionID }) else { return }
        var sub = subscriptions[index]
        let meta = result.meta
        if let title = meta.title, !title.isEmpty { sub.name = title }
        if let hours = meta.updateIntervalHours { sub.updateIntervalHours = hours }
        sub.usage = meta.usage ?? sub.usage
        sub.supportURL = meta.supportURL ?? sub.supportURL
        sub.webPageURL = meta.webPageURL ?? sub.webPageURL
        sub.announce = meta.announce
        sub.lastUpdated = date
        sub.lastError = nil
        sub.skippedCount = result.skipped.count
        var seenReasons = Set<String>()
        sub.skippedReasons = Array(result.skipped.map(\.reason).filter { seenReasons.insert($0).inserted }.prefix(5))
        subscriptions[index] = sub

        let old = servers.filter { $0.subscriptionID == subscriptionID }
        var oldByKey: [String: Server] = [:]
        for server in old where !server.isAuto && oldByKey[server.matchKey] == nil {
            oldByKey[server.matchKey] = server
        }
        let rows: [Server] = result.servers.map { parsed in
            var server = Server(subscriptionID: subscriptionID, parsed: parsed)
            if let previous = oldByKey.removeValue(forKey: parsed.matchKey) {
                server.id = previous.id
                server.lastPing = previous.lastPing
            }
            return server
        }
        let fresh = withAutoRow(rows, in: subscriptionID, previous: old)
        replaceServers(in: subscriptionID, with: fresh)

        if let selected = selectedServerID, !servers.contains(where: { $0.id == selected }) {
            // The selected server left the subscription: fall back to its first server.
            selectedServerID = fresh.first?.id ?? servers.first?.id
        }
        if selectedServerID == nil { selectedServerID = fresh.first?.id }
    }

    /// A subscription's rows with its auto row in front, when it gets one.
    private func withAutoRow(_ rows: [Server], in subscriptionID: UUID, previous: [Server]) -> [Server] {
        guard let auto = ServerGroup.auto(for: rows) else { return rows }
        var row = Server(subscriptionID: subscriptionID, parsed: auto)
        if let old = previous.first(where: \.isAuto) {
            row.id = old.id
            row.lastPing = old.lastPing
        }
        return [row] + rows
    }

    /// Keeps the subscription's block where it was in the list.
    private mutating func replaceServers(in subscriptionID: UUID, with fresh: [Server]) {
        let insertAt = servers.firstIndex { $0.subscriptionID == subscriptionID } ?? servers.count
        let before = servers[..<insertAt].filter { $0.subscriptionID != subscriptionID }
        let after = servers[insertAt...].filter { $0.subscriptionID != subscriptionID }
        servers = Array(before) + fresh + Array(after)
    }

    /// Gives every subscription the auto row it should have: a state saved by
    /// an older build has none. Returns whether anything changed.
    public mutating func syncAutoGroups() -> Bool {
        let before = servers
        for sub in subscriptions {
            let old = servers(in: sub.id)
            guard !old.isEmpty else { continue }
            replaceServers(in: sub.id, with: withAutoRow(old.filter { !$0.isAuto }, in: sub.id, previous: old))
        }
        if let selected = selectedServerID, !servers.contains(where: { $0.id == selected }) {
            selectedServerID = servers.first?.id
        }
        return servers != before
    }

    /// How many servers a subscription really has: groups repeat them.
    public func serverCount(in subscriptionID: UUID?) -> Int {
        ServerGroup.uniqueMembers(of: servers(in: subscriptionID)).count
    }

    /// The rows standing for a group's members, when its subscription lists
    /// them all on their own.
    private func memberRows(of group: Server) -> [Server]? {
        guard group.subscriptionID != nil, let members = ServerGroup.members(of: group.outbound) else { return nil }
        var byKey: [JSONValue: Server] = [:]
        for row in servers(in: group.subscriptionID) where !row.isGroup {
            let key = ServerGroup.key(row.outbound)
            if byKey[key] == nil { byKey[key] = row }
        }
        let rows = members.compactMap { byKey[ServerGroup.key($0)] }
        return rows.count == members.count ? rows : nil
    }

    /// What to measure for these rows. A group that repeats its subscription's
    /// rows is measured through them, so no server is tested twice.
    public func pingTargets(_ list: [Server]) -> [Server] {
        var seen = Set<UUID>()
        var result: [Server] = []
        for server in list {
            for target in memberRows(of: server) ?? [server] where seen.insert(target.id).inserted {
                result.append(target)
            }
        }
        return result
    }

    /// Records a result, and in the groups built from this row their fastest.
    public mutating func setPing(_ result: PingResult?, for id: UUID) {
        guard let index = servers.firstIndex(where: { $0.id == id }) else { return }
        servers[index].lastPing = result
        let row = servers[index]
        guard !row.isGroup, row.subscriptionID != nil else { return }
        for group in servers(in: row.subscriptionID) where group.isGroup {
            guard let rows = memberRows(of: group), rows.contains(where: { $0.id == id }),
                  let groupIndex = servers.firstIndex(where: { $0.id == group.id })
            else { continue }
            servers[groupIndex].lastPing = Ping.fastest(rows.map(\.lastPing))
        }
    }

    public mutating func removeSubscription(_ id: UUID) {
        subscriptions.removeAll { $0.id == id }
        servers.removeAll { $0.subscriptionID == id }
        if let selected = selectedServerID, !servers.contains(where: { $0.id == selected }) {
            selectedServerID = servers.first?.id
        }
    }

    public mutating func removeServer(_ id: UUID) {
        servers.removeAll { $0.id == id }
        if selectedServerID == id { selectedServerID = servers.first?.id }
    }

    /// Groups stay on top in any order: they are not one more server to compare.
    public func sorted(_ list: [Server]) -> [Server] {
        let groups = list.filter(\.isGroup)
        let rest = list.filter { !$0.isGroup }
        switch settings.sortMode {
        case .none:
            return list
        case .name:
            return groups + rest.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        case .ping:
            // Stable: servers without a result keep their order at the end.
            return groups + rest.enumerated().sorted { a, b in
                let pa = a.element.lastPing?.sortValue ?? Int.max
                let pb = b.element.lastPing?.sortValue ?? Int.max
                return pa == pb ? a.offset < b.offset : pa < pb
            }.map(\.element)
        }
    }
}
