import Foundation

public enum ShareLinkError: Error, Equatable, LocalizedError {
    case unsupportedScheme(String)
    case unsupported(String)
    case invalid(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedScheme(let scheme): return "Протокол \(scheme):// не поддерживается"
        case .unsupported(let what): return "Не поддерживается: \(what)"
        case .invalid(let why): return "Ссылка повреждена: \(why)"
        }
    }
}

/// Turns proxy share links (vless://, hysteria2://, trojan://, ss://, vmess://,
/// tuic://, socks://) into sing-box outbounds.
public enum ShareLink {
    static let parsers: [String: (String) throws -> ParsedServer] = [
        "vless": parseVLESS,
        "trojan": parseTrojan,
        "hysteria2": parseHysteria2,
        "hy2": parseHysteria2,
        "ss": parseShadowsocks,
        "vmess": parseVMess,
        "tuic": parseTUIC,
        "socks": parseSOCKS,
        "socks5": parseSOCKS,
    ]

    /// Schemes we recognise as proxy links, including ones we can only report as unsupported.
    static let knownSchemes: Set<String> = Set(parsers.keys).union(["wireguard", "wg", "hysteria", "ssr", "anytls", "naive+https", "juicity"])

    public static func scheme(of line: String) -> String? {
        guard let range = line.range(of: "://") else { return nil }
        let scheme = line[..<range.lowerBound].lowercased()
        return knownSchemes.contains(scheme) ? scheme : nil
    }

    public static func parse(_ raw: String) throws -> ParsedServer {
        let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let scheme = scheme(of: line) else {
            throw ShareLinkError.unsupportedScheme(line.components(separatedBy: "://").first ?? line)
        }
        guard let parser = parsers[scheme] else {
            throw ShareLinkError.unsupportedScheme(scheme)
        }
        return try parser(line)
    }

    // MARK: - VLESS

    static func parseVLESS(_ line: String) throws -> ParsedServer {
        let uri = try LinkURI(line)
        guard let uuid = uri.user, !uuid.isEmpty else { throw ShareLinkError.invalid("нет UUID") }
        let port = try uri.singlePort()
        if let encryption = uri.query["encryption"], !encryption.isEmpty, encryption != "none" {
            throw ShareLinkError.unsupported("VLESS Encryption")
        }
        var outbound: JSONValue = [
            "type": "vless",
            "server": .string(uri.host),
            "server_port": .int(port),
            "uuid": .string(uuid),
            "packet_encoding": "xudp",
        ]
        if let flow = uri.query["flow"], flow.hasPrefix("xtls-rprx-vision") {
            outbound["flow"] = "xtls-rprx-vision"
        }
        outbound["tls"] = try tls(security: uri.query["security"] ?? "none", query: uri.query, host: uri.host)
        outbound["transport"] = try transport(type: uri.query["type"] ?? "tcp", query: uri.query)
        return ParsedServer(name: uri.name, proto: .vless, host: uri.host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - Trojan

    static func parseTrojan(_ line: String) throws -> ParsedServer {
        let uri = try LinkURI(line)
        guard let password = uri.userinfo, !password.isEmpty else { throw ShareLinkError.invalid("нет пароля") }
        let port = try uri.singlePort()
        var outbound: JSONValue = [
            "type": "trojan",
            "server": .string(uri.host),
            "server_port": .int(port),
            "password": .string(password),
        ]
        outbound["tls"] = try tls(security: uri.query["security"] ?? "tls", query: uri.query, host: uri.host)
        outbound["transport"] = try transport(type: uri.query["type"] ?? "tcp", query: uri.query)
        return ParsedServer(name: uri.name, proto: .trojan, host: uri.host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - Hysteria2

    static func parseHysteria2(_ line: String) throws -> ParsedServer {
        let uri = try LinkURI(line)
        let q = uri.query
        var single: Int?
        var ranges: [String] = []
        let portParts = uri.portSpec.split(separator: ",") + (q["mport"] ?? "").split(separator: ",")
        for part in portParts {
            let bounds = part.split(separator: "-", maxSplits: 1).map { Int($0.trimmingCharacters(in: .whitespaces)) }
            if bounds.count == 2, let lo = bounds[0], let hi = bounds[1], validPort(lo), validPort(hi), lo <= hi {
                ranges.append("\(lo):\(hi)")
            } else if bounds.count == 1, let p = bounds[0], validPort(p) {
                if single == nil { single = p } else { ranges.append("\(p):\(p)") }
            } else {
                throw ShareLinkError.invalid("порт \(part)")
            }
        }
        if single == nil && ranges.isEmpty { single = 443 }
        let port = single ?? Int(ranges[0].split(separator: ":")[0])!

        var outbound: JSONValue = [
            "type": "hysteria2",
            "server": .string(uri.host),
            "server_port": .int(port),
            "password": .string(uri.userinfo ?? ""),
        ]
        if !ranges.isEmpty {
            // server_port is ignored once server_ports is set, so the main port goes in the list too.
            if let single { ranges.insert("\(single):\(single)", at: 0) }
            outbound["server_ports"] = .strings(ranges)
            if let interval = q["mporthopint"] ?? q["hopinterval"] ?? q["hop_interval"], !interval.isEmpty {
                outbound["hop_interval"] = .string(Int(interval) != nil ? "\(interval)s" : interval)
            }
        }
        if let obfs = q["obfs"], !obfs.isEmpty, obfs != "none" {
            guard obfs == "salamander" else { throw ShareLinkError.unsupported("obfs \(obfs)") }
            outbound["obfs"] = ["type": "salamander", "password": .string(q["obfs-password"] ?? "")]
        }
        if let up = q["upmbps"].flatMap(Int.init) { outbound["up_mbps"] = .int(up) }
        if let down = q["downmbps"].flatMap(Int.init) { outbound["down_mbps"] = .int(down) }

        var tls: JSONValue = ["enabled": true]
        if let sni = nonEmpty(q["sni"]) ?? (isIPAddress(uri.host) ? nil : uri.host) {
            tls["server_name"] = .string(sni)
        }
        if isTrue(q["insecure"]) || isTrue(q["allowinsecure"]) { tls["insecure"] = true }
        if let alpn = nonEmpty(q["alpn"]) { tls["alpn"] = .strings(splitList(alpn)) }
        outbound["tls"] = tls
        return ParsedServer(name: uri.name, proto: .hysteria2, host: uri.host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - TUIC

    static func parseTUIC(_ line: String) throws -> ParsedServer {
        let uri = try LinkURI(line)
        guard let uuid = uri.user, !uuid.isEmpty else { throw ShareLinkError.invalid("нет UUID") }
        let port = try uri.singlePort()
        let q = uri.query
        var outbound: JSONValue = [
            "type": "tuic",
            "server": .string(uri.host),
            "server_port": .int(port),
            "uuid": .string(uuid),
            "password": .string(uri.password ?? ""),
        ]
        if let cc = nonEmpty(q["congestion_control"]) { outbound["congestion_control"] = .string(cc) }
        if let mode = nonEmpty(q["udp_relay_mode"]) { outbound["udp_relay_mode"] = .string(mode) }
        var tls: JSONValue = ["enabled": true]
        if let sni = nonEmpty(q["sni"]) ?? (isIPAddress(uri.host) ? nil : uri.host) { tls["server_name"] = .string(sni) }
        if isTrue(q["allow_insecure"]) || isTrue(q["insecure"]) || isTrue(q["allowinsecure"]) { tls["insecure"] = true }
        tls["alpn"] = .strings(splitList(nonEmpty(q["alpn"]) ?? "h3"))
        outbound["tls"] = tls
        return ParsedServer(name: uri.name, proto: .tuic, host: uri.host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - SOCKS

    static func parseSOCKS(_ line: String) throws -> ParsedServer {
        let uri = try LinkURI(line)
        let port = try uri.singlePort()
        var outbound: JSONValue = [
            "type": "socks",
            "server": .string(uri.host),
            "server_port": .int(port),
            "version": "5",
        ]
        if let userinfo = uri.userinfo, !userinfo.isEmpty {
            // Either user:pass, or base64(user:pass) as v2rayN writes it.
            let plain = userinfo.contains(":") ? userinfo : (Base64.decodeString(userinfo) ?? userinfo)
            let parts = plain.split(separator: ":", maxSplits: 1).map(String.init)
            outbound["username"] = .string(parts[0])
            if parts.count > 1 { outbound["password"] = .string(parts[1]) }
        }
        return ParsedServer(name: uri.name, proto: .socks, host: uri.host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - Shadowsocks

    static func parseShadowsocks(_ line: String) throws -> ParsedServer {
        var body = String(line.dropFirst("ss://".count))
        var name: String?
        if let hash = body.firstIndex(of: "#") {
            name = decodeName(String(body[body.index(after: hash)...]))
            body = String(body[..<hash])
        }
        var query: [String: String] = [:]
        if let q = body.firstIndex(of: "?") {
            query = LinkURI.parseQuery(String(body[body.index(after: q)...]))
            body = String(body[..<q])
        }
        while body.hasSuffix("/") { body.removeLast() }

        let methodPassword: String
        let hostPort: String
        if let at = body.lastIndex(of: "@") {
            // SIP002: userinfo is base64url(method:password), or percent-encoded for 2022 ciphers.
            let rawUser = String(body[..<at])
            hostPort = String(body[body.index(after: at)...])
            let decoded = rawUser.removingPercentEncoding ?? rawUser
            if decoded.contains(":") {
                methodPassword = decoded
            } else if let b64 = Base64.decodeString(rawUser) {
                methodPassword = b64
            } else {
                throw ShareLinkError.invalid("userinfo")
            }
        } else {
            // Legacy: base64(method:password@host:port)
            guard let decoded = Base64.decodeString(body), let at = decoded.lastIndex(of: "@") else {
                throw ShareLinkError.invalid("base64")
            }
            methodPassword = String(decoded[..<at])
            hostPort = String(decoded[decoded.index(after: at)...])
        }
        let mp = methodPassword.split(separator: ":", maxSplits: 1).map(String.init)
        guard mp.count == 2, !mp[0].isEmpty else { throw ShareLinkError.invalid("метод и пароль") }
        let (host, portSpec) = try LinkURI.splitHostPort(hostPort)
        guard let port = Int(portSpec), validPort(port) else { throw ShareLinkError.invalid("порт") }

        var outbound: JSONValue = [
            "type": "shadowsocks",
            "server": .string(host),
            "server_port": .int(port),
            "method": .string(mp[0].lowercased()),
            "password": .string(mp[1]),
        ]
        if let plugin = nonEmpty(query["plugin"]) {
            let parts = plugin.split(separator: ";", maxSplits: 1).map(String.init)
            switch parts[0] {
            case "obfs-local", "simple-obfs": outbound["plugin"] = "obfs-local"
            case "v2ray-plugin": outbound["plugin"] = "v2ray-plugin"
            default: throw ShareLinkError.unsupported("плагин \(parts[0])")
            }
            if parts.count > 1 { outbound["plugin_opts"] = .string(parts[1]) }
        }
        return ParsedServer(name: name ?? "\(host):\(port)", proto: .shadowsocks, host: host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - VMess

    static func parseVMess(_ line: String) throws -> ParsedServer {
        var body = String(line.dropFirst("vmess://".count))
        if let cut = body.firstIndex(where: { $0 == "?" || $0 == "#" }) { body = String(body[..<cut]) }
        guard let data = Base64.decode(body), let json = try? JSONValue.parse(data), json.objectValue != nil else {
            throw ShareLinkError.invalid("vmess base64")
        }
        func field(_ key: String) -> String? {
            switch json[key] {
            case .string(let s): return s.isEmpty ? nil : s
            case .int(let i): return String(i)
            case .double(let d): return String(Int(d))
            default: return nil
            }
        }
        guard let host = field("add") else { throw ShareLinkError.invalid("нет адреса") }
        guard let port = field("port").flatMap(Int.init), validPort(port) else { throw ShareLinkError.invalid("порт") }
        guard let uuid = field("id") else { throw ShareLinkError.invalid("нет UUID") }

        var outbound: JSONValue = [
            "type": "vmess",
            "server": .string(host),
            "server_port": .int(port),
            "uuid": .string(uuid),
            "security": .string(field("scy") ?? "auto"),
            "alter_id": .int(field("aid").flatMap(Int.init) ?? 0),
            "packet_encoding": "xudp",
        ]
        var query: [String: String] = [:]
        query["host"] = field("host")
        query["path"] = field("path")
        query["servicename"] = field("path")
        query["headertype"] = field("type")
        query["sni"] = field("sni")
        query["alpn"] = field("alpn")
        query["fp"] = field("fp")
        query["allowinsecure"] = field("allowInsecure") ?? field("insecure")
        outbound["tls"] = try tls(security: field("tls") ?? "none", query: query, host: host)
        outbound["transport"] = try transport(type: field("net") ?? "tcp", query: query)
        return ParsedServer(name: field("ps") ?? "\(host):\(port)", proto: .vmess, host: host, port: port, outbound: outbound, shareLink: line)
    }

    // MARK: - Shared pieces

    static let utlsFingerprints: Set<String> = ["chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized"]

    static func tls(security: String, query q: [String: String], host: String) throws -> JSONValue? {
        let security = security.lowercased()
        switch security {
        case "", "none":
            return nil
        case "tls", "xtls", "reality":
            break
        default:
            throw ShareLinkError.unsupported("security=\(security)")
        }
        var tls: JSONValue = ["enabled": true]
        if let sni = nonEmpty(q["sni"]) ?? nonEmpty(q["peer"]) ?? nonEmpty(q["host"]).map({ splitList($0)[0] })
            ?? (isIPAddress(host) ? nil : host) {
            tls["server_name"] = .string(sni)
        }
        if isTrue(q["allowinsecure"]) || isTrue(q["insecure"]) { tls["insecure"] = true }
        if let alpn = nonEmpty(q["alpn"]) { tls["alpn"] = .strings(splitList(alpn)) }
        let fingerprint = nonEmpty(q["fp"])?.lowercased()
        if fingerprint != nil || security == "reality" {
            var fp = fingerprint ?? "chrome"
            if fp == "randomizednoalpn" { fp = "randomized" }
            if !utlsFingerprints.contains(fp) { fp = "chrome" }
            tls["utls"] = ["enabled": true, "fingerprint": .string(fp)]
        }
        if security == "reality" {
            guard let pbk = nonEmpty(q["pbk"]) else { throw ShareLinkError.invalid("Reality без pbk") }
            tls["reality"] = ["enabled": true, "public_key": .string(pbk), "short_id": .string(q["sid"] ?? "")]
        }
        if q["fragment"] != nil { tls["fragment"] = true }
        return tls
    }

    static func transport(type: String, query q: [String: String]) throws -> JSONValue? {
        let host = nonEmpty(q["host"])
        switch type.lowercased() {
        case "", "tcp", "raw":
            if let header = q["headertype"], header.lowercased() == "http" {
                throw ShareLinkError.unsupported("TCP с HTTP-маскировкой")
            }
            return nil
        case "ws", "websocket":
            var t: JSONValue = ["type": "ws"]
            var path = nonEmpty(q["path"]) ?? "/"
            if let ed = path.range(of: "?ed=") {
                if let n = Int(path[ed.upperBound...].prefix { $0.isNumber }) {
                    t["max_early_data"] = .int(n)
                    t["early_data_header_name"] = "Sec-WebSocket-Protocol"
                }
                path = String(path[..<ed.lowerBound])
            }
            t["path"] = .string(path)
            if let host { t["headers"] = ["Host": .string(host)] }
            return t
        case "grpc", "gun":
            return ["type": "grpc", "service_name": .string(nonEmpty(q["servicename"]) ?? "")]
        case "httpupgrade":
            var t: JSONValue = ["type": "httpupgrade", "path": .string(nonEmpty(q["path"]) ?? "/")]
            if let host { t["host"] = .string(host) }
            return t
        case "http", "h2":
            var t: JSONValue = ["type": "http", "path": .string(nonEmpty(q["path"]) ?? "/")]
            if let host { t["host"] = .strings(splitList(host)) }
            return t
        case "xhttp", "splithttp":
            throw ShareLinkError.unsupported("XHTTP (нет в ядре sing-box)")
        case "kcp", "mkcp":
            throw ShareLinkError.unsupported("mKCP")
        default:
            throw ShareLinkError.unsupported("транспорт \(type)")
        }
    }

    static func validPort(_ port: Int) -> Bool { (1...65535).contains(port) }

    static func isTrue(_ value: String?) -> Bool {
        guard let value = value?.lowercased() else { return false }
        return value == "1" || value == "true"
    }

    static func nonEmpty(_ value: String?) -> String? {
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    static func splitList(_ value: String) -> [String] {
        value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    static func decodeName(_ raw: String) -> String? {
        let name = (raw.removingPercentEncoding ?? raw).trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? nil : name
    }

    public static func isIPAddress(_ host: String) -> Bool {
        if host.contains(":") { return true }
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy { UInt8($0) != nil }
    }
}

/// `scheme://userinfo@host:port/path?query#name`, parsed by hand: share links
/// carry port lists and raw passwords that `URLComponents` rejects.
struct LinkURI {
    var scheme: String
    var userinfo: String?
    var host: String
    var portSpec: String
    var query: [String: String]
    var fragment: String?

    init(_ line: String) throws {
        guard let schemeEnd = line.range(of: "://") else { throw ShareLinkError.invalid("нет схемы") }
        scheme = line[..<schemeEnd.lowerBound].lowercased()
        var rest = String(line[schemeEnd.upperBound...])
        if let hash = rest.firstIndex(of: "#") {
            fragment = ShareLink.decodeName(String(rest[rest.index(after: hash)...]))
            rest = String(rest[..<hash])
        }
        query = [:]
        if let q = rest.firstIndex(of: "?") {
            query = LinkURI.parseQuery(String(rest[rest.index(after: q)...]))
            rest = String(rest[..<q])
        }
        var authority = rest
        let at = authority.lastIndex(of: "@")
        let hostStart = at.map { authority.index(after: $0) } ?? authority.startIndex
        if let slash = authority[hostStart...].firstIndex(of: "/") {
            authority = String(authority[..<slash])
        }
        if let at {
            let raw = String(authority[..<at])
            userinfo = raw.removingPercentEncoding ?? raw
            authority = String(authority[authority.index(after: at)...])
        }
        (host, portSpec) = try LinkURI.splitHostPort(authority)
        guard !host.isEmpty else { throw ShareLinkError.invalid("нет адреса") }
    }

    var name: String { fragment ?? "\(host):\(portSpec)" }

    /// The part of userinfo before the first `:`.
    var user: String? {
        userinfo.map { String($0.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)[0]) }
    }

    var password: String? {
        guard let userinfo, let colon = userinfo.firstIndex(of: ":") else { return nil }
        return String(userinfo[userinfo.index(after: colon)...])
    }

    func singlePort() throws -> Int {
        guard let port = Int(portSpec), ShareLink.validPort(port) else {
            throw ShareLinkError.invalid(portSpec.isEmpty ? "нет порта" : "порт \(portSpec)")
        }
        return port
    }

    static func splitHostPort(_ s: String) throws -> (String, String) {
        if s.hasPrefix("[") {
            guard let close = s.firstIndex(of: "]") else { throw ShareLinkError.invalid("IPv6-адрес") }
            let host = String(s[s.index(after: s.startIndex)..<close])
            let after = s[s.index(after: close)...]
            return (host, after.hasPrefix(":") ? String(after.dropFirst()) : "")
        }
        if let colon = s.firstIndex(of: ":") {
            return (String(s[..<colon]), String(s[s.index(after: colon)...]))
        }
        return (s, "")
    }

    /// Keys are lowercased; values are percent-decoded. `+` stays `+`:
    /// passwords and keys in these links use it literally.
    static func parseQuery(_ s: String) -> [String: String] {
        var result: [String: String] = [:]
        for pair in s.split(separator: "&") {
            let kv = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            let key = (String(kv[0]).removingPercentEncoding ?? String(kv[0])).lowercased()
            let raw = kv.count > 1 ? String(kv[1]) : ""
            if result[key] == nil { result[key] = raw.removingPercentEncoding ?? raw }
        }
        return result
    }
}
