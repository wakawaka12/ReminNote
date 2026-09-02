using NodaTime;
using ReminNote.Core.Reminders.Calculation;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests.P302;

public sealed class ReminderTimeCalculatorTests
{
    public static TheoryData<TimeSpec, ReminderTiming, DateTimeZone, Instant> RelativeTimeMatrix =>
        new()
        {
            {
                TimeSpec.At(P302TestValues.PlanDate, new LocalTime(14, 0)),
                new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -900),
                DateTimeZone.Utc,
                Instant.FromUtc(2026, 8, 28, 13, 45)
            },
            {
                TimeSpec.Range(P302TestValues.PlanDate, new LocalTime(14, 0), new LocalTime(17, 0)),
                new ReminderTiming.Relative(ReminderAnchor.RANGE_START, 0),
                DateTimeZone.Utc,
                Instant.FromUtc(2026, 8, 28, 14, 0)
            },
            {
                TimeSpec.Range(P302TestValues.PlanDate, new LocalTime(14, 0), new LocalTime(17, 0)),
                new ReminderTiming.Relative(ReminderAnchor.RANGE_END, -600),
                DateTimeZone.Utc,
                Instant.FromUtc(2026, 8, 28, 16, 50)
            },
            {
                TimeSpec.Range(P302TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)),
                new ReminderTiming.Relative(ReminderAnchor.RANGE_END, -300),
                DateTimeZone.Utc,
                Instant.FromUtc(2026, 8, 29, 0, 55)
            },
            {
                TimeSpec.At(P302TestValues.PlanDate, new LocalTime(14, 0)),
                new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -600),
                P302TestValues.Shanghai,
                Instant.FromUtc(2026, 8, 28, 5, 50)
            }
        };

    [Theory]
    [MemberData(nameof(RelativeTimeMatrix))]
    public void RelativeMatrixUsesTaskShapeAndExplicitZone(
        TimeSpec taskTimeSpec,
        ReminderTiming timing,
        DateTimeZone timeZone,
        Instant expectedTriggerAtUtc)
    {
        var result = ReminderTimeCalculator.Calculate(taskTimeSpec, timing, timeZone);

        Assert.Equal(ReminderTimingKind.RELATIVE, result.TimingKind);
        Assert.Equal(expectedTriggerAtUtc, result.TriggerAtUtc);
        Assert.Equal(timeZone.Id, result.TimeZoneId);
        Assert.NotNull(result.AnchorLocalDateTime);
        Assert.NotNull(result.ResolvedLocalDateTime);
        Assert.NotNull(result.AppliedOffset);
    }

    [Fact]
    public void AbsoluteUtcWorksForAnytimeWithoutAZone()
    {
        var expected = Instant.FromUtc(2026, 8, 28, 12, 34, 56);

        var result = ReminderTimeCalculator.Calculate(
            TimeSpec.Anytime(P302TestValues.PlanDate),
            new ReminderTiming.AbsoluteUtc(expected),
            null);

        Assert.Equal(ReminderTimingKind.ABSOLUTE_UTC, result.TimingKind);
        Assert.Equal(expected, result.TriggerAtUtc);
        Assert.Null(result.TimeZoneId);
        Assert.Equal(ReminderTimeMappingResolution.NOT_APPLICABLE, result.MappingResolution);
        Assert.Null(result.AnchorLocalDateTime);
        Assert.Null(result.ResolvedLocalDateTime);
        Assert.Null(result.AppliedOffset);
    }

    [Fact]
    public void SkippedLocalTimeMovesForwardDeterministically()
    {
        var result = ReminderTimeCalculator.Calculate(
            TimeSpec.At(new LocalDate(2026, 3, 8), new LocalTime(2, 30)),
            new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
            P302TestValues.NewYork);

        Assert.Equal(ReminderTimeMappingResolution.SKIPPED_FORWARD, result.MappingResolution);
        Assert.Equal(new LocalDateTime(2026, 3, 8, 2, 30), result.AnchorLocalDateTime);
        Assert.Equal(new LocalDateTime(2026, 3, 8, 3, 30), result.ResolvedLocalDateTime);
        Assert.Equal(Offset.FromHours(-4), result.AppliedOffset);
        Assert.Equal(Instant.FromUtc(2026, 3, 8, 7, 30), result.TriggerAtUtc);
    }

    [Fact]
    public void AmbiguousLocalTimeChoosesEarlierOffsetDeterministically()
    {
        var result = ReminderTimeCalculator.Calculate(
            TimeSpec.At(new LocalDate(2026, 11, 1), new LocalTime(1, 30)),
            new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
            P302TestValues.NewYork);

        Assert.Equal(ReminderTimeMappingResolution.AMBIGUOUS_EARLIER, result.MappingResolution);
        Assert.Equal(Offset.FromHours(-4), result.AppliedOffset);
        Assert.Equal(Instant.FromUtc(2026, 11, 1, 5, 30), result.TriggerAtUtc);
    }

    [Fact]
    public void AnytimeRelativeAndMissingZoneAreRejectedWithStableCodes()
    {
        P302TestValues.AssertCode(
            () => ReminderTimeCalculator.Calculate(
                TimeSpec.Anytime(P302TestValues.PlanDate),
                new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
                DateTimeZone.Utc),
            "reminder.timing.anytime.relative");

        P302TestValues.AssertCode(
            () => ReminderTimeCalculator.Calculate(
                TimeSpec.At(P302TestValues.PlanDate, new LocalTime(9, 0)),
                new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
                null),
            "reminder.time_zone.required");
    }

    [Fact]
    public void OffsetBoundsRejectOutOfRangeAndRepeatCallsAreEqual()
    {
        var limits = new ReminderCalculationLimits(-60, 60, maxRepeatIntervalSeconds: 3_600, maxRepeatCount: 4);
        var timing = new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 61);

        P302TestValues.AssertCode(
            () => ReminderTimeCalculator.Calculate(
                TimeSpec.At(P302TestValues.PlanDate, new LocalTime(9, 0)),
                timing,
                DateTimeZone.Utc,
                limits),
            "reminder.offset.out_of_range");

        var first = ReminderTimeCalculator.Calculate(
            TimeSpec.At(P302TestValues.PlanDate, new LocalTime(9, 0)),
            new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -60),
            DateTimeZone.Utc,
            limits);
        var second = ReminderTimeCalculator.Calculate(
            TimeSpec.At(P302TestValues.PlanDate, new LocalTime(9, 0)),
            new ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -60),
            DateTimeZone.Utc,
            limits);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RangeEndUsesDerivedEndLocalDateAcrossMidnight()
    {
        var range = TimeSpec.Range(P302TestValues.PlanDate, new LocalTime(23, 30), new LocalTime(0, 15));

        var result = ReminderTimeCalculator.Calculate(
            range,
            new ReminderTiming.Relative(ReminderAnchor.RANGE_END, 0),
            DateTimeZone.Utc);

        Assert.Equal(P302TestValues.PlanDate.PlusDays(1).At(new LocalTime(0, 15)), result.AnchorLocalDateTime);
        Assert.Equal(Instant.FromUtc(2026, 8, 29, 0, 15), result.TriggerAtUtc);
    }
}
