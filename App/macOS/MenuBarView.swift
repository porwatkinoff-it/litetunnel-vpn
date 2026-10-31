import AppKit
import LiteKit
import SwiftUI

enum MainWindow {
    static let id = "main"
}

/// The menu bar panel: connect, pick a server, see traffic.
struct MenuBarView: View {
    @EnvironmentObject private var vpn: VPNController
    @EnvironmentObject private var model: AppModel
    @Environment(\.openWindow) private var openWindow

    private var selection: Binding<UUID?> {
        Binding(
            get: { model.state.selectedServerID },
            set: { if let id = $0 { model.select(id) } }
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Circle()
                    .fill(vpn.status == .connected ? Color.green : vpn.isActive ? Color.orange : Color.secondary)
                    .frame(width: 8, height: 8)
                VStack(alignment: .leading, spacing: 2) {
                    Text(vpn.statusText).font(.headline)
                    if let since = vpn.connectedSince, vpn.status == .connected {
                        TimelineView(.periodic(from: since, by: 1)) { context in
                            Text(Format.duration(since: since, now: context.date))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                Spacer()
                Toggle("", isOn: Binding(
                    get: { vpn.isActive },
                    set: { _ in Task { await model.toggleConnection() } }
                ))
                .toggleStyle(.switch)
                .labelsHidden()
                .disabled(model.state.selectedServerID == nil && !vpn.isActive)
            }

            if model.state.servers.isEmpty {
                Text("Нет серверов. Добавьте подписку в окне приложения.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                Picker("Сервер", selection: selection) {
                    ForEach(model.state.subscriptions) { sub in
                        Section(sub.name) {
                            ForEach(model.state.sorted(model.state.servers(in: sub.id))) { server in
                                Text(server.name).tag(UUID?.some(server.id))
                            }
                        }
                    }
                    let manual = model.state.servers(in: nil)
                    if !manual.isEmpty {
                        Section("Мои серверы") {
                            ForEach(model.state.sorted(manual)) { server in
                                Text(server.name).tag(UUID?.some(server.id))
                            }
                        }
                    }
                }
                .labelsHidden()
            }

            if vpn.status == .connected {
                TrafficView(traffic: vpn.traffic)
            }

            Divider()

            HStack {
                Button("Открыть окно") {
                    openWindow(id: MainWindow.id)
                    NSApp.activate(ignoringOtherApps: true)
                }
                Spacer()
                Button("Выйти") { NSApp.terminate(nil) }
            }
        }
        .padding()
        .frame(width: 300)
        .trafficPolling()
    }
}
