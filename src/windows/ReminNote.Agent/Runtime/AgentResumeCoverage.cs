namespace ReminNote.Agent.Runtime;

/// <summary>
/// A process-local resume/sleep coverage signal for the console Agent. Windows
/// may suspend the process without delivering a managed power notification to
/// a console host, so the monotonic TickCount64 observation is kept alongside
/// the wall-clock check. An unexpected observation gap is treated as a resume
/// before the next scheduler cycle; an explicit signal can be raised by a
/// future native power-event adapter without changing the scheduler contract.
/// </summary>
internal sealed class AgentResumeCoverage
{
    private const long FallbackCoverageGapMilliseconds = 45_000;
    private const long SchedulerJitterMilliseconds = 500;
    private const long NoExpectedDelay = -1;
    private long lastTickCount = Environment.TickCount64;
    private long expectedDelayMilliseconds = NoExpectedDelay;
    private int explicitResume;

    public void SignalResume() => Interlocked.Exchange(ref explicitResume, 1);

    /// <summary>
    /// Arms the next observation with the delay requested by the scheduler.
    /// Comparing against that delay lets a 5-40 second suspend be detected
    /// without treating every ordinary 30-second scheduler tick as a resume.
    /// </summary>
    public void ArmForDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks);
        var milliseconds = delay == TimeSpan.MaxValue
            ? long.MaxValue
            : checked((long)Math.Ceiling(delay.TotalMilliseconds));
        Interlocked.Exchange(ref expectedDelayMilliseconds, milliseconds);
    }

    public bool ConsumeSignal()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Exchange(ref lastTickCount, now);
        var elapsed = unchecked(now - previous);
        var expected = Interlocked.Exchange(
            ref expectedDelayMilliseconds,
            NoExpectedDelay);
        return Interlocked.Exchange(ref explicitResume, 0) != 0 ||
            elapsed < 0 ||
            IsUnexpectedGap(elapsed, expected);
    }

    /// <summary>
    /// Deterministic seam for tests and for explaining the process-local
    /// coverage rule. An unarmed observation retains the conservative
    /// 45-second fallback used before the scheduler can arm its timer.
    /// </summary>
    internal static bool IsUnexpectedGap(
        long elapsedMilliseconds,
        long expectedDelayMilliseconds)
    {
        if (elapsedMilliseconds < 0)
        {
            return true;
        }

        if (expectedDelayMilliseconds >= 0)
        {
            var threshold = expectedDelayMilliseconds >
                long.MaxValue - SchedulerJitterMilliseconds
                ? long.MaxValue
                : expectedDelayMilliseconds + SchedulerJitterMilliseconds;
            return elapsedMilliseconds > threshold;
        }

        return elapsedMilliseconds >= FallbackCoverageGapMilliseconds;
    }
}
