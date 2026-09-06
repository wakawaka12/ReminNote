namespace ReminNote.Agent.Runtime;

/// <summary>
/// A process-local resume/sleep coverage signal for the console Agent. Windows
/// may suspend the process without delivering a managed power notification to
/// a console host, so the monotonic TickCount64 observation is kept alongside
/// the wall-clock check. A long observation gap is treated as a resume before
/// the next scheduler cycle; an explicit signal can be raised by a future
/// native power-event adapter without changing the scheduler contract.
/// </summary>
internal sealed class AgentResumeCoverage
{
    private const long CoverageGapMilliseconds = 45_000;
    private long lastTickCount = Environment.TickCount64;
    private int explicitResume;

    public void SignalResume() => Interlocked.Exchange(ref explicitResume, 1);

    public bool ConsumeSignal()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Exchange(ref lastTickCount, now);
        var elapsed = unchecked(now - previous);
        return Interlocked.Exchange(ref explicitResume, 0) != 0 ||
            elapsed < 0 ||
            elapsed >= CoverageGapMilliseconds;
    }
}
