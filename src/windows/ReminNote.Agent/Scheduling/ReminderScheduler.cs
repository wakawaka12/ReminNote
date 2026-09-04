using NodaTime;
using ReminNote.Core;
using Calculation = ReminNote.Core.Reminders.Calculation;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Policy;

namespace ReminNote.Agent.Scheduling;

/// <summary>
/// Agent-owned orchestration for the durable Reminder loop. It retains no
/// pending schedule cache: every pass reads the store, consumes all schedules
/// already due at one clock instant, and then reads durable state again to
/// calculate the next wakeup.
/// </summary>
public sealed class ReminderScheduler : IAsyncDisposable
{
    private readonly IReminderSchedulerStore store;
    private readonly IClock clock;
    private readonly ReminderRecoveryPolicy recoveryPolicy;
    private readonly ReminderRepeatSafetyLimits repeatLimits;
    private readonly IReminderIdentityGenerator identityGenerator;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private int disposed;

    public ReminderScheduler(
        IReminderSchedulerStore store,
        IClock clock,
        ReminderRecoveryPolicy? recoveryPolicy = null,
        ReminderRepeatSafetyLimits? repeatLimits = null,
        IReminderIdentityGenerator? identityGenerator = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.recoveryPolicy = recoveryPolicy ?? ReminderRecoveryPolicy.Default;
        this.repeatLimits = repeatLimits ?? ReminderRepeatSafetyLimits.Default;
        this.identityGenerator = identityGenerator ?? new UuidV7ReminderIdentityGenerator();
    }

    /// <summary>
    /// Processes every schedule that is due at the instant captured at the
    /// beginning of the pass. The durable read is repeated after each batch so
    /// a repeat derived from an overdue origin is also recovered in this pass;
    /// processed IDs prevent rejected/still-pending rows from causing a busy
    /// loop. Repeat policy bounds the number of newly created rows.
    /// </summary>
    public async ValueTask<ReminderSchedulerRunResult> RunDueCycleAsync(
        bool isRecovery = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var evaluatedAtUtc = clock.GetCurrentInstant();
            var processedScheduleIds = new HashSet<ReminderScheduleId>();
            var results = new List<ReminderDueResult>();
            while (true)
            {
                var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
                var due = pending
                    .Where(item =>
                        item.Schedule.TriggerAtUtc <= evaluatedAtUtc &&
                        !processedScheduleIds.Contains(item.Schedule.Id))
                    .OrderBy(item => item.Schedule.TriggerAtUtc)
                    .ThenBy(item => item.Schedule.ScheduleRevision)
                    .ThenBy(item => item.Schedule.Id.Value.ToString("D"), StringComparer.Ordinal)
                    .ToArray();
                if (due.Length == 0)
                {
                    break;
                }

                foreach (var candidate in due)
                {
                    processedScheduleIds.Add(candidate.Schedule.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(await ProcessDueAsync(
                            candidate,
                            evaluatedAtUtc,
                            isRecovery,
                            cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            var nextWakeupUtc = await FindNextWakeupCoreAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return new(
                evaluatedAtUtc,
                isRecovery,
                results,
                nextWakeupUtc);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    /// <summary>
    /// Restart/resume entry point. It deliberately has no in-memory recovery
    /// state: the store's durable PENDING rows are the recovery input.
    /// </summary>
    public ValueTask<ReminderSchedulerRunResult> RecoverAsync(
        CancellationToken cancellationToken = default) =>
        RunDueCycleAsync(isRecovery: true, cancellationToken);

    /// <summary>
    /// Returns the nearest in-process timer wakeup across all pending rows.
    /// A NO wake policy still needs this timer while the Agent is running; OS
    /// wake permission is evaluated by <see cref="FindNearestWakeupAsync"/>.
    /// </summary>
    public async ValueTask<Instant?> FindNextWakeupAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindNextWakeupCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    /// <summary>
    /// Returns the nearest wakeup permitted by the profile and OS capability.
    /// This is a policy result only; failure to request OS wake never changes a
    /// schedule or Instance.
    /// </summary>
    public async ValueTask<Instant?> FindNearestWakeupAsync(
        WakeProfile profile,
        WakeCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capability);
        ObjectDisposedException.ThrowIf(disposed != 0, this);

        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nowUtc = clock.GetCurrentInstant();
            var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
            var candidates = pending
                .Where(item => item.Rule?.Enabled == true)
                .Select(item => new PendingWakeCandidate(
                    item.Schedule.Id.Value,
                    item.Schedule.TriggerAtUtc,
                    ReminderWakePolicy.Resolve(
                            item.Rule!.WakePolicy,
                            profile,
                            capability)
                        .Outcome))
                .ToArray();

            return ReminderWakePolicy.FindNearestWakeup(nowUtc, candidates);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        cycleGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<ReminderDueResult> ProcessDueAsync(
        ReminderPendingSchedule candidate,
        Instant evaluatedAtUtc,
        bool isRecovery,
        CancellationToken cancellationToken)
    {
        var transaction = await store.BeginDueTransactionAsync(
                candidate.Schedule.Id,
                evaluatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null)
        {
            return NotClaimed(candidate.Schedule.Id);
        }

        var commitBoundaryEntered = false;
        try
        {
            var schedule = transaction.Schedule;
            if (!schedule.IsPending)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return NotClaimed(schedule.Id);
            }

            if (schedule.TriggerAtUtc > evaluatedAtUtc)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    schedule.Id,
                    ReminderDueResultKind.NOT_DUE,
                    ReminderSchedulerCodes.NotDue,
                    ScheduleState.PENDING,
                    null,
                    null,
                    Replayed: false,
                    ShouldDispatch: false);
            }

            var mutation = BuildMutation(transaction, evaluatedAtUtc, isRecovery);
            if (mutation is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    schedule.Id,
                    ReminderDueResultKind.REJECTED,
                    ReminderSchedulerCodes.RecoveryRejected,
                    ScheduleState.PENDING,
                    null,
                    null,
                    Replayed: false,
                    ShouldDispatch: false);
            }

            // A due transaction is the Agent's commit boundary. Cancellation
            // after this point must not turn an unknown commit into a second
            // domain attempt. CommitAsync owns rollback/reconciliation for
            // failures after its boundary has been entered.
            cancellationToken.ThrowIfCancellationRequested();
            commitBoundaryEntered = true;
            var result = await transaction.CommitAsync(
                    mutation,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return MapCommitResult(schedule.Id, mutation, result);
        }
        catch
        {
            if (!commitBoundaryEntered)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ReminderDueMutation? BuildMutation(
        IReminderDueTransaction transaction,
        Instant evaluatedAtUtc,
        bool isRecovery)
    {
        var schedule = transaction.Schedule;
        var rule = transaction.Rule;
        if (rule is null)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_DELETED,
                ReminderSchedulerCodes.RuleMissing);
        }

        if (rule.Id != schedule.RuleId || rule.OccurrenceId != schedule.OccurrenceId)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_DELETED,
                ReminderSchedulerCodes.TargetMismatch);
        }

        if (rule.RuleRevision != schedule.RuleRevision)
        {
            // A pending row from an old rule must never be fired. The rebuild
            // owner should normally have supplied a replacement; without one,
            // CANCELLED is the only truthful terminal state available here.
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.RULE_REBUILT,
                ReminderSchedulerCodes.RuleRevisionStale);
        }

        if (!rule.Enabled)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.RULE_DISABLED,
                ReminderSchedulerCodes.RuleDisabled);
        }

        var target = transaction.Target;
        if (target is null)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_DELETED,
                ReminderSchedulerCodes.TargetMissing);
        }

        if (target.TargetId != rule.TargetId || target.OccurrenceId != rule.OccurrenceId)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_DELETED,
                ReminderSchedulerCodes.TargetMismatch);
        }

        // A regular timer tick is not a recovery summary. Cancellation and
        // duplicate guards still apply, but a due schedule with no prior core
        // fact is recorded immediately. RecoveryPolicy is reserved for rows
        // that were already overdue when the Agent resumed/restarted.
        if (target.OccurrenceCancelled)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_DELETED,
                ReminderPolicyCodes.RecoveryOccurrenceCancelled);
        }

        if (target.TaskResultRecorded)
        {
            return ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                ScheduleStateReason.TASK_RESULT_RECORDED,
                ReminderPolicyCodes.RecoveryTaskResultRecorded);
        }

        if (transaction.ExistingInstance is not null)
        {
            return ReminderDueMutation.ConsumeDuplicate(
                evaluatedAtUtc,
                ReminderPolicyCodes.RecoveryDuplicate);
        }

        if (!isRecovery)
        {
            return BuildTriggeredMutation(
                transaction,
                evaluatedAtUtc,
                evaluatedAtUtc,
                ReminderSchedulerCodes.DueTriggered);
        }

        var decision = recoveryPolicy.Evaluate(new ReminderRecoveryInput(
            schedule.PurposeSnapshot,
            schedule.TriggerAtUtc,
            evaluatedAtUtc,
            target.TaskStartAtUtc,
            target.OccurrenceCancelled,
            target.TaskResultRecorded,
            transaction.ExistingInstance is not null,
            target.CustomReminderMeaningful,
            schedule.PrioritySnapshot,
            schedule.PinnedSnapshot));

        return decision.SuggestedScheduleState switch
        {
            ScheduleState.CANCELLED => ReminderDueMutation.Cancel(
                evaluatedAtUtc,
                decision.ReasonCode == ReminderPolicyCodes.RecoveryOccurrenceCancelled
                    ? ScheduleStateReason.TASK_DELETED
                    : ScheduleStateReason.TASK_RESULT_RECORDED,
                decision.ReasonCode),
            ScheduleState.EXPIRED => ReminderDueMutation.Expire(
                evaluatedAtUtc,
                decision.ReasonCode),
            ScheduleState.CONSUMED when decision.RecordCoreInstance =>
                BuildTriggeredMutation(
                    transaction,
                    evaluatedAtUtc,
                    decision.CoreFactAtUtc ?? evaluatedAtUtc,
                    decision.ReasonCode),
            ScheduleState.CONSUMED => ReminderDueMutation.ConsumeDuplicate(
                evaluatedAtUtc,
                decision.ReasonCode),
            ScheduleState.PENDING => null,
            _ => null
        };
    }

    private ReminderDueMutation BuildTriggeredMutation(
        IReminderDueTransaction transaction,
        Instant evaluatedAtUtc,
        Instant triggeredAtUtc,
        string reasonCode)
    {
        var schedule = transaction.Schedule;
        var instance = ReminderInstance.CreateFromSchedule(
            schedule,
            transaction.NextAttemptOrdinal,
            triggeredAtUtc,
            identityGenerator.NewInstanceId());
        var derivedSchedule = TryCreateRepeatSchedule(transaction, evaluatedAtUtc);
        return ReminderDueMutation.Trigger(
            evaluatedAtUtc,
            reasonCode,
            instance,
            derivedSchedule);
    }

    private ReminderSchedule? TryCreateRepeatSchedule(
        IReminderDueTransaction transaction,
        Instant evaluatedAtUtc)
    {
        var rule = transaction.Rule;
        if (rule is null || !rule.RepeatPolicy.Enabled)
        {
            return null;
        }

        int nextAttemptOrdinal;
        try
        {
            nextAttemptOrdinal = checked(transaction.NextAttemptOrdinal + 1);
        }
        catch (OverflowException)
        {
            return null;
        }

        var decision = ReminderRepeatPolicyEvaluator.Evaluate(
            rule.RepeatPolicy,
            nextAttemptOrdinal,
            repeatLimits);
        if (!decision.Allowed || decision.IntervalSeconds is null)
        {
            return null;
        }

        if (transaction.NextScheduleRevision <= transaction.Schedule.ScheduleRevision)
        {
            throw new InvalidOperationException(
                "The due transaction did not supply a fresh schedule revision for repeat.");
        }

        var derivedScheduleId = identityGenerator.NewScheduleId();
        var origin = new Calculation.ReminderScheduleOrigin(
            transaction.Schedule.Id.Value,
            transaction.Schedule.RuleId.Value,
            rule.TargetId,
            transaction.Schedule.OccurrenceId.Value,
            transaction.Schedule.LogicalReminderId.Value,
            ToCalculationPurpose(transaction.Schedule.PurposeSnapshot),
            ToCalculationPriority(transaction.Schedule.PrioritySnapshot),
            transaction.Schedule.PinnedSnapshot,
            transaction.Schedule.RuleRevision,
            transaction.Schedule.ScheduleRevision,
            transaction.Schedule.TriggerAtUtc,
            transaction.Schedule.TimeZoneId,
            Calculation.ReminderScheduleState.CONSUMED);
        var calculationPolicy = new Calculation.ReminderRepeatPolicy(
            rule.RepeatPolicy.Enabled,
            rule.RepeatPolicy.IntervalSeconds,
            rule.RepeatPolicy.MaxCount);
        var calculationLimits = new Calculation.ReminderCalculationLimits(
            -Calculation.ReminderCalculationLimits.DefaultHorizonSeconds,
            Calculation.ReminderCalculationLimits.DefaultHorizonSeconds,
            repeatLimits.MaxIntervalSeconds,
            repeatLimits.MaxCount);

        Calculation.ReminderScheduleDerivationResult derivation;
        try
        {
            derivation = Calculation.ReminderSchedulePlanner.DeriveRepeat(
                origin,
                derivedScheduleId.Value,
                transaction.NextScheduleRevision,
                decision.NextAttemptOrdinal,
                calculationPolicy,
                calculationLimits);
        }
        catch (DomainValidationException exception) when (IsRepeatTimeOverflow(exception))
        {
            // The first core trigger can still be committed. A repeat whose
            // calculated time is outside Noda Time's range is simply not
            // scheduled, rather than making the already-due fact retryable.
            return null;
        }

        if (!derivation.IsScheduled || derivation.Schedule is not { } plan)
        {
            return null;
        }

        return ReminderSchedule.CreateDerived(
            transaction.Schedule,
            ScheduleCause.REPEAT,
            transaction.NextScheduleRevision,
            plan.TriggerAtUtc,
            evaluatedAtUtc,
            plan.TimeZoneId,
            derivedScheduleId);
    }

    private static ReminderDueResult MapCommitResult(
        ReminderScheduleId scheduleId,
        ReminderDueMutation mutation,
        ReminderDueCommitResult result)
    {
        if (result.Disposition == ReminderDueCommitDisposition.NOT_DUE)
        {
            return new(
                scheduleId,
                ReminderDueResultKind.NOT_DUE,
                result.ReasonCode,
                result.ScheduleState,
                null,
                null,
                Replayed: false,
                ShouldDispatch: false);
        }

        if (result.Disposition == ReminderDueCommitDisposition.REJECTED)
        {
            return new(
                scheduleId,
                ReminderDueResultKind.REJECTED,
                result.ReasonCode,
                result.ScheduleState,
                null,
                null,
                Replayed: false,
                ShouldDispatch: false);
        }

        // The durable adapter re-checks rule/task facts after the scan. A
        // trigger plan can therefore be closed as CANCELLED/EXPIRED when a
        // Task result or rule change won the Agent writer race. Map from the
        // committed state first so that a stale scan can never dispatch a
        // notification for a terminal schedule.
        var kind = result.ScheduleState switch
        {
            ScheduleState.CANCELLED => ReminderDueResultKind.CANCELLED,
            ScheduleState.EXPIRED => ReminderDueResultKind.EXPIRED,
            _ => mutation.Kind switch
            {
                ReminderDueMutationKind.TRIGGER => result.IsReplay
                    ? ReminderDueResultKind.DUPLICATE
                    : ReminderDueResultKind.TRIGGERED,
                ReminderDueMutationKind.CONSUME_DUPLICATE => ReminderDueResultKind.DUPLICATE,
                ReminderDueMutationKind.CANCEL => ReminderDueResultKind.CANCELLED,
                ReminderDueMutationKind.EXPIRE => ReminderDueResultKind.EXPIRED,
                _ => ReminderDueResultKind.REJECTED
            }
        };
        var instance = result.IsReplay
            ? result.Instance
            : result.Instance ?? mutation.Instance;
        var shouldDispatch = kind == ReminderDueResultKind.TRIGGERED &&
            !result.IsReplay &&
            instance is not null;
        var derivedSchedule = result.IsReplay
            ? result.DerivedSchedule
            : result.DerivedSchedule ?? mutation.DerivedSchedule;

        var dueResult = new ReminderDueResult(
            scheduleId,
            kind,
            result.ReasonCode,
            result.ScheduleState,
            instance,
            derivedSchedule,
            result.IsReplay,
            shouldDispatch);
        return dueResult with
        {
            TriggerFact = shouldDispatch
                ? ToNotificationTriggerFact(instance!)
                : null
        };
    }

    private static ReminderDueResult NotClaimed(ReminderScheduleId scheduleId) => new(
        scheduleId,
        ReminderDueResultKind.NOT_CLAIMED,
        ReminderSchedulerCodes.NotClaimed,
        null,
        null,
        null,
        Replayed: false,
        ShouldDispatch: false);

    private async ValueTask<Instant?> FindNextWakeupCoreAsync(
        CancellationToken cancellationToken)
    {
        var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        var wakeups = pending
            .Select(item => item.Schedule.TriggerAtUtc)
            .OrderBy(instant => instant)
            .ToArray();
        return wakeups.Length == 0 ? null : wakeups[0];
    }

    public static NotificationTriggerFact ToNotificationTriggerFact(
        ReminderInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new NotificationTriggerFact(
            instance.Id.Value,
            instance.ScheduleId.Value,
            instance.RuleId.Value,
            instance.OccurrenceId.Value,
            instance.LogicalReminderId.Value,
            instance.AttemptOrdinal,
            new NotificationPurposeSnapshot(ToNotificationPurpose(instance.PurposeSnapshot)),
            ToNotificationPriority(instance.PrioritySnapshot),
            instance.PinnedSnapshot,
            instance.TriggeredAtUtc);
    }

    private static string ToNotificationPurpose(ReminderPurpose purpose) => purpose switch
    {
        ReminderPurpose.TASK_PRE_START => "TASK_PRE_START",
        ReminderPurpose.TASK_START => "TASK_START",
        ReminderPurpose.TASK_RANGE_END => "TASK_RANGE_END",
        ReminderPurpose.TASK_CUSTOM => "TASK_CUSTOM",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    private static Calculation.ReminderPurpose ToCalculationPurpose(ReminderPurpose purpose) => purpose switch
    {
        ReminderPurpose.TASK_PRE_START => Calculation.ReminderPurpose.TASK_PRE_START,
        ReminderPurpose.TASK_START => Calculation.ReminderPurpose.TASK_START,
        ReminderPurpose.TASK_RANGE_END => Calculation.ReminderPurpose.TASK_RANGE_END,
        ReminderPurpose.TASK_CUSTOM => Calculation.ReminderPurpose.TASK_CUSTOM,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    private static Calculation.ReminderPriority ToCalculationPriority(ReminderPriority priority) => priority switch
    {
        ReminderPriority.LOW => Calculation.ReminderPriority.LOW,
        ReminderPriority.NORMAL => Calculation.ReminderPriority.NORMAL,
        ReminderPriority.HIGH => Calculation.ReminderPriority.HIGH,
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };

    private static bool IsRepeatTimeOverflow(DomainValidationException exception) =>
        exception.Errors.Any(error => error.Code == "reminder.repeat.time.overflow");

    private static NotificationPriority ToNotificationPriority(ReminderPriority priority) => priority switch
    {
        ReminderPriority.LOW => NotificationPriority.LOW,
        ReminderPriority.NORMAL => NotificationPriority.NORMAL,
        ReminderPriority.HIGH => NotificationPriority.HIGH,
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };
}
