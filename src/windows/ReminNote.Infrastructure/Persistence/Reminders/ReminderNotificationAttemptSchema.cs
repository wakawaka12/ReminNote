using System.Data;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// Additive P3-04 storage shape for the append-only delivery-attempt event
/// stream. Production creates it through the formal P3-04 EF migration;
/// InitializeAsync is reserved for isolated fixtures and never targets an
/// Active database behind the P2.75 gate.
/// </summary>
public static class ReminderNotificationAttemptSchema
{
    public const string TableName = "notification_delivery_attempt_events";

    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS notification_delivery_attempt_events (
            profile_scope TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            event_ordinal INTEGER NOT NULL,
            instance_id TEXT NOT NULL,
            schedule_id TEXT NOT NULL,
            rule_id TEXT NOT NULL,
            occurrence_id TEXT NOT NULL,
            logical_reminder_id TEXT NOT NULL,
            channel TEXT NOT NULL,
            correlation_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            purpose_snapshot TEXT NOT NULL,
            priority_snapshot TEXT NOT NULL,
            pinned_snapshot INTEGER NOT NULL,
            trigger_attempt_ordinal INTEGER NOT NULL,
            triggered_at_utc TEXT NOT NULL,
            scheduled_trigger_at_utc TEXT NULL,
            attempt_number INTEGER NOT NULL,
            state TEXT NOT NULL,
            outcome TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            next_attempt_at_utc TEXT NULL,
            error_code TEXT NULL,
            retryable INTEGER NOT NULL,
            revision INTEGER NOT NULL,
            journal_batch_id TEXT NOT NULL,
            receipt_id TEXT NOT NULL,
            PRIMARY KEY (profile_scope, attempt_id, event_ordinal),
            FOREIGN KEY (profile_scope)
                REFERENCES revision_state (profile_scope)
                ON DELETE RESTRICT,
            CONSTRAINT ck_notification_attempt_profile_scope
                CHECK (length(profile_scope) BETWEEN 1 AND 67),
            CONSTRAINT ck_notification_attempt_id_uuid_v7
                CHECK (length(attempt_id) = 36 AND attempt_id = lower(attempt_id)
                    AND attempt_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(attempt_id, 9, 1) = '-'
                    AND substr(attempt_id, 14, 1) = '-'
                    AND substr(attempt_id, 19, 1) = '-'
                    AND substr(attempt_id, 24, 1) = '-'
                    AND substr(attempt_id, 15, 1) = '7'
                    AND substr(attempt_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_event_ordinal
                CHECK (event_ordinal >= 0),
            CONSTRAINT ck_notification_attempt_instance_uuid_v7
                CHECK (length(instance_id) = 36 AND instance_id = lower(instance_id)
                    AND instance_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(instance_id, 9, 1) = '-'
                    AND substr(instance_id, 14, 1) = '-'
                    AND substr(instance_id, 19, 1) = '-'
                    AND substr(instance_id, 24, 1) = '-'
                    AND substr(instance_id, 15, 1) = '7'
                    AND substr(instance_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_schedule_uuid_v7
                CHECK (length(schedule_id) = 36 AND schedule_id = lower(schedule_id)
                    AND schedule_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(schedule_id, 9, 1) = '-'
                    AND substr(schedule_id, 14, 1) = '-'
                    AND substr(schedule_id, 19, 1) = '-'
                    AND substr(schedule_id, 24, 1) = '-'
                    AND substr(schedule_id, 15, 1) = '7'
                    AND substr(schedule_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_rule_uuid_v7
                CHECK (length(rule_id) = 36 AND rule_id = lower(rule_id)
                    AND rule_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(rule_id, 9, 1) = '-'
                    AND substr(rule_id, 14, 1) = '-'
                    AND substr(rule_id, 19, 1) = '-'
                    AND substr(rule_id, 24, 1) = '-'
                    AND substr(rule_id, 15, 1) = '7'
                    AND substr(rule_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_occurrence_uuid_v7
                CHECK (length(occurrence_id) = 36 AND occurrence_id = lower(occurrence_id)
                    AND occurrence_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(occurrence_id, 9, 1) = '-'
                    AND substr(occurrence_id, 14, 1) = '-'
                    AND substr(occurrence_id, 19, 1) = '-'
                    AND substr(occurrence_id, 24, 1) = '-'
                    AND substr(occurrence_id, 15, 1) = '7'
                    AND substr(occurrence_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_logical_uuid_v7
                CHECK (length(logical_reminder_id) = 36 AND logical_reminder_id = lower(logical_reminder_id)
                    AND logical_reminder_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(logical_reminder_id, 9, 1) = '-'
                    AND substr(logical_reminder_id, 14, 1) = '-'
                    AND substr(logical_reminder_id, 19, 1) = '-'
                    AND substr(logical_reminder_id, 24, 1) = '-'
                    AND substr(logical_reminder_id, 15, 1) = '7'
                    AND substr(logical_reminder_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_channel
                CHECK (channel IN ('TOAST', 'TRAY', 'WIDGET', 'SOUND', 'WAKE_TIMER')),
            CONSTRAINT ck_notification_attempt_correlation_uuid_v7
                CHECK (length(correlation_id) = 36 AND correlation_id = lower(correlation_id)
                    AND correlation_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(correlation_id, 9, 1) = '-'
                    AND substr(correlation_id, 14, 1) = '-'
                    AND substr(correlation_id, 19, 1) = '-'
                    AND substr(correlation_id, 24, 1) = '-'
                    AND substr(correlation_id, 15, 1) = '7'
                    AND substr(correlation_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_request_uuid_v7
                CHECK (length(request_id) = 36 AND request_id = lower(request_id)
                    AND request_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(request_id, 9, 1) = '-'
                    AND substr(request_id, 14, 1) = '-'
                    AND substr(request_id, 19, 1) = '-'
                    AND substr(request_id, 24, 1) = '-'
                    AND substr(request_id, 15, 1) = '7'
                    AND substr(request_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_idempotency_uuid_v7
                CHECK (length(idempotency_key) = 36 AND idempotency_key = lower(idempotency_key)
                    AND idempotency_key NOT GLOB '*[^0-9a-f-]*'
                    AND substr(idempotency_key, 9, 1) = '-'
                    AND substr(idempotency_key, 14, 1) = '-'
                    AND substr(idempotency_key, 19, 1) = '-'
                    AND substr(idempotency_key, 24, 1) = '-'
                    AND substr(idempotency_key, 15, 1) = '7'
                    AND substr(idempotency_key, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_purpose
                CHECK (length(purpose_snapshot) BETWEEN 1 AND 64),
            CONSTRAINT ck_notification_attempt_priority
                CHECK (priority_snapshot IN ('LOW', 'NORMAL', 'HIGH')),
            CONSTRAINT ck_notification_attempt_pinned
                CHECK (pinned_snapshot IN (0, 1)),
            CONSTRAINT ck_notification_attempt_trigger_ordinal
                CHECK (trigger_attempt_ordinal >= 1),
            CONSTRAINT ck_notification_attempt_triggered_at
                CHECK (length(triggered_at_utc) = 30),
            CONSTRAINT ck_notification_attempt_scheduled_at
                CHECK (scheduled_trigger_at_utc IS NULL OR length(scheduled_trigger_at_utc) = 30),
            CONSTRAINT ck_notification_attempt_number
                CHECK (attempt_number >= 1),
            CONSTRAINT ck_notification_attempt_state
                CHECK (state IN ('PENDING', 'DELIVERED', 'BLOCKED', 'UNAVAILABLE',
                    'FAILED', 'SUPPRESSED_QUIET_HOURS', 'NOT_ATTEMPTED')),
            CONSTRAINT ck_notification_attempt_outcome
                CHECK (outcome IS NULL OR outcome IN ('DELIVERED', 'BLOCKED', 'UNAVAILABLE',
                    'FAILED', 'SUPPRESSED_QUIET_HOURS', 'NOT_ATTEMPTED')),
            CONSTRAINT ck_notification_attempt_state_shape
                CHECK ((state = 'PENDING' AND outcome IS NULL AND error_code IS NULL
                        AND retryable = 0 AND next_attempt_at_utc IS NULL)
                    OR (state <> 'PENDING' AND outcome IS NOT NULL
                        AND ((state = 'DELIVERED' AND outcome = 'DELIVERED')
                            OR (state = 'BLOCKED' AND outcome = 'BLOCKED')
                            OR (state = 'UNAVAILABLE' AND outcome = 'UNAVAILABLE')
                            OR (state = 'FAILED' AND outcome = 'FAILED')
                            OR (state = 'SUPPRESSED_QUIET_HOURS' AND outcome = 'SUPPRESSED_QUIET_HOURS')
                            OR (state = 'NOT_ATTEMPTED' AND outcome = 'NOT_ATTEMPTED'))
                        AND (state = 'DELIVERED' AND error_code IS NULL
                            OR state <> 'DELIVERED' AND error_code IS NOT NULL)
                        AND (retryable = 1 OR next_attempt_at_utc IS NULL))),
            CONSTRAINT ck_notification_attempt_created_at
                CHECK (length(created_at_utc) = 30),
            CONSTRAINT ck_notification_attempt_updated_at
                CHECK (length(updated_at_utc) = 30 AND updated_at_utc >= created_at_utc),
            CONSTRAINT ck_notification_attempt_next_at
                CHECK (next_attempt_at_utc IS NULL OR length(next_attempt_at_utc) = 30),
            CONSTRAINT ck_notification_attempt_error_code
                CHECK (error_code IS NULL OR length(error_code) BETWEEN 1 AND 96),
            CONSTRAINT ck_notification_attempt_retryable
                CHECK (retryable IN (0, 1)),
            CONSTRAINT ck_notification_attempt_revision
                CHECK (revision > 0),
            CONSTRAINT ck_notification_attempt_journal_uuid_v7
                CHECK (length(journal_batch_id) = 36 AND journal_batch_id = lower(journal_batch_id)
                    AND journal_batch_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(journal_batch_id, 9, 1) = '-'
                    AND substr(journal_batch_id, 14, 1) = '-'
                    AND substr(journal_batch_id, 19, 1) = '-'
                    AND substr(journal_batch_id, 24, 1) = '-'
                    AND substr(journal_batch_id, 15, 1) = '7'
                    AND substr(journal_batch_id, 20, 1) IN ('8', '9', 'a', 'b')),
            CONSTRAINT ck_notification_attempt_receipt_uuid_v7
                CHECK (length(receipt_id) = 36 AND receipt_id = lower(receipt_id)
                    AND receipt_id NOT GLOB '*[^0-9a-f-]*'
                    AND substr(receipt_id, 9, 1) = '-'
                    AND substr(receipt_id, 14, 1) = '-'
                    AND substr(receipt_id, 19, 1) = '-'
                    AND substr(receipt_id, 24, 1) = '-'
                    AND substr(receipt_id, 15, 1) = '7'
                    AND substr(receipt_id, 20, 1) IN ('8', '9', 'a', 'b'))
        )
        """;

    public const string CreateIndexesSql = """
        CREATE INDEX IF NOT EXISTS ix_notification_attempt_key
            ON notification_delivery_attempt_events
                (profile_scope, instance_id, channel, idempotency_key, event_ordinal);
        CREATE INDEX IF NOT EXISTS ix_notification_attempt_logical
            ON notification_delivery_attempt_events
                (profile_scope, instance_id, logical_reminder_id, channel, attempt_number, event_ordinal);
        CREATE INDEX IF NOT EXISTS ix_notification_attempt_recovery
            ON notification_delivery_attempt_events
                (profile_scope, state, retryable, next_attempt_at_utc);
        """;

    public static async ValueTask InitializeAsync(
        SqliteConnection connection,
        string profileScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            await ExecuteAsync(connection, CreateTableSql, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, CreateIndexesSql, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public static async ValueTask VerifyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", TableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                "The P3-04 notification delivery attempt migration is not installed.");
        }
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
