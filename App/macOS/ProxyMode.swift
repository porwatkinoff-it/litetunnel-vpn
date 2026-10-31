import AppKit
import LiteKit

/// Connecting without a VPN profile, for Mac builds signed without Apple's
/// VPN entitlement (scripts/package.sh): the core runs in the app and the
/// system proxy points at it. Programs that ignore the system proxy go
/// direct.
///
/// Steps run one after another, so a quick connect and disconnect cannot
/// leave the proxy pointing at a stopped core.
@MainActor
final class ProxyMode {
    private let core = LocalCore()
    private let system = SystemProxySwitch()
    private var chain: Task<Void, Never>?
    private var port = 0
    private var quitObserver: NSObjectProtocol?

    init() {
        let core = core
        let system = system
        // Quitting does not wait for async work, so this blocks for a moment.
        quitObserver = NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { _ in
            system.restoreNow()
            core.stopNow()
        }
    }

    /// Undoes a system proxy left on by a crash or a power cut.
    func recover() async {
        do {
            try await serial { try await self.system.restore() }
        } catch {
            LocalCore.log.error("restore at launch: \(error.localizedDescription, privacy: .public)")
        }
    }

    func start(settings: AppSettings) async throws {
        try await serial {
            // Leftovers first, so they are not taken for the user's settings.
            try await self.system.restore()
            let port = try await self.core.start()
            do {
                try await self.system.enable(port: port, bypass: Self.bypass(settings))
            } catch {
                await self.core.stop()
                throw error
            }
            self.port = port
            LocalCore.log.info("local proxy on 127.0.0.1:\(port, privacy: .public)")
        }
    }

    /// The core stops only once the system proxy is back as it was, so the
    /// Mac never keeps a proxy that nothing answers.
    func stop() async throws {
        try await serial {
            try await self.system.restore()
            await self.core.stop()
            self.port = 0
        }
    }

    /// The local network setting lives in the proxy's exceptions.
    func updateBypass(settings: AppSettings) async throws {
        try await serial {
            guard self.port != 0 else { return }
            try await self.system.restore()
            try await self.system.enable(port: self.port, bypass: Self.bypass(settings))
        }
    }

    func handle(_ command: TunnelCommand) async -> Data? {
        await core.handle(command)
    }

    /// Hosts the system sends past the proxy. The core sends private
    /// addresses direct anyway; this saves them the hop.
    static func bypass(_ settings: AppSettings) -> [String] {
        var hosts = ["127.0.0.1", "localhost", "::1", "*.local", "169.254/16"]
        if settings.excludeLocalNetworks {
            hosts += ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]
        }
        return hosts
    }

    private func serial<T>(_ work: @escaping @MainActor () async throws -> T) async throws -> T {
        let previous = chain
        let task = Task { @MainActor () async throws -> T in
            await previous?.value
            return try await work()
        }
        chain = Task { _ = try? await task.value }
        return try await task.value
    }
}
