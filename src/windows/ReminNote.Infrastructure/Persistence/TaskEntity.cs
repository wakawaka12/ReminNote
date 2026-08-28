using NodaTime;
using ReminNote.Core.Tasks;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Relational representation of the Task aggregate. The civil-time columns
/// are deliberately explicit so SQLite constraints can reject mixed shape
/// states before the value is rehydrated by the domain.
/// </summary>
public sealed class TaskEntity
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public TaskTimeType TimeType { get; set; }

    public LocalDate LocalDate { get; set; }

    public LocalTime? TimePoint { get; set; }

    public LocalTime? RangeStart { get; set; }

    public LocalTime? RangeEnd { get; set; }

    public TaskResult? Result { get; set; }

    public Instant? ResultRecordedAt { get; set; }

    public string? ResultNote { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public int SortOrder { get; set; }

    public Guid? ContinuedFromTaskId { get; set; }

    public static TaskEntity FromDomain(TaskAggregate task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var entity = new TaskEntity
        {
            Id = task.Id.Value,
            Title = task.Title,
            TimeType = task.TimeSpec.Type,
            LocalDate = task.TimeSpec.LocalDate,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            Result = task.Result,
            ResultRecordedAt = task.ResultRecord?.RecordedAt,
            ResultNote = task.ResultRecord?.Note,
            SortOrder = task.SortOrder,
            ContinuedFromTaskId = task.ContinuedFromTaskId?.Value
        };

        switch (task.TimeSpec)
        {
            case AnytimeSpec:
                break;
            case TimePointSpec timePoint:
                entity.TimePoint = timePoint.TimePoint;
                break;
            case TimeRangeSpec range:
                entity.RangeStart = range.RangeStart;
                entity.RangeEnd = range.RangeEnd;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Task time specification: {task.TimeSpec.GetType().Name}.");
        }

        return entity;
    }

    public void Apply(TaskAggregate task)
    {
        var updated = FromDomain(task);

        Id = updated.Id;
        Title = updated.Title;
        TimeType = updated.TimeType;
        LocalDate = updated.LocalDate;
        TimePoint = updated.TimePoint;
        RangeStart = updated.RangeStart;
        RangeEnd = updated.RangeEnd;
        Result = updated.Result;
        ResultRecordedAt = updated.ResultRecordedAt;
        ResultNote = updated.ResultNote;
        CreatedAt = updated.CreatedAt;
        UpdatedAt = updated.UpdatedAt;
        SortOrder = updated.SortOrder;
        ContinuedFromTaskId = updated.ContinuedFromTaskId;
    }

    public TaskAggregate ToDomain()
    {
        var timeSpec = TimeSpec.Create(TimeType, LocalDate, TimePoint, RangeStart, RangeEnd);
        TaskResultRecord? resultRecord = null;

        if (Result is not null)
        {
            if (ResultRecordedAt is null)
            {
                throw new InvalidOperationException(
                    "A stored Task result must have a result-recorded timestamp.");
            }

            resultRecord = TaskResultRecord.Create(Result.Value, ResultRecordedAt.Value, ResultNote);
        }
        else if (ResultRecordedAt is not null || ResultNote is not null)
        {
            throw new InvalidOperationException(
                "A stored Task without a result cannot have result metadata.");
        }

        return TaskAggregate.Rehydrate(
            TaskId.From(Id),
            Title,
            timeSpec,
            CreatedAt,
            UpdatedAt,
            resultRecord,
            SortOrder,
            ContinuedFromTaskId is { } continuedFromTaskId
                ? TaskId.From(continuedFromTaskId)
                : null);
    }
}
