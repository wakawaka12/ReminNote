using NodaTime;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Today;

public enum TodayTaskGroup
{
    OVERDUE,
    MORNING,
    AFTERNOON,
    EVENING,
    ANYTIME,
    COMPLETED
}

public enum TodayTaskStatus
{
    OVERDUE,
    PLANNED,
    UPCOMING,
    AwaitingResult,
    COMPLETED
}

public sealed record TodayQueryRequest(
    Instant Now,
    LocalDate? Workday = null);

public sealed record TodayTaskReadModel(
    TaskSnapshot Task,
    TodayTaskGroup Group,
    TodayTaskStatus Status,
    bool IsNeedsReview)
{
    public bool IsCompleted => Task.Result is not null;

    /// <summary>
    /// A RANGE that ended without a result is overdue for a user decision even
    /// though it remains in its original planned time group.
    /// </summary>
    public bool IsOverdue => Status is TodayTaskStatus.OVERDUE or TodayTaskStatus.AwaitingResult;

    public bool IsRange => Task.TimeSpec is TimeRangeSpec;

    public bool IsCrossMidnight =>
        Task.TimeSpec is TimeRangeSpec range && range.IsCrossMidnight;
}

public sealed record TodayReadModel(
    LocalDate Workday,
    IReadOnlyList<TodayTaskReadModel> Tasks)
{
    public int OpenTaskCount => Tasks.Count(task => !task.IsCompleted);

    public int CompletedTaskCount => Tasks.Count(task => task.IsCompleted);

    public int NeedsReviewCount => Tasks.Count(task => task.IsNeedsReview);

    public IReadOnlyList<TodayTaskReadModel> NeedsReview =>
        Tasks.Where(task => task.IsNeedsReview).ToArray();
}

public interface ITodayQueryService
{
    ValueTask<TodayReadModel> GetAsync(
        TodayQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure Today classifier. Query adapters decide which rows are in scope and
/// call this classifier with the local civil time for the selected workday.
/// </summary>
public static class TodayTaskClassifier
{
    public static TodayTaskReadModel Classify(
        TaskSnapshot task,
        LocalDate workday,
        LocalDateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Result is not null)
        {
            return new TodayTaskReadModel(
                task,
                TodayTaskGroup.COMPLETED,
                TodayTaskStatus.COMPLETED,
                IsNeedsReview: false);
        }

        if (task.TimeSpec.LocalDate > workday)
        {
            throw new ArgumentOutOfRangeException(
                nameof(task),
                "Today cannot classify a task after the selected workday.");
        }

        return task.TimeSpec switch
        {
            AnytimeSpec => new TodayTaskReadModel(
                task,
                task.TimeSpec.LocalDate < workday
                    ? TodayTaskGroup.OVERDUE
                    : TodayTaskGroup.ANYTIME,
                task.TimeSpec.LocalDate < workday
                    ? TodayTaskStatus.OVERDUE
                    : TodayTaskStatus.PLANNED,
                IsNeedsReview: false),
            TimePointSpec point => ClassifyPoint(task, point, workday, localNow),
            TimeRangeSpec range => ClassifyRange(task, range, localNow),
            _ => throw new InvalidOperationException(
                $"Unsupported Task time specification: {task.TimeSpec.GetType().Name}.")
        };
    }

    public static TodayTaskGroup GroupForTime(LocalTime time) =>
        time.Hour < 12
            ? TodayTaskGroup.MORNING
            : time.Hour < 18
                ? TodayTaskGroup.AFTERNOON
                : TodayTaskGroup.EVENING;

    private static TodayTaskReadModel ClassifyPoint(
        TaskSnapshot task,
        TimePointSpec point,
        LocalDate workday,
        LocalDateTime localNow)
    {
        var overdue = task.TimeSpec.LocalDate < workday ||
            localNow > point.LocalDateTime;
        return new TodayTaskReadModel(
            task,
            overdue ? TodayTaskGroup.OVERDUE : GroupForTime(point.TimePoint),
            overdue ? TodayTaskStatus.OVERDUE : TodayTaskStatus.UPCOMING,
            IsNeedsReview: false);
    }

    private static TodayTaskReadModel ClassifyRange(
        TaskSnapshot task,
        TimeRangeSpec range,
        LocalDateTime localNow)
    {
        if (localNow >= range.EndLocalDateTime)
        {
            return new TodayTaskReadModel(
                task,
                // A finished RANGE remains in the original planned position;
                // AWAITING RESULT and IsNeedsReview carry its overdue state.
                GroupForTime(range.RangeStart),
                TodayTaskStatus.AwaitingResult,
                IsNeedsReview: true);
        }

        return new TodayTaskReadModel(
            task,
            GroupForTime(range.RangeStart),
            localNow >= range.StartLocalDateTime
                ? TodayTaskStatus.PLANNED
                : TodayTaskStatus.UPCOMING,
            IsNeedsReview: false);
    }
}
