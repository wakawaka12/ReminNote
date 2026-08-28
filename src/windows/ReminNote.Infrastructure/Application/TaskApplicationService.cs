using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// Explicit Task use-case boundary for the P1 local writer. The service owns
/// orchestration and obtains timestamps from the supplied clock; persistence
/// remains behind <see cref="ITaskRepository"/> so this boundary can later be
/// routed through the Agent command path.
/// </summary>
public sealed class TaskApplicationService : ITaskApplicationService
{
    private readonly ITaskRepository taskRepository;
    private readonly IClock clock;
    private readonly ITaskWriteGate writeGate;

    public TaskApplicationService(
        ITaskRepository taskRepository,
        IClock clock,
        ITaskWriteGate? writeGate = null)
    {
        this.taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.writeGate = writeGate ?? new NoopTaskWriteGate();
    }

    public async ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var lease = writeGate.Enter(cancellationToken);

        var task = TaskAggregate.Create(
            command.Title,
            command.TimeSpec,
            clock.GetCurrentInstant());

        await taskRepository.AddAsync(task, cancellationToken: cancellationToken).ConfigureAwait(false);
        return task.ToSnapshot();
    }

    public async ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var lease = writeGate.Enter(cancellationToken);

        var storedTask = await taskRepository
            .FindAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (storedTask is null)
        {
            return null;
        }

        // Work on a rehydrated copy so a failed title/time validation cannot
        // mutate an aggregate returned by a repository implementation that
        // keeps it in memory or tracks it for the duration of the call.
        var updatedTask = CopyOf(storedTask);
        var before = storedTask.ToSnapshot();
        var changedAt = clock.GetCurrentInstant();
        updatedTask.Rename(command.Title, changedAt);
        TaskHistoryRecord? history = null;
        if (updatedTask.TimeSpec != command.TimeSpec)
        {
            updatedTask.ChangeTime(command.TimeSpec, changedAt);
            history = new TaskHistoryRecord(
                updatedTask.Id,
                TaskHistoryKind.PlanChanged,
                changedAt,
                before);
        }

        await taskRepository.UpdateAsync(
                updatedTask,
                history,
                cancellationToken)
            .ConfigureAwait(false);
        return updatedTask.ToSnapshot();
    }

    public async ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var lease = writeGate.Enter(cancellationToken);

        var storedTask = await taskRepository
            .FindAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (storedTask is null)
        {
            return null;
        }

        var updatedTask = CopyOf(storedTask);
        var recordedAt = clock.GetCurrentInstant();
        updatedTask.RecordResult(
            command.Result,
            recordedAt,
            command.Note);

        var history = new TaskHistoryRecord(
            updatedTask.Id,
            TaskHistoryKind.ResultRecorded,
            recordedAt,
            updatedTask.ToSnapshot());

        await taskRepository.UpdateAsync(
                updatedTask,
                history,
                cancellationToken)
            .ConfigureAwait(false);
        return updatedTask.ToSnapshot();
    }

    public async ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default)
    {
        using var lease = writeGate.Enter(cancellationToken);
        return await taskRepository.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TaskSnapshot?> ReorderAsync(
        ReorderTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var lease = writeGate.Enter(cancellationToken);

        var storedTask = await taskRepository
            .FindAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);
        if (storedTask is null)
        {
            return null;
        }

        if (storedTask.SortOrder == command.SortOrder)
        {
            return storedTask.ToSnapshot();
        }

        var updatedTask = CopyOf(storedTask);
        var changedAt = clock.GetCurrentInstant();
        updatedTask.SetSortOrder(command.SortOrder, changedAt);
        var history = new TaskHistoryRecord(
            updatedTask.Id,
            TaskHistoryKind.SortOrderChanged,
            changedAt,
            updatedTask.ToSnapshot());

        await taskRepository.UpdateAsync(
                updatedTask,
                history,
                cancellationToken)
            .ConfigureAwait(false);
        return updatedTask.ToSnapshot();
    }

    public async ValueTask<TaskSnapshot?> ContinueAsync(
        ContinueTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var lease = writeGate.Enter(cancellationToken);

        var source = await taskRepository
            .FindAsync(command.SourceTaskId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        if (source.Result != TaskResult.PARTIAL)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.continuation.requires_partial",
                "Only a RANGE task with a PARTIAL result can continue.",
                nameof(command.SourceTaskId)));
        }

        var createdAt = clock.GetCurrentInstant();
        var continuedTask = TaskAggregate.CreateContinuation(
            source,
            command.Title,
            command.TimeSpec,
            createdAt);
        var history = new TaskHistoryRecord(
            continuedTask.Id,
            TaskHistoryKind.Continued,
            createdAt,
            continuedTask.ToSnapshot(),
            source.Id);

        await taskRepository.AddAsync(
                continuedTask,
                history,
                cancellationToken)
            .ConfigureAwait(false);
        return continuedTask.ToSnapshot();
    }

    private static TaskAggregate CopyOf(TaskAggregate task) =>
        TaskAggregate.Rehydrate(
            task.Id,
            task.Title,
            task.TimeSpec,
            task.CreatedAt,
            task.UpdatedAt,
            task.ResultRecord,
            task.SortOrder,
            task.ContinuedFromTaskId);
}
