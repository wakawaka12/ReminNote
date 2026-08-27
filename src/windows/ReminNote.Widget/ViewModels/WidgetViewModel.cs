using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.Resources.Localization;

namespace ReminNote.Widget.ViewModels;

public enum WidgetPage
{
    Today,
    Anime
}

public enum WidgetInteractionState
{
    Locked,
    TempInteractive,
    Unlocked,
    Alert
}

public enum WidgetResponsiveMode
{
    Compact,
    Standard,
    Expanded
}

public sealed class WidgetQueueItem(string time, string title, string meta, string marker)
{
    public string Time { get; } = time;

    public string Title { get; } = title;

    public string Meta { get; } = meta;

    public string Marker { get; } = marker;
}

public sealed class WidgetViewModel : ObservableObject
{
    private WidgetPage _activePage = WidgetPage.Today;
    private WidgetInteractionState _interactionState = WidgetInteractionState.Locked;
    private WidgetResponsiveMode _responsiveMode = WidgetResponsiveMode.Standard;
    private WidgetInteractionState _stateBeforeAlert = WidgetInteractionState.Locked;
    private bool _isQuickAddOpen;
    private bool _isReminderDrawerOpen;
    private bool _isTaskCompleted;
    private string _taskFeedback = string.Empty;
    private string _animeFeedback = string.Empty;
    private string _quickAddText = string.Empty;
    private string _quickAddFeedback = string.Empty;

    public WidgetViewModel()
    {
        TodayUpcomingItems =
        [
            new WidgetQueueItem("12:00", "吃钙片", UiText.Get(UiText.WidgetTodayHealthMetaKey), "NOW"),
            new WidgetQueueItem("18:30", "准备明日计划", UiText.Get(UiText.WidgetTodayReviewMetaKey), "NEXT")
        ];
        AnimeUpcomingItems =
        [
            new WidgetQueueItem("22:30", "药屋少女的呢喃", UiText.Get(UiText.WidgetAnimeWatchingMetaKey), "01:12"),
            new WidgetQueueItem("明天", "迷宫饭", UiText.Get(UiText.WidgetAnimeWatchLaterMetaKey), "+2")
        ];

        ShowTodayCommand = new RelayCommand(ShowToday);
        ShowAnimeCommand = new RelayCommand(ShowAnime);
        ToggleInteractionCommand = new RelayCommand(ToggleInteractionState);
        ToggleQuickAddCommand = new RelayCommand(ToggleQuickAdd);
        SubmitQuickAddCommand = new RelayCommand(SubmitQuickAdd, CanSubmitQuickAdd);
        CompleteTaskCommand = new RelayCommand(CompleteTask);
        SnoozeTaskCommand = new RelayCommand(SnoozeTask);
        RescheduleTaskCommand = new RelayCommand(RescheduleTask);
        WatchAnimeCommand = new RelayCommand(WatchAnime);
        WatchLaterCommand = new RelayCommand(WatchLater);
        ScheduleAnimeTaskCommand = new RelayCommand(ScheduleAnimeTask);
        OpenReminderDrawerCommand = new RelayCommand(OpenReminderDrawer);
        CloseReminderDrawerCommand = new RelayCommand(CloseReminderDrawer);
        MarkReminderReadCommand = new RelayCommand(MarkReminderRead);
        SimulateAlertCommand = new RelayCommand(SimulateAlert);
        DismissAlertCommand = new RelayCommand(DismissAlert);
    }

    public IReadOnlyList<WidgetQueueItem> TodayUpcomingItems { get; }

    public IReadOnlyList<WidgetQueueItem> AnimeUpcomingItems { get; }

    public IRelayCommand ShowTodayCommand { get; }

    public IRelayCommand ShowAnimeCommand { get; }

    public IRelayCommand ToggleInteractionCommand { get; }

    public IRelayCommand ToggleQuickAddCommand { get; }

    public IRelayCommand SubmitQuickAddCommand { get; }

    public IRelayCommand CompleteTaskCommand { get; }

    public IRelayCommand SnoozeTaskCommand { get; }

    public IRelayCommand RescheduleTaskCommand { get; }

    public IRelayCommand WatchAnimeCommand { get; }

    public IRelayCommand WatchLaterCommand { get; }

    public IRelayCommand ScheduleAnimeTaskCommand { get; }

    public IRelayCommand OpenReminderDrawerCommand { get; }

    public IRelayCommand CloseReminderDrawerCommand { get; }

    public IRelayCommand MarkReminderReadCommand { get; }

    public IRelayCommand SimulateAlertCommand { get; }

    public IRelayCommand DismissAlertCommand { get; }

    public string ActivePageTitle => _activePage == WidgetPage.Today
        ? UiText.ShellTodayTitle
        : UiText.ShellAnimeTitle;

    public string ActivePageSubtitle => _activePage == WidgetPage.Today
        ? UiText.Get(UiText.WidgetTodayPageSubtitleKey)
        : UiText.Get(UiText.WidgetAnimePageSubtitleKey);

    public bool IsTodayActive => _activePage == WidgetPage.Today;

    public bool IsAnimeActive => _activePage == WidgetPage.Anime;

    public string InteractionStateLabel => _interactionState switch
    {
        WidgetInteractionState.Locked => UiText.Get(UiText.WidgetStateLockedKey),
        WidgetInteractionState.TempInteractive => UiText.Get(UiText.WidgetStateTempKey),
        WidgetInteractionState.Unlocked => UiText.Get(UiText.WidgetStateUnlockedKey),
        WidgetInteractionState.Alert => UiText.Get(UiText.WidgetStateAlertKey),
        _ => UiText.Get(UiText.WidgetStateMockKey)
    };

    public string InteractionStateIcon => _interactionState switch
    {
        WidgetInteractionState.Locked => "◆",
        WidgetInteractionState.TempInteractive => "◈",
        WidgetInteractionState.Unlocked => "◇",
        WidgetInteractionState.Alert => "!",
        _ => "·"
    };

    public string TaskStatusLabel => _isTaskCompleted
        ? UiText.Get(UiText.WidgetTaskCompletedKey)
        : UiText.Get(UiText.WidgetTaskAnytimeKey);

    public string TaskFeedback
    {
        get => _taskFeedback;
        private set
        {
            if (SetProperty(ref _taskFeedback, value))
            {
                OnPropertyChanged(nameof(HasTaskFeedback));
            }
        }
    }

    public bool HasTaskFeedback => !string.IsNullOrEmpty(_taskFeedback);

    public string AnimeFeedback
    {
        get => _animeFeedback;
        private set
        {
            if (SetProperty(ref _animeFeedback, value))
            {
                OnPropertyChanged(nameof(HasAnimeFeedback));
            }
        }
    }

    public bool HasAnimeFeedback => !string.IsNullOrEmpty(_animeFeedback);

    public string QuickAddText
    {
        get => _quickAddText;
        set
        {
            if (SetProperty(ref _quickAddText, value))
            {
                SubmitQuickAddCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string QuickAddFeedback
    {
        get => _quickAddFeedback;
        private set
        {
            if (SetProperty(ref _quickAddFeedback, value))
            {
                OnPropertyChanged(nameof(HasQuickAddFeedback));
            }
        }
    }

    public bool HasQuickAddFeedback => !string.IsNullOrEmpty(_quickAddFeedback);

    public bool IsQuickAddOpen
    {
        get => _isQuickAddOpen;
        private set => SetProperty(ref _isQuickAddOpen, value);
    }

    public bool IsReminderDrawerOpen
    {
        get => _isReminderDrawerOpen;
        private set => SetProperty(ref _isReminderDrawerOpen, value);
    }

    public bool IsAlert => _interactionState == WidgetInteractionState.Alert;

    public bool IsCompact => _responsiveMode == WidgetResponsiveMode.Compact;

    public bool IsNotCompact => !IsCompact;

    public bool IsExpanded => _responsiveMode == WidgetResponsiveMode.Expanded;

    public void SetViewportWidth(double width)
    {
        var newMode = width < 430
            ? WidgetResponsiveMode.Compact
            : width >= 650
                ? WidgetResponsiveMode.Expanded
                : WidgetResponsiveMode.Standard;

        if (_responsiveMode == newMode)
        {
            return;
        }

        _responsiveMode = newMode;
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsNotCompact));
        OnPropertyChanged(nameof(IsExpanded));
    }

    private void ShowToday()
    {
        if (_activePage == WidgetPage.Today)
        {
            return;
        }

        _activePage = WidgetPage.Today;
        NotifyPageChanged();
    }

    private void ShowAnime()
    {
        if (_activePage == WidgetPage.Anime)
        {
            return;
        }

        _activePage = WidgetPage.Anime;
        NotifyPageChanged();
    }

    private void ToggleInteractionState()
    {
        _interactionState = _interactionState switch
        {
            WidgetInteractionState.Locked => WidgetInteractionState.TempInteractive,
            WidgetInteractionState.TempInteractive => WidgetInteractionState.Unlocked,
            WidgetInteractionState.Unlocked => WidgetInteractionState.Locked,
            WidgetInteractionState.Alert => WidgetInteractionState.Unlocked,
            _ => WidgetInteractionState.Locked
        };
        NotifyInteractionStateChanged();
    }

    private void ToggleQuickAdd()
    {
        IsQuickAddOpen = !IsQuickAddOpen;
    }

    private bool CanSubmitQuickAdd() => !string.IsNullOrWhiteSpace(QuickAddText);

    private void SubmitQuickAdd()
    {
        QuickAddFeedback = UiText.Format(UiText.WidgetQuickAddFeedbackKey, QuickAddText.Trim());
        QuickAddText = string.Empty;
    }

    private void CompleteTask()
    {
        _isTaskCompleted = true;
        TaskFeedback = UiText.Get(UiText.WidgetTaskCompletedFeedbackKey);
    }

    private void SnoozeTask()
    {
        _isTaskCompleted = false;
        TaskFeedback = UiText.Get(UiText.WidgetTaskSnoozedFeedbackKey);
    }

    private void RescheduleTask()
    {
        _isTaskCompleted = false;
        TaskFeedback = UiText.Get(UiText.WidgetTaskRescheduledFeedbackKey);
    }

    private void WatchAnime()
    {
        AnimeFeedback = UiText.Get(UiText.WidgetAnimeWatchedFeedbackKey);
    }

    private void WatchLater()
    {
        AnimeFeedback = UiText.Get(UiText.WidgetAnimeWatchLaterFeedbackKey);
    }

    private void ScheduleAnimeTask()
    {
        AnimeFeedback = UiText.Get(UiText.WidgetAnimeScheduledFeedbackKey);
    }

    private void OpenReminderDrawer()
    {
        IsReminderDrawerOpen = true;
    }

    private void CloseReminderDrawer()
    {
        IsReminderDrawerOpen = false;
    }

    private void MarkReminderRead()
    {
        IsReminderDrawerOpen = false;
        QuickAddFeedback = UiText.Get(UiText.WidgetReminderReadFeedbackKey);
        IsQuickAddOpen = true;
    }

    private void SimulateAlert()
    {
        if (_interactionState != WidgetInteractionState.Alert)
        {
            _stateBeforeAlert = _interactionState;
        }

        _interactionState = WidgetInteractionState.Alert;
        NotifyInteractionStateChanged();
    }

    private void DismissAlert()
    {
        _interactionState = _stateBeforeAlert;
        NotifyInteractionStateChanged();
    }

    private void NotifyPageChanged()
    {
        OnPropertyChanged(nameof(ActivePageTitle));
        OnPropertyChanged(nameof(ActivePageSubtitle));
        OnPropertyChanged(nameof(IsTodayActive));
        OnPropertyChanged(nameof(IsAnimeActive));
    }

    private void NotifyInteractionStateChanged()
    {
        OnPropertyChanged(nameof(InteractionStateLabel));
        OnPropertyChanged(nameof(InteractionStateIcon));
        OnPropertyChanged(nameof(IsAlert));
    }

}
