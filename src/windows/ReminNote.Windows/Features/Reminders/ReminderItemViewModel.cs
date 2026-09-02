using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using NodaTime.Text;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Windows.Resources.Localization;

namespace ReminNote.Windows.Features.Reminders;

/// <summary>
/// A presentation row backed by one immutable read snapshot item. It owns no
/// persistence and sends every action back to its surface/client callback.
/// </summary>
public sealed class ReminderItemViewModel : ObservableObject
{
    public const long DefaultSnoozeSeconds = 30 * 60;

    private readonly Func<bool> canAct;
    private readonly Func<ReminderItemViewModel, ResolutionAction, long?, System.Threading.Tasks.Task> executeAction;
    private readonly Func<ReminderItemViewModel, System.Threading.Tasks.Task> markRead;
    private bool isBusy;
    private string actionFeedback = string.Empty;

    public ReminderItemViewModel(
        ReminderReadModel model,
        Func<bool> canAct,
        Func<ReminderItemViewModel, ResolutionAction, long?, System.Threading.Tasks.Task> executeAction,
        Func<ReminderItemViewModel, System.Threading.Tasks.Task> markRead,
        long? snapshotRevision = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.canAct = canAct ?? throw new ArgumentNullException(nameof(canAct));
        this.executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
        this.markRead = markRead ?? throw new ArgumentNullException(nameof(markRead));
        Model = model;
        SnapshotRevision = snapshotRevision;

        DoneCommand = new AsyncRelayCommand(
            () => ExecuteActionAsync(ResolutionAction.DONE),
            () => CanResolve);
        SnoozeCommand = new AsyncRelayCommand(
            () => ExecuteActionAsync(ResolutionAction.SNOOZE, DefaultSnoozeSeconds),
            () => CanResolve);
        SkipCommand = new AsyncRelayCommand(
            () => ExecuteActionAsync(ResolutionAction.SKIP),
            () => CanResolve);
        IgnoreCommand = new AsyncRelayCommand(
            () => ExecuteActionAsync(ResolutionAction.IGNORE),
            () => CanResolve);
        MarkReadCommand = new AsyncRelayCommand(MarkReadAsync, () => CanMarkRead);
    }

    public ReminderReadModel Model { get; }

    /// <summary>
    /// Revision of the complete snapshot that created this row. The parent
    /// surface uses it to prevent a row kept by WPF during a refresh from
    /// accidentally issuing a command against a newer snapshot.
    /// </summary>
    public long? SnapshotRevision { get; }

    public string Title => Model.Title;

    public string PurposeLabel => Model.Purpose.ToString();

    public string PriorityLabel => Model.Priority.ToString();

    public string TriggeredAtLabel => InstantPattern.Format(Model.TriggeredAtUtc);

    public string LifecycleLabel => Model.Lifecycle switch
    {
        ReminderLifecycle.UNREAD => UiText.ReminderLifecycleUnread,
        ReminderLifecycle.READ => UiText.ReminderLifecycleRead,
        ReminderLifecycle.RESOLVED => UiText.ReminderLifecycleResolved,
        _ => Model.Lifecycle.ToString()
    };

    public string LifecycleDescription => Model.Lifecycle switch
    {
        ReminderLifecycle.UNREAD => UiText.ReminderLifecycleUnreadDescription,
        ReminderLifecycle.READ => UiText.ReminderLifecycleReadDescription,
        ReminderLifecycle.RESOLVED => UiText.ReminderLifecycleResolvedDescription,
        _ => Model.Lifecycle.ToString()
    };

    public bool HasResolutionAction => Model.ResolutionAction is not null;

    public string ResolutionActionLabel => Model.ResolutionAction?.ToString() ?? string.Empty;

    public string SnapshotRevisionLabel => SnapshotRevision is { } revision
        ? UiText.Format(UiText.ReminderRowRevisionKey, revision)
        : UiText.ReminderRowRevisionUnavailable;

    public bool IsUnread => Model.IsUnread;

    public bool IsResolved => Model.IsResolved;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string ActionFeedback
    {
        get => actionFeedback;
        private set
        {
            if (SetProperty(ref actionFeedback, value))
            {
                OnPropertyChanged(nameof(HasActionFeedback));
            }
        }
    }

    public bool HasActionFeedback => !string.IsNullOrEmpty(ActionFeedback);

    public bool CanResolve => !IsResolved && !IsBusy && canAct();

    public bool CanMarkRead => IsUnread && !IsBusy && canAct();

    public IAsyncRelayCommand DoneCommand { get; }

    public IAsyncRelayCommand SnoozeCommand { get; }

    public IAsyncRelayCommand SkipCommand { get; }

    public IAsyncRelayCommand IgnoreCommand { get; }

    public IAsyncRelayCommand MarkReadCommand { get; }

    internal void RefreshCommandState()
    {
        NotifyCommandStateChanged();
    }

    internal void SetActionFeedback(string? value)
    {
        ActionFeedback = value ?? string.Empty;
    }

    private async System.Threading.Tasks.Task ExecuteActionAsync(
        ResolutionAction action,
        long? snoozeSeconds = null)
    {
        if (!CanResolve)
        {
            return;
        }

        IsBusy = true;
        ActionFeedback = string.Empty;
        try
        {
            await executeAction(this, action, snoozeSeconds).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async System.Threading.Tasks.Task MarkReadAsync()
    {
        if (!CanMarkRead)
        {
            return;
        }

        IsBusy = true;
        ActionFeedback = string.Empty;
        try
        {
            await markRead(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyCommandStateChanged()
    {
        DoneCommand.NotifyCanExecuteChanged();
        SnoozeCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        IgnoreCommand.NotifyCanExecuteChanged();
        MarkReadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanResolve));
        OnPropertyChanged(nameof(CanMarkRead));
    }

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm'Z'");
}
