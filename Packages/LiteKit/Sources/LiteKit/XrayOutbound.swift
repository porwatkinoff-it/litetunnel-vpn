import Foundation

/// Servers only Xray-core reaches: XHTTP, mKCP, VLESS Encryption, TCP with an
/// HTTP header, finalmask. They run as LiteTunnel's own sing-box outbound,
/// `{"type": "xray", "outbound": <Xray outbound>}` (scripts/xray), so Xray
/// talks to the server and sing-box does everything else. Every other server
/// keeps sing-box's own outbound, the one the battery figures were measured on.
public enum XrayOutbound {
    static let type = "xray"

    /// The Xray protocols linked into the core, and what the list calls them.
    /// Hysteria is version 2 only, as in Xray itself.
    static let protocols: [String: ProxyProtocol] = [
        "vless": .vless, "vmess": .vmess, "trojan": .trojan, "shadowsocks": .shadowsocks,
        "socks": .socks, "http": .http, "hysteria": .hysteria2,
    ]

    /// The sing-box outbound for an Xray one. The tag is the config's own and
    /// would make the same server look like two.
    static func wrap(_ xray: JSONValue) -> JSONValue {
        var inner = xray
        inner["tag"] = nil
        return ["type": .string(type), "outbound": inner]
    }

    /// The Xray outbound inside, or nil for any other outbound.
    static func inner(of outbound: JSONValue) -> JSONValue? {
        outbound["type"]?.stringValue == type ? outbound["outbound"] : nil
    }

    /// The server an Xray outbound connects to: in vnext or servers, or flat
    /// in settings as newer Xray allows.
    static func endpoint(_ xray: JSONValue) -> (proto: ProxyProtocol, host: String, port: Int)? {
        guard let proto = protocols[(xray["protocol"]?.stringValue ?? "").lowercased()] else { return nil }
        let settings = xray["settings"] ?? [:]
        let endpoint = settings["vnext"]?[0] ?? settings["servers"]?[0] ?? settings
        guard let host = ShareLink.nonEmpty(endpoint["address"]?.stringValue),
              let port = endpoint["port"]?.intValue, ShareLink.validPort(port)
        else { return nil }
        return (proto, host, port)
    }

    /// An outbound from an Xray config, kept as it is.
    static func server(_ xray: JSONValue, name: String?) throws -> ParsedServer {
        let proto = (xray["protocol"]?.stringValue ?? "").lowercased()
        guard protocols[proto] != nil else { throw ShareLinkError.unsupported("Xray \(proto.isEmpty ? "без protocol" : proto)") }
        if proto == "hysteria", (xray["settings"]?["version"]?.intValue ?? 2) != 2 {
            throw ShareLinkError.unsupported("Hysteria 1")
        }
        guard let (kind, host, port) = endpoint(xray) else { throw ShareLinkError.invalid("нет адреса или порта") }
        try checkSecurity(xray, host: host)
        return ParsedServer(name: name ?? "\(host):\(port)", proto: kind, host: host, port: port, outbound: wrap(xray), shareLink: nil)
    }

    /// Whether an Xray config's outbound has to stay Xray even where sing-box
    /// has the protocol: finalmask changes every packet, and nothing in
    /// sing-box does the same.
    static func needsXray(_ xray: JSONValue) -> Bool {
        let stream = xray["streamSettings"] ?? [:]
        return stream["finalmask"] != nil || stream["finalMask"] != nil
    }

    // MARK: - Share links

    /// An Xray outbound from a share link that sing-box cannot run. `user` is
    /// the protocol's own part: id and encryption for VLESS, a password for
    /// Trojan.
    static func fromLink(protocol proto: String, host: String, port: Int, user: JSONValue,
                         network: String, security: String, query q: [String: String]) throws -> JSONValue {
        var server: JSONValue = ["address": .string(host), "port": .int(port)]
        let settings: JSONValue
        switch proto {
        case "vless", "vmess":
            server["users"] = [user]
            settings = ["vnext": [server]]
        case "trojan":
            for (key, value) in user.objectValue ?? [:] { server[key] = value }
            settings = ["servers": [server]]
        default:
            throw ShareLinkError.unsupported(proto)
        }
        let xray: JSONValue = [
            "protocol": .string(proto),
            "settings": settings,
            "streamSettings": try stream(network: network, security: security, query: q, host: host),
        ]
        try checkSecurity(xray, host: host)
        return xray
    }

    /// Xray refuses VLESS without TLS or VLESS Encryption, and Trojan without
    /// TLS, to a server outside the local network: the id or the password would
    /// go out in the clear. It also runs Reality over RAW, XHTTP and gRPC only.
    /// Such a server is skipped with that reason, not added to fail on connect.
    static func checkSecurity(_ xray: JSONValue, host: String) throws {
        let security = (xray["streamSettings"]?["security"]?.stringValue ?? "").lowercased()
        let network = (xray["streamSettings"]?["network"]?.stringValue ?? "").lowercased()
        if security == "reality", !realityNetworks.contains(network) {
            throw ShareLinkError.unsupported("Reality с транспортом \(network)")
        }
        guard security.isEmpty || security == "none", !isLocal(host) else { return }
        switch (xray["protocol"]?.stringValue ?? "").lowercased() {
        case "vless":
            let settings = xray["settings"] ?? [:]
            let user = settings["vnext"]?[0]?["users"]?[0] ?? settings
            let encryption = user["encryption"]?.stringValue ?? ""
            if encryption.isEmpty || encryption == "none" { throw ShareLinkError.unsupported("VLESS без TLS и шифрования") }
        case "trojan":
            throw ShareLinkError.unsupported("Trojan без TLS")
        default:
            break
        }
    }

    /// A private, loopback or link-local address, or a name Xray takes as local.
    static func isLocal(_ raw: String) -> Bool {
        var host = raw.lowercased()
        if host.hasSuffix(".") { host.removeLast() }
        if host.contains(":") {
            return host == "::1" || host.hasPrefix("fc") || host.hasPrefix("fd") || host.hasPrefix("fe8")
                || host.hasPrefix("fe9") || host.hasPrefix("fea") || host.hasPrefix("feb")
        }
        if ShareLink.isIPAddress(host) {
            let b = host.split(separator: ".").compactMap { Int($0) }
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && (16...31).contains(b[1]))
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
        }
        if !host.contains(".") { return true }
        return localSuffixes.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    private static let realityNetworks: Set<String> = ["", "raw", "tcp", "xhttp", "splithttp", "grpc"]

    private static let localSuffixes = ["lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal"]

    /// streamSettings from the share-link keys Xray's own links use.
    static func stream(network: String, security: String, query q: [String: String], host address: String) throws -> JSONValue {
        let host = ShareLink.nonEmpty(q["host"])
        let path = ShareLink.nonEmpty(q["path"])
        var s: JSONValue = [:]
        switch network.lowercased() {
        case "", "tcp", "raw":
            s["network"] = "raw"
            if q["headertype"]?.lowercased() == "http" {
                var request: JSONValue = ["path": .strings(ShareLink.splitList(path ?? "/"))]
                if let host { request["headers"] = ["Host": .strings(ShareLink.splitList(host))] }
                s["rawSettings"] = ["header": ["type": "http", "request": request]]
            }
        case "xhttp", "splithttp":
            // extra is the rest of xhttpSettings, as JSON.
            var x: JSONValue = [:]
            if let extra = q["extra"], let data = extra.data(using: .utf8),
               let json = try? JSONValue.parse(data), json.objectValue != nil {
                x = json
            }
            x["path"] = .string(path ?? "/")
            if let host { x["host"] = .string(host) }
            x["mode"] = .string(ShareLink.nonEmpty(q["mode"]) ?? "auto")
            s["network"] = "xhttp"
            s["xhttpSettings"] = x
        case "kcp", "mkcp":
            var k: JSONValue = [:]
            if let header = ShareLink.nonEmpty(q["headertype"]), header != "none" { k["header"] = ["type": .string(header)] }
            if let seed = ShareLink.nonEmpty(q["seed"]) { k["seed"] = .string(seed) }
            s["network"] = "kcp"
            s["kcpSettings"] = k
        case "ws", "websocket":
            var w: JSONValue = ["path": .string(path ?? "/")]
            if let host { w["host"] = .string(host) }
            s["network"] = "ws"
            s["wsSettings"] = w
        case "grpc", "gun":
            var g: JSONValue = ["serviceName": .string(ShareLink.nonEmpty(q["servicename"]) ?? "")]
            if q["mode"]?.lowercased() == "multi" { g["multiMode"] = true }
            if let authority = ShareLink.nonEmpty(q["authority"]) { g["authority"] = .string(authority) }
            s["network"] = "grpc"
            s["grpcSettings"] = g
        case "httpupgrade":
            var h: JSONValue = ["path": .string(path ?? "/")]
            if let host { h["host"] = .string(host) }
            s["network"] = "httpupgrade"
            s["httpupgradeSettings"] = h
        default:
            throw ShareLinkError.unsupported("транспорт \(network)")
        }

        let security = security.lowercased()
        let sni = ShareLink.nonEmpty(q["sni"]) ?? ShareLink.nonEmpty(q["peer"])
            ?? host.map { ShareLink.splitList($0)[0] } ?? (ShareLink.isIPAddress(address) ? nil : address)
        let fingerprint = ShareLink.nonEmpty(q["fp"])?.lowercased()
        switch security {
        case "", "none":
            break
        case "tls", "xtls":
            // allowInsecure is gone from Xray 26; the core drops it, and a pinned
            // certificate (pcs) or a name to check it by (vcn) stands in.
            var t: JSONValue = [:]
            if let sni { t["serverName"] = .string(sni) }
            if let fingerprint { t["fingerprint"] = .string(fingerprint) }
            if let alpn = ShareLink.nonEmpty(q["alpn"]) { t["alpn"] = .strings(ShareLink.splitList(alpn)) }
            if let pcs = ShareLink.nonEmpty(q["pcs"]) { t["pinnedPeerCertSha256"] = .string(pcs) }
            if let vcn = ShareLink.nonEmpty(q["vcn"]) { t["verifyPeerCertByName"] = .string(vcn) }
            if let ech = ShareLink.nonEmpty(q["ech"]) { t["echConfigList"] = .string(ech) }
            s["security"] = "tls"
            s["tlsSettings"] = t
        case "reality":
            guard let pbk = ShareLink.nonEmpty(q["pbk"]) else { throw ShareLinkError.invalid("Reality без pbk") }
            var r: JSONValue = ["publicKey": .string(pbk), "fingerprint": .string(fingerprint ?? "chrome")]
            if let sni { r["serverName"] = .string(sni) }
            if let sid = ShareLink.nonEmpty(q["sid"]) { r["shortId"] = .string(sid) }
            if let spx = ShareLink.nonEmpty(q["spx"]) { r["spiderX"] = .string(spx) }
            if let pqv = ShareLink.nonEmpty(q["pqv"]) { r["mldsa65Verify"] = .string(pqv) }
            s["security"] = "reality"
            s["realitySettings"] = r
        default:
            throw ShareLinkError.unsupported("security=\(security)")
        }
        return s
    }

    // MARK: - The list

    /// Security, transport and what needs Xray, e.g. "Reality · XHTTP · Xray".
    /// What a ping times for an Xray server. Hysteria and XHTTP over HTTP/3
    /// answer a QUIC handshake, unless finalmask hides it; mKCP answers nothing
    /// without the seed; everything else runs over TCP.
    static func handshake(_ xray: JSONValue, host: String) -> Ping.Handshake? {
        let stream = xray["streamSettings"] ?? [:]
        let tls = stream["tlsSettings"]
        let alpn = tls?["alpn"]?.arrayValue?.compactMap(\.stringValue) ?? []
        let quic = Ping.Handshake.quic(sni: tls?["serverName"]?.stringValue ?? host, alpn: alpn.isEmpty ? ["h3"] : alpn)
        switch (stream["network"]?.stringValue ?? "raw").lowercased() {
        case "kcp", "mkcp":
            return nil
        case "hysteria":
            return needsXray(xray) ? nil : quic
        case "xhttp", "splithttp":
            guard stream["security"]?.stringValue == "tls", alpn == ["h3"] else { return .tcp }
            return needsXray(xray) ? nil : quic
        default:
            return .tcp
        }
    }

    static func detail(_ xray: JSONValue) -> String {
        let stream = xray["streamSettings"] ?? [:]
        let user = xray["settings"]?["vnext"]?[0]?["users"]?[0] ?? xray["settings"] ?? [:]
        var parts: [String] = []
        switch stream["security"]?.stringValue?.lowercased() {
        case "tls": parts.append("TLS")
        case "reality": parts.append("Reality")
        default: break
        }
        switch (stream["network"]?.stringValue ?? "raw").lowercased() {
        case "xhttp", "splithttp": parts.append("XHTTP")
        case "kcp", "mkcp": parts.append("mKCP")
        case "raw", "tcp":
            let header = stream["rawSettings"]?["header"] ?? stream["tcpSettings"]?["header"]
            if header?["type"]?.stringValue == "http" { parts.append("HTTP") }
        case "hysteria": break
        case let other: parts.append(other.uppercased())
        }
        if user["flow"]?.stringValue?.hasPrefix("xtls-rprx-vision") == true { parts.append("Vision") }
        if let encryption = user["encryption"]?.stringValue, !encryption.isEmpty, encryption != "none" {
            parts.append("Encryption")
        }
        if needsXray(xray) { parts.append("finalmask") }
        parts.append("Xray")
        return parts.joined(separator: " · ")
    }
}

extension ParsedServer {
    /// Protocol, host and port of a sing-box outbound, an Xray one included.
    static func endpoint(of outbound: JSONValue) -> (proto: ProxyProtocol, host: String, port: Int)? {
        if let xray = XrayOutbound.inner(of: outbound) { return XrayOutbound.endpoint(xray) }
        guard let proto = outbound["type"]?.stringValue.flatMap(ProxyProtocol.init(rawValue:)),
              let host = outbound["server"]?.stringValue, let port = outbound["server_port"]?.intValue
        else { return nil }
        return (proto, host, port)
    }
}
