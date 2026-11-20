import AppIntents
import WidgetKit

/// What the widgets show and the app reloads them by.
enum WidgetKind {
    static let status = "app.litetunnel.widget.status"
    static let control = "app.litetunnel.control.vpn"
    static let open = "app.litetunnel.control.open"
}

/// Connects or disconnects without opening the app.
///
/// A live-activity intent runs in the app's process, not the widget's. Only
/// the app may save the VPN profile: after another VPN app took the switch,
/// or to clear the On Demand that 0.1.10 and 0.1.11 left on.
@available(iOS 17.0, *)
struct ToggleTunnelIntent: SetValueIntent, LiveActivityIntent {
    static let title: LocalizedStringResource = "Подключить Link"
    static let description = IntentDescription("Подключает или отключает VPN.")

    @Parameter(title: "Подключено")
    var value: Bool

    init() {}

    init(value: Bool) {
        self.value = value
    }

    func perform() async throws -> some IntentResult {
        try await TunnelSwitch.set(on: value)
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.status)
        if #available(iOS 18.0, *) {
            ControlCenter.shared.reloadControls(ofKind: WidgetKind.control)
        }
        return .result()
    }
}
