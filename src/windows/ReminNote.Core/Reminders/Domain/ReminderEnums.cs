namespace ReminNote.Core.Reminders.Domain;

// These names are the frozen uppercase wire/storage tokens from P3-00. The
// underscore style is intentional and is not a .NET identifier typo.
#pragma warning disable CA1707

/// <summary>
/// The target namespace used by the first P3 reminder contract. Additional
/// target kinds must be introduced by a later contract rather than inferred
/// from the target id.
/// </summary>
public enum ReminderTargetKind
{
    TASK_INSTANCE
}

/// <summary>
/// Semantic purpose is part of recovery/expiry policy, not presentation text.
/// Anime purposes are retained as reserved values but are not executable in
/// the P3 Task target path.
/// </summary>
public enum ReminderPurpose
{
    TASK_PRE_START,
    TASK_START,
    TASK_RANGE_END,
    TASK_CUSTOM,
    ANIME_PRE_AIRING,
    ANIME_AIRING,
    ANIME_CUSTOM
}

public enum ReminderPriority
{
    LOW,
    NORMAL,
    HIGH
}

public enum WakePolicy
{
    DEFAULT,
    YES,
    NO
}

public enum ReminderTimingKind
{
    RELATIVE,
    ABSOLUTE_UTC
}

public enum ReminderAnchor
{
    TASK_TIME,
    RANGE_START,
    RANGE_END
}

public enum ScheduleCause
{
    RULE,
    SNOOZE,
    REPEAT
}

public enum ScheduleState
{
    PENDING,
    CONSUMED,
    SUPERSEDED,
    CANCELLED,
    EXPIRED
}

/// <summary>
/// Stable terminal reasons. The values deliberately describe facts rather
/// than localized UI wording.
/// </summary>
public enum ScheduleStateReason
{
    DUE_CONSUMED,
    RULE_REBUILT,
    TASK_PLAN_CHANGED,
    TIME_ZONE_CHANGED,
    TASK_RESULT_RECORDED,
    RULE_DISABLED,
    TASK_DELETED,
    RECOVERY_OBSOLETE,
    MANUAL_CANCELLED,
    REPLACED
}

public enum ReminderLifecycle
{
    UNREAD,
    READ,
    RESOLVED
}

public enum ResolutionAction
{
    DONE,
    SNOOZE,
    WATCHED,
    WATCH_LATER,
    SKIP,
    IGNORE
}

public enum ReminderDeliveryChannel
{
    TOAST,
    TRAY,
    WIDGET,
    SOUND,
    WAKE_TIMER
}

public enum ReminderDeliveryOutcome
{
    DELIVERED,
    BLOCKED,
    UNAVAILABLE,
    FAILED,
    SUPPRESSED_QUIET_HOURS,
    NOT_ATTEMPTED
}

#pragma warning restore CA1707
