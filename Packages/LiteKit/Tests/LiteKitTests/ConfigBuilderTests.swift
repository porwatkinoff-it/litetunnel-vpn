import XCTest
@testable import LiteKit

final class ConfigBuilderTests: XCTestCase {
    var ruleDir: URL!

    override func setUpWithError() throws {
        ruleDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: ruleDir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: ruleDir)
    }

    func server(_ link: String) throws -> Server {
        Server(subscriptionID: nil, parsed: try ShareLink.parse(link))
    }

    func testGlobalModeIsMinimal() throws {
        var settings = AppSettings()
        settings.routing = .global
        let config = ConfigBuilder.build(server: try server("hy2://pw@h.example.com:443#H"), settings: settings, ruleSetDirectory: ruleDir)

        XCTAssertEqual(config["log"]?["level"]?.stringValue, "warn")
        XCTAssertNil(config["experimental"], "no clash API or cache file in the tunnel")
        XCTAssertNil(config["route"]?["rule_set"])
        XCTAssertEqual(config["route"]?["final"]?.stringValue, "proxy")
        XCTAssertEqual(config["route"]?["auto_detect_interface"]?.boolValue, true)

        let outbounds = try XCTUnwrap(config["outbounds"]?.arrayValue)
        XCTAssertEqual(outbounds.compactMap { $0["tag"]?.stringValue }, ["proxy", "direct"])
        XCTAssertFalse(outbounds.contains { ["urltest", "selector"].contains($0["type"]?.stringValue) })

        let tun = try XCTUnwrap(config["inbounds"]?[0])
        XCTAssertEqual(tun["type"]?.stringValue, "tun")
        XCTAssertEqual(tun["address"], ["172.19.0.1/30"])
        XCTAssertEqual(tun["stack"]?.stringValue, "mixed")
        XCTAssertNil(tun["mtu"], "the core picks 4064 inside a Network Extension")

        let dns = try XCTUnwrap(config["dns"])
        XCTAssertEqual(dns["final"]?.stringValue, "dns-remote")
        XCTAssertEqual(dns["strategy"]?.stringValue, "ipv4_only")
        XCTAssertEqual(dns["servers"]?[0]?["detour"]?.stringValue, "proxy")
        XCTAssertEqual(dns["servers"]?[0]?["type"]?.stringValue, "https")
        XCTAssertEqual(dns["rules"]?.arrayValue?.count, 0)
    }

    func testRussiaDirectUsesOnlyDownloadedRuleSets() throws {
        try Data("SRS\u{1}".utf8).write(to: ruleDir.appendingPathComponent("geoip-ru.srs"))
        var settings = AppSettings()
        settings.blockAds = true  // ads list is not downloaded: rule must be left out
        let config = ConfigBuilder.build(server: try server("vless://id@v.example.com:443?security=tls#V"), settings: settings, ruleSetDirectory: ruleDir)

        let sets = try XCTUnwrap(config["route"]?["rule_set"]?.arrayValue)
        XCTAssertEqual(sets.count, 1)
        XCTAssertEqual(sets[0]["tag"]?.stringValue, "geoip-ru")
        XCTAssertEqual(sets[0]["type"]?.stringValue, "local")
        XCTAssertEqual(sets[0]["path"]?.stringValue, ruleDir.appendingPathComponent("geoip-ru.srs").path)
        XCTAssertTrue(sets[0]["path"]?.stringValue?.hasPrefix("/") == true, "libbox does not chdir; paths must be absolute")

        let rules = try XCTUnwrap(config["route"]?["rules"]?.arrayValue)
        XCTAssertFalse(rules.contains { $0["action"]?.stringValue == "reject" })
        XCTAssertTrue(rules.contains { $0["rule_set"] == ["geoip-ru"] && $0["outbound"]?.stringValue == "direct" })
        XCTAssertTrue(rules.contains { $0["domain_suffix"] == ["ru", "su", "xn--p1ai"] })

        // DNS rules may only use domain rule-sets; the geoip one must stay out.
        let dnsRules = try XCTUnwrap(config["dns"]?["rules"]?.arrayValue)
        XCTAssertFalse(dnsRules.contains { $0["rule_set"] != nil })
        XCTAssertEqual(dnsRules.first?["server"]?.stringValue, "dns-direct")
    }

    func testRuleOrderAndCustomDomains() throws {
        for tag in [RuleSets.ads, RuleSets.geositeRU, RuleSets.geoipRU] {
            try Data("SRS\u{1}".utf8).write(to: ruleDir.appendingPathComponent("\(tag).srs"))
        }
        var settings = AppSettings()
        settings.blockAds = true
        settings.blockDomains = ["tracker.example"]
        settings.proxyDomains = ["youtube.com"]
        settings.directDomains = ["bank.example"]
        let config = ConfigBuilder.build(server: try server("hy2://pw@h.example.com:443#H"), settings: settings, ruleSetDirectory: ruleDir)
        let rules = try XCTUnwrap(config["route"]?["rules"]?.arrayValue)
        func index(_ match: (JSONValue) -> Bool) -> Int { rules.firstIndex(where: match) ?? -1 }

        let sniff = index { $0["action"]?.stringValue == "sniff" }
        let dns = index { $0["action"]?.stringValue == "hijack-dns" }
        let block = index { $0["domain_suffix"] == ["tracker.example"] }
        let ads = index { $0["rule_set"] == [JSONValue.string(RuleSets.ads)] }
        let proxy = index { $0["domain_suffix"] == ["youtube.com"] }
        let direct = index { $0["domain_suffix"] == ["bank.example"] }
        let russia = index { $0["rule_set"] == [JSONValue.string(RuleSets.geositeRU), JSONValue.string(RuleSets.geoipRU)] }
        XCTAssertEqual([sniff, dns, block, ads, proxy, direct, russia], [sniff, dns, block, ads, proxy, direct, russia].sorted())
        XCTAssertFalse([sniff, dns, block, ads, proxy, direct, russia].contains(-1))
        XCTAssertEqual(config["route"]?["rule_set"]?.arrayValue?.count, 3)

        let dnsRules = try XCTUnwrap(config["dns"]?["rules"]?.arrayValue)
        XCTAssertTrue(dnsRules.contains { $0["rule_set"] == [JSONValue.string(RuleSets.geositeRU)] })
        XCTAssertTrue(dnsRules.contains { $0["domain_suffix"] == ["bank.example"] })
    }

    func testTLSFragmentSkipsRealityAndQUIC() throws {
        var settings = AppSettings()
        settings.tlsFragment = true
        let tls = ConfigBuilder.build(server: try server("trojan://pw@t.example.com:443#T"), settings: settings, ruleSetDirectory: nil)
        XCTAssertEqual(tls["outbounds"]?[0]?["tls"]?["fragment"]?.boolValue, true)
        let reality = ConfigBuilder.build(server: try server("vless://id@v.example.com:443?security=reality&pbk=k#R"), settings: settings, ruleSetDirectory: nil)
        XCTAssertNil(reality["outbounds"]?[0]?["tls"]?["fragment"])
        let hy2 = ConfigBuilder.build(server: try server("hy2://pw@h.example.com:443#H"), settings: settings, ruleSetDirectory: nil)
        XCTAssertNil(hy2["outbounds"]?[0]?["tls"]?["fragment"])
    }

    func testDNSByNameGetsResolvers() throws {
        var settings = AppSettings()
        settings.remoteDNS = DNSServer(kind: .https, address: "dns.google")
        settings.directDNS = DNSServer(kind: .tls, address: "common.dot.dns.yandex.net")
        settings.ipv6 = true
        let config = ConfigBuilder.build(server: try server("hy2://pw@h.example.com:443#H"), settings: settings, ruleSetDirectory: nil)
        let servers = try XCTUnwrap(config["dns"]?["servers"]?.arrayValue)
        XCTAssertEqual(servers.map { $0["tag"]?.stringValue }, ["dns-remote", "dns-direct", "dns-bootstrap"])
        XCTAssertEqual(servers[0]["domain_resolver"]?.stringValue, "dns-direct")
        XCTAssertEqual(servers[1]["domain_resolver"]?.stringValue, "dns-bootstrap")
        XCTAssertNil(servers[2]["domain_resolver"])
        XCTAssertEqual(config["dns"]?["strategy"]?.stringValue, "prefer_ipv4")
        XCTAssertEqual(config["inbounds"]?[0]?["address"]?.arrayValue?.count, 2)
    }

    func testOutputIsValidJSON() throws {
        let config = ConfigBuilder.build(server: try server("vless://id@v.example.com:443?security=reality&pbk=k&sni=a.com#V"),
                                         settings: AppSettings(), ruleSetDirectory: ruleDir)
        let data = try config.encoded(pretty: true)
        XCTAssertNotNil(try JSONSerialization.jsonObject(with: data))
        XCTAssertEqual(try JSONValue.parse(data), config)
    }

    func testLocalProxyReplacesOnlyTheTun() throws {
        let config = ConfigBuilder.build(server: try server("hy2://pw@h.example.com:443#H"), settings: AppSettings(), ruleSetDirectory: ruleDir)
        let local = ConfigBuilder.withLocalProxy(config, port: 51080)

        let inbounds = try XCTUnwrap(local["inbounds"]?.arrayValue)
        XCTAssertEqual(inbounds.count, 1)
        XCTAssertEqual(inbounds[0]["type"]?.stringValue, "mixed")
        XCTAssertEqual(inbounds[0]["listen"]?.stringValue, "127.0.0.1", "never reachable from the network")
        XCTAssertEqual(inbounds[0]["listen_port"]?.intValue, 51080)

        for key in ["log", "dns", "outbounds", "route"] {
            XCTAssertEqual(local[key], config[key], key)
        }
    }

    func testProbeRoutesEachPortToItsServer() throws {
        var settings = AppSettings()
        settings.tlsFragment = true
        let servers = [
            try server("vless://11111111-1111-1111-1111-111111111111@a.example.com:443?security=tls&sni=a.example.com#A"),
            try server("hy2://pw@h.example.com:443#H"),
        ]
        let config = ConfigBuilder.probe(servers, ports: [20001, 20002], settings: settings)

        XCTAssertEqual(config["log"]?["level"]?.stringValue, "error")
        XCTAssertEqual(config["experimental"]?["cache_file"]?["path"]?.stringValue, "probe.db",
                       "the tunnel locks the default cache file")
        XCTAssertNil(config["route"]?["rule_set"])
        XCTAssertEqual(config["route"]?["auto_detect_interface"]?.boolValue, true)

        let inbounds = try XCTUnwrap(config["inbounds"]?.arrayValue)
        XCTAssertEqual(inbounds.compactMap { $0["listen_port"]?.intValue }, [20001, 20002])
        XCTAssertEqual(inbounds.compactMap { $0["type"]?.stringValue }, ["mixed", "mixed"])
        XCTAssertEqual(inbounds.compactMap { $0["listen"]?.stringValue }, ["127.0.0.1", "127.0.0.1"])

        let rules = try XCTUnwrap(config["route"]?["rules"]?.arrayValue)
        XCTAssertEqual(rules, [
            ["inbound": ["in-0"], "outbound": "probe-0"],
            ["inbound": ["in-1"], "outbound": "probe-1"],
        ])
        let outbounds = try XCTUnwrap(config["outbounds"]?.arrayValue)
        XCTAssertEqual(outbounds.compactMap { $0["tag"]?.stringValue }, ["probe-0", "probe-1", "direct"])
        XCTAssertEqual(outbounds[0]["server"]?.stringValue, "a.example.com")
        XCTAssertEqual(outbounds[0]["tls"]?["fragment"]?.boolValue, true)
        XCTAssertNil(outbounds[1]["tls"]?["fragment"], "QUIC cannot fragment")

        // Only the direct resolver: nothing goes through a remote one.
        let dns = try XCTUnwrap(config["dns"]?["servers"]?.arrayValue)
        XCTAssertEqual(dns.compactMap { $0["tag"]?.stringValue }, ["dns-direct"])
        XCTAssertNoThrow(try JSONValue.parse(Data(config.jsonString().utf8)))
    }

    func testProbeSkipsGroups() throws {
        let members = [
            try ShareLink.parse("hy2://pw@a.example.com:443#A"),
            try ShareLink.parse("hy2://pw@b.example.com:443#B"),
        ]
        let group = Server(subscriptionID: nil, parsed: ServerGroup.make(members, name: "Авто"))
        let config = ConfigBuilder.probe([group] + group.members, ports: [1, 2, 3], settings: AppSettings())
        XCTAssertEqual(config["inbounds"]?.arrayValue?.compactMap { $0["listen_port"]?.intValue }, [2, 3])
    }
}

final class PingTests: XCTestCase {
    func testRealModeReadsLikeAPing() {
        XCTAssertEqual(Ping.result(330, mode: .real), .ms(100))
        XCTAssertEqual(Ping.result(1, mode: .real), .ms(1))
        XCTAssertEqual(Ping.result(330, mode: .proxyGet), .ms(330))
        XCTAssertEqual(Ping.result(nil, mode: .real), .failed)
    }

    func testFastestPrefersTimeOverFailure() {
        XCTAssertEqual(Ping.fastest([.failed, .ms(80), nil, .ms(40)]), .ms(40))
        XCTAssertEqual(Ping.fastest([nil, .failed]), .failed)
        XCTAssertNil(Ping.fastest([nil]))
    }

    func testChecksum() {
        // Echo request, id 0x1234, sequence 1.
        let packet: [UInt8] = [8, 0, 0, 0, 0x12, 0x34, 0, 1]
        XCTAssertEqual(Ping.checksum(packet), 0xe5ca)
    }

    func testOldSettingsGetPingDefaults() throws {
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"pingOnOpen": true}"#.utf8))
        XCTAssertTrue(settings.pingOnOpen)
        XCTAssertEqual(settings.pingMode, .real)
        XCTAssertEqual(settings.pingURL, AppSettings.defaultPingURL)
        XCTAssertEqual(settings.pingDisplay, .time)
        XCTAssertFalse(settings.pingOnUpdate)
    }

    func testICMPReachesLoopback() async {
        let result = await Ping.icmp("127.0.0.1", interface: 0, timeout: 2)
        guard case .ms = result else { return XCTFail("\(result)") }
    }
}
