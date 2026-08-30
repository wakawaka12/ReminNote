using System.IO.Pipes;
using System.Runtime.Versioning;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Transport;

/// <summary>
/// Unwired raw-frame client for the scoped business or control pipe. It does
/// not replay frames, mint request identifiers or reconcile response loss;
/// those decisions belong to the shared command adapter.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedPipeTransportClient : ITransportConnection
{
    private readonly NamedPipeTransportEndpoint endpoint;
    private readonly ITransportFrameCodec frameCodec;
    private readonly TransportLimits limits;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private NamedPipeClientStream? stream;
    private int state = (int)TransportConnectionState.Disconnected;

    public NamedPipeTransportClient(
        NamedPipeTransportEndpoint endpoint,
        TransportLimits limits,
        ITransportFrameCodec? frameCodec = null)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.limits.Validate();
        this.frameCodec = frameCodec ?? new LengthPrefixedFrameCodec(this.limits);
    }

    public TransportConnectionState State =>
        (TransportConnectionState)Volatile.Read(ref state);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotClosed();
            if (State == TransportConnectionState.Connected)
            {
                return;
            }

            SetState(TransportConnectionState.Connecting);
            NamedPipeClientStream? newStream = null;
            try
            {
                newStream = endpoint.CreateClientStream();
                await newStream.ConnectAsync(
                    ToTimeoutMilliseconds(limits.ConnectDeadline),
                    cancellationToken).ConfigureAwait(false);
                lock (stateGate)
                {
                    stream = newStream;
                    state = (int)TransportConnectionState.Connected;
                }
            }
            catch
            {
                if (newStream is not null)
                {
                    await newStream.DisposeAsync().ConfigureAwait(false);
                }

                SetStateIfNotClosed(TransportConnectionState.Disconnected);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotClosed();
            lock (stateGate)
            {
                if (state == (int)TransportConnectionState.Disconnected && stream is null)
                {
                    return;
                }

                state = (int)TransportConnectionState.Closing;
            }

            await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await readGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await CloseCurrentStreamAsync().ConfigureAwait(false);
                }
                finally
                {
                    readGate.Release();
                }
            }
            finally
            {
                writeGate.Release();
            }

            SetStateIfNotClosed(TransportConnectionState.Disconnected);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        NamedPipeClientStream? activeStream = null;
        try
        {
            activeStream = GetConnectedStream();
            await frameCodec.WriteAsync(
                activeStream,
                frame,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (activeStream is not null && RequiresDisconnect(exception))
        {
            MarkDisconnected(activeStream);
            throw;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<byte[]?> ReceiveFrameAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        NamedPipeClientStream? activeStream = null;
        try
        {
            activeStream = GetConnectedStream();
            var frame = await frameCodec.ReadAsync(
                activeStream,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                MarkDisconnected(activeStream);
            }

            return frame;
        }
        catch (Exception exception) when (activeStream is not null && RequiresDisconnect(exception))
        {
            MarkDisconnected(activeStream);
            throw;
        }
        finally
        {
            readGate.Release();
        }
    }

    /// <summary>
    /// Reconnects within a finite transport budget. No application frame is
    /// replayed and no business key or hash is changed by this method.
    /// </summary>
    public async ValueTask ReconnectAsync(
        TransportReconnectPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var executor = new TransportReconnectExecutor(policy, timeProvider);
        await executor.ExecuteAsync(
            async (_, attemptCancellationToken) =>
            {
                await DisconnectAsync(attemptCancellationToken).ConfigureAwait(false);
                await ConnectAsync(attemptCancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (state == (int)TransportConnectionState.Closed)
                {
                    return;
                }

                state = (int)TransportConnectionState.Closing;
            }

            await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await readGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await CloseCurrentStreamAsync().ConfigureAwait(false);
                }
                finally
                {
                    readGate.Release();
                }
            }
            finally
            {
                writeGate.Release();
            }

            lock (stateGate)
            {
                state = (int)TransportConnectionState.Closed;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask CloseCurrentStreamAsync()
    {
        NamedPipeClientStream? activeStream;
        lock (stateGate)
        {
            activeStream = stream;
            stream = null;
        }

        if (activeStream is not null)
        {
            await activeStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeClientStream GetConnectedStream()
    {
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(
                state == (int)TransportConnectionState.Closed,
                nameof(NamedPipeTransportClient));

            if (state != (int)TransportConnectionState.Connected || stream is null)
            {
                throw new TransportFailureException(
                    TransportFailureKind.Closed,
                    "The transport connection is not ready.",
                    connectionMustClose: true);
            }

            return stream;
        }
    }

    private void MarkDisconnected(NamedPipeClientStream expectedStream)
    {
        NamedPipeClientStream? streamToClose = null;
        lock (stateGate)
        {
            if (ReferenceEquals(stream, expectedStream))
            {
                stream = null;
                if (state != (int)TransportConnectionState.Closed &&
                    state != (int)TransportConnectionState.Closing)
                {
                    state = (int)TransportConnectionState.Disconnected;
                }

                streamToClose = expectedStream;
            }
        }

        streamToClose?.Dispose();
    }

    private void EnsureNotClosed()
    {
        ObjectDisposedException.ThrowIf(
            State == TransportConnectionState.Closed,
            nameof(NamedPipeTransportClient));
    }

    private void SetState(TransportConnectionState nextState) =>
        Volatile.Write(ref state, (int)nextState);

    private void SetStateIfNotClosed(TransportConnectionState nextState)
    {
        lock (stateGate)
        {
            if (state != (int)TransportConnectionState.Closed)
            {
                state = (int)nextState;
            }
        }
    }

    private static bool RequiresDisconnect(Exception exception) => exception switch
    {
        TransportDeadlineExceededException => true,
        TransportFailureException failure => failure.ConnectionMustClose,
        IOException or TimeoutException or OperationCanceledException => true,
        _ => false
    };

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        checked((int)Math.Ceiling(timeout.TotalMilliseconds));
}
