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
        TaskResultRecord? resultRecord,
        int sortOrder,
        TaskId? continuedFromTaskId,
        bool enforceTitleLength)
    {
        ValidateTitle(title, enforceTitleLength);
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

        if (sortOrder < 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.sort_order.invalid",
                "Task sort order cannot be negative.",
                nameof(sortOrder)));
        }

        if (continuedFromTaskId == id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.continuation.self_reference",
                "A task cannot continue itself.",
                nameof(continuedFromTaskId)));
        }

        Id = id;
        Title = title.Trim();
        TimeSpec = timeSpec;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ResultRecord = resultRecord;
        SortOrder = sortOrder;
        ContinuedFromTaskId = continuedFromTaskId;
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
    /// Persistent ordering within the same Today date/group. It never changes
    /// the planning time or moves a task between groups.
    /// </summary>
    public int SortOrder { get; private set; }

    public TaskId? ContinuedFromTaskId { get; }

    /// <summary>
    /// Creates a new task with a UUID v7 identity.
    /// </summary>
    public static Task Create(
        string title,
        TimeSpec timeSpec,
        Instant createdAt,
        int sortOrder = 0) =>
        Create(TaskId.New(), title, timeSpec, createdAt, sortOrder);

    public static Task Create(
        TaskId id,
        string title,
        TimeSpec timeSpec,
        Instant createdAt,
        int sortOrder = 0) =>
        new(
            id,
            title,
            timeSpec,
            createdAt,
            createdAt,
            resultRecord: null,
            sortOrder,
            continuedFromTaskId: null,
            enforceTitleLength: true);

    /// <summary>
    /// Creates the only supported kind of continuation: a new plan sourced
    /// from a RANGE task whose current result is PARTIAL.
    /// </summary>
    public static Task CreateContinuation(
        Task source,
        string title,
        TimeSpec timeSpec,
        Instant createdAt,
        int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Result != TaskResult.PARTIAL || source.TimeSpec is not TimeRangeSpec)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.continuation.requires_partial",
                "Only a RANGE task with a PARTIAL result can continue.",
                nameof(source)));
        }

        return new(
            TaskId.New(),
            title,
            timeSpec,
            createdAt,
            createdAt,
            resultRecord: null,
            sortOrder,
            source.Id,
            enforceTitleLength: true);
    }

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
        TaskResultRecord? resultRecord,
        int sortOrder = 0,
        TaskId? continuedFromTaskId = null) =>
        new(
            id,
            title,
            timeSpec,
            createdAt,
            updatedAt,
            resultRecord,
            sortOrder,
            continuedFromTaskId,
            // P1 did not have a title-length rule. Rehydration therefore
            // preserves legacy titles, while every new/renamed value still
            // enforces the current 500-character limit.
            enforceTitleLength: false);

    public void Rename(string title, Instant changedAt)
    {
        // P1 had no title-length rule. A legacy title may therefore be
        // longer than 500 characters; keeping that exact value is not a new
        // title write and must remain possible for later plan/result changes.
        // Any genuinely new or changed title still follows the P2 limit.
        if (string.IsNullOrWhiteSpace(title))
        {
            ValidateTitle(title);
        }

        var normalizedTitle = title.Trim();
        var isUnchangedLegacyTitle = normalizedTitle.Length > 500 &&
            string.Equals(normalizedTitle, Title, StringComparison.Ordinal);
        ValidateTitle(title, enforceLength: !isUnchangedLegacyTitle);
        ValidateChangedAt(changedAt);
        Title = normalizedTitle;
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

        if (ResultRecord is not null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.time_spec.changed_after_result",
                "A task with a recorded result cannot change its planned time.",
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

    public void SetSortOrder(int sortOrder, Instant changedAt)
    {
        if (sortOrder < 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.sort_order.invalid",
                "Task sort order cannot be negative.",
                nameof(sortOrder)));
        }

        ValidateChangedAt(changedAt);
        SortOrder = sortOrder;
        Touch(changedAt);
    }

    public TaskSnapshot ToSnapshot() => new(
        Id,
        Title,
        TimeSpec,
        ResultRecord,
        CreatedAt,
        UpdatedAt,
        SortOrder,
        ContinuedFromTaskId);

    private static void ValidateTitle(string title, bool enforceLength = true)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.title.required",
                "Task title is required.",
                nameof(title)));
        }

        if (enforceLength && title.Trim().Length > 500)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.title.too_long",
                "Task title cannot exceed 500 characters.",
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
    Instant UpdatedAt,
    int SortOrder = 0,
    TaskId? ContinuedFromTaskId = null)
{
    public TaskTimeType TimeType => TimeSpec.Type;

    public TaskResult? Result => ResultRecord?.Result;

    public string? ResultNote => ResultRecord?.Note;

    public Instant? RecordedAt => ResultRecord?.RecordedAt;
}
