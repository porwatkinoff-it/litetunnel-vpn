import Foundation

/// Keeps an auto-select group on a server that answers, with the app closed.
///
/// The core tests the group on its own only every few minutes, and not at
/// all while the device sleeps; a server that died in between stays picked.
/// The tunnel therefore listens to the core's group reports (they come only
/// when a test result changes, nothing polls) and asks for a full test as
/// soon as the picked server has no working result: its test failed, or a
/// connection through it did. While the screen is on it also checks the
/// picked server alone, one request every `probeInterval`, which catches a
/// server that still accepts connections but passes no traffic.
///
/// A working server is enough: the core moves off one only when another is
/// clearly faster (`ServerGroup.tolerance`). Full tests that leave the pick
/// without an answer, as with no network at all, are retried further and
/// further apart, up to `maxRetry`.
///
/// Times are seconds on any clock that only goes forward.
public struct GroupWatch: Sendable {
    /// One request through the picked server, while the screen is on.
    public static let probeInterval: TimeInterval = 120
    /// The screen just came on: check the pick unless it was checked this recently.
    public static let wakeGap: TimeInterval = 60
    static let firstRetry: TimeInterval = 30
    static let maxRetry: TimeInterval = 600

    private var lastTest: TimeInterval = -.infinity
    private var lastProbe: TimeInterval = -.infinity
    /// Full tests in a row after which the pick still had no answer.
    private var misses = 0
    /// Whether the last report had a working result for the pick.
    public private(set) var answered = true

    public init() {}

    /// Seconds a failed pick waits before the next full test.
    public var retryDelay: TimeInterval {
        min(Self.firstRetry * pow(2, Double(misses)), Self.maxRetry)
    }

    /// The core reported the group. True when it should be tested in full now.
    public mutating func reported(answered: Bool, now: TimeInterval) -> Bool {
        self.answered = answered
        if answered {
            misses = 0
            return false
        }
        return retryDue(now: now)
    }

    /// A retry timer fired. True when the pick still has no answer and the
    /// group should be tested again.
    public mutating func retryDue(now: TimeInterval) -> Bool {
        guard !answered, now - lastTest >= retryDelay else { return false }
        lastTest = now
        misses += 1
        return true
    }

    /// The group was tested in full for another reason: a start, the app.
    public mutating func tested(now: TimeInterval) {
        lastTest = now
    }

    /// True when the picked server should be checked now, `gap` seconds or
    /// more after the last check or full test.
    public mutating func probeDue(now: TimeInterval, gap: TimeInterval) -> Bool {
        guard now - max(lastProbe, lastTest) >= gap else { return false }
        lastProbe = now
        return true
    }

    /// The core restarted with another config: nothing known carries over.
    public mutating func reset() {
        self = GroupWatch()
    }
}
