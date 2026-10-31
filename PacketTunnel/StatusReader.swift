import Foundation
import Libbox
import LiteKit
import os

/// Reads speed and logs from the core for the app.
///
/// The core's status stream wakes a goroutine every second, so it runs only
/// while the app keeps asking: the first request opens it, and it closes
/// itself after a few quiet seconds. With the app closed, nothing ticks.
final class StatusReader: NSObject, LibboxCommandClientHandlerProtocol {
    static let maxLogLines = 300
    private static let idleTimeout: DispatchTimeInterval = .seconds(5)
    private static let log = Logger(subsystem: "LiteTunnel", category: "status")

    private let queue = DispatchQueue(label: "litetunnel.status", qos: .utility)
    private var client: LibboxCommandClient?
    private var idleTimer: DispatchSourceTimer?
    private var lastRequest = DispatchTime.now()
    private var latest = TunnelStatus()
    private var lines: [String] = []

    func status() -> TunnelStatus {
        queue.sync {
            touch()
            return latest
        }
    }

    func logs() -> [String] {
        queue.sync {
            touch()
            return lines
        }
    }

    func stop() {
        queue.sync { disconnect() }
    }

    // MARK: on `queue`

    private func touch() {
        lastRequest = .now()
        guard client == nil else { return }
        let options = LibboxCommandClientOptions()
        options.addCommand(LibboxCommandStatus)
        options.addCommand(LibboxCommandLog)
        options.statusInterval = Int64(NSEC_PER_SEC)
        guard let client = LibboxNewCommandClient(self, options) else { return }
        self.client = client
        // Dialing retries until the server answers, so it stays off `queue`.
        DispatchQueue.global(qos: .utility).async {
            do {
                try client.connect()
            } catch {
                Self.log.error("status client: \(error.localizedDescription, privacy: .public)")
            }
        }

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.idleTimeout, repeating: Self.idleTimeout, leeway: .seconds(1))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if .now() > self.lastRequest + Self.idleTimeout { self.disconnect() }
        }
        timer.resume()
        idleTimer = timer
    }

    private func disconnect() {
        idleTimer?.cancel()
        idleTimer = nil
        guard let client else { return }
        self.client = nil
        // Totals stay for the next look; speeds would be stale.
        latest.uplink = 0
        latest.downlink = 0
        DispatchQueue.global(qos: .utility).async { try? client.disconnect() }
    }

    // MARK: LibboxCommandClientHandlerProtocol, called on core threads

    func connected() {}
    func disconnected(_ message: String?) {}

    func writeStatus(_ message: LibboxStatusMessage?) {
        guard let message else { return }
        var status = TunnelStatus()
        status.uplink = message.uplink
        status.downlink = message.downlink
        status.uplinkTotal = message.uplinkTotal
        status.downlinkTotal = message.downlinkTotal
        status.memory = message.memory
        status.connections = message.connectionsOut
        queue.async { self.latest = status }
    }

    func clearLogs() {
        queue.async { self.lines.removeAll() }
    }

    func writeLogs(_ messageList: LibboxLogIteratorProtocol?) {
        guard let messageList else { return }
        var batch: [String] = []
        while messageList.hasNext() {
            if let entry = messageList.next() { batch.append(Self.plain(entry.message)) }
        }
        queue.async {
            self.lines.append(contentsOf: batch)
            if self.lines.count > Self.maxLogLines {
                self.lines.removeFirst(self.lines.count - Self.maxLogLines)
            }
        }
    }

    /// Log lines without terminal colour codes.
    static func plain(_ line: String) -> String {
        guard line.contains("\u{1B}[") else { return line }
        return line.replacingOccurrences(of: "\u{1B}\\[[0-9;]*m", with: "", options: .regularExpression)
    }

    func setDefaultLogLevel(_ level: Int32) {}
    func initializeClashMode(_ modeList: LibboxStringIteratorProtocol?, currentMode: String?) {}
    func updateClashMode(_ newMode: String?) {}
    func write(_ events: LibboxConnectionEvents?) {}
    func writeGroups(_ message: LibboxOutboundGroupIteratorProtocol?) {}
    func writeOutbounds(_ message: LibboxOutboundGroupItemIteratorProtocol?) {}
}
