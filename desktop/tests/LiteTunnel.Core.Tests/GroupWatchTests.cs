using LiteTunnel.Core;
using Xunit;

namespace LiteTunnel.Core.Tests;

public class GroupWatchTests
{
    [Fact]
    public void WorkingPickNeedsNoTest()
    {
        var watch = new GroupWatch();
        Assert.False(watch.Reported(answered: true, now: 0));
        Assert.False(watch.RetryDue(1000));
    }

    [Fact]
    public void DeadPickIsTestedAtOnce()
    {
        var watch = new GroupWatch();
        watch.Tested(0);
        Assert.True(watch.Reported(answered: false, now: 40));
        // Checks during that test change nothing.
        Assert.False(watch.Reported(answered: false, now: 41));
    }

    [Fact]
    public void FailedTestsBackOff()
    {
        var watch = new GroupWatch();
        Assert.True(watch.Reported(answered: false, now: 0));
        Assert.Equal(60, watch.RetryDelay);
        Assert.False(watch.RetryDue(59));
        Assert.True(watch.RetryDue(60));
        Assert.Equal(120, watch.RetryDelay);
        double now = 60;
        for (int i = 0; i < 10; i++)
        {
            now += watch.RetryDelay;
            Assert.True(watch.RetryDue(now));
        }
        Assert.Equal(600, watch.RetryDelay);
    }

    [Fact]
    public void AnswerEndsTheBackOff()
    {
        var watch = new GroupWatch();
        watch.Reported(answered: false, now: 0);
        watch.RetryDue(60);
        Assert.False(watch.Reported(answered: true, now: 70));
        Assert.Equal(30, watch.RetryDelay);
        Assert.False(watch.RetryDue(500));
        Assert.True(watch.Reported(answered: false, now: 500));
    }

    [Fact]
    public void ProbeKeepsItsGap()
    {
        var watch = new GroupWatch();
        Assert.True(watch.ProbeDue(0, GroupWatch.WakeGap));
        Assert.False(watch.ProbeDue(30, GroupWatch.WakeGap));
        Assert.True(watch.ProbeDue(60, GroupWatch.WakeGap));
        watch.Tested(100);
        Assert.False(watch.ProbeDue(150, GroupWatch.WakeGap));
        Assert.True(watch.ProbeDue(220, GroupWatch.ProbeInterval));
    }
}
