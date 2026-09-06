using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Core.Protocol;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Reminders;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Resources.Localization;

namespace ReminNote.Windows.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly TodayPageViewModel _todayPage;
    private readonly AnimePageViewModel _animePage;
    private readonly ReminderCenterViewModel? _reminderCenter;
    private readonly ReminderSettingsViewModel? _reminderSettings;
    private ShellPageViewModel _currentPage;

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage)
        : this(todayPage, animePage, null, null)
    {
    }

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage,
        ReminderCenterViewModel? reminderCenter)
        : this(todayPage, animePage, reminderCenter, null)
    {
    }

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage,
        ReminderCenterViewModel? reminderCenter,
        ReminderSettingsViewModel? reminderSettings)
    {
        _todayPage = todayPage;
        _animePage = animePage;
        _reminderCenter = reminderCenter;
        _reminderSettings = reminderSettings;
        NavigationItems =
            BuildNavigationItems(reminderSettings is not null);
        NavigateCommand = new RelayCommand<NavigationItemViewModel?>(Navigate);
        OpenReminderCenterCommand = new AsyncRelayCommand(
            OpenReminderCenterAsync,
            () => ReminderCenter is not null);
        _currentPage = _todayPage;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public TodayPageViewModel TodayPage => _todayPage;

    public AnimePageViewModel AnimePage => _animePage;

    public ReminderCenterViewModel? ReminderCenter => _reminderCenter;

    public ReminderSettingsViewModel? ReminderSettings => _reminderSettings;

    public bool IsReminderCenterAvailable => ReminderCenter is not null;

    public IRelayCommand<NavigationItemViewModel?> NavigateCommand { get; }

    public IAsyncRelayCommand OpenReminderCenterCommand { get; }

    /// <summary>
    /// Handles a validated Toast activation in the current profile. The
    /// Reminder Center owns the read/row matching logic; this shell method
    /// only coordinates the visible surface.
    /// </summary>
    public async System.Threading.Tasks.Task HandleNotificationActivationAsync(
        ReminderNotificationActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (ReminderCenter is not null)
        {
            await ReminderCenter
                .OpenForActivationAsync(activation.LogicalReminderIds)
                .ConfigureAwait(true);
        }
    }

    public ShellPageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ActiveTitle => CurrentPage switch
    {
        TodayPageViewModel => UiText.ShellTodayTitle,
        AnimePageViewModel => UiText.ShellAnimeTitle,
        ReminderSettingsViewModel => "提醒规则设置",
        _ => UiText.ShellFallbackTitle
    };

    private void Navigate(NavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        CurrentPage = item.Page switch
        {
            ShellPage.Today => _todayPage,
            ShellPage.Anime => _animePage,
            ShellPage.Reminders when _reminderSettings is not null => _reminderSettings,
            _ => _todayPage
        };
        OnPropertyChanged(nameof(ActiveTitle));
    }

    private async System.Threading.Tasks.Task OpenReminderCenterAsync()
    {
        if (ReminderCenter is not null)
        {
            await ReminderCenter.ToggleCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private static List<NavigationItemViewModel> BuildNavigationItems(bool includeReminderSettings)
    {
        var items = new List<NavigationItemViewModel>
        {
            new(ShellPage.Today, UiText.ShellTodayTitle, UiText.ShellTodaySubtitle),
            new(ShellPage.Anime, UiText.ShellAnimeTitle, UiText.ShellAnimeSubtitle)
        };
        if (includeReminderSettings)
        {
            items.Add(new NavigationItemViewModel(
                ShellPage.Reminders,
                "提醒规则",
                "提前、重复、优先级和唤醒"));
        }

        return items;
    }
}
