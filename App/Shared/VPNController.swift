import Foundation
import LiteKit
import NetworkExtension

/// The system VPN profile and the tunnel's state.
///
/// Status comes from the system's change notification, never from polling.
/// Speed is asked from the tunnel only by a screen that is showing it.
///
/// A Mac build without the VPN entitlement connects through the system
/// proxy instead (see `ProxyMode`), with the same states.
@MainActor
final class VPNController: ObservableObject {
    @Published private(set) var status: NEVPNStatus = .invalid
    @Published private(set) var connectedSince: Date?
    @Published private(set) var traffic = TunnelStatus()

    private var manager: NETunnelProviderManager?
    /// The app's state for the tunnel, sent with every start and reload. A
    /// tunnel that shares the app's App Group ignores it.
    var handoff: (@MainActor () -> TunnelHandoff?)?
    private var observer: NSObjectProtocol?
    private var loaded = false

    static let profileName = "LiteTunnel"

    /// Read from the extension itself, since a tool that re-signs the app
    /// may give it other bundle IDs.
    static let providerBundleID: String = {
        let plugins = Bundle.main.builtInPlugInsURL.flatMap {
            try? FileManager.default.contentsOfDirectory(at: $0, includingPropertiesForKeys: nil)
        } ?? []
        for url in plugins where url.pathExtension == "appex" {
            guard let bundle = Bundle(url: url),
                  let ext = bundle.object(forInfoDictionaryKey: "NSExtension") as? [String: Any],
                  ext["NSExtensionPointIdentifier"] as? String == "com.apple.networkextension.packet-tunnel",
                  let id = bundle.bundleIdentifier
            else { continue }
            return id
        }
        return (Bundle.main.bundleIdentifier ?? "com.example.litetunnel") + ".tunnel"
    }()

    #if os(macOS)
    /// Builds from scripts/package.sh have no Apple profile, so their
    /// signature leaves the Network Extension entitlement out.
    private static let hasVPNEntitlement: Bool = {
        guard let task = SecTaskCreateFromSelf(nil) else { return true }
        return SecTaskCopyValueForEntitlement(task, "com.apple.developer.networking.networkextension" as CFString, nil) != nil
    }()

    private lazy var proxyMode = ProxyMode()
    #endif

    /// True for a Mac build that may not create a VPN.
    static var usesSystemProxy: Bool {
        #if os(macOS)
        return !hasVPNEntitlement
        #else
        return false
        #endif
    }

    var isActive: Bool {
        switch status {
        case .connecting, .connected, .reasserting: return true
        default: return false
        }
    }

    var statusText: String {
        switch status {
        case .connected: return "Подключено"
        case .connecting: return "Подключение…"
        case .reasserting: return "Переподключение…"
        case .disconnecting: return "Отключение…"
        case .disconnected, .invalid: return "Отключено"
        @unknown default: return "Отключено"
        }
    }

    init() {
        observer = NotificationCenter.default.addObserver(forName: .NEVPNStatusDidChange, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.syncStatus() }
        }
    }

    /// Finds our profile, if one was saved before.
    func load() async {
        #if os(macOS)
        if Self.usesSystemProxy {
            guard !loaded else { return }
            loaded = true
            await proxyMode.recover()
            if status == .invalid { status = .disconnected }
            return
        }
        #endif
        let managers = (try? await NETunnelProviderManager.loadAllFromPreferences()) ?? []
        manager = managers.first {
            ($0.protocolConfiguration as? NETunnelProviderProtocol)?.providerBundleIdentifier == Self.providerBundleID
        }
        loaded = true
        syncStatus()
    }

    private func syncStatus() {
        if Self.usesSystemProxy { return }
        guard let connection = manager?.connection else {
            status = .invalid
            connectedSince = nil
            return
        }
        status = connection.status
        connectedSince = status == .connected ? connection.connectedDate : nil
        if status != .connected { traffic = TunnelStatus() }
    }

    // MARK: profile

    /// Creates or updates the profile. The first save shows the system's
    /// "Add VPN configuration" prompt.
    @discardableResult
    private func ensureProfile(_ settings: AppSettings) async throws -> NETunnelProviderManager {
        if !loaded { await load() }
        let manager = self.manager ?? NETunnelProviderManager()
        let proto = (manager.protocolConfiguration as? NETunnelProviderProtocol) ?? NETunnelProviderProtocol()

        let changed = self.manager == nil
            || !manager.isEnabled
            || proto.providerBundleIdentifier != Self.providerBundleID
            || proto.includeAllNetworks != settings.killSwitch
            || proto.excludeLocalNetworks != settings.excludeLocalNetworks
        guard changed else { return manager }

        proto.providerBundleIdentifier = Self.providerBundleID
        proto.serverAddress = Self.profileName
        proto.includeAllNetworks = settings.killSwitch
        proto.excludeLocalNetworks = settings.excludeLocalNetworks
        proto.disconnectOnSleep = false
        manager.protocolConfiguration = proto
        manager.localizedDescription = Self.profileName
        manager.isEnabled = true

        #if os(macOS)
        // Without the entitlement macOS fails the save with the same error as
        // a refused prompt, and shows no prompt at all.
        guard Self.hasVPNEntitlement else {
            throw VPNError("Эта сборка для Mac подписана без сертификата Apple, и macOS не даёт ей создать VPN. Подписки и пинг работают, а для подключения приложение нужно подписать аккаунтом разработчика Apple.")
        }
        #endif
        do {
            try await manager.saveToPreferences()
        } catch let error as NEVPNError where error.code == .configurationReadWriteFailed {
            throw VPNError("Не удалось сохранить профиль VPN. Разрешите добавление конфигурации VPN.")
        }
        // A saved profile must be reloaded before it can start.
        try await manager.loadFromPreferences()
        self.manager = manager
        syncStatus()
        return manager
    }

    func applyProfile(settings: AppSettings, restartIfActive: Bool) async throws {
        #if os(macOS)
        if Self.usesSystemProxy {
            if status == .connected { try await proxyMode.updateBypass(settings: settings) }
            return
        }
        #endif
        guard manager != nil else { return }
        let wasActive = isActive
        try await ensureProfile(settings)
        if restartIfActive && wasActive {
            disconnect()
            await waitUntilStopped()
            try await connect(settings: settings)
        }
    }

    // MARK: connect

    func connect(settings: AppSettings) async throws {
        #if os(macOS)
        if Self.usesSystemProxy {
            guard !isActive else { return }
            status = .connecting
            do {
                try await proxyMode.start(settings: settings)
            } catch {
                if status == .connecting { status = .disconnected }
                throw error
            }
            // A disconnect pressed meanwhile has the last word.
            if status == .connecting {
                status = .connected
                connectedSince = Date()
            }
            return
        }
        #endif
        let manager = try await ensureProfile(settings)
        let options = (try? handoff?()?.encoded()).map { [TunnelHandoff.optionKey: $0 as NSData] }
        do {
            try manager.connection.startVPNTunnel(options: options)
        } catch let error as NEVPNError {
            switch error.code {
            case .configurationDisabled, .configurationInvalid:
                throw VPNError("Профиль VPN выключен или повреждён. Попробуйте ещё раз.")
            default:
                throw error
            }
        }
    }

    func disconnect() {
        #if os(macOS)
        if Self.usesSystemProxy {
            guard isActive else { return }
            status = .disconnecting
            Task {
                do {
                    try await proxyMode.stop()
                } catch {
                    // The proxy is still on, so the core keeps answering it;
                    // pressing again retries.
                    LocalCore.log.error("disconnect: \(error.localizedDescription, privacy: .public)")
                    if status == .disconnecting {
                        status = .connected
                        connectedSince = connectedSince ?? Date()
                    }
                    return
                }
                if status == .disconnecting {
                    status = .disconnected
                    connectedSince = nil
                    traffic = TunnelStatus()
                }
            }
            return
        }
        #endif
        manager?.connection.stopVPNTunnel()
    }

    private func waitUntilStopped() async {
        // A short wait during a restart only; usually under a second.
        var tries = 0
        while (isActive || status == .disconnecting) && tries < 50 {
            try? await Task.sleep(nanoseconds: 100_000_000)
            syncStatus()
            tries += 1
        }
    }

    // MARK: messages to the tunnel

    /// Sends a command to the running tunnel. Nil when it is not running or
    /// did not answer.
    @discardableResult
    func send(_ command: TunnelCommand) async -> Data? {
        #if os(macOS)
        if Self.usesSystemProxy {
            guard status == .connected else { return nil }
            return await proxyMode.handle(command)
        }
        #endif
        return await sendToTunnel(command.data)
    }

    private func sendToTunnel(_ message: Data) async -> Data? {
        guard status == .connected, let session = manager?.connection as? NETunnelProviderSession else { return nil }
        return await withCheckedContinuation { continuation in
            do {
                try session.sendProviderMessage(message) { continuation.resume(returning: $0) }
            } catch {
                continuation.resume(returning: nil)
            }
        }
    }

    /// Restarts the core with the new config.json, or with the state handed
    /// over. Returns the error text, if any.
    func reload() async -> String? {
        let reply: Data?
        if !Self.usesSystemProxy, let message = try? handoff?()?.message() {
            reply = await sendToTunnel(message)
        } else {
            reply = await send(.reload)
        }
        guard let reply else { return nil }
        return reply.isEmpty ? nil : String(decoding: reply, as: UTF8.self)
    }

    /// Asks the tunnel for speed and totals. Called once a second by a
    /// visible screen only.
    func pollTraffic() async {
        guard let data = await send(.status),
              let value = try? JSONDecoder().decode(TunnelStatus.self, from: data)
        else { return }
        traffic = value
    }

    func logs() async -> [String] {
        guard let data = await send(.logs) else { return [] }
        return (try? JSONDecoder().decode([String].self, from: data)) ?? []
    }
}

struct VPNError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
