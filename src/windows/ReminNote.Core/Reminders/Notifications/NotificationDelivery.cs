using NodaTime;

#pragma warning disable CA1707 // Frozen wire names use uppercase underscore codes.
namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Immutable snapshot of the durable core trigger. It is supplied by the
/// Agent after its due transaction has recorded a ReminderInstance; a channel
/// adapter cannot manufacture or mutate this fact.
/// </summary>
public sealed record NotificationTriggerFact
{
    public NotificationTriggerFact(
        Guid instanceId,
        Guid scheduleId,
        Guid ruleId,
        Guid occurrenceId,
        Guid logicalReminderId,
        int attemptOrdinal,
        NotificationPurposeSnapshot purposeSnapshot,
        NotificationPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant triggeredAtUtc,
        Instant? scheduledTriggerAtUtc = null)
    {
        NotificationValidation.RequireUuidV7(instanceId, nameof(instanceId));
        NotificationValidation.RequireUuidV7(scheduleId, nameof(scheduleId));
        NotificationValidation.RequireUuidV7(ruleId, nameof(ruleId));
        NotificationValidation.RequireUuidV7(occurrenceId, nameof(occurrenceId));
        NotificationValidation.RequireUuidV7(logicalReminderId, nameof(logicalReminderId));
        if (attemptOrdinal < 1)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "attemptOrdinal must start at one.",
                nameof(attemptOrdinal));
        }

        _ = new NotificationPurposeSnapshot(purposeSnapshot.Value);
        NotificationValidation.ValidatePriority(prioritySnapshot);

        InstanceId = instanceId;
        ScheduleId = scheduleId;
        RuleId = ruleId;
        OccurrenceId = occurrenceId;
        LogicalReminderId = logicalReminderId;
        AttemptOrdinal = attemptOrdinal;
        PurposeSnapshot = purposeSnapshot;
        PrioritySnapshot = prioritySnapshot;
        PinnedSnapshot = pinnedSnapshot;
        TriggeredAtUtc = triggeredAtUtc;
        ScheduledTriggerAtUtc = scheduledTriggerAtUtc;
    }

    public Guid InstanceId { get; }

    public Guid ScheduleId { get; }

    public Guid RuleId { get; }

    public Guid OccurrenceId { get; }

    public Guid LogicalReminderId { get; }

    public int AttemptOrdinal { get; }

    public NotificationPurposeSnapshot PurposeSnapshot { get; }

    public NotificationPriority PrioritySnapshot { get; }

    public bool PinnedSnapshot { get; }

    public Instant TriggeredAtUtc { get; }

    /// <summary>
    /// The schedule instant that caused this core trigger, when the Agent
    /// supplies it. It is intentionally separate from TriggeredAtUtc: a
    /// channel adapter must not reinterpret the time the Agent recorded the
    /// fact as the time at which a wake request should have fired.
    /// </summary>
    public Instant? ScheduledTriggerAtUtc { get; }
}

/// <summary>
/// A channel dispatch request. It has no free-form message field; adapters
/// obtain target text from a read model and use the logical ID as their
/// replace/update key.
/// </summary>
public sealed record NotificationDeliveryRequest
{
    public NotificationDeliveryRequest(
        NotificationTriggerFact coreTrigger,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid idempotencyKey)
        : this(
            coreTrigger,
            channelId,
            correlationId,
            idempotencyKey,
            Guid.CreateVersion7())
    {
    }

    /// <summary>
    /// Creates one delivery request attempt. <paramref name="idempotencyKey"/>
    /// identifies the effect attempt while <paramref name="requestId"/>
    /// identifies this transport/request execution. A replay may therefore
    /// use a new request ID without changing the durable delivery intent.
    /// </summary>
    public NotificationDeliveryRequest(
        NotificationTriggerFact coreTrigger,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid idempotencyKey,
        Guid requestId)
        : this(
            coreTrigger,
            channelId,
            correlationId,
            idempotencyKey,
            requestId,
            summary: null)
    {
    }

    /// <summary>
    /// Creates one delivery request, optionally carrying bounded summary
    /// metadata for a quiet-hours aggregate. The metadata never changes the
    /// durable core trigger or logical delivery key.
    /// </summary>
    public NotificationDeliveryRequest(
        NotificationTriggerFact coreTrigger,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid idempotencyKey,
        Guid requestId,
        NotificationSummarySnapshot? summary)
    {
        CoreTrigger = coreTrigger ?? throw new ArgumentNullException(nameof(coreTrigger));
        NotificationChannels.RequireKnown(channelId);
        NotificationValidation.RequireUuidV7(correlationId, nameof(correlationId));
        NotificationValidation.RequireUuidV7(idempotencyKey, nameof(idempotencyKey));
        NotificationValidation.RequireUuidV7(requestId, nameof(requestId));
        ChannelId = channelId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        RequestId = requestId;
        Summary = summary;
    }

    public NotificationTriggerFact CoreTrigger { get; }

    public NotificationChannelId ChannelId { get; }

    /// <summary>Correlates this dispatch with the Agent business operation.</summary>
    public Guid CorrelationId { get; }

    /// <summary>
    /// Stable key for one logical delivery attempt. A retry that intends a new
    /// channel side effect must use a new key; replay reuses this key.
    /// </summary>
    public Guid IdempotencyKey { get; }

    /// <summary>
    /// Identifies the request execution, not the durable effect. Retries keep
    /// the same logical reminder/channel intent and allocate a new request ID.
    /// </summary>
    public Guid RequestId { get; }

    /// <summary>
    /// Bounded collection metadata for one quiet-hours summary effect, or
    /// null for an ordinary single-reminder delivery.
    /// </summary>
    public NotificationSummarySnapshot? Summary { get; }

    /// <summary>
    /// Stable logical replacement/deduplication key. It deliberately contains
    /// only the frozen logical reminder identity and channel, never user text.
    /// </summary>
    public string LogicalDeliveryKey =>
        $"{CoreTrigger.LogicalReminderId:D}:{ChannelId.Value}";

    /// <summary>
    /// Builds a new effect attempt for the same committed core trigger. The
    /// logical identity and correlation are retained; both the effect key and
    /// request execution identity are fresh UUID v7 values.
    /// </summary>
    public NotificationDeliveryRequest CreateRetry() => new(
        CoreTrigger,
        ChannelId,
        CorrelationId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Summary);
}

/// <summary>
/// Result reported by a channel adapter. Quiet Hours suppression is owned by
/// the policy seam and therefore cannot be returned by a channel itself.
/// </summary>
public sealed record NotificationChannelDeliveryResponse
{
    public NotificationChannelDeliveryResponse(
        NotificationDeliveryOutcome outcome,
        string? errorCode = null,
        Instant? nextAttemptAtUtc = null)
        : this(outcome, errorCode, nextAttemptAtUtc, allowPolicySuppression: false)
    {
    }

    private NotificationChannelDeliveryResponse(
        NotificationDeliveryOutcome outcome,
        string? errorCode,
        Instant? nextAttemptAtUtc,
        bool allowPolicySuppression)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.DeliveryOutcomeInvalid,
                "Delivery outcome is not supported.",
                nameof(outcome));
        }

        if (outcome == NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS &&
            !allowPolicySuppression)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.DeliveryOutcomeInvalid,
                "Quiet Hours suppression must come from the presentation policy.",
                nameof(outcome));
        }

        NotificationValidation.ValidateOptionalStableCode(errorCode, nameof(errorCode));
        if (nextAttemptAtUtc is not null &&
            outcome is not (NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS or
                NotificationDeliveryOutcome.UNAVAILABLE or
                NotificationDeliveryOutcome.FAILED or
                NotificationDeliveryOutcome.NOT_ATTEMPTED))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A delivered or blocked response cannot carry a retry time.",
                nameof(nextAttemptAtUtc));
        }

        Outcome = outcome;
        ErrorCode = errorCode;
        NextAttemptAtUtc = nextAttemptAtUtc;
    }

    /// <summary>
    /// Creates the policy-owned suppression result. Channel adapters cannot
    /// manufacture this outcome through the public constructor.
    /// </summary>
    public static NotificationChannelDeliveryResponse SuppressedByPolicy(
        string reasonCode,
        Instant? nextAttemptAtUtc = null) =>
        new(
            NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS,
            reasonCode,
            nextAttemptAtUtc,
            allowPolicySuppression: true);

    public NotificationDeliveryOutcome Outcome { get; }

    public string? ErrorCode { get; }

    public Instant? NextAttemptAtUtc { get; }
}

public enum NotificationDeliveryOutcome
{
    DELIVERED,
    BLOCKED,
    UNAVAILABLE,
    FAILED,
    SUPPRESSED_QUIET_HOURS,
    NOT_ATTEMPTED,
}

/// <summary>
/// Append-only child fact for one channel attempt. No property on this type
/// represents or overwrites the core ReminderInstance lifecycle.
/// </summary>
public sealed record NotificationDeliveryAttempt
{
    public NotificationDeliveryAttempt(
        Guid attemptId,
        Guid instanceId,
        Guid logicalReminderId,
        NotificationChannelId channelId,
        Guid correlationId,
        Guid idempotencyKey,
        NotificationPurposeSnapshot purposeSnapshot,
        NotificationPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant attemptedAtUtc,
        NotificationDeliveryOutcome outcome,
        string? errorCode = null)
    {
        NotificationValidation.RequireUuidV7(attemptId, nameof(attemptId));
        NotificationValidation.RequireUuidV7(instanceId, nameof(instanceId));
        NotificationValidation.RequireUuidV7(logicalReminderId, nameof(logicalReminderId));
        NotificationChannels.RequireKnown(channelId);
        NotificationValidation.RequireUuidV7(correlationId, nameof(correlationId));
        NotificationValidation.RequireUuidV7(idempotencyKey, nameof(idempotencyKey));
        _ = new NotificationPurposeSnapshot(purposeSnapshot.Value);
        NotificationValidation.ValidatePriority(prioritySnapshot);
        if (!Enum.IsDefined(outcome))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.DeliveryOutcomeInvalid,
                "Delivery outcome is not supported.",
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

        AttemptId = attemptId;
        InstanceId = instanceId;
        LogicalReminderId = logicalReminderId;
        ChannelId = channelId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        PurposeSnapshot = purposeSnapshot;
        PrioritySnapshot = prioritySnapshot;
        PinnedSnapshot = pinnedSnapshot;
        AttemptedAtUtc = attemptedAtUtc;
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    public Guid AttemptId { get; }

    public Guid InstanceId { get; }

    public Guid LogicalReminderId { get; }

    public NotificationChannelId ChannelId { get; }

    public Guid CorrelationId { get; }

    public Guid IdempotencyKey { get; }

    public NotificationPurposeSnapshot PurposeSnapshot { get; }

    public NotificationPriority PrioritySnapshot { get; }

    public bool PinnedSnapshot { get; }

    public Instant AttemptedAtUtc { get; }

    public NotificationDeliveryOutcome Outcome { get; }

    public string? ErrorCode { get; }

    public static NotificationDeliveryAttempt From(
        NotificationDeliveryRequest request,
        Guid attemptId,
        Instant attemptedAtUtc,
        NotificationChannelDeliveryResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var errorCode = response.ErrorCode;
        if (response.Outcome != NotificationDeliveryOutcome.DELIVERED && errorCode is null)
        {
            errorCode = NotificationErrorCodes.DeliveryFailed;
        }

        if (response.Outcome == NotificationDeliveryOutcome.DELIVERED)
        {
            errorCode = null;
        }

        return new NotificationDeliveryAttempt(
            attemptId,
            request.CoreTrigger.InstanceId,
            request.CoreTrigger.LogicalReminderId,
            request.ChannelId,
            request.CorrelationId,
            request.IdempotencyKey,
            request.CoreTrigger.PurposeSnapshot,
            request.CoreTrigger.PrioritySnapshot,
            request.CoreTrigger.PinnedSnapshot,
            attemptedAtUtc,
            response.Outcome,
            errorCode);
    }

    public bool MatchesIntent(NotificationDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InstanceId == request.CoreTrigger.InstanceId &&
            LogicalReminderId == request.CoreTrigger.LogicalReminderId &&
            ChannelId == request.ChannelId &&
            CorrelationId == request.CorrelationId &&
            IdempotencyKey == request.IdempotencyKey &&
            PurposeSnapshot == request.CoreTrigger.PurposeSnapshot &&
            PrioritySnapshot == request.CoreTrigger.PrioritySnapshot &&
            PinnedSnapshot == request.CoreTrigger.PinnedSnapshot;
    }
}

public enum NotificationAttemptRecordDisposition
{
    NOT_APPLICABLE,
    APPENDED,
    REPLAYED,
}

/// <summary>
/// Store seam for an Agent-owned append-only child collection. The production
/// implementation must enforce the unique (instance, channel, key) tuple in
/// the same writer serialization used for Reminder facts.
/// </summary>
public interface INotificationDeliveryAttemptStore
{
    ValueTask<NotificationDeliveryAttempt?> FindAsync(
        Guid instanceId,
        NotificationChannelId channelId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<NotificationDeliveryAppendResult> AppendAsync(
        NotificationDeliveryAttempt attempt,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<NotificationDeliveryAttempt>> ListForInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationDeliveryAppendResult
{
    private NotificationDeliveryAppendResult(
        NotificationAttemptRecordDisposition disposition,
        NotificationDeliveryAttempt attempt)
    {
        if (disposition is not (
            NotificationAttemptRecordDisposition.APPENDED or
            NotificationAttemptRecordDisposition.REPLAYED))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Append result disposition is not supported.",
                nameof(disposition));
        }

        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        Disposition = disposition;
    }

    public NotificationAttemptRecordDisposition Disposition { get; }

    public NotificationDeliveryAttempt Attempt { get; }

    public static NotificationDeliveryAppendResult Appended(NotificationDeliveryAttempt attempt) =>
        new(NotificationAttemptRecordDisposition.APPENDED, attempt);

    public static NotificationDeliveryAppendResult Replayed(NotificationDeliveryAttempt attempt) =>
        new(NotificationAttemptRecordDisposition.REPLAYED, attempt);
}

public interface INotificationChannel
{
    NotificationChannelId ChannelId { get; }

    NotificationChannelStatus GetStatus();

    ValueTask<NotificationChannelDeliveryResponse> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

public interface INotificationChannelCatalog
{
    bool TryGet(NotificationChannelId channelId, out INotificationChannel channel);
}

/// <summary>
/// Optional enumeration seam used by Agent composition. Existing catalog
/// implementations that only support lookup remain valid; a production host
/// should expose its complete injected channel set through this interface.
/// </summary>
public interface INotificationChannelCatalogSnapshot : INotificationChannelCatalog
{
    IReadOnlyCollection<NotificationChannelId> ChannelIds { get; }
}

public enum NotificationCoreTriggerDisposition
{
    NOT_TRIGGERED,
    TRIGGERED,
}

/// <summary>
/// Coordinator result deliberately carries both facts: a core trigger can be
/// recorded while delivery is blocked/unavailable, and a non-triggered result
/// has no delivery attempt at all.
/// </summary>
public sealed record NotificationDispatchResult
{
    private NotificationDispatchResult(
        NotificationCoreTriggerDisposition coreTriggerDisposition,
        NotificationTriggerFact? coreTrigger,
        NotificationDeliveryAttempt? attempt,
        NotificationAttemptRecordDisposition recordDisposition,
        string? notTriggeredReasonCode)
    {
        if (coreTriggerDisposition == NotificationCoreTriggerDisposition.TRIGGERED &&
            (coreTrigger is null || attempt is null || recordDisposition is
                NotificationAttemptRecordDisposition.NOT_APPLICABLE))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A triggered result must carry a core fact and an attempt.",
                nameof(coreTriggerDisposition));
        }

        if (coreTriggerDisposition == NotificationCoreTriggerDisposition.NOT_TRIGGERED &&
            (coreTrigger is not null || attempt is not null ||
             recordDisposition != NotificationAttemptRecordDisposition.NOT_APPLICABLE))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A non-triggered result cannot carry a delivery attempt.",
                nameof(coreTriggerDisposition));
        }

        NotificationValidation.ValidateOptionalStableCode(
            notTriggeredReasonCode,
            nameof(notTriggeredReasonCode));
        if (coreTriggerDisposition == NotificationCoreTriggerDisposition.NOT_TRIGGERED &&
            notTriggeredReasonCode is null)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A non-triggered result must carry a stable reason code.",
                nameof(notTriggeredReasonCode));
        }

        CoreTriggerDisposition = coreTriggerDisposition;
        CoreTrigger = coreTrigger;
        Attempt = attempt;
        RecordDisposition = recordDisposition;
        NotTriggeredReasonCode = notTriggeredReasonCode;
    }

    public NotificationCoreTriggerDisposition CoreTriggerDisposition { get; }

    public NotificationTriggerFact? CoreTrigger { get; }

    public NotificationDeliveryAttempt? Attempt { get; }

    public NotificationDeliveryOutcome? Outcome => Attempt?.Outcome;

    public NotificationAttemptRecordDisposition RecordDisposition { get; }

    public string? NotTriggeredReasonCode { get; }

    public bool CoreTriggerWasRecorded =>
        CoreTriggerDisposition == NotificationCoreTriggerDisposition.TRIGGERED;

    public bool ChannelWasBlockedOrUnavailable =>
        Outcome is NotificationDeliveryOutcome.BLOCKED or NotificationDeliveryOutcome.UNAVAILABLE;

    public bool WasDuplicate => RecordDisposition == NotificationAttemptRecordDisposition.REPLAYED;

    public static NotificationDispatchResult Triggered(
        NotificationTriggerFact coreTrigger,
        NotificationDeliveryAttempt attempt,
        NotificationAttemptRecordDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(coreTrigger);
        ArgumentNullException.ThrowIfNull(attempt);
        return new(
            NotificationCoreTriggerDisposition.TRIGGERED,
            coreTrigger,
            attempt,
            disposition,
            null);
    }

    public static NotificationDispatchResult NotTriggered(string reasonCode) =>
        new(
            NotificationCoreTriggerDisposition.NOT_TRIGGERED,
            null,
            null,
            NotificationAttemptRecordDisposition.NOT_APPLICABLE,
            reasonCode);
}

/// <summary>
/// Agent-side orchestration that never writes a ReminderRule/Schedule/Instance.
/// The caller must already have committed the core trigger fact; this class
/// only appends channel-attempt facts and invokes replaceable adapters.
/// </summary>
public sealed class NotificationDeliveryCoordinator
{
    private readonly INotificationChannelCatalog channelCatalog;
    private readonly INotificationDeliveryAttemptStore attemptStore;
    private readonly INotificationPresentationPolicy presentationPolicy;
    private readonly IClock clock;

    public NotificationDeliveryCoordinator(
        INotificationChannelCatalog channelCatalog,
        INotificationDeliveryAttemptStore attemptStore,
        INotificationPresentationPolicy presentationPolicy,
        IClock clock)
    {
        this.channelCatalog = channelCatalog ?? throw new ArgumentNullException(nameof(channelCatalog));
        this.attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        this.presentationPolicy = presentationPolicy ?? throw new ArgumentNullException(nameof(presentationPolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<NotificationDispatchResult> DispatchAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await attemptStore.FindAsync(
            request.CoreTrigger.InstanceId,
            request.ChannelId,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            EnsureMatchesIntent(existing, request);
            return NotificationDispatchResult.Triggered(
                request.CoreTrigger,
                existing,
                NotificationAttemptRecordDisposition.REPLAYED);
        }

        var response = await ResolveResponseAsync(request, cancellationToken);
        var attempt = NotificationDeliveryAttempt.From(
            request,
            Guid.CreateVersion7(),
            clock.GetCurrentInstant(),
            response);
        var appendResult = await attemptStore.AppendAsync(attempt, cancellationToken);
        if (appendResult.Disposition == NotificationAttemptRecordDisposition.REPLAYED)
        {
            EnsureMatchesIntent(appendResult.Attempt, request);
        }

        return NotificationDispatchResult.Triggered(
            request.CoreTrigger,
            appendResult.Attempt,
            appendResult.Disposition);
    }

    private async ValueTask<NotificationChannelDeliveryResponse> ResolveResponseAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!channelCatalog.TryGet(request.ChannelId, out var channel))
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.NOT_ATTEMPTED,
                NotificationErrorCodes.ChannelMissing);
        }

        if (channel.ChannelId != request.ChannelId)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.ChannelIdentityMismatch,
                "Channel catalog returned a channel with a different ID.",
                nameof(request));
        }

        var status = channel.GetStatus() ?? throw new InvalidOperationException(
            "Notification channel returned a null status.");
        if (!status.Supports(NotificationCapability.PRESENT))
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.NOT_ATTEMPTED,
                NotificationErrorCodes.CapabilityMissing);
        }

        if (status.Health == NotificationChannelHealth.BLOCKED)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.BLOCKED,
                status.HealthCode ?? NotificationErrorCodes.ChannelBlocked);
        }

        if (status.Health is NotificationChannelHealth.UNAVAILABLE or NotificationChannelHealth.UNKNOWN)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                status.HealthCode ?? NotificationErrorCodes.ChannelUnavailable);
        }

        if (status.Health is not (NotificationChannelHealth.HEALTHY or NotificationChannelHealth.DEGRADED))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Channel health cannot be dispatched.",
                nameof(status));
        }

        var decision = presentationPolicy.Evaluate(new NotificationPresentationContext(
            request.CoreTrigger,
            status,
            clock.GetCurrentInstant()));
        if (decision is null)
        {
            throw new InvalidOperationException("Notification presentation policy returned null.");
        }

        if (decision.Kind == NotificationPresentationDecisionKind.SUPPRESSED_QUIET_HOURS)
        {
            return NotificationChannelDeliveryResponse.SuppressedByPolicy(
                decision.ReasonCode!);
        }

        var channelResponse = await channel.DeliverAsync(request, cancellationToken);
        if (channelResponse is null)
        {
            throw new InvalidOperationException("Notification channel returned null delivery response.");
        }

        return channelResponse;
    }

    private static void EnsureMatchesIntent(
        NotificationDeliveryAttempt existing,
        NotificationDeliveryRequest request)
    {
        if (!existing.MatchesIntent(request))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "The idempotency key is already bound to a different delivery intent.",
                nameof(request));
        }
    }
}

#pragma warning restore CA1707
