using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Transport;

/// <summary>
/// Raw identity seam for the P2.5-01 adapter. ProfileScope is deliberately a
/// string here; the shared protocol owner supplies its one public value type.
/// </summary>
internal readonly record struct TransportPeerIdentity(
    string UserSid,
    string ProfileScope);

/// <summary>
/// Handshake seam. The implementation owns hello fields, version negotiation,
/// feature requirements and the shared response envelope.
/// </summary>
internal readonly record struct TransportHandshakeResult(
    bool Ready,
    bool CloseConnection,
    ReadOnlyMemory<byte> ResponseFrame);

internal interface ITransportHandshake
{
    ValueTask<TransportHandshakeResult> HandleAsync(
        ReadOnlyMemory<byte> firstOrPendingFrame,
        TransportPeerIdentity peer,
        CancellationToken cancellationToken);
}

/// <summary>
/// Business dispatch seam. It receives and returns shared-contract frames
/// without interpreting them in the transport layer. It has no cancellation
/// token by design: cancellation of the pipe session must not kill an
/// accepted commit; the 01/03 adapter reconciles response loss by key/hash.
/// </summary>
internal interface ITransportRequestDispatcher
{
    ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        ReadOnlyMemory<byte> requestFrame,
        TransportPeerIdentity peer);
}

/// <summary>
/// Unwired stream session loop. A host decides when to create it and remains
/// responsible for closing the stream after a fail-closed exception.
/// </summary>
internal sealed class NamedPipeTransportSession : IAsyncDisposable
{
    private readonly ITransportFrameCodec frameCodec;
    private readonly ITransportPayloadValidator payloadValidator;
    private readonly ITransportHandshake handshake;
    private readonly ITransportRequestDispatcher dispatcher;
    private readonly TransportLimits limits;
    private readonly TransportWriteGate writeGate = new();

    public NamedPipeTransportSession(
        ITransportPayloadValidator payloadValidator,
        ITransportHandshake handshake,
        ITransportRequestDispatcher dispatcher,
        TransportLimits limits,
        ITransportFrameCodec? frameCodec = null)
    {
        this.payloadValidator = payloadValidator ?? throw new ArgumentNullException(nameof(payloadValidator));
        this.handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.limits.Validate();
        this.frameCodec = frameCodec ?? new LengthPrefixedFrameCodec(this.limits);
    }

    public async ValueTask RunAsync(
        Stream stream,
        TransportPeerIdentity peer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var inFlight = new SemaphoreSlim(
            limits.MaxInFlightRequests,
            limits.MaxInFlightRequests);
        var pending = new ConcurrentDictionary<long, Task>();
        var freeSlots = new ConcurrentQueue<int>(Enumerable.Range(0, limits.MaxInFlightRequests));
        var pendingGate = new object();
        var pendingSlots = new Task?[limits.MaxInFlightRequests];
        var firstDispatchFailure = new TaskCompletionSource<ExceptionDispatchInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? loopFailure = null;
        var nextDispatchId = 0L;

        try
        {
            var ready = false;
            while (!sessionCancellation.IsCancellationRequested)
            {
                var frame = await frameCodec.ReadAsync(
                    stream,
                    limits.HelloQueryStatusDeadline,
                    sessionCancellation.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                payloadValidator.Validate(frame);
                if (!ready)
                {
                    var handshakeResult = await handshake.HandleAsync(
                        frame,
                        peer,
                        sessionCancellation.Token).ConfigureAwait(false);
                    EnsureHandshakeResponse(handshakeResult.ResponseFrame);
                    await WriteFrameAsync(
                        stream,
                        handshakeResult.ResponseFrame,
                        limits.HelloQueryStatusDeadline).ConfigureAwait(false);

                    if (handshakeResult.CloseConnection)
                    {
                        break;
                    }

                    ready = handshakeResult.Ready;
                    continue;
                }

                if (!inFlight.Wait(0, CancellationToken.None))
                {
                    throw new TransportFailureException(
                        TransportFailureKind.Io,
                        "The transport in-flight request limit was exceeded.",
                        connectionMustClose: true);
                }

                if (!freeSlots.TryDequeue(out var slot))
                {
                    inFlight.Release();
                    throw new TransportFailureException(
                        TransportFailureKind.Io,
                        "The transport dispatch slot pool is inconsistent.",
                        connectionMustClose: true);
                }

                var dispatchId = Interlocked.Increment(ref nextDispatchId);
                Task dispatchTask;
                try
                {
                    dispatchTask = DispatchAndWriteAsync(stream, frame, peer);
                }
                catch
                {
                    freeSlots.Enqueue(slot);
                    inFlight.Release();
                    throw;
                }

                var trackedTask = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pendingGate)
                {
                    pendingSlots[slot] = trackedTask.Task;
                }

                // Register the tracked task before starting its completion
                // observer so a synchronously-completed dispatch is drained.
                pending[dispatchId] = trackedTask.Task;
                _ = CompleteDispatchAsync(
                    dispatchId,
                    slot,
                    dispatchTask,
                    trackedTask,
                    pending,
                    pendingGate,
                    pendingSlots,
                    freeSlots,
                    inFlight,
                    firstDispatchFailure,
                    sessionCancellation);
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            // External cancellation and a dispatch failure are both handled
            // after accepted dispatches have drained below.
        }
        catch (Exception exception)
        {
            loopFailure = ExceptionDispatchInfo.Capture(exception);
            sessionCancellation.Cancel();
        }
        finally
        {
            sessionCancellation.Cancel();
            await DrainPendingAsync(pending, pendingGate, pendingSlots).ConfigureAwait(false);
        }

        if (firstDispatchFailure.Task.IsCompletedSuccessfully)
        {
            firstDispatchFailure.Task.Result.Throw();
        }

        loopFailure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task DispatchAndWriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> requestFrame,
        TransportPeerIdentity peer)
    {
        var responseFrame = await dispatcher.DispatchAsync(requestFrame, peer).ConfigureAwait(false);
        payloadValidator.Validate(responseFrame);
        await WriteFrameAsync(
            stream,
            responseFrame,
            limits.AbsoluteRequestDeadline).ConfigureAwait(false);
    }

    private ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        TimeSpan timeout)
    {
        return writeGate.WriteAsync(
            () => frameCodec.WriteAsync(
                stream,
                frame,
                timeout,
                CancellationToken.None));
    }

    private static async Task CompleteDispatchAsync(
        long dispatchId,
        int slot,
        Task dispatchTask,
        TaskCompletionSource<object?> trackedTask,
        ConcurrentDictionary<long, Task> pending,
        object pendingGate,
        Task?[] pendingSlots,
        ConcurrentQueue<int> freeSlots,
        SemaphoreSlim inFlight,
        TaskCompletionSource<ExceptionDispatchInfo> firstDispatchFailure,
        CancellationTokenSource sessionCancellation)
    {
        try
        {
            await dispatchTask.ConfigureAwait(false);
            trackedTask.TrySetResult(null);
        }
        catch (Exception exception)
        {
            var failure = ExceptionDispatchInfo.Capture(exception);
            firstDispatchFailure.TrySetResult(failure);
            trackedTask.TrySetException(exception);
            sessionCancellation.Cancel();
        }
        finally
        {
            lock (pendingGate)
            {
                pendingSlots[slot] = null;
            }

            freeSlots.Enqueue(slot);
            inFlight.Release();
            pending.TryRemove(dispatchId, out _);
        }
    }

    private static async Task DrainPendingAsync(
        ConcurrentDictionary<long, Task> pending,
        object pendingGate,
        Task?[] pendingSlots)
    {
        while (true)
        {
            if (pending.IsEmpty)
            {
                return;
            }

            Task[] tasks;
            lock (pendingGate)
            {
                tasks = pendingSlots
                    .Where(task => task is not null)
                    .Cast<Task>()
                    .ToArray();
            }

            if (tasks.Length == 0)
            {
                continue;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The first failure is retained by CompleteDispatchAsync.
            }
        }
    }

    private void EnsureHandshakeResponse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length == 0)
        {
            throw new TransportFailureException(
                TransportFailureKind.InvalidJson,
                "The handshake did not return a response frame.",
                connectionMustClose: true);
        }

        payloadValidator.Validate(frame);
    }

    public ValueTask DisposeAsync()
    {
        return writeGate.DisposeAsync();
    }
}
