import Foundation

/// Messages the app sends to the tunnel with `sendProviderMessage`.
///
/// The app never links the sing-box core: the Go runtime starts its own
/// threads on load, and the app has no use for them. Speed and logs come
/// from the tunnel instead, and only while a screen is asking for them.
public enum TunnelCommand: String, Sendable {
    /// Re-read config.json and restart the core in place. Reply: empty on
    /// success, the error text otherwise.
    case reload
    /// Reply: `TunnelStatus` as JSON.
    case status
    /// Reply: `[String]`, the latest log lines, as JSON.
    case logs
    /// The app came to the foreground: end the core's sleep pause now.
    case wake

    public var data: Data { Data(rawValue.utf8) }

    public init?(data: Data) {
        self.init(rawValue: String(decoding: data, as: UTF8.self))
    }
}

public struct TunnelStatus: Codable, Hashable, Sendable {
    /// Bytes per second.
    public var uplink: Int64 = 0
    public var downlink: Int64 = 0
    public var uplinkTotal: Int64 = 0
    public var downlinkTotal: Int64 = 0
    /// Bytes used by the core.
    public var memory: Int64 = 0
    public var connections: Int32 = 0

    public init() {}
}
