using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Startup;
using System.Diagnostics;

namespace ReminNote.Tests;

public sealed class StartupAndMainUiInteractionTests
{
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
        using var secondary = await Task.Run(
            () => new SingleInstanceCoordinator(
                $"Local\\ReminNote.Tests.Main.{suffix}",
                $"ReminNote.Tests.Main.{suffix}"));
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
        using var secondary = await Task.Run(
            () => new WidgetSingleInstanceCoordinator(
                $"Local\\ReminNote.Tests.Widget.{suffix}",
                $"ReminNote.Tests.Widget.{suffix}"));
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
}
