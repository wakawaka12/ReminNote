using NodaTime;
using NodaTime.TimeZones;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Reminders.Calculation;

/// <summary>
/// Pure conversion from an existing Task.TimeSpec and frozen reminder timing
/// into a UTC trigger. No clock, storage, ID generation, or process state is
/// consulted.
/// </summary>
public static class ReminderTimeCalculator
{
    private static readonly ZoneLocalMappingResolver LocalMappingResolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted);

    public static ReminderTimeCalculation Calculate(
        TimeSpec taskTimeSpec,
        ReminderTiming timing,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null)
    {
        if (taskTimeSpec is null)
        {
            ReminderCalculationValidation.Fail(
                "reminder.time_spec.required",
                "Task time specification is required.",
                nameof(taskTimeSpec));
        }

        ReminderCalculationValidation.ValidateTimingKind(timing);
        limits ??= ReminderCalculationLimits.Default;

        if (timing is ReminderTiming.AbsoluteUtc absolute)
        {
            return new ReminderTimeCalculation(
                ReminderTimingKind.ABSOLUTE_UTC,
                null,
                null,
                null,
                null,
                null,
                null,
                ReminderTimeMappingResolution.NOT_APPLICABLE,
                absolute.AtUtc);
        }

        var relative = (ReminderTiming.Relative)timing;
        if (relative.OffsetSeconds < limits.MinRelativeOffsetSeconds ||
            relative.OffsetSeconds > limits.MaxRelativeOffsetSeconds)
        {
            ReminderCalculationValidation.Fail(
                "reminder.offset.out_of_range",
                "Relative offset is outside the configured calculator bounds.",
                nameof(relative.OffsetSeconds));
        }

        var validatedTaskTimeSpec = taskTimeSpec!;
        ReminderCalculationValidation.ValidateRelativeAnchor(validatedTaskTimeSpec, relative.Anchor);
        if (timeZone is null)
        {
            ReminderCalculationValidation.Fail(
                "reminder.time_zone.required",
                "A user time zone is required for relative timing.",
                nameof(timeZone));
        }

        var resolvedTimeZone = timeZone!;
        if (string.IsNullOrWhiteSpace(resolvedTimeZone.Id))
        {
            ReminderCalculationValidation.Fail(
                "reminder.time_zone.invalid",
                "A relative reminder requires a stable time-zone id.",
                nameof(timeZone));
        }

        var anchorLocalDateTime = GetAnchorLocalDateTime(validatedTaskTimeSpec, relative.Anchor);
        var mapping = resolvedTimeZone.MapLocal(anchorLocalDateTime);
        var resolution = mapping.Count switch
        {
            0 => ReminderTimeMappingResolution.SKIPPED_FORWARD,
            1 => ReminderTimeMappingResolution.UNAMBIGUOUS,
            2 => ReminderTimeMappingResolution.AMBIGUOUS_EARLIER,
            _ => throw new InvalidOperationException("Noda Time returned an unsupported local mapping count.")
        };

        ZonedDateTime resolved;
        Instant triggerAtUtc;
        try
        {
            resolved = resolvedTimeZone.ResolveLocal(anchorLocalDateTime, LocalMappingResolver);
            triggerAtUtc = resolved.ToInstant().Plus(Duration.FromSeconds(relative.OffsetSeconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            ReminderCalculationValidation.Fail(
                "reminder.time.overflow",
                "The resolved reminder instant is outside Noda Time's representable range.",
                nameof(relative.OffsetSeconds));
            throw;
        }
        catch (OverflowException)
        {
            ReminderCalculationValidation.Fail(
                "reminder.time.overflow",
                "The resolved reminder instant is outside Noda Time's representable range.",
                nameof(relative.OffsetSeconds));
            throw;
        }

        return new ReminderTimeCalculation(
            ReminderTimingKind.RELATIVE,
            relative.Anchor,
            relative.OffsetSeconds,
            anchorLocalDateTime,
            resolved.LocalDateTime,
            resolved.Offset,
            resolvedTimeZone.Id,
            resolution,
            triggerAtUtc);
    }

    private static LocalDateTime GetAnchorLocalDateTime(TimeSpec taskTimeSpec, ReminderAnchor anchor) =>
        (taskTimeSpec, anchor) switch
        {
            (TimePointSpec timePoint, ReminderAnchor.TASK_TIME) => timePoint.LocalDateTime,
            (TimeRangeSpec range, ReminderAnchor.RANGE_START) => range.StartLocalDateTime,
            (TimeRangeSpec range, ReminderAnchor.RANGE_END) => range.EndLocalDateTime,
            _ => throw new InvalidOperationException("Validated reminder anchor did not match the Task time shape.")
        };
}
