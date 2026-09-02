using NodaTime;
using ReminNote.Core.Reminders.Calculation;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests.P302;

public sealed class ReminderSchedulePlannerTests
{
    [Fact]
    public void FirstScheduleMaterializesRuleSnapshotAndTrigger()
    {
        var rule = P302TestValues.Rule();

        var result = ReminderSchedulePlanner.DeriveFirstSchedule(
            rule,
            new ReminderScheduleRequest(P302TestValues.Id(21), 1),
            P302TestValues.Shanghai);

        Assert.True(result.IsScheduled);
        Assert.Null(result.ReasonCode);
        var schedule = Assert.IsType<ReminderSchedulePlan>(result.Schedule);
        Assert.Equal(P302TestValues.Id(21), schedule.ScheduleId);
        Assert.Equal(rule.RuleId, schedule.RuleId);
        Assert.Equal(rule.TargetId, schedule.TargetId);
        Assert.Equal(rule.OccurrenceId, schedule.OccurrenceId);
        Assert.Equal(rule.LogicalReminderId, schedule.LogicalReminderId);
        Assert.Equal(ReminderScheduleCause.RULE, schedule.Cause);
        Assert.Equal(ReminderScheduleState.PENDING, schedule.State);
        Assert.Equal(Instant.FromUtc(2026, 8, 28, 5, 50), schedule.TriggerAtUtc);
        Assert.Equal(P302TestValues.Shanghai.Id, schedule.TimeZoneId);
    }

    [Fact]
    public void DisabledRuleProducesNoScheduleAndDoesNotNeedAZone()
    {
        var result = ReminderSchedulePlanner.DeriveFirstSchedule(
            P302TestValues.Rule(enabled: false),
            new ReminderScheduleRequest(P302TestValues.Id(21), 1),
            null);

        Assert.False(result.IsScheduled);
        Assert.Equal(ReminderCalculationCodes.RuleDisabled, result.ReasonCode);
        Assert.Null(result.Schedule);
    }

    [Fact]
    public void AnytimeCustomAbsoluteRuleProducesExplicitUtcSchedule()
    {
        var trigger = Instant.FromUtc(2026, 8, 28, 18, 0);
        var rule = P302TestValues.Rule(
            taskTimeSpec: TimeSpec.Anytime(P302TestValues.PlanDate),
            timing: new ReminderTiming.AbsoluteUtc(trigger),
            purpose: ReminderPurpose.TASK_CUSTOM);

        var result = ReminderSchedulePlanner.DeriveFirstSchedule(
            rule,
            new ReminderScheduleRequest(P302TestValues.Id(21), 1),
            null);

        Assert.Equal(trigger, Assert.IsType<ReminderSchedulePlan>(result.Schedule).TriggerAtUtc);
        Assert.Null(result.Schedule!.TimeZoneId);
    }

    [Fact]
    public void OccurrenceAdapterInputMustStayInRuleScope()
    {
        var rule = P302TestValues.Rule();
        var occurrence = new ReminderOccurrenceInput(
            P302TestValues.Id(13),
            TimeSpec.At(P302TestValues.PlanDate, new LocalTime(15, 0)));

        var viaOccurrence = ReminderSchedulePlanner.CalculateForOccurrence(
            rule,
            occurrence,
            new ReminderScheduleRequest(P302TestValues.Id(21), 1),
            DateTimeZone.Utc);
        var direct = ReminderSchedulePlanner.DeriveFirstSchedule(
            rule with { TaskTimeSpec = occurrence.TaskTimeSpec },
            new ReminderScheduleRequest(P302TestValues.Id(21), 1),
            DateTimeZone.Utc);

        Assert.Equal(direct, viaOccurrence);

        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.CalculateForOccurrence(
                rule,
                occurrence with { OccurrenceId = P302TestValues.Id(99) },
                new ReminderScheduleRequest(P302TestValues.Id(21), 1),
                DateTimeZone.Utc),
            "reminder.occurrence.scope_mismatch");
    }

    [Fact]
    public void RebuildSupersedesPendingSchedulesInDeterministicOrder()
    {
        var previous = P302TestValues.Rule();
        var next = previous with
        {
            TaskTimeSpec = TimeSpec.At(P302TestValues.PlanDate, new LocalTime(15, 0)),
            RuleRevision = 2,
            LogicalReminderId = P302TestValues.Id(15)
        };
        var pending = new[]
        {
            P302TestValues.Pending(22, scheduleRevision: 2),
            P302TestValues.Pending(21, scheduleRevision: 1)
        };

        var result = ReminderSchedulePlanner.Rebuild(
            previous,
            next,
            pending,
            new ReminderScheduleRequest(P302TestValues.Id(23), 3),
            P302TestValues.Shanghai);

        Assert.False(result.IsNoOp);
        Assert.Equal(ReminderScheduleTerminalReason.TASK_TIME_CHANGED, result.RebuildReason);
        Assert.Equal(
            new[] { P302TestValues.Id(21), P302TestValues.Id(22) },
            result.InvalidatedSchedules.Select(item => item.ScheduleId));
        Assert.All(result.InvalidatedSchedules, item =>
        {
            Assert.Equal(ReminderScheduleState.SUPERSEDED, item.State);
            Assert.Equal(ReminderScheduleTerminalReason.TASK_TIME_CHANGED, item.Reason);
            Assert.Equal(P302TestValues.Id(23), item.ReplacementScheduleId);
        });

        var replacement = Assert.IsType<ReminderSchedulePlan>(result.ReplacementSchedule);
        Assert.Equal(2, replacement.RuleRevision);
        Assert.Equal(3, replacement.ScheduleRevision);
        Assert.Equal(P302TestValues.Id(15), replacement.LogicalReminderId);
        Assert.Equal(Instant.FromUtc(2026, 8, 28, 6, 50), replacement.TriggerAtUtc);
    }

    [Fact]
    public void TimeZoneChangeForcesRevisionedRebuildEvenWhenRuleShapeIsSame()
    {
        var previous = P302TestValues.Rule();
        var next = previous with { RuleRevision = 2, LogicalReminderId = P302TestValues.Id(16) };

        var result = ReminderSchedulePlanner.Rebuild(
            previous,
            next,
            new[] { P302TestValues.Pending(21, timeZoneId: P302TestValues.Shanghai.Id) },
            new ReminderScheduleRequest(P302TestValues.Id(22), 2),
            P302TestValues.Berlin);

        Assert.Equal(ReminderScheduleTerminalReason.TIME_ZONE_CHANGED, result.RebuildReason);
        Assert.Equal(ReminderScheduleTerminalReason.TIME_ZONE_CHANGED, result.InvalidatedSchedules.Single().Reason);
        Assert.Equal(P302TestValues.Berlin.Id, result.ReplacementSchedule!.TimeZoneId);
        Assert.Equal(Instant.FromUtc(2026, 8, 28, 11, 50), result.ReplacementSchedule.TriggerAtUtc);
    }

    [Fact]
    public void DisabledRevisionCancelsPendingWithoutReplacement()
    {
        var previous = P302TestValues.Rule();
        var next = previous with
        {
            Enabled = false,
            RuleRevision = 2,
            LogicalReminderId = P302TestValues.Id(15)
        };

        var result = ReminderSchedulePlanner.Rebuild(
            previous,
            next,
            new[] { P302TestValues.Pending(21), P302TestValues.Pending(22, scheduleRevision: 2) },
            replacementSchedule: null,
            timeZone: null);

        Assert.Null(result.ReplacementSchedule);
        Assert.Equal(ReminderScheduleTerminalReason.RULE_DISABLED, result.RebuildReason);
        Assert.All(result.InvalidatedSchedules, item =>
        {
            Assert.Equal(ReminderScheduleState.CANCELLED, item.State);
            Assert.Equal(ReminderScheduleTerminalReason.RULE_DISABLED, item.Reason);
            Assert.Null(item.ReplacementScheduleId);
        });
    }

    [Fact]
    public void SemanticNoOpDoesNotCreateRevisionOrTouchPendingRows()
    {
        var rule = P302TestValues.Rule();
        var pending = new[] { P302TestValues.Pending(21) };

        var result = ReminderSchedulePlanner.Rebuild(
            rule,
            rule with { },
            pending,
            replacementSchedule: null,
            timeZone: P302TestValues.Shanghai);

        Assert.True(result.IsNoOp);
        Assert.Empty(result.InvalidatedSchedules);
        Assert.Null(result.ReplacementSchedule);
        Assert.Null(result.RebuildReason);
        Assert.Equal(P302TestValues.Id(21), pending[0].ScheduleId);
    }

    [Fact]
    public void CancelPendingOnlyCancelsTheRequestedOccurrence()
    {
        var occurrence = P302TestValues.Id(13);
        var transitions = ReminderSchedulePlanner.CancelPending(
            occurrence,
            new[] { P302TestValues.Pending(22), P302TestValues.Pending(21) },
            ReminderScheduleTerminalReason.TASK_RESULT_RECORDED);

        Assert.Equal(new[] { P302TestValues.Id(21), P302TestValues.Id(22) }, transitions.Select(item => item.ScheduleId));
        Assert.All(transitions, item =>
        {
            Assert.Equal(ReminderScheduleState.CANCELLED, item.State);
            Assert.Equal(ReminderScheduleTerminalReason.TASK_RESULT_RECORDED, item.Reason);
            Assert.Null(item.ReplacementScheduleId);
        });

        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.CancelPending(
                occurrence,
                new[] { P302TestValues.Pending(21), P302TestValues.Pending(22, occurrenceId: P302TestValues.Id(99)) },
                ReminderScheduleTerminalReason.TASK_DELETED),
            "reminder.occurrence.scope_mismatch");
    }

    [Fact]
    public void SnoozeCreatesNewPendingScheduleAndPreservesOriginSnapshot()
    {
        var originPlan = Assert.IsType<ReminderSchedulePlan>(
            ReminderSchedulePlanner.DeriveFirstSchedule(
                P302TestValues.Rule(pinned: true, priority: ReminderPriority.HIGH),
                new ReminderScheduleRequest(P302TestValues.Id(21), 1),
                P302TestValues.Shanghai).Schedule);
        var origin = P302TestValues.Origin(originPlan);
        var snoozeAt = origin.TriggerAtUtc.Plus(Duration.FromMinutes(15));

        var result = ReminderSchedulePlanner.DeriveSnooze(origin, P302TestValues.Id(22), 2, snoozeAt);
        var snooze = Assert.IsType<ReminderSchedulePlan>(result.Schedule);

        Assert.Equal(ReminderScheduleCause.SNOOZE, snooze.Cause);
        Assert.Equal(origin.ScheduleId, snooze.OriginScheduleId);
        Assert.Equal(origin.LogicalReminderId, snooze.LogicalReminderId);
        Assert.Equal(origin.Purpose, snooze.Purpose);
        Assert.Equal(origin.Priority, snooze.Priority);
        Assert.Equal(origin.Pinned, snooze.Pinned);
        Assert.Equal(snoozeAt, snooze.TriggerAtUtc);
        Assert.Equal(origin.TriggerAtUtc, originPlan.TriggerAtUtc);
    }

    [Fact]
    public void RepeatHonorsBoundedCountAndDisabledPolicy()
    {
        var originPlan = Assert.IsType<ReminderSchedulePlan>(
            ReminderSchedulePlanner.DeriveFirstSchedule(
                P302TestValues.Rule(),
                new ReminderScheduleRequest(P302TestValues.Id(21), 1),
                DateTimeZone.Utc).Schedule);
        var origin = P302TestValues.Origin(originPlan);
        var policy = new ReminderRepeatPolicy(true, 3_600, 2);

        var repeated = ReminderSchedulePlanner.DeriveRepeat(origin, P302TestValues.Id(22), 2, 2, policy);
        Assert.Equal(ReminderScheduleCause.REPEAT, repeated.Schedule!.Cause);
        Assert.Equal(origin.ScheduleId, repeated.Schedule.OriginScheduleId);
        Assert.Equal(origin.TriggerAtUtc.Plus(Duration.FromHours(1)), repeated.Schedule.TriggerAtUtc);

        var limit = ReminderSchedulePlanner.DeriveRepeat(origin, P302TestValues.Id(23), 3, 3, policy);
        Assert.False(limit.IsScheduled);
        Assert.Equal(ReminderCalculationCodes.RepeatLimitReached, limit.ReasonCode);

        var disabled = ReminderSchedulePlanner.DeriveRepeat(
            origin,
            P302TestValues.Id(24),
            4,
            2,
            new ReminderRepeatPolicy(false, null, null));
        Assert.False(disabled.IsScheduled);
        Assert.Equal(ReminderCalculationCodes.RepeatDisabled, disabled.ReasonCode);
    }

    [Fact]
    public void InvalidOriginAndRepeatPolicyAreRejected()
    {
        var originPlan = Assert.IsType<ReminderSchedulePlan>(
            ReminderSchedulePlanner.DeriveFirstSchedule(
                P302TestValues.Rule(),
                new ReminderScheduleRequest(P302TestValues.Id(21), 1),
                DateTimeZone.Utc).Schedule);
        var pendingOrigin = P302TestValues.Origin(originPlan, ReminderScheduleState.PENDING);

        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.DeriveSnooze(
                pendingOrigin,
                P302TestValues.Id(22),
                2,
                originPlan.TriggerAtUtc.Plus(Duration.FromMinutes(1))),
            "reminder.origin.not_consumed");

        var consumed = P302TestValues.Origin(originPlan);
        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.DeriveRepeat(
                consumed,
                P302TestValues.Id(22),
                2,
                2,
                new ReminderRepeatPolicy(true, 0, null)),
            "reminder.repeat.interval.invalid");

        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.DeriveRepeat(
                consumed,
                P302TestValues.Id(22),
                2,
                2,
                new ReminderRepeatPolicy(true, 3_601, null),
                new ReminderCalculationLimits(-60, 60, maxRepeatIntervalSeconds: 3_600, maxRepeatCount: 4)),
            "reminder.repeat.interval.out_of_range");

        P302TestValues.AssertCode(
            () => ReminderSchedulePlanner.DeriveSnooze(
                consumed,
                P302TestValues.Id(22),
                2,
                consumed.TriggerAtUtc),
            "reminder.snooze.trigger_not_after_origin");
    }
}
