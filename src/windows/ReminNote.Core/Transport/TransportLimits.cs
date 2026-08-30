namespace ReminNote.Core.Transport;

/// <summary>
/// Read-only projection of the P2.5-01 ProtocolLimits used by framing and
/// stream coordination. The protocol owner supplies the values; this slice
/// deliberately has no parallel defaults or configurable upper-bound set.
/// </summary>
public interface ITransportLimitSource
{
    int MaxFrameBytes { get; }

    int MaxCommandPayloadBytes { get; }

    int MaxEventPayloadBytes { get; }

    int MaxErrorDetailsBytes { get; }

    int MaxSuccessPayloadBytes { get; }

    int MaxJsonDepth { get; }

    int MaxInFlightRequests { get; }

    int MaxQueuedRequests { get; }

    TimeSpan ConnectDeadline { get; }

    TimeSpan HelloQueryStatusDeadline { get; }

    TimeSpan DefaultMutationTimeout { get; }

    TimeSpan CancelTimeout { get; }

    TimeSpan AbsoluteRequestDeadline { get; }
}

/// <summary>
/// Validated alias over the shared protocol limits. It adds only transport
/// payload-kind selection and relationship checks; it does not own numeric
/// protocol limits.
/// </summary>
public sealed class TransportLimits
{
    private readonly int maxFrameBytes;
    private readonly int maxCommandPayloadBytes;
    private readonly int maxEventPayloadBytes;
    private readonly int maxErrorDetailsBytes;
    private readonly int maxSuccessPayloadBytes;
    private readonly int maxJsonDepth;
    private readonly int maxInFlightRequests;
    private readonly int maxQueuedRequests;
    private readonly TimeSpan connectDeadline;
    private readonly TimeSpan helloQueryStatusDeadline;
    private readonly TimeSpan defaultMutationTimeout;
    private readonly TimeSpan cancelTimeout;
    private readonly TimeSpan absoluteRequestDeadline;

    public TransportLimits(ITransportLimitSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        maxFrameBytes = source.MaxFrameBytes;
        maxCommandPayloadBytes = source.MaxCommandPayloadBytes;
        maxEventPayloadBytes = source.MaxEventPayloadBytes;
        maxErrorDetailsBytes = source.MaxErrorDetailsBytes;
        maxSuccessPayloadBytes = source.MaxSuccessPayloadBytes;
        maxJsonDepth = source.MaxJsonDepth;
        maxInFlightRequests = source.MaxInFlightRequests;
        maxQueuedRequests = source.MaxQueuedRequests;
        connectDeadline = source.ConnectDeadline;
        helloQueryStatusDeadline = source.HelloQueryStatusDeadline;
        defaultMutationTimeout = source.DefaultMutationTimeout;
        cancelTimeout = source.CancelTimeout;
        absoluteRequestDeadline = source.AbsoluteRequestDeadline;
        Validate();
    }

    public int MaxFrameBytes => maxFrameBytes;

    public int MaxCommandPayloadBytes => maxCommandPayloadBytes;

    public int MaxEventPayloadBytes => maxEventPayloadBytes;

    public int MaxErrorDetailsBytes => maxErrorDetailsBytes;

    public int MaxSuccessPayloadBytes => maxSuccessPayloadBytes;

    public int MaxJsonDepth => maxJsonDepth;

    public int MaxInFlightRequests => maxInFlightRequests;

    public int MaxQueuedRequests => maxQueuedRequests;

    public TimeSpan ConnectDeadline => connectDeadline;

    public TimeSpan HelloQueryStatusDeadline => helloQueryStatusDeadline;

    public TimeSpan DefaultMutationTimeout => defaultMutationTimeout;

    public TimeSpan CancelTimeout => cancelTimeout;

    public TimeSpan AbsoluteRequestDeadline => absoluteRequestDeadline;

    public void Validate()
    {
        ValidatePositive(nameof(MaxFrameBytes), MaxFrameBytes);
        ValidatePositive(nameof(MaxCommandPayloadBytes), MaxCommandPayloadBytes);
        ValidatePositive(nameof(MaxEventPayloadBytes), MaxEventPayloadBytes);
        ValidatePositive(nameof(MaxErrorDetailsBytes), MaxErrorDetailsBytes);
        ValidatePositive(nameof(MaxSuccessPayloadBytes), MaxSuccessPayloadBytes);
        ValidatePositive(nameof(MaxJsonDepth), MaxJsonDepth);
        ValidatePositive(nameof(MaxInFlightRequests), MaxInFlightRequests);
        ValidatePositive(nameof(MaxQueuedRequests), MaxQueuedRequests);

        if (MaxCommandPayloadBytes > MaxFrameBytes ||
            MaxEventPayloadBytes > MaxFrameBytes ||
            MaxErrorDetailsBytes > MaxFrameBytes ||
            MaxSuccessPayloadBytes > MaxFrameBytes)
        {
            throw new ArgumentException(
                "Protocol payload limits must not exceed the frame limit.",
                nameof(MaxFrameBytes));
        }

        ValidateDeadline(nameof(AbsoluteRequestDeadline), AbsoluteRequestDeadline);
        ValidateDeadline(nameof(ConnectDeadline), ConnectDeadline);
        ValidateDeadline(nameof(HelloQueryStatusDeadline), HelloQueryStatusDeadline);
        ValidateDeadline(nameof(DefaultMutationTimeout), DefaultMutationTimeout);
        ValidateDeadline(nameof(CancelTimeout), CancelTimeout);
    }

    public TimeSpan ValidateTimeoutMilliseconds(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 1 ||
            timeoutMilliseconds > AbsoluteRequestDeadline.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        return TimeSpan.FromMilliseconds(timeoutMilliseconds);
    }

    public int GetPayloadLimit(TransportPayloadKind kind) => kind switch
    {
        TransportPayloadKind.Generic => MaxFrameBytes,
        TransportPayloadKind.Command => MaxCommandPayloadBytes,
        TransportPayloadKind.Event => MaxEventPayloadBytes,
        TransportPayloadKind.Success => MaxSuccessPayloadBytes,
        TransportPayloadKind.ErrorDetails => MaxErrorDetailsBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ValidatePositive(string name, int value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private void ValidateDeadline(string name, TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > AbsoluteRequestDeadline)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public enum TransportPayloadKind
{
    Generic,
    Command,
    Event,
    Success,
    ErrorDetails
}
