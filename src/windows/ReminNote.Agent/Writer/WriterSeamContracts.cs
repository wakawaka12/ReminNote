using ReminNote.Agent.Command;

namespace ReminNote.Agent.Writer;

/// <summary>
/// Non-runnable P2.5-03 seam. The eventual persistence adapter owns the
/// concrete transaction and must replace this boundary during integration.
/// </summary>
internal enum WriterTransactionKind
{
    Receipt,
    Domain
}

internal enum WriterReceiptStatus
{
    Pending,
    Committed,
    RejectedStale,
    Rejected,
    RolledBack,
    Cancelled,
    TimedOut,
    Unknown
}

internal enum WriterOutcome
{
    Changed,
    NoOp,
    Replayed,
    Stale,
    Rejected,
    RolledBack,
    Cancelled,
    Timeout,
    Pending,
    Unknown
}

internal enum WriterExecutionKind
{
    Changed,
    NoOp,
    Rejected
}

/// <summary>
/// Journal-safe metadata produced by a command executor. It deliberately has
/// no user content, SQL, wire fields, or persistence-specific entity data.
/// </summary>
internal sealed record WriterChange
{
    public WriterChange(string entityType, string entityId, string changeKind)
    {
        EntityType = RequireToken(entityType, nameof(entityType));
        EntityId = RequireToken(entityId, nameof(entityId));
        ChangeKind = RequireToken(changeKind, nameof(changeKind));
    }

    public string EntityType { get; }

    public string EntityId { get; }

    public string ChangeKind { get; }

    private static string RequireToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>
/// Internal command-execution result. The real P2.5-01 command contract is
/// intentionally not redefined here; this is only the writer test seam.
/// </summary>
internal sealed class WriterExecutionResult
{
    private WriterExecutionResult(
        WriterExecutionKind kind,
        IReadOnlyList<WriterChange> changes,
        string? errorCode)
    {
        Kind = kind;
        Changes = changes;
        ErrorCode = errorCode;
    }

    public WriterExecutionKind Kind { get; }

    public IReadOnlyList<WriterChange> Changes { get; }

    public string? ErrorCode { get; }

    public static WriterExecutionResult Changed(IReadOnlyList<WriterChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new ArgumentException(
                "A changed command must provide at least one change.",
                nameof(changes));
        }

        return new WriterExecutionResult(
            WriterExecutionKind.Changed,
            changes.ToArray(),
            errorCode: null);
    }

    public static WriterExecutionResult NoOp() =>
        new(
            WriterExecutionKind.NoOp,
            Array.Empty<WriterChange>(),
            errorCode: null);

    public static WriterExecutionResult Rejected(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new WriterExecutionResult(
            WriterExecutionKind.Rejected,
            Array.Empty<WriterChange>(),
            errorCode);
    }
}

/// <summary>
/// Durable receipt metadata used by the actor seam. Payload content is never
/// retained here; only the already-computed fixed-size hash is carried.
/// </summary>
internal sealed class WriterReceipt
{
    public WriterReceipt(
        string idempotencyKey,
        string operation,
        ReadOnlyMemory<byte> canonicalPayloadHash,
        WriterReceiptStatus status,
        bool? changed,
        long? committedRevision,
        string? errorCode,
        int attemptCount)
    {
        IdempotencyKey = RequireToken(idempotencyKey, nameof(idempotencyKey));
        Operation = RequireToken(operation, nameof(operation));
        if (canonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException(
                "A writer receipt hash must contain exactly 32 bytes.",
                nameof(canonicalPayloadHash));
        }

        if (committedRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(committedRevision),
                "A committed revision cannot be negative.");
        }

        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                "A receipt must have at least one attempt.");
        }

        CanonicalPayloadHash = canonicalPayloadHash.ToArray();
        Status = status;
        Changed = changed;
        CommittedRevision = committedRevision;
        ErrorCode = errorCode;
        AttemptCount = attemptCount;
    }

    public string IdempotencyKey { get; }

    public string Operation { get; }

    public ReadOnlyMemory<byte> CanonicalPayloadHash { get; }

    public WriterReceiptStatus Status { get; }

    public bool? Changed { get; }

    public long? CommittedRevision { get; }

    public string? ErrorCode { get; }

    public int AttemptCount { get; }

    public static WriterReceipt Pending(
        string idempotencyKey,
        string operation,
        ReadOnlyMemory<byte> canonicalPayloadHash,
        int attemptCount = 1) =>
        new(
            idempotencyKey,
            operation,
            canonicalPayloadHash,
            WriterReceiptStatus.Pending,
            changed: null,
            committedRevision: null,
            errorCode: null,
            attemptCount);

    public bool HasSameHash(ReadOnlySpan<byte> canonicalPayloadHash) =>
        CanonicalPayloadHash.Span.SequenceEqual(canonicalPayloadHash);

    public WriterReceipt WithStatus(
        WriterReceiptStatus status,
        bool? changed,
        long? committedRevision,
        string? errorCode,
        int? attemptCount = null) =>
        new(
            IdempotencyKey,
            Operation,
            CanonicalPayloadHash,
            status,
            changed,
            committedRevision,
            errorCode,
            attemptCount ?? AttemptCount);

    private static string RequireToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>
/// Internal actor result. It is not a wire response and intentionally omits
/// the frozen P2.5-01 envelope until the integration window supplies it.
/// </summary>
internal sealed record WriterResponse(
    bool Ok,
    WriterOutcome Outcome,
    bool Replayed,
    WriterReceiptStatus? ReceiptStatus,
    bool? Changed,
    long? CommittedRevision,
    long? CurrentRevision,
    string? ErrorCode,
    bool Retryable);

internal enum WriterCancelOutcome
{
    Requested,
    AlreadyAtCommitBoundary,
    NotFound
}

internal sealed record WriterCancelResponse(
    WriterCancelOutcome Outcome,
    WriterReceiptStatus? ReceiptStatus);

/// <summary>
/// Storage seam for the future approved P2.5-02 adapter. No implementation is
/// registered in the Agent host by this Slice.
/// </summary>
internal interface IWriterPersistence
{
    ValueTask<IWriterTransaction> BeginTransactionAsync(
        WriterTransactionKind kind,
        CancellationToken cancellationToken = default);

    ValueTask<WriterReceipt?> ReadReceiptAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Transaction seam used by the actor. The adapter is responsible for making
/// all methods part of one concrete storage transaction.
/// </summary>
internal interface IWriterTransaction : IAsyncDisposable
{
    ValueTask<WriterReceipt?> FindReceiptAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask StoreReceiptAsync(
        WriterReceipt receipt,
        CancellationToken cancellationToken = default);

    ValueTask<long> ReadCurrentRevisionAsync(
        CancellationToken cancellationToken = default);

    ValueTask PersistDomainChangesAsync(
        IReadOnlyList<WriterChange> changes,
        long committedRevision,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);

    void DiscardDirtyState();
}

internal sealed class WriterCommitCancelledException : OperationCanceledException
{
    public WriterCommitCancelledException()
        : base("The writer command was cancelled before its commit boundary.")
    {
    }
}

internal sealed class WriterCommandTimeoutException : Exception
{
    public WriterCommandTimeoutException()
        : base("The writer command timed out before a durable commit.")
    {
    }
}

internal sealed class WriterCommandUnknownException : Exception
{
    public WriterCommandUnknownException()
        : base("The writer command outcome could not be confirmed.")
    {
    }
}

internal sealed class WriterTransactionRecoveryException : Exception
{
    public WriterTransactionRecoveryException(
        Exception transactionException,
        Exception recoveryException)
        : base(
            "The writer transaction failed and its rollback/dirty-state cleanup was not fully confirmed.",
            transactionException)
    {
        RecoveryException = recoveryException;
    }

    public Exception RecoveryException { get; }
}
