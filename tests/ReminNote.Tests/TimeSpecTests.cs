using NodaTime;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TimeSpecTests
{
    public static TheoryData<TaskTimeType, LocalTime?, LocalTime?, LocalTime?> LegalShapes =>
        new()
        {
            { TaskTimeType.ANYTIME, null, null, null },
            { TaskTimeType.TIME, TestValues.Afternoon, null, null },
            { TaskTimeType.RANGE, null, new LocalTime(14, 0), new LocalTime(17, 0) },
            { TaskTimeType.RANGE, null, new LocalTime(23, 0), new LocalTime(1, 0) }
        };

    public static TheoryData<TaskTimeType, LocalTime?, LocalTime?, LocalTime?> InvalidShapes =>
        new()
        {
            { TaskTimeType.ANYTIME, TestValues.Afternoon, null, null },
            { TaskTimeType.ANYTIME, null, new LocalTime(14, 0), null },
            { TaskTimeType.ANYTIME, null, null, new LocalTime(17, 0) },
            { TaskTimeType.TIME, null, null, null },
            { TaskTimeType.TIME, TestValues.Afternoon, new LocalTime(14, 0), null },
            { TaskTimeType.TIME, TestValues.Afternoon, null, new LocalTime(17, 0) },
            { TaskTimeType.RANGE, null, new LocalTime(14, 0), null },
            { TaskTimeType.RANGE, null, null, new LocalTime(17, 0) },
            { TaskTimeType.RANGE, TestValues.Afternoon, new LocalTime(14, 0), new LocalTime(17, 0) }
        };

    [Theory]
    [MemberData(nameof(LegalShapes))]
    public void CreateAcceptsOnlyLegalTimeShapeColumnCombinations(
        TaskTimeType type,
        LocalTime? timePoint,
        LocalTime? rangeStart,
        LocalTime? rangeEnd)
    {
        var spec = TimeSpec.Create(type, TestValues.PlanDate, timePoint, rangeStart, rangeEnd);

        Assert.Equal(type, spec.Type);
        Assert.Equal(TestValues.PlanDate, spec.LocalDate);
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void CreateRejectsMixedOrMissingColumnsDeterministically(
        TaskTimeType type,
        LocalTime? timePoint,
        LocalTime? rangeStart,
        LocalTime? rangeEnd)
    {
        TestValues.AssertValidationCode(
            () => TimeSpec.Create(type, TestValues.PlanDate, timePoint, rangeStart, rangeEnd),
            "task.time_spec.mixed_columns");
    }

    [Fact]
    public void CreateRejectsUnknownTimeTypeDeterministically()
    {
        TestValues.AssertValidationCode(
            () => TimeSpec.Create((TaskTimeType)99, TestValues.PlanDate, null, null, null),
            "task.time_type.invalid");
    }

    [Fact]
    public void AnytimeSpecOwnsOnlyItsPlanDate()
    {
        var spec = TimeSpec.Anytime(TestValues.PlanDate);

        Assert.Equal(TaskTimeType.ANYTIME, spec.Type);
        Assert.Equal(TestValues.PlanDate, spec.LocalDate);
        Assert.IsType<AnytimeSpec>(spec);
    }

    [Fact]
    public void TimePointSpecPreservesLocalDateAndLocalTime()
    {
        var time = new LocalTime(14, 30, 15);
        var spec = TimeSpec.At(TestValues.PlanDate, time);

        Assert.Equal(TaskTimeType.TIME, spec.Type);
        Assert.Equal(TestValues.PlanDate, spec.LocalDate);
        Assert.Equal(time, spec.TimePoint);
        Assert.Equal(TestValues.PlanDate.At(time), spec.LocalDateTime);
    }

    [Fact]
    public void SameDayRangeHasSameEndDateAndPositiveDuration()
    {
        var spec = TimeSpec.Range(TestValues.PlanDate, new LocalTime(14, 0), new LocalTime(17, 0));

        Assert.False(spec.IsCrossMidnight);
        Assert.Equal(TestValues.PlanDate, spec.EndLocalDate);
        Assert.Equal(Duration.FromHours(3), spec.Duration);
        Assert.Equal(TestValues.PlanDate.At(new LocalTime(14, 0)), spec.StartLocalDateTime);
        Assert.Equal(TestValues.PlanDate.At(new LocalTime(17, 0)), spec.EndLocalDateTime);
    }

    [Fact]
    public void CrossMidnightRangeDerivesNextEndDateAndKeepsPlanDateOwner()
    {
        var spec = TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0));

        Assert.True(spec.IsCrossMidnight);
        Assert.Equal(TestValues.PlanDate, spec.LocalDate);
        Assert.Equal(TestValues.PlanDate.PlusDays(1), spec.EndLocalDate);
        Assert.Equal(Duration.FromHours(2), spec.Duration);
        Assert.Equal(TestValues.PlanDate.At(new LocalTime(23, 0)), spec.StartLocalDateTime);
        Assert.Equal(TestValues.PlanDate.PlusDays(1).At(new LocalTime(1, 0)), spec.EndLocalDateTime);
    }

    [Fact]
    public void RangeRejectsEqualStartAndEndInsteadOfCreatingZeroDuration()
    {
        TestValues.AssertValidationCode(
            () => TimeSpec.Range(TestValues.PlanDate, TestValues.Afternoon, TestValues.Afternoon),
            "task.time_range.zero_duration");
    }

    [Fact]
    public void TimeSpecsUseStructuralValueEquality()
    {
        Assert.Equal(
            TimeSpec.Anytime(TestValues.PlanDate),
            TimeSpec.Anytime(TestValues.PlanDate));
        Assert.NotEqual(
            TimeSpec.Anytime(TestValues.PlanDate),
            TimeSpec.Anytime(TestValues.PlanDate.PlusDays(1)));
        Assert.Equal(
            TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon),
            TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon));
        Assert.Equal(
            TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)),
            TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)));
    }
}
