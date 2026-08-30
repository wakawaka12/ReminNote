using ReminNote.Core.Transport;

namespace ReminNote.Tests;

public sealed class TransportLimitsTests
{
    [Fact]
    public void ProtocolLimitProjectionMatchesTheFrozenTransportBudgets()
    {
        var limits = TransportTestLimits.Create();

        Assert.Equal(1_048_576, limits.MaxFrameBytes);
        Assert.Equal(262_144, limits.MaxCommandPayloadBytes);
        Assert.Equal(65_536, limits.MaxEventPayloadBytes);
        Assert.Equal(8_192, limits.MaxErrorDetailsBytes);
        Assert.Equal(16_384, limits.MaxSuccessPayloadBytes);
        Assert.Equal(32, limits.MaxJsonDepth);
        Assert.Equal(32, limits.MaxInFlightRequests);
        Assert.Equal(256, limits.MaxQueuedRequests);
        Assert.Equal(TimeSpan.FromSeconds(2), limits.ConnectDeadline);
        Assert.Equal(TimeSpan.FromSeconds(2), limits.HelloQueryStatusDeadline);
        Assert.Equal(TimeSpan.FromSeconds(5), limits.DefaultMutationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), limits.CancelTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.AbsoluteRequestDeadline);
        limits.Validate();
    }

    [Fact]
    public void APayloadLimitCannotExceedTheFrameLimit()
    {
        var source = new TransportTestLimitSource
        {
            MaxFrameBytes = 128,
            MaxCommandPayloadBytes = 129
        };
        Assert.Throws<ArgumentException>(() => new TransportLimits(source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30_001)]
    public void PerRequestTimeoutUsesTheFrozenOneToThirtySecondRange(int milliseconds)
    {
        var limits = TransportTestLimits.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => limits.ValidateTimeoutMilliseconds(milliseconds));
    }

    [Fact]
    public void PayloadKindsUseTheirIndependentCaps()
    {
        var limits = TransportTestLimits.Create();
        var source = new TransportTestLimitSource();

        Assert.Equal(source.MaxFrameBytes, limits.GetPayloadLimit(TransportPayloadKind.Generic));
        Assert.Equal(source.MaxCommandPayloadBytes, limits.GetPayloadLimit(TransportPayloadKind.Command));
        Assert.Equal(source.MaxEventPayloadBytes, limits.GetPayloadLimit(TransportPayloadKind.Event));
        Assert.Equal(source.MaxSuccessPayloadBytes, limits.GetPayloadLimit(TransportPayloadKind.Success));
        Assert.Equal(source.MaxErrorDetailsBytes, limits.GetPayloadLimit(TransportPayloadKind.ErrorDetails));
    }
}
