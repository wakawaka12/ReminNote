using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private ShellPageViewModel _currentPage;

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage)
        : this(todayPage, animePage, null)
    {
    }

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage,
        ReminderCenterViewModel? reminderCenter)
    {
        _todayPage = todayPage;
        _animePage = animePage;
        _reminderCenter = reminderCenter;
        NavigationItems =
        [
            new NavigationItemViewModel(ShellPage.Today, UiText.ShellTodayTitle, UiText.ShellTodaySubtitle),
            new NavigationItemViewModel(ShellPage.Anime, UiText.ShellAnimeTitle, UiText.ShellAnimeSubtitle)
        ];
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

    public bool IsReminderCenterAvailable => ReminderCenter is not null;

    public IRelayCommand<NavigationItemViewModel?> NavigateCommand { get; }

    public IAsyncRelayCommand OpenReminderCenterCommand { get; }

    public ShellPageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ActiveTitle => CurrentPage switch
    {
        TodayPageViewModel => UiText.ShellTodayTitle,
        AnimePageViewModel => UiText.ShellAnimeTitle,
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
}
