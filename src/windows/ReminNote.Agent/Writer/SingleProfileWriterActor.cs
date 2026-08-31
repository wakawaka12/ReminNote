using System.Threading.Channels;
using ReminNote.Agent.Command;
using ReminNote.Core.Protocol;

namespace ReminNote.Agent.Writer;

/// <summary>
/// A single-profile, single-reader writer actor. This type is deliberately
/// not composed by Program.cs: it is a non-runnable P2.5-03 preparation seam
/// until the P2.5-01/02 contracts are integrated by the totalization window.
/// </summary>
internal sealed class SingleProfileWriterActor : IAsyncDisposable
{
    private const int MaxRecoveryAttempts = 2;

    private readonly string profileScope;
    private readonly IWriterPersistence persistence;
    private readonly WriterTransactionAdapter transactionAdapter;
    private readonly WriterCommandDispatcher dispatcher;
    private readonly Channel<IQueueItem> queue;
    private readonly object stateLock = new();
    private readonly Dictionary<string, WorkItem> workItems = new(StringComparer.Ordinal);
    // This tracks only accepted live queue items. Completed entries are removed
    // immediately; retaining them for the actor lifetime defeats the bounded
    // queue contract and leaks every command's cancellation source.
    private readonly HashSet<IQueueItem> queueItems = new();
    private readonly Task loopTask;
    private int disposed;

    public SingleProfileWriterActor(
        string profileScope,
        IWriterPersistence persistence,
        WriterCommandDispatcher dispatcher,
        int queueCapacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope, nameof(profileScope));
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (queueCapacity is < 1 or > ProtocolLimits.MaxQueuedRequestsPerProfile)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueCapacity),
                $"The writer queue must be between 1 and {ProtocolLimits.MaxQueuedRequestsPerProfile}.");
        }

        this.profileScope = profileScope;
        this.persistence = persistence;
        transactionAdapter = new WriterTransactionAdapter(persistence);
        this.dispatcher = dispatcher;
        queue = Channel.CreateBounded<IQueueItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        loopTask = ProcessLoopAsync();
    }

    /// <summary>
    /// Enqueues one internal command. The cancellation token only cancels the
    /// caller's wait; server-side cancellation is explicit through
    /// <see cref="CancelAsync"/> and never abandons an accepted command.
    /// </summary>
    public async ValueTask<WriterResponse> ExecuteAsync(
        WriterCommandRequest request,
        CancellationToken responseCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!dispatcher.CanExecute(request.Operation))
        {
            return RejectedResponse(
                "ipc.request.invalid",
                retryable: false);
        }

        Task<WriterResponse> completion;
        var replay = false;

        lock (stateLock)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return UnavailableResponse();
            }

            if (workItems.TryGetValue(request.IdempotencyKey, out var existing))
            {
                if (!IsSameLogicalCommand(existing.Request, request))
                {
                    return ConflictResponse();
                }

                completion = existing.Completion.Task;
                replay = true;
            }
            else
            {
                var workItem = new WorkItem(request);
                if (!queue.Writer.TryWrite(workItem))
                {
                    return RejectedResponse(
                        "ipc.request.overloaded",
                        retryable: true);
                }

                workItems.Add(request.IdempotencyKey, workItem);
                queueItems.Add(workItem);
                completion = workItem.Completion.Task;
            }
        }

        var response = await completion
            .WaitAsync(responseCancellationToken)
            .ConfigureAwait(false);
        return replay ? AsReplay(response) : response;
    }

    /// <summary>
    /// Requests server-side cancellation for an accepted command. The actor
    /// serializes the commit boundary so a post-boundary cancellation cannot
    /// undo a committed domain fact.
    /// </summary>
    public async ValueTask<WriterCancelResponse> CancelAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey));

        WorkItem? workItem;
        CancelWorkItem? cancelWorkItem = null;
        lock (stateLock)
        {
            workItems.TryGetValue(idempotencyKey, out workItem);

            if (workItem is null)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return UnavailableCancelResponse();
                }

                cancelWorkItem = new CancelWorkItem(idempotencyKey);
                if (!queue.Writer.TryWrite(cancelWorkItem))
                {
                    return new WriterCancelResponse(
                        WriterCancelOutcome.AlreadyAtCommitBoundary,
                        WriterReceiptStatus.Unknown);
                }

                queueItems.Add(cancelWorkItem);
            }
        }

        if (workItem is not null)
        {
            return workItem.RequestCancel();
        }

        try
        {
            return await cancelWorkItem!.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return UnavailableCancelResponse();
        }
    }

    /// <summary>
    /// Reads bounded receipt state for reconciliation. It never dispatches a
    /// command and never changes a receipt, revision, or journal.
    /// </summary>
    public async ValueTask<WriterResponse> GetStatusAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey));

        bool hasInFlightWork;
        lock (stateLock)
        {
            hasInFlightWork = workItems.ContainsKey(idempotencyKey);
        }

        try
        {
            var receipt = await GetReceiptAsync(idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null)
            {
                return hasInFlightWork
                    ? PendingResponse()
                    : RejectedResponse("ipc.request.not_found", retryable: false);
            }

            return ResponseFromReceipt(receipt, replayed: false);
        }
        catch
        {
            return UnknownResponse();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            queue.Writer.TryComplete();
        }

        await loopTask.ConfigureAwait(false);

        IQueueItem[] allocated;
        lock (stateLock)
        {
            allocated = queueItems.ToArray();
            queueItems.Clear();
        }

        foreach (var queueItem in allocated)
        {
            queueItem.Dispose();
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var queueItem in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (queueItem)
                {
                    case WorkItem workItem:
                        await ProcessCommandQueueItemAsync(workItem).ConfigureAwait(false);
                        break;
                    case CancelWorkItem cancelWorkItem:
                        await ProcessCancelQueueItemAsync(cancelWorkItem).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown writer queue item.");
                }
            }
        }
        catch
        {
            IQueueItem[] abandoned;
            lock (stateLock)
            {
                abandoned = queueItems.ToArray();
                queueItems.Clear();
                workItems.Clear();
                foreach (var queueItem in abandoned)
                {
                    queueItem.CompleteUnavailable();
                }
            }

            foreach (var queueItem in abandoned)
            {
                queueItem.Dispose();
            }
        }
    }

    private async Task ProcessCommandQueueItemAsync(WorkItem workItem)
    {
        WriterResponse response;
        try
        {
            response = await ProcessWorkItemAsync(workItem).ConfigureAwait(false);
        }
        catch
        {
            // The actor must not die and abandon later queue entries because
            // an adapter violated its seam. No user content is included in
            // this defensive result.
            response = UnknownResponse();
        }

        lock (stateLock)
        {
            workItem.MarkCompleted();
            if (workItems.TryGetValue(workItem.Request.IdempotencyKey, out var registered) &&
                ReferenceEquals(registered, workItem))
            {
                workItems.Remove(workItem.Request.IdempotencyKey);
            }

            queueItems.Remove(workItem);
        }

        workItem.Completion.TrySetResult(response);
        workItem.Dispose();
    }

    private async Task ProcessCancelQueueItemAsync(CancelWorkItem cancelWorkItem)
    {
        WriterCancelResponse response;
        try
        {
            var cancelled = await FinalizeReceiptAsync(
                    cancelWorkItem.IdempotencyKey,
                    WriterReceiptStatus.Cancelled,
                    changed: false,
                    committedRevision: null,
                    errorCode: "ipc.request.cancelled",
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            response = cancelled is null
                ? new WriterCancelResponse(
                    WriterCancelOutcome.NotFound,
                    ReceiptStatus: null)
                : new WriterCancelResponse(
                    cancelled.Status == WriterReceiptStatus.Cancelled
                        ? WriterCancelOutcome.Requested
                        : WriterCancelOutcome.AlreadyAtCommitBoundary,
                    cancelled.Status);
        }
        catch
        {
            response = UnavailableCancelResponse();
        }

        lock (stateLock)
        {
            queueItems.Remove(cancelWorkItem);
        }

        cancelWorkItem.Completion.TrySetResult(response);
        cancelWorkItem.Dispose();
    }

    private async ValueTask<WriterResponse> ProcessWorkItemAsync(WorkItem workItem)
    {
        Admission admission;
        try
        {
            admission = await AdmitAsync(workItem).ConfigureAwait(false);
        }
        catch
        {
            return UnknownResponse();
        }

        workItem.PendingReceipt = admission.Receipt;

        return admission.Kind switch
        {
            AdmissionKind.Conflict => ConflictResponse(),
            AdmissionKind.Replay => ResponseFromReceipt(admission.Receipt, replayed: true),
            AdmissionKind.Execute => await ExecuteDomainAsync(workItem).ConfigureAwait(false),
            AdmissionKind.Unknown => ResponseFromReceipt(admission.Receipt, replayed: false),
            _ => throw new InvalidOperationException("Unknown writer admission state.")
        };
    }

    private ValueTask<Admission> AdmitAsync(WorkItem workItem) =>
        transactionAdapter.RunAsync(
            WriterTransactionKind.Receipt,
            async (transaction, _) =>
            {
                var existing = await transaction
                    .FindReceiptAsync(workItem.Request.IdempotencyKey, CancellationToken.None)
                    .ConfigureAwait(false);

                if (existing is null)
                {
                    var pending = WriterReceipt.Pending(
                        workItem.Request.IdempotencyKey,
                        workItem.Request.Operation,
                        workItem.Request.CanonicalPayloadHash);
                    await transaction
                        .StoreReceiptAsync(pending, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new Admission(AdmissionKind.Execute, pending);
                }

                if (!string.Equals(existing.Operation, workItem.Request.Operation, StringComparison.Ordinal) ||
                    !existing.HasSameHash(workItem.Request.CanonicalPayloadHash.Span))
                {
                    return new Admission(AdmissionKind.Conflict, existing);
                }

                if (existing.Status == WriterReceiptStatus.Pending)
                {
                    // Reaching this branch means the durable receipt has no
                    // in-memory owner in this actor (for example after an
                    // Agent restart). Re-enter once, then make the outcome
                    // explicitly unknown instead of returning Pending forever.
                    if (existing.AttemptCount < MaxRecoveryAttempts)
                    {
                        var pending = existing.WithStatus(
                            WriterReceiptStatus.Pending,
                            changed: null,
                            committedRevision: null,
                            errorCode: null,
                            attemptCount: existing.AttemptCount + 1);
                        await transaction
                            .StoreReceiptAsync(pending, CancellationToken.None)
                            .ConfigureAwait(false);
                        return new Admission(AdmissionKind.Execute, pending);
                    }

                    var unknown = existing.WithStatus(
                        WriterReceiptStatus.Unknown,
                        changed: false,
                        committedRevision: null,
                        errorCode: "storage.transaction_failed");
                    await transaction
                        .StoreReceiptAsync(unknown, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new Admission(AdmissionKind.Unknown, unknown);
                }

                if (IsRecoverable(existing.Status) && existing.AttemptCount < MaxRecoveryAttempts)
                {
                    var pending = existing.WithStatus(
                        WriterReceiptStatus.Pending,
                        changed: null,
                        committedRevision: null,
                        errorCode: null,
                        attemptCount: existing.AttemptCount + 1);
                    await transaction
                        .StoreReceiptAsync(pending, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new Admission(AdmissionKind.Execute, pending);
                }

                return new Admission(AdmissionKind.Replay, existing);
            });

    private async ValueTask<WriterResponse> ExecuteDomainAsync(WorkItem workItem)
    {
        try
        {
            return await transactionAdapter
                .RunAsync(
                    WriterTransactionKind.Domain,
                    async (transaction, cancellationToken) =>
                    {
                        if (workItem.IsCancelRequested)
                        {
                            throw new WriterCommitCancelledException();
                        }

                        // This is intentionally inside the writer
                        // transaction. A pre-queue revision check would be
                        // stale by the time this command reaches the actor.
                        var currentRevision = await transaction
                            .ReadCurrentRevisionAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (currentRevision != workItem.Request.ExpectedRevision)
                        {
                            var stale = workItem.PendingReceipt!.WithStatus(
                                WriterReceiptStatus.RejectedStale,
                                changed: false,
                                committedRevision: null,
                                errorCode: "revision.expected_mismatch");
                            await transaction
                                .StoreReceiptAsync(stale, cancellationToken)
                                .ConfigureAwait(false);
                            return new WriterResponse(
                                Ok: false,
                                Outcome: WriterOutcome.Stale,
                                Replayed: false,
                                ReceiptStatus: stale.Status,
                                Changed: false,
                                CommittedRevision: null,
                                CurrentRevision: currentRevision,
                                ErrorCode: stale.ErrorCode,
                                Retryable: false);
                        }

                        if (workItem.IsCancelRequested)
                        {
                            throw new WriterCommitCancelledException();
                        }

                        var execution = await dispatcher
                            .ExecuteAsync(workItem.Request, transaction, cancellationToken)
                            .ConfigureAwait(false);
                        ArgumentNullException.ThrowIfNull(execution);

                        switch (execution.Kind)
                        {
                            case WriterExecutionKind.Changed:
                            {
                                var committedRevision = checked(currentRevision + 1);
                                await transaction
                                    .PersistDomainChangesAsync(
                                        execution.Changes,
                                        committedRevision,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                var committed = workItem.PendingReceipt!.WithStatus(
                                    WriterReceiptStatus.Committed,
                                    changed: true,
                                    committedRevision: committedRevision,
                                    errorCode: null);
                                await transaction
                                    .StoreReceiptAsync(committed, cancellationToken)
                                    .ConfigureAwait(false);
                                return new WriterResponse(
                                    Ok: true,
                                    Outcome: WriterOutcome.Changed,
                                    Replayed: false,
                                    ReceiptStatus: committed.Status,
                                    Changed: true,
                                    CommittedRevision: committedRevision,
                                    CurrentRevision: committedRevision,
                                    ErrorCode: null,
                                    Retryable: false);
                            }

                            case WriterExecutionKind.NoOp:
                            {
                                var committed = workItem.PendingReceipt!.WithStatus(
                                    WriterReceiptStatus.Committed,
                                    changed: false,
                                    committedRevision: currentRevision,
                                    errorCode: null);
                                await transaction
                                    .StoreReceiptAsync(committed, cancellationToken)
                                    .ConfigureAwait(false);
                                return new WriterResponse(
                                    Ok: true,
                                    Outcome: WriterOutcome.NoOp,
                                    Replayed: false,
                                    ReceiptStatus: committed.Status,
                                    Changed: false,
                                    CommittedRevision: currentRevision,
                                    CurrentRevision: currentRevision,
                                    ErrorCode: null,
                                    Retryable: false);
                            }

                            case WriterExecutionKind.Rejected:
                            {
                                var rejected = workItem.PendingReceipt!.WithStatus(
                                    WriterReceiptStatus.Rejected,
                                    changed: false,
                                    committedRevision: null,
                                    errorCode: execution.ErrorCode);
                                await transaction
                                    .StoreReceiptAsync(rejected, cancellationToken)
                                    .ConfigureAwait(false);
                                return new WriterResponse(
                                    Ok: false,
                                    Outcome: WriterOutcome.Rejected,
                                    Replayed: false,
                                    ReceiptStatus: rejected.Status,
                                    Changed: false,
                                    CommittedRevision: null,
                                    CurrentRevision: currentRevision,
                                    ErrorCode: rejected.ErrorCode,
                                    Retryable: false);
                            }

                            default:
                                throw new InvalidOperationException(
                                    "The command executor returned an unknown execution kind.");
                        }
                    },
                    tryEnterCommit: workItem.TryEnterCommit,
                    operationCancellationToken: workItem.ExecutionToken)
                .ConfigureAwait(false);
        }
        catch (WriterCommitCancelledException)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.Cancelled,
                    "ipc.request.cancelled")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workItem.IsCancelRequested)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.Cancelled,
                    "ipc.request.cancelled")
                .ConfigureAwait(false);
        }
        catch (WriterCommandTimeoutException)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.TimedOut,
                    "ipc.request.timeout")
                .ConfigureAwait(false);
        }
        catch (WriterCommandUnknownException)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.Unknown,
                    "storage.transaction_failed")
                .ConfigureAwait(false);
        }
        catch (WriterCommitUnknownException)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.Unknown,
                    "storage.transaction_failed")
                .ConfigureAwait(false);
        }
        catch (WriterTransactionRecoveryException)
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.Unknown,
                    "storage.transaction_failed")
                .ConfigureAwait(false);
        }
        catch
        {
            return await FinalizeFailureAsync(
                    workItem,
                    WriterReceiptStatus.RolledBack,
                    "storage.transaction_failed")
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<WriterResponse> FinalizeFailureAsync(
        WorkItem workItem,
        WriterReceiptStatus status,
        string errorCode)
    {
        var receipt = await FinalizeReceiptAsync(
                workItem.Request.IdempotencyKey,
                status,
                changed: false,
                committedRevision: null,
                errorCode: errorCode,
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        return receipt is null
            ? UnknownResponse()
            : ResponseFromReceipt(receipt, replayed: false);
    }

    private ValueTask<WriterReceipt?> FinalizeReceiptAsync(
        string idempotencyKey,
        WriterReceiptStatus status,
        bool? changed,
        long? committedRevision,
        string? errorCode,
        CancellationToken cancellationToken) =>
        transactionAdapter.RunAsync(
            WriterTransactionKind.Receipt,
            async (transaction, _) =>
            {
                var existing = await transaction
                    .FindReceiptAsync(idempotencyKey, CancellationToken.None)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                if (existing.Status != WriterReceiptStatus.Pending)
                {
                    return existing;
                }

                // This read/conditional store is one receipt transaction and
                // is reached only in actor queue order. The real adapter must
                // preserve that atomic compare-and-set boundary.
                var finalized = existing.WithStatus(
                    status,
                    changed,
                    committedRevision,
                    errorCode);
                await transaction
                    .StoreReceiptAsync(finalized, CancellationToken.None)
                    .ConfigureAwait(false);
                return finalized;
            },
            operationCancellationToken: cancellationToken);

    private async ValueTask<WriterReceipt?> GetReceiptAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // The concrete implementation is profile-scoped by construction;
        // this seam never accepts a caller-provided database path or SID.
        return await persistence
            .ReadReceiptAsync(idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSameLogicalCommand(
        WriterCommandRequest first,
        WriterCommandRequest second) =>
        string.Equals(first.Operation, second.Operation, StringComparison.Ordinal) &&
        first.HasSameHash(second.CanonicalPayloadHash.Span);

    private static bool IsRecoverable(WriterReceiptStatus status) =>
        status is WriterReceiptStatus.RolledBack or
            WriterReceiptStatus.TimedOut or
            WriterReceiptStatus.Unknown;

    private static WriterResponse ResponseFromReceipt(
        WriterReceipt receipt,
        bool replayed)
    {
        var outcome = receipt.Status switch
        {
            WriterReceiptStatus.Committed when receipt.Changed == true => WriterOutcome.Changed,
            WriterReceiptStatus.Committed => WriterOutcome.NoOp,
            WriterReceiptStatus.RejectedStale => WriterOutcome.Stale,
            WriterReceiptStatus.Rejected => WriterOutcome.Rejected,
            WriterReceiptStatus.RolledBack => WriterOutcome.RolledBack,
            WriterReceiptStatus.Cancelled => WriterOutcome.Cancelled,
            WriterReceiptStatus.TimedOut => WriterOutcome.Timeout,
            WriterReceiptStatus.Unknown => WriterOutcome.Unknown,
            WriterReceiptStatus.Pending => WriterOutcome.Pending,
            _ => throw new InvalidOperationException("Unknown writer receipt status.")
        };

        var ok = receipt.Status == WriterReceiptStatus.Committed;
        var retryable = receipt.Status is WriterReceiptStatus.Pending or
            WriterReceiptStatus.RolledBack or
            WriterReceiptStatus.TimedOut or
            WriterReceiptStatus.Unknown;

        return new WriterResponse(
            Ok: ok,
            Outcome: replayed ? WriterOutcome.Replayed : outcome,
            Replayed: replayed,
            ReceiptStatus: receipt.Status,
            Changed: receipt.Changed,
            CommittedRevision: receipt.CommittedRevision,
            CurrentRevision: null,
            ErrorCode: receipt.ErrorCode,
            Retryable: retryable);
    }

    private static WriterResponse AsReplay(WriterResponse response) =>
        response.Outcome == WriterOutcome.Pending
            ? response
            : response with
            {
                Outcome = WriterOutcome.Replayed,
                Replayed = true
            };

    private static WriterResponse ConflictResponse() =>
        RejectedResponse("ipc.idempotency.conflict", retryable: false);

    private static WriterResponse PendingResponse() =>
        new(
            Ok: false,
            Outcome: WriterOutcome.Pending,
            Replayed: false,
            ReceiptStatus: WriterReceiptStatus.Pending,
            Changed: null,
            CommittedRevision: null,
            CurrentRevision: null,
            ErrorCode: null,
            Retryable: true);

    private static WriterResponse RejectedResponse(string errorCode, bool retryable) =>
        new(
            Ok: false,
            Outcome: WriterOutcome.Rejected,
            Replayed: false,
            ReceiptStatus: null,
            Changed: false,
            CommittedRevision: null,
            CurrentRevision: null,
            ErrorCode: errorCode,
            Retryable: retryable);

    private static WriterResponse UnknownResponse() =>
        new(
            Ok: false,
            Outcome: WriterOutcome.Unknown,
            Replayed: false,
            ReceiptStatus: null,
            Changed: null,
            CommittedRevision: null,
            CurrentRevision: null,
            ErrorCode: "storage.transaction_failed",
            Retryable: true);

    private static WriterResponse UnavailableResponse() =>
        new(
            Ok: false,
            Outcome: WriterOutcome.Unknown,
            Replayed: false,
            ReceiptStatus: null,
            Changed: null,
            CommittedRevision: null,
            CurrentRevision: null,
            ErrorCode: "ipc.agent.unavailable",
            Retryable: true);

    private static WriterCancelResponse UnavailableCancelResponse() =>
        new(
            WriterCancelOutcome.AlreadyAtCommitBoundary,
            WriterReceiptStatus.Unknown);

    private readonly record struct Admission(AdmissionKind Kind, WriterReceipt Receipt);

    private interface IQueueItem : IDisposable
    {
        void CompleteUnavailable();
    }

    private enum AdmissionKind
    {
        Execute,
        Replay,
        Conflict,
        Unknown
    }

    private sealed class CancelWorkItem : IQueueItem
    {
        public CancelWorkItem(string idempotencyKey)
        {
            IdempotencyKey = idempotencyKey;
            Completion = new TaskCompletionSource<WriterCancelResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string IdempotencyKey { get; }

        public TaskCompletionSource<WriterCancelResponse> Completion { get; }

        public void CompleteUnavailable() =>
            Completion.TrySetResult(UnavailableCancelResponse());

        public void Dispose()
        {
        }
    }

    private sealed class WorkItem : IQueueItem
    {
        private readonly object boundaryLock = new();
        private readonly CancellationTokenSource cancellation = new();
        private int cancelRequested;
        private int commitStarted;
        private int completed;

        public WorkItem(WriterCommandRequest request)
        {
            Request = request;
            Completion = new TaskCompletionSource<WriterResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public WriterCommandRequest Request { get; }

        public TaskCompletionSource<WriterResponse> Completion { get; }

        public WriterReceipt? PendingReceipt { get; set; }

        public CancellationToken ExecutionToken => cancellation.Token;

        public bool IsCancelRequested => Volatile.Read(ref cancelRequested) != 0;

        public bool TryEnterCommit()
        {
            lock (boundaryLock)
            {
                if (Volatile.Read(ref cancelRequested) != 0 ||
                    Volatile.Read(ref completed) != 0)
                {
                    return false;
                }

                Volatile.Write(ref commitStarted, 1);
                return true;
            }
        }

        public WriterCancelResponse RequestCancel()
        {
            lock (boundaryLock)
            {
                if (Volatile.Read(ref commitStarted) != 0 ||
                    Volatile.Read(ref completed) != 0)
                {
                    return new WriterCancelResponse(
                        WriterCancelOutcome.AlreadyAtCommitBoundary,
                        WriterReceiptStatus.Pending);
                }

                if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
                {
                    try
                    {
                        cancellation.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Actor disposal wins the race. The completed item
                        // cannot be committed after this boundary anyway.
                    }
                }
            }

            return new WriterCancelResponse(
                WriterCancelOutcome.Requested,
                WriterReceiptStatus.Pending);
        }

        public void MarkCompleted()
        {
            lock (boundaryLock)
            {
                Volatile.Write(ref completed, 1);
            }
        }

        public void CompleteUnavailable()
        {
            MarkCompleted();
            Completion.TrySetResult(UnavailableResponse());
        }

        public void Dispose() => cancellation.Dispose();
    }

    // The profile is fixed when the actor is created; a future adapter binds
    // its persistence and endpoint to this value during serial integration.
    internal string ProfileScope => profileScope;
}
