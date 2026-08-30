using System.Buffers.Binary;

namespace ReminNote.Core.Transport;

/// <summary>
/// Reads and writes the frozen four-byte unsigned big-endian length-prefixed
/// frame. The codec never allocates a frame buffer until the length has been
/// checked against the configured upper bound.
/// </summary>
public sealed class LengthPrefixedFrameCodec : ITransportFrameCodec
{
    private readonly TransportLimits limits;

    public LengthPrefixedFrameCodec(TransportLimits limits)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.limits.Validate();
    }

    public async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var deadline = CreateDeadline(timeout, cancellationToken);

        try
        {
            var prefix = new byte[sizeof(uint)];
            var prefixBytes = await ReadAtMostAsync(
                stream,
                prefix,
                deadline.Token).ConfigureAwait(false);
            if (prefixBytes == 0)
            {
                return null;
            }

            if (prefixBytes != prefix.Length)
            {
                throw new TransportFailureException(
                    TransportFailureKind.InvalidFrame,
                    connectionMustClose: true);
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length == 0)
            {
                throw new TransportFailureException(
                    TransportFailureKind.InvalidFrame,
                    connectionMustClose: true);
            }

            if (length > limits.MaxFrameBytes)
            {
                throw new TransportFailureException(
                    TransportFailureKind.FrameTooLarge,
                    connectionMustClose: true);
            }

            var frame = GC.AllocateUninitializedArray<byte>((int)length);
            await ReadExactlyAsync(stream, frame, deadline.Token).ConfigureAwait(false);
            return frame;
        }
        catch (OperationCanceledException) when (deadline.IsDeadlineElapsed && !cancellationToken.IsCancellationRequested)
        {
            throw new TransportDeadlineExceededException();
        }
    }

    public ValueTask<byte[]?> ReadAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ReadFrameAsync(stream, timeout, cancellationToken);

    public async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateFrameSize(frame.Length);
        using var deadline = CreateDeadline(timeout, cancellationToken);

        try
        {
            var prefix = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)frame.Length);
            await stream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsDeadlineElapsed && !cancellationToken.IsCancellationRequested)
        {
            throw new TransportDeadlineExceededException();
        }
    }

    public ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, frame, timeout, cancellationToken);

    public void ValidatePayloadSize(ReadOnlyMemory<byte> payload, TransportPayloadKind kind)
    {
        var limit = limits.GetPayloadLimit(kind);
        if (payload.Length < 1 || payload.Length > limit)
        {
            throw new TransportFailureException(
                payload.Length > limit
                    ? TransportFailureKind.FrameTooLarge
                    : TransportFailureKind.InvalidFrame,
                connectionMustClose: true);
        }
    }

    private void ValidateFrameSize(int length)
    {
        if (length == 0)
        {
            throw new TransportFailureException(
                TransportFailureKind.InvalidFrame,
                connectionMustClose: true);
        }

        if (length > limits.MaxFrameBytes)
        {
            throw new TransportFailureException(
                TransportFailureKind.FrameTooLarge,
                connectionMustClose: true);
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[total..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[total..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new TransportFailureException(
                    TransportFailureKind.InvalidFrame,
                    connectionMustClose: true);
            }

            total += read;
        }
    }

    private DeadlineCancellation CreateDeadline(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > limits.AbsoluteRequestDeadline)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return new DeadlineCancellation(source);
    }

    private sealed class DeadlineCancellation : IDisposable
    {
        private readonly CancellationTokenSource source;
        public DeadlineCancellation(CancellationTokenSource source)
        {
            this.source = source;
        }

        public CancellationToken Token => source.Token;

        public bool IsDeadlineElapsed => source.IsCancellationRequested;

        public void Dispose() => source.Dispose();
    }
}

public interface ITransportFrameCodec
{
    ValueTask<byte[]?> ReadAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
