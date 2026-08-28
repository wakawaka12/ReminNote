using Microsoft.Data.Sqlite;
using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Today;
using ReminNote.Infrastructure.Application;
using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;
using SystemTask = System.Threading.Tasks.Task;

namespace ReminNote.Tests;

public sealed class WidgetInteractionTests
{
    [Fact]
    public void QuickAddToggleAndExplicitCloseDoNotLeaveThePanelOpen()
    {
        var viewModel = new WidgetViewModel();
        var closedLabel = viewModel.QuickAddTriggerLabel;

        viewModel.ToggleQuickAddCommand.Execute(null);
        Assert.True(viewModel.IsQuickAddOpen);
        Assert.NotEqual(closedLabel, viewModel.QuickAddTriggerLabel);

        viewModel.CloseQuickAddCommand.Execute(null);
        Assert.False(viewModel.IsQuickAddOpen);

        viewModel.ToggleQuickAddCommand.Execute(null);
        viewModel.ToggleQuickAddCommand.Execute(null);
        Assert.False(viewModel.IsQuickAddOpen);
    }

    [Fact]
    public void OpeningReminderDrawerTogglesAndClosesQuickAdd()
    {
        var viewModel = new WidgetViewModel();
        var closedLabel = viewModel.ReminderTriggerLabel;
        viewModel.ToggleQuickAddCommand.Execute(null);

        viewModel.OpenReminderDrawerCommand.Execute(null);

        Assert.False(viewModel.IsQuickAddOpen);
        Assert.True(viewModel.IsReminderDrawerOpen);
        Assert.NotEqual(closedLabel, viewModel.ReminderTriggerLabel);

        viewModel.OpenReminderDrawerCommand.Execute(null);
        Assert.False(viewModel.IsReminderDrawerOpen);
    }

    [Fact]
    public void SimulatingAlertClosesOtherPanelsAndDismissRestoresThePreviousState()
    {
        var viewModel = new WidgetViewModel();
        var normalLabel = viewModel.AlertTriggerLabel;
        viewModel.ToggleInteractionCommand.Execute(null);
        viewModel.ToggleInteractionCommand.Execute(null);
        viewModel.ToggleQuickAddCommand.Execute(null);
        var previousStateLabel = viewModel.InteractionStateLabel;

        viewModel.SimulateAlertCommand.Execute(null);

        Assert.True(viewModel.IsAlert);
        Assert.NotEqual(normalLabel, viewModel.AlertTriggerLabel);
        Assert.False(viewModel.IsQuickAddOpen);
        Assert.False(viewModel.IsReminderDrawerOpen);

        viewModel.DismissAlertCommand.Execute(null);

        Assert.False(viewModel.IsAlert);
        Assert.Equal(normalLabel, viewModel.AlertTriggerLabel);
        Assert.Equal(previousStateLabel, viewModel.InteractionStateLabel);
    }

    [Fact]
    public void OpeningReminderFromAlertDismissesAlertBeforeShowingTheDrawer()
    {
        var viewModel = new WidgetViewModel();
        viewModel.SimulateAlertCommand.Execute(null);

        viewModel.OpenReminderDrawerCommand.Execute(null);

        Assert.False(viewModel.IsAlert);
        Assert.True(viewModel.IsReminderDrawerOpen);
        Assert.False(viewModel.IsQuickAddOpen);
    }

    [Fact]
    public void SimulateAlertCommandActsAsDismissToggleWhenAlertIsAlreadyOpen()
    {
        var viewModel = new WidgetViewModel();

        viewModel.SimulateAlertCommand.Execute(null);
        viewModel.SimulateAlertCommand.Execute(null);

        Assert.False(viewModel.IsAlert);
    }

    [Fact]
    public async SystemTask LiveQuickAddUsesTaskParserAndRefreshesTheQueue()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var query = new FakeTodayQueryService(application);
        application.Add(CreateSnapshot("原有计划", TimeSpec.Anytime(TestValues.PlanDate)));
        var viewModel = new WidgetViewModel(query, application, clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        viewModel.QuickAddText = "18:00 Widget 新任务";
        await viewModel.SubmitQuickAddCommand.ExecuteAsync(null);

        var created = Assert.Single(application.CreatedTasks);
        Assert.Equal("Widget 新任务", created.Title);
        Assert.Equal(TimeSpec.At(TestValues.PlanDate, new LocalTime(18, 0)), created.TimeSpec);
        Assert.Empty(viewModel.QuickAddText);
        Assert.Contains(viewModel.TodayUpcomingItems, item => item.Title == "Widget 新任务");
        Assert.Contains("LIVE TASK", viewModel.DataSourceLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveQuickAddRejectsUnsupportedSyntaxWithoutWriting()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        viewModel.QuickAddText = "#tag 不应写入";
        await viewModel.SubmitQuickAddCommand.ExecuteAsync(null);

        Assert.Empty(application.CreatedTasks);
        Assert.Contains("task.parser.reserved_syntax", viewModel.QuickAddFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveDoneRecordsCompletedAndRefreshesToTheNextOpenTask()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var first = CreateSnapshot("先做的计划", TimeSpec.Anytime(TestValues.PlanDate));
        var second = CreateSnapshot("队列里的计划", TimeSpec.At(TestValues.PlanDate, new LocalTime(18, 0)));
        application.Add(first);
        application.Add(second);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("先做的计划", viewModel.PrimaryTaskTitle);
        await viewModel.CompleteTaskCommand.ExecuteAsync(null);

        Assert.Equal(TaskResult.COMPLETED, application.Find(first.Id)!.Result);
        Assert.Equal(1, viewModel.OpenTaskCount);
        Assert.Equal(1, viewModel.CompletedTaskCount);
        Assert.Equal("队列里的计划", viewModel.PrimaryTaskTitle);
        Assert.Contains("已记录", viewModel.TaskFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveRangeResultActionRecordsPartialAndClearsNeedsReview()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var range = CreateSnapshot(
            "跨午夜 Widget 计划",
            TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)));
        application.Add(range, TodayTaskGroup.OVERDUE, TodayTaskStatus.AwaitingResult, isNeedsReview: true);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsResultActionsVisible);
        await viewModel.RecordPartialTaskCommand.ExecuteAsync(null);

        Assert.Equal(TaskResult.PARTIAL, application.Find(range.Id)!.Result);
        Assert.Equal(0, viewModel.NeedsReviewCount);
        Assert.Equal(1, viewModel.CompletedTaskCount);
        Assert.False(viewModel.IsResultActionsVisible);
        Assert.Equal("PARTIAL", viewModel.TaskStatusLabel);
    }

    [Fact]
    public async SystemTask LiveSnoozeAndRescheduleRemainExplicitlyUnavailable()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var task = CreateSnapshot("不应被伪造改期", TimeSpec.Anytime(TestValues.PlanDate));
        application.Add(task);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        viewModel.SnoozeTaskCommand.Execute(null);
        Assert.Contains("暂不在 Widget 自动改期", viewModel.TaskFeedback, StringComparison.Ordinal);
        viewModel.RescheduleTaskCommand.Execute(null);
        Assert.Contains("暂不在 Widget 自动改期", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Equal(task, application.Find(task.Id));
        Assert.Empty(application.RecordedResults);
    }

    [Fact]
    public async SystemTask LiveWidgetUsesTheSameSqliteWorkspaceAfterRestart()
    {
        using var repository = new TemporaryRepository();
        var clock = new FixedClock(Instant.FromUtc(2026, 8, 28, 12, 0));
        var timeZone = new SystemUserTimeZoneProvider(DateTimeZone.Utc);

        TaskSnapshot created;
        using (var workspace = new TaskWorkspace(repository.Root, clock, timeZone))
        {
            workspace.Initialize();
            using var viewModel = new WidgetViewModel(workspace, workspace, clock);

            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
            viewModel.QuickAddText = "18:00 跨重启保留的 Widget Task";
            await viewModel.SubmitQuickAddCommand.ExecuteAsync(null);

            created = Assert.Single(await ReadTasksAsync(
                workspace,
                new LocalDate(2026, 8, 28),
                TestContext.Current.CancellationToken));
            Assert.Equal("跨重启保留的 Widget Task", created.Title);
            Assert.Equal(1, viewModel.OpenTaskCount);

            await viewModel.CompleteTaskCommand.ExecuteAsync(null);
            Assert.Equal(
                TaskResult.COMPLETED,
                (await workspace.FindAsync(
                    created.Id,
                    TestContext.Current.CancellationToken))!.Result);
        }

        using (var restartedWorkspace = new TaskWorkspace(repository.Root, clock, timeZone))
        {
            restartedWorkspace.Initialize();
            using var restartedViewModel = new WidgetViewModel(
                restartedWorkspace,
                restartedWorkspace,
                clock);

            await restartedViewModel.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal(created.Title, restartedViewModel.PrimaryTaskTitle);
            Assert.Equal("COMPLETED", restartedViewModel.TaskStatusLabel);
            Assert.Equal(0, restartedViewModel.OpenTaskCount);
            Assert.Equal(1, restartedViewModel.CompletedTaskCount);
            Assert.Empty(restartedViewModel.TodayUpcomingItems);
        }
    }

    [Fact]
    public void StartupOptionsRequireAndValidateAnExplicitRepositoryRoot()
    {
        var repositoryRoot = FindRepositoryRoot();

        var resolved = WidgetStartupOptions.ResolveRepositoryRoot(
            ["--repo-root", repositoryRoot],
            currentDirectory: Path.GetTempPath());

        Assert.Equal(Path.GetFullPath(repositoryRoot), resolved);
        Assert.Throws<ArgumentException>(() =>
            WidgetStartupOptions.ResolveRepositoryRoot(["--repo-root"], repositoryRoot));
        Assert.Throws<ArgumentException>(() =>
            WidgetStartupOptions.ResolveRepositoryRoot([], repositoryRoot));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "ReminNote.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("测试需要从 ReminNote 仓库构建输出目录运行。");
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<TaskSnapshot>> ReadTasksAsync(
        TaskWorkspace queryService,
        LocalDate planDate,
        CancellationToken cancellationToken)
    {
        var tasks = new List<TaskSnapshot>();
        await foreach (var task in queryService.ListAsync(
                           new TaskQuery(planDate),
                           cancellationToken))
        {
            tasks.Add(task);
        }

        return tasks;
    }

    private sealed class TemporaryRepository : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("reminnote-widget-");

        public TemporaryRepository()
        {
            File.WriteAllText(Path.Combine(directory.FullName, ".git"), "gitdir: test");
            File.WriteAllText(Path.Combine(directory.FullName, "ReminNote.sln"), string.Empty);
        }

        public string Root => directory.FullName;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static TaskSnapshot CreateSnapshot(string title, TimeSpec timeSpec) =>
        new(
            TestValues.TaskId(Guid.CreateVersion7().ToString()),
            title,
            timeSpec,
            ResultRecord: null,
            CreatedAt: TestValues.CreatedAt,
            UpdatedAt: TestValues.CreatedAt);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class FakeTodayQueryService(FakeTaskApplicationService application)
        : ITodayQueryService
    {
        public ValueTask<TodayReadModel> GetAsync(
            TodayQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = application.Tasks
                .Select(application.ToReadModel)
                .ToArray();
            return ValueTask.FromResult<TodayReadModel>(
                new TodayReadModel(TestValues.PlanDate, tasks));
        }
    }

    private sealed class FakeTaskApplicationService(FixedClock clock)
        : ITaskApplicationService
    {
        private readonly List<TaskSnapshot> tasks = [];
        private readonly Dictionary<TaskId, (TodayTaskGroup Group, TodayTaskStatus Status, bool IsNeedsReview)> metadata = [];

        public List<TaskSnapshot> CreatedTasks { get; } = [];

        public List<RecordTaskResultCommand> RecordedResults { get; } = [];

        public IReadOnlyList<TaskSnapshot> Tasks => tasks;

        public void Add(
            TaskSnapshot task,
            TodayTaskGroup group = TodayTaskGroup.ANYTIME,
            TodayTaskStatus status = TodayTaskStatus.PLANNED,
            bool isNeedsReview = false)
        {
            tasks.Add(task);
            metadata[task.Id] = (group, status, isNeedsReview);
        }

        public TaskSnapshot? Find(TaskId id) => tasks.SingleOrDefault(task => task.Id == id);

        public TodayTaskReadModel ToReadModel(TaskSnapshot task)
        {
            if (task.Result is not null)
            {
                return new TodayTaskReadModel(
                    task,
                    TodayTaskGroup.COMPLETED,
                    TodayTaskStatus.COMPLETED,
                    IsNeedsReview: false);
            }

            var info = metadata[task.Id];
            return new TodayTaskReadModel(task, info.Group, info.Status, info.IsNeedsReview);
        }

        public ValueTask<TaskSnapshot> CreateAsync(
            CreateTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new TaskSnapshot(
                TestValues.TaskId(Guid.CreateVersion7().ToString()),
                command.Title,
                command.TimeSpec,
                ResultRecord: null,
                CreatedAt: clock.GetCurrentInstant(),
                UpdatedAt: clock.GetCurrentInstant());
            Add(snapshot);
            CreatedTasks.Add(snapshot);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<TaskSnapshot?> UpdateAsync(
            UpdateTaskCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskSnapshot?>(null);

        public ValueTask<TaskSnapshot?> RecordResultAsync(
            RecordTaskResultCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = tasks.FindIndex(task => task.Id == command.TaskId);
            if (index < 0)
            {
                return ValueTask.FromResult<TaskSnapshot?>(null);
            }

            RecordedResults.Add(command);
            var updated = tasks[index] with
            {
                ResultRecord = TaskResultRecord.Create(
                    command.Result,
                    clock.GetCurrentInstant(),
                    command.Note),
                UpdatedAt = clock.GetCurrentInstant()
            };
            tasks[index] = updated;
            return ValueTask.FromResult<TaskSnapshot?>(updated);
        }

        public ValueTask<bool> DeleteAsync(
            TaskId taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<TaskSnapshot?> ReorderAsync(
            ReorderTaskCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskSnapshot?>(null);

        public ValueTask<TaskSnapshot?> ContinueAsync(
            ContinueTaskCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskSnapshot?>(null);
    }
}
