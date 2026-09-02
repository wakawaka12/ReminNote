using NodaTime;
using ReminNote.Core.Reminders.Calculation;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests.P302;

internal static class P302TestValues
{
    public static readonly LocalDate PlanDate = new(2026, 8, 28);
    public static readonly DateTimeZone Shanghai = DateTimeZoneProviders.Tzdb["Asia/Shanghai"];
    public static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];
    public static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

    public static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");

    public static ReminderRuleCalculationInput Rule(
        TimeSpec? taskTimeSpec = null,
        ReminderTiming? timing = null,
        ReminderPurpose purpose = ReminderPurpose.TASK_START,
        ReminderPriority priority = ReminderPriority.NORMAL,
        bool pinned = false,
        bool enabled = true,
        long ruleRevision = 1,
        Guid? logicalReminderId = null,
        Guid? ruleId = null,
        Guid? targetId = null,
        Guid? occurrenceId = null) =>
        new(
            ruleId ?? Id(11),
            ReminderTargetKind.TASK_INSTANCE,
            targetId ?? Id(12),
            occurrenceId ?? Id(13),
            logicalReminderId ?? Id(14),
            purpose,
            timing ?? new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -600),
            priority,
            pinned,
            enabled,
            ruleRevision,
            taskTimeSpec ?? TimeSpec.At(PlanDate, new LocalTime(14, 0)));

    public static PendingScheduleSnapshot Pending(
        int scheduleSuffix,
        long scheduleRevision = 1,
        Guid? ruleId = null,
        Guid? occurrenceId = null,
        Guid? logicalReminderId = null,
        string? timeZoneId = "Asia/Shanghai",
        Instant? triggerAtUtc = null) =>
        new(
            Id(scheduleSuffix),
            ruleId ?? Id(11),
            Id(12),
            occurrenceId ?? Id(13),
            logicalReminderId ?? Id(14),
            ReminderPurpose.TASK_START,
            ReminderPriority.NORMAL,
            false,
            1,
            scheduleRevision,
            triggerAtUtc ?? Instant.FromUtc(2026, 8, 28, 5, 50),
            timeZoneId);

    public static ReminderScheduleOrigin Origin(ReminderSchedulePlan plan, ReminderScheduleState state = ReminderScheduleState.CONSUMED) =>
        new(
            plan.ScheduleId,
            plan.RuleId,
            plan.TargetId,
            plan.OccurrenceId,
            plan.LogicalReminderId,
            plan.Purpose,
            plan.Priority,
            plan.Pinned,
            plan.RuleRevision,
            plan.ScheduleRevision,
            plan.TriggerAtUtc,
            plan.TimeZoneId,
            state);

    public static void AssertCode(Action action, string expectedCode) =>
        TestValues.AssertValidationCode(action, expectedCode);
}
