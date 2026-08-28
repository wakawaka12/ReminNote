using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Immutable relational snapshot of a Task at a result/plan/continuation
/// boundary. The current Task row remains the source of current state.
/// </summary>
public sealed class TaskHistoryEntity
{
    public long Id { get; set; }

    public Guid TaskId { get; set; }

    public TaskHistoryKind Kind { get; set; }

    public Instant OccurredAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public TaskTimeType TimeType { get; set; }

    public LocalDate LocalDate { get; set; }

    public LocalTime? TimePoint { get; set; }

    public LocalTime? RangeStart { get; set; }

    public LocalTime? RangeEnd { get; set; }

    public TaskResult? Result { get; set; }

    public Instant? ResultRecordedAt { get; set; }

    public string? ResultNote { get; set; }

    public int SortOrder { get; set; }

    public Guid? ContinuedFromTaskId { get; set; }

    public Guid? RelatedTaskId { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public static TaskHistoryEntity FromRecord(TaskHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var snapshot = NormalizeSnapshot(record.Snapshot);
        var entity = new TaskHistoryEntity
        {
            TaskId = record.TaskId.Value,
            Kind = record.Kind,
            OccurredAt = record.OccurredAt,
            Title = snapshot.Title,
            TimeType = snapshot.TimeSpec.Type,
            LocalDate = snapshot.TimeSpec.LocalDate,
            Result = snapshot.Result,
            ResultRecordedAt = snapshot.ResultRecord?.RecordedAt,
            ResultNote = snapshot.ResultRecord?.Note,
            SortOrder = snapshot.SortOrder,
            ContinuedFromTaskId = snapshot.ContinuedFromTaskId?.Value,
            RelatedTaskId = record.RelatedTaskId?.Value,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt
        };

        switch (snapshot.TimeSpec)
        {
            case AnytimeSpec:
                break;
            case TimePointSpec point:
                entity.TimePoint = point.TimePoint;
                break;
            case TimeRangeSpec range:
                entity.RangeStart = range.RangeStart;
                entity.RangeEnd = range.RangeEnd;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Task time specification: {snapshot.TimeSpec.GetType().Name}.");
        }

        return entity;
    }

    public TaskHistoryRecord ToRecord()
    {
        var timeSpec = TimeSpec.Create(TimeType, LocalDate, TimePoint, RangeStart, RangeEnd);
        TaskResultRecord? resultRecord = null;
        if (Result is { } result)
        {
            if (ResultRecordedAt is not { } recordedAt)
            {
                throw new InvalidOperationException(
                    "A stored Task history result must have a result timestamp.");
            }

            resultRecord = TaskResultRecord.Create(result, recordedAt, ResultNote);
        }

        var snapshot = TaskAggregate.Rehydrate(
                ReminNote.Core.Tasks.TaskId.From(TaskId),
                Title,
                timeSpec,
                CreatedAt,
                UpdatedAt,
                resultRecord,
                SortOrder,
                ContinuedFromTaskId is { } continuedFromTaskId
                    ? ReminNote.Core.Tasks.TaskId.From(continuedFromTaskId)
                    : null)
            .ToSnapshot();

        return new TaskHistoryRecord(
            ReminNote.Core.Tasks.TaskId.From(TaskId),
            Kind,
            OccurredAt,
            snapshot,
            RelatedTaskId is { } relatedTaskId
                ? ReminNote.Core.Tasks.TaskId.From(relatedTaskId)
                : null);
    }

    private static TaskSnapshot NormalizeSnapshot(TaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return TaskAggregate.Rehydrate(
                snapshot.Id,
                snapshot.Title,
                snapshot.TimeSpec,
                snapshot.CreatedAt,
                snapshot.UpdatedAt,
                snapshot.ResultRecord,
                snapshot.SortOrder,
                snapshot.ContinuedFromTaskId)
            .ToSnapshot();
    }
}
