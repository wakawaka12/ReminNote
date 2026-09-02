using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Tests;

public sealed class ReminderDomainTests
{
    private static readonly Instant CreatedAt = Instant.FromUtc(2026, 9, 2, 8, 0);
    private static readonly Instant ChangedAt = Instant.FromUtc(2026, 9, 2, 8, 5);
    private static readonly Instant TriggerAt = Instant.FromUtc(2026, 9, 2, 8, 30);

    [Fact]
    public void RuleCreationAndNoOpUpdateKeepRevisionStable()
    {
        var taskId = Guid.CreateVersion7();
        var rule = ReminderRule.CreateForTask(
            taskId,
            ReminderPurpose.TASK_PRE_START,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -900),
            ReminderPriority.HIGH,
            true,
            RepeatPolicy.Disabled,
            WakePolicy.DEFAULT,
            true,
            CreatedAt);

        Assert.Equal(1, rule.RuleRevision);
        Assert.Equal(taskId, rule.TargetId);
        Assert.Equal(OccurrenceId.From(taskId), rule.OccurrenceId);

        Assert.False(rule.Update(
            rule.Purpose,
            rule.Timing,
            rule.Priority,
            rule.Pinned,
            rule.RepeatPolicy,
            rule.WakePolicy,
            rule.Enabled,
            ChangedAt));
        Assert.Equal(1, rule.RuleRevision);
        Assert.Equal(CreatedAt, rule.UpdatedAtUtc);

        Assert.True(rule.Update(
            rule.Purpose,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -1_800),
            rule.Priority,
            rule.Pinned,
            rule.RepeatPolicy,
            rule.WakePolicy,
            rule.Enabled,
            ChangedAt));
        Assert.Equal(2, rule.RuleRevision);
        Assert.Equal(ChangedAt, rule.UpdatedAtUtc);
    }

    [Fact]
    public void RuleRejectsReservedPurposeAndMismatchedTiming()
    {
        var taskId = Guid.CreateVersion7();

        Assert.Throws<DomainValidationException>(() => ReminderRule.CreateForTask(
            taskId,
            ReminderPurpose.ANIME_CUSTOM,
            ReminderTiming.AbsoluteUtc(TriggerAt),
            ReminderPriority.NORMAL,
            false,
            RepeatPolicy.Disabled,
            WakePolicy.DEFAULT,
            true,
            CreatedAt));

        var exception = Assert.Throws<DomainValidationException>(() => ReminderRule.CreateForTask(
            taskId,
            ReminderPurpose.TASK_RANGE_END,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
            ReminderPriority.NORMAL,
            false,
            RepeatPolicy.Disabled,
            WakePolicy.DEFAULT,
            true,
            CreatedAt));
        Assert.Contains(exception.Errors, error => error.Code == "reminder.purpose.timing_mismatch");
    }

    [Fact]
    public void ScheduleDerivedRowPreservesOriginAndTerminalTransitionIsOneWay()
    {
        var rule = CreateRule();
        var logicalId = LogicalReminderId.New();
        var original = ReminderSchedule.CreateFromRule(
            rule,
            logicalId,
            scheduleRevision: 1,
            triggerAtUtc: TriggerAt,
            createdAtUtc: CreatedAt,
            timeZoneId: "Asia/Shanghai");
        var derived = ReminderSchedule.CreateDerived(
            original,
            ScheduleCause.SNOOZE,
            scheduleRevision: 2,
            triggerAtUtc: TriggerAt.Plus(Duration.FromMinutes(10)),
            createdAtUtc: ChangedAt);

        Assert.Equal(original.Id, derived.OriginScheduleId);
        Assert.Equal(original.LogicalReminderId, derived.LogicalReminderId);
        Assert.Equal(original.TriggerAtUtc, TriggerAt);
        Assert.Equal(original.PurposeSnapshot, derived.PurposeSnapshot);
        Assert.Equal(original.PrioritySnapshot, derived.PrioritySnapshot);
        Assert.True(original.IsPending);

        var replacement = ReminderScheduleId.New();
        original.Supersede(replacement, ScheduleStateReason.RULE_REBUILT, ChangedAt);
        Assert.Equal(ScheduleState.SUPERSEDED, original.State);
        Assert.Equal(replacement, original.ReplacementScheduleId);
        Assert.Equal(ScheduleStateReason.RULE_REBUILT, original.TerminalReason);

        Assert.Throws<DomainValidationException>(() => original.Cancel(
            ScheduleStateReason.MANUAL_CANCELLED,
            ChangedAt.Plus(Duration.FromMinutes(1))));
    }

    [Fact]
    public void InstanceLifecycleAndResolutionAreMonotonicAndIdempotent()
    {
        var rule = CreateRule();
        var schedule = ReminderSchedule.CreateFromRule(rule, LogicalReminderId.New(), 1, TriggerAt, CreatedAt);
        var instance = ReminderInstance.CreateFromSchedule(schedule, 1, TriggerAt);

        Assert.True(instance.MarkRead(TriggerAt.Plus(Duration.FromSeconds(1))));
        Assert.Equal(ReminderLifecycle.READ, instance.Lifecycle);
        Assert.False(instance.MarkRead(TriggerAt.Plus(Duration.FromSeconds(2))));

        Assert.True(instance.Resolve(ResolutionAction.SNOOZE, TriggerAt.Plus(Duration.FromSeconds(3))));
        Assert.Equal(ReminderLifecycle.RESOLVED, instance.Lifecycle);
        Assert.False(instance.Resolve(ResolutionAction.SNOOZE, TriggerAt.Plus(Duration.FromSeconds(4))));

        var conflict = Assert.Throws<DomainValidationException>(() => instance.Resolve(
            ResolutionAction.DONE,
            TriggerAt.Plus(Duration.FromSeconds(4))));
        Assert.Contains(conflict.Errors, error => error.Code == "reminder.instance.resolution.conflict");

        var unread = ReminderInstance.CreateFromSchedule(schedule, 2, TriggerAt);
        var unsupported = Assert.Throws<DomainValidationException>(() => unread.Resolve(
            ResolutionAction.WATCHED,
            TriggerAt));
        Assert.Contains(unsupported.Errors, error => error.Code == "reminder.instance.resolution.unsupported");
    }

    [Fact]
    public void DeliveryAttemptIsAppendOnlyAndErrorCodeIsBounded()
    {
        var rule = CreateRule();
        var schedule = ReminderSchedule.CreateFromRule(rule, LogicalReminderId.New(), 1, TriggerAt, CreatedAt);
        var instance = ReminderInstance.CreateFromSchedule(schedule, 1, TriggerAt);
        var attempt = ReminderDeliveryAttempt.Create(
            instance.Id,
            ReminderDeliveryChannel.TOAST,
            TriggerAt,
            ReminderDeliveryOutcome.BLOCKED,
            "quiet_hours");

        Assert.Equal(instance.Id, attempt.InstanceId);
        Assert.Equal("quiet_hours", attempt.ErrorCode);
        Assert.Throws<DomainValidationException>(() => ReminderDeliveryAttempt.Create(
            instance.Id,
            ReminderDeliveryChannel.TOAST,
            TriggerAt,
            ReminderDeliveryOutcome.FAILED,
            new string('x', ReminderDeliveryAttempt.MaxErrorCodeBytes + 1)));
    }

    private static ReminderRule CreateRule() => ReminderRule.CreateForTask(
        Guid.CreateVersion7(),
        ReminderPurpose.TASK_START,
        ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
        ReminderPriority.NORMAL,
        false,
        RepeatPolicy.Disabled,
        WakePolicy.DEFAULT,
        true,
        CreatedAt);
}
