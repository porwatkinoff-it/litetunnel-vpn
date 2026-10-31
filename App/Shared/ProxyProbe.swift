import Foundation
import Libbox
import LiteKit
import os

/// Pings servers through themselves, for the proxy ping modes: a request to
/// the test address through each server shows whether it passes traffic, not
/// only whether it answers.
///
/// A second core runs in the app for the measurement, with one local proxy
/// per server, and closes right after. Its sockets are bound outside the
/// tunnel, so the VPN can stay up. It opens no command socket and shares no
/// files with the tunnel's core.
enum ProxyProbe {
    static let log = Logger(subsystem: "LiteTunnel", category: "ping")

    /// Calls `onResult` for each server as its result comes in; a group's
    /// once all its members are done. Throws when the core does not start.
    static func measure(_ servers: [Server], settings: AppSettings, store: SharedStore,
                        onResult: @escaping @Sendable (UUID, PingResult?) async -> Void) async throws {
        let mode = settings.pingMode
        let url = testURL(settings.pingURL)
        var targets: [Target] = []
        var counts: [UUID: Int] = [:]
        for server in servers {
            let members = server.members
            for member in members.isEmpty ? [server] : members {
                targets.append(Target(owner: server.id, server: member))
            }
            counts[server.id] = max(1, members.count)
        }
        guard !targets.isEmpty else { return }

        let core = try await Core.start(targets.map(\.server), settings: settings, store: store)
        let results = Results(counts)
        await Ping.forEach(Array(zip(targets, core.ports)), concurrency: 8) { target, port in
            let ms = await Ping.request(url, through: port, head: mode == .proxyHead)
            if let done = await results.add(Ping.result(ms, mode: mode), for: target.owner) {
                await onResult(target.owner, done)
            }
        }
        await core.stop()
    }

    /// The address from settings when it is a web one, else the default.
    static func testURL(_ text: String) -> URL {
        if let url = URL(string: text.trimmingCharacters(in: .whitespaces)),
           ["http", "https"].contains(url.scheme?.lowercased() ?? ""), url.host != nil {
            return url
        }
        return URL(string: AppSettings.defaultPingURL)!
    }

    private struct Target: Sendable {
        let owner: UUID
        let server: Server
    }

    /// Members' results until the whole group is in.
    private actor Results {
        private var left: [UUID: Int]
        private var seen: [UUID: [PingResult]] = [:]

        init(_ counts: [UUID: Int]) { left = counts }

        func add(_ result: PingResult, for owner: UUID) -> PingResult? {
            seen[owner, default: []].append(result)
            left[owner, default: 1] -= 1
            return left[owner] == 0 ? Ping.fastest(seen[owner] ?? []) : nil
        }
    }

    /// The core, on its own queue: starting and closing it block.
    private final class Core: @unchecked Sendable {
        private static let queue = DispatchQueue(label: "litetunnel.probe", qos: .userInitiated)
        let ports: [Int]
        private let server: LibboxCommandServer
        private let platform: PlatformInterface

        static func start(_ servers: [Server], settings: AppSettings, store: SharedStore) async throws -> Core {
            try await withCheckedThrowingContinuation { continuation in
                queue.async {
                    continuation.resume(with: Result { try Core(servers, settings: settings, store: store) })
                }
            }
        }

        private init(_ servers: [Server], settings: AppSettings, store: SharedStore) throws {
            try CoreSetup.ensure(store)
            ports = try CoreSetup.freePorts(servers.count)
            let config = ConfigBuilder.probe(servers, ports: ports, settings: settings).jsonString()
            platform = PlatformInterface(provider: nil, includeAllNetworks: false, bindOutside: true)
            var error: NSError?
            guard let server = LibboxNewCommandServer(Handler(), platform, &error) else {
                throw error ?? VPNError("Не удалось создать ядро")
            }
            self.server = server
            // No start(): that opens the command socket, which only the
            // app's screens would use.
            do {
                try server.startOrReloadService(config, options: LibboxOverrideOptions())
            } catch {
                server.close()
                platform.reset()
                log.error("probe core: \(error.localizedDescription, privacy: .public)")
                throw error
            }
        }

        func stop() async {
            await withCheckedContinuation { continuation in
                Self.queue.async {
                    try? self.server.closeService()
                    self.server.close()
                    self.platform.reset()
                    continuation.resume()
                }
            }
        }
    }

    private final class Handler: NSObject, LibboxCommandServerHandlerProtocol {
        func serviceStop() throws {}
        func serviceReload() throws {}
        func getSystemProxyStatus() throws -> LibboxSystemProxyStatus { LibboxSystemProxyStatus() }
        func setSystemProxyEnabled(_ enabled: Bool) throws {}

        func connectSSHAgent(_ ret0_: UnsafeMutablePointer<Int32>?) throws {
            throw VPNError("Не поддерживается")
        }

        func triggerNativeCrash() throws {
            throw VPNError("Не поддерживается")
        }

        func writeDebugMessage(_ message: String?) {}
    }
}
