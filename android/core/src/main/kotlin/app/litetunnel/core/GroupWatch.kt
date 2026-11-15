package app.litetunnel.core

import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow

/**
 * Keeps an auto-select group on a server that answers, with the app closed.
 * Ported from Packages/LiteKit/Sources/LiteKit/GroupWatch.swift.
 *
 * The core tests the group on its own only every few minutes, and not at all
 * while the device dozes; a server that died in between stays picked. The
 * service therefore listens to the core's group reports (they come only when
 * a test result changes, nothing polls) and asks for a full test as soon as
 * the picked server has no working result: its test failed, or a connection
 * through it did. While the screen is on it also checks the picked server
 * alone, one request every [PROBE_INTERVAL], which catches a server that
 * still accepts connections but passes no traffic.
 *
 * A working server is enough: the core moves off one only when another is
 * clearly faster ([ServerGroup.TOLERANCE]). Full tests that leave the pick
 * without an answer, as with no network at all, are retried further and
 * further apart, up to [MAX_RETRY].
 *
 * Times are seconds on any clock that only goes forward.
 */
class GroupWatch {
    private var lastTest = Double.NEGATIVE_INFINITY
    private var lastProbe = Double.NEGATIVE_INFINITY

    /** Full tests in a row after which the pick still had no answer. */
    private var misses = 0

    /** Whether the last report had a working result for the pick. */
    var answered = true
        private set

    /** Seconds a failed pick waits before the next full test. */
    val retryDelay: Double get() = min(FIRST_RETRY * 2.0.pow(misses), MAX_RETRY)

    /** The core reported the group. True when it should be tested in full now. */
    fun reported(answered: Boolean, now: Double): Boolean {
        this.answered = answered
        if (answered) {
            misses = 0
            return false
        }
        return retryDue(now)
    }

    /** A retry timer fired. True when the pick still has no answer and the group should be tested again. */
    fun retryDue(now: Double): Boolean {
        if (answered || now - lastTest < retryDelay) return false
        lastTest = now
        misses++
        return true
    }

    /** The group was tested in full for another reason: a start, the app. */
    fun tested(now: Double) {
        lastTest = now
    }

    /** True when the picked server should be checked now, [gap] seconds or more after the last check or full test. */
    fun probeDue(now: Double, gap: Double): Boolean {
        if (now - max(lastProbe, lastTest) < gap) return false
        lastProbe = now
        return true
    }

    /** The core restarted with another config: nothing known carries over. */
    fun reset() {
        lastTest = Double.NEGATIVE_INFINITY
        lastProbe = Double.NEGATIVE_INFINITY
        misses = 0
        answered = true
    }

    companion object {
        /** One request through the picked server, while the screen is on. */
        const val PROBE_INTERVAL = 120.0

        /** The screen just came on: check the pick unless it was checked this recently. */
        const val WAKE_GAP = 60.0
        private const val FIRST_RETRY = 30.0
        private const val MAX_RETRY = 600.0
    }
}
