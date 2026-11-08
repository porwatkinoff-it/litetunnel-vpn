import XCTest
@testable import LiteKit

final class SharedStoreTests: XCTestCase {
    private func profile(groups: [String]?) -> Data {
        var entitlements: [String: Any] = ["application-identifier": "ABCDE12345.org.example.app"]
        if let groups { entitlements["com.apple.security.application-groups"] = groups }
        let plist: [String: Any] = ["Name": "test", "Entitlements": entitlements]
        let xml = try! PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        // Stand-ins for the CMS wrapper around the plist.
        return Data([0x30, 0x82, 0x1f, 0x00, 0x06, 0x09]) + xml + Data([0xa0, 0x82, 0x0d, 0x00, 0x3c, 0x2f])
    }

    func testReadsGroupsFromProfile() {
        let data = profile(groups: ["group.org.example.app", "group.org.example.other"])
        XCTAssertEqual(SharedStore.appGroups(inProfile: data), ["group.org.example.app", "group.org.example.other"])
    }

    func testProfileWithoutGroupsGivesNone() {
        XCTAssertEqual(SharedStore.appGroups(inProfile: profile(groups: nil)), [])
        XCTAssertEqual(SharedStore.appGroups(inProfile: Data("not a profile".utf8)), [])
        XCTAssertEqual(SharedStore.appGroups(inProfile: Data()), [])
    }

    func testTunnelAlsoReadsTheAppsProfile() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("Test.app", isDirectory: true)
        let appex = app.appendingPathComponent("PlugIns/Tunnel.appex", isDirectory: true)
        try FileManager.default.createDirectory(at: appex, withIntermediateDirectories: true)
        try profile(groups: ["group.org.example.app"]).write(to: app.appendingPathComponent("embedded.mobileprovision"))
        let info: [String: Any] = ["CFBundleIdentifier": "org.example.app.tunnel", "LTAppGroup": "group.com.example.litetunnel"]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: appex.appendingPathComponent("Info.plist"))

        let bundle = try XCTUnwrap(Bundle(url: appex))
        XCTAssertEqual(SharedStore.candidateGroups(bundle: bundle), ["group.com.example.litetunnel", "group.org.example.app"])
    }
}
