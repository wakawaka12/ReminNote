namespace ReminNote.Core.Reminders.Policy;

/// <summary>
/// Stable, non-localized codes returned by the P3-07 pure policy seams.
/// Application and UI layers may translate these codes, but must not use
/// localized text as a persistence or retry discriminator.
/// </summary>
public static class ReminderPolicyCodes
{
    public const string CoreNotTriggered = "reminder.core.not_triggered";
    public const string ChannelBlocked = "reminder.channel.blocked";
    public const string ChannelUnavailable = "reminder.channel.unavailable";
    public const string Delivered = "reminder.channel.deliver";
    public const string QuietHoursSuppressed = "reminder.quiet_hours.suppressed";
    public const string QuietHoursSummary = "reminder.quiet_hours.summary";
    public const string QuietHoursEscalated = "reminder.quiet_hours.escalated";

    public const string RepeatDisabled = "reminder.repeat.disabled";
    public const string RepeatAllowed = "reminder.repeat.allowed";
    public const string RepeatLimitReached = "reminder.repeat.limit_reached";
    public const string RepeatIntervalInvalid = "reminder.repeat.interval.invalid";
    public const string RepeatIntervalOutOfRange = "reminder.repeat.interval.out_of_range";
    public const string RepeatMaxCountOutOfRange = "reminder.repeat.max_count.out_of_range";
    public const string RepeatOrdinalInvalid = "reminder.repeat.ordinal.invalid";

    public const string WakeNotRequested = "reminder.wake.not_requested";
    public const string WakeDeniedByProfile = "reminder.wake.denied_by_profile";
    public const string WakeDeniedSafeMode = "reminder.wake.denied_safe_mode";
    public const string WakeUnavailable = "reminder.wake.unavailable";
    public const string WakeRequested = "reminder.wake.requested";

    public const string RecoveryNotDue = "reminder.recovery.not_due";
    public const string RecoveryOccurrenceCancelled = "reminder.recovery.occurrence_cancelled";
    public const string RecoveryTaskResultRecorded = "reminder.recovery.task_result_recorded";
    public const string RecoveryDuplicate = "reminder.recovery.duplicate";
    public const string RecoveryPreStartObsolete = "reminder.recovery.pre_start_obsolete";
    public const string RecoveryStartSummary = "reminder.recovery.start_summary";
    public const string RecoveryStartTriggered = "reminder.recovery.start_triggered";
    public const string RecoveryRangeEndTriggered = "reminder.recovery.range_end_triggered";
    public const string RecoveryCustomTriggered = "reminder.recovery.custom_triggered";
    public const string RecoveryCustomExpired = "reminder.recovery.custom_expired";
    public const string RecoveryCustomNotMeaningful = "reminder.recovery.custom_not_meaningful";
    public const string RecoveryAnimeNotActivated = "reminder.recovery.anime_not_activated";
    public const string RecoveryPreStartRequiresTaskStart = "reminder.recovery.pre_start_task_start_required";
}
