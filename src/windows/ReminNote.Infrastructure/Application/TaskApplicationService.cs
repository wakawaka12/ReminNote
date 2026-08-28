using NodaTime;
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

    public TaskApplicationService(ITaskRepository taskRepository, IClock clock)
    {
        this.taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var task = TaskAggregate.Create(
            command.Title,
            command.TimeSpec,
            clock.GetCurrentInstant());

        await taskRepository.AddAsync(task, cancellationToken).ConfigureAwait(false);
        return task.ToSnapshot();
    }

    public async ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

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
        var changedAt = clock.GetCurrentInstant();
        updatedTask.Rename(command.Title, changedAt);
        updatedTask.ChangeTime(command.TimeSpec, changedAt);

        await taskRepository.UpdateAsync(updatedTask, cancellationToken).ConfigureAwait(false);
        return updatedTask.ToSnapshot();
    }

    public async ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storedTask = await taskRepository
            .FindAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (storedTask is null)
        {
            return null;
        }

        var updatedTask = CopyOf(storedTask);
        updatedTask.RecordResult(
            command.Result,
            clock.GetCurrentInstant(),
            command.Note);

        await taskRepository.UpdateAsync(updatedTask, cancellationToken).ConfigureAwait(false);
        return updatedTask.ToSnapshot();
    }

    public ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default) =>
        taskRepository.DeleteAsync(taskId, cancellationToken);

    private static TaskAggregate CopyOf(TaskAggregate task) =>
        TaskAggregate.Rehydrate(
            task.Id,
            task.Title,
            task.TimeSpec,
            task.CreatedAt,
            task.UpdatedAt,
            task.ResultRecord);
}
