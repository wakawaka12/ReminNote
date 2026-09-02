using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Core.Reminders.Policy;

#pragma warning disable CA1707 // Frozen uppercase values are wire/storage tokens.

public enum ReminderRecoveryAction
{
    DEFER,
    TRIGGER_ONCE,
    SUMMARY,
    EXPIRE,
    SKIP,
    REJECT
}

#pragma warning restore CA1707

/// <summary>Inputs needed to classify one overdue pending schedule.</summary>
public sealed record ReminderRecoveryInput
{
    public ReminderRecoveryInput(
        ReminderPurpose purpose,
        Instant triggerAtUtc,
        Instant recoveredAtUtc,
        Instant? taskStartAtUtc,
        bool occurrenceCancelled,
        bool taskResultRecorded,
        bool coreInstanceExists,
        bool customReminderMeaningful,
        ReminderPriority priority,
        bool pinned)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.invalid",
                "Reminder purpose is not supported.",
                nameof(purpose)));
        }

        if (!Enum.IsDefined(priority))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.priority.invalid",
                "Reminder priority is not supported.",
                nameof(priority)));
        }

        Purpose = purpose;
        TriggerAtUtc = triggerAtUtc;
        RecoveredAtUtc = recoveredAtUtc;
        TaskStartAtUtc = taskStartAtUtc;
        OccurrenceCancelled = occurrenceCancelled;
        TaskResultRecorded = taskResultRecorded;
        CoreInstanceExists = coreInstanceExists;
        CustomReminderMeaningful = customReminderMeaningful;
        Priority = priority;
        Pinned = pinned;
    }

    public ReminderPurpose Purpose { get; }

    public Instant TriggerAtUtc { get; }

    public Instant RecoveredAtUtc { get; }

    public Instant? TaskStartAtUtc { get; }

    public bool OccurrenceCancelled { get; }

    public bool TaskResultRecorded { get; }

    public bool CoreInstanceExists { get; }

    public bool CustomReminderMeaningful { get; }

    public ReminderPriority Priority { get; }

    public bool Pinned { get; }
}

/// <summary>
/// Recovery result. For TRIGGER_ONCE and SUMMARY the core fact is recorded at
/// RecoveredAtUtc; the original schedule trigger is never rewritten.
/// </summary>
public sealed record ReminderRecoveryDecision(
    ReminderRecoveryAction Action,
    string ReasonCode,
    ScheduleState? SuggestedScheduleState,
    bool RecordCoreInstance,
    Instant? CoreFactAtUtc)
{
    public bool UsesOriginalTriggerAtUtc { get; }
}

/// <summary>Deterministic policy for restart/resume overdue handling.</summary>
public sealed class ReminderRecoveryPolicy
{
    public ReminderRecoveryPolicy(
        Duration customLateWindow,
        bool summarizeNormalTaskStart = true)
    {
        if (customLateWindow <= Duration.Zero)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.recovery.custom_window.invalid",
                "The custom reminder late window must be positive.",
                nameof(customLateWindow)));
        }

        CustomLateWindow = customLateWindow;
        SummarizeNormalTaskStart = summarizeNormalTaskStart;
    }

    /// <summary>Product default recorded by P3-07: 24 hours.</summary>
    public static ReminderRecoveryPolicy Default { get; } = new(Duration.FromHours(24));

    public Duration CustomLateWindow { get; }

    public bool SummarizeNormalTaskStart { get; }

    public ReminderRecoveryDecision Evaluate(ReminderRecoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.OccurrenceCancelled)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.SKIP,
                ReminderPolicyCodes.RecoveryOccurrenceCancelled,
                ScheduleState.CANCELLED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        if (input.TaskResultRecorded)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.SKIP,
                ReminderPolicyCodes.RecoveryTaskResultRecorded,
                ScheduleState.CANCELLED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        if (input.CoreInstanceExists)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.SKIP,
                ReminderPolicyCodes.RecoveryDuplicate,
                ScheduleState.CONSUMED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        if (input.RecoveredAtUtc < input.TriggerAtUtc)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.DEFER,
                ReminderPolicyCodes.RecoveryNotDue,
                ScheduleState.PENDING,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        if (input.Purpose is ReminderPurpose.ANIME_PRE_AIRING or
            ReminderPurpose.ANIME_AIRING or
            ReminderPurpose.ANIME_CUSTOM)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.REJECT,
                ReminderPolicyCodes.RecoveryAnimeNotActivated,
                SuggestedScheduleState: null,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        return input.Purpose switch
        {
            ReminderPurpose.TASK_PRE_START => EvaluatePreStart(input),
            ReminderPurpose.TASK_START => EvaluateTaskStart(input),
            ReminderPurpose.TASK_RANGE_END => Trigger(
                ReminderPolicyCodes.RecoveryRangeEndTriggered,
                ScheduleState.CONSUMED,
                input.RecoveredAtUtc),
            ReminderPurpose.TASK_CUSTOM => EvaluateCustom(input),
            _ => new ReminderRecoveryDecision(
                ReminderRecoveryAction.REJECT,
                ReminderPolicyCodes.RecoveryAnimeNotActivated,
                SuggestedScheduleState: null,
                RecordCoreInstance: false,
                CoreFactAtUtc: null)
        };
    }

    private static ReminderRecoveryDecision EvaluatePreStart(ReminderRecoveryInput input)
    {
        if (input.TaskStartAtUtc is null)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.REJECT,
                ReminderPolicyCodes.RecoveryPreStartRequiresTaskStart,
                SuggestedScheduleState: null,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        return input.RecoveredAtUtc >= input.TaskStartAtUtc.Value
            ? new ReminderRecoveryDecision(
                ReminderRecoveryAction.EXPIRE,
                ReminderPolicyCodes.RecoveryPreStartObsolete,
                ScheduleState.EXPIRED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null)
            : Trigger(
                ReminderPolicyCodes.RecoveryStartTriggered,
                ScheduleState.CONSUMED,
                input.RecoveredAtUtc);
    }

    private ReminderRecoveryDecision EvaluateTaskStart(ReminderRecoveryInput input) =>
        SummarizeNormalTaskStart &&
        input.Priority != ReminderPriority.HIGH &&
        !input.Pinned
            ? new ReminderRecoveryDecision(
                ReminderRecoveryAction.SUMMARY,
                ReminderPolicyCodes.RecoveryStartSummary,
                ScheduleState.CONSUMED,
                RecordCoreInstance: true,
                CoreFactAtUtc: input.RecoveredAtUtc)
            : Trigger(
                ReminderPolicyCodes.RecoveryStartTriggered,
                ScheduleState.CONSUMED,
                input.RecoveredAtUtc);

    private ReminderRecoveryDecision EvaluateCustom(ReminderRecoveryInput input)
    {
        if (!input.CustomReminderMeaningful)
        {
            return new ReminderRecoveryDecision(
                ReminderRecoveryAction.SKIP,
                ReminderPolicyCodes.RecoveryCustomNotMeaningful,
                ScheduleState.CANCELLED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
        }

        var age = input.RecoveredAtUtc - input.TriggerAtUtc;
        return age <= CustomLateWindow
            ? Trigger(
                ReminderPolicyCodes.RecoveryCustomTriggered,
                ScheduleState.CONSUMED,
                input.RecoveredAtUtc)
            : new ReminderRecoveryDecision(
                ReminderRecoveryAction.EXPIRE,
                ReminderPolicyCodes.RecoveryCustomExpired,
                ScheduleState.EXPIRED,
                RecordCoreInstance: false,
                CoreFactAtUtc: null);
    }

    private static ReminderRecoveryDecision Trigger(
        string reasonCode,
        ScheduleState state,
        Instant factAtUtc) =>
        new(
            ReminderRecoveryAction.TRIGGER_ONCE,
            reasonCode,
            state,
            RecordCoreInstance: true,
            CoreFactAtUtc: factAtUtc);
}
