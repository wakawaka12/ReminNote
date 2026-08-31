using System.Data;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Provides the test-fixture initializer and post-migration profile bootstrap
/// for P2.5 storage. Production first applies the formal EF migration and then
/// calls EnsureProfileAsync; InitializeAsync remains available for isolated
/// storage tests that intentionally do not use the product DbContext.
/// </summary>
public static class P25StorageSchema
{
    private static readonly string[] CreateStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS revision_state (
            profile_scope TEXT NOT NULL PRIMARY KEY,
            current_revision INTEGER NOT NULL CHECK (current_revision >= 0),
            oldest_available_revision INTEGER NOT NULL CHECK (oldest_available_revision >= 0),
            CHECK (
                oldest_available_revision = 0
                OR oldest_available_revision <= current_revision
            )
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS change_journal (
            profile_scope TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision > 0),
            change_ordinal INTEGER NOT NULL CHECK (change_ordinal >= 0),
            batch_id TEXT NOT NULL CHECK (length(batch_id) BETWEEN 1 AND 96),
            entity_type TEXT NOT NULL CHECK (length(entity_type) BETWEEN 1 AND 128),
            entity_id TEXT NOT NULL CHECK (length(entity_id) BETWEEN 1 AND 256),
            change_kind TEXT NOT NULL CHECK (length(change_kind) BETWEEN 1 AND 64),
            changed_at_utc TEXT NOT NULL CHECK (length(changed_at_utc) BETWEEN 1 AND 64),
            PRIMARY KEY (profile_scope, revision, change_ordinal),
            FOREIGN KEY (profile_scope)
                REFERENCES revision_state (profile_scope)
                ON DELETE RESTRICT
        )
        """,
        $"""
        CREATE TABLE IF NOT EXISTS command_receipt (
            actual_user_sid TEXT NOT NULL CHECK (length(actual_user_sid) BETWEEN 1 AND 256),
            profile_scope TEXT NOT NULL,
            idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) = 36),
            operation TEXT NOT NULL CHECK (length(operation) BETWEEN 1 AND 96),
            hash_version TEXT NOT NULL CHECK (length(hash_version) BETWEEN 1 AND 32),
            canonical_payload_hash BLOB NOT NULL CHECK (length(canonical_payload_hash) = 32),
            status TEXT NOT NULL CHECK (
                status IN (
                    'PENDING',
                    'COMMITTED',
                    'REJECTED_STALE',
                    'REJECTED',
                    'ROLLED_BACK',
                    'CANCELLED',
                    'TIMED_OUT',
                    'UNKNOWN'
                )
            ),
            changed INTEGER NOT NULL CHECK (changed IN (0, 1)),
            committed_revision INTEGER NULL CHECK (committed_revision IS NULL OR committed_revision >= 0),
            error_code TEXT NULL CHECK (error_code IS NULL OR length(error_code) BETWEEN 1 AND 160),
            first_accepted_at_utc TEXT NOT NULL CHECK (length(first_accepted_at_utc) BETWEEN 1 AND 64),
            updated_at_utc TEXT NOT NULL CHECK (length(updated_at_utc) BETWEEN 1 AND 64),
            first_request_id TEXT NOT NULL CHECK (length(first_request_id) = 36),
            last_request_id TEXT NOT NULL CHECK (length(last_request_id) = 36),
            attempt_count INTEGER NOT NULL CHECK (attempt_count BETWEEN 1 AND {P25StorageLimits.MaxReceiptAttempts}),
            agent_instance_id TEXT NULL CHECK (agent_instance_id IS NULL OR length(agent_instance_id) = 36),
            PRIMARY KEY (actual_user_sid, profile_scope, idempotency_key),
            FOREIGN KEY (profile_scope)
                REFERENCES revision_state (profile_scope)
                ON DELETE RESTRICT
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_change_journal_profile_revision
            ON change_journal (profile_scope, revision, change_ordinal)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_command_receipt_profile_key
            ON command_receipt (profile_scope, idempotency_key)
        """
    ];

    public static async ValueTask InitializeAsync(
        SqliteConnection connection,
        string profileScope,
        bool requireWal = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await ConfigureConnectionAsync(connection, requireWal, cancellationToken).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            foreach (var statement in CreateStatements)
            {
                await ExecuteNonQueryAsync(
                        connection,
                        statement,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO revision_state (
                        profile_scope,
                        current_revision,
                        oldest_available_revision
                    )
                    VALUES ($profileScope, 0, 0)
                    ON CONFLICT (profile_scope) DO NOTHING;
                    """,
                    transaction,
                    cancellationToken,
                    command => command.Parameters.AddWithValue("$profileScope", profileScope))
                .ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Configures an already migrated database and creates the resolved
    /// profile's initial revision row without issuing any DDL. Production
    /// composition calls this after EF has applied the additive migration;
    /// fixture composition continues to use InitializeAsync above.
    /// </summary>
    public static async ValueTask EnsureProfileAsync(
        SqliteConnection connection,
        string profileScope,
        bool requireWal = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await ConfigureConnectionAsync(connection, requireWal, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO revision_state (
                        profile_scope,
                        current_revision,
                        oldest_available_revision
                    )
                    VALUES ($profileScope, 0, 0)
                    ON CONFLICT (profile_scope) DO NOTHING;
                    """,
                    transaction,
                    cancellationToken,
                    command => command.Parameters.AddWithValue("$profileScope", profileScope))
                .ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        bool requireWal,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA foreign_keys = ON;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);

        var journalMode = await ExecuteScalarStringAsync(
                connection,
                "PRAGMA journal_mode = WAL;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (requireWal && !string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"P2.5 storage requires WAL, but SQLite reported '{journalMode}'.");
        }

        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA busy_timeout = 5000;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken,
        Action<SqliteCommand>? configure = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
