using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Explicitly constructed, isolated P2.5 persistence seam. It is not
/// registered by the current product and does not alter the P1/P2 DbContext.
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
            await P25StorageSchema.InitializeAsync(
                    connection,
                    profileScope,
                    requireWal,
                    cancellationToken)
                .ConfigureAwait(false);
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
