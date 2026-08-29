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

    [Fact]
    public async SystemTask RescheduleUpdatesPlanThroughTheParserAndKeepsTheTitle()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "早晨练习",
            TimeSpec.At(Workday, new LocalTime(9, 0)),
            "0191f6a4-3b25-7c12-8d34-56789abcde17"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var task = FindTask(viewModel, "早晨练习");

        task.RescheduleCommand.Execute(null);

        Assert.True(viewModel.IsRescheduleOpen);
        Assert.Equal("早晨练习", viewModel.RescheduleTaskTitle);
        Assert.Equal("2026-08-28", viewModel.RescheduleDateText);
        Assert.Equal("09:00", viewModel.RescheduleTimeText);

        viewModel.RescheduleDateText = "今天";
        viewModel.RescheduleTimeText = "20:00";
        await viewModel.ConfirmRescheduleCommand.ExecuteAsync(null);

        var update = Assert.Single(store.UpdatedCommands);
        var rescheduled = Assert.IsType<TimePointSpec>(update.TimeSpec);
        Assert.Equal(Workday, rescheduled.LocalDate);
        Assert.Equal(new LocalTime(20, 0), rescheduled.TimePoint);
        Assert.Equal("早晨练习", update.Title);
        Assert.False(viewModel.IsRescheduleOpen);
        Assert.Contains("已改期", viewModel.InteractionMessage, StringComparison.Ordinal);
        var refreshed = FindTask(viewModel, "早晨练习");
        Assert.Equal("20:00", refreshed.TimeLabel);
        Assert.Equal(2, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask RescheduleRejectsCompletedTasksAndInvalidParserInput()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "非法输入任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde18"));
        store.Add(CreateSnapshot(
            "已完成任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde19",
            TaskResult.COMPLETED));
        var viewModel = new TodayPageViewModel(store, store, store);

        var completed = FindTask(viewModel, "已完成任务");
        Assert.False(completed.IsPlanActionsVisible);
        completed.RescheduleCommand.Execute(null);
        Assert.False(viewModel.IsRescheduleOpen);
        Assert.Empty(store.UpdatedCommands);

        var target = FindTask(viewModel, "非法输入任务");
        target.RescheduleCommand.Execute(null);
        Assert.True(viewModel.IsRescheduleOpen);
        Assert.Equal(string.Empty, viewModel.RescheduleTimeText);

        viewModel.RescheduleDateText = string.Empty;
        await viewModel.ConfirmRescheduleCommand.ExecuteAsync(null);
        Assert.Contains("task.parser.empty", viewModel.InteractionMessage, StringComparison.Ordinal);

        viewModel.RescheduleDateText = "#2026-08-28";
        await viewModel.ConfirmRescheduleCommand.ExecuteAsync(null);

        Assert.Empty(store.UpdatedCommands);
        Assert.True(viewModel.IsRescheduleOpen);
        Assert.Contains("task.parser.reserved_syntax", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "20:00", "task.parser.date.invalid")]
    [InlineData("不是日期", "", "task.parser.date.invalid")]
    [InlineData("今天", "不是时间", "task.parser.time.invalid")]
    [InlineData("今天", "25:00", "task.parser.time.invalid")]
    [InlineData("不是日期", "20:00", "task.parser.date.invalid")]
    public async SystemTask RescheduleRejectsBlankInvalidOrFreeTextFieldsWithoutUpdating(
        string dateText,
        string timeText,
        string expectedErrorCode)
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "保持原计划",
            TimeSpec.At(Workday, new LocalTime(9, 0)),
            "0191f6a4-3b25-7c12-8d34-56789abcde25"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var target = FindTask(viewModel, "保持原计划");

        target.RescheduleCommand.Execute(null);
        viewModel.RescheduleDateText = dateText;
        viewModel.RescheduleTimeText = timeText;
        await viewModel.ConfirmRescheduleCommand.ExecuteAsync(null);

        Assert.Empty(store.UpdatedCommands);
        Assert.True(viewModel.IsRescheduleOpen);
        Assert.Contains($"无法改期：{expectedErrorCode}", viewModel.InteractionMessage, StringComparison.Ordinal);
        Assert.Equal("09:00", target.TimeLabel);
        Assert.Equal(1, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask ReschedulePreservesRangeSemanticsAfterFieldValidation()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "跨午夜范围",
            TimeSpec.Range(Workday, new LocalTime(14, 0), new LocalTime(16, 0)),
            "0191f6a4-3b25-7c12-8d34-56789abcde26"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var target = FindTask(viewModel, "跨午夜范围");

        target.RescheduleCommand.Execute(null);
        viewModel.RescheduleDateText = "明天";
        viewModel.RescheduleTimeText = "23:00–01:00";
        await viewModel.ConfirmRescheduleCommand.ExecuteAsync(null);

        var update = Assert.Single(store.UpdatedCommands);
        var range = Assert.IsType<TimeRangeSpec>(update.TimeSpec);
        Assert.Equal(Workday.PlusDays(1), range.LocalDate);
        Assert.Equal(new LocalTime(23, 0), range.RangeStart);
        Assert.Equal(new LocalTime(1, 0), range.RangeEnd);
        Assert.True(range.IsCrossMidnight);
        Assert.False(viewModel.IsRescheduleOpen);
        Assert.Equal(2, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask ReorderSwapsPersistedSortOrderWithinSameDateAndGroup()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "第一项",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde20"));
        store.Add(CreateSnapshot(
            "第二项",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde21",
            sortOrder: 1));
        var viewModel = new TodayPageViewModel(store, store, store);
        var anytimeGroup = viewModel.Groups.Single(group => group.Group == UiTodayTaskGroup.Anytime);
        Assert.Equal(["第一项", "第二项"], anytimeGroup.Items.Select(task => task.Title).ToArray());

        var second = anytimeGroup.Items[1];
        await second.MoveUpCommand.ExecuteAsync(null);

        Assert.Equal(2, store.ReorderCommands.Count);
        Assert.Equal(0, store.Snapshots.Single(task => task.Title == "第二项").SortOrder);
        Assert.Equal(1, store.Snapshots.Single(task => task.Title == "第一项").SortOrder);
        Assert.Equal(["第二项", "第一项"], anytimeGroup.Items.Select(task => task.Title).ToArray());
        Assert.Contains("分组内顺序", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask ReorderMovesTasksThatShareTheDefaultSortOrder()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "P2_FINAL_SMOKE SORT C",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde27"));
        store.Add(CreateSnapshot(
            "P2_FINAL_SMOKE SORT D",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde28"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var anytimeGroup = viewModel.Groups.Single(group => group.Group == UiTodayTaskGroup.Anytime);

        var taskC = anytimeGroup.Items.Single(task => task.Title == "P2_FINAL_SMOKE SORT C");
        await taskC.MoveDownCommand.ExecuteAsync(null);

        Assert.Equal(2, store.ReorderCommands.Count);
        Assert.Equal(
            ["P2_FINAL_SMOKE SORT D", "P2_FINAL_SMOKE SORT C"],
            anytimeGroup.Items.Select(task => task.Title).ToArray());
        Assert.Equal(0, store.Snapshots.Single(task => task.Title == "P2_FINAL_SMOKE SORT D").SortOrder);
        Assert.Equal(1, store.Snapshots.Single(task => task.Title == "P2_FINAL_SMOKE SORT C").SortOrder);
    }

    [Fact]
    public async SystemTask DragReorderMovesATaskToTheDroppedPosition()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "拖动第一项",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde29"));
        store.Add(CreateSnapshot(
            "拖动第二项",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde30",
            sortOrder: 1));
        store.Add(CreateSnapshot(
            "拖动第三项",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde31",
            sortOrder: 2));
        var viewModel = new TodayPageViewModel(store, store, store);
        var anytimeGroup = viewModel.Groups.Single(group => group.Group == UiTodayTaskGroup.Anytime);
        var source = anytimeGroup.Items.Single(task => task.Title == "拖动第一项");
        var target = anytimeGroup.Items.Single(task => task.Title == "拖动第三项");

        await viewModel.ReorderTaskByDropAsync(source, target, insertAfter: true);

        Assert.Equal(
            ["拖动第二项", "拖动第三项", "拖动第一项"],
            anytimeGroup.Items.Select(task => task.Title).ToArray());
        Assert.Equal(0, store.Snapshots.Single(task => task.Title == "拖动第二项").SortOrder);
        Assert.Equal(1, store.Snapshots.Single(task => task.Title == "拖动第三项").SortOrder);
        Assert.Equal(2, store.Snapshots.Single(task => task.Title == "拖动第一项").SortOrder);
    }

    [Fact]
    public async SystemTask CompletedTasksCannotEnterTheReorderWritePath()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "开放任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde32"));
        store.Add(CreateSnapshot(
            "已有结果",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde33",
            TaskResult.COMPLETED));
        var viewModel = new TodayPageViewModel(store, store, store);
        var openTask = FindTask(viewModel, "开放任务");
        var completedTask = FindTask(viewModel, "已有结果");

        Assert.False(completedTask.CanReorder);
        Assert.False(viewModel.Groups
            .Single(group => group.Group == UiTodayTaskGroup.Completed)
            .IsReorderEnabled);

        await viewModel.ReorderTaskByDropAsync(completedTask, openTask);
        await completedTask.MoveUpCommand.ExecuteAsync(null);
        await viewModel.ReorderTaskByDropAsync(openTask, completedTask);

        Assert.Empty(store.ReorderCommands);
        Assert.Contains("已有结果的 Task 不能排序", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask FailedRefreshKeepsTheExistingTodayState()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "刷新前任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde34"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var existingTask = FindTask(viewModel, "刷新前任务");
        store.FailReads = true;

        InvalidOperationException? refreshFailure = null;
        try
        {
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            refreshFailure = exception;
        }

        Assert.NotNull(refreshFailure);

        Assert.Same(existingTask, FindTask(viewModel, "刷新前任务"));
        Assert.Same(existingTask, viewModel.SelectedTask);
        Assert.Contains("TODAY 刷新失败", viewModel.InteractionMessage, StringComparison.Ordinal);
        Assert.Equal(2, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask ConcurrentRefreshesAreSerializedAndTheNewestReadModelWins()
    {
        const string taskId = "0191f6a4-3b25-7c12-8d34-56789abcde37";
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot("旧查询", TimeSpec.Anytime(Workday), taskId));
        var viewModel = new TodayPageViewModel(store, store, store);
        var firstQuery = store.BlockNextQuery();

        var firstRefresh = viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await firstQuery.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        store.Replace(CreateSnapshot("新查询", TimeSpec.Anytime(Workday), taskId));
        var secondRefresh = viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(secondRefresh.IsCompleted);
        Assert.Equal(1, store.ActiveQueryCount);

        firstQuery.Release.TrySetResult(true);
        await SystemTask.WhenAll(firstRefresh, secondRefresh);

        Assert.Equal(1, store.MaxConcurrentQueryCount);
        Assert.Equal("新查询", Assert.Single(viewModel.Groups
            .SelectMany(group => group.Items)).Title);
        Assert.Equal(3, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask FailedRefreshReleasesTheGateForTheFollowingRefresh()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "可保留任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde38"));
        var viewModel = new TodayPageViewModel(store, store, store);
        store.FailReads = true;

        InvalidOperationException? refreshFailure = null;
        try
        {
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            refreshFailure = exception;
        }

        Assert.NotNull(refreshFailure);
        Assert.Equal("可保留任务", Assert.Single(viewModel.Groups
            .SelectMany(group => group.Items)).Title);

        store.FailReads = false;
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("可保留任务", Assert.Single(viewModel.Groups
            .SelectMany(group => group.Items)).Title);
        Assert.Equal(3, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask CancelledQueuedRefreshReleasesTheGate()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "取消时保留",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde39"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var firstQuery = store.BlockNextQuery();

        var firstRefresh = viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await firstQuery.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        OperationCanceledException? cancellation = null;
        try
        {
            await viewModel.RefreshAsync(cancelled.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        Assert.NotNull(cancellation);
        Assert.Equal(1, store.ActiveQueryCount);

        firstQuery.Release.TrySetResult(true);
        await firstRefresh;
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("取消时保留", Assert.Single(viewModel.Groups
            .SelectMany(group => group.Items)).Title);
        Assert.Equal(1, store.MaxConcurrentQueryCount);
        Assert.Equal(3, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask DisposingDuringRefreshCancelsItAndRejectsLaterRefreshes()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "释放时保留",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde40"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var existingTask = FindTask(viewModel, "释放时保留");
        var firstQuery = store.BlockNextQuery();

        var refresh = viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await firstQuery.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        viewModel.Dispose();
        OperationCanceledException? cancellation = null;
        try
        {
            await refresh;
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        Assert.NotNull(cancellation);
        Assert.Same(existingTask, FindTask(viewModel, "释放时保留"));
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, store.TodayQueryCount);
    }

    [Fact]
    public async SystemTask RefreshClearsASelectedTaskThatDisappearedWithoutFallingBackToPrimary()
    {
        var sourceId = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde35");
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "将消失的任务",
            TimeSpec.Anytime(Workday),
            sourceId.Value.ToString()));
        store.Add(CreateSnapshot(
            "刷新后的主任务",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde36",
            sortOrder: 1));
        var viewModel = new TodayPageViewModel(store, store, store);
        var source = FindTask(viewModel, "将消失的任务");
        source.SelectCommand.Execute(null);
        var invalidatedId = (TaskId?)null;
        viewModel.SelectedTaskInvalidated += taskId => invalidatedId = taskId;

        store.Remove(sourceId);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.SelectedTask);
        Assert.Equal(sourceId, invalidatedId);
        Assert.Equal("刷新后的主任务", viewModel.PrimaryTask?.Title);
        Assert.Contains("详情已关闭", viewModel.InteractionMessage, StringComparison.Ordinal);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.SelectedTask);
    }

    [Fact]
    public async SystemTask ReorderRefusesCrossDateNeighborsAndGroupBoundaries()
    {
        var store = new InMemoryTodayStore(Now, Workday);
        store.Add(CreateSnapshot(
            "两天前",
            TimeSpec.Anytime(Workday.PlusDays(-2)),
            "0191f6a4-3b25-7c12-8d34-56789abcde22"));
        store.Add(CreateSnapshot(
            "昨天",
            TimeSpec.Anytime(Workday.PlusDays(-1)),
            "0191f6a4-3b25-7c12-8d34-56789abcde23"));
        store.Add(CreateSnapshot(
            "今天唯一",
            TimeSpec.Anytime(Workday),
            "0191f6a4-3b25-7c12-8d34-56789abcde24"));
        var viewModel = new TodayPageViewModel(store, store, store);
        var overdueGroup = viewModel.Groups.Single(group => group.Group == UiTodayTaskGroup.Overdue);

        var yesterday = overdueGroup.Items.Single(task => task.Title == "昨天");
        await yesterday.MoveUpCommand.ExecuteAsync(null);

        Assert.Empty(store.ReorderCommands);
        Assert.Contains("排序仅在同一计划日期和分组内生效", viewModel.InteractionMessage, StringComparison.Ordinal);

        var anytimeGroup = viewModel.Groups.Single(group => group.Group == UiTodayTaskGroup.Anytime);
        var onlyToday = Assert.Single(anytimeGroup.Items);
        await onlyToday.MoveDownCommand.ExecuteAsync(null);

        Assert.Empty(store.ReorderCommands);
        Assert.Contains("分组边界", viewModel.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingATodayTaskRequestsItsDetails()
    {
        var viewModel = new TodayPageViewModel();
        var task = viewModel.Groups
            .SelectMany(group => group.Items)
            .Single(candidate => candidate.Title == "准备设计评审材料");
        TodayTaskViewModel? requested = null;
        viewModel.DetailsRequested += selected => requested = selected;

        task.SelectCommand.Execute(null);

        Assert.Same(task, requested);
        Assert.Same(task, viewModel.SelectedTask);
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
        TaskResult? result = null,
        int sortOrder = 0)
    {
        var task = DomainTask.Create(
            TestValues.TaskId(id),
            title,
            timeSpec,
            Now.Plus(Duration.FromMinutes(-30)));
        if (sortOrder != 0)
        {
            task.SetSortOrder(sortOrder, Now);
        }

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

        public bool FailReads { get; set; }

        public int TodayQueryCount { get; private set; }

        public int ActiveQueryCount => Volatile.Read(ref _activeQueryCount);

        public int MaxConcurrentQueryCount { get; private set; }

        public List<CreateTaskCommand> CreatedCommands { get; } = [];

        public List<UpdateTaskCommand> UpdatedCommands { get; } = [];

        public List<RecordTaskResultCommand> RecordCommands { get; } = [];

        public List<ReorderTaskCommand> ReorderCommands { get; } = [];

        public List<ContinueTaskCommand> ContinueCommands { get; } = [];

        public IReadOnlyCollection<TaskSnapshot> Snapshots => tasks.Values;

        public void Add(TaskSnapshot snapshot) => tasks.Add(snapshot.Id, snapshot);

        public void Replace(TaskSnapshot snapshot) => tasks[snapshot.Id] = snapshot;

        public bool Remove(TaskId taskId) => tasks.Remove(taskId);

        public QueryGate BlockNextQuery()
        {
            var gate = new QueryGate();
            _nextQueryGate = gate;
            return gate;
        }

        public Instant GetCurrentInstant() => Current;

        public async ValueTask<TodayReadModel> GetAsync(
            TodayQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TodayQueryCount++;
            var queryGate = _nextQueryGate;
            _nextQueryGate = null;
            var activeQueries = Interlocked.Increment(ref _activeQueryCount);
            MaxConcurrentQueryCount = Math.Max(MaxConcurrentQueryCount, activeQueries);

            try
            {
                if (FailReads)
                {
                    throw new InvalidOperationException("Today query failed for test.");
                }

                var localNow = request.Now.InUtc().LocalDateTime;
                var workday = request.Workday ?? Workday;
                var readModels = tasks.Values
                    .Where(task => task.TimeSpec.LocalDate <= workday)
                    .Where(task => task.Result is null || task.TimeSpec.LocalDate == workday)
                    .Select(task => TodayTaskClassifier.Classify(task, workday, localNow))
                    .OrderBy(task => task.Task.SortOrder)
                    .ThenBy(task => task.Task.Id.ToString(), StringComparer.Ordinal)
                    .ToArray();
                var readModel = new TodayReadModel(workday, readModels);

                if (queryGate is not null)
                {
                    queryGate.Started.TrySetResult(true);
                    await queryGate.Release.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return readModel;
            }
            finally
            {
                Interlocked.Decrement(ref _activeQueryCount);
            }
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
            UpdatedCommands.Add(command);
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
            ReorderCommands.Add(command);
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

        private int _activeQueryCount;
        private QueryGate? _nextQueryGate;

        public sealed class QueryGate
        {
            public TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
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
