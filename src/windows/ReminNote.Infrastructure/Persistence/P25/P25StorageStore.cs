using Microsoft.Data.Sqlite;
using System.Data;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Explicitly constructed P2.5 persistence owner. Agent runtime creates one
/// instance per resolved profile; it does not register a second UI writer or
/// alter the existing P1/P2 task tables beyond the additive migration.
/// </summary>
public sealed class P25StorageStore : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<ValueTask>? afterDomainCommit;
    private int disposed;

    private P25StorageStore(
        SqliteConnection connection,
        string profileScope,
        Func<DateTimeOffset> utcNow,
        P25StorageTestHooks? testHooks)
    {
        this.connection = connection;
        ProfileScope = profileScope;
        this.utcNow = utcNow;
        afterDomainCommit = testHooks?.AfterDomainCommit;
        Writer = new P25StorageWriter(this);
    }

    public string ProfileScope { get; }

    public P25StorageWriter Writer { get; }

    public static async ValueTask<P25StorageStore> OpenAsync(
        SqliteConnection connection,
        string profileScope,
        Func<DateTimeOffset>? utcNow = null,
        bool requireWal = true,
        bool initializeSchema = true,
        P25StorageTestHooks? testHooks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);

        utcNow ??= static () => DateTimeOffset.UtcNow;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (initializeSchema)
            {
                await P25StorageSchema.InitializeAsync(
                        connection,
                        profileScope,
                        requireWal,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await P25StorageSchema.EnsureProfileAsync(
                        connection,
                        profileScope,
                        requireWal,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new P25StorageStore(connection, profileScope, utcNow, testHooks);
    }

    internal SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return connection;
        }
    }

    internal Func<DateTimeOffset> UtcNow => utcNow;

    internal SemaphoreSlim WriterGate => writerGate;

    public async ValueTask<P25RevisionState> ReadRevisionStateAsync(
        CancellationToken cancellationToken = default)
    {
        using var transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using var command = P25StorageSql.CreateCommand(
            Connection,
            """
            SELECT profile_scope, current_revision, oldest_available_revision
            FROM revision_state
            WHERE profile_scope = $profileScope;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", ProfileScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The P2.5 profile revision state is missing.");
        }

        var state = new P25RevisionState(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
        transaction.Commit();
        return state;
    }

    public async ValueTask<string?> FindJournalEntityIdAsync(
        long revision,
        string entityType,
        string changeKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeKind);

        await using var command = P25StorageSql.CreateCommand(
            Connection,
            """
            SELECT entity_id
            FROM change_journal
            WHERE profile_scope = $profileScope
              AND revision = $revision
              AND entity_type = $entityType
              AND change_kind = $changeKind
            ORDER BY change_ordinal
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$profileScope", ProfileScope);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$changeKind", changeKind);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async ValueTask CommitDomainTransactionAsync(SqliteTransaction transaction)
    {
        transaction.Commit();
        if (afterDomainCommit is not null)
        {
            await afterDomainCommit().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        writerGate.Dispose();
    }
}
