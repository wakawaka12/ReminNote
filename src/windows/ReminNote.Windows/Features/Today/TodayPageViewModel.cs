using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.Resources.Localization;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Today;

public sealed class TodayPageViewModel : ShellPageViewModel
{
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
    private int _quickTaskNumber = 1;
    private bool _isQuickAddOpen;
    private string _quickAddText = string.Empty;
    private string _interactionMessage = UiText.Get(UiText.TodayInteractionLoadedKey);
    private TodayTaskViewModel? _selectedTask;

    public TodayPageViewModel()
        : this(new TodayMockDataService())
    {
    }

    public TodayPageViewModel(ITodayMockDataService mockDataService)
        : base(UiText.TodayPageDescription)
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

    public string MockBadge { get; } = UiText.TodayMockBadge;

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
        InteractionMessage = UiText.Get(UiText.TodayInteractionQuickAddOpenedKey);
    }

    private void CancelQuickAdd()
    {
        QuickAddText = string.Empty;
        IsQuickAddOpen = false;
        InteractionMessage = UiText.Get(UiText.TodayInteractionQuickAddCancelledKey);
    }

    private void AddQuickTask()
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
        IsQuickAddOpen = false;
        InteractionMessage = UiText.Format(UiText.TodayInteractionQuickAddAddedKey, task.Title);
        NotifySummaryChanged();
    }

    private void OpenNeedsReview()
    {
        var task = _tasks.FirstOrDefault(candidate => candidate.IsNeedsReviewVisible);
        if (task is null)
        {
            InteractionMessage = UiText.Get(UiText.TodayInteractionNoReviewKey);
            return;
        }

        SelectTask(task);
        InteractionMessage = UiText.Format(UiText.TodayInteractionReviewLocatedKey, task.Title);
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
            ? UiText.Format(UiText.TodayInteractionCompletedKey, task.Title)
            : UiText.Format(UiText.TodayInteractionRestoredKey, task.Title);
    }

    private void ToggleTaskPin(TodayTaskViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.SetPinned(!task.IsPinned);
        InteractionMessage = task.IsPinned
            ? UiText.Format(UiText.TodayInteractionPinnedKey, task.Title)
            : UiText.Format(UiText.TodayInteractionUnpinnedKey, task.Title);
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
    private readonly TodayMockStatus _sourceStatus;
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
        _sourceStatus = mockTask.Status;
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

    public string StatusLabel => IsCompleted
        ? UiText.Get(UiText.TodayStatusCompletedKey)
        : _sourceStatus switch
        {
            TodayMockStatus.Overdue => UiText.Get(UiText.TodayStatusOverdueKey),
            TodayMockStatus.Completed => UiText.Get(UiText.TodayStatusCompletedKey),
            TodayMockStatus.Upcoming => UiText.Get(UiText.TodayStatusUpcomingKey),
            TodayMockStatus.AwaitingResult => UiText.Get(UiText.TodayStatusAwaitingResultKey),
            TodayMockStatus.Planned => UiText.Get(UiText.TodayStatusPlannedKey),
            _ => UiText.Get(UiText.TodayStatusPlannedKey)
        };

    public string StatusDescription => IsCompleted
        ? UiText.Get(UiText.TodayStatusDescriptionCompletedKey)
        : IsNeedsReviewVisible
            ? UiText.Get(UiText.TodayStatusDescriptionReviewKey)
            : UiText.Get(UiText.TodayStatusDescriptionPlannedKey);

    public string CompletionGlyph => IsCompleted ? "✓" : "○";

    public string CompletionActionLabel => IsCompleted
        ? UiText.Get(UiText.TodayRestoreIncompleteKey)
        : UiText.Get(UiText.TodayMarkCompleteKey);

    public string PinGlyph => IsPinned ? "★" : "☆";

    public string PinActionLabel => IsPinned
        ? UiText.Get(UiText.TodayUnpinKey)
        : UiText.Get(UiText.TodayPinKey);

    public string ReviewLabel => IsNeedsReviewVisible ? UiText.Get(UiText.TodayNeedsReviewLabelKey) : string.Empty;

    public string AutomationName => Title;

    public string AutomationHelpText => UiText.Format(UiText.CommonAutomationContextKey, TimeLabel, StatusLabel);

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
