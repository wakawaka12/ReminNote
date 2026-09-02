using System.Text;
using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// 可重建的执行计划。创建后其触发时间、版本、cause 和链关系均为事实，
/// 只能通过状态转换标记为 terminal，不能覆盖原行。
/// </summary>
public sealed class ReminderSchedule
{
    private const int MaxTimeZoneIdBytes = 128;

    private ReminderSchedule(
        ReminderScheduleId id,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        ReminderScheduleId? originScheduleId,
        ScheduleCause cause,
        long ruleRevision,
        long scheduleRevision,
        Instant triggerAtUtc,
        string? timeZoneId,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        bool pinnedSnapshot,
        ScheduleState state,
        ScheduleStateReason? terminalReason,
        ReminderScheduleId? replacementScheduleId,
        Instant createdAtUtc,
        Instant? terminalAtUtc)
    {
        Validate(
            id,
            ruleId,
            occurrenceId,
            logicalReminderId,
            originScheduleId,
            cause,
            ruleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId,
            purposeSnapshot,
            prioritySnapshot,
            state,
            terminalReason,
            replacementScheduleId,
            createdAtUtc,
            terminalAtUtc);

        Id = id;
        RuleId = ruleId;
        OccurrenceId = occurrenceId;
        LogicalReminderId = logicalReminderId;
        OriginScheduleId = originScheduleId;
        Cause = cause;
        RuleRevision = ruleRevision;
        ScheduleRevision = scheduleRevision;
        TriggerAtUtc = triggerAtUtc;
        TimeZoneId = NormalizeTimeZoneId(timeZoneId);
        PurposeSnapshot = purposeSnapshot;
        PrioritySnapshot = prioritySnapshot;
        PinnedSnapshot = pinnedSnapshot;
        State = state;
        TerminalReason = terminalReason;
        ReplacementScheduleId = replacementScheduleId;
        CreatedAtUtc = createdAtUtc;
        TerminalAtUtc = terminalAtUtc;
    }

    public ReminderScheduleId Id { get; }

    public ReminderRuleId RuleId { get; }

    public OccurrenceId OccurrenceId { get; }

    public LogicalReminderId LogicalReminderId { get; }

    public ReminderScheduleId? OriginScheduleId { get; }

    public ScheduleCause Cause { get; }

    public long RuleRevision { get; }

    public long ScheduleRevision { get; }

    public Instant TriggerAtUtc { get; }

    /// <summary>相对时间计算所用的时区 provenance；绝对 UTC 计划可以为空。</summary>
    public string? TimeZoneId { get; }

    /// <summary>
    /// 在计划创建时复制的业务展示语义。Rule 更新不会改写这些快照。
    /// </summary>
    public ReminderPurpose PurposeSnapshot { get; }

    public ReminderPriority PrioritySnapshot { get; }

    public bool PinnedSnapshot { get; }

    public ScheduleState State { get; private set; }

    public ScheduleStateReason? TerminalReason { get; private set; }

    public ReminderScheduleId? ReplacementScheduleId { get; private set; }

    public Instant CreatedAtUtc { get; }

    public Instant? TerminalAtUtc { get; private set; }

    public bool IsPending => State == ScheduleState.PENDING;

    public bool IsTerminal => State != ScheduleState.PENDING;

    public static ReminderSchedule Create(
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        ScheduleCause cause,
        long ruleRevision,
        long scheduleRevision,
        Instant triggerAtUtc,
        string? timeZoneId,
        Instant createdAtUtc,
        ReminderPurpose purposeSnapshot = ReminderPurpose.TASK_CUSTOM,
        ReminderPriority prioritySnapshot = ReminderPriority.NORMAL,
        bool pinnedSnapshot = false,
        ReminderScheduleId? originScheduleId = null) =>
        Create(
            ReminderScheduleId.New(),
            ruleId,
            occurrenceId,
            logicalReminderId,
            cause,
            ruleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId,
            createdAtUtc,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            originScheduleId);

    public static ReminderSchedule Create(
        ReminderScheduleId id,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        ScheduleCause cause,
        long ruleRevision,
        long scheduleRevision,
        Instant triggerAtUtc,
        string? timeZoneId,
        Instant createdAtUtc,
        ReminderPurpose purposeSnapshot = ReminderPurpose.TASK_CUSTOM,
        ReminderPriority prioritySnapshot = ReminderPriority.NORMAL,
        bool pinnedSnapshot = false,
        ReminderScheduleId? originScheduleId = null) =>
        new(
            id,
            ruleId,
            occurrenceId,
            logicalReminderId,
            originScheduleId,
            cause,
            ruleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            ScheduleState.PENDING,
            terminalReason: null,
            replacementScheduleId: null,
            createdAtUtc,
            terminalAtUtc: null);

    public static ReminderSchedule CreateFromRule(
        ReminderRule rule,
        LogicalReminderId logicalReminderId,
        long scheduleRevision,
        Instant triggerAtUtc,
        Instant createdAtUtc,
        string? timeZoneId = null,
        ReminderScheduleId? id = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return Create(
            id ?? ReminderScheduleId.New(),
            rule.Id,
            rule.OccurrenceId,
            logicalReminderId,
            ScheduleCause.RULE,
            rule.RuleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId,
            createdAtUtc,
            rule.Purpose,
            rule.Priority,
            rule.Pinned);
    }

    /// <summary>
    /// Creates the next immutable chain row for SNOOZE or REPEAT. The origin
    /// row is never changed and the logical key/snapshots are copied verbatim.
    /// </summary>
    public static ReminderSchedule CreateDerived(
        ReminderSchedule origin,
        ScheduleCause cause,
        long scheduleRevision,
        Instant triggerAtUtc,
        Instant createdAtUtc,
        string? timeZoneId = null,
        ReminderScheduleId? id = null)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (cause is not (ScheduleCause.SNOOZE or ScheduleCause.REPEAT))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.derived_cause.invalid",
                "A derived schedule must use SNOOZE or REPEAT cause.",
                nameof(cause)));
        }

        if (scheduleRevision <= origin.ScheduleRevision)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.revision.not_monotonic",
                "A derived schedule revision must be greater than its origin.",
                nameof(scheduleRevision)));
        }

        return Create(
            id ?? ReminderScheduleId.New(),
            origin.RuleId,
            origin.OccurrenceId,
            origin.LogicalReminderId,
            cause,
            origin.RuleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId ?? origin.TimeZoneId,
            createdAtUtc,
            origin.PurposeSnapshot,
            origin.PrioritySnapshot,
            origin.PinnedSnapshot,
            origin.Id);
    }

    public static ReminderSchedule Rehydrate(
        ReminderScheduleId id,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        ReminderScheduleId? originScheduleId,
        ScheduleCause cause,
        long ruleRevision,
        long scheduleRevision,
        Instant triggerAtUtc,
        string? timeZoneId,
        ScheduleState state,
        ScheduleStateReason? terminalReason,
        ReminderScheduleId? replacementScheduleId,
        Instant createdAtUtc,
        Instant? terminalAtUtc,
        ReminderPurpose purposeSnapshot = ReminderPurpose.TASK_CUSTOM,
        ReminderPriority prioritySnapshot = ReminderPriority.NORMAL,
        bool pinnedSnapshot = false) =>
        new(
            id,
            ruleId,
            occurrenceId,
            logicalReminderId,
            originScheduleId,
            cause,
            ruleRevision,
            scheduleRevision,
            triggerAtUtc,
            timeZoneId,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            state,
            terminalReason,
            replacementScheduleId,
            createdAtUtc,
            terminalAtUtc);

    public void Consume(Instant atUtc)
    {
        EnsurePending();
        SetTerminal(ScheduleState.CONSUMED, ScheduleStateReason.DUE_CONSUMED, replacementScheduleId: null, atUtc);
    }

    public void Supersede(
        ReminderScheduleId replacementScheduleId,
        ScheduleStateReason reason,
        Instant atUtc)
    {
        EnsurePending();
        _ = ReminderScheduleId.From(replacementScheduleId.Value);

        if (replacementScheduleId == Id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.replacement.self_reference",
                "A schedule cannot replace itself.",
                nameof(replacementScheduleId)));
        }

        SetTerminal(ScheduleState.SUPERSEDED, reason, replacementScheduleId, atUtc);
    }

    public void Cancel(ScheduleStateReason reason, Instant atUtc)
    {
        EnsurePending();
        SetTerminal(ScheduleState.CANCELLED, reason, replacementScheduleId: null, atUtc);
    }

    public void Expire(ScheduleStateReason reason, Instant atUtc)
    {
        EnsurePending();
        SetTerminal(ScheduleState.EXPIRED, reason, replacementScheduleId: null, atUtc);
    }

    private void EnsurePending()
    {
        if (State != ScheduleState.PENDING)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.transition.invalid",
                "Only a pending schedule can transition to a terminal state.",
                nameof(State)));
        }
    }

    private void SetTerminal(
        ScheduleState state,
        ScheduleStateReason reason,
        ReminderScheduleId? replacementScheduleId,
        Instant atUtc)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.reason.invalid",
                "Schedule terminal reason is not supported.",
                nameof(reason)));
        }

        if (atUtc < CreatedAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.terminal_at.before_created_at",
                "TerminalAt cannot be earlier than CreatedAt.",
                nameof(atUtc)));
        }

        State = state;
        TerminalReason = reason;
        ReplacementScheduleId = replacementScheduleId;
        TerminalAtUtc = atUtc;
    }

    private static void Validate(
        ReminderScheduleId id,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        ReminderScheduleId? originScheduleId,
        ScheduleCause cause,
        long ruleRevision,
        long scheduleRevision,
        Instant triggerAtUtc,
        string? timeZoneId,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        ScheduleState state,
        ScheduleStateReason? terminalReason,
        ReminderScheduleId? replacementScheduleId,
        Instant createdAtUtc,
        Instant? terminalAtUtc)
    {
        _ = ReminderScheduleId.From(id.Value);
        _ = ReminderRuleId.From(ruleId.Value);
        _ = OccurrenceId.From(occurrenceId.Value);
        _ = LogicalReminderId.From(logicalReminderId.Value);

        if (originScheduleId == id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.origin.self_reference",
                "A schedule cannot originate from itself.",
                nameof(originScheduleId)));
        }

        if (replacementScheduleId == id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.replacement.self_reference",
                "A schedule cannot replace itself.",
                nameof(replacementScheduleId)));
        }

        if (originScheduleId is not null)
        {
            _ = ReminderScheduleId.From(originScheduleId.Value.Value);
        }

        if (replacementScheduleId is not null)
        {
            _ = ReminderScheduleId.From(replacementScheduleId.Value.Value);
        }

        if (!Enum.IsDefined(cause))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.cause.invalid",
                "Schedule cause is not supported.",
                nameof(cause)));
        }

        if ((cause == ScheduleCause.RULE) != (originScheduleId is null))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.origin.required",
                "RULE schedules have no origin; SNOOZE/REPEAT schedules require one.",
                nameof(originScheduleId)));
        }

        if (ruleRevision < 1)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.rule_revision.invalid",
                "Schedule rule revision must be positive.",
                nameof(ruleRevision)));
        }

        if (scheduleRevision < 1)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.revision.invalid",
                "Schedule revision must be positive.",
                nameof(scheduleRevision)));
        }

        ValidatePurposeSnapshot(purposeSnapshot);

        if (!Enum.IsDefined(prioritySnapshot))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.priority.invalid",
                "Reminder priority is not supported.",
                nameof(prioritySnapshot)));
        }

        ValidateTimeZoneId(timeZoneId);
        ValidateState(state, terminalReason, replacementScheduleId, createdAtUtc, terminalAtUtc);
    }

    private static void ValidatePurposeSnapshot(ReminderPurpose purpose)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.invalid",
                "Reminder purpose is not supported.",
                nameof(purpose)));
        }

        if (purpose is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.unsupported",
                "Anime reminder purposes are reserved and not executable in P3.",
                nameof(purpose)));
        }
    }

    private static void ValidateState(
        ScheduleState state,
        ScheduleStateReason? terminalReason,
        ReminderScheduleId? replacementScheduleId,
        Instant createdAtUtc,
        Instant? terminalAtUtc)
    {
        if (!Enum.IsDefined(state))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.state.invalid",
                "Schedule state is not supported.",
                nameof(state)));
        }

        if (terminalReason is not null && !Enum.IsDefined(terminalReason.Value))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.reason.invalid",
                "Schedule terminal reason is not supported.",
                nameof(terminalReason)));
        }

        var pending = state == ScheduleState.PENDING;
        var requiresTerminal = state is ScheduleState.CONSUMED or ScheduleState.SUPERSEDED or ScheduleState.CANCELLED or ScheduleState.EXPIRED;

        if (pending && (terminalReason is not null || replacementScheduleId is not null || terminalAtUtc is not null))
        {
            throw InvalidState("Pending schedules cannot carry terminal facts.");
        }

        if (requiresTerminal && (terminalReason is null || terminalAtUtc is null))
        {
            throw InvalidState("Terminal schedules must carry a reason and terminal timestamp.");
        }

        if (state == ScheduleState.CONSUMED && (terminalReason != ScheduleStateReason.DUE_CONSUMED || replacementScheduleId is not null))
        {
            throw InvalidState("Consumed schedules require DUE_CONSUMED and no replacement.");
        }

        if (state == ScheduleState.SUPERSEDED && replacementScheduleId is null)
        {
            throw InvalidState("Superseded schedules require a replacement schedule.");
        }

        if (state is ScheduleState.CANCELLED or ScheduleState.EXPIRED && replacementScheduleId is not null)
        {
            throw InvalidState("Cancelled or expired schedules cannot carry a replacement.");
        }

        if (terminalAtUtc is not null && terminalAtUtc.Value < createdAtUtc)
        {
            throw InvalidState("TerminalAt cannot be earlier than CreatedAt.");
        }
    }

    private static DomainValidationException InvalidState(string message) =>
        new(new DomainValidationError(
            "reminder.schedule.state.inconsistent",
            message,
            nameof(ScheduleState)));

    private static void ValidateTimeZoneId(string? timeZoneId)
    {
        if (timeZoneId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Any(char.IsControl))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.time_zone.invalid",
                "Time zone ID must be non-empty and contain no control characters.",
                nameof(timeZoneId)));
        }

        if (Encoding.UTF8.GetByteCount(timeZoneId.Trim()) > MaxTimeZoneIdBytes)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.schedule.time_zone.too_long",
                "Time zone ID exceeds the persistence limit.",
                nameof(timeZoneId)));
        }
    }

    private static string? NormalizeTimeZoneId(string? timeZoneId) =>
        timeZoneId is null ? null : timeZoneId.Trim();
}
