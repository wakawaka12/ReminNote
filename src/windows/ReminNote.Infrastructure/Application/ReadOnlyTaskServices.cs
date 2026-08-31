using System.Runtime.CompilerServices;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// Read projection used by Main and Widget after the Agent cutover. Every
/// operation opens SQLite with OS read-only mode and connection-local
/// query_only=1; it cannot create a file, migrate a schema or write settings.
/// </summary>
public sealed class ReadOnlyTaskServices :
    ITaskQueryService,
    ITodayQueryService,
    IWorkdaySettingsStore
{
    private readonly string databasePath;
    private readonly IClock clock;
    private readonly IUserTimeZoneProvider timeZoneProvider;

    public ReadOnlyTaskServices(
        string databasePath,
        IClock? clock = null,
        IUserTimeZoneProvider? timeZoneProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.clock = clock ?? SystemClock.Instance;
        this.timeZoneProvider = timeZoneProvider ?? new SystemUserTimeZoneProvider();
    }

    public async ValueTask<TaskSnapshot?> FindAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        using var context = ReminNoteDatabase.CreateContext(connection);
        return await new TaskQueryService(context).FindAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        using var context = ReminNoteDatabase.CreateContext(connection);
        await foreach (var task in new TaskQueryService(context)
            .ListAsync(query, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return task;
        }
    }

    public async ValueTask<TodayReadModel> GetAsync(
        TodayQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        using var context = ReminNoteDatabase.CreateContext(connection);
        var settings = new AppSettingsRepository(context, clock);
        var query = new TaskQueryService(context);
        return await new TodayQueryService(query, timeZoneProvider, settings)
            .GetAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkdaySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        using var context = ReminNoteDatabase.CreateContext(connection);
        return await new AppSettingsRepository(context, clock)
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask SetAsync(
        WorkdaySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        throw new InvalidOperationException(
            "Main and Widget read projections cannot write settings; route settings commands through the Agent.");
    }
}
