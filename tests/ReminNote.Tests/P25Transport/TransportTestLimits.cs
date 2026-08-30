using ReminNote.Core.Transport;

namespace ReminNote.Tests;

/// <summary>
/// Isolated fixture projection for the contract-owned ProtocolLimits. It is
/// test input only and is never used by an Agent runtime.
/// </summary>
internal sealed class TransportTestLimitSource : ITransportLimitSource
{
    public int MaxFrameBytes { get; set; } = 1_048_576;

    public int MaxCommandPayloadBytes { get; set; } = 262_144;

    public int MaxEventPayloadBytes { get; set; } = 65_536;

    public int MaxErrorDetailsBytes { get; set; } = 8_192;

    public int MaxSuccessPayloadBytes { get; set; } = 16_384;

    public int MaxJsonDepth { get; set; } = 32;

    public int MaxInFlightRequests { get; set; } = 32;

    public int MaxQueuedRequests { get; set; } = 256;

    public TimeSpan ConnectDeadline { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan HelloQueryStatusDeadline { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DefaultMutationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan CancelTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan AbsoluteRequestDeadline { get; set; } = TimeSpan.FromSeconds(30);
}

internal static class TransportTestLimits
{
    internal static TransportLimits Create(TransportTestLimitSource? source = null) =>
        new(source ?? new TransportTestLimitSource());
}
