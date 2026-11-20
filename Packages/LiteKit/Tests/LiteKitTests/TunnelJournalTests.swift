import XCTest
@testable import LiteKit

final class TunnelJournalTests: XCTestCase {
    private var folder: URL!

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("journal-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    func testStartAndStopAreRecorded() {
        let journal = TunnelJournal(folder: folder)
        XCTAssertFalse(journal.started())
        journal.stopped(reason: 1)
        let lines = journal.lines(running: false)
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines[0].hasSuffix("VPN включён"))
        XCTAssertTrue(lines[1].contains("VPN выключен: вручную"))
    }

    func testRunWithoutStopIsShownAsCutOff() {
        let journal = TunnelJournal(folder: folder)
        journal.started()
        // The process was killed: no stop. The VPN is off now.
        let shown = journal.lines(running: false)
        XCTAssertEqual(shown.count, 2)
        XCTAssertTrue(shown[1].contains("VPN оборвался"))
        // While it runs, the marker is its own and means nothing.
        XCTAssertEqual(journal.lines(running: true).count, 1)
    }

    func testNextStartRecordsTheCutOff() {
        let journal = TunnelJournal(folder: folder)
        journal.started()
        XCTAssertTrue(journal.started())
        let lines = journal.lines(running: true)
        XCTAssertEqual(lines.count, 3)
        XCTAssertTrue(lines[1].contains("VPN оборвался"))
        XCTAssertTrue(lines[2].hasSuffix("VPN включён"))
    }

    func testFailedStartLeavesNoMarker() {
        let journal = TunnelJournal(folder: folder)
        journal.started()
        journal.failed("Сервер не выбран")
        let lines = journal.lines(running: false)
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines[1].contains("Не удалось включить: Сервер не выбран"))
    }

    func testOnlyTheLastLinesAreKept() {
        let journal = TunnelJournal(folder: folder)
        for index in 0..<(TunnelJournal.maxLines + 5) { journal.note("строка \(index)") }
        let lines = journal.lines(running: true)
        XCTAssertEqual(lines.count, TunnelJournal.maxLines)
        XCTAssertTrue(lines.last!.hasSuffix("строка \(TunnelJournal.maxLines + 4)"))
    }

    func testUnknownReasonIsNamedByNumber() {
        XCTAssertEqual(TunnelJournal.describe(99), "причина 99")
        XCTAssertEqual(TunnelJournal.describe(11), "включён другой VPN")
    }
}
