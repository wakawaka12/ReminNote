using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// Production P3-04 attempt store. Every append is executed through
/// <see cref="P25StorageWriter"/>, so the attempt event, P2.5 receipt,
/// change-journal row and global revision share one SQLite transaction.
/// Reads use the already-open Agent connection and never create a writer.
/// </summary>
public sealed class SqliteNotificationDeliveryAttemptStore : INotificationDeliveryAttemptJournalStore
{
    private const string PendingOperation = "reminder.notification.pending";
    private const string OutcomeOperation = "reminder.notification.outcome";
    private const string DirectOperation = "reminder.notification.append";

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private readonly P25StorageStore storage;
    private readonly string actualUserSid;
    private readonly Guid? agentInstanceId;

    public SqliteNotificationDeliveryAttemptStore(
        P25StorageStore storage,
        string actualUserSid,
        Guid? agentInstanceId = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (string.IsNullOrWhiteSpace(actualUserSid) || actualUserSid.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded user SID is required.", nameof(actualUserSid));
        }

        this.actualUserSid = actualUserSid;
        if (agentInstanceId is { } id)
        {
            RequireUuidV7(id, nameof(agentInstanceId));
        }

        this.agentInstanceId = agentInstanceId;
    }

    /// <summary>
    /// Test-fixture-only schema bootstrap. Production must rely on the formal
    /// P3-04 migration and call VerifySchemaAsync instead.
    /// </summary>
    public ValueTask InitializeSchemaForFixtureAsync(
        CancellationToken cancellationToken = default) =>
        ReminderNotificationAttemptSchema.InitializeAsync(
            storage.Connection,
            storage.ProfileScope,
            cancellationToken);

    public ValueTask VerifySchemaAsync(
        CancellationToken cancellationToken = default) =>
        ReminderNotificationAttemptSchema.VerifyAsync(
            storage.Connection,
            cancellationToken);

    public async ValueTask<NotificationDeliveryAttemptRecord?> FindByIdempotencyKeyAsync(
        Guid instanceId,
        NotificationChannelId channelId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireUuidV7(instanceId, nameof(instanceId));
        RequireUuidV7(idempotencyKey, nameof(idempotencyKey));
        NotificationChannels.RequireKnown(channelId);
        return await ReadOneAsync(
                $"""
                SELECT {SelectColumns}
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
                  AND instance_id = $instanceId
                  AND channel = $channel
                  AND idempotency_key = $idempotencyKey
                ORDER BY event_ordinal DESC
                LIMIT 1;
                """,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$instanceId", FormatGuid(instanceId));
                    command.Parameters.AddWithValue("$channel", channelId.Value);
                    command.Parameters.AddWithValue("$idempotencyKey", FormatGuid(idempotencyKey));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<NotificationDeliveryAttemptRecord?> FindCurrentAsync(
        Guid instanceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        RequireUuidV7(instanceId, nameof(instanceId));
        RequireUuidV7(logicalReminderId, nameof(logicalReminderId));
        NotificationChannels.RequireKnown(channelId);
        return await ReadOneAsync(
                $"""
                SELECT {SelectColumns}
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
                  AND instance_id = $instanceId
                  AND logical_reminder_id = $logicalReminderId
                  AND channel = $channel
                ORDER BY attempt_number DESC, event_ordinal DESC
                LIMIT 1;
                """,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$instanceId", FormatGuid(instanceId));
                    command.Parameters.AddWithValue("$logicalReminderId", FormatGuid(logicalReminderId));
                    command.Parameters.AddWithValue("$channel", channelId.Value);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<NotificationDeliveryPendingResult> AppendPendingAsync(
        NotificationDeliveryRequest request,
        int maxAttempts,
        Instant recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxAttempts < 1 || maxAttempts > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var payload = BuildPendingPayload(request, maxAttempts);
        var preferredCommandKey = request.IdempotencyKey;
        var activeCommandKey = preferredCommandKey;
        var result = await ExecuteMutationWithRevisionRetryAsync(
                PendingOperation,
                preferredCommandKey,
                payload,
                async (context, token) =>
                {
                    var exact = await ReadByIdempotencyKeyAsync(
                            request.IdempotencyKey,
                            context.Transaction,
                            token)
                        .ConfigureAwait(false);
                    if (exact is not null)
                    {
                        return exact.MatchesIntent(request)
                            ? P25MutationDecision.NoOp()
                            : P25MutationDecision.Rejected(NotificationErrorCodes.IdempotencyConflict);
                    }

                    var current = await ReadCurrentAsync(
                            request.CoreTrigger.InstanceId,
                            request.CoreTrigger.LogicalReminderId,
                            request.ChannelId,
                            context.Transaction,
                            token)
                        .ConfigureAwait(false);
                    if (current is not null)
                    {
                        var quietHoursSuppression =
                            current.Outcome == NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS;
                        if (current.IsPending ||
                            !current.Retryable ||
                            current.NextAttemptAtUtc is not { } next ||
                            next > recordedAtUtc ||
                            !quietHoursSuppression && current.AttemptNumber >= maxAttempts)
                        {
                            return P25MutationDecision.NoOp();
                        }
                    }

                    var attemptNumber = current is null ? 1 : checked(current.AttemptNumber + 1);
                    var pending = NotificationDeliveryAttemptRecord.Pending(
                        request,
                        Guid.CreateVersion7(),
                        attemptNumber,
                        recordedAtUtc);
                    var durable = pending.WithDurability(
                        context.ProposedRevision,
                        Guid.Parse(context.ProposedBatchId),
                        activeCommandKey,
                        eventOrdinal: 0);
                    await InsertAsync(
                            durable,
                            context.Transaction,
                            token)
                        .ConfigureAwait(false);
                    return P25MutationDecision.Changed(
                        [new P25JournalChange(
                            "notification_delivery_attempt",
                            durable.AttemptId.ToString("D"),
                            "pending")]);
                },
                cancellationToken,
                commandKey => activeCommandKey = commandKey)
            .ConfigureAwait(false);
        ThrowIfMutationFailed(result);

        var exactRecord = await FindByIdempotencyKeyAsync(
                request.CoreTrigger.InstanceId,
                request.ChannelId,
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (exactRecord is not null)
        {
            EnsureMatchesIntent(exactRecord, request);
            return new(
                result.Changed && !result.Replayed
                    ? NotificationDeliveryPendingDisposition.APPENDED
                    : NotificationDeliveryPendingDisposition.REPLAYED,
                exactRecord);
        }

        var currentRecord = await FindCurrentAsync(
                request.CoreTrigger.InstanceId,
                request.CoreTrigger.LogicalReminderId,
                request.ChannelId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The committed notification pending event could not be read back.");
        var disposition = currentRecord.IsPending
            ? result.Changed
                ? NotificationDeliveryPendingDisposition.APPENDED
                : NotificationDeliveryPendingDisposition.REPLAYED
            : currentRecord.Retryable &&
              currentRecord.NextAttemptAtUtc is { } retryAt &&
              retryAt > recordedAtUtc
                ? NotificationDeliveryPendingDisposition.DEFERRED
                : NotificationDeliveryPendingDisposition.TERMINAL;
        return new(disposition, currentRecord);
    }

    public async ValueTask<NotificationDeliveryAttemptRecord> AppendOutcomeAsync(
        NotificationDeliveryAttemptRecord pending,
        NotificationChannelDeliveryResponse response,
        bool retryable,
        Instant recordedAtUtc,
        Instant? nextAttemptAtUtc,
        Guid? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(response);
        if (!pending.IsPending)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "Only a pending event can be completed.",
                nameof(pending));
        }

        var effectiveRequestId = requestId ?? pending.RequestId;
        RequireUuidV7(effectiveRequestId, nameof(requestId));
        var commandKey = Guid.CreateVersion7();
        var activeCommandKey = commandKey;
        var payload = BuildOutcomePayload(
            pending,
            response,
            retryable,
            nextAttemptAtUtc,
            effectiveRequestId);
        var result = await ExecuteMutationWithRevisionRetryAsync(
                OutcomeOperation,
                commandKey,
                payload,
                async (context, token) =>
                {
                    var current = await ReadLatestByAttemptIdAsync(
                            pending.AttemptId,
                            context.Transaction,
                            token)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        return P25MutationDecision.Rejected(NotificationErrorCodes.IdempotencyConflict);
                    }

                    if (!current.IsPending)
                    {
                        return P25MutationDecision.NoOp();
                    }

                    if (!SameAttemptIntent(current, pending))
                    {
                        return P25MutationDecision.Rejected(NotificationErrorCodes.IdempotencyConflict);
                    }

                    var durable = current.Complete(
                        response,
                        recordedAtUtc,
                        retryable,
                        nextAttemptAtUtc,
                        context.ProposedRevision,
                        Guid.Parse(context.ProposedBatchId),
                        activeCommandKey,
                        checked(current.EventOrdinal + 1),
                        effectiveRequestId);
                    await InsertAsync(durable, context.Transaction, token).ConfigureAwait(false);
                    return P25MutationDecision.Changed(
                        [new P25JournalChange(
                            "notification_delivery_attempt",
                            durable.AttemptId.ToString("D"),
                            "outcome")]);
                },
                cancellationToken,
                commandKey => activeCommandKey = commandKey)
            .ConfigureAwait(false);
        ThrowIfMutationFailed(result);

        return await ReadLatestByAttemptIdAsync(
                pending.AttemptId,
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The committed notification outcome event could not be read back.");
    }

    public async ValueTask<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListRecoverableAsync(
        Instant evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            WITH ranked AS (
                SELECT {SelectColumns},
                       ROW_NUMBER() OVER (
                           PARTITION BY instance_id, logical_reminder_id, channel
                           ORDER BY attempt_number DESC, event_ordinal DESC) AS rank_no
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
            )
            SELECT {SelectColumnsFromRanked}
            FROM ranked
            WHERE rank_no = 1
              AND (state = 'PENDING'
                   OR (retryable = 1 AND next_attempt_at_utc IS NOT NULL
                       AND next_attempt_at_utc <= $evaluatedAtUtc))
            ORDER BY updated_at_utc, attempt_number, attempt_id;
            """;
        return await ReadManyAsync(
                sql,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$evaluatedAtUtc", FormatInstant(evaluatedAtUtc));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            WITH ranked AS (
                SELECT {SelectColumns},
                       ROW_NUMBER() OVER (
                           PARTITION BY instance_id, logical_reminder_id, channel
                           ORDER BY attempt_number DESC, event_ordinal DESC) AS rank_no
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
            )
            SELECT {SelectColumnsFromRanked}
            FROM ranked
            WHERE rank_no = 1 AND state = 'PENDING'
            ORDER BY updated_at_utc, attempt_number, attempt_id;
            """;
        return await ReadManyAsync(
                sql,
                AddScope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<NotificationDeliveryAttempt?> FindAsync(
        Guid instanceId,
        NotificationChannelId channelId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var record = await FindByIdempotencyKeyAsync(
                instanceId,
                channelId,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        return record?.IsTerminal == true ? record.ToContractAttempt() : null;
    }

    public async ValueTask<NotificationDeliveryAppendResult> AppendAsync(
        NotificationDeliveryAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var existing = await FindByIdempotencyKeyAsync(
                attempt.InstanceId,
                attempt.ChannelId,
                attempt.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var existingAttempt = existing.IsTerminal
                ? existing.ToContractAttempt()
                : throw new InvalidOperationException(
                    "The legacy attempt store seam cannot append over a pending event.");
            return NotificationDeliveryAppendResult.Replayed(existingAttempt);
        }

        var current = await FindCurrentAsync(
                attempt.InstanceId,
                attempt.LogicalReminderId,
                attempt.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        var state = ToState(attempt.Outcome);
        var errorCode = attempt.ErrorCode ??
            (attempt.Outcome == NotificationDeliveryOutcome.DELIVERED
                ? null
                : NotificationErrorCodes.DeliveryFailed);
        var record = NotificationDeliveryAttemptRecord.Rehydrate(
            attempt.AttemptId,
            attempt.InstanceId,
            attempt.LogicalReminderId,
            attempt.ChannelId,
            attempt.CorrelationId,
            attempt.CorrelationId,
            attempt.IdempotencyKey,
            attempt.PurposeSnapshot,
            attempt.PrioritySnapshot,
            attempt.PinnedSnapshot,
            current is null ? 1 : checked(current.AttemptNumber + 1),
            state,
            attempt.Outcome,
            attempt.AttemptedAtUtc,
            attempt.AttemptedAtUtc,
            nextAttemptAtUtc: null,
            errorCode,
            retryable: false,
            revision: 0,
            journalBatchId: null,
            receiptId: null,
            eventOrdinal: current is null ? 0 : checked(current.EventOrdinal + 1));
        var storageKey = Guid.CreateVersion7();
        var activeStorageKey = storageKey;
        var payload = BuildDirectPayload(record);
        var result = await ExecuteMutationWithRevisionRetryAsync(
                DirectOperation,
                storageKey,
                payload,
                async (context, token) =>
                {
                    var duplicate = await ReadByIdempotencyKeyAsync(
                            attempt.IdempotencyKey,
                            context.Transaction,
                            token)
                        .ConfigureAwait(false);
                    if (duplicate is not null)
                    {
                        return duplicate.MatchesIntent(
                                new NotificationDeliveryRequest(
                                    new NotificationTriggerFact(
                                        attempt.InstanceId,
                                        attempt.InstanceId,
                                        attempt.InstanceId,
                                        attempt.InstanceId,
                                        attempt.LogicalReminderId,
                                        1,
                                        attempt.PurposeSnapshot,
                                        attempt.PrioritySnapshot,
                                        attempt.PinnedSnapshot,
                                        attempt.AttemptedAtUtc),
                                    attempt.ChannelId,
                                    attempt.CorrelationId,
                                    attempt.IdempotencyKey,
                                    attempt.CorrelationId))
                            ? P25MutationDecision.NoOp()
                            : P25MutationDecision.Rejected(NotificationErrorCodes.IdempotencyConflict);
                    }

                    var durable = record.WithDurability(
                        context.ProposedRevision,
                        Guid.Parse(context.ProposedBatchId),
                        activeStorageKey,
                        record.EventOrdinal);
                    await InsertAsync(durable, context.Transaction, token).ConfigureAwait(false);
                    return P25MutationDecision.Changed(
                        [new P25JournalChange(
                            "notification_delivery_attempt",
                            durable.AttemptId.ToString("D"),
                            "outcome")]);
                },
                cancellationToken,
                commandKey => activeStorageKey = commandKey)
            .ConfigureAwait(false);
        ThrowIfMutationFailed(result);
        var persisted = await FindByIdempotencyKeyAsync(
                attempt.InstanceId,
                attempt.ChannelId,
                attempt.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The notification attempt could not be read back.");
        return result.Changed
            ? NotificationDeliveryAppendResult.Appended(persisted.ToContractAttempt())
            : NotificationDeliveryAppendResult.Replayed(persisted.ToContractAttempt());
    }

    public async ValueTask<IReadOnlyList<NotificationDeliveryAttempt>> ListForInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        RequireUuidV7(instanceId, nameof(instanceId));
        var sql = $"""
            WITH ranked AS (
                SELECT {SelectColumns},
                       ROW_NUMBER() OVER (
                           PARTITION BY attempt_id
                           ORDER BY event_ordinal DESC) AS rank_no
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope AND instance_id = $instanceId
            )
            SELECT {SelectColumnsFromRanked}
            FROM ranked
            WHERE rank_no = 1 AND state <> 'PENDING'
            ORDER BY updated_at_utc, attempt_number, attempt_id;
            """;
        var records = await ReadManyAsync(
                sql,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$instanceId", FormatGuid(instanceId));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return records.Select(record => record.ToContractAttempt()).ToArray();
    }

    private async ValueTask<P25WriteResult> ExecuteMutationWithRevisionRetryAsync(
        string operation,
        Guid preferredCommandKey,
        string canonicalPayload,
        P25MutationHandler handler,
        CancellationToken cancellationToken,
        Action<Guid>? commandKeyObserver = null)
    {
        var commandKey = preferredCommandKey;
        for (var retry = 0; retry < 3; retry++)
        {
            commandKeyObserver?.Invoke(commandKey);
            var revision = await storage.ReadRevisionStateAsync(cancellationToken).ConfigureAwait(false);
            var request = new P25CommandRequest(
                actualUserSid,
                storage.ProfileScope,
                FormatGuid(commandKey),
                operation,
                P25StorageLimits.CanonicalHashVersion,
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)),
                revision.CurrentRevision,
                Guid.CreateVersion7().ToString("D"),
                agentInstanceId?.ToString("D"));
            var result = await storage.Writer.ExecuteMutationAsync(
                    request,
                    handler,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == P25MutationOutcome.Stale)
            {
                commandKey = Guid.CreateVersion7();
                continue;
            }

            return result;
        }

        throw new InvalidOperationException("The notification attempt writer remained stale after bounded retries.");
    }

    private async ValueTask<NotificationDeliveryAttemptRecord?> ReadByIdempotencyKeyAsync(
        Guid idempotencyKey,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        return await ReadOneAsync(
                $"""
                SELECT {SelectColumns}
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
                  AND idempotency_key = $idempotencyKey
                ORDER BY event_ordinal DESC
                LIMIT 1;
                """,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$idempotencyKey", FormatGuid(idempotencyKey));
                },
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<NotificationDeliveryAttemptRecord?> ReadCurrentAsync(
        Guid instanceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        return await ReadOneAsync(
                $"""
                SELECT {SelectColumns}
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope
                  AND instance_id = $instanceId
                  AND logical_reminder_id = $logicalReminderId
                  AND channel = $channel
                ORDER BY attempt_number DESC, event_ordinal DESC
                LIMIT 1;
                """,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$instanceId", FormatGuid(instanceId));
                    command.Parameters.AddWithValue("$logicalReminderId", FormatGuid(logicalReminderId));
                    command.Parameters.AddWithValue("$channel", channelId.Value);
                },
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<NotificationDeliveryAttemptRecord?> ReadLatestByAttemptIdAsync(
        Guid attemptId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        return await ReadOneAsync(
                $"""
                SELECT {SelectColumns}
                FROM notification_delivery_attempt_events
                WHERE profile_scope = $profileScope AND attempt_id = $attemptId
                ORDER BY event_ordinal DESC
                LIMIT 1;
                """,
                command =>
                {
                    AddScope(command);
                    command.Parameters.AddWithValue("$attemptId", FormatGuid(attemptId));
                },
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<NotificationDeliveryAttemptRecord?> ReadOneAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken) =>
        await ReadOneAsync(sql, configure, transaction: null, cancellationToken).ConfigureAwait(false);

    private async ValueTask<NotificationDeliveryAttemptRecord?> ReadOneAsync(
        string sql,
        Action<SqliteCommand> configure,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = storage.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private async ValueTask<IReadOnlyList<NotificationDeliveryAttemptRecord>> ReadManyAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = storage.Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<NotificationDeliveryAttemptRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadRecord(reader));
        }

        return result;
    }

    private void AddScope(SqliteCommand command) =>
        command.Parameters.AddWithValue("$profileScope", storage.ProfileScope);

    private async ValueTask InsertAsync(
        NotificationDeliveryAttemptRecord record,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string? profileScopeOverride = null)
    {
        var profileScope = profileScopeOverride ?? storage.ProfileScope;
        await using var command = storage.Connection.CreateCommand();
        command.CommandText = InsertSql;
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        AddInsertParameters(command, profileScope, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string InsertSql = """
            INSERT INTO notification_delivery_attempt_events (
                profile_scope, attempt_id, event_ordinal, instance_id,
                schedule_id, rule_id, occurrence_id, logical_reminder_id,
                channel, correlation_id, request_id,
                idempotency_key, purpose_snapshot, priority_snapshot,
                pinned_snapshot, trigger_attempt_ordinal, triggered_at_utc,
                scheduled_trigger_at_utc, attempt_number, state, outcome,
                created_at_utc, updated_at_utc, next_attempt_at_utc,
                error_code, retryable, revision, journal_batch_id, receipt_id)
            VALUES (
                $profileScope, $attemptId, $eventOrdinal, $instanceId,
                $scheduleId, $ruleId, $occurrenceId, $logicalReminderId,
                $channel, $correlationId, $requestId,
                $idempotencyKey, $purposeSnapshot, $prioritySnapshot,
                $pinnedSnapshot, $triggerAttemptOrdinal, $triggeredAtUtc,
                $scheduledTriggerAtUtc, $attemptNumber, $state, $outcome,
                $createdAtUtc, $updatedAtUtc, $nextAttemptAtUtc,
                $errorCode, $retryable, $revision, $journalBatchId, $receiptId);
        """;

    private static void AddInsertParameters(
        SqliteCommand command,
        string profileScope,
        NotificationDeliveryAttemptRecord record)
    {
        command.Parameters.AddWithValue("$profileScope", profileScope);
        command.Parameters.AddWithValue("$attemptId", FormatGuid(record.AttemptId));
        command.Parameters.AddWithValue("$eventOrdinal", record.EventOrdinal);
        command.Parameters.AddWithValue("$instanceId", FormatGuid(record.InstanceId));
        command.Parameters.AddWithValue("$scheduleId", FormatGuid(record.ScheduleId));
        command.Parameters.AddWithValue("$ruleId", FormatGuid(record.RuleId));
        command.Parameters.AddWithValue("$occurrenceId", FormatGuid(record.OccurrenceId));
        command.Parameters.AddWithValue("$logicalReminderId", FormatGuid(record.LogicalReminderId));
        command.Parameters.AddWithValue("$channel", record.ChannelId.Value);
        command.Parameters.AddWithValue("$correlationId", FormatGuid(record.CorrelationId));
        command.Parameters.AddWithValue("$requestId", FormatGuid(record.RequestId));
        command.Parameters.AddWithValue("$idempotencyKey", FormatGuid(record.IdempotencyKey));
        command.Parameters.AddWithValue("$purposeSnapshot", record.PurposeSnapshot.Value);
        command.Parameters.AddWithValue("$prioritySnapshot", record.PrioritySnapshot.ToString());
        command.Parameters.AddWithValue("$pinnedSnapshot", record.PinnedSnapshot ? 1 : 0);
        command.Parameters.AddWithValue("$triggerAttemptOrdinal", record.TriggerAttemptOrdinal);
        command.Parameters.AddWithValue("$triggeredAtUtc", FormatInstant(record.TriggeredAtUtc));
        command.Parameters.AddWithValue("$scheduledTriggerAtUtc", record.ScheduledTriggerAtUtc is { } scheduled
            ? FormatInstant(scheduled)
            : DBNull.Value);
        command.Parameters.AddWithValue("$attemptNumber", record.AttemptNumber);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$outcome", record.Outcome?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", FormatInstant(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", FormatInstant(record.UpdatedAtUtc));
        command.Parameters.AddWithValue("$nextAttemptAtUtc", record.NextAttemptAtUtc is { } next
            ? FormatInstant(next)
            : DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", record.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$retryable", record.Retryable ? 1 : 0);
        command.Parameters.AddWithValue("$revision", record.Revision);
        command.Parameters.AddWithValue("$journalBatchId", FormatGuid(record.JournalBatchId!.Value));
        command.Parameters.AddWithValue("$receiptId", FormatGuid(record.ReceiptId!.Value));
    }

    private static NotificationDeliveryAttemptRecord ReadRecord(SqliteDataReader reader)
    {
        var state = ParseState(reader.GetString(19));
        var outcome = reader.IsDBNull(20)
            ? (NotificationDeliveryOutcome?)null
            : ParseOutcome(reader.GetString(20));
        return NotificationDeliveryAttemptRecord.Rehydrate(
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(3)),
            Guid.Parse(reader.GetString(4)),
            Guid.Parse(reader.GetString(5)),
            Guid.Parse(reader.GetString(6)),
            Guid.Parse(reader.GetString(7)),
            NotificationChannelId.Parse(reader.GetString(8)),
            Guid.Parse(reader.GetString(9)),
            Guid.Parse(reader.GetString(10)),
            Guid.Parse(reader.GetString(11)),
            NotificationPurposeSnapshot.Parse(reader.GetString(12)),
            ParsePriority(reader.GetString(13)),
            reader.GetInt64(14) != 0,
            reader.GetInt32(15),
            ParseInstant(reader.GetString(16)),
            reader.IsDBNull(17) ? null : ParseInstant(reader.GetString(17)),
            reader.GetInt32(18),
            state,
            outcome,
            ParseInstant(reader.GetString(21)),
            ParseInstant(reader.GetString(22)),
            reader.IsDBNull(23) ? (Instant?)null : ParseInstant(reader.GetString(23)),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.GetInt64(25) != 0,
            reader.GetInt64(26),
            Guid.Parse(reader.GetString(27)),
            Guid.Parse(reader.GetString(28)),
            reader.GetInt64(2));
    }

    private static readonly string SelectColumns = """
        profile_scope, attempt_id, event_ordinal, instance_id,
        schedule_id, rule_id, occurrence_id, logical_reminder_id,
        channel, correlation_id, request_id,
        idempotency_key, purpose_snapshot, priority_snapshot,
        pinned_snapshot, trigger_attempt_ordinal, triggered_at_utc,
        scheduled_trigger_at_utc, attempt_number, state, outcome,
        created_at_utc, updated_at_utc, next_attempt_at_utc,
        error_code, retryable, revision, journal_batch_id, receipt_id
        """;

    private static readonly string SelectColumnsFromRanked = """
        profile_scope, attempt_id, event_ordinal, instance_id,
        schedule_id, rule_id, occurrence_id, logical_reminder_id,
        channel, correlation_id, request_id,
        idempotency_key, purpose_snapshot, priority_snapshot,
        pinned_snapshot, trigger_attempt_ordinal, triggered_at_utc,
        scheduled_trigger_at_utc, attempt_number, state, outcome,
        created_at_utc, updated_at_utc, next_attempt_at_utc,
        error_code, retryable, revision, journal_batch_id, receipt_id
        """;

    private static string BuildPendingPayload(NotificationDeliveryRequest request, int maxAttempts) =>
        string.Join('|',
            request.CoreTrigger.InstanceId.ToString("D"),
            request.CoreTrigger.LogicalReminderId.ToString("D"),
            request.ChannelId.Value,
            request.CorrelationId.ToString("D"),
            request.IdempotencyKey.ToString("D"),
            maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string BuildOutcomePayload(
        NotificationDeliveryAttemptRecord pending,
        NotificationChannelDeliveryResponse response,
        bool retryable,
        Instant? nextAttemptAtUtc,
        Guid requestId) =>
        string.Join('|',
            pending.AttemptId.ToString("D"),
            response.Outcome.ToString(),
            response.ErrorCode ?? string.Empty,
            retryable ? "1" : "0",
            nextAttemptAtUtc is { } next ? FormatInstant(next) : string.Empty,
            requestId.ToString("D"));

    private static string BuildDirectPayload(NotificationDeliveryAttemptRecord record) =>
        string.Join('|',
            record.AttemptId.ToString("D"),
            record.IdempotencyKey.ToString("D"),
            record.Outcome?.ToString() ?? string.Empty);

    private static void ThrowIfMutationFailed(P25WriteResult result)
    {
        if (result.Outcome is P25MutationOutcome.Changed or
            P25MutationOutcome.NoOp or
            P25MutationOutcome.Replayed)
        {
            return;
        }

        if (result.ErrorCode == NotificationErrorCodes.IdempotencyConflict)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "The notification idempotency key is already bound to another intent.");
        }

        throw new InvalidOperationException(
            $"The notification attempt transaction failed: {result.ErrorCode ?? "unknown"}.");
    }

    private static void EnsureMatchesIntent(
        NotificationDeliveryAttemptRecord record,
        NotificationDeliveryRequest request)
    {
        if (!record.MatchesIntent(request))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "The notification idempotency key is already bound to another intent.");
        }
    }

    private static bool SameAttemptIntent(
        NotificationDeliveryAttemptRecord left,
        NotificationDeliveryAttemptRecord right) =>
        left.AttemptId == right.AttemptId &&
        left.InstanceId == right.InstanceId &&
        left.LogicalReminderId == right.LogicalReminderId &&
        left.ChannelId == right.ChannelId &&
        left.CorrelationId == right.CorrelationId &&
        left.RequestId == right.RequestId &&
        left.IdempotencyKey == right.IdempotencyKey;

    private static NotificationDeliveryAttemptState ToState(NotificationDeliveryOutcome outcome) => outcome switch
    {
        NotificationDeliveryOutcome.DELIVERED => NotificationDeliveryAttemptState.DELIVERED,
        NotificationDeliveryOutcome.BLOCKED => NotificationDeliveryAttemptState.BLOCKED,
        NotificationDeliveryOutcome.UNAVAILABLE => NotificationDeliveryAttemptState.UNAVAILABLE,
        NotificationDeliveryOutcome.FAILED => NotificationDeliveryAttemptState.FAILED,
        NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS => NotificationDeliveryAttemptState.SUPPRESSED_QUIET_HOURS,
        NotificationDeliveryOutcome.NOT_ATTEMPTED => NotificationDeliveryAttemptState.NOT_ATTEMPTED,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static NotificationDeliveryAttemptState ParseState(string value) =>
        Enum.TryParse<NotificationDeliveryAttemptState>(value, out var state) &&
        Enum.IsDefined(state)
            ? state
            : throw new InvalidOperationException($"Unknown notification attempt state '{value}'.");

    private static NotificationDeliveryOutcome ParseOutcome(string value) =>
        Enum.TryParse<NotificationDeliveryOutcome>(value, out var outcome) &&
        Enum.IsDefined(outcome)
            ? outcome
            : throw new InvalidOperationException($"Unknown notification attempt outcome '{value}'.");

    private static NotificationPriority ParsePriority(string value) =>
        Enum.TryParse<NotificationPriority>(value, out var priority) &&
        Enum.IsDefined(priority)
            ? priority
            : throw new InvalidOperationException($"Unknown notification attempt priority '{value}'.");

    private static Instant ParseInstant(string value) => InstantPattern.Parse(value).GetValueOrThrow();

    private static string FormatInstant(Instant value) => InstantPattern.Format(value);

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static void RequireUuidV7(Guid value, string parameterName)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (value == Guid.Empty || !value.TryWriteBytes(bytes) || (bytes[8] & 0xC0) != 0x80 ||
            value.ToString("D")[14] != '7')
        {
            throw new ArgumentException("The value must be a UUID v7.", parameterName);
        }
    }
}
