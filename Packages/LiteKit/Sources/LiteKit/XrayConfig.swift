import Foundation

/// Xray JSON, as panels serve it to Happ and v2RayTun: an array of full Xray
/// configs, one per server, or a single config. Each proxy outbound becomes a
/// sing-box one through the same TLS and transport code as share links. A
/// config with a balancer is one entry: a ServerGroup of the balanced outbounds.
enum XrayConfig {
    /// Routing targets inside the config, not servers.
    static let ignored: Set<String> = ["freedom", "blackhole", "dns", "loopback"]

    static func parse(_ config: JSONValue, into result: inout SubscriptionResult) {
        let remarks = ShareLink.nonEmpty(config["remarks"]?.stringValue)
        let proxies = (config["outbounds"]?.arrayValue ?? []).filter {
            !ignored.contains(($0["protocol"]?.stringValue ?? "freedom").lowercased())
        }
        let balanced = balancedOutbounds(config, proxies)
        if balanced.count > 1 {
            parseGroup(balanced, name: remarks ?? "Авто-выбор", into: &result)
            return
        }
        for outbound in proxies {
            let tag = ShareLink.nonEmpty(outbound["tag"]?.stringValue)
            var name = remarks ?? tag
            if proxies.count > 1, let remarks, let tag { name = "\(remarks) · \(tag)" }
            do {
                result.servers.append(try server(outbound, name: name))
            } catch {
                result.skipped.append(SkippedEntry(line: name ?? "Xray", reason: error.localizedDescription))
            }
        }
    }

    /// Proxy outbounds a routing balancer picks from: its `selector` lists tag
    /// prefixes, and as in Xray an empty one matches every tag. Empty when the
    /// config has no balancer.
    static func balancedOutbounds(_ config: JSONValue, _ proxies: [JSONValue]) -> [JSONValue] {
        let prefixes = (config["routing"]?["balancers"]?.arrayValue ?? [])
            .flatMap { $0["selector"]?.arrayValue ?? [] }
            .compactMap(\.stringValue)
        guard !prefixes.isEmpty else { return [] }
        return proxies.filter { outbound in
            let tag = outbound["tag"]?.stringValue ?? ""
            return prefixes.contains { tag.hasPrefix($0) }
        }
    }

    /// Members the core cannot run are left out and listed as skipped; the
    /// group stays as long as one of them is left.
    static func parseGroup(_ outbounds: [JSONValue], name: String, into result: inout SubscriptionResult) {
        var members: [ParsedServer] = []
        for outbound in outbounds {
            let line = "\(name) · \(outbound["tag"]?.stringValue ?? "?")"
            do {
                members.append(try server(outbound, name: line))
            } catch {
                result.skipped.append(SkippedEntry(line: line, reason: error.localizedDescription))
            }
        }
        if members.count == 1 {
            members[0].name = name
            result.servers.append(members[0])
        } else if !members.isEmpty {
            result.servers.append(ServerGroup.make(members, name: name))
        }
    }

    static func server(_ outbound: JSONValue, name: String?) throws -> ParsedServer {
        let proto = (outbound["protocol"]?.stringValue ?? "").lowercased()
        let settings = outbound["settings"] ?? [:]
        // Nested in vnext/servers, or flat in settings as newer Xray allows.
        let endpoint = settings["vnext"]?[0] ?? settings["servers"]?[0] ?? settings
        let user = endpoint["users"]?[0] ?? endpoint
        guard let host = ShareLink.nonEmpty(endpoint["address"]?.stringValue) else { throw ShareLinkError.invalid("нет адреса") }
        guard let port = endpoint["port"]?.intValue, ShareLink.validPort(port) else { throw ShareLinkError.invalid("порт") }
        let stream = outbound["streamSettings"] ?? [:]
        let security = stream["security"]?.stringValue ?? "none"
        let network = stream["network"]?.stringValue ?? "tcp"
        let q = query(stream, security: security, network: network)

        var out: JSONValue = ["server": .string(host), "server_port": .int(port)]
        let kind: ProxyProtocol
        switch proto {
        case "vless":
            guard let id = ShareLink.nonEmpty(user["id"]?.stringValue) else { throw ShareLinkError.invalid("нет UUID") }
            if let encryption = ShareLink.nonEmpty(user["encryption"]?.stringValue), encryption != "none" {
                throw ShareLinkError.unsupported("VLESS Encryption")
            }
            kind = .vless
            out["type"] = "vless"
            out["uuid"] = .string(id)
            out["packet_encoding"] = "xudp"
            if let flow = user["flow"]?.stringValue, flow.hasPrefix("xtls-rprx-vision") { out["flow"] = "xtls-rprx-vision" }
        case "vmess":
            guard let id = ShareLink.nonEmpty(user["id"]?.stringValue) else { throw ShareLinkError.invalid("нет UUID") }
            kind = .vmess
            out["type"] = "vmess"
            out["uuid"] = .string(id)
            out["security"] = .string(ShareLink.nonEmpty(user["security"]?.stringValue) ?? "auto")
            out["alter_id"] = .int(user["alterId"]?.intValue ?? 0)
            out["packet_encoding"] = "xudp"
        case "trojan":
            guard let password = ShareLink.nonEmpty(user["password"]?.stringValue) else { throw ShareLinkError.invalid("нет пароля") }
            kind = .trojan
            out["type"] = "trojan"
            out["password"] = .string(password)
        case "shadowsocks":
            guard let method = ShareLink.nonEmpty(user["method"]?.stringValue) else { throw ShareLinkError.invalid("нет метода") }
            out["type"] = "shadowsocks"
            out["method"] = .string(method.lowercased())
            out["password"] = .string(user["password"]?.stringValue ?? "")
            return ParsedServer(name: name ?? "\(host):\(port)", proto: .shadowsocks, host: host, port: port, outbound: out, shareLink: nil)
        case "socks":
            out["type"] = "socks"
            out["version"] = "5"
            if let username = ShareLink.nonEmpty(user["user"]?.stringValue) { out["username"] = .string(username) }
            if let password = ShareLink.nonEmpty(user["pass"]?.stringValue) { out["password"] = .string(password) }
            return ParsedServer(name: name ?? "\(host):\(port)", proto: .socks, host: host, port: port, outbound: out, shareLink: nil)
        case "hysteria":
            guard (settings["version"]?.intValue ?? 2) == 2 else { throw ShareLinkError.unsupported("Hysteria 1") }
            // Salamander and the like live in finalmask, whose shape is not documented yet.
            if stream["finalmask"] != nil || stream["finalMask"] != nil { throw ShareLinkError.unsupported("Hysteria с finalmask") }
            out["type"] = "hysteria2"
            out["password"] = .string(stream["hysteriaSettings"]?["auth"]?.stringValue ?? "")
            // QUIC: no uTLS fingerprint and no Reality.
            var tls = try ShareLink.tls(security: "tls", query: q, host: host)
            tls?["utls"] = nil
            out["tls"] = tls
            return ParsedServer(name: name ?? "\(host):\(port)", proto: .hysteria2, host: host, port: port, outbound: out, shareLink: nil)
        default:
            throw ShareLinkError.unsupported("Xray \(proto.isEmpty ? "без protocol" : proto)")
        }
        out["tls"] = try ShareLink.tls(security: security, query: q, host: host)
        out["transport"] = try ShareLink.transport(type: network, query: q)
        return ParsedServer(name: name ?? "\(host):\(port)", proto: kind, host: host, port: port, outbound: out, shareLink: nil)
    }

    /// streamSettings as the share-link query keys ShareLink.tls and .transport read.
    static func query(_ stream: JSONValue, security: String, network: String) -> [String: String] {
        var q: [String: String] = [:]
        let tls = (security.lowercased() == "reality" ? stream["realitySettings"] : stream["tlsSettings"]) ?? [:]
        q["sni"] = tls["serverName"]?.stringValue
        q["fp"] = tls["fingerprint"]?.stringValue
        q["alpn"] = list(tls["alpn"])
        if tls["allowInsecure"]?.boolValue == true { q["allowinsecure"] = "1" }
        q["pbk"] = tls["publicKey"]?.stringValue ?? tls["password"]?.stringValue
        q["sid"] = tls["shortId"]?.stringValue

        let transport: JSONValue?
        switch network.lowercased() {
        case "tcp", "raw": transport = stream["rawSettings"] ?? stream["tcpSettings"]
        case "ws", "websocket": transport = stream["wsSettings"]
        case "grpc", "gun": transport = stream["grpcSettings"]
        case "httpupgrade": transport = stream["httpupgradeSettings"]
        case "h2", "http": transport = stream["httpSettings"]
        default: transport = nil
        }
        guard let transport else { return q }
        q["path"] = transport["path"]?.stringValue
        q["host"] = list(transport["host"]) ?? transport["headers"]?["Host"]?.stringValue
        q["servicename"] = transport["serviceName"]?.stringValue
        q["headertype"] = transport["header"]?["type"]?.stringValue
        return q
    }

    /// A string, or an array of strings joined by commas as share links write them.
    static func list(_ value: JSONValue?) -> String? {
        if let s = value?.stringValue { return s }
        return value?.arrayValue.map { $0.compactMap(\.stringValue).joined(separator: ",") }
    }
}
