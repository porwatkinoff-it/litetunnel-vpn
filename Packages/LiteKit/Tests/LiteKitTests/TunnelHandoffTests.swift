import XCTest
@testable import LiteKit

/// The path a signature without an App Group takes: the app hands its state
/// to the tunnel, which keeps it in its own folder.
final class TunnelHandoffTests: XCTestCase {
    private var root: URL!
    private var app: SharedStore!
    private var tunnel: SharedStore!

    private let srs = Data("SRS".utf8) + Data(repeating: 1, count: 16)

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        app = SharedStore(container: root.appendingPathComponent("app"))
        tunnel = SharedStore(container: root.appendingPathComponent("tunnel"))
        try app.prepareDirectories()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func state(routing: RoutingMode = .russiaDirect) throws -> AppState {
        var state = AppState()
        let server = Server(subscriptionID: nil, parsed: try ShareLink.parse(
            "vless://00000000-0000-0000-0000-000000000000@example.com:443?security=tls&type=tcp#Test"))
        state.servers = [server]
        state.selectedServerID = server.id
        state.settings.routing = routing
        state.settings.smartSleep = false
        return state
    }

    func testCarriesStateAndNeededRuleSets() throws {
        try srs.write(to: app.ruleSetDirectory.appendingPathComponent("\(RuleSets.geoipRU).srs"))
        try srs.write(to: app.ruleSetDirectory.appendingPathComponent("\(RuleSets.ads).srs"))
        let handoff = app.handoff(for: try state(), appGroup: nil)
        // Ads are off, so that file stays behind.
        XCTAssertEqual(Set(handoff.ruleSets.keys), [RuleSets.geoipRU])
        XCTAssertNil(handoff.appGroup)

        let decoded = try TunnelHandoff(encoded: try handoff.encoded())
        XCTAssertEqual(decoded.state.selectedServerID, handoff.state.selectedServerID)
        XCTAssertEqual(decoded.ruleSets, handoff.ruleSets)
    }

    func testReloadMessageIsNotACommand() throws {
        let handoff = TunnelHandoff(state: try state(), appGroup: "group.example")
        let message = try handoff.message()
        XCTAssertNil(TunnelCommand(data: message))
        XCTAssertEqual(TunnelHandoff(message: message)?.appGroup, "group.example")
        for command in [TunnelCommand.reload, .status, .logs, .wake] {
            XCTAssertNil(TunnelHandoff(message: command.data))
        }
        XCTAssertNil(TunnelHandoff(message: Data("handoff\nnot json".utf8)))
    }

    func testTunnelBuildsConfigInItsOwnFolder() throws {
        try srs.write(to: app.ruleSetDirectory.appendingPathComponent("\(RuleSets.geoipRU).srs"))
        try srs.write(to: app.ruleSetDirectory.appendingPathComponent("\(RuleSets.geositeRU).srs"))
        let sent = try state()
        try tunnel.apply(app.handoff(for: sent, appGroup: nil))

        XCTAssertEqual(tunnel.loadState().selectedServerID, sent.selectedServerID)
        XCTAssertFalse(tunnel.loadState().settings.smartSleep)
        XCTAssertEqual(RuleSets.available(in: tunnel.ruleSetDirectory), [RuleSets.geoipRU, RuleSets.geositeRU])
        let config = try tunnel.readConfig()
        XCTAssertTrue(config.contains(tunnel.ruleSetDirectory.path), "rule-sets must be read from the tunnel's folder")
        XCTAssertFalse(config.contains(app.container.path))
    }

    func testDropsRuleSetsTheAppNoLongerSends() throws {
        try srs.write(to: app.ruleSetDirectory.appendingPathComponent("\(RuleSets.geoipRU).srs"))
        try tunnel.apply(app.handoff(for: try state(), appGroup: nil))
        try tunnel.apply(app.handoff(for: try state(routing: .global), appGroup: nil))
        XCTAssertEqual(RuleSets.available(in: tunnel.ruleSetDirectory), [])
        XCTAssertFalse(try tunnel.readConfig().contains("rule_set\":[{"))
    }

    func testWritesOnlyKnownValidRuleSets() throws {
        var handoff = TunnelHandoff(state: try state())
        handoff.ruleSets = [
            "../escape": srs,
            RuleSets.geositeRU: Data("not srs".utf8),
            RuleSets.geoipRU: srs,
        ]
        try tunnel.apply(handoff)
        XCTAssertEqual(RuleSets.available(in: tunnel.ruleSetDirectory), [RuleSets.geoipRU])
        XCTAssertFalse(FileManager.default.fileExists(atPath: tunnel.container.appendingPathComponent("escape.srs").path))
    }

    func testNoServerLeavesNoConfig() throws {
        var empty = try state()
        empty.selectedServerID = nil
        empty.servers = []
        try tunnel.apply(TunnelHandoff(state: try state()))
        try tunnel.apply(TunnelHandoff(state: empty))
        XCTAssertThrowsError(try tunnel.readConfig())
    }
}
