import Foundation
import Network
import XCTest
@testable import LiteKit

/// The check through a server, against a local proxy that answers every
/// request itself, the way a broken server or a network on the way might.
final class PingCheckTests: XCTestCase {
    func testGenerate204NeedsA204() async throws {
        let url = URL(string: "http://check.invalid/generate_204")!
        let stub = try FakeProxy { _ in .init(status: "200 OK", body: Data("ok".utf8)) }
        let ms = await Ping.request(url, through: stub.port, head: false, timeout: 3)
        XCTAssertNil(ms, "a page instead of 204 is not the test address")

        let real = try FakeProxy { _ in .init(status: "204 No Content", body: Data()) }
        let time = await Ping.request(url, through: real.port, head: false, timeout: 3)
        XCTAssertNotNil(time)
    }

    func testOtherAddressesPassWith200To399() {
        let url = URL(string: "https://example.com/")!
        XCTAssertTrue(Ping.passes(200, from: url))
        XCTAssertTrue(Ping.passes(302, from: url))
        XCTAssertFalse(Ping.passes(404, from: url))
        XCTAssertFalse(Ping.passes(500, from: url))
        let generate = URL(string: "https://www.gstatic.com/generate_204")!
        XCTAssertTrue(Ping.passes(204, from: generate))
        XCTAssertFalse(Ping.passes(200, from: generate))
        XCTAssertFalse(Ping.passes(302, from: generate))
    }

    func testCheckPassesOnTheWholeDownload() async throws {
        let proxy = try FakeProxy { head in
            XCTAssertTrue(head.lowercased().contains("range: bytes=0-\(Ping.checkBytes - 1)"))
            return .init(status: "206 Partial Content", body: Data(count: Ping.checkBytes))
        }
        let passed = await Ping.check(through: proxy.port, url: Self.file, idle: 1, total: 3)
        XCTAssertTrue(passed)
    }

    func testCheckFailsWhenTheConnectionStalls() async throws {
        // About what some networks let through before they freeze a connection.
        let proxy = try FakeProxy { _ in
            .init(status: "200 OK", body: Data(count: 16 * 1024), length: Ping.checkBytes, keepOpen: true)
        }
        let start = Date()
        let passed = await Ping.check(through: proxy.port, url: Self.file, idle: 1, total: 3)
        XCTAssertFalse(passed)
        XCTAssertLessThan(Date().timeIntervalSince(start), 5)
    }

    func testCheckFailsOnAnErrorPage() async throws {
        let proxy = try FakeProxy { _ in .init(status: "403 Forbidden", body: Data(count: Ping.checkBytes)) }
        let passed = await Ping.check(through: proxy.port, url: Self.file, idle: 1, total: 3)
        XCTAssertFalse(passed)
    }

    func testCheckStopsWhenTheRangeIsIgnored() async throws {
        let proxy = try FakeProxy { _ in
            .init(status: "200 OK", body: Data(count: 4 * Ping.checkBytes))
        }
        let passed = await Ping.check(through: proxy.port, url: Self.file, idle: 1, total: 3)
        XCTAssertTrue(passed)
    }

    func testRowsShowingIncludeTheirGroups() {
        let sub = UUID()
        let a = server("a", sub), b = server("b", sub), c = server("c", sub)
        var group = server("auto", sub)
        group.outbound = ["type": "urltest", "outbounds": .array([a.outbound, b.outbound])]
        var state = AppState()
        state.servers = [group, a, b, c]
        guard state.servers[0].isGroup else { return XCTFail("the urltest row is not a group") }
        XCTAssertEqual(state.rowsShowing([a.id]), [a.id, group.id])
        XCTAssertEqual(state.rowsShowing([c.id]), [c.id])
        XCTAssertEqual(state.rowsShowing([]), [])
    }

    private static let file = URL(string: "http://check.invalid/file.bin")!

    private func server(_ name: String, _ subscription: UUID) -> Server {
        let outbound: JSONValue = [
            "type": "vless", "server": .string("\(name).example.com"), "server_port": 443,
            "uuid": "00000000-0000-0000-0000-000000000000",
        ]
        return Server(subscriptionID: subscription, parsed: ParsedServer(
            name: name, proto: .vless, host: "\(name).example.com", port: 443, outbound: outbound, shareLink: nil))
    }
}

/// Answers every proxied request with `reply`, on 127.0.0.1.
private final class FakeProxy: @unchecked Sendable {
    struct Reply: Sendable {
        var status: String
        var body: Data
        /// The Content-Length sent, when it is not the body's.
        var length: Int?
        /// Leaves the connection open after the body, as a stalled one.
        var keepOpen = false
    }

    let port: Int
    private let listener: NWListener
    private let connections = Connections()

    init(_ reply: @escaping @Sendable (String) -> Reply) throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        let listener = try NWListener(using: parameters)
        let queue = DispatchQueue(label: "fake-proxy")
        let connections = connections
        let ready = DispatchSemaphore(value: 0)
        listener.stateUpdateHandler = { state in
            if case .ready = state { ready.signal() }
        }
        listener.newConnectionHandler = { connection in
            connections.list.append(connection)
            connection.start(queue: queue)
            Self.readHead(connection, Data()) { head in
                let answer = reply(head)
                var data = Data("HTTP/1.1 \(answer.status)\r\nContent-Length: \(answer.length ?? answer.body.count)\r\n\r\n".utf8)
                data.append(answer.body)
                connection.send(content: data, completion: .contentProcessed { _ in
                    if !answer.keepOpen { connection.cancel() }
                })
            }
        }
        listener.start(queue: queue)
        guard ready.wait(timeout: .now() + 5) == .success, let port = listener.port else {
            listener.cancel()
            throw URLError(.cannotConnectToHost)
        }
        self.listener = listener
        self.port = Int(port.rawValue)
    }

    deinit {
        listener.cancel()
        connections.list.forEach { $0.cancel() }
    }

    /// Kept so a stalled connection stays open until the test ends.
    private final class Connections: @unchecked Sendable {
        var list: [NWConnection] = []
    }

    private static func readHead(_ connection: NWConnection, _ buffer: Data, _ done: @escaping (String) -> Void) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { data, _, complete, error in
            var buffer = buffer
            if let data { buffer.append(data) }
            if let end = buffer.range(of: Data("\r\n\r\n".utf8)) {
                done(String(decoding: buffer[..<end.lowerBound], as: UTF8.self))
            } else if !complete, error == nil {
                readHead(connection, buffer, done)
            }
        }
    }
}
