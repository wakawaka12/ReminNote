using NodaTime;
using ReminNote.Core.Reminders.Domain;
using OccurrenceIdentity = ReminNote.Core.Reminders.Domain.OccurrenceId;
using LogicalReminderIdentity = ReminNote.Core.Reminders.Domain.LogicalReminderId;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderScheduleEntity
{
    public Guid Id { get; set; }

    public Guid RuleId { get; set; }

    public Guid OccurrenceId { get; set; }

    public Guid LogicalReminderId { get; set; }

    public Guid? OriginScheduleId { get; set; }

    public ScheduleCause Cause { get; set; }

    public long RuleRevision { get; set; }

    public long ScheduleRevision { get; set; }

    public Instant TriggerAtUtc { get; set; }

    public string? TimeZoneId { get; set; }

    public ReminderPurpose PurposeSnapshot { get; set; }

    public ReminderPriority PrioritySnapshot { get; set; }

    public bool PinnedSnapshot { get; set; }

    public ScheduleState State { get; set; }

    public ScheduleStateReason? TerminalReason { get; set; }

    public Guid? ReplacementScheduleId { get; set; }

    public Instant CreatedAtUtc { get; set; }

    public Instant? TerminalAtUtc { get; set; }

    public static ReminderScheduleEntity FromDomain(ReminderSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new ReminderScheduleEntity
        {
            Id = schedule.Id.Value,
            RuleId = schedule.RuleId.Value,
            OccurrenceId = schedule.OccurrenceId.Value,
            LogicalReminderId = schedule.LogicalReminderId.Value,
            OriginScheduleId = schedule.OriginScheduleId?.Value,
            Cause = schedule.Cause,
            RuleRevision = schedule.RuleRevision,
            ScheduleRevision = schedule.ScheduleRevision,
            TriggerAtUtc = schedule.TriggerAtUtc,
            TimeZoneId = schedule.TimeZoneId,
            PurposeSnapshot = schedule.PurposeSnapshot,
            PrioritySnapshot = schedule.PrioritySnapshot,
            PinnedSnapshot = schedule.PinnedSnapshot,
            State = schedule.State,
            TerminalReason = schedule.TerminalReason,
            ReplacementScheduleId = schedule.ReplacementScheduleId?.Value,
            CreatedAtUtc = schedule.CreatedAtUtc,
            TerminalAtUtc = schedule.TerminalAtUtc
        };
    }

    public void Apply(ReminderSchedule schedule)
    {
        var updated = FromDomain(schedule);

        Id = updated.Id;
        RuleId = updated.RuleId;
        OccurrenceId = updated.OccurrenceId;
        LogicalReminderId = updated.LogicalReminderId;
        OriginScheduleId = updated.OriginScheduleId;
        Cause = updated.Cause;
        RuleRevision = updated.RuleRevision;
        ScheduleRevision = updated.ScheduleRevision;
        TriggerAtUtc = updated.TriggerAtUtc;
        TimeZoneId = updated.TimeZoneId;
        PurposeSnapshot = updated.PurposeSnapshot;
        PrioritySnapshot = updated.PrioritySnapshot;
        PinnedSnapshot = updated.PinnedSnapshot;
        State = updated.State;
        TerminalReason = updated.TerminalReason;
        ReplacementScheduleId = updated.ReplacementScheduleId;
        CreatedAtUtc = updated.CreatedAtUtc;
        TerminalAtUtc = updated.TerminalAtUtc;
    }

    public ReminderSchedule ToDomain() => ReminderSchedule.Rehydrate(
        ReminderScheduleId.From(Id),
        ReminderRuleId.From(RuleId),
        OccurrenceIdentity.From(OccurrenceId),
        LogicalReminderIdentity.From(LogicalReminderId),
        OriginScheduleId is { } origin ? ReminderScheduleId.From(origin) : null,
        Cause,
        RuleRevision,
        ScheduleRevision,
        TriggerAtUtc,
        TimeZoneId,
        State,
        TerminalReason,
        ReplacementScheduleId is { } replacement ? ReminderScheduleId.From(replacement) : null,
        CreatedAtUtc,
        TerminalAtUtc,
        PurposeSnapshot,
        PrioritySnapshot,
        PinnedSnapshot);
}
