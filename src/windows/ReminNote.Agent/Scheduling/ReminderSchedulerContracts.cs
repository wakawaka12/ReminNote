using NodaTime;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Notifications;

#pragma warning disable CA1707 // Frozen uppercase state names are part of the P3 contract.

namespace ReminNote.Agent.Scheduling;

/// <summary>
/// Durable input returned by the scheduler store. The store must read this
/// snapshot from the same Agent-owned persistence boundary as the due
/// transaction; it must not be assembled from an in-process timer cache.
/// </summary>
public sealed record ReminderPendingSchedule
{
    public ReminderPendingSchedule(ReminderSchedule schedule, ReminderRule? rule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.IsPending)
        {
            throw new ArgumentException(
                "A pending scheduler input must contain a PENDING schedule.",
                nameof(schedule));
        }

        if (rule is not null &&
            (schedule.RuleId != rule.Id || schedule.OccurrenceId != rule.OccurrenceId))
        {
            throw new ArgumentException(
                "The schedule and rule must belong to the same reminder occurrence.",
                nameof(rule));
        }

        Schedule = schedule;
        Rule = rule;
    }

    public ReminderSchedule Schedule { get; }

    public ReminderRule? Rule { get; }
}

/// <summary>
/// Target facts needed to distinguish an obsolete, cancelled, or meaningful
/// due reminder. This is a read-only snapshot; it is not a second writer.
/// </summary>
public sealed record ReminderTargetState(
    Guid TargetId,
    OccurrenceId OccurrenceId,
    Instant? TaskStartAtUtc,
    bool OccurrenceCancelled,
    bool TaskResultRecorded,
    bool CustomReminderMeaningful);

/// <summary>
/// The result of one durable due transaction. A production implementation must
/// make the schedule transition, optional core instance, and optional derived
/// schedule one atomic Agent-writer operation.
/// </summary>
public sealed record ReminderDueCommitResult(
    ReminderDueCommitDisposition Disposition,
    ScheduleState ScheduleState,
    ReminderInstance? Instance,
    ReminderSchedule? DerivedSchedule,
    string ReasonCode)
{
    public bool IsReplay => Disposition == ReminderDueCommitDisposition.REPLAYED;

    public bool IsCommitted => Disposition is
        ReminderDueCommitDisposition.COMMITTED or
        ReminderDueCommitDisposition.REPLAYED;
}

public enum ReminderDueCommitDisposition
{
    COMMITTED,
    REPLAYED,
    NOT_DUE,
    REJECTED
}

/// <summary>
/// A mutation plan produced by the scheduler. It contains no channel side
/// effect. Channel delivery starts only after the store reports a committed
/// core fact.
/// </summary>
public sealed record ReminderDueMutation(
    ReminderDueMutationKind Kind,
    Instant AtUtc,
    string ReasonCode,
    ScheduleStateReason TerminalReason,
    ReminderInstance? Instance,
    ReminderSchedule? DerivedSchedule)
{
    public static ReminderDueMutation Trigger(
        Instant atUtc,
        string reasonCode,
        ReminderInstance instance,
        ReminderSchedule? derivedSchedule = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new(
            ReminderDueMutationKind.TRIGGER,
            atUtc,
            RequireReasonCode(reasonCode),
            ScheduleStateReason.DUE_CONSUMED,
            instance,
            derivedSchedule);
    }

    public static ReminderDueMutation ConsumeDuplicate(
        Instant atUtc,
        string reasonCode) =>
        new(
            ReminderDueMutationKind.CONSUME_DUPLICATE,
            atUtc,
            RequireReasonCode(reasonCode),
            ScheduleStateReason.DUE_CONSUMED,
            null,
            null);

    public static ReminderDueMutation Cancel(
        Instant atUtc,
        ScheduleStateReason terminalReason,
        string reasonCode) =>
        new(
            ReminderDueMutationKind.CANCEL,
            atUtc,
            RequireReasonCode(reasonCode),
            RequireTerminalReason(terminalReason),
            null,
            null);

    public static ReminderDueMutation Expire(
        Instant atUtc,
        string reasonCode) =>
        new(
            ReminderDueMutationKind.EXPIRE,
            atUtc,
            RequireReasonCode(reasonCode),
            ScheduleStateReason.RECOVERY_OBSOLETE,
            null,
            null);

    private static string RequireReasonCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A scheduler reason code must be bounded and contain no controls.",
                nameof(value));
        }

        return value;
    }

    private static ScheduleStateReason RequireTerminalReason(ScheduleStateReason value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

public enum ReminderDueMutationKind
{
    TRIGGER,
    CONSUME_DUPLICATE,
    CANCEL,
    EXPIRE
}

/// <summary>
/// The transaction-bound view used by <see cref="ReminderScheduler"/>. The
/// implementation owns the concrete transaction and must re-check PENDING at
/// commit time. It is expected to be entered through the existing Agent writer
/// serialization, never through a new DbContext or timer callback.
/// </summary>
public interface IReminderDueTransaction : IAsyncDisposable
{
    ReminderSchedule Schedule { get; }

    ReminderRule? Rule { get; }

    ReminderTargetState? Target { get; }

    ReminderInstance? ExistingInstance { get; }

    /// <summary>
    /// The next attempt ordinal for this logical chain, calculated from durable
    /// Instance rows while the transaction is bound to the writer.
    /// </summary>
    int NextAttemptOrdinal { get; }

    /// <summary>
    /// The next (rule, occurrence) schedule revision. It must be greater than
    /// every existing schedule revision, including terminal rows.
    /// </summary>
    long NextScheduleRevision { get; }

    /// <summary>
    /// Atomically applies the schedule terminal transition, optional Instance,
    /// and optional repeat schedule. Commit is the due transaction boundary;
    /// the scheduler does not call a notification channel from this method.
    /// The implementation owns rollback/reconciliation if commit fails after
    /// that boundary is entered; the scheduler must not issue a second
    /// rollback in that case.
    /// </summary>
    ValueTask<ReminderDueCommitResult> CommitAsync(
        ReminderDueMutation mutation,
        CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable scheduler store seam. The eventual implementation is an adapter to
/// the already-open Agent writer transaction. This interface intentionally has
/// no SQLite/EF operations, so a fake can prove ordering without becoming a
/// production writer.
/// </summary>
public interface IReminderSchedulerStore
{
    ValueTask<IReadOnlyList<ReminderPendingSchedule>> ReadPendingAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads and claims one schedule in the Agent writer serialization. A
    /// null result means the durable row is no longer PENDING (for example a
    /// concurrent command or a previous scan already consumed it).
    /// </summary>
    ValueTask<IReminderDueTransaction?> BeginDueTransactionAsync(
        ReminderScheduleId scheduleId,
        Instant observedAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>UUID v7 allocation is injectable to keep the seam deterministic in tests.</summary>
public interface IReminderIdentityGenerator
{
    ReminderInstanceId NewInstanceId();

    ReminderScheduleId NewScheduleId();
}

public sealed class UuidV7ReminderIdentityGenerator : IReminderIdentityGenerator
{
    public ReminderInstanceId NewInstanceId() => ReminderInstanceId.New();

    public ReminderScheduleId NewScheduleId() => ReminderScheduleId.New();
}

public enum ReminderDueResultKind
{
    TRIGGERED,
    CANCELLED,
    EXPIRED,
    DUPLICATE,
    NOT_DUE,
    NOT_CLAIMED,
    REJECTED
}

/// <summary>Observable result for one schedule considered by a due cycle.</summary>
public sealed record ReminderDueResult(
    ReminderScheduleId ScheduleId,
    ReminderDueResultKind Kind,
    string ReasonCode,
    ScheduleState? FinalScheduleState,
    ReminderInstance? Instance,
    ReminderSchedule? DerivedSchedule,
    bool Replayed,
    bool ShouldDispatch)
{
    public bool CoreTriggerWasRecorded => Instance is not null;

    /// <summary>
    /// Channel-neutral projection of the committed core fact. The scheduler
    /// never invokes a channel; the caller may pass this fact to the P3-04
    /// delivery coordinator after the due transaction has committed.
    /// </summary>
    public NotificationTriggerFact? TriggerFact { get; init; }
}

/// <summary>One scheduler pass, including a freshly recomputed next wakeup.</summary>
public sealed record ReminderSchedulerRunResult(
    Instant EvaluatedAtUtc,
    bool IsRecovery,
    IReadOnlyList<ReminderDueResult> DueResults,
    Instant? NextWakeupUtc)
{
    public int ProcessedCount => DueResults.Count;

    public int TriggeredCount => DueResults.Count(result => result.Kind == ReminderDueResultKind.TRIGGERED);
}

public static class ReminderSchedulerCodes
{
    public const string NotDue = "reminder.scheduler.not_due";
    public const string NotClaimed = "reminder.scheduler.not_claimed";
    public const string DueTriggered = "reminder.scheduler.due_triggered";
    public const string Duplicate = "reminder.recovery.duplicate";
    public const string RuleMissing = "reminder.scheduler.rule_missing";
    public const string RuleDisabled = "reminder.scheduler.rule_disabled";
    public const string RuleRevisionStale = "reminder.scheduler.rule_revision_stale";
    public const string TargetMissing = "reminder.scheduler.target_missing";
    public const string TargetMismatch = "reminder.scheduler.target_mismatch";
    public const string RecoveryRejected = "reminder.scheduler.recovery_rejected";
    public const string CommitRejected = "reminder.scheduler.commit_rejected";
}

#pragma warning restore CA1707
