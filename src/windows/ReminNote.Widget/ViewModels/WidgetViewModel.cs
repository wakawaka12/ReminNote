using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodaTime;
using ReminNote.Agent.Runtime;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Core.Tasks.Parsing;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Windows.Features.Reminders;
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

internal enum WidgetRefreshReason
{
    External,
    OwnResultRecorded
}

public sealed class WidgetQueueItem : ObservableObject
{
    private bool _isSelected;

    public WidgetQueueItem(
        string time,
        string title,
        string meta,
        string marker,
        Action<WidgetQueueItem>? select = null,
        TaskId? taskId = null,
        TodayTaskReadModel? readModel = null)
    {
        Time = time;
        Title = title;
        Meta = meta;
        Marker = marker;
        TaskId = taskId;
        ReadModel = readModel;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public string Time { get; }

    public string Title { get; }

    public string Meta { get; }

    public string Marker { get; }

    public TaskId? TaskId { get; }

    public bool IsCompleted => ReadModel?.IsCompleted ?? false;

    public bool IsRange => ReadModel?.IsRange ?? false;

    public bool IsNeedsReview => ReadModel?.IsNeedsReview ?? false;

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public IRelayCommand SelectCommand { get; }

    internal TodayTaskReadModel? ReadModel { get; }

    internal void SetSelected(bool value)
    {
        IsSelected = value;
    }
}

public sealed class WidgetViewModel : ObservableObject, IDisposable
{
    private const string LiveSelectionInvalidatedFeedback =
        "已选 Task 已不在 TODAY 列表中 · 已清除选择，请重新选择";
    private const string LiveRefreshFailedFeedback =
        "TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试";
    private const string LiveQuickAddWriteFailedFeedback =
        "无法创建 Task：写入失败 · 当前 TODAY 列表保持不变";
    private const string LiveResultWriteFailedFeedback =
        "无法记录结果：写入失败 · 当前 TODAY 列表保持不变";
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
    private string _reminderFeedback = string.Empty;
    private readonly ITaskApplicationService? _taskApplicationService;
    private readonly ITodayQueryService? _todayQueryService;
    private readonly IReminderQueryService? _reminderQueryService;
    private readonly IReminderCommandClient? _reminderCommandClient;
    private readonly IClock _clock;
    private readonly bool _isLive;
    private readonly ObservableCollection<WidgetQueueItem> _todayItems = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ObservableCollection<ReminderItemViewModel> _reminderItems = [];
    private readonly SemaphoreSlim _reminderRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TodayTaskReadModel? _livePrimaryTask;
    private TodayTaskReadModel? _liveSelectedTask;
    private WidgetQueueItem? _selectedTaskItem;
    private bool _selectionWasInvalidated;
    private TaskId? _currentTaskId;
    private LocalDate _currentWorkday;
    private int _openTaskCount;
    private int _completedTaskCount;
    private int _needsReviewCount;
    private string _primaryTaskTitle = "整理桌面资料";
    private string _primaryTaskMeta = "TODAY · ANYTIME · 生活";
    private string _primaryTaskTimeLabel = UiText.WidgetNow;
    private ReminderSnapshotState _reminderSnapshot = ReminderSnapshotState.Empty;
    private int _disposed;

    public WidgetViewModel()
    {
        _clock = SystemClock.Instance;
        _isLive = false;
        TodayUpcomingItems =
        [
            new WidgetQueueItem("12:00", "吃钙片", UiText.Get(UiText.WidgetTodayHealthMetaKey), "NOW", SelectTask),
            new WidgetQueueItem("18:30", "准备明日计划", UiText.Get(UiText.WidgetTodayReviewMetaKey), "NEXT", SelectTask)
        ];
        AnimeUpcomingItems =
        [
            new WidgetQueueItem("22:30", "药屋少女的呢喃", UiText.Get(UiText.WidgetAnimeWatchingMetaKey), "01:12"),
            new WidgetQueueItem("明天", "迷宫饭", UiText.Get(UiText.WidgetAnimeWatchLaterMetaKey), "+2")
        ];

        ConfigureCommands();
    }

    public WidgetViewModel(
        ITodayQueryService todayQueryService,
        ITaskApplicationService taskApplicationService,
        IClock clock)
        : this(
            todayQueryService,
            taskApplicationService,
            reminderQueryService: null,
            reminderCommandClient: null,
            clock)
    {
    }

    public WidgetViewModel(
        ITodayQueryService todayQueryService,
        ITaskApplicationService taskApplicationService,
        IReminderQueryService? reminderQueryService,
        IReminderCommandClient? reminderCommandClient,
        IClock clock)
    {
        _todayQueryService = todayQueryService ?? throw new ArgumentNullException(nameof(todayQueryService));
        _taskApplicationService = taskApplicationService ?? throw new ArgumentNullException(nameof(taskApplicationService));
        _reminderQueryService = reminderQueryService;
        _reminderCommandClient = reminderCommandClient;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _isLive = true;
        _primaryTaskTitle = "暂无 TODAY Task";
        _primaryTaskMeta = "TODAY · 暂无计划";
        _primaryTaskTimeLabel = "—";
        TodayUpcomingItems = _todayItems;
        AnimeUpcomingItems =
        [
            new WidgetQueueItem("22:30", "药屋少女的呢喃", UiText.Get(UiText.WidgetAnimeWatchingMetaKey), "01:12"),
            new WidgetQueueItem("明天", "迷宫饭", UiText.Get(UiText.WidgetAnimeWatchLaterMetaKey), "+2")
        ];

        ConfigureCommands();
    }

    private void ConfigureCommands()
    {
        ShowTodayCommand = new RelayCommand(ShowToday);
        ShowAnimeCommand = new RelayCommand(ShowAnime);
        ToggleInteractionCommand = new RelayCommand(ToggleInteractionState);
        ToggleQuickAddCommand = new RelayCommand(ToggleQuickAdd);
        CloseQuickAddCommand = new RelayCommand(CloseQuickAdd);
        SubmitQuickAddCommand = new AsyncRelayCommand(SubmitQuickAddAsync, CanSubmitQuickAdd);
        CompleteTaskCommand = new AsyncRelayCommand(CompleteTaskAsync);
        RecordPartialTaskCommand = new AsyncRelayCommand(RecordPartialTaskAsync);
        RecordMissedTaskCommand = new AsyncRelayCommand(RecordMissedTaskAsync);
        SnoozeTaskCommand = new RelayCommand(SnoozeTask);
        RescheduleTaskCommand = new RelayCommand(RescheduleTask);
        WatchAnimeCommand = new RelayCommand(WatchAnime);
        WatchLaterCommand = new RelayCommand(WatchLater);
        ScheduleAnimeTaskCommand = new RelayCommand(ScheduleAnimeTask);
        OpenReminderDrawerCommand = new AsyncRelayCommand(OpenReminderDrawerAsync);
        CloseReminderDrawerCommand = new RelayCommand(CloseReminderDrawer);
        MarkReminderReadCommand = new AsyncRelayCommand(
            MarkReminderReadAsync,
            CanMarkReminderRead);
        SimulateAlertCommand = new RelayCommand(SimulateAlert);
        DismissAlertCommand = new RelayCommand(DismissAlert);
    }

    public IReadOnlyList<WidgetQueueItem> TodayUpcomingItems { get; private set; }

    public IReadOnlyList<WidgetQueueItem> AnimeUpcomingItems { get; }

    public IRelayCommand ShowTodayCommand { get; private set; } = null!;

    public IRelayCommand ShowAnimeCommand { get; private set; } = null!;

    public IRelayCommand ToggleInteractionCommand { get; private set; } = null!;

    public IRelayCommand ToggleQuickAddCommand { get; private set; } = null!;

    public IRelayCommand CloseQuickAddCommand { get; private set; } = null!;

    public IAsyncRelayCommand SubmitQuickAddCommand { get; private set; } = null!;

    public IAsyncRelayCommand CompleteTaskCommand { get; private set; } = null!;

    public IAsyncRelayCommand RecordPartialTaskCommand { get; private set; } = null!;

    public IAsyncRelayCommand RecordMissedTaskCommand { get; private set; } = null!;

    public IRelayCommand SnoozeTaskCommand { get; private set; } = null!;

    public IRelayCommand RescheduleTaskCommand { get; private set; } = null!;

    public IRelayCommand WatchAnimeCommand { get; private set; } = null!;

    public IRelayCommand WatchLaterCommand { get; private set; } = null!;

    public IRelayCommand ScheduleAnimeTaskCommand { get; private set; } = null!;

    public IRelayCommand OpenReminderDrawerCommand { get; private set; } = null!;

    public IRelayCommand CloseReminderDrawerCommand { get; private set; } = null!;

    public IRelayCommand MarkReminderReadCommand { get; private set; } = null!;

    public IRelayCommand SimulateAlertCommand { get; private set; } = null!;

    public IRelayCommand DismissAlertCommand { get; private set; } = null!;

    public string ActivePageTitle => _activePage == WidgetPage.Today
        ? UiText.ShellTodayTitle
        : UiText.ShellAnimeTitle;

    public string ActivePageSubtitle => _activePage == WidgetPage.Today
        ? UiText.Get(UiText.WidgetTodayPageSubtitleKey)
        : UiText.Get(UiText.WidgetAnimePageSubtitleKey);

    public string WidgetSubtitle => _isLive
        ? "轻量 Widget · P2 Task loop"
        : UiText.WidgetSubtitle;

    public string WindowTitle => _isLive
        ? "ReminNote Widget"
        : UiText.WidgetWindowTitle;

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

    public bool IsLive => _isLive;

    public bool IsMock => !_isLive;

    public ObservableCollection<ReminderItemViewModel> ReminderItems => _reminderItems;

    public int ReminderCount => _reminderItems.Count;

    public bool HasReminderItems => ReminderCount > 0;

    public bool HasNoReminderItems => !HasReminderItems;

    public ReminderSnapshotStatus ReminderSnapshotStatus => _reminderSnapshot.Status;

    public long? ReminderSnapshotRevision => _reminderSnapshot.SnapshotRevision;

    public string? ReminderSnapshotStatusCode => _reminderSnapshot.StatusCode;

    public bool HasReminderSnapshot => _reminderSnapshot.HasSnapshot;

    public bool ReminderActionsEnabled =>
        _isLive &&
        _reminderQueryService is not null &&
        _reminderCommandClient is not null &&
        _reminderSnapshot.CanWrite;

    public bool HasReminderSnapshotIssue => ReminderSnapshotStatus is not ReminderSnapshotStatus.Fresh;

    public string ReminderEmptyStateLabel => ReminderSnapshotStatus switch
    {
        ReminderSnapshotStatus.Fresh => UiText.ReminderSnapshotEmpty,
        ReminderSnapshotStatus.Stale => UiText.Format(
            UiText.ReminderSnapshotStaleEmptyKey,
            ReminderSnapshotRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"),
        ReminderSnapshotStatus.Unavailable => UiText.Format(
            UiText.ReminderSnapshotUnavailableEmptyKey,
            ReminderSnapshotStatusCode ?? "unknown"),
        _ => UiText.ReminderSnapshotUnavailableEmpty
    };

    public string ReminderSnapshotStatusLabel => ReminderSnapshotStatus switch
    {
        ReminderSnapshotStatus.Fresh => UiText.Format(
            UiText.ReminderSnapshotFreshKey,
            ReminderSnapshotRevision ?? 0),
        ReminderSnapshotStatus.Stale => UiText.Format(
            UiText.ReminderSnapshotStaleKey,
            ReminderSnapshotRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"),
        ReminderSnapshotStatus.Unavailable => UiText.Format(
            UiText.ReminderSnapshotUnavailableKey,
            ReminderSnapshotStatusCode ?? "unknown"),
        _ => ReminderSnapshotStatus.ToString()
    };

    public string ReminderDrawerHeading => _isLive
        ? UiText.Format(UiText.ReminderCenterTitleCountKey, ReminderCount)
        : UiText.WidgetReminderDrawerHeading;

    public string ReminderDrawerTitle => _isLive
        ? UiText.ReminderCenterSubtitle
        : UiText.WidgetReminderDrawerTitle;

    public string PrimaryTaskTitle => _primaryTaskTitle;

    public string PrimaryTaskMeta => _primaryTaskMeta;

    public string PrimaryTaskTimeLabel => _primaryTaskTimeLabel;

    public string SelectedTaskTitle => _selectedTaskItem?.Title ?? (_isLive
        ? _selectionWasInvalidated
            ? "未选择 Task"
            : _livePrimaryTask?.Task.Title ?? "暂无 TODAY Task"
        : _primaryTaskTitle);

    public string SelectedTaskMeta => _selectedTaskItem?.Meta ?? (_isLive
        ? _selectionWasInvalidated
            ? "TODAY · 已选 Task 不可用，请重新选择"
            : _livePrimaryTask is null ? "TODAY · 暂无计划" : FormatTaskMeta(_livePrimaryTask)
        : _primaryTaskMeta);

    public string SelectedTaskTimeLabel => _selectedTaskItem?.Time ?? (_isLive
        ? _selectionWasInvalidated
            ? "—"
            : _livePrimaryTask is null ? "—" : FormatTaskTime(_livePrimaryTask.Task.TimeSpec)
        : _primaryTaskTimeLabel);

    public string TaskStatusLabel => _isLive
        ? _selectionWasInvalidated
            ? "—"
            : FormatStatusLabel(_liveSelectedTask ?? _livePrimaryTask)
        : _isTaskCompleted
            ? UiText.Get(UiText.WidgetTaskCompletedKey)
            : UiText.Get(UiText.WidgetTaskAnytimeKey);

    public int OpenTaskCount => _openTaskCount;

    public int CompletedTaskCount => _completedTaskCount;

    public int NeedsReviewCount => _needsReviewCount;

    public string TodaySummaryFull => _isLive
        ? FormatLiveSummary(includeNeedsReview: true)
        : UiText.WidgetTodaySummaryFull;

    public string TodaySummaryCompact => _isLive
        ? FormatLiveSummary(includeNeedsReview: false)
        : UiText.WidgetTodaySummaryCompact;

    public string DataSourceLabel => _isLive
        ? "LOCAL SQLITE · LIVE TASKS"
        : UiText.WidgetFooterLocalMock;

    public string QuickAddHeading => _isLive
        ? "QUICK ADD · LIVE TASK"
        : UiText.WidgetQuickAddHeading;

    public string QuickAddDescription => _isLive
        ? "输入确定的日期/时间语法，任务会写入本地 SQLite。"
        : UiText.WidgetQuickAddDescription;

    public string QuickAddToolTip => _isLive
        ? "例如：明天 18:00 买东西"
        : UiText.WidgetQuickAddToolTip;

    public bool IsResultActionsVisible =>
        _isLive && _liveSelectedTask is { IsRange: true, IsCompleted: false };

    public string ResultActionHint => _liveSelectedTask?.IsNeedsReview == true
        ? UiText.TodayRangeNeedsResult
        : "RANGE · 记录结果";

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

    public string ReminderFeedback
    {
        get => _reminderFeedback;
        private set
        {
            if (SetProperty(ref _reminderFeedback, value))
            {
                OnPropertyChanged(nameof(HasReminderFeedback));
            }
        }
    }

    public bool HasReminderFeedback => !string.IsNullOrEmpty(_reminderFeedback);

    public bool IsQuickAddOpen
    {
        get => _isQuickAddOpen;
        private set
        {
            if (SetProperty(ref _isQuickAddOpen, value))
            {
                OnPropertyChanged(nameof(QuickAddTriggerLabel));
            }
        }
    }

    public bool IsReminderDrawerOpen
    {
        get => _isReminderDrawerOpen;
        private set
        {
            if (SetProperty(ref _isReminderDrawerOpen, value))
            {
                OnPropertyChanged(nameof(ReminderTriggerLabel));
            }
        }
    }

    public bool IsAlert => _interactionState == WidgetInteractionState.Alert;

    public string QuickAddTriggerLabel => IsQuickAddOpen ? UiText.WidgetClose : UiText.WidgetQuickAdd;

    public string ReminderTriggerLabel => IsReminderDrawerOpen
        ? UiText.WidgetClose
        : _isLive
            ? UiText.Format(UiText.WidgetReminderCountKey, ReminderCount)
            : UiText.WidgetReminders;

    public string AlertTriggerLabel => IsAlert ? UiText.WidgetDismissAlert : UiText.WidgetSimulateReminder;

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

    /// <summary>
    /// Refreshes the widget from the same local Today read model used by the
    /// Main window. The default constructor remains the P0 in-memory fixture.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (!_isLive)
        {
            return;
        }

        try
        {
            await RefreshCoreAsync(
                    WidgetRefreshReason.External,
                    cancellationToken)
                .ConfigureAwait(true);
            await RefreshRemindersAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            TaskFeedback = LiveRefreshFailedFeedback;
            throw;
        }
    }

    private async System.Threading.Tasks.Task RefreshCoreAsync(
        WidgetRefreshReason refreshReason,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var readModel = await _todayQueryService!
                .GetAsync(new TodayQueryRequest(_clock.GetCurrentInstant()), cancellationToken)
                .ConfigureAwait(true);

            var openTasks = readModel.Tasks
                .Where(task => !task.IsCompleted)
                .ToArray();
            var nextPrimaryTask = openTasks.FirstOrDefault()
                ?? (readModel.Tasks.Count > 0 ? readModel.Tasks[0] : null);
            var nextItems = openTasks
                .Select(task => ToQueueItem(task, SelectTask))
                .ToArray();

            var selectedTaskId = _selectedTaskItem?.TaskId;
            var nextSelectedItem = refreshReason == WidgetRefreshReason.OwnResultRecorded
                ? nextItems.FirstOrDefault()
                : selectedTaskId is { } id
                    ? nextItems.FirstOrDefault(item => item.TaskId == id)
                    : _selectionWasInvalidated
                        ? null
                        : nextItems.FirstOrDefault();
            var selectionWasLost = refreshReason == WidgetRefreshReason.External
                && selectedTaskId is not null
                && nextSelectedItem is null;
            var nextSelectionWasInvalidated = refreshReason == WidgetRefreshReason.External
                && (selectionWasLost || (_selectionWasInvalidated && nextSelectedItem is null));

            var nextPrimaryTaskTitle = nextPrimaryTask?.Task.Title ?? "暂无 TODAY Task";
            var nextPrimaryTaskMeta = nextPrimaryTask is null
                ? "TODAY · 暂无计划"
                : FormatTaskMeta(nextPrimaryTask);
            var nextPrimaryTaskTimeLabel = nextPrimaryTask is null
                ? "—"
                : FormatTaskTime(nextPrimaryTask.Task.TimeSpec);

            _currentWorkday = readModel.Workday;
            _openTaskCount = readModel.OpenTaskCount;
            _completedTaskCount = readModel.CompletedTaskCount;
            _needsReviewCount = readModel.NeedsReviewCount;
            _livePrimaryTask = nextPrimaryTask;
            _primaryTaskTitle = nextPrimaryTaskTitle;
            _primaryTaskMeta = nextPrimaryTaskMeta;
            _primaryTaskTimeLabel = nextPrimaryTaskTimeLabel;

            _todayItems.Clear();
            foreach (var item in nextItems)
            {
                _todayItems.Add(item);
            }

            SetSelectedTask(
                nextSelectedItem,
                nextSelectionWasInvalidated ? null : _livePrimaryTask,
                nextSelectionWasInvalidated);

            OnPropertyChanged(nameof(PrimaryTaskTitle));
            OnPropertyChanged(nameof(PrimaryTaskMeta));
            OnPropertyChanged(nameof(PrimaryTaskTimeLabel));
            OnPropertyChanged(nameof(SelectedTaskTitle));
            OnPropertyChanged(nameof(SelectedTaskMeta));
            OnPropertyChanged(nameof(SelectedTaskTimeLabel));
            OnPropertyChanged(nameof(TaskStatusLabel));
            OnPropertyChanged(nameof(OpenTaskCount));
            OnPropertyChanged(nameof(CompletedTaskCount));
            OnPropertyChanged(nameof(NeedsReviewCount));
            OnPropertyChanged(nameof(TodaySummaryFull));
            OnPropertyChanged(nameof(TodaySummaryCompact));
            OnPropertyChanged(nameof(IsResultActionsVisible));
            OnPropertyChanged(nameof(ResultActionHint));

            if (selectionWasLost)
            {
                TaskFeedback = LiveSelectionInvalidatedFeedback;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetimeCancellation.Cancel();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Applies a complete reminder read result without exposing any storage
    /// object to the Widget. Unavailable refreshes preserve an existing model
    /// while marking it unavailable and keeping all actions disabled.
    /// </summary>
    public void ApplyReminderSnapshot(ReminderReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureNotDisposed();
        _reminderSnapshot = _reminderSnapshot.Apply(snapshot);
        if (snapshot.Status != ReminderSnapshotStatus.Unavailable || !_reminderSnapshot.HasSnapshot)
        {
            ReplaceReminderItems(_reminderSnapshot.Items, _reminderSnapshot.SnapshotRevision);
        }

        NotifyReminderSnapshotChanged();
    }

    private async System.Threading.Tasks.Task RefreshRemindersAsync(
        CancellationToken cancellationToken)
    {
        if (!_isLive)
        {
            return;
        }

        EnsureNotDisposed();

        await _reminderRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ReminderReadSnapshot snapshot;
            if (_reminderQueryService is null)
            {
                snapshot = ReminderReadSnapshot.Unavailable(ReminderSnapshotCodes.QueryNotConfigured);
            }
            else
            {
                try
                {
                    snapshot = await _reminderQueryService
                        .GetAsync(ReminderQuery.ActiveOnly, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (!IsFatalException(exception))
                {
                    snapshot = ReminderReadSnapshot.Unavailable(GetReminderFailureCode(exception));
                }
            }

            ApplyReminderSnapshot(snapshot);
            ReminderFeedback = snapshot.Status == ReminderSnapshotStatus.Fresh
                ? string.Empty
                : UiText.Format(
                    UiText.ReminderSnapshotReadFailedKey,
                    snapshot.StatusCode ?? "unknown");
        }
        finally
        {
            _reminderRefreshGate.Release();
        }
    }

    private void SelectTask(WidgetQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        SetSelectedTask(item, item.ReadModel);
        TaskFeedback = $"已选中 Task：「{item.Title}」";
    }

    private void SetSelectedTask(
        WidgetQueueItem? item,
        TodayTaskReadModel? fallbackTask,
        bool selectionWasInvalidated = false)
    {
        foreach (var candidate in _todayItems)
        {
            candidate.SetSelected(ReferenceEquals(candidate, item));
        }

        _selectedTaskItem = item;
        _selectionWasInvalidated = selectionWasInvalidated;
        _liveSelectedTask = item?.ReadModel ?? fallbackTask;
        _currentTaskId = _liveSelectedTask?.Task.Id;
        _isTaskCompleted = _liveSelectedTask?.IsCompleted ?? false;

        OnPropertyChanged(nameof(SelectedTaskTitle));
        OnPropertyChanged(nameof(SelectedTaskMeta));
        OnPropertyChanged(nameof(SelectedTaskTimeLabel));
        OnPropertyChanged(nameof(TaskStatusLabel));
        OnPropertyChanged(nameof(IsResultActionsVisible));
        OnPropertyChanged(nameof(ResultActionHint));
    }

    private static string FormatStatusLabel(TodayTaskReadModel? task)
    {
        if (task is null)
        {
            return "—";
        }

        return task.Task.Result?.ToString() ?? (task.Status switch
        {
            TodayTaskStatus.OVERDUE => UiText.Get(UiText.TodayStatusOverdueKey),
            TodayTaskStatus.AwaitingResult => UiText.Get(UiText.TodayStatusAwaitingResultKey),
            TodayTaskStatus.UPCOMING => UiText.Get(UiText.TodayStatusUpcomingKey),
            TodayTaskStatus.PLANNED => UiText.Get(UiText.TodayStatusPlannedKey),
            TodayTaskStatus.COMPLETED => UiText.Get(UiText.TodayStatusCompletedKey),
            _ => UiText.Get(UiText.TodayStatusPlannedKey)
        });
    }

    private static string FormatTaskMeta(TodayTaskReadModel task)
    {
        var reviewSuffix = task.IsNeedsReview ? " · NEEDS REVIEW" : string.Empty;
        return $"{task.Group} · {FormatStatusLabel(task)}{reviewSuffix}";
    }

    private static string FormatTaskTime(TimeSpec timeSpec) => timeSpec switch
    {
        AnytimeSpec => "ANYTIME",
        TimePointSpec point => FormatLocalTime(point.TimePoint),
        TimeRangeSpec range =>
            $"{FormatLocalTime(range.RangeStart)}–{FormatLocalTime(range.RangeEnd)}",
        _ => "—"
    };

    private static string FormatLocalTime(LocalTime time) =>
        $"{time.Hour:00}:{time.Minute:00}";

    private static WidgetQueueItem ToQueueItem(
        TodayTaskReadModel task,
        Action<WidgetQueueItem> select)
    {
        var marker = task.IsNeedsReview
            ? "REVIEW"
            : task.Task.Result?.ToString() ?? (task.Status switch
            {
                TodayTaskStatus.UPCOMING => "NEXT",
                TodayTaskStatus.OVERDUE => "OPEN",
                _ => "OPEN"
            });
        return new WidgetQueueItem(
            FormatTaskTime(task.Task.TimeSpec),
            task.Task.Title,
            FormatTaskMeta(task),
            marker,
            select,
            task.Task.Id,
            task);
    }

    private string FormatLiveSummary(bool includeNeedsReview)
    {
        var summary = UiText.Format(
            UiText.TodayPlanSummaryKey,
            OpenTaskCount,
            CompletedTaskCount);
        return includeNeedsReview && NeedsReviewCount > 0
            ? $"{summary} · {UiText.Format(UiText.TodayNeedsReviewTextKey, NeedsReviewCount)}"
            : summary;
    }

    private string FormatResultFeedback(string title, TaskResult result) =>
        _selectedTaskItem is { } nextItem
            ? $"已记录「{title}」的 {result} 结果 · 已转到下一开放 Task：「{nextItem.Title}」。"
            : $"已记录「{title}」的 {result} 结果 · 当前没有下一开放 Task。";

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
        if (IsQuickAddOpen)
        {
            CloseQuickAdd();
            return;
        }

        CloseReminderDrawer();
        DismissAlertIfOpen();
        IsQuickAddOpen = true;
    }

    private void CloseQuickAdd()
    {
        IsQuickAddOpen = false;
    }

    private bool CanSubmitQuickAdd() => !string.IsNullOrWhiteSpace(QuickAddText);

    private async System.Threading.Tasks.Task SubmitQuickAddAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = QuickAddText.Trim();
        if (!_isLive)
        {
            QuickAddFeedback = UiText.Format(UiText.WidgetQuickAddFeedbackKey, input);
            QuickAddText = string.Empty;
            return;
        }

        var workday = _currentWorkday == default
            ? new WorkdayService(
                DateTimeZoneProviders.Tzdb.GetSystemDefault(),
                LocalTime.Midnight).GetWorkday(_clock.GetCurrentInstant())
            : _currentWorkday;
        var parsed = TaskParser.Parse(input, workday);
        if (!parsed.IsSuccess)
        {
            QuickAddFeedback = $"无法创建 Task：{parsed.Errors[0].Code}";
            return;
        }

        try
        {
            var created = await _taskApplicationService!
                .CreateAsync(
                    new CreateTaskCommand(parsed.Value!.Title, parsed.Value.TimeSpec),
                    cancellationToken)
                .ConfigureAwait(true);
            QuickAddText = string.Empty;
            if (!await TryRefreshAfterWriteAsync(
                    () => QuickAddFeedback =
                        $"已写入本地 Task：「{created.Title}」，但 TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试",
                    WidgetRefreshReason.External,
                    cancellationToken)
                .ConfigureAwait(true))
            {
                return;
            }

            QuickAddFeedback = $"已写入本地 Task：「{created.Title}」· TODAY 已刷新";
        }
        catch (Exception exception) when (exception is DomainValidationException or AgentCommandException)
        {
            QuickAddFeedback = $"无法创建 Task：{exception.Message}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            QuickAddFeedback = LiveQuickAddWriteFailedFeedback;
        }
    }

    private async System.Threading.Tasks.Task CompleteTaskAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLive)
        {
            _isTaskCompleted = true;
            OnPropertyChanged(nameof(TaskStatusLabel));
            TaskFeedback = UiText.Get(UiText.WidgetTaskCompletedFeedbackKey);
            return;
        }

        await RecordLiveResultAsync(
                TaskResult.COMPLETED,
                null,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RecordPartialTaskAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLive)
        {
            return;
        }

        await RecordLiveResultAsync(
                TaskResult.PARTIAL,
                "由 Widget 记录的 PARTIAL 结果。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RecordMissedTaskAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLive)
        {
            return;
        }

        await RecordLiveResultAsync(
                TaskResult.MISSED,
                null,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RecordLiveResultAsync(
        TaskResult result,
        string? note,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentTaskId is not { } taskId || _liveSelectedTask is null)
        {
            TaskFeedback = "当前没有可记录结果的 TODAY Task。";
            return;
        }

        if (_liveSelectedTask.IsCompleted)
        {
            TaskFeedback = "该 Task 已有结果，不能重复通过 Widget 记录。";
            return;
        }

        if (result == TaskResult.PARTIAL && !_liveSelectedTask.IsRange)
        {
            TaskFeedback = "PARTIAL 结果只适用于 RANGE Task。";
            return;
        }

        var title = _liveSelectedTask.Task.Title;
        try
        {
            var recorded = await _taskApplicationService!
                .RecordResultAsync(
                    new RecordTaskResultCommand(taskId, result, note),
                    cancellationToken)
                .ConfigureAwait(true);
            if (recorded is null)
            {
                if (!await TryRefreshAfterWriteAsync(
                        () => TaskFeedback =
                            "当前 Task 已不存在，且 TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试",
                        WidgetRefreshReason.External,
                        cancellationToken)
                    .ConfigureAwait(true))
                {
                    return;
                }

                TaskFeedback = "当前 Task 已不存在，Widget 已刷新。";
                return;
            }

            if (!await TryRefreshAfterWriteAsync(
                    () => TaskFeedback =
                        $"已记录「{title}」的 {result} 结果，但 TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试",
                    WidgetRefreshReason.OwnResultRecorded,
                    cancellationToken)
                .ConfigureAwait(true))
            {
                return;
            }

            TaskFeedback = FormatResultFeedback(title, result);
        }
        catch (Exception exception) when (exception is DomainValidationException or AgentCommandException)
        {
            TaskFeedback = $"无法记录结果：{exception.Message}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            TaskFeedback = LiveResultWriteFailedFeedback;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryRefreshAfterWriteAsync(
        Action setFailureFeedback,
        WidgetRefreshReason refreshReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshCoreAsync(refreshReason, cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            setFailureFeedback();
            return false;
        }
    }

    internal static bool IsFatalException(Exception exception)
    {
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or System.Runtime.InteropServices.SEHException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsFatalException);
        }

        return exception.InnerException is not null
            && IsFatalException(exception.InnerException);
    }

    private void SnoozeTask()
    {
        if (_isLive)
        {
            TaskFeedback = "P2 暂不在 Widget 自动改期，请在 Main TODAY 编辑计划。";
            return;
        }

        _isTaskCompleted = false;
        OnPropertyChanged(nameof(TaskStatusLabel));
        TaskFeedback = UiText.Get(UiText.WidgetTaskSnoozedFeedbackKey);
    }

    private void RescheduleTask()
    {
        if (_isLive)
        {
            TaskFeedback = "P2 暂不在 Widget 自动改期，请在 Main TODAY 编辑计划。";
            return;
        }

        _isTaskCompleted = false;
        OnPropertyChanged(nameof(TaskStatusLabel));
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

    private async System.Threading.Tasks.Task OpenReminderDrawerAsync()
    {
        if (IsReminderDrawerOpen)
        {
            CloseReminderDrawer();
            return;
        }

        CloseQuickAdd();
        DismissAlertIfOpen();
        IsReminderDrawerOpen = true;
        if (_isLive)
        {
            try
            {
                await RefreshRemindersAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private void CloseReminderDrawer()
    {
        IsReminderDrawerOpen = false;
    }

    private async System.Threading.Tasks.Task MarkReminderReadAsync()
    {
        if (_isLive && _reminderItems.FirstOrDefault() is { } item)
        {
            await item.MarkReadCommand.ExecuteAsync(null).ConfigureAwait(true);
            return;
        }

        IsReminderDrawerOpen = false;
        ReminderFeedback = UiText.Get(UiText.WidgetReminderReadFeedbackKey);
    }

    private bool CanMarkReminderRead() =>
        !_isLive ||
        _reminderItems.FirstOrDefault()?.CanMarkRead == true;

    private async System.Threading.Tasks.Task ExecuteReminderActionAsync(
        ReminderItemViewModel item,
        ResolutionAction action,
        long? snoozeSeconds)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryGetReminderActionRevision(item, out var revision))
        {
            SetReminderActionUnavailable(item);
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await _reminderCommandClient!
                .ExecuteAsync(
                    new ReminderActionCommand(item.Model.InstanceId, action, revision, snoozeSeconds),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            result = new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: GetReminderFailureCode(exception));
        }

        await HandleReminderCommandResultAsync(item, result).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task MarkReminderReadAsync(ReminderItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryGetReminderActionRevision(item, out var revision))
        {
            SetReminderActionUnavailable(item);
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await _reminderCommandClient!
                .MarkReadAsync(
                    new ReminderMarkReadCommand(item.Model.InstanceId, revision),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            result = new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: GetReminderFailureCode(exception));
        }

        await HandleReminderCommandResultAsync(item, result).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task HandleReminderCommandResultAsync(
        ReminderItemViewModel item,
        ReminderCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            item.SetActionFeedback(null);
            ReminderFeedback = string.Empty;
            await RefreshRemindersAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            if (ReminderSnapshotStatus != ReminderSnapshotStatus.Fresh)
            {
                var refreshMessage = ReminderSnapshotStatus == ReminderSnapshotStatus.Stale
                    ? UiText.ReminderActionCommittedStale
                    : UiText.ReminderActionCommittedUnavailable;
                item.SetActionFeedback(refreshMessage);
                ReminderFeedback = refreshMessage;
            }

            return;
        }

        var message = result.Outcome switch
        {
            ReminderCommandOutcome.Stale => UiText.ReminderActionStale,
            ReminderCommandOutcome.Unavailable => UiText.ReminderActionUnavailable,
            _ => UiText.ReminderActionRejected
        };
        var errorCode = result.ErrorCode ?? (result.Outcome switch
        {
            ReminderCommandOutcome.Stale => ProtocolErrorCodes.ExpectedRevisionMismatch,
            ReminderCommandOutcome.Unavailable => ProtocolErrorCodes.AgentUnavailable,
            _ => null
        });
        item.SetActionFeedback(message);
        ReminderFeedback = errorCode is null
            ? message
            : $"{message} · {errorCode}";
        if (result.Outcome == ReminderCommandOutcome.Stale)
        {
            _reminderSnapshot = _reminderSnapshot.MarkStale(errorCode ?? ProtocolErrorCodes.ExpectedRevisionMismatch);
            NotifyReminderSnapshotChanged();
        }
        else if (result.Outcome == ReminderCommandOutcome.Unavailable)
        {
            _reminderSnapshot = _reminderSnapshot.MarkUnavailable(errorCode ?? ProtocolErrorCodes.AgentUnavailable);
            NotifyReminderSnapshotChanged();
        }
    }

    private void ReplaceReminderItems(IReadOnlyList<ReminderReadModel> models, long? revision)
    {
        _reminderItems.Clear();
        foreach (var model in models)
        {
            var rowRevision = revision;
            _reminderItems.Add(new ReminderItemViewModel(
                model,
                () => ReminderActionsEnabled && ReminderSnapshotRevision == rowRevision,
                ExecuteReminderActionAsync,
                MarkReminderReadAsync,
                rowRevision));
        }
    }

    private void NotifyReminderSnapshotChanged()
    {
        OnPropertyChanged(nameof(ReminderItems));
        OnPropertyChanged(nameof(ReminderCount));
        OnPropertyChanged(nameof(HasReminderItems));
        OnPropertyChanged(nameof(HasNoReminderItems));
        OnPropertyChanged(nameof(ReminderSnapshotStatus));
        OnPropertyChanged(nameof(ReminderSnapshotRevision));
        OnPropertyChanged(nameof(ReminderSnapshotStatusCode));
        OnPropertyChanged(nameof(HasReminderSnapshot));
        OnPropertyChanged(nameof(HasReminderSnapshotIssue));
        OnPropertyChanged(nameof(ReminderActionsEnabled));
        OnPropertyChanged(nameof(ReminderSnapshotStatusLabel));
        OnPropertyChanged(nameof(ReminderEmptyStateLabel));
        OnPropertyChanged(nameof(ReminderDrawerHeading));
        OnPropertyChanged(nameof(ReminderDrawerTitle));
        OnPropertyChanged(nameof(ReminderTriggerLabel));
        MarkReminderReadCommand.NotifyCanExecuteChanged();
        foreach (var item in _reminderItems)
        {
            item.RefreshCommandState();
        }
    }

    private static string GetReminderFailureCode(Exception exception) => exception switch
    {
        ReminNote.Agent.Runtime.AgentCommandException agentException => agentException.Code,
        FileNotFoundException or DirectoryNotFoundException or IOException => ProtocolErrorCodes.StorageNotReady,
        _ => ProtocolErrorCodes.AgentUnavailable
    };

    private bool TryGetReminderActionRevision(ReminderItemViewModel item, out long revision)
    {
        if (!ReminderActionsEnabled ||
            item.SnapshotRevision is not { } rowRevision ||
            ReminderSnapshotRevision != rowRevision)
        {
            revision = default;
            return false;
        }

        revision = rowRevision;
        return true;
    }

    private void SetReminderActionUnavailable(ReminderItemViewModel item)
    {
        var message = ReminderSnapshotStatus == ReminderSnapshotStatus.Stale
            ? UiText.ReminderActionStale
            : UiText.ReminderActionUnavailable;
        item.SetActionFeedback(message);
        ReminderFeedback = message;
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private void SimulateAlert()
    {
        if (IsAlert)
        {
            DismissAlert();
            return;
        }

        CloseQuickAdd();
        CloseReminderDrawer();
        _stateBeforeAlert = _interactionState;

        _interactionState = WidgetInteractionState.Alert;
        NotifyInteractionStateChanged();
    }

    private void DismissAlert()
    {
        if (!IsAlert)
        {
            return;
        }

        _interactionState = _stateBeforeAlert;
        NotifyInteractionStateChanged();
    }

    private void DismissAlertIfOpen()
    {
        if (IsAlert)
        {
            DismissAlert();
        }
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
        OnPropertyChanged(nameof(AlertTriggerLabel));
    }

}
