using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Windows.Features.Today;

using DomainTask = ReminNote.Core.Tasks.Task;
using SystemTask = System.Threading.Tasks.Task;
using UiTodayTaskGroup = ReminNote.Windows.Features.Today.TodayTaskGroup;

namespace ReminNote.Tests;

public sealed class TodayPageViewModelTests
{
    private static readonly LocalDate Workday = new(2026, 8, 28);
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 28, 18, 0);

    [Fact]
    public void LiveConstructorLoadsTheRealReadModelAndDoesNotExposeMockState()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "等待复盘的范围",
            TimeSpec.Range(Workday, new LocalTime(14, 0), new LocalTime(16, 0)),
            "0191f6a4-3b25-7c12-8d34-56789abcde11"));

        var viewModel = new TodayPageViewModel(store, store, store);

        Assert.Equal(1, store.TodayQueryCount);
        Assert.False(viewModel.IsMock);
        Assert.DoesNotContain("Mock", viewModel.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2026年8月28日", viewModel.DateLabel);
        Assert.Equal("星期五", viewModel.WeekdayLabel);
        Assert.Equal(1, viewModel.NeedsReviewCount);
        Assert.Equal("AWAITING RESULT", viewModel.Groups
            .Single(group => group.Group == UiTodayTaskGroup.Afternoon)
            .Items.Single()
            .StatusLabel);
    }

    [Fact]
    public async SystemTask QuickAddUsesTheParserAndRefreshesWithoutPublishingAnInvisibleFutureTask()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "当前任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde12"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var published = false;
        viewModel.QuickTaskAdded += _ => published = true;

        viewModel.OpenQuickAddCommand.Execute(null);
        viewModel.QuickAddText = "明天 18:00 明日计划";
        await viewModel.AddQuickTaskCommand.ExecuteAsync(null);

        var created = Assert.Single(store.CreatedCommands);
        var time = Assert.IsType<TimePointSpec>(created.TimeSpec);
        Assert.Equal(Workday.PlusDays(1), time.LocalDate);
        Assert.Equal(new LocalTime(18, 0), time.TimePoint);
        Assert.Contains(store.Snapshots, task => task.Title == "明日计划");
        Assert.False(published);
        Assert.Empty(viewModel.QuickAddText);
        Assert.True(viewModel.IsQuickAddOpen);
        Assert.Contains("明日计划", viewModel.InteractionMessage, StringComparison.Ordinal);
        Assert.Equal(2, store.TodayQueryCount);

        viewModel.QuickAddText = "今天 买东西 #生活";
        await viewModel.AddQuickTaskCommand.ExecuteAsync(null);

        Assert.Single(store.CreatedCommands);
        Assert.Equal("无法创建 Task：task.parser.reserved_syntax", viewModel.InteractionMessage);
        Assert.Equal("今天 买东西 #生活", viewModel.QuickAddText);
    }

    [Fact]
    public async SystemTask DoneWritesCompletedAndRefreshesTheReadModel()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "今天完成",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde13"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var task = FindTask(viewModel, "今天完成");

        await task.ToggleCompletionCommand.ExecuteAsync(null);

        var result = Assert.Single(store.RecordCommands);
        Assert.Equal(TaskResult.COMPLETED, result.Result);
        var refreshed = FindTask(viewModel, "今天完成");
        Assert.True(refreshed.IsCompleted);
        Assert.Equal("COMPLETED", refreshed.StatusLabel);
        Assert.Equal(2, store.TodayQueryCount);
        Assert.Contains("RecordedAt 只表示记录动作时间", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask NeedsReviewRecordsPartialAndContinueCreatesTheRelation()
    {
        var sourceId = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde14");
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "原范围计划",
            TimeSpec.Range(Workday, new LocalTime(14, 0), new LocalTime(16, 0)),
            sourceId.Value.ToString()));
        var viewModel = new TodayPageViewModel(store, store, store);

        viewModel.OpenNeedsReviewCommand.Execute(null);
        Assert.NotNull(viewModel.SelectedTask);
        var reviewTask = viewModel.SelectedTask!;
        Assert.Equal(sourceId, reviewTask.DomainTaskId);
        Assert.True(reviewTask.IsNeedsReviewVisible);

        await reviewTask.RecordPartialCommand.ExecuteAsync(null);

        var partial = FindTask(viewModel, "原范围计划");
        Assert.Equal("PARTIAL", partial.StatusLabel);
        Assert.True(partial.IsPartial);
        Assert.True(partial.CanContinue);
        Assert.Equal(0, viewModel.NeedsReviewCount);
        Assert.Equal(TaskResult.PARTIAL, Assert.Single(store.RecordCommands).Result);

        await partial.ContinueCommand.ExecuteAsync(null);

        var continuation = Assert.Single(store.ContinueCommands);
        Assert.Equal(sourceId, continuation.SourceTaskId);
        Assert.Equal(TimeSpec.Anytime(Workday), continuation.TimeSpec);
        var createdContinuation = Assert.Single(
            store.Snapshots,
            task => task.ContinuedFromTaskId == sourceId);
        Assert.Equal("原范围计划（继续）", createdContinuation.Title);
        Assert.Contains("已建立继续关系", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask NeedsReviewCanRecordMissedAndLeavesNoPendingReview()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "未完成范围计划",
            TimeSpec.Range(Workday, new LocalTime(14, 0), new LocalTime(16, 0)),
            "0191f6a4-3b25-7c12-8d34-56789abcde16"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var task = FindTask(viewModel, "未完成范围计划");

        await task.RecordMissedCommand.ExecuteAsync(null);

        var missed = FindTask(viewModel, "未完成范围计划");
        Assert.Equal(TaskResult.MISSED, Assert.Single(store.RecordCommands).Result);
        Assert.Equal("MISSED", missed.StatusLabel);
        Assert.True(missed.IsCompleted);
        Assert.Equal(0, viewModel.NeedsReviewCount);
        Assert.Contains("MISSED", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask FailedWriteKeepsTheExistingTodayState()
    {
        var store = new InMemoryTodayStore(Now, Workday)
        {
            FailWrites = true
        };
        store.Add(CreateSnapshot(
            "保持不变",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde15"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var task = FindTask(viewModel, "保持不变");

        await task.ToggleCompletionCommand.ExecuteAsync(null);

        Assert.False(task.IsCompleted);
        Assert.Empty(store.RecordCommands);
        Assert.Contains("Task 写入失败", viewModel.InteractionMessage, StringComparison.Ordinal);
        Assert.Equal(1, store.TodayQueryCount);
    }

    private static TodayTaskViewModel FindTask(
        TodayPageViewModel viewModel,
        string title) =>
        Assert.Single(
            viewModel.Groups.SelectMany(group => group.Items),
            task => task.Title == title);

    private static TaskSnapshot CreateSnapshot(
        string title,
        TimeSpec timeSpec,
        string id,
        TaskResult? result = null)
    {
        var task = DomainTask.Create(
            TestValues.TaskId(id),
            title,
            timeSpec,
            Now.Plus(Duration.FromMinutes(-30)));
        if (result is { } taskResult)
        {
            task.RecordResult(taskResult, Now.Plus(Duration.FromMinutes(-5)));
        }

        return task.ToSnapshot();
    }

    private sealed class InMemoryTodayStore :
        ITodayQueryService,
        ITaskApplicationService,
        IClock
    {
        private readonly Dictionary<TaskId, TaskSnapshot> tasks = [];

        public InMemoryTodayStore(Instant now, LocalDate workday)
        {
            Current = now;
            Workday = workday;
        }

        public Instant Current { get; }

        public LocalDate Workday { get; }

        public bool FailWrites { get; set; }

        public int TodayQueryCount { get; private set; }

        public List<CreateTaskCommand> CreatedCommands { get; } = [];

        public List<RecordTaskResultCommand> RecordCommands { get; } = [];

        public List<ContinueTaskCommand> ContinueCommands { get; } = [];

        public IReadOnlyCollection<TaskSnapshot> Snapshots => tasks.Values;

        public void Add(TaskSnapshot snapshot) => tasks.Add(snapshot.Id, snapshot);

        public Instant GetCurrentInstant() => Current;

        public ValueTask<TodayReadModel> GetAsync(
            TodayQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TodayQueryCount++;
            var localNow = request.Now.InUtc().LocalDateTime;
            var workday = request.Workday ?? Workday;
            var readModels = tasks.Values
                .Where(task => task.TimeSpec.LocalDate <= workday)
                .Where(task => task.Result is null || task.TimeSpec.LocalDate == workday)
                .Select(task => TodayTaskClassifier.Classify(task, workday, localNow))
                .OrderBy(task => task.Task.Id.ToString(), StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult(new TodayReadModel(workday, readModels));
        }

        public ValueTask<TaskSnapshot> CreateAsync(
            CreateTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            CreatedCommands.Add(command);
            var task = DomainTask.Create(command.Title, command.TimeSpec, Current);
            var snapshot = task.ToSnapshot();
            tasks.Add(snapshot.Id, snapshot);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<TaskSnapshot?> UpdateAsync(
            UpdateTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            if (!tasks.TryGetValue(command.TaskId, out var stored))
            {
                return ValueTask.FromResult<TaskSnapshot?>(null);
            }

            var task = Rehydrate(stored);
            task.Rename(command.Title, Current);
            if (task.TimeSpec != command.TimeSpec)
            {
                task.ChangeTime(command.TimeSpec, Current);
            }

            return ValueTask.FromResult<TaskSnapshot?>(Save(task));
        }

        public ValueTask<TaskSnapshot?> RecordResultAsync(
            RecordTaskResultCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            if (!tasks.TryGetValue(command.TaskId, out var stored))
            {
                return ValueTask.FromResult<TaskSnapshot?>(null);
            }

            RecordCommands.Add(command);
            var task = Rehydrate(stored);
            task.RecordResult(command.Result, Current, command.Note);
            return ValueTask.FromResult<TaskSnapshot?>(Save(task));
        }

        public ValueTask<bool> DeleteAsync(
            TaskId taskId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            return ValueTask.FromResult(tasks.Remove(taskId));
        }

        public ValueTask<TaskSnapshot?> ReorderAsync(
            ReorderTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            if (!tasks.TryGetValue(command.TaskId, out var stored))
            {
                return ValueTask.FromResult<TaskSnapshot?>(null);
            }

            var task = Rehydrate(stored);
            task.SetSortOrder(command.SortOrder, Current);
            return ValueTask.FromResult<TaskSnapshot?>(Save(task));
        }

        public ValueTask<TaskSnapshot?> ContinueAsync(
            ContinueTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWriteBlocked(cancellationToken);
            ContinueCommands.Add(command);
            if (!tasks.TryGetValue(command.SourceTaskId, out var source) ||
                source.Result != TaskResult.PARTIAL)
            {
                return ValueTask.FromResult<TaskSnapshot?>(null);
            }

            var sourceTask = Rehydrate(source);
            var task = DomainTask.CreateContinuation(
                sourceTask,
                command.Title,
                command.TimeSpec,
                Current);
            return ValueTask.FromResult<TaskSnapshot?>(Save(task));
        }

        private TaskSnapshot Save(DomainTask task)
        {
            var snapshot = task.ToSnapshot();
            tasks[snapshot.Id] = snapshot;
            return snapshot;
        }

        private void ThrowIfWriteBlocked(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites)
            {
                throw new TaskWriteGateBusyException();
            }
        }

        private static DomainTask Rehydrate(TaskSnapshot snapshot) =>
            DomainTask.Rehydrate(
                snapshot.Id,
                snapshot.Title,
                snapshot.TimeSpec,
                snapshot.CreatedAt,
                snapshot.UpdatedAt,
                snapshot.ResultRecord,
                snapshot.SortOrder,
                snapshot.ContinuedFromTaskId);
    }
}
