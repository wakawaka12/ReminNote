using Microsoft.EntityFrameworkCore;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Repository for an already-open transaction. It deliberately never starts
/// or commits a transaction; P2.5StorageWriter owns that boundary and commits
/// the domain rows, journal and receipt as one unit.
/// </summary>
public sealed class TransactionBoundTaskRepository : ITaskRepository
{
    private readonly ReminNoteDbContext dbContext;

    public TransactionBoundTaskRepository(ReminNoteDbContext dbContext)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<TaskAggregate?> FindAsync(TaskId id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Tasks
            .AsNoTracking()
            .SingleOrDefaultAsync(task => task.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);
        return entity?.ToDomain();
    }

    public ValueTask AddAsync(TaskAggregate task, CancellationToken cancellationToken = default) =>
        AddAsync(task, history: null, cancellationToken);

    public async ValueTask AddAsync(
        TaskAggregate task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await dbContext.Tasks.AddAsync(TaskEntity.FromDomain(task), cancellationToken).ConfigureAwait(false);
        if (history is not null)
        {
            await dbContext.TaskHistory.AddAsync(ToHistoryEntity(task, history), cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask UpdateAsync(TaskAggregate task, CancellationToken cancellationToken = default) =>
        UpdateAsync(task, history: null, cancellationToken);

    public async ValueTask UpdateAsync(
        TaskAggregate task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var entity = await dbContext.Tasks
            .SingleOrDefaultAsync(storedTask => storedTask.Id == task.Id.Value, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException($"Task '{task.Id}' does not exist.");
        }

        entity.Apply(task);
        if (history is not null)
        {
            await dbContext.TaskHistory.AddAsync(ToHistoryEntity(task, history), cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(TaskId id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Tasks
            .SingleOrDefaultAsync(task => task.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        dbContext.Tasks.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static TaskHistoryEntity ToHistoryEntity(TaskAggregate task, TaskHistoryRecord history)
    {
        if (history.TaskId != task.Id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.task_id_mismatch",
                "Task history must belong to the persisted task.",
                nameof(history)));
        }

        if (history.Kind != TaskHistoryKind.PlanChanged && history.Snapshot != task.ToSnapshot())
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.snapshot_mismatch",
                "Task history snapshot must match the persisted task for this history kind.",
                nameof(history)));
        }

        return TaskHistoryEntity.FromRecord(history);
    }
}
