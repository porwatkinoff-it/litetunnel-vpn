import XCTest
@testable import LiteKit

final class SubscriptionTests: XCTestCase {
    static let lines = [
        "vless://id-1@nl.example.com:443?security=reality&pbk=key&sni=www.microsoft.com&flow=xtls-rprx-vision#NL",
        "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
        "vless://id-2@fi.example.com:443?security=reality&pbk=key&type=xhttp#FI xhttp",
        "happ://routing/add/eyJOYW1lIjoiUlUifQ==",
        "# comment",
    ]

    func testBase64BodyWithHeaders() throws {
        let body = Data(Self.lines.joined(separator: "\n").utf8).base64EncodedData()
        let headers = [
            "Profile-Title": "base64:" + Data("Мой VPN".utf8).base64EncodedString(),
            "Profile-Update-Interval": "12",
            "Subscription-Userinfo": "upload=100; download=900; total=10000; expire=1893456000",
            "Support-Url": "https://t.me/support",
            "Announce": "%D0%9F%D1%80%D0%B8%D0%B2%D0%B5%D1%82",
        ]
        let result = try SubscriptionParser.parse(body: body, headers: headers)
        XCTAssertEqual(result.servers.map(\.name), ["NL", "DE"])
        XCTAssertEqual(result.skipped.count, 1)
        XCTAssertTrue(result.skipped[0].reason.contains("XHTTP"))
        XCTAssertEqual(result.routingLines.count, 1)
        XCTAssertEqual(result.meta.title, "Мой VPN")
        XCTAssertEqual(result.meta.updateIntervalHours, 12)
        XCTAssertEqual(result.meta.usage?.used, 1000)
        XCTAssertEqual(result.meta.usage?.fraction, 0.1)
        XCTAssertEqual(result.meta.usage?.expire, Date(timeIntervalSince1970: 1893456000))
        XCTAssertEqual(result.meta.supportURL?.absoluteString, "https://t.me/support")
        XCTAssertEqual(result.meta.announce, "Привет")
    }

    func testPlainBodyAndURLSafeBase64() throws {
        let plain = try SubscriptionParser.parse(body: Data(Self.lines.joined(separator: "\r\n").utf8), headers: [:])
        XCTAssertEqual(plain.servers.count, 2)
        let urlSafe = Data(Self.lines[1].utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        XCTAssertEqual(try SubscriptionParser.parse(body: Data(urlSafe.utf8), headers: [:]).servers.first?.proto, .hysteria2)
    }

    func testSingBoxJSONBody() throws {
        let json = #"""
        {"outbounds":[
          {"type":"hysteria2","tag":"JP","server":"jp.example.com","server_port":443,"password":"x","detour":"other"},
          {"type":"direct","tag":"direct"},
          {"type":"selector","tag":"auto","outbounds":["JP"]}
        ]}
        """#
        let result = try SubscriptionParser.parse(body: Data(json.utf8), headers: [:])
        XCTAssertEqual(result.servers.count, 1)
        XCTAssertEqual(result.servers[0].name, "JP")
        XCTAssertNil(result.servers[0].outbound["tag"])
        XCTAssertNil(result.servers[0].outbound["detour"])
    }

    func testXrayJSONBody() throws {
        // As Remnawave serves it to Happ: one full Xray config per server.
        let json = #"""
        [
          {"remarks":"🇳🇱 NL","dns":{},"routing":{},"inbounds":[{"protocol":"socks","port":10808}],"outbounds":[
            {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"nl.example.com","port":443,
              "users":[{"id":"id-1","encryption":"none","flow":"xtls-rprx-vision"}]}]},
             "streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"www.microsoft.com",
              "fingerprint":"chrome","publicKey":"key","shortId":"ab","spiderX":""}}},
            {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"}]},
          {"remarks":"DE ws","outbounds":[
            {"tag":"proxy","protocol":"vless","settings":{"address":"de.example.com","port":8443,"id":"id-2","encryption":"none"},
             "streamSettings":{"network":"ws","security":"tls","tlsSettings":{"serverName":"cdn.example.com","alpn":["h2","http/1.1"],
              "allowInsecure":true},"wsSettings":{"path":"/ws?ed=2048","headers":{"Host":"cdn.example.com"}}}}]},
          {"remarks":"FI","outbounds":[
            {"tag":"proxy","protocol":"trojan","settings":{"servers":[{"address":"fi.example.com","port":443,"password":"pw"}]},
             "streamSettings":{"network":"grpc","security":"tls","grpcSettings":{"serviceName":"svc"}}},
            {"tag":"proxy-2","protocol":"shadowsocks","settings":{"servers":[{"address":"1.2.3.4","port":8388,
              "method":"2022-BLAKE3-AES-128-GCM","password":"AAAAAAAAAAAAAAAAAAAAAA=="}]}}]},
          {"remarks":"JP hy2","outbounds":[
            {"tag":"proxy","protocol":"hysteria","settings":{"version":2,"address":"jp.example.com","port":443},
             "streamSettings":{"network":"hysteria","security":"tls","tlsSettings":{"serverName":"jp.example.com","alpn":["h3"],
              "fingerprint":"chrome"},"hysteriaSettings":{"version":2,"auth":"secret"}}}]},
          {"remarks":"US xhttp","outbounds":[
            {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"us.example.com","port":443,"users":[{"id":"id-3"}]}]},
             "streamSettings":{"network":"xhttp","security":"reality","realitySettings":{"publicKey":"key"}}}]}
        ]
        """#
        let result = try SubscriptionParser.parse(body: Data(json.utf8), headers: [:])
        XCTAssertEqual(result.servers.map(\.name), ["🇳🇱 NL", "DE ws", "FI · proxy", "FI · proxy-2", "JP hy2"])
        XCTAssertEqual(result.servers.map(\.proto), [.vless, .vless, .trojan, .shadowsocks, .hysteria2])
        XCTAssertEqual(result.skipped.map(\.line), ["US xhttp"])
        XCTAssertTrue(result.skipped[0].reason.contains("XHTTP"))

        let nl = result.servers[0].outbound
        XCTAssertEqual(nl["uuid"], "id-1")
        XCTAssertEqual(nl["flow"], "xtls-rprx-vision")
        XCTAssertEqual(nl["tls"]?["server_name"], "www.microsoft.com")
        XCTAssertEqual(nl["tls"]?["reality"]?["public_key"], "key")
        XCTAssertEqual(nl["tls"]?["reality"]?["short_id"], "ab")
        XCTAssertNil(nl["transport"])

        let de = result.servers[1].outbound
        XCTAssertEqual(de["server_port"], 8443)
        XCTAssertEqual(de["tls"]?["insecure"], true)
        XCTAssertEqual(de["tls"]?["alpn"], .strings(["h2", "http/1.1"]))
        XCTAssertEqual(de["transport"]?["path"], "/ws")
        XCTAssertEqual(de["transport"]?["max_early_data"], 2048)
        XCTAssertEqual(de["transport"]?["headers"]?["Host"], "cdn.example.com")

        XCTAssertEqual(result.servers[2].outbound["transport"]?["service_name"], "svc")
        XCTAssertEqual(result.servers[2].outbound["tls"]?["server_name"], "fi.example.com")
        XCTAssertEqual(result.servers[3].outbound["method"], "2022-blake3-aes-128-gcm")

        let jp = result.servers[4].outbound
        XCTAssertEqual(jp["password"], "secret")
        XCTAssertEqual(jp["tls"]?["alpn"], .strings(["h3"]))
        XCTAssertNil(jp["tls"]?["utls"])
    }

    func testXrayJSONSingleConfig() throws {
        let json = #"""
        {"outbounds":[{"tag":"proxy","protocol":"vmess","settings":{"vnext":[{"address":"vm.example.com","port":443,
          "users":[{"id":"id","alterId":0,"security":"auto"}]}]},"streamSettings":{"network":"tcp"}},
          {"protocol":"freedom","tag":"direct"}]}
        """#
        let result = try SubscriptionParser.parse(body: Data(json.utf8), headers: [:])
        XCTAssertEqual(result.servers.map(\.name), ["proxy"])
        XCTAssertEqual(result.servers[0].outbound["uuid"], "id")
        XCTAssertNil(result.servers[0].outbound["tls"])
        XCTAssertThrowsError(try SubscriptionParser.parse(body: Data(#"{"foo":1}"#.utf8), headers: [:])) { error in
            XCTAssertEqual(error as? SubscriptionError, .nothingSupported("Неизвестный формат JSON"))
        }
    }

    func testXrayJSONBalancer() throws {
        let reality = #""security":"reality","realitySettings":{"serverName":"sni.example.com","publicKey":"pbk","shortId":"ab","fingerprint":"chrome"}"#
        let json = #"""
        [{"remarks":"Авто-выбор","outbounds":[
          {"tag":"proxy-1","protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
           "streamSettings":{"network":"grpc","grpcSettings":{"serviceName":"g"},\#(reality)}},
          {"tag":"proxy-2","protocol":"vless","settings":{"vnext":[{"address":"b.example.com","port":443,"users":[{"id":"id","flow":"xtls-rprx-vision","encryption":"none"}]}]},
           "streamSettings":{"network":"tcp",\#(reality)}},
          {"tag":"proxy-3","protocol":"vless","settings":{"vnext":[{"address":"c.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
           "streamSettings":{"network":"xhttp",\#(reality)}},
          {"protocol":"freedom","tag":"direct"}],
          "routing":{"balancers":[{"tag":"auto","selector":["proxy"]}]}}]
        """#
        let result = try SubscriptionParser.parse(body: Data(json.utf8), headers: [:])
        XCTAssertEqual(result.servers.map(\.name), ["Авто-выбор"])
        XCTAssertEqual(result.skipped.map(\.line), ["Авто-выбор · proxy-3"])

        let server = Server(subscriptionID: nil, parsed: result.servers[0])
        XCTAssertEqual(server.members.map(\.host), ["a.example.com", "b.example.com"])
        XCTAssertEqual(server.detail, "Самый быстрый из 2")
        XCTAssertEqual(server.subtitle, "Самый быстрый из 2")

        let config = ConfigBuilder.build(server: server, settings: AppSettings(), ruleSetDirectory: nil)
        let outbounds = config["outbounds"]?.arrayValue ?? []
        XCTAssertEqual(outbounds.compactMap { $0["tag"]?.stringValue }, ["proxy", "proxy-1", "proxy-2", "direct"])
        XCTAssertEqual(outbounds[0]["type"], "urltest")
        XCTAssertEqual(outbounds[0]["outbounds"], .strings(["proxy-1", "proxy-2"]))
        XCTAssertEqual(outbounds[2]["flow"], "xtls-rprx-vision")
    }

    func testEmptyAndUnsupportedBodies() {
        XCTAssertThrowsError(try SubscriptionParser.parse(body: Data(), headers: [:])) { error in
            XCTAssertEqual(error as? SubscriptionError, .empty)
        }
        XCTAssertThrowsError(try SubscriptionParser.parse(body: Data("wireguard://a@b:1".utf8), headers: [:])) { error in
            guard case .nothingSupported = error as? SubscriptionError else { return XCTFail("\(error)") }
        }
    }

    func testApplyKeepsIDsSelectionAndPosition() throws {
        var state = AppState()
        let manual = try ShareLink.parse("trojan://pw@m.example.com:443#Manual")
        state.addManual([manual])
        let sub = Subscription(name: "sub", url: URL(string: "https://s.example.com/x")!)
        state.subscriptions.append(sub)

        let first = try SubscriptionParser.parse(body: Data(Self.lines.joined(separator: "\n").utf8), headers: [:])
        state.apply(first, to: sub.id)
        XCTAssertEqual(state.servers.map(\.name), ["Manual", "Авто-выбор", "NL", "DE"])
        let de = state.servers[3]
        state.selectedServerID = de.id
        state.servers[3].lastPing = .ms(42)

        // Provider drops NL, keeps DE, adds US.
        let second = try SubscriptionParser.parse(body: Data([
            "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
            "vless://id-3@us.example.com:443?security=tls#US",
        ].joined(separator: "\n").utf8), headers: ["profile-title": "Renamed"])
        state.apply(second, to: sub.id)
        XCTAssertEqual(state.servers.map(\.name), ["Manual", "Авто-выбор", "DE", "US"])
        XCTAssertEqual(state.servers[2].id, de.id)
        XCTAssertEqual(state.servers[2].lastPing, .ms(42))
        XCTAssertEqual(state.selectedServerID, de.id)
        XCTAssertEqual(state.subscriptions[0].name, "Renamed")

        // Provider drops the selected server: selection moves to its first server.
        let third = try SubscriptionParser.parse(body: Data("vless://id-3@us.example.com:443?security=tls#US".utf8), headers: [:])
        state.apply(third, to: sub.id)
        XCTAssertEqual(state.selectedServer?.name, "US")

        state.removeSubscription(sub.id)
        XCTAssertEqual(state.servers.map(\.name), ["Manual"])
        XCTAssertEqual(state.selectedServer?.name, "Manual")
    }

    // MARK: auto row

    private func parsed(_ names: String...) throws -> [ParsedServer] {
        try names.map { try ShareLink.parse("trojan://pw@\($0).example.com:443#\($0)") }
    }

    private func result(_ servers: [ParsedServer]) -> SubscriptionResult {
        SubscriptionResult(servers: servers, skipped: [], meta: SubscriptionMeta(), routingLines: [])
    }

    private func subscription(_ state: inout AppState, _ servers: [ParsedServer]) -> UUID {
        let sub = Subscription(name: "sub", url: URL(string: "https://s.example.com/x")!)
        state.subscriptions.append(sub)
        state.apply(result(servers), to: sub.id)
        return sub.id
    }

    func testEverySubscriptionGetsAnAutoRow() throws {
        var state = AppState()
        let id = subscription(&state, try parsed("a", "b"))
        XCTAssertEqual(state.servers.map(\.name), ["Авто-выбор", "a", "b"])

        // A new subscription starts on it, and it lists the servers by name.
        let auto = state.servers[0]
        XCTAssertTrue(auto.isAuto)
        XCTAssertEqual(state.selectedServerID, auto.id)
        XCTAssertNil(auto.shareLink)
        XCTAssertEqual(auto.subtitle, "Самый быстрый из 2")
        XCTAssertEqual(auto.members.map(\.name), ["a", "b"])
        XCTAssertEqual(state.serverCount(in: id), 2)

        // A refresh keeps the row and adds the new server to it.
        state.servers[0].lastPing = .ms(30)
        state.apply(result(try parsed("a", "b", "c")), to: id)
        XCTAssertEqual(state.servers[0].id, auto.id)
        XCTAssertEqual(state.servers[0].lastPing, .ms(30))
        XCTAssertEqual(state.servers[0].members.map(\.name), ["a", "b", "c"])
        XCTAssertEqual(state.selectedServerID, auto.id)

        // With one server left there is nothing to choose from.
        state.apply(result(try parsed("c")), to: id)
        XCTAssertEqual(state.servers.map(\.name), ["c"])
        XCTAssertEqual(state.selectedServer?.name, "c")
    }

    func testAutoRowRunsAsAPlainGroup() throws {
        var state = AppState()
        _ = subscription(&state, try parsed("a", "b"))
        let config = ConfigBuilder.build(server: state.servers[0], settings: AppSettings(), ruleSetDirectory: nil)
        let outbounds = config["outbounds"]?.arrayValue ?? []
        XCTAssertEqual(outbounds.compactMap { $0["tag"]?.stringValue }, ["proxy", "proxy-1", "proxy-2", "direct"])
        XCTAssertEqual(outbounds[0]["type"], "urltest")
        XCTAssertEqual(outbounds[0]["interval"], "10m")
        XCTAssertNil(outbounds[0]["auto"])
    }

    func testProviderGroupCoveringEverythingIsEnough() throws {
        let singles = try parsed("a", "b")
        var state = AppState()
        let id = subscription(&state, [ServerGroup.make(singles, name: "Группа")] + singles)
        XCTAssertEqual(state.servers.map(\.name), ["Группа", "a", "b"])
        XCTAssertEqual(state.serverCount(in: id), 2)

        // The same with nothing but the group in the subscription.
        var only = AppState()
        let onlyID = subscription(&only, [ServerGroup.make(singles, name: "Группа")])
        XCTAssertEqual(only.servers.map(\.name), ["Группа"])
        XCTAssertEqual(only.serverCount(in: onlyID), 2)
    }

    func testProviderGroupOverSomeServersGetsAnAutoRowOverAll() throws {
        let members = try parsed("a", "b")
        var state = AppState()
        let id = subscription(&state, [ServerGroup.make(members, name: "Группа")] + (try parsed("a", "c")))
        XCTAssertEqual(state.servers.map(\.name), ["Авто-выбор", "Группа", "a", "c"])
        XCTAssertEqual(state.servers[0].members.map(\.name), ["a", "c", "b.example.com:443"])
        XCTAssertEqual(state.serverCount(in: id), 3)
    }

    func testSyncAddsAutoRowToOlderStateOnce() throws {
        var state = AppState()
        let id = UUID()
        state.subscriptions = [Subscription(id: id, name: "sub", url: URL(string: "https://s.example.com/x")!)]
        state.servers = try parsed("a", "b").map { Server(subscriptionID: id, parsed: $0) }
        state.selectedServerID = state.servers[1].id

        XCTAssertTrue(state.syncAutoGroups())
        XCTAssertEqual(state.servers.map(\.name), ["Авто-выбор", "a", "b"])
        XCTAssertEqual(state.selectedServer?.name, "b")

        let auto = state.servers[0]
        XCTAssertFalse(state.syncAutoGroups())
        XCTAssertEqual(state.servers[0].id, auto.id)
    }

    func testSortKeepsGroupsOnTop() throws {
        var state = AppState()
        _ = subscription(&state, try parsed("b", "a"))
        state.servers[1].lastPing = .ms(90)
        state.servers[2].lastPing = .ms(10)
        state.servers[0].lastPing = .ms(10)
        state.settings.sortMode = .name
        XCTAssertEqual(state.sorted(state.servers).map(\.name), ["Авто-выбор", "a", "b"])
        state.settings.sortMode = .ping
        XCTAssertEqual(state.sorted(state.servers).map(\.name), ["Авто-выбор", "a", "b"])
    }

    func testGroupIsPingedThroughItsRows() throws {
        var state = AppState()
        let id = subscription(&state, try parsed("a", "b"))
        let (auto, a, b) = (state.servers[0], state.servers[1], state.servers[2])

        // Each server once, whatever is asked for.
        XCTAssertEqual(state.pingTargets([auto]).map(\.id), [a.id, b.id])
        XCTAssertEqual(state.pingTargets(state.servers(in: id)).map(\.id), [a.id, b.id])
        XCTAssertEqual(state.pingTargets([b]).map(\.id), [b.id])

        // The group shows its fastest as results come in.
        state.setPing(.ms(80), for: a.id)
        XCTAssertEqual(state.servers[0].lastPing, .ms(80))
        state.setPing(.ms(20), for: b.id)
        XCTAssertEqual(state.servers[0].lastPing, .ms(20))
        state.setPing(.failed, for: b.id)
        XCTAssertEqual(state.servers[0].lastPing, .ms(80))
        state.setPing(.failed, for: a.id)
        XCTAssertEqual(state.servers[0].lastPing, .failed)
    }

    func testGroupWithoutItsRowsIsPingedItself() throws {
        var state = AppState()
        let group = ServerGroup.make(try parsed("a", "b"), name: "Группа")
        _ = subscription(&state, [group])
        let row = state.servers[0]
        XCTAssertEqual(state.pingTargets([row]).map(\.id), [row.id])
        state.setPing(.ms(50), for: row.id)
        XCTAssertEqual(state.servers[0].lastPing, .ms(50))

        // Servers added by hand are never treated as a group's rows.
        var manual = AppState()
        manual.addManual([group] + (try parsed("a", "b")))
        XCTAssertEqual(manual.pingTargets([manual.servers[0]]).map(\.id), [manual.servers[0].id])
    }

    func testSortByPingIsStable() throws {
        var state = AppState()
        state.addManual(try ["a", "b", "c", "d"].map { try ShareLink.parse("trojan://pw@\($0).example.com:443#\($0)") })
        state.servers[0].lastPing = .failed
        state.servers[1].lastPing = .ms(80)
        state.servers[3].lastPing = .ms(20)
        state.settings.sortMode = .ping
        XCTAssertEqual(state.sorted(state.servers).map(\.name), ["d", "b", "a", "c"])
    }

    func testStateRoundTripsThroughStore() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = SharedStore(container: dir)
        XCTAssertTrue(store.loadState().servers.isEmpty)

        var state = AppState()
        state.addManual([try ShareLink.parse("hy2://pw@h.example.com:443?obfs=salamander&obfs-password=x#H")])
        state.settings.blockAds = true
        try store.save(state)
        let loaded = store.loadState()
        XCTAssertEqual(loaded.servers, state.servers)
        XCTAssertEqual(loaded.selectedServerID, state.selectedServerID)
        XCTAssertTrue(loaded.settings.blockAds)
        XCTAssertTrue(try store.writeConfig(for: loaded))
        XCTAssertTrue(try store.readConfig().contains("\"salamander\""))
    }

    func testDeviceLimitHeaders() {
        let placeholder = Data("vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1#App not supported".utf8)
        XCTAssertThrowsError(try SubscriptionParser.parse(body: placeholder, headers: ["X-Hwid-Active": "true", "X-Hwid-Not-Supported": "true"])) {
            XCTAssertEqual($0 as? SubscriptionError, .deviceIDRefused)
        }
        XCTAssertThrowsError(try SubscriptionParser.parse(body: placeholder, headers: ["x-hwid-max-devices-reached": "true"])) {
            XCTAssertEqual($0 as? SubscriptionError, .deviceLimit)
        }
        XCTAssertNoThrow(try SubscriptionParser.parse(body: placeholder, headers: ["x-hwid-active": "true", "x-hwid-not-supported": "false"]))
    }

    func testDeviceInfo() {
        XCTAssertNotNil(DeviceInfo.hwid.range(of: "^[A-Za-z0-9=-]{10,64}$", options: .regularExpression))
        XCTAssertEqual(DeviceInfo.headers["x-hwid"], DeviceInfo.hwid)
        XCTAssertFalse(DeviceInfo.model.isEmpty)
        XCTAssertFalse(DeviceInfo.osVersion.isEmpty)
    }

    func testImporter() {
        XCTAssertEqual(Importer.classify(" https://sub.example.com/abc \n"),
                       .subscription(URL(string: "https://sub.example.com/abc")!, name: nil))
        XCTAssertEqual(Importer.classify("litetunnel://add/https%3A%2F%2Fsub.example.com%2Fabc"),
                       .subscription(URL(string: "https://sub.example.com/abc")!, name: nil))
        XCTAssertEqual(Importer.classify("litetunnel://import?url=https%3A%2F%2Fs.example.com%2F1&name=Home"),
                       .subscription(URL(string: "https://s.example.com/1")!, name: "Home"))
        guard case .servers(let servers, _) = Importer.classify("hy2://pw@h.example.com:443#H\nvless://id@v.example.com:443#V")
        else { return XCTFail() }
        XCTAssertEqual(servers.map(\.proto), [.hysteria2, .vless])
        guard case .nothing = Importer.classify("hello") else { return XCTFail() }
    }
}
