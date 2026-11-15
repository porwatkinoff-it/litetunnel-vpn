import XCTest
@testable import LiteKit

final class GroupWatchTests: XCTestCase {
    func testWorkingPickNeedsNoTest() {
        var watch = GroupWatch()
        XCTAssertFalse(watch.reported(answered: true, now: 0))
        XCTAssertFalse(watch.retryDue(now: 1000))
    }

    func testDeadPickIsTestedAtOnce() {
        var watch = GroupWatch()
        watch.tested(now: 0)
        XCTAssertTrue(watch.reported(answered: false, now: 40))
        // Reports during that test change nothing.
        XCTAssertFalse(watch.reported(answered: false, now: 41))
    }

    func testFailedTestsBackOff() {
        var watch = GroupWatch()
        XCTAssertTrue(watch.reported(answered: false, now: 0))
        XCTAssertEqual(watch.retryDelay, 60)
        XCTAssertFalse(watch.retryDue(now: 59))
        XCTAssertTrue(watch.retryDue(now: 60))
        XCTAssertEqual(watch.retryDelay, 120)
        var now: TimeInterval = 60
        for _ in 0..<10 {
            now += watch.retryDelay
            XCTAssertTrue(watch.retryDue(now: now))
        }
        XCTAssertEqual(watch.retryDelay, 600, "never more than ten minutes apart")
    }

    func testAnswerEndsTheBackOff() {
        var watch = GroupWatch()
        _ = watch.reported(answered: false, now: 0)
        _ = watch.retryDue(now: 60)
        XCTAssertFalse(watch.reported(answered: true, now: 70))
        XCTAssertEqual(watch.retryDelay, 30)
        XCTAssertFalse(watch.retryDue(now: 500), "nothing to retry while the pick answers")
        XCTAssertTrue(watch.reported(answered: false, now: 500))
    }

    func testProbeKeepsItsGap() {
        var watch = GroupWatch()
        XCTAssertTrue(watch.probeDue(now: 0, gap: GroupWatch.wakeGap))
        XCTAssertFalse(watch.probeDue(now: 30, gap: GroupWatch.wakeGap))
        XCTAssertTrue(watch.probeDue(now: 60, gap: GroupWatch.wakeGap))
        watch.tested(now: 100)
        XCTAssertFalse(watch.probeDue(now: 150, gap: GroupWatch.wakeGap), "a full test counts as a check")
        XCTAssertTrue(watch.probeDue(now: 220, gap: GroupWatch.probeInterval))
    }

    func testResetForgets() {
        var watch = GroupWatch()
        _ = watch.reported(answered: false, now: 0)
        watch.reset()
        XCTAssertTrue(watch.answered)
        XCTAssertTrue(watch.reported(answered: false, now: 1))
    }
}
