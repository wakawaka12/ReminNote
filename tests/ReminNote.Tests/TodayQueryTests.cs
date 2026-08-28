using System.Runtime.CompilerServices;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Infrastructure.Application;
using SystemTask = System.Threading.Tasks.Task;
using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Tests;

public sealed class TodayQueryTests
{
    [Fact]
    public void WorkdayBoundaryUsesPreviousDateBeforeBoundaryAndCurrentDateAtBoundary()
    {
        var zone = DateTimeZoneProviders.Tzdb["Asia/Shanghai"];
        var service = new WorkdayService(zone, new LocalTime(2, 0));
        var date = new LocalDate(2026, 8, 28);

        Assert.Equal(
            date.PlusDays(-1),
            service.GetWorkday(date.At(new LocalTime(1, 59, 59))));
        Assert.Equal(
            date,
            service.GetWorkday(date.At(new LocalTime(2, 0))));
        Assert.Equal(
            date,
            service.GetWorkday(date.At(new LocalTime(23, 59))));
    }

    [Fact]
    public async SystemTask QueryConvertsInstantThroughTheUserTimeZoneBeforeResolvingWorkday()
    {
        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var localDate = new LocalDate(2026, 8, 27);
        var task = Snapshot(
            1,
            "纽约晚间计划",
            TimeSpec.At(localDate, new LocalTime(23, 45)));
        var futureCalendarDateTask = Snapshot(
            2,
            "UTC 日期不应泄漏",
            TimeSpec.Anytime(localDate.PlusDays(1)));
        var service = CreateService(
            [task, futureCalendarDateTask],
            zone,
            new WorkdaySettings(2 * 60));

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 28, 3, 30)),
            TestContext.Current.CancellationToken);

        Assert.Equal(localDate, readModel.Workday);
        var item = Assert.Single(readModel.Tasks);
        Assert.Equal(task.Id, item.Task.Id);
        Assert.Equal(TodayTaskGroup.EVENING, item.Group);
        Assert.Equal(TodayTaskStatus.UPCOMING, item.Status);
    }

    [Fact]
    public async SystemTask QueryClassifiesPastCurrentAndFuturePlans()
    {
        var date = new LocalDate(2026, 8, 28);
        var pastAnytime = Snapshot(1, "历史任意计划", TimeSpec.Anytime(date.PlusDays(-1)));
        var pastTime = Snapshot(2, "已过时间点", TimeSpec.At(date, new LocalTime(14, 0)));
        var exactTime = Snapshot(3, "当前时间点", TimeSpec.At(date, new LocalTime(15, 0)));
        var currentRange = Snapshot(
            4,
            "当前范围",
            TimeSpec.Range(date, new LocalTime(14, 0), new LocalTime(16, 0)));
        var futureRange = Snapshot(
            5,
            "未来范围",
            TimeSpec.Range(date, new LocalTime(16, 0), new LocalTime(17, 0)));
        var futureDate = Snapshot(6, "明日计划", TimeSpec.Anytime(date.PlusDays(1)));
        var service = CreateService(
            [pastAnytime, pastTime, exactTime, currentRange, futureRange, futureDate],
            DateTimeZone.Utc,
            WorkdaySettings.Default);

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 28, 15, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(date, readModel.Workday);
        Assert.DoesNotContain(readModel.Tasks, item => item.Task.Id == futureDate.Id);

        AssertItem(readModel, pastAnytime, TodayTaskGroup.OVERDUE, TodayTaskStatus.OVERDUE, false);
        AssertItem(readModel, pastTime, TodayTaskGroup.OVERDUE, TodayTaskStatus.OVERDUE, false);
        AssertItem(readModel, exactTime, TodayTaskGroup.AFTERNOON, TodayTaskStatus.UPCOMING, false);
        AssertItem(readModel, currentRange, TodayTaskGroup.AFTERNOON, TodayTaskStatus.PLANNED, false);
        AssertItem(readModel, futureRange, TodayTaskGroup.AFTERNOON, TodayTaskStatus.UPCOMING, false);
    }

    [Fact]
    public async SystemTask CrossMidnightRangeRemainsVisibleInOriginalSlotAfterCalendarMidnight()
    {
        var planDate = new LocalDate(2026, 8, 28);
        var range = Snapshot(
            1,
            "跨午夜计划",
            TimeSpec.Range(planDate, new LocalTime(23, 0), new LocalTime(1, 0)));
        var service = CreateService([range], DateTimeZone.Utc, WorkdaySettings.Default);

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 29, 0, 30)),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(readModel.Tasks);
        Assert.Equal(new LocalDate(2026, 8, 29), readModel.Workday);
        Assert.Equal(planDate, item.Task.TimeSpec.LocalDate);
        Assert.Equal(TodayTaskGroup.EVENING, item.Group);
        Assert.Equal(TodayTaskStatus.PLANNED, item.Status);
        Assert.False(item.IsNeedsReview);
        Assert.True(item.IsCrossMidnight);

        var endedReadModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 29, 1, 0)),
            TestContext.Current.CancellationToken);

        var ended = Assert.Single(endedReadModel.Tasks);
        Assert.Equal(TodayTaskGroup.EVENING, ended.Group);
        Assert.Equal(TodayTaskStatus.AwaitingResult, ended.Status);
        Assert.True(ended.IsNeedsReview);
        Assert.Equal(1, endedReadModel.NeedsReviewCount);
    }

    [Fact]
    public async SystemTask EndedRangeAwaitsResultAndKeepsItsPlannedPosition()
    {
        var planDate = new LocalDate(2026, 8, 28);
        var range = Snapshot(
            1,
            "等待复盘的范围",
            TimeSpec.Range(planDate, new LocalTime(10, 0), new LocalTime(11, 0)));
        var service = CreateService([range], DateTimeZone.Utc, WorkdaySettings.Default);

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 28, 11, 0)),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(readModel.Tasks);
        Assert.Equal(TodayTaskGroup.MORNING, item.Group);
        Assert.Equal(TodayTaskStatus.AwaitingResult, item.Status);
        Assert.True(item.IsNeedsReview);
        Assert.True(item.IsOverdue);
        Assert.Equal(1, readModel.NeedsReviewCount);
        Assert.Same(item, Assert.Single(readModel.NeedsReview));
        Assert.Equal(1, readModel.OpenTaskCount);
        Assert.Equal(0, readModel.CompletedTaskCount);
    }

    [Fact]
    public async SystemTask CompletedPlansUseCompletedGroupAndHistoricalCompletedPlansStayOutOfToday()
    {
        var date = new LocalDate(2026, 8, 28);
        var completedToday = Snapshot(
            1,
            "今日已完成",
            TimeSpec.At(date, new LocalTime(9, 0)),
            result: TaskResult.COMPLETED);
        var completedRange = Snapshot(
            2,
            "今日范围已完成",
            TimeSpec.Range(date, new LocalTime(10, 0), new LocalTime(11, 0)),
            result: TaskResult.PARTIAL);
        var completedHistory = Snapshot(
            3,
            "历史已完成",
            TimeSpec.Anytime(date.PlusDays(-1)),
            result: TaskResult.COMPLETED);
        var unfinishedHistory = Snapshot(
            4,
            "历史未完成",
            TimeSpec.Anytime(date.PlusDays(-1)));
        var service = CreateService(
            [completedToday, completedRange, completedHistory, unfinishedHistory],
            DateTimeZone.Utc,
            WorkdaySettings.Default);

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 28, 12, 0)),
            TestContext.Current.CancellationToken);

        Assert.Contains(readModel.Tasks, item => item.Task.Id == completedToday.Id);
        Assert.Contains(readModel.Tasks, item => item.Task.Id == completedRange.Id);
        Assert.Contains(readModel.Tasks, item => item.Task.Id == unfinishedHistory.Id);
        Assert.DoesNotContain(readModel.Tasks, item => item.Task.Id == completedHistory.Id);

        AssertItem(readModel, completedToday, TodayTaskGroup.COMPLETED, TodayTaskStatus.COMPLETED, false);
        AssertItem(readModel, completedRange, TodayTaskGroup.COMPLETED, TodayTaskStatus.COMPLETED, false);
        AssertItem(readModel, unfinishedHistory, TodayTaskGroup.OVERDUE, TodayTaskStatus.OVERDUE, false);
        Assert.Equal(2, readModel.CompletedTaskCount);
    }

    [Fact]
    public async SystemTask StableOrderUsesGroupDateSortOrderTimeAndIdWithoutMovingReviewItemsToTheFront()
    {
        var date = new LocalDate(2026, 8, 28);
        var overdue = Snapshot(
            1,
            "较早逾期",
            TimeSpec.At(date.PlusDays(-1), new LocalTime(20, 0)));
        var morningSortOne = Snapshot(
            2,
            "上午排序一",
            TimeSpec.At(date, new LocalTime(11, 30)),
            sortOrder: 1);
        var morningSameSortAndTime = Snapshot(
            3,
            "上午同序",
            TimeSpec.At(date, new LocalTime(11, 30)),
            sortOrder: 1);
        var morningSortZero = Snapshot(
            4,
            "上午排序零",
            TimeSpec.At(date, new LocalTime(11, 0)),
            sortOrder: 0);
        var review = Snapshot(
            5,
            "上午待复盘",
            TimeSpec.Range(date, new LocalTime(10, 0), new LocalTime(11, 0)),
            sortOrder: 2);
        var service = CreateService(
            [review, morningSameSortAndTime, overdue, morningSortOne, morningSortZero],
            DateTimeZone.Utc,
            WorkdaySettings.Default);

        var readModel = await service.GetAsync(
            new TodayQueryRequest(Instant.FromUtc(2026, 8, 28, 11, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [overdue.Id, morningSortZero.Id, morningSortOne.Id, morningSameSortAndTime.Id, review.Id],
            readModel.Tasks.Select(item => item.Task.Id));
        Assert.Equal(TodayTaskGroup.OVERDUE, readModel.Tasks[0].Group);
        Assert.Equal(TodayTaskGroup.MORNING, readModel.Tasks[1].Group);
        Assert.True(readModel.Tasks[^1].IsNeedsReview);
    }

    [Theory]
    [InlineData(11, 59, "MORNING")]
    [InlineData(12, 0, "AFTERNOON")]
    [InlineData(17, 59, "AFTERNOON")]
    [InlineData(18, 0, "EVENING")]
    public void TimeGroupsUseTheFrozenHalfOpenBoundaries(int hour, int minute, string expected)
    {
        var group = TodayTaskClassifier.GroupForTime(new LocalTime(hour, minute));

        Assert.Equal(Enum.Parse<TodayTaskGroup>(expected), group);
    }

    private static TodayQueryService CreateService(
        IEnumerable<TaskSnapshot> tasks,
        DateTimeZone timeZone,
        WorkdaySettings settings) =>
        new(
            new InMemoryTaskQueryService(tasks.ToArray()),
            new FixedTimeZoneProvider(timeZone),
            new FixedWorkdaySettingsStore(settings));

    private static TaskSnapshot Snapshot(
        int id,
        string title,
        TimeSpec timeSpec,
        int sortOrder = 0,
        TaskResult? result = null)
    {
        var task = TaskAggregate.Create(
            TaskId(id),
            title,
            timeSpec,
            TestValues.CreatedAt,
            sortOrder);
        if (result is { } recordedResult)
        {
            task.RecordResult(recordedResult, TestValues.ChangedAt);
        }

        return task.ToSnapshot();
    }

    private static void AssertItem(
        TodayReadModel readModel,
        TaskSnapshot task,
        TodayTaskGroup group,
        TodayTaskStatus status,
        bool needsReview)
    {
        var item = Assert.Single(readModel.Tasks, candidate => candidate.Task.Id == task.Id);
        Assert.Equal(group, item.Group);
        Assert.Equal(status, item.Status);
        Assert.Equal(needsReview, item.IsNeedsReview);
    }

    private static TaskId TaskId(int suffix) =>
        ReminNote.Core.Tasks.TaskId.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:00}");

    private sealed class FixedTimeZoneProvider(DateTimeZone timeZone) : IUserTimeZoneProvider
    {
        public DateTimeZone TimeZone { get; } = timeZone;
    }

    private sealed class FixedWorkdaySettingsStore(WorkdaySettings settings) : IWorkdaySettingsStore
    {
        public ValueTask<WorkdaySettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(settings);
        }

        public ValueTask SetAsync(
            WorkdaySettings value,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(value);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryTaskQueryService(
        IReadOnlyList<TaskSnapshot> tasks) : ITaskQueryService
    {
        public ValueTask<TaskSnapshot?> FindAsync(
            TaskId id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(tasks.SingleOrDefault(task => task.Id == id));
        }

        public async IAsyncEnumerable<TaskSnapshot> ListAsync(
            TaskQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await System.Threading.Tasks.Task.Yield();
                yield return task;
            }
        }
    }
}
