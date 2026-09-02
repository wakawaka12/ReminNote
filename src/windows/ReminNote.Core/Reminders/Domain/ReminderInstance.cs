using NodaTime;
using ReminNote.Core;
using ResolutionActionKind = ReminNote.Core.Reminders.Domain.ResolutionAction;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// 已发生的一次核心 reminder 事实。事实字段在创建后只读，生命周期仅沿
/// UNREAD → READ → RESOLVED 单向推进。
/// </summary>
public sealed class ReminderInstance
{
    private ReminderInstance(
        ReminderInstanceId id,
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        int attemptOrdinal,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant triggeredAtUtc,
        ReminderLifecycle lifecycle,
        Instant? readAtUtc,
        Instant? resolvedAtUtc,
        ResolutionAction? resolutionAction)
    {
        Validate(
            id,
            scheduleId,
            ruleId,
            occurrenceId,
            logicalReminderId,
            attemptOrdinal,
            purposeSnapshot,
            prioritySnapshot,
            triggeredAtUtc,
            lifecycle,
            readAtUtc,
            resolvedAtUtc,
            resolutionAction);

        Id = id;
        ScheduleId = scheduleId;
        RuleId = ruleId;
        OccurrenceId = occurrenceId;
        LogicalReminderId = logicalReminderId;
        AttemptOrdinal = attemptOrdinal;
        PurposeSnapshot = purposeSnapshot;
        PrioritySnapshot = prioritySnapshot;
        PinnedSnapshot = pinnedSnapshot;
        TriggeredAtUtc = triggeredAtUtc;
        Lifecycle = lifecycle;
        ReadAtUtc = readAtUtc;
        ResolvedAtUtc = resolvedAtUtc;
        ResolutionAction = resolutionAction;
    }

    public ReminderInstanceId Id { get; }

    public ReminderScheduleId ScheduleId { get; }

    public ReminderRuleId RuleId { get; }

    public OccurrenceId OccurrenceId { get; }

    public LogicalReminderId LogicalReminderId { get; }

    public int AttemptOrdinal { get; }

    public ReminderPurpose PurposeSnapshot { get; }

    public ReminderPriority PrioritySnapshot { get; }

    public bool PinnedSnapshot { get; }

    public Instant TriggeredAtUtc { get; }

    public ReminderLifecycle Lifecycle { get; private set; }

    public Instant? ReadAtUtc { get; private set; }

    public Instant? ResolvedAtUtc { get; private set; }

    public ResolutionAction? ResolutionAction { get; private set; }

    public bool IsUnread => Lifecycle == ReminderLifecycle.UNREAD;

    public bool IsRead => Lifecycle == ReminderLifecycle.READ;

    public bool IsResolved => Lifecycle == ReminderLifecycle.RESOLVED;

    public static ReminderInstance Create(
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        int attemptOrdinal,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant triggeredAtUtc) =>
        Create(
            ReminderInstanceId.New(),
            scheduleId,
            ruleId,
            occurrenceId,
            logicalReminderId,
            attemptOrdinal,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            triggeredAtUtc);

    public static ReminderInstance Create(
        ReminderInstanceId id,
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        int attemptOrdinal,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant triggeredAtUtc) =>
        new(
            id,
            scheduleId,
            ruleId,
            occurrenceId,
            logicalReminderId,
            attemptOrdinal,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            triggeredAtUtc,
            ReminderLifecycle.UNREAD,
            readAtUtc: null,
            resolvedAtUtc: null,
            resolutionAction: null);

    public static ReminderInstance CreateFromSchedule(
        ReminderSchedule schedule,
        int attemptOrdinal,
        Instant triggeredAtUtc,
        ReminderInstanceId? id = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return Create(
            id ?? ReminderInstanceId.New(),
            schedule.Id,
            schedule.RuleId,
            schedule.OccurrenceId,
            schedule.LogicalReminderId,
            attemptOrdinal,
            schedule.PurposeSnapshot,
            schedule.PrioritySnapshot,
            schedule.PinnedSnapshot,
            triggeredAtUtc);
    }

    public static ReminderInstance Rehydrate(
        ReminderInstanceId id,
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        int attemptOrdinal,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        bool pinnedSnapshot,
        Instant triggeredAtUtc,
        ReminderLifecycle lifecycle,
        Instant? readAtUtc,
        Instant? resolvedAtUtc,
        ResolutionAction? resolutionAction) =>
        new(
            id,
            scheduleId,
            ruleId,
            occurrenceId,
            logicalReminderId,
            attemptOrdinal,
            purposeSnapshot,
            prioritySnapshot,
            pinnedSnapshot,
            triggeredAtUtc,
            lifecycle,
            readAtUtc,
            resolvedAtUtc,
            resolutionAction);

    /// <summary>记录 Toast close/read 事实；重复调用是幂等 no-op。</summary>
    public bool MarkRead(Instant readAtUtc)
    {
        if (Lifecycle != ReminderLifecycle.UNREAD)
        {
            return false;
        }

        ValidateReadAt(readAtUtc);
        Lifecycle = ReminderLifecycle.READ;
        ReadAtUtc = readAtUtc;
        return true;
    }

    /// <summary>
    /// 记录用户动作。UNREAD 直接 resolve 时同时保留 readAtUtc，且动作首次写入后
    /// 不允许换成另一种动作；相同动作重试幂等。
    /// </summary>
    public bool Resolve(ResolutionAction action, Instant resolvedAtUtc)
    {
        ValidateResolutionAction(action, PurposeSnapshot);

        if (Lifecycle == ReminderLifecycle.RESOLVED)
        {
            if (ResolutionAction == action)
            {
                return false;
            }

            throw ConflictingResolution(action);
        }

        if (Lifecycle == ReminderLifecycle.UNREAD)
        {
            ValidateReadAt(resolvedAtUtc);
            ReadAtUtc = resolvedAtUtc;
        }
        else
        {
            // READ implies a persisted read fact. Rehydration validation makes
            // the null case impossible, but keep this guard explicit at the
            // mutation boundary for defensive use by future serializers.
            if (ReadAtUtc is null)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.instance.lifecycle.inconsistent",
                    "A READ instance must have a read timestamp.",
                    nameof(ReadAtUtc)));
            }

            if (resolvedAtUtc < ReadAtUtc.Value)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.instance.resolved_at.before_read_at",
                    "ResolvedAt cannot be earlier than ReadAt.",
                    nameof(resolvedAtUtc)));
            }
        }

        Lifecycle = ReminderLifecycle.RESOLVED;
        ResolvedAtUtc = resolvedAtUtc;
        ResolutionAction = action;
        return true;
    }

    private static DomainValidationException ConflictingResolution(ResolutionAction action) =>
        new(new DomainValidationError(
            "reminder.instance.resolution.conflict",
            $"The instance has already been resolved with another action; cannot apply {action}.",
            nameof(action)));

    private static void Validate(
        ReminderInstanceId id,
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        LogicalReminderId logicalReminderId,
        int attemptOrdinal,
        ReminderPurpose purposeSnapshot,
        ReminderPriority prioritySnapshot,
        Instant triggeredAtUtc,
        ReminderLifecycle lifecycle,
        Instant? readAtUtc,
        Instant? resolvedAtUtc,
        ResolutionAction? resolutionAction)
    {
        _ = ReminderInstanceId.From(id.Value);
        _ = ReminderScheduleId.From(scheduleId.Value);
        _ = ReminderRuleId.From(ruleId.Value);
        _ = OccurrenceId.From(occurrenceId.Value);
        _ = LogicalReminderId.From(logicalReminderId.Value);

        if (attemptOrdinal < 1)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.attempt_ordinal.invalid",
                "Instance attempt ordinal must start at one.",
                nameof(attemptOrdinal)));
        }

        if (!Enum.IsDefined(purposeSnapshot) || purposeSnapshot is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            throw new DomainValidationException(new DomainValidationError(
                purposeSnapshot is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM
                    ? "reminder.purpose.unsupported"
                    : "reminder.purpose.invalid",
                "Instance purpose snapshot is not executable in P3.",
                nameof(purposeSnapshot)));
        }

        if (!Enum.IsDefined(prioritySnapshot))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.priority.invalid",
                "Reminder priority is not supported.",
                nameof(prioritySnapshot)));
        }

        if (!Enum.IsDefined(lifecycle))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.lifecycle.invalid",
                "Reminder lifecycle is not supported.",
                nameof(lifecycle)));
        }

        if (resolutionAction is not null)
        {
            ValidateResolutionAction(resolutionAction.Value, purposeSnapshot);
        }

        switch (lifecycle)
        {
            case ReminderLifecycle.UNREAD when readAtUtc is not null || resolvedAtUtc is not null || resolutionAction is not null:
                throw InvalidLifecycle("UNREAD instances cannot carry read, resolved, or action facts.");
            case ReminderLifecycle.READ when readAtUtc is null || resolvedAtUtc is not null || resolutionAction is not null:
                throw InvalidLifecycle("READ instances require only a read timestamp.");
            case ReminderLifecycle.RESOLVED when readAtUtc is null || resolvedAtUtc is null || resolutionAction is null:
                throw InvalidLifecycle("RESOLVED instances require read, resolved, and action facts.");
        }

        if (readAtUtc is not null && readAtUtc.Value < triggeredAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.read_at.before_triggered_at",
                "ReadAt cannot be earlier than TriggeredAt.",
                nameof(readAtUtc)));
        }

        if (resolvedAtUtc is not null && readAtUtc is not null && resolvedAtUtc.Value < readAtUtc.Value)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.resolved_at.before_read_at",
                "ResolvedAt cannot be earlier than ReadAt.",
                nameof(resolvedAtUtc)));
        }
    }

    private static void ValidateResolutionAction(ResolutionAction action, ReminderPurpose purpose)
    {
        if (!Enum.IsDefined(action))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.resolution.invalid",
                "Resolution action is not supported.",
                nameof(action)));
        }

        if (purpose is ReminderPurpose.TASK_PRE_START or ReminderPurpose.TASK_START or ReminderPurpose.TASK_RANGE_END or ReminderPurpose.TASK_CUSTOM)
        {
            if (action is ResolutionActionKind.WATCHED or ResolutionActionKind.WATCH_LATER)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.instance.resolution.unsupported",
                    "WATCHED and WATCH_LATER are not enabled for TASK_INSTANCE in P3.",
                    nameof(action)));
            }
        }
    }

    private void ValidateReadAt(Instant readAtUtc)
    {
        if (readAtUtc < TriggeredAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.instance.read_at.before_triggered_at",
                "ReadAt cannot be earlier than TriggeredAt.",
                nameof(readAtUtc)));
        }
    }

    private static DomainValidationException InvalidLifecycle(string message) =>
        new(new DomainValidationError(
            "reminder.instance.lifecycle.inconsistent",
            message,
            nameof(Lifecycle)));
}
