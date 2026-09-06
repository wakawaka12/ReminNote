using NodaTime;
using ReminNote.Agent.Runtime;
using System.Runtime.Versioning;

namespace ReminNote.Tests.P3_03;

[SupportedOSPlatform("windows")]
public sealed class AgentRuntimeRecoveryTests
{
    [Fact]
    public void SchedulerRecoversAfterShortAndLongSuspendGaps()
    {
        Assert.False(AgentRuntime.ShouldRecover(
            initialRecovery: false,
            clockGap: Duration.FromSeconds(30),
            resumeSignaled: false));
        Assert.True(AgentRuntime.ShouldRecover(
            initialRecovery: false,
            clockGap: Duration.FromSeconds(90),
            resumeSignaled: false));
        Assert.True(AgentRuntime.ShouldRecover(
            initialRecovery: false,
            clockGap: Duration.FromMinutes(3),
            resumeSignaled: false));
        Assert.True(AgentRuntime.ShouldRecover(
            initialRecovery: false,
            clockGap: Duration.FromSeconds(1),
            resumeSignaled: true));
        Assert.True(AgentRuntime.ShouldRecover(
            initialRecovery: false,
            clockGap: Duration.FromSeconds(-1),
            resumeSignaled: false));
    }

    [Fact]
    public void ExplicitResumeSignalIsConsumedOnce()
    {
        var coverage = new AgentResumeCoverage();
        Assert.False(coverage.ConsumeSignal());
        coverage.SignalResume();
        Assert.True(coverage.ConsumeSignal());
        Assert.False(coverage.ConsumeSignal());
    }
}
