using NodaTime;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using DomainTask = ReminNote.Core.Tasks.Task;

namespace ReminNote.Core.Application;

/// <summary>
/// Persistence boundary. Implementations own transactions and storage; Core
/// does not know about EF Core, SQLite, or database paths.
/// </summary>
public interface ITaskRepository
{
    ValueTask<DomainTask?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    ValueTask AddAsync(
        DomainTask task,
        CancellationToken cancellationToken = default);

    ValueTask AddAsync(
        DomainTask task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(
        DomainTask task,
        CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(
        DomainTask task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(TaskId id, CancellationToken cancellationToken = default);
}

public sealed record TaskQuery(LocalDate? PlanDate = null);

public interface ITaskQueryService
{
    ValueTask<TaskSnapshot?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default);
}

public enum TaskHistoryKind
{
    ResultRecorded,
    PlanChanged,
    Continued,
    SortOrderChanged,
    ImportedResult
}

/// <summary>
/// An immutable Task snapshot captured at a meaningful P2 change boundary.
/// For PlanChanged the snapshot is the old plan; for ResultRecorded it is the
/// new recorded result. This keeps both facts queryable without overwriting
/// the current Task row.
/// </summary>
public sealed record TaskHistoryRecord
{
    public TaskHistoryRecord(
        TaskId taskId,
        TaskHistoryKind kind,
        Instant occurredAt,
        TaskSnapshot snapshot,
        TaskId? relatedTaskId = null)
    {
        _ = TaskId.From(taskId.Value);
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = TaskId.From(snapshot.Id.Value);

        if (taskId != snapshot.Id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.task_id_mismatch",
                "Task history must identify the same task as its snapshot.",
                nameof(taskId)));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.kind.invalid",
                "Task history kind is not supported.",
                nameof(kind)));
        }

        if (occurredAt < snapshot.CreatedAt)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.occurred_at.before_created_at",
                "Task history cannot occur before task creation.",
                nameof(occurredAt)));
        }

        if (kind == TaskHistoryKind.Continued)
        {
            if (relatedTaskId is not { } relatedId || snapshot.ContinuedFromTaskId != relatedId)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "task.history.continuation_relation.invalid",
                    "A continuation history entry must point to its source task.",
                    nameof(relatedTaskId)));
            }
        }
        else if (relatedTaskId is not null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.related_task.unexpected",
                "Only continuation history may reference a related task.",
                nameof(relatedTaskId)));
        }

        if (relatedTaskId is { } validRelatedId)
        {
            _ = TaskId.From(validRelatedId.Value);
        }

        TaskId = taskId;
        Kind = kind;
        OccurredAt = occurredAt;
        Snapshot = snapshot;
        RelatedTaskId = relatedTaskId;
    }

    public TaskId TaskId { get; }

    public TaskHistoryKind Kind { get; }

    public Instant OccurredAt { get; }

    public TaskSnapshot Snapshot { get; }

    public TaskId? RelatedTaskId { get; }
}

public interface ITaskHistoryQueryService
{
    IAsyncEnumerable<TaskHistoryRecord> ListAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional reminder intent carried by a Task command. The Task aggregate
/// remains reminder-agnostic; the Agent materializes this intent into a
/// ReminderRule and its first Schedule inside the same writer transaction.
/// </summary>
public sealed record ReminderRuleOptions(
    ReminderPurpose Purpose,
    ReminderTiming Timing,
    ReminderPriority Priority = ReminderPriority.NORMAL,
    bool Pinned = false,
    RepeatPolicy? RepeatPolicy = null,
    WakePolicy WakePolicy = WakePolicy.DEFAULT,
    bool Enabled = true)
{
    public RepeatPolicy EffectiveRepeatPolicy =>
        RepeatPolicy ?? global::ReminNote.Core.Reminders.Domain.RepeatPolicy.Disabled;
}

public sealed record CreateTaskCommand(
    string Title,
    TimeSpec TimeSpec,
    ReminderRuleOptions? Reminder = null);

public sealed record UpdateTaskCommand(
    TaskId TaskId,
    string Title,
    TimeSpec TimeSpec,
    ReminderRuleOptions? Reminder = null);

public sealed record RecordTaskResultCommand(
    TaskId TaskId,
    TaskResult Result,
    string? Note = null);

public sealed record ReorderTaskCommand(TaskId TaskId, int SortOrder);

public sealed record ContinueTaskCommand(
    TaskId SourceTaskId,
    string Title,
    TimeSpec TimeSpec,
    ReminderRuleOptions? Reminder = null);

public interface ITaskWriteGate
{
    IDisposable Enter(CancellationToken cancellationToken = default);
}

public sealed class TaskWriteGateBusyException : InvalidOperationException
{
    public TaskWriteGateBusyException()
        : base("Task write gate is busy.")
    {
    }

    public const string Code = "task.write_gate.busy";
}

public sealed class NoopTaskWriteGate : ITaskWriteGate
{
    public IDisposable Enter(CancellationToken cancellationToken = default) =>
        NoopLease.Instance;

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Explicit application boundary for Task CRUD and result recording. A
/// concrete implementation obtains time from Noda Time's IClock and persists
/// through ITaskRepository.
/// </summary>
public interface ITaskApplicationService
{
    ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> ReorderAsync(
        ReorderTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> ContinueAsync(
        ContinueTaskCommand command,
        CancellationToken cancellationToken = default);
}
