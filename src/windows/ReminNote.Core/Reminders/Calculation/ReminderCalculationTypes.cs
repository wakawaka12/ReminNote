using NodaTime;
using ReminNote.Core.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace ReminNote.Core.Reminders.Calculation;

/// <summary>
/// The only target kind that the P3 calculator accepts. Other target kinds
/// may be added to the shared contract later, but are intentionally not
/// executable in this slice.
/// </summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderTargetKind
{
    TASK_INSTANCE
}

/// <summary>Why a task occurrence is being reminded.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderPurpose
{
    TASK_PRE_START,
    TASK_START,
    TASK_RANGE_END,
    TASK_CUSTOM
}

/// <summary>Reminder priority; pinning is represented independently.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderPriority
{
    LOW,
    NORMAL,
    HIGH
}

/// <summary>Local task point used by a relative reminder.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderAnchor
{
    TASK_TIME,
    RANGE_START,
    RANGE_END
}

/// <summary>Discriminator for the two frozen timing shapes.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderTimingKind
{
    RELATIVE,
    ABSOLUTE_UTC
}

/// <summary>How a schedule was derived.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderScheduleCause
{
    RULE,
    SNOOZE,
    REPEAT
}

/// <summary>Durable schedule state names from the P3 contract.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderScheduleState
{
    PENDING,
    CONSUMED,
    SUPERSEDED,
    CANCELLED,
    EXPIRED
}

/// <summary>Stable reasons for a terminal schedule transition.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderScheduleTerminalReason
{
    RULE_REVISED,
    TASK_TIME_CHANGED,
    TIME_ZONE_CHANGED,
    RULE_DISABLED,
    TASK_RESULT_RECORDED,
    TASK_DELETED,
    RECOVERY_OBSOLETE
}

/// <summary>Deterministic result of mapping a local civil time through a zone.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderTimeMappingResolution
{
    NOT_APPLICABLE,
    UNAMBIGUOUS,
    AMBIGUOUS_EARLIER,
    SKIPPED_FORWARD
}

/// <summary>Whether a calculator call produced a pending schedule.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "P3 contract enum literals intentionally retain wire names.")]
public enum ReminderScheduleDerivationStatus
{
    SCHEDULED,
    NOT_SCHEDULED
}

/// <summary>
/// Product safety bounds owned by the pure calculator. They prevent an
/// int64 offset or a repeat policy from silently overflowing Noda Time or
/// creating an unbounded chain. The defaults deliberately use a conservative
/// ten-year horizon and a 64-trigger chain; callers may inject a stricter
/// profile for a deployment or test.
/// </summary>
public sealed record ReminderCalculationLimits
{
    public const long SecondsPerDay = 86_400L;
    public const long DefaultHorizonSeconds = 10L * 365L * SecondsPerDay;

    public static ReminderCalculationLimits Default { get; } = new(
        -DefaultHorizonSeconds,
        DefaultHorizonSeconds,
        DefaultHorizonSeconds,
        64);

    public ReminderCalculationLimits(
        long minRelativeOffsetSeconds,
        long maxRelativeOffsetSeconds,
        long maxRepeatIntervalSeconds = DefaultHorizonSeconds,
        int maxRepeatCount = 64)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minRelativeOffsetSeconds, maxRelativeOffsetSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRepeatIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRepeatCount);

        MinRelativeOffsetSeconds = minRelativeOffsetSeconds;
        MaxRelativeOffsetSeconds = maxRelativeOffsetSeconds;
        MaxRepeatIntervalSeconds = maxRepeatIntervalSeconds;
        MaxRepeatCount = maxRepeatCount;
    }

    public long MinRelativeOffsetSeconds { get; }

    public long MaxRelativeOffsetSeconds { get; }

    public long MaxRepeatIntervalSeconds { get; }

    /// <summary>Maximum total trigger count, including the first trigger.</summary>
    public int MaxRepeatCount { get; }
}

/// <summary>Frozen relative or absolute reminder timing shape.</summary>
public abstract record ReminderTiming
{
    private ReminderTiming(ReminderTimingKind kind)
    {
        Kind = kind;
    }

    public ReminderTimingKind Kind { get; }

    public sealed record Relative(ReminderAnchor Anchor, long OffsetSeconds)
        : ReminderTiming(ReminderTimingKind.RELATIVE);

    public sealed record AbsoluteUtc(Instant AtUtc)
        : ReminderTiming(ReminderTimingKind.ABSOLUTE_UTC);
}

/// <summary>
/// The narrow calculator projection of a P3-01 ReminderRule. Repeat/wake
/// persistence and agent timestamps remain outside this pure seam.
/// </summary>
public sealed record ReminderRuleCalculationInput(
    Guid RuleId,
    ReminderTargetKind TargetKind,
    Guid TargetId,
    Guid OccurrenceId,
    Guid LogicalReminderId,
    ReminderPurpose Purpose,
    ReminderTiming Timing,
    ReminderPriority Priority,
    bool Pinned,
    bool Enabled,
    long RuleRevision,
    TimeSpec TaskTimeSpec);

/// <summary>Identity and revision supplied by the persistence/agent adapter.</summary>
public sealed record ReminderScheduleRequest(Guid ScheduleId, long ScheduleRevision);

/// <summary>
/// An occurrence adapter input. P3-02 does not define recurrence; a future
/// adapter supplies a stable occurrence id and its existing Task.TimeSpec.
/// </summary>
public sealed record ReminderOccurrenceInput(Guid OccurrenceId, TimeSpec TaskTimeSpec);

/// <summary>Optional read-only seam for a future occurrence provider.</summary>
public interface IReminderOccurrenceInputResolver
{
    bool TryResolve(Guid occurrenceId, out ReminderOccurrenceInput occurrence);
}

/// <summary>Resolved local/UTC result, including timezone provenance.</summary>
public sealed record ReminderTimeCalculation(
    ReminderTimingKind TimingKind,
    ReminderAnchor? Anchor,
    long? OffsetSeconds,
    LocalDateTime? AnchorLocalDateTime,
    LocalDateTime? ResolvedLocalDateTime,
    Offset? AppliedOffset,
    string? TimeZoneId,
    ReminderTimeMappingResolution MappingResolution,
    Instant TriggerAtUtc);

/// <summary>A new immutable pending schedule plan.</summary>
public sealed record ReminderSchedulePlan(
    Guid ScheduleId,
    Guid RuleId,
    Guid TargetId,
    Guid OccurrenceId,
    Guid LogicalReminderId,
    ReminderPurpose Purpose,
    ReminderPriority Priority,
    bool Pinned,
    ReminderScheduleCause Cause,
    Guid? OriginScheduleId,
    long RuleRevision,
    long ScheduleRevision,
    Instant TriggerAtUtc,
    string? TimeZoneId,
    ReminderScheduleState State);

/// <summary>
/// Minimal immutable snapshot of a currently pending schedule used for a
/// rebuild. It deliberately contains no ReminderInstance or mutable state.
/// </summary>
public sealed record PendingScheduleSnapshot(
    Guid ScheduleId,
    Guid RuleId,
    Guid TargetId,
    Guid OccurrenceId,
    Guid LogicalReminderId,
    ReminderPurpose Purpose,
    ReminderPriority Priority,
    bool Pinned,
    long RuleRevision,
    long ScheduleRevision,
    Instant TriggerAtUtc,
    string? TimeZoneId)
{
    /// <summary>Optional provenance retained by an adapter when available.</summary>
    public ReminderScheduleCause Cause { get; init; } = ReminderScheduleCause.RULE;

    /// <summary>Optional origin retained by an adapter when available.</summary>
    public Guid? OriginScheduleId { get; init; }
}

/// <summary>
/// Read-only origin snapshot for SNOOZE/REPEAT derivation. The origin must be
/// CONSUMED; its trigger and other snapshot fields are never rewritten.
/// </summary>
public sealed record ReminderScheduleOrigin(
    Guid ScheduleId,
    Guid RuleId,
    Guid TargetId,
    Guid OccurrenceId,
    Guid LogicalReminderId,
    ReminderPurpose Purpose,
    ReminderPriority Priority,
    bool Pinned,
    long RuleRevision,
    long ScheduleRevision,
    Instant TriggerAtUtc,
    string? TimeZoneId,
    ReminderScheduleState State);

/// <summary>A repeat policy projection; maxCount includes the first trigger.</summary>
public sealed record ReminderRepeatPolicy(bool Enabled, long? IntervalSeconds, int? MaxCount);

/// <summary>One terminal transition produced during rebuild/cancellation.</summary>
public sealed record ReminderScheduleTerminalTransition(
    Guid ScheduleId,
    ReminderScheduleState State,
    ReminderScheduleTerminalReason Reason,
    Guid? ReplacementScheduleId);

/// <summary>Pure result of rebuilding a set of pending schedules.</summary>
public sealed record ReminderScheduleRebuildPlan
{
    public ReminderScheduleRebuildPlan(
        bool isNoOp,
        IReadOnlyList<ReminderScheduleTerminalTransition> invalidatedSchedules,
        ReminderSchedulePlan? replacementSchedule,
        ReminderScheduleTerminalReason? rebuildReason)
    {
        ArgumentNullException.ThrowIfNull(invalidatedSchedules);

        IsNoOp = isNoOp;
        InvalidatedSchedules = invalidatedSchedules.ToArray();
        ReplacementSchedule = replacementSchedule;
        RebuildReason = rebuildReason;
    }

    public bool IsNoOp { get; }

    public IReadOnlyList<ReminderScheduleTerminalTransition> InvalidatedSchedules { get; }

    public ReminderSchedulePlan? ReplacementSchedule { get; }

    public ReminderScheduleTerminalReason? RebuildReason { get; }
}

/// <summary>Pure schedule derivation result; no persistence side effects.</summary>
public sealed record ReminderScheduleDerivationResult(
    ReminderSchedulePlan? Schedule,
    ReminderScheduleDerivationStatus Status,
    string? ReasonCode)
{
    public bool IsScheduled => Status == ReminderScheduleDerivationStatus.SCHEDULED;

    public static ReminderScheduleDerivationResult Scheduled(ReminderSchedulePlan schedule) =>
        new(schedule, ReminderScheduleDerivationStatus.SCHEDULED, null);

    public static ReminderScheduleDerivationResult NotScheduled(string reasonCode) =>
        new(null, ReminderScheduleDerivationStatus.NOT_SCHEDULED, reasonCode);
}

/// <summary>Stable calculator error codes shared by tests and adapters.</summary>
public static class ReminderCalculationCodes
{
    public const string RuleDisabled = "reminder.rule.disabled";
    public const string RepeatDisabled = "reminder.repeat.disabled";
    public const string RepeatLimitReached = "reminder.repeat.limit_reached";
}
