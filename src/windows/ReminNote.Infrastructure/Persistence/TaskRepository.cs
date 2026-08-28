using Microsoft.EntityFrameworkCore;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Basic EF-backed Task repository. Each method owns one unit of work and
/// saves immediately, which keeps the P1 CRUD path explicit and easy to move
/// behind the future Agent single-writer boundary.
/// </summary>
public sealed class TaskRepository : ITaskRepository
{
    private readonly ReminNoteDbContext dbContext;

    public TaskRepository(ReminNoteDbContext dbContext)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<TaskAggregate?> FindAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Tasks
            .AsNoTracking()
            .SingleOrDefaultAsync(task => task.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return entity?.ToDomain();
    }

    public ValueTask AddAsync(
        TaskAggregate task,
        CancellationToken cancellationToken = default) =>
        AddAsync(task, history: null, cancellationToken: cancellationToken);

    public async ValueTask AddAsync(
        TaskAggregate task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var taskEntity = TaskEntity.FromDomain(task);
        var historyEntity = CreateHistoryEntity(task, history);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await dbContext.Tasks
                .AddAsync(taskEntity, cancellationToken)
                .ConfigureAwait(false);
            if (historyEntity is not null)
            {
                await dbContext.TaskHistory
                    .AddAsync(historyEntity, cancellationToken)
                    .ConfigureAwait(false);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The transaction is disposed below and rolls back if commit did
            // not complete. Clear tracked state as well, so a caller that
            // reuses this unit of work cannot accidentally save a failed
            // aggregate on a later call.
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public ValueTask UpdateAsync(
        TaskAggregate task,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(task, history: null, cancellationToken: cancellationToken);

    public async ValueTask UpdateAsync(
        TaskAggregate task,
        TaskHistoryRecord? history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var historyEntity = CreateHistoryEntity(task, history);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entity = await dbContext.Tasks
                .SingleOrDefaultAsync(storedTask => storedTask.Id == task.Id.Value, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                throw new KeyNotFoundException($"Task '{task.Id}' does not exist.");
            }

            entity.Apply(task);
            if (historyEntity is not null)
            {
                await dbContext.TaskHistory
                    .AddAsync(historyEntity, cancellationToken)
                    .ConfigureAwait(false);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async ValueTask<bool> DeleteAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entity = await dbContext.Tasks
                .SingleOrDefaultAsync(task => task.Id == id.Value, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            dbContext.Tasks.Remove(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private static TaskHistoryEntity? CreateHistoryEntity(
        TaskAggregate task,
        TaskHistoryRecord? history)
    {
        if (history is null)
        {
            return null;
        }

        if (history.TaskId != task.Id)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.task_id_mismatch",
                "Task history must belong to the persisted task.",
                nameof(history)));
        }

        if (history.Kind != TaskHistoryKind.PlanChanged &&
            history.Snapshot != task.ToSnapshot())
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.history.snapshot_mismatch",
                "Task history snapshot must match the persisted task for this history kind.",
                nameof(history)));
        }

        return TaskHistoryEntity.FromRecord(history);
    }
}
