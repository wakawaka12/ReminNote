using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Opens an existing P2.5 database in OS read-only mode and enables SQLite's
/// connection-local query-only guard. It never creates a missing file.
/// </summary>
public static class P25ReadOnlyConnectionFactory
{
    public static async ValueTask<SqliteConnection> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The read-only P2.5 database does not exist.", fullPath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "PRAGMA query_only = ON;", cancellationToken)
                .ConfigureAwait(false);

            var queryOnly = await ExecuteScalarLongAsync(
                    connection,
                    "PRAGMA query_only;",
                    cancellationToken)
                .ConfigureAwait(false);
            if (queryOnly != 1)
            {
                throw new InvalidOperationException("The SQLite read connection did not enable query_only.");
            }

            var foreignKeys = await ExecuteScalarLongAsync(
                    connection,
                    "PRAGMA foreign_keys;",
                    cancellationToken)
                .ConfigureAwait(false);
            if (foreignKeys != 1)
            {
                throw new InvalidOperationException("The SQLite read connection did not enable foreign_keys.");
            }

            var journalMode = await ExecuteScalarStringAsync(
                    connection,
                    "PRAGMA journal_mode;",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The SQLite read connection requires WAL, but reported '{journalMode}'.");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull or null
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
