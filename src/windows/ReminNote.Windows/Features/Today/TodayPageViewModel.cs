using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Today;

public sealed class TodayPageViewModel : ShellPageViewModel
{
    private static readonly TodayGroupDefinition[] GroupDefinitions =
    [
        new(TodayTaskGroup.Overdue, "OVERDUE", "需要重新安排", true),
        new(TodayTaskGroup.Morning, "MORNING", "上午计划", true),
        new(TodayTaskGroup.Afternoon, "AFTERNOON", "下午计划", true),
        new(TodayTaskGroup.Evening, "EVENING", "晚间计划", true),
        new(TodayTaskGroup.Anytime, "ANYTIME", "没有固定时间", true),
        new(TodayTaskGroup.Completed, "COMPLETED", "已记录结果", false)
    ];

    private readonly List<TodayTaskViewModel> _tasks = [];
    private int _quickTaskNumber = 1;
    private bool _isQuickAddOpen;
    private string _quickAddText = string.Empty;
    private string _interactionMessage = "示例数据已加载 · 所有操作仅在本次运行中有效";
    private TodayTaskViewModel? _selectedTask;

    public TodayPageViewModel()
        : this(new TodayMockDataService())
    {
    }

    public TodayPageViewModel(ITodayMockDataService mockDataService)
        : base("TODAY 高保真 Mock · 仅使用进程内示例数据，不连接数据库或网络。")
    {
        ArgumentNullException.ThrowIfNull(mockDataService);

        var snapshot = mockDataService.Load();
        DateLabel = snapshot.DateLabel;
        WeekdayLabel = snapshot.WeekdayLabel;

        foreach (var definition in GroupDefinitions)
        {
            Groups.Add(new TodayTaskGroupViewModel(
                definition.Group,
                definition.Title,
                definition.Subtitle,
                definition.IsExpanded));
        }

        foreach (var mockTask in snapshot.Tasks)
        {
            _tasks.Add(CreateTask(mockTask));
        }

        PrimaryTask = _tasks.Single(task => task.Id == snapshot.PrimaryTaskId);
        RebuildGroups();

        OpenQuickAddCommand = new RelayCommand(OpenQuickAdd);
        CancelQuickAddCommand = new RelayCommand(CancelQuickAdd);
        AddQuickTaskCommand = new RelayCommand(AddQuickTask);
        OpenNeedsReviewCommand = new RelayCommand(OpenNeedsReview);
        SelectTask(PrimaryTask);
    }

    public ObservableCollection<TodayTaskGroupViewModel> Groups { get; } = [];

    public TodayTaskViewModel PrimaryTask { get; }

    public TodayTaskViewModel? SelectedTask
    {
        get => _selectedTask;
        private set => SetProperty(ref _selectedTask, value);
    }

    public string DateLabel { get; }

    public string WeekdayLabel { get; }

    public string DateSummary => $"{DateLabel} · {WeekdayLabel}";

    public string MockBadge { get; } = "MOCK · 仅内存";

    public string NeedsReviewText => $"NEEDS REVIEW · {NeedsReviewCount}";

    public string NeedsReviewDescription => NeedsReviewCount == 0
        ? "今天没有等待结果的 RANGE 计划"
        : "有 RANGE 计划等待你的结果记录";

    public string PlanSummary => $"{OpenTaskCount} OPEN  ·  {CompletedTaskCount} DONE";

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

    public string InteractionMessage
    {
        get => _interactionMessage;
        private set => SetProperty(ref _interactionMessage, value);
    }

    public IRelayCommand OpenQuickAddCommand { get; }

    public IRelayCommand CancelQuickAddCommand { get; }

    public IRelayCommand AddQuickTaskCommand { get; }

    public IRelayCommand OpenNeedsReviewCommand { get; }

    private TodayTaskViewModel CreateTask(TodayMockTask mockTask)
    {
        return new TodayTaskViewModel(
            mockTask,
            SelectTask,
            ToggleTaskCompletion,
            ToggleTaskPin);
    }

    private void OpenQuickAdd()
    {
        IsQuickAddOpen = true;
        InteractionMessage = "Quick Add 已展开 · P0 Mock 只会创建 ANYTIME 任务";
    }

    private void CancelQuickAdd()
    {
        QuickAddText = string.Empty;
        IsQuickAddOpen = false;
        InteractionMessage = "已取消 Quick Add · 示例数据未改变";
    }

    private void AddQuickTask()
    {
        var title = QuickAddText.Trim();
        if (title.Length == 0)
        {
            InteractionMessage = "请输入任务标题后再添加";
            return;
        }

        var mockTask = new TodayMockTask(
            Id: $"quick-add-{_quickTaskNumber++:00}",
            Title: title,
            Group: TodayTaskGroup.Anytime,
            TimeLabel: "ANYTIME",
            StatusLabel: "PLANNED",
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
        IsQuickAddOpen = false;
        InteractionMessage = $"已添加「{task.Title}」到 ANYTIME · 状态仅在本次运行有效";
        NotifySummaryChanged();
    }

    private void OpenNeedsReview()
    {
        var task = _tasks.FirstOrDefault(candidate => candidate.IsNeedsReviewVisible);
        if (task is null)
        {
            InteractionMessage = "当前没有需要复盘的 RANGE Mock 任务";
            return;
        }

        SelectTask(task);
        InteractionMessage = $"已定位到待复盘任务「{task.Title}」 · 请记录结果";
    }

    private void SelectTask(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        foreach (var candidate in _tasks)
        {
            candidate.SetSelected(ReferenceEquals(candidate, task));
        }

        SelectedTask = task;
    }

    private void ToggleTaskCompletion(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.SetCompleted(!task.IsCompleted);
        RebuildGroups();
        NotifySummaryChanged();
        InteractionMessage = task.IsCompleted
            ? $"已完成「{task.Title}」 · 未记录真实完成时间 · 状态仅在本次运行有效"
            : $"已恢复「{task.Title}」 · 仍保留原计划时间 · 状态仅在本次运行有效";
    }

    private void ToggleTaskPin(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.SetPinned(!task.IsPinned);
        InteractionMessage = task.IsPinned
            ? $"已置顶「{task.Title}」 · PIN 独立于优先级"
            : $"已取消置顶「{task.Title}」";
    }

    private void RebuildGroups()
    {
        foreach (var group in Groups)
        {
            group.SetItems(_tasks.Where(task => task.CurrentGroup == group.Group));
        }
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(OpenTaskCount));
        OnPropertyChanged(nameof(CompletedTaskCount));
        OnPropertyChanged(nameof(NeedsReviewCount));
        OnPropertyChanged(nameof(NeedsReviewText));
        OnPropertyChanged(nameof(NeedsReviewDescription));
        OnPropertyChanged(nameof(PlanSummary));
    }

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

    public string ItemCountText => $"{Items.Count} ITEMS";

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
    private readonly string _sourceStatusLabel;
    private bool _isCompleted;
    private bool _isPinned;
    private bool _isSelected;

    internal TodayTaskViewModel(
        TodayMockTask mockTask,
        Action<TodayTaskViewModel> select,
        Action<TodayTaskViewModel> toggleCompletion,
        Action<TodayTaskViewModel> togglePin)
    {
        ArgumentNullException.ThrowIfNull(mockTask);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(toggleCompletion);
        ArgumentNullException.ThrowIfNull(togglePin);

        Id = mockTask.Id;
        Title = mockTask.Title;
        OriginalGroup = mockTask.Group;
        TimeLabel = mockTask.TimeLabel;
        _sourceStatusLabel = mockTask.StatusLabel;
        PriorityLabel = mockTask.PriorityLabel;
        CategoryLabel = mockTask.CategoryLabel;
        TimeShapeLabel = mockTask.TimeShapeLabel;
        Notes = mockTask.Notes;
        _isCompleted = mockTask.IsCompleted;
        _isPinned = mockTask.IsPinned;
        IsNeedsReview = mockTask.IsNeedsReview;

        SelectCommand = new RelayCommand(() => select(this));
        ToggleCompletionCommand = new RelayCommand(() => toggleCompletion(this));
        TogglePinCommand = new RelayCommand(() => togglePin(this));
    }

    public string Id { get; }

    public string Title { get; }

    public TodayTaskGroup OriginalGroup { get; }

    public string TimeLabel { get; }

    public string PriorityLabel { get; }

    public string CategoryLabel { get; }

    public string TimeShapeLabel { get; }

    public string Notes { get; }

    public bool IsNeedsReview { get; }

    public bool IsNeedsReviewVisible => IsNeedsReview && !IsCompleted;

    public bool IsRange => TimeShapeLabel == "RANGE";

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

    public string StatusLabel => IsCompleted ? "COMPLETED" : _sourceStatusLabel;

    public string StatusDescription => IsCompleted
        ? "已记录完成 · 仅为本次运行内的 Mock 状态"
        : IsNeedsReviewVisible
            ? "计划时间已结束 · 等待用户记录结果"
            : "计划状态 · 不代表正在执行或已追踪耗时";

    public string CompletionGlyph => IsCompleted ? "✓" : "○";

    public string CompletionActionLabel => IsCompleted ? "恢复未完成" : "标记完成";

    public string PinGlyph => IsPinned ? "★" : "☆";

    public string PinActionLabel => IsPinned ? "取消置顶" : "置顶";

    public string ReviewLabel => IsNeedsReviewVisible ? "NEEDS REVIEW" : string.Empty;

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand ToggleCompletionCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

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
        SetProperty(ref _isSelected, value, nameof(IsSelected));
    }
}
