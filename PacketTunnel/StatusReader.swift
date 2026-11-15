import Foundation
import Libbox
import LiteKit
import os

/// Reads speed and logs from the core for the app, and keeps an auto-select
/// group on a working server (`GroupWatch`).
///
/// The core's status stream wakes a goroutine every second, so it runs only
/// while the app keeps asking: the first request opens it, and it closes
/// itself after a few quiet seconds. With the app closed, nothing ticks but
/// the group check, one request every few minutes while the screen is on.
/// The group reports themselves cost nothing between test results.
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
    /// The member the core picked, as its tag ("proxy-3").
    private var selected: String?
    private var groupGeneration = 0
    private var watch = GroupWatch()
    private var groups: GroupListener?
    private var screenOn = true
    private var probeTimer: DispatchSourceTimer?

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
            watch.reset()
            guard isGroup else {
                stopWatching()
                return
            }
            startWatching()
            let generation = groupGeneration
            for delay in [3, 20] {
                queue.asyncAfter(deadline: .now() + .seconds(delay)) { [weak self] in
                    guard let self, self.groupGeneration == generation else { return }
                    self.testGroup()
                }
            }
        }
    }

    /// The app opened: test the group unless it was checked in the last minute.
    func refreshGroup() {
        queue.sync {
            if isGroup, watch.probeDue(now: Self.now, gap: GroupWatch.wakeGap) { testGroup() }
        }
    }

    /// The screen turned on or off (on a Mac: the Mac woke or went to sleep).
    /// With it on, the picked server is checked now and then; the first check
    /// waits a few seconds for the network to come back.
    func screen(on: Bool) {
        queue.async {
            self.screenOn = on
            self.probeTimer?.cancel()
            self.probeTimer = nil
            guard on, self.isGroup else { return }
            let generation = self.groupGeneration
            self.queue.asyncAfter(deadline: .now() + .seconds(3)) { [weak self] in
                guard let self, self.groupGeneration == generation, self.screenOn else { return }
                if self.watch.probeDue(now: Self.now, gap: GroupWatch.wakeGap) { self.probe() }
            }
            self.startProbeTimer()
        }
    }

    func stop() {
        queue.sync {
            disconnect()
            stopWatching()
        }
    }

    // MARK: on `queue`

    /// Seconds on a clock that keeps counting while the device sleeps.
    private static var now: TimeInterval {
        TimeInterval(clock_gettime_nsec_np(CLOCK_MONOTONIC)) / 1e9
    }

    private func testGroup() {
        guard isGroup else { return }
        watch.tested(now: Self.now)
        runGroupTest()
    }

    private func runGroupTest() {
        Self.urlTest(ConfigBuilder.proxyTag)
    }

    /// One request through the picked server. A failure drops its result,
    /// the core reports that, and `groupsReported` tests the whole group.
    private func probe() {
        guard isGroup, let selected else { return }
        Self.urlTest(selected)
    }

    private static func urlTest(_ tag: String) {
        DispatchQueue.global(qos: .utility).async {
            do {
                try LibboxNewStandaloneCommandClient()?.urlTest(tag)
            } catch {
                log.error("url test: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    private func groupsReported(picked: String?, delay: Int32) {
        guard isGroup else { return }
        selected = picked?.isEmpty == false ? picked : nil
        guard selected != nil else { return }
        if watch.reported(answered: delay > 0, now: Self.now) {
            runGroupTest()
            scheduleRetry()
        }
    }

    /// Tests the group again later while the pick still has no answer and
    /// the screen is on; with it off the next check comes when it turns on.
    private func scheduleRetry() {
        let generation = groupGeneration
        queue.asyncAfter(deadline: .now() + watch.retryDelay) { [weak self] in
            guard let self, self.groupGeneration == generation, self.screenOn else { return }
            if self.watch.retryDue(now: Self.now) {
                self.runGroupTest()
                self.scheduleRetry()
            }
        }
    }

    private func startProbeTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = Int(GroupWatch.probeInterval)
        timer.schedule(deadline: .now() + .seconds(interval), repeating: .seconds(interval), leeway: .seconds(10))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            // A probe timer may fire a little early.
            if self.watch.probeDue(now: Self.now, gap: GroupWatch.probeInterval - 15) { self.probe() }
        }
        timer.resume()
        probeTimer = timer
    }

    private func startWatching() {
        if groups == nil {
            groups = GroupListener(queue: queue) { [weak self] picked, delay in
                self?.groupsReported(picked: picked, delay: delay)
            }
        }
        if screenOn, probeTimer == nil { startProbeTimer() }
    }

    private func stopWatching() {
        groups?.close()
        groups = nil
        probeTimer?.cancel()
        probeTimer = nil
    }

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

/// The core's reports on the auto-select group: which member it picked and
/// whether that member has a working test result. The core sends one only
/// when a result changes. Reconnects if the core drops it.
private final class GroupListener: NSObject, LibboxCommandClientHandlerProtocol {
    private let queue: DispatchQueue
    private let onReport: (_ picked: String?, _ delay: Int32) -> Void
    private var client: LibboxCommandClient?
    private var closed = false

    init(queue: DispatchQueue, onReport: @escaping (String?, Int32) -> Void) {
        self.queue = queue
        self.onReport = onReport
        super.init()
        connect()
    }

    /// On `queue`.
    func close() {
        closed = true
        guard let client else { return }
        self.client = nil
        DispatchQueue.global(qos: .utility).async { try? client.disconnect() }
    }

    /// On `queue`.
    private func connect() {
        guard !closed, client == nil else { return }
        let options = LibboxCommandClientOptions()
        options.addCommand(LibboxCommandGroup)
        guard let client = LibboxNewCommandClient(self, options) else { return }
        self.client = client
        DispatchQueue.global(qos: .utility).async { [weak self] in
            do {
                try client.connect()
            } catch {
                self?.disconnected(error.localizedDescription)
            }
        }
    }

    func connected() {}

    func disconnected(_ message: String?) {
        queue.asyncAfter(deadline: .now() + .seconds(5)) { [weak self] in
            guard let self, !self.closed else { return }
            self.client = nil
            self.connect()
        }
    }

    func writeGroups(_ message: LibboxOutboundGroupIteratorProtocol?) {
        guard let message else { return }
        var picked: String?
        var delay: Int32 = 0
        while message.hasNext() {
            guard let group = message.next(), group.tag == ConfigBuilder.proxyTag else { continue }
            picked = group.selected
            if let items = group.getItems() {
                while items.hasNext() {
                    if let item = items.next(), item.tag == group.selected { delay = item.urlTestDelay }
                }
            }
        }
        queue.async { self.onReport(picked, delay) }
    }

    func writeStatus(_ message: LibboxStatusMessage?) {}
    func clearLogs() {}
    func writeLogs(_ messageList: LibboxLogIteratorProtocol?) {}
    func setDefaultLogLevel(_ level: Int32) {}
    func initializeClashMode(_ modeList: LibboxStringIteratorProtocol?, currentMode: String?) {}
    func updateClashMode(_ newMode: String?) {}
    func write(_ events: LibboxConnectionEvents?) {}
    func writeOutbounds(_ message: LibboxOutboundGroupItemIteratorProtocol?) {}
}
