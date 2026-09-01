using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Resources.Localization;
using ReminNote.Windows.Startup;
using System.Diagnostics;

namespace ReminNote.Tests;

public sealed class StartupAndMainUiInteractionTests
{
    [Fact]
    public void FormalLiveShellCopySeparatesPersistentTodayFromMockAnime()
    {
        var shellCopy = new[]
        {
            UiText.ShellStageLabel,
            UiText.ShellHeaderSubtitle,
            UiText.ShellFooter
        };

        Assert.Contains("TODAY 本地持久化 Task", UiText.ShellStageLabel, StringComparison.Ordinal);
        Assert.Contains("ANIME 本地 Mock", UiText.ShellStageLabel, StringComparison.Ordinal);
        Assert.Contains("TODAY 使用本地持久化 Task", UiText.ShellHeaderSubtitle, StringComparison.Ordinal);
        Assert.Contains("ANIME 仍为本地 Mock", UiText.ShellHeaderSubtitle, StringComparison.Ordinal);
        Assert.Contains("TODAY Task 持久化于本地 SQLite", UiText.ShellFooter, StringComparison.Ordinal);
        Assert.Contains("ANIME 仍为本地 Mock", UiText.ShellFooter, StringComparison.Ordinal);

        foreach (var copy in shellCopy)
        {
            Assert.DoesNotContain("P0", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("进程内", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("业务数据尚未接入", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("状态仅在本次运行有效", copy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainTodayDragUsesOnlyAReachableHandleAndHidesCompletedSorting()
    {
        var resources = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "Features", "Today", "TodayResources.xaml"));
        var behavior = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "Features", "Today", "TodayTaskDragDropBehavior.cs"));

        Assert.Contains("TodayDragHandleButtonStyle", resources, StringComparison.Ordinal);
        Assert.Contains("today:TodayTaskDragDropBehavior.IsDragHandle=\"True\"", resources, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding CanReorder, Converter={StaticResource TodayBooleanToVisibilityConverter}}\"",
            resources,
            StringComparison.Ordinal);
        Assert.Contains(
            "today:TodayTaskDragDropBehavior.IsEnabled=\"{Binding IsReorderEnabled}\"",
            resources,
            StringComparison.Ordinal);
        Assert.Contains("FindDragHandle", behavior, StringComparison.Ordinal);
        Assert.Contains("!task.CanReorder", behavior, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FindTask(args.OriginalSource as DependencyObject) is not { } task",
            behavior,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainTodayRefreshLifecycleCoversStartupActivationAndShutdown()
    {
        var mainWindow = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "MainWindow.xaml.cs"));
        var app = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "App.xaml.cs"));
        var todayViewModel = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "Features", "Today", "TodayPageViewModel.cs"));

        Assert.Contains("new DispatcherTimer(", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Loaded += OnLoaded", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Activated += OnActivated", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_viewModel.TodayPage", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RefreshAsync(cancellationToken)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_todayRefreshTimer.Start()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_todayRefreshTimer.Stop()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_todayRefreshTimer.Tick -= OnTodayRefreshTimerTick", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_viewModel.TodayPage.Dispose()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", todayViewModel, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(refreshCancellation.Token)", todayViewModel, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _refreshGeneration)", todayViewModel, StringComparison.Ordinal);
        Assert.Contains("window.ActivateFromExternalRequest()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void MainTodayDetailsClosesWhenTheSelectedTaskIsInvalidated()
    {
        var mainWindow = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "MainWindow.xaml.cs"));
        var todayViewModel = ReadWorkspaceFile(
            Path.Combine("src", "windows", "ReminNote.Windows", "Features", "Today", "TodayPageViewModel.cs"));

        Assert.Contains(
            "SelectedTaskInvalidated += OnTodaySelectedTaskInvalidated",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains("detailsWindow.Close()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SelectedTaskInvalidated?.Invoke(invalidatedId)", todayViewModel, StringComparison.Ordinal);
        Assert.Contains(
            "SelectTask(null, clearSelectionInvalidation: false)",
            todayViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (selectedId is { } invalidatedId && selected is null)",
            todayViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainAndWidgetUseDifferentLocalActivationIdentities()
    {
        Assert.NotEqual(MainInstanceIdentity.MutexName, WidgetInstanceIdentity.MutexName);
        Assert.NotEqual(MainInstanceIdentity.PipeName, WidgetInstanceIdentity.PipeName);
        Assert.DoesNotContain("Global\\", MainInstanceIdentity.MutexName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global\\", WidgetInstanceIdentity.MutexName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondaryInstanceSignalsThePrimaryWithoutCreatingAnotherWindow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(
            $"Local\\ReminNote.Tests.Main.{suffix}",
            $"ReminNote.Tests.Main.{suffix}");
        using var secondary = await Task.Factory.StartNew(
            () => new SingleInstanceCoordinator(
                $"Local\\ReminNote.Tests.Main.{suffix}",
                $"ReminNote.Tests.Main.{suffix}"),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        primary.StartListener(() => activated.TrySetResult(true));

        Assert.True(secondary.TryActivateExisting());
        Assert.True(await activated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WidgetSecondaryInstanceSignalsTheWidgetPrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var primary = new WidgetSingleInstanceCoordinator(
            $"Local\\ReminNote.Tests.Widget.{suffix}",
            $"ReminNote.Tests.Widget.{suffix}");
        using var secondary = await Task.Factory.StartNew(
            () => new WidgetSingleInstanceCoordinator(
                $"Local\\ReminNote.Tests.Widget.{suffix}",
                $"ReminNote.Tests.Widget.{suffix}"),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        primary.StartListener(() => activated.TrySetResult(true));

        Assert.True(secondary.TryActivateExisting());
        Assert.True(await activated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DisposingPrimaryStopsTheListenerAndReleasesItsMutex()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var primary = new SingleInstanceCoordinator(
            $"Local\\ReminNote.Tests.Dispose.{suffix}",
            $"ReminNote.Tests.Dispose.{suffix}");
        primary.StartListener(() => { });

        var stopwatch = Stopwatch.StartNew();
        primary.Dispose();
        stopwatch.Stop();

        using var replacement = new SingleInstanceCoordinator(
            $"Local\\ReminNote.Tests.Dispose.{suffix}",
            $"ReminNote.Tests.Dispose.{suffix}");
        Assert.True(replacement.IsPrimary);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void QuickAddReportsSuccessSelectsAndPublishesTheNewAnytimeTask()
    {
        var viewModel = new TodayPageViewModel();
        TodayTaskViewModel? addedTask = null;
        viewModel.QuickTaskAdded += task => addedTask = task;

        viewModel.OpenQuickAddCommand.Execute(null);
        viewModel.QuickAddText = "整理新的 ANYTIME 事项";
        viewModel.AddQuickTaskCommand.Execute(null);

        var anytimeGroup = viewModel.Groups.Single(group => group.Group == TodayTaskGroup.Anytime);
        var task = anytimeGroup.Items.Single(item => item.Title == "整理新的 ANYTIME 事项");
        Assert.Same(task, addedTask);
        Assert.Same(task, viewModel.SelectedTask);
        Assert.True(viewModel.IsQuickAddOpen);
        Assert.Empty(viewModel.QuickAddText);
        Assert.Contains("ANYTIME", viewModel.InteractionMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.QuickAddText = "连续添加的第二项";
        viewModel.AddQuickTaskCommand.Execute(null);

        Assert.True(viewModel.IsQuickAddOpen);
        Assert.Empty(viewModel.QuickAddText);
        Assert.Contains(
            anytimeGroup.Items,
            item => item.Title == "连续添加的第二项");
    }

    [Fact]
    public void WidgetStillUsesItsOwnMockStateAndDoesNotShareMainIdentity()
    {
        var viewModel = new WidgetViewModel();

        viewModel.ToggleQuickAddCommand.Execute(null);
        viewModel.SimulateAlertCommand.Execute(null);

        Assert.True(viewModel.IsAlert);
        Assert.False(viewModel.IsQuickAddOpen);
        Assert.NotEqual(MainInstanceIdentity.MutexName, WidgetInstanceIdentity.MutexName);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "ReminNote.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
