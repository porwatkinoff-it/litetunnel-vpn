import Foundation

/// Builds the sing-box config the tunnel runs.
///
/// Power rules this file enforces: no selector groups, a urltest group only
/// for a subscription's "Авто-выбор" entry and then tested every 10 minutes,
/// no remote rule-sets (they re-download on a timer), no clash API, log level
/// from settings (warn by default).
public enum ConfigBuilder {
    public static let proxyTag = "proxy"
    public static let directTag = "direct"
    static let remoteDNSTag = "dns-remote"
    static let directDNSTag = "dns-direct"
    static let bootstrapDNSTag = "dns-bootstrap"
    static let russianSuffixes = ["ru", "su", "xn--p1ai"]

    /// - Parameter ruleSetDirectory: folder with downloaded `<tag>.srs` files.
    ///   Rule-sets that are missing there are left out of the config.
    public static func build(server: Server, settings: AppSettings, ruleSetDirectory: URL?) -> JSONValue {
        let available = ruleSetDirectory.map { RuleSets.available(in: $0) } ?? []

        // A group runs its members behind a urltest outbound with the proxy tag.
        var proxies = ServerGroup.outbounds(server.outbound, tag: proxyTag)
        if settings.tlsFragment { fragmentTLS(&proxies) }

        var usedSets: [String] = []
        func use(_ tags: [String]) -> [String] {
            let present = tags.filter(available.contains)
            for tag in present where !usedSets.contains(tag) { usedSets.append(tag) }
            return present
        }

        // DNS: remote resolver through the proxy; the direct one only for
        // domains that go direct anyway and for the proxy server's own name.
        var dnsRules: [JSONValue] = []
        if settings.routing == .russiaDirect {
            dnsRules.append(["domain_suffix": .strings(russianSuffixes), "server": .string(directDNSTag)])
            let sets = use([RuleSets.geositeRU])
            if !sets.isEmpty { dnsRules.append(["rule_set": .strings(sets), "server": .string(directDNSTag)]) }
        }
        if !settings.directDomains.isEmpty {
            dnsRules.append(["domain_suffix": .strings(settings.directDomains), "server": .string(directDNSTag)])
        }
        var dnsServers = [
            dnsServer(settings.remoteDNS, tag: remoteDNSTag, detour: proxyTag, resolver: directDNSTag),
            dnsServer(settings.directDNS, tag: directDNSTag, detour: nil, resolver: bootstrapDNSTag),
        ]
        if !ShareLink.isIPAddress(settings.directDNS.address) {
            dnsServers.append(dnsServer(.yandex, tag: bootstrapDNSTag, detour: nil, resolver: nil))
        }
        let dns: JSONValue = [
            "servers": .array(dnsServers),
            "rules": .array(dnsRules),
            "final": .string(remoteDNSTag),
            "strategy": settings.ipv6 ? "prefer_ipv4" : "ipv4_only",
        ]

        var addresses = ["172.19.0.1/30"]
        if settings.ipv6 { addresses.append("fdfe:dcba:9876::1/126") }
        var tun: JSONValue = [
            "type": "tun",
            "tag": "tun-in",
            "address": .strings(addresses),
            "auto_route": true,
            "strict_route": true,
            "stack": .string(settings.tunStack.rawValue),
        ]
        if settings.mtu > 0 { tun["mtu"] = .int(settings.mtu) }

        var rules: [JSONValue] = []
        if settings.sniffing { rules.append(["action": "sniff"]) }
        rules.append(["protocol": "dns", "action": "hijack-dns"])
        rules.append(["port": 53, "action": "hijack-dns"])
        rules.append(["ip_is_private": true, "outbound": .string(directTag)])
        if !settings.blockDomains.isEmpty {
            rules.append(["domain_suffix": .strings(settings.blockDomains), "action": "reject"])
        }
        if settings.blockAds {
            let sets = use([RuleSets.ads])
            if !sets.isEmpty { rules.append(["rule_set": .strings(sets), "action": "reject"]) }
        }
        if !settings.proxyDomains.isEmpty {
            rules.append(["domain_suffix": .strings(settings.proxyDomains), "outbound": .string(proxyTag)])
        }
        if !settings.directDomains.isEmpty {
            rules.append(["domain_suffix": .strings(settings.directDomains), "outbound": .string(directTag)])
        }
        if settings.routing == .russiaDirect {
            rules.append(["domain_suffix": .strings(russianSuffixes), "outbound": .string(directTag)])
            let sets = use([RuleSets.geositeRU, RuleSets.geoipRU])
            if !sets.isEmpty { rules.append(["rule_set": .strings(sets), "outbound": .string(directTag)]) }
        }

        var route: JSONValue = [
            "rules": .array(rules),
            "final": .string(proxyTag),
            "auto_detect_interface": true,
            "default_domain_resolver": .string(directDNSTag),
        ]
        if let ruleSetDirectory, !usedSets.isEmpty {
            route["rule_set"] = .array(usedSets.map { tag in
                [
                    "type": "local",
                    "tag": .string(tag),
                    "format": "binary",
                    "path": .string(ruleSetDirectory.appendingPathComponent("\(tag).srs").path),
                ]
            })
        }

        return [
            "log": ["level": .string(settings.logLevel.rawValue), "timestamp": true],
            "dns": dns,
            "inbounds": [tun],
            "outbounds": .array(proxies + [["type": "direct", "tag": .string(directTag)]]),
            "route": route,
        ]
    }

    public static let localProxyTag = "mixed-in"

    /// The same config with a local SOCKS and HTTP proxy in place of the tun.
    /// Mac builds without the VPN entitlement run it in the app.
    public static func withLocalProxy(_ config: JSONValue, port: Int) -> JSONValue {
        var config = config
        config["inbounds"] = [[
            "type": "mixed",
            "tag": .string(localProxyTag),
            "listen": "127.0.0.1",
            "listen_port": .int(port),
        ]]
        return config
    }

    /// A config for pinging servers through themselves: a local proxy on
    /// `ports[i]` for each of `servers`, routed to that server alone. No tun,
    /// no rule-sets, errors-only log. Groups are left out: ping their members.
    public static func probe(_ servers: [Server], ports: [Int], settings: AppSettings) -> JSONValue {
        var inbounds: [JSONValue] = []
        var proxies: [JSONValue] = []
        var rules: [JSONValue] = []
        for (index, (server, port)) in zip(servers, ports).enumerated() where server.members.isEmpty {
            let inTag = "in-\(index)", outTag = "probe-\(index)"
            inbounds.append(["type": "mixed", "tag": .string(inTag), "listen": "127.0.0.1", "listen_port": .int(port)])
            var outbound = server.outbound
            outbound["tag"] = .string(outTag)
            proxies.append(outbound)
            rules.append(["inbound": .strings([inTag]), "outbound": .string(outTag)])
        }
        if settings.tlsFragment { fragmentTLS(&proxies) }

        var dnsServers = [dnsServer(settings.directDNS, tag: directDNSTag, detour: nil, resolver: bootstrapDNSTag)]
        if !ShareLink.isIPAddress(settings.directDNS.address) {
            dnsServers.append(dnsServer(.yandex, tag: bootstrapDNSTag, detour: nil, resolver: nil))
        }
        return [
            "log": ["level": "error"],
            "dns": [
                "servers": .array(dnsServers),
                "final": .string(directDNSTag),
                "strategy": settings.ipv6 ? "prefer_ipv4" : "ipv4_only",
            ],
            "inbounds": .array(inbounds),
            "outbounds": .array(proxies + [["type": "direct", "tag": .string(directTag)]]),
            "route": [
                "rules": .array(rules),
                "final": .string(directTag),
                "auto_detect_interface": true,
                "default_domain_resolver": .string(directDNSTag),
            ],
            // The tunnel holds a lock on the default cache file; a second
            // core waits on it and fails to start.
            "experimental": ["cache_file": ["enabled": true, "path": "probe.db"]],
        ]
    }

    /// Splits the TLS ClientHello of outbounds that run TLS over TCP. Reality
    /// and QUIC protocols cannot take it.
    static func fragmentTLS(_ outbounds: inout [JSONValue]) {
        for i in outbounds.indices where outbounds[i]["tls"]?["enabled"]?.boolValue == true && outbounds[i]["tls"]?["reality"] == nil {
            let type = outbounds[i]["type"]?.stringValue
            if type != ProxyProtocol.hysteria2.rawValue && type != ProxyProtocol.tuic.rawValue {
                outbounds[i]["tls"]?["fragment"] = true
            }
        }
    }

    static func dnsServer(_ server: DNSServer, tag: String, detour: String?, resolver: String?) -> JSONValue {
        var json: JSONValue = ["type": .string(server.kind.rawValue), "tag": .string(tag), "server": .string(server.address)]
        if let detour { json["detour"] = .string(detour) }
        if let resolver, !ShareLink.isIPAddress(server.address) {
            // A DNS server given by name needs another server to find its address.
            json["domain_resolver"] = .string(resolver)
        }
        return json
    }
}

public enum RuleSets {
    public static let geoipRU = "geoip-ru"
    public static let geositeRU = "geosite-category-ru"
    public static let ads = "geosite-category-ads-all"

    /// Primary source first, then a CDN mirror.
    public static func urls(for tag: String) -> [URL] {
        let repo = tag.hasPrefix("geoip-") ? "sing-geoip" : "sing-geosite"
        return [
            URL(string: "https://raw.githubusercontent.com/SagerNet/\(repo)/rule-set/\(tag).srs")!,
            URL(string: "https://cdn.jsdelivr.net/gh/SagerNet/\(repo)@rule-set/\(tag).srs")!,
        ]
    }

    public static func needed(for settings: AppSettings) -> [String] {
        var tags: [String] = []
        if settings.routing == .russiaDirect { tags += [geositeRU, geoipRU] }
        if settings.blockAds { tags.append(ads) }
        return tags
    }

    public static func available(in directory: URL) -> Set<String> {
        let files = (try? FileManager.default.contentsOfDirectory(atPath: directory.path)) ?? []
        return Set(files.filter { $0.hasSuffix(".srs") }.map { String($0.dropLast(4)) })
    }

    /// Binary rule-sets start with "SRS".
    static func looksValid(_ data: Data) -> Bool {
        data.count > 3 && data.prefix(3) == Data("SRS".utf8)
    }
}
