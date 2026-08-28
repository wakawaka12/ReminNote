using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Tasks;

/// <summary>
/// The Task aggregate for planning and recording a user-confirmed result.
/// It deliberately has no elapsed-work or real-completion-time fields.
/// </summary>
public sealed class Task
{
    private Task(
        TaskId id,
        string title,
        TimeSpec timeSpec,
        Instant createdAt,
        Instant updatedAt,
        TaskResultRecord? resultRecord)
    {
        ValidateTitle(title);
        if (timeSpec is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.time_spec.required",
                "Task time specification is required.",
                nameof(timeSpec)));
        }

        // A default TaskId is possible because TaskId is a value type. Re-run
        // its invariant at the aggregate boundary instead of allowing it into
        // a new or rehydrated task.
        _ = TaskId.From(id.Value);

        if (updatedAt < createdAt)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.updated_at.before_created_at",
                "UpdatedAt cannot be earlier than CreatedAt.",
                nameof(updatedAt)));
        }

        ValidateResult(timeSpec, resultRecord);

        if (resultRecord is not null && resultRecord.RecordedAt < createdAt)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.result.recorded_at.before_created_at",
                "RecordedAt cannot be earlier than CreatedAt.",
                nameof(resultRecord)));
        }

        if (resultRecord is not null && resultRecord.RecordedAt > updatedAt)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.result.recorded_at.after_updated_at",
                "UpdatedAt cannot be earlier than RecordedAt.",
                nameof(updatedAt)));
        }

        Id = id;
        Title = title.Trim();
        TimeSpec = timeSpec;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ResultRecord = resultRecord;
    }

    public TaskId Id { get; }

    public string Title { get; private set; }

    public TimeSpec TimeSpec { get; private set; }

    public TaskTimeType TimeType => TimeSpec.Type;

    public Instant CreatedAt { get; }

    public Instant UpdatedAt { get; private set; }

    public TaskResultRecord? ResultRecord { get; private set; }

    public TaskResult? Result => ResultRecord?.Result;

    public string? ResultNote => ResultRecord?.Note;

    public Instant? RecordedAt => ResultRecord?.RecordedAt;

    /// <summary>
    /// Creates a new task with a UUID v7 identity.
    /// </summary>
    public static Task Create(string title, TimeSpec timeSpec, Instant createdAt) =>
        Create(TaskId.New(), title, timeSpec, createdAt);

    public static Task Create(TaskId id, string title, TimeSpec timeSpec, Instant createdAt) =>
        new(id, title, timeSpec, createdAt, createdAt, resultRecord: null);

    /// <summary>
    /// Rehydrates an aggregate without changing the stored identity or
    /// result-record timestamp.
    /// </summary>
    public static Task Rehydrate(
        TaskId id,
        string title,
        TimeSpec timeSpec,
        Instant createdAt,
        Instant updatedAt,
        TaskResultRecord? resultRecord) =>
        new(id, title, timeSpec, createdAt, updatedAt, resultRecord);

    public void Rename(string title, Instant changedAt)
    {
        ValidateTitle(title);
        ValidateChangedAt(changedAt);
        Title = title.Trim();
        Touch(changedAt);
    }

    public void ChangeTime(TimeSpec timeSpec, Instant changedAt)
    {
        if (timeSpec is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.time_spec.required",
                "Task time specification is required.",
                nameof(timeSpec)));
        }

        ValidateResult(timeSpec, ResultRecord);
        ValidateChangedAt(changedAt);
        TimeSpec = timeSpec;
        Touch(changedAt);
    }

    /// <summary>
    /// Records a user decision. A missing result remains missing; it is never
    /// inferred as MISSED when a range ends.
    /// </summary>
    public void RecordResult(TaskResult result, Instant recordedAt, string? note = null)
    {
        ValidateResult(TimeSpec, result);
        ValidateChangedAt(recordedAt);
        ResultRecord = TaskResultRecord.Create(result, recordedAt, note);
        Touch(recordedAt);
    }

    public TaskSnapshot ToSnapshot() => new(
        Id,
        Title,
        TimeSpec,
        ResultRecord,
        CreatedAt,
        UpdatedAt);

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.title.required",
                "Task title is required.",
                nameof(title)));
        }
    }

    private static void ValidateResult(TimeSpec timeSpec, TaskResultRecord? resultRecord)
    {
        if (resultRecord is not null)
        {
            ValidateResult(timeSpec, resultRecord.Result);
        }
    }

    private static void ValidateResult(TimeSpec timeSpec, TaskResult result)
    {
        if (result is not (TaskResult.COMPLETED or TaskResult.MISSED or TaskResult.PARTIAL))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.result.invalid",
                "Task result is not supported.",
                nameof(result)));
        }

        if (result == TaskResult.PARTIAL && timeSpec.Type != TaskTimeType.RANGE)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.result.partial_requires_range",
                "PARTIAL is valid only for a RANGE task.",
                nameof(result)));
        }
    }

    private void Touch(Instant changedAt)
    {
        ValidateChangedAt(changedAt);
        UpdatedAt = changedAt;
    }

    private void ValidateChangedAt(Instant changedAt)
    {
        if (changedAt < CreatedAt)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.changed_at.before_created_at",
                "A task change cannot be recorded before task creation.",
                nameof(changedAt)));
        }
    }
}

/// <summary>
/// Immutable read model shared by query/application boundaries.
/// </summary>
public sealed record TaskSnapshot(
    TaskId Id,
    string Title,
    TimeSpec TimeSpec,
    TaskResultRecord? ResultRecord,
    Instant CreatedAt,
    Instant UpdatedAt)
{
    public TaskTimeType TimeType => TimeSpec.Type;

    public TaskResult? Result => ResultRecord?.Result;

    public string? ResultNote => ResultRecord?.Note;

    public Instant? RecordedAt => ResultRecord?.RecordedAt;
}
