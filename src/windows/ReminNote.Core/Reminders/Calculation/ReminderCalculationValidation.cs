using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Reminders.Calculation;

internal static class ReminderCalculationValidation
{
    public static void RequireRule(ReminderRuleCalculationInput? rule, string parameterName = "rule")
    {
        ArgumentNullException.ThrowIfNull(rule, parameterName);

        RequireUuidV7(rule.RuleId, nameof(rule.RuleId));
        RequireUuidV7(rule.TargetId, nameof(rule.TargetId));
        RequireUuidV7(rule.OccurrenceId, nameof(rule.OccurrenceId));
        RequireUuidV7(rule.LogicalReminderId, nameof(rule.LogicalReminderId));

        if (rule.TargetKind != ReminderTargetKind.TASK_INSTANCE)
        {
            Fail("reminder.target_kind.unsupported", "Only TASK_INSTANCE is executable in P3.", nameof(rule.TargetKind));
        }

        if (!Enum.IsDefined(rule.Purpose))
        {
            Fail("reminder.purpose.invalid", "Reminder purpose is not supported.", nameof(rule.Purpose));
        }

        if (!Enum.IsDefined(rule.Priority))
        {
            Fail("reminder.priority.invalid", "Reminder priority is not supported.", nameof(rule.Priority));
        }

        if (rule.RuleRevision <= 0)
        {
            Fail("reminder.rule_revision.invalid", "Rule revision must start at one.", nameof(rule.RuleRevision));
        }

        if (rule.Timing is null)
        {
            Fail("reminder.timing.required", "Reminder timing is required.", nameof(rule.Timing));
        }

        if (rule.TaskTimeSpec is null)
        {
            Fail("reminder.time_spec.required", "Task time specification is required.", nameof(rule.TaskTimeSpec));
        }
    }

    public static void RequireScheduleRequest(
        ReminderScheduleRequest? request,
        long? requiredRevision = null,
        string parameterName = "schedule")
    {
        ArgumentNullException.ThrowIfNull(request, parameterName);
        RequireUuidV7(request.ScheduleId, nameof(request.ScheduleId));

        if (request.ScheduleRevision <= 0)
        {
            Fail(
                "reminder.schedule_revision.invalid",
                "Schedule revision must start at one.",
                nameof(request.ScheduleRevision));
        }

        if (requiredRevision is not null && request.ScheduleRevision != requiredRevision.Value)
        {
            Fail(
                "reminder.schedule.first_revision.invalid",
                "The first schedule revision must be one.",
                nameof(request.ScheduleRevision));
        }
    }

    public static void RequireOccurrence(ReminderOccurrenceInput? occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence, nameof(occurrence));
        RequireUuidV7(occurrence.OccurrenceId, nameof(occurrence.OccurrenceId));

        if (occurrence.TaskTimeSpec is null)
        {
            Fail("reminder.time_spec.required", "Task time specification is required.", nameof(occurrence.TaskTimeSpec));
        }
    }

    public static void ValidateRuleTimingShape(ReminderRuleCalculationInput rule)
    {
        ValidateTimingKind(rule.Timing);

        if (rule.Timing is ReminderTiming.AbsoluteUtc)
        {
            if (rule.Purpose != ReminderPurpose.TASK_CUSTOM)
            {
                Fail(
                    "reminder.timing.purpose_mismatch",
                    "Absolute UTC timing is reserved for TASK_CUSTOM.",
                    nameof(rule.Purpose));
            }

            return;
        }

        var relative = (ReminderTiming.Relative)rule.Timing;
        ValidateRelativeAnchor(rule.TaskTimeSpec, relative.Anchor);

        switch (rule.TaskTimeSpec.Type)
        {
            case TaskTimeType.ANYTIME:
                Fail(
                    "reminder.timing.anytime.relative",
                    "ANYTIME does not create a clock reminder from a relative anchor.",
                    nameof(rule.Timing));
                break;
            case TaskTimeType.TIME:
                if (relative.Anchor != ReminderAnchor.TASK_TIME ||
                    (rule.Purpose != ReminderPurpose.TASK_PRE_START &&
                     rule.Purpose != ReminderPurpose.TASK_START))
                {
                    Fail(
                        "reminder.timing.anchor.incompatible",
                        "TIME reminders must use TASK_TIME for TASK_PRE_START or TASK_START.",
                        nameof(rule.Timing));
                }

                break;
            case TaskTimeType.RANGE:
                var expectedAnchor = rule.Purpose switch
                {
                    ReminderPurpose.TASK_PRE_START or ReminderPurpose.TASK_START => ReminderAnchor.RANGE_START,
                    ReminderPurpose.TASK_RANGE_END => ReminderAnchor.RANGE_END,
                    _ => (ReminderAnchor?)null
                };

                if (expectedAnchor is null || relative.Anchor != expectedAnchor.Value)
                {
                    Fail(
                        "reminder.timing.anchor.incompatible",
                        "RANGE reminders must use RANGE_START or RANGE_END according to purpose.",
                        nameof(rule.Timing));
                }

                break;
            default:
                Fail("reminder.time_spec.invalid", "Task time specification type is not supported.", nameof(rule.TaskTimeSpec));
                break;
        }
    }

    public static void ValidateRelativeAnchor(TimeSpec taskTimeSpec, ReminderAnchor anchor)
    {
        if (!Enum.IsDefined(anchor))
        {
            Fail("reminder.anchor.invalid", "Reminder anchor is not supported.", nameof(anchor));
        }

        var compatible = taskTimeSpec.Type switch
        {
            TaskTimeType.TIME when taskTimeSpec is TimePointSpec => anchor == ReminderAnchor.TASK_TIME,
            TaskTimeType.RANGE when taskTimeSpec is TimeRangeSpec =>
                anchor == ReminderAnchor.RANGE_START || anchor == ReminderAnchor.RANGE_END,
            _ => false
        };

        if (!compatible)
        {
            if (taskTimeSpec.Type == TaskTimeType.ANYTIME)
            {
                Fail(
                    "reminder.timing.anytime.relative",
                    "ANYTIME does not have a relative reminder anchor.",
                    nameof(anchor));
            }

            Fail(
                "reminder.timing.anchor.incompatible",
                "Reminder anchor does not match the Task time shape.",
                nameof(anchor));
        }
    }

    public static void RequireUuidV7(Guid value, string fieldName)
    {
        if (value == Guid.Empty)
        {
            Fail("reminder.identity.empty", "Reminder identity cannot be empty.", fieldName);
        }

        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);

        // Guid's first three fields are little-endian in TryWriteBytes, as in
        // TaskId. The version nibble is byte 7 and the RFC variant is byte 8.
        if ((bytes[7] & 0xF0) != 0x70 || (bytes[8] & 0xC0) != 0x80)
        {
            Fail("reminder.identity.not_uuid_v7", "Reminder identity must use UUID version 7.", fieldName);
        }
    }

    public static void RequireTerminalReason(ReminderScheduleTerminalReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            Fail("reminder.terminal_reason.invalid", "Terminal schedule reason is not supported.", nameof(reason));
        }
    }

    public static void RequireOrigin(ReminderScheduleOrigin? origin)
    {
        ArgumentNullException.ThrowIfNull(origin, nameof(origin));
        RequireUuidV7(origin.ScheduleId, nameof(origin.ScheduleId));
        RequireUuidV7(origin.RuleId, nameof(origin.RuleId));
        RequireUuidV7(origin.TargetId, nameof(origin.TargetId));
        RequireUuidV7(origin.OccurrenceId, nameof(origin.OccurrenceId));
        RequireUuidV7(origin.LogicalReminderId, nameof(origin.LogicalReminderId));

        if (!Enum.IsDefined(origin.Purpose))
        {
            Fail("reminder.purpose.invalid", "Reminder purpose is not supported.", nameof(origin.Purpose));
        }

        if (!Enum.IsDefined(origin.Priority))
        {
            Fail("reminder.priority.invalid", "Reminder priority is not supported.", nameof(origin.Priority));
        }

        if (origin.RuleRevision <= 0)
        {
            Fail("reminder.rule_revision.invalid", "Rule revision must be positive.", nameof(origin.RuleRevision));
        }

        if (origin.ScheduleRevision <= 0)
        {
            Fail("reminder.schedule_revision.invalid", "Schedule revision must be positive.", nameof(origin.ScheduleRevision));
        }

        if (origin.State != ReminderScheduleState.CONSUMED)
        {
            Fail("reminder.origin.not_consumed", "Snooze and repeat require a CONSUMED origin schedule.", nameof(origin.State));
        }
    }

    public static void ValidateTimingKind(ReminderTiming? timing)
    {
        ArgumentNullException.ThrowIfNull(timing, nameof(timing));

        var expected = timing switch
        {
            ReminderTiming.Relative => ReminderTimingKind.RELATIVE,
            ReminderTiming.AbsoluteUtc => ReminderTimingKind.ABSOLUTE_UTC,
            _ => (ReminderTimingKind?)null
        };

        if (expected is null || timing.Kind != expected.Value)
        {
            Fail("reminder.timing.invalid", "Reminder timing shape is not supported.", nameof(timing));
        }
    }

    public static void Fail(string code, string message, string? fieldName = null) =>
        throw new DomainValidationException(new DomainValidationError(code, message, fieldName));
}
