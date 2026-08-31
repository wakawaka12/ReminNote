using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Tests.P25Storage;

public sealed class P25FormalMigrationTests
{
    [Fact]
    public async Task AdditiveMigrationPreservesP2TablesAndEnablesReadOnlyWal()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reminnote-p25-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "reminnote.sqlite");
        var profileScope = ProtocolProfileScope.Derive(
            "S-1-5-21-100-200-300-400",
            Path.GetFullPath(databasePath));

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString();

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using (var context = ReminNoteDatabase.CreateContext(connection))
                {
                    await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                }

                await P25StorageSchema.EnsureProfileAsync(
                        connection,
                        profileScope,
                        cancellationToken: TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);

                Assert.Equal(
                    "wal",
                    await ScalarStringAsync(
                        connection,
                        "PRAGMA journal_mode;",
                        TestContext.Current.CancellationToken));

                var tables = await ReadTableNamesAsync(
                        connection,
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Contains("tasks", tables);
                Assert.Contains("task_history", tables);
                Assert.Contains("app_settings", tables);
                Assert.Contains("revision_state", tables);
                Assert.Contains("change_journal", tables);
                Assert.Contains("command_receipt", tables);
                Assert.Equal(
                    1L,
                    await ScalarLongAsync(
                        connection,
                        "SELECT COUNT(*) FROM revision_state WHERE profile_scope = $profileScope;",
                        TestContext.Current.CancellationToken,
                        command => command.Parameters.AddWithValue("$profileScope", profileScope)));
            }

            await using var readOnly = await P25ReadOnlyConnectionFactory
                .OpenAsync(databasePath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(
                1L,
                await ScalarLongAsync(
                    readOnly,
                    "PRAGMA query_only;",
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await using var command = readOnly.CreateCommand();
                command.CommandText =
                    "INSERT INTO revision_state (profile_scope, current_revision, oldest_available_revision) " +
                    "VALUES ('p1-read-only-test', 0, 0);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        Action<SqliteCommand>? configure = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
