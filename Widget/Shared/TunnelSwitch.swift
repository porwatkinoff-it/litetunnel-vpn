import Foundation
import LiteKit
import NetworkExtension
import os

/// The app's VPN profile seen from outside the app: Control Center and the
/// widgets. A start from here carries no options; the tunnel runs what the
/// app gave it last, as it does for a start the system makes.
enum TunnelSwitch {
    private static let log = Logger(subsystem: "LiteTunnel", category: "switch")

    struct State: Sendable {
        var status: NEVPNStatus = .invalid
        var since: Date?
        /// The selected server, when the App Group shares the app's state.
        var server: String?

        var isOn: Bool {
            switch status {
            case .connecting, .connected, .reasserting: return true
            default: return false
            }
        }

        var text: String {
            switch status {
            case .connected: return "Подключено"
            case .connecting: return "Подключение…"
            case .reasserting: return "Переподключение…"
            case .disconnecting: return "Отключение…"
            default: return "Отключено"
            }
        }
    }

    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    /// Only this app's profiles are listed; it saves one.
    static func manager() async -> NETunnelProviderManager? {
        let managers = (try? await NETunnelProviderManager.loadAllFromPreferences()) ?? []
        return managers.first { $0.protocolConfiguration is NETunnelProviderProtocol }
    }

    static func state() async -> State {
        var state = State()
        if let connection = await manager()?.connection {
            state.status = connection.status
            state.since = connection.status == .connected ? connection.connectedDate : nil
        }
        state.server = SharedStore.openGroup()?.store.loadState().selectedServer?.name
        return state
    }

    static func set(on: Bool) async throws {
        guard let manager = await manager() else {
            throw Failure(message: "Откройте Link и подключитесь один раз: так появится профиль VPN.")
        }
        let before = manager.connection.status
        if on {
            // Another VPN app may have taken the system's switch. Rules are
            // there only while the app's "reconnect" setting is on.
            let reconnect = !(manager.onDemandRules ?? []).isEmpty
            if !manager.isEnabled || manager.isOnDemandEnabled != reconnect {
                manager.isEnabled = true
                manager.isOnDemandEnabled = reconnect
                try await manager.saveToPreferences()
                try await manager.loadFromPreferences()
            }
            do {
                try manager.connection.startVPNTunnel()
            } catch {
                throw Failure(message: "Не удалось подключиться. Откройте Link.")
            }
        } else {
            // On Demand first, or the system starts the tunnel again at once.
            var saved = true
            if manager.isOnDemandEnabled {
                manager.isOnDemandEnabled = false
                do {
                    try await manager.saveToPreferences()
                } catch {
                    saved = false
                    log.error("off: could not turn reconnect off: \(error.localizedDescription, privacy: .public)")
                }
            }
            manager.connection.stopVPNTunnel()
            if !saved {
                throw Failure(message: "Не удалось отключить переподключение. Откройте Link и отключитесь там.")
            }
        }
        // Let the status move, so the control and the widget redraw with it.
        for _ in 0..<10 where manager.connection.status == before {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }
}
