using NodaTime;
using ReminNote.Core.Reminders.Domain;
using OccurrenceIdentity = ReminNote.Core.Reminders.Domain.OccurrenceId;
using LogicalReminderIdentity = ReminNote.Core.Reminders.Domain.LogicalReminderId;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderInstanceEntity
{
    public Guid Id { get; set; }

    public Guid ScheduleId { get; set; }

    public Guid RuleId { get; set; }

    public Guid OccurrenceId { get; set; }

    public Guid LogicalReminderId { get; set; }

    public int AttemptOrdinal { get; set; }

    public ReminderPurpose PurposeSnapshot { get; set; }

    public ReminderPriority PrioritySnapshot { get; set; }

    public bool PinnedSnapshot { get; set; }

    public Instant TriggeredAtUtc { get; set; }

    public ReminderLifecycle Lifecycle { get; set; }

    public Instant? ReadAtUtc { get; set; }

    public Instant? ResolvedAtUtc { get; set; }

    public ResolutionAction? ResolutionAction { get; set; }

    public static ReminderInstanceEntity FromDomain(ReminderInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new ReminderInstanceEntity
        {
            Id = instance.Id.Value,
            ScheduleId = instance.ScheduleId.Value,
            RuleId = instance.RuleId.Value,
            OccurrenceId = instance.OccurrenceId.Value,
            LogicalReminderId = instance.LogicalReminderId.Value,
            AttemptOrdinal = instance.AttemptOrdinal,
            PurposeSnapshot = instance.PurposeSnapshot,
            PrioritySnapshot = instance.PrioritySnapshot,
            PinnedSnapshot = instance.PinnedSnapshot,
            TriggeredAtUtc = instance.TriggeredAtUtc,
            Lifecycle = instance.Lifecycle,
            ReadAtUtc = instance.ReadAtUtc,
            ResolvedAtUtc = instance.ResolvedAtUtc,
            ResolutionAction = instance.ResolutionAction
        };
    }

    public void Apply(ReminderInstance instance)
    {
        var updated = FromDomain(instance);

        Id = updated.Id;
        ScheduleId = updated.ScheduleId;
        RuleId = updated.RuleId;
        OccurrenceId = updated.OccurrenceId;
        LogicalReminderId = updated.LogicalReminderId;
        AttemptOrdinal = updated.AttemptOrdinal;
        PurposeSnapshot = updated.PurposeSnapshot;
        PrioritySnapshot = updated.PrioritySnapshot;
        PinnedSnapshot = updated.PinnedSnapshot;
        TriggeredAtUtc = updated.TriggeredAtUtc;
        Lifecycle = updated.Lifecycle;
        ReadAtUtc = updated.ReadAtUtc;
        ResolvedAtUtc = updated.ResolvedAtUtc;
        ResolutionAction = updated.ResolutionAction;
    }

    public ReminderInstance ToDomain() => ReminderInstance.Rehydrate(
        ReminderInstanceId.From(Id),
        ReminderScheduleId.From(ScheduleId),
        ReminderRuleId.From(RuleId),
        OccurrenceIdentity.From(OccurrenceId),
        LogicalReminderIdentity.From(LogicalReminderId),
        AttemptOrdinal,
        PurposeSnapshot,
        PrioritySnapshot,
        PinnedSnapshot,
        TriggeredAtUtc,
        Lifecycle,
        ReadAtUtc,
        ResolvedAtUtc,
        ResolutionAction);
}
