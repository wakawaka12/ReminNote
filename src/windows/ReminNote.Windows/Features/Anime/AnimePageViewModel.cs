using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.Resources.Localization;
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
    private string _interactionStatus = UiText.Get(UiText.AnimeMockStatusKey);

    public AnimePageViewModel(IAnimeMockCatalog mockCatalog)
        : base(UiText.AnimePageDescription)
    {
        ArgumentNullException.ThrowIfNull(mockCatalog);
        _allEntries = mockCatalog.LoadCatalog();
        VisibleEntries = new ObservableCollection<AnimeMockEntry>();
        Sections =
        [
            new AnimeSectionItem(AllSectionKey, UiText.Get(UiText.AnimeSectionAllLabelKey), UiText.Get(UiText.AnimeSectionAllEnglishKey)),
            new AnimeSectionItem(ThisSeasonSectionKey, UiText.Get(UiText.AnimeSectionThisSeasonLabelKey), UiText.Get(UiText.AnimeSectionThisSeasonEnglishKey)),
            new AnimeSectionItem(WatchingSectionKey, UiText.Get(UiText.AnimeSectionWatchingLabelKey), UiText.Get(UiText.AnimeSectionWatchingEnglishKey)),
            new AnimeSectionItem(WatchLaterSectionKey, UiText.Get(UiText.AnimeSectionWatchLaterLabelKey), UiText.Get(UiText.AnimeSectionWatchLaterEnglishKey)),
            new AnimeSectionItem(PlanToWatchSectionKey, UiText.Get(UiText.AnimeSectionPlanToWatchLabelKey), UiText.Get(UiText.AnimeSectionPlanToWatchEnglishKey))
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

    public event Action<AnimeMockEntry>? SelectedAnimeChanged;

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
        ThisSeasonSectionKey => UiText.Get(UiText.AnimeSectionThisSeasonDescriptionKey),
        WatchingSectionKey => UiText.Get(UiText.AnimeSectionWatchingDescriptionKey),
        WatchLaterSectionKey => UiText.Get(UiText.AnimeSectionWatchLaterDescriptionKey),
        PlanToWatchSectionKey => UiText.Get(UiText.AnimeSectionPlanToWatchDescriptionKey),
        _ => UiText.Get(UiText.AnimeSectionAllDescriptionKey)
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
                if (value is not null)
                {
                    SelectedAnimeChanged?.Invoke(value);
                }
            }
        }
    }

    public string SelectedEntryTitle => SelectedEntry?.DisplayTitle ?? UiText.Get(UiText.AnimeNoSelectedTitleKey);

    public string SelectedEntrySubtitle => SelectedEntry?.Subtitle ?? UiText.Get(UiText.AnimeNoSelectedSubtitleKey);

    public string SelectedEntryDescription => SelectedEntry?.DetailDescription ?? UiText.Get(UiText.AnimeNoSelectedDescriptionKey);

    public string SelectedEntryStatus => SelectedEntry?.StatusLabel ?? UiText.Get(UiText.AnimeNoSelectedStatusKey);

    public string SelectedEntryProgress => SelectedEntry?.ProgressLabel ?? UiText.Get(UiText.AnimeNoSelectedProgressKey);

    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public bool HasNoVisibleEntries => !HasVisibleEntries;

    public string VisibleCountLabel => UiText.Format(UiText.AnimeVisibleCountKey, VisibleEntries.Count);

    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchQuery)
        ? UiText.Get(UiText.AnimeEmptyTitleKey)
        : UiText.Get(UiText.AnimeEmptySearchTitleKey);

    public string EmptyStateDescription => string.IsNullOrWhiteSpace(SearchQuery)
        ? UiText.Get(UiText.AnimeEmptyDescriptionKey)
        : UiText.Format(UiText.AnimeEmptySearchDescriptionKey, SearchQuery);

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
        InteractionStatus = UiText.Format(UiText.AnimeInteractionSelectedKey, entry.DisplayTitle);
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
            InteractionStatus = UiText.Format(UiText.AnimeInteractionWatchLaterAddedKey, entry.DisplayTitle);
        }
        else
        {
            InteractionStatus = UiText.Format(UiText.AnimeInteractionWatchLaterRemovedKey, entry.DisplayTitle);
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

        InteractionStatus = UiText.Format(UiText.AnimeInteractionWatchedKey, entry.DisplayTitle);
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
            ? UiText.Format(UiText.AnimeInteractionTrackingOnKey, entry.DisplayTitle)
            : UiText.Format(UiText.AnimeInteractionTrackingOffKey, entry.DisplayTitle);
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
            ? UiText.Format(UiText.AnimeInteractionReminderOnKey, entry.DisplayTitle)
            : UiText.Format(UiText.AnimeInteractionReminderOffKey, entry.DisplayTitle);
    }

    private void RefreshMock()
    {
        InteractionStatus = UiText.Get(UiText.AnimeInteractionRefreshKey);
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
