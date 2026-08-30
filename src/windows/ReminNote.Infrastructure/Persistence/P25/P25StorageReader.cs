using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Read-only revision/change and snapshot adapter for one P2.5 profile.
/// Journal pages are bounded by revision and never split a revision batch.
/// </summary>
public sealed class P25StorageReader : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly string profileScope;
    private int disposed;

    private P25StorageReader(SqliteConnection connection, string profileScope)
    {
        this.connection = connection;
        this.profileScope = profileScope;
    }

    public static async ValueTask<P25StorageReader> OpenReadOnlyAsync(
        string databasePath,
        string profileScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        return new P25StorageReader(connection, profileScope);
    }

    public async ValueTask<P25RevisionState> ReadRevisionStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        var state = await ReadRevisionStateAsync(transaction, cancellationToken).ConfigureAwait(false);
        ValidateRevisionState(state);
        transaction.Commit();
        return state;
    }

    public async ValueTask<P25ChangesPage> GetChangesAsync(
        long afterRevision,
        int maxRevisions,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (afterRevision < 0 ||
            maxRevisions < 1 ||
            maxRevisions > P25StorageLimits.MaxChangesPageRevisions)
        {
            return P25ChangesPage.Invalid("ipc.request.invalid");
        }

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        try
        {
            var state = await ReadRevisionStateAsync(transaction, cancellationToken).ConfigureAwait(false);
            if (!IsRevisionStateValid(state))
            {
                return IntegrityFailure(state.CurrentRevision, state.CurrentRevision, "storage.integrity_failed");
            }

            if (afterRevision > state.CurrentRevision)
            {
                return new P25ChangesPage(
                    P25ChangesOutcome.Ahead,
                    state.CurrentRevision,
                    afterRevision,
                    afterRevision,
                    false,
                    false,
                    Array.Empty<P25RevisionBatch>(),
                    "revision.ahead");
            }

            if (afterRevision == state.CurrentRevision)
            {
                return new P25ChangesPage(
                    P25ChangesOutcome.Success,
                    state.CurrentRevision,
                    afterRevision,
                    afterRevision,
                    false,
                    false,
                    Array.Empty<P25RevisionBatch>(),
                    ErrorCode: null);
            }

            if (state.OldestAvailableRevision > 0 &&
                afterRevision < state.OldestAvailableRevision - 1)
            {
                return Gap(state.CurrentRevision, afterRevision);
            }

            var journalBounds = await ReadJournalBoundsAsync(transaction, cancellationToken).ConfigureAwait(false);
            if (!IsJournalBoundsValid(state, journalBounds))
            {
                return IntegrityFailure(state.CurrentRevision, afterRevision, "storage.integrity_failed");
            }

            var snapshotUpperBound = state.CurrentRevision;
            var revisions = await ReadCandidateRevisionsAsync(
                    transaction,
                    afterRevision,
                    snapshotUpperBound,
                    maxRevisions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (revisions.Count == 0)
            {
                return IntegrityFailure(snapshotUpperBound, afterRevision, "revision.gap");
            }

            var hasMore = revisions.Count > maxRevisions;
            if (hasMore)
            {
                revisions.RemoveAt(revisions.Count - 1);
            }

            var expectedRevision = checked(afterRevision + 1);
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                if (revision != expectedRevision)
                {
                    return Gap(snapshotUpperBound, afterRevision);
                }

                if (index < revisions.Count - 1)
                {
                    expectedRevision = checked(revision + 1);
                }
            }

            var lastRevision = revisions[^1];
            if (!hasMore && lastRevision != snapshotUpperBound)
            {
                return IntegrityFailure(snapshotUpperBound, afterRevision, "revision.gap");
            }

            var rows = await ReadJournalRowsAsync(
                    transaction,
                    afterRevision,
                    lastRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            var batches = BuildBatches(revisions, rows, snapshotUpperBound, afterRevision);
            var totalBytes = 0;
            foreach (var batch in batches)
            {
                var batchBytes = EstimateBatchBytes(batch);
                if (batchBytes > P25StorageLimits.MaxChangePageBytes)
                {
                    return new P25ChangesPage(
                        P25ChangesOutcome.BatchTooLarge,
                        snapshotUpperBound,
                        afterRevision,
                        afterRevision,
                        false,
                        false,
                        Array.Empty<P25RevisionBatch>(),
                        "revision.batch_too_large");
                }

                totalBytes = checked(totalBytes + batchBytes);
                if (totalBytes > P25StorageLimits.MaxChangePageBytes)
                {
                    return new P25ChangesPage(
                        P25ChangesOutcome.BatchTooLarge,
                        snapshotUpperBound,
                        afterRevision,
                        afterRevision,
                        false,
                        false,
                        Array.Empty<P25RevisionBatch>(),
                        "revision.batch_too_large");
                }
            }

            return new P25ChangesPage(
                P25ChangesOutcome.Success,
                snapshotUpperBound,
                afterRevision,
                lastRevision,
                hasMore,
                false,
                batches,
                ErrorCode: null);
        }
        catch (P25JournalIntegrityException exception)
        {
            return IntegrityFailure(
                exception.SnapshotUpperBound,
                exception.AfterRevision,
                exception.ErrorCode);
        }
        catch (SqliteException)
        {
            return IntegrityFailure(0, afterRevision, "storage.integrity_failed");
        }
    }

    public async ValueTask<P25ReadSnapshot<T>> ReadSnapshotAsync<T>(
        P25SnapshotHandler<T> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        var state = await ReadRevisionStateAsync(transaction, cancellationToken).ConfigureAwait(false);
        ValidateRevisionState(state);
        var context = new P25SnapshotContext(connection, transaction, state.CurrentRevision);
        var value = await handler(context, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return new P25ReadSnapshot<T>(state.CurrentRevision, value);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<P25RevisionState> ReadRevisionStateAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            connection,
            """
            SELECT profile_scope, current_revision, oldest_available_revision
            FROM revision_state
            WHERE profile_scope = $profileScope;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The P2.5 profile revision state is missing.");
        }

        return new P25RevisionState(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async ValueTask<(long Count, long? Min, long? Max)> ReadJournalBoundsAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            connection,
            """
            SELECT COUNT(*), MIN(revision), MAX(revision)
            FROM change_journal
            WHERE profile_scope = $profileScope;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The P2.5 journal bounds query returned no row.");
        }

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private async ValueTask<List<long>> ReadCandidateRevisionsAsync(
        SqliteTransaction transaction,
        long afterRevision,
        long snapshotUpperBound,
        int maxRevisions,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            connection,
            """
            SELECT revision
            FROM change_journal
            WHERE profile_scope = $profileScope
              AND revision > $afterRevision
              AND revision <= $snapshotUpperBound
            GROUP BY revision
            ORDER BY revision
            LIMIT $limit;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        command.Parameters.AddWithValue("$afterRevision", afterRevision);
        command.Parameters.AddWithValue("$snapshotUpperBound", snapshotUpperBound);
        command.Parameters.AddWithValue("$limit", maxRevisions + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var revisions = new List<long>(maxRevisions + 1);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(reader.GetInt64(0));
        }

        return revisions;
    }

    private async ValueTask<List<P25JournalRow>> ReadJournalRowsAsync(
        SqliteTransaction transaction,
        long afterRevision,
        long lastRevision,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            connection,
            """
            SELECT
                revision,
                change_ordinal,
                batch_id,
                entity_type,
                entity_id,
                change_kind
            FROM change_journal
            WHERE profile_scope = $profileScope
              AND revision > $afterRevision
              AND revision <= $lastRevision
            ORDER BY revision, change_ordinal;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        command.Parameters.AddWithValue("$afterRevision", afterRevision);
        command.Parameters.AddWithValue("$lastRevision", lastRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<P25JournalRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new P25JournalRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static List<P25RevisionBatch> BuildBatches(
        List<long> revisions,
        IReadOnlyList<P25JournalRow> rows,
        long snapshotUpperBound,
        long afterRevision)
    {
        var byRevision = rows
            .GroupBy(row => row.Revision)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var batches = new List<P25RevisionBatch>(revisions.Count);
        foreach (var revision in revisions)
        {
            if (!byRevision.TryGetValue(revision, out var revisionRows) || revisionRows.Length == 0)
            {
                throw new P25JournalIntegrityException(
                    snapshotUpperBound,
                    afterRevision,
                    "revision.gap");
            }

            var batchId = revisionRows[0].BatchId;
            if (string.IsNullOrWhiteSpace(batchId))
            {
                throw new P25JournalIntegrityException(
                    snapshotUpperBound,
                    afterRevision,
                    "storage.integrity_failed");
            }

            var changes = new List<P25JournalChange>(revisionRows.Length);
            for (var index = 0; index < revisionRows.Length; index++)
            {
                var row = revisionRows[index];
                if (row.ChangeOrdinal != index || row.BatchId != batchId)
                {
                    throw new P25JournalIntegrityException(
                        snapshotUpperBound,
                        afterRevision,
                        "storage.integrity_failed");
                }

                changes.Add(new P25JournalChange(row.EntityType, row.EntityId, row.ChangeKind));
            }

            batches.Add(new P25RevisionBatch(revision, batchId, changes));
        }

        return batches;
    }

    private static int EstimateBatchBytes(P25RevisionBatch batch)
    {
        var bytes = Encoding.UTF8.GetByteCount(batch.BatchId) + 64;
        foreach (var change in batch.Changes)
        {
            bytes = checked(bytes +
                Encoding.UTF8.GetByteCount(change.EntityType) +
                Encoding.UTF8.GetByteCount(change.EntityId) +
                Encoding.UTF8.GetByteCount(change.ChangeKind) +
                32);
        }

        return bytes;
    }

    private static bool IsRevisionStateValid(P25RevisionState state) =>
        state.CurrentRevision >= 0 &&
        state.OldestAvailableRevision >= 0 &&
        (state.CurrentRevision == 0
            ? state.OldestAvailableRevision == 0
            : state.OldestAvailableRevision > 0 &&
              state.OldestAvailableRevision <= state.CurrentRevision);

    private static void ValidateRevisionState(P25RevisionState state)
    {
        if (!IsRevisionStateValid(state))
        {
            throw new InvalidOperationException("The P2.5 revision state is inconsistent.");
        }
    }

    private static bool IsJournalBoundsValid(
        P25RevisionState state,
        (long Count, long? Min, long? Max) bounds) =>
        bounds.Count >= 0 &&
        (state.CurrentRevision == 0
            ? bounds.Count == 0
            : bounds.Count > 0 &&
              bounds.Min is not null &&
              bounds.Max is not null &&
              bounds.Min >= state.OldestAvailableRevision &&
              bounds.Max <= state.CurrentRevision);

    private static P25ChangesPage Gap(long snapshotUpperBound, long afterRevision) => new(
        P25ChangesOutcome.Gap,
        snapshotUpperBound,
        afterRevision,
        afterRevision,
        false,
        true,
        Array.Empty<P25RevisionBatch>(),
        "revision.gap");

    private static P25ChangesPage IntegrityFailure(
        long snapshotUpperBound,
        long afterRevision,
        string errorCode) => new(
        P25ChangesOutcome.IntegrityFailed,
        snapshotUpperBound,
        afterRevision,
        afterRevision,
        false,
        true,
        Array.Empty<P25RevisionBatch>(),
        errorCode);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    private sealed record P25JournalRow(
        long Revision,
        long ChangeOrdinal,
        string BatchId,
        string EntityType,
        string EntityId,
        string ChangeKind);

    private sealed class P25JournalIntegrityException : InvalidOperationException
    {
        public P25JournalIntegrityException(long snapshotUpperBound, long afterRevision, string errorCode)
            : base(errorCode)
        {
            SnapshotUpperBound = snapshotUpperBound;
            AfterRevision = afterRevision;
            ErrorCode = errorCode;
        }

        public long SnapshotUpperBound { get; }

        public long AfterRevision { get; }

        public string ErrorCode { get; }
    }
}
