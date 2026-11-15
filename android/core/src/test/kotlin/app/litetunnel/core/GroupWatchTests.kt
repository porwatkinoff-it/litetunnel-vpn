package app.litetunnel.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Ported from Packages/LiteKit/Tests/LiteKitTests/GroupWatchTests.swift. */
class GroupWatchTests {
    @Test
    fun workingPickNeedsNoTest() {
        val watch = GroupWatch()
        assertFalse(watch.reported(answered = true, now = 0.0))
        assertFalse(watch.retryDue(now = 1000.0))
    }

    @Test
    fun deadPickIsTestedAtOnce() {
        val watch = GroupWatch()
        watch.tested(now = 0.0)
        assertTrue(watch.reported(answered = false, now = 40.0))
        // Reports during that test change nothing.
        assertFalse(watch.reported(answered = false, now = 41.0))
    }

    @Test
    fun failedTestsBackOff() {
        val watch = GroupWatch()
        assertTrue(watch.reported(answered = false, now = 0.0))
        assertEquals(60.0, watch.retryDelay, 0.0)
        assertFalse(watch.retryDue(now = 59.0))
        assertTrue(watch.retryDue(now = 60.0))
        assertEquals(120.0, watch.retryDelay, 0.0)
        var now = 60.0
        repeat(10) {
            now += watch.retryDelay
            assertTrue(watch.retryDue(now))
        }
        assertEquals("never more than ten minutes apart", 600.0, watch.retryDelay, 0.0)
    }

    @Test
    fun answerEndsTheBackOff() {
        val watch = GroupWatch()
        watch.reported(answered = false, now = 0.0)
        watch.retryDue(now = 60.0)
        assertFalse(watch.reported(answered = true, now = 70.0))
        assertEquals(30.0, watch.retryDelay, 0.0)
        assertFalse("nothing to retry while the pick answers", watch.retryDue(now = 500.0))
        assertTrue(watch.reported(answered = false, now = 500.0))
    }

    @Test
    fun probeKeepsItsGap() {
        val watch = GroupWatch()
        assertTrue(watch.probeDue(now = 0.0, gap = GroupWatch.WAKE_GAP))
        assertFalse(watch.probeDue(now = 30.0, gap = GroupWatch.WAKE_GAP))
        assertTrue(watch.probeDue(now = 60.0, gap = GroupWatch.WAKE_GAP))
        watch.tested(now = 100.0)
        assertFalse("a full test counts as a check", watch.probeDue(now = 150.0, gap = GroupWatch.WAKE_GAP))
        assertTrue(watch.probeDue(now = 220.0, gap = GroupWatch.PROBE_INTERVAL))
    }

    @Test
    fun resetForgets() {
        val watch = GroupWatch()
        watch.reported(answered = false, now = 0.0)
        watch.reset()
        assertTrue(watch.answered)
        assertTrue(watch.reported(answered = false, now = 1.0))
    }
}
