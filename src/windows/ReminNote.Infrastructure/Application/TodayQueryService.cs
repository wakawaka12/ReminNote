using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Core.Tasks;

namespace ReminNote.Infrastructure.Application;

public sealed class TodayQueryService : ITodayQueryService
{
    private readonly ITaskQueryService taskQueryService;
    private readonly IUserTimeZoneProvider timeZoneProvider;
    private readonly IWorkdaySettingsStore settingsStore;

    public TodayQueryService(
        ITaskQueryService taskQueryService,
        IUserTimeZoneProvider timeZoneProvider,
        IWorkdaySettingsStore settingsStore)
    {
        this.taskQueryService = taskQueryService ?? throw new ArgumentNullException(nameof(taskQueryService));
        this.timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public async ValueTask<TodayReadModel> GetAsync(
        TodayQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var localNow = request.Now.InZone(timeZoneProvider.TimeZone).LocalDateTime;
        var workday = request.Workday;
        if (workday is null)
        {
            var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            workday = new WorkdayService(timeZoneProvider.TimeZone, settings.Boundary)
                .GetWorkday(request.Now);
        }

        var tasks = new List<TodayTaskReadModel>();
        await foreach (var task in taskQueryService
            .ListAsync(new TaskQuery(), cancellationToken)
            .ConfigureAwait(false))
        {
            var planDate = task.TimeSpec.LocalDate;
            if (planDate > workday.Value)
            {
                continue;
            }

            // Completed historical plans remain in history/query views, not
            // in the active Today loop. A result planned for this workday is
            // still visible in COMPLETED.
            if (task.Result is not null && planDate != workday.Value)
            {
                continue;
            }

            tasks.Add(TodayTaskClassifier.Classify(task, workday.Value, localNow));
        }

        var ordered = tasks
            .OrderBy(task => GroupOrder(task.Group))
            // SortOrder is only meaningful within one planned date/group. The
            // date key keeps older overdue plans in chronological order before
            // applying that user-controlled order.
            .ThenBy(task => task.Task.TimeSpec.LocalDate)
            .ThenBy(task => task.Task.SortOrder)
            .ThenBy(task => TimeOrder(task.Task.TimeSpec))
            .ThenBy(task => task.Task.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

        return new TodayReadModel(workday.Value, ordered);
    }

    private static int GroupOrder(TodayTaskGroup group) => group switch
    {
        TodayTaskGroup.OVERDUE => 0,
        TodayTaskGroup.MORNING => 1,
        TodayTaskGroup.AFTERNOON => 2,
        TodayTaskGroup.EVENING => 3,
        TodayTaskGroup.ANYTIME => 4,
        TodayTaskGroup.COMPLETED => 5,
        _ => 99
    };

    private static long TimeOrder(TimeSpec timeSpec) => timeSpec switch
    {
        TimePointSpec point => point.TimePoint.NanosecondOfDay,
        TimeRangeSpec range => range.RangeStart.NanosecondOfDay,
        _ => long.MaxValue
    };
}
