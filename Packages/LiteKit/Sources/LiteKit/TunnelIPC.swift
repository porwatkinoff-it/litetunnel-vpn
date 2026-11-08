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

/// The app's state and the rule-sets it downloaded, sent in the start
/// options and, for a reload, as a provider message. A tunnel that opened the
/// same App Group as the app ignores it and reads the group. One that did not
/// (some re-signing tools drop the group from the signature) has no folder in
/// common with the app: it keeps the handoff in its own folder, the last one
/// also for a start the system makes alone.
public struct TunnelHandoff: Codable, Sendable {
    public var state: AppState
    /// Tag to `.srs` file contents. About 70 KB for all three.
    public var ruleSets: [String: Data]
    /// The group the app keeps its state in; nil when it has none.
    public var appGroup: String?

    public init(state: AppState, ruleSets: [String: Data] = [:], appGroup: String? = nil) {
        self.state = state
        self.ruleSets = ruleSets
        self.appGroup = appGroup
    }

    /// The key in `startVPNTunnel(options:)`; the value is `encoded()` as NSData.
    public static let optionKey = "handoff"
    /// Starts a reload message, so it never parses as a `TunnelCommand`.
    static let messagePrefix = Data("handoff\n".utf8)

    public func encoded() throws -> Data {
        try SharedStore.encoder.encode(self)
    }

    public init(encoded data: Data) throws {
        self = try SharedStore.decoder.decode(Self.self, from: data)
    }

    /// A reload with this state. Reply as for `TunnelCommand.reload`.
    public func message() throws -> Data {
        Self.messagePrefix + (try encoded())
    }

    /// Nil when the message is something else.
    public init?(message data: Data) {
        guard data.starts(with: Self.messagePrefix),
              let value = try? Self(encoded: Data(data.dropFirst(Self.messagePrefix.count)))
        else { return nil }
        self = value
    }
}
