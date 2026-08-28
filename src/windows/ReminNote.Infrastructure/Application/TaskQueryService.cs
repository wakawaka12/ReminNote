using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// Read-only Task query adapter. EF entities are materialized without
/// tracking and converted to Core snapshots before they leave Infrastructure.
/// </summary>
public sealed class TaskQueryService : ITaskQueryService
{
    private readonly ReminNoteDbContext dbContext;

    public TaskQueryService(ReminNoteDbContext dbContext)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<TaskSnapshot?> FindAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Tasks
            .AsNoTracking()
            .SingleOrDefaultAsync(task => task.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return entity?.ToDomain().ToSnapshot();
    }

    public async IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var entities = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.LocalDate == query.PlanDate)
            .OrderBy(task => task.TimeType)
            .ThenBy(task => task.TimePoint)
            .ThenBy(task => task.RangeStart)
            .ThenBy(task => task.RangeEnd)
            .ThenBy(task => task.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entity.ToDomain().ToSnapshot();
        }
    }
}
