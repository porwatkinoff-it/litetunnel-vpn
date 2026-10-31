import Foundation
import Libbox
import LiteKit
import NetworkExtension
import os

/// Runs the sing-box core for the server the app selected.
///
/// The app writes `config.json` to the App Group; this extension only reads it.
/// Nothing here polls: speed and logs are read from the core only while the
/// app is on screen and asking (see `StatusReader`), and on iOS the core pauses
/// its timers while the device sleeps.
final class PacketTunnelProvider: NEPacketTunnelProvider {
    private let queue = DispatchQueue(label: "litetunnel.tunnel", qos: .userInitiated)
    private var store: SharedStore?
    private var server: LibboxCommandServer?
    private var platform: PlatformInterface?
    private var statusReader: StatusReader?
    private var smartSleep = true
    #if os(iOS)
    private var screen: ScreenObserver?
    #endif

    private static var didSetup = false
    static let log = Logger(subsystem: "LiteTunnel", category: "tunnel")

    override func startTunnel(options: [String: NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        queue.async {
            do {
                try self.start()
                completionHandler(nil)
            } catch {
                Self.log.error("start failed: \(error.localizedDescription, privacy: .public)")
                self.shutdown()
                completionHandler(error)
            }
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        queue.async {
            self.shutdown()
            completionHandler()
        }
    }

    private func start() throws {
        guard let store = SharedStore.shared() else {
            throw TunnelError("Нет доступа к общей папке App Group. Проверьте подпись приложения и расширения.")
        }
        self.store = store
        try store.prepareDirectories()
        try Self.setupCore(store)

        let config: String
        do {
            config = try store.readConfig()
        } catch {
            throw TunnelError("Сервер не выбран")
        }
        smartSleep = store.loadState().settings.smartSleep

        let killSwitch = (protocolConfiguration as? NETunnelProviderProtocol)?.includeAllNetworks ?? false
        let platform = PlatformInterface(provider: self, includeAllNetworks: killSwitch)
        var error: NSError?
        guard let server = LibboxNewCommandServer(self, platform, &error) else {
            throw error ?? TunnelError("Не удалось создать ядро")
        }
        self.platform = platform
        self.server = server
        try server.start()
        try server.startOrReloadService(config, options: LibboxOverrideOptions())
        statusReader = StatusReader()

        #if os(iOS)
        // The core ignores `wake()` on iOS: the extension is woken for every
        // push and background task. Its sleep pause ends when the screen
        // turns on, or when the app comes to the foreground.
        screen = ScreenObserver { [weak self] on in
            guard on else { return }
            self?.queue.async { self?.server?.wakeNow() }
        }
        #endif
    }

    private static func setupCore(_ store: SharedStore) throws {
        guard !didSetup else { return }
        let options = LibboxSetupOptions()
        options.basePath = store.container.path
        options.workingPath = store.workingDirectory.path
        options.tempPath = store.tempDirectory.path
        options.logMaxLines = StatusReader.maxLogLines
        #if os(iOS)
        // Keeps the Go heap under the extension's memory limit (about 50 MB)
        // instead of letting iOS kill the tunnel.
        options.oomKillerEnabled = true
        #endif
        var error: NSError?
        LibboxSetup(options, &error)
        if let error { throw error }
        didSetup = true
    }

    private func shutdown() {
        #if os(iOS)
        screen = nil
        #endif
        statusReader?.stop()
        statusReader = nil
        if let server {
            try? server.closeService()
            server.close()
        }
        server = nil
        platform?.reset()
        platform = nil
    }

    /// Re-reads config.json and restarts the core in place, keeping the tunnel up.
    private func reload() throws {
        guard let store, let server else { throw TunnelError("Туннель не запущен") }
        let config: String
        do {
            config = try store.readConfig()
        } catch {
            throw TunnelError("Сервер не выбран")
        }
        smartSleep = store.loadState().settings.smartSleep
        try server.startOrReloadService(config, options: LibboxOverrideOptions())
    }

    override func handleAppMessage(_ messageData: Data, completionHandler: ((Data?) -> Void)?) {
        guard let command = TunnelCommand(data: messageData) else {
            completionHandler?(nil)
            return
        }
        queue.async {
            switch command {
            case .reload:
                do {
                    try self.reload()
                    completionHandler?(Data())
                } catch {
                    completionHandler?(Data(error.localizedDescription.utf8))
                }
            case .status:
                let status = self.statusReader?.status() ?? TunnelStatus()
                completionHandler?(try? JSONEncoder().encode(status))
            case .logs:
                let lines = self.statusReader?.logs() ?? []
                completionHandler?(try? JSONEncoder().encode(lines))
            case .wake:
                self.server?.wakeNow()
                completionHandler?(nil)
            }
        }
    }

    override func sleep(completionHandler: @escaping () -> Void) {
        queue.async {
            if self.smartSleep {
                // On iOS: closes idle connections and stops the core's timers.
                self.server?.pause()
            }
            completionHandler()
        }
    }

    override func wake() {
        queue.async {
            #if os(iOS)
            if ScreenObserver.isScreenOn { self.server?.wakeNow() }
            #else
            self.server?.wake()
            #endif
        }
    }
}

extension PacketTunnelProvider: LibboxCommandServerHandlerProtocol {
    func serviceStop() throws {
        cancelTunnelWithError(nil)
    }

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
        throw TunnelError("Не поддерживается")
    }

    func triggerNativeCrash() throws {
        throw TunnelError("Не поддерживается")
    }

    func writeDebugMessage(_ message: String?) {
        guard let message else { return }
        Self.log.debug("\(message, privacy: .public)")
    }
}

struct TunnelError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
