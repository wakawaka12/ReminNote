using NodaTime;
using ReminNote.Agent.Scheduling;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

#pragma warning disable CA1707, CA1716 // Frozen notification states and selector seam use contract names.
namespace ReminNote.Agent.Notifications;

public enum NotificationDurableDispatchDisposition
{
    APPENDED,
    REPLAYED,
    DEFERRED,
    TERMINAL,
    SKIPPED_SAFE_MODE,
}

/// <summary>
/// Result of a durable dispatch. CoreTrigger is always a fact that has
/// already crossed the scheduler's commit boundary; a missing/failed channel
/// therefore never changes the core-trigger interpretation.
/// </summary>
public sealed record NotificationDurableDispatchResult(
    NotificationTriggerFact CoreTrigger,
    NotificationDeliveryAttemptRecord? Attempt,
    NotificationDurableDispatchDisposition Disposition,
    bool ChannelInvoked,
    string? ReasonCode)
{
    public bool CoreTriggerWasRecorded => CoreTrigger is not null;

    public NotificationDeliveryOutcome? Outcome => Attempt?.Outcome;

    public bool WasDuplicate => Disposition == NotificationDurableDispatchDisposition.REPLAYED;

    public bool WasSkippedBySafeMode =>
        Disposition == NotificationDurableDispatchDisposition.SKIPPED_SAFE_MODE;

    public static NotificationDurableDispatchResult SafeMode(
        NotificationTriggerFact coreTrigger,
        string reasonCode) => new(
            coreTrigger,
            null,
            NotificationDurableDispatchDisposition.SKIPPED_SAFE_MODE,
            ChannelInvoked: false,
            ReasonCode: reasonCode);
}

/// <summary>
/// Mutable safety gate owned by Agent composition. Safe mode stops external
/// channel effects and durable delivery mutations; it never opens a fallback
/// database writer or invokes a channel directly.
/// </summary>
public sealed class NotificationDeliverySafetyGate
{
    private readonly object sync = new();
    private bool safeMode;
    private string reasonCode = "notification.safe_mode";

    public bool IsSafeMode
    {
        get
        {
            lock (sync)
            {
                return safeMode;
            }
        }
    }

    public string ReasonCode
    {
        get
        {
            lock (sync)
            {
                return reasonCode;
            }
        }
    }

    public void Enter(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) ||
            reasonCode.Length > NotificationContractLimits.MaxStableCodeBytes ||
            reasonCode.Any(character => character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("Safe-mode reason must be a stable code.", nameof(reasonCode));
        }

        lock (sync)
        {
            safeMode = true;
            this.reasonCode = reasonCode;
        }
    }

    public void Leave()
    {
        lock (sync)
        {
            safeMode = false;
        }
    }
}

public interface IReminderNotificationChannelSelector
{
    IReadOnlyList<NotificationChannelId> GetChannels(NotificationTriggerFact coreTrigger);
}

/// <summary>
/// Default channel selection uses the complete catalog snapshot supplied by
/// host composition. A lookup-only catalog falls back to the frozen P3 list,
/// which preserves a deterministic attempt for missing channels.
/// </summary>
public sealed class CatalogNotificationChannelSelector : IReminderNotificationChannelSelector
{
    private readonly INotificationChannelCatalog catalog;
    private readonly IReadOnlyList<NotificationChannelId>? configured;

    public CatalogNotificationChannelSelector(
        INotificationChannelCatalog catalog,
        IEnumerable<NotificationChannelId>? configuredChannels = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (configuredChannels is not null)
        {
            var values = configuredChannels.ToArray();
            if (values.Length == 0)
            {
                throw new ArgumentException("At least one channel must be configured.", nameof(configuredChannels));
            }

            foreach (var channel in values)
            {
                NotificationChannels.RequireKnown(channel);
            }

            if (values.Distinct().Count() != values.Length)
            {
                throw new ArgumentException("Configured channels cannot contain duplicates.", nameof(configuredChannels));
            }

            configured = values;
        }
    }

    public IReadOnlyList<NotificationChannelId> GetChannels(NotificationTriggerFact coreTrigger)
    {
        ArgumentNullException.ThrowIfNull(coreTrigger);
        if (configured is not null)
        {
            return configured;
        }

        if (catalog is INotificationChannelCatalogSnapshot snapshot)
        {
            return snapshot.ChannelIds
                .OrderBy(channel => channel.Value, StringComparer.Ordinal)
                .ToArray();
        }

        return NotificationChannels.P3;
    }
}

/// <summary>
/// Agent-side durable dispatcher. The process-local gate makes one pending
/// effect transition at a time; the durable store remains the authority for
/// restart/replay and cross-process races.
/// </summary>
public sealed class NotificationDeliveryDispatcher : IAsyncDisposable
{
    private readonly INotificationChannelCatalog channelCatalog;
    private readonly INotificationDeliveryAttemptJournalStore attemptStore;
    private readonly INotificationPresentationPolicy presentationPolicy;
    private readonly IClock clock;
    private readonly NotificationRetryPolicy retryPolicy;
    private readonly NotificationDeliverySafetyGate safetyGate;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private int disposed;

    public NotificationDeliveryDispatcher(
        INotificationChannelCatalog channelCatalog,
        INotificationDeliveryAttemptJournalStore attemptStore,
        INotificationPresentationPolicy presentationPolicy,
        IClock clock,
        NotificationRetryPolicy? retryPolicy = null,
        NotificationDeliverySafetyGate? safetyGate = null)
    {
        this.channelCatalog = channelCatalog ?? throw new ArgumentNullException(nameof(channelCatalog));
        this.attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        this.presentationPolicy = presentationPolicy ?? throw new ArgumentNullException(nameof(presentationPolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.retryPolicy = retryPolicy ?? new NotificationRetryPolicy();
        this.safetyGate = safetyGate ?? new NotificationDeliverySafetyGate();
    }

    public NotificationDeliverySafetyGate SafetyGate => safetyGate;

    public async ValueTask<NotificationDurableDispatchResult> DispatchAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        if (safetyGate.IsSafeMode)
        {
            return NotificationDurableDispatchResult.SafeMode(
                request.CoreTrigger,
                safetyGate.ReasonCode);
        }

        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DispatchSerializedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            dispatchGate.Release();
        }
    }

    /// <summary>
    /// Replays PENDING events and schedules retryable terminal failures whose
    /// backoff has elapsed. Pending requests reuse their effect key and get a
    /// fresh request ID; a retryable terminal event receives a new effect key
    /// and request ID while retaining the same logical core identity.
    /// </summary>
    public async ValueTask<IReadOnlyList<NotificationDurableDispatchResult>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (safetyGate.IsSafeMode)
        {
            return Array.Empty<NotificationDurableDispatchResult>();
        }

        var recoverable = await attemptStore.ListRecoverableAsync(
                clock.GetCurrentInstant(),
                cancellationToken)
            .ConfigureAwait(false);
        var results = new List<NotificationDurableDispatchResult>(recoverable.Count);
        foreach (var record in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new NotificationDeliveryRequest(
                record.ToTriggerFact(),
                record.ChannelId,
                record.CorrelationId,
                record.IsPending ? record.IdempotencyKey : Guid.CreateVersion7(),
                Guid.CreateVersion7());
            results.Add(await DispatchAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            dispatchGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<NotificationDurableDispatchResult> DispatchSerializedAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var exact = await attemptStore.FindByIdempotencyKeyAsync(
                request.CoreTrigger.InstanceId,
                request.ChannelId,
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (exact is not null)
        {
            EnsureMatchesIntent(exact, request);
            if (!exact.IsPending)
            {
                return Result(
                    request.CoreTrigger,
                    exact,
                    NotificationDurableDispatchDisposition.REPLAYED,
                    channelInvoked: false,
                    exact.ErrorCode);
            }

            if (exact.NextAttemptAtUtc is { } exactNext && exactNext > now)
            {
                return Result(
                    request.CoreTrigger,
                    exact,
                    NotificationDurableDispatchDisposition.DEFERRED,
                    channelInvoked: false,
                    "notification.retry.backoff");
            }

            return await DeliverPendingAsync(request, exact, cancellationToken).ConfigureAwait(false);
        }

        var current = await attemptStore.FindCurrentAsync(
                request.CoreTrigger.InstanceId,
                request.CoreTrigger.LogicalReminderId,
                request.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            EnsureMatchesCoreIntent(current, request);
            if (current.IsPending)
            {
                return await DeliverPendingAsync(
                        RequestForRecord(request, current),
                        current,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!current.Retryable)
            {
                return Result(
                    request.CoreTrigger,
                    current,
                    NotificationDurableDispatchDisposition.TERMINAL,
                    channelInvoked: false,
                    current.ErrorCode);
            }

            if (current.NextAttemptAtUtc is not { } next || next > now)
            {
                return Result(
                    request.CoreTrigger,
                    current,
                    NotificationDurableDispatchDisposition.DEFERRED,
                    channelInvoked: false,
                    "notification.retry.backoff");
            }

            request = RequestForRetry(request, current);
        }

        var pendingResult = await attemptStore.AppendPendingAsync(
                request,
                retryPolicy.MaxAttempts,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (!pendingResult.Record.IsPending)
        {
            var disposition = pendingResult.Disposition == NotificationDeliveryPendingDisposition.REPLAYED
                ? NotificationDurableDispatchDisposition.REPLAYED
                : pendingResult.Disposition == NotificationDeliveryPendingDisposition.DEFERRED
                    ? NotificationDurableDispatchDisposition.DEFERRED
                    : NotificationDurableDispatchDisposition.TERMINAL;
            return Result(
                request.CoreTrigger,
                pendingResult.Record,
                disposition,
                channelInvoked: false,
                pendingResult.Record.ErrorCode);
        }

        if (pendingResult.Disposition == NotificationDeliveryPendingDisposition.DEFERRED)
        {
            return Result(
                request.CoreTrigger,
                pendingResult.Record,
                NotificationDurableDispatchDisposition.DEFERRED,
                channelInvoked: false,
                "notification.retry.backoff");
        }

        var deliveryRequest = pendingResult.Disposition ==
            NotificationDeliveryPendingDisposition.APPENDED
            ? request
            : RequestForRecord(request, pendingResult.Record);
        return await DeliverPendingAsync(
                deliveryRequest,
                pendingResult.Record,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<NotificationDurableDispatchResult> DeliverPendingAsync(
        NotificationDeliveryRequest request,
        NotificationDeliveryAttemptRecord pending,
        CancellationToken cancellationToken)
    {
        EnsureMatchesIntent(pending, request);
        var invoked = false;
        NotificationChannelDeliveryResponse response;
        try
        {
            response = await ResolveResponseAsync(
                    request,
                    () => invoked = true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response = new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryCancelled);
        }
        catch (NotificationContractException exception)
        {
            response = new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                exception.Code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            response = new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryFailed);
        }

        var retryable = retryPolicy.CanScheduleNext(
            pending.AttemptNumber,
            response.Outcome,
            response.ErrorCode);
        Instant? nextAttemptAtUtc = null;
        if (retryable)
        {
            nextAttemptAtUtc = retryPolicy.CalculateNextAttempt(
                clock.GetCurrentInstant(),
                pending.AttemptNumber,
                response.Outcome,
                response.ErrorCode);
        }

        var completed = await attemptStore.AppendOutcomeAsync(
                pending,
                response,
                retryable,
                clock.GetCurrentInstant(),
                nextAttemptAtUtc,
                request.RequestId,
                CancellationToken.None)
            .ConfigureAwait(false);
        var disposition = completed.EventOrdinal > pending.EventOrdinal
            ? NotificationDurableDispatchDisposition.APPENDED
            : NotificationDurableDispatchDisposition.REPLAYED;
        return Result(
            request.CoreTrigger,
            completed,
            disposition,
            invoked,
            completed.ErrorCode);
    }

    private async ValueTask<NotificationChannelDeliveryResponse> ResolveResponseAsync(
        NotificationDeliveryRequest request,
        Action markInvoked,
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
                "Channel catalog returned a channel with a different ID.");
        }

        var status = channel.GetStatus() ?? throw new InvalidOperationException(
            "Notification channel returned a null status.");
        if (!status.Supports(NotificationCapability.PRESENT) ||
            (request.ChannelId == NotificationChannelId.WakeTimer &&
             !status.Supports(NotificationCapability.WAKE)))
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
                "Channel health cannot be dispatched.");
        }

        var policyDecision = presentationPolicy.Evaluate(new NotificationPresentationContext(
            request.CoreTrigger,
            status,
            clock.GetCurrentInstant()));
        if (policyDecision is null)
        {
            throw new InvalidOperationException("Notification presentation policy returned null.");
        }

        if (policyDecision.Kind == NotificationPresentationDecisionKind.SUPPRESSED_QUIET_HOURS)
        {
            return NotificationChannelDeliveryResponse.SuppressedByPolicy(
                policyDecision.ReasonCode!);
        }

        markInvoked();
        return await channel.DeliverAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Notification channel returned null delivery response.");
    }

    private static NotificationDeliveryRequest RequestForRecord(
        NotificationDeliveryRequest source,
        NotificationDeliveryAttemptRecord record) =>
        new(
            source.CoreTrigger,
            record.ChannelId,
            record.CorrelationId,
            record.IdempotencyKey,
            Guid.CreateVersion7());

    private static NotificationDeliveryRequest RequestForRetry(
        NotificationDeliveryRequest source,
        NotificationDeliveryAttemptRecord current) =>
        new(
            source.CoreTrigger,
            current.ChannelId,
            current.CorrelationId,
            source.IdempotencyKey,
            source.RequestId);

    private static NotificationDurableDispatchResult Result(
        NotificationTriggerFact coreTrigger,
        NotificationDeliveryAttemptRecord record,
        NotificationDurableDispatchDisposition disposition,
        bool channelInvoked,
        string? reasonCode) => new(
        coreTrigger,
        record,
        disposition,
        channelInvoked,
        reasonCode);

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

    private static void EnsureMatchesCoreIntent(
        NotificationDeliveryAttemptRecord record,
        NotificationDeliveryRequest request)
    {
        if (!record.MatchesCoreIntent(request.CoreTrigger, request.ChannelId))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.IdempotencyConflict,
                "The notification logical intent is already bound to another core fact.");
        }
    }
}

/// <summary>
/// Binds committed scheduler facts to the durable dispatcher. The scheduler
/// itself remains channel-agnostic: this class consumes only results returned
/// after IReminderDueTransaction.CommitAsync has completed.
/// </summary>
public sealed class ReminderNotificationRuntime : IAsyncDisposable
{
    private readonly ReminderScheduler scheduler;
    private readonly NotificationDeliveryDispatcher dispatcher;
    private readonly IReminderNotificationChannelSelector channelSelector;
    private readonly INotificationCommittedTriggerRecoverySource? committedTriggerSource;
    private int disposed;

    public ReminderNotificationRuntime(
        ReminderScheduler scheduler,
        NotificationDeliveryDispatcher dispatcher,
        IReminderNotificationChannelSelector channelSelector,
        INotificationCommittedTriggerRecoverySource? committedTriggerSource = null)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.channelSelector = channelSelector ?? throw new ArgumentNullException(nameof(channelSelector));
        this.committedTriggerSource = committedTriggerSource;
    }

    public NotificationDeliverySafetyGate SafetyGate => dispatcher.SafetyGate;

    public async ValueTask<ReminderNotificationRunResult> RunDueCycleAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (SafetyGate.IsSafeMode)
        {
            return ReminderNotificationRunResult.SafeMode(SafetyGate.ReasonCode);
        }

        // A normal scheduler tick also services due notification retries. The
        // loop is intentionally driven by the scheduler wakeup, but the
        // attempt projection remains the source of truth for retry timing and
        // idempotency.
        var deliveries = new List<NotificationDurableDispatchResult>();
        deliveries.AddRange(await dispatcher.RecoverAsync(cancellationToken).ConfigureAwait(false));
        var schedulerResult = await scheduler.RunDueCycleAsync(
                isRecovery: false,
                cancellationToken)
            .ConfigureAwait(false);
        deliveries.AddRange(await DispatchSchedulerResultAsync(
            schedulerResult,
            cancellationToken).ConfigureAwait(false));
        return new(schedulerResult, deliveries, IsRecovery: false, SkippedSafeMode: false);
    }

    public async ValueTask<ReminderNotificationRunResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (SafetyGate.IsSafeMode)
        {
            return ReminderNotificationRunResult.SafeMode(SafetyGate.ReasonCode);
        }

        var deliveries = new List<NotificationDurableDispatchResult>();
        deliveries.AddRange(await dispatcher.RecoverAsync(cancellationToken).ConfigureAwait(false));
        if (committedTriggerSource is not null)
        {
            var committedTriggers = await committedTriggerSource
                .ListCommittedTriggerFactsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var committedTrigger in committedTriggers)
            {
                deliveries.AddRange(await DispatchCommittedTriggerAsync(
                        committedTrigger,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        var schedulerResult = await scheduler.RecoverAsync(cancellationToken).ConfigureAwait(false);
        deliveries.AddRange(await DispatchSchedulerResultAsync(schedulerResult, cancellationToken).ConfigureAwait(false));
        return new(schedulerResult, deliveries, IsRecovery: true, SkippedSafeMode: false);
    }

    public async ValueTask<IReadOnlyList<NotificationDurableDispatchResult>> DispatchCommittedTriggerAsync(
        NotificationTriggerFact coreTrigger,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(coreTrigger);
        if (SafetyGate.IsSafeMode)
        {
            return channelSelector.GetChannels(coreTrigger)
                .Select(_ => NotificationDurableDispatchResult.SafeMode(coreTrigger, SafetyGate.ReasonCode))
                .ToArray();
        }

        var results = new List<NotificationDurableDispatchResult>();
        foreach (var channelId in channelSelector.GetChannels(coreTrigger))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new NotificationDeliveryRequest(
                coreTrigger,
                channelId,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7());
            results.Add(await dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
            await scheduler.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IReadOnlyList<NotificationDurableDispatchResult>> DispatchSchedulerResultAsync(
        ReminderSchedulerRunResult schedulerResult,
        CancellationToken cancellationToken)
    {
        var deliveries = new List<NotificationDurableDispatchResult>();
        foreach (var dueResult in schedulerResult.DueResults)
        {
            if (!dueResult.ShouldDispatch || dueResult.TriggerFact is not { } triggerFact)
            {
                continue;
            }

            deliveries.AddRange(await DispatchCommittedTriggerAsync(
                    triggerFact,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return deliveries;
    }
}

/// <summary>
/// Composition helper for the total Agent assembly. Windows/Widget host
/// composition supplies its real catalog (or a composite of both hosts); the
/// attempt store is then bound to the already-open Agent P2.5 store.
/// </summary>
public static class AgentNotificationRuntimeComposition
{
    public static ReminderNotificationRuntime Create(
        ReminderScheduler scheduler,
        P25StorageStore storage,
        string actualUserSid,
        INotificationChannelCatalog channelCatalog,
        IClock clock,
        INotificationPresentationPolicy? presentationPolicy = null,
        NotificationRetryPolicy? retryPolicy = null,
        NotificationDeliverySafetyGate? safetyGate = null,
        IEnumerable<NotificationChannelId>? configuredChannels = null,
        Guid? agentInstanceId = null,
        INotificationCommittedTriggerRecoverySource? committedTriggerSource = null,
        INotificationDeliveryAttemptJournalStore? attemptStore = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(channelCatalog);
        ArgumentNullException.ThrowIfNull(clock);
        var attempts = attemptStore ?? new SqliteNotificationDeliveryAttemptStore(
            storage,
            actualUserSid,
            agentInstanceId);
        var dispatcher = new NotificationDeliveryDispatcher(
            channelCatalog,
            attempts,
            presentationPolicy ?? new AllowAllNotificationPresentationPolicy(),
            clock,
            retryPolicy,
            safetyGate);
        return new ReminderNotificationRuntime(
            scheduler,
            dispatcher,
            new CatalogNotificationChannelSelector(channelCatalog, configuredChannels),
            committedTriggerSource);
    }
}

/// <summary>
/// Optional recovery source supplied by the P3-03 concrete scheduler store.
/// It returns only already-committed core trigger facts that the Agent wants
/// reconciled with channel attempts. Reading this source never creates a core
/// instance; the durable attempt store performs the logical deduplication.
/// </summary>
public interface INotificationCommittedTriggerRecoverySource
{
    ValueTask<IReadOnlyList<NotificationTriggerFact>> ListCommittedTriggerFactsAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ReminderNotificationRunResult(
    ReminderSchedulerRunResult? SchedulerResult,
    IReadOnlyList<NotificationDurableDispatchResult> Deliveries,
    bool IsRecovery,
    bool SkippedSafeMode)
{
    public string? ReasonCode { get; init; }

    public static ReminderNotificationRunResult SafeMode(string reasonCode) => new(
        SchedulerResult: null,
        Deliveries: [],
        IsRecovery: false,
        SkippedSafeMode: true)
    {
        ReasonCode = reasonCode
    };
}

#pragma warning restore CA1707, CA1716
