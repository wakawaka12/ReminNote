using NodaTime;

#pragma warning disable CA1707 // Frozen uppercase state names are part of the P3 contract.
namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Durable state of one append-only delivery-attempt projection. PENDING is
/// an intent row written before an external channel effect; terminal states
/// are appended as a second event and never overwrite the original row.
/// </summary>
public enum NotificationDeliveryAttemptState
{
    PENDING,
    DELIVERED,
    BLOCKED,
    UNAVAILABLE,
    FAILED,
    SUPPRESSED_QUIET_HOURS,
    NOT_ATTEMPTED,
}

/// <summary>
/// The durable projection of one attempt event chain. Revision, journal batch
/// and receipt are populated by the Agent writer adapter after the P2.5
/// transaction commits. A zero revision is permitted only for an in-memory
/// contract fixture; production stores must return a committed revision.
/// </summary>
public sealed record NotificationDeliveryAttemptRecord
{
    private NotificationDeliveryAttemptRecord(
        Guid attemptId,
        Guid instanceId,
        Guid scheduleId,
        Guid ruleId,
        Guid occurrenceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid requestId,
        Guid idempotencyKey,
        NotificationPurposeSnapshot purposeSnapshot,
        NotificationPriority prioritySnapshot,
        bool pinnedSnapshot,
        int triggerAttemptOrdinal,
        Instant triggeredAtUtc,
        Instant? scheduledTriggerAtUtc,
        int attemptNumber,
        NotificationDeliveryAttemptState state,
        NotificationDeliveryOutcome? outcome,
        Instant createdAtUtc,
        Instant updatedAtUtc,
        Instant? nextAttemptAtUtc,
        string? errorCode,
        bool retryable,
        long revision,
        Guid? journalBatchId,
        Guid? receiptId,
        long eventOrdinal)
    {
        NotificationValidation.RequireUuidV7(attemptId, nameof(attemptId));
        NotificationValidation.RequireUuidV7(instanceId, nameof(instanceId));
        NotificationValidation.RequireUuidV7(scheduleId, nameof(scheduleId));
        NotificationValidation.RequireUuidV7(ruleId, nameof(ruleId));
        NotificationValidation.RequireUuidV7(occurrenceId, nameof(occurrenceId));
        NotificationValidation.RequireUuidV7(logicalReminderId, nameof(logicalReminderId));
        NotificationChannels.RequireKnown(channelId);
        NotificationValidation.RequireUuidV7(correlationId, nameof(correlationId));
        NotificationValidation.RequireUuidV7(requestId, nameof(requestId));
        NotificationValidation.RequireUuidV7(idempotencyKey, nameof(idempotencyKey));
        _ = new NotificationPurposeSnapshot(purposeSnapshot.Value);
        NotificationValidation.ValidatePriority(prioritySnapshot);
        if (attemptNumber < 1)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "attemptNumber must start at one.",
                nameof(attemptNumber));
        }

        if (triggerAttemptOrdinal < 1)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "triggerAttemptOrdinal must start at one.",
                nameof(triggerAttemptOrdinal));
        }

        if (!Enum.IsDefined(state))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.DeliveryOutcomeInvalid,
                "Delivery attempt state is not supported.",
                nameof(state));
        }

        if (state == NotificationDeliveryAttemptState.PENDING)
        {
            if (outcome is not null || errorCode is not null || retryable || nextAttemptAtUtc is not null)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "A pending attempt cannot carry a terminal outcome, failure or retry schedule.",
                    nameof(state));
            }
        }
        else
        {
            var expectedOutcome = ToOutcome(state);
            if (outcome != expectedOutcome)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.DeliveryOutcomeInvalid,
                    "Attempt state and outcome must describe the same terminal result.",
                    nameof(outcome));
            }

            NotificationValidation.ValidateOptionalStableCode(errorCode, nameof(errorCode));
            if (outcome == NotificationDeliveryOutcome.DELIVERED && errorCode is not null)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "A delivered attempt cannot carry an error code.",
                    nameof(errorCode));
            }

            if (outcome != NotificationDeliveryOutcome.DELIVERED && errorCode is null)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "A non-delivered attempt must carry a stable error code.",
                    nameof(errorCode));
            }

            if (!retryable && nextAttemptAtUtc is not null)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "A non-retryable attempt cannot carry a next-attempt time.",
                    nameof(nextAttemptAtUtc));
            }

            if (retryable && nextAttemptAtUtc is null)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "A retryable attempt must carry a next-attempt time.",
                    nameof(nextAttemptAtUtc));
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        if (journalBatchId is { } batchId)
        {
            NotificationValidation.RequireUuidV7(batchId, nameof(journalBatchId));
        }

        if (receiptId is { } durableReceiptId)
        {
            NotificationValidation.RequireUuidV7(durableReceiptId, nameof(receiptId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(eventOrdinal);

        if (nextAttemptAtUtc is { } next && next < updatedAtUtc)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidTimestamp,
                "The next attempt cannot precede the event update time.",
                nameof(nextAttemptAtUtc));
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidTimestamp,
                "The event update time cannot precede its creation time.",
                nameof(updatedAtUtc));
        }

        AttemptId = attemptId;
        InstanceId = instanceId;
        ScheduleId = scheduleId;
        RuleId = ruleId;
        OccurrenceId = occurrenceId;
        LogicalReminderId = logicalReminderId;
        ChannelId = channelId;
        CorrelationId = correlationId;
        RequestId = requestId;
        IdempotencyKey = idempotencyKey;
        PurposeSnapshot = purposeSnapshot;
        PrioritySnapshot = prioritySnapshot;
        PinnedSnapshot = pinnedSnapshot;
        TriggerAttemptOrdinal = triggerAttemptOrdinal;
        TriggeredAtUtc = triggeredAtUtc;
        ScheduledTriggerAtUtc = scheduledTriggerAtUtc;
        AttemptNumber = attemptNumber;
        State = state;
        Outcome = outcome;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        NextAttemptAtUtc = nextAttemptAtUtc;
        ErrorCode = errorCode;
        Retryable = retryable;
        Revision = revision;
        JournalBatchId = journalBatchId;
        ReceiptId = receiptId;
        EventOrdinal = eventOrdinal;
    }

    public Guid AttemptId { get; }

    public Guid InstanceId { get; }

    public Guid ScheduleId { get; }

    public Guid RuleId { get; }

    public Guid OccurrenceId { get; }

    public Guid LogicalReminderId { get; }

    public NotificationChannelId ChannelId { get; }

    public Guid CorrelationId { get; }

    public Guid RequestId { get; }

    public Guid IdempotencyKey { get; }

    public NotificationPurposeSnapshot PurposeSnapshot { get; }

    public NotificationPriority PrioritySnapshot { get; }

    public bool PinnedSnapshot { get; }

    public int TriggerAttemptOrdinal { get; }

    public Instant TriggeredAtUtc { get; }

    public Instant? ScheduledTriggerAtUtc { get; }

    public int AttemptNumber { get; }

    public NotificationDeliveryAttemptState State { get; }

    public NotificationDeliveryOutcome? Outcome { get; }

    public Instant CreatedAtUtc { get; }

    public Instant UpdatedAtUtc { get; }

    public Instant? NextAttemptAtUtc { get; }

    public string? ErrorCode { get; }

    public bool Retryable { get; }

    public long Revision { get; }

    public Guid? JournalBatchId { get; }

    public Guid? ReceiptId { get; }

    public long EventOrdinal { get; }

    public bool IsPending => State == NotificationDeliveryAttemptState.PENDING;

    public bool IsTerminal => !IsPending;

    public string LogicalDeliveryKey =>
        $"{LogicalReminderId:D}:{ChannelId.Value}";

    public static NotificationDeliveryAttemptRecord Pending(
        NotificationDeliveryRequest request,
        Guid attemptId,
        int attemptNumber,
        Instant createdAtUtc) =>
        new(
            attemptId,
            request.CoreTrigger.InstanceId,
            request.CoreTrigger.ScheduleId,
            request.CoreTrigger.RuleId,
            request.CoreTrigger.OccurrenceId,
            request.CoreTrigger.LogicalReminderId,
            request.ChannelId,
            request.CorrelationId,
            request.RequestId,
            request.IdempotencyKey,
            request.CoreTrigger.PurposeSnapshot,
            request.CoreTrigger.PrioritySnapshot,
            request.CoreTrigger.PinnedSnapshot,
            request.CoreTrigger.AttemptOrdinal,
            request.CoreTrigger.TriggeredAtUtc,
            request.CoreTrigger.ScheduledTriggerAtUtc,
            attemptNumber,
            NotificationDeliveryAttemptState.PENDING,
            outcome: null,
            createdAtUtc,
            createdAtUtc,
            nextAttemptAtUtc: null,
            errorCode: null,
            retryable: false,
            revision: 0,
            journalBatchId: null,
            receiptId: null,
            eventOrdinal: 0);

    public static NotificationDeliveryAttemptRecord Rehydrate(
        Guid attemptId,
        Guid instanceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid requestId,
        Guid idempotencyKey,
        NotificationPurposeSnapshot purposeSnapshot,
        NotificationPriority prioritySnapshot,
        bool pinnedSnapshot,
        int attemptNumber,
        NotificationDeliveryAttemptState state,
        NotificationDeliveryOutcome? outcome,
        Instant createdAtUtc,
        Instant updatedAtUtc,
        Instant? nextAttemptAtUtc,
        string? errorCode,
        bool retryable,
        long revision,
        Guid? journalBatchId,
        Guid? receiptId,
        long eventOrdinal) =>
        Rehydrate(
            attemptId,
            instanceId,
            instanceId,
            instanceId,
            instanceId,
            logicalReminderId,
            channelId,
            correlationId,
            requestId,
            idempotencyKey,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            attemptNumber,
            createdAtUtc,
            null,
            attemptNumber,
            state,
            outcome,
            createdAtUtc,
            updatedAtUtc,
            nextAttemptAtUtc,
            errorCode,
            retryable,
            revision,
            journalBatchId,
            receiptId,
            eventOrdinal);

    public static NotificationDeliveryAttemptRecord Rehydrate(
        Guid attemptId,
        Guid instanceId,
        Guid scheduleId,
        Guid ruleId,
        Guid occurrenceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid requestId,
        Guid idempotencyKey,
        NotificationPurposeSnapshot purposeSnapshot,
        NotificationPriority prioritySnapshot,
        bool pinnedSnapshot,
        int triggerAttemptOrdinal,
        Instant triggeredAtUtc,
        Instant? scheduledTriggerAtUtc,
        int attemptNumber,
        NotificationDeliveryAttemptState state,
        NotificationDeliveryOutcome? outcome,
        Instant createdAtUtc,
        Instant updatedAtUtc,
        Instant? nextAttemptAtUtc,
        string? errorCode,
        bool retryable,
        long revision,
        Guid? journalBatchId,
        Guid? receiptId,
        long eventOrdinal) =>
        new(
            attemptId,
            instanceId,
            scheduleId,
            ruleId,
            occurrenceId,
            logicalReminderId,
            channelId,
            correlationId,
            requestId,
            idempotencyKey,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            triggerAttemptOrdinal,
            triggeredAtUtc,
            scheduledTriggerAtUtc,
            attemptNumber,
            state,
            outcome,
            createdAtUtc,
            updatedAtUtc,
            nextAttemptAtUtc,
            errorCode,
            retryable,
            revision,
            journalBatchId,
            receiptId,
            eventOrdinal);

    public NotificationDeliveryAttemptRecord Complete(
        NotificationChannelDeliveryResponse response,
        Instant updatedAtUtc,
        bool retryable,
        Instant? nextAttemptAtUtc,
        long revision = 0,
        Guid? journalBatchId = null,
        Guid? receiptId = null,
        long eventOrdinal = 1,
        Guid? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!IsPending)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "Only a pending delivery attempt can receive its terminal event.",
                nameof(response));
        }

        var outcome = response.Outcome;
        var errorCode = response.ErrorCode;
        if (outcome != NotificationDeliveryOutcome.DELIVERED && errorCode is null)
        {
            errorCode = NotificationErrorCodes.DeliveryFailed;
        }

        if (outcome == NotificationDeliveryOutcome.DELIVERED)
        {
            retryable = false;
            nextAttemptAtUtc = null;
            errorCode = null;
        }

        var effectiveRequestId = requestId ?? RequestId;
        NotificationValidation.RequireUuidV7(effectiveRequestId, nameof(requestId));

        return new(
            AttemptId,
            InstanceId,
            ScheduleId,
            RuleId,
            OccurrenceId,
            LogicalReminderId,
            ChannelId,
            CorrelationId,
            effectiveRequestId,
            IdempotencyKey,
            PurposeSnapshot,
            PrioritySnapshot,
            PinnedSnapshot,
            TriggerAttemptOrdinal,
            TriggeredAtUtc,
            ScheduledTriggerAtUtc,
            AttemptNumber,
            ToState(outcome),
            outcome,
            CreatedAtUtc,
            updatedAtUtc,
            nextAttemptAtUtc,
            errorCode,
            retryable,
            revision,
            journalBatchId,
            receiptId,
            eventOrdinal);
    }

    public NotificationDeliveryAttemptRecord WithDurability(
        long revision,
        Guid journalBatchId,
        Guid receiptId,
        long eventOrdinal) =>
        Rehydrate(
            AttemptId,
            InstanceId,
            ScheduleId,
            RuleId,
            OccurrenceId,
            LogicalReminderId,
            ChannelId,
            CorrelationId,
            RequestId,
            IdempotencyKey,
            PurposeSnapshot,
            PrioritySnapshot,
            PinnedSnapshot,
            TriggerAttemptOrdinal,
            TriggeredAtUtc,
            ScheduledTriggerAtUtc,
            AttemptNumber,
            State,
            Outcome,
            CreatedAtUtc,
            UpdatedAtUtc,
            NextAttemptAtUtc,
            ErrorCode,
            Retryable,
            revision,
            journalBatchId,
            receiptId,
            eventOrdinal);

    public bool MatchesIntent(NotificationDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MatchesCoreIntent(request.CoreTrigger, request.ChannelId) &&
            CorrelationId == request.CorrelationId &&
            IdempotencyKey == request.IdempotencyKey;
    }

    public bool MatchesCoreIntent(
        NotificationTriggerFact coreTrigger,
        NotificationChannelId channelId)
    {
        ArgumentNullException.ThrowIfNull(coreTrigger);
        return InstanceId == coreTrigger.InstanceId &&
            ScheduleId == coreTrigger.ScheduleId &&
            RuleId == coreTrigger.RuleId &&
            OccurrenceId == coreTrigger.OccurrenceId &&
            LogicalReminderId == coreTrigger.LogicalReminderId &&
            ChannelId == channelId &&
            TriggerAttemptOrdinal == coreTrigger.AttemptOrdinal &&
            PurposeSnapshot == coreTrigger.PurposeSnapshot &&
            PrioritySnapshot == coreTrigger.PrioritySnapshot &&
            PinnedSnapshot == coreTrigger.PinnedSnapshot &&
            TriggeredAtUtc == coreTrigger.TriggeredAtUtc &&
            ScheduledTriggerAtUtc == coreTrigger.ScheduledTriggerAtUtc;
    }

    public NotificationTriggerFact ToTriggerFact() => new(
        InstanceId,
        ScheduleId,
        RuleId,
        OccurrenceId,
        LogicalReminderId,
        TriggerAttemptOrdinal,
        PurposeSnapshot,
        PrioritySnapshot,
        PinnedSnapshot,
        TriggeredAtUtc,
        ScheduledTriggerAtUtc);

    public NotificationDeliveryAttempt ToContractAttempt()
    {
        if (Outcome is not { } outcome)
        {
            throw new InvalidOperationException("A pending delivery attempt has no terminal contract outcome.");
        }

        return new NotificationDeliveryAttempt(
            AttemptId,
            InstanceId,
            LogicalReminderId,
            ChannelId,
            CorrelationId,
            IdempotencyKey,
            PurposeSnapshot,
            PrioritySnapshot,
            PinnedSnapshot,
            UpdatedAtUtc,
            outcome,
            ErrorCode);
    }

    private static NotificationDeliveryAttemptState ToState(
        NotificationDeliveryOutcome outcome) => outcome switch
    {
        NotificationDeliveryOutcome.DELIVERED => NotificationDeliveryAttemptState.DELIVERED,
        NotificationDeliveryOutcome.BLOCKED => NotificationDeliveryAttemptState.BLOCKED,
        NotificationDeliveryOutcome.UNAVAILABLE => NotificationDeliveryAttemptState.UNAVAILABLE,
        NotificationDeliveryOutcome.FAILED => NotificationDeliveryAttemptState.FAILED,
        NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS => NotificationDeliveryAttemptState.SUPPRESSED_QUIET_HOURS,
        NotificationDeliveryOutcome.NOT_ATTEMPTED => NotificationDeliveryAttemptState.NOT_ATTEMPTED,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.DeliveryOutcomeInvalid,
            "Delivery outcome is not supported.",
            nameof(outcome)),
    };

    private static NotificationDeliveryOutcome ToOutcome(
        NotificationDeliveryAttemptState state) => state switch
    {
        NotificationDeliveryAttemptState.DELIVERED => NotificationDeliveryOutcome.DELIVERED,
        NotificationDeliveryAttemptState.BLOCKED => NotificationDeliveryOutcome.BLOCKED,
        NotificationDeliveryAttemptState.UNAVAILABLE => NotificationDeliveryOutcome.UNAVAILABLE,
        NotificationDeliveryAttemptState.FAILED => NotificationDeliveryOutcome.FAILED,
        NotificationDeliveryAttemptState.SUPPRESSED_QUIET_HOURS => NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS,
        NotificationDeliveryAttemptState.NOT_ATTEMPTED => NotificationDeliveryOutcome.NOT_ATTEMPTED,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

public enum NotificationDeliveryPendingDisposition
{
    APPENDED,
    REPLAYED,
    DEFERRED,
    TERMINAL,
}

public sealed record NotificationDeliveryPendingResult(
    NotificationDeliveryPendingDisposition Disposition,
    NotificationDeliveryAttemptRecord Record);

/// <summary>
/// Agent-owned append-only persistence seam. Implementations must serialize
/// every mutation through the same writer as Rule/Schedule/Instance and must
/// make logical deduplication plus the pending-to-terminal transition atomic.
/// </summary>
public interface INotificationDeliveryAttemptJournalStore : INotificationDeliveryAttemptStore
{
    ValueTask<NotificationDeliveryAttemptRecord?> FindByIdempotencyKeyAsync(
        Guid instanceId,
        NotificationChannelId channelId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<NotificationDeliveryAttemptRecord?> FindCurrentAsync(
        Guid instanceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        CancellationToken cancellationToken = default);

    ValueTask<NotificationDeliveryPendingResult> AppendPendingAsync(
        NotificationDeliveryRequest request,
        int maxAttempts,
        Instant recordedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<NotificationDeliveryAttemptRecord> AppendOutcomeAsync(
        NotificationDeliveryAttemptRecord pending,
        NotificationChannelDeliveryResponse response,
        bool retryable,
        Instant recordedAtUtc,
        Instant? nextAttemptAtUtc,
        Guid? requestId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListRecoverableAsync(
        Instant evaluatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListPendingAsync(
        CancellationToken cancellationToken = default);
}

#pragma warning restore CA1707
