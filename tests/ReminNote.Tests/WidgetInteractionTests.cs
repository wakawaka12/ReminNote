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
    public async SystemTask LiveQuickAddReportsGenericWriteFailureAndKeepsTheExistingQueue()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock)
        {
            CreateException = new InvalidOperationException("storage unavailable")
        };
        application.Add(CreateSnapshot("原有计划", TimeSpec.Anytime(TestValues.PlanDate)));
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        viewModel.QuickAddText = "18:00 不应写入的任务";

        await viewModel.SubmitQuickAddCommand.ExecuteAsync(null);

        Assert.Contains("写入失败", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.Contains("当前 TODAY 列表保持不变", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.Equal("18:00 不应写入的任务", viewModel.QuickAddText);
        Assert.Empty(application.CreatedTasks);
        Assert.Equal(["原有计划"], viewModel.TodayUpcomingItems.Select(item => item.Title));
    }

    [Fact]
    public async SystemTask LiveQuickAddSeparatesSuccessfulWriteFromRefreshFailure()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        application.Add(CreateSnapshot("刷新前的计划", TimeSpec.Anytime(TestValues.PlanDate)));
        var query = new FakeTodayQueryService(application);
        var viewModel = new WidgetViewModel(query, application, clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        query.NextException = new InvalidOperationException("query unavailable");
        viewModel.QuickAddText = "18:00 已写入但待刷新";

        await viewModel.SubmitQuickAddCommand.ExecuteAsync(null);

        Assert.Single(application.CreatedTasks);
        Assert.Contains("已写入本地 Task", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.Contains("刷新失败", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.Contains("当前列表保持不变", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("TODAY 已刷新", viewModel.QuickAddFeedback, StringComparison.Ordinal);
        Assert.Equal(["刷新前的计划"], viewModel.TodayUpcomingItems.Select(item => item.Title));

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Contains(viewModel.TodayUpcomingItems, item => item.Title == "已写入但待刷新");
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
        Assert.Equal("队列里的计划", viewModel.SelectedTaskTitle);
        Assert.Contains("已记录", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Contains("已转到下一开放 Task", viewModel.TaskFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveExternalRefreshClearsMissingSelectionWithoutFallingBackToPrimary()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var selectedTask = CreateSnapshot("外部完成的计划", TimeSpec.Anytime(TestValues.PlanDate));
        var nextTask = CreateSnapshot(
            "刷新后的主任务",
            TimeSpec.At(TestValues.PlanDate, new LocalTime(18, 0)));
        application.Add(selectedTask);
        application.Add(nextTask);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var selectedItem = Assert.Single(
            viewModel.TodayUpcomingItems,
            item => item.Title == selectedTask.Title);
        selectedItem.SelectCommand.Execute(null);
        Assert.Equal(selectedTask.Title, viewModel.SelectedTaskTitle);

        application.CompleteExternally(selectedTask.Id);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(nextTask.Title, viewModel.PrimaryTaskTitle);
        Assert.Equal([nextTask.Title], viewModel.TodayUpcomingItems.Select(item => item.Title));
        Assert.Equal("未选择 Task", viewModel.SelectedTaskTitle);
        Assert.Equal("TODAY · 已选 Task 不可用，请重新选择", viewModel.SelectedTaskMeta);
        Assert.Equal("—", viewModel.SelectedTaskTimeLabel);
        Assert.Equal("—", viewModel.TaskStatusLabel);
        Assert.False(viewModel.IsResultActionsVisible);
        Assert.DoesNotContain(
            viewModel.TodayUpcomingItems,
            item => item.IsSelected);
        Assert.Contains("已不在 TODAY 列表中", viewModel.TaskFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveWidgetCanSelectANonPrimaryTaskBeforeRecordingItsResult()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var primary = CreateSnapshot("先做的计划", TimeSpec.Anytime(TestValues.PlanDate));
        var range = CreateSnapshot(
            "指定处理的 RANGE",
            TimeSpec.Range(TestValues.PlanDate, new LocalTime(13, 0), new LocalTime(14, 0)));
        application.Add(primary);
        application.Add(range, TodayTaskGroup.AFTERNOON, TodayTaskStatus.AwaitingResult, isNeedsReview: true);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var selected = Assert.Single(
            viewModel.TodayUpcomingItems,
            item => item.Title == "指定处理的 RANGE");

        selected.SelectCommand.Execute(null);
        Assert.Equal("指定处理的 RANGE", viewModel.SelectedTaskTitle);
        Assert.True(viewModel.IsResultActionsVisible);

        await viewModel.RecordPartialTaskCommand.ExecuteAsync(null);

        Assert.Equal(TaskResult.PARTIAL, application.Find(range.Id)!.Result);
        Assert.Null(application.Find(primary.Id)!.Result);
    }

    [Fact]
    public async SystemTask LiveWidgetCompleteActsOnTheSelectedNonPrimaryTask()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var primary = CreateSnapshot("默认首项", TimeSpec.Anytime(TestValues.PlanDate));
        var selectedTask = CreateSnapshot(
            "指定完成项",
            TimeSpec.At(TestValues.PlanDate, new LocalTime(18, 0)));
        application.Add(primary);
        application.Add(selectedTask);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var selected = Assert.Single(
            viewModel.TodayUpcomingItems,
            item => item.Title == "指定完成项");
        selected.SelectCommand.Execute(null);

        await viewModel.CompleteTaskCommand.ExecuteAsync(null);

        Assert.Equal(TaskResult.COMPLETED, application.Find(selectedTask.Id)!.Result);
        Assert.Null(application.Find(primary.Id)!.Result);
        Assert.Contains("指定完成项", viewModel.TaskFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask LiveResultWriteReportsGenericFailureAndKeepsTheExistingQueue()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock)
        {
            RecordResultException = new InvalidOperationException("storage unavailable")
        };
        var task = CreateSnapshot("结果写入失败的计划", TimeSpec.Anytime(TestValues.PlanDate));
        application.Add(task);
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.CompleteTaskCommand.ExecuteAsync(null);

        Assert.Contains("写入失败", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Contains("当前 TODAY 列表保持不变", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("已记录", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Null(application.Find(task.Id)!.Result);
        Assert.Equal(["结果写入失败的计划"], viewModel.TodayUpcomingItems.Select(item => item.Title));
    }

    [Fact]
    public async SystemTask LiveResultWriteSeparatesSuccessfulWriteFromRefreshFailure()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        var task = CreateSnapshot("结果已写入但待刷新", TimeSpec.Anytime(TestValues.PlanDate));
        application.Add(task);
        var query = new FakeTodayQueryService(application);
        var viewModel = new WidgetViewModel(query, application, clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        query.NextException = new InvalidOperationException("query unavailable");

        await viewModel.CompleteTaskCommand.ExecuteAsync(null);

        Assert.Equal(TaskResult.COMPLETED, application.Find(task.Id)!.Result);
        Assert.Contains("已记录", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Contains("刷新失败", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Contains("当前列表保持不变", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("TODAY 已刷新", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Equal(["结果已写入但待刷新"], viewModel.TodayUpcomingItems.Select(item => item.Title));

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(viewModel.TodayUpcomingItems);
        Assert.Equal(1, viewModel.CompletedTaskCount);
    }

    [Fact]
    public async SystemTask LiveRefreshFailureReportsFeedbackAndDoesNotClearTheExistingQueue()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock);
        application.Add(CreateSnapshot("刷新失败时仍可见", TimeSpec.Anytime(TestValues.PlanDate)));
        var query = new FakeTodayQueryService(application);
        var viewModel = new WidgetViewModel(query, application, clock);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        query.NextException = new InvalidOperationException("query unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Contains("TODAY 刷新失败", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Contains("当前列表保持不变", viewModel.TaskFeedback, StringComparison.Ordinal);
        Assert.Equal(["刷新失败时仍可见"], viewModel.TodayUpcomingItems.Select(item => item.Title));
    }

    [Fact]
    public async SystemTask LiveCancellationIsPropagatedWithoutWriteFailureFeedback()
    {
        var clock = new FixedClock(TestValues.CreatedAt);
        var application = new FakeTaskApplicationService(clock)
        {
            CreateException = new OperationCanceledException()
        };
        var viewModel = new WidgetViewModel(
            new FakeTodayQueryService(application),
            application,
            clock);
        viewModel.QuickAddText = "18:00 取消的任务";

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await viewModel.SubmitQuickAddCommand.ExecuteAsync(null));

        Assert.Empty(viewModel.QuickAddFeedback);
        Assert.Empty(application.CreatedTasks);
        Assert.Equal("18:00 取消的任务", viewModel.QuickAddText);
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
        Assert.Contains("当前没有下一开放 Task", viewModel.TaskFeedback, StringComparison.Ordinal);
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
        public Exception? NextException { get; set; }

        public ValueTask<TodayReadModel> GetAsync(
            TodayQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NextException is { } exception)
            {
                NextException = null;
                throw exception;
            }

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

        public Exception? CreateException { get; set; }

        public Exception? RecordResultException { get; set; }

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

        public void CompleteExternally(TaskId taskId)
        {
            var index = tasks.FindIndex(task => task.Id == taskId);
            if (index < 0)
            {
                throw new InvalidOperationException("测试 Task 不存在。");
            }

            tasks[index] = tasks[index] with
            {
                ResultRecord = TaskResultRecord.Create(
                    TaskResult.COMPLETED,
                    clock.GetCurrentInstant(),
                    note: null),
                UpdatedAt = clock.GetCurrentInstant()
            };
        }

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
            if (CreateException is { } exception)
            {
                CreateException = null;
                throw exception;
            }

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
            if (RecordResultException is { } exception)
            {
                RecordResultException = null;
                throw exception;
            }

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
