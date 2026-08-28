using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Time;

namespace ReminNote.Infrastructure.Persistence;

public sealed class AppSettingsRepository : IWorkdaySettingsStore
{
    private readonly ReminNoteDbContext dbContext;
    private readonly IClock clock;

    public AppSettingsRepository(ReminNoteDbContext dbContext, IClock clock)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<WorkdaySettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AppSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == 1, cancellationToken)
            .ConfigureAwait(false);

        return entity is null
            ? WorkdaySettings.Default
            : new WorkdaySettings(entity.WorkdayBoundaryMinutes);
    }

    public async ValueTask SetAsync(
        WorkdaySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var entity = await dbContext.AppSettings
            .SingleOrDefaultAsync(value => value.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new AppSettingsEntity { Id = 1 };
            await dbContext.AppSettings.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        entity.WorkdayBoundaryMinutes = settings.BoundaryMinutes;
        entity.UpdatedAt = clock.GetCurrentInstant();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
