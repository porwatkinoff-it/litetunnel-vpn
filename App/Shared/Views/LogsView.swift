import LiteKit
import SwiftUI

/// The core's latest log lines, read from the tunnel every two seconds while
/// this screen is open.
struct LogsView: View {
    @EnvironmentObject private var vpn: VPNController
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var lines: [String] = []

    var body: some View {
        Group {
            if vpn.status != .connected && lines.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "text.alignleft")
                        .font(.largeTitle)
                        .foregroundStyle(.secondary)
                    Text("Журнал виден, пока VPN включён")
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 4) {
                            ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                                Text(line)
                                    .font(.caption.monospaced())
                                    .foregroundStyle(color(for: line))
                                    .textSelection(.enabled)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .id(index)
                            }
                        }
                        .padding()
                    }
                    .onChange(of: lines.count) { count in
                        if count > 0 { proxy.scrollTo(count - 1, anchor: .bottom) }
                    }
                }
            }
        }
        .navigationTitle("Журнал")
        .toolbar {
            ToolbarItem {
                Button {
                    Clipboard.copy(lines.joined(separator: "\n"))
                    model.show("Журнал скопирован")
                } label: {
                    Label("Копировать", systemImage: "doc.on.doc")
                }
                .disabled(lines.isEmpty)
            }
        }
        .task(id: vpn.status == .connected && scenePhase == .active) {
            guard vpn.status == .connected, scenePhase == .active else { return }
            while !Task.isCancelled {
                lines = await vpn.logs()
                try? await Task.sleep(nanoseconds: 2_000_000_000)
            }
        }
    }

    private func color(for line: String) -> Color {
        let lower = line.lowercased()
        if lower.contains("error") || lower.contains("fatal") { return .red }
        if lower.contains("warn") { return .orange }
        return .primary
    }
}
