using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;

namespace ReminNote.Infrastructure.Application;

public sealed class TaskHistoryQueryService : ITaskHistoryQueryService
{
    private readonly ReminNoteDbContext dbContext;

    public TaskHistoryQueryService(ReminNoteDbContext dbContext)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async IAsyncEnumerable<TaskHistoryRecord> ListAsync(
        TaskId taskId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.TaskHistory
            .AsNoTracking()
            .Where(history => history.TaskId == taskId.Value)
            .OrderBy(history => history.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entity.ToRecord();
        }
    }
}
