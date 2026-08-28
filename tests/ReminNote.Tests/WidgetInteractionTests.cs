using ReminNote.Widget.ViewModels;

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
}
