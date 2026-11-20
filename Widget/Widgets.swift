import AppIntents
import NetworkExtension
import SwiftUI
import WidgetKit

@main
struct LiteTunnelWidgets: WidgetBundle {
    var body: some Widget {
        StatusWidget()
        if #available(iOS 18.0, *) {
            TunnelControl()
            OpenAppControl()
        }
    }
}

// MARK: Control Center

@available(iOS 18.0, *)
struct TunnelControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: WidgetKind.control, provider: Provider()) { isOn in
            ControlWidgetToggle("Link", isOn: isOn, action: ToggleTunnelIntent()) { on in
                Label(on ? "Подключено" : "Отключено", systemImage: "power")
            }
            .tint(WidgetColors.connected)
        }
        .displayName("Link")
        .description("Подключает и отключает VPN.")
    }

    struct Provider: ControlValueProvider {
        var previewValue: Bool { false }

        func currentValue() async throws -> Bool {
            await TunnelSwitch.state().isOn
        }
    }
}

@available(iOS 18.0, *)
struct OpenAppControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: WidgetKind.open) {
            ControlWidgetButton(action: OpenAppIntent()) {
                Label("Link", image: "logo")
            }
        }
        .displayName("Открыть Link")
        .description("Открывает приложение.")
    }
}

// MARK: Home and Lock Screen

struct StatusEntry: TimelineEntry {
    var date = Date()
    var state = TunnelSwitch.State()
}

struct StatusProvider: TimelineProvider {
    func placeholder(in context: Context) -> StatusEntry {
        StatusEntry()
    }

    func getSnapshot(in context: Context, completion: @escaping (StatusEntry) -> Void) {
        Task { completion(StatusEntry(state: await TunnelSwitch.state())) }
    }

    /// No refresh on a schedule: the app, the control and the widget's own
    /// button reload it when the status changes.
    func getTimeline(in context: Context, completion: @escaping (Timeline<StatusEntry>) -> Void) {
        Task { completion(Timeline(entries: [StatusEntry(state: await TunnelSwitch.state())], policy: .never)) }
    }
}

struct StatusWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WidgetKind.status, provider: StatusProvider()) { entry in
            StatusWidgetView(entry: entry)
                .widgetURL(URL(string: "litetunnel://open"))
        }
        .configurationDisplayName("Link")
        .description("Состояние VPN. Кнопка подключает, нажатие на виджет открывает приложение.")
        .supportedFamilies([.systemSmall, .accessoryCircular, .accessoryRectangular, .accessoryInline])
    }
}

enum WidgetColors {
    static let connected = Color(light: 0x34C759, dark: 0x30D158)
    static let busy = Color(light: 0xFF9500, dark: 0xFF9F0A)
    static let secondary = Color(light: 0x6C6C70, dark: 0x8E8E93)

    static func status(_ status: NEVPNStatus) -> Color {
        switch status {
        case .connected: return connected
        case .connecting, .reasserting, .disconnecting: return busy
        default: return secondary
        }
    }
}

struct StatusWidgetView: View {
    let entry: StatusEntry
    @Environment(\.widgetFamily) private var family

    private var state: TunnelSwitch.State { entry.state }

    var body: some View {
        switch family {
        case .accessoryCircular:
            ZStack {
                AccessoryWidgetBackground()
                Image(systemName: "power")
                    .font(.system(size: 22, weight: .semibold))
                    .opacity(state.isOn ? 1 : 0.5)
            }
            .widgetBackground()
        case .accessoryRectangular:
            VStack(alignment: .leading, spacing: 2) {
                Label("Link", image: "logo")
                    .font(.headline)
                Text(state.text)
                if let since = state.since {
                    Text(since, style: .timer)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .widgetBackground()
        case .accessoryInline:
            Label(state.text, systemImage: "power")
                .widgetBackground()
        default:
            small
        }
    }

    private var small: some View {
        let color = WidgetColors.status(state.status)
        return VStack(spacing: 6) {
            powerButton(color: color)
            Text(state.text)
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
            if let since = state.since {
                Text(since, style: .timer)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            } else if let server = state.server {
                Text(server)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .widgetBackground()
    }

    @ViewBuilder
    private func powerButton(color: Color) -> some View {
        let circle = ZStack {
            Circle().fill(color.opacity(state.isOn ? 0.18 : 0.08))
            Circle().strokeBorder(color, lineWidth: 2.5)
            Image(systemName: "power")
                .font(.system(size: 26, weight: .semibold))
                .foregroundStyle(color)
        }
        .frame(width: 64, height: 64)
        if #available(iOS 17.0, *) {
            Button(intent: ToggleTunnelIntent(value: !state.isOn)) { circle }
                .buttonStyle(.plain)
                .disabled(state.status == .disconnecting)
        } else {
            circle
        }
    }
}

extension View {
    /// iOS 17 asks every widget for a container background.
    @ViewBuilder
    func widgetBackground() -> some View {
        if #available(iOS 17.0, *) {
            containerBackground(.fill.tertiary, for: .widget)
        } else {
            self
        }
    }
}

extension Color {
    init(light: UInt32, dark: UInt32) {
        func rgb(_ v: UInt32) -> UIColor {
            UIColor(red: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: 1)
        }
        self.init(uiColor: UIColor { $0.userInterfaceStyle == .dark ? rgb(dark) : rgb(light) })
    }
}
