namespace ReminNote.Core.Transport;

public enum TransportConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Closing,
    Closed
}

/// <summary>
/// Stream-level connection seam. Message envelopes and business dispatch are
/// intentionally absent; the P2.5-01 contract supplies those later.
/// </summary>
public interface ITransportConnection : IAsyncDisposable
{
    TransportConnectionState State { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReceiveFrameAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload validation seam. P2.5-01 owns strict UTF-8/JSON, envelope and
/// stable error mapping; the transport session invokes this before handing a
/// frame to handshake or business dispatch and does not interpret its fields.
/// </summary>
public interface ITransportPayloadValidator
{
    void Validate(ReadOnlyMemory<byte> frame);
}
