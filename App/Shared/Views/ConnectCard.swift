import LiteKit
import SwiftUI

struct ConnectCard: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var vpn: VPNController

    var body: some View {
        VStack(spacing: 14) {
            Button {
                Task { await model.toggleConnection() }
            } label: {
                ZStack {
                    Circle()
                        .fill(tint.opacity(vpn.isActive ? 0.18 : 0.08))
                    Circle()
                        .strokeBorder(tint, lineWidth: 3)
                    Image(systemName: "power")
                        .font(.system(size: 38, weight: .semibold))
                        .foregroundStyle(tint)
                }
                .frame(width: 112, height: 112)
                .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .disabled(vpn.status == .disconnecting)
            .accessibilityLabel(vpn.isActive ? "Отключить VPN" : "Подключить VPN")
            .keyboardShortcut(.return, modifiers: .command)

            VStack(spacing: 4) {
                Text(vpn.statusText)
                    .font(.title3.weight(.semibold))
                if let since = vpn.connectedSince {
                    TimelineView(.periodic(from: since, by: 1)) { context in
                        Text(Format.duration(since: since, now: context.date))
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            }

            if let server = model.state.selectedServer {
                VStack(spacing: 2) {
                    Text(server.name)
                        .font(.callout)
                        .lineLimit(1)
                    Text(Self.detail(of: server, picked: vpn.status == .connected ? vpn.traffic.selected : nil))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("Сервер не выбран")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if vpn.status == .connected {
                TrafficView(traffic: vpn.traffic)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 12)
        .trafficPolling()
    }

    /// Under the server's name. A connected group says which member it runs on.
    private static func detail(of server: Server, picked: String?) -> String {
        guard server.isGroup else { return server.proto.title }
        if let picked, let member = server.member(tagged: picked) { return "Сейчас: \(member.name)" }
        return server.detail
    }

    private var tint: Color {
        switch vpn.status {
        case .connected: return .green
        case .connecting, .reasserting, .disconnecting: return .orange
        default: return .secondary
        }
    }
}

struct TrafficView: View {
    let traffic: TunnelStatus

    var body: some View {
        HStack(spacing: 20) {
            stat("arrow.up", Format.speed(traffic.uplink), Format.size(traffic.uplinkTotal))
            stat("arrow.down", Format.speed(traffic.downlink), Format.size(traffic.downlinkTotal))
        }
        .font(.caption.monospacedDigit())
    }

    private func stat(_ icon: String, _ speed: String, _ total: String) -> some View {
        VStack(spacing: 2) {
            Label(speed, systemImage: icon)
            Text(total).foregroundStyle(.secondary)
        }
        .frame(minWidth: 110)
    }
}
