using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Tasks;

/// <summary>
/// Immutable local-civil planning time. It intentionally contains no UTC
/// conversion and no reminder semantics.
/// </summary>
public abstract record TimeSpec
{
    internal TimeSpec(TaskTimeType type, LocalDate localDate)
    {
        Type = type;
        LocalDate = localDate;
    }

    public TaskTimeType Type { get; }

    public TaskTimeType TimeType => Type;

    /// <summary>
    /// The calendar date that owns the plan. For a cross-midnight range this
    /// remains the date on which the range starts.
    /// </summary>
    public LocalDate LocalDate { get; }

    public static AnytimeSpec Anytime(LocalDate localDate) => new(localDate);

    public static TimePointSpec At(LocalDate localDate, LocalTime timePoint) => new(localDate, timePoint);

    public static TimeRangeSpec Range(LocalDate localDate, LocalTime rangeStart, LocalTime rangeEnd) =>
        new(localDate, rangeStart, rangeEnd);

    /// <summary>
    /// Reconstructs a time shape from the relational columns used by the
    /// persistence layer. Mixed column states are rejected deterministically.
    /// </summary>
    public static TimeSpec Create(
        TaskTimeType type,
        LocalDate localDate,
        LocalTime? timePoint,
        LocalTime? rangeStart,
        LocalTime? rangeEnd)
    {
        return type switch
        {
            TaskTimeType.ANYTIME when timePoint is null && rangeStart is null && rangeEnd is null =>
                new AnytimeSpec(localDate),
            TaskTimeType.TIME when timePoint is not null && rangeStart is null && rangeEnd is null =>
                new TimePointSpec(localDate, timePoint.Value),
            TaskTimeType.RANGE when timePoint is null && rangeStart is not null && rangeEnd is not null =>
                new TimeRangeSpec(localDate, rangeStart.Value, rangeEnd.Value),
            TaskTimeType.ANYTIME or TaskTimeType.TIME or TaskTimeType.RANGE =>
                throw MixedState(type),
            _ => throw new DomainValidationException(new DomainValidationError(
                "task.time_type.invalid",
                "Task time type is not supported.",
                nameof(type)))
        };
    }

    private static DomainValidationException MixedState(TaskTimeType type) =>
        new(new DomainValidationError(
            "task.time_spec.mixed_columns",
            $"Columns do not match the {type} time shape.",
            nameof(type)));
}

public sealed record AnytimeSpec(LocalDate LocalDate) : TimeSpec(TaskTimeType.ANYTIME, LocalDate);

public sealed record TimePointSpec(LocalDate LocalDate, LocalTime TimePoint)
    : TimeSpec(TaskTimeType.TIME, LocalDate)
{
    public LocalTime Time => TimePoint;

    public LocalDateTime LocalDateTime => LocalDate.At(TimePoint);
}

public sealed record TimeRangeSpec : TimeSpec
{
    public TimeRangeSpec(LocalDate localDate, LocalTime rangeStart, LocalTime rangeEnd)
        : base(TaskTimeType.RANGE, localDate)
    {
        if (rangeStart == rangeEnd)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.time_range.zero_duration",
                "Range start and end must be different.",
                nameof(rangeStart)));
        }

        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
    }

    public LocalTime RangeStart { get; }

    public LocalTime RangeEnd { get; }

    public LocalTime Start => RangeStart;

    public LocalTime End => RangeEnd;

    /// <summary>
    /// Cross-midnight is derived from the local clock ordering. It is not a
    /// separately persisted flag.
    /// </summary>
    public bool IsCrossMidnight => RangeEnd.CompareTo(RangeStart) < 0;

    public LocalDate EndLocalDate => IsCrossMidnight ? LocalDate.PlusDays(1) : LocalDate;

    public LocalDateTime StartLocalDateTime => LocalDate.At(RangeStart);

    public LocalDateTime EndLocalDateTime => EndLocalDate.At(RangeEnd);

    public Duration Duration
    {
        get
        {
            const long nanosecondsPerDay = 86_400_000_000_000;
            var start = RangeStart.NanosecondOfDay;
            var end = RangeEnd.NanosecondOfDay;
            var elapsed = IsCrossMidnight ? nanosecondsPerDay - start + end : end - start;
            return Duration.FromNanoseconds(elapsed);
        }
    }
}
