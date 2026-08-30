using Microsoft.Data.Sqlite;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Tests.P25Storage;

internal sealed class P25StorageFixture : IAsyncDisposable
{
    private P25StorageFixture(
        string root,
        string databasePath,
        P25StorageStore store)
    {
        Root = root;
        DatabasePath = databasePath;
        Store = store;
    }

    public const string ProfileScope = "p25-test-profile";

    public const string UserSid = "S-1-5-21-1000-1000-1000-1000";

    public string Root { get; }

    public string DatabasePath { get; }

    public P25StorageStore Store { get; }

    public static async ValueTask<P25StorageFixture> CreateAsync(
        Func<ValueTask>? afterDomainCommit = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reminnote-p25-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "profile.sqlite");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };

        P25StorageStore? store = null;
        try
        {
            store = await P25StorageStore.OpenAsync(
                    new SqliteConnection(builder.ConnectionString),
                    ProfileScope,
                    testHooks: afterDomainCommit is null
                        ? null
                        : new P25StorageTestHooks(afterDomainCommit))
                .ConfigureAwait(false);
            var fixture = new P25StorageFixture(root, databasePath, store);
            await fixture.CreateDomainTablesAsync().ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            if (store is not null)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    public async ValueTask<SqliteConnection> OpenReadWriteConnectionAsync()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask ExecuteSqlAsync(
        string sql,
        Action<SqliteCommand>? configure = null)
    {
        await using var connection = await OpenReadWriteConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async ValueTask<T> ExecuteScalarAsync<T>(
        string sql,
        Func<SqliteDataReader, T> read,
        Action<SqliteCommand>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var connection = await OpenReadWriteConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The fixture query returned no row.");
        }

        return read(reader);
    }

    private async ValueTask CreateDomainTablesAsync()
    {
        await ExecuteSqlAsync(
            """
            CREATE TABLE p25_fixture_task (
                task_id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL,
                revision INTEGER NOT NULL,
                batch_id TEXT NOT NULL
            );
            """).ConfigureAwait(false);
        await ExecuteSqlAsync(
            """
            CREATE TABLE p25_fixture_history (
                history_id TEXT NOT NULL PRIMARY KEY,
                task_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                batch_id TEXT NOT NULL,
                FOREIGN KEY (task_id) REFERENCES p25_fixture_task (task_id)
            );
            """).ConfigureAwait(false);
    }
}
