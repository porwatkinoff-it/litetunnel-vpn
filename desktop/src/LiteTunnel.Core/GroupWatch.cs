namespace LiteTunnel.Core;

/// <summary>
/// Keeps an auto-select group on a server that answers, with the window closed.
/// </summary>
/// <remarks>
/// The core tests the group on its own only every few minutes, and not while
/// the computer sleeps; a server that died in between stays picked. The app
/// therefore checks the picked server alone, one request every
/// <see cref="ProbeInterval"/>, and has the core test the whole group as soon
/// as it does not answer. A working server is enough: the core moves off one
/// only when another is clearly faster (<see cref="ServerGroup.Tolerance"/>).
/// Full tests that leave the pick without an answer, as with no network at
/// all, are retried further and further apart, up to ten minutes.
/// Times are seconds on any clock that only goes forward.
/// </remarks>
public sealed class GroupWatch
{
    /// <summary>One request through the picked server.</summary>
    public const double ProbeInterval = 120;

    /// <summary>The window just came back: check the pick unless it was checked this recently.</summary>
    public const double WakeGap = 60;

    private const double FirstRetry = 30;
    private const double MaxRetry = 600;

    private double lastTest = double.NegativeInfinity;
    private double lastProbe = double.NegativeInfinity;

    /// <summary>Full tests in a row after which the pick still had no answer.</summary>
    private int misses;

    /// <summary>Whether the last check had a working result for the pick.</summary>
    public bool Answered { get; private set; } = true;

    /// <summary>Seconds a failed pick waits before the next full test.</summary>
    public double RetryDelay => Math.Min(FirstRetry * Math.Pow(2, misses), MaxRetry);

    /// <summary>The pick was checked. True when the group should be tested in full now.</summary>
    public bool Reported(bool answered, double now)
    {
        Answered = answered;
        if (answered)
        {
            misses = 0;
            return false;
        }
        return RetryDue(now);
    }

    /// <summary>True when the pick still has no answer and the group should be tested again.</summary>
    public bool RetryDue(double now)
    {
        if (Answered || now - lastTest < RetryDelay) return false;
        lastTest = now;
        misses++;
        return true;
    }

    /// <summary>The group was tested in full for another reason: a start, the window.</summary>
    public void Tested(double now) => lastTest = now;

    /// <summary>True when the picked server should be checked now, <paramref name="gap"/> seconds or more after the last check or full test.</summary>
    public bool ProbeDue(double now, double gap)
    {
        if (now - Math.Max(lastProbe, lastTest) < gap) return false;
        lastProbe = now;
        return true;
    }
}
