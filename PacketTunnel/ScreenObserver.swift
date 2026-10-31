#if os(iOS)
import Foundation
import notify

/// Tells when the iPhone screen turns on or off, through a Darwin notification
/// SpringBoard posts. Costs nothing while the screen stays as it is.
final class ScreenObserver {
    private static let name = "com.apple.springboard.hasBlankedScreen"
    private var token: Int32 = NOTIFY_TOKEN_INVALID

    init(_ onChange: @escaping (_ on: Bool) -> Void) {
        notify_register_dispatch(Self.name, &token, DispatchQueue.global(qos: .utility)) { token in
            onChange(Self.state(token) == 0)
        }
    }

    deinit {
        if token != NOTIFY_TOKEN_INVALID { notify_cancel(token) }
    }

    /// True when unknown, so a failed lookup never keeps the core paused.
    static var isScreenOn: Bool {
        var token: Int32 = NOTIFY_TOKEN_INVALID
        guard notify_register_check(name, &token) == NOTIFY_STATUS_OK else { return true }
        defer { notify_cancel(token) }
        return state(token) == 0
    }

    private static func state(_ token: Int32) -> UInt64 {
        var value: UInt64 = 0
        notify_get_state(token, &value)
        return value
    }
}
#endif
