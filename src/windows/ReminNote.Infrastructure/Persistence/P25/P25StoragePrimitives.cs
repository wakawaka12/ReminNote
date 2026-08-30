using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Bounds used by the isolated P2.5 storage seam. These values mirror the
/// frozen contract; they are not a transport implementation.
/// </summary>
public static class P25StorageLimits
{
    public const string CanonicalHashVersion = "rn-cj-1";

    public const int MaxOperationBytes = 96;

    public const int MaxChangesPageRevisions = 128;

    public const int MaxChangePageBytes = 262_144;

    public const int MaxRecoveryRetries = 1;

    // One initial domain attempt plus one serialized recovery retry.
    public const int MaxReceiptAttempts = 1 + MaxRecoveryRetries;
}

public enum P25ReceiptStatus
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

public enum P25MutationDecisionKind
{
    Changed,
    NoOp,
    Rejected,
    Cancelled,
    TimedOut,
    Unknown
}

public enum P25MutationOutcome
{
    Changed,
    NoOp,
    Replayed,
    Stale,
    Rejected,
    RolledBack,
    Cancelled,
    TimedOut,
    Pending,
    Unknown
}

public enum P25ChangesOutcome
{
    Success,
    Gap,
    Ahead,
    InvalidRequest,
    IntegrityFailed,
    BatchTooLarge
}

/// <summary>
/// The small durable identity used by the storage seam. It deliberately does
/// not model a public Core or wire DTO.
/// </summary>
public sealed record P25CommandRequest(
    string ActualUserSid,
    string ProfileScope,
    string IdempotencyKey,
    string Operation,
    string HashVersion,
    byte[] CanonicalPayloadHash,
    long ExpectedRevision,
    string RequestId,
    string? AgentInstanceId = null);

public sealed record P25JournalChange(
    string EntityType,
    string EntityId,
    string ChangeKind);

public sealed record P25RevisionState(
    string ProfileScope,
    long CurrentRevision,
    long OldestAvailableRevision);

public sealed record P25Receipt(
    string ActualUserSid,
    string ProfileScope,
    string IdempotencyKey,
    string Operation,
    string HashVersion,
    byte[] CanonicalPayloadHash,
    P25ReceiptStatus Status,
    bool Changed,
    long? CommittedRevision,
    string? ErrorCode,
    DateTimeOffset FirstAcceptedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string FirstRequestId,
    string LastRequestId,
    int AttemptCount,
    string? AgentInstanceId);

public sealed record P25WriteResult(
    P25MutationOutcome Outcome,
    P25ReceiptStatus Status,
    bool Changed,
    long? CommittedRevision,
    string? ErrorCode,
    bool Replayed,
    string? BatchId,
    P25Receipt? Receipt,
    bool ReceiptDurable);

public sealed record P25RevisionBatch(
    long Revision,
    string BatchId,
    IReadOnlyList<P25JournalChange> Changes);

public sealed record P25ChangesPage(
    P25ChangesOutcome Outcome,
    long SnapshotUpperBound,
    long FromExclusive,
    long ToInclusive,
    bool HasMore,
    bool FullRefreshRequired,
    IReadOnlyList<P25RevisionBatch> Batches,
    string? ErrorCode)
{
    public static P25ChangesPage Invalid(string errorCode) => new(
        P25ChangesOutcome.InvalidRequest,
        0,
        0,
        0,
        false,
        false,
        Array.Empty<P25RevisionBatch>(),
        errorCode);
}

public sealed record P25ReadSnapshot<T>(long SnapshotRevision, T Value);

public sealed record P25ReadModelState<T>(T Value, long LastSeenRevision);

/// <summary>
/// Optional fault injection for isolated storage tests. The current product
/// never supplies this seam and it does not alter normal writer behavior.
/// </summary>
public sealed record P25StorageTestHooks(Func<ValueTask>? AfterDomainCommit = null);

/// <summary>
/// A transaction-bound context handed to a test/integration domain adapter.
/// The adapter can write its isolated Task/History fixture rows using the same
/// transaction that the storage seam uses for revision, journal and receipt.
/// </summary>
public sealed class P25MutationContext
{
    internal P25MutationContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long currentRevision,
        long proposedRevision,
        string proposedBatchId)
    {
        Connection = connection;
        Transaction = transaction;
        CurrentRevision = currentRevision;
        ProposedRevision = proposedRevision;
        ProposedBatchId = proposedBatchId;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public long CurrentRevision { get; }

    public long ProposedRevision { get; }

    public string ProposedBatchId { get; }

    public SqliteCommand CreateCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        var command = Connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = Transaction;
        return command;
    }
}

public sealed class P25SnapshotContext
{
    internal P25SnapshotContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotRevision)
    {
        Connection = connection;
        Transaction = transaction;
        SnapshotRevision = snapshotRevision;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public long SnapshotRevision { get; }

    public SqliteCommand CreateCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        var command = Connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = Transaction;
        return command;
    }
}

public delegate ValueTask<P25MutationDecision> P25MutationHandler(
    P25MutationContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<T> P25SnapshotHandler<T>(
    P25SnapshotContext context,
    CancellationToken cancellationToken);

public sealed class P25MutationDecision
{
    private P25MutationDecision(
        P25MutationDecisionKind kind,
        IReadOnlyList<P25JournalChange> changes,
        string? errorCode)
    {
        Kind = kind;
        Changes = changes;
        ErrorCode = errorCode;
    }

    public P25MutationDecisionKind Kind { get; }

    public IReadOnlyList<P25JournalChange> Changes { get; }

    public string? ErrorCode { get; }

    public static P25MutationDecision Changed(IEnumerable<P25JournalChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return new(
            P25MutationDecisionKind.Changed,
            changes.ToArray(),
            errorCode: null);
    }

    public static P25MutationDecision NoOp() => new(
        P25MutationDecisionKind.NoOp,
        Array.Empty<P25JournalChange>(),
        errorCode: null);

    public static P25MutationDecision Rejected(string errorCode) => new(
        P25MutationDecisionKind.Rejected,
        Array.Empty<P25JournalChange>(),
        ValidateErrorCode(errorCode));

    public static P25MutationDecision Cancelled() => new(
        P25MutationDecisionKind.Cancelled,
        Array.Empty<P25JournalChange>(),
        "ipc.request.cancelled");

    public static P25MutationDecision TimedOut() => new(
        P25MutationDecisionKind.TimedOut,
        Array.Empty<P25JournalChange>(),
        "ipc.request.timeout");

    public static P25MutationDecision Unknown() => new(
        P25MutationDecisionKind.Unknown,
        Array.Empty<P25JournalChange>(),
        "storage.transaction_failed");

    internal void Validate()
    {
        switch (Kind)
        {
            case P25MutationDecisionKind.Changed:
                if (Changes.Count == 0)
                {
                    throw new InvalidOperationException(
                        "A changed P2.5 mutation must report at least one journal change.");
                }

                foreach (var change in Changes)
                {
                    if (change is null ||
                        string.IsNullOrWhiteSpace(change.EntityType) ||
                        string.IsNullOrWhiteSpace(change.EntityId) ||
                        string.IsNullOrWhiteSpace(change.ChangeKind))
                    {
                        throw new InvalidOperationException(
                            "Every P2.5 journal change must have bounded metadata.");
                    }
                }

                break;
            case P25MutationDecisionKind.NoOp:
                if (Changes.Count != 0 || ErrorCode is not null)
                {
                    throw new InvalidOperationException(
                        "A no-op P2.5 mutation cannot carry changes or an error.");
                }

                break;
            case P25MutationDecisionKind.Rejected:
                _ = ValidateErrorCode(ErrorCode);
                break;
            case P25MutationDecisionKind.Cancelled:
            case P25MutationDecisionKind.TimedOut:
            case P25MutationDecisionKind.Unknown:
                _ = ValidateErrorCode(ErrorCode);
                break;
            default:
                throw new InvalidOperationException("Unknown P2.5 mutation decision.");
        }
    }

    private static string ValidateErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 160)
        {
            throw new ArgumentException(
                "P2.5 error codes must be non-empty and bounded.",
                nameof(errorCode));
        }

        return errorCode;
    }
}

/// <summary>
/// In-memory atomic swap used by the isolated read-recovery tests. It models
/// the client-side rule that LastSeen advances only after a complete apply.
/// </summary>
public sealed class P25AtomicReadModel<T>
{
    private readonly object sync = new();
    private T value;
    private long lastSeenRevision;

    public P25AtomicReadModel(T initialValue, long initialLastSeenRevision = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialLastSeenRevision);

        value = initialValue;
        lastSeenRevision = initialLastSeenRevision;
    }

    public P25ReadModelState<T> Read()
    {
        lock (sync)
        {
            return new(value, lastSeenRevision);
        }
    }

    public bool TryApply(P25ReadSnapshot<T> snapshot, Func<T, T> completeApply)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(completeApply);

        lock (sync)
        {
            if (snapshot.SnapshotRevision < lastSeenRevision)
            {
                return false;
            }
        }

        T nextValue;
        try
        {
            nextValue = completeApply(snapshot.Value);
        }
        catch
        {
            return false;
        }

        lock (sync)
        {
            if (snapshot.SnapshotRevision < lastSeenRevision)
            {
                return false;
            }

            value = nextValue;
            lastSeenRevision = snapshot.SnapshotRevision;
            return true;
        }
    }
}

internal static class P25StorageValidation
{
    public static void ValidateRequest(P25CommandRequest request, string expectedProfileScope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileScope);

        if (!string.Equals(request.ProfileScope, expectedProfileScope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A command cannot cross the storage profile scope.",
                nameof(request));
        }

        ValidateBoundedText(request.ActualUserSid, 256, nameof(request.ActualUserSid));
        ValidateBoundedText(request.ProfileScope, 256, nameof(request.ProfileScope));
        ValidateCanonicalUuid(request.IdempotencyKey, nameof(request.IdempotencyKey));
        ValidateCanonicalUuid(request.RequestId, nameof(request.RequestId));
        if (request.AgentInstanceId is not null)
        {
            ValidateCanonicalUuid(request.AgentInstanceId, nameof(request.AgentInstanceId));
        }

        ValidateBoundedText(request.Operation, P25StorageLimits.MaxOperationBytes, nameof(request.Operation));
        if (request.HashVersion != P25StorageLimits.CanonicalHashVersion)
        {
            throw new ArgumentException(
                $"Only {P25StorageLimits.CanonicalHashVersion} is supported by this seam.",
                nameof(request));
        }

        if (request.CanonicalPayloadHash is null || request.CanonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException(
                "The canonical payload hash must contain exactly 32 bytes.",
                nameof(request));
        }

        if (request.ExpectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public static void ValidateCanonicalUuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be a lower-case UUID in D format.",
                parameterName);
        }
    }

    public static void ValidateJournalChange(P25JournalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateBoundedText(change.EntityType, 128, nameof(change.EntityType));
        ValidateBoundedText(change.EntityId, 256, nameof(change.EntityId));
        ValidateBoundedText(change.ChangeKind, 64, nameof(change.ChangeKind));
    }

    private static void ValidateBoundedText(string value, int maxBytes, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            System.Text.Encoding.UTF8.GetByteCount(value) > maxBytes ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The value must be non-empty, bounded UTF-8 text without controls.",
                parameterName);
        }
    }
}
