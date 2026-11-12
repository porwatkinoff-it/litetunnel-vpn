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
    /// Whether the running config is an auto-select group.
    private var isGroup = false
    private var selected: String?
    private var lastGroupTest = DispatchTime(uptimeNanoseconds: 0)
    private var groupGeneration = 0

    func status() -> TunnelStatus {
        queue.sync {
            touch()
            var status = latest
            status.selected = isGroup ? selected : nil
            return status
        }
    }

    func logs() -> [String] {
        queue.sync {
            touch()
            return lines
        }
    }

    /// The core started or reloaded. An auto-select group is tested again a
    /// little later: the core's own first test can run before the network is
    /// up, fail for every member and leave the first one picked until the
    /// next test.
    func groupChanged(isGroup: Bool) {
        queue.sync {
            self.isGroup = isGroup
            selected = nil
            groupGeneration += 1
            guard isGroup else { return }
            let generation = groupGeneration
            for delay in [3, 20] {
                queue.asyncAfter(deadline: .now() + .seconds(delay)) { [weak self] in
                    guard let self, self.groupGeneration == generation else { return }
                    self.testGroup()
                }
            }
        }
    }

    /// The app opened: test the group unless it was tested in the last minute.
    func refreshGroup() {
        queue.sync {
            if .now() > lastGroupTest + .seconds(60) { testGroup() }
        }
    }

    func stop() {
        queue.sync { disconnect() }
    }

    // MARK: on `queue`

    private func testGroup() {
        guard isGroup else { return }
        lastGroupTest = .now()
        DispatchQueue.global(qos: .utility).async {
            do {
                try LibboxNewStandaloneCommandClient()?.urlTest(ConfigBuilder.proxyTag)
            } catch {
                Self.log.error("group test: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    private func touch() {
        lastRequest = .now()
        guard client == nil else { return }
        let options = LibboxCommandClientOptions()
        options.addCommand(LibboxCommandStatus)
        options.addCommand(LibboxCommandLog)
        options.addCommand(LibboxCommandGroup)
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
    func writeGroups(_ message: LibboxOutboundGroupIteratorProtocol?) {
        guard let message else { return }
        var picked: String?
        while message.hasNext() {
            if let group = message.next(), group.tag == ConfigBuilder.proxyTag { picked = group.selected }
        }
        queue.async { self.selected = picked?.isEmpty == false ? picked : nil }
    }
    func writeOutbounds(_ message: LibboxOutboundGroupItemIteratorProtocol?) {}
}
