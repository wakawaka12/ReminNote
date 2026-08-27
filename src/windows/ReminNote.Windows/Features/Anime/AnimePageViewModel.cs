using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Anime;

public sealed class AnimePageViewModel : ShellPageViewModel
{
    private const string AllSectionKey = "ALL";
    private const string ThisSeasonSectionKey = "THIS_SEASON";
    private const string WatchingSectionKey = "WATCHING";
    private const string WatchLaterSectionKey = "WATCH_LATER";
    private const string PlanToWatchSectionKey = "PLAN_TO_WATCH";

    private readonly IReadOnlyList<AnimeMockEntry> _allEntries;
    private AnimeMockEntry? _selectedEntry;
    private string _selectedSectionKey = AllSectionKey;
    private string _searchQuery = string.Empty;
    private string _interactionStatus = "LOCAL MOCK · 固定数据 · 未连接网络";

    public AnimePageViewModel(IAnimeMockCatalog mockCatalog)
        : base("本地动画库 Mock · 追番、播出与待看状态均为当前运行中的演示数据。")
    {
        ArgumentNullException.ThrowIfNull(mockCatalog);
        _allEntries = mockCatalog.LoadCatalog();
        VisibleEntries = new ObservableCollection<AnimeMockEntry>();
        Sections =
        [
            new AnimeSectionItem(AllSectionKey, "总览", "LIBRARY"),
            new AnimeSectionItem(ThisSeasonSectionKey, "本季", "THIS SEASON"),
            new AnimeSectionItem(WatchingSectionKey, "追番中", "WATCHING"),
            new AnimeSectionItem(WatchLaterSectionKey, "待看", "WATCH LATER"),
            new AnimeSectionItem(PlanToWatchSectionKey, "计划观看", "PLAN TO WATCH")
        ];

        SelectSectionCommand = new RelayCommand<string?>(SelectSection);
        SelectAnimeCommand = new RelayCommand<AnimeMockEntry?>(SelectAnime, entry => entry is not null);
        ToggleWatchLaterCommand = new RelayCommand<AnimeMockEntry?>(ToggleWatchLater, entry => entry is not null);
        MarkWatchedCommand = new RelayCommand<AnimeMockEntry?>(MarkWatched, entry => entry is not null);
        ToggleTrackingCommand = new RelayCommand<AnimeMockEntry?>(ToggleTracking, entry => entry is not null);
        ToggleReminderCommand = new RelayCommand<AnimeMockEntry?>(ToggleReminder, entry => entry is not null);
        RefreshMockCommand = new RelayCommand(RefreshMock);
        ClearSearchCommand = new RelayCommand(ClearSearch);

        _selectedEntry = _allEntries[0];
        UpdateSectionCounts();
        SelectSection(AllSectionKey);
    }

    public IReadOnlyList<AnimeSectionItem> Sections { get; }

    public ObservableCollection<AnimeMockEntry> VisibleEntries { get; }

    public AnimeMockEntry NextAiring => _allEntries[0];

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                RefreshVisibleEntries();
            }
        }
    }

    public string SelectedSectionTitle => Sections.First(section => section.Key == _selectedSectionKey).Label;

    public string SelectedSectionEnglishTitle => Sections.First(section => section.Key == _selectedSectionKey).EnglishLabel;

    public string SelectedSectionDescription => _selectedSectionKey switch
    {
        ThisSeasonSectionKey => "本季正在关注的本地条目与下一集安排。",
        WatchingSectionKey => "已经开启追番的条目，会优先显示下一集播出信息。",
        WatchLaterSectionKey => "已播但尚未观看的剧集，按当前 Mock 的相关性排序。",
        PlanToWatchSectionKey => "暂存到计划观看的条目，等待你决定何时开始。",
        _ => "用一个轻量的本地面板管理播出、进度和下一集。"
    };

    public AnimeMockEntry? SelectedEntry
    {
        get => _selectedEntry;
        private set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(SelectedEntryTitle));
                OnPropertyChanged(nameof(SelectedEntrySubtitle));
                OnPropertyChanged(nameof(SelectedEntryDescription));
                OnPropertyChanged(nameof(SelectedEntryStatus));
                OnPropertyChanged(nameof(SelectedEntryProgress));
            }
        }
    }

    public string SelectedEntryTitle => SelectedEntry?.DisplayTitle ?? "尚未选择动画";

    public string SelectedEntrySubtitle => SelectedEntry?.Subtitle ?? "从上方卡片选择一个本地 Mock 条目";

    public string SelectedEntryDescription => SelectedEntry?.DetailDescription ?? "没有匹配的条目时，详情面板会保留在这里。";

    public string SelectedEntryStatus => SelectedEntry?.StatusLabel ?? "NO SELECTION";

    public string SelectedEntryProgress => SelectedEntry?.ProgressLabel ?? "等待选择";

    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public bool HasNoVisibleEntries => !HasVisibleEntries;

    public string VisibleCountLabel => $"{VisibleEntries.Count:00} RESULTS · LOCAL CATALOG";

    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchQuery)
        ? "这个栏目暂时没有条目"
        : "没有找到匹配的动画";

    public string EmptyStateDescription => string.IsNullOrWhiteSpace(SearchQuery)
        ? "切换其他栏目，或重新载入固定的本地 Mock 数据。"
        : $"“{SearchQuery}”不在当前 Mock 目录中，请尝试标题或英文副标题。";

    public string InteractionStatus
    {
        get => _interactionStatus;
        private set => SetProperty(ref _interactionStatus, value);
    }

    public IRelayCommand<string?> SelectSectionCommand { get; }

    public IRelayCommand<AnimeMockEntry?> SelectAnimeCommand { get; }

    public IRelayCommand<AnimeMockEntry?> ToggleWatchLaterCommand { get; }

    public IRelayCommand<AnimeMockEntry?> MarkWatchedCommand { get; }

    public IRelayCommand<AnimeMockEntry?> ToggleTrackingCommand { get; }

    public IRelayCommand<AnimeMockEntry?> ToggleReminderCommand { get; }

    public IRelayCommand RefreshMockCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    private void SelectSection(string? sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey) || Sections.All(section => section.Key != sectionKey))
        {
            return;
        }

        _selectedSectionKey = sectionKey;
        foreach (var section in Sections)
        {
            section.IsSelected = section.Key == sectionKey;
        }

        OnPropertyChanged(nameof(SelectedSectionTitle));
        OnPropertyChanged(nameof(SelectedSectionEnglishTitle));
        OnPropertyChanged(nameof(SelectedSectionDescription));
        RefreshVisibleEntries();
    }

    private void SelectAnime(AnimeMockEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SelectedEntry = entry;
        InteractionStatus = $"已选择 {entry.DisplayTitle} · 详情面板已更新";
    }

    private void ToggleWatchLater(AnimeMockEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsWatchLater = !entry.IsWatchLater;
        if (entry.IsWatchLater)
        {
            entry.IsWatched = false;
            InteractionStatus = $"{entry.DisplayTitle} 已加入 WATCH LATER · 仅更新当前 Mock 状态";
        }
        else
        {
            InteractionStatus = $"{entry.DisplayTitle} 已移出 WATCH LATER · 仅更新当前 Mock 状态";
        }

        UpdateSectionCounts();
        RefreshVisibleEntries();
    }

    private void MarkWatched(AnimeMockEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!entry.IsWatched)
        {
            entry.WatchedEpisodes = Math.Min(entry.TotalEpisodes, entry.WatchedEpisodes + 1);
            entry.IsWatched = true;
            entry.IsWatchLater = false;
        }

        InteractionStatus = $"{entry.DisplayTitle} · 当前剧集已标记为已看 · 没有创建真实历史记录";
        UpdateSectionCounts();
        RefreshVisibleEntries();
    }

    private void ToggleTracking(AnimeMockEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsTracked = !entry.IsTracked;
        InteractionStatus = entry.IsTracked
            ? $"{entry.DisplayTitle} 已开启追番 · 仅更新当前 Mock 状态"
            : $"{entry.DisplayTitle} 已关闭追番 · 仅更新当前 Mock 状态";
        UpdateSectionCounts();
        RefreshVisibleEntries();
    }

    private void ToggleReminder(AnimeMockEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsReminderArmed = !entry.IsReminderArmed;
        InteractionStatus = entry.IsReminderArmed
            ? $"{entry.DisplayTitle} · 模拟提醒已开启（不会触发真实通知）"
            : $"{entry.DisplayTitle} · 模拟提醒已关闭";
    }

    private void RefreshMock()
    {
        InteractionStatus = "MOCK 已重新载入 · 数据固定在内存中 · 未访问网络";
        RefreshVisibleEntries();
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    private void RefreshVisibleEntries()
    {
        var matches = _allEntries
            .Where(MatchesSelectedSection)
            .Where(MatchesSearch)
            .ToList();

        VisibleEntries.Clear();
        foreach (var entry in matches)
        {
            VisibleEntries.Add(entry);
        }

        if (SelectedEntry is null || !matches.Contains(SelectedEntry))
        {
            SelectedEntry = matches.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasVisibleEntries));
        OnPropertyChanged(nameof(HasNoVisibleEntries));
        OnPropertyChanged(nameof(VisibleCountLabel));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
    }

    private bool MatchesSelectedSection(AnimeMockEntry entry) => _selectedSectionKey switch
    {
        ThisSeasonSectionKey => entry.IsThisSeason,
        WatchingSectionKey => entry.IsTracked,
        WatchLaterSectionKey => entry.IsWatchLater,
        PlanToWatchSectionKey => entry.IsPlanToWatch,
        _ => true
    };

    private bool MatchesSearch(AnimeMockEntry entry)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            return true;
        }

        return entry.DisplayTitle.Contains(SearchQuery, StringComparison.CurrentCultureIgnoreCase)
            || entry.Subtitle.Contains(SearchQuery, StringComparison.CurrentCultureIgnoreCase)
            || entry.GenreLabel.Contains(SearchQuery, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateSectionCounts()
    {
        foreach (var section in Sections)
        {
            section.Count = section.Key switch
            {
                ThisSeasonSectionKey => _allEntries.Count(entry => entry.IsThisSeason),
                WatchingSectionKey => _allEntries.Count(entry => entry.IsTracked),
                WatchLaterSectionKey => _allEntries.Count(entry => entry.IsWatchLater),
                PlanToWatchSectionKey => _allEntries.Count(entry => entry.IsPlanToWatch),
                _ => _allEntries.Count
            };
        }
    }
}
