import AppIntents

/// Brings Link to the front: the Control Center button and Shortcuts.
/// Built into the app as well as the widget extension, which is what lets
/// the system open the app for it.
@available(iOS 16.0, macOS 13.0, *)
struct OpenAppIntent: AppIntent {
    static let title: LocalizedStringResource = "Открыть Link"
    static let description = IntentDescription("Открывает приложение.")
    static let openAppWhenRun = true

    init() {}

    func perform() async throws -> some IntentResult {
        .result()
    }
}
