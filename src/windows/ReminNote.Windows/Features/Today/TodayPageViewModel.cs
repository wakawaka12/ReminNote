using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Tasks.Parsing;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Windows.Resources.Localization;
using ReminNote.Windows.ViewModels;

using DomainTaskResult = ReminNote.Core.Tasks.TaskResult;
using RemoteTodayTaskGroup = ReminNote.Core.Today.TodayTaskGroup;

namespace ReminNote.Windows.Features.Today;

public sealed class TodayPageViewModel : ShellPageViewModel
{
    private const string LivePageDescription = "TODAY · 本地 Task 计划与结果";
    private const string LiveQuickAddOpenedMessage = "Quick Add 已展开 · 支持今天、明天、后天、日期、时间和 RANGE";
    private const string LiveQuickAddCancelledMessage = "已取消 Quick Add · 未写入 Task";
    private const string LiveLoadedMessage = "真实 Task 已加载 · 数据来自本地应用查询边界";
    private const string LiveInitialLoadFailedMessage = "真实 Task 初次加载失败 · 请点击刷新重试";
    private const string LiveRefreshedMessage = "TODAY 已刷新 · 已重新读取本地 Task";
    private const string LiveNoReviewMessage = "当前没有需要复盘的 RANGE Task";
    private const string LiveRefreshFailedMessage = "TODAY 刷新失败 · 请稍后重试";
    private const string LiveWriteRefreshFailedMessage = "Task 已写入，但 TODAY 刷新失败 · 请点击刷新重试";
    private const string LiveWriteFailedMessage = "Task 写入失败 · 原有 TODAY 数据保持不变";
    private const string LiveContinueUnavailableMessage = "只有已记录 PARTIAL 结果的 RANGE Task 才能继续";
    private const string LivePinNotPersistedMessage = "置顶状态仅当前页面有效 · P2 尚未持久化 PIN";
    private const string LiveRescheduleOpenedMessage = "改期面板已打开 · 支持今天、明天、后天、yyyy-MM-dd 和 HH:mm[-HH:mm]";
    private const string LiveRescheduleCancelledMessage = "已取消改期 · 原计划保持不变";
    private const string LiveRescheduleUnavailableMessage = "只有未记录结果的 Task 才能改期";
    private const string LiveReorderBoundaryMessage = "已到达分组边界 · 没有可交换的相邻任务";
    private const string LiveReorderCrossDateMessage = "排序仅在同一计划日期和分组内生效 · 跨日期请使用改期";

    private static readonly TodayGroupDefinition[] GroupDefinitions =
    [
        new(TodayTaskGroup.Overdue, UiText.Get(UiText.TodayGroupOverdueTitleKey), UiText.Get(UiText.TodayGroupOverdueSubtitleKey), true),
        new(TodayTaskGroup.Morning, UiText.Get(UiText.TodayGroupMorningTitleKey), UiText.Get(UiText.TodayGroupMorningSubtitleKey), true),
        new(TodayTaskGroup.Afternoon, UiText.Get(UiText.TodayGroupAfternoonTitleKey), UiText.Get(UiText.TodayGroupAfternoonSubtitleKey), true),
        new(TodayTaskGroup.Evening, UiText.Get(UiText.TodayGroupEveningTitleKey), UiText.Get(UiText.TodayGroupEveningSubtitleKey), true),
        new(TodayTaskGroup.Anytime, UiText.Get(UiText.TodayGroupAnytimeTitleKey), UiText.Get(UiText.TodayGroupAnytimeSubtitleKey), true),
        new(TodayTaskGroup.Completed, UiText.Get(UiText.TodayGroupCompletedTitleKey), UiText.Get(UiText.TodayGroupCompletedSubtitleKey), false)
    ];

    private readonly List<TodayTaskViewModel> _tasks = [];
    private readonly ITaskApplicationService? _taskApplicationService;
    private readonly ITodayQueryService? _todayQueryService;
    private readonly IClock _clock;
    private readonly bool _isLive;
    private int _quickTaskNumber = 1;
    private bool _isQuickAddOpen;
    private string _quickAddText = string.Empty;
    private string _interactionMessage = UiText.Get(UiText.TodayInteractionLoadedKey);
    private string _dateLabel = string.Empty;
    private string _weekdayLabel = string.Empty;
    private TodayTaskViewModel? _selectedTask;
    private LocalDate _currentWorkday;
    private bool _isRescheduleOpen;
    private string _rescheduleDateText = string.Empty;
    private string _rescheduleTimeText = string.Empty;
    private TodayTaskViewModel? _rescheduleTarget;

    public TodayPageViewModel()
        : this(new TodayMockDataService())
    {
    }

    public TodayPageViewModel(ITodayMockDataService mockDataService)
        : base(UiText.TodayPageDescription)
    {
        ArgumentNullException.ThrowIfNull(mockDataService);

        var snapshot = mockDataService.Load();
        _dateLabel = snapshot.DateLabel;
        _weekdayLabel = snapshot.WeekdayLabel;
        _clock = SystemClock.Instance;
        _isLive = false;
        BuildGroups();

        foreach (var mockTask in snapshot.Tasks)
        {
            _tasks.Add(CreateTask(mockTask));
        }

        PrimaryTask = _tasks.Single(task => task.Id == snapshot.PrimaryTaskId);
        RebuildGroups();
        ConfigureCommands();
        SelectTask(PrimaryTask);
    }

    public TodayPageViewModel(
        ITodayQueryService todayQueryService,
        ITaskApplicationService taskApplicationService,
        IClock clock)
        : base(LivePageDescription)
    {
        _todayQueryService = todayQueryService ?? throw new ArgumentNullException(nameof(todayQueryService));
        _taskApplicationService = taskApplicationService ?? throw new ArgumentNullException(nameof(taskApplicationService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _isLive = true;
        BuildGroups();
        ConfigureCommands();
        LoadInitialReadModel();
    }

    public event Action<TodayTaskViewModel>? QuickTaskAdded;

    public ObservableCollection<TodayTaskGroupViewModel> Groups { get; } = [];

    public TodayTaskViewModel? PrimaryTask { get; private set; }

    public TodayTaskViewModel? SelectedTask
    {
        get => _selectedTask;
        private set => SetProperty(ref _selectedTask, value);
    }

    public string DateLabel
    {
        get => _dateLabel;
        private set => SetProperty(ref _dateLabel, value);
    }

    public string WeekdayLabel
    {
        get => _weekdayLabel;
        private set => SetProperty(ref _weekdayLabel, value);
    }

    public string DateSummary => string.IsNullOrEmpty(WeekdayLabel)
        ? DateLabel
        : $"{DateLabel} · {WeekdayLabel}";

    public string MockBadge => _isLive ? string.Empty : UiText.TodayMockBadge;

    public bool IsMock => !_isLive;

    public string QuickAddDescription => _isLive
        ? "支持今天、明天、后天、yyyy-MM-dd、HH:mm 和 HH:mm-HH:mm；不支持的标签/优先级语法会被拒绝。"
        : UiText.TodayQuickAddDescription;

    public string NeedsReviewText => UiText.Format(UiText.TodayNeedsReviewTextKey, NeedsReviewCount);

    public string NeedsReviewDescription => NeedsReviewCount == 0
        ? UiText.Get(UiText.TodayNeedsReviewNoneKey)
        : UiText.Get(UiText.TodayNeedsReviewPendingKey);

    public string PlanSummary => UiText.Format(UiText.TodayPlanSummaryKey, OpenTaskCount, CompletedTaskCount);

    public int OpenTaskCount => _tasks.Count(task => !task.IsCompleted);

    public int CompletedTaskCount => _tasks.Count(task => task.IsCompleted);

    public int NeedsReviewCount => _tasks.Count(task => task.IsNeedsReviewVisible);

    public bool IsQuickAddOpen
    {
        get => _isQuickAddOpen;
        private set => SetProperty(ref _isQuickAddOpen, value);
    }

    public string QuickAddText
    {
        get => _quickAddText;
        set => SetProperty(ref _quickAddText, value);
    }

    public bool IsRescheduleOpen
    {
        get => _isRescheduleOpen;
        private set => SetProperty(ref _isRescheduleOpen, value);
    }

    public string RescheduleTaskTitle => _rescheduleTarget?.Title ?? string.Empty;

    public string RescheduleDateText
    {
        get => _rescheduleDateText;
        set => SetProperty(ref _rescheduleDateText, value);
    }

    public string RescheduleTimeText
    {
        get => _rescheduleTimeText;
        set => SetProperty(ref _rescheduleTimeText, value);
    }

    public string InteractionMessage
    {
        get => _interactionMessage;
        private set => SetProperty(ref _interactionMessage, value);
    }

    public IRelayCommand OpenQuickAddCommand { get; private set; } = null!;

    public IRelayCommand CancelQuickAddCommand { get; private set; } = null!;

    public IAsyncRelayCommand AddQuickTaskCommand { get; private set; } = null!;

    public IRelayCommand OpenNeedsReviewCommand { get; private set; } = null!;

    public IAsyncRelayCommand RefreshCommand { get; private set; } = null!;

    public IAsyncRelayCommand ConfirmRescheduleCommand { get; private set; } = null!;

    public IRelayCommand CancelRescheduleCommand { get; private set; } = null!;

    /// <summary>
    /// Loads the real Today read model. The no-argument constructor remains a
    /// deterministic P0 compatibility fixture and intentionally does nothing.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_isLive)
        {
            return;
        }

        var readModel = await _todayQueryService!
            .GetAsync(new TodayQueryRequest(_clock.GetCurrentInstant()), cancellationToken)
            .ConfigureAwait(true);
        ApplyReadModel(readModel);
    }

    private void ConfigureCommands()
    {
        OpenQuickAddCommand = new RelayCommand(OpenQuickAdd);
        CancelQuickAddCommand = new RelayCommand(CancelQuickAdd);
        AddQuickTaskCommand = new AsyncRelayCommand(AddQuickTaskAsync);
        OpenNeedsReviewCommand = new RelayCommand(OpenNeedsReview);
        RefreshCommand = new AsyncRelayCommand(RefreshFromCommandAsync);
        ConfirmRescheduleCommand = new AsyncRelayCommand(ConfirmRescheduleAsync);
        CancelRescheduleCommand = new RelayCommand(CancelReschedule);
    }

    private void LoadInitialReadModel()
    {
        try
        {
            var readModel = _todayQueryService!
                .GetAsync(new TodayQueryRequest(_clock.GetCurrentInstant()))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            ApplyReadModel(readModel);
            InteractionMessage = LiveLoadedMessage;
        }
        catch (Exception)
        {
            InteractionMessage = LiveInitialLoadFailedMessage;
        }
    }

    private void ApplyReadModel(TodayReadModel readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        var selectedId = SelectedTask?.DomainTaskId;
        _currentWorkday = readModel.Workday;
        DateLabel = FormatDate(readModel.Workday);
        WeekdayLabel = FormatWeekday(readModel.Workday);

        _tasks.Clear();
        foreach (var task in readModel.Tasks)
        {
            _tasks.Add(CreateTask(task));
        }

        PrimaryTask = _tasks.FirstOrDefault(task => !task.IsCompleted) ?? _tasks.FirstOrDefault();
        OnPropertyChanged(nameof(PrimaryTask));
        RebuildGroups();

        var selected = selectedId is { } id
            ? _tasks.FirstOrDefault(task => task.DomainTaskId == id)
            : null;
        SelectTask(selected ?? PrimaryTask);
        NotifySummaryChanged();
    }

    private async System.Threading.Tasks.Task RefreshFromCommandAsync()
    {
        if (!_isLive)
        {
            InteractionMessage = UiText.Get(UiText.TodayInteractionLoadedKey);
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(true);
            InteractionMessage = LiveRefreshedMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveRefreshFailedMessage;
        }
    }

    private void BuildGroups()
    {
        foreach (var definition in GroupDefinitions)
        {
            Groups.Add(new TodayTaskGroupViewModel(
                definition.Group,
                definition.Title,
                definition.Subtitle,
                definition.IsExpanded));
        }
    }

    private TodayTaskViewModel CreateTask(TodayMockTask mockTask) =>
        new(
            mockTask,
            SelectTask,
            ToggleTaskCompletionAsync,
            ToggleTaskPin,
            RecordPartialResultAsync,
            RecordMissedResultAsync,
            ContinueTaskAsync,
            static _ => { },
            static _ => System.Threading.Tasks.Task.CompletedTask,
            static _ => System.Threading.Tasks.Task.CompletedTask);

    private TodayTaskViewModel CreateTask(TodayTaskReadModel task) =>
        new(
            task,
            SelectTask,
            ToggleTaskCompletionAsync,
            ToggleTaskPin,
            RecordPartialResultAsync,
            RecordMissedResultAsync,
            ContinueTaskAsync,
            OpenRescheduleFor,
            MoveTaskUpAsync,
            MoveTaskDownAsync);

    private void OpenQuickAdd()
    {
        IsQuickAddOpen = true;
        IsRescheduleOpen = false;
        InteractionMessage = _isLive
            ? LiveQuickAddOpenedMessage
            : UiText.Get(UiText.TodayInteractionQuickAddOpenedKey);
    }

    private void CancelQuickAdd()
    {
        QuickAddText = string.Empty;
        IsQuickAddOpen = false;
        InteractionMessage = _isLive
            ? LiveQuickAddCancelledMessage
            : UiText.Get(UiText.TodayInteractionQuickAddCancelledKey);
    }

    private async System.Threading.Tasks.Task AddQuickTaskAsync()
    {
        if (!_isLive)
        {
            AddMockQuickTask();
            return;
        }

        var logicalToday = ResolveLogicalToday();
        var parsed = TaskParser.Parse(QuickAddText, logicalToday);
        if (!parsed.IsSuccess)
        {
            InteractionMessage = $"无法创建 Task：{parsed.Errors[0].Code}";
            return;
        }

        TaskSnapshot created;
        try
        {
            created = await _taskApplicationService!
                .CreateAsync(new CreateTaskCommand(parsed.Value!.Title, parsed.Value.TimeSpec))
                .ConfigureAwait(true);
        }
        catch (DomainValidationException exception)
        {
            InteractionMessage = $"无法创建 Task：{exception.Message}";
            return;
        }
        catch (TaskWriteGateBusyException)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }

        QuickAddText = string.Empty;
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteRefreshFailedMessage;
            return;
        }

        var createdTask = _tasks.FirstOrDefault(task => task.DomainTaskId == created.Id);
        if (createdTask is not null)
        {
            SelectTask(createdTask);
            QuickTaskAdded?.Invoke(createdTask);
        }

        InteractionMessage = $"已添加「{created.Title}」到本地 Task · TODAY 已刷新";
    }

    private void AddMockQuickTask()
    {
        var title = QuickAddText.Trim();
        if (title.Length == 0)
        {
            InteractionMessage = UiText.Get(UiText.TodayInteractionQuickAddEmptyKey);
            return;
        }

        var mockTask = new TodayMockTask(
            Id: $"quick-add-{_quickTaskNumber++:00}",
            Title: title,
            Group: TodayTaskGroup.Anytime,
            TimeLabel: "ANYTIME",
            Status: TodayMockStatus.Planned,
            PriorityLabel: "NORMAL",
            CategoryLabel: "Quick Add",
            TimeShapeLabel: "ANYTIME",
            Notes: "由 Quick Add 创建的 P0 内存 Mock 任务。",
            IsCompleted: false,
            IsPinned: false,
            IsNeedsReview: false);

        var task = CreateTask(mockTask);
        _tasks.Add(task);
        RebuildGroups();
        SelectTask(task);
        QuickAddText = string.Empty;
        InteractionMessage = UiText.Format(UiText.TodayInteractionQuickAddAddedKey, task.Title);
        NotifySummaryChanged();
        QuickTaskAdded?.Invoke(task);
    }

    private LocalDate ResolveLogicalToday() => _currentWorkday == default
        ? new WorkdayService(DateTimeZoneProviders.Tzdb.GetSystemDefault(), LocalTime.Midnight)
            .GetWorkday(_clock.GetCurrentInstant())
        : _currentWorkday;

    private void OpenRescheduleFor(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_isLive || task.DomainTaskId is null || task.IsCompleted)
        {
            if (_isLive)
            {
                InteractionMessage = LiveRescheduleUnavailableMessage;
            }

            return;
        }

        _rescheduleTarget = task;
        RescheduleDateText = FormatRescheduleDate(task.PlanDate!.Value);
        RescheduleTimeText = FormatRescheduleTime(task.LiveTimeSpec);
        IsQuickAddOpen = false;
        IsRescheduleOpen = true;
        OnPropertyChanged(nameof(RescheduleTaskTitle));
        InteractionMessage = LiveRescheduleOpenedMessage;
    }

    private async System.Threading.Tasks.Task ConfirmRescheduleAsync()
    {
        if (!_isLive)
        {
            IsRescheduleOpen = false;
            return;
        }

        if (_rescheduleTarget is not { } target || target.DomainTaskId is not { } taskId)
        {
            IsRescheduleOpen = false;
            return;
        }

        var datePart = RescheduleDateText.Trim();
        var timePart = RescheduleTimeText.Trim();
        if (datePart.Length == 0 && timePart.Length == 0)
        {
            InteractionMessage = "无法改期：task.parser.empty";
            return;
        }

        var combined = timePart.Length == 0 ? datePart : $"{datePart} {timePart}";
        var parsed = TaskParser.Parse($"{combined} 改期占位", ResolveLogicalToday());
        if (!parsed.IsSuccess)
        {
            InteractionMessage = $"无法改期：{parsed.Errors[0].Code}";
            return;
        }

        TaskSnapshot? updated;
        try
        {
            updated = await _taskApplicationService!
                .UpdateAsync(new UpdateTaskCommand(taskId, target.Title, parsed.Value!.TimeSpec))
                .ConfigureAwait(true);
        }
        catch (DomainValidationException exception)
        {
            InteractionMessage = $"无法改期：{exception.Message}";
            return;
        }
        catch (TaskWriteGateBusyException)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }

        if (updated is null)
        {
            InteractionMessage = "无法改期：Task 不存在，TODAY 数据未改变";
            return;
        }

        CancelRescheduleCore();
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteRefreshFailedMessage;
            return;
        }

        InteractionMessage = $"已改期「{updated.Title}」· 旧计划已保存到 task_history";
    }

    private void CancelReschedule()
    {
        CancelRescheduleCore();
        InteractionMessage = _isLive
            ? LiveRescheduleCancelledMessage
            : UiText.Get(UiText.TodayInteractionLoadedKey);
    }

    private void CancelRescheduleCore()
    {
        _rescheduleTarget = null;
        RescheduleDateText = string.Empty;
        RescheduleTimeText = string.Empty;
        IsRescheduleOpen = false;
        OnPropertyChanged(nameof(RescheduleTaskTitle));
    }

    private System.Threading.Tasks.Task MoveTaskUpAsync(TodayTaskViewModel task) =>
        MoveTaskWithinGroupAsync(task, -1);

    private System.Threading.Tasks.Task MoveTaskDownAsync(TodayTaskViewModel task) =>
        MoveTaskWithinGroupAsync(task, 1);

    private async System.Threading.Tasks.Task MoveTaskWithinGroupAsync(
        TodayTaskViewModel task,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_isLive || task.DomainTaskId is not { } taskId || task.PlanDate is not { } planDate)
        {
            return;
        }

        var group = Groups.FirstOrDefault(candidate => candidate.Group == task.CurrentGroup);
        if (group is null)
        {
            return;
        }

        var items = group.Items.ToList();
        var index = items.FindIndex(candidate => ReferenceEquals(candidate, task));
        var neighborIndex = index + offset;
        if (index < 0 || neighborIndex < 0 || neighborIndex >= items.Count)
        {
            InteractionMessage = LiveReorderBoundaryMessage;
            return;
        }

        var neighbor = items[neighborIndex];
        if (neighbor.DomainTaskId is not { } neighborId ||
            neighbor.PlanDate is not { } neighborPlanDate ||
            neighborPlanDate != planDate)
        {
            InteractionMessage = LiveReorderCrossDateMessage;
            return;
        }

        try
        {
            var first = await _taskApplicationService!
                .ReorderAsync(new ReorderTaskCommand(taskId, neighbor.SortOrder))
                .ConfigureAwait(true);
            if (first is null)
            {
                InteractionMessage = "无法排序：Task 不存在，TODAY 数据未改变";
                return;
            }

            var second = await _taskApplicationService!
                .ReorderAsync(new ReorderTaskCommand(neighborId, task.SortOrder))
                .ConfigureAwait(true);
            if (second is null)
            {
                InteractionMessage = "无法排序：相邻 Task 不存在，TODAY 数据未改变";
                return;
            }
        }
        catch (TaskWriteGateBusyException)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteRefreshFailedMessage;
            return;
        }

        InteractionMessage = $"已调整「{task.Title}」的分组内顺序 · 仅影响同日期同分组排序";
    }

    private static string FormatRescheduleDate(LocalDate date) =>
        LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd").Format(date);

    private static string FormatRescheduleTime(TimeSpec? timeSpec) => timeSpec switch
    {
        TimePointSpec point => $"{point.TimePoint.Hour:00}:{point.TimePoint.Minute:00}",
        TimeRangeSpec range => $"{range.RangeStart.Hour:00}:{range.RangeStart.Minute:00}" +
            $"-{range.RangeEnd.Hour:00}:{range.RangeEnd.Minute:00}",
        _ => string.Empty
    };

    private void OpenNeedsReview()
    {
        var task = _tasks.FirstOrDefault(candidate => candidate.IsNeedsReviewVisible);
        if (task is null)
        {
            InteractionMessage = _isLive
                ? LiveNoReviewMessage
                : UiText.Get(UiText.TodayInteractionNoReviewKey);
            return;
        }

        SelectTask(task);
        InteractionMessage = _isLive
            ? $"已定位到待复盘 Task「{task.Title}」· 请记录结果"
            : UiText.Format(UiText.TodayInteractionReviewLocatedKey, task.Title);
    }

    private void SelectTask(TodayTaskViewModel? task)
    {
        foreach (var candidate in _tasks)
        {
            candidate.SetSelected(ReferenceEquals(candidate, task));
        }

        SelectedTask = task;
    }

    private async System.Threading.Tasks.Task ToggleTaskCompletionAsync(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_isLive)
        {
            if (task.IsCompleted || task.DomainTaskId is not { } taskId)
            {
                InteractionMessage = "该 Task 已有结果，不能通过 DONE 按钮撤销事实结果。";
                return;
            }

            await RecordLiveResultAsync(taskId, DomainTaskResult.COMPLETED, null, task.Title)
                .ConfigureAwait(true);
            return;
        }

        task.SetCompleted(!task.IsCompleted);
        RebuildGroups();
        NotifySummaryChanged();
        InteractionMessage = task.IsCompleted
            ? UiText.Format(UiText.TodayInteractionCompletedKey, task.Title)
            : UiText.Format(UiText.TodayInteractionRestoredKey, task.Title);
    }

    private System.Threading.Tasks.Task RecordPartialResultAsync(TodayTaskViewModel task) =>
        RecordResultAsync(task, DomainTaskResult.PARTIAL, "由 TODAY 记录的部分完成结果");

    private System.Threading.Tasks.Task RecordMissedResultAsync(TodayTaskViewModel task) =>
        RecordResultAsync(task, DomainTaskResult.MISSED, null);

    private async System.Threading.Tasks.Task RecordResultAsync(
        TodayTaskViewModel task,
        DomainTaskResult result,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_isLive)
        {
            task.SetCompleted(true);
            RebuildGroups();
            NotifySummaryChanged();
            return;
        }

        if (task.IsCompleted)
        {
            InteractionMessage = "该 Task 已有结果，不能重复记录结果。";
            return;
        }

        if (task.DomainTaskId is { } taskId)
        {
            await RecordLiveResultAsync(taskId, result, note, task.Title)
                .ConfigureAwait(true);
        }
    }

    private async System.Threading.Tasks.Task RecordLiveResultAsync(
        TaskId taskId,
        DomainTaskResult result,
        string? note,
        string title)
    {
        TaskSnapshot? recorded;
        try
        {
            recorded = await _taskApplicationService!
                .RecordResultAsync(new RecordTaskResultCommand(taskId, result, note))
                .ConfigureAwait(true);
        }
        catch (DomainValidationException exception)
        {
            InteractionMessage = $"无法记录结果：{exception.Message}";
            return;
        }
        catch (TaskWriteGateBusyException)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }

        if (recorded is null)
        {
            InteractionMessage = "无法记录结果：Task 不存在，TODAY 数据未改变";
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteRefreshFailedMessage;
            return;
        }

        InteractionMessage = ResultRecordedMessage(title, result);
    }

    private async System.Threading.Tasks.Task ContinueTaskAsync(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_isLive || task.DomainTaskId is not { } sourceTaskId || !task.IsPartial)
        {
            InteractionMessage = _isLive
                ? LiveContinueUnavailableMessage
                : "继续关系只对真实 RANGE Task 生效。";
            return;
        }

        TaskSnapshot? continued;
        try
        {
            continued = await _taskApplicationService!
                .ContinueAsync(new ContinueTaskCommand(
                    sourceTaskId,
                    $"{task.Title}（继续）",
                    TimeSpec.Anytime(_currentWorkday)))
                .ConfigureAwait(true);
        }
        catch (DomainValidationException exception)
        {
            InteractionMessage = $"无法创建继续 Task：{exception.Message}";
            return;
        }
        catch (TaskWriteGateBusyException)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteFailedMessage;
            return;
        }

        if (continued is null)
        {
            InteractionMessage = "无法创建继续 Task：源 Task 不存在，TODAY 数据未改变";
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            InteractionMessage = LiveWriteRefreshFailedMessage;
            return;
        }

        InteractionMessage = $"已创建「{continued.Title}」· 已建立继续关系并刷新 TODAY";
    }

    private void ToggleTaskPin(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.SetPinned(!task.IsPinned);
        InteractionMessage = _isLive
            ? LivePinNotPersistedMessage
            : task.IsPinned
                ? UiText.Format(UiText.TodayInteractionPinnedKey, task.Title)
                : UiText.Format(UiText.TodayInteractionUnpinnedKey, task.Title);
    }

    private static string ResultRecordedMessage(string title, DomainTaskResult result) =>
        $"已记录「{title}」的 {result} 结果 · RecordedAt 只表示记录动作时间";

    private void RebuildGroups()
    {
        foreach (var group in Groups)
        {
            group.SetItems(_tasks.Where(task => task.CurrentGroup == group.Group));
        }
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(DateSummary));
        OnPropertyChanged(nameof(OpenTaskCount));
        OnPropertyChanged(nameof(CompletedTaskCount));
        OnPropertyChanged(nameof(NeedsReviewCount));
        OnPropertyChanged(nameof(NeedsReviewText));
        OnPropertyChanged(nameof(NeedsReviewDescription));
        OnPropertyChanged(nameof(PlanSummary));
    }

    private static string FormatDate(LocalDate date) =>
        $"{date.Year}年{date.Month}月{date.Day}日";

    private static string FormatWeekday(LocalDate date) => date.DayOfWeek switch
    {
        IsoDayOfWeek.Monday => "星期一",
        IsoDayOfWeek.Tuesday => "星期二",
        IsoDayOfWeek.Wednesday => "星期三",
        IsoDayOfWeek.Thursday => "星期四",
        IsoDayOfWeek.Friday => "星期五",
        IsoDayOfWeek.Saturday => "星期六",
        IsoDayOfWeek.Sunday => "星期日",
        _ => string.Empty
    };

    private sealed record TodayGroupDefinition(
        TodayTaskGroup Group,
        string Title,
        string Subtitle,
        bool IsExpanded);
}

public sealed class TodayTaskGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public TodayTaskGroupViewModel(
        TodayTaskGroup group,
        string title,
        string subtitle,
        bool isExpanded)
    {
        Group = group;
        Title = title;
        Subtitle = subtitle;
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(ToggleExpanded);
    }

    public TodayTaskGroup Group { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public ObservableCollection<TodayTaskViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string ItemCountText => UiText.Format(UiText.TodayItemsKey, Items.Count);

    public string AutomationName => Title;

    public string AutomationHelpText => ItemCountText;

    public string ExpandGlyph => IsExpanded ? "−" : "+";

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public IRelayCommand ToggleCommand { get; }

    internal void SetItems(IEnumerable<TodayTaskViewModel> tasks)
    {
        Items.Clear();
        foreach (var task in tasks)
        {
            Items.Add(task);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ItemCountText));
    }

    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

public sealed class TodayTaskViewModel : ObservableObject
{
    private readonly TodayMockStatus? _sourceStatus;
    private TodayTaskStatus? _liveStatus;
    private readonly DomainTaskResult? _liveResult;
    private readonly bool _isLive;
    private readonly bool _isContinuation;
    private bool _isCompleted;
    private bool _isPinned;
    private bool _isSelected;
    private bool _isNeedsReview;

    internal TodayTaskViewModel(
        TodayMockTask mockTask,
        Action<TodayTaskViewModel> select,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> toggleCompletion,
        Action<TodayTaskViewModel> togglePin,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordPartial,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordMissed,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> continueTask,
        Action<TodayTaskViewModel> reschedule,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveUp,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveDown)
    {
        ArgumentNullException.ThrowIfNull(mockTask);
        InitializeCallbacks(select, toggleCompletion, togglePin, recordPartial, recordMissed, continueTask, reschedule, moveUp, moveDown);

        _isLive = false;
        Id = mockTask.Id;
        Title = mockTask.Title;
        OriginalGroup = mockTask.Group;
        TimeLabel = mockTask.TimeLabel;
        _sourceStatus = mockTask.Status;
        PriorityLabel = mockTask.PriorityLabel;
        CategoryLabel = mockTask.CategoryLabel;
        TimeShapeLabel = mockTask.TimeShapeLabel;
        Notes = mockTask.Notes;
        _isCompleted = mockTask.IsCompleted;
        _isPinned = mockTask.IsPinned;
        _isNeedsReview = mockTask.IsNeedsReview;
    }

    internal TodayTaskViewModel(
        TodayTaskReadModel readModel,
        Action<TodayTaskViewModel> select,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> toggleCompletion,
        Action<TodayTaskViewModel> togglePin,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordPartial,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordMissed,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> continueTask,
        Action<TodayTaskViewModel> reschedule,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveUp,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveDown)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        InitializeCallbacks(select, toggleCompletion, togglePin, recordPartial, recordMissed, continueTask, reschedule, moveUp, moveDown);

        _isLive = true;
        Id = readModel.Task.Id.ToString();
        DomainTaskId = readModel.Task.Id;
        Title = readModel.Task.Title;
        OriginalGroup = MapGroup(readModel.Group);
        TimeLabel = FormatTime(readModel.Task.TimeSpec);
        _liveStatus = readModel.Status;
        _liveResult = readModel.Task.Result;
        _isContinuation = readModel.Task.ContinuedFromTaskId is not null;
        PlanDate = readModel.Task.TimeSpec.LocalDate;
        LiveTimeSpec = readModel.Task.TimeSpec;
        SortOrder = readModel.Task.SortOrder;
        PriorityLabel = "NORMAL";
        CategoryLabel = "Task";
        TimeShapeLabel = readModel.Task.TimeType.ToString();
        Notes = CreateLiveNotes(readModel);
        _isCompleted = readModel.IsCompleted;
        _isNeedsReview = readModel.IsNeedsReview;
    }

    public string Id { get; }

    public TaskId? DomainTaskId { get; }

    public LocalDate? PlanDate { get; }

    public TimeSpec? LiveTimeSpec { get; }

    public int SortOrder { get; }

    public string Title { get; }

    public TodayTaskGroup OriginalGroup { get; }

    public string TimeLabel { get; }

    public string PriorityLabel { get; }

    public string CategoryLabel { get; }

    public string TimeShapeLabel { get; }

    public string Notes { get; }

    public bool IsNeedsReview => _isNeedsReview;

    public bool IsNeedsReviewVisible => IsNeedsReview && !IsCompleted;

    public bool IsRange => TimeShapeLabel == nameof(TaskTimeType.RANGE);

    public bool IsPartial => _liveResult == DomainTaskResult.PARTIAL;

    public bool CanContinue => IsPartial;

    public bool IsCompletionActionEnabled => !_isLive || !IsCompleted;

    public TodayTaskGroup CurrentGroup => IsCompleted
        ? TodayTaskGroup.Completed
        : OriginalGroup;

    public bool IsCompleted
    {
        get => _isCompleted;
        private set => SetProperty(ref _isCompleted, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        private set => SetProperty(ref _isPinned, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public string StatusLabel => _liveResult switch
        {
            DomainTaskResult.COMPLETED => UiText.Get(UiText.TodayStatusCompletedKey),
            DomainTaskResult.PARTIAL => "PARTIAL",
            DomainTaskResult.MISSED => "MISSED",
            _ => IsCompleted
                ? UiText.Get(UiText.TodayStatusCompletedKey)
                : _liveStatus switch
        {
            TodayTaskStatus.OVERDUE => UiText.Get(UiText.TodayStatusOverdueKey),
            TodayTaskStatus.AwaitingResult => UiText.Get(UiText.TodayStatusAwaitingResultKey),
            TodayTaskStatus.UPCOMING => UiText.Get(UiText.TodayStatusUpcomingKey),
            TodayTaskStatus.PLANNED => UiText.Get(UiText.TodayStatusPlannedKey),
            _ => _sourceStatus switch
            {
                TodayMockStatus.Overdue => UiText.Get(UiText.TodayStatusOverdueKey),
                TodayMockStatus.Completed => UiText.Get(UiText.TodayStatusCompletedKey),
                TodayMockStatus.Upcoming => UiText.Get(UiText.TodayStatusUpcomingKey),
                TodayMockStatus.AwaitingResult => UiText.Get(UiText.TodayStatusAwaitingResultKey),
                _ => UiText.Get(UiText.TodayStatusPlannedKey)
            }
        }
        };

    public string StatusDescription => _liveResult switch
    {
        DomainTaskResult.COMPLETED => "已记录 COMPLETED · RecordedAt 只表示记录动作时间",
        DomainTaskResult.PARTIAL => "已记录 PARTIAL · 可以创建新的继续 Task",
        DomainTaskResult.MISSED => "已记录 MISSED · 原计划仍保留在历史中",
        _ when _isLive && _isContinuation => "这是上一条 PARTIAL Task 的继续计划。",
        _ when IsNeedsReviewVisible => UiText.Get(UiText.TodayStatusDescriptionReviewKey),
        _ when IsCompleted => UiText.Get(UiText.TodayStatusDescriptionCompletedKey),
        _ => UiText.Get(UiText.TodayStatusDescriptionPlannedKey)
    };

    public string CompletionGlyph => IsCompleted ? "✓" : "○";

    public string CompletionActionLabel => _isLive && IsCompleted
        ? "已记录结果"
        : IsCompleted
            ? UiText.Get(UiText.TodayRestoreIncompleteKey)
            : UiText.Get(UiText.TodayMarkCompleteKey);

    public string PinGlyph => IsPinned ? "★" : "☆";

    public string PinActionLabel => IsPinned
        ? UiText.Get(UiText.TodayUnpinKey)
        : UiText.Get(UiText.TodayPinKey);

    public string ReviewLabel => IsNeedsReviewVisible ? UiText.Get(UiText.TodayNeedsReviewLabelKey) : string.Empty;

    public string ContinueActionLabel => _isLive ? "继续 Task" : "继续";

    public string RescheduleActionLabel => _isLive ? "改期" : string.Empty;

    public string MoveUpActionLabel => _isLive ? "上移" : string.Empty;

    public string MoveDownActionLabel => _isLive ? "下移" : string.Empty;

    public bool IsPlanActionsVisible => _isLive && !IsCompleted;

    public string RecordPartialActionLabel => _isLive ? "记录 PARTIAL" : "PARTIAL";

    public string RecordMissedActionLabel => _isLive ? "记录 MISSED" : "MISSED";

    public string AutomationName => Title;

    public string AutomationHelpText => UiText.Format(UiText.CommonAutomationContextKey, TimeLabel, StatusLabel);

    public IRelayCommand SelectCommand { get; private set; } = null!;

    public IAsyncRelayCommand ToggleCompletionCommand { get; private set; } = null!;

    public IRelayCommand TogglePinCommand { get; private set; } = null!;

    public IAsyncRelayCommand RecordPartialCommand { get; private set; } = null!;

    public IAsyncRelayCommand RecordMissedCommand { get; private set; } = null!;

    public IAsyncRelayCommand ContinueCommand { get; private set; } = null!;

    public IRelayCommand RescheduleCommand { get; private set; } = null!;

    public IAsyncRelayCommand MoveUpCommand { get; private set; } = null!;

    public IAsyncRelayCommand MoveDownCommand { get; private set; } = null!;

    internal void SetCompleted(bool value)
    {
        if (!SetProperty(ref _isCompleted, value, nameof(IsCompleted)))
        {
            return;
        }

        OnPropertyChanged(nameof(CurrentGroup));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDescription));
        OnPropertyChanged(nameof(IsNeedsReviewVisible));
        OnPropertyChanged(nameof(CompletionGlyph));
        OnPropertyChanged(nameof(CompletionActionLabel));
        OnPropertyChanged(nameof(IsCompletionActionEnabled));
        OnPropertyChanged(nameof(IsPlanActionsVisible));
        OnPropertyChanged(nameof(ReviewLabel));
    }

    internal void SetPinned(bool value)
    {
        if (!SetProperty(ref _isPinned, value, nameof(IsPinned)))
        {
            return;
        }

        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinActionLabel));
    }

    internal void SetSelected(bool value)
    {
        SetProperty(ref _isSelected, value);
    }

    private void InitializeCallbacks(
        Action<TodayTaskViewModel> select,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> toggleCompletion,
        Action<TodayTaskViewModel> togglePin,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordPartial,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> recordMissed,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> continueTask,
        Action<TodayTaskViewModel> reschedule,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveUp,
        Func<TodayTaskViewModel, System.Threading.Tasks.Task> moveDown)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(toggleCompletion);
        ArgumentNullException.ThrowIfNull(togglePin);
        ArgumentNullException.ThrowIfNull(recordPartial);
        ArgumentNullException.ThrowIfNull(recordMissed);
        ArgumentNullException.ThrowIfNull(continueTask);
        ArgumentNullException.ThrowIfNull(reschedule);
        ArgumentNullException.ThrowIfNull(moveUp);
        ArgumentNullException.ThrowIfNull(moveDown);

        SelectCommand = new RelayCommand(() => select(this));
        ToggleCompletionCommand = new AsyncRelayCommand(() => toggleCompletion(this));
        TogglePinCommand = new RelayCommand(() => togglePin(this));
        RecordPartialCommand = new AsyncRelayCommand(() => recordPartial(this));
        RecordMissedCommand = new AsyncRelayCommand(() => recordMissed(this));
        ContinueCommand = new AsyncRelayCommand(() => continueTask(this));
        RescheduleCommand = new RelayCommand(() => reschedule(this));
        MoveUpCommand = new AsyncRelayCommand(() => moveUp(this));
        MoveDownCommand = new AsyncRelayCommand(() => moveDown(this));
    }

    private static string CreateLiveNotes(TodayTaskReadModel readModel)
    {
        if (!string.IsNullOrWhiteSpace(readModel.Task.ResultNote))
        {
            return readModel.Task.ResultNote!;
        }

        return readModel.Task.Result switch
        {
            DomainTaskResult.COMPLETED => "已记录 COMPLETED；RecordedAt 只表示记录动作时间。",
            DomainTaskResult.PARTIAL => "已记录 PARTIAL；如需继续，请创建新的计划。",
            DomainTaskResult.MISSED => "已记录 MISSED；原计划保留在历史中。",
            _ when readModel.IsNeedsReview => "RANGE 计划已结束但尚未记录结果。",
            _ when readModel.Task.ContinuedFromTaskId is not null => "来自上一条 PARTIAL Task 的继续计划。",
            _ => "来自本地 Task 查询结果。"
        };
    }

    private static TodayTaskGroup MapGroup(RemoteTodayTaskGroup group) => group switch
    {
        RemoteTodayTaskGroup.OVERDUE => TodayTaskGroup.Overdue,
        RemoteTodayTaskGroup.MORNING => TodayTaskGroup.Morning,
        RemoteTodayTaskGroup.AFTERNOON => TodayTaskGroup.Afternoon,
        RemoteTodayTaskGroup.EVENING => TodayTaskGroup.Evening,
        RemoteTodayTaskGroup.ANYTIME => TodayTaskGroup.Anytime,
        RemoteTodayTaskGroup.COMPLETED => TodayTaskGroup.Completed,
        _ => TodayTaskGroup.Anytime
    };

    private static string FormatTime(TimeSpec timeSpec) => timeSpec switch
    {
        AnytimeSpec => "ANYTIME",
        TimePointSpec point => FormatLocalTime(point.TimePoint),
        TimeRangeSpec range => $"{FormatLocalTime(range.RangeStart)}–{FormatLocalTime(range.RangeEnd)}" +
            (range.IsCrossMidnight ? "（跨午夜）" : string.Empty),
        _ => timeSpec.Type.ToString()
    };

    private static string FormatLocalTime(LocalTime time) =>
        $"{time.Hour:00}:{time.Minute:00}";
}
