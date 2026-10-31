import Foundation
import Libbox
import LiteKit
import os

/// The core run inside the app, for Mac builds that may not create a VPN.
///
/// It reads the same config.json as the tunnel, with a local SOCKS and HTTP
/// proxy in place of the tun. Like the tunnel it does not poll: speed is read
/// only while a screen asks (see `StatusReader`).
///
/// All state lives on `queue`.
final class LocalCore: NSObject, @unchecked Sendable {
    static let log = Logger(subsystem: "LiteTunnel", category: "proxy")

    private let queue = DispatchQueue(label: "litetunnel.core", qos: .userInitiated)
    private var store: SharedStore?
    private var server: LibboxCommandServer?
    private var platform: PlatformInterface?
    private var statusReader: StatusReader?
    private var port = 0

    /// Starts the core and returns the port of its local proxy.
    func start() async throws -> Int {
        try await onQueue {
            guard let store = SharedStore.shared() else {
                throw VPNError("Нет доступа к общей папке App Group. Проверьте подпись приложения.")
            }
            try CoreSetup.ensure(store)
            let port = try CoreSetup.freePorts(1)[0]
            let config = try Self.config(store, port: port)

            let platform = PlatformInterface(provider: nil, includeAllNetworks: false)
            var error: NSError?
            guard let server = LibboxNewCommandServer(self, platform, &error) else {
                throw error ?? VPNError("Не удалось создать ядро")
            }
            self.store = store
            self.platform = platform
            self.server = server
            self.port = port
            do {
                try server.start()
                try server.startOrReloadService(config, options: LibboxOverrideOptions())
            } catch {
                self.shutdown()
                throw error
            }
            self.statusReader = StatusReader()
            return port
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            queue.async {
                self.shutdown()
                continuation.resume()
            }
        }
    }

    /// For app quit, which does not wait for async work.
    func stopNow() {
        queue.sync { shutdown() }
    }

    /// Answers the app's commands as the tunnel does.
    func handle(_ command: TunnelCommand) async -> Data? {
        await withCheckedContinuation { continuation in
            queue.async {
                switch command {
                case .reload:
                    do {
                        try self.reload()
                        continuation.resume(returning: Data())
                    } catch {
                        continuation.resume(returning: Data(error.localizedDescription.utf8))
                    }
                case .status:
                    let status = self.statusReader?.status() ?? TunnelStatus()
                    continuation.resume(returning: try? JSONEncoder().encode(status))
                case .logs:
                    let lines = self.statusReader?.logs() ?? []
                    continuation.resume(returning: try? JSONEncoder().encode(lines))
                case .wake:
                    self.server?.wakeNow()
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    // MARK: on `queue`

    private func shutdown() {
        statusReader?.stop()
        statusReader = nil
        if let server {
            try? server.closeService()
            server.close()
        }
        server = nil
        platform?.reset()
        platform = nil
        port = 0
    }

    /// Re-reads config.json and restarts the core in place, on the same port.
    private func reload() throws {
        guard let store, let server else { throw VPNError("Прокси не запущен") }
        try server.startOrReloadService(try Self.config(store, port: port), options: LibboxOverrideOptions())
    }

    private func onQueue<T>(_ body: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { continuation.resume(with: Result { try body() }) }
        }
    }

    private static func config(_ store: SharedStore, port: Int) throws -> String {
        let text: String
        do {
            text = try store.readConfig()
        } catch {
            throw VPNError("Сервер не выбран")
        }
        let config = try JSONValue.parse(Data(text.utf8))
        return ConfigBuilder.withLocalProxy(config, port: port).jsonString()
    }
}

extension LocalCore: LibboxCommandServerHandlerProtocol {
    func serviceStop() throws {}

    // Called on a core thread. Not through `queue`: that queue may itself be
    // waiting on the core.
    func serviceReload() throws {
        try reload()
    }

    func getSystemProxyStatus() throws -> LibboxSystemProxyStatus {
        LibboxSystemProxyStatus()
    }

    func setSystemProxyEnabled(_ enabled: Bool) throws {}

    func connectSSHAgent(_ ret0_: UnsafeMutablePointer<Int32>?) throws {
        throw VPNError("Не поддерживается")
    }

    func triggerNativeCrash() throws {
        throw VPNError("Не поддерживается")
    }

    func writeDebugMessage(_ message: String?) {
        guard let message else { return }
        Self.log.debug("\(message, privacy: .public)")
    }
}
