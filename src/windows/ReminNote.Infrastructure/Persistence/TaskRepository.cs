using Microsoft.EntityFrameworkCore;
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

    public async ValueTask AddAsync(
        TaskAggregate task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        await dbContext.Tasks
            .AddAsync(TaskEntity.FromDomain(task), cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(
        TaskAggregate task,
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
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
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
}
